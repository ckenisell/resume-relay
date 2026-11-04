using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ResumeRelay;

/// <summary>
/// The Jellyfin server and sign-in, kept in jellyfin.json. Each Jellyfin user that syncs is a Device of Kind "jellyfin".
/// One administrator's sign-in (or an API key) is used for everything: it can see every session and read and write
/// every user's progress, so the users themselves need no sign-in of their own.
/// </summary>
public sealed class JellyfinConfig
{
    /// <summary>Identifies this Resume Relay to Jellyfin (it shows under Dashboard → Devices).</summary>
    public string DeviceId { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    /// <summary>The administrator's access token (from signing in or Quick Connect), or an API key.</summary>
    public string Token { get; set; } = "";
    public bool ApiKey { get; set; }
    /// <summary>Who signed in (empty for an API key).</summary>
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    /// <summary>A Quick Connect sign-in in progress (the code shown on the Jellyfin card).</summary>
    public string QuickSecret { get; set; } = "";
    public string QuickCode { get; set; } = "";
    /// <summary>"Jellyfin sees /shares/Entertainment where the Zidoos see smb://10.0.0.56/Entertainment" (same form as Plex's).</summary>
    public List<PlexMapping> Mappings { get; set; } = new();

    public bool Ready => ServerUrl.Length > 0 && Token.Length > 0;
}

/// <summary>
/// What the copy queue, Progress Reset, Watch Sync and Live Poster need from a media server (Plex or Jellyfin), for
/// one user. Items come back as PlexItem records either way: Jellyfin's are translated (its item ids get small
/// numbers of their own, see JellyfinKeys).
/// </summary>
public interface IMediaServer
{
    Task<List<PlexItem>?> AllItemsAsync(CancellationToken ct = default);
    Task<PlexItem?> ItemAsync(int key, CancellationToken ct = default);
    Task<List<PlexItem>?> ShowEpisodesAsync(int showKey, CancellationToken ct = default);
    Task<bool> SetProgressAsync(int key, int positionMs, CancellationToken ct = default);
    /// <summary>Mark as played: watched, no resume point.</summary>
    Task<bool> ScrobbleAsync(int key, CancellationToken ct = default);
    /// <summary>Mark as unplayed: not watched, no resume point (on a show: every episode).</summary>
    Task<bool> UnscrobbleAsync(int key, CancellationToken ct = default);
    Task<byte[]?> ImageAsync(string thumbPath, CancellationToken ct = default);
    Task<byte[]?> OriginalImageAsync(string thumbPath, CancellationToken ct = default);
    Task<string?> AnyEpisodeFileAsync(int showKey, CancellationToken ct = default);
}

/// <summary>
/// Jellyfin item ids are 32-character strings; everything else in Resume Relay keys a server's items by number (Plex's
/// ratingKey, a Zidoo's id). Each Jellyfin id gets a number the first time it's seen, kept for as long as Resume
/// Relay runs (nothing that uses them is saved to disk).
/// </summary>
public static class JellyfinKeys
{
    private static readonly ConcurrentDictionary<string, int> ByGuid = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, string> ByNumber = new();
    private static int _next = 1_000_000;

    public static int Of(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return 0;
        return ByGuid.GetOrAdd(guid, g => { int n = Interlocked.Increment(ref _next); ByNumber[n] = g; return n; });
    }

    public static string? Guid(int key) => ByNumber.TryGetValue(key, out var g) ? g : null;
}

public sealed record JellyfinUser(string Id, string Name, bool Admin, bool Disabled);
public sealed record JellyfinAuth(string Token, string UserId, string UserName, bool Admin, string ServerId);
public sealed record JellyfinInfo(string Id, string Name, string Version);
public sealed record JellyfinFound(string Url, string Id, string Name);

/// <summary>A Jellyfin server, as one of its users (UserId; empty for server-wide calls). Network problems never throw: calls return null/false.</summary>
public sealed class JellyfinClient : IMediaServer
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(90);
    private const long TicksPerMs = 10_000;

    private readonly string _base, _token, _deviceId;
    public string UserId { get; }

    public JellyfinClient(string baseUrl, string token, string deviceId, string userId = "")
    {
        _base = baseUrl.TrimEnd('/');
        _token = token;
        _deviceId = deviceId.Length > 0 ? deviceId : "resume-relay";
        UserId = userId;
    }

    public JellyfinClient ForUser(string userId) => new(_base, _token, _deviceId, userId);

    private string AuthHeader() =>
        $"MediaBrowser Client=\"Resume Relay\", Device=\"Resume Relay\", DeviceId=\"{_deviceId}\", Version=\"{RelayHost.Version}\"" +
        (_token.Length > 0 ? $", Token=\"{_token}\"" : "");

    private async Task<(int Status, JsonDocument? Doc)> SendAsync(HttpMethod method, string pathAndQuery, object? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(method, _base + pathAndQuery);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader());
            if (_token.Length > 0) req.Headers.TryAddWithoutValidation("X-Emby-Token", _token);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, cts.Token);
            string text = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text.Trim().Length == 0 ? "{}" : text); } catch { }
            return ((int)resp.StatusCode, doc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (0, null); }
    }

    private async Task<JsonDocument?> GetAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, pathAndQuery, null, timeout, ct);
        if (status is >= 200 and < 300) return doc;
        doc?.Dispose();
        return null;
    }

    /// <summary>Jellyfin 10.9 moved the per-user calls (/Users/{user}/Items/… → /Items/…?userId=…); older servers only
    /// know the old ones. The new form is tried first, the old one when the server doesn't know it.</summary>
    private async Task<(int Status, JsonDocument? Doc)> SendEitherAsync(HttpMethod method, string newPath, string oldPath, object? body, TimeSpan timeout, CancellationToken ct)
    {
        var r = await SendAsync(method, newPath, body, timeout, ct);
        if (r.Status is not (404 or 405)) return r;
        r.Doc?.Dispose();
        return await SendAsync(method, oldPath, body, timeout, ct);
    }

    private static IEnumerable<JsonElement> Items(JsonDocument d) =>
        d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.EnumerateArray()
        : d.RootElement.TryGetProperty("Items", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray()
        : Enumerable.Empty<JsonElement>();

    private string U => Uri.EscapeDataString(UserId);
    private static string E(string s) => Uri.EscapeDataString(s);

    // ------------------------------------------------------------------ the server

    /// <summary>The server's name, id and version (no sign-in needed), or null when nothing answers at this address.</summary>
    public async Task<JellyfinInfo?> PublicInfoAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var d = await GetAsync("/System/Info/Public", timeout, ct);
        if (d == null) return null;
        string id = J.Str(d.RootElement, "Id");
        return id.Length > 0 ? new JellyfinInfo(id, J.Str(d.RootElement, "ServerName"), J.Str(d.RootElement, "Version")) : null;
    }

    private static JellyfinAuth? ParseAuth(JsonDocument? d)
    {
        if (d == null) return null;
        var r = d.RootElement;
        string token = J.Str(r, "AccessToken");
        if (token.Length == 0 || !r.TryGetProperty("User", out var u)) return null;
        bool admin = u.TryGetProperty("Policy", out var p) && J.Bool(p, "IsAdministrator");
        return new JellyfinAuth(token, J.Str(u, "Id"), J.Str(u, "Name"), admin, J.Str(r, "ServerId"));
    }

    /// <summary>Signs in with a user name and password. (auth, "") or (null, why).</summary>
    public async Task<(JellyfinAuth? Auth, string Error)> SignInAsync(string user, string password, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", new { Username = user, Pw = password }, Normal, ct);
        using (doc)
        {
            if (status == 0) return (null, "Jellyfin didn't answer at that address.");
            if (status is 401 or 403) return (null, "Jellyfin didn't accept that user name and password.");
            var a = ParseAuth(doc);
            return a == null ? (null, $"Jellyfin answered the sign-in with an error ({status}).") : (a, "");
        }
    }

    /// <summary>Whether Quick Connect is turned on (Dashboard → Quick Connect).</summary>
    public async Task<bool> QuickConnectEnabledAsync(CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, "/QuickConnect/Enabled", null, Normal, ct);
        using (doc) return status is >= 200 and < 300 && doc?.RootElement.ValueKind == JsonValueKind.True;
    }

    /// <summary>Starts a Quick Connect sign-in: (secret, code), the code to approve in a signed-in Jellyfin app.</summary>
    public async Task<(string Secret, string Code)?> QuickConnectStartAsync(CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Post, "/QuickConnect/Initiate", null, Normal, ct);
        using (doc)
        {
            if (status is < 200 or >= 300 || doc == null) return null;
            string secret = J.Str(doc.RootElement, "Secret"), code = J.Str(doc.RootElement, "Code");
            return secret.Length > 0 && code.Length > 0 ? (secret, code) : null;
        }
    }

    /// <summary>The sign-in once the code was approved; (null, false) while waiting; (null, true) when it expired.</summary>
    public async Task<(JellyfinAuth? Auth, bool Expired)> QuickConnectCheckAsync(string secret, CancellationToken ct = default)
    {
        var (status, doc) = await SendAsync(HttpMethod.Get, "/QuickConnect/Connect?secret=" + E(secret), null, Normal, ct);
        using (doc)
        {
            if (status is 400 or 404) return (null, true);
            if (status is < 200 or >= 300 || doc == null || !J.Bool(doc.RootElement, "Authenticated")) return (null, false);
        }
        var (s2, d2) = await SendAsync(HttpMethod.Post, "/Users/AuthenticateWithQuickConnect", new { Secret = secret }, Normal, ct);
        using (d2) return (s2 is >= 200 and < 300 ? ParseAuth(d2) : null, false);
    }

    /// <summary>Every user on the server (needs an administrator's sign-in or an API key). Null when refused or unanswered.</summary>
    public async Task<List<JellyfinUser>?> UsersAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/Users", Normal, ct);
        if (d == null || d.RootElement.ValueKind != JsonValueKind.Array) return null;
        return d.RootElement.EnumerateArray().Select(u =>
        {
            var p = u.TryGetProperty("Policy", out var pol) ? pol : default;
            return new JellyfinUser(J.Str(u, "Id"), J.Str(u, "Name"), J.Bool(p, "IsAdministrator"), J.Bool(p, "IsDisabled"));
        }).ToList();
    }

    /// <summary>Library folders, for the folder mapping's suggestions.</summary>
    public async Task<List<(string Name, List<string> Paths)>?> LibraryFoldersAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/Library/VirtualFolders", Normal, ct);
        if (d == null || d.RootElement.ValueKind != JsonValueKind.Array) return null;
        return d.RootElement.EnumerateArray().Where(f => J.Str(f, "CollectionType") is "movies" or "tvshows" or "")
            .Select(f => (J.Str(f, "Name"), f.TryGetProperty("Locations", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>()))
            .ToList();
    }

    // ------------------------------------------------------------------ items

    private const string Fields = "Path,MediaSources,ProviderIds,DateCreated";

    /// <summary>A Jellyfin item as Resume Relay keeps items (positions in ms, played as a count, ids as numbers).</summary>
    public static PlexItem ParseItem(JsonElement m)
    {
        string type = J.Str(m, "Type") switch { "Movie" => "movie", "Episode" => "episode", "Series" => "show", "Season" => "season", _ => "clip" };
        var ud = m.TryGetProperty("UserData", out var u) ? u : default;
        bool played = J.Bool(ud, "Played");
        long last = 0;
        if (J.Str(ud, "LastPlayedDate") is { Length: > 0 } lp && DateTimeOffset.TryParse(lp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
            last = when.ToUnixTimeSeconds();
        var files = new List<string>();
        if (J.Str(m, "Path") is { Length: > 0 } p) files.Add(p);
        if (m.TryGetProperty("MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var s in ms.EnumerateArray())
                if (J.Str(s, "Path") is { Length: > 0 } sp && J.Str(s, "Protocol") is "" or "File" && !files.Contains(sp)) files.Add(sp);
        string id = J.Str(m, "Id");
        bool hasPrimary = m.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("Primary", out _);
        string Img(string itemId) => itemId.Length > 0 ? $"/Items/{itemId}/Images/Primary" : "";
        string parentImg = J.Str(m, "ParentPrimaryImageTag").Length > 0 ? Img(J.Str(m, "ParentPrimaryImageItemId"))
            : J.Str(m, "SeasonId").Length > 0 && type == "episode" ? Img(J.Str(m, "SeasonId")) : "";
        string seriesImg = J.Str(m, "SeriesPrimaryImageTag").Length > 0 ? Img(J.Str(m, "SeriesId")) : "";
        return new PlexItem(
            Key: JellyfinKeys.Of(id),
            Type: type,
            Title: J.Str(m, "Name"),
            Show: J.Str(m, "SeriesName"),
            Season: J.Int(m, "ParentIndexNumber"),
            Episode: J.Int(m, "IndexNumber"),
            Year: J.Int(m, "ProductionYear"),
            ViewOffsetMs: (int)Math.Clamp(J.Long(ud, "PlaybackPositionTicks") / TicksPerMs, 0, int.MaxValue),
            ViewCount: played ? Math.Max(1, J.Int(ud, "PlayCount")) : 0,
            LastViewedAt: last,
            DurationMs: (int)Math.Clamp(J.Long(m, "RunTimeTicks") / TicksPerMs, 0, int.MaxValue),
            Files: files,
            Thumb: hasPrimary ? Img(id) : "",
            ParentThumb: parentImg,
            GrandparentThumb: seriesImg,
            ShowKey: JellyfinKeys.Of(J.Str(m, "SeriesId")));
    }

    /// <summary>Every movie and episode with this user's progress, a page at a time. Null when the server doesn't answer.</summary>
    public async Task<List<PlexItem>?> AllItemsAsync(CancellationToken ct = default) => await ListAsync("Movie,Episode", ct);

    private async Task<List<PlexItem>?> ListAsync(string types, CancellationToken ct)
    {
        var all = new List<PlexItem>();
        const int page = 1000;
        for (int start = 0; start < 1_000_000; start += page)
        {
            string q = $"/Items?Recursive=true&IncludeItemTypes={types}&Fields={Fields}&EnableUserData=true&EnableImageTypes=Primary&SortBy=SortName&StartIndex={start}&Limit={page}"
                       + (UserId.Length > 0 ? "&userId=" + U : "");
            using var d = await GetAsync(q, Long, ct);
            if (d == null) return null;
            var batch = Items(d).Select(ParseItem).ToList();
            all.AddRange(batch);
            int total = J.Int(d.RootElement, "TotalRecordCount", -1);
            if (batch.Count < page || (total >= 0 && all.Count >= total)) break;
        }
        return all;
    }

    public async Task<PlexItem?> ItemAsync(int key, CancellationToken ct = default)
    {
        if (JellyfinKeys.Guid(key) is not { } id) return null;
        var (status, d) = await SendEitherAsync(HttpMethod.Get, $"/Items/{E(id)}?userId={U}&Fields={Fields}", $"/Users/{U}/Items/{E(id)}?Fields={Fields}", null, Normal, ct);
        using (d) return status is >= 200 and < 300 && d != null && J.Str(d.RootElement, "Id").Length > 0 ? ParseItem(d.RootElement) : null;
    }

    public async Task<List<PlexItem>?> ShowEpisodesAsync(int showKey, CancellationToken ct = default)
    {
        if (JellyfinKeys.Guid(showKey) is not { } id) return null;
        using var d = await GetAsync($"/Shows/{E(id)}/Episodes?userId={U}&Fields={Fields}&EnableUserData=true", Long, ct);
        return d == null ? null : Items(d).Select(ParseItem).ToList();
    }

    public async Task<string?> AnyEpisodeFileAsync(int showKey, CancellationToken ct = default) =>
        (await ShowEpisodesAsync(showKey, ct))?.SelectMany(e => e.Files).FirstOrDefault();

    /// <summary>Sets this user's resume point (and "not played").</summary>
    public async Task<bool> SetProgressAsync(int key, int positionMs, CancellationToken ct = default)
    {
        if (JellyfinKeys.Guid(key) is not { } id) return false;
        var body = new { PlaybackPositionTicks = positionMs * TicksPerMs, Played = false };
        var (status, d) = await SendEitherAsync(HttpMethod.Post, $"/UserItems/{E(id)}/UserData?userId={U}", $"/Users/{U}/Items/{E(id)}/UserData", body, Normal, ct);
        d?.Dispose();
        return status is >= 200 and < 300;
    }

    public Task<bool> ScrobbleAsync(int key, CancellationToken ct = default) => PlayedAsync(key, HttpMethod.Post, ct);
    public Task<bool> UnscrobbleAsync(int key, CancellationToken ct = default) => PlayedAsync(key, HttpMethod.Delete, ct);

    /// <summary>Mark played (POST) or unplayed (DELETE): Jellyfin clears the resume point either way; on a series it
    /// covers every episode.</summary>
    private async Task<bool> PlayedAsync(int key, HttpMethod method, CancellationToken ct)
    {
        if (JellyfinKeys.Guid(key) is not { } id) return false;
        var (status, d) = await SendEitherAsync(method, $"/UserPlayedItems/{E(id)}?userId={U}", $"/Users/{U}/PlayedItems/{E(id)}", null, Normal, ct);
        d?.Dispose();
        return status is >= 200 and < 300;
    }

    // ------------------------------------------------------------------ what's playing

    /// <summary>What is playing on the server, for every user (needs an administrator). Null when it doesn't answer.</summary>
    public async Task<List<PlexSession>?> SessionsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var d = await GetAsync("/Sessions?activeWithinSeconds=960", timeout, ct);
        if (d == null || d.RootElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PlexSession>();
        foreach (var s in d.RootElement.EnumerateArray())
        {
            if (!s.TryGetProperty("NowPlayingItem", out var np) || np.ValueKind != JsonValueKind.Object) continue;
            var item = ParseItem(np);
            var ps = s.TryGetProperty("PlayState", out var st) ? st : default;
            item = item with { ViewOffsetMs = (int)Math.Clamp(J.Long(ps, "PositionTicks") / TicksPerMs, 0, int.MaxValue) };
            list.Add(new PlexSession(item, J.Bool(ps, "IsPaused") ? "paused" : "playing", J.Str(s, "UserId"), J.Str(s, "UserName")));
        }
        return list;
    }

    /// <summary>The server's raw answer for what's playing, for the troubleshooting page.</summary>
    public async Task<string?> SessionsTextAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/Sessions?activeWithinSeconds=960", Normal, ct);
        return d == null ? null : JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    // ------------------------------------------------------------------ pictures and ratings

    private async Task<byte[]?> BytesAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _base + url);
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader());
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public Task<byte[]?> ImageAsync(string thumbPath, CancellationToken ct = default) =>
        thumbPath.Length == 0 ? Task.FromResult<byte[]?>(null) : BytesAsync(thumbPath + "?maxHeight=360&quality=90", ct);

    public Task<byte[]?> OriginalImageAsync(string thumbPath, CancellationToken ct = default) =>
        thumbPath.Length == 0 ? Task.FromResult<byte[]?>(null) : BytesAsync(thumbPath, ct);

    /// <summary>
    /// Every movie and series with Jellyfin's community rating, for Highly Acclaimed. Jellyfin gets it from TMDB unless
    /// its OMDb plugin is installed (then it's IMDb's), and can't say which, so it's labelled "Jellyfin". Its critic
    /// rating is Rotten Tomatoes': never used.
    /// </summary>
    public async Task<List<PlexRated>?> RatedTitlesAsync(CancellationToken ct = default)
    {
        var all = new List<PlexRated>();
        const int page = 1000;
        for (int start = 0; start < 1_000_000; start += page)
        {
            using var d = await GetAsync($"/Items?Recursive=true&IncludeItemTypes=Movie,Series&Fields=Path,ProviderIds&EnableImageTypes=Primary&StartIndex={start}&Limit={page}"
                                         + (UserId.Length > 0 ? "&userId=" + U : ""), Long, ct);
            if (d == null) return null;
            var batch = Items(d).ToList();
            foreach (var m in batch)
            {
                var it = ParseItem(m);
                all.Add(new PlexRated(it.Key, it.Type == "show" ? "show" : "movie", it.Title, it.Year, J.Dbl(m, "CommunityRating"), "Jellyfin", it.Thumb, it.Files));
            }
            if (batch.Count < page) break;
        }
        return all;
    }

    // ------------------------------------------------------------------ finding servers

    /// <summary>Jellyfin servers on the network (they answer "Who is JellyfinServer?" on UDP 7359). A second or two.
    /// RESUMERELAY_JELLYFIN_DISCOVER sends the question to one address instead (the tests use it).</summary>
    public static async Task<List<JellyfinFound>> DiscoverAsync(CancellationToken ct = default)
    {
        var found = new List<JellyfinFound>();
        try
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            var msg = Encoding.UTF8.GetBytes("Who is JellyfinServer?");
            var targets = Environment.GetEnvironmentVariable("RESUMERELAY_JELLYFIN_DISCOVER") is { Length: > 0 } one
                ? new[] { IPAddress.Parse(one) }
                : new[] { IPAddress.Broadcast }.Concat(Discovery.LocalSubnets().Select(n => IPAddress.Parse(n.TrimEnd('.', '*') + ".255"))).ToArray();
            foreach (var t in targets) try { await udp.SendAsync(msg, msg.Length, new IPEndPoint(t, 7359)); } catch { }
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(until - DateTime.UtcNow);
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { break; }
                try
                {
                    using var d = JsonDocument.Parse(r.Buffer);
                    string url = J.Str(d.RootElement, "Address"), id = J.Str(d.RootElement, "Id");
                    if (url.Length > 0 && id.Length > 0 && found.All(f => f.Id != id)) found.Add(new JellyfinFound(url.TrimEnd('/'), id, J.Str(d.RootElement, "Name")));
                }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return found;
    }
}
