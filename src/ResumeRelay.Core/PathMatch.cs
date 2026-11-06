using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>
/// Automatic matching: the same file, wherever each player or server keeps it. Plex might see
/// "/mnt/media/Movies/Arrival (2016)/Arrival (2016).mkv", Jellyfin "/data/Movies/Arrival (2016)/Arrival (2016).mkv" and a
/// Zidoo "smb://10.0.0.56/Entertainment/Movies/Arrival (2016)/Arrival (2016).mkv": what comes before the library differs,
/// the end of the path doesn't. A file is matched by the shortest end of its path that only one file on the other side
/// has, and that names something (a movie's file name with its year, or a folder or file name that isn't just
/// "Season 01" or "S01E01"), so "Season 01/S01E01.mkv" alone never matches one show's episode to another's.
/// A folder mapping, where one is set, still decides first; this covers everything else.
/// </summary>
public static class PathMatch
{
    /// <summary>The address given to a file a Plex or Jellyfin server has that no folder mapping turns into a Zidoo
    /// path: "server:" + the server's own path. The copy queue matches it on other servers by the end of the path.</summary>
    public const string ServerScheme = "server:";

    public static bool IsServerUrl(string? url) => url != null && url.StartsWith(ServerScheme, StringComparison.Ordinal);
    public static string ServerUrl(string path) => ServerScheme + Norm(path);
    public static string ServerPath(string url) => IsServerUrl(url) ? url[ServerScheme.Length..] : url;

    private static string Norm(string? p) => (p ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>A path's parts, lower-case, from a plain path, a server: address or an smb:// or nfs:// address
    /// (whose server and share are left out).</summary>
    public static string[] Parts(string? pathOrUrl)
    {
        string p = Norm(ServerPath(pathOrUrl ?? ""));
        try { if (p.Contains('%')) p = Uri.UnescapeDataString(p); } catch { }
        var m = Regex.Match(p, @"^[a-z][a-z0-9+.-]*://[^/]*(?<rest>/.*)?$", RegexOptions.IgnoreCase);
        if (m.Success) p = m.Groups["rest"].Value;
        return p.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToArray();
    }

    public static string Tail(string[] parts, int n) => string.Join("/", parts[^Math.Min(n, parts.Length)..]);

    private static readonly Regex GenericFolder = new(
        @"^(season|series|staffel|saison|temporada|stagione|seizoen)[ ._-]*\d+|^s\d{1,3}$|^specials?$|^extras?$|^featurettes?$|^bonus|^trailers?$" +
        @"|^(behind the scenes|deleted scenes|interviews|scenes|shorts|other|others|samples?)$|^(disc|disk|cd|dvd)[ ._-]*\d+$" +
        @"|^(movies?|films?|tv|tv shows?|shows?|series|television|media|videos?|kids|4k|uhd|hd)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeTag = new(@"\bs\d{1,3}[ ._-]*e\d{1,4}([ ._-]*-?[ ._-]*e?\d{1,4})*\b|\b\d{1,2}x\d{2,3}\b|\b(e|ep|episode)[ ._-]*\d{1,4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GenericFile = new(@"^(trailer|sample|featurette|interview|behind the scenes|deleted scene|scene|short|extra|bonus|other|clip|teaser|making of)s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Year = new(@"(^|[^0-9])(19|20)\d{2}([^0-9]|$)", RegexOptions.Compiled);

    /// <summary>Whether a file name says which movie or episode it is by itself (words beyond an episode tag).</summary>
    private static bool NamesSomething(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (GenericFile.IsMatch(stem.Trim())) return false;
        string rest = EpisodeTag.Replace(stem, " ");
        rest = Regex.Replace(rest, @"\b(2160p|1080p|720p|480p|4k|uhd|hdr10?\+?|dv|dovi|remux|bluray|blu-ray|web-?dl|webrip|x26[45]|h\.?26[45]|hevc|avc|aac|ac3|dts(-hd)?|truehd|atmos)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Matches(rest, @"\p{L}").Count >= 3;
    }

    /// <summary>Whether the last n parts are enough to name a file: n = 1 only for a movie-like file name with a year;
    /// otherwise at least one of the parts must name something (not "Season 01", "Extras" or "S01E01.mkv").</summary>
    public static bool Distinctive(string[] parts, int n)
    {
        if (parts.Length == 0) return false;
        string file = parts[^1];
        if (n <= 1) return parts.Length == 1 ? NamesSomething(file) : NamesSomething(file) && Year.IsMatch(Path.GetFileNameWithoutExtension(file)) && !EpisodeTag.IsMatch(file);
        if (NamesSomething(file)) return true;
        for (int i = 2; i <= Math.Min(n, parts.Length); i++)
            if (!GenericFolder.IsMatch(parts[^i])) return true;
        return false;
    }

    /// <summary>The files on one side, to look the other side's files up in.</summary>
    public sealed class Index<T>
    {
        private readonly Dictionary<string, List<(T Value, string[] Parts)>> _byTail = new(StringComparer.Ordinal);
        public int Count { get; private set; }
        public const int MaxDepth = 8;

        public void Add(string pathOrUrl, T value)
        {
            var parts = Parts(pathOrUrl);
            if (parts.Length == 0) return;
            Count++;
            for (int n = 1; n <= Math.Min(MaxDepth, parts.Length); n++)
            {
                string t = Tail(parts, n);
                if (!_byTail.TryGetValue(t, out var list)) _byTail[t] = list = new();
                list.Add((value, parts));
            }
        }

        /// <summary>The one file whose path ends the same way: the shortest end that's distinctive and that only one file
        /// here has. Nothing when no file here ends that way, or the ends that match are all shared by several files.</summary>
        public bool TryFind(string pathOrUrl, out T value)
        {
            value = default!;
            var parts = Parts(pathOrUrl);
            for (int n = 1; n <= Math.Min(MaxDepth, parts.Length); n++)
            {
                if (!_byTail.TryGetValue(Tail(parts, n), out var list)) return false; // nothing here ends this way
                if (!Distinctive(parts, n)) continue;
                var distinct = list.Select(x => x.Value).Distinct().Take(2).ToList();
                if (distinct.Count == 1) { value = distinct[0]; return true; }
            }
            return false;
        }

        /// <summary>Files here with the same file name (but no unique match), for the Compare panel's "same name, different folder".</summary>
        public bool HasFileName(string pathOrUrl)
        {
            var parts = Parts(pathOrUrl);
            return parts.Length > 0 && _byTail.ContainsKey(parts[^1]);
        }
    }
}
