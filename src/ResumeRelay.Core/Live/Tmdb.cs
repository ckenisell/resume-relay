using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ResumeRelay.Live;

/// <summary>
/// TMDB (themoviedb.org): posters for titles that come with no file and no Plex item, like a Netflix or Disney+ show
/// playing on an Apple TV. The title is looked up, and of its posters (2:3 only) the best is taken: in the title's
/// language first, then without words, then any; at each, the biggest first (2000×3000, then 1920×2880, 1000×1500…),
/// the most-voted breaking ties. A TV episode gets its season's poster when TMDB has one, as everywhere on Live Poster.
/// The API key is built into release builds (from the TMDB_API_KEY secret) and never shown; with none, nothing is
/// looked up. "This product uses the TMDB API but is not endorsed or certified by TMDB."
/// </summary>
public sealed class Tmdb
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(30) };

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
    private static readonly string ApiBase = Env("RESUMERELAY_TMDB_API") is { Length: > 0 } a ? a.TrimEnd('/') : "https://api.themoviedb.org/3";
    private static readonly string ImageBase = Env("RESUMERELAY_TMDB_IMAGES") is { Length: > 0 } i ? i.TrimEnd('/') : "https://image.tmdb.org/t/p/original";

    /// <summary>The key: RESUMERELAY_TMDB_KEY (tests, or someone's own), else the one built in. A v4 read access token
    /// (it has dots) is sent as a bearer token, a v3 key as api_key.</summary>
    public static string Key { get; } = Env("RESUMERELAY_TMDB_KEY") is { Length: > 0 } k ? k
        : typeof(Tmdb).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "TmdbApiKey")?.Value ?? "";

    public static bool Available => Key.Length > 0;

    private static async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        if (!Available) return null;
        bool bearer = Key.Contains('.');
        string url = ApiBase + path + (bearer ? "" : (path.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(Key));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>A movie or series on TMDB.</summary>
    public sealed record Match(int Id, string Kind, string Name, int Year, string Language);

    /// <summary>The title on TMDB: a series when it's an episode (or kind is "tv"), a movie when kind is "movie", else
    /// whichever TMDB ranks first. A year, when known, narrows a movie search.</summary>
    public async Task<Match?> FindAsync(string title, string kind, int year, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0) return null;
        string q = Uri.EscapeDataString(title);
        string path = kind switch
        {
            "tv" => $"/search/tv?query={q}",
            "movie" => $"/search/movie?query={q}" + (year > 0 ? $"&year={year}" : ""),
            _ => $"/search/multi?query={q}",
        };
        using var d = await GetAsync(path, ct);
        if (d == null || !d.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var list = results.EnumerateArray().Select(r =>
        {
            string type = kind is "tv" or "movie" ? kind : J.Str(r, "media_type");
            string name = type == "tv" ? J.Str(r, "name") : J.Str(r, "title");
            string date = type == "tv" ? J.Str(r, "first_air_date") : J.Str(r, "release_date");
            int y = date.Length >= 4 && int.TryParse(date[..4], out var yy) ? yy : 0;
            return new Match(J.Int(r, "id"), type, name, y, J.Str(r, "original_language"));
        }).Where(m => m.Id > 0 && m.Kind is "tv" or "movie").ToList();
        // The one named exactly the same wins (TMDB sometimes ranks a similar, more popular title first).
        return list.FirstOrDefault(m => string.Equals(m.Name, title, StringComparison.OrdinalIgnoreCase) && (year == 0 || m.Year == 0 || m.Year == year))
            ?? list.FirstOrDefault(m => string.Equals(m.Name, title, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    /// <summary>One of a title's posters.</summary>
    public sealed record Poster(string FilePath, int Width, int Height, string Language, double Votes, int VoteCount);

    /// <summary>The best poster (see the class): 2:3 only; the language, then none, then any; biggest; most voted.</summary>
    public static Poster? Best(IEnumerable<Poster> posters, string language)
    {
        var tall = posters.Where(p => p.Width > 0 && p.Height > 0 && Math.Abs(p.Width / (double)p.Height - 2.0 / 3.0) < 0.01).ToList();
        int Rank(Poster p) => p.Language == language && language.Length > 0 ? 0 : p.Language.Length == 0 ? 1 : 2;
        return tall.OrderBy(Rank).ThenByDescending(p => p.Width).ThenByDescending(p => p.Votes).ThenByDescending(p => p.VoteCount).FirstOrDefault();
    }

    private async Task<List<Poster>> PostersAsync(string path, CancellationToken ct)
    {
        using var d = await GetAsync(path, ct);
        if (d == null || !d.RootElement.TryGetProperty("posters", out var ps) || ps.ValueKind != JsonValueKind.Array) return new();
        return ps.EnumerateArray().Select(p => new Poster(J.Str(p, "file_path"), J.Int(p, "width"), J.Int(p, "height"),
            p.TryGetProperty("iso_639_1", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "",
            p.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0, J.Int(p, "vote_count")))
            .Where(p => p.FilePath.Length > 0).ToList();
    }

    /// <summary>The best poster's picture for a title (its season's for an episode, when TMDB has one), with what it
    /// is for the card ("TMDB: Wednesday, season 1, 2000×3000"). Null when TMDB isn't available or has none.</summary>
    public async Task<(byte[] Bytes, string Label)?> PosterAsync(string title, string kind, int year, int season, string language = "en", CancellationToken ct = default)
    {
        var m = await FindAsync(title, season > 0 ? "tv" : kind, year, ct);
        if (m == null) return null;
        // Images in every language, so "no words" and others are there to fall back on.
        string all = "include_image_language=" + Uri.EscapeDataString(string.Join(",", new[] { language, m.Language, "null" }.Where(x => x.Length > 0).Distinct()))
                     + "&language=" + Uri.EscapeDataString(language);
        Poster? best = null;
        string what = m.Name;
        if (m.Kind == "tv" && season > 0)
        {
            best = Best(await PostersAsync($"/tv/{m.Id}/season/{season}/images?{all}", ct), language);
            if (best != null) what += $", season {season}";
        }
        best ??= Best(await PostersAsync($"/{m.Kind}/{m.Id}/images?{all}", ct), language);
        if (best == null) return null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(ImageBase + best.FilePath, ct);
            return bytes.Length > 0 ? (bytes, $"TMDB: {what}, {best.Width}×{best.Height}") : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
