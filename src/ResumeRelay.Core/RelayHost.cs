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
    public static string Version =>
        (Assembly.GetEntryAssembly() ?? typeof(RelayHost).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

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

        var app = builder.Build();
        MapApi(app, store, app.Services.GetRequiredService<RelayEngine>(), options.Host);
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
                string power = l.Power switch
                {
                    PowerAction.TurningOn => "turningOn",
                    PowerAction.TurningOff => "turningOff",
                    _ => l.Online ? "on" : "off",
                };
                return new
                {
                    dev.Id, dev.Name, dev.Ip, dev.Port, dev.Enabled, dev.Model, dev.Firmware, dev.Mac,
                    Online = online,
                    l.LastSeenUtc,
                    Power = power,
                    CanWake = new[] { dev.NetMac, dev.WifiMac, dev.Mac }.Any(m => WakeOnLan.ParseMac(m) != null),
                    HomeTheater = l.Online ? l.HomeTheaterRunning : null,
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
            e.Id, e.TimeUtc, e.Title, e.Subtitle, e.FromName, e.PositionMs, e.DurationMs, e.Finished, e.Reset,
            Poster = e.Poster.Length > 0 ? "/api/poster/" + e.Poster : null,
            e.Targets,
        })));

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

    private sealed record AddDeviceRequest(string? Ip, int? Port);
    private sealed record UpdateDeviceRequest(string? Name, bool? Enabled);
    private sealed record AutostartRequest(bool Enabled);
    private sealed record PowerRequest(bool On, bool? LaunchHomeTheater);
}
