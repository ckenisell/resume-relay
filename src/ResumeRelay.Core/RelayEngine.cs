using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>
/// The heart of the app.
///
///  1. Every few seconds, ask each enabled player what it is playing (getPlayStatus).
///  2. When a player that was playing a file stops (806), switches to another file, or drops off the
///     network, that file's last position becomes a bookmark to copy to every other enabled player.
///  3. Each copy is queued per player (and saved to disk), so a sleeping player gets it when it wakes.
///  4. Before writing, the target's own entry for that file is looked up by path and checked:
///     the id must point at the same path, the target must not hold a newer bookmark, and it must not
///     already be at that position. After writing, the position is read back; the reply is not trusted.
/// </summary>
public sealed class RelayEngine : BackgroundService
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdentityCheckEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineBeforeRescan = TimeSpan.FromSeconds(90);
    private static readonly Regex EpisodeTag = new(@"S\d{1,2}E\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Store _store;
    private readonly ConcurrentDictionary<string, DeviceLive> _live = new();
    private readonly ConcurrentDictionary<string, DateTime> _identityCheckedUtc = new();
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private DateTime _lastRescanUtc = DateTime.MinValue;
    private int _rescanning;

    public RelayEngine(Store store) => _store = store;

    public DeviceLive LiveOf(string deviceId) => _live.GetOrAdd(deviceId, _ => new DeviceLive());

    public void NudgeQueue() => _queueSignal.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _store.Log(ActivityLevel.Info, $"Resume Relay started on .NET {Environment.Version}. Data folder: {_store.DataDirectory}");
        if (_store.Settings().WatchOnly)
            _store.Log(ActivityLevel.Warn, "Watch-only mode is ON: bookmarks are logged, not copied. Turn it off in Settings when ready.");

        var queueTask = Task.Run(() => QueueLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try { await PollAllAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, "Poll cycle failed: " + ex.Message); }

            var wait = TimeSpan.FromSeconds(_store.Settings().PollSeconds) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
        try { await queueTask; } catch (OperationCanceledException) { }
    }

    // ===================================================================== polling

    private async Task PollAllAsync(CancellationToken ct)
    {
        var devices = _store.Devices().Where(d => d.Enabled && d.Ip.Length > 0).ToList();
        await Task.WhenAll(devices.Select(d => PollOneAsync(d, ct)));
        MaybeRescanForMovedPlayers(devices, ct);
    }

    private async Task PollOneAsync(Device d, CancellationToken ct)
    {
        var live = LiveOf(d.Id);
        var status = await new ZidooClient(d.Ip, d.Port).GetPlayStatusAsync(TimeSpan.FromSeconds(3), ct);
        var now = DateTime.UtcNow;

        if (status.State == PlayState.Unreachable)
        {
            live.ConsecutiveFailures++;
            if (live.Online && live.ConsecutiveFailures >= 2)
            {
                live.Online = false;
                live.OfflineSinceUtc = now;
                _store.Log(ActivityLevel.Info, $"{d.Name} is not answering (asleep, off or unplugged).");
                if (live.Playing != null)
                {
                    // It went away mid-movie (powered off from the remote, power cut...). Its last known position is the bookmark.
                    var last = live.Playing;
                    live.Playing = null;
                    OnStopped(d, last, now, "player went offline");
                }
            }
            else if (!live.Online && live.OfflineSinceUtc == null)
            {
                live.OfflineSinceUtc = now;
            }
            return;
        }

        // Reachable.
        live.ConsecutiveFailures = 0;
        live.LastSeenUtc = now;
        if (!live.Online)
        {
            // Make sure the player at this address is still the one we think it is before trusting it.
            if (!await VerifyIdentityAsync(d, ct)) { live.OfflineSinceUtc ??= now; return; }
            bool wasKnownOffline = live.OfflineSinceUtc != null && live.LastSeenUtc != null;
            live.Online = true;
            live.OfflineSinceUtc = null;
            if (wasKnownOffline) _store.Log(ActivityLevel.Info, $"{d.Name} is back online.");
            NudgeQueue(); // deliver anything that waited for it
        }
        else if (now - _identityCheckedUtc.GetValueOrDefault(d.Id) > IdentityCheckEvery)
        {
            if (!await VerifyIdentityAsync(d, ct)) { live.Online = false; live.OfflineSinceUtc = now; return; }
        }

        var prev = live.Playing;
        if (status.State == PlayState.Playing)
        {
            if (prev != null && !string.Equals(prev.Path, status.Path, StringComparison.Ordinal))
                OnStopped(d, prev, now, "switched to another file"); // e.g. auto-play of the next episode

            live.Playing = new NowPlaying
            {
                Path = status.Path,
                SourceUrl = PathMapper.ToSourceUrl(status.Path) ?? "",
                Title = status.Title,
                PositionMs = status.PositionMs,
                DurationMs = status.DurationMs,
                Paused = status.Paused,
                UpdatedUtc = now,
            };
        }
        else // Idle
        {
            live.Playing = null;
            if (prev != null) OnStopped(d, prev, now, "stopped");
        }
    }

    private async Task<bool> VerifyIdentityAsync(Device d, CancellationToken ct)
    {
        var info = await new ZidooClient(d.Ip, d.Port).GetModelAsync(TimeSpan.FromSeconds(4), ct);
        if (info == null) return false;
        if (info.Identity != d.Id)
        {
            _store.Log(ActivityLevel.Warn, $"The player at {d.Ip} is no longer {d.Name} (its IP address was probably reassigned). Searching the network for {d.Name}…");
            _lastRescanUtc = DateTime.MinValue; // rescan right away
            return false;
        }
        _identityCheckedUtc[d.Id] = DateTime.UtcNow;
        if (info.Firmware.Length > 0 && info.Firmware != d.Firmware)
            _store.UpsertDiscovered(new Device { Id = d.Id, Ip = d.Ip, Port = d.Port, Model = info.Model, Firmware = info.Firmware, Mac = info.BestMac });
        return true;
    }

    /// <summary>A registered player that stays unreachable may simply have a new IP address from the router.
    /// Re-scan the network now and then, and move it by MAC.</summary>
    private void MaybeRescanForMovedPlayers(List<Device> devices, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        bool anyMissing = devices.Any(d =>
        {
            var l = LiveOf(d.Id);
            return !l.Online && l.OfflineSinceUtc != null && now - l.OfflineSinceUtc > OfflineBeforeRescan;
        }) || _lastRescanUtc == DateTime.MinValue && devices.Any(d => !LiveOf(d.Id).Online);

        if (!anyMissing || now - _lastRescanUtc < RescanEvery) return;
        if (Interlocked.Exchange(ref _rescanning, 1) == 1) return;
        _lastRescanUtc = now;
        _ = Task.Run(async () =>
        {
            try { await RescanAsync(addNew: false, ct); }
            catch { }
            finally { Interlocked.Exchange(ref _rescanning, 0); }
        }, ct);
    }

    /// <summary>Scans the network. Known players get their IP refreshed; unknown ones are added only when addNew is set.</summary>
    public async Task<List<(Device Device, bool IsNew)>> RescanAsync(bool addNew, CancellationToken ct = default)
    {
        var found = await Discovery.ScanAsync(ct);
        var known = _store.Devices().ToDictionary(d => d.Id);
        var result = new List<(Device, bool)>();
        foreach (var f in found)
        {
            bool isKnown = known.TryGetValue(f.Info.Identity, out var k);
            if (!isKnown && !addNew) continue;
            var dev = new Device
            {
                Id = f.Info.Identity,
                Name = isKnown ? k!.Name : DefaultName(f),
                Ip = f.Ip,
                Port = Discovery.ZidooPort,
                Model = f.Info.Model,
                Firmware = f.Info.Firmware,
                Mac = f.Info.BestMac,
            };
            if (isKnown && k!.Ip != f.Ip)
                _store.Log(ActivityLevel.Info, $"{k.Name} moved from {k.Ip} to {f.Ip}; updated.");
            bool isNew = _store.UpsertDiscovered(dev);
            if (isNew) _store.Log(ActivityLevel.Info, $"Found a new player: {dev.Name} ({dev.Model}, {dev.Ip}).");
            result.Add((_store.Device(dev.Id)!, isNew));
        }
        return result;
    }

    public async Task<(Device? Device, string Error)> AddByIpAsync(string ip, int port, CancellationToken ct = default)
    {
        var info = await new ZidooClient(ip, port).GetModelAsync(TimeSpan.FromSeconds(5), ct);
        if (info == null) return (null, $"Nothing answered as a Zidoo at {ip}:{port}. Is it powered on?");
        if (info.Identity.Length == 0) return (null, "The player did not report a MAC address or ID.");
        var dev = new Device { Id = info.Identity, Name = DefaultName(new Discovery.Found(ip, info)), Ip = ip, Port = port, Model = info.Model, Firmware = info.Firmware, Mac = info.BestMac };
        bool isNew = _store.UpsertDiscovered(dev);
        _store.Log(ActivityLevel.Info, isNew ? $"Added {dev.Name} ({dev.Model}, {ip})." : $"{_store.Device(dev.Id)!.Name} is already registered; address updated to {ip}.");
        return (_store.Device(dev.Id), "");
    }

    private static string DefaultName(Discovery.Found f) => $"{(f.Info.Model.Length > 0 ? f.Info.Model : "Zidoo")} ({f.Ip})";

    // ===================================================================== stop → queue

    private void OnStopped(Device source, NowPlaying last, DateTime stoppedUtc, string how)
    {
        string label = Label(last.Title, last.Path);

        if (last.SourceUrl.Length == 0)
        {
            _store.Log(ActivityLevel.Skip, $"{label} {how} on {source.Name} at {Clock(last.PositionMs)}, but it plays from a drive other players cannot see ({last.Path}).");
            return;
        }

        // This player has just watched the file itself, so any older copy still waiting to reach it is out of date.
        if (_store.RemovePending(source.Id + "|" + last.SourceUrl.ToLowerInvariant()))
            _store.Log(ActivityLevel.Info, $"Discarded an older waiting copy of {label} for {source.Name}: it was just watched there.");

        // Any position is copied, however short. 0:00 is the one exception: writing 0 tells the player to erase the
        // bookmark and the watched flag, which is not what stopping at the very start means.
        if (last.PositionMs <= 0)
        {
            _store.Log(ActivityLevel.Skip, $"{label} {how} on {source.Name} at 0:00; nothing to copy.");
            return;
        }

        var targets = _store.Devices().Where(d => d.Enabled && d.Id != source.Id).ToList();
        _store.Log(ActivityLevel.Info, $"{label} {how} on {source.Name} at {Clock(last.PositionMs)}" +
                                       (targets.Count == 0 ? ". No other players to copy to." : $". Copying to {string.Join(", ", targets.Select(t => t.Name))}…"));

        long lastWatch = new DateTimeOffset(stoppedUtc).ToUnixTimeMilliseconds();
        foreach (var t in targets)
        {
            _store.Enqueue(new PendingCopy
            {
                TargetId = t.Id,
                SourceUrl = last.SourceUrl,
                Title = label,
                FromName = source.Name,
                PositionMs = last.PositionMs,
                DurationMs = last.DurationMs,
                LastWatchTimeMs = lastWatch,
                CreatedUtc = stoppedUtc,
            });
        }
        NudgeQueue();
    }

    // ===================================================================== queue → players

    private async Task QueueLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _queueSignal.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
            while (_queueSignal.CurrentCount > 0) _queueSignal.Wait(0); // collapse bursts

            try
            {
                int expired = _store.ExpirePending(TimeSpan.FromDays(_store.Settings().PendingMaxDays));
                if (expired > 0) _store.Log(ActivityLevel.Warn, $"{expired} waiting bookmark copies expired (their player stayed offline too long).");

                // Players whose Home Theater app did not answer in this pass: their other copies wait for the next pass
                // instead of each spending its own timeout.
                var htNotReady = new HashSet<string>();
                foreach (var job in _store.Pending())
                {
                    if (ct.IsCancellationRequested) break;
                    if (htNotReady.Contains(job.TargetId)) continue;
                    if (job.LastAttemptUtc.HasValue && DateTime.UtcNow - job.LastAttemptUtc < RetryBackoff && job.LastNote.StartsWith("retry")) continue;
                    await ProcessAsync(job, htNotReady, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, "Copy queue failed: " + ex.Message); }
        }
    }

    private async Task ProcessAsync(PendingCopy job, HashSet<string> htNotReady, CancellationToken ct)
    {
        var target = _store.Device(job.TargetId);
        if (target == null || !target.Enabled) { _store.Dequeue(job); return; }

        var live = LiveOf(target.Id);
        if (!live.Online)
        {
            if (job.LastNote != "waiting") { job.LastNote = "waiting"; _store.UpdatePending(job); }
            return; // stays queued until the player answers again
        }

        string what = $"{job.Title} ({Clock(job.PositionMs)}) from {job.FromName} to {target.Name}";

        if (live.Playing != null && PathMapper.Same(live.Playing.SourceUrl, job.SourceUrl))
        {
            _store.Log(ActivityLevel.Skip, $"Not copying {what}: it is playing there right now.");
            _store.Dequeue(job);
            return;
        }

        var api = new ZidooClient(target.Ip, target.Port);

        // 1. Find the file in the target's own library, by path.
        var (lookup, hit) = await api.GetIdByPathnameAsync(job.SourceUrl, ct);
        if (lookup == Lookup.NotFound)
        {
            _store.Log(ActivityLevel.Skip, $"Not copying {what}: {target.Name}'s library does not have that file.");
            _store.Dequeue(job);
            return;
        }
        // The player answers but its library does not: the Home Theater app is closed (the library API only runs
        // while it is open). That is a reason to wait, not a failure, so it never counts toward giving up.
        if (lookup == Lookup.Failed) { WaitForHomeTheater(job, target, htNotReady); return; }

        // 2. Safety check: the id we are about to write must belong to this exact file.
        var entry = await api.GetEntryAsync(hit!.Id, ct);
        if (entry == null) { WaitForHomeTheater(job, target, htNotReady); return; }
        if (!PathMapper.Same(entry.Path, job.SourceUrl))
        {
            _store.Log(ActivityLevel.Error, $"Safety check stopped {what}: id {hit.Id} on {target.Name} points at \"{entry.Path}\", not this file. Nothing written.");
            _store.Dequeue(job);
            return;
        }

        // 3. Already there?
        if (Math.Abs(entry.PlayPointMs - job.PositionMs) <= 10_000)
        {
            _store.Log(ActivityLevel.Skip, $"{target.Name} already has {job.Title} at {Clock(entry.PlayPointMs)}.");
            _store.Dequeue(job);
            return;
        }

        // The latest stop always wins: whatever bookmark the target holds is replaced (owner's choice, 0.1.3).

        if (_store.Settings().WatchOnly)
        {
            _store.Log(ActivityLevel.Info, $"[watch-only] Would copy {what} (entry {entry.Id}, currently {Clock(entry.PlayPointMs)}).");
            _store.Dequeue(job);
            return;
        }

        // 5. Write, then believe only what we read back.
        await api.SetPlayPointAsync(entry.Id, job.PositionMs, job.DurationMs, job.LastWatchTimeMs, ct);
        int? readBack = null;
        for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800 + 700 * i), ct);
            readBack = (await api.GetEntryAsync(entry.Id, ct))?.PlayPointMs;
            if (readBack.HasValue && Landed(readBack.Value, job)) break;
        }

        if (readBack.HasValue && Landed(readBack.Value, job))
        {
            _store.Log(ActivityLevel.Ok, $"Copied {what}" + (NearEnd(job) ? " (marked watched)." : "."));
            _store.Dequeue(job);
        }
        else
        {
            Retry(job, what, readBack.HasValue ? $"the player still shows {Clock(readBack.Value)} after the write" : "could not read the bookmark back");
        }
    }

    private void WaitForHomeTheater(PendingCopy job, Device target, HashSet<string> htNotReady)
    {
        htNotReady.Add(target.Id);
        const string note = "waiting for the Home Theater app to be opened";
        if (job.LastNote == note) return;
        bool firstForPlayer = !_store.Pending().Any(p => p.TargetId == target.Id && p.LastNote == note);
        job.LastNote = note;
        _store.UpdatePending(job);
        if (firstForPlayer)
            _store.Log(ActivityLevel.Info, $"{target.Name} is on, but its Home Theater app isn't open. Copies for it will go through once it is.");
    }

    private void Retry(PendingCopy job, string what, string why)
    {
        int attempt = 1 + (job.LastNote.StartsWith("retry ") && int.TryParse(job.LastNote.Split(' ')[1], out int n) ? n : 0);
        if (attempt >= MaxAttempts)
        {
            _store.Log(ActivityLevel.Error, $"Gave up copying {what}: {why}.");
            _store.Dequeue(job);
            return;
        }
        job.LastAttemptUtc = DateTime.UtcNow;
        job.LastNote = $"retry {attempt}: {why}";
        _store.UpdatePending(job);
        _store.Log(ActivityLevel.Warn, $"Will retry {what}: {why}.");
    }

    /// <summary>A write counts as landed when the player now shows the position we sent (±2 s). Near the end the
    /// player may round up to the full duration as it marks the file watched; that counts too.</summary>
    private static bool Landed(int readBack, PendingCopy job) =>
        Math.Abs(readBack - job.PositionMs) <= 2_000 ||
        (NearEnd(job) && readBack >= job.PositionMs - 2_000 && (job.DurationMs <= 0 || readBack <= job.DurationMs + 2_000));

    /// <summary>The player marks a file watched when the point is within 5 minutes of the end or at 93% or more.</summary>
    private static bool NearEnd(PendingCopy j) =>
        j.DurationMs > 0 && (j.PositionMs >= j.DurationMs * 0.93 || j.DurationMs - j.PositionMs <= 5 * 60_000);

    // ===================================================================== helpers

    public static string Clock(int ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Label(string title, string path)
    {
        string file = PathMapper.FileName(path);
        var ep = EpisodeTag.Match(file);
        string t = title.Trim().Length > 0 ? title.Trim() : file;
        return ep.Success && !t.Contains(ep.Value, StringComparison.OrdinalIgnoreCase) ? $"{t} {ep.Value.ToUpperInvariant()}" : t;
    }
}
