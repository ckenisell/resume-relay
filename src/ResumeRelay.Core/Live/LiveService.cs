using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResumeRelay.Live;

/// <summary>
/// Decides what every screen shows, about once a second:
///  • something is playing (or paused) on one of its players → that title's poster over the Now Playing background;
///    after a stop it stays up for the "wait after a stop" time;
///  • otherwise the slideshow: Coming Soon, Recently Watched and Highly Acclaimed take turns (each on/off, each with its
///    own background), 10 Highly Acclaimed titles per round so a big library doesn't crowd out the others.
/// A screen app asks for its state every couple of seconds and plays the video it's given; when a slideshow round
/// ends it says so and gets the next round.
/// It also answers screen apps looking for Resume Relay on the network (a UDP broadcast on port 47090).
/// </summary>
public sealed class LiveService : BackgroundService
{
    public const int DiscoveryPort = 47090;
    private const int AcclaimedPerRound = 10;
    private static readonly Regex EpisodeTag = new(@"\bS(\d{1,2})E\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Store _store;
    private readonly LiveStore _live;
    private readonly RelayEngine _engine;
    private readonly PosterFinder _finder;
    private readonly Renderer _renderer;
    private readonly ConcurrentDictionary<string, ScreenRuntime> _rt = new();
    private readonly ConcurrentDictionary<string, PairRequest> _pairs = new();
    private readonly ConcurrentDictionary<string, DateTime> _started = new();      // device|file → when it started playing
    private readonly ConcurrentDictionary<string, Task<PosterResult?>> _posters = new();
    private List<SlideItem> _recentAll = new(), _acclaimed = new();
    private DateTime _acclaimedAt = DateTime.MinValue, _recentAt = DateTime.MinValue;
    private string _acclaimedNote = "";
    private readonly SemaphoreSlim _acclaimedGate = new(1, 1);
    private readonly int _salt = Random.Shared.Next();

    public LiveService(Store store, LiveStore live, RelayEngine engine, PosterFinder finder, Renderer renderer)
    {
        _store = store;
        _live = live;
        _engine = engine;
        _finder = finder;
        _renderer = renderer;
    }

    public Renderer Renderer => _renderer;
    public PosterFinder Finder => _finder;
    public string AcclaimedNote => _acclaimedNote;

    // ------------------------------------------------------------------ runtime state

    public sealed class ScreenRuntime
    {
        public DateTime? LastSeenUtc;
        public string Mode = "starting";        // playing | idle | test | waiting
        public string Url = "";                 // the render file name
        public bool Loop = true;
        public string Title = "";
        public string PosterSource = "";
        public string Note = "";
        // Now Playing
        public NowItem? Shown;
        public DateTime? StoppedUtc;
        // Slideshow
        public int Round;
        public string IdleUrl = "";
        public string IdleKey = "";             // what the current reel was made from
        public string BuildingKey = "";
        public List<string> RoundTitles = new();
        public bool AdvanceAfterEnd;
        public bool WantNext;
        /// <summary>The slideshow up is the start of a round still being made.</summary>
        public bool Partial;
        /// <summary>While a round is being made: slides ready, and how many it will have (for the card's progress bar).</summary>
        public int MadeSlides, TotalSlides;
        public DateTime TestUntil;
        /// <summary>A test picture was asked for: it shows for this long once it's made (making it can take a moment).</summary>
        public int TestSeconds;
        /// <summary>What the screen app last said about playing (for the dashboard): "playing", or an error.</summary>
        public string AppSays = "";
        public DateTime AppSaysUtc;
    }

    public sealed record NowItem(string DeviceId, string Player, string SourceUrl, string PlexFile, string Title, bool IsEpisode, int Season, int PlexKey, bool Paused);

    public sealed record SlideItem(string Set, string Title, PosterRequest? Request, string? File)
    {
        public double Score { get; init; }
        public string Source { get; init; } = "";
        public string Kind { get; init; } = "";
    }

    public sealed class PairRequest
    {
        public string DeviceKey = "", Code = "", Platform = "", Model = "", Version = "";
        public DateTime SeenUtc;
    }

    public ScreenRuntime RuntimeOf(string screenId) => _rt.GetOrAdd(screenId, _ => new ScreenRuntime());
    public IReadOnlyCollection<PairRequest> Pairs() => _pairs.Values.Where(p => DateTime.UtcNow - p.SeenUtc < TimeSpan.FromMinutes(2)).ToList();

    // ------------------------------------------------------------------ screen apps

    /// <summary>A screen app says hello. Paired: its token. Not yet: a 4-digit code to enter on the Live Poster tab.</summary>
    public (LiveScreen? Screen, PairRequest? Pending) Hello(string deviceKey, string platform, string model, string version)
    {
        deviceKey = deviceKey.Trim();
        var screen = _live.Get().Screens.FirstOrDefault(s => s.DeviceKey == deviceKey);
        if (screen != null)
        {
            if ((platform.Length > 0 && screen.Platform != platform) || (model.Length > 0 && screen.Model != model) || (version.Length > 0 && screen.AppVersion != version))
                _live.Update(c =>
                {
                    if (c.Screens.FirstOrDefault(s => s.Id == screen.Id) is not { } s) return;
                    if (platform.Length > 0) s.Platform = platform;
                    if (model.Length > 0) s.Model = model;
                    if (version.Length > 0) s.AppVersion = version;
                });
            RuntimeOf(screen.Id).LastSeenUtc = DateTime.UtcNow;
            _pairs.TryRemove(deviceKey, out _);
            return (screen, null);
        }
        var p = _pairs.GetOrAdd(deviceKey, k => new PairRequest { DeviceKey = k, Code = NewCode() });
        p.Platform = platform; p.Model = model; p.Version = version; p.SeenUtc = DateTime.UtcNow;
        return (null, p);
    }

    private string NewCode()
    {
        for (int i = 0; i < 50; i++)
        {
            string c = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
            if (_pairs.Values.All(p => p.Code != c)) return c;
        }
        return RandomNumberGenerator.GetInt32(1000, 10000).ToString();
    }

    /// <summary>Pairs the screen showing this code.</summary>
    public (LiveScreen? Screen, string Error) Pair(string code, string name, List<string> players, string mount, string resolution)
    {
        var p = Pairs().FirstOrDefault(x => x.Code == code.Trim());
        if (p == null) return (null, "No screen is showing that code. Open the Resume Relay app on the screen and use the code it shows.");
        var screen = new LiveScreen
        {
            Name = name.Trim().Length > 0 ? name.Trim() : "Screen",
            DeviceKey = p.DeviceKey, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Platform = p.Platform, Model = p.Model, AppVersion = p.Version, Players = players.Distinct().ToList(),
            Mount = mount is "portrait" or "left" or "right" ? mount : p.Platform is "android-phone" or "web-portrait" ? "portrait" : "right",
            Resolution = resolution == "1080" ? "1080" : "4k",
        };
        _live.Update(c => { c.Screens.RemoveAll(s => s.DeviceKey == p.DeviceKey); c.Screens.Add(screen); });
        _pairs.TryRemove(p.DeviceKey, out _);
        RuntimeOf(screen.Id).TestSeconds = 30; // show the "this way up" picture first
        _store.Log(ActivityLevel.Ok, $"Live Poster: paired the screen \"{screen.Name}\" ({screen.Platform}{(screen.Model.Length > 0 ? ", " + screen.Model : "")}).");
        return (screen, "");
    }

    /// <summary>A screen app reporting how playback is going (shown on its card, to see problems from the dashboard).</summary>
    public bool Report(string token, string msg)
    {
        var screen = _live.Get().Screens.FirstOrDefault(s => s.Token.Length > 0 && s.Token == token);
        if (screen == null) return false;
        var rt = RuntimeOf(screen.Id);
        rt.AppSays = msg.Length > 200 ? msg[..200] : msg;
        rt.AppSaysUtc = DateTime.UtcNow;
        if (msg.Contains("error", StringComparison.OrdinalIgnoreCase))
            _store.Log(ActivityLevel.Warn, $"Live Poster: the screen \"{screen.Name}\" couldn't play its video: {rt.AppSays}");
        return true;
    }

    /// <summary>What a screen should play now. Null: unknown token (unpaired or removed).</summary>
    public object? StateFor(string token, string ended)
    {
        var screen = _live.Get().Screens.FirstOrDefault(s => s.Token.Length > 0 && s.Token == token);
        if (screen == null) return null;
        var rt = RuntimeOf(screen.Id);
        rt.LastSeenUtc = DateTime.UtcNow;
        if (ended.Length > 0 && rt.Mode == "idle" && ended == rt.IdleUrl) rt.AdvanceAfterEnd = true;
        return new
        {
            Paired = true, screen.Name, rt.Mode, rt.Loop, rt.Title,
            Url = rt.Url.Length > 0 ? "/live/media/" + rt.Url : "",
            Version = rt.Url, Poll = 2000,
        };
    }

    // ------------------------------------------------------------------ main loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _renderer.InitAsync(ct);
        _renderer.TidyReels(Array.Empty<string>(), all: true);
        var tidyReels = DateTime.UtcNow;
        _ = Task.Run(() => _renderer.RunQueueAsync(ct), ct);
        _ = Task.Run(() => DiscoveryAsync(ct), ct);
        var tidy = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _live.Get();
                RefreshRecent(cfg);
                if (cfg.Acclaimed.Enabled && DateTime.UtcNow - _acclaimedAt > TimeSpan.FromHours(1)) _ = RefreshAcclaimedAsync(ct);
                foreach (var s in cfg.Screens) Update(s, cfg);
                foreach (var id in _rt.Keys.Where(k => cfg.Screens.All(s => s.Id != k)).ToList()) _rt.TryRemove(id, out _);
                if (DateTime.UtcNow - tidy > TimeSpan.FromHours(6)) { tidy = DateTime.UtcNow; _renderer.Tidy(); }
                if (DateTime.UtcNow - tidyReels > TimeSpan.FromMinutes(5))
                {
                    tidyReels = DateTime.UtcNow;
                    _renderer.TidyReels(_rt.Values.SelectMany(r => new[] { r.Url, r.IdleUrl }).Where(u => u.Length > 0).ToHashSet());
                }
            }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, "Live Poster: " + ex.Message); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Update(LiveScreen s, LiveConfig cfg)
    {
        var rt = RuntimeOf(s.Id);
        if (!_renderer.Ready) { rt.Mode = "waiting"; rt.Url = ""; rt.Note = _renderer.Status; return; }

        if (rt.TestSeconds > 0)
        {
            var ready = _renderer.TestPatternAsync(s.Mount, s.Resolution, CodecOf(s));
            if (ready.IsCompleted) { rt.TestUntil = DateTime.UtcNow.AddSeconds(rt.TestSeconds); rt.TestSeconds = 0; }
        }
        if (rt.TestUntil > DateTime.UtcNow)
        {
            var test = _renderer.TestPatternAsync(s.Mount, s.Resolution, CodecOf(s));
            if (test.IsCompletedSuccessfully && test.Result != null) { Show(rt, "test", test.Result, loop: true, "Test picture: the arrow should point up"); return; }
        }

        // ---- Now Playing
        var now = NowPlayingFor(s, rt, cfg);
        if (now != null && cfg.NowPlaying.Enabled)
        {
            rt.MadeSlides = rt.TotalSlides = 0;
            var bg = Background(cfg, cfg.NowPlaying.BackgroundId);
            var req = new PosterRequest(now.SourceUrl, now.PlexFile, now.DeviceId, now.IsEpisode, now.Season, false, now.Title, now.PlexKey);
            var poster = PosterTask(req);
            if (poster.IsCompleted)
            {
                var found = poster.IsCompletedSuccessfully ? poster.Result : null;
                var video = _renderer.HoldAsync(bg, found?.Path, s.Mount, s.Resolution, CodecOf(s), priority: 0);
                if (video.IsCompletedSuccessfully && video.Result != null)
                {
                    rt.PosterSource = found?.Source ?? "no poster found";
                    Show(rt, "playing", video.Result, loop: true, now.Title);
                    return;
                }
            }
            // While the poster is found and made: the background alone (made once).
            var plain = _renderer.HoldAsync(bg, null, s.Mount, s.Resolution, CodecOf(s), priority: 0);
            if (plain.IsCompletedSuccessfully && plain.Result != null) { rt.PosterSource = "finding the poster…"; Show(rt, "playing", plain.Result, loop: true, now.Title); }
            return;
        }

        // ---- Slideshow
        Slideshow(s, rt, cfg);
    }

    /// <summary>The video format a screen gets: HEVC (Rokus play 4K only as HEVC; every 4K TV and stick plays it), or
    /// H.264 for web browsers (most can't play HEVC).</summary>
    public static string CodecOf(LiveScreen s) =>
        s.Codec is "hevc" or "h264" ? s.Codec : s.Platform.StartsWith("web", StringComparison.Ordinal) ? "h264" : "hevc";

    private void Show(ScreenRuntime rt, string mode, string file, bool loop, string title)
    {
        rt.Mode = mode;
        rt.Url = Path.IsPathRooted(file) ? _renderer.Rel(file) : file;   // the slideshow's is already relative
        rt.Loop = loop;
        rt.Title = title;
    }

    /// <summary>What a screen's players are playing: the title already up stays while it plays (or is paused);
    /// otherwise the most recently started. After a stop it's kept for the wait time.</summary>
    private NowItem? NowPlayingFor(LiveScreen s, ScreenRuntime rt, LiveConfig cfg)
    {
        var devices = _store.Devices().ToDictionary(d => d.Id);
        var playing = new List<NowItem>();
        foreach (var id in s.Players)
        {
            if (!devices.TryGetValue(id, out var d)) continue;
            var p = _engine.LiveOf(id).Playing;
            if (p == null || (p.SourceUrl.Length == 0 && p.Path.Length == 0)) continue;
            string file = p.SourceUrl.Length > 0 ? p.SourceUrl : p.Path;
            _started.TryAdd(id + "|" + file, DateTime.UtcNow);
            var tag = EpisodeTag.Match(p.Subtitle + " " + PathMapper.FileName(p.Path) + " " + PathMapper.FileName(p.SourceUrl));
            playing.Add(new NowItem(id, d.Name, p.SourceUrl, d.IsPlex ? p.Path : "", p.Title, tag.Success, tag.Success ? int.Parse(tag.Groups[1].Value) : 0, p.PlexKey, p.Paused));
        }
        foreach (var k in _started.Keys.Where(k => s.Players.Any(id => k.StartsWith(id + "|")) && !playing.Any(x => k == x.DeviceId + "|" + (x.SourceUrl.Length > 0 ? x.SourceUrl : x.PlexFile))).ToList())
            _started.TryRemove(k, out _);

        NowItem? pick = null;
        if (rt.Shown != null) pick = playing.FirstOrDefault(x => x.DeviceId == rt.Shown.DeviceId && Same(x, rt.Shown));
        pick ??= playing.OrderBy(x => x.Paused).ThenByDescending(x => _started.GetValueOrDefault(x.DeviceId + "|" + (x.SourceUrl.Length > 0 ? x.SourceUrl : x.PlexFile))).FirstOrDefault();
        if (pick != null) { rt.Shown = pick; rt.StoppedUtc = null; return pick; }
        if (rt.Shown != null)
        {
            rt.StoppedUtc ??= DateTime.UtcNow;
            if (DateTime.UtcNow - rt.StoppedUtc.Value < TimeSpan.FromSeconds(Math.Max(0, cfg.StopWaitSeconds))) return rt.Shown;
            rt.Shown = null; rt.StoppedUtc = null;
        }
        return null;
    }

    private static bool Same(NowItem a, NowItem b) =>
        a.SourceUrl.Length > 0 ? PathMapper.Same(a.SourceUrl, b.SourceUrl) : a.PlexFile == b.PlexFile && a.PlexKey == b.PlexKey;

    private Task<PosterResult?> PosterTask(PosterRequest r)
    {
        string key = $"{r.SourceUrl}|{r.PlexFile}|{r.SeriesOnly}|{r.Season}|{r.PlexKey}|{r.PlexThumb}";
        var t = _posters.GetOrAdd(key, _ => Task.Run(() => _finder.FindAsync(r)));
        if (t.IsCompleted && (t.IsFaulted || t.Result == null || !File.Exists(t.Result.Path)) && !_posterRetry.ContainsKey(key))
        {
            // Not found: look again in a few minutes (a player may have been off, or a poster added).
            _posterRetry[key] = DateTime.UtcNow;
        }
        if (_posterRetry.TryGetValue(key, out var at) && DateTime.UtcNow - at > TimeSpan.FromMinutes(5))
        {
            _posterRetry.TryRemove(key, out _);
            _posters.TryRemove(key, out _);
        }
        return t;
    }
    private readonly ConcurrentDictionary<string, DateTime> _posterRetry = new();

    public void ForgetPosters() { _posters.Clear(); _posterRetry.Clear(); _finder.Forget(); }

    private LiveBackground? Background(LiveConfig cfg, string id) =>
        cfg.Backgrounds.FirstOrDefault(b => b.Id == id && File.Exists(_live.BackgroundPath(b)))
        ?? cfg.Backgrounds.FirstOrDefault(b => b.BuiltIn && File.Exists(_live.BackgroundPath(b)));

    // ------------------------------------------------------------------ slideshow

    /// <summary>The titles of one slideshow round, in order: Coming Soon, Recently Watched (the screen's players),
    /// then the next 10 Highly Acclaimed. Only sets that are on.</summary>
    public List<SlideItem> RoundItems(LiveScreen s, LiveConfig cfg, int round)
    {
        var sets = new List<List<SlideItem>>();
        if (cfg.ComingSoon.Enabled)
            sets.Add(cfg.Coming.Where(c => File.Exists(_live.ComingPath(c))).Select(c => new SlideItem("coming", c.Title, null, _live.ComingPath(c))).ToList());
        if (cfg.RecentlyWatched.Enabled)
        {
            var names = _store.Devices().Where(d => s.Players.Contains(d.Id)).Select(d => d.Name).ToHashSet();
            sets.Add(RecentFor(s.Players, names));
        }
        if (cfg.Acclaimed.Enabled && _acclaimed.Count > 0)
        {
            // 10 picked at random for each round: any title can come up again in a later round. The pick depends only on
            // the screen and the round, so it stays the same while the round is being made and played.
            var rnd = new Random(HashCode.Combine(s.Id, round, _salt));
            var all = _acclaimed;
            sets.Add(Enumerable.Range(0, all.Count).OrderBy(_ => rnd.Next()).Take(AcclaimedPerRound).Select(i => all[i]).ToList());
        }
        // The sets take turns, one poster each (Coming Soon 1, Recently Watched 1, Highly Acclaimed 1, Coming Soon 2…);
        // a set that runs out drops out of the turns.
        var items = new List<SlideItem>();
        for (int i = 0; sets.Any(x => i < x.Count); i++)
            foreach (var set in sets) if (i < set.Count) items.Add(set[i]);
        return items;
    }

    /// <summary>"Rebuild slideshow": the next round is made now (with any new Coming Soon posters) and goes up as soon
    /// as it's ready, without waiting for the current round to finish.</summary>
    public void Rebuild(string screenId)
    {
        _recentAt = DateTime.MinValue;
        var rt = RuntimeOf(screenId);
        rt.Round++;
        rt.WantNext = true;
        // The round up now may be replaced by the new one's first slides as soon as they're ready (not only at its end).
        rt.Partial = true;
        rt.RoundTitles = new();
    }

    private void Slideshow(LiveScreen s, ScreenRuntime rt, LiveConfig cfg)
    {
        // The screen finished a round: the next one goes up as soon as it's made (the screen replays this one meanwhile).
        // (After a partial round, the same round comes back complete, or longer, instead of a new one.)
        if (rt.AdvanceAfterEnd) { rt.AdvanceAfterEnd = false; if (!rt.Partial) rt.Round++; rt.WantNext = true; }
        var items = RoundItems(s, cfg, rt.Round);
        // Each title's poster and slide; the round's reel once they're all made.
        var slides = new List<string>();
        bool pending = false;
        var titles = new List<string>();
        // The slides ready from the start of the round, in order (up to the first one still being made).
        var firstSlides = new List<string>();
        var firstTitles = new List<string>();
        foreach (var it in items)
        {
            string? poster = it.File;
            if (poster == null && it.Request != null)
            {
                var t = PosterTask(it.Request);
                if (!t.IsCompleted) { pending = true; continue; }
                poster = t.IsCompletedSuccessfully ? t.Result?.Path : null;
            }
            if (poster == null) continue; // no poster anywhere: left out
            var setCfg = it.Set switch { "coming" => cfg.ComingSoon, "recent" => cfg.RecentlyWatched, _ => cfg.Acclaimed };
            var slide = _renderer.SlideAsync(Background(cfg, setCfg.BackgroundId), poster, s.Mount, s.Resolution, CodecOf(s), cfg.SlideSeconds);
            if (!slide.IsCompleted) { pending = true; continue; }
            if (slide.IsCompletedSuccessfully && slide.Result != null)
            {
                slides.Add(slide.Result); titles.Add(it.Title);
                if (!pending) { firstSlides.Add(slide.Result); firstTitles.Add(it.Title); }
            }
        }
        rt.MadeSlides = pending ? slides.Count : 0;
        rt.TotalSlides = pending ? slides.Count + items.Count(it => it.File != null || it.Request != null) - titles.Count : 0;
        string key = string.Join("|", slides.Select(Path.GetFileName));
        // A new reel replaces the one up only between rounds (or when none is up), never in the middle of one.
        bool canSwap = rt.IdleUrl.Length == 0 || rt.WantNext || rt.Mode != "idle";
        if (!pending && slides.Count > 0 && canSwap)
        {
            if (rt.IdleKey == key && rt.IdleUrl.Length > 0) rt.WantNext = false; // nothing new: the same round again
            else
            {
                var reel = _renderer.ReelAsync(slides);
                if (reel.IsCompletedSuccessfully && reel.Result != null)
                {
                    rt.IdleUrl = _renderer.Rel(reel.Result);
                    rt.IdleKey = key;
                    rt.RoundTitles = titles;
                    rt.WantNext = false;
                    rt.Partial = false;
                }
            }
        }
        // Still being made and nothing (or only a shorter start) is up: the first slides go up as soon as there are three,
        // rather than waiting for the whole round. The rest follows when this part ends.
        else if (pending && canSwap && (rt.IdleUrl.Length == 0 || rt.Partial) && firstSlides.Count >= Math.Min(3, items.Count)
                 && firstSlides.Count > (rt.Partial && rt.IdleUrl.Length > 0 ? rt.RoundTitles.Count : 0))
        {
            var reel = _renderer.ReelAsync(firstSlides);
            if (reel.IsCompletedSuccessfully && reel.Result != null)
            {
                rt.IdleUrl = _renderer.Rel(reel.Result);
                rt.IdleKey = string.Join("|", firstSlides.Select(Path.GetFileName));
                rt.RoundTitles = firstTitles;
                rt.WantNext = false;
                rt.Partial = true;
            }
        }
        if (rt.IdleUrl.Length > 0 && File.Exists(Path.Combine(_live.RenderDir, rt.IdleUrl)))
        {
            Show(rt, "idle", rt.IdleUrl, loop: false, $"Slideshow: {rt.RoundTitles.Count} poster{(rt.RoundTitles.Count == 1 ? "" : "s")}");
            rt.Note = pending ? "Making more slides…" : "";
            return;
        }
        // Nothing made yet (or no posters at all): a background on its own.
        var firstSet = new[] { cfg.ComingSoon, cfg.RecentlyWatched, cfg.Acclaimed }.FirstOrDefault(x => x.Enabled) ?? cfg.NowPlaying;
        var plain = _renderer.HoldAsync(Background(cfg, firstSet.BackgroundId), null, s.Mount, s.Resolution, CodecOf(s), priority: 1);
        if (plain.IsCompletedSuccessfully && plain.Result != null)
        {
            Show(rt, "idle", plain.Result, loop: true, items.Count == 0 ? "Slideshow: no posters to show yet" : "Making the slideshow…");
            rt.IdleUrl = "";
        }
    }

    /// <summary>Recently Watched: the last 10 titles stopped on these players (a TV show counts once per season).</summary>
    private List<SlideItem> RecentFor(List<string> players, HashSet<string> names) =>
        _recentAll.Where(i => i.Request != null && (players.Contains(i.Request.DeviceId) || names.Contains(i.Set["recent:".Length..])))
            .Select(i => new SlideItem("recent", i.Title, i.Request, null))
            .DistinctBy(i => i.Title).Take(10).ToList();

    private static readonly Regex SeasonDir = new(@"^(?:season|series|staffel|saison|temporada|seizoen)[ ._-]*\d+$|^specials$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A TV show's name from an episode's path: its folder, or the one above a "Season 01" folder.</summary>
    private static string ShowName(string url, string fallback)
    {
        var parts = url.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return fallback;
        string dir = Uri.UnescapeDataString(parts[^2]);
        if (SeasonDir.IsMatch(dir) && parts.Length >= 3) dir = Uri.UnescapeDataString(parts[^3]);
        return dir.Length > 0 ? dir : fallback;
    }

    private void RefreshRecent(LiveConfig cfg)
    {
        if (DateTime.UtcNow - _recentAt < TimeSpan.FromSeconds(30)) return;
        _recentAt = DateTime.UtcNow;
        var list = new List<SlideItem>();
        foreach (var e in _store.Events(300).Where(e => !e.Manual && !e.Batch && !e.Copied && e.SourceUrl.Length > 0).OrderByDescending(e => e.TimeUtc))
        {
            var tag = EpisodeTag.Match(e.Subtitle + " " + e.FileName);
            bool ep = tag.Success;
            int season = ep ? int.Parse(tag.Groups[1].Value) : 0;
            // A TV show counts once, however many episodes were watched: it's named after its folder (the show's name),
            // and shows the season poster of the episode watched last.
            string title = ep ? ShowName(e.SourceUrl, e.Title) : e.Title;
            list.Add(new SlideItem("recent:" + e.FromName, title,
                new PosterRequest(e.SourceUrl, "", e.FromDeviceId, ep, season, false, title), null));
        }
        _recentAll = list;
    }

    /// <summary>
    /// Highly Acclaimed: every movie and series rated at least the minimum. Reread hourly.
    /// With Plex: the ratings Plex keeps (IMDb when that's its ratings source), plus movies only the Zidoos have.
    /// Without Plex: the Zidoo's: IMDb for movies, TMDB for series (the HT app keeps no IMDb rating for series).
    /// </summary>
    public async Task RefreshAcclaimedAsync(CancellationToken ct = default)
    {
        if (!await _acclaimedGate.WaitAsync(0, ct)) return;
        try
        {
            _acclaimedAt = DateTime.UtcNow;
            double min = _live.Get().AcclaimedMin;
            var list = new List<SlideItem>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int fromPlex = 0, fromZidoo = 0;
            bool plexFailed = false, zidooRead = false;
            // The Zidoo's ratings first: IMDb for movies (by file), TMDB for series.
            var zidooRated = new Dictionary<string, ZidooRatings.Rated>(StringComparer.Ordinal);
            var zidooList = new List<ZidooRatings.Rated>();
            var zidoo = _store.Devices().FirstOrDefault(d => !d.IsPlex && d.Enabled && _engine.LiveOf(d.Id).Online);
            if (zidoo != null)
            {
                zidooList = await ZidooRatings.ReadAsync(new ZidooClient(zidoo.Ip, zidoo.Port), min, ct);
                zidooRead = true;
                foreach (var r in zidooList)
                    if (PlexPaths.RelOfUrl(r.SourceUrl) is { } rel) zidooRated[PlexPaths.Key(rel)] = r;
            }
            var plex = _engine.PlexServer();
            if (plex != null)
            {
                var rated = await plex.RatedTitlesAsync(ct);
                if (rated == null) plexFailed = true;
                else
                {
                    var mappings = _store.Plex().Mappings;
                    // Rotten Tomatoes ratings are never used (Rating() leaves them out); a movie Plex rates only that way
                    // comes from the Zidoo below instead.
                    foreach (var r in rated.Where(r => r.Score > 0))
                    {
                        string file = r.Files.FirstOrDefault() ?? "";
                        string url = file.Length > 0 ? PlexPaths.ToSourceUrl(file, mappings) ?? "" : "";
                        string? key = url.Length > 0 && PlexPaths.RelOfUrl(url) is { } rel ? PlexPaths.Key(rel) : null;
                        // A movie Plex rates by TMDB, that the Zidoo rates by IMDb: the Zidoo's (IMDb) wins.
                        if (r.Type == "movie" && r.Source != "IMDb" && key != null && zidooRated.ContainsKey(key)) continue;
                        if (r.Score < min - 1e-9) { if (key != null && r.Source == "IMDb") keys.Add(key); continue; }
                        if (r.Type == "show") { file = await plex.AnyEpisodeFileAsync(r.Key, ct) ?? ""; url = file.Length > 0 ? PlexPaths.ToSourceUrl(file, mappings) ?? "" : ""; }
                        string title = r.Year > 0 && r.Type == "movie" ? $"{r.Title} ({r.Year})" : r.Title;
                        if (key != null) keys.Add(key);
                        if (r.Type == "show" && url.Length > 0 && PlexPaths.RelOfUrl(url) is { } srel) keys.Add(PlexPaths.Key(srel));
                        keys.Add(r.Type + "|" + r.Title.ToLowerInvariant());
                        list.Add(new SlideItem("acclaimed", title, new PosterRequest(url, file, "", false, 0, r.Type == "show", title, r.Key, r.Thumb), null)
                            { Score = r.Score, Source = r.Source, Kind = r.Type });
                        fromPlex++;
                    }
                }
            }
            // Then the Zidoo's: everything when there's no Plex, else what Plex doesn't rate (or rates only by TMDB).
            if (zidoo != null)
                foreach (var r in zidooList)
                {
                    string? rel = PlexPaths.RelOfUrl(r.SourceUrl);
                    if ((rel != null && keys.Contains(PlexPaths.Key(rel))) || keys.Contains(r.Kind + "|" + r.Title.ToLowerInvariant())) continue;
                    string title = r.Year > 0 && r.Kind == "movie" ? $"{r.Title} ({r.Year})" : r.Title;
                    list.Add(new SlideItem("acclaimed", title, new PosterRequest(r.SourceUrl, "", zidoo.Id, false, 0, r.Kind == "show", title), null)
                        { Score = r.Score, Source = r.Source, Kind = r.Kind });
                    fromZidoo++;
                }
            if (plexFailed && !zidooRead) { _acclaimedNote = "The Plex server didn't answer; trying again in 10 minutes."; _acclaimedAt = DateTime.UtcNow.AddMinutes(-50); return; }
            if (plex == null && !zidooRead) { _acclaimedNote = "No player is on to read ratings from; trying again in 10 minutes."; _acclaimedAt = DateTime.UtcNow.AddMinutes(-50); return; }
            _acclaimed = list.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            string where = fromPlex > 0 && fromZidoo > 0 ? $" ({fromPlex:N0} rated by Plex, {fromZidoo:N0} by the Zidoo)"
                : fromZidoo > 0 ? " (from the Zidoo: IMDb for movies, TMDB for series)" : "";
            _acclaimedNote = $"{list.Count:N0} title{(list.Count == 1 ? "" : "s")} rated {min:0.0} or higher{where}. 10 picked at random each round.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _acclaimedNote = "Reading the ratings failed: " + ex.Message; }
        finally { _acclaimedGate.Release(); }
    }

    public IReadOnlyList<SlideItem> Acclaimed => _acclaimed;

    // ------------------------------------------------------------------ discovery

    /// <summary>Screen apps broadcast "RESUMERELAY_DISCOVER" to port 47090; Resume Relay answers with its address.</summary>
    private async Task DiscoveryAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("RESUMERELAY_NO_DISCOVERY") == "1") return;
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch (Exception ex) { _store.Log(ActivityLevel.Warn, $"Live Poster: screens can't find Resume Relay by themselves (UDP port {DiscoveryPort}: {ex.Message}). Type this computer's address into the screen app instead."); udp?.Dispose(); return; }
        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(ct);
                    string msg = Encoding.UTF8.GetString(r.Buffer).Trim();
                    if (!msg.StartsWith("RESUMERELAY_DISCOVER", StringComparison.Ordinal)) continue;
                    int port = int.TryParse(Environment.GetEnvironmentVariable("RESUMERELAY_PORT"), out var p) ? p : 8090;
                    var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { app = "Resume Relay", port, name = Environment.MachineName, version = RelayHost.Version }));
                    await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(500, CancellationToken.None); }
            }
        }
    }
}
