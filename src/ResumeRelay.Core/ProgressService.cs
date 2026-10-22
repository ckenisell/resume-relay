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
}

public sealed class ProgressDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? ReadUtc { get; set; }
    public string Error { get; set; } = "";
    public int Titles { get; set; }
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

    public ProgressService(Store store, RelayEngine engine)
    {
        _store = store;
        _engine = engine;
        engine.FileWritten += OnFileWritten;
    }

    public bool Reading => Volatile.Read(ref _reading) == 1;

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
                if (rows == null || rows.Count == 0 || !rows.Any(r => seen.Add(r.Id))) break;
                tiles.AddRange(rows);
            }

            // 2. Open collections and series: movies and seasons are what getDetail reads.
            var work = new List<(int Id, bool Season, string Series)>();
            async Task Expand(LibraryRow r, int depth)
            {
                switch (r.Type)
                {
                    case 1: work.Add((r.Id, false, "")); break;
                    case 4: work.Add((r.Id, true, SeriesFromSeason(r.Name))); break;
                    case 2 or 3 when depth < 2:
                        var members = await api.GetCollectionAsync(r.CollectionId > 0 ? r.CollectionId : r.Id, ct);
                        if (members.Count == 0 && r.CollectionId > 0) members = await api.GetCollectionAsync(r.Id, ct);
                        if (members.Count == 0) members = await api.GetChildrenAsync(r.Id, ct);
                        foreach (var m in members)
                        {
                            if (r.Type == 3 && m.Type == 4) work.Add((m.Id, true, r.Name));
                            else if (r.Type == 3 && m.Type == 5) { work.Add((r.Id, true, r.Name)); break; } // single-season listing
                            else await Expand(m, depth + 1);
                        }
                        break;
                }
            }
            foreach (var t in tiles) await Expand(t, 0);
            work = work.DistinctBy(w => w.Id).ToList();
            result.Titles = work.Count;

            // 3. Each movie and season, four at a time.
            int done = 0;
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (w, token) =>
            {
                var read = await ReadTitleAsync(api, w.Id, w.Season, w.Series, token);
                lock (result.Cells) // four titles are read at once
                    foreach (var (file, cell) in read)
                    {
                        files.TryAdd(file.Key, file);
                        result.Cells[file.Key] = cell;
                    }
                int n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == work.Count) _status[d.Id] = $"Reading {d.Name}… {n:N0} of {work.Count:N0} titles";
            });
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
        }
    }

    /// <summary>Reads one movie or season. Public for the TV show reset, which reads a series' seasons the same way.</summary>
    public static async Task<List<(ProgressFile File, ProgressCell Cell)>> ReadTitleAsync(ZidooClient api, int id, bool season, string series, CancellationToken ct)
    {
        var found = new List<(ProgressFile, ProgressCell)>();
        using (var doc = await api.GetDetailAsync(id, ct))
            if (doc != null) Walk(doc.RootElement, new Ctx { Series = series, IsSeason = season }, found);
        if (found.Count == 0)
            using (var doc = await api.GetDetailV2Async(id, ct))
                if (doc != null) Walk(doc.RootElement, new Ctx { Series = series, IsSeason = season }, found);
        return found;
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
                Reading,
                Status = _status.Values.OrderBy(s => s).ToList(),
                Devices = _devices.Values.Select(d => new { d.DeviceId, d.Name, d.ReadUtc, d.Error, d.Titles }),
                Files = shown.Select(f => new
                {
                    f.Key, f.Display, f.IsEpisode, f.Title, f.FileName,
                    Cells = _devices.Values.Where(d => d.Cells.ContainsKey(f.Key)).ToDictionary(d => d.DeviceId, d => d.Cells[f.Key]),
                }).ToList(),
            };
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
                c.Watched = positionMs > 0 && c.DurationMs > 0 && (positionMs >= c.DurationMs * 0.93 || c.DurationMs - positionMs <= 5 * 60_000);
            }
        }
    }

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
