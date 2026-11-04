using System.Text.RegularExpressions;

namespace ResumeRelay.Live;

/// <summary>
/// Feature Presentation: each screen shows one title of its own choosing, once per round, over its own background.
/// The titles to choose from are the libraries' own lists (Plex, Jellyfin, a Zidoo's poster wall), so it's one entry
/// per movie (never its extras) and one per TV show (never its episodes); a show then gets a season, whose poster is
/// used. Watch Sync lists every file because resume points belong to files; this lists titles.
/// </summary>
public sealed partial class LiveService
{
    /// <summary>One title to choose from. Id is "plex:123", "jellyfin:1000004", "zidoo:{player}:5012" or
    /// "kaleidescape:{content handle}".</summary>
    public sealed record FeatureEntry(string Id, string From, int Key, string DeviceId, string Kind, string Title, int Year, string Thumb, string File);
    public sealed record FeatureSeason(int Season, string Name, int ZidooId);

    private List<FeatureEntry> _featureTitles = new();
    private DateTime _featureAt = DateTime.MinValue;
    private string _featureNote = "";
    private readonly SemaphoreSlim _featureGate = new(1, 1);

    private static readonly Regex Leading = new(@"^(?:the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeasonNo = new(@"(?:season|series|staffel|saison|temporada|seizoen|s)\s*0*(\d{1,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The name a title sorts by: "Dark Knight, The" sorts under D.</summary>
    public static string SortName(string title) => Leading.Replace(title.Trim(), "");

    /// <summary>
    /// Every movie and TV show the libraries list, A to Z: Plex first, then Jellyfin, then one Zidoo's poster wall;
    /// a title already listed by an earlier library isn't listed again. Read again after 10 minutes (or when asked).
    /// </summary>
    public async Task<(List<FeatureEntry> Titles, string Note)> FeatureTitlesAsync(bool fresh, CancellationToken ct = default)
    {
        if (!fresh && DateTime.UtcNow - _featureAt < TimeSpan.FromMinutes(10) && _featureTitles.Count > 0) return (_featureTitles, _featureNote);
        await _featureGate.WaitAsync(ct);
        try
        {
            if (!fresh && DateTime.UtcNow - _featureAt < TimeSpan.FromMinutes(10) && _featureTitles.Count > 0) return (_featureTitles, _featureNote);
            var list = new List<FeatureEntry>();
            // Movies are the same title when the names match and the years do (or one isn't known); a remake with the
            // same name (Dune 1984, Dune 2021) is its own title. Shows go by name.
            var movies = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var shows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Add(FeatureEntry e)
            {
                string name = e.Title.Trim();
                if (name.Length == 0) return false;
                if (e.Kind == "show") { if (!shows.Add(name)) return false; }
                else
                {
                    if (movies.TryGetValue(name, out var years) && years.Any(y => y == 0 || e.Year == 0 || Math.Abs(y - e.Year) <= 1)) return false;
                    (movies.TryGetValue(name, out var ys) ? ys : movies[name] = new()).Add(e.Year);
                }
                list.Add(e);
                return true;
            }
            var parts = new List<string>();
            var problems = new List<string>();
            if (_engine.PlexServer() is { } plex)
            {
                var rows = await plex.RatedTitlesAsync(ct);
                if (rows == null) problems.Add("the Plex server didn't answer");
                else
                {
                    int n = rows.Count(r => Add(new FeatureEntry($"plex:{r.Key}", "plex", r.Key, "", r.Type, r.Title, r.Year, r.Thumb, r.Files.FirstOrDefault() ?? "")));
                    if (n > 0) parts.Add($"{n:N0} from Plex");
                }
            }
            if (_engine.JellyfinServer() is { } jf)
            {
                var rows = await jf.RatedTitlesAsync(ct);
                if (rows == null) problems.Add("the Jellyfin server didn't answer");
                else
                {
                    int n = rows.Count(r => Add(new FeatureEntry($"jellyfin:{r.Key}", "jellyfin", r.Key, "", r.Type, r.Title, r.Year, r.Thumb, r.Files.FirstOrDefault() ?? "")));
                    if (n > 0) parts.Add($"{n:N0} from Jellyfin");
                }
            }
            var zidoo = _store.Devices().FirstOrDefault(d => !d.IsServerUser && d.Enabled && _engine.LiveOf(d.Id).Online);
            if (zidoo != null)
            {
                var (zm, zs) = await ZidooRatings.WallAsync(new ZidooClient(zidoo.Ip, zidoo.Port), ct);
                int n = zm.Count(m => Add(new FeatureEntry($"zidoo:{zidoo.Id}:{m.Id}", "zidoo", m.Id, zidoo.Id, "movie", m.Name, m.Year, "", "")))
                      + zs.Count(s => Add(new FeatureEntry($"zidoo:{zidoo.Id}:{s.Id}", "zidoo", s.Id, zidoo.Id, "show", s.Name, s.Year, "", "")));
                if (n > 0) parts.Add($"{n:N0} from {zidoo.Name}");
            }
            else if (_store.Devices().Any(d => !d.IsServerUser && d.Enabled)) problems.Add("no Zidoo is on to read its library from");
            // Kaleidescape: its library can't be listed over its control protocol, so it's the movies Resume Relay has
            // seen it play. Last, so a movie a library above also has uses that library's (and folder's) poster.
            var seen = _store.Kaleidescape().Seen;
            int nk = seen.Count(t => Add(new FeatureEntry($"kaleidescape:{t.Handle}", "kaleidescape", 0, "", "movie", t.Title, t.Year, t.BestCover, "")));
            if (nk > 0) parts.Add($"{nk:N0} seen on a Kaleidescape");
            _featureTitles = list.OrderBy(e => SortName(e.Title), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Year).ToList();
            _featureAt = DateTime.UtcNow;
            _featureNote = (list.Count == 0 ? "No titles found" : $"{list.Count:N0} title{(list.Count == 1 ? "" : "s")}{(parts.Count > 1 ? " (" + string.Join(", ", parts) + ")" : "")}")
                + (problems.Count > 0 ? ". Not read: " + string.Join("; ", problems) : "") + ".";
            // Nothing read at all (a server or player off): try again on the next look, not in 10 minutes.
            if (list.Count == 0) _featureAt = DateTime.MinValue;
            return (_featureTitles, _featureNote);
        }
        finally { _featureGate.Release(); }
    }

    private async Task<FeatureEntry?> FeatureEntryAsync(string id, CancellationToken ct)
    {
        var (all, _) = await FeatureTitlesAsync(false, ct);
        return all.FirstOrDefault(e => e.Id == id) ?? (await FeatureTitlesAsync(true, ct)).Titles.FirstOrDefault(e => e.Id == id);
    }

    private IMediaServer? ServerOf(FeatureEntry e) => e.From == "plex" ? _engine.PlexServer() : e.From == "jellyfin" ? _engine.JellyfinServer() : null;
    private List<PlexMapping> MappingsOf(string from) => from == "jellyfin" ? _store.Jellyfin().Mappings : _store.Plex().Mappings;

    /// <summary>A show's seasons, in order (Specials first, as season 0). Null: the show or its library can't be read.</summary>
    public async Task<List<FeatureSeason>?> FeatureSeasonsAsync(string id, CancellationToken ct = default)
    {
        var e = await FeatureEntryAsync(id, ct);
        if (e == null || e.Kind != "show") return null;
        if (e.From == "zidoo")
        {
            var d = _store.Device(e.DeviceId);
            if (d == null) return null;
            var api = new ZidooClient(d.Ip, d.Port);
            // A one-season show is on the wall as its season (type 4); a series lists its seasons.
            var rows = await api.GetCollectionAsync(e.Key, ct);
            var seasons = rows.Where(r => r.Type == 4).ToList();
            if (seasons.Count == 0) return new() { new FeatureSeason(SeasonNumber(e.Title, 1), SeasonLabel(SeasonNumber(e.Title, 1)), e.Key) };
            return seasons.Select((r, i) => (No: SeasonNumber(r.Name, i + 1), r.Id)).OrderBy(x => x.No)
                .Select(x => new FeatureSeason(x.No, SeasonLabel(x.No), x.Id)).ToList();
        }
        var server = ServerOf(e);
        var eps = server == null ? null : await server.ShowEpisodesAsync(e.Key, ct);
        if (eps == null) return null;
        return eps.Where(x => x.Files.Count > 0).Select(x => x.Season).Distinct().OrderBy(n => n).Select(n => new FeatureSeason(n, SeasonLabel(n), 0)).ToList();
    }

    private static int SeasonNumber(string name, int fallback)
    {
        if (name.Contains("special", StringComparison.OrdinalIgnoreCase)) return 0;
        var m = SeasonNo.Match(name.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value) : fallback;
    }

    private static string SeasonLabel(int n) => n == 0 ? "Specials" : "Season " + n;

    /// <summary>
    /// Makes a title a screen's Feature Presentation: finds one of its files (a movie's own file, or an episode of the
    /// chosen season) so its poster can be found the usual way (folder → Plex/Jellyfin → Zidoo). Returns an error to
    /// show, or "" when it's saved.
    /// </summary>
    public async Task<string> ChooseFeatureAsync(string screenId, string id, int season, CancellationToken ct = default)
    {
        if (_live.Get().Screens.All(s => s.Id != screenId)) return "That screen isn't there any more.";
        var e = await FeatureEntryAsync(id, ct);
        if (e == null) return "That title isn't in the libraries any more. Open the list again.";
        var f = new FeatureTitle { From = e.From, Key = e.Key, DeviceId = e.DeviceId, Kind = e.Kind, Title = e.Title, Year = e.Year };
        if (e.Kind == "show")
        {
            var seasons = await FeatureSeasonsAsync(id, ct);
            var pick = seasons?.FirstOrDefault(s => s.Season == season);
            if (pick == null) return "That season couldn't be found. Choose the show again.";
            f.Season = pick.Season;
            f.SeasonName = pick.Name;
            if (e.From == "zidoo")
            {
                var d = _store.Device(e.DeviceId);
                f.SourceUrl = d == null ? "" : await ZidooRatings.FirstFileAsync(new ZidooClient(d.Ip, d.Port), pick.ZidooId, ct);
            }
            else if (ServerOf(e) is { } server && await server.ShowEpisodesAsync(e.Key, ct) is { } eps)
            {
                var ep = eps.Where(x => x.Season == pick.Season && x.Files.Count > 0).OrderBy(x => x.Episode).FirstOrDefault();
                if (ep != null)
                {
                    f.ServerFile = ep.Files[0];
                    f.SourceUrl = PlexPaths.ToSourceUrl(ep.Files[0], MappingsOf(e.From)) ?? "";
                    f.Thumb = ep.ParentThumb;   // the season's poster
                }
            }
        }
        else if (e.From == "zidoo")
        {
            var d = _store.Device(e.DeviceId);
            f.SourceUrl = d == null ? "" : await ZidooRatings.FirstFileAsync(new ZidooClient(d.Ip, d.Port), e.Key, ct);
        }
        else if (e.From == "kaleidescape")
        {
            // Its own cover is its poster (no file anywhere).
            f.SourceUrl = PosterFinder.KaleidescapePrefix + e.Id["kaleidescape:".Length..];
            f.Thumb = e.Thumb;
        }
        else
        {
            f.ServerFile = e.File;
            f.SourceUrl = e.File.Length > 0 ? PlexPaths.ToSourceUrl(e.File, MappingsOf(e.From)) ?? "" : "";
            f.Thumb = e.Thumb;
        }
        if (f.SourceUrl.Length == 0 && f.ServerFile.Length == 0 && f.Thumb.Length == 0) return "No file of that title could be found to find its poster from.";
        bool found = false;
        _live.Update(c => { if (c.Screens.FirstOrDefault(s => s.Id == screenId) is { } s) { s.Feature = f; found = true; } });
        if (!found) return "That screen isn't there any more.";
        // The next round has the new title (the round up keeps playing until it's made).
        RuntimeOf(screenId).WantNext = true;
        _ = PosterTask(FeatureRequest(f));
        return "";
    }

    public void ClearFeature(string screenId)
    {
        _live.Update(c => { if (c.Screens.FirstOrDefault(s => s.Id == screenId) is { } s) s.Feature = null; });
        RuntimeOf(screenId).WantNext = true;
    }

    /// <summary>What the poster finder is given for a Feature Presentation: a movie's poster, or the season's.</summary>
    public static PosterRequest FeatureRequest(FeatureTitle f) =>
        f.From == "kaleidescape"
            ? new(f.SourceUrl, "", "", false, 0, false, f.Display, CoverUrl: f.Thumb)
            : new(f.SourceUrl, f.ServerFile, f.From == "zidoo" ? f.DeviceId : "", f.Kind == "show", f.Kind == "show" ? f.Season : 0, false, f.Display,
                0, f.Thumb, f.From == "jellyfin" ? "jellyfin" : "");

    /// <summary>The poster a screen's Feature Presentation shows (for its card), once it's been found.</summary>
    public async Task<PosterResult?> FeaturePosterAsync(string screenId, CancellationToken ct = default)
    {
        var f = _live.Get().Screens.FirstOrDefault(s => s.Id == screenId)?.Feature;
        if (f == null) return null;
        var t = PosterTask(FeatureRequest(f));
        try { return await t.WaitAsync(TimeSpan.FromSeconds(25), ct); }
        catch (TimeoutException) { return null; }
    }
}
