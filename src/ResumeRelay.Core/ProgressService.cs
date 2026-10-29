using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>One file in the library, as the Progress Reset tab lists it. The same file on every player shares one Key.</summary>
public sealed class ProgressFile
{
    /// <summary>The file's share-relative path, lower-case ("/movies/arrival (2016)/arrival (2016).mkv").</summary>
    public string Key { get; set; } = "";
    public string Uri { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsEpisode { get; set; }
    /// <summary>Movie title, or the series name for an episode.</summary>
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string EpisodeName { get; set; } = "";

    /// <summary>What the Title column shows: the full file name for a movie; "Show - S01E17: Episode name" for an episode.</summary>
    public string Display =>
        !IsEpisode ? FileName
        : Season > 0 && Episode > 0
            ? $"{Title} - S{Season:00}E{Episode:00}" + (EpisodeName.Length > 0 && !Regex.IsMatch(EpisodeName, @"^Episode \d+$", RegexOptions.IgnoreCase) ? ": " + EpisodeName : "")
            : $"{Title} - {(EpisodeName.Length > 0 ? EpisodeName : FileName)}";
}

/// <summary>A file's state on one player.</summary>
public sealed class ProgressCell
{
    /// <summary>The player's own id for the file (what setPlayPoint takes).</summary>
    public int FileId { get; set; }
    public int PlayPointMs { get; set; }
    public int DurationMs { get; set; }
    public bool Watched { get; set; }
    /// <summary>Unix ms; -1 when never.</summary>
    public long LastWatchMs { get; set; } = -1;
    /// <summary>The movie or season this file was read from, to read it back after a change.</summary>
    public int TitleId { get; set; }
    public bool FromSeason { get; set; }
}

public sealed class ProgressDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? ReadUtc { get; set; }
    public string Error { get; set; } = "";
    public int Titles { get; set; }
    /// <summary>Every poster on the player's library wall, as "Kind: name" (to compare the walls of the players).</summary>
    public List<string> Wall { get; set; } = new();
    /// <summary>Titles the player never answered for, even when asked again; their last known files are kept.</summary>
    public List<string> Unread { get; set; } = new();
    /// <summary>What the library listing held and how each collection or series was opened (for troubleshooting).</summary>
    public List<string> Diagnostics { get; set; } = new();
    public Dictionary<string, ProgressCell> Cells { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The Progress Reset tab's data: reads every file's resume point on the chosen players, title by title, and keeps the
/// last read of each player in memory. Movies and seasons are read with getDetail, which lists each file with its
/// playPoint, lastWatchTime and share-relative uri. Resets that land update the cached cells, so a reset row leaves
/// the list without reading the players again.
/// </summary>
public sealed class ProgressService
{
    private readonly Store _store;
    private readonly RelayEngine _engine;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProgressFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProgressDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _status = new();
    private int _reading;
    private long _version;
    public long Version => Interlocked.Read(ref _version);
    private void Changed() => Interlocked.Increment(ref _version);

    public ProgressService(Store store, RelayEngine engine)
    {
        _store = store;
        _engine = engine;
        engine.FileWritten += OnFileWritten;
    }

    public bool Reading => Volatile.Read(ref _reading) == 1;
    public List<string> StatusLines => _status.Values.OrderBy(s => s).ToList();

    /// <summary>Starts reading the given players in the background. Returns an error, or null when started.</summary>
    public string? StartRead(IReadOnlyCollection<string> deviceIds, CancellationToken stopping)
    {
        var (err, _) = BeginRead(deviceIds, stopping);
        return err;
    }

    /// <summary>Reads players and waits for it (Catch Up). If a read is already running, waits for it to end first.</summary>
    public async Task ReadAndWaitAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        for (int i = 0; i < 600 && Reading; i++) await Task.Delay(1000, ct);
        var (err, task) = BeginRead(deviceIds, ct);
        if (task != null) await task;
    }

    private (string? Error, Task? Read) BeginRead(IReadOnlyCollection<string> deviceIds, CancellationToken stopping)
    {
        var devices = _store.Devices().Where(d => deviceIds.Contains(d.Id)).ToList();
        if (devices.Count == 0) return ("Tick at least one player to read.", null);
        if (Interlocked.Exchange(ref _reading, 1) == 1) return ("Already reading the players.", null);
        foreach (var k in _status.Keys.Where(k => k != "bulk").ToList()) _status.TryRemove(k, out _);
        foreach (var d in devices) _status[d.Id] = $"Waiting to read {d.Name}…";
        var task = Task.Run(async () =>
        {
            try { await Task.WhenAll(devices.Select(d => ReadDeviceAsync(d, stopping))); }
            finally { foreach (var k in _status.Keys.Where(k => k != "bulk").ToList()) _status.TryRemove(k, out _); Interlocked.Exchange(ref _reading, 0); }
        }, stopping);
        return (null, task);
    }

    /// <summary>A player whose last read had "last watched" times in the future: its clock is probably ahead.</summary>
    public bool ClockAhead(string deviceId)
    {
        long soon = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds();
        lock (_lock) return _devices.TryGetValue(deviceId, out var d) && d.Cells.Values.Any(c => c.LastWatchMs > soon);
    }

    /// <summary>When each player was last read successfully (for Catch Up and the health notes).</summary>
    public DateTime? ReadUtcOf(string deviceId) { lock (_lock) return _devices.GetValueOrDefault(deviceId)?.ReadUtc; }

    /// <summary>
    /// Catch Up: for each file, the player with the newest "last watched" time after <paramref name="sinceUtc"/> wins, and
    /// every other read player that holds the file but shows something different gets its state (resume point,
    /// watched, or reset), through the copy queue, as one Activity card with Undo. Returns the number of files.
    /// Only the given players are compared (each must have been read).
    /// </summary>
    /// <summary>
    /// Whether a player already holds the same thing: the same watched mark and the same resume point (within 2 s).
    /// Lengths aren't compared: Plex and the Zidoos often report a file's length a few seconds apart (a Zidoo that
    /// never played a file may not know it at all), so "watched" on both is the same whatever the lengths say.
    /// </summary>
    internal static bool SameState(ProgressCell a, ProgressCell b) =>
        a.Watched == b.Watched && Math.Abs(Math.Max(0, a.PlayPointMs) - Math.Max(0, b.PlayPointMs)) <= 2000;

    /// <summary>
    /// What to send to copy a cell: its resume point, or for a watched file its length (which marks it watched), or 0
    /// (not started: a reset). A watched file whose player doesn't know its length borrows another player's; if none
    /// knows it, null, because sending 0 would reset (unwatch) it everywhere.
    /// </summary>
    internal static (int Pos, int Dur)? CopyPosition(ProgressCell src, IEnumerable<ProgressCell> others)
    {
        int dur = src.DurationMs > 0 ? src.DurationMs : others.Select(c => c.DurationMs).DefaultIfEmpty(0).Max();
        if (src.PlayPointMs > 0) return (src.PlayPointMs, dur);
        if (!src.Watched) return (0, dur);
        return dur > 0 ? (dur, dur) : null;
    }

    public async Task<(int Files, int Copies)> CatchUpAsync(DateTime sinceUtc, IReadOnlyCollection<string> playerIds, string reason)
    {
        long since = new DateTimeOffset(sinceUtc).ToUnixTimeMilliseconds();
        var devices = _store.Devices().Where(d => d.Enabled && playerIds.Contains(d.Id)).ToDictionary(d => d.Id);
        var plan = new List<(string Key, string WinnerId, int Pos, int Dur, List<Device> To, List<UndoItem> Undo)>();
        // Files with copies still on their way are left alone: what was read for them is about to change.
        var busy = _store.Pending().Select(p => PlexPaths.RelOfUrl(p.SourceUrl)).Where(r => r != null).Select(r => PlexPaths.Key(r!)).ToHashSet();
        lock (_lock)
        {
            var read = _devices.Values.Where(d => d.ReadUtc != null && devices.ContainsKey(d.DeviceId)).ToList();
            foreach (var f in _files.Values)
            {
                var cells = read.Where(d => d.Cells.TryGetValue(f.Key, out var c) && c.FileId > 0).Select(d => (d.DeviceId, Cell: d.Cells[f.Key])).ToList();
                if (cells.Count < 2 || busy.Contains(f.Key)) continue;
                var win = cells.OrderByDescending(x => x.Cell.LastWatchMs).First();
                if (win.Cell.LastWatchMs <= since) continue; // nothing new for this file since Resume Relay last saw the players
                if (CopyPosition(win.Cell, cells.Select(x => x.Cell)) is not var (pos, dur)) continue; // watched, but no player knows its length
                var to = new List<Device>();
                var undo = new List<UndoItem>();
                foreach (var (id, c) in cells.Where(x => x.DeviceId != win.DeviceId))
                {
                    if (SameState(c, win.Cell)) continue;
                    to.Add(devices[id]);
                    undo.Add(new UndoItem { DeviceId = id, Title = f.FileName, PositionMs = c.PlayPointMs, DurationMs = c.DurationMs, Watched = c.Watched });
                }
                if (to.Count > 0) plan.Add((f.Key, win.DeviceId, pos, dur, to, undo));
            }
        }
        if (plan.Count == 0) return (0, 0);
        var items = new List<RelayEngine.BatchItem>();
        var undoAll = new List<UndoItem>();
        foreach (var p in plan)
        {
            if (await UrlForAsync(p.Key) is not { } url) continue;
            items.Add(new RelayEngine.BatchItem(url, _files.GetValueOrDefault(p.Key)?.Display ?? p.Key, p.Pos, p.Dur, p.To, devices[p.WinnerId].Name));
            foreach (var u in p.Undo) { u.SourceUrl = url; undoAll.Add(u); }
        }
        if (items.Count == 0) return (0, 0);
        _engine.CopyBatch("Catch Up", $"{items.Count:N0} file{(items.Count == 1 ? "" : "s")}", reason, RelayEngine.ManualResetFrom, "", items, undoAll);
        return (items.Count, items.Sum(i => i.Targets.Count));
    }

    private async Task ReadDeviceAsync(Device d, CancellationToken ct)
    {
        var result = new ProgressDevice { DeviceId = d.Id, Name = d.Name };
        var files = new ConcurrentDictionary<string, ProgressFile>(StringComparer.Ordinal);
        try
        {
            if (d.IsPlex) { await ReadPlexAsync(d, result, files, ct); return; }
            var api = new ZidooClient(d.Ip, d.Port);
            _status[d.Id] = $"Connecting to {d.Name}…";
            if (await api.GetModelAsync(TimeSpan.FromSeconds(6), ct) == null) { result.Error = "off or not answering"; return; }
            if (!await api.IsHomeTheaterRunningAsync(ct)) { result.Error = "Home Theater app isn't open"; return; }

            // 1. Everything on the poster wall, page by page (some players repeat page 1 instead of ending: stop then).
            _status[d.Id] = $"Listing {d.Name}'s library…";
            var tiles = new List<LibraryRow>();
            var seen = new HashSet<int>();
            for (int page = 1; page <= 200; page++)
            {
                var rows = await api.GetVideoListPageAsync(page, 500, ct);
                for (int retry = 1; rows == null && retry <= 3; retry++) // no answer is not the end of the list
                {
                    await Task.Delay(1000 * retry, ct);
                    rows = await api.GetVideoListPageAsync(page, 500, ct);
                }
                if (rows == null) throw new InvalidOperationException($"the library list stopped answering at page {page}");
                if (rows.Count == 0 || !rows.Any(r => seen.Add(r.Id))) break;
                tiles.AddRange(rows);
            }

            // 2. Open collections and series: movies and seasons are what getDetail reads.
            var work = new List<(int Id, bool Season, string Series, string Name)>();
            var diag = new List<string>();
            var unopened = new List<string>();
            async Task Expand(LibraryRow r, int depth)
            {
                switch (r.Type)
                {
                    case 1: work.Add((r.Id, false, "", r.Name)); break;
                    case 4: work.Add((r.Id, true, SeriesFromSeason(r.Name), r.Name)); break;
                    case 0:
                        lock (diag) diag.Add($"Unmatched file tile \"{r.Name}\" (id {r.Id}): not read (no movie or season to read it from).");
                        break;
                    case 2 or 3 or 6 when depth < 3:
                        // Collections (2) and "my collections" (6) open by the tile's own id, as Z-Keeper does; the
                        // tile's collectionId can point somewhere else. Series (3) keep the order proven on real players.
                        var tries = r.Type == 3
                            ? new[] { r.CollectionId, r.Id }
                            : new[] { r.Id, r.CollectionId };
                        var members = new List<LibraryRow>();
                        string via = "";
                        // Asked again only when the player didn't answer properly (no reply, an error, busy). A
                        // collection that answered and really is empty is not waited on.
                        bool allAnswered = true;
                        for (int attempt = 0; attempt < 3 && members.Count == 0; attempt++)
                        {
                            if (attempt > 0) { if (allAnswered) break; await Task.Delay(1000 * attempt, ct); }
                            allAnswered = true;
                            foreach (int cid in tries.Where(x => x > 0).Distinct())
                            {
                                var (got, ok) = await api.GetCollectionCheckedAsync(cid, ct);
                                allAnswered &= ok;
                                // A collection must hold movies (or other collections); a series must hold seasons or episodes.
                                members = got.Where(m => r.Type == 3 ? m.Type is 4 or 5 : m.Type is 0 or 1 or 2 or 3 or 4 or 6).ToList();
                                if (members.Count > 0) { via = $"getCollection?id={cid}"; break; }
                            }
                            if (members.Count == 0)
                            {
                                var (kids, ok) = await api.GetChildrenCheckedAsync(r.Id, ct);
                                allAnswered &= ok;
                                members = kids; via = $"getEntry children of {r.Id}";
                            }
                        }
                        if (members.Count == 0) lock (unopened) unopened.Add(r.Name);
                        lock (diag) diag.Add($"{(r.Type == 3 ? "Series" : r.Type == 6 ? "My collection" : "Collection")} \"{r.Name}\" (id {r.Id}, collectionId {r.CollectionId}): "
                            + (members.Count == 0 ? "could not be opened" : $"{members.Count} member(s) via {via}: " + string.Join("; ", members.Take(12).Select(m => $"{m.Name} [type {m.Type}, id {m.Id}]"))));
                        foreach (var m in members)
                        {
                            if (r.Type == 3 && m.Type == 4) work.Add((m.Id, true, r.Name, $"{r.Name}: {m.Name}"));
                            else if (r.Type == 3 && m.Type == 5) { work.Add((r.Id, true, r.Name, r.Name)); break; } // single-season listing
                            else await Expand(m, depth + 1);
                        }
                        break;
                }
            }
            result.Wall = tiles.Select(t => $"{TileKind(t.Type)}: {t.Name}").ToList();
            foreach (var t in tiles.Where(t => t.Type == 4)) diag.Add($"Season on the wall by itself: \"{t.Name}\" (id {t.Id})");
            diag.Add($"{tiles.Count:N0} tiles: " + string.Join(", ", tiles.GroupBy(t => t.Type).OrderBy(g => g.Key).Select(g => $"{g.Count():N0} of type {g.Key}")));
            foreach (var t in tiles) await Expand(t, 0);
            result.Diagnostics = diag;
            work = work.DistinctBy(w => w.Id).ToList();
            result.Titles = work.Count;

            // 3. Each movie and season, four at a time. A title the player didn't answer for is not an empty title:
            //    it is set aside and asked again, slowly, after the first pass.
            int done = 0;
            var failed = new ConcurrentBag<(int Id, bool Season, string Series, string Name)>();
            void Keep(List<(ProgressFile File, ProgressCell Cell)> read)
            {
                lock (result.Cells)
                    foreach (var (file, cell) in read)
                    {
                        files.TryAdd(file.Key, file);
                        result.Cells[file.Key] = cell;
                    }
            }
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (w, token) =>
            {
                var (read, answered) = await ReadTitleCheckedAsync(api, w.Id, w.Season, w.Series, token);
                if (answered) Keep(read); else failed.Add(w);
                int n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == work.Count) _status[d.Id] = $"Reading {d.Name}… {n:N0} of {work.Count:N0} titles";
            });
            var left = failed.ToList();
            for (int round = 1; round <= 3 && left.Count > 0; round++)
            {
                _status[d.Id] = $"Reading {d.Name}… asking again for {left.Count:N0} title(s) it didn't answer";
                await Task.Delay(1000 * round, ct);
                var still = new List<(int, bool, string, string)>();
                foreach (var w in left) // one at a time
                {
                    var (read, answered) = await ReadTitleCheckedAsync(api, w.Id, w.Season, w.Series, ct);
                    if (answered) Keep(read); else still.Add(w);
                }
                left = still;
            }
            // Titles that never answered keep what the last read found for them, so their files don't turn into
            // "not in library"; the player's column says how many couldn't be read.
            result.Unread = left.Select(w => w.Name.Length > 0 ? w.Name : $"title {w.Id}").Concat(unopened.Select(n => n + " (collection or series)")).ToList();
            if (left.Count > 0)
                lock (_lock)
                    if (_devices.TryGetValue(d.Id, out var prev))
                    {
                        var ids = left.Select(w => w.Id).ToHashSet();
                        lock (result.Cells)
                            foreach (var (key, cell) in prev.Cells.Where(c => ids.Contains(c.Value.TitleId)))
                                result.Cells.TryAdd(key, cell);
                    }
            foreach (var u in result.Unread) diag.Add($"Couldn't be read (the player didn't answer, even when asked again): {u}");
            result.ReadUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Error = "read failed: " + ex.Message; }
        finally
        {
            lock (_lock)
            {
                foreach (var f in files.Values) _files[f.Key] = f;
                if (result.ReadUtc != null || !_devices.ContainsKey(d.Id)) _devices[d.Id] = result;
                else _devices[d.Id].Error = result.Error; // keep the last good read, but say this one failed
            }
            _status.TryRemove(d.Id, out _);
            Changed();
        }
    }

    /// <summary>A Plex user: the whole library in one listing per library, with that user's progress.</summary>
    private async Task ReadPlexAsync(Device d, ProgressDevice result, ConcurrentDictionary<string, ProgressFile> files, CancellationToken ct)
    {
        _status[d.Id] = $"Reading {d.Name} from Plex…";
        var cfg = _store.Plex();
        var items = await _engine.PlexItemsAsync(d, refresh: true, ct);
        if (items == null) { result.Error = "the Plex server isn't answering"; return; }
        int unmapped = 0;
        foreach (var it in items)
            foreach (var f in it.Files)
            {
                var rel = PlexPaths.Rel(f, cfg.Mappings);
                if (rel == null) { unmapped++; continue; }
                var m = EpisodeTag.Match(Path.GetFileName(rel));
                var file = new ProgressFile
                {
                    Key = PlexPaths.Key(rel),
                    Uri = rel,
                    FileName = Path.GetFileName(rel),
                    IsEpisode = it.IsEpisode,
                    Title = it.IsEpisode ? it.Show : it.Title,
                    Year = it.Year,
                    Season = it.Season > 0 ? it.Season : m.Success ? int.Parse(m.Groups[1].Value) : 0,
                    Episode = it.Episode > 0 ? it.Episode : m.Success ? int.Parse(m.Groups[2].Value) : 0,
                    EpisodeName = it.IsEpisode ? it.Title : "",
                };
                files.TryAdd(file.Key, file);
                result.Cells[file.Key] = new ProgressCell
                {
                    FileId = it.Key,
                    TitleId = it.Key,
                    PlayPointMs = it.ViewOffsetMs,
                    DurationMs = it.DurationMs,
                    Watched = it.Watched,
                    LastWatchMs = it.LastViewedAt > 0 ? it.LastViewedAt * 1000 : -1,
                };
            }
        result.Titles = items.Count;
        result.Diagnostics.Add($"Plex: {items.Count:N0} movies and episodes, {result.Cells.Count:N0} files" + (unmapped > 0 ? $"; {unmapped:N0} file(s) outside the Plex card's folder mapping were left out" : ""));
        result.ReadUtc = DateTime.UtcNow;
    }

    /// <summary>Reads one movie or season. Public for the TV show reset, which reads a series' seasons the same way.</summary>
    public static async Task<List<(ProgressFile File, ProgressCell Cell)>> ReadTitleAsync(ZidooClient api, int id, bool season, string series, CancellationToken ct) =>
        (await ReadTitleCheckedAsync(api, id, season, series, ct)).Files;

    /// <summary>Reads one movie or season. Answered is false when the player never gave a proper answer (no reply,
    /// busy, an error): that title's files are unknown, not absent.</summary>
    public static async Task<(List<(ProgressFile File, ProgressCell Cell)> Files, bool Answered)> ReadTitleCheckedAsync(ZidooClient api, int id, bool season, string series, CancellationToken ct)
    {
        var found = new List<(ProgressFile, ProgressCell)>();
        bool answered = false;
        static bool Ok(JsonDocument d) => d.RootElement.ValueKind == JsonValueKind.Object && (!J.Has(d.RootElement, "status") || J.Int(d.RootElement, "status") == 200);
        using (var doc = await api.GetDetailAsync(id, ct))
            if (doc != null && Ok(doc)) { answered = true; Walk(doc.RootElement, new Ctx { Series = series, IsSeason = season }, found); }
        if (found.Count == 0)
            using (var doc = await api.GetDetailV2Async(id, ct))
                if (doc != null && Ok(doc)) { answered = true; Walk(doc.RootElement, new Ctx { Series = series, IsSeason = season }, found); }
        foreach (var (_, cell) in found) { cell.TitleId = id; cell.FromSeason = season; }
        return (found, answered);
    }

    private sealed record Ctx
    {
        public string Series { get; init; } = "";
        public bool IsSeason { get; init; }
        public string Title { get; init; } = "";
        public int Year { get; init; }
        public int Duration { get; init; }
        public int Season { get; init; }
        public int Episode { get; init; }
        public string EpisodeName { get; init; } = "";
    }

    /// <summary>
    /// Walks a getDetail answer. A movie: root (type 1) → aggregation.aggregations[] file rows (type 0) whose own
    /// "aggregation" holds uri, playPoint and lastWatchTime. A season: root (type 4) → episode rows (type 5, with
    /// seasonNumber/episodeNumber in their aggregation) → their file rows. The file row's id is the write id.
    /// </summary>
    private static void Walk(JsonElement e, Ctx ctx, List<(ProgressFile, ProgressCell)> found)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, ctx, found); return; }
        if (e.ValueKind != JsonValueKind.Object) return;

        int type = J.Int(e, "type", -1);
        var agg = e.TryGetProperty("aggregation", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        int dur = J.Int(e, "duration");
        if (dur > 0) ctx = ctx with { Duration = dur };
        if (type == 1) ctx = ctx with { Title = J.Str(e, "name"), Year = J.Int(e, "year") };
        if (type == 4 && ctx.Series.Length == 0) ctx = ctx with { Series = SeriesFromSeason(J.Str(e, "name")) };
        if (type == 5)
            ctx = ctx with
            {
                EpisodeName = J.Str(e, "name"),
                Season = J.Int(agg, "seasonNumber") is > 0 and var s ? s : J.Int(e, "seasonNumber"),
                Episode = J.Int(agg, "episodeNumber") is > 0 and var n ? n : J.Int(e, "episodeNumber"),
            };

        string uri = J.Str(agg, "uri");
        if (uri.Length == 0 && type == 0) uri = J.Str(e, "uri");
        if (uri.Length > 0 && type is 0 or -1)
        {
            int pp = J.Has(agg, "playPoint") ? J.Int(agg, "playPoint") : J.Has(e, "playPoint") ? J.Int(e, "playPoint") : J.Int(e, "position");
            int fileDur = dur > 0 ? dur : J.Int(agg, "duration") is > 0 and var ad ? ad : ctx.Duration;
            bool watched = J.Bool(e, "watched") || J.Bool(agg, "watched") || pp < 0; // markAsWatched parks the point as a negative value
            if (pp < 0) pp = 0;
            string name = Path.GetFileName(uri.TrimEnd('/'));
            var m = EpisodeTag.Match(name);
            bool episode = ctx.IsSeason || ctx.Episode > 0;
            var file = new ProgressFile
            {
                Key = PlexPaths.RelOfUrl(uri) is { } r ? PlexPaths.Key(r) : uri.Replace('\\', '/').ToLowerInvariant(),
                Uri = uri,
                FileName = name,
                IsEpisode = episode,
                Title = episode ? (ctx.Series.Length > 0 ? ctx.Series : ctx.Title) : (ctx.Title.Length > 0 ? ctx.Title : name),
                Year = ctx.Year,
                Season = ctx.Season > 0 ? ctx.Season : m.Success ? int.Parse(m.Groups[1].Value) : 0,
                Episode = ctx.Episode > 0 ? ctx.Episode : m.Success ? int.Parse(m.Groups[2].Value) : 0,
                EpisodeName = ctx.EpisodeName,
            };
            found.Add((file, new ProgressCell
            {
                FileId = J.Int(e, "id"),
                PlayPointMs = pp,
                DurationMs = fileDur,
                Watched = watched,
                LastWatchMs = J.Has(agg, "lastWatchTime") ? J.Long(agg, "lastWatchTime") : J.Has(e, "lastWatchTime") ? J.Long(e, "lastWatchTime") : -1,
            }));
            return;
        }

        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(p.Value, ctx, found);
    }

    /// <summary>"The Matrix" sorts under M, "A Beautiful Mind" under B.</summary>
    public static string SortTitle(string title) => Regex.Replace(title, @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase);

    private static string TileKind(int type) => type switch
    {
        0 => "Unmatched file", 1 => "Movie", 2 => "Collection", 3 => "Show", 4 => "Season", 6 => "My collection", _ => $"Type {type}",
    };

    private static readonly Regex EpisodeTag = new(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase);
    private static string SeriesFromSeason(string name) => Regex.Replace(name, @"\s*[-:]?\s*Season\s+\d+\s*$", "", RegexOptions.IgnoreCase).Trim();

    // ---------- results ----------

    public object Snapshot()
    {
        lock (_lock)
        {
            var shown = _files.Values.Where(f => _devices.Values.Any(d => d.Cells.TryGetValue(f.Key, out var c) && c.PlayPointMs > 0));
            return new
            {
                Version,
                Reading,
                Status = _status.Values.OrderBy(s => s).ToList(),
                Devices = _devices.Values.Select(d => new { d.DeviceId, d.Name, d.ReadUtc, d.Error, d.Titles, Unread = d.Unread.Count }),
                Jobs = Jobs(),
                Files = shown.Select(f => new
                {
                    f.Key, f.Display, f.IsEpisode, f.Title, f.FileName,
                    Cells = _devices.Values.Where(d => d.Cells.ContainsKey(f.Key)).ToDictionary(d => d.DeviceId, d => d.Cells[f.Key]),
                }).ToList(),
            };
        }
    }

    /// <summary>
    /// The Watch Sync list, one page at a time. show: "different" (watched on some of the chosen players that have
    /// the file, not on others), "watched" (on any of them), "unwatched" (on none), "missing" (some of the chosen
    /// players have the file and some don't), or "all". sort: "title" or "watched" (most recently watched first).
    /// </summary>
    /// <summary>The Watch Sync list as filtered (Show, ticked players, search), before sorting and pages. Call under _lock.</summary>
    private (List<ProgressDevice> Devs, List<ProgressFile> Rows) WatchRows(string show, IReadOnlyCollection<string> players, string search)
    {
        var devs = _devices.Values.Where(d => d.ReadUtc != null && (players.Count == 0 || players.Contains(d.DeviceId))).ToList();
        var q = search.Trim();
        var rows = _files.Values.Where(f =>
        {
            if (q.Length > 0 && !f.Display.Contains(q, StringComparison.OrdinalIgnoreCase) && !f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
            var cells = devs.Select(d => d.Cells.GetValueOrDefault(f.Key)).Where(c => c != null).ToList();
            if (cells.Count == 0) return false;
            if (show == "movies" && f.IsEpisode || show == "tv" && !f.IsEpisode) return false;
            int w = cells.Count(c => c!.Watched);
            return show switch
            {
                "missing" => cells.Count < devs.Count,
                "watched" => w > 0,
                "unwatched" => w == 0,
                "all" or "movies" or "tv" => true,
                _ => w > 0 && w < cells.Count,
            };
        }).ToList();
        return (devs, rows);
    }

    public object WatchSnapshot(string show, IReadOnlyCollection<string> players, string search, string sort, int page, int pageSize)
    {
        lock (_lock)
        {
            var (devs, rows) = WatchRows(show, players, search);
            long LastWatched(ProgressFile f) => devs.Select(d => d.Cells.GetValueOrDefault(f.Key)?.LastWatchMs ?? -1).DefaultIfEmpty(-1).Max();
            var ordered = sort == "watched"
                ? rows.OrderByDescending(LastWatched).ThenBy(f => SortTitle(f.Display), NaturalOrder.Instance)
                : rows.OrderBy(f => SortTitle(f.Display), NaturalOrder.Instance);
            pageSize = Math.Clamp(pageSize, 1, 2000);
            int pages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);
            return new
            {
                Version,
                Reading,
                Status = _status.Values.OrderBy(s => s).ToList(),
                Devices = _devices.Values.Select(d => new { d.DeviceId, d.Name, d.ReadUtc, d.Error, d.Titles, Unread = d.Unread.Count }),
                Jobs = Jobs(),
                Total = rows.Count,
                Page = page,
                Pages = pages,
                PageSize = pageSize,
                Files = ordered
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(f => new
                    {
                        f.Key, f.Display, f.IsEpisode, f.Title, f.FileName,
                        Cells = _devices.Values.Where(d => d.Cells.ContainsKey(f.Key)).ToDictionary(d => d.DeviceId, d => d.Cells[f.Key]),
                    }).ToList(),
            };
        }
    }

    /// <summary>
    /// Marks one file watched or unwatched on the given players, and reads it back from each.
    /// Watched: markAsWatched (falls back to a resume point at the end). Unwatched: the file's own resume point is
    /// written back, which clears the watched mark but keeps the progress (0 when there was none).
    /// </summary>
    public async Task<List<WatchResult>> SetWatchedAsync(string key, IReadOnlyCollection<string> deviceIds, bool watched)
    {
        var results = new List<WatchResult>();
        ProgressFile? file;
        lock (_lock) file = _files.GetValueOrDefault(key);
        if (file == null) return results;
        foreach (string id in deviceIds.Distinct())
        {
            var d = _store.Device(id);
            if (d == null) continue;
            ProgressCell? cell;
            lock (_lock) cell = _devices.GetValueOrDefault(id)?.Cells.GetValueOrDefault(key);
            if (cell == null || cell.FileId <= 0) { results.Add(new(id, d.Name, false, "not in its library")); continue; }
            if (!_engine.LiveOf(id).Online) { results.Add(new(id, d.Name, false, "is off or not answering")); continue; }
            if (d.IsPlex) { results.Add(await SetPlexWatchedAsync(d, file, key, cell, watched)); continue; }

            var api = new ZidooClient(d.Ip, d.Port);
            async Task<ProgressCell?> ReadBack()
            {
                var now = await ReadTitleAsync(api, cell.TitleId, cell.FromSeason, file.Title, default);
                return now.FirstOrDefault(x => x.File.Key == key).Cell;
            }
            ProgressCell? after;
            if (watched)
            {
                // Watched, with no resume point left (reset first: "mark as watched" leaves the point of a file that's
                // already watched).
                var entry = await api.GetEntryAsync(cell.FileId);
                after = entry != null ? (await RelayEngine.MarkWatchedCleanAsync(api, entry)).After : null;
                if (after == null) after = await ReadBack();
            }
            else
            {
                int keep = cell.PlayPointMs > 0 && !NearEnd(cell.PlayPointMs, cell.DurationMs) ? cell.PlayPointMs : 0;
                if (keep > 0) await api.SetPlayPointAsync(cell.FileId, keep, cell.DurationMs, cell.LastWatchMs > 0 ? cell.LastWatchMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                else await api.ResetPlayPointAsync(cell.FileId);
                after = await ReadBack();
            }

            bool ok = after != null && after.Watched == watched;
            if (after != null)
                lock (_lock)
                    if (_devices.TryGetValue(id, out var dev) && dev.Cells.TryGetValue(key, out var c))
                    {
                        c.PlayPointMs = after.PlayPointMs; c.Watched = after.Watched; c.LastWatchMs = after.LastWatchMs;
                        if (after.DurationMs > 0) c.DurationMs = after.DurationMs;
                    }
            Changed();
            _store.LogCopy(ok ? ActivityLevel.Ok : ActivityLevel.Error, ok
                ? $"Marked {file.FileName} {(watched ? "watched" : "unwatched")} on {d.Name} (Watch Sync)."
                : $"Could not mark {file.FileName} {(watched ? "watched" : "unwatched")} on {d.Name}: the player still shows it {(after?.Watched == true ? "watched" : "unwatched")}.");
            results.Add(new(id, d.Name, ok, ok ? "" : "the player didn't take the change"));
        }
        return results;
    }

    /// <summary>Watch Sync for a Plex user: "mark as played" / "mark as unplayed" (which clears the resume point, so it is
    /// written back afterwards), then read back.</summary>
    private async Task<WatchResult> SetPlexWatchedAsync(Device d, ProgressFile file, string key, ProgressCell cell, bool watched)
    {
        var api = _engine.PlexFor(d);
        if (watched) await api.ScrobbleAsync(cell.FileId);
        else
        {
            int keep = cell.PlayPointMs > 0 && !NearEnd(cell.PlayPointMs, cell.DurationMs) ? cell.PlayPointMs : 0;
            await api.UnscrobbleAsync(cell.FileId);
            if (keep > 0) await api.SetProgressAsync(cell.FileId, keep);
        }
        var after = await api.ItemAsync(cell.FileId);
        bool ok = after != null && after.Watched == watched;
        if (after != null)
            lock (_lock)
                if (_devices.TryGetValue(d.Id, out var dev) && dev.Cells.TryGetValue(key, out var c))
                {
                    c.PlayPointMs = after.ViewOffsetMs; c.Watched = after.Watched;
                    c.LastWatchMs = after.LastViewedAt > 0 ? after.LastViewedAt * 1000 : -1;
                    if (after.DurationMs > 0) c.DurationMs = after.DurationMs;
                }
        Changed();
        _store.LogCopy(ok ? ActivityLevel.Ok : ActivityLevel.Error, ok
            ? $"Marked {file.FileName} {(watched ? "watched" : "unwatched")} on {d.Name} (Watch Sync)."
            : $"Could not mark {file.FileName} {(watched ? "watched" : "unwatched")} on {d.Name}: Plex still shows it {(after?.Watched == true ? "played" : "unplayed")}.");
        return new WatchResult(d.Id, d.Name, ok, ok ? "" : "Plex didn't take the change");
    }

    /// <summary>A Watched All / Unwatched All / Copy all (Watch Sync) / cleanup job. They run one at a time, in order.</summary>
    private sealed class BulkJob
    {
        public string Id = Guid.NewGuid().ToString("N")[..8];
        public string Key = "";         // what it's about: a show's title, "tidy", "copy:<player id>"
        public bool Watched;
        public string Label = "";       // "Marking The Big Bang Theory watched"
        /// <summary>Works out the changes when the job starts (not when it was queued), so it acts on what earlier jobs left.
        /// Each file: the players to change and whether to mark it watched there.</summary>
        public Func<List<(string Key, List<string> Devices, bool Watched)>> Plan = () => new();
        public Func<int, int, string> Summary = (_, _) => "";
        /// <summary>When set, the job gets an Activity card (chips counting files per player) with Undo.</summary>
        public (string Title, string Summary, string FromName, string FromKind)? Card;
        public int Total, Done;
        public bool Running, Cancel;
    }
    private readonly List<BulkJob> _jobs = new();
    private bool _worker;

    /// <summary>The queue as the tabs show it: what's running (with its count) and what's waiting.</summary>
    public object Jobs()
    {
        lock (_jobs) return _jobs.Select(j => new { j.Id, j.Key, j.Watched, j.Label, j.Running, j.Done, j.Total }).ToList();
    }

    /// <summary>The show being marked (kept for older pages).</summary>
    public string ShowBusy { get { lock (_jobs) return _jobs.FirstOrDefault(j => j.Running)?.Key ?? ""; } }

    /// <summary>Removes a waiting job, or stops the running one after the file it's on.</summary>
    public bool CancelJob(string id)
    {
        lock (_jobs)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j == null) return false;
            if (j.Running) j.Cancel = true; else _jobs.Remove(j);
        }
        Changed();
        return true;
    }

    /// <summary>
    /// "Watched All" / "Unwatched All" on a TV show's heading (Watch Sync): every episode of the show that the last read
    /// found, on the given players, each one marked like the per-file buttons do (and read back). Only cells that need
    /// it are touched (marking watched also tidies a watched episode that kept a resume point). dryRun only counts.
    /// </summary>
    public (string? Error, int Episodes, int Changes, int LosingResume) SetShowWatched(string show, IReadOnlyCollection<string> deviceIds, bool watched, bool dryRun)
    {
        List<(string Key, List<string> Devices, int Losing)>? Plan()
        {
            lock (_lock)
            {
                var eps = _files.Values.Where(f => f.IsEpisode && string.Equals(f.Title, show, StringComparison.OrdinalIgnoreCase)).ToList();
                if (eps.Count == 0) return null;
                return eps.Select(f =>
                {
                    var devs = deviceIds.Where(id => _devices.TryGetValue(id, out var d) && d.Cells.TryGetValue(f.Key, out var c) && c.FileId > 0
                                                     && (watched ? !c.Watched || c.PlayPointMs > 0 : c.Watched)).ToList();
                    int losing = watched ? devs.Count(id => !_devices[id].Cells[f.Key].Watched && _devices[id].Cells[f.Key].PlayPointMs > 0) : 0;
                    return (f.Key, devs, losing);
                }).ToList();
            }
        }
        var work = Plan();
        if (work == null) return ("That show is no longer in the list. Read the players again.", 0, 0, 0);
        int changes = work.Sum(w => w.Devices.Count), lose = work.Sum(w => w.Losing), episodes = work.Count;
        // Queued even when nothing needs changing right now: a job ahead of it may change that.
        bool ahead; lock (_jobs) ahead = _jobs.Count > 0;
        if (dryRun || (changes == 0 && !ahead)) return (null, episodes, changes, lose);
        string what = watched ? "watched" : "unwatched";
        string? err = Enqueue(new BulkJob
        {
            Key = show, Watched = watched, Label = $"Marking {show} {what}",
            Plan = () => (Plan() ?? new()).Select(w => (w.Key, w.Devices, watched)).ToList(),
            Card = (show, $"Watch Sync: every episode marked {what}", RelayEngine.ManualResetFrom, ""),
            Summary = (n, failed) => $"Watch Sync: marked {show} {what} ({n:N0} change{(n == 1 ? "" : "s")} across {episodes:N0} episodes)"
                                     + (failed > 0 ? $"; {failed} didn't take. See the Watch Sync tab." : "."),
        });
        return (err, episodes, changes, lose);
    }

    /// <summary>
    /// "Copy all" from one player's column on Watch Sync: for every file in the list as it's filtered (all pages), the
    /// ticked players get the same watched mark as that player. Files it doesn't have, and players that already match,
    /// are left alone. dryRun only counts.
    /// </summary>
    public (string? Error, int Files, int ToWatched, int ToUnwatched) CopyWatchedFrom(string fromId, IReadOnlyCollection<string> toIds,
        string show, IReadOnlyCollection<string> players, string search, bool dryRun)
    {
        var source = _store.Device(fromId);
        if (source == null) return ("That player is no longer there.", 0, 0, 0);
        var targets = toIds.Where(id => id != fromId).Distinct().ToList();
        if (targets.Count == 0) return ("Tick Target on at least one other player.", 0, 0, 0);
        List<(string Key, List<string> Devices, bool Watched)> Plan()
        {
            lock (_lock)
            {
                if (!_devices.TryGetValue(fromId, out var src)) return new();
                return WatchRows(show, players, search).Rows
                    .Where(f => src.Cells.TryGetValue(f.Key, out var c) && c.FileId > 0)
                    .Select(f =>
                    {
                        bool w = src.Cells[f.Key].Watched;
                        var devs = targets.Where(id => _devices.TryGetValue(id, out var d) && d.Cells.TryGetValue(f.Key, out var c) && c.FileId > 0
                                                       && (c.Watched != w || (w && c.PlayPointMs > 0))).ToList();
                        return (f.Key, devs, w);
                    })
                    .Where(x => x.devs.Count > 0).ToList();
            }
        }
        var work = Plan();
        int toW = work.Where(w => w.Watched).Sum(w => w.Devices.Count), toU = work.Where(w => !w.Watched).Sum(w => w.Devices.Count);
        if (dryRun || work.Count == 0) return (null, work.Count, toW, toU);
        string? err = Enqueue(new BulkJob
        {
            Key = "copy:" + fromId, Watched = true, Label = $"Copying watched marks from {source.Name}", Plan = Plan,
            Card = ($"Watched marks from {source.Name}", $"Copied from {source.Name} on Watch Sync", source.Name, source.IsPlex ? "plex" : "zidoo"),
            Summary = (n, failed) => $"Watch Sync: copied {source.Name}'s watched marks ({n:N0} change{(n == 1 ? "" : "s")})" + (failed > 0 ? $"; {failed} didn't take." : "."),
        });
        return (err, work.Count, toW, toU);
    }

    /// <summary>
    /// The one-time cleanup (Progress Reset): every file a Zidoo has marked watched that still holds a resume point (a
    /// stop near the end keeps it) is set to watched with no resume point, like Plex stores it. dryRun only counts.
    /// </summary>
    public (string? Error, int Changes, int Files) TidyWatched(IReadOnlyCollection<string> deviceIds, bool dryRun)
    {
        List<(string Key, List<string> Devices, bool Watched)> Plan()
        {
            lock (_lock)
                return _files.Values.Select(f => (f.Key, deviceIds.Where(id => _store.Device(id) is { IsPlex: false }
                        && _devices.TryGetValue(id, out var d) && d.Cells.TryGetValue(f.Key, out var c) && c.FileId > 0 && c.Watched && c.PlayPointMs > 0).ToList(), true))
                    .Where(w => w.Item2.Count > 0).ToList();
        }
        var work = Plan();
        int changes = work.Sum(w => w.Item2.Count);
        if (dryRun || changes == 0) return (null, changes, work.Count);
        return (Enqueue(new BulkJob
        {
            Key = "tidy", Watched = true, Label = "Clearing leftover resume points", Plan = Plan,
            Summary = (n, failed) => $"Progress Reset: cleared {n:N0} leftover resume point{(n == 1 ? "" : "s")} on watched files" + (failed > 0 ? $"; {failed} didn't take." : "."),
        }), changes, work.Count);
    }

    /// <summary>Queues a job (refused when the same one is already running or waiting). Jobs run one at a time in the
    /// background, one file at a time; the tabs show its progress and cells update as each file is read back.</summary>
    private string? Enqueue(BulkJob job)
    {
        lock (_jobs)
        {
            var same = _jobs.FirstOrDefault(j => j.Key == job.Key && j.Watched == job.Watched && !j.Cancel);
            if (same != null) return same.Running ? $"{same.Label} is already running." : $"{same.Label} is already queued.";
            job.Total = job.Plan().Sum(w => w.Devices.Count);
            _jobs.Add(job);
            if (!_worker) { _worker = true; _ = Task.Run(WorkAsync); }
        }
        Changed();
        return null;
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            BulkJob? job;
            lock (_jobs)
            {
                job = _jobs.FirstOrDefault();
                if (job == null) { _worker = false; return; }
                job.Running = true;
            }
            int failed = 0;
            string cardId = "";
            try
            {
                var work = job.Plan().Where(w => w.Devices.Count > 0).ToList();
                job.Total = work.Sum(w => w.Devices.Count);
                if (job.Card is { } c && work.Count > 0) cardId = await NewJobCardAsync(job, c, work);
                foreach (var w in work)
                {
                    if (job.Cancel)
                    {
                        foreach (var dev in w.Devices) _store.UpdateEventTarget(cardId, dev, TargetState.Skipped, "stopped", w.Key);
                        continue;
                    }
                    Changed();
                    var r = await SetWatchedAsync(w.Key, w.Devices, w.Watched);
                    job.Done += w.Devices.Count;
                    failed += r.Count(x => !x.Ok);
                    foreach (var x in r) _store.UpdateEventTarget(cardId, x.DeviceId, x.Ok ? TargetState.Ok : TargetState.Failed, x.Error, w.Key);
                    foreach (var dev in w.Devices.Where(d => r.All(x => x.DeviceId != d)))
                        _store.UpdateEventTarget(cardId, dev, TargetState.Skipped, "no longer in the list", w.Key);
                }
                _store.Log(failed == 0 && !job.Cancel ? ActivityLevel.Ok : ActivityLevel.Warn,
                    job.Cancel ? $"{job.Label}: stopped after {job.Done:N0} of {job.Total:N0}." : job.Summary(job.Total, failed));
            }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, $"{job.Label} stopped: {ex.Message}"); }
            finally
            {
                lock (_jobs) _jobs.Remove(job);
                Changed();
            }
        }
    }

    /// <summary>The Activity card for a job, with what each player held before (for Undo).</summary>
    private async Task<string> NewJobCardAsync(BulkJob job, (string Title, string Summary, string FromName, string FromKind) c,
        List<(string Key, List<string> Devices, bool Watched)> work)
    {
        var undo = new List<UndoItem>();
        foreach (var w in work)
        {
            string url = await UrlForAsync(w.Key) ?? "";
            lock (_lock)
                foreach (var dev in w.Devices)
                    if (_devices.TryGetValue(dev, out var d) && d.Cells.TryGetValue(w.Key, out var cell))
                        undo.Add(new UndoItem { DeviceId = dev, SourceUrl = url, Title = _files.GetValueOrDefault(w.Key)?.FileName ?? w.Key,
                                                PositionMs = cell.PlayPointMs, DurationMs = cell.DurationMs, Watched = cell.Watched });
        }
        var devices = _store.Devices().ToDictionary(d => d.Id);
        var ev = new StopEvent
        {
            TimeUtc = DateTime.UtcNow, Title = c.Title, Subtitle = $"{work.Count:N0} file{(work.Count == 1 ? "" : "s")}", Summary = c.Summary,
            FromName = c.FromName, FromKind = c.FromKind, Batch = true, Manual = true, Files = work.Count, Undo = undo,
            Changes = work.Select(w => new BatchChange { Key = w.Key, Title = _files.GetValueOrDefault(w.Key)?.Display ?? w.Key, Watched = w.Watched }).ToList(),
            Targets = work.SelectMany(w => w.Devices).Distinct().Where(devices.ContainsKey).Select(id => new StopTarget
            {
                DeviceId = id, Name = devices[id].Name, Kind = devices[id].IsPlex ? "plex" : "zidoo",
                Items = work.Where(w => w.Devices.Contains(id)).ToDictionary(w => w.Key, _ => TargetState.Pending),
            }).ToList(),
        };
        foreach (var t in ev.Targets) Store.CountBatch(t);
        _store.AddEvent(ev);
        return ev.Id;
    }

    /// <summary>
    /// "Copy all" from one player's column on Progress Reset: for every file given (the list as filtered), the ticked
    /// players get that player's progress, through the copy queue, as one Activity card with Undo. Files that player
    /// hasn't started are left out unless includeNotStarted (then they're reset on the others). dryRun only counts.
    /// </summary>
    public async Task<(string? Error, int Files, int Copies, int Resets, int NotStarted)> CopyProgressFrom(string fromId, IReadOnlyCollection<string> toIds,
        IReadOnlyCollection<string> keys, bool includeNotStarted, bool dryRun)
    {
        var source = _store.Device(fromId);
        if (source == null) return ("That player is no longer there.", 0, 0, 0, 0);
        var targets = toIds.Where(id => id != fromId).Distinct().Select(id => _store.Device(id)).Where(d => d != null).Select(d => d!).ToList();
        if (targets.Count == 0) return ("Tick Target on at least one other player.", 0, 0, 0, 0);
        var plan = new List<(string Key, int Pos, int Dur, List<Device> To, List<UndoItem> Undo)>();
        int notStarted = 0;
        lock (_lock)
        {
            if (!_devices.TryGetValue(fromId, out var src)) return ("Read that player first.", 0, 0, 0, 0);
            foreach (string key in keys.Distinct())
            {
                if (!src.Cells.TryGetValue(key, out var sc) || sc.FileId <= 0) continue;
                var others = targets.Select(t => _devices.TryGetValue(t.Id, out var d) && d.Cells.TryGetValue(key, out var c) ? c : null).Where(c => c != null).Select(c => c!);
                if (CopyPosition(sc, others) is not var (pos, dur)) continue; // watched, but no player knows its length
                if (pos == 0 && !includeNotStarted)
                {
                    if (targets.Any(t => _devices.TryGetValue(t.Id, out var d) && d.Cells.TryGetValue(key, out var c) && (c.PlayPointMs > 0 || c.Watched))) notStarted++;
                    continue;
                }
                var to = new List<Device>();
                var undo = new List<UndoItem>();
                foreach (var t in targets)
                {
                    if (!_devices.TryGetValue(t.Id, out var d) || !d.Cells.TryGetValue(key, out var c) || c.FileId <= 0) continue;
                    if (SameState(c, sc)) continue;
                    to.Add(t);
                    undo.Add(new UndoItem { DeviceId = t.Id, Title = _files.GetValueOrDefault(key)?.FileName ?? key, PositionMs = c.PlayPointMs, DurationMs = c.DurationMs, Watched = c.Watched });
                }
                if (to.Count > 0) plan.Add((key, pos, dur, to, undo));
            }
        }
        int copies = plan.Where(p => p.Pos > 0).Sum(p => p.To.Count), resets = plan.Where(p => p.Pos == 0).Sum(p => p.To.Count);
        if (dryRun || plan.Count == 0) return (null, plan.Count, copies, resets, notStarted);

        var items = new List<RelayEngine.BatchItem>();
        var undoAll = new List<UndoItem>();
        foreach (var p in plan)
        {
            string? url = await UrlForAsync(p.Key);
            if (url == null) continue; // none of the players holding it answered: left out
            string label = _files.GetValueOrDefault(p.Key)?.Display ?? p.Key;
            items.Add(new RelayEngine.BatchItem(url, label, p.Pos, p.Dur, p.To));
            foreach (var u in p.Undo) { u.SourceUrl = url; undoAll.Add(u); }
        }
        if (items.Count == 0) return ("None of the players holding these files is answering right now.", 0, 0, 0, notStarted);
        _engine.CopyBatch($"Progress from {source.Name}", $"{items.Count:N0} file{(items.Count == 1 ? "" : "s")}", $"Copied from {source.Name} on Progress Reset",
            source.Name, source.IsPlex ? "plex" : "zidoo", items, undoAll);
        return (null, items.Count, copies, resets, notStarted);
    }

    // ---------- each file's network path (for the copy queue), without asking a player for every file ----------

    private readonly ConcurrentDictionary<string, string> _sharePrefix = new();

    /// <summary>
    /// A file's network path (smb://…). A Zidoo is asked for one file's path once; what comes before the file's in-share
    /// path is that player's share address, used for every other file it has. A Plex user's path comes from the folder
    /// mapping. Null when no player holding it answers.
    /// </summary>
    public async Task<string?> UrlForAsync(string key)
    {
        if (Find(key) is not { } found) return null;
        string rel = found.File.Uri;
        foreach (var (deviceId, fileId) in found.Holders.OrderBy(h => _store.Device(h.DeviceId)?.IsPlex == true).ThenByDescending(h => _engine.LiveOf(h.DeviceId).Online))
        {
            var d = _store.Device(deviceId);
            if (d == null || fileId <= 0) continue;
            if (d.IsPlex) { if (await _engine.PlexSourceUrlAsync(d, fileId) is { } pu) return pu; continue; }
            if (rel.Length > 0 && _sharePrefix.TryGetValue(deviceId, out var prefix)) return prefix + rel;
            var entry = await new ZidooClient(d.Ip, d.Port).GetEntryAsync(fileId);
            if (entry == null || entry.Path.Length == 0) continue;
            if (rel.Length > 0 && entry.Path.EndsWith(rel, StringComparison.OrdinalIgnoreCase)) _sharePrefix[deviceId] = entry.Path[..^rel.Length];
            return entry.Path;
        }
        return null;
    }

    /// <summary>Plain-text report of the last read of each player, for troubleshooting a "not in library".</summary>
    public string DiagnosticsText(string search)
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            // Posters one player's wall shows and another's doesn't (or shows a different number of times).
            var walls = _devices.Values.Where(d => d.Wall.Count > 0).OrderBy(d => d.Name).ToList();
            if (walls.Count > 1)
            {
                var counts = walls.Select(d => d.Wall.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)).ToList();
                var diffs = counts.SelectMany(c => c.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(k => counts.Select(c => c.GetValueOrDefault(k)).Distinct().Count() > 1)
                    .Where(k => search.Length == 0 || k.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, NaturalOrder.Instance).ToList();
                sb.AppendLine(diffs.Count == 0
                    ? "== Library walls: every player shows the same posters."
                    : $"== Library walls: {diffs.Count} poster(s) differ between players (number of times each wall shows it):");
                foreach (var k in diffs)
                    sb.AppendLine($"  {k} — " + string.Join(", ", walls.Select((d, i) => $"{d.Name} {counts[i].GetValueOrDefault(k)}")));
                sb.AppendLine();
            }
            foreach (var d in _devices.Values.OrderBy(d => d.Name))
            {
                sb.AppendLine($"== {d.Name} · read {d.ReadUtc?.ToLocalTime():g} · {d.Titles:N0} titles · {d.Cells.Count:N0} files{(d.Unread.Count > 0 ? $" · {d.Unread.Count} couldn't be read" : "")}{(d.Error.Length > 0 ? " · " + d.Error : "")}");
                foreach (var line in d.Diagnostics.Where((l, i) => i == 0 || search.Length == 0 || l.StartsWith("Couldn't be read") || l.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    sb.AppendLine("  " + line);
                if (search.Length > 0)
                    foreach (var f in _files.Values.Where(f => d.Cells.ContainsKey(f.Key) && f.Display.Contains(search, StringComparison.OrdinalIgnoreCase)))
                        sb.AppendLine($"  file: {f.Uri} (id {d.Cells[f.Key].FileId}, title {d.Cells[f.Key].TitleId})");
                sb.AppendLine();
            }
            return sb.Length == 0 ? "Nothing read yet. Press Read players first." : sb.ToString();
        }
    }

    /// <summary>A player that holds this file, with its id there (to look up the file's network path).</summary>
    public (ProgressFile File, List<(string DeviceId, int FileId)> Holders)? Find(string key)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(key, out var f)) return null;
            var holders = _devices.Values.Where(d => d.Cells.ContainsKey(key)).Select(d => (d.DeviceId, d.Cells[key].FileId)).ToList();
            return (f, holders);
        }
    }

    private void OnFileWritten(string deviceId, int fileId, int positionMs, long lastWatchMs, bool watched)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var d)) return;
            foreach (var c in d.Cells.Values.Where(c => c.FileId == fileId))
            {
                c.PlayPointMs = positionMs;
                c.LastWatchMs = lastWatchMs;
                c.Watched = watched;
            }
            Changed();
        }
    }

    private static bool NearEnd(int positionMs, int durationMs) =>
        durationMs > 0 && (positionMs >= durationMs * 0.93 || durationMs - positionMs <= 5 * 60_000);

    // ---------- resets from the tab ----------

    /// <summary>"Reset": the same as Reset Progress on an Activity card, for one row of the list.</summary>
    public async Task<string?> ResetFileAsync(string key)
    {
        var (file, url, poster) = await ResolveAsync(key);
        if (file == null) return url; // url holds the error
        string subtitle = file.IsEpisode ? (file.Season > 0 ? $"S{file.Season:00}E{file.Episode:00}" : "") : (file.Year > 0 ? file.Year.ToString() : "");
        return _engine.ResetFile(file.Title, subtitle, file.FileName, url, poster, 0).Error is { Length: > 0 } e ? e : null;
    }

    /// <summary>
    /// Copies one player's progress for a file to other players (dragged on the Progress Reset tab). A file marked
    /// watched with no resume point (how Plex keeps a played file) is copied as its full length, which marks it watched.
    /// </summary>
    public async Task<(string? Error, string? CardId)> CopyAsync(string key, string fromId, IReadOnlyCollection<string> toIds)
    {
        ProgressCell? cell;
        lock (_lock) cell = _devices.GetValueOrDefault(fromId)?.Cells.GetValueOrDefault(key);
        var source = _store.Device(fromId);
        if (cell == null || source == null) return ("That player's progress for this file is no longer in the list. Read the players again.", null);
        var targets = toIds.Distinct().Where(id => id != fromId).Select(id => _store.Device(id)).Where(d => d != null).Select(d => d!).ToList();
        if (targets.Count == 0) return ("Nothing to copy to.", null);
        var (file, url, poster) = await ResolveAsync(key);
        if (file == null) return (url, null);
        int pos = cell.PlayPointMs > 0 ? cell.PlayPointMs : cell.Watched && cell.DurationMs > 0 ? cell.DurationMs : 0;
        string subtitle = file.IsEpisode ? (file.Season > 0 ? $"S{file.Season:00}E{file.Episode:00}" : "") : (file.Year > 0 ? file.Year.ToString() : "");
        var undo = new List<UndoItem>();
        lock (_lock)
            foreach (var t in targets)
                if (_devices.TryGetValue(t.Id, out var d) && d.Cells.TryGetValue(key, out var c))
                    undo.Add(new UndoItem { DeviceId = t.Id, SourceUrl = url, Title = file.FileName, PositionMs = c.PlayPointMs, DurationMs = c.DurationMs, Watched = c.Watched });
        var (card, err) = _engine.CopyFile(file.Title, subtitle, file.FileName, url, poster, cell.DurationMs, source, pos, targets, undo);
        return card == null ? (err, null) : (null, card.Id);
    }

    /// <summary>"Reset TV Show": every episode of every season of the row's series, on every player.</summary>
    public async Task<string?> ResetSeriesAsync(string key)
    {
        var (file, url, poster) = await ResolveAsync(key);
        if (file == null) return url;
        if (!file.IsEpisode) return "That row is not a TV episode.";
        return _engine.ResetSeries(file.Title, url, poster).Error is { Length: > 0 } e ? e : null;
    }

    /// <summary>The row's file, its network path (asked from a player that holds it), and its poster.</summary>
    private async Task<(ProgressFile? File, string UrlOrError, string Poster)> ResolveAsync(string key)
    {
        if (Find(key) is not { } found) return (null, "That file is no longer in the list. Read the players again.", "");
        // A Zidoo that has the file is asked first (it has the exact network path); a Plex user's path comes from the
        // folder mapping.
        foreach (var (deviceId, fileId) in found.Holders.OrderByDescending(h => _engine.LiveOf(h.DeviceId).Online).ThenBy(h => _store.Device(h.DeviceId)?.IsPlex == true))
        {
            var d = _store.Device(deviceId);
            if (d == null || fileId <= 0) continue;
            if (d.IsPlex)
            {
                if (await _engine.PlexSourceUrlAsync(d, fileId) is not { } url) continue;
                string pp = "";
                try { pp = await _engine.PosterForAsync(d, url); } catch { }
                return (found.File, url, pp);
            }
            var entry = await new ZidooClient(d.Ip, d.Port).GetEntryAsync(fileId);
            if (entry == null || entry.Path.Length == 0) continue;
            string poster = "";
            try { poster = await _engine.PosterForAsync(d, entry.Path); } catch { }
            return (found.File, entry.Path, poster);
        }
        return (null, "None of the players that have this file is answering right now, so its location can't be looked up.", "");
    }
}

/// <summary>Sorts "Episode 2" before "Episode 10", ignoring case, like the page's own sort.</summary>
internal sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();
    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

public sealed record WatchResult(string DeviceId, string Name, bool Ok, string Error);

/// <summary>Lenient JSON readers (Home Theater sends numbers as strings now and then).</summary>
internal static class J
{
    public static bool Has(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out _);

    public static long Long(JsonElement e, string n, long def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return (long)d;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s) ? s : def;
    }

    public static int Int(JsonElement e, string n, int def = 0) => (int)Math.Clamp(Long(e, n, def), int.MinValue, int.MaxValue);

    public static string Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool Bool(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) && i != 0));
}
