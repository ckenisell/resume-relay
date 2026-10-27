using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>The Plex sign-in and server, kept in plex.json. Each Plex user that syncs is a Device of Kind "plex".</summary>
public sealed class PlexConfig
{
    /// <summary>Identifies this Resume Relay to plex.tv (it shows under Authorized Devices in Plex).</summary>
    public string ClientId { get; set; } = "";
    public string AccountToken { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>A sign-in in progress (the code shown on the Plex card).</summary>
    public int PinId { get; set; }
    public string PinCode { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    /// <summary>The owner's access token for the server: what reads the server's sessions (what everyone is playing).</summary>
    public string ServerToken { get; set; } = "";
    public List<PlexMapping> Mappings { get; set; } = new();

    public bool SignedIn => AccountToken.Length > 0;
    public bool Ready => ServerUrl.Length > 0 && ServerToken.Length > 0;
}

/// <summary>"Plex sees /shares/Entertainment where the Zidoos see smb://10.0.0.56/Entertainment".</summary>
public sealed class PlexMapping
{
    public string PlexFolder { get; set; } = "";
    public string ZidooFolder { get; set; } = "";
}

/// <summary>
/// Turning Plex's file paths into the Zidoos' network paths and back. Files are matched by their path inside the
/// share ("/Movies/Arrival (2016)/Arrival (2016).mkv"), the same key the Zidoo libraries use.
/// </summary>
public static class PlexPaths
{
    private static readonly Regex ShareUrl = new(@"^(smb|nfs)://[^/]+/[^/]+(?<rel>/.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Norm(string? p) => (p ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    private static bool Under(string path, string folder, out string rest)
    {
        rest = "";
        if (folder.Length == 0 || !path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return false;
        rest = path[folder.Length..];
        return true;
    }

    /// <summary>The Zidoos' path for a file Plex has, or null when no folder mapping covers it.</summary>
    public static string? ToSourceUrl(string plexPath, IEnumerable<PlexMapping> mappings)
    {
        string p = Norm(plexPath);
        foreach (var m in mappings)
            if (Under(p, Norm(m.PlexFolder), out var rest)) return Norm(m.ZidooFolder) + rest;
        if (ShareUrl.IsMatch(p)) return p; // Plex reading the share directly by its smb:// address
        return null;
    }

    /// <summary>The path inside the share, from either side's spelling ("/Movies/…"). Null when unknown.</summary>
    public static string? Rel(string path, IEnumerable<PlexMapping> mappings)
    {
        string p = Norm(path);
        foreach (var m in mappings)
        {
            if (Under(p, Norm(m.PlexFolder), out var a)) return RelOfUrl(Norm(m.ZidooFolder) + a);
        }
        return RelOfUrl(p);
    }

    /// <summary>"smb://10.0.0.56/Entertainment/Movies/x.mkv" → "/Movies/x.mkv".</summary>
    public static string? RelOfUrl(string url)
    {
        string u = Norm(url);
        try { u = Uri.UnescapeDataString(u); } catch { }
        var m = ShareUrl.Match(u);
        return m.Success ? m.Groups["rel"].Value : null;
    }

    /// <summary>The matching key: the in-share path, lower-case (the same as a Zidoo file's key on Progress Reset).</summary>
    public static string Key(string rel) => rel.Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Suggests a folder mapping from one file both sides know: the same file name at the end of a Plex path and a
    /// Zidoo path. Whatever the two paths share at the end is the same folder; what comes before it differs.
    /// </summary>
    public static PlexMapping? Suggest(string plexPath, string zidooUrl)
    {
        var a = Norm(plexPath).Split('/');
        var b = Norm(zidooUrl).Split('/');
        int i = a.Length - 1, j = b.Length - 1, same = 0;
        while (i >= 0 && j >= 0 && string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase)) { i--; j--; same++; }
        if (same < 2 || i < 0 || j < 2) return null;
        // Keep the share itself in the Zidoo folder ("smb:" "" "10.0.0.56" "Entertainment" → 4 parts at least).
        while (j < 3 && same > 1) { i++; j++; same--; }
        if (j < 3 || i < 0) return null;
        return new PlexMapping { PlexFolder = string.Join('/', a[..(i + 1)]), ZidooFolder = string.Join('/', b[..(j + 1)]) };
    }
}

/// <summary>A movie or episode as the Plex server reports it, for one user.</summary>
public sealed record PlexItem(
    int Key, string Type, string Title, string Show, int Season, int Episode, int Year,
    int ViewOffsetMs, int ViewCount, long LastViewedAt, int DurationMs, List<string> Files,
    string Thumb, string ParentThumb, string GrandparentThumb, int ShowKey)
{
    public bool IsEpisode => Type == "episode";
    public bool Watched => ViewCount > 0;
    public string PosterPath => IsEpisode ? (ParentThumb.Length > 0 ? ParentThumb : GrandparentThumb.Length > 0 ? GrandparentThumb : Thumb) : Thumb;
}

/// <summary>Something playing on the server right now, and for whom.</summary>
public sealed record PlexSession(PlexItem Item, string State, string UserId, string UserTitle);

public sealed record PlexSection(string Key, string Type, string Title, List<string> Locations);

/// <summary>A Plex Media Server, with one user's token. Calls never throw for network problems; they return null.</summary>
public sealed class PlexClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private const string Library = "com.plexapp.plugins.library";
    private readonly string _base, _token, _clientId;

    public PlexClient(string baseUrl, string token, string clientId)
    {
        _base = baseUrl.TrimEnd('/');
        _token = token;
        _clientId = clientId;
    }

    private async Task<JsonDocument?> GetAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct, HttpMethod? method = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, _base + pathAndQuery);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_token.Length > 0) req.Headers.Add("X-Plex-Token", _token);
            req.Headers.Add("X-Plex-Client-Identifier", _clientId);
            req.Headers.Add("X-Plex-Product", "Resume Relay");
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            string text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (text.Trim().Length == 0) return JsonDocument.Parse("{}");
            return JsonDocument.Parse(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(90);

    private static JsonElement Container(JsonDocument d) =>
        d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("MediaContainer", out var mc) ? mc : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>The server's machine id, or null when it doesn't answer.</summary>
    public async Task<string?> IdentityAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var d = await GetAsync("/identity", timeout, ct);
        if (d == null) return null;
        string id = J.Str(Container(d), "machineIdentifier");
        return id.Length > 0 ? id : null;
    }

    public static PlexItem ParseItem(JsonElement m)
    {
        var files = Arr(m, "Media").SelectMany(x => Arr(x, "Part")).Select(p => J.Str(p, "file")).Where(f => f.Length > 0).ToList();
        return new PlexItem(
            Key: J.Int(m, "ratingKey"),
            Type: J.Str(m, "type"),
            Title: J.Str(m, "title"),
            Show: J.Str(m, "grandparentTitle"),
            Season: J.Int(m, "parentIndex"),
            Episode: J.Int(m, "index"),
            Year: J.Int(m, "year"),
            ViewOffsetMs: J.Int(m, "viewOffset"),
            ViewCount: J.Int(m, "viewCount"),
            LastViewedAt: J.Long(m, "lastViewedAt"),
            DurationMs: J.Int(m, "duration"),
            Files: files,
            Thumb: J.Str(m, "thumb"),
            ParentThumb: J.Str(m, "parentThumb"),
            GrandparentThumb: J.Str(m, "grandparentThumb"),
            ShowKey: J.Int(m, "grandparentRatingKey"));
    }

    /// <summary>What is playing on the server (needs the owner's token). Null when the server doesn't answer.</summary>
    public async Task<List<PlexSession>?> SessionsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var d = await GetAsync("/status/sessions", timeout, ct);
        if (d == null) return null;
        return Arr(Container(d), "Metadata").Select(m =>
        {
            var user = m.TryGetProperty("User", out var u) ? u : default;
            var player = m.TryGetProperty("Player", out var pl) ? pl : default;
            string uid = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var idv)
                ? (idv.ValueKind == JsonValueKind.Number ? idv.GetRawText() : idv.GetString() ?? "") : "";
            return new PlexSession(ParseItem(m), J.Str(player, "state"), uid, J.Str(user, "title"));
        }).ToList();
    }

    /// <summary>The server's raw answer for what's playing, for the troubleshooting page (null when it doesn't answer).</summary>
    public async Task<string?> SessionsTextAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/status/sessions", Normal, ct);
        return d == null ? null : JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<List<PlexSection>?> SectionsAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/library/sections", Normal, ct);
        if (d == null) return null;
        return Arr(Container(d), "Directory")
            .Select(x => new PlexSection(J.Str(x, "key"), J.Str(x, "type"), J.Str(x, "title"), Arr(x, "Location").Select(l => J.Str(l, "path")).ToList()))
            .ToList();
    }

    /// <summary>Every movie (type 1) or episode (type 4) of a library, with this user's progress.</summary>
    public async Task<List<PlexItem>?> SectionItemsAsync(string sectionKey, int type, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={type}", Long, ct);
        if (d == null) return null;
        return Arr(Container(d), "Metadata").Select(ParseItem).ToList();
    }

    /// <summary>Every movie and episode on the server, with this user's progress. Null when the server doesn't answer.</summary>
    public async Task<List<PlexItem>?> AllItemsAsync(CancellationToken ct = default)
    {
        var sections = await SectionsAsync(ct);
        if (sections == null) return null;
        var all = new List<PlexItem>();
        foreach (var s in sections.Where(s => s.Type is "movie" or "show"))
        {
            var items = await SectionItemsAsync(s.Key, s.Type == "movie" ? 1 : 4, ct);
            if (items == null) return null;
            all.AddRange(items);
        }
        return all;
    }

    public async Task<PlexItem?> ItemAsync(int key, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/library/metadata/{key}", Normal, ct);
        if (d == null) return null;
        var m = Arr(Container(d), "Metadata").FirstOrDefault();
        return m.ValueKind == JsonValueKind.Object ? ParseItem(m) : null;
    }

    public async Task<List<PlexItem>?> ShowEpisodesAsync(int showKey, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/library/metadata/{showKey}/allLeaves", Long, ct);
        if (d == null) return null;
        return Arr(Container(d), "Metadata").Select(ParseItem).ToList();
    }

    /// <summary>Sets this user's resume point, as a Plex app does when you stop.</summary>
    public async Task<bool> SetProgressAsync(int key, int positionMs, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/:/progress?key={key}&identifier={Library}&time={positionMs}&state=stopped", Normal, ct);
        return d != null;
    }

    /// <summary>"Mark as played": watched, and the resume point is cleared.</summary>
    public async Task<bool> ScrobbleAsync(int key, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/:/scrobble?key={key}&identifier={Library}", Normal, ct);
        return d != null;
    }

    /// <summary>"Mark as unplayed": not watched, and the resume point is cleared. On a show it covers every episode.</summary>
    public async Task<bool> UnscrobbleAsync(int key, CancellationToken ct = default)
    {
        using var d = await GetAsync($"/:/unscrobble?key={key}&identifier={Library}", Normal, ct);
        return d != null;
    }

    /// <summary>A poster, resized by the server.</summary>
    public async Task<byte[]?> ImageAsync(string thumbPath, CancellationToken ct = default)
    {
        if (thumbPath.Length == 0) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string url = $"{_base}/photo/:/transcode?width=240&height=360&minSize=1&upscale=1&url={Uri.EscapeDataString(thumbPath)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Plex-Token", _token);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}

public sealed record PlexAccount(string Id, string Uuid, string Username, string Title);
public sealed record PlexConnection(string Uri, bool Local, bool Relay);
public sealed record PlexResource(string Name, string ClientId, string AccessToken, bool Owned, List<PlexConnection> Connections);
public sealed record PlexHomeUser(string Id, string Uuid, string Title, bool Admin, bool Protected);

/// <summary>plex.tv: sign-in with a 4-character code (plex.tv/link), the account's servers and its Plex Home users.</summary>
public sealed class PlexTv
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>https://plex.tv, unless RESUMERELAY_PLEXTV says otherwise (the tests use a stand-in).</summary>
    public static string Base => Environment.GetEnvironmentVariable("RESUMERELAY_PLEXTV") is { Length: > 0 } b ? b.TrimEnd('/') : "https://plex.tv";

    private readonly string _clientId;
    public PlexTv(string clientId) => _clientId = clientId;

    /// <summary>One retry when the request didn't get through at all (a dropped connection is not an answer).</summary>
    private async Task<(int Status, JsonDocument? Doc)> SendAsync(HttpMethod method, string path, string token, CancellationToken ct)
    {
        var r = await SendOnceAsync(method, path, token, ct);
        if (r.Status != 0) return r;
        await Task.Delay(800, ct);
        return await SendOnceAsync(method, path, token, ct);
    }

    private async Task<(int Status, JsonDocument? Doc)> SendOnceAsync(HttpMethod method, string path, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, Base + path);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Add("X-Plex-Product", "Resume Relay");
            req.Headers.Add("X-Plex-Version", RelayHost.Version);
            req.Headers.Add("X-Plex-Client-Identifier", _clientId);
            req.Headers.Add("X-Plex-Device-Name", "Resume Relay");
            req.Headers.Add("X-Plex-Platform", OperatingSystem.IsWindows() ? "Windows" : "Linux");
            if (token.Length > 0) req.Headers.Add("X-Plex-Token", token);
            if (method == HttpMethod.Post) req.Content = new StringContent("");
            using var resp = await Http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            try { if (text.Trim().Length > 0) doc = JsonDocument.Parse(text); } catch { }
            return ((int)resp.StatusCode, doc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (0, null); }
    }

    private static string IdText(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "") : "";

    /// <summary>Starts a sign-in: a short code the user enters at plex.tv/link.</summary>
    public async Task<(int Id, string Code)?> CreatePinAsync(CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Post, "/api/v2/pins", "", ct);
        using (doc)
        {
            if (status is < 200 or >= 300 || doc == null) return null;
            int id = J.Int(doc.RootElement, "id");
            string code = J.Str(doc.RootElement, "code");
            return id > 0 && code.Length > 0 ? (id, code) : null;
        }
    }

    /// <summary>The account token once the code has been entered; "" while waiting; null if the code expired.</summary>
    public async Task<string?> CheckPinAsync(int id, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, $"/api/v2/pins/{id}", "", ct);
        using (doc)
        {
            if (status == 404) return null;
            if (doc == null) return "";
            return J.Str(doc.RootElement, "authToken");
        }
    }

    public async Task<PlexAccount?> AccountAsync(string token, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, "/api/v2/user", token, ct);
        using (doc)
        {
            if (status is < 200 or >= 300 || doc == null) return null;
            var r = doc.RootElement;
            return new PlexAccount(IdText(r, "id"), J.Str(r, "uuid"), J.Str(r, "username"), J.Str(r, "title"));
        }
    }

    /// <summary>The Plex servers this account can use, each with this account's token for it and its addresses.</summary>
    public async Task<List<PlexResource>?> ServersAsync(string token, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, "/api/v2/resources?includeHttps=1&includeRelay=1", token, ct);
        using (doc)
        {
            if (status is < 200 or >= 300 || doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray()
                .Where(r => J.Str(r, "provides").Split(',').Contains("server"))
                .Select(r => new PlexResource(J.Str(r, "name"), J.Str(r, "clientIdentifier"), J.Str(r, "accessToken"), J.Bool(r, "owned"),
                    (r.TryGetProperty("connections", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .Select(c => new PlexConnection(J.Str(c, "uri"), J.Bool(c, "local"), J.Bool(c, "relay"))).ToList()))
                .ToList();
        }
    }

    /// <summary>The users of the account's Plex Home (just the account itself when it has no Home).</summary>
    public async Task<List<PlexHomeUser>?> HomeUsersAsync(string token, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, "/api/v2/home/users", token, ct);
        using (doc)
        {
            if (status is < 200 or >= 300 || doc == null) return null;
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("users", out var u) ? u : default;
            if (list.ValueKind != JsonValueKind.Array) return null;
            return list.EnumerateArray().Select(x => new PlexHomeUser(IdText(x, "id"), J.Str(x, "uuid"), J.Str(x, "title"), J.Bool(x, "admin"), J.Bool(x, "protected"))).ToList();
        }
    }

    /// <summary>Switches to a Plex Home user (with their PIN when they have one) and returns that user's account token.
    /// Error text when it fails.</summary>
    public async Task<(string? Token, string Error)> SwitchUserAsync(string token, string uuid, string pin, CancellationToken ct = default)
    {
        string q = pin.Length > 0 ? "?pin=" + Uri.EscapeDataString(pin) : "";
        var (status, doc) = await SendAsync(HttpMethod.Post, $"/api/v2/home/users/{Uri.EscapeDataString(uuid)}/switch{q}", token, ct);
        using (doc)
        {
            if (status is 401 or 403) return (null, pin.Length > 0 ? "That PIN isn't right." : "This user has a PIN. Enter it to add them.");
            if (status is < 200 or >= 300 || doc == null) return (null, "plex.tv didn't answer. Try again in a moment.");
            string t = J.Str(doc.RootElement, "authToken");
            return t.Length > 0 ? (t, "") : (null, "plex.tv didn't return a token for that user.");
        }
    }
}
