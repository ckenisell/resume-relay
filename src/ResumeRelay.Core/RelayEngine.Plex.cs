using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ResumeRelay;

/// <summary>
/// Plex (and Jellyfin) users as players. Both servers go through the same code here (IMediaServer); only how their
/// sessions are read and matched to a user differs (Jellyfin's are in RelayEngine.Jellyfin.cs).
/// Plex users as players. The server's sessions say what each user is playing; a stop is handled exactly like a
/// Zidoo stop (the same card, the same queue). Copies to a Plex user are written with Plex's own calls:
/// a resume point (/:/progress), "mark as played" for a finished file, "mark as unplayed" for a reset.
/// Files are matched by their path inside the share, through the folder mapping set on the Plex card.
/// </summary>
public sealed partial class RelayEngine
{
    /// <summary>Plex keeps one library listing per user, rebuilt when it is older than this or a file isn't in it.</summary>
    private static readonly TimeSpan PlexIndexMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PlexIndexMinAge = TimeSpan.FromSeconds(60);

    private sealed class PlexIndex
    {
        public DateTime BuiltUtc;
        public Dictionary<string, PlexItem> ByKey = new(StringComparer.Ordinal);   // in-share path (lower-case) → item
        public Dictionary<int, PlexItem> ByRating = new();
        /// <summary>Every file by the end of its path (automatic matching, for files no folder mapping covers).</summary>
        public PathMatch.Index<PlexItem> ByTail = new();
        public List<PlexItem> Items = new();
        /// <summary>Plex files no folder mapping covers (they can't be matched to the Zidoos).</summary>
        public int Unmapped;
        public List<string> UnmappedFolders = new();
    }

    /// <summary>For the health notes: how many Plex files the folder mapping doesn't cover (from the last listing).</summary>
    public (int Count, List<string> Folders)? PlexUnmapped() => Unmapped(d => d.IsPlex);
    public (int Count, List<string> Folders)? JellyfinUnmapped() => Unmapped(d => d.IsJellyfin);

    private (int Count, List<string> Folders)? Unmapped(Func<Device, bool> kind)
    {
        var ids = _store.Devices().Where(kind).Select(d => d.Id).ToHashSet();
        var idx = _plexIndex.Where(kv => ids.Contains(kv.Key)).Select(kv => kv.Value).OrderByDescending(i => i.BuiltUtc).FirstOrDefault();
        return idx == null ? null : (idx.Unmapped, idx.UnmappedFolders);
    }

    private readonly ConcurrentDictionary<string, PlexIndex> _plexIndex = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _plexIndexLocks = new();

    public PlexClient PlexFor(Device d, PlexConfig? cfg = null)
    {
        cfg ??= _store.Plex();
        return new PlexClient(cfg.ServerUrl, d.PlexToken, cfg.ClientId);
    }

    private static PlexClient PlexOwner(PlexConfig cfg) => new(cfg.ServerUrl, cfg.ServerToken, cfg.ClientId);

    /// <summary>The media server a Plex or Jellyfin user is on, as that user.</summary>
    public IMediaServer ServerFor(Device d)
    {
        if (d.IsJellyfin) { var j = _store.Jellyfin(); return new JellyfinClient(j.ServerUrl, j.Token, j.DeviceId, d.JellyfinUserId); }
        return PlexFor(d);
    }

    /// <summary>The folder mapping of the server a user is on (Plex's or Jellyfin's card).</summary>
    public List<PlexMapping> MappingsFor(Device d) => d.IsJellyfin ? _store.Jellyfin().Mappings : _store.Plex().Mappings;

    private bool ServerReady(Device d) => d.IsJellyfin ? _store.Jellyfin().Ready : _store.Plex().Ready;

    /// <summary>A user's whole library with their progress (cached). Null when the server doesn't answer.</summary>
    public async Task<List<PlexItem>?> PlexItemsAsync(Device d, bool refresh, CancellationToken ct = default) =>
        (await PlexIndexAsync(d, refresh ? TimeSpan.Zero : PlexIndexMaxAge, ct))?.Items;

    private async Task<PlexIndex?> PlexIndexAsync(Device d, TimeSpan maxAge, CancellationToken ct)
    {
        if (_plexIndex.TryGetValue(d.Id, out var have) && DateTime.UtcNow - have.BuiltUtc < maxAge) return have;
        var gate = _plexIndexLocks.GetOrAdd(d.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_plexIndex.TryGetValue(d.Id, out have) && DateTime.UtcNow - have.BuiltUtc < maxAge) return have;
            if (!ServerReady(d)) return null;
            var mappings = MappingsFor(d);
            var items = await ServerFor(d).AllItemsAsync(ct);
            if (items == null) return null;
            var idx = new PlexIndex { BuiltUtc = DateTime.UtcNow, Items = items };
            foreach (var it in items)
            {
                idx.ByRating[it.Key] = it;
                foreach (var f in it.Files)
                {
                    idx.ByTail.Add(f, it);
                    if (PlexPaths.ToSourceUrl(f, mappings) == null)
                    {
                        idx.Unmapped++;
                        string folder = (Path.GetDirectoryName(f.Replace('\\', '/')) ?? "").Replace('\\', '/');
                        if (idx.UnmappedFolders.Count < 3 && !idx.UnmappedFolders.Contains(folder)) idx.UnmappedFolders.Add(folder);
                        continue;
                    }
                    if (PlexPaths.Rel(f, mappings) is { } rel) idx.ByKey[PlexPaths.Key(rel)] = it;
                }
            }
            _plexIndex[d.Id] = idx;
            return idx;
        }
        finally { gate.Release(); }
    }

    /// <summary>Finds a file in a Plex user's library by the Zidoos' path, and reads its current state.
    /// (item, true) found; (null, true) Plex doesn't have it; (null, false) the server didn't answer.</summary>
    private async Task<(PlexItem? Item, bool Answered)> FindPlexItemAsync(Device d, string sourceUrl, CancellationToken ct)
    {
        var idx = await PlexIndexAsync(d, PlexIndexMaxAge, ct);
        if (idx == null) return (null, false);
        var hit = FindInIndex(idx, sourceUrl);
        if (hit == null && DateTime.UtcNow - idx.BuiltUtc > PlexIndexMinAge)
        {
            idx = await PlexIndexAsync(d, TimeSpan.Zero, ct); // a file added since the last listing
            if (idx == null) return (null, false);
            hit = FindInIndex(idx, sourceUrl);
        }
        if (hit == null) return (null, true);
        // The listing can be minutes old: read the item itself for the current state.
        var fresh = await ServerFor(d).ItemAsync(hit.Key, ct);
        return (fresh ?? hit, fresh != null);
    }

    /// <summary>A file in a server's listing, by the Zidoos' path (through the folder mapping), or automatically by the
    /// end of its path: for an smb:// path no folder mapping covers, and for another server's file (a server: address).</summary>
    private static PlexItem? FindInIndex(PlexIndex idx, string sourceUrl)
    {
        if (!PathMatch.IsServerUrl(sourceUrl) && PlexPaths.RelOfUrl(sourceUrl) is { } rel && idx.ByKey.TryGetValue(PlexPaths.Key(rel), out var hit)) return hit;
        return idx.ByTail.TryFind(sourceUrl, out var auto) ? auto : null;
    }

    /// <summary>The address the copy queue uses for a file of a Plex or Jellyfin user's library, by its ratingKey: the
    /// Zidoos' path through the folder mapping, or else the server's own path (a server: address, matched on the
    /// other servers by the end of the path). Null when the listing doesn't have it.</summary>
    public async Task<string?> PlexSourceUrlAsync(Device d, int ratingKey, CancellationToken ct = default)
    {
        var idx = await PlexIndexAsync(d, PlexIndexMaxAge, ct);
        var item = idx?.ByRating.GetValueOrDefault(ratingKey);
        if (item == null || item.Files.Count == 0) return null;
        var mappings = MappingsFor(d);
        return item.Files.Select(f => PlexPaths.ToSourceUrl(f, mappings)).FirstOrDefault(u => u != null) ?? PathMatch.ServerUrl(item.Files[0]);
    }

    /// <summary>Live Poster: a file's Plex item by the Zidoos' path, from the cached listing of any Plex user (null when
    /// Plex isn't set up, doesn't have it, or doesn't answer).</summary>
    public Task<PlexItem?> PlexItemForUrlAsync(string sourceUrl, CancellationToken ct = default) =>
        ServerItemForUrlAsync(_store.Devices().FirstOrDefault(x => x.IsPlex && x.Enabled), sourceUrl, ct);

    /// <summary>The same for a Plex or Jellyfin user's server (from that user's cached listing).</summary>
    public async Task<PlexItem?> ServerItemForUrlAsync(Device? d, string sourceUrl, CancellationToken ct = default)
    {
        if (d == null || sourceUrl.Length == 0) return null;
        var idx = await PlexIndexAsync(d, PlexIndexMaxAge, ct);
        return idx == null ? null : FindInIndex(idx, sourceUrl);
    }

    /// <summary>A client for the Plex server with the owner's token (null when Plex isn't set up).</summary>
    public PlexClient? PlexServer()
    {
        var cfg = _store.Plex();
        return cfg.Ready ? PlexOwner(cfg) : null;
    }

    /// <summary>Forgets cached listings (after the folder mapping or the users change).</summary>
    public void ForgetPlexIndex() => _plexIndex.Clear();

    // ===================================================================== polling

    /// <summary>Is this session the user's? By account id; the owner also as user "1" (how a server lists its owner);
    /// and by name, as Plex shows it (the owner's can be the account's username rather than their display name).</summary>
    public static bool IsUser(Device d, PlexSession s, PlexConfig cfg) =>
        (s.UserId.Length > 0 && s.UserId == d.PlexUserId)
        || (d.PlexOwner && s.UserId == "1")
        || (s.UserTitle.Length > 0 && string.Equals(s.UserTitle, d.PlexUserTitle, StringComparison.OrdinalIgnoreCase))
        || (d.PlexOwner && s.UserTitle.Length > 0 && string.Equals(s.UserTitle, cfg.Username, StringComparison.OrdinalIgnoreCase));

    private readonly ConcurrentDictionary<int, List<string>> _plexFiles = new();   // Plex and Jellyfin keys never overlap (Jellyfin's start at 1,000,000)

    /// <summary>The file of a playing item. Sessions normally list it; if not, it is read from the item once.</summary>
    private async Task<List<string>> PlexSessionFilesAsync(Device d, PlexItem it, CancellationToken ct)
    {
        if (it.Files.Count > 0 || it.Key <= 0) return it.Files;
        if (_plexFiles.TryGetValue(it.Key, out var known)) return known;
        var full = await ServerFor(d).ItemAsync(it.Key, ct);
        if (full == null || full.Files.Count == 0) return it.Files;
        if (_plexFiles.Count > 200) _plexFiles.Clear();
        return _plexFiles[it.Key] = full.Files;
    }

    private async Task PollPlexAsync(Device d, CancellationToken ct)
    {
        var cfg = _store.Plex();
        var sessions = cfg.Ready ? await PlexOwner(cfg).SessionsAsync(TimeSpan.FromSeconds(4), ct) : null;
        await ApplySessionsAsync(d, sessions, x => IsUser(d, x, cfg), cfg.Mappings, ct);
    }

    /// <summary>What a Plex or Jellyfin user is playing, from the server's sessions (null: the server didn't answer).
    /// A stop is noticed and handled exactly like a Zidoo's.</summary>
    private async Task ApplySessionsAsync(Device d, List<PlexSession>? sessions, Func<PlexSession, bool> mine, List<PlexMapping> mappings, CancellationToken ct)
    {
        var live = LiveOf(d.Id);
        var now = DateTime.UtcNow;
        if (sessions == null)
        {
            live.ConsecutiveFailures++;
            if (live.Online && live.ConsecutiveFailures >= 2)
            {
                live.Online = false;
                live.OfflineSinceUtc = now;
                _store.Log(ActivityLevel.Info, $"The {d.ServerLabel} server isn't answering, so {d.Name} can't be synced until it is.");
                if (live.Playing != null)
                {
                    var last = live.Playing;
                    live.Playing = null;
                    OnStopped(d, last, now, "server went offline", canAskPlayer: false);
                }
            }
            else if (!live.Online) live.OfflineSinceUtc ??= now;
            return;
        }

        bool wasKnownOffline = live.OfflineSinceUtc != null && live.LastSeenUtc != null;
        live.ConsecutiveFailures = 0;
        live.LastSeenUtc = now;
        if (!live.Online)
        {
            live.Online = true;
            live.OfflineSinceUtc = null;
            if (wasKnownOffline) _store.Log(ActivityLevel.Info, $"The {d.ServerLabel} server is answering again ({d.Name}).");
            CameOnline?.Invoke(d.Id);
            NudgeQueue();
        }

        var prev = live.Playing;
        var s = sessions.FirstOrDefault(x => mine(x) && (x.Item.Type is "movie" or "episode" or "clip"));
        var files = s == null ? new List<string>() : await PlexSessionFilesAsync(d, s.Item, ct);
        if (s != null && files.Count > 0)
        {
            string file = files[0];
            bool sameFile = prev != null && string.Equals(prev.Path, file, StringComparison.Ordinal);
            if (prev != null && !sameFile) OnStopped(d, prev, now, "switched to another file", canAskPlayer: true);
            var np = new NowPlaying
            {
                Path = file,
                SourceUrl = sameFile && prev!.SourceUrl.Length > 0 ? prev.SourceUrl : await SourceUrlForServerFileAsync(file, mappings),
                Title = s.Item.IsEpisode ? s.Item.Show : s.Item.Title,
                Subtitle = sameFile ? prev!.Subtitle : PlexSubtitle(s.Item),
                PositionMs = s.Item.ViewOffsetMs,
                DurationMs = s.Item.DurationMs,
                Paused = s.State == "paused",
                UpdatedUtc = now,
                PlexKey = s.Item.Key,
                Poster = sameFile ? prev!.Poster : "",
            };
            live.Playing = np;
            if (!sameFile) _ = FillPlexPosterAsync(d, np, s.Item);
        }
        else
        {
            live.Playing = null;
            if (prev != null) OnStopped(d, prev, now, "stopped", canAskPlayer: true);
        }
    }

    /// <summary>Progress Reset's last read, to find the Zidoos' path for a server file no folder mapping covers
    /// (automatic matching by the end of the path). Set by the host; null: not available.</summary>
    public Func<string, Task<string?>>? FindZidooUrl { get; set; }
    private readonly ConcurrentDictionary<string, (string Url, DateTime At)> _autoUrls = new(StringComparer.Ordinal);

    /// <summary>The address a server file is copied by: the Zidoos' path through the folder mapping; else the Zidoos' path
    /// found automatically (the same file, by the end of its path, in the Zidoos' last read on Progress Reset); else the
    /// server's own path (a server: address, which other Plex and Jellyfin users are matched to the same way).</summary>
    private async Task<string> SourceUrlForServerFileAsync(string file, List<PlexMapping> mappings)
    {
        if (PlexPaths.ToSourceUrl(file, mappings) is { } mapped) return mapped;
        if (_autoUrls.TryGetValue(file, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Url;
        string url = "";
        try { if (FindZidooUrl != null) url = await FindZidooUrl(file) ?? ""; } catch { }
        if (url.Length == 0) url = PathMatch.ServerUrl(file);
        if (_autoUrls.Count > 200) _autoUrls.Clear();
        _autoUrls[file] = (url, DateTime.UtcNow);
        return url;
    }

    private static string PlexSubtitle(PlexItem it) =>
        it.IsEpisode && it.Season > 0 && it.Episode > 0 ? $"S{it.Season:00}E{it.Episode:00}" : it.Year > 0 ? it.Year.ToString() : "";

    private async Task FillPlexPosterAsync(Device d, NowPlaying np, PlexItem item)
    {
        try { np.Poster = await PlexPosterAsync(d, item); } catch { }
    }

    private async Task<string> PlexPosterAsync(Device d, PlexItem item)
    {
        string thumb = item.PosterPath;
        if (thumb.Length == 0) return "";
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.KindName + "|" + thumb)))[..16].ToLowerInvariant();
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
        {
            string existing = Path.Combine(_store.PosterDirectory, name + ext);
            if (File.Exists(existing)) { File.SetLastWriteTimeUtc(existing, DateTime.UtcNow); return name + ext; }
        }
        var bytes = await ServerFor(d).ImageAsync(thumb, _stopping);
        if (bytes == null) return "";
        string file = name + (ZidooClient.ImageExtension(bytes) ?? ".jpg");
        await File.WriteAllBytesAsync(Path.Combine(_store.PosterDirectory, file), bytes, _stopping);
        return file;
    }

    /// <summary>Poster and subtitle of a file in a Plex user's library (for Activity cards).</summary>
    private async Task<TitleInfo> LookUpPlexTitleAsync(Device d, string sourceUrl, string subtitle)
    {
        var (item, _) = await FindPlexItemAsync(d, sourceUrl, _stopping);
        if (item == null) return new TitleInfo("", subtitle);
        string sub = PlexSubtitle(item);
        return new TitleInfo(await PlexPosterAsync(d, item), sub.Length > 0 ? sub : subtitle);
    }

    /// <summary>
    /// The position Plex saved for the user when they stopped. Plex apps report every ten seconds or so, so the last
    /// polled position can be a little behind. A file Plex counted as finished (past about 90%) comes back with no
    /// resume point and a fresh "played": that is a finish.
    /// </summary>
    private async Task<int> ExactPlexStopPositionAsync(Device source, NowPlaying last, DateTime stoppedUtc)
    {
        try
        {
            if (last.PlexKey <= 0) return last.PositionMs;
            var api = ServerFor(source);
            int lo = last.PositionMs - 1500;
            int hi = last.PositionMs + _store.Settings().PollSeconds * 1000 + 15000;
            long stoppedUnix = new DateTimeOffset(stoppedUtc).ToUnixTimeSeconds();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(attempt == 0 ? 800 : 1200, _stopping);
                var it = await api.ItemAsync(last.PlexKey, _stopping);
                if (it == null) continue;
                if (it.ViewOffsetMs >= lo && it.ViewOffsetMs <= hi && it.ViewOffsetMs > 0) return it.ViewOffsetMs;
                bool justPlayed = it.ViewCount > 0 && it.ViewOffsetMs == 0 && it.LastViewedAt >= stoppedUnix - 120;
                if (justPlayed && last.DurationMs > 0 && last.PositionMs >= last.DurationMs * 0.8) return last.DurationMs;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return last.PositionMs;
    }

    // ===================================================================== queue → Plex

    private async Task ProcessPlexAsync(PendingCopy job, Device target, CancellationToken ct)
    {
        string what = $"{job.Title} ({Clock(job.PositionMs)}) from {job.FromName} to {target.Name}";
        string why = job.FromName == ManualResetFrom ? "reset from Resume Relay" : $"stopped at 0:00 on {job.FromName}";
        var api = ServerFor(target);
        string server = target.ServerLabel;
        var (item, answered) = await FindPlexItemAsync(target, job.SourceUrl, ct);
        if (item == null)
        {
            if (!answered) { Retry(job, what, $"the {server} server didn't answer"); return; }
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: {server} doesn't have that file (it wasn't found there by its path or the end of its path).");
            Finish(job, TargetState.Skipped, "not in its library");
            return;
        }

        if (job.Kind == PendingCopy.SeriesKind)
        {
            await ProcessPlexSeriesResetAsync(job, target, api, item, ct);
            return;
        }

        bool reset = job.PositionMs == 0;
        int duration = item.DurationMs > 0 ? item.DurationMs : job.DurationMs;
        bool finished = !reset && NearEnd(job.PositionMs, duration);

        if (reset ? item.ViewOffsetMs == 0 && item.ViewCount == 0
            : finished ? item.Watched && item.ViewOffsetMs == 0
            : item.ViewCount == 0 && Math.Abs(item.ViewOffsetMs - job.PositionMs) <= 2_000)
        {
            _store.LogCopy(ActivityLevel.Skip, reset ? $"{target.Name} already has {job.Title} reset." : $"{target.Name} already has {job.Title} at {Clock(finished ? duration : item.ViewOffsetMs)}.");
            Finish(job, TargetState.Ok, "already there");
            return;
        }

        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, reset
                ? $"[watch-only] Would reset {job.Title} on {target.Name} ({why}; {server} item {item.Key}, currently {Clock(item.ViewOffsetMs)})."
                : $"[watch-only] Would copy {what} ({server} item {item.Key}, currently {Clock(item.ViewOffsetMs)}).");
            Finish(job, TargetState.WatchOnly, reset ? "watch-only: would reset" : "watch-only: would copy");
            return;
        }

        // Write, then believe only what Plex reads back.
        if (reset) await api.UnscrobbleAsync(item.Key, ct);
        else if (finished) await api.ScrobbleAsync(item.Key, ct);
        else
        {
            // A resume point in the middle means "not finished", as on a Zidoo: clear an old played mark first.
            if (item.Watched) await api.UnscrobbleAsync(item.Key, ct);
            await api.SetProgressAsync(item.Key, job.PositionMs, ct);
        }

        PlexItem? after = null;
        bool landed = false;
        for (int i = 0; i < 3 && !ct.IsCancellationRequested && !landed; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600 + 600 * i), ct);
            after = await api.ItemAsync(item.Key, ct);
            landed = after != null && (reset ? after.ViewOffsetMs == 0 && after.ViewCount == 0
                : finished ? after.Watched
                : Math.Abs(after.ViewOffsetMs - job.PositionMs) <= 2_000);
        }

        if (landed)
        {
            FileWritten?.Invoke(target.Id, item.Key, reset || finished ? 0 : after!.ViewOffsetMs, reset ? -1 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), finished);
            _store.LogCopy(ActivityLevel.Ok, reset ? $"Reset {job.Title} on {target.Name} ({why})." : $"Copied {what}" + (finished ? " (marked played)." : "."));
            Finish(job, TargetState.Ok, reset ? "reset" : finished ? "watched" : "");
        }
        else if (!reset && !finished && after != null && after.ViewOffsetMs == 0 && job.PositionMs < 60_000)
        {
            // Plex doesn't keep a resume point this close to the start (it treats it as not started).
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: {server} doesn't keep a resume point this close to the start.");
            Finish(job, TargetState.Skipped, $"too close to the start for {server}");
        }
        else
        {
            Retry(job, what, after == null ? $"could not read it back from {server}" : $"{server} still shows {Clock(after.ViewOffsetMs)}{(after.Watched ? ", played" : "")} after the write");
        }
    }

    /// <summary>"Reset TV Show" for a Plex user: "mark as unplayed" on the show clears every episode in one call.</summary>
    private async Task ProcessPlexSeriesResetAsync(PendingCopy job, Device target, IMediaServer api, PlexItem episode, CancellationToken ct)
    {
        string what = $"every episode of {job.Title} on {target.Name}";
        string server = target.ServerLabel;
        if (episode.ShowKey <= 0) { Retry(job, what, $"couldn't find the show on {server}"); return; }
        var before = await api.ShowEpisodesAsync(episode.ShowKey, ct);
        if (before == null) { Retry(job, what, $"the {server} server didn't answer"); return; }
        var with = before.Where(e => e.ViewOffsetMs > 0 || e.ViewCount > 0).ToList();
        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, $"[watch-only] Would reset {with.Count} episode(s) of {job.Title} on {target.Name}.");
            Finish(job, TargetState.WatchOnly, $"watch-only: would reset {with.Count} episode{(with.Count == 1 ? "" : "s")}");
            return;
        }
        if (with.Count > 0) await api.UnscrobbleAsync(episode.ShowKey, ct);
        var after = with.Count == 0 ? new List<PlexItem>() : await api.ShowEpisodesAsync(episode.ShowKey, ct);
        if (after == null) { Retry(job, what, $"could not read the show back from {server}"); return; }
        var left = after.Where(e => e.ViewOffsetMs > 0 || e.ViewCount > 0).ToList();
        foreach (var e in with.Where(w => !left.Any(l => l.Key == w.Key))) FileWritten?.Invoke(target.Id, e.Key, 0, -1, false);
        if (left.Count > 0) { Retry(job, what, $"{left.Count} episode(s) still show progress on {server} after the reset"); return; }
        _store.LogCopy(ActivityLevel.Ok, $"Reset every episode of {job.Title} on {target.Name} ({with.Count} had progress).");
        Finish(job, TargetState.Ok, with.Count == 0 ? "nothing to reset" : $"{with.Count} episode{(with.Count == 1 ? "" : "s")} reset");
    }
}
