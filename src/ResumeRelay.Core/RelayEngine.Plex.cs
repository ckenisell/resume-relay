using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ResumeRelay;

/// <summary>
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
        public List<PlexItem> Items = new();
    }

    private readonly ConcurrentDictionary<string, PlexIndex> _plexIndex = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _plexIndexLocks = new();

    public PlexClient PlexFor(Device d, PlexConfig? cfg = null)
    {
        cfg ??= _store.Plex();
        return new PlexClient(cfg.ServerUrl, d.PlexToken, cfg.ClientId);
    }

    private static PlexClient PlexOwner(PlexConfig cfg) => new(cfg.ServerUrl, cfg.ServerToken, cfg.ClientId);

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
            var cfg = _store.Plex();
            if (!cfg.Ready) return null;
            var items = await PlexFor(d, cfg).AllItemsAsync(ct);
            if (items == null) return null;
            var idx = new PlexIndex { BuiltUtc = DateTime.UtcNow, Items = items };
            foreach (var it in items)
            {
                idx.ByRating[it.Key] = it;
                foreach (var f in it.Files)
                    if (PlexPaths.Rel(f, cfg.Mappings) is { } rel) idx.ByKey[PlexPaths.Key(rel)] = it;
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
        string? rel = PlexPaths.RelOfUrl(sourceUrl);
        if (rel == null) return (null, true);
        string key = PlexPaths.Key(rel);
        var idx = await PlexIndexAsync(d, PlexIndexMaxAge, ct);
        if (idx == null) return (null, false);
        if (!idx.ByKey.TryGetValue(key, out var hit) && DateTime.UtcNow - idx.BuiltUtc > PlexIndexMinAge)
        {
            idx = await PlexIndexAsync(d, TimeSpan.Zero, ct); // a file added since the last listing
            if (idx == null) return (null, false);
            idx.ByKey.TryGetValue(key, out hit);
        }
        if (hit == null) return (null, true);
        // The listing can be minutes old: read the item itself for the current state.
        var fresh = await PlexFor(d).ItemAsync(hit.Key, ct);
        return (fresh ?? hit, fresh != null);
    }

    /// <summary>The Zidoos' path for a file of a Plex user's library, by its ratingKey (null when unmapped).</summary>
    public async Task<string?> PlexSourceUrlAsync(Device d, int ratingKey, CancellationToken ct = default)
    {
        var idx = await PlexIndexAsync(d, PlexIndexMaxAge, ct);
        var item = idx?.ByRating.GetValueOrDefault(ratingKey);
        var cfg = _store.Plex();
        return item?.Files.Select(f => PlexPaths.ToSourceUrl(f, cfg.Mappings)).FirstOrDefault(u => u != null);
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

    private readonly ConcurrentDictionary<int, List<string>> _plexFiles = new();

    /// <summary>The file of a playing item. Sessions normally list it; if not, it is read from the item once.</summary>
    private async Task<List<string>> PlexSessionFilesAsync(Device d, PlexItem it, CancellationToken ct)
    {
        if (it.Files.Count > 0 || it.Key <= 0) return it.Files;
        if (_plexFiles.TryGetValue(it.Key, out var known)) return known;
        var full = await PlexFor(d).ItemAsync(it.Key, ct);
        if (full == null || full.Files.Count == 0) return it.Files;
        if (_plexFiles.Count > 200) _plexFiles.Clear();
        return _plexFiles[it.Key] = full.Files;
    }

    private async Task PollPlexAsync(Device d, CancellationToken ct)
    {
        var live = LiveOf(d.Id);
        var cfg = _store.Plex();
        var now = DateTime.UtcNow;
        var sessions = cfg.Ready ? await PlexOwner(cfg).SessionsAsync(TimeSpan.FromSeconds(4), ct) : null;
        if (sessions == null)
        {
            live.ConsecutiveFailures++;
            if (live.Online && live.ConsecutiveFailures >= 2)
            {
                live.Online = false;
                live.OfflineSinceUtc = now;
                _store.Log(ActivityLevel.Info, $"The Plex server isn't answering, so {d.Name} can't be synced until it is.");
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
            if (wasKnownOffline) _store.Log(ActivityLevel.Info, $"The Plex server is answering again ({d.Name}).");
            NudgeQueue();
        }

        var prev = live.Playing;
        var s = sessions.FirstOrDefault(x => IsUser(d, x, cfg) && (x.Item.Type is "movie" or "episode"));
        var files = s == null ? new List<string>() : await PlexSessionFilesAsync(d, s.Item, ct);
        if (s != null && files.Count > 0)
        {
            string file = files[0];
            bool sameFile = prev != null && string.Equals(prev.Path, file, StringComparison.Ordinal);
            if (prev != null && !sameFile) OnStopped(d, prev, now, "switched to another file", canAskPlayer: true);
            var np = new NowPlaying
            {
                Path = file,
                SourceUrl = PlexPaths.ToSourceUrl(file, cfg.Mappings) ?? "",
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
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("plex|" + thumb)))[..16].ToLowerInvariant();
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
        {
            string existing = Path.Combine(_store.PosterDirectory, name + ext);
            if (File.Exists(existing)) { File.SetLastWriteTimeUtc(existing, DateTime.UtcNow); return name + ext; }
        }
        var bytes = await PlexFor(d).ImageAsync(thumb, _stopping);
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
            var api = PlexFor(source);
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
        var api = PlexFor(target);
        var (item, answered) = await FindPlexItemAsync(target, job.SourceUrl, ct);
        if (item == null)
        {
            if (!answered) { Retry(job, what, "the Plex server didn't answer"); return; }
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: Plex doesn't have that file (or the Plex card's folder mapping doesn't cover it).");
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
                ? $"[watch-only] Would reset {job.Title} on {target.Name} ({why}; Plex item {item.Key}, currently {Clock(item.ViewOffsetMs)})."
                : $"[watch-only] Would copy {what} (Plex item {item.Key}, currently {Clock(item.ViewOffsetMs)}).");
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
            FileWritten?.Invoke(target.Id, item.Key, reset ? 0 : finished ? duration : after!.ViewOffsetMs, reset ? -1 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _store.LogCopy(ActivityLevel.Ok, reset ? $"Reset {job.Title} on {target.Name} ({why})." : $"Copied {what}" + (finished ? " (marked played)." : "."));
            Finish(job, TargetState.Ok, reset ? "reset" : finished ? "watched" : "");
        }
        else if (!reset && !finished && after != null && after.ViewOffsetMs == 0 && job.PositionMs < 60_000)
        {
            // Plex doesn't keep a resume point this close to the start (it treats it as not started).
            _store.LogCopy(ActivityLevel.Skip, $"Not copying {what}: Plex doesn't keep a resume point this close to the start.");
            Finish(job, TargetState.Skipped, "too close to the start for Plex");
        }
        else
        {
            Retry(job, what, after == null ? "could not read it back from Plex" : $"Plex still shows {Clock(after.ViewOffsetMs)}{(after.Watched ? ", played" : "")} after the write");
        }
    }

    /// <summary>"Reset TV Show" for a Plex user: "mark as unplayed" on the show clears every episode in one call.</summary>
    private async Task ProcessPlexSeriesResetAsync(PendingCopy job, Device target, PlexClient api, PlexItem episode, CancellationToken ct)
    {
        string what = $"every episode of {job.Title} on {target.Name}";
        if (episode.ShowKey <= 0) { Retry(job, what, "couldn't find the show on Plex"); return; }
        var before = await api.ShowEpisodesAsync(episode.ShowKey, ct);
        if (before == null) { Retry(job, what, "the Plex server didn't answer"); return; }
        var with = before.Where(e => e.ViewOffsetMs > 0 || e.ViewCount > 0).ToList();
        if (_store.Settings().WatchOnly)
        {
            _store.LogCopy(ActivityLevel.Info, $"[watch-only] Would reset {with.Count} episode(s) of {job.Title} on {target.Name}.");
            Finish(job, TargetState.WatchOnly, $"watch-only: would reset {with.Count} episode{(with.Count == 1 ? "" : "s")}");
            return;
        }
        if (with.Count > 0) await api.UnscrobbleAsync(episode.ShowKey, ct);
        var after = with.Count == 0 ? new List<PlexItem>() : await api.ShowEpisodesAsync(episode.ShowKey, ct);
        if (after == null) { Retry(job, what, "could not read the show back from Plex"); return; }
        var left = after.Where(e => e.ViewOffsetMs > 0 || e.ViewCount > 0).ToList();
        foreach (var e in with.Where(w => !left.Any(l => l.Key == w.Key))) FileWritten?.Invoke(target.Id, e.Key, 0, -1);
        if (left.Count > 0) { Retry(job, what, $"{left.Count} episode(s) still show progress on Plex after the reset"); return; }
        _store.LogCopy(ActivityLevel.Ok, $"Reset every episode of {job.Title} on {target.Name} ({with.Count} had progress).");
        Finish(job, TargetState.Ok, with.Count == 0 ? "nothing to reset" : $"{with.Count} episode{(with.Count == 1 ? "" : "s")} reset");
    }
}
