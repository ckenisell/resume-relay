using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>
/// The heart of the app.
///
///  1. Every few seconds, ask each enabled player what it is playing (getPlayStatus).
///  2. When a player that was playing a file stops (806), switches to another file, or drops off the
///     network, that file's bookmark is copied to every other enabled player. The position copied is the
///     one the player itself saved when it stopped (read back from it), not the last polled guess.
///  3. Each stop becomes a card on the Activity tab, with the title's poster and a chip per player.
///  4. Each copy is queued per player (and saved to disk), so a sleeping player gets it when it wakes.
///  5. Before writing, the target's own entry for that file is looked up by path and checked: the id
///     must point at the same path, and it must not already be at that position. After writing, the
///     position is read back; the reply is not trusted.
/// It also turns players on (Wake-on-LAN) and off, and opens their Home Theater app, as Z-Keeper does.
/// </summary>
public sealed partial class RelayEngine : BackgroundService
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdentityCheckEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineBeforeRescan = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HomeTheaterCheckEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HtVersionCheckEvery = TimeSpan.FromMinutes(30);
    /// <summary>Shown as the source of a reset started with "Reset Progress".</summary>
    public const string ManualResetFrom = "Resume Relay";
    private static readonly TimeSpan PowerOnTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan WakePacketEvery = TimeSpan.FromSeconds(10);
    private static readonly Regex EpisodeTag = new(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string WaitOff = "off";
    private const string WaitHomeTheater = "ht";

    private readonly Store _store;
    private readonly ConcurrentDictionary<string, DeviceLive> _live = new();
    private readonly ConcurrentDictionary<string, DateTime> _identityCheckedUtc = new();
    private readonly ConcurrentDictionary<string, Task<TitleInfo>> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private DateTime _lastRescanUtc = DateTime.MinValue;
    private int _rescanning;
    private CancellationToken _stopping;

    private sealed record TitleInfo(string Poster, string Subtitle);

    /// <summary>A player (or the Plex server, for a Plex user) answers again after being off.</summary>
    public event Action<string>? CameOnline;

    public RelayEngine(Store store) => _store = store;

    public DeviceLive LiveOf(string deviceId) => _live.GetOrAdd(deviceId, _ => new DeviceLive());

    public void NudgeQueue() => _queueSignal.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
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
        var devices = _store.Devices().Where(d => d.Enabled && (d.IsPlex || d.Ip.Length > 0)).ToList();
        await Task.WhenAll(devices.Select(d => d.IsPlex ? PollPlexAsync(d, ct) : PollOneAsync(d, ct)));
        MaybeRescanForMovedPlayers(devices.Where(d => !d.IsPlex).ToList(), ct);
    }

    private async Task PollOneAsync(Device d, CancellationToken ct)
    {
        var live = LiveOf(d.Id);
        var api = new ZidooClient(d.Ip, d.Port);
        var status = await api.GetPlayStatusAsync(TimeSpan.FromSeconds(3), ct);
        var now = DateTime.UtcNow;

        if (status.State == PlayState.Unreachable)
        {
            live.ConsecutiveFailures++;
            if (live.Online && live.ConsecutiveFailures >= 2)
            {
                live.Online = false;
                live.OfflineSinceUtc = now;
                live.HomeTheaterRunning = null;
                // Not worth a line when Resume Relay switched it off itself ("Turned X off." says it).
                if (live.Power != PowerAction.TurningOff && now - live.PowerSinceUtc > TimeSpan.FromSeconds(90))
                    _store.Log(ActivityLevel.Info, $"{d.Name} is not answering (asleep, off or unplugged).");
                if (live.Playing != null)
                {
                    // It went away mid-film (powered off, power cut...). It can't be asked any more, so its last
                    // polled position is the best bookmark there is.
                    var last = live.Playing;
                    live.Playing = null;
                    OnStopped(d, last, now, "player went offline", canAskPlayer: false);
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
            bool wasKnownOffline = live.OfflineSinceUtc != null;
            live.Online = true;
            live.OfflineSinceUtc = null;
            live.HomeTheaterCheckedUtc = DateTime.MinValue; // check the app straight away
            live.HtVersionCheckedUtc = DateTime.MinValue;   // ...and its version (it may have been updated)
            if (wasKnownOffline && live.Power != PowerAction.TurningOn) _store.Log(ActivityLevel.Info, $"{d.Name} is back online.");
            CameOnline?.Invoke(d.Id);
            NudgeQueue(); // deliver anything that waited for it
        }
        else if (now - _identityCheckedUtc.GetValueOrDefault(d.Id) > IdentityCheckEvery)
        {
            if (!await VerifyIdentityAsync(d, ct)) { live.Online = false; live.OfflineSinceUtc = now; return; }
        }

        var prev = live.Playing;
        if (status.State == PlayState.Playing)
        {
            bool sameFile = prev != null && string.Equals(prev.Path, status.Path, StringComparison.Ordinal);
            if (prev != null && !sameFile)
                OnStopped(d, prev, now, "switched to another file", canAskPlayer: true); // e.g. auto-play of the next episode

            var np = new NowPlaying
            {
                Path = status.Path,
                SourceUrl = PathMapper.ToSourceUrl(status.Path) ?? "",
                Title = status.Title,
                PositionMs = status.PositionMs,
                DurationMs = status.DurationMs,
                Paused = status.Paused,
                UpdatedUtc = now,
                Poster = sameFile ? prev!.Poster : "",
                Subtitle = sameFile ? prev!.Subtitle : "",
            };
            live.Playing = np;
            live.HomeTheaterRunning = true; // it's playing through it
            live.HomeTheaterCheckedUtc = now;

            // Look the title up once (in the background) for the card's poster and episode number.
            if (!sameFile && np.SourceUrl.Length > 0)
                _ = FillNowPlayingAsync(d, np);
            await CheckHtVersionAsync(d, live, api, ct);
        }
        else // Idle
        {
            live.Playing = null;
            if (prev != null) OnStopped(d, prev, now, "stopped", canAskPlayer: true);
            await CheckHomeTheaterAsync(d, live, api, force: false, ct);
        }
    }

    private async Task FillNowPlayingAsync(Device d, NowPlaying np)
    {
        try
        {
            var info = await GetTitleInfoAsync(d, np.SourceUrl, np.Path);
            np.Poster = info.Poster;
            np.Subtitle = info.Subtitle;
        }
        catch { }
    }

    /// <summary>Is the player's Home Theater app running? Checked every 20 s while the player is idle; a change to
    /// "running" releases any copies that were waiting for it.</summary>
    /// <summary>The Home Theater app version shown on the card: read when a player comes online, then every 30 minutes.</summary>
    private async Task CheckHtVersionAsync(Device d, DeviceLive live, ZidooClient api, CancellationToken ct)
    {
        if (DateTime.UtcNow - live.HtVersionCheckedUtc < HtVersionCheckEvery) return;
        live.HtVersionCheckedUtc = DateTime.UtcNow;
        if (await api.GetHomeTheaterVersionAsync(ct) is { } v) _store.SetHomeTheaterVersion(d.Id, v.Name, v.Code);
    }

    private async Task CheckHomeTheaterAsync(Device d, DeviceLive live, ZidooClient api, bool force, CancellationToken ct)
    {
        await CheckHtVersionAsync(d, live, api, ct);
        if (!force && DateTime.UtcNow - live.HomeTheaterCheckedUtc < HomeTheaterCheckEvery) return;
        bool running = await api.IsHomeTheaterRunningAsync(ct);
        bool? before = live.HomeTheaterRunning;
        live.HomeTheaterRunning = running;
        live.HomeTheaterCheckedUtc = DateTime.UtcNow;
        if (running && before == false) NudgeQueue();
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
        // Keep firmware and both MACs current (both are needed for Wake-on-LAN; older installs only stored one).
        if ((info.Firmware.Length > 0 && info.Firmware != d.Firmware) || d.NetMac != info.WiredMac || d.WifiMac != info.WirelessMac)
            _store.UpsertDiscovered(new Device
            {
                Id = d.Id, Ip = d.Ip, Port = d.Port, Model = info.Model, Firmware = info.Firmware,
                Mac = info.BestMac, NetMac = info.WiredMac, WifiMac = info.WirelessMac,
            });
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
            var dev = FromModel(f.Info, f.Ip, Discovery.ZidooPort, isKnown ? k!.Name : DefaultName(f));
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
        var dev = FromModel(info, ip, port, DefaultName(new Discovery.Found(ip, info)));
        bool isNew = _store.UpsertDiscovered(dev);
        _store.Log(ActivityLevel.Info, isNew ? $"Added {dev.Name} ({dev.Model}, {ip})." : $"{_store.Device(dev.Id)!.Name} is already registered; address updated to {ip}.");
        return (_store.Device(dev.Id), "");
    }

    private static Device FromModel(ModelInfo info, string ip, int port, string name) => new()
    {
        Id = info.Identity, Name = name, Ip = ip, Port = port, Model = info.Model, Firmware = info.Firmware,
        Mac = info.BestMac, NetMac = info.WiredMac, WifiMac = info.WirelessMac,
    };

    private static string DefaultName(Discovery.Found f) => $"{(f.Info.Model.Length > 0 ? f.Info.Model : "Zidoo")} ({f.Ip})";

    // ===================================================================== power and Home Theater

    /// <summary>Turns a player on (Wake-on-LAN) and, unless asked not to, opens its Home Theater app once it answers.
    /// Mirrors Z-Keeper: packets to the wired and Wi-Fi MAC every 10 s for up to 2 minutes.</summary>
    public string? StartPowerOn(string deviceId, bool launchHomeTheater)
    {
        var d = _store.Device(deviceId);
        if (d == null) return "Unknown player.";
        if (d.IsPlex) return $"{d.Name} is a Plex user, not a player: there's nothing to turn on.";
        var live = LiveOf(d.Id);
        if (live.Power != PowerAction.None) return $"{d.Name} is already being switched.";
        var macs = new[] { d.NetMac, d.WifiMac, d.Mac }.Where(m => WakeOnLan.ParseMac(m) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (macs.Count == 0) return $"{d.Name} has no MAC address on record, so it can't be woken. Scan the network while it's on.";

        live.Power = PowerAction.TurningOn;
        live.PowerSinceUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var api = new ZidooClient(d.Ip, d.Port);
            var until = DateTime.UtcNow + PowerOnTimeout;
            var nextPacket = DateTime.MinValue;
            bool up = false;
            try
            {
                while (DateTime.UtcNow < until && !_stopping.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= nextPacket)
                    {
                        foreach (var mac in macs) WakeOnLan.Send(mac, d.Ip);
                        nextPacket = DateTime.UtcNow + WakePacketEvery;
                    }
                    if (await api.GetModelAsync(TimeSpan.FromSeconds(3), _stopping) != null) { up = true; break; }
                    await Task.Delay(3000, _stopping);
                }
                if (!up)
                {
                    _store.Log(ActivityLevel.Warn, $"{d.Name} didn't answer within 2 minutes. Wake-on-LAN only works when the player is in standby " +
                                                   "(not unplugged), on a wired connection, with its network wake option on.");
                    return;
                }
                if (launchHomeTheater)
                {
                    await api.LaunchHomeTheaterAsync(_stopping);
                    await WaitForHomeTheaterAsync(d, api, TimeSpan.FromSeconds(45));
                }
                _store.Log(ActivityLevel.Info, $"Turned {d.Name} on" + (launchHomeTheater ? " and opened Home Theater." : "."));
                NudgeQueue();
            }
            catch (OperationCanceledException) { }
            finally { live.Power = PowerAction.None; }
        });
        return null;
    }

    /// <summary>Puts a player in standby (the remote's power key), then waits for it to go quiet.</summary>
    public string? StartPowerOff(string deviceId)
    {
        var d = _store.Device(deviceId);
        if (d == null) return "Unknown player.";
        if (d.IsPlex) return $"{d.Name} is a Plex user, not a player: there's nothing to turn off.";
        var live = LiveOf(d.Id);
        if (live.Power != PowerAction.None) return $"{d.Name} is already being switched.";
        live.Power = PowerAction.TurningOff;
        live.PowerSinceUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var api = new ZidooClient(d.Ip, d.Port);
            try
            {
                await api.PowerOffAsync(_stopping);
                var until = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < until && !_stopping.IsCancellationRequested)
                {
                    await Task.Delay(2000, _stopping);
                    if (await api.GetModelAsync(TimeSpan.FromSeconds(2), _stopping) == null)
                    {
                        _store.Log(ActivityLevel.Info, $"Turned {d.Name} off.");
                        // Keep "turning off" until the poller has also seen it go (it needs two missed checks, a few
                        // seconds). Ending here would show the player as on again for those seconds. The poller is
                        // left to mark it off itself, because that is also where a film playing at the time is copied.
                        var settle = DateTime.UtcNow.AddSeconds(30);
                        while (live.Online && DateTime.UtcNow < settle && !_stopping.IsCancellationRequested)
                            await Task.Delay(500, _stopping);
                        return;
                    }
                }
                _store.Log(ActivityLevel.Warn, $"{d.Name} is still answering a minute after being told to turn off.");
            }
            catch (OperationCanceledException) { }
            finally { live.Power = PowerAction.None; }
        });
        return null;
    }

    /// <summary>Launch HT: asks the player to open the HT app, then waits (up to 30 s) until the app answers, so the
    /// page can keep its spinner going and say when it has started. Error text, or null once it is running.</summary>
    public async Task<string?> LaunchHomeTheaterAsync(string deviceId, CancellationToken ct)
    {
        var d = _store.Device(deviceId);
        if (d == null) return "Unknown player.";
        if (d.IsPlex) return $"{d.Name} is a Plex user: there's no Home Theater app to open.";
        var api = new ZidooClient(d.Ip, d.Port);
        if (!await api.LaunchHomeTheaterAsync(ct)) return $"{d.Name} didn't accept the request. Is it on?";
        if (await WaitForHomeTheaterAsync(d, api, TimeSpan.FromSeconds(30), interval: 1000))
        {
            _store.Log(ActivityLevel.Info, $"Opened the Home Theater app on {d.Name}.");
            return null;
        }
        _ = WaitForHomeTheaterAsync(d, api, TimeSpan.FromSeconds(60)); // keep watching in the background
        return $"The Home Theater app on {d.Name} hasn't answered yet. It may still be starting; its card will update when it does.";
    }

    private async Task<bool> WaitForHomeTheaterAsync(Device d, ZidooClient api, TimeSpan max, int interval = 2500)
    {
        var live = LiveOf(d.Id);
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && !_stopping.IsCancellationRequested)
        {
            await CheckHomeTheaterAsync(d, live, api, force: true, _stopping);
            if (live.HomeTheaterRunning == true) { NudgeQueue(); return true; }
            try { await Task.Delay(interval, _stopping); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    // ===================================================================== stop → card → queue

    private void OnStopped(Device source, NowPlaying last, DateTime stoppedUtc, string how, bool canAskPlayer) =>
        _ = Task.Run(async () =>
        {
            try { await HandleStopAsync(source, last, stoppedUtc, how, canAskPlayer); }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, $"Could not process the stop on {source.Name}: {ex.Message}"); }
        });

    private async Task HandleStopAsync(Device source, NowPlaying last, DateTime stoppedUtc, string how, bool canAskPlayer)
    {
        string label = Label(last.Title, last.Path);

        if (last.SourceUrl.Length == 0 && source.IsPlex)
        {
            _store.Log(ActivityLevel.Skip, $"{label} {how} on {source.Name} at {Clock(last.PositionMs)}, but Plex's path for it ({last.Path}) isn't covered by the folder mapping on the Plex card, so the Zidoos can't be matched to it.");
            return;
        }
        if (last.SourceUrl.Length == 0)
        {
            _store.Log(ActivityLevel.Skip, $"{label} {how} on {source.Name} at {Clock(last.PositionMs)}, but it plays from a drive other players cannot see ({last.Path}).");
            return;
        }

        // This player has just watched the file itself, so any older copy still waiting to reach it is out of date.
        var superseded = _store.RemovePending(source.Id + "|" + last.SourceUrl.ToLowerInvariant());
        if (superseded != null)
        {
            _store.UpdateEventTarget(superseded.EventId, source.Id, TargetState.Skipped, "watched there instead", superseded.SourceUrl);
            _store.Log(ActivityLevel.Info, $"Discarded an older waiting copy of {label} for {source.Name}: it was just watched there.");
        }

        // The polled position can be up to one poll interval old. The player saved its own bookmark when it
        // stopped; ask for it, so every room gets the exact second.
        int position = !canAskPlayer ? last.PositionMs
            : source.IsPlex ? await ExactPlexStopPositionAsync(source, last, stoppedUtc)
            : await ExactStopPositionAsync(source, last);

        // Any position is copied, however short. A stop at 0:00 (under one second) means "start over": the other
        // players get the title's progress reset, which erases their bookmark and their watched mark.
        bool reset = position < 1000;
        if (reset) position = 0;

        var info = await GetTitleInfoAsync(source, last.SourceUrl, last.Path);
        var targets = _store.Devices().Where(d => d.Enabled && d.Id != source.Id).ToList();
        bool finished = !reset && NearEnd(position, last.DurationMs);

        var ev = new StopEvent
        {
            TimeUtc = stoppedUtc,
            Title = TitleOnly(last.Title, last.Path),
            Subtitle = info.Subtitle,
            FileName = PathMapper.FileName(last.Path),
            SourceUrl = last.SourceUrl,
            FromName = source.Name,
            FromDeviceId = source.Id,
            FromKind = KindOf(source),
            PositionMs = position,
            DurationMs = last.DurationMs,
            Finished = finished,
            Reset = reset,
            Poster = info.Poster.Length > 0 ? info.Poster : last.Poster,
            Targets = targets.Select(t => new StopTarget { DeviceId = t.Id, Name = t.Name, Kind = KindOf(t) }).ToList(),
        };
        _store.AddEvent(ev);
        _store.LogCopy(ActivityLevel.Info, $"{label} {how} on {source.Name} at {Clock(position)}" +
                                           (targets.Count == 0 ? ". No other players to copy to."
                                            : reset ? $". Resetting its progress on {string.Join(", ", targets.Select(t => t.Name))}…"
                                            : $". Copying to {string.Join(", ", targets.Select(t => t.Name))}…"));

        long lastWatch = new DateTimeOffset(stoppedUtc).ToUnixTimeMilliseconds();
        foreach (var t in targets)
        {
            var replaced = _store.Enqueue(new PendingCopy
            {
                TargetId = t.Id,
                SourceUrl = last.SourceUrl,
                Title = label,
                FromName = source.Name,
                PositionMs = position,
                DurationMs = last.DurationMs,
                LastWatchTimeMs = lastWatch,
                CreatedUtc = stoppedUtc,
                EventId = ev.Id,
            });
            if (replaced != null)
                _store.UpdateEventTarget(replaced.EventId, t.Id, TargetState.Skipped, "replaced by a later stop", replaced.SourceUrl);
        }
        // Finished on a Zidoo: the HT app keeps the stop position on a watched file. Clear it there too, so every player
        // stores "finished" the same way (watched, no resume point). Not shown as a chip on the card.
        if (finished && !source.IsPlex)
            _store.Enqueue(new PendingCopy
            {
                TargetId = source.Id, SourceUrl = last.SourceUrl, Title = label, FromName = source.Name,
                PositionMs = position, DurationMs = last.DurationMs, LastWatchTimeMs = lastWatch, CreatedUtc = stoppedUtc,
            });
        NudgeQueue();
    }

    /// <summary>Raised when a copy or reset lands on a player (device id, that player's file id, position, last-watched
    /// unix ms; 0 and -1 for a reset), so the Progress Reset tab can update its list without reading the players again.</summary>
    public event Action<string, int, int, long, bool>? FileWritten;

    /// <summary>"Reset Progress" on an Activity card: resets that card's file everywhere.</summary>
    public (StopEvent? Card, string Error) ResetEverywhere(string eventId)
    {
        var from = _store.Events(300).FirstOrDefault(e => e.Id == eventId);
        if (from == null) return (null, "That card no longer exists.");
        if (from.SourceUrl.Length == 0) return (null, "This card was made by an older version of Resume Relay and doesn't know where the file is.");
        return ResetFile(from.Title, from.Subtitle, from.FileName, from.SourceUrl, from.Poster, from.DurationMs);
    }

    /// <summary>
    /// Clears one file's bookmark and watched mark on every player, including the one it was stopped on. It goes through
    /// the same queue as a stop at 0:00, so each reset is checked by reading it back, and a player that's off gets it
    /// when it comes back. Shown as a new card "from Resume Relay".
    /// </summary>
    public (StopEvent? Card, string Error) ResetFile(string title, string subtitle, string fileName, string sourceUrl, string poster, int durationMs)
    {
        var targets = _store.Devices().Where(d => d.Enabled).ToList();
        if (targets.Count == 0) return (null, "There are no players to reset.");
        var ev = NewResetCard(title, subtitle, fileName, sourceUrl, poster, durationMs, series: false, targets);
        string label = subtitle.Length > 0 && subtitle.StartsWith('S') ? $"{title} {subtitle}" : title;
        _store.LogCopy(ActivityLevel.Info, $"Reset Progress pressed for {fileName}. Resetting it on {string.Join(", ", targets.Select(t => t.Name))}…");
        EnqueueResets(ev, targets, sourceUrl, label, durationMs, kind: "");
        return (ev, "");
    }

    /// <summary>
    /// "Reset TV Show": clears the bookmark and watched mark of every episode of every season of a series, on every
    /// player. The series is found on each player from one of its episodes' paths, so each player uses its own ids.
    /// </summary>
    public (StopEvent? Card, string Error) ResetSeries(string series, string anyEpisodeSourceUrl, string poster)
    {
        var targets = _store.Devices().Where(d => d.Enabled).ToList();
        if (targets.Count == 0) return (null, "There are no players to reset.");
        var ev = NewResetCard(series, "All episodes", "", anyEpisodeSourceUrl, poster, 0, series: true, targets);
        _store.LogCopy(ActivityLevel.Info, $"Reset TV Show pressed for {series}. Resetting every episode on {string.Join(", ", targets.Select(t => t.Name))}…");
        EnqueueResets(ev, targets, anyEpisodeSourceUrl, series, 0, kind: PendingCopy.SeriesKind);
        return (ev, "");
    }

    /// <summary>
    /// A copy made on the Progress Reset tab: one player's progress for a file (dragged onto other players' columns)
    /// goes to those players through the same queue as a stop, so each write is read back, and a player that's off
    /// gets it when it's back. 0 resets the file there; the file's length marks it watched.
    /// </summary>
    public (StopEvent? Card, string Error) CopyFile(string title, string subtitle, string fileName, string sourceUrl, string poster,
        int durationMs, Device source, int positionMs, List<Device> targets, List<UndoItem>? undo = null)
    {
        if (targets.Count == 0) return (null, "Nothing to copy to.");
        bool reset = positionMs <= 0;
        var ev = new StopEvent
        {
            TimeUtc = DateTime.UtcNow,
            Title = title,
            Subtitle = subtitle,
            FileName = fileName,
            SourceUrl = sourceUrl,
            FromName = source.Name,
            FromKind = KindOf(source),
            PositionMs = Math.Max(0, positionMs),
            DurationMs = durationMs,
            Finished = !reset && NearEnd(positionMs, durationMs),
            Reset = reset,
            Copied = true,
            Poster = poster,
            Undo = undo ?? new List<UndoItem>(),
            Targets = targets.Select(t => new StopTarget { DeviceId = t.Id, Name = t.Name, Kind = KindOf(t) }).ToList(),
        };
        _store.AddEvent(ev);
        string label = subtitle.Length > 0 && subtitle.StartsWith('S') ? $"{title} {subtitle}" : title;
        _store.LogCopy(ActivityLevel.Info, $"Progress Reset: copying {fileName} ({(reset ? "not started" : Clock(positionMs))}) from {source.Name} to {string.Join(", ", targets.Select(t => t.Name))}…");
        long now = new DateTimeOffset(ev.TimeUtc).ToUnixTimeMilliseconds();
        foreach (var t in targets)
        {
            var replaced = _store.Enqueue(new PendingCopy
            {
                TargetId = t.Id,
                SourceUrl = sourceUrl,
                Title = label,
                FromName = source.Name,
                PositionMs = Math.Max(0, positionMs),
                DurationMs = durationMs,
                LastWatchTimeMs = now,
                CreatedUtc = ev.TimeUtc,
                EventId = ev.Id,
            });
            if (replaced != null) _store.UpdateEventTarget(replaced.EventId, t.Id, TargetState.Skipped, "replaced by a later copy", replaced.SourceUrl);
        }
        NudgeQueue();
        return (ev, "");
    }

    /// <summary>One file of a batch: where to send it, and what position (0 resets; its length marks it watched).</summary>
    public sealed record BatchItem(string SourceUrl, string Label, int PositionMs, int DurationMs, List<Device> Targets, string FromName = "");

    /// <summary>
    /// Many copies at once (Copy all on Progress Reset, an Undo): one Activity card whose chips count the files, and one
    /// queued copy per file and player, handled like any stop (read back, waits for players that are off).
    /// </summary>
    public StopEvent CopyBatch(string title, string subtitle, string summary, string fromName, string fromKind, List<BatchItem> items, List<UndoItem> undo, string undoOf = "")
    {
        var targets = items.SelectMany(i => i.Targets).DistinctBy(t => t.Id).ToList();
        var ev = new StopEvent
        {
            TimeUtc = DateTime.UtcNow, Title = title, Subtitle = subtitle, Summary = summary, FromName = fromName, FromKind = fromKind,
            Batch = true, Copied = true, Files = items.Select(i => i.SourceUrl).Distinct().Count(), Undo = undo, UndoOf = undoOf,
            Changes = items.Select(i => new BatchChange
            {
                Key = Store.ItemKey(i.SourceUrl), Title = i.Label, FromName = i.FromName, DeviceIds = i.Targets.Select(t => t.Id).ToList(),
                Watched = i.DurationMs > 0 && i.PositionMs >= i.DurationMs, PositionMs = i.DurationMs > 0 && i.PositionMs >= i.DurationMs ? 0 : Math.Max(0, i.PositionMs),
            }).ToList(),
            Targets = targets.Select(t => new StopTarget
            {
                DeviceId = t.Id, Name = t.Name, Kind = KindOf(t),
                Items = items.Where(i => i.Targets.Any(x => x.Id == t.Id)).Select(i => i.SourceUrl).Distinct().ToDictionary(u => u, _ => TargetState.Pending),
            }).ToList(),
        };
        foreach (var t in ev.Targets) Store.CountBatch(t);
        _store.AddEvent(ev);
        _store.LogCopy(ActivityLevel.Info, $"{summary}: {ev.Files:N0} file(s) to {string.Join(", ", targets.Select(t => t.Name))}…");
        long now = new DateTimeOffset(ev.TimeUtc).ToUnixTimeMilliseconds();
        foreach (var i in items)
            foreach (var t in i.Targets)
            {
                var replaced = _store.Enqueue(new PendingCopy
                {
                    TargetId = t.Id, SourceUrl = i.SourceUrl, Title = i.Label, FromName = fromName,
                    PositionMs = Math.Max(0, i.PositionMs), DurationMs = i.DurationMs, LastWatchTimeMs = now, CreatedUtc = ev.TimeUtc, EventId = ev.Id,
                });
                if (replaced != null && replaced.EventId != ev.Id)
                    _store.UpdateEventTarget(replaced.EventId, t.Id, TargetState.Skipped, "replaced by a later copy", replaced.SourceUrl);
            }
        NudgeQueue();
        return ev;
    }

    /// <summary>Undo on a card: puts every file back the way each player held it before (resume point, watched or
    /// nothing), through the same queue. Last-watched dates aren't restored.</summary>
    public (StopEvent? Card, string Error) UndoCard(string eventId)
    {
        var card = _store.Events(300).FirstOrDefault(e => e.Id == eventId);
        if (card == null) return (null, "That card no longer exists.");
        if (card.Undone) return (null, "That was already undone.");
        if (card.Undo.Count == 0) return (null, "There's nothing to undo on that card.");
        if (card.Targets.Any(t => t.State is TargetState.Pending or TargetState.Waiting))
            return (null, "Some of its copies haven't finished yet. Wait until they're done, then undo.");
        var devices = _store.Devices().ToDictionary(d => d.Id);
        var items = card.Undo.Where(u => devices.ContainsKey(u.DeviceId) && u.SourceUrl.Length > 0)
            .Select(u => new BatchItem(u.SourceUrl, u.Title,
                u.Watched ? (u.DurationMs > 0 ? u.DurationMs : 0) : u.PositionMs, u.DurationMs, new List<Device> { devices[u.DeviceId] }))
            .ToList();
        if (items.Count == 0) return (null, "The players on that card have been removed.");
        _store.MarkUndone(eventId);
        var undo = CopyBatch("Undo: " + (card.Batch ? card.Title : card.FileName.Length > 0 ? card.FileName : card.Title),
            card.Batch ? card.Subtitle : card.Subtitle, "Undo: put back as it was", ManualResetFrom, "", items, new List<UndoItem>(), eventId);
        return (undo, "");
    }

    private StopEvent NewResetCard(string title, string subtitle, string fileName, string sourceUrl, string poster, int durationMs, bool series, List<Device> targets)
    {
        var ev = new StopEvent
        {
            TimeUtc = DateTime.UtcNow,
            Title = title,
            Subtitle = subtitle,
            FileName = fileName,
            SourceUrl = sourceUrl,
            FromName = ManualResetFrom,
            DurationMs = durationMs,
            Reset = true,
            Manual = true,
            Series = series,
            Poster = poster,
            Targets = targets.Select(t => new StopTarget { DeviceId = t.Id, Name = t.Name, Kind = KindOf(t) }).ToList(),
        };
        _store.AddEvent(ev);
        return ev;
    }

    private void EnqueueResets(StopEvent ev, List<Device> targets, string sourceUrl, string label, int durationMs, string kind)
    {
        // Stamped "now": the queue keeps the newest job per player and file, so an older copy still waiting for an
        // off player must lose to this reset (an unstamped reset used to lose to it, and was dropped).
        long now = new DateTimeOffset(ev.TimeUtc).ToUnixTimeMilliseconds();
        foreach (var t in targets)
        {
            var replaced = _store.Enqueue(new PendingCopy
            {
                TargetId = t.Id,
                Kind = kind,
                SourceUrl = sourceUrl,
                Title = label,
                FromName = ManualResetFrom,
                PositionMs = 0,
                DurationMs = durationMs,
                LastWatchTimeMs = now,
                CreatedUtc = ev.TimeUtc,
                EventId = ev.Id,
            });
            if (replaced != null)
                _store.UpdateEventTarget(replaced.EventId, t.Id, TargetState.Skipped, "replaced by a later reset", replaced.SourceUrl);
        }
        NudgeQueue();
    }

    /// <summary>
    /// Reads the bookmark the source player saved when it stopped. It is accepted only if it is plausible: no earlier
    /// than the last polled position, and no later than one poll interval (plus slack) after it. The player may take
    /// a moment to save, so it is asked a few times. If nothing plausible comes back, the polled position is used.
    /// </summary>
    private async Task<int> ExactStopPositionAsync(Device source, NowPlaying last)
    {
        try
        {
            var api = new ZidooClient(source.Ip, source.Port);
            var (lookup, hit) = await api.GetIdByPathnameAsync(last.SourceUrl, _stopping);
            if (lookup != Lookup.Found) return last.PositionMs;

            int lo = last.PositionMs - 1500;
            int hi = last.PositionMs + _store.Settings().PollSeconds * 1000 + 4000;
            bool nearEnd = NearEnd(last.PositionMs, last.DurationMs);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(attempt == 0 ? 600 : 800, _stopping);
                var entry = await api.GetEntryAsync(hit!.Id, _stopping);
                if (entry == null || !PathMapper.Same(entry.Path, last.SourceUrl)) continue;
                int saved = entry.PlayPointMs;
                if (saved >= lo && saved <= hi) return saved;
                // A finished file may be saved at its full length.
                if (nearEnd && last.DurationMs > 0 && saved >= lo && saved <= last.DurationMs + 1000) return saved;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return last.PositionMs;
    }

    /// <summary>The poster for a file as seen on one player (for reset cards made from the Progress Reset tab).</summary>
    public async Task<string> PosterForAsync(Device d, string sourceUrl) =>
        (await GetTitleInfoAsync(d, sourceUrl, PathMapper.FileName(sourceUrl))).Poster;

    /// <summary>Poster (season poster for episodes) and subtitle for a file, looked up once per player and file.</summary>
    private Task<TitleInfo> GetTitleInfoAsync(Device d, string sourceUrl, string playPath)
    {
        string key = d.Id + "|" + sourceUrl;
        var task = _titles.GetOrAdd(key, _ => LookUpTitleAsync(d, sourceUrl, playPath));
        // Don't keep an empty answer forever (the Home Theater app may simply not have been running).
        _ = task.ContinueWith(t => { if (t.IsFaulted || (t.Result.Poster.Length == 0)) _titles.TryRemove(key, out _); }, TaskScheduler.Default);
        if (_titles.Count > 500) _titles.Clear();
        return task;
    }

    private async Task<TitleInfo> LookUpTitleAsync(Device d, string sourceUrl, string playPath)
    {
        string subtitle = "";
        var m = EpisodeTag.Match(PathMapper.FileName(playPath));
        if (m.Success) subtitle = $"S{int.Parse(m.Groups[1].Value):00}E{int.Parse(m.Groups[2].Value):00}";
        else if (Regex.Match(PathMapper.FileName(playPath), @"\((19\d\d|20\d\d)\)") is { Success: true } y) subtitle = y.Groups[1].Value;
        if (d.IsPlex)
        {
            try { return await LookUpPlexTitleAsync(d, sourceUrl, subtitle); }
            catch (OperationCanceledException) { throw; }
            catch { return new TitleInfo("", subtitle); }
        }
        try
        {
            var api = new ZidooClient(d.Ip, d.Port);
            var (lookup, hit) = await api.GetIdByPathnameAsync(sourceUrl, _stopping);
            if (lookup != Lookup.Found) return new TitleInfo("", subtitle);

            var file = await api.GetEntryAsync(hit!.Id, _stopping);
            if (file != null && file.Season > 0 && file.Episode > 0) subtitle = $"S{file.Season:00}E{file.Episode:00}";

            // Walk up from the file: the season (4) is preferred for episodes, then the film (1), then the series (3).
            int posterId = 0, seriesId = 0, parent = file?.ParentId ?? 0;
            for (int level = 0; level < 4 && parent > 1 && posterId == 0; level++)
            {
                var e = await api.GetEntryAsync(parent, _stopping);
                if (e == null) break;
                if (e.Type is 4 or 1) posterId = e.Id;
                else if (e.Type == 3 && seriesId == 0) seriesId = e.Id;
                parent = e.ParentId;
            }
            if (posterId == 0) posterId = seriesId;
            if (posterId == 0 && hit.Matched && hit.TitleId > 0 && hit.TitleId != hit.Id) posterId = hit.TitleId;
            if (posterId == 0) return new TitleInfo("", subtitle);

            var bytes = await api.GetPosterAsync(posterId, ct: _stopping);
            if (bytes == null) return new TitleInfo("", subtitle);
            string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Id + "|" + posterId)))[..16].ToLowerInvariant()
                          + ZidooClient.ImageExtension(bytes);
            string path = Path.Combine(_store.PosterDirectory, name);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, _stopping);
            else File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new TitleInfo(name, subtitle);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new TitleInfo("", subtitle); }
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
                int days = _store.Settings().PendingMaxDays;
                var expired = _store.ExpirePending(TimeSpan.FromDays(days));
                foreach (var x in expired)
                    _store.UpdateEventTarget(x.EventId, x.TargetId, TargetState.Failed, $"expired: the player stayed off for {days} days", x.SourceUrl);
                if (expired.Count > 0) _store.Log(ActivityLevel.Warn, $"{expired.Count} waiting bookmark copies expired (their player stayed offline too long).");

                // Players whose Home Theater app did not answer in this pass: their other copies wait for the next pass
                // instead of each spending its own timeout.
                var htNotReady = new HashSet<string>();
                foreach (var job in _store.Pending())
                {
                    if (ct.IsCancellationRequested) break;
                    if (htNotReady.Contains(job.TargetId)) { MarkWaiting(job, WaitHomeTheater); continue; }
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
        if (target == null || !target.Enabled)
        {
            Finish(job, TargetState.Skipped, target == null ? "player removed" : "sync is off for it");
            return;
        }

        var live = LiveOf(target.Id);
        if (!live.Online || live.Power == PowerAction.TurningOff)
        {
            MarkWaiting(job, WaitOff);
            return; // stays queued until the player answers again
        }

        // A stop at 0:00 is copied as a reset: the bookmark and the watched mark are cleared.
        bool reset = job.PositionMs == 0;
        string why = job.FromName == ManualResetFrom ? "reset from Resume Relay" : $"stopped at 0:00 on {job.FromName}";
        string what = $"{job.Title} ({Clock(job.PositionMs)}) from {job.FromName} to {target.Name}";

        if (live.Playing != null && PathMapper.Same(live.Playing.SourceUrl, job.SourceUrl))
        {
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: it is playing there right now.");
            Finish(job, TargetState.Skipped, "playing there right now");
            return;
        }

        if (target.IsPlex) { await ProcessPlexAsync(job, target, ct); return; }
        var api = new ZidooClient(target.Ip, target.Port);
        if (job.Kind == PendingCopy.SeriesKind) { await ProcessSeriesResetAsync(job, target, api, htNotReady, ct); return; }

        // 1. Find the file in the target's own library, by path.
        var (lookup, hit) = await api.GetIdByPathnameAsync(job.SourceUrl, ct);
        if (lookup == Lookup.NotFound)
        {
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: {target.Name}'s library does not have that file.");
            Finish(job, TargetState.Skipped, "not in its library");
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
            _store.LogCopy(ActivityLevel.Error, $"Safety check stopped {what}: id {hit.Id} on {target.Name} points at \"{entry.Path}\", not this file. Nothing written.");
            Finish(job, TargetState.Failed, "safety check stopped it, nothing written");
            return;
        }
        live.HomeTheaterRunning = true;

        // A finished file is stored as watched with no resume point (as Plex does), not as a point at the end.
        if (!reset && NearEnd(job.PositionMs, job.DurationMs)) { await ProcessFinishedAsync(job, target, api, entry, what, ct); return; }

        // 3. Already there? (Not for a reset: a file can show 0:00 and still carry a watched mark to clear.)
        if (!reset && Math.Abs(entry.PlayPointMs - job.PositionMs) <= 2_000)
        {
            _store.LogCopy(ActivityLevel.Skip, $"{target.Name} already has {job.Title} at {Clock(entry.PlayPointMs)}.");
            Finish(job, TargetState.Ok, "already there");
            return;
        }

        // The latest stop always wins: whatever bookmark the target holds is replaced (owner's choice, 0.1.3).

        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, reset
                ? $"[watch-only] Would reset {job.Title} on {target.Name} ({why}; entry {entry.Id}, currently {Clock(entry.PlayPointMs)})."
                : $"[watch-only] Would copy {what} (entry {entry.Id}, currently {Clock(entry.PlayPointMs)}).");
            Finish(job, TargetState.WatchOnly, reset ? "watch-only: would reset" : "watch-only: would copy");
            return;
        }

        // 4. Write, then believe only what we read back.
        if (reset) await api.ResetPlayPointAsync(entry.Id, ct);
        else await api.SetPlayPointAsync(entry.Id, job.PositionMs, job.DurationMs, job.LastWatchTimeMs, ct);
        int? readBack = null;
        for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800 + 700 * i), ct);
            readBack = (await api.GetEntryAsync(entry.Id, ct))?.PlayPointMs;
            if (readBack.HasValue && Landed(readBack.Value, job)) break;
        }

        if (readBack.HasValue && Landed(readBack.Value, job))
        {
            bool watched = !reset && NearEnd(job.PositionMs, job.DurationMs);
            FileWritten?.Invoke(target.Id, entry.Id, reset ? 0 : readBack.Value, reset ? -1 : job.LastWatchTimeMs, watched);
            _store.LogCopy(ActivityLevel.Ok, reset
                ? $"Reset {job.Title} on {target.Name} ({why})."
                : $"Copied {what}" + (watched ? " (marked watched)." : "."));
            string note = string.Join(", ", new[]
            {
                reset ? "reset" : watched ? "watched" : "",
                job.WaitedFor == WaitOff ? "after it woke up" : job.WaitedFor == WaitHomeTheater ? "after Home Theater opened" : "",
            }.Where(s => s.Length > 0));
            Finish(job, TargetState.Ok, note);
        }
        else
        {
            Retry(job, what, readBack.HasValue ? $"the player still shows {Clock(readBack.Value)} after the write" : "could not read the bookmark back");
        }
    }

    /// <summary>
    /// A finished file on a Zidoo: watched, with no resume point left at the end. The HT app's "mark as watched" only
    /// does that for a file that isn't watched yet (on a watched one it leaves the old point), so the file is reset
    /// first, then marked watched, then read back. On the player the stop happened on, this tidies its own leftover.
    /// </summary>
    private async Task ProcessFinishedAsync(PendingCopy job, Device target, ZidooClient api, EntryInfo entry, string what, CancellationToken ct)
    {
        bool self = job.FromName == target.Name && job.EventId.Length == 0;
        var before = await ReadFileCellAsync(api, entry, ct);
        if (before != null && before.Watched && before.PlayPointMs <= 0)
        {
            if (!self) _store.LogCopy(ActivityLevel.Skip, $"{target.Name} already has {job.Title} watched.");
            Finish(job, TargetState.Ok, "already there");
            return;
        }
        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, self ? $"[watch-only] Would clear the leftover resume point of {job.Title} on {target.Name} (it was finished there)."
                                                    : $"[watch-only] Would mark {job.Title} watched on {target.Name} (from {job.FromName}).");
            Finish(job, TargetState.WatchOnly, "watch-only: would mark watched");
            return;
        }
        var (clean, after) = await MarkWatchedCleanAsync(api, entry, ct);
        if (after?.Watched == true)
        {
            FileWritten?.Invoke(target.Id, entry.Id, after.PlayPointMs, after.LastWatchMs, true);
            _store.LogCopy(ActivityLevel.Ok, self
                ? $"Cleared the leftover resume point of {job.Title} on {target.Name} (finished there; it stays watched)."
                : $"Copied {what} (marked watched{(clean ? "" : "; the player kept a resume point at the end")}).");
            string note = string.Join(", ", new[]
            {
                "watched",
                job.WaitedFor == WaitOff ? "after it woke up" : job.WaitedFor == WaitHomeTheater ? "after Home Theater opened" : "",
            }.Where(s => s.Length > 0));
            Finish(job, TargetState.Ok, note);
        }
        else Retry(job, what, after == null ? "could not read it back" : "the player doesn't show it watched after the write");
    }

    /// <summary>Marks a Zidoo file watched with no resume point: reset, then "mark as watched", then read back. If the
    /// player won't take "mark as watched", it's put at its full length instead, which the player also counts as watched.
    /// Clean is true when it ended watched with no resume point.</summary>
    public static async Task<(bool Clean, ProgressCell? After)> MarkWatchedCleanAsync(ZidooClient api, EntryInfo file, CancellationToken ct = default)
    {
        await api.ResetPlayPointAsync(file.Id, ct);
        await api.MarkAsWatchedAsync(file.Id, ct);
        ProgressCell? after = null;
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(600 + 600 * i, ct);
            after = await ReadFileCellAsync(api, file, ct);
            if (after?.Watched == true && after.PlayPointMs <= 0) return (true, after);
        }
        int dur = after?.DurationMs ?? 0;
        if (after?.Watched != true && dur > 0)
        {
            await api.SetPlayPointAsync(file.Id, dur, dur, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
            await Task.Delay(800, ct);
            after = await ReadFileCellAsync(api, file, ct);
        }
        return (false, after);
    }

    /// <summary>One file's state on a Zidoo (resume point, length, watched), read from the movie or season that holds it.</summary>
    public static async Task<ProgressCell?> ReadFileCellAsync(ZidooClient api, EntryInfo file, CancellationToken ct = default)
    {
        var p = await api.GetEntryAsync(file.ParentId, ct);
        if (p == null) return null;
        (int title, bool season) = p.Type switch { 5 => (p.ParentId, true), 4 => (p.Id, true), _ => (p.Id, false) };
        var read = await ProgressService.ReadTitleAsync(api, title, season, "", ct);
        return read.FirstOrDefault(x => x.Cell.FileId == file.Id).Cell;
    }

    /// <summary>
    /// "Reset TV Show" on one player: find the series from one episode's path (this player's own ids), read every
    /// season, clear each episode file that has a resume point or a watched mark, then read the seasons again to
    /// make sure nothing is left.
    /// </summary>
    private async Task ProcessSeriesResetAsync(PendingCopy job, Device target, ZidooClient api, HashSet<string> htNotReady, CancellationToken ct)
    {
        string what = $"every episode of {job.Title} on {target.Name}";
        var (lookup, hit) = await api.GetIdByPathnameAsync(job.SourceUrl, ct);
        if (lookup == Lookup.NotFound)
        {
            _store.LogCopy(ActivityLevel.Skip, $"Not resetting {job.Title} on {target.Name}: its library does not have that show.");
            Finish(job, TargetState.Skipped, "not in its library");
            return;
        }
        if (lookup == Lookup.Failed) { WaitForHomeTheater(job, target, htNotReady); return; }

        // Walk up from the episode's file to the series (file → episode → season → series).
        int seriesId = 0, seasonId = 0, parent = hit!.Id;
        for (int i = 0; i < 6 && parent > 1 && seriesId == 0; i++)
        {
            var e = await api.GetEntryAsync(parent, ct);
            if (e == null) break;
            if (e.Type == 3) seriesId = e.Id;
            else if (e.Type == 4 && seasonId == 0) seasonId = e.Id;
            parent = e.ParentId;
        }
        var seasons = new List<int>();
        if (seriesId > 0)
        {
            var kids = await api.GetChildrenAsync(seriesId, ct);
            if (!kids.Any(k => k.Type == 4)) kids = await api.GetCollectionAsync(seriesId, ct);
            seasons.AddRange(kids.Where(k => k.Type == 4).Select(k => k.Id));
        }
        if (seasons.Count == 0 && seasonId > 0) seasons.Add(seasonId); // a single-season show
        if (seasons.Count == 0)
        {
            Retry(job, what, "couldn't find the show's seasons");
            return;
        }

        async Task<List<ProgressCell>> ReadAll()
        {
            var all = new List<ProgressCell>();
            foreach (int id in seasons)
                all.AddRange((await ProgressService.ReadTitleAsync(api, id, true, job.Title, ct)).Select(x => x.Cell));
            return all;
        }
        static bool HasProgress(ProgressCell c) => c.PlayPointMs > 0 || c.Watched;

        var before = await ReadAll();
        var ids = before.Where(HasProgress).Select(c => c.FileId).Where(id => id > 0).Distinct().ToList();
        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, $"[watch-only] Would reset {ids.Count} episode file(s) of {job.Title} on {target.Name}.");
            Finish(job, TargetState.WatchOnly, $"watch-only: would reset {ids.Count} episode{(ids.Count == 1 ? "" : "s")}");
            return;
        }
        foreach (int id in ids) await api.ResetPlayPointAsync(id, ct);

        var left = ids.Count == 0 ? new List<int>() : (await ReadAll()).Where(HasProgress).Select(c => c.FileId).Intersect(ids).ToList();
        foreach (int id in ids.Except(left)) FileWritten?.Invoke(target.Id, id, 0, -1, false);
        if (left.Count > 0)
        {
            Retry(job, what, $"{left.Count} episode(s) still show progress after the reset");
            return;
        }
        _store.LogCopy(ActivityLevel.Ok, $"Reset every episode of {job.Title} on {target.Name} ({ids.Count} had progress).");
        string note = string.Join(", ", new[]
        {
            ids.Count == 0 ? "nothing to reset" : $"{ids.Count} episode{(ids.Count == 1 ? "" : "s")} reset",
            job.WaitedFor == WaitOff ? "after it woke up" : job.WaitedFor == WaitHomeTheater ? "after Home Theater opened" : "",
        }.Where(x => x.Length > 0));
        Finish(job, TargetState.Ok, note);
    }

    /// <summary>Removes the copy from the queue and sets its chip on the Activity card.</summary>
    private void Finish(PendingCopy job, TargetState state, string note)
    {
        _store.Dequeue(job);
        _store.UpdateEventTarget(job.EventId, job.TargetId, state, note, job.SourceUrl);
    }

    private void MarkWaiting(PendingCopy job, string reason)
    {
        if (job.WaitedFor == reason && job.LastNote.Length > 0) return;
        job.WaitedFor = reason;
        job.LastNote = reason == WaitOff ? "waiting" : "waiting for the Home Theater app to be opened";
        _store.UpdatePending(job);
        _store.UpdateEventTarget(job.EventId, job.TargetId, TargetState.Waiting,
            reason != WaitOff ? "waiting, Home Theater isn't open"
            : _store.Device(job.TargetId)?.IsPlex == true ? "waiting, the Plex server isn't answering" : "waiting, player is off", job.SourceUrl);
    }

    private void WaitForHomeTheater(PendingCopy job, Device target, HashSet<string> htNotReady)
    {
        htNotReady.Add(target.Id);
        LiveOf(target.Id).HomeTheaterRunning = false;
        if (job.WaitedFor == WaitHomeTheater) return;
        bool firstForPlayer = !_store.Pending().Any(p => p.TargetId == target.Id && p.WaitedFor == WaitHomeTheater);
        MarkWaiting(job, WaitHomeTheater);
        if (firstForPlayer)
            _store.Log(ActivityLevel.Info, $"{target.Name} is on, but its Home Theater app isn't open. Copies for it will go through once it is.");
    }

    private void Retry(PendingCopy job, string what, string why)
    {
        int attempt = 1 + (job.LastNote.StartsWith("retry ") && int.TryParse(job.LastNote.Split(' ')[1].TrimEnd(':'), out int n) ? n : 0);
        if (attempt >= MaxAttempts)
        {
            _store.LogCopy(ActivityLevel.Error, $"Gave up copying {what}: {why}.");
            Finish(job, TargetState.Failed, why);
            return;
        }
        job.LastAttemptUtc = DateTime.UtcNow;
        job.LastNote = $"retry {attempt}: {why}";
        _store.UpdatePending(job);
        _store.UpdateEventTarget(job.EventId, job.TargetId, TargetState.Waiting, "retrying: " + why, job.SourceUrl);
        _store.LogCopy(ActivityLevel.Warn, $"Will retry {what}: {why}.");
    }

    /// <summary>Discarding a waiting copy from the page.</summary>
    public bool Discard(string key)
    {
        var p = _store.RemovePending(key);
        if (p == null) return false;
        _store.UpdateEventTarget(p.EventId, p.TargetId, TargetState.Skipped, "discarded", p.SourceUrl);
        return true;
    }

    /// <summary>A write counts as landed when the player now shows the position we sent (±2 s). Near the end the
    /// player may round up to the full duration as it marks the file watched; that counts too.</summary>
    private static bool Landed(int readBack, PendingCopy job) =>
        Math.Abs(readBack - job.PositionMs) <= 2_000 ||
        (NearEnd(job.PositionMs, job.DurationMs) && readBack >= job.PositionMs - 2_000 && (job.DurationMs <= 0 || readBack <= job.DurationMs + 2_000));

    /// <summary>The player marks a file watched when the point is within 5 minutes of the end or at 93% or more.</summary>
    private static bool NearEnd(int positionMs, int durationMs) =>
        durationMs > 0 && (positionMs >= durationMs * 0.93 || durationMs - positionMs <= 5 * 60_000);

    // ===================================================================== helpers

    private static string KindOf(Device d) => d.IsPlex ? "plex" : "zidoo";

    public static string Clock(int ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Label(string title, string path)
    {
        string file = PathMapper.FileName(path);
        var ep = EpisodeTag.Match(file);
        string t = TitleOnly(title, path);
        return ep.Success && !t.Contains(ep.Value, StringComparison.OrdinalIgnoreCase) ? $"{t} {ep.Value.ToUpperInvariant()}" : t;
    }

    private static string TitleOnly(string title, string path) =>
        title.Trim().Length > 0 ? title.Trim() : PathMapper.FileName(path);
}
