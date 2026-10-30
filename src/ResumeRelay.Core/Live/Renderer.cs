using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ResumeRelay.Live;

/// <summary>
/// Makes the screens' videos with ffmpeg. A picture is a 2160×3840 portrait canvas: the background (an image or a
/// looping video, cover-fitted), and the poster stretched to 2000×3000, centered (80 px at the sides, 420 at the top
/// and bottom). TVs and sticks play landscape video, so for a TV turned on its side the canvas is turned into a
/// 3840×2160 frame; a tablet held upright gets it as is. 1080p screens get the same at half size.
///
/// Encoding uses the computer's video hardware when ffmpeg can (Intel Quick Sync / VA-API: the same hardware Plex
/// transcodes with, which leaves the processor alone), else the x264 software encoder. ffmpeg always runs at the
/// lowest priority, one job at a time, so Plex and everything else come first. Every video is kept (named by what
/// went into it), so a title is only ever made once per background and screen layout.
/// </summary>
public sealed class Renderer
{
    public const int Fps = 24;
    private readonly LiveStore _live;
    private readonly Store _store;
    private readonly object _q = new();
    private readonly PriorityQueue<Job, (int, long)> _queue = new();
    private readonly Dictionary<string, Task<string?>> _inFlight = new();
    private readonly SemaphoreSlim _signal = new(0);
    private long _seq;

    public string? Ffmpeg { get; private set; }
    public string Encoder { get; private set; } = "";
    public string Status { get; private set; } = "Starting…";
    public bool Ready => Ffmpeg != null && Encoder.Length > 0;
    public int Queued { get { lock (_q) return _queue.Count; } }
    public string Working { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public bool Downloading { get; private set; }

    public Renderer(LiveStore live, Store store)
    {
        _live = live;
        _store = store;
    }

    private sealed record Job(string Key, string Output, string Label, Func<string, string> Args, TaskCompletionSource<string?> Done);

    // ------------------------------------------------------------------ setup

    /// <summary>Finds ffmpeg and picks the encoder (hardware first). Safe to call again (after a download).</summary>
    public async Task InitAsync(CancellationToken ct = default)
    {
        Ffmpeg = FindFfmpeg();
        if (Ffmpeg == null)
        {
            Encoder = "";
            Status = OperatingSystem.IsWindows()
                ? "ffmpeg isn't installed. Press \"Download ffmpeg\" (about 100 MB, kept in Resume Relay's data folder)."
                : "ffmpeg isn't installed. On Ubuntu: sudo apt install ffmpeg (the Ubuntu installer does this for you).";
            return;
        }
        string forced = Environment.GetEnvironmentVariable("RESUMERELAY_ENCODER") ?? "";
        var candidates = forced.Length > 0 ? new[] { forced }
            : OperatingSystem.IsWindows() ? new[] { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" }
            : new[] { "h264_vaapi", "h264_qsv", "libx264" };
        foreach (var enc in candidates)
        {
            if (await TestEncoderAsync(enc, ct)) { Encoder = enc; break; }
        }
        Status = Encoder.Length == 0 ? "ffmpeg is installed, but none of its H.264 encoders work."
            : Encoder == "libx264" ? "Ready (software encoding: this computer's video hardware isn't available to ffmpeg)."
            : $"Ready (hardware encoding: {Encoder}).";
        await EnsureBuiltInBackgroundsAsync(ct);
    }

    private string? FindFfmpeg()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var env = Environment.GetEnvironmentVariable("RESUMERELAY_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string local = Path.Combine(_store.DataDirectory, "ffmpeg", exe);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe); } catch { }
        }
        return null;
    }

    private static string RenderNode => File.Exists("/dev/dri/renderD128") ? "/dev/dri/renderD128" : "/dev/dri/renderD129";

    private async Task<bool> TestEncoderAsync(string enc, CancellationToken ct)
    {
        if (enc == "h264_vaapi" && !File.Exists(RenderNode)) return false;
        string pre = enc == "h264_vaapi" ? $"-vaapi_device {RenderNode} " : "";
        string vf = enc == "h264_vaapi" ? "-vf format=nv12,hwupload " : enc is "h264_qsv" ? "-pix_fmt nv12 " : "-pix_fmt yuv420p ";
        var (code, _) = await RunAsync($"-hide_banner -loglevel error {pre}-f lavfi -i color=c=black:s=640x360:r=24:d=0.5 {vf}-c:v {enc} -f null -", TimeSpan.FromSeconds(30), ct, lowPriority: false);
        return code == 0;
    }

    /// <summary>Windows: downloads a static ffmpeg build into the data folder (the FFmpeg project's own builds on GitHub).</summary>
    public async Task<string?> DownloadFfmpegAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return "Install ffmpeg with your system's package manager (sudo apt install ffmpeg).";
        if (Downloading) return "Already downloading.";
        Downloading = true;
        Status = "Downloading ffmpeg…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string zip = Path.Combine(_store.DataDirectory, "ffmpeg-download.zip");
            await using (var s = await http.GetStreamAsync(url, ct))
            await using (var f = File.Create(zip)) await s.CopyToAsync(f, ct);
            string dir = Path.Combine(_store.DataDirectory, "ffmpeg");
            Directory.CreateDirectory(dir);
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries.Where(e => e.Name is "ffmpeg.exe" or "ffprobe.exe"))
                    e.ExtractToFile(Path.Combine(dir, e.Name), true);
            File.Delete(zip);
            await InitAsync(ct);
            return null;
        }
        catch (Exception ex) { Status = "Downloading ffmpeg failed: " + ex.Message; return Status; }
        finally { Downloading = false; }
    }

    // ------------------------------------------------------------------ layout

    public sealed record Layout(int W, int H, int PW, int PH, int PX, int PY);

    public static Layout LayoutFor(string resolution) => resolution == "1080"
        ? new Layout(1080, 1920, 1000, 1500, 40, 210)
        : new Layout(2160, 3840, 2000, 3000, 80, 420);

    /// <summary>A TV turned with its top on the right shows the canvas turned a quarter left (and the other way round).</summary>
    private static string Turn(string mount) => mount switch { "right" => ",transpose=2", "left" => ",transpose=1", _ => "" };

    // ------------------------------------------------------------------ videos

    /// <summary>
    /// A video that loops seamlessly with the poster held: Now Playing. With a still background it's 2 seconds (the
    /// player loops it); with a video background it's the background's own length. No poster: the background alone.
    /// </summary>
    public Task<string?> HoldAsync(LiveBackground? bg, string? poster, string mount, string resolution, int priority = 0)
    {
        double seconds = bg is { Kind: "video", DurationMs: > 0 } ? Math.Clamp(bg.DurationMs / 1000.0, 2, 60) : 2;
        return MakeAsync("hold", bg, poster, mount, resolution, seconds, fade: false, priority);
    }

    /// <summary>One slideshow poster: the poster fades in over the background, holds, and fades out again, so slides
    /// played one after another dissolve through the background.</summary>
    public Task<string?> SlideAsync(LiveBackground? bg, string poster, string mount, string resolution, int seconds, int priority = 5) =>
        MakeAsync("slide", bg, poster, mount, resolution, Math.Clamp(seconds, 4, 120), fade: true, priority);

    /// <summary>A screen's test picture: an arrow pointing up and the screen's name, to check which way it's turned.</summary>
    public Task<string?> TestPatternAsync(string mount, string resolution)
    {
        var l = LayoutFor(resolution);
        string key = Hash($"test|{mount}|{resolution}|{Encoder}|2");
        string output = Path.Combine(_live.RenderDir, key + ".mp4");
        // An arrow made of boxes (no fonts needed): a shaft and a head pointing to the top of the portrait canvas.
        int cx = l.W / 2, unit = l.W / 18;
        string arrow = $"drawbox=x={cx - unit}:y={l.H / 3}:w={unit * 2}:h={l.H / 3}:color=white:t=fill";
        for (int i = 0; i < 6; i++)
        {
            int w = unit * (2 + i * 2), y = l.H / 3 - unit * (6 - i);
            arrow += $",drawbox=x={cx - w / 2}:y={y}:w={w}:h={unit}:color=white:t=fill";
        }
        arrow += $",drawbox=x=0:y=0:w={l.W}:h={l.H}:color=0xF2A33A:t={Math.Max(8, unit / 3)}";
        return Enqueue(key, output, "test picture", 0, o =>
            $"{Pre}-f lavfi -i color=c=0x101826:s={l.W}x{l.H}:r={Fps}:d=2 -vf \"{arrow}{Turn(mount)}{Final}\" -t 2 {EncodeArgs(still: true)} \"{o}\"");
    }

    private Task<string?> MakeAsync(string kind, LiveBackground? bg, string? poster, string mount, string resolution, double seconds, bool fade, int priority)
    {
        var l = LayoutFor(resolution);
        string bgPath = bg == null ? "" : _live.BackgroundPath(bg);
        string key = Hash($"{kind}|{Stamp(bgPath)}|{Stamp(poster)}|{mount}|{resolution}|{seconds.ToString(CultureInfo.InvariantCulture)}|{Encoder}|v1");
        string output = Path.Combine(_live.RenderDir, key + ".mp4");
        string label = kind == "hold" ? "Now Playing" : "slide";
        bool still = bg == null || bg.Kind != "video";
        string s = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        return Enqueue(key, output, label, priority, o =>
        {
            var a = new StringBuilder(Pre);
            if (bg == null) a.Append($"-f lavfi -t {s} -i color=c=black:s={l.W}x{l.H}:r={Fps} ");
            else if (bg.Kind == "video") a.Append($"-stream_loop -1 -t {s} -i \"{bgPath}\" ");
            else a.Append($"-loop 1 -framerate {Fps} -t {s} -i \"{bgPath}\" ");
            if (poster != null) a.Append($"-loop 1 -framerate {Fps} -t {s} -i \"{poster}\" ");
            string fades = fade
                ? $",fade=t=in:st=0:d=0.8:alpha=1,fade=t=out:st={(seconds - 0.8).ToString("0.###", CultureInfo.InvariantCulture)}:d=0.8:alpha=1"
                : "";
            string graph = $"[0:v]scale={l.W}:{l.H}:force_original_aspect_ratio=increase,crop={l.W}:{l.H},setsar=1,fps={Fps}[bg]";
            graph += poster != null
                ? $";[1:v]scale={l.PW}:{l.PH},setsar=1,format=yuva420p{fades}[p];[bg][p]overlay={l.PX}:{l.PY}:format=auto{Turn(mount)}{Final}[v]"
                : $";[bg]null{Turn(mount)}{Final}[v]";
            a.Append($"-filter_complex \"{graph}\" -map \"[v]\" -t {s} {EncodeArgs(still)} \"{o}\"");
            return a.ToString();
        });
    }

    /// <summary>Slides joined into one video without re-encoding (they share every encoding setting).</summary>
    public Task<string?> ReelAsync(IReadOnlyList<string> slides, int priority = 6)
    {
        string key = Hash("reel|" + string.Join("|", slides.Select(Path.GetFileName)));
        // Rounds are joined copies of their slides (one file, so the screen plays them without a gap). They go in
        // their own folder and are deleted once no screen is playing them (see TidyReels).
        Directory.CreateDirectory(ReelDir);
        string output = Path.Combine(ReelDir, key + ".mp4");
        if (slides.Count == 1) return Task.FromResult<string?>(slides[0]);
        return Enqueue(key, output, "slideshow", priority, o =>
        {
            string list = o + ".txt";
            File.WriteAllLines(list, slides.Select(f => $"file '{f.Replace("'", "'\\''")}'"));
            return $"-f concat -safe 0 -i \"{list}\" -c copy -movflags +faststart \"{o}\"";
        }, cleanup: o => { try { File.Delete(o + ".txt"); } catch { } });
    }

    /// <summary>A small upright still of a picture for the dashboard (540×960 JPEG).</summary>
    public async Task<byte[]?> PreviewAsync(LiveBackground? bg, string? poster, CancellationToken ct = default)
    {
        if (Ffmpeg == null) return null;
        var l = new Layout(540, 960, 500, 750, 20, 105);
        string bgPath = bg == null ? "" : _live.BackgroundPath(bg);
        string tmp = Path.Combine(_live.RenderDir, "preview-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
        var a = new StringBuilder("-hide_banner -loglevel error -y ");
        a.Append(bg == null ? $"-f lavfi -i color=c=black:s={l.W}x{l.H} " : $"-i \"{bgPath}\" ");
        if (poster != null) a.Append($"-i \"{poster}\" ");
        string graph = $"[0:v]scale={l.W}:{l.H}:force_original_aspect_ratio=increase,crop={l.W}:{l.H},setsar=1[bg]";
        graph += poster != null ? $";[1:v]scale={l.PW}:{l.PH},setsar=1[p];[bg][p]overlay={l.PX}:{l.PY}[v]" : ";[bg]null[v]";
        a.Append($"-filter_complex \"{graph}\" -map \"[v]\" -frames:v 1 -q:v 3 \"{tmp}\"");
        var (code, _) = await RunAsync(a.ToString(), TimeSpan.FromSeconds(30), ct, lowPriority: false);
        try { return code == 0 && File.Exists(tmp) ? await File.ReadAllBytesAsync(tmp, ct) : null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>One frame of a made video, turned upright and shrunk (540×960 JPEG), for the dashboard's preview.</summary>
    public async Task<byte[]?> FrameAsync(string video, string mount, double atSeconds, CancellationToken ct = default)
    {
        if (Ffmpeg == null || !File.Exists(video)) return null;
        string undo = mount switch { "right" => "transpose=1,", "left" => "transpose=2,", _ => "" };
        string tmp = Path.Combine(_live.RenderDir, "frame-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
        var (code, _) = await RunAsync($"-hide_banner -loglevel error -y -ss {atSeconds.ToString("0.##", CultureInfo.InvariantCulture)} -i \"{video}\" -frames:v 1 -vf \"{undo}scale=540:960\" -q:v 3 \"{tmp}\"",
            TimeSpan.FromSeconds(30), ct, lowPriority: false);
        try { return code == 0 && File.Exists(tmp) ? await File.ReadAllBytesAsync(tmp, ct) : null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>A background's size and length (video), read with ffmpeg. (0, 0, 0) when it can't be read.</summary>
    public async Task<(int W, int H, int DurationMs)> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (Ffmpeg == null) return (0, 0, 0);
        var (_, err) = await RunAsync($"-hide_banner -i \"{path}\"", TimeSpan.FromSeconds(20), ct, lowPriority: false);
        int w = 0, h = 0, ms = 0;
        var d = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (d.Success) ms = (int)((int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000);
        var v = Regex.Match(err, @"Video:.*?\b(\d{2,5})x(\d{2,5})\b");
        if (v.Success) { w = int.Parse(v.Groups[1].Value); h = int.Parse(v.Groups[2].Value); }
        return (w, h, ms);
    }

    /// <summary>VA-API needs its device named before the inputs, and the frames handed to it at the end of the graph.</summary>
    private string Pre => Encoder == "h264_vaapi" ? $"-vaapi_device {RenderNode} " : "";
    private string Final => Encoder == "h264_vaapi" ? ",format=nv12,hwupload" : Encoder == "h264_qsv" ? ",format=nv12" : ",format=yuv420p";

    private string EncodeArgs(bool still)
    {
        string common = $"-an -r {Fps} -g {Fps * 2} -movflags +faststart";
        return Encoder switch
        {
            "h264_vaapi" => $"-c:v h264_vaapi -qp 20 -profile:v high {common}",
            "h264_qsv" => $"-c:v h264_qsv -global_quality 20 -profile:v high {common}",
            "h264_nvenc" => $"-pix_fmt yuv420p -c:v h264_nvenc -cq 20 -profile:v high {common}",
            "h264_amf" => $"-pix_fmt yuv420p -c:v h264_amf -qp_i 20 -qp_p 22 -profile:v high {common}",
            _ => $"-pix_fmt yuv420p -c:v libx264 -preset veryfast -crf 18{(still ? " -tune stillimage" : "")} -profile:v high -level 5.1 -threads {Math.Max(1, Environment.ProcessorCount / 2)} {common}",
        };
    }

    // ------------------------------------------------------------------ queue

    private Task<string?> Enqueue(string key, string output, string label, int priority, Func<string, string> args, Action<string>? cleanup = null)
    {
        if (File.Exists(output)) { Touch(output); return Task.FromResult<string?>(output); }
        if (!Ready) return Task.FromResult<string?>(null);
        Task<string?> task;
        lock (_q)
        {
            if (_inFlight.TryGetValue(key, out var running)) return running;
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _inFlight[key] = tcs.Task;
            _queue.Enqueue(new Job(key, output, label, o => args(o), tcs), (priority, _seq++));
            if (cleanup != null) tcs.Task.ContinueWith(_ => cleanup(output));
        }
        _signal.Release();
        return task;
    }

    /// <summary>The worker: one ffmpeg at a time, Now Playing first.</summary>
    public async Task RunQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct); } catch (OperationCanceledException) { return; }
            Job? job;
            lock (_q) if (!_queue.TryDequeue(out job, out _)) continue;
            string tmp = job.Output + ".part.mp4";
            Working = job.Label;
            string? result = null;
            try
            {
                if (File.Exists(job.Output)) result = job.Output;
                else
                {
                    var (code, err) = await RunAsync("-hide_banner -loglevel error -y " + job.Args(tmp), TimeSpan.FromMinutes(20), ct, lowPriority: true);
                    if (code == 0 && File.Exists(tmp)) { File.Move(tmp, job.Output, true); result = job.Output; LastError = ""; }
                    else LastError = $"Making a {job.Label} failed: " + LastLine(err);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LastError = $"Making a {job.Label} failed: {ex.Message}"; }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                Working = "";
                lock (_q) _inFlight.Remove(job.Key);
                job.Done.TrySetResult(result);
            }
        }
    }

    private async Task<(int Code, string Err)> RunAsync(string args, TimeSpan timeout, CancellationToken ct, bool lowPriority)
    {
        if (Ffmpeg == null) return (-1, "ffmpeg isn't installed");
        var psi = new ProcessStartInfo { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        // Lowest priority: on Linux through nice (when present), on Windows by setting the process's class below.
        if (lowPriority && !OperatingSystem.IsWindows() && File.Exists("/usr/bin/nice")) { psi.FileName = "/usr/bin/nice"; psi.Arguments = $"-n 19 \"{Ffmpeg}\" {args}"; }
        else { psi.FileName = Ffmpeg; psi.Arguments = args; }
        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 20000) err.AppendLine(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        try { p.Start(); } catch (Exception ex) { return (-1, ex.Message); }
        if (lowPriority && OperatingSystem.IsWindows()) { try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { } }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "took too long");
        }
        return (p.ExitCode, err.ToString());
    }

    // ------------------------------------------------------------------ built-in backgrounds

    /// <summary>Plain gradient placeholders until real designs are added (one per set).</summary>
    public static readonly (string Id, string Name, string Top, string Bottom)[] BuiltIns =
    {
        ("builtin-nowshowing", "Now Showing (placeholder)", "0x3a0610", "0x0a0204"),
        ("builtin-comingsoon", "Coming Soon (placeholder)", "0x0b1f45", "0x03070f"),
        ("builtin-recent", "Recently Watched (placeholder)", "0x0b3a33", "0x020b0a"),
        ("builtin-acclaimed", "Highly Acclaimed (placeholder)", "0x4a3608", "0x0d0902"),
    };

    private async Task EnsureBuiltInBackgroundsAsync(CancellationToken ct)
    {
        foreach (var (id, name, top, bottom) in BuiltIns)
        {
            string file = id + ".png", path = Path.Combine(_live.BackgroundDir, file);
            if (!File.Exists(path))
            {
                var (code, _) = await RunAsync($"-hide_banner -loglevel error -y -f lavfi -i \"gradients=s=2160x3840:c0={top}:c1={bottom}:x0=1080:y0=0:x1=1080:y1=3840:nb_colors=2:speed=0\" -frames:v 1 \"{path}\"",
                    TimeSpan.FromSeconds(60), ct, lowPriority: false);
                if (code != 0)
                    await RunAsync($"-hide_banner -loglevel error -y -f lavfi -i color=c={top}:s=2160x3840 -frames:v 1 \"{path}\"", TimeSpan.FromSeconds(60), ct, lowPriority: false);
            }
            if (!File.Exists(path)) continue;
            _live.Update(c =>
            {
                if (c.Backgrounds.Any(b => b.Id == id)) return;
                c.Backgrounds.Insert(c.Backgrounds.Count(b => b.BuiltIn), new LiveBackground { Id = id, Name = name, File = file, Kind = "image", BuiltIn = true, Width = 2160, Height = 3840 });
            });
        }
    }

    // ------------------------------------------------------------------ helpers

    public string ReelDir => Path.Combine(_live.RenderDir, "reels");

    /// <summary>Keeps the made videos in check: those not used for 30 days go, and if they still take more than
    /// 20 GB, the least recently used go first. (A 4K slide is a few MB, so that's thousands of titles.)</summary>
    public const long MaxBytes = 20L * 1024 * 1024 * 1024;
    public void Tidy()
    {
        try
        {
            var files = new DirectoryInfo(_live.RenderDir).GetFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                bool old = DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(30);
                if (!old && total <= MaxBytes) continue;
                total -= f.Length;
                f.Delete();
            }
        }
        catch { }
    }

    /// <summary>Deletes slideshow rounds no screen is playing (made more than 10 minutes ago). All of them at start.</summary>
    public void TidyReels(IReadOnlyCollection<string> inUse, bool all = false)
    {
        try
        {
            if (!Directory.Exists(ReelDir)) return;
            foreach (var f in new DirectoryInfo(ReelDir).GetFiles())
                if (all || (!inUse.Contains("reels/" + f.Name) && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(10))) f.Delete();
        }
        catch { }
    }

    /// <summary>The path a screen is given for a made video: relative to the renders folder ("x.mp4", "reels/x.mp4").</summary>
    public string Rel(string file) => Path.GetRelativePath(_live.RenderDir, file).Replace('\\', '/');

    private static void Touch(string f) { try { File.SetLastWriteTimeUtc(f, DateTime.UtcNow); } catch { } }

    private static string Stamp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "-";
        try { var fi = new FileInfo(path); return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; } catch { return path; }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20].ToLowerInvariant();

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unknown error";
}
