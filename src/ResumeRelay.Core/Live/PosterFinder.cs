using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ResumeRelay.Live;

/// <summary>
/// Finds the poster for a title, in Chet's order:
///   movies:  the movie's folder → Plex → the Zidoo;
///   TV:      the season poster in the show's folder → the series poster there → Plex's series poster → the Zidoo's.
/// Folder posters follow the Kodi naming (Aladdin (1992).4k-poster.jpg, poster.jpg, season1-poster.jpg…), for shows
/// with their episodes in the show's folder or in "Season 01" folders. Zidoos keep posters at 500×750, so they come
/// last. Results are remembered for 10 minutes (a poster added to a folder shows up after that).
/// </summary>
public sealed class PosterFinder
{
    private static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly Regex SeasonFolder = new(@"^(?:season|series|staffel|saison|temporada|seizoen)[ ._-]*(\d+)$|^specials$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeTag = new(@"\bS(\d{1,2})E\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Store _store;
    private readonly LiveStore _live;
    private readonly RelayEngine _engine;
    private readonly ConcurrentDictionary<string, (PosterResult? Result, DateTime At)> _cache = new();

    public PosterFinder(Store store, LiveStore live, RelayEngine engine, AppleTvService appleTv)
    {
        _store = store;
        _live = live;
        _engine = engine;
        _appleTv = appleTv;
    }

    private readonly AppleTvService _appleTv;
    private readonly Tmdb _tmdb = new();

    /// <summary>A title known only by its name (an app on an Apple TV): "tmdb:" + kind ("tv", "movie" or "any") + "|" +
    /// the title (escaped) + "|" + year. Its poster comes from TMDB; the CoverUrl ("appletv:" + id) is the app's own
    /// artwork, used when TMDB has none.</summary>
    public const string TmdbPrefix = "tmdb:";
    public const string AppleTvArtPrefix = "appletv:";

    public static string TmdbUrl(string kind, string title, int year) => $"{TmdbPrefix}{kind}|{Uri.EscapeDataString(title)}|{year}";

    public void Forget() => _cache.Clear();

    /// <summary>What a Kaleidescape title's SourceUrl starts with (its content handle follows): there's no file.</summary>
    public const string KaleidescapePrefix = "kaleidescape://";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
            var bytes = await Http.GetByteArrayAsync(u, ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public async Task<PosterResult?> FindAsync(PosterRequest r, CancellationToken ct = default)
    {
        string key = $"{r.SourceUrl}|{r.PlexFile}|{r.SeriesOnly}|{r.Season}|{r.PlexKey}|{r.Server}|{r.CoverUrl}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(4)
            && (hit.Result == null || File.Exists(hit.Result.Path)))
            return hit.Result;
        PosterResult? found = null;
        try { found = await FindNowAsync(r, ct); }
        catch (OperationCanceledException) { throw; }
        catch { }
        _cache[key] = (found, DateTime.UtcNow);
        return found;
    }

    private async Task<PosterResult?> FindNowAsync(PosterRequest r, CancellationToken ct)
    {
        bool tv = r.IsEpisode || r.SeriesOnly;
        // A title an Apple TV app is playing: TMDB's best poster (the season's for an episode), else the app's artwork.
        if (r.SourceUrl.StartsWith(TmdbPrefix, StringComparison.Ordinal))
        {
            var parts = r.SourceUrl[TmdbPrefix.Length..].Split('|');
            string kind = parts.Length > 0 ? parts[0] : "any", title = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : r.Title;
            int year = parts.Length > 2 && int.TryParse(parts[2], out var y) ? y : 0;
            if (await _tmdb.PosterAsync(title, kind, year, tv && !r.SeriesOnly ? r.Season : 0, "en", ct) is { } t)
                return Save("tmdb|" + r.SourceUrl + "|" + r.Season, t.Bytes, t.Label);
            if (r.CoverUrl.StartsWith(AppleTvArtPrefix, StringComparison.Ordinal) && await _appleTv.ArtworkAsync(r.CoverUrl[AppleTvArtPrefix.Length..], ct) is { Length: > 0 } art)
                return Save("appletv|" + r.SourceUrl + "|" + r.Season, art, "the app's own artwork (TMDB has no poster for it)");
            return null;
        }
        // A Kaleidescape movie: its own cover, and nothing else (it plays its own copy, not a file on the NAS).
        if (r.CoverUrl.Length > 0 || r.SourceUrl.StartsWith(KaleidescapePrefix, StringComparison.Ordinal))
            return r.CoverUrl.Length > 0 && await DownloadAsync(r.CoverUrl, ct) is { } cover ? Save("kaleidescape|" + r.CoverUrl, cover, "Kaleidescape") : null;
        // 1. The NAS folder.
        if (LocalFile(r) is { } local)
        {
            var hit = tv ? FolderTvPoster(local, r.Season, r.SeriesOnly) : FolderMoviePoster(local);
            if (hit != null) return hit;
        }
        // 2. Plex, or Jellyfin for a Jellyfin user (the series poster for TV).
        if (await PlexPosterAsync(r, tv, ct) is { } plex) return plex;
        // 3. The Zidoo (the series poster for TV).
        return await ZidooPosterAsync(r, tv, ct);
    }

    // ------------------------------------------------------------------ folders

    /// <summary>Where this computer can open the file: Plex's own path (Plex runs here), else the folders set on the
    /// Live Poster tab, else Plex's folder mapping, else (Windows) the share's \\server\share address.</summary>
    public string? LocalFile(PosterRequest r)
    {
        if (r.PlexFile.Length > 0 && SafeExists(r.PlexFile)) return r.PlexFile;
        return LocalPathOf(r.SourceUrl);
    }

    public string? LocalPathOf(string sourceUrl)
    {
        if (sourceUrl.Length == 0) return null;
        // A Plex or Jellyfin file known only by the server's own path: there when Resume Relay runs on the server's computer.
        if (PathMatch.IsServerUrl(sourceUrl)) { string own = PathMatch.ServerPath(sourceUrl); return SafeExists(own) ? own : null; }
        string url = sourceUrl.Replace('\\', '/');
        try { url = Uri.UnescapeDataString(url); } catch { }
        var cfg = _live.Get();
        var maps = cfg.Folders.Where(f => f.ZidooFolder.Length > 0 && f.LocalFolder.Length > 0)
            .Select(f => (Z: f.ZidooFolder, L: f.LocalFolder))
            .Concat(_store.Plex().Mappings.Concat(_store.Jellyfin().Mappings).Where(m => m.ZidooFolder.Length > 0 && m.PlexFolder.Length > 0).Select(m => (Z: m.ZidooFolder, L: m.PlexFolder)));
        foreach (var (z, l) in maps)
        {
            string zz = z.Replace('\\', '/').TrimEnd('/');
            if (!url.StartsWith(zz + "/", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = l.TrimEnd('/', '\\') + url[zz.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (SafeExists(candidate) || SafeDirExists(Path.GetDirectoryName(candidate))) return candidate;
        }
        if (OperatingSystem.IsWindows() && url.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            string unc = @"\\" + url["smb://".Length..].Replace('/', '\\');
            if (SafeDirExists(Path.GetDirectoryName(unc))) return unc;
        }
        return null;
    }

    private static PosterResult? FolderMoviePoster(string file)
    {
        string? dir = Path.GetDirectoryName(file);
        if (dir == null) return null;
        string stem = Path.GetFileNameWithoutExtension(file);
        return FirstImage(dir, new[] { stem + "-poster", "poster", "folder", "movie", "cover" });
    }

    private static PosterResult? FolderTvPoster(string file, int season, bool seriesOnly)
    {
        string? fileDir = Path.GetDirectoryName(file);
        if (fileDir == null) return null;
        string? seasonDir = null, showDir = fileDir;
        var sm = SeasonFolder.Match(Path.GetFileName(fileDir));
        if (sm.Success)
        {
            seasonDir = fileDir;
            showDir = Path.GetDirectoryName(fileDir);
            if (season <= 0) season = sm.Groups[1].Success ? int.Parse(sm.Groups[1].Value) : 0;
        }
        if (season <= 0 && EpisodeTag.Match(Path.GetFileName(file)) is { Success: true } em) season = int.Parse(em.Groups[1].Value);
        if (showDir == null) return null;

        if (!seriesOnly)
        {
            if (seasonDir != null && FirstImage(seasonDir, new[] { "poster", "folder", "cover" }) is { } inSeason) return inSeason;
            var names = season == 0
                ? new List<string> { "season-specials-poster", "season00-poster", "season0-poster", "specials" }
                : new List<string> { $"season{season:00}-poster", $"season{season}-poster", $"season{season:00}", $"season{season}" };
            if (FirstImage(showDir, names) is { } named) return named;
            // "Ahsoka S01-poster.jpg"
            var tagged = new Regex($@"(?:^|[ ._-])S0*{season}-poster$", RegexOptions.IgnoreCase);
            if (FirstImage(showDir, null, tagged) is { } t) return t;
        }
        return FirstImage(showDir, new[] { "poster", "folder", "show" });
    }

    /// <summary>The first image in a folder whose name (without extension) is one of the names, in their order, or
    /// matches the pattern. Case doesn't matter.</summary>
    private static PosterResult? FirstImage(string dir, IList<string>? names, Regex? pattern = null)
    {
        string[] files;
        try { files = Directory.GetFiles(dir); } catch { return null; }
        var images = files.Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        IEnumerable<string> ordered = names != null
            ? names.SelectMany(n => images.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), n, StringComparison.OrdinalIgnoreCase)))
            : images.Where(f => pattern!.IsMatch(Path.GetFileNameWithoutExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in ordered)
        {
            var (w, h) = ImageInfo.Size(f);
            if (w > 0) return new PosterResult(f, "Folder: " + Path.GetFileName(f), w, h);
        }
        return null;
    }

    // ------------------------------------------------------------------ Plex and Zidoo

    private async Task<PosterResult?> PlexPosterAsync(PosterRequest r, bool tv, CancellationToken ct)
    {
        // A Jellyfin user's title (or a Jellyfin rating's) comes from Jellyfin; anything else from Plex.
        var device = r.DeviceId.Length > 0 ? _store.Device(r.DeviceId) : null;
        bool jellyfin = r.Server == "jellyfin" || device?.IsJellyfin == true;
        IMediaServer? plex = jellyfin ? (device is { IsJellyfin: true } ? _engine.ServerFor(device) : _engine.JellyfinServer()) : _engine.PlexServer();
        if (plex == null) return null;
        string label = jellyfin ? "Jellyfin" : "Plex";
        string thumb = r.PlexThumb;
        if (thumb.Length == 0)
        {
            PlexItem? item = r.PlexKey > 0 ? await plex.ItemAsync(r.PlexKey, ct) : null;
            item ??= r.SourceUrl.Length > 0
                ? jellyfin ? await _engine.ServerItemForUrlAsync(device ?? _store.Devices().FirstOrDefault(d => d.IsJellyfin && d.Enabled), r.SourceUrl, ct)
                  : await _engine.PlexItemForUrlAsync(r.SourceUrl, ct)
                : null;
            if (item == null) return null;
            thumb = tv ? (item.Type == "show" ? item.Thumb : item.GrandparentThumb) : item.Thumb;
            if (tv && thumb.Length == 0 && item.ShowKey > 0 && await plex.ItemAsync(item.ShowKey, ct) is { } show) thumb = show.Thumb;
        }
        if (thumb.Length == 0) return null;
        var bytes = await plex.OriginalImageAsync(thumb, ct);
        // A season's own poster is only ever asked for by its path (Feature Presentation); the rest is the series poster.
        string what = !tv ? label : r.PlexThumb.Length > 0 && r.IsEpisode && !r.SeriesOnly ? $"{label} season poster" : $"{label} series poster";
        return bytes == null ? null : Save(label.ToLowerInvariant() + "|" + thumb, bytes, what);
    }

    private async Task<PosterResult?> ZidooPosterAsync(PosterRequest r, bool tv, CancellationToken ct)
    {
        if (r.SourceUrl.Length == 0 || PathMatch.IsServerUrl(r.SourceUrl)) return null;
        var zidoos = _store.Devices().Where(d => !d.IsServerUser && d.Enabled && _engine.LiveOf(d.Id).Online)
            .OrderByDescending(d => d.Id == r.DeviceId).ToList();
        foreach (var d in zidoos.Take(2))
        {
            var api = new ZidooClient(d.Ip, d.Port);
            var ids = await api.PosterIdsAsync(r.SourceUrl, ct);
            if (ids == null) continue;
            int id = tv ? (ids.Value.SeriesId > 0 ? ids.Value.SeriesId : ids.Value.SeasonId) : (ids.Value.TitleId > 0 ? ids.Value.TitleId : ids.Value.SeasonId);
            if (id <= 0) continue;
            var bytes = await api.GetStoredPosterAsync(id, ct);
            if (bytes != null) return Save($"zidoo|{d.Id}|{id}", bytes, $"Zidoo ({d.Name}){(tv ? " series poster" : "")}");
        }
        return null;
    }

    private PosterResult? Save(string key, byte[] bytes, string source)
    {
        var (w, h) = ImageInfo.Size(bytes);
        if (w <= 0) return null;
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant() + ImageInfo.Extension(bytes);
        string path = Path.Combine(_live.PosterDir, name);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) File.WriteAllBytes(path, bytes);
        }
        catch { return null; }
        return new PosterResult(path, source, w, h);
    }

    private static bool SafeExists(string p) { try { return File.Exists(p); } catch { return false; } }
    private static bool SafeDirExists(string? p) { try { return p != null && Directory.Exists(p); } catch { return false; } }
}
