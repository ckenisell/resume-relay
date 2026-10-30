using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

namespace ResumeRelay.Live;

/// <summary>The Live Poster tab's API, and the few calls the screen apps make.</summary>
public static class LiveApi
{
    private static readonly Regex MediaName = new(@"^(reels/)?[0-9a-f]{20}\.mp4$", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> ImageTypes = new() { ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp" };
    private static readonly Dictionary<string, (byte[] Jpg, DateTime At)> Previews = new();

    public record PairBody(string Code, string Name, List<string>? Players, string? Mount, string? Resolution);
    public record ScreenBody(string? Name, List<string>? Players, string? Mount, string? Resolution, string? Codec);
    public record HelloBody(string DeviceKey, string? Platform, string? Model, string? Version);
    public record SettingsBody(int StopWaitSeconds, int SlideSeconds, double AcclaimedMin, LiveSet NowPlaying, LiveSet ComingSoon, LiveSet RecentlyWatched, LiveSet Acclaimed, List<LiveFolder>? Folders);
    public record OrderBody(List<string> Ids);
    public record TitleBody(string Title);

    public static void Map(WebApplication app, Store store, LiveStore live, LiveService svc, RelayEngine engine)
    {
        var renderer = svc.Renderer;

        // ---------------------------------------------------------------- dashboard
        app.MapGet("/api/live", () =>
        {
            var cfg = live.Get();
            var devices = store.Devices();
            return Results.Ok(new
            {
                Renderer = new
                {
                    renderer.Ready, renderer.Status, renderer.Encoder, renderer.Queued, renderer.Working, renderer.LastError, renderer.Downloading,
                    CanDownload = OperatingSystem.IsWindows() && renderer.Ffmpeg == null,
                },
                Config = new { cfg.StopWaitSeconds, cfg.SlideSeconds, cfg.AcclaimedMin, cfg.NowPlaying, cfg.ComingSoon, cfg.RecentlyWatched, cfg.Acclaimed, cfg.Folders },
                PlexFolders = store.Plex().Mappings.Select(m => new LiveFolder { ZidooFolder = m.ZidooFolder, LocalFolder = m.PlexFolder }),
                Screens = cfg.Screens.Select(s =>
                {
                    var rt = svc.RuntimeOf(s.Id);
                    return new
                    {
                        s.Id, s.Name, s.Platform, s.Model, s.AppVersion, s.Players, s.Mount, s.Resolution, s.Codec, VideoCodec = LiveService.CodecOf(s),
                        AppSays = rt.AppSays, rt.AppSaysUtc,
                        Online = rt.LastSeenUtc is { } seen && DateTime.UtcNow - seen < TimeSpan.FromSeconds(20),
                        rt.LastSeenUtc, rt.Mode, rt.Title, rt.PosterSource, rt.Note, rt.RoundTitles, Version = rt.Url,
                    };
                }),
                Pairs = svc.Pairs().Select(p => new { p.Code, p.Platform, p.Model, p.SeenUtc }),
                Backgrounds = cfg.Backgrounds.Select(b => new { b.Id, b.Name, b.Kind, b.BuiltIn, b.Width, b.Height, b.DurationMs }),
                Coming = cfg.Coming.Select(c => new { c.Id, c.Title, c.Width, c.Height }),
                AcclaimedNote = svc.AcclaimedNote,
                AcclaimedCount = svc.Acclaimed.Count,
                Players = devices.Where(d => d.Enabled).Select(d => new { d.Id, d.Name, Kind = d.IsPlex ? "plex" : "zidoo" }),
            });
        });

        app.MapPost("/api/live/settings", (SettingsBody b) =>
        {
            var cfg = live.Update(c =>
            {
                c.StopWaitSeconds = Math.Clamp(b.StopWaitSeconds, 0, 3600);
                c.SlideSeconds = Math.Clamp(b.SlideSeconds, 5, 120);
                c.AcclaimedMin = Math.Clamp(Math.Round(b.AcclaimedMin * 2) / 2, 1, 10);   // in steps of 0.5 (the tab's list)
                c.NowPlaying = b.NowPlaying; c.ComingSoon = b.ComingSoon; c.RecentlyWatched = b.RecentlyWatched; c.Acclaimed = b.Acclaimed;
                if (b.Folders != null) c.Folders = b.Folders.Where(f => f.ZidooFolder.Trim().Length > 0 && f.LocalFolder.Trim().Length > 0)
                    .Select(f => new LiveFolder { ZidooFolder = f.ZidooFolder.Trim(), LocalFolder = f.LocalFolder.Trim() }).ToList();
            });
            if (b.Folders != null) svc.ForgetPosters();
            // Changed settings show at once: each screen takes the new slideshow as soon as it's made.
            foreach (var sc in cfg.Screens) svc.RuntimeOf(sc.Id).WantNext = true;
            _ = svc.RefreshAcclaimedAsync();
            return Results.Ok(new { cfg.StopWaitSeconds, cfg.SlideSeconds, cfg.AcclaimedMin });
        });

        // ---- screens
        app.MapPost("/api/live/pair", (PairBody b) =>
        {
            var (screen, err) = svc.Pair(b.Code, b.Name, b.Players ?? new(), b.Mount ?? "", b.Resolution ?? "4k");
            return screen == null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { screen.Id });
        });
        app.MapPatch("/api/live/screens/{id}", (string id, ScreenBody b) =>
        {
            bool found = false;
            live.Update(c =>
            {
                if (c.Screens.FirstOrDefault(s => s.Id == id) is not { } s) return;
                found = true;
                if (b.Name is { } n && n.Trim().Length > 0) s.Name = n.Trim();
                if (b.Players != null) s.Players = b.Players.Distinct().ToList();
                if (b.Mount is "portrait" or "left" or "right" && b.Mount != s.Mount) { s.Mount = b.Mount; svc.RuntimeOf(id).TestUntil = DateTime.UtcNow.AddSeconds(20); }
                if (b.Resolution is "4k" or "1080") s.Resolution = b.Resolution;
                if (b.Codec is "auto" or "hevc" or "h264") s.Codec = b.Codec;
            });
            if (found) { var rt = svc.RuntimeOf(id); rt.IdleUrl = ""; rt.IdleKey = ""; rt.Shown = null; }
            return found ? Results.Ok() : Results.NotFound();
        });
        app.MapDelete("/api/live/screens/{id}", (string id) =>
        {
            live.Update(c => c.Screens.RemoveAll(s => s.Id == id));
            return Results.Ok();
        });
        app.MapPost("/api/live/screens/{id}/test", (string id) =>
        {
            svc.RuntimeOf(id).TestUntil = DateTime.UtcNow.AddSeconds(30);
            return Results.Ok();
        });
        // What the screen shows now, as a small upright picture.
        app.MapGet("/api/live/screens/{id}/preview", async (string id, CancellationToken ct) =>
        {
            var s = live.Get().Screens.FirstOrDefault(x => x.Id == id);
            var rt = svc.RuntimeOf(id);
            if (s == null || rt.Url.Length == 0) return Results.NotFound();
            string key = rt.Url + "|" + s.Mount;
            lock (Previews) if (Previews.TryGetValue(key, out var hit)) return Results.File(hit.Jpg, "image/jpeg");
            string video = Path.Combine(live.RenderDir, rt.Url);
            // A slideshow is caught with its first poster fully in (just after the fade).
            var jpg = await renderer.FrameAsync(video, s.Mount, rt.Mode == "idle" ? 2.5 : 0.5, ct);
            if (jpg == null) return Results.NotFound();
            lock (Previews) { if (Previews.Count > 200) Previews.Clear(); Previews[key] = (jpg, DateTime.UtcNow); }
            return Results.File(jpg, "image/jpeg");
        });

        // ---- backgrounds
        app.MapPost("/api/live/backgrounds", async (HttpRequest req, string? name, CancellationToken ct) =>
        {
            string type = (req.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            string? ext = type == "video/mp4" ? ".mp4" : ImageTypes.GetValueOrDefault(type);
            if (ext == null) return Results.BadRequest(new { Error = "Choose a PNG, JPG or WebP image, or an MP4 video." });
            string id = "bg-" + Guid.NewGuid().ToString("N")[..10];
            string file = id + ext, path = Path.Combine(live.BackgroundDir, file);
            if (await SaveBodyAsync(req, path, 2L * 1024 * 1024 * 1024, ct) is { } err) return Results.BadRequest(new { Error = err });
            int w, h, ms = 0;
            if (ext == ".mp4")
            {
                (w, h, ms) = await renderer.ProbeAsync(path, ct);
                if (w == 0) { File.Delete(path); return Results.BadRequest(new { Error = "That video can't be read." }); }
            }
            else
            {
                (w, h) = ImageInfo.Size(path);
                if (w == 0) { File.Delete(path); return Results.BadRequest(new { Error = "That image can't be read." }); }
            }
            string title = (name ?? "").Trim();
            if (title.Length == 0) title = "Background";
            live.Update(c => c.Backgrounds.Add(new LiveBackground { Id = id, Name = title, File = file, Kind = ext == ".mp4" ? "video" : "image", Width = w, Height = h, DurationMs = ms }));
            return Results.Ok(new { Id = id, Width = w, Height = h, DurationMs = ms });
        });
        app.MapPatch("/api/live/backgrounds/{id}", (string id, TitleBody b) =>
        {
            live.Update(c => { if (c.Backgrounds.FirstOrDefault(x => x.Id == id) is { } bg && b.Title.Trim().Length > 0) bg.Name = b.Title.Trim(); });
            return Results.Ok();
        });
        app.MapDelete("/api/live/backgrounds/{id}", (string id) =>
        {
            LiveBackground? gone = null;
            live.Update(c =>
            {
                gone = c.Backgrounds.FirstOrDefault(b => b.Id == id && !b.BuiltIn);
                if (gone == null) return;
                c.Backgrounds.Remove(gone);
                // Sets that used it go back to their placeholder.
                foreach (var (set, builtin) in new[] { (c.NowPlaying, "builtin-nowshowing"), (c.ComingSoon, "builtin-comingsoon"), (c.RecentlyWatched, "builtin-recent"), (c.Acclaimed, "builtin-acclaimed") })
                    if (set.BackgroundId == id) set.BackgroundId = builtin;
            });
            if (gone == null) return Results.BadRequest(new { Error = "The placeholders can't be deleted." });
            try { File.Delete(Path.Combine(live.BackgroundDir, gone.File)); } catch { }
            return Results.Ok();
        });
        app.MapGet("/api/live/backgrounds/{id}/preview", async (string id, CancellationToken ct) =>
        {
            var bg = live.Get().Backgrounds.FirstOrDefault(b => b.Id == id);
            if (bg == null) return Results.NotFound();
            string path = live.BackgroundPath(bg);
            string key = "bg|" + id + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
            lock (Previews) if (Previews.TryGetValue(key, out var hit)) return Results.File(hit.Jpg, "image/jpeg");
            var jpg = await renderer.PreviewAsync(bg, null, ct);
            if (jpg == null) return Results.NotFound();
            lock (Previews) Previews[key] = (jpg, DateTime.UtcNow);
            return Results.File(jpg, "image/jpeg");
        });

        // ---- Coming Soon posters
        app.MapPost("/api/live/coming", async (HttpRequest req, string? title, CancellationToken ct) =>
        {
            string type = (req.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (ImageTypes.GetValueOrDefault(type) is not { } ext) return Results.BadRequest(new { Error = "Choose a PNG, JPG or WebP poster." });
            var item = new ComingSoonItem { Title = (title ?? "").Trim() };
            item.File = item.Id + ext;
            string path = Path.Combine(live.ComingDir, item.File);
            if (await SaveBodyAsync(req, path, 100L * 1024 * 1024, ct) is { } err) return Results.BadRequest(new { Error = err });
            (item.Width, item.Height) = ImageInfo.Size(path);
            if (item.Width == 0) { File.Delete(path); return Results.BadRequest(new { Error = "That image can't be read." }); }
            if (item.Title.Length == 0) item.Title = "Coming Soon";
            live.Update(c => c.Coming.Add(item));
            return Results.Ok(new { item.Id, item.Width, item.Height });
        });
        app.MapGet("/api/live/coming/{id}/image", (string id) =>
        {
            var c = live.Get().Coming.FirstOrDefault(x => x.Id == id);
            if (c == null || !File.Exists(live.ComingPath(c))) return Results.NotFound();
            string ext = Path.GetExtension(c.File);
            return Results.File(live.ComingPath(c), ImageTypes.FirstOrDefault(t => t.Value == ext).Key ?? "image/jpeg");
        });
        app.MapPatch("/api/live/coming/{id}", (string id, TitleBody b) =>
        {
            live.Update(c => { if (c.Coming.FirstOrDefault(x => x.Id == id) is { } item && b.Title.Trim().Length > 0) item.Title = b.Title.Trim(); });
            return Results.Ok();
        });
        app.MapDelete("/api/live/coming/{id}", (string id) =>
        {
            ComingSoonItem? gone = null;
            live.Update(c => { gone = c.Coming.FirstOrDefault(x => x.Id == id); if (gone != null) c.Coming.Remove(gone); });
            if (gone != null) try { File.Delete(live.ComingPath(gone)); } catch { }
            return Results.Ok();
        });
        app.MapPost("/api/live/coming/order", (OrderBody b) =>
        {
            live.Update(c => c.Coming = c.Coming.OrderBy(x => b.Ids.IndexOf(x.Id) is var i && i >= 0 ? i : int.MaxValue).ToList());
            return Results.Ok();
        });

        // ---- Highly Acclaimed
        app.MapGet("/api/live/acclaimed", () => Results.Ok(new
        {
            Note = svc.AcclaimedNote,
            Titles = svc.Acclaimed.OrderByDescending(a => a.Score).ThenBy(a => a.Title)
                .Select(a => new { a.Title, a.Kind, a.Score, a.Source }),
        }));
        app.MapPost("/api/live/acclaimed/refresh", async () => { await svc.RefreshAcclaimedAsync(); return Results.Ok(new { Note = svc.AcclaimedNote }); });

        // ---- posters and ffmpeg
        app.MapPost("/api/live/posters/forget", () => { svc.ForgetPosters(); return Results.Ok(); });
        app.MapPost("/api/live/ffmpeg/download", async (CancellationToken ct) =>
        {
            _ = Task.Run(() => renderer.DownloadFfmpegAsync(CancellationToken.None));
            await Task.Delay(300, ct);
            return Results.Ok();
        });
        // Where a title's poster comes from (troubleshooting): /api/live/poster?url=smb://…&tv=1
        app.MapGet("/api/live/poster", async (string url, int? tv, bool? series, CancellationToken ct) =>
        {
            var tag = Regex.Match(PathMapper.FileName(url), @"\bS(\d{1,2})E\d", RegexOptions.IgnoreCase);
            bool ep = (tv ?? 0) == 1 || tag.Success;
            var r = await svc.Finder.FindAsync(new PosterRequest(url, "", "", ep, tag.Success ? int.Parse(tag.Groups[1].Value) : 0, series == true, ""), ct);
            return r == null ? Results.NotFound(new { Error = "No poster found (folder, Plex or Zidoo)." })
                : Results.Ok(new { r.Source, r.Width, r.Height, Folder = svc.Finder.LocalPathOf(url) });
        });

        // ---------------------------------------------------------------- screen apps
        app.MapPost("/api/screen/hello", (HelloBody b) =>
        {
            if (string.IsNullOrWhiteSpace(b.DeviceKey) || b.DeviceKey.Length > 100) return Results.BadRequest(new { Error = "deviceKey is needed." });
            var (screen, pending) = svc.Hello(b.DeviceKey, Clean(b.Platform), Clean(b.Model), Clean(b.Version));
            return screen != null
                ? Results.Ok(new { Paired = true, screen.Token, screen.Name, Code = "" })
                : Results.Ok(new { Paired = false, Token = "", Name = "", pending!.Code });
        });
        app.MapGet("/api/screen/state", (string token, string? ended) =>
        {
            var state = svc.StateFor(token ?? "", ended ?? "");
            return state == null ? Results.Json(new { Paired = false }, statusCode: 401) : Results.Ok(state);
        });
        app.MapGet("/api/screen/report", (string token, string msg) =>
            svc.Report(token ?? "", msg ?? "") ? Results.Ok() : Results.Json(new { Paired = false }, statusCode: 401));
        app.MapGet("/live/media/{**file}", (string file) =>
        {
            if (!MediaName.IsMatch(file)) return Results.NotFound();
            string path = Path.Combine(live.RenderDir, file);
            return File.Exists(path) ? Results.File(path, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
        });
        string screenHtml;
        using (var s = typeof(LiveApi).Assembly.GetManifestResourceStream("screen.html")!)
        using (var r = new StreamReader(s))
            screenHtml = r.ReadToEnd();
        app.MapGet("/screen", () => Results.Content(screenHtml, "text/html; charset=utf-8"));
    }

    private static string Clean(string? s) => (s ?? "").Trim() is var t && t.Length > 60 ? t[..60] : (s ?? "").Trim();

    /// <summary>Streams an upload straight to a file (up to the limit), so big 4K videos never sit in memory.</summary>
    private static async Task<string?> SaveBodyAsync(HttpRequest req, string path, long max, CancellationToken ct)
    {
        if (req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = max;
        string tmp = path + ".part";
        try
        {
            long total = 0;
            await using (var file = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await req.Body.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > max) { file.Close(); File.Delete(tmp); return $"That file is too big (the limit is {max / 1024 / 1024:N0} MB)."; }
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (total == 0) { File.Delete(tmp); return "That file is empty."; }
            File.Move(tmp, path, true);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            return "The upload didn't finish: " + ex.Message;
        }
    }
}
