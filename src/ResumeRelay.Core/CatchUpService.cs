using System.Collections.Concurrent;
using System.Text.Json;

namespace ResumeRelay;

/// <summary>
/// Catch Up. Resume Relay notes the time every minute (alive.json). When it starts after being down for more than a few
/// minutes, it reads the players and, for every file watched while it was down, copies the newest state to the others
/// (latest stop wins, as when it's running). A player that's off then is read when it comes back. "Catch up now" does
/// the same for the last 14 days, for changes made where Resume Relay can't see them.
/// What it can't see: a Plex "mark as unplayed" (Plex keeps no time for it), and players whose clock is wrong.
/// </summary>
public sealed class CatchUpService : BackgroundService
{
    private static readonly TimeSpan DownBeforeCatchUp = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualWindow = TimeSpan.FromDays(14);

    private readonly Store _store;
    private readonly RelayEngine _engine;
    private readonly ProgressService _progress;
    private readonly string _file;
    private readonly ConcurrentDictionary<string, DateTime> _waiting = new(); // player id → "since", for players that were off
    private readonly SemaphoreSlim _one = new(1, 1);
    private CancellationToken _stopping;

    public bool Running { get; private set; }
    public string Status { get; private set; } = "";
    public DateTime? LastRunUtc { get; private set; }
    public string LastResult { get; private set; } = "";
    public IReadOnlyCollection<string> WaitingFor => _waiting.Keys.ToList();

    public CatchUpService(Store store, RelayEngine engine, ProgressService progress)
    {
        _store = store;
        _engine = engine;
        _progress = progress;
        _file = Path.Combine(store.DataDirectory, "alive.json");
        engine.CameOnline += OnCameOnline;
    }

    private DateTime? LastAlive()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<DateTime>(File.ReadAllText(_file)) : null; } catch { return null; }
    }

    private void Heartbeat()
    {
        try { File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(DateTime.UtcNow)); File.Move(_file + ".tmp", _file, true); } catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        var lastAlive = LastAlive();
        Heartbeat();
        var down = lastAlive.HasValue ? DateTime.UtcNow - lastAlive.Value : TimeSpan.Zero;
        if (lastAlive.HasValue && down > DownBeforeCatchUp && _store.Settings().CatchUpAuto)
            _ = Task.Run(async () =>
            {
                // Give the players a minute to be found and answer first.
                try { await Task.Delay(TimeSpan.FromSeconds(Delay(75)), ct); } catch { return; }
                await RunAsync(lastAlive.Value.AddMinutes(-2), $"Catch Up after Resume Relay was off for {Pretty(down)}", null, ct);
            }, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
            Heartbeat();
        }
    }

    /// <summary>"Catch up now": the last 14 days, all players.</summary>
    public string? StartNow()
    {
        if (Running) return "Catch Up is already running.";
        _ = Task.Run(() => RunAsync(DateTime.UtcNow - ManualWindow, "Catch Up (pressed in Settings)", null, _stopping));
        return null;
    }

    private void OnCameOnline(string deviceId)
    {
        if (!_waiting.TryGetValue(deviceId, out var since)) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Delay(60)), _stopping); } catch { return; }
            var d = _store.Device(deviceId);
            if (d == null || !_engine.LiveOf(deviceId).Online) return;
            _waiting.TryRemove(deviceId, out _);
            await RunAsync(since, $"Catch Up for {d.Name}, back on after Resume Relay was off", deviceId, _stopping);
        });
    }

    /// <summary>Reads the players that are on (or just the one that came back), then copies what changed since.</summary>
    private async Task RunAsync(DateTime sinceUtc, string reason, string? onlyNew, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        Running = true;
        try
        {
            var all = _store.Devices().Where(d => d.Enabled).ToList();
            var on = all.Where(d => _engine.LiveOf(d.Id).Online).ToList();
            if (onlyNew == null)
                foreach (var d in all.Where(d => !on.Contains(d))) _waiting[d.Id] = sinceUtc; // read when it comes back
            var toRead = onlyNew == null ? on.Select(d => d.Id).ToList() : new List<string> { onlyNew };
            // The others are compared from their last read; if a player was never read (or long ago), read it too.
            if (onlyNew != null)
                toRead.AddRange(on.Where(d => d.Id != onlyNew && (_progress.ReadUtcOf(d.Id) is not { } r || DateTime.UtcNow - r > TimeSpan.FromHours(6))).Select(d => d.Id));
            if (toRead.Count == 0) { LastResult = "No players were on to read."; return; }
            Status = $"Reading {toRead.Count} player{(toRead.Count == 1 ? "" : "s")}…";
            await _progress.ReadAndWaitAsync(toRead, ct);
            Status = "Comparing…";
            var compare = on.Select(d => d.Id).Where(id => _progress.ReadUtcOf(id) != null).ToList();
            var (files, copies) = await _progress.CatchUpAsync(sinceUtc, compare, reason);
            LastRunUtc = DateTime.UtcNow;
            LastResult = files == 0 ? "Everything was already in sync." : $"{files:N0} file{(files == 1 ? "" : "s")} copied ({copies:N0} change{(copies == 1 ? "" : "s")}).";
            var waiting = _waiting.Keys.Select(id => _store.Device(id)?.Name).Where(n => n != null).ToList();
            _store.Log(ActivityLevel.Info, $"{reason}: {LastResult}" + (waiting.Count > 0 ? $" Waiting to read {string.Join(", ", waiting)} when {(waiting.Count == 1 ? "it comes" : "they come")} back on." : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastResult = "Catch Up stopped: " + ex.Message; _store.Log(ActivityLevel.Error, LastResult); }
        finally { Running = false; Status = ""; _one.Release(); }
    }

    /// <summary>Seconds to wait for players to answer first; RESUMERELAY_CATCHUP_DELAY shortens it (the tests do).</summary>
    private static int Delay(int normal) => int.TryParse(Environment.GetEnvironmentVariable("RESUMERELAY_CATCHUP_DELAY"), out int s) ? s : normal;

    private static string Pretty(TimeSpan t) =>
        t.TotalDays >= 1.5 ? $"{Math.Round(t.TotalDays):N0} days" : t.TotalHours >= 1.5 ? $"{Math.Round(t.TotalHours):N0} hours" : $"{Math.Max(1, Math.Round(t.TotalMinutes)):N0} minutes";
}
