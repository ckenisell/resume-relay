using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ResumeRelay;

/// <summary>
/// Kaleidescape players, for Live Poster only. A Kaleidescape plays its own downloaded copies of movies, not the
/// files on the NAS, and its control protocol can't set a resume point, so nothing is copied to or from it: it never
/// appears on Progress Reset, Watch Sync or the stop cards. Resume Relay only watches what it plays (title, position,
/// cover) so a Live Poster screen can show it, and remembers its stops for Recently Watched and the titles it has seen
/// for Feature Presentation. Kept apart from the Zidoo/Plex/Jellyfin players (kaleidescape.json), so none of the copy
/// code can ever pick one up.
///
/// Built from Kaleidescape's published control protocol (TCP port 10000, one line per message) without a real player
/// to test on (Chet, 2026-11-04): "01/1/GET_PLAY_STATUS:" is answered "01/1/000:PLAY_STATUS:2:0:01:07200:00123:…:/89".
/// </summary>
public sealed class KaleidescapePlayer
{
    public string Id { get; set; } = "k-" + Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = KaleidescapeClient.DefaultPort;
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A movie seen playing on a Kaleidescape (its library can't be listed over the protocol, so this is what
/// Feature Presentation can offer from it).</summary>
public sealed class KaleidescapeTitle
{
    public string Handle { get; set; } = "";
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public string Cover { get; set; } = "";
    public string HiResCover { get; set; } = "";
    public DateTime SeenUtc { get; set; } = DateTime.UtcNow;
    public string BestCover => HiResCover.Length > 0 ? HiResCover : Cover;
}

/// <summary>A movie stopped on a Kaleidescape (Recently Watched).</summary>
public sealed class KaleidescapeStop
{
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public int PositionSec { get; set; }
    public int LengthSec { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
}

public sealed class KaleidescapeConfig
{
    public List<KaleidescapePlayer> Players { get; set; } = new();
    public List<KaleidescapeTitle> Seen { get; set; } = new();
    public List<KaleidescapeStop> Stops { get; set; } = new();
}

/// <summary>What a Kaleidescape says it's doing.</summary>
public sealed record KaleidescapeStatus(bool On, string Play, int LengthSec, int PositionSec, string TitleName, string Handle)
{
    public bool Playing => Play is "playing" or "paused" or "forward" or "reverse";
}

public sealed record KaleidescapeInfo(string Serial, string Name, string Model);

/// <summary>One Kaleidescape player's control protocol. Each call opens a connection, asks, and closes it.</summary>
public sealed class KaleidescapeClient
{
    public const int DefaultPort = 10000;
    private readonly string _ip;
    private readonly int _port;

    public KaleidescapeClient(string ip, int port = DefaultPort) { _ip = ip; _port = port; }

    /// <summary>A reply: its status (0 = fine), name and fields.</summary>
    public sealed record Reply(int Seq, int Status, string Name, List<string> Fields);

    /// <summary>Sends the requests (sequence numbers 1 to 9) and returns every reply that came back for them, in
    /// order; events (sequence "!") are skipped. A request that wants several lines back (CONTENT_DETAILS) is
    /// waited for until its count is in. Null when the player can't be reached.</summary>
    public async Task<List<Reply>?> AskAsync(IReadOnlyList<string> requests, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Parse(_ip), _port, cts.Token);
            using var stream = tcp.GetStream();
            var sb = new StringBuilder();
            for (int i = 0; i < requests.Count; i++) sb.Append($"01/{i + 1}/{requests[i]}\n");
            var bytes = Encoding.Latin1.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, cts.Token);
            var replies = new List<Reply>();
            var done = new HashSet<int>();
            var expect = new Dictionary<int, int>();   // seq -> lines still to come (CONTENT_DETAILS)
            var buf = new byte[8192];
            var pending = new StringBuilder();
            while (done.Count < requests.Count)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                pending.Append(Encoding.Latin1.GetString(buf, 0, n));
                string all = pending.ToString();
                int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    string line = all[..nl].TrimEnd('\r');
                    all = all[(nl + 1)..];
                    if (Parse(line) is not { } r || r.Seq <= 0) continue;
                    replies.Add(r);
                    if (r.Name == "CONTENT_DETAILS_OVERVIEW" && r.Fields.Count > 0 && int.TryParse(r.Fields[0], out int count) && count > 0)
                        expect[r.Seq] = count;
                    else if (r.Name == "CONTENT_DETAILS" && expect.TryGetValue(r.Seq, out int left))
                        expect[r.Seq] = left - 1;
                    if (!expect.TryGetValue(r.Seq, out int still) || still <= 0) done.Add(r.Seq);
                }
                pending.Clear().Append(all);
            }
            return replies;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>"01/1/000:PLAY_STATUS:2:0:01:07200:00123:…:/89" → (1, 0, "PLAY_STATUS", ["2", "0", …]).</summary>
    public static Reply? Parse(string line)
    {
        int a = line.IndexOf('/');
        if (a < 0) return null;
        int b = line.IndexOf('/', a + 1);
        if (b < 0) return null;
        string seqText = line[(a + 1)..b];
        int seq = seqText == "!" ? -1 : int.TryParse(seqText, out int sq) ? sq : 0;
        string rest = line[(b + 1)..];
        if (rest.Length < 4 || rest[3] != ':' || !int.TryParse(rest[..3], out int status)) return null;
        rest = rest[4..];
        int c = rest.IndexOf(':');
        if (c < 0) return new Reply(seq, status, rest.TrimEnd('/'), new());
        string name = rest[..c];
        var fields = new List<string>();
        var field = new StringBuilder();
        for (int i = c + 1; i < rest.Length; i++)
        {
            char ch = rest[i];
            if (ch == '\\' && i + 1 < rest.Length)
            {
                char e = rest[++i];
                if (e == 'd' && i + 3 < rest.Length && int.TryParse(rest.AsSpan(i + 1, 3), out int code)) { field.Append((char)code); i += 3; }
                else field.Append(e switch { 'r' => '\r', 'n' => '\n', 't' => '\t', _ => e });
            }
            else if (ch == '/') break;
            else if (ch == ':') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }
        return new Reply(seq, status, name, fields);
    }

    private static string F(Reply? r, int i) => r != null && i < r.Fields.Count ? r.Fields[i] : "";
    private static int N(Reply? r, int i) => int.TryParse(F(r, i), out int v) ? v : 0;

    public async Task<KaleidescapeStatus?> StatusAsync(CancellationToken ct = default)
    {
        var replies = await AskAsync(new[] { "GET_DEVICE_POWER_STATE:", "GET_PLAY_STATUS:", "GET_PLAYING_TITLE_NAME:", "GET_HIGHLIGHTED_SELECTION:" }, TimeSpan.FromSeconds(4), ct);
        if (replies == null) return null;
        Reply? Of(string name) => replies.FirstOrDefault(r => r.Status == 0 && r.Name == name);
        var power = Of("DEVICE_POWER_STATE");
        if (power == null && replies.Count == 0) return null;
        var play = Of("PLAY_STATUS");
        string mode = N(play, 0) switch { 1 => "paused", 2 => "playing", 4 => "forward", 6 => "reverse", _ => "none" };
        bool on = power == null || F(power, 0) == "1";
        return new KaleidescapeStatus(on, on ? mode : "none", N(play, 3), N(play, 4), F(Of("TITLE_NAME"), 0), F(Of("HIGHLIGHTED_SELECTION"), 0));
    }

    /// <summary>A movie's details: title, year, cover pictures (Cover_URL, HiRes_cover_URL).</summary>
    public async Task<KaleidescapeTitle?> DetailsAsync(string handle, CancellationToken ct = default)
    {
        if (handle.Length == 0) return null;
        var replies = await AskAsync(new[] { $"GET_CONTENT_DETAILS:{Escape(handle)}::" }, TimeSpan.FromSeconds(6), ct);
        if (replies == null || !replies.Any(r => r.Status == 0 && r.Name == "CONTENT_DETAILS_OVERVIEW")) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in replies.Where(r => r.Status == 0 && r.Name == "CONTENT_DETAILS" && r.Fields.Count >= 3)) d[r.Fields[1]] = r.Fields[2];
        string Get(string k) => d.TryGetValue(k, out var v) ? v.Trim() : "";
        return new KaleidescapeTitle
        {
            Handle = handle, Title = Get("Title"), Year = int.TryParse(Get("Year"), out int y) ? y : 0,
            Cover = Get("Cover_URL"), HiResCover = Get("HiRes_cover_URL"),
        };
    }

    /// <summary>Is a Kaleidescape answering here? Its serial number, name and kind.</summary>
    public async Task<KaleidescapeInfo?> InfoAsync(CancellationToken ct = default)
    {
        var replies = await AskAsync(new[] { "GET_DEVICE_INFO:", "GET_FRIENDLY_NAME:", "GET_DEVICE_TYPE_NAME:" }, TimeSpan.FromSeconds(3), ct);
        var info = replies?.FirstOrDefault(r => r.Status == 0 && r.Name == "DEVICE_INFO");
        if (info == null) return null;
        string serial = F(info, 1).TrimStart('0');
        return new KaleidescapeInfo(serial.Length > 0 ? serial : F(info, 1),
            F(replies!.FirstOrDefault(r => r.Status == 0 && r.Name == "FRIENDLY_NAME"), 0),
            F(replies!.FirstOrDefault(r => r.Status == 0 && r.Name == "DEVICE_TYPE_NAME"), 0));
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(":", "\\:").Replace("/", "\\/");
}

/// <summary>
/// Asks each Kaleidescape what it's playing every few seconds (the Players tab's "Check players every"). Its card shows
/// the movie; a Live Poster screen following it shows the poster; a stop after at least a minute of the movie is
/// remembered for Recently Watched and noted on the Activity tab.
/// </summary>
public sealed class KaleidescapeService : BackgroundService
{
    private readonly Store _store;
    private readonly ConcurrentDictionary<string, Live> _live = new();

    public sealed class Live
    {
        public bool Online;
        public DateTime? LastSeenUtc;
        public bool On;
        public KaleidescapeStatus? Status;
        public KaleidescapeTitle? Movie;
        public DateTime UpdatedUtc;
    }

    public KaleidescapeService(Store store) { _store = store; }

    public Live LiveOf(string id) => _live.GetOrAdd(id, _ => new Live());
    public List<KaleidescapePlayer> Players() => _store.Kaleidescape().Players;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var players = Players().Where(p => p.Enabled && p.Ip.Length > 0).ToList();
                await Task.WhenAll(players.Select(p => PollAsync(p, ct)));
                foreach (var id in _live.Keys.Where(k => players.All(p => p.Id != k)).ToList()) _live.TryRemove(id, out _);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, "Kaleidescape: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_store.Settings().PollSeconds, 1, 60)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(KaleidescapePlayer p, CancellationToken ct)
    {
        var live = LiveOf(p.Id);
        var api = new KaleidescapeClient(p.Ip, p.Port);
        var st = await api.StatusAsync(ct);
        if (st == null)
        {
            if (live.Online) _store.Log(ActivityLevel.Warn, $"{p.Name} (Kaleidescape) isn't answering at {p.Ip}.");
            Stopped(p, live);
            live.Online = false;
            return;
        }
        live.Online = true;
        live.LastSeenUtc = DateTime.UtcNow;
        live.On = st.On;
        if (!st.Playing) { Stopped(p, live); live.Status = st; return; }
        // A new movie: its details (title, year, covers), remembered for Feature Presentation.
        string handle = st.Handle;
        if (live.Movie == null || live.Movie.Handle != handle || live.Status is not { Playing: true })
        {
            if (live.Status is { Playing: true } && live.Movie != null && live.Movie.Handle != handle) Stopped(p, live);
            var known = _store.Kaleidescape().Seen.FirstOrDefault(t => t.Handle == handle && handle.Length > 0);
            var movie = known != null && DateTime.UtcNow - known.SeenUtc < TimeSpan.FromDays(30) ? known : await api.DetailsAsync(handle, ct) ?? known;
            movie ??= new KaleidescapeTitle { Handle = handle, Title = st.TitleName };
            if (movie.Title.Length == 0) movie.Title = st.TitleName;
            live.Movie = movie;
            if (handle.Length > 0 && movie.Title.Length > 0) Remember(movie);
        }
        live.Status = st;
        live.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>The movie that was up has stopped (or the player went off, or another movie started).</summary>
    private void Stopped(KaleidescapePlayer p, Live live)
    {
        if (live.Status is not { Playing: true } st || live.Movie == null) { live.Movie = null; return; }
        var m = live.Movie;
        live.Movie = null;
        live.Status = st with { Play = "none" };
        if (st.PositionSec < 60) return;   // a minute or less: a peek, not a watch
        _store.UpdateKaleidescape(c =>
        {
            c.Stops.Insert(0, new KaleidescapeStop { PlayerId = p.Id, PlayerName = p.Name, Handle = m.Handle, Title = m.Title, Cover = m.BestCover, PositionSec = st.PositionSec, LengthSec = st.LengthSec });
            if (c.Stops.Count > 50) c.Stops.RemoveRange(50, c.Stops.Count - 50);
        });
        string at = TimeSpan.FromSeconds(st.PositionSec).ToString(st.PositionSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        _store.Log(ActivityLevel.Info, $"{Display(m)} was watched on {p.Name} (Kaleidescape), stopped at {at}. Nothing is copied: a Kaleidescape plays its own copies.");
    }

    private void Remember(KaleidescapeTitle movie) => _store.UpdateKaleidescape(c =>
    {
        c.Seen.RemoveAll(t => t.Handle == movie.Handle);
        c.Seen.Insert(0, new KaleidescapeTitle { Handle = movie.Handle, Title = movie.Title, Year = movie.Year, Cover = movie.Cover, HiResCover = movie.HiResCover, SeenUtc = DateTime.UtcNow });
        if (c.Seen.Count > 3000) c.Seen.RemoveRange(3000, c.Seen.Count - 3000);
    });

    public static string Display(KaleidescapeTitle m) => m.Year > 0 ? $"{m.Title} ({m.Year})" : m.Title;

    // ------------------------------------------------------------------ adding players

    /// <summary>Adds (or refreshes) the Kaleidescape at this address. Null and an error when none answers.</summary>
    public async Task<(KaleidescapePlayer? Player, string Error)> AddAsync(string ip, int port, CancellationToken ct = default)
    {
        var info = await new KaleidescapeClient(ip, port).InfoAsync(ct);
        if (info == null) return (null, $"Nothing answered as a Kaleidescape at {ip}:{port}.");
        KaleidescapePlayer? result = null;
        bool isNew = false;
        _store.UpdateKaleidescape(c =>
        {
            var p = c.Players.FirstOrDefault(x => (info.Serial.Length > 0 && x.Serial == info.Serial) || x.Ip == ip);
            if (p == null)
            {
                isNew = true;
                p = new KaleidescapePlayer { Name = info.Name.Length > 0 ? info.Name : $"Kaleidescape ({ip})" };
                c.Players.Add(p);
            }
            p.Ip = ip; p.Port = port; p.Serial = info.Serial; p.Model = info.Model;
            result = p;
        });
        _store.Log(ActivityLevel.Info, isNew ? $"Added {result!.Name} (Kaleidescape{(info.Model.Length > 0 ? " " + info.Model : "")}, {ip}): for Live Poster only."
            : $"{result!.Name} (Kaleidescape) is already added; address updated to {ip}.");
        return (result, "");
    }

    /// <summary>Scan network: every Kaleidescape on the local networks (TCP port 10000).</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        int found = 0;
        using var gate = new SemaphoreSlim(96);
        await Task.WhenAll(Discovery.ScanHosts().Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!await Discovery.IsPortOpenAsync(ip, ct, KaleidescapeClient.DefaultPort)) return;
                if (Players().Any(p => p.Ip == ip)) { Interlocked.Increment(ref found); return; }
                var (p, _) = await AddAsync(ip, KaleidescapeClient.DefaultPort, ct);
                if (p != null) Interlocked.Increment(ref found);
            }
            finally { gate.Release(); }
        }));
        return found;
    }
}
