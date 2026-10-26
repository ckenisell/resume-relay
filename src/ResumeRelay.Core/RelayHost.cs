using System.Reflection;
using System.Text.Json.Serialization;

namespace ResumeRelay;

/// <summary>What the host program (Linux service or Windows app) can do beyond the shared core.</summary>
public interface IHostIntegration
{
    /// <summary>Shown in the page footer, e.g. "Linux service" or "Windows app".</summary>
    string Edition { get; }

    /// <summary>True when the host can start itself with the operating system (the Windows app).</summary>
    bool SupportsAutostart { get; }
    bool GetAutostart();
    void SetAutostart(bool enabled);
}

/// <summary>Used by the Linux service, where systemd already starts it with the computer.</summary>
public sealed class ServiceHostIntegration : IHostIntegration
{
    public string Edition => "Linux service";
    public bool SupportsAutostart => false;
    public bool GetAutostart() => true;
    public void SetAutostart(bool enabled) { }
}

public sealed record RelayOptions(string DataDirectory, int Port, IHostIntegration Host);

/// <summary>Builds the web server, the API and the relay engine. Both editions call this.</summary>
public static class RelayHost
{
    /// <summary>The build label, e.g. "0.3.1" or "0.3.1-beta.1" (CI stamps the release tag into InformationalVersion).</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(RelayHost).Assembly;
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>Default data folder: %LocalAppData%\ResumeRelay on Windows, ~/.local/share/ResumeRelay on Linux,
    /// unless RESUMERELAY_DATA says otherwise.</summary>
    public static string DefaultDataDirectory() =>
        Environment.GetEnvironmentVariable("RESUMERELAY_DATA") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ResumeRelay");

    public static int DefaultPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("RESUMERELAY_PORT"), out int p) && p > 0 ? p : 8090;

    public static WebApplication Build(string[] args, RelayOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // All addresses, so the page can also be opened from a phone on the same network.
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var store = new Store(options.DataDirectory);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(options.Host);
        builder.Services.AddSingleton<RelayEngine>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayEngine>());
        builder.Services.AddSingleton<ProgressService>();

        var app = builder.Build();
        MapApi(app, store, app.Services.GetRequiredService<RelayEngine>(), options.Host);
        MapProgressApi(app, app.Services.GetRequiredService<ProgressService>(), app.Lifetime.ApplicationStopping);
        PlexApi.Map(app, store, app.Services.GetRequiredService<RelayEngine>());
        MapIcons(app, store);
        return app;
    }

    private static void MapApi(WebApplication app, Store store, RelayEngine engine, IHostIntegration host)
    {
        string indexHtml;
        using (var s = typeof(RelayHost).Assembly.GetManifestResourceStream("index.html")!)
        using (var r = new StreamReader(s))
            indexHtml = r.ReadToEnd();
        app.MapGet("/", () => Results.Content(indexHtml, "text/html; charset=utf-8"));

        app.MapGet("/api/state", () =>
        {
            var pendingAll = store.Pending().OrderBy(x => x.CreatedUtc).ToList();
            var devices = store.Devices().Select(dev =>
            {
                var l = engine.LiveOf(dev.Id);
                bool online = dev.Enabled && l.Online;
                string power = dev.IsPlex ? (l.Online ? "on" : "off") : l.Power switch
                {
                    PowerAction.TurningOn => "turningOn",
                    PowerAction.TurningOff => "turningOff",
                    _ => l.Online ? "on" : "off",
                };
                return new
                {
                    dev.Id, Kind = dev.IsPlex ? "plex" : "zidoo", dev.Name, dev.Ip, dev.Port, dev.Enabled, dev.Model, dev.Firmware, dev.Mac, dev.HtVersion, dev.HtVersionCode,
                    PlexUser = dev.IsPlex ? dev.PlexUserTitle : null,
                    Online = online,
                    l.LastSeenUtc,
                    Power = power,
                    CanWake = !dev.IsPlex && new[] { dev.NetMac, dev.WifiMac, dev.Mac }.Any(m => WakeOnLan.ParseMac(m) != null),
                    HomeTheater = l.Online && !dev.IsPlex ? l.HomeTheaterRunning : null,
                    Playing = l.Playing == null || !dev.Enabled ? null : new
                    {
                        l.Playing.Title,
                        l.Playing.Subtitle,
                        File = PathMapper.FileName(l.Playing.Path),
                        l.Playing.PositionMs,
                        l.Playing.DurationMs,
                        l.Playing.Paused,
                        Shareable = l.Playing.SourceUrl.Length > 0,
                        Poster = l.Playing.Poster.Length > 0 ? "/api/poster/" + l.Playing.Poster : null,
                    },
                    Waiting = pendingAll.Where(p => p.TargetId == dev.Id).Select(p => new
                    {
                        p.Key, p.Title, p.FromName, p.PositionMs, p.CreatedUtc,
                        Why = p.WaitedFor == "ht" ? "Home Theater isn't open" : p.LastNote.StartsWith("retry") ? p.LastNote : "",
                    }),
                };
            }).ToList();

            var names = store.Devices().ToDictionary(x => x.Id, x => x.Name);
            var pending = pendingAll.Select(x => new
            {
                x.Key, x.Title, x.FromName, To = names.GetValueOrDefault(x.TargetId, "?"),
                x.PositionMs, x.CreatedUtc, x.LastNote,
            });

            return Results.Ok(new
            {
                Version,
                host.Edition,
                Autostart = host.SupportsAutostart ? (bool?)host.GetAutostart() : null,
                Settings = store.Settings(),
                Devices = devices,
                Plex = PlexApi.Summary(store),
                Icons = new { Zidoo = IconFile(store, "zidoo") != null, Plex = IconFile(store, "plex") != null, V = IconVersion(store) },
                Pending = pending,
                DataFolder = store.DataDirectory,
            });
        });

        // The full journal (system=false) or only the system rows the Activity tab shows between stop cards.
        app.MapGet("/api/activity", (int? n, bool? system) =>
        {
            var all = store.Activity(1000).Where(e => system != true || e.System);
            return Results.Ok(all.Take(Math.Clamp(n ?? 200, 1, 1000)));
        });

        app.MapGet("/api/events", (int? n) => Results.Ok(store.Events(Math.Clamp(n ?? 100, 1, 300)).Select(e => new
        {
            e.Id, e.TimeUtc, e.Title, e.Subtitle, e.FileName, e.Manual, e.Series, CanReset = e.SourceUrl.Length > 0, e.FromName,
            FromKind = e.FromKind.Length > 0 ? e.FromKind : e.Manual ? "" : "zidoo", e.PositionMs, e.DurationMs, e.Finished, e.Reset,
            Poster = e.Poster.Length > 0 ? "/api/poster/" + e.Poster : null,
            e.Targets,
        })));

        // "Reset Progress" on an Activity card.
        app.MapPost("/api/events/{id}/reset", (string id) =>
        {
            var (card, err) = engine.ResetEverywhere(id);
            return card == null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { card.Id });
        });

        app.MapGet("/api/poster/{name}", (string name) =>
        {
            // Only names this app created: 16 hex characters and an image extension.
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-f0-9]{16}\.(jpg|png|webp)$")) return Results.NotFound();
            string path = Path.Combine(store.PosterDirectory, name);
            if (!File.Exists(path)) return Results.NotFound();
            string type = name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg";
            return Results.File(path, type, enableRangeProcessing: false);
        });

        app.MapPost("/api/devices/{id}/power", (string id, PowerRequest req) =>
        {
            string? err = req.On ? engine.StartPowerOn(id, req.LaunchHomeTheater ?? true) : engine.StartPowerOff(id);
            return err == null ? Results.Ok() : Results.BadRequest(new { Error = err });
        });

        app.MapPost("/api/devices/{id}/launch", async (string id, CancellationToken ct) =>
        {
            string? err = await engine.LaunchHomeTheaterAsync(id, ct);
            return err == null ? Results.Ok() : Results.BadRequest(new { Error = err });
        });

        app.MapPost("/api/scan", async (CancellationToken ct) =>
        {
            try
            {
                var found = await engine.RescanAsync(addNew: true, ct);
                return Results.Ok(new { Found = found.Count, New = found.Count(f => f.IsNew), Subnets = Discovery.LocalSubnets() });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Say what went wrong, on the page and in the Activity log, instead of a bare "500".
                store.Log(ActivityLevel.Error, $"Network scan failed: {ex.GetType().Name}: {ex.Message}");
                return Results.Json(new { Error = $"The network scan failed ({ex.Message}). You can still add players by IP address." }, statusCode: 500);
            }
        });

        app.MapPost("/api/devices", async (AddDeviceRequest req, CancellationToken ct) =>
        {
            string ip = (req.Ip ?? "").Trim();
            if (!System.Net.IPAddress.TryParse(ip, out _)) return Results.BadRequest(new { Error = "Enter an IP address like 10.0.0.39." });
            var (dev, err) = await engine.AddByIpAsync(ip, req.Port is > 0 ? req.Port.Value : Discovery.ZidooPort, ct);
            return dev == null ? Results.BadRequest(new { Error = err }) : Results.Ok(dev);
        });

        app.MapPatch("/api/devices/{id}", (string id, UpdateDeviceRequest req) =>
            store.UpdateDevice(id, req.Name, req.Enabled) ? Results.Ok() : Results.NotFound());

        app.MapDelete("/api/devices/{id}", (string id) =>
            store.RemoveDevice(id) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/settings", (Settings s) =>
        {
            bool wasWatchOnly = store.Settings().WatchOnly;
            store.SaveSettings(s);
            if (wasWatchOnly != s.WatchOnly)
                store.Log(ActivityLevel.Info, s.WatchOnly ? "Watch-only mode turned ON: bookmarks will be logged, not copied." : "Watch-only mode turned OFF: bookmarks will now be copied.");
            return Results.Ok(store.Settings());
        });

        app.MapPost("/api/autostart", (AutostartRequest req) =>
        {
            if (!host.SupportsAutostart) return Results.BadRequest(new { Error = "This edition is started by the system." });
            host.SetAutostart(req.Enabled);
            return Results.Ok(new { Enabled = host.GetAutostart() });
        });

        app.MapDelete("/api/pending", (string key) =>
            engine.Discard(key) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/pending/retry", () => { engine.NudgeQueue(); return Results.Ok(); });
    }

    // ---------- badge icons (Settings: "Activity badges") ----------

    private static readonly string[] IconKinds = { "zidoo", "plex" };
    private static readonly Dictionary<string, string> IconTypes = new()
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
    };

    private static string IconDir(Store store) => Path.Combine(store.DataDirectory, "icons");

    private static string? IconFile(Store store, string kind) =>
        Directory.Exists(IconDir(store)) ? Directory.EnumerateFiles(IconDir(store), kind + ".*").FirstOrDefault() : null;

    private static long IconVersion(Store store) =>
        IconKinds.Select(k => IconFile(store, k)).Where(f => f != null).Select(f => File.GetLastWriteTimeUtc(f!).Ticks / 10_000_000).DefaultIfEmpty(0).Max();

    /// <summary>The person's own picture for the Zidoo or Plex badge, kept in the data folder (never shipped with the app).</summary>
    private static void MapIcons(WebApplication app, Store store)
    {
        app.MapGet("/api/icon/{kind}", (string kind) =>
        {
            if (!IconKinds.Contains(kind) || IconFile(store, kind) is not { } f) return Results.NotFound();
            // An SVG is served as an image only (it can't run scripts when used in <img>), with a strict policy anyway.
            return Results.File(f, IconTypes.GetValueOrDefault(Path.GetExtension(f).ToLowerInvariant(), "application/octet-stream"));
        });
        app.MapPost("/api/icon/{kind}", async (string kind, HttpRequest req) =>
        {
            if (!IconKinds.Contains(kind)) return Results.NotFound();
            string type = (req.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            string? ext = IconTypes.FirstOrDefault(t => t.Value == type).Key;
            if (ext == null) return Results.BadRequest(new { Error = "Choose a PNG, JPG, WebP, GIF, ICO or SVG image." });
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length == 0) return Results.BadRequest(new { Error = "That file is empty." });
            if (ms.Length > 512 * 1024) return Results.BadRequest(new { Error = "Choose an image under 512 KB (a small icon is plenty)." });
            Directory.CreateDirectory(IconDir(store));
            if (IconFile(store, kind) is { } old) File.Delete(old);
            await File.WriteAllBytesAsync(Path.Combine(IconDir(store), kind + ext), ms.ToArray());
            return Results.Ok();
        });
        app.MapDelete("/api/icon/{kind}", (string kind) =>
        {
            if (IconKinds.Contains(kind) && IconFile(store, kind) is { } f) File.Delete(f);
            return Results.Ok();
        });
    }

    /// <summary>The Progress Reset tab.</summary>
    private static void MapProgressApi(WebApplication app, ProgressService progress, CancellationToken stopping)
    {
        // The page passes the version it already has; an unchanged list isn't sent again.
        app.MapGet("/api/progress", (long? v) =>
            v == progress.Version ? Results.Ok(new { Unchanged = true, progress.Version, progress.Reading, Status = progress.StatusLines }) : Results.Ok(progress.Snapshot()));
        app.MapGet("/api/watched", (string? show, string? players, string? q, string? sort, int? page, int? pageSize) =>
            Results.Ok(progress.WatchSnapshot(show ?? "different",
                (players ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), q ?? "",
                sort ?? "title", page ?? 1, pageSize ?? 500)));
        app.MapPost("/api/watched/set", async (WatchSetRequest req) =>
        {
            var r = await progress.SetWatchedAsync(req.Key ?? "", req.DeviceIds ?? new List<string>(), req.Watched);
            return r.Count == 0 ? Results.BadRequest(new { Error = "That file is no longer in the list. Read the players again." }) : Results.Ok(r);
        });
        // e.g. /api/progress/diagnostics?q=mario : what each player's library listing held, for troubleshooting.
        app.MapGet("/api/progress/diagnostics", (string? q) => Results.Text(progress.DiagnosticsText(q ?? ""), "text/plain; charset=utf-8"));
        app.MapPost("/api/progress/read", (ProgressReadRequest req) =>
            progress.StartRead(req.DeviceIds ?? new List<string>(), stopping) is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        app.MapPost("/api/progress/reset", async (ProgressKeyRequest req) =>
            await progress.ResetFileAsync(req.Key ?? "") is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        app.MapPost("/api/progress/reset-series", async (ProgressKeyRequest req) =>
            await progress.ResetSeriesAsync(req.Key ?? "") is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
    }

    private sealed record ProgressReadRequest(List<string>? DeviceIds);
    private sealed record ProgressKeyRequest(string? Key);
    private sealed record WatchSetRequest(string? Key, List<string>? DeviceIds, bool Watched);
    private sealed record AddDeviceRequest(string? Ip, int? Port);
    private sealed record UpdateDeviceRequest(string? Name, bool? Enabled);
    private sealed record AutostartRequest(bool Enabled);
    private sealed record PowerRequest(bool On, bool? LaunchHomeTheater);
}
