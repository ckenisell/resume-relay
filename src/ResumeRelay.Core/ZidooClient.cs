using System.Text.Json;

namespace ResumeRelay;

public sealed record ModelInfo(string Model, string Firmware, string NetMac, string WifiMac, string Uuid, string Ip)
{
    /// <summary>The identity a device is stored under. Wired MAC first because it never changes.</summary>
    public string Identity =>
        Mac(NetMac) ?? Mac(WifiMac) ?? (Uuid.Length > 0 ? "uuid:" + Uuid.ToLowerInvariant() : "");

    public string BestMac => Mac(NetMac) ?? Mac(WifiMac) ?? "";

    private static string? Mac(string m)
    {
        string s = (m ?? "").Trim().ToLowerInvariant().Replace('-', ':');
        return s.Length >= 11 && s != "00:00:00:00:00:00" && s != "02:00:00:00:00:00" ? s : null;
    }
}

/// <summary>What getPlayStatus reported.</summary>
public enum PlayState { Unreachable, Idle, Playing }

public sealed record PlayStatus(PlayState State, string Path, string Title, int PositionMs, int DurationMs, bool Paused);

/// <summary>Result of getIdByPathname. Id is the FILE's entry id (the response's fileId), which getEntry and setPlayPoint take.</summary>
public sealed record PathHit(int Id, int VideoInfoId, bool Matched);

/// <summary>Result of getEntry for a single file.</summary>
public sealed record EntryInfo(int Id, int ParentId, int VideoInfoId, string Name, string Path, int PlayPointMs);

public enum Lookup { Found, NotFound, Failed }

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
            return (Lookup.Found, new PathHit(fileEntry, Int(r, "videoInfoId"), matched));
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
            return new EntryInfo(id, Int(r, "parentId"), Int(r, "videoInfoId"), Str(r, "name"), Str(r, "path"), Int(r, "playPoint"));
        }
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
