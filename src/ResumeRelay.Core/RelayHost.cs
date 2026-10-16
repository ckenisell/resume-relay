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
            var devices = store.Devices().Select(dev =>
            {
                var l = engine.LiveOf(dev.Id);
                return new
                {
                    dev.Id, dev.Name, dev.Ip, dev.Port, dev.Enabled, dev.Model, dev.Firmware, dev.Mac,
                    Online = dev.Enabled && l.Online,
                    l.LastSeenUtc,
                    Playing = l.Playing == null ? null : new
                    {
                        l.Playing.Title,
                        File = PathMapper.FileName(l.Playing.Path),
                        l.Playing.PositionMs,
                        l.Playing.DurationMs,
                        l.Playing.Paused,
                        Shareable = l.Playing.SourceUrl.Length > 0,
                    },
                };
            }).ToList();

            var names = store.Devices().ToDictionary(x => x.Id, x => x.Name);
            var pending = store.Pending().OrderBy(x => x.CreatedUtc).Select(x => new
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

        app.MapGet("/api/activity", (int? n) => Results.Ok(store.Activity(Math.Clamp(n ?? 200, 1, 1000))));

        app.MapPost("/api/scan", async (CancellationToken ct) =>
        {
            var found = await engine.RescanAsync(addNew: true, ct);
            return Results.Ok(new { Found = found.Count, New = found.Count(f => f.IsNew), Subnets = Discovery.LocalSubnets() });
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
            store.RemovePending(key) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/pending/retry", () => { engine.NudgeQueue(); return Results.Ok(); });
    }

    private sealed record AddDeviceRequest(string? Ip, int? Port);
    private sealed record UpdateDeviceRequest(string? Name, bool? Enabled);
    private sealed record AutostartRequest(bool Enabled);
}
