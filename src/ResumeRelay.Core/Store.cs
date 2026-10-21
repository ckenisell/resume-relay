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
    private const int MaxEvents = 300;

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
    private readonly LinkedList<StopEvent> _events;

    public string DataDirectory => _dir;
    public string PosterDirectory => Path.Combine(_dir, "posters");

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
        _events = new LinkedList<StopEvent>(Load<List<StopEvent>>("events.json") ?? new());
        Directory.CreateDirectory(PosterDirectory);
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
            if (found.NetMac.Length > 0) existing.NetMac = found.NetMac;
            if (found.WifiMac.Length > 0) existing.WifiMac = found.WifiMac;
            Save("devices.json", _devices);
            return false;
        }
    }

    public void SetHomeTheaterVersion(string id, string name, int code)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.Id == id);
            if (d == null || (d.HtVersion == name && d.HtVersionCode == code)) return;
            d.HtVersion = name;
            d.HtVersionCode = code;
            Save("devices.json", _devices);
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

    /// <summary>Queues a copy. If one is already waiting for the same player and file, the newer bookmark replaces it;
    /// the replaced copy is returned so its Activity chip can say so.</summary>
    public PendingCopy? Enqueue(PendingCopy p)
    {
        lock (_lock)
        {
            var existing = _pending.FirstOrDefault(x => x.Key == p.Key);
            if (existing != null)
            {
                if (existing.LastWatchTimeMs > p.LastWatchTimeMs) return null;
                _pending.Remove(existing);
            }
            _pending.Add(Clone(p));
            Save("pending.json", _pending);
            return existing == null ? null : Clone(existing);
        }
    }

    public PendingCopy? PendingFor(string key)
    {
        lock (_lock) return _pending.FirstOrDefault(x => x.Key == key) is { } p ? Clone(p) : null;
    }

    public void UpdatePending(PendingCopy p)
    {
        lock (_lock)
        {
            var existing = _pending.FirstOrDefault(x => x.Key == p.Key);
            if (existing == null) return;
            existing.LastAttemptUtc = p.LastAttemptUtc;
            existing.LastNote = p.LastNote;
            existing.WaitedFor = p.WaitedFor;
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

    /// <summary>Removes a queued copy and returns it (null if there was none).</summary>
    public PendingCopy? RemovePending(string key)
    {
        lock (_lock)
        {
            var p = _pending.FirstOrDefault(x => x.Key == key);
            if (p == null) return null;
            _pending.Remove(p);
            Save("pending.json", _pending);
            return p;
        }
    }

    public List<PendingCopy> ExpirePending(TimeSpan maxAge)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var gone = _pending.Where(x => x.CreatedUtc < cutoff).ToList();
            if (gone.Count > 0) { _pending.RemoveAll(x => x.CreatedUtc < cutoff); Save("pending.json", _pending); }
            return gone;
        }
    }

    // ---------- stop events (the Activity cards) ----------

    public void AddEvent(StopEvent e)
    {
        lock (_lock)
        {
            _events.AddFirst(Clone(e));
            while (_events.Count > MaxEvents) _events.RemoveLast();
            Save("events.json", _events.ToList());
            PrunePosters();
        }
        Changed?.Invoke();
    }

    /// <summary>Updates one player's chip on a stop card.</summary>
    public void UpdateEventTarget(string eventId, string deviceId, TargetState state, string note)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        lock (_lock)
        {
            var t = _events.FirstOrDefault(x => x.Id == eventId)?.Targets.FirstOrDefault(x => x.DeviceId == deviceId);
            if (t == null) return;
            t.State = state;
            t.Note = note;
            Save("events.json", _events.ToList());
        }
        Changed?.Invoke();
    }

    public List<StopEvent> Events(int max)
    {
        lock (_lock) return _events.Take(max).Select(Clone).ToList();
    }

    /// <summary>Deletes cached posters no card refers to any more (the now-playing poster is re-fetched if needed).</summary>
    private void PrunePosters()
    {
        try
        {
            var used = new HashSet<string>(_events.Select(e => e.Poster).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(PosterDirectory))
            {
                var name = Path.GetFileName(f);
                if (!used.Contains(name) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2))
                    File.Delete(f);
            }
        }
        catch { }
    }

    // ---------- activity log ----------

    /// <summary>A system message: shown as a row on the Activity tab.</summary>
    public void Log(ActivityLevel level, string message) => Write(level, message, system: true);

    /// <summary>A copy detail: kept in the journal (and the service log), shown on the tab as part of a stop card.</summary>
    public void LogCopy(ActivityLevel level, string message) => Write(level, message, system: false);

    private void Write(ActivityLevel level, string message, bool system)
    {
        var e = new ActivityEntry { Level = level, Message = message, TimeUtc = DateTime.UtcNow, System = system };
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
