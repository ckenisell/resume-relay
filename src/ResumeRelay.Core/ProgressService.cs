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
        var devices = _store.Devices().Where(d => deviceIds.Contains(d.Id)).ToList();
        if (devices.Count == 0) return "Tick at least one player to read.";
        if (Interlocked.Exchange(ref _reading, 1) == 1) return "Already reading the players.";
        _status.Clear();
        foreach (var d in devices) _status[d.Id] = $"Waiting to read {d.Name}…";
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(devices.Select(d => ReadDeviceAsync(d, stopping))); }
            finally { _status.Clear(); Interlocked.Exchange(ref _reading, 0); }
        }, stopping);
        return null;
    }

    private async Task ReadDeviceAsync(Device d, CancellationToken ct)
    {
        var result = new ProgressDevice { DeviceId = d.Id, Name = d.Name };
        var files = new ConcurrentDictionary<string, ProgressFile>(StringComparer.Ordinal);
        try
        {
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
                    await Task.Delay(1500 * retry, ct);
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
                        for (int attempt = 0; attempt < 3 && members.Count == 0; attempt++) // an empty answer may just be a busy player
                        {
                            if (attempt > 0) await Task.Delay(1500 * attempt, ct);
                            foreach (int cid in tries.Where(x => x > 0).Distinct())
                            {
                                members = await api.GetCollectionAsync(cid, ct);
                                // A collection must hold movies (or other collections); a series must hold seasons or episodes.
                                members = members.Where(m => r.Type == 3 ? m.Type is 4 or 5 : m.Type is 0 or 1 or 2 or 3 or 4 or 6).ToList();
                                if (members.Count > 0) { via = $"getCollection?id={cid}"; break; }
                            }
                            if (members.Count == 0) { members = await api.GetChildrenAsync(r.Id, ct); via = $"getEntry children of {r.Id}"; }
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

            // 3. Each movie and season, three at a time. A title the player didn't answer for is not an empty title:
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
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (w, token) =>
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
                await Task.Delay(2000 * round, ct);
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
                Key = uri.Replace('\\', '/').ToLowerInvariant(),
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
    public object WatchSnapshot(string show, IReadOnlyCollection<string> players, string search, string sort, int page, int pageSize)
    {
        lock (_lock)
        {
            var devs = _devices.Values.Where(d => d.ReadUtc != null && (players.Count == 0 || players.Contains(d.DeviceId))).ToList();
            var q = search.Trim();
            var rows = _files.Values.Where(f =>
            {
                if (q.Length > 0 && !f.Display.Contains(q, StringComparison.OrdinalIgnoreCase) && !f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
                var cells = devs.Select(d => d.Cells.GetValueOrDefault(f.Key)).Where(c => c != null).ToList();
                if (cells.Count == 0) return false;
                int w = cells.Count(c => c!.Watched);
                return show switch
                {
                    "missing" => cells.Count < devs.Count,
                    "watched" => w > 0,
                    "unwatched" => w == 0,
                    "all" => true,
                    _ => w > 0 && w < cells.Count,
                };
            }).ToList();
            long LastWatched(ProgressFile f) => devs.Select(d => d.Cells.GetValueOrDefault(f.Key)?.LastWatchMs ?? -1).DefaultIfEmpty(-1).Max();
            var ordered = sort == "watched"
                ? rows.OrderByDescending(LastWatched).ThenBy(f => f.Display, NaturalOrder.Instance)
                : rows.OrderBy(f => f.Display, NaturalOrder.Instance);
            pageSize = Math.Clamp(pageSize, 1, 2000);
            int pages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);
            return new
            {
                Version,
                Reading,
                Status = _status.Values.OrderBy(s => s).ToList(),
                Devices = _devices.Values.Select(d => new { d.DeviceId, d.Name, d.ReadUtc, d.Error, d.Titles, Unread = d.Unread.Count }),
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

            var api = new ZidooClient(d.Ip, d.Port);
            async Task<ProgressCell?> ReadBack()
            {
                var now = await ReadTitleAsync(api, cell.TitleId, cell.FromSeason, file.Title, default);
                return now.FirstOrDefault(x => x.File.Key == key).Cell;
            }
            ProgressCell? after;
            if (watched)
            {
                await api.MarkAsWatchedAsync(cell.FileId);
                after = await ReadBack();
                if (after?.Watched != true && cell.DurationMs > 0)
                {
                    await api.SetPlayPointAsync(cell.FileId, cell.DurationMs, cell.DurationMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    after = await ReadBack();
                }
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

    private void OnFileWritten(string deviceId, int fileId, int positionMs, long lastWatchMs)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var d)) return;
            foreach (var c in d.Cells.Values.Where(c => c.FileId == fileId))
            {
                c.PlayPointMs = positionMs;
                c.LastWatchMs = lastWatchMs;
                c.Watched = positionMs > 0 && NearEnd(positionMs, c.DurationMs);
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
        foreach (var (deviceId, fileId) in found.Holders.OrderByDescending(h => _engine.LiveOf(h.DeviceId).Online))
        {
            var d = _store.Device(deviceId);
            if (d == null || fileId <= 0) continue;
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
