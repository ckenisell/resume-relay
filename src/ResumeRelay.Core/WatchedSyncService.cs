namespace ResumeRelay;

/// <summary>
/// Copies watched marks that someone sets or clears on a player (in the Zidoo's HT app, or in a Plex app) to the
/// other players, the way stops are copied.
///
/// Why it works by reading: a stop shows up in what a player says it's playing (asked every few seconds), but marking
/// a title watched plays nothing, and neither the Zidoo's HT app nor Plex announces it. So every few minutes (15 by
/// default) the players are read, the same read as Progress Reset and Watch Sync, and each file's mark is compared with
/// the previous read. A file whose mark changed on one player gets that mark on the others.
///  • A Zidoo that's playing something is left out of that read (it's read next time), so a read never disturbs playback.
///  • Marks Resume Relay itself writes are already in the last read (it updates it), so they aren't copied twice.
///  • A file changed in opposite ways on two players between reads is left alone (and logged).
///  • The first read only takes note of everything, as does a read after a long gap (over 3 intervals), so changes that
///    happened long ago are never pushed out unexpectedly.
/// </summary>
public sealed class WatchedSyncService : BackgroundService
{
    private readonly Store _store;
    private readonly RelayEngine _engine;
    private readonly ProgressService _progress;
    private DateTime _last = DateTime.MinValue;

    public string Status { get; private set; } = "";
    public DateTime? LastRunUtc { get; private set; }
    public string LastResult { get; private set; } = "";

    public WatchedSyncService(Store store, RelayEngine engine, ProgressService progress)
    {
        _store = store;
        _engine = engine;
        _progress = progress;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the players a few minutes to be found after starting (Catch Up reads them first).
        try { await Task.Delay(TimeSpan.FromSeconds(Delay(180)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var s = _store.Settings();
            int minutes = Math.Clamp(s.WatchedSyncMinutes, 2, 1440);
            if (s.WatchedSync && DateTime.UtcNow - _last >= TimeSpan.FromSeconds(Delay(minutes * 60)))
            {
                _last = DateTime.UtcNow;
                try { await RunAsync(TimeSpan.FromMinutes(minutes), ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { LastResult = "Stopped: " + ex.Message; }
                finally { Status = ""; }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Delay(30))), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Checks now (Settings: "Check now").</summary>
    public void CheckSoon() => _last = DateTime.MinValue;

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        var players = _store.Devices().Where(d => d.Enabled && _engine.LiveOf(d.Id).Online && (d.IsPlex || _engine.LiveOf(d.Id).Playing == null)).ToList();
        if (players.Count == 0) return;
        Status = $"Reading {players.Count} player{(players.Count == 1 ? "" : "s")}…";
        var before = players.ToDictionary(p => p.Id, p => _progress.WatchedSnapshot(p.Id));
        await _progress.ReadAndWaitAsync(players.Select(p => p.Id).ToList(), ct);
        LastRunUtc = DateTime.UtcNow;

        // What changed on each player since its previous read: file → new mark.
        var changed = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var p in players)
        {
            var b = before[p.Id];
            var a = _progress.WatchedSnapshot(p.Id);
            if (b == null || a == null || a.Value.ReadUtc <= b.Value.ReadUtc) continue;   // first read, or this read failed
            if (a.Value.ReadUtc - b.Value.ReadUtc > interval * 3 + TimeSpan.FromMinutes(10)) continue; // too long ago to trust
            var diff = a.Value.Marks.Where(kv => b.Value.Marks.TryGetValue(kv.Key, out var was) && was != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (diff.Count > 0) changed[p.Id] = diff;
        }
        // A file changed one way on one player and the other way on another: left alone.
        var conflicts = changed.SelectMany(c => c.Value).GroupBy(kv => kv.Key).Where(g => g.Select(x => x.Value).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        int files = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, diff) in changed)
        {
            var keys = diff.Keys.Where(k => !conflicts.Contains(k) && seen.Add(k)).ToList();
            if (keys.Count == 0) continue;
            var name = _store.Device(id)?.Name ?? id;
            int on = keys.Count(k => diff[k]), off = keys.Count - on;
            _store.Log(ActivityLevel.Info, $"Watched marks changed on {name}: {Describe(on, off)}. Copying to the other players.");
            if (_progress.SyncWatchedFrom(id, keys) is { } err) _store.Log(ActivityLevel.Warn, $"Copying watched marks from {name}: {err}");
            files += keys.Count;
        }
        if (conflicts.Count > 0)
            _store.Log(ActivityLevel.Warn, $"{conflicts.Count} file{(conflicts.Count == 1 ? " was" : "s were")} marked watched on one player and unwatched on another since the last check; left as they are.");
        LastResult = files == 0 ? "No watched marks changed." : $"{files:N0} file{(files == 1 ? "" : "s")} changed on a player: copied to the others.";
    }

    private static string Describe(int on, int off) =>
        string.Join(", ", new[] { on > 0 ? $"{on} marked watched" : "", off > 0 ? $"{off} marked unwatched" : "" }.Where(x => x.Length > 0));

    /// <summary>Seconds; RESUMERELAY_WATCHSYNC_DELAY shortens every wait (the tests use it).</summary>
    private static int Delay(int normal) => int.TryParse(Environment.GetEnvironmentVariable("RESUMERELAY_WATCHSYNC_DELAY"), out int s) ? Math.Min(normal, s) : normal;
}
