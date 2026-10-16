using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumeRelay;

/// <summary>
/// Everything the relay remembers, kept as small JSON files in one data folder.
/// All access goes through one lock; files are written to a temp name and swapped in,
/// so a crash or power cut mid-write never leaves a half-written file behind.
/// </summary>
public sealed class Store
{
    private const int MaxActivity = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly string _dir;
    private List<Device> _devices;
    private Settings _settings;
    private List<PendingCopy> _pending;
    private readonly LinkedList<ActivityEntry> _activity;

    public string DataDirectory => _dir;

    /// <summary>Raised whenever the activity log gains an entry (used to nudge the web page).</summary>
    public event Action? Changed;

    public Store(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _devices = Load<List<Device>>("devices.json") ?? new();
        _settings = Load<Settings>("settings.json") ?? new();
        _pending = Load<List<PendingCopy>>("pending.json") ?? new();
        _activity = new LinkedList<ActivityEntry>(Load<List<ActivityEntry>>("activity.json") ?? new());
    }

    // ---------- devices ----------

    public List<Device> Devices()
    {
        lock (_lock) return _devices.Select(Clone).ToList();
    }

    public Device? Device(string id)
    {
        lock (_lock) return _devices.FirstOrDefault(d => d.Id == id) is { } d ? Clone(d) : null;
    }

    /// <summary>Adds a new device, or refreshes IP/model/firmware of one already known by the same identity.
    /// Returns true when the device is new.</summary>
    public bool UpsertDiscovered(Device found)
    {
        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d => d.Id == found.Id);
            if (existing == null)
            {
                if (string.IsNullOrWhiteSpace(found.Name))
                    found.Name = $"Zidoo {found.Ip}";
                _devices.Add(Clone(found));
                Save("devices.json", _devices);
                return true;
            }

            existing.Ip = found.Ip;
            existing.Port = found.Port;
            if (found.Model.Length > 0) existing.Model = found.Model;
            if (found.Firmware.Length > 0) existing.Firmware = found.Firmware;
            if (found.Mac.Length > 0) existing.Mac = found.Mac;
            Save("devices.json", _devices);
            return false;
        }
    }

    public bool UpdateDevice(string id, string? name, bool? enabled)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.Id == id);
            if (d == null) return false;
            if (name != null && name.Trim().Length > 0) d.Name = name.Trim();
            if (enabled.HasValue) d.Enabled = enabled.Value;
            Save("devices.json", _devices);
            return true;
        }
    }

    public bool RemoveDevice(string id)
    {
        lock (_lock)
        {
            int n = _devices.RemoveAll(d => d.Id == id);
            _pending.RemoveAll(p => p.TargetId == id);
            Save("devices.json", _devices);
            Save("pending.json", _pending);
            return n > 0;
        }
    }

    // ---------- settings ----------

    public Settings Settings()
    {
        lock (_lock) return JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_settings, Json), Json)!;
    }

    public void SaveSettings(Settings s)
    {
        s.PollSeconds = Math.Clamp(s.PollSeconds, 1, 60);
        s.PendingMaxDays = Math.Clamp(s.PendingMaxDays, 1, 365);
        lock (_lock)
        {
            _settings = s;
            Save("settings.json", _settings);
        }
    }

    // ---------- pending copies ----------

    public List<PendingCopy> Pending()
    {
        lock (_lock) return _pending.Select(Clone).ToList();
    }

    /// <summary>Queues a copy. If one is already waiting for the same player and file, the newer bookmark replaces it.</summary>
    public void Enqueue(PendingCopy p)
    {
        lock (_lock)
        {
            var existing = _pending.FirstOrDefault(x => x.Key == p.Key);
            if (existing != null)
            {
                if (existing.LastWatchTimeMs > p.LastWatchTimeMs) return;
                _pending.Remove(existing);
            }
            _pending.Add(Clone(p));
            Save("pending.json", _pending);
        }
    }

    public void UpdatePending(PendingCopy p)
    {
        lock (_lock)
        {
            var existing = _pending.FirstOrDefault(x => x.Key == p.Key);
            if (existing == null) return;
            existing.LastAttemptUtc = p.LastAttemptUtc;
            existing.LastNote = p.LastNote;
            Save("pending.json", _pending);
        }
    }

    /// <summary>Removes a queued copy, but only if it is still the same bookmark (a newer one may have replaced it meanwhile).</summary>
    public void Dequeue(PendingCopy p)
    {
        lock (_lock)
        {
            int n = _pending.RemoveAll(x => x.Key == p.Key && x.LastWatchTimeMs == p.LastWatchTimeMs);
            if (n > 0) Save("pending.json", _pending);
        }
    }

    public bool RemovePending(string key)
    {
        lock (_lock)
        {
            int n = _pending.RemoveAll(x => x.Key == key);
            if (n > 0) Save("pending.json", _pending);
            return n > 0;
        }
    }

    public int ExpirePending(TimeSpan maxAge)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            int n = _pending.RemoveAll(x => x.CreatedUtc < cutoff);
            if (n > 0) Save("pending.json", _pending);
            return n;
        }
    }

    // ---------- activity log ----------

    public void Log(ActivityLevel level, string message)
    {
        var e = new ActivityEntry { Level = level, Message = message, TimeUtc = DateTime.UtcNow };
        lock (_lock)
        {
            _activity.AddFirst(e);
            while (_activity.Count > MaxActivity) _activity.RemoveLast();
            Save("activity.json", _activity.ToList());
        }
        Console.WriteLine($"{e.TimeUtc:O} [{level}] {message}");
        Changed?.Invoke();
    }

    public List<ActivityEntry> Activity(int max)
    {
        lock (_lock) return _activity.Take(max).ToList();
    }

    // ---------- plumbing ----------

    private T? Load<T>(string file) where T : class
    {
        string path = Path.Combine(_dir, file);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex)
        {
            // A corrupt file must not stop the service; keep a copy for inspection and start fresh.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            Console.Error.WriteLine($"Could not read {path}: {ex.Message}. Starting with defaults.");
            return null;
        }
    }

    private void Save<T>(string file, T value)
    {
        string path = Path.Combine(_dir, file);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static T Clone<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!;
}
