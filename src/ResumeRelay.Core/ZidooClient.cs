using System.Text.Json;

namespace ResumeRelay;

public sealed record ModelInfo(string Model, string Firmware, string NetMac, string WifiMac, string Uuid, string Ip)
{
    /// <summary>The identity a device is stored under. Wired MAC first because it never changes.</summary>
    public string Identity =>
        Mac(NetMac) ?? Mac(WifiMac) ?? (Uuid.Length > 0 ? "uuid:" + Uuid.ToLowerInvariant() : "");

    public string BestMac => Mac(NetMac) ?? Mac(WifiMac) ?? "";
    public string WiredMac => Mac(NetMac) ?? "";
    public string WirelessMac => Mac(WifiMac) ?? "";

    private static string? Mac(string m)
    {
        string s = (m ?? "").Trim().ToLowerInvariant().Replace('-', ':');
        return s.Length >= 11 && s != "00:00:00:00:00:00" && s != "02:00:00:00:00:00" ? s : null;
    }
}

/// <summary>What getPlayStatus reported.</summary>
public enum PlayState { Unreachable, Idle, Playing }

public sealed record PlayStatus(PlayState State, string Path, string Title, int PositionMs, int DurationMs, bool Paused);

/// <summary>Result of getIdByPathname. Id is the FILE's entry id (the response's fileId), which getEntry and setPlayPoint
/// take. TitleId is the response's "id": the film (or the file itself when unmatched).</summary>
public sealed record PathHit(int Id, int VideoInfoId, bool Matched, int TitleId);

/// <summary>Result of getEntry. Type: 0 file, 1 film, 3 series, 4 season (see the HT5 API guide).</summary>
public sealed record EntryInfo(int Id, int ParentId, int VideoInfoId, string Name, string Path, int PlayPointMs, int Type = 0, int Season = 0, int Episode = 0);

public enum Lookup { Found, NotFound, Failed }

/// <summary>A row of a library listing. Type: 0 file, 1 movie, 2 collection, 3 series, 4 season, 5 episode.</summary>
public sealed record LibraryRow(int Id, int Type, string Name, int CollectionId, int Year);

/// <summary>
/// Thin wrapper over the Zidoo Home Theater HTTP API (port 9529). Every call is a plain GET.
/// Calls never throw for network problems; they report Unreachable/Failed instead.
/// </summary>
public sealed class ZidooClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _base;

    public ZidooClient(string ip, int port = 9529) => _base = $"http://{ip}:{port}";

    // ---------- device ----------

    public async Task<ModelInfo?> GetModelAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooControlCenter/getModel", timeout, ct);
        if (doc == null) return null;
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string model = Str(r, "model");
            if (model.Length == 0 && Str(r, "net_mac").Length == 0) return null;
            return new ModelInfo(model, Str(r, "firmware"), Str(r, "net_mac"), Str(r, "wif_mac"), Str(r, "duuid"), Str(r, "ip"));
        }
    }

    // ---------- playback ----------

    public async Task<PlayStatus> GetPlayStatusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooVideoPlay/getPlayStatus", timeout, ct);
        if (doc == null) return new PlayStatus(PlayState.Unreachable, "", "", 0, 0, false);
        using (doc)
        {
            var r = doc.RootElement;
            // 806 "The resource does not exist" = no video playing. Anything without a video block is treated the same.
            if (Int(r, "status") != 200 || !r.TryGetProperty("video", out var v) || v.ValueKind != JsonValueKind.Object)
                return new PlayStatus(PlayState.Idle, "", "", 0, 0, false);

            string path = Str(v, "path");
            if (path.Length == 0) return new PlayStatus(PlayState.Idle, "", "", 0, 0, false);
            // video.status: 1 = playing, 0 = paused (observed on HT 5.1.x).
            return new PlayStatus(PlayState.Playing, path, Str(v, "title"), Int(v, "currentPosition"), Int(v, "duration"), Int(v, "status") != 1);
        }
    }

    // ---------- library lookups ----------

    public async Task<(Lookup Result, PathHit? Hit)> GetIdByPathnameAsync(string sourceUrl, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooPoster/v2/getIdByPathname?path=" + Uri.EscapeDataString(sourceUrl), TimeSpan.FromSeconds(8), ct);
        if (doc == null) return (Lookup.Failed, null);
        using (doc)
        {
            var r = doc.RootElement;
            int status = Int(r, "status");
            if (status == 404 || Str(r, "code").Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                return (Lookup.NotFound, null);
            if (status != 200) return (Lookup.Failed, null);
            bool matched = r.TryGetProperty("matched", out var m) && m.ValueKind == JsonValueKind.True;
            // "id" is the TITLE (e.g. Braveheart = 1310) when the file is matched; the file's own entry is "fileId"
            // (24004). For an unmatched file, "id" is the file entry itself. Bookmarks live on the file entry.
            int fileEntry = Int(r, "fileId");
            if (fileEntry <= 0 && !matched) fileEntry = Int(r, "id");
            if (fileEntry <= 0) return (Lookup.Failed, null);
            return (Lookup.Found, new PathHit(fileEntry, Int(r, "videoInfoId"), matched, Int(r, "id")));
        }
    }

    public async Task<EntryInfo?> GetEntryAsync(int id, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/ZidooPoster/v2/getEntry?id={id}&start=0&count=1", TimeSpan.FromSeconds(8), ct);
        if (doc == null) return null;
        using (doc)
        {
            var r = doc.RootElement;
            if (Int(r, "status") != 200 || Int(r, "id") != id) return null;
            return new EntryInfo(id, Int(r, "parentId"), Int(r, "videoInfoId"), Str(r, "name"), Str(r, "path"), Int(r, "playPoint"),
                Int(r, "type"), Int(r, "seasonNumber"), Int(r, "episodeNumber"));
        }
    }

    // ---------- library reads for the Progress Reset tab ----------
    // Shapes per the HT5 API guide (z-keeper.com/learn/ht5-api-guide). Every call answers 804 while Home Theater
    // is busy; those are retried after a pause, as the guide advises.

    /// <summary>A raw JSON answer (caller disposes), retrying "busy" (804) answers a few times.</summary>
    public async Task<JsonDocument?> GetLibraryJsonAsync(string pathAndQuery, CancellationToken ct = default)
    {
        int[] waits = { 600, 1500, 3000 };
        for (int attempt = 0; ; attempt++)
        {
            var doc = await GetJsonAsync(pathAndQuery, TimeSpan.FromSeconds(20), ct);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object || Int(doc.RootElement, "status") != 804 || attempt >= waits.Length)
                return doc;
            doc.Dispose();
            await Task.Delay(waits[attempt], ct);
        }
    }

    /// <summary>One page of the poster wall's top-level tiles (movies, collections, series, single-season shows).</summary>
    public async Task<List<LibraryRow>?> GetVideoListPageAsync(int page, int pageSize, CancellationToken ct = default)
    {
        using var doc = await GetLibraryJsonAsync($"/ZidooPoster/getVideoList?page={page}&pagesize={pageSize}&type=0", ct);
        if (doc == null || Int(doc.RootElement, "status") != 200) return null;
        return Rows(doc.RootElement, "data");
    }

    /// <summary>The members of a collection, or a series' seasons.</summary>
    public async Task<List<LibraryRow>> GetCollectionAsync(int id, CancellationToken ct = default)
    {
        using var doc = await GetLibraryJsonAsync($"/ZidooPoster/getCollection?id={id}", ct);
        return doc == null ? new() : Rows(doc.RootElement, "data");
    }

    /// <summary>An entry's direct children (a series' seasons, a season's episodes...). HT 5.1.03+.</summary>
    public async Task<List<LibraryRow>> GetChildrenAsync(int id, CancellationToken ct = default)
    {
        using var doc = await GetLibraryJsonAsync($"/ZidooPoster/v2/getEntry?id={id}&start=0&count=500", ct);
        return doc == null ? new() : Rows(doc.RootElement, "children");
    }

    /// <summary>A movie's or season's detail: every file with its resume point and last-watched time.</summary>
    public Task<JsonDocument?> GetDetailAsync(int id, CancellationToken ct = default) =>
        GetLibraryJsonAsync($"/ZidooPoster/getDetail?id={id}", ct);

    public Task<JsonDocument?> GetDetailV2Async(int id, CancellationToken ct = default) =>
        GetLibraryJsonAsync($"/Poster/v2/getDetail?id={id}", ct);

    private static List<LibraryRow> Rows(JsonElement root, string name)
    {
        var list = new List<LibraryRow>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            int id = Int(e, "id");
            if (id <= 0) continue;
            list.Add(new LibraryRow(id, Int(e, "type"), Str(e, "name"), Int(e, "collectionId"), Int(e, "year")));
        }
        return list;
    }

    /// <summary>A poster image, or null. Tries the same endpoints Z-Keeper uses, newest API first.</summary>
    public async Task<byte[]?> GetPosterAsync(int id, int width = 240, int height = 360, CancellationToken ct = default)
    {
        foreach (var path in new[]
                 {
                     $"/ZidooPoster/v2/getPoster?id={id}&w={width}&h={height}",
                     $"/ZidooPoster/getFile/getPoster?id={id}&w={width}&h={height}",
                 })
        {
            var bytes = await GetBytesAsync(path, TimeSpan.FromSeconds(10), ct);
            if (bytes != null && IsImage(bytes)) return bytes;
        }
        return null;
    }

    /// <summary>True when the Home Theater app's library answers (the check Z-Keeper uses). The player itself can be
    /// on and answering other calls while this is false: the app isn't running yet.</summary>
    public async Task<bool> IsHomeTheaterRunningAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooPoster/getVideoList?page=1&pagesize=1&type=0", TimeSpan.FromSeconds(5), ct);
        if (doc == null) return false;
        using (doc) return Int(doc.RootElement, "status") == 200;
    }

    /// <summary>
    /// The installed Home Theater app's version, from the player's list of installed apps (package com.zidoo.poster),
    /// e.g. ("5.1.11", 5111). This list comes from the player itself, so it answers even while Home Theater is closed.
    /// </summary>
    public async Task<(string Name, int Code)?> GetHomeTheaterVersionAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooControlCenter/Apps/getApps", TimeSpan.FromSeconds(8), ct);
        if (doc == null) return null;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return null;
            foreach (var app in apps.EnumerateArray())
            {
                if (!string.Equals(Str(app, "packageName"), "com.zidoo.poster", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Str(app, "versionName").Trim().TrimStart('v', 'V');
                int code = int.TryParse(Str(app, "versionCode"), out int c) ? c : 0;
                return name.Length > 0 || code > 0 ? (name, code) : null;
            }
            return null;
        }
    }

    /// <summary>Opens the Home Theater app on the player (com.zidoo.poster).</summary>
    public async Task<bool> LaunchHomeTheaterAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooControlCenter/Apps/openApp?packageName=com.zidoo.poster", TimeSpan.FromSeconds(8), ct);
        if (doc == null) return false;
        using (doc) return Int(doc.RootElement, "status") == 200;
    }

    /// <summary>Presses the remote's power button for you: the player goes to standby (Z-Keeper's power-off call).</summary>
    /// The reply is not relied on: a player going to standby may drop the connection before answering.
    /// Success is judged by the player going quiet afterwards.
    public async Task PowerOffAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/ZidooControlCenter/RemoteControl/sendkey?key=Key.PowerOn.Poweroff", TimeSpan.FromSeconds(8), ct);
        doc?.Dispose();
    }

    /// <summary>
    /// The per-file last-watched time lives only in getDetail of the title (or season) that holds the file,
    /// inside the file's inner record (id = videoInfoId). Walks up at most three parents to find it.
    /// Returns -1 when the file has never been watched, null when it could not be determined.
    /// </summary>
    public async Task<long?> GetLastWatchTimeAsync(EntryInfo file, CancellationToken ct = default)
    {
        int parent = file.ParentId;
        for (int level = 0; level < 3 && parent > 1; level++)
        {
            foreach (var path in new[] { $"/ZidooPoster/getDetail?id={parent}", $"/Poster/v2/getDetail?id={parent}" })
            {
                var doc = await GetJsonAsync(path, TimeSpan.FromSeconds(10), ct);
                if (doc == null) continue;
                using (doc)
                {
                    long? t = FindLastWatchTime(doc.RootElement, file.Id, file.VideoInfoId, 0);
                    if (t.HasValue) return t;
                }
            }
            var up = await GetEntryAsync(parent, ct);
            if (up == null) break;
            parent = up.ParentId;
        }
        return null;
    }

    private static long? FindLastWatchTime(JsonElement e, int entryId, int videoInfoId, int depth)
    {
        if (depth > 8) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            // The outer file row (id = entry id) carries the inner record under "aggregation".
            if (Int(e, "id") == entryId && e.TryGetProperty("aggregation", out var agg) && agg.ValueKind == JsonValueKind.Object
                && agg.TryGetProperty("lastWatchTime", out var lw1) && lw1.TryGetInt64(out long t1))
                return t1;
            // Or we may land directly on the inner record (id = videoInfoId).
            if (videoInfoId > 0 && Int(e, "id") == videoInfoId && e.TryGetProperty("lastWatchTime", out var lw2) && lw2.TryGetInt64(out long t2))
                return t2;
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var t = FindLastWatchTime(p.Value, entryId, videoInfoId, depth + 1);
                    if (t.HasValue) return t;
                }
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var t = FindLastWatchTime(item, entryId, videoInfoId, depth + 1);
                if (t.HasValue) return t;
            }
        }
        return null;
    }

    // ---------- writes ----------

    /// <summary>Sets a file's resume point. id is the entry id from getIdByPathname/getEntry (NOT videoInfoId).
    /// The reply is not trusted: HT 5.1.11 on 1619B reports errors for writes that worked, and a wrong id can
    /// report success. Callers must read back with GetEntryAsync.</summary>
    public async Task<bool> SetPlayPointAsync(int id, int playPointMs, int durationMs, long lastWatchTimeMs, CancellationToken ct = default)
    {
        if (id <= 0 || playPointMs <= 0) return false; // playPoint=0 would wipe the bookmark and watched flag
        string url = $"/ZidooPoster/v2/setPlayPoint?id={id}&playPoint={playPointMs}";
        if (durationMs > 0) url += $"&duration={durationMs}";
        if (lastWatchTimeMs > 0) url += $"&lastWatchTime={lastWatchTimeMs}";
        var doc = await GetJsonAsync(url, TimeSpan.FromSeconds(8), ct);
        if (doc == null) return false;
        using (doc) return Int(doc.RootElement, "status") == 200;
    }

    /// <summary>
    /// Clears a file's progress: playPoint=0 erases the bookmark and the watched mark, as if it had never been
    /// played. Only used on purpose, for a stop at 0:00 (SetPlayPointAsync refuses 0 so it can't happen by accident).
    /// </summary>
    public async Task<bool> ResetPlayPointAsync(int id, CancellationToken ct = default)
    {
        if (id <= 0) return false;
        var doc = await GetJsonAsync($"/ZidooPoster/v2/setPlayPoint?id={id}&playPoint=0", TimeSpan.FromSeconds(8), ct);
        if (doc == null) return false;
        using (doc) return Int(doc.RootElement, "status") == 200;
    }

    /// <summary>
    /// Marks one file watched (HT5 API guide: call once per file; it records "now" as last watched and parks the resume
    /// point as a negative value). The reply isn't trusted; callers read the title back. Never sent with watched=0:
    /// on some players that closes the Home Theater app.
    /// </summary>
    public async Task MarkAsWatchedAsync(int fileId, CancellationToken ct = default)
    {
        if (fileId <= 0) return;
        using var doc = await GetJsonAsync($"/Poster/v2/markAsWatched?aggregationId={fileId}&watched=1", TimeSpan.FromSeconds(8), ct);
    }

    // ---------- plumbing ----------

    private async Task<JsonDocument?> GetJsonAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await Http.GetAsync(_base + pathAndQuery, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            int start = body.IndexOfAny(new[] { '{', '[' }); // tolerate JSONP wrappers some HT builds add
            if (start < 0) return null;
            int end = body.LastIndexOfAny(new[] { '}', ']' });
            return JsonDocument.Parse(body.AsMemory(start, end - start + 1));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<byte[]?> GetBytesAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await Http.GetAsync(_base + pathAndQuery, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length is > 0 and < 5_000_000 ? bytes : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>JPEG, PNG or WebP by their first bytes (an error comes back as JSON text instead).</summary>
    public static string? ImageExtension(byte[] b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg" :
        b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png" :
        b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[8] == (byte)'W' && b[9] == (byte)'E' ? ".webp" : null;

    private static bool IsImage(byte[] b) => ImageExtension(b) != null;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : "")
            : "";

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out int i)) return i;
            if (v.TryGetInt64(out long l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            if (v.TryGetDouble(out double d)) return (int)d;
        }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        return 0;
    }
}
