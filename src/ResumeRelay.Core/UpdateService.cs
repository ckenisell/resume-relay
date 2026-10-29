using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResumeRelay;

/// <summary>A newer version found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Url, string Notes, bool Beta, DateTime PublishedUtc);

/// <summary>
/// Once a day (and a minute after starting), asks GitHub for Resume Relay's releases and keeps the newest one that is
/// newer than this build. Betas are offered only when "Include beta versions" is on, or when this build is a beta.
/// Only the public release list is fetched; nothing about the user or their players is sent.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Store _store;
    private readonly SemaphoreSlim _now = new(0, 1);

    public UpdateInfo? Available { get; private set; }
    public DateTime? CheckedUtc { get; private set; }
    public string LastError { get; private set; } = "";

    public UpdateService(Store store) => _store = store;

    /// <summary>https://api.github.com, unless RESUMERELAY_UPDATE_API says otherwise (the tests use a stand-in).</summary>
    private static string ApiBase => Environment.GetEnvironmentVariable("RESUMERELAY_UPDATE_API") is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.github.com";

    public void CheckSoon() { if (_now.CurrentCount == 0) _now.Release(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("RESUMERELAY_UPDATE_API") is { Length: > 0 } ? 2 : 60), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (_store.Settings().CheckUpdates) await CheckAsync(ct);
            else Available = null;
            try { await _now.WaitAsync(TimeSpan.FromHours(24), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/repos/ckenisell/resume-relay/releases?per_page=30");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ResumeRelay", RelayHost.Version));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) { LastError = $"GitHub answered {(int)resp.StatusCode}"; return; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var current = SemVer.Parse(RelayHost.Version);
            bool betas = _store.Settings().IncludeBetas || current?.Pre != null;
            UpdateInfo? best = null;
            SemVer? bestV = null;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (J.Bool(r, "draft")) continue;
                bool pre = J.Bool(r, "prerelease");
                if (pre && !betas) continue;
                var v = SemVer.Parse(J.Str(r, "tag_name"));
                if (v == null || (current != null && v.CompareTo(current) <= 0)) continue;
                if (bestV != null && v.CompareTo(bestV) <= 0) continue;
                bestV = v;
                DateTime.TryParse(J.Str(r, "published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var published);
                best = new UpdateInfo(v.ToString(), J.Str(r, "html_url"), J.Str(r, "body"), pre, published);
            }
            Available = best;
            CheckedUtc = DateTime.UtcNow;
            LastError = "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// <summary>"0.5.0", "v0.5.0-beta.2": compared as numbers; a full release beats its own betas.</summary>
    public sealed record SemVer(int Major, int Minor, int Patch, string? Pre, int PreNum) : IComparable<SemVer>
    {
        public static SemVer? Parse(string s)
        {
            var m = Regex.Match(s.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)(?:-([A-Za-z]+)\.?(\d+)?)?");
            if (!m.Success) return null;
            return new SemVer(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? m.Groups[4].Value.ToLowerInvariant() : null, m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0);
        }

        public int CompareTo(SemVer? o)
        {
            if (o == null) return 1;
            int c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            if (Pre == null || o.Pre == null) return Pre == null ? (o.Pre == null ? 0 : 1) : -1;
            c = string.CompareOrdinal(Pre, o.Pre); if (c != 0) return c;
            return PreNum.CompareTo(o.PreNum);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre != null ? $"-{Pre}.{PreNum}" : "");
    }
}
