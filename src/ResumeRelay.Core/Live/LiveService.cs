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
///  • otherwise the slideshow: Coming Soon, Recently Watched, Highly Acclaimed and Feature Presentation take turns (each
///    on/off per screen, each with its own background), 10 Highly Acclaimed titles per round so a big library doesn't
///    crowd out the others, and the screen's one Feature Presentation title once per round.
/// A screen app asks for its state every couple of seconds and plays the video it's given; when a slideshow round
/// ends it says so and gets the next round.
/// It also answers screen apps looking for Resume Relay on the network (a UDP broadcast on port 47090).
/// </summary>
public sealed partial class LiveService : BackgroundService
{
    public const int DiscoveryPort = 47090;
    private const int AcclaimedPerRound = 10;
    private static readonly Regex EpisodeTag = new(@"\bS(\d{1,2})E\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Store _store;
    private readonly LiveStore _live;
    private readonly RelayEngine _engine;
    private readonly PosterFinder _finder;
    private readonly Renderer _renderer;
    private readonly KaleidescapeService _kaleidescape;
    private readonly AppleTvService _appleTv;
    private readonly ConcurrentDictionary<string, ScreenRuntime> _rt = new();
    private readonly ConcurrentDictionary<string, PairRequest> _pairs = new();
    private readonly ConcurrentDictionary<string, DateTime> _started = new();      // device|file → when it started playing
    private readonly ConcurrentDictionary<string, Task<PosterResult?>> _posters = new();
    private List<SlideItem> _recentAll = new(), _acclaimed = new();
    private DateTime _acclaimedAt = DateTime.MinValue, _recentAt = DateTime.MinValue;
    private string _acclaimedNote = "";
    private readonly SemaphoreSlim _acclaimedGate = new(1, 1);
    private readonly int _salt = Random.Shared.Next();

    public LiveService(Store store, LiveStore live, RelayEngine engine, PosterFinder finder, Renderer renderer, KaleidescapeService kaleidescape, AppleTvService appleTv)
    {
        _kaleidescape = kaleidescape;
        _appleTv = appleTv;
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
        public string Mode = "starting";        // playing | idle | making | test | waiting | off
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
        /// <summary>The titles of the round up now, with the set each came from (the card's "See the titles").</summary>
        public List<SlideItem> RoundShown = new();
        public bool AdvanceAfterEnd;
        /// <summary>How many rounds the slideshow video being made (or up) holds.</summary>
        public int RoundsUp = 1;
        public bool WantNext;
        /// <summary>"Rebuild slideshow" was pressed: the round is made even while the screen is off.</summary>
        public bool ForceBuild;
        /// <summary>While a slideshow video is being made (null otherwise): its two steps, for the card and the
        /// screen's Loading bar.</summary>
        public Building? Making;
        public DateTime TestUntil;
        /// <summary>A test picture was asked for: it shows for this long once it's made (making it can take a moment).</summary>
        public int TestSeconds;
        /// <summary>What the screen app last said about playing (for the dashboard): "playing", or an error.</summary>
        public string AppSays = "";
        public DateTime AppSaysUtc;
        /// <summary>The video that was being handed out when the app last said "playing" (so the card only says
        /// "playing" about the video that's actually up).</summary>
        public string AppPlaying = "";
    }

    /// <summary>
    /// How far along the next slideshow video is. Step 1 makes each poster into a slide; step 2 makes the dissolves
    /// between them and joins everything into one video. Counts only go up within one build (a build is one round
    /// number), so the card never jumps back.
    /// </summary>
    public sealed class Building
    {
        public int Round;
        public int Step = 1;
        public int Slides, SlidesTotal;
        public int Dissolves, DissolvesTotal;
        public bool Joining;
        /// <summary>The slideshow already up keeps playing until this one is ready.</summary>
        public bool ShowingOld;
        public double Fraction;
        public int SecondsLeft;
    }

    public sealed record NowItem(string DeviceId, string Player, string SourceUrl, string PlexFile, string Title, bool IsEpisode, int Season, int PlexKey, bool Paused, string CoverUrl = "");

    public sealed record SlideItem(string Set, string Title, PosterRequest? Request, string? File)
    {
        public double Score { get; init; }
        public string Source { get; init; } = "";
        public string Kind { get; init; } = "";
        /// <summary>Highly Acclaimed: where the rating came from ("plex" or "zidoo").</summary>
        public string From { get; init; } = "";
        /// <summary>Recently Watched: when it was stopped, and on which player.</summary>
        public DateTime TimeUtc { get; init; }
        public string Player { get; init; } = "";
        public string PlayerKind { get; init; } = "";
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
            Sets = SetKeys.ToList(),
        };
        _live.Update(c => { c.Screens.RemoveAll(s => s.DeviceKey == p.DeviceKey); c.Screens.Add(screen); });
        _pairs.TryRemove(p.DeviceKey, out _);
        RuntimeOf(screen.Id).TestSeconds = 30; // show the "this way up" picture first
        RuntimeOf(screen.Id).LastSeenUtc = DateTime.UtcNow; // its app is asking for a code right now: it's on
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
        rt.AppPlaying = msg == "playing" ? rt.Url : "";
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
            // While the first slideshow is being made (mode "making"): how far along it is (0 to 1), for the screen's
            // "Loading…" bar, and which way the screen is mounted, so the app can turn its words to match the posters.
            Progress = rt.Mode == "making" && rt.Making != null ? Math.Round(rt.Making.Fraction, 3) : 0,
            screen.Mount,
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
                // Ratings are reread hourly, and only while a screen is on (pressing "Read the ratings again" always works).
                if (DateTime.UtcNow - _acclaimedAt > TimeSpan.FromHours(1) && cfg.Screens.Any(sc => Awake(RuntimeOf(sc.Id)) && Shows(sc, cfg, "acclaimed")))
                    _ = RefreshAcclaimedAsync(ct);
                foreach (var s in cfg.Screens) using (_renderer.ForScreen(s.Id)) Update(s, cfg);
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
            rt.Making = null;
            var bg = Background(cfg, cfg.NowPlaying.BackgroundId);
            var req = new PosterRequest(now.SourceUrl, now.PlexFile, now.DeviceId, now.IsEpisode, now.Season, false, now.Title, now.PlexKey, CoverUrl: now.CoverUrl);
            var poster = PosterTask(req);
            if (poster.IsCompleted)
            {
                var found = poster.IsCompletedSuccessfully ? poster.Result : null;
                var video = _renderer.HoldAsync(bg, found?.Path, s.Mount, s.Resolution, CodecOf(s), priority: 0);
                if (video.IsCompletedSuccessfully && video.Result != null)
                {
                    // The screen gets it copied end to end into a 20-minute video (a second or two), so it rarely loops:
                    // a Roku shows black for a moment each time a video starts over.
                    var longer = _renderer.LongAsync(video.Result, Renderer.HoldSeconds(bg));
                    if (!longer.IsCompleted) return;
                    rt.PosterSource = found?.Source ?? "no poster found";
                    Show(rt, "playing", longer.IsCompletedSuccessfully && longer.Result != null ? longer.Result : video.Result, loop: true, now.Title);
                    return;
                }
            }
            // While the poster is found and its Now Showing video made, the slideshow that's up stays up (it replays if
            // it ends first; no new slideshow work meanwhile, so the Now Showing video is made as fast as it can be).
            // The screen switches only once that video is ready. With no slideshow to show (none made yet, or every set
            // off): the background alone (made once), as before.
            rt.PosterSource = poster.IsCompleted ? "making Now Showing…" : "finding the poster…";
            if (rt.IdleUrl.Length > 0 && File.Exists(Path.Combine(_live.RenderDir, rt.IdleUrl)))
            {
                rt.AdvanceAfterEnd = false;
                Show(rt, "idle", rt.IdleUrl, loop: false, $"Slideshow, until Now Showing is ready: {now.Title}");
                return;
            }
            var plain = _renderer.HoldAsync(bg, null, s.Mount, s.Resolution, CodecOf(s), priority: 0);
            if (plain.IsCompletedSuccessfully && plain.Result != null) Show(rt, "playing", plain.Result, loop: true, now.Title);
            return;
        }

        // ---- Slideshow, only while the screen is on: a screen that hasn't checked in for a while gets no slideshow work
        // (no posters read, no slides made). Now Playing above still is, so its poster is ready when the screen comes on.
        if (!Awake(rt) && !rt.ForceBuild)
        {
            rt.Mode = "off"; rt.Url = ""; rt.Title = ""; rt.Note = "The screen is off: its slideshow is made when it comes back on.";
            rt.Making = null;
            return;
        }
        Slideshow(s, rt, cfg);
        // A rebuild asked for while the screen is off: done once the whole round is made (it's then ready when the screen comes on).
        if (rt.ForceBuild && !rt.WantNext && rt.Making == null) rt.ForceBuild = false;
    }

    /// <summary>The build's progress, with counts that never go down within one build (one round number), and a
    /// fraction and time left from how long slides, dissolves and joins have been taking on this computer.</summary>
    private Building Progress(ScreenRuntime rt, int slides, int slidesTotal, Renderer.RoundStep? step2)
    {
        var b = rt.Making is { } old && old.Round == rt.Round ? old : new Building { Round = rt.Round };
        if (step2 == null)
        {
            b.Step = Math.Max(b.Step, 1);
            b.SlidesTotal = slidesTotal;
            b.Slides = Math.Min(Math.Max(b.Slides, slides), slidesTotal);
        }
        else
        {
            b.Step = 2;
            b.Slides = b.SlidesTotal = slidesTotal;
            b.DissolvesTotal = step2.Total;
            b.Dissolves = Math.Max(b.Dissolves, Math.Min(step2.Done, step2.Total));
            b.Joining = step2.Joining;
        }
        double slide = _renderer.TypicalSeconds("slide", 8), dissolve = _renderer.TypicalSeconds("dissolve", 2), join = _renderer.TypicalSeconds("slideshow", 5);
        int dTotal = b.Step == 2 ? b.DissolvesTotal : b.SlidesTotal + 1;
        double all = b.SlidesTotal * slide + dTotal * dissolve + join;
        double done = b.Slides * slide + (b.Step == 2 ? b.Dissolves * dissolve + (b.Joining ? join * 0.5 : 0) : 0);
        b.Fraction = all <= 0 ? 0 : Math.Clamp(done / all, 0, 0.99);
        b.SecondsLeft = (int)Math.Ceiling(Math.Max(0, all - done));
        return b;
    }

    /// <summary>A screen counts as on while its app keeps asking what to play (every few seconds).</summary>
    public static readonly TimeSpan AwakeFor = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("RESUMERELAY_SCREEN_AWAKE"), out var a) && a > 0 ? a : 120);
    /// <summary>The slideshow sets, in their turn order.</summary>
    public static readonly string[] SetKeys = { "coming", "recent", "acclaimed", "feature" };

    /// <summary>Whether a screen shows a slideshow set (each screen has its own; older ones follow the old all-screens switches).</summary>
    public static bool Shows(LiveScreen s, LiveConfig cfg, string set) =>
        s.Sets != null ? s.Sets.Contains(set)
        : set switch { "coming" => cfg.ComingSoon.Enabled, "recent" => cfg.RecentlyWatched.Enabled, "acclaimed" => cfg.Acclaimed.Enabled, _ => false };

    /// <summary>A set's shared settings (its background).</summary>
    public static LiveSet SetOf(LiveConfig cfg, string set) => set switch
    {
        "coming" => cfg.ComingSoon, "recent" => cfg.RecentlyWatched, "acclaimed" => cfg.Acclaimed, "feature" => cfg.Feature, _ => cfg.NowPlaying,
    };

    public static bool Awake(ScreenRuntime rt) => rt.LastSeenUtc is { } seen && DateTime.UtcNow - seen < AwakeFor;

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
        // Kaleidescapes: the movie and its own cover.
        foreach (var k in _kaleidescape.Players().Where(k => k.Enabled && s.Players.Contains(k.Id)))
        {
            var l = _kaleidescape.LiveOf(k.Id);
            if (l.Movie is not { } m || l.Status is not { Playing: true } st) continue;
            string url = PosterFinder.KaleidescapePrefix + m.Handle;
            _started.TryAdd(k.Id + "|" + url, DateTime.UtcNow);
            playing.Add(new NowItem(k.Id, k.Name, url, "", KaleidescapeService.Display(m), false, 0, 0, st.Play == "paused", m.BestCover));
        }
        // Apple TVs: the Plex app's title through Plex (its item and poster, and folder posters through its file); any
        // other app's by its name, with a TMDB poster (or the app's own artwork).
        foreach (var a in _appleTv.Devices().Where(a => a.Enabled && s.Players.Contains(a.Id)))
        {
            var l = _appleTv.LiveOf(a.Id);
            if (l.Now is not { } n) continue;
            NowItem item;
            if (n.IsPlex && l.PlexItem is { } px)
            {
                string src = l.PlexFile.Length > 0 ? PlexPaths.ToSourceUrl(l.PlexFile, _store.Plex().Mappings) ?? "" : "";
                string title = px.IsEpisode ? px.Show : px.Title;
                item = new NowItem(a.Id, a.Name, src, l.PlexFile, title, px.IsEpisode, px.Season, px.Key, n.Paused);
            }
            else
            {
                string url = PosterFinder.TmdbUrl(n.IsEpisode ? "tv" : n.Year > 0 ? "movie" : "any", n.ShowOrTitle, n.IsEpisode ? 0 : n.Year);
                item = new NowItem(a.Id, a.Name, url, "", n.ShowOrTitle, n.IsEpisode, n.Season, 0, n.Paused, PosterFinder.AppleTvArtPrefix + a.Id);
            }
            _started.TryAdd(a.Id + "|" + (item.SourceUrl.Length > 0 ? item.SourceUrl : item.PlexFile), DateTime.UtcNow);
            playing.Add(item);
        }
        foreach (var id in s.Players)
        {
            if (!devices.TryGetValue(id, out var d)) continue;
            var p = _engine.LiveOf(id).Playing;
            if (p == null || (p.SourceUrl.Length == 0 && p.Path.Length == 0)) continue;
            string file = p.SourceUrl.Length > 0 ? p.SourceUrl : p.Path;
            _started.TryAdd(id + "|" + file, DateTime.UtcNow);
            var tag = EpisodeTag.Match(p.Subtitle + " " + PathMapper.FileName(p.Path) + " " + PathMapper.FileName(p.SourceUrl));
            playing.Add(new NowItem(id, d.Name, p.SourceUrl, d.IsServerUser ? p.Path : "", p.Title, tag.Success, tag.Success ? int.Parse(tag.Groups[1].Value) : 0, p.PlexKey, p.Paused));
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
        if (t.IsCompleted && !_posterRetry.ContainsKey(key))
        {
            // Look again later: soon when nothing was found (a player may have been off), and every 10 minutes when one
            // was, so a poster added to the folder since (or one with a better name, like Movie-poster.jpg) is picked up.
            // A poster file replaced under the same name needs no new look: each video's fingerprint has the file's size
            // and date, so it's remade from the new file at once.
            bool found = !t.IsFaulted && t.Result != null && File.Exists(t.Result.Path);
            _posterRetry[key] = found ? DateTime.UtcNow.AddMinutes(5) : DateTime.UtcNow;
        }
        if (_posterRetry.TryGetValue(key, out var at) && DateTime.UtcNow - at > TimeSpan.FromMinutes(5))
        {
            _posterRetry.TryRemove(key, out _);
            // The poster up keeps being used while the new look runs, so the screen never loses it in between.
            if (t.IsCompletedSuccessfully && t.Result != null) _posterNext.TryAdd(key, Task.Run(() => _finder.FindAsync(r)));
            else _posters.TryRemove(key, out _);
        }
        if (_posterNext.TryGetValue(key, out var next) && next.IsCompleted)
        {
            _posterNext.TryRemove(key, out _);
            if (next.IsCompletedSuccessfully && next.Result != null) { _posters[key] = next; t = next; }
        }
        return t;
    }
    private readonly ConcurrentDictionary<string, DateTime> _posterRetry = new();
    private readonly ConcurrentDictionary<string, Task<PosterResult?>> _posterNext = new();

    public void ForgetPosters() { _posters.Clear(); _posterRetry.Clear(); _posterNext.Clear(); _finder.Forget(); }

    private LiveBackground? Background(LiveConfig cfg, string id) =>
        cfg.Backgrounds.FirstOrDefault(b => b.Id == id && File.Exists(_live.BackgroundPath(b)))
        ?? cfg.Backgrounds.FirstOrDefault(b => b.BuiltIn && File.Exists(_live.BackgroundPath(b)));

    // ------------------------------------------------------------------ slideshow

    /// <summary>The titles of one slideshow round, in order: Coming Soon, Recently Watched (the screen's players),
    /// then the next 10 Highly Acclaimed. Only sets that are on.</summary>
    public List<SlideItem> RoundItems(LiveScreen s, LiveConfig cfg, int round)
    {
        var sets = new List<List<SlideItem>>();
        if (Shows(s, cfg, "coming"))
            sets.Add(cfg.Coming.Where(c => File.Exists(_live.ComingPath(c))).Select(c => new SlideItem("coming", c.Title, null, _live.ComingPath(c))).ToList());
        if (Shows(s, cfg, "recent"))
        {
            var names = _store.Devices().Where(d => s.Players.Contains(d.Id)).Select(d => d.Name).ToHashSet();
            sets.Add(RecentFor(s.Players, names));
        }
        if (Shows(s, cfg, "acclaimed") && _acclaimed.Count > 0)
        {
            // 10 picked at random for each round: any title can come up again in a later round. The pick depends only on
            // the screen and the round, so it stays the same while the round is being made and played.
            var rnd = new Random(HashCode.Combine(s.Id, round, _salt));
            var all = _acclaimed;
            sets.Add(Enumerable.Range(0, all.Count).OrderBy(_ => rnd.Next()).Take(AcclaimedPerRound).Select(i => all[i]).ToList());
        }
        // Feature Presentation: the screen's own title, once per round.
        if (Shows(s, cfg, "feature") && s.Feature is { } f)
            sets.Add(new() { new SlideItem("feature", f.Display, FeatureRequest(f), null) });
        // The sets take turns, one poster each (Coming Soon 1, Recently Watched 1, Highly Acclaimed 1, Coming Soon 2…);
        // a set that runs out drops out of the turns.
        var items = new List<SlideItem>();
        for (int i = 0; sets.Any(x => i < x.Count); i++)
            foreach (var set in sets) if (i < set.Count) items.Add(set[i]);
        return items;
    }

    /// <summary>"Rebuild slideshow": the next round is made now (with any new Coming Soon posters) and goes up as soon
    /// as it's ready, without waiting for the current round to finish.</summary>
    /// <summary>A screen's card changed (what it shows or how): whatever was being made only for it is stopped, so
    /// the new build starts now instead of after the old one.</summary>
    public void StopBuild(string screenId)
    {
        int n = _renderer.StopFor(screenId);
        var rt = RuntimeOf(screenId);
        rt.Making = null;
        if (n > 0) _store.Log(ActivityLevel.Info, $"Live Poster: the screen \"{_live.Get().Screens.FirstOrDefault(x => x.Id == screenId)?.Name}\" changed, so the {n} video{(n == 1 ? "" : "s")} it was still waiting for {(n == 1 ? "was" : "were")} dropped; its new slideshow is being made.");
    }

    public void Rebuild(string screenId)
    {
        StopBuild(screenId);
        _recentAt = DateTime.MinValue;
        var rt = RuntimeOf(screenId);
        rt.Round += Math.Max(1, rt.RoundsUp);
        rt.WantNext = true;
        // The slideshow up keeps playing (and its titles stay listed) until the new one is ready.
        rt.Making = null;
        rt.ForceBuild = true;
    }

    private void Slideshow(LiveScreen s, ScreenRuntime rt, LiveConfig cfg)
    {
        // Whatever was said before (like "the screen is off") no longer applies; each branch below says what it's doing.
        rt.Note = "";
        // The screen finished a round: the next one goes up as soon as it's made (the screen replays this one meanwhile).
        // (After a partial round, the same round comes back complete, or longer, instead of a new one.)
        if (rt.AdvanceAfterEnd) { rt.AdvanceAfterEnd = false; rt.Round += Math.Max(1, rt.RoundsUp); rt.WantNext = true; }
        // One slideshow video holds several rounds in a row (each its own random pick), at least 15 minutes, so the screen
        // only has to start a new video (a moment of black on a Roku) a few times an hour.
        var items = RoundItems(s, cfg, rt.Round);
        int perRound = items.Count;
        // (One poster alone, like a Feature Presentation by itself: as many copies as fill the 15 minutes, joined end to end.)
        int rounds = perRound == 0 ? 1 : Math.Clamp((int)Math.Ceiling(15 * 60.0 / (perRound * Math.Clamp(cfg.SlideSeconds, 4, 120))), 1, perRound == 1 ? 240 : 8);
        for (int k = 1; k < rounds; k++) items.AddRange(RoundItems(s, cfg, rt.Round + k));
        rt.RoundsUp = rounds;
        // Step 1: each title's poster and slide. Step 2 (once they're all made): the slideshow video.
        var slides = new List<string>();
        int waiting = 0;
        var titles = new List<string>();
        var shown = new List<SlideItem>();
        foreach (var it in items)
        {
            string? poster = it.File;
            if (poster == null && it.Request != null)
            {
                var t = PosterTask(it.Request);
                if (!t.IsCompleted) { waiting++; continue; }
                poster = t.IsCompletedSuccessfully ? t.Result?.Path : null;
            }
            if (poster == null) continue; // no poster anywhere: left out
            var setCfg = SetOf(cfg, it.Set.StartsWith("recent", StringComparison.Ordinal) ? "recent" : it.Set);
            var slide = _renderer.SlideAsync(Background(cfg, setCfg.BackgroundId), poster, s.Mount, s.Resolution, CodecOf(s), cfg.SlideSeconds);
            if (!slide.IsCompleted) { waiting++; continue; }
            if (slide.IsCompletedSuccessfully && slide.Result != null) { slides.Add(slide.Result); titles.Add(it.Title); shown.Add(it); }
        }
        bool pending = waiting > 0;
        string key = string.Join("|", slides.Select(Path.GetFileName));
        // A new video replaces the one up only between rounds (or when none is up), never in the middle of one.
        bool canSwap = rt.IdleUrl.Length == 0 || rt.WantNext || rt.Mode is not ("idle" or "making");
        bool upToDate = !pending && rt.IdleKey == key && rt.IdleUrl.Length > 0;
        Renderer.RoundStep? step2 = null;
        if (!pending && slides.Count > 0 && canSwap)
        {
            // Nothing new (the same round again, e.g. a rebuild that picked the same titles in the same order): keep the
            // video that's up, but say again what's in it.
            if (upToDate) { rt.WantNext = false; rt.RoundTitles = titles; rt.RoundShown = shown; }
            else
            {
                var reel = _renderer.RoundVideoAsync(slides, s.Mount, s.Resolution, CodecOf(s));
                if (reel.IsCompleted && !(reel.IsCompletedSuccessfully && reel.Result != null)) pending = true;
                if (reel.IsCompletedSuccessfully && reel.Result != null)
                {
                    rt.IdleUrl = _renderer.Rel(reel.Result);
                    rt.IdleKey = key;
                    rt.RoundTitles = titles;
                    rt.RoundShown = shown;
                    rt.WantNext = false;
                    upToDate = true;
                }
                else step2 = _renderer.RoundProgress(slides, s.Mount, s.Resolution, CodecOf(s));
            }
        }
        // The card's progress (and the screen's Loading bar): only for a video that will go up when it's ready.
        bool building = canSwap && !upToDate && items.Count > 0 && (pending || step2 != null);
        rt.Making = building ? Progress(rt, slides.Count, slides.Count + waiting, step2) : null;
        bool have = rt.IdleUrl.Length > 0 && File.Exists(Path.Combine(_live.RenderDir, rt.IdleUrl));
        if (rt.Making != null) rt.Making.ShowingOld = have;
        if (have)
        {
            Show(rt, "idle", rt.IdleUrl, loop: false, $"Slideshow: {rt.RoundTitles.Count} poster{(rt.RoundTitles.Count == 1 ? "" : "s")}");
            return;
        }
        // The first slideshow (or one after a change) is being made and nothing is up: the screen shows "Loading…" with
        // a progress bar (the screen app draws it), and the slideshow goes up when it's ready.
        if (rt.Making != null)
        {
            rt.Mode = "making"; rt.Url = ""; rt.Loop = true; rt.Title = "Making the slideshow…";
            return;
        }
        // Nothing made yet (or no posters at all): a background on its own.
        var firstKey = SetKeys.FirstOrDefault(k => Shows(s, cfg, k));
        var firstSet = SetOf(cfg, firstKey ?? "");
        var plain = _renderer.HoldAsync(Background(cfg, firstSet.BackgroundId), null, s.Mount, s.Resolution, CodecOf(s), priority: 1);
        if (plain.IsCompletedSuccessfully && plain.Result != null)
        {
            Show(rt, "idle", plain.Result, loop: true, items.Count > 0 ? "Slideshow: no posters found for its titles"
                : SetKeys.Any(k => Shows(s, cfg, k)) ? "Slideshow: no posters to show yet" : "Slideshow: no sets are on for this screen");
            rt.IdleUrl = "";
        }
    }

    /// <summary>Recently Watched: the last 10 titles stopped on these players (a TV show counts once per season).</summary>
    private List<SlideItem> RecentFor(List<string> players, HashSet<string> names) =>
        _recentAll.Where(i => i.Request != null && (players.Contains(i.Request.DeviceId) || names.Contains(i.Set["recent:".Length..])))
            .Select(i => new SlideItem("recent", i.Title, i.Request, null) { TimeUtc = i.TimeUtc, Player = i.Player, PlayerKind = i.PlayerKind })
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
                new PosterRequest(e.SourceUrl, "", e.FromDeviceId, ep, season, false, title), null)
                { TimeUtc = e.TimeUtc, Player = e.FromName, PlayerKind = _store.Device(e.FromDeviceId)?.KindName ?? "zidoo" });
        }
        // Kaleidescape stops (nothing copied, but they were watched): its own cover.
        foreach (var k in _store.Kaleidescape().Stops)
            list.Add(new SlideItem("recent:" + k.PlayerName, k.Title,
                new PosterRequest(PosterFinder.KaleidescapePrefix + k.Handle, "", k.PlayerId, false, 0, false, k.Title, CoverUrl: k.Cover), null)
                { TimeUtc = k.TimeUtc, Player = k.PlayerName, PlayerKind = "kaleidescape" });
        _recentAll = list.OrderByDescending(i => i.TimeUtc).ToList();
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
            var zidoo = _store.Devices().FirstOrDefault(d => !d.IsServerUser && d.Enabled && _engine.LiveOf(d.Id).Online);
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
                            { Score = r.Score, Source = r.Source, Kind = r.Type, From = "plex" });
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
                        { Score = r.Score, Source = r.Source, Kind = r.Kind, From = "zidoo" });
                    fromZidoo++;
                    if (rel != null) keys.Add(PlexPaths.Key(rel));
                    keys.Add(r.Kind + "|" + r.Title.ToLowerInvariant());
                }
            // Jellyfin's ratings are never used (Chet, 2026-11-06): IMDb first, TMDB only when there's no IMDb rating,
            // from Plex or the Zidoo. Jellyfin's community rating can't say where it came from, and some are nonsense
            // (series showing 3,039,430.0).
            int fromJellyfin = 0;
            bool jellyfinRead = false;
            if (plexFailed && !zidooRead && !jellyfinRead) { _acclaimedNote = "The Plex server didn't answer (and no Zidoo or Jellyfin could be read); trying again in 10 minutes."; _acclaimedAt = DateTime.UtcNow.AddMinutes(-50); return; }
            if (plex == null && !zidooRead && !jellyfinRead)
            {
                _acclaimedNote = _engine.JellyfinServer() != null
                    ? "Highly Acclaimed takes its ratings from Plex or a Zidoo (IMDb, or TMDB when there's no IMDb rating); Jellyfin's aren't used. No Plex, and no Zidoo is on; trying again in 10 minutes."
                    : "No player is on to read ratings from; trying again in 10 minutes.";
                _acclaimedAt = DateTime.UtcNow.AddMinutes(-50); return;
            }
            _acclaimed = list.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var parts = new List<string>();
            if (fromPlex > 0) parts.Add($"{fromPlex:N0} rated by Plex");
            if (fromZidoo > 0) parts.Add($"{fromZidoo:N0} by the Zidoo");
            if (fromJellyfin > 0) parts.Add($"{fromJellyfin:N0} by Jellyfin");
            string where = parts.Count > 1 ? $" ({string.Join(", ", parts)})"
                : fromZidoo > 0 ? " (from the Zidoo: IMDb for movies, TMDB for series)"
                : fromJellyfin > 0 ? " (from Jellyfin)" : "";
            _acclaimedNote = $"{list.Count:N0} title{(list.Count == 1 ? "" : "s")} rated {min:0.0} or higher{where}. 10 picked at random each round.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _acclaimedNote = "Reading the ratings failed: " + ex.Message; }
        finally { _acclaimedGate.Release(); }
    }

    public IReadOnlyList<SlideItem> Acclaimed => _acclaimed;

    /// <summary>The last 10 titles stopped on any player (a TV show once), newest first.</summary>
    public List<SlideItem> RecentTitles() => _recentAll.DistinctBy(i => i.Title).Take(10).ToList();

    /// <summary>A title's Highly Acclaimed rating, when it has one (for titles shown by the other sets too).</summary>
    public SlideItem? RatingOf(string title)
    {
        var all = _acclaimed;
        return all.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => a.Title.StartsWith(title + " (", StringComparison.OrdinalIgnoreCase));
    }

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
