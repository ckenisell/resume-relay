using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppleTvControlLibrary.Auth;
using AppleTvControlLibrary.Discovery.AirPlay;
using AppleTvControlLibrary.Mrp.AirPlay;
using AppleTvControlLibrary.Mrp.AirPlay.Auth;
using AppleTvControlLibrary.Mrp.AirPlay.Http;
using AppleTvControlLibrary.Mrp.PlayerState;
using AppleTvControlLibrary.Mrp.Protocol;
using AppleTvControlLibrary.Mrp.RemoteControl;
using Microsoft.Extensions.Hosting;

namespace ResumeRelay;

// Apple TV for Live Poster, the way a Kaleidescape is: Scan network finds it, it's paired once (a PIN shows on the
// TV), and from then on it tells Resume Relay what's playing and in which app. Nothing is copied to or from it (an
// app on an Apple TV plays its own streams, not your files). When the Plex app is playing, Resume Relay asks the Plex
// server for that session (matched by the Apple TV's address) and uses Plex's poster; any other app's title gets its
// poster from TMDB. The protocol (Media Remote Protocol over AirPlay 2) is Apple's own, unpublished one, as the
// iPhone's Remote uses; it's spoken by AppleTvControlLibrary (MIT, in third_party), a C# port of pyatv.

public sealed class AppleTvDevice
{
    public string Id { get; set; } = "a-" + Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 7000;
    /// <summary>The Apple TV's own id (its AirPlay "deviceid"), so a new address is recognised as the same one.</summary>
    public string UniqueId { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>The pairing keys (from the PIN pairing); empty until paired. Kept in Resume Relay's data folder only.</summary>
    public string Credentials { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppleTvConfig
{
    public List<AppleTvDevice> Devices { get; set; } = new();
}

/// <summary>What an Apple TV is playing, as its app reports it (apps differ in how much they say).</summary>
public sealed record AppleTvNow(string AppId, string AppName, string Title, string Series, int Season, int Episode, int Year,
    int PositionSec, int LengthSec, bool Paused)
{
    public bool IsPlex => AppId.StartsWith("com.plexapp", StringComparison.OrdinalIgnoreCase);
    /// <summary>The show's name for an episode (else the title).</summary>
    public string ShowOrTitle => Series.Length > 0 ? Series : Title;
    public bool IsEpisode => Series.Length > 0 || Episode > 0;
    public string Display =>
        IsEpisode && Season > 0 && Episode > 0 ? $"{ShowOrTitle} S{Season:00}E{Episode:00}" : Year > 0 ? $"{Title} ({Year})" : ShowOrTitle;
}

/// <summary>An Apple TV found on the network (not necessarily added).</summary>
public sealed record AppleTvFound(string Name, string Ip, int Port, string UniqueId, string Model);

/// <summary>The connection to one paired Apple TV.</summary>
public interface IAppleTvLink : IDisposable
{
    Task ConnectAsync(CancellationToken ct);
    /// <summary>What's playing now (null: nothing, or no app is in front).</summary>
    AppleTvNow? Now();
    /// <summary>The playing title's own artwork, when the app gives one.</summary>
    Task<byte[]?> ArtworkAsync(CancellationToken ct);
    /// <summary>Raised when the connection ends.</summary>
    event Action<Exception?>? Closed;
}

/// <summary>Pairing: starting it shows a PIN on the TV; finishing with that PIN gives the keys.</summary>
public interface IAppleTvPairing : IDisposable
{
    Task BeginAsync(CancellationToken ct);
    Task<string> FinishAsync(int pin);
}

/// <summary>How Resume Relay reaches Apple TVs: the real protocol, or (for the tests) a stand-in over HTTP.</summary>
public interface IAppleTvTransport
{
    Task<List<AppleTvFound>> ScanAsync(TimeSpan timeout, CancellationToken ct);
    IAppleTvPairing Pairing(AppleTvDevice d);
    IAppleTvLink Link(AppleTvDevice d);
}

// ====================================================================================== the real thing

public sealed class MrpTransport : IAppleTvTransport
{
    public async Task<List<AppleTvFound>> ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = await new MulticastAirPlayDiscovery().ScanAsync(timeout, ct);
        // AirPlay is also spoken by Macs, HomePods and speakers: only Apple TVs ("AppleTV…" models) are kept.
        return found.Where(f => f.Address != null && (f.Properties.TryGetValue("model", out var m) ? m : "").StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase))
            .Select(f => new AppleTvFound(AirPlayServiceInfo.RemoveNameCollisionSuffix(f.Name), f.Address!.ToString(), f.Port, f.UniqueId ?? "",
                f.Properties.TryGetValue("model", out var m) ? m : ""))
            .GroupBy(f => f.UniqueId.Length > 0 ? f.UniqueId : f.Ip).Select(g => g.First()).ToList();
    }

    public IAppleTvPairing Pairing(AppleTvDevice d) => new MrpPairing(d);
    public IAppleTvLink Link(AppleTvDevice d) => new MrpLink(d);

    private sealed class MrpPairing(AppleTvDevice d) : IAppleTvPairing
    {
        private HttpConnection? _conn;
        private AirPlayHapPairSetupProcedure? _proc;

        public async Task BeginAsync(CancellationToken ct)
        {
            var srp = new SrpAuthHandler();
            _conn = await HttpConnection.ConnectAsync(d.Ip, d.Port, ct).ConfigureAwait(false);
            _proc = new AirPlayHapPairSetupProcedure(_conn, srp);
            await _proc.StartPairingAsync().ConfigureAwait(false);
        }

        public async Task<string> FinishAsync(int pin)
        {
            if (_proc == null) throw new InvalidOperationException("Pairing wasn't started.");
            HapCredentials c = await _proc.FinishPairingAsync(pin).ConfigureAwait(false);
            return c.ToString();
        }

        public void Dispose() { _conn?.Dispose(); _conn = null; _proc = null; }
    }

    private sealed class MrpLink(AppleTvDevice d) : IAppleTvLink
    {
        private Ap2Session? _session;
        private AirPlayMrpConnection? _connection;
        private MrpProtocol? _protocol;
        private MrpPlayerStateManager? _state;
        private MrpRemoteControl? _remote;
        private int _closed;
        public event Action<Exception?>? Closed;

        public async Task ConnectAsync(CancellationToken ct)
        {
            HapCredentials credentials = HapCredentials.Parse(d.Credentials);
            var session = new Ap2Session(d.Ip, d.Port, credentials);
            try
            {
                await session.ConnectAsync(ct).ConfigureAwait(false);
                await session.SetupRemoteControlAsync(ct).ConfigureAwait(false);
                session.KeepAliveFinished += () => Close(null);
                session.KeepAliveFailed += ex => Close(ex);
                session.StartKeepAlive();
                var connection = new AirPlayMrpConnection(session);
                connection.ConnectionLost += ex => Close(ex);
                connection.Connect();
                var srp = new SrpAuthHandler { PairingId = credentials.ClientId };
                var protocol = new MrpProtocol(connection, srp, new MrpInfoSettings()) { AsyncSender = connection.SendAsync };
                var state = new MrpPlayerStateManager();
                protocol.Listener = state;
                await protocol.StartAsync(cancellationToken: ct).ConfigureAwait(false);
                _session = session; _connection = connection; _protocol = protocol; _state = state;
                _remote = new MrpRemoteControl(protocol, state);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public AppleTvNow? Now()
        {
            var st = _state;
            if (st?.Client is not { } client) return null;
            MrpPlayerState p;
            try { p = st.Playing; } catch { return null; }
            if (p == null || !p.IsValid) return null;
            var m = p.Metadata;
            if (m == null) return null;
            var playback = p.PlaybackStateValue;
            bool playing = playback == AppleTvControlLibrary.Mrp.Protobuf.PlaybackState.Types.Enum.Playing;
            bool paused = playback == AppleTvControlLibrary.Mrp.Protobuf.PlaybackState.Types.Enum.Paused;
            if (!playing && !paused) return null;
            string title = m.HasTitle ? m.Title : "";
            string series = m.HasSeriesName ? m.SeriesName : "";
            if (title.Length == 0 && series.Length == 0) return null;
            int year = 0;
            // Apple's dates count seconds from 1 January 2001.
            if (m.HasReleaseDate && m.ReleaseDate > 0) { try { year = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.ReleaseDate).Year; } catch { } }
            return new AppleTvNow(client.BundleIdentifier ?? "", client.DisplayName ?? "", title, series,
                m.HasSeasonNumber ? m.SeasonNumber : 0, m.HasEpisodeNumber ? m.EpisodeNumber : 0, year,
                p.Position ?? 0, p.TotalTime ?? 0, paused);
        }

        public async Task<byte[]?> ArtworkAsync(CancellationToken ct)
        {
            try { return _remote == null ? null : (await _remote.GetArtworkAsync(-1, 1500, ct).ConfigureAwait(false))?.Data; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private void Close(Exception? ex)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            Closed?.Invoke(ex);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            try { _protocol?.Stop(); } catch { }
            try { _connection?.Dispose(); } catch { }
            try { _session?.Dispose(); } catch { }
            _protocol = null; _connection = null; _session = null; _state = null; _remote = null;
        }
    }
}

// ====================================================================================== the tests' stand-in

/// <summary>RESUMERELAY_APPLETV_MOCK=http://host:port: a stand-in Apple TV over plain HTTP (test/mock_appletv.py), so
/// everything around the protocol can be tested without one. The protocol itself is AppleTvControlLibrary's, which
/// has its own tests against a simulated Apple TV.</summary>
public sealed class MockTransport(string baseUrl) : IAppleTvTransport
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _base = baseUrl.TrimEnd('/');

    public async Task<List<AppleTvFound>> ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var j = await Http.GetFromJsonAsync<JsonElement>(_base + "/info", ct);
            var u = new Uri(_base);
            return new() { new AppleTvFound(J.Str(j, "name"), u.Host, J.Int(j, "port"), J.Str(j, "uniqueId"), J.Str(j, "model")) };
        }
        catch { return new(); }
    }

    public IAppleTvPairing Pairing(AppleTvDevice d) => new Pair(_base);
    public IAppleTvLink Link(AppleTvDevice d) => new Link_(_base, d.Credentials);

    private sealed class Pair(string b) : IAppleTvPairing
    {
        public async Task BeginAsync(CancellationToken ct) => (await Http.PostAsync(b + "/pair/begin", null, ct)).EnsureSuccessStatusCode();
        public async Task<string> FinishAsync(int pin)
        {
            var r = await Http.PostAsync(b + "/pair/finish?pin=" + pin, null);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException("The PIN wasn't accepted.");
            return J.Str(await r.Content.ReadFromJsonAsync<JsonElement>(), "credentials");
        }
        public void Dispose() { }
    }

    private sealed class Link_(string b, string cred) : IAppleTvLink
    {
        private AppleTvNow? _now;
        private CancellationTokenSource? _poll;
        public event Action<Exception?>? Closed;

        public async Task ConnectAsync(CancellationToken ct)
        {
            var r = await Http.GetAsync(b + "/state?cred=" + Uri.EscapeDataString(cred), ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException("not paired (" + (int)r.StatusCode + ")");
            Read(await r.Content.ReadFromJsonAsync<JsonElement>(ct));
            _poll = new CancellationTokenSource();
            var token = _poll.Token;
            _ = Task.Run(async () =>
            {
                int fails = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500, token);
                        var x = await Http.GetAsync(b + "/state?cred=" + Uri.EscapeDataString(cred), token);
                        if (!x.IsSuccessStatusCode) throw new InvalidOperationException("not paired");
                        Read(await x.Content.ReadFromJsonAsync<JsonElement>(token));
                        fails = 0;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { if (++fails >= 3) { Closed?.Invoke(ex); return; } }
                }
            }, token);
        }

        private void Read(JsonElement j)
        {
            if (J.Str(j, "title").Length == 0 && J.Str(j, "series").Length == 0) { _now = null; return; }
            _now = new AppleTvNow(J.Str(j, "appId"), J.Str(j, "appName"), J.Str(j, "title"), J.Str(j, "series"), J.Int(j, "season"), J.Int(j, "episode"),
                J.Int(j, "year"), J.Int(j, "pos"), J.Int(j, "len"), J.Bool(j, "paused"));
        }

        public AppleTvNow? Now() => _now;

        public async Task<byte[]?> ArtworkAsync(CancellationToken ct)
        {
            try { var r = await Http.GetAsync(b + "/artwork", ct); return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync(ct) : null; }
            catch { return null; }
        }

        public void Dispose() { try { _poll?.Cancel(); } catch { } }
    }
}

// ====================================================================================== the service

public sealed class AppleTvService : BackgroundService
{
    private readonly Store _store;
    private readonly RelayEngine _engine;
    private readonly IAppleTvTransport _transport;
    private readonly ConcurrentDictionary<string, Live> _live = new();
    private readonly ConcurrentDictionary<string, IAppleTvPairing> _pairing = new();

    public sealed class Live
    {
        public bool Online;
        public string Error = "";
        public DateTime? LastSeenUtc;
        public AppleTvNow? Now;
        public DateTime StartedUtc;
        /// <summary>When the Plex app is playing: Plex's own session on this Apple TV (matched by its address).</summary>
        public PlexItem? PlexItem;
        public string PlexFile = "";
        public IAppleTvLink? Link;
        public DateTime NextTryUtc;
        public int Fails;
    }

    public AppleTvService(Store store, RelayEngine engine)
    {
        _store = store;
        _engine = engine;
        string mock = Environment.GetEnvironmentVariable("RESUMERELAY_APPLETV_MOCK") ?? "";
        _transport = mock.Length > 0 ? new MockTransport(mock) : new MrpTransport();
    }

    public Live LiveOf(string id) => _live.GetOrAdd(id, _ => new Live());
    public List<AppleTvDevice> Devices() => _store.AppleTv().Devices;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var devices = Devices().Where(d => d.Enabled && d.Ip.Length > 0 && d.Credentials.Length > 0).ToList();
                await Task.WhenAll(devices.Select(d => TickAsync(d, ct)));
                foreach (var id in _live.Keys.Where(k => devices.All(d => d.Id != k)).ToList())
                    if (_live.TryRemove(id, out var gone)) gone.Link?.Dispose();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _store.Log(ActivityLevel.Error, "Apple TV: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(AppleTvDevice d, CancellationToken ct)
    {
        var live = LiveOf(d.Id);
        if (live.Link == null)
        {
            if (DateTime.UtcNow < live.NextTryUtc) return;
            var link = _transport.Link(d);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                await link.ConnectAsync(cts.Token);
                link.Closed += ex =>
                {
                    live.Link = null; live.Online = false; live.Error = ex?.Message ?? "the connection closed";
                    live.NextTryUtc = DateTime.UtcNow.AddSeconds(5);
                    try { link.Dispose(); } catch { }
                };
                live.Link = link; live.Online = true; live.Error = ""; live.Fails = 0;
                _store.Log(ActivityLevel.Info, $"Connected to {d.Name} (Apple TV) at {d.Ip}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { link.Dispose(); throw; }
            catch (Exception ex)
            {
                link.Dispose();
                live.Online = false;
                live.Fails++;
                if (live.Fails == 1) _store.Log(ActivityLevel.Warn, $"Can't connect to {d.Name} (Apple TV) at {d.Ip}: {ex.Message}. Trying again; if it keeps failing, pair it again.");
                live.Error = ex.Message;
                live.NextTryUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 10 * live.Fails));
                Stopped(d, live);
                return;
            }
        }
        live.LastSeenUtc = DateTime.UtcNow;
        var now = live.Link?.Now();
        var prev = live.Now;
        bool same = now != null && prev != null && now.AppId == prev.AppId && now.Title == prev.Title && now.Series == prev.Series && now.Episode == prev.Episode;
        if (prev != null && !same) Stopped(d, live);
        if (now != null && !same) { live.StartedUtc = DateTime.UtcNow; live.PlexItem = null; live.PlexFile = ""; }
        live.Now = now;
        if (now is { IsPlex: true } && live.PlexItem == null) await FindPlexSessionAsync(d, live, ct);
    }

    /// <summary>The Plex app is playing: the Plex server's session on this Apple TV (any Plex user; Plex only needs to be
    /// signed in on its card), so Live Poster gets Plex's item and its poster (and folder posters, through its file).</summary>
    private async Task FindPlexSessionAsync(AppleTvDevice d, Live live, CancellationToken ct)
    {
        var plex = _engine.PlexServer();
        if (plex == null) return;
        var sessions = await plex.SessionsAsync(TimeSpan.FromSeconds(4), ct);
        var s = sessions?.FirstOrDefault(x => x.Address == d.Ip) ?? (sessions?.Count(x => x.Platform.Contains("tvOS", StringComparison.OrdinalIgnoreCase)) == 1
            ? sessions.First(x => x.Platform.Contains("tvOS", StringComparison.OrdinalIgnoreCase)) : null);
        if (s == null) return;
        live.PlexItem = s.Item;
        live.PlexFile = s.Item.Files.FirstOrDefault() ?? "";
    }

    /// <summary>The title that was up has stopped (or another started, or the Apple TV went away).</summary>
    private void Stopped(AppleTvDevice d, Live live)
    {
        var was = live.Now;
        live.Now = null;
        live.PlexItem = null;
        if (was == null || DateTime.UtcNow - live.StartedUtc < TimeSpan.FromMinutes(1)) return;   // a peek, not a watch
        string app = was.AppName.Length > 0 ? was.AppName : was.AppId;
        _store.Log(ActivityLevel.Info, $"{was.Display} was watched on {d.Name} (Apple TV{(app.Length > 0 ? ", " + app : "")}). Nothing is copied: an app on an Apple TV plays its own streams.");
    }

    // ------------------------------------------------------------------ finding, adding, pairing

    /// <summary>Scan network: Apple TVs on the network (they announce themselves), added unpaired. Returns how many.</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        List<AppleTvFound> found;
        try { found = await _transport.ScanAsync(TimeSpan.FromSeconds(4), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return 0; }
        foreach (var f in found) Add(f);
        return found.Count;
    }

    private AppleTvDevice Add(AppleTvFound f)
    {
        AppleTvDevice? result = null;
        bool isNew = false;
        _store.UpdateAppleTv(c =>
        {
            var d = c.Devices.FirstOrDefault(x => (f.UniqueId.Length > 0 && x.UniqueId == f.UniqueId) || x.Ip == f.Ip);
            if (d == null) { isNew = true; d = new AppleTvDevice { Name = f.Name.Length > 0 ? f.Name : $"Apple TV ({f.Ip})" }; c.Devices.Add(d); }
            d.Ip = f.Ip; d.Port = f.Port > 0 ? f.Port : d.Port; if (f.UniqueId.Length > 0) d.UniqueId = f.UniqueId; if (f.Model.Length > 0) d.Model = f.Model;
            result = d;
        });
        if (isNew) _store.Log(ActivityLevel.Info, $"Found {result!.Name} (Apple TV, {f.Ip}): for Live Poster only. Pair it on its card on the Players tab.");
        return result!;
    }

    /// <summary>Starts pairing: the Apple TV shows a PIN. Error text, or "" when the PIN is up.</summary>
    public async Task<string> BeginPairAsync(string id, CancellationToken ct = default)
    {
        var d = Devices().FirstOrDefault(x => x.Id == id);
        if (d == null) return "That Apple TV isn't there any more.";
        if (_pairing.TryRemove(id, out var old)) old.Dispose();
        var p = _transport.Pairing(d);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await p.BeginAsync(cts.Token);
            _pairing[id] = p;
            return "";
        }
        catch (Exception ex)
        {
            p.Dispose();
            return $"{d.Name} didn't start pairing ({ex.Message}). Is it on? In its Settings → AirPlay and HomeKit, \"Allow Access\" must include this network.";
        }
    }

    /// <summary>Finishes pairing with the PIN the TV shows. Error text, or "" when paired.</summary>
    public async Task<string> FinishPairAsync(string id, string pin)
    {
        if (!_pairing.TryGetValue(id, out var p)) return "Press Pair first, so the Apple TV shows a PIN.";
        if (!int.TryParse(pin.Trim(), out int n)) return "Type the 4 numbers the TV shows.";
        try
        {
            string cred = await p.FinishAsync(n);
            if (cred.Length == 0) return "The Apple TV didn't finish pairing. Press Pair to try again.";
            _store.UpdateAppleTv(c => { if (c.Devices.FirstOrDefault(x => x.Id == id) is { } d) d.Credentials = cred; });
            var live = LiveOf(id);
            live.Link?.Dispose(); live.Link = null; live.NextTryUtc = DateTime.MinValue; live.Fails = 0;
            _store.Log(ActivityLevel.Ok, $"Paired with {Devices().FirstOrDefault(x => x.Id == id)?.Name} (Apple TV). Live Poster can follow it now.");
            return "";
        }
        catch (Exception ex) { return "That PIN wasn't accepted (" + ex.Message + "). Press Pair to get a new one."; }
        finally { if (_pairing.TryRemove(id, out var done)) done.Dispose(); }
    }

    /// <summary>Forgets the pairing (the Apple TV stays listed, to pair again).</summary>
    public void Unpair(string id)
    {
        _store.UpdateAppleTv(c => { if (c.Devices.FirstOrDefault(x => x.Id == id) is { } d) d.Credentials = ""; });
        if (_live.TryRemove(id, out var l)) l.Link?.Dispose();
    }

    public void Forget(string id)
    {
        if (_live.TryRemove(id, out var l)) l.Link?.Dispose();
        if (_pairing.TryRemove(id, out var p)) p.Dispose();
    }

    /// <summary>The playing title's own artwork (for a poster when TMDB has none).</summary>
    public async Task<byte[]?> ArtworkAsync(string id, CancellationToken ct = default) =>
        LiveOf(id).Link is { } link ? await link.ArtworkAsync(ct) : null;
}
