using System.Text.Json;

namespace ResumeRelay.Live;

/// <summary>
/// Highly Acclaimed without Plex: the ratings the Zidoo's HT app keeps.
///  • Movies: the poster wall's "voteAverage", which is the IMDb rating (getDetail: displayRating, displayRatingType 2,
///    with IMDb's vote count). Movies inside collections are read from their collection.
///  • TV series: HT 5.1.x keeps no IMDb rating for series (displayRating is 0), only TMDB's: getDetail's "tv" section.
/// One player's library is read; a title's file path is looked up only when it's rated high enough.
/// </summary>
public static class ZidooRatings
{
    public sealed record Rated(int Id, string Kind, string Title, int Year, double Score, string Source, string SourceUrl);

    public static async Task<List<Rated>> ReadAsync(ZidooClient api, double min, CancellationToken ct)
    {
        // 1. The poster wall, page by page (some players repeat the first page instead of ending).
        var tiles = new List<LibraryRow>();
        var seen = new HashSet<int>();
        for (int page = 1; page <= 200; page++)
        {
            var rows = await api.GetVideoListPageAsync(page, 500, ct);
            if (rows == null || rows.Count == 0 || rows.All(r => seen.Contains(r.Id))) break;
            foreach (var r in rows) if (seen.Add(r.Id)) tiles.Add(r);
            if (rows.Count < 500) break;
        }

        // 2. Movies (opening collections) and series.
        var movies = new List<LibraryRow>();
        var series = new List<LibraryRow>();
        async Task Expand(LibraryRow r, int depth)
        {
            switch (r.Type)
            {
                case 1: movies.Add(r); break;
                case 3 or 4: series.Add(r); break;   // a series, or a one-season show on the wall as its season
                case 2 or 6 when depth < 3:
                    var members = await api.GetCollectionAsync(r.Id, ct);
                    if (members.Count == 0 && r.CollectionId > 0) members = await api.GetCollectionAsync(r.CollectionId, ct);
                    foreach (var m in members) await Expand(m, depth + 1);
                    break;
            }
        }
        foreach (var t in tiles) await Expand(t, 0);

        var result = new List<Rated>();
        foreach (var m in movies.DistinctBy(m => m.Id).Where(m => m.Rating >= min - 1e-9))
        {
            string url = await FirstFileAsync(api, m.Id, ct);
            if (url.Length > 0) result.Add(new Rated(m.Id, "movie", m.Name, m.Year, m.Rating, "IMDb", url));
        }
        foreach (var sr in series.DistinctBy(x => x.Id))
        {
            var (score, name) = await SeriesRatingAsync(api, sr.Id, ct);
            if (score < min - 1e-9) continue;
            string url = await FirstFileAsync(api, sr.Id, ct);
            if (url.Length > 0) result.Add(new Rated(sr.Id, "show", name.Length > 0 ? name : sr.Name, sr.Year, score, "TMDB", url));
        }
        return result;
    }

    /// <summary>A series' TMDB rating and name, from its details ("tv" section). (0, "") when there's none.</summary>
    private static async Task<(double Score, string Name)> SeriesRatingAsync(ZidooClient api, int id, CancellationToken ct)
    {
        using var doc = await api.GetDetailAsync(id, ct);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return (0, "");
        if (doc.RootElement.TryGetProperty("tv", out var tv) && tv.ValueKind == JsonValueKind.Object)
        {
            double imdb = J.Dbl(tv, "displayRating");   // used if a later HT app fills it in for series too
            return (imdb > 0 ? imdb : J.Dbl(tv, "voteAverage"), J.Str(tv, "name"));
        }
        if (doc.RootElement.TryGetProperty("aggregation", out var ag) && ag.ValueKind == JsonValueKind.Object)
            return (J.Dbl(ag, "voteAverage"), J.Str(ag, "name"));
        return (0, "");
    }

    /// <summary>The Zidoos' path of one of a title's files: for a movie its main file (the one highest in the folder,
    /// not an extra in a subfolder), for a series any episode. "" when none is found.</summary>
    private static async Task<string> FirstFileAsync(ZidooClient api, int id, CancellationToken ct)
    {
        for (int depth = 0, cur = id; depth < 4 && cur > 0; depth++)
        {
            var kids = await api.EntryChildrenAsync(cur, ct);
            var files = kids.Where(k => k.Path.Contains("://")).Select(k => k.Path).ToList();
            if (files.Count > 0) return files.OrderBy(f => f.Count(c => c == '/')).First();
            cur = kids.Where(k => k.Id > 0).Select(k => k.Id).FirstOrDefault();
        }
        return "";
    }
}
