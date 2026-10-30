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

    public PosterFinder(Store store, LiveStore live, RelayEngine engine)
    {
        _store = store;
        _live = live;
        _engine = engine;
    }

    public void Forget() => _cache.Clear();

    public async Task<PosterResult?> FindAsync(PosterRequest r, CancellationToken ct = default)
    {
        string key = $"{r.SourceUrl}|{r.PlexFile}|{r.SeriesOnly}|{r.Season}|{r.PlexKey}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)
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
        // 1. The NAS folder.
        if (LocalFile(r) is { } local)
        {
            var hit = tv ? FolderTvPoster(local, r.Season, r.SeriesOnly) : FolderMoviePoster(local);
            if (hit != null) return hit;
        }
        // 2. Plex (the series poster for TV).
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
        string url = sourceUrl.Replace('\\', '/');
        try { url = Uri.UnescapeDataString(url); } catch { }
        var cfg = _live.Get();
        var maps = cfg.Folders.Where(f => f.ZidooFolder.Length > 0 && f.LocalFolder.Length > 0)
            .Select(f => (Z: f.ZidooFolder, L: f.LocalFolder))
            .Concat(_store.Plex().Mappings.Where(m => m.ZidooFolder.Length > 0 && m.PlexFolder.Length > 0).Select(m => (Z: m.ZidooFolder, L: m.PlexFolder)));
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
        var plex = _engine.PlexServer();
        if (plex == null) return null;
        string thumb = r.PlexThumb;
        if (thumb.Length == 0)
        {
            PlexItem? item = r.PlexKey > 0 ? await plex.ItemAsync(r.PlexKey, ct) : null;
            item ??= r.SourceUrl.Length > 0 ? await _engine.PlexItemForUrlAsync(r.SourceUrl, ct) : null;
            if (item == null) return null;
            thumb = tv ? (item.Type == "show" ? item.Thumb : item.GrandparentThumb) : item.Thumb;
            if (tv && thumb.Length == 0 && item.ShowKey > 0 && await plex.ItemAsync(item.ShowKey, ct) is { } show) thumb = show.Thumb;
        }
        if (thumb.Length == 0) return null;
        var bytes = await plex.OriginalImageAsync(thumb, ct);
        return bytes == null ? null : Save("plex|" + thumb, bytes, tv ? "Plex series poster" : "Plex");
    }

    private async Task<PosterResult?> ZidooPosterAsync(PosterRequest r, bool tv, CancellationToken ct)
    {
        if (r.SourceUrl.Length == 0) return null;
        var zidoos = _store.Devices().Where(d => !d.IsPlex && d.Enabled && _engine.LiveOf(d.Id).Online)
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
