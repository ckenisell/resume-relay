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
    /// <summary>"Docker" inside a container (the update notice then explains how to update the image).</summary>
    public string Edition => File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("RESUMERELAY_DOCKER") == "1" ? "Docker" : "Linux service";
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
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<CatchUpService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CatchUpService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        builder.Services.AddSingleton<WatchedSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchedSyncService>());
        builder.Services.AddSingleton<KaleidescapeService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KaleidescapeService>());
        builder.Services.AddSingleton<AppleTvService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AppleTvService>());
        builder.Services.AddSingleton<Live.LiveStore>();
        builder.Services.AddSingleton<Live.PosterFinder>();
        builder.Services.AddSingleton<Live.Renderer>();
        builder.Services.AddSingleton<Live.LiveService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Live.LiveService>());

        var app = builder.Build();
        var updates = app.Services.GetRequiredService<UpdateService>();
        var catchUp = app.Services.GetRequiredService<CatchUpService>();
        MapApi(app, store, app.Services.GetRequiredService<RelayEngine>(), options.Host, updates, catchUp);
        MapProgressApi(app, app.Services.GetRequiredService<ProgressService>(), app.Lifetime.ApplicationStopping);
        // Automatic matching for live copying: a Plex or Jellyfin file no folder mapping covers is found on the Zidoos
        // through their last read on Progress Reset.
        app.Services.GetRequiredService<RelayEngine>().FindZidooUrl = app.Services.GetRequiredService<ProgressService>().ZidooUrlForServerPathAsync;
        PlexApi.Map(app, store, app.Services.GetRequiredService<RelayEngine>());
        JellyfinApi.Map(app, store, app.Services.GetRequiredService<RelayEngine>());
        MapIcons(app, store);
        Live.LiveApi.Map(app, store, app.Services.GetRequiredService<Live.LiveStore>(), app.Services.GetRequiredService<Live.LiveService>(),
            app.Services.GetRequiredService<RelayEngine>());
        return app;
    }

    private static void MapApi(WebApplication app, Store store, RelayEngine engine, IHostIntegration host, UpdateService updates, CatchUpService catchUp)
    {
        var kaleidescape = app.Services.GetRequiredService<KaleidescapeService>();
        var appleTv = app.Services.GetRequiredService<AppleTvService>();
        // Back up: players, names, settings, the Plex and Jellyfin setups (with their sign-ins) and badge pictures, as one file.
        app.MapGet("/api/backup", () =>
        {
            var icons = IconKinds.Select(k => (k, f: IconFile(store, k))).Where(x => x.f != null)
                .ToDictionary(x => x.k, x => new BackupIcon(Path.GetExtension(x.f!), Convert.ToBase64String(File.ReadAllBytes(x.f!))));
            var b = new Backup("Resume Relay", Version, DateTime.UtcNow, store.Devices(), store.Settings(), store.Plex(), icons, store.Jellyfin(), store.Kaleidescape().Players, store.AppleTv().Devices);
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(b, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            return Results.File(json, "application/json", $"resume-relay-backup-{DateTime.Now:yyyy-MM-dd}.json");
        });
        app.MapPost("/api/restore", (Backup b) =>
        {
            if (b == null || b.App != "Resume Relay" || b.Devices == null || b.Settings == null)
                return Results.BadRequest(new { Error = "That isn't a Resume Relay backup file." });
            store.Restore(b.Devices, b.Settings, b.Plex ?? new PlexConfig(), b.Jellyfin, b.Kaleidescapes, b.AppleTvs);
            foreach (var k in IconKinds) if (IconFile(store, k) is { } old) File.Delete(old);
            foreach (var (k, icon) in b.Icons ?? new())
                if (IconKinds.Contains(k) && IconTypes.ContainsKey(icon.Ext))
                {
                    Directory.CreateDirectory(IconDir(store));
                    File.WriteAllBytes(Path.Combine(IconDir(store), k + icon.Ext), Convert.FromBase64String(icon.Data));
                }
            engine.ForgetPlexIndex();
            store.Log(ActivityLevel.Info, $"Restored settings from a backup made {b.CreatedUtc.ToLocalTime():g} (version {b.Version}): {b.Devices.Count} player(s).");
            return Results.Ok(new { Players = b.Devices.Count });
        });

        // "Catch up now" (Settings).
        var watchedSync = app.Services.GetRequiredService<WatchedSyncService>();
        app.MapPost("/api/watchedsync/check", () => { watchedSync.CheckSoon(); return Results.Ok(); });
        app.MapPost("/api/catchup", () => catchUp.StartNow() is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        // Check GitHub for a newer version now (Settings: "Check now").
        app.MapPost("/api/update/check", async (CancellationToken ct) =>
        {
            await updates.CheckAsync(ct);
            return Results.Ok(new { Update = updates.Available, updates.CheckedUtc, Error = updates.LastError });
        });
        string indexHtml;
        using (var s = typeof(RelayHost).Assembly.GetManifestResourceStream("index.html")!)
        using (var r = new StreamReader(s))
            indexHtml = r.ReadToEnd();
        app.MapGet("/", () => Results.Content(indexHtml, "text/html; charset=utf-8"));
        // The dashboard's icons (browser tab, home screen, page header): Resume Relay's own, not the screen apps'.
        foreach (var name in new[] { "icon-64.png", "icon-192.png", "apple-touch-icon.png" })
        {
            byte[] png;
            using (var s = typeof(RelayHost).Assembly.GetManifestResourceStream("web-" + name)!)
            using (var m = new MemoryStream()) { s.CopyTo(m); png = m.ToArray(); }
            app.MapGet("/" + name, (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(png, "image/png");
            });
        }
        app.MapGet("/favicon.ico", () => Results.Redirect("/icon-64.png"));

        app.MapGet("/api/state", () =>
        {
            var pendingAll = store.Pending().OrderBy(x => x.CreatedUtc).ToList();
            var devices = store.Devices().Select(dev =>
            {
                var l = engine.LiveOf(dev.Id);
                bool online = dev.Enabled && l.Online;
                string power = dev.IsServerUser ? (l.Online ? "on" : "off") : l.Power switch
                {
                    PowerAction.TurningOn => "turningOn",
                    PowerAction.TurningOff => "turningOff",
                    _ => l.Online ? "on" : "off",
                };
                return new
                {
                    dev.Id, Kind = dev.KindName, dev.Name, dev.Ip, dev.Port, dev.Enabled, dev.Model, dev.Firmware, dev.Mac, dev.HtVersion, dev.HtVersionCode,
                    PlexUser = dev.IsPlex ? dev.PlexUserTitle : dev.IsJellyfin ? dev.JellyfinUserName : null,
                    Online = online,
                    l.LastSeenUtc,
                    Power = power,
                    CanWake = !dev.IsServerUser && new[] { dev.NetMac, dev.WifiMac, dev.Mac }.Any(m => WakeOnLan.ParseMac(m) != null),
                    HomeTheater = l.Online && !dev.IsServerUser ? l.HomeTheaterRunning : null,
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
                // Live Poster only: listed on the Players tab and in Live Poster, never on Progress Reset or Watch Sync.
                Kaleidescapes = kaleidescape.Players().Select(k =>
                {
                    var l = kaleidescape.LiveOf(k.Id);
                    var m = l.Movie;
                    return new
                    {
                        k.Id, Kind = "kaleidescape", k.Name, k.Ip, k.Enabled, k.Model, k.Serial,
                        Online = k.Enabled && l.Online, l.LastSeenUtc, Standby = l.Online && !l.On,
                        Playing = !k.Enabled || m == null || l.Status is not { Playing: true } st ? null : new
                        {
                            Title = KaleidescapeService.Display(m), PositionMs = st.PositionSec * 1000, DurationMs = st.LengthSec * 1000,
                            Paused = st.Play == "paused", Poster = m.BestCover.Length > 0 ? m.BestCover : null,
                        },
                    };
                }),
                // Live Poster only, like Kaleidescapes. Never the pairing keys: only whether it's paired.
                AppleTvs = appleTv.Devices().Select(a =>
                {
                    var l = appleTv.LiveOf(a.Id);
                    var n = l.Now;
                    return new
                    {
                        a.Id, Kind = "appletv", a.Name, a.Ip, a.Enabled, a.Model, Paired = a.Credentials.Length > 0,
                        Online = a.Enabled && l.Online, l.LastSeenUtc, Problem = a.Credentials.Length > 0 && !l.Online ? l.Error : "",
                        Playing = !a.Enabled || n == null ? null : new
                        {
                            Title = n.Display, App = n.AppName.Length > 0 ? n.AppName : n.AppId, PositionMs = n.PositionSec * 1000, DurationMs = n.LengthSec * 1000,
                            n.Paused, FromPlex = n.IsPlex && l.PlexItem != null,
                        },
                    };
                }),
                Plex = PlexApi.Summary(store),
                Jellyfin = JellyfinApi.Summary(store),
                TmdbPosters = Live.Tmdb.Available,   // whether this build has its TMDB key (Settings → About says so)
                Update = store.Settings().CheckUpdates ? updates.Available : null,
                UpdateCheckedUtc = updates.CheckedUtc,
                Health = Health(store, engine, app.Services.GetRequiredService<ProgressService>()),
                CatchUp = new
                {
                    catchUp.Running, catchUp.Status, catchUp.LastRunUtc, catchUp.LastResult,
                    Waiting = catchUp.WaitingFor.Select(id => store.Device(id)?.Name).Where(n => n != null),
                },
                WatchedSync = new { watchedSync.Status, watchedSync.LastRunUtc, watchedSync.LastResult },
                Icons = new { Zidoo = IconFile(store, "zidoo") != null, Plex = IconFile(store, "plex") != null, Jellyfin = IconFile(store, "jellyfin") != null, Kaleidescape = IconFile(store, "kaleidescape") != null, Appletv = IconFile(store, "appletv") != null, Imdb = IconFile(store, "imdb") != null, Tmdb = IconFile(store, "tmdb") != null, V = IconVersion(store) },
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
            e.Id, e.TimeUtc, e.Title, e.Subtitle, e.FileName, e.Manual, e.Series, e.Copied, e.Batch, e.Files, e.Summary, e.Undone,
            CanUndo = e.Undo.Count > 0 && !e.Undone, UndoOf = e.UndoOf.Length > 0 ? e.UndoOf : null, CanReset = e.SourceUrl.Length > 0, e.FromName,
            FromKind = e.FromKind.Length > 0 ? e.FromKind : e.Manual || e.Batch ? "" : "zidoo", e.PositionMs, e.DurationMs, e.Finished, e.Reset,
            Poster = e.Poster.Length > 0 ? "/api/poster/" + e.Poster : null,
            Targets = e.Targets.Select(t => new { t.DeviceId, t.Name, t.Kind, t.State, t.Note }),
        })));

        // Undo on a card: put every file back the way each player held it before.
        // "Show files" on a batch card: each file, what it was set to, and each player's state before and result.
        app.MapGet("/api/events/{id}/files", (string id) =>
        {
            var e = store.Events(300).FirstOrDefault(x => x.Id == id);
            if (e == null || !e.Batch) return Results.NotFound();
            var before = e.Undo.GroupBy(u => (Store.ItemKey(u.SourceUrl), u.DeviceId)).ToDictionary(g => g.Key, g => g.First());
            var results = e.Targets.Where(t => t.Items != null)
                .SelectMany(t => t.Items!.Select(kv => (Key: Store.ItemKey(kv.Key), t, kv.Value))).ToList();
            var keys = (e.Changes?.Select(c => c.Key) ?? Enumerable.Empty<string>())
                .Concat(e.Undo.Select(u => Store.ItemKey(u.SourceUrl))).Concat(results.Select(r => r.Key)).Distinct().ToList();
            var changes = (e.Changes ?? new()).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.ToList());
            static object St(bool watched, int pos) => new { Watched = watched, PositionMs = Math.Max(0, pos) };
            var files = keys.Select(k =>
            {
                var ch = changes.GetValueOrDefault(k);
                string title = ch?.First().Title is { Length: > 0 } t ? t : e.Undo.FirstOrDefault(u => Store.ItemKey(u.SourceUrl) == k)?.Title ?? k;
                return new
                {
                    Title = title,
                    From = ch?.First().FromName ?? "",
                    Targets = results.Where(r => r.Key == k).Select(r =>
                    {
                        var c = ch?.FirstOrDefault(x => x.DeviceIds == null || x.DeviceIds.Contains(r.t.DeviceId));
                        var b = before.GetValueOrDefault((k, r.t.DeviceId));
                        return new
                        {
                            r.t.Name, Kind = r.t.Kind.Length > 0 ? r.t.Kind : "zidoo", Result = r.Value.ToString(),
                            Before = b == null ? null : St(b.Watched, b.PositionMs),
                            After = c == null ? null : St(c.Watched, c.PositionMs),
                        };
                    }).ToList(),
                };
            }).ToList();
            return Results.Ok(new { e.Id, Files = files });
        });
        app.MapPost("/api/events/{id}/undo", (string id) =>
        {
            var (card, err) = engine.UndoCard(id);
            return card == null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { card.Id });
        });

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
                var scanK = kaleidescape.ScanAsync(ct);   // Kaleidescapes (port 10000) at the same time
                var scanA = appleTv.ScanAsync(ct);        // and Apple TVs (they announce themselves)
                var found = await engine.RescanAsync(addNew: true, ct);
                int k = await scanK, a = await scanA;
                return Results.Ok(new { Found = found.Count + k + a, New = found.Count(f => f.IsNew), Kaleidescapes = k, AppleTvs = a, Subnets = Discovery.LocalSubnets() });
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
            if (dev != null) return Results.Ok(dev);
            // Not a Zidoo: maybe a Kaleidescape (Live Poster only).
            if (req.Port is null or <= 0 or KaleidescapeClient.DefaultPort)
            {
                var (k, _) = await kaleidescape.AddAsync(ip, KaleidescapeClient.DefaultPort, ct);
                if (k != null) return Results.Ok(new { k.Id, k.Name, Kind = "kaleidescape" });
            }
            return Results.BadRequest(new { Error = $"Nothing answered as a Zidoo or a Kaleidescape at {ip}. Is it powered on?" });
        });

        // Kaleidescape players: rename, switch off, remove.
        app.MapPatch("/api/kaleidescape/{id}", (string id, UpdateDeviceRequest req) =>
        {
            bool found = false;
            store.UpdateKaleidescape(c =>
            {
                if (c.Players.FirstOrDefault(p => p.Id == id) is not { } p) return;
                found = true;
                if (req.Name is { } n && n.Trim().Length > 0) p.Name = n.Trim();
                if (req.Enabled is { } on) p.Enabled = on;
            });
            return found ? Results.Ok() : Results.NotFound();
        });
        app.MapDelete("/api/kaleidescape/{id}", (string id) =>
        {
            bool found = false;
            store.UpdateKaleidescape(c => found = c.Players.RemoveAll(p => p.Id == id) > 0);
            return found ? Results.Ok() : Results.NotFound();
        });

        // Apple TVs: pair (the TV shows a PIN), rename, switch off, unpair, remove.
        app.MapPost("/api/appletv/{id}/pair", async (string id, CancellationToken ct) =>
            await appleTv.BeginPairAsync(id, ct) is { Length: > 0 } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        app.MapPost("/api/appletv/{id}/pin", async (string id, PinRequest req) =>
            await appleTv.FinishPairAsync(id, req.Pin ?? "") is { Length: > 0 } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        app.MapDelete("/api/appletv/{id}/pairing", (string id) => { appleTv.Unpair(id); return Results.Ok(); });
        app.MapPatch("/api/appletv/{id}", (string id, UpdateDeviceRequest req) =>
        {
            bool found = false;
            store.UpdateAppleTv(c =>
            {
                if (c.Devices.FirstOrDefault(d => d.Id == id) is not { } d) return;
                found = true;
                if (req.Name is { } n && n.Trim().Length > 0) d.Name = n.Trim();
                if (req.Enabled is { } on) d.Enabled = on;
            });
            return found ? Results.Ok() : Results.NotFound();
        });
        app.MapDelete("/api/appletv/{id}", (string id) =>
        {
            bool found = false;
            store.UpdateAppleTv(c => found = c.Devices.RemoveAll(d => d.Id == id) > 0);
            appleTv.Forget(id);
            return found ? Results.Ok() : Results.NotFound();
        });

        app.MapPatch("/api/devices/{id}", (string id, UpdateDeviceRequest req) =>
            store.UpdateDevice(id, req.Name, req.Enabled) ? Results.Ok() : Results.NotFound());

        app.MapDelete("/api/devices/{id}", (string id) =>
            store.RemoveDevice(id) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/settings", (Settings s) =>
        {
            var before = store.Settings();
            bool wasWatchOnly = before.WatchOnly;
            store.SaveSettings(s);
            if (before.CheckUpdates != s.CheckUpdates || before.IncludeBetas != s.IncludeBetas) updates.CheckSoon();
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

    // ---------- health notes (Players tab) ----------

    /// <summary>Things worth knowing before they cost a resume point. Each has a stable key (the page lets you dismiss it).</summary>
    private sealed record HealthNote(string Key, string Text);

    private static List<HealthNote> Health(Store store, RelayEngine engine, ProgressService progress)
    {
        var notes = new List<HealthNote>();
        void Add(string key, string text) => notes.Add(new HealthNote(key, text));
        var now = DateTime.UtcNow;
        var pending = store.Pending();
        foreach (var d in store.Devices().Where(d => d.Enabled))
        {
            var live = engine.LiveOf(d.Id);
            if (!d.IsServerUser && d.HtVersionCode is > 0 and < 5111)
                Add("ht-old:" + d.Id, $"{d.Name}'s HT app is v{d.HtVersion} ({d.HtVersionCode}). Resume Relay needs 5.1.11 (5111) or newer: update the HT app on that player.");
            var old = pending.Where(p => p.TargetId == d.Id && now - p.CreatedUtc > TimeSpan.FromDays(2)).ToList();
            if (old.Count > 0)
                Add("waiting:" + d.Id, $"{old.Count} cop{(old.Count == 1 ? "y has" : "ies have")} waited {Math.Floor((now - old.Min(p => p.CreatedUtc)).TotalDays):N0} days for {d.Name}. " +
                    (d.IsServerUser ? $"Is the {d.ServerLabel} server running?" : "Is it unplugged, or is its HT app closed?"));
            if (!d.IsServerUser && !live.Online && live.OfflineSinceUtc is { } off && now - off > TimeSpan.FromDays(3))
                Add("unseen:" + d.Id, $"{d.Name} hasn't answered for {Math.Floor((now - off).TotalDays):N0} days. If it's gone for good, remove it so copies don't wait for it.");
            if (d.IsPlex && !live.Online && live.OfflineSinceUtc is { } poff && now - poff > TimeSpan.FromMinutes(10))
                Add("plex-down", $"The Plex server hasn't answered for {(int)(now - poff).TotalMinutes:N0} minutes, so Plex users aren't syncing.");
            if (d.IsJellyfin && !live.Online && live.OfflineSinceUtc is { } joff && now - joff > TimeSpan.FromMinutes(10))
                Add("jellyfin-down", $"The Jellyfin server hasn't answered for {(int)(now - joff).TotalMinutes:N0} minutes, so Jellyfin users aren't syncing.");
            if (progress.ClockAhead(d.Id))
                Add("clock:" + d.Id, $"{d.Name}'s clock seems to be ahead: it recorded watch times in the future. Check its date, time and time zone settings, or Catch Up may pick the wrong player.");
        }
        // (Plex and Jellyfin files outside the cards' folders are no longer a problem: they're matched automatically.)
        return notes.DistinctBy(n => n.Key).ToList();
    }

    // ---------- badge icons (Settings: "Activity badges" and "Rating badges") ----------

    private static readonly string[] IconKinds = { "zidoo", "plex", "jellyfin", "kaleidescape", "appletv", "imdb", "tmdb" };
    private static readonly Dictionary<string, string> IconTypes = new()
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
    };

    private static string IconDir(Store store) => Path.Combine(store.DataDirectory, "icons");

    private static string? IconFile(Store store, string kind) =>
        Directory.Exists(IconDir(store)) ? Directory.EnumerateFiles(IconDir(store), kind + ".*").FirstOrDefault() : null;

    private static long IconVersion(Store store) =>
        IconKinds.Select(k => IconFile(store, k)).Where(f => f != null).Select(f => File.GetLastWriteTimeUtc(f!).Ticks / 10_000_000).DefaultIfEmpty(0).Max();

    /// <summary>The person's own picture for the Zidoo, Plex, IMDb or TMDB badge, kept in the data folder (never shipped with the app).</summary>
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
        var store = app.Services.GetRequiredService<Store>();
        // The page passes the version it already has; an unchanged list isn't sent again.
        app.MapGet("/api/progress", (long? v) =>
            v == progress.Version ? Results.Ok(new { Unchanged = true, progress.Version, progress.Reading, Status = progress.StatusLines, Jobs = progress.Jobs() }) : Results.Ok(progress.Snapshot()));
        app.MapGet("/api/watched", (string? show, string? players, string? q, string? sort, int? page, int? pageSize) =>
            Results.Ok(progress.WatchSnapshot(show ?? "different",
                (players ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), q ?? "",
                sort ?? "title", page ?? 1, pageSize ?? 500)));
        app.MapPost("/api/watched/set", async (WatchSetRequest req) =>
        {
            var r = await progress.SetWatchedAsync(req.Key ?? "", req.DeviceIds ?? new List<string>(), req.Watched);
            if (r.Where(x => x.WatchOnly).Select(x => x.Name).ToList() is { Count: > 0 } would)
                store.Log(ActivityLevel.Skip, $"Watch-only: {progress.DisplayOf(req.Key ?? "")} would be marked {(req.Watched ? "watched" : "not watched")} on {string.Join(", ", would)} (Watch Sync). Nothing was written; turn on Copying to make changes.");
            return r.Count == 0 ? Results.BadRequest(new { Error = "That file is no longer in the list. Read the players again." }) : Results.Ok(r);
        });
        // Remove a waiting Watched All / cleanup job, or stop the running one after its current file.
        app.MapDelete("/api/progress/jobs/{id}", (string id) => progress.CancelJob(id) ? Results.Ok() : Results.NotFound());
        // "Copy all" from a player's column (both tabs). dryRun=true only counts.
        app.MapPost("/api/progress/batch", async (ProgressBatchRequest req) =>
        {
            var (err, files, copies, resets, notStarted) = await progress.CopyProgressFrom(req.From ?? "", req.To ?? new(), req.Keys ?? new(), req.IncludeNotStarted, req.DryRun);
            return err != null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { Files = files, Copies = copies, Resets = resets, NotStarted = notStarted });
        });
        app.MapPost("/api/watched/batch", (WatchBatchRequest req) =>
        {
            var (err, files, toW, toU) = progress.CopyWatchedFrom(req.From ?? "", req.To ?? new(), req.Show ?? "different",
                (req.Players ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), req.Q ?? "", req.DryRun);
            return err != null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { Files = files, ToWatched = toW, ToUnwatched = toU });
        });
        // The one-time cleanup on Progress Reset: watched files that kept a resume point. dryRun=true only counts.
        app.MapPost("/api/progress/tidy", (TidyRequest req) =>
        {
            var (err, changes, files) = progress.TidyWatched(req.DeviceIds ?? new List<string>(), req.DryRun);
            return err != null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { Changes = changes, Files = files });
        });
        // "Watched All" / "Unwatched All" on a TV show (Watch Sync). dryRun=true only counts.
        app.MapPost("/api/watched/set-show", (WatchShowRequest req) =>
        {
            var (err, episodes, changes, losing) = progress.SetShowWatched(req.Show ?? "", req.DeviceIds ?? new List<string>(), req.Watched, req.DryRun);
            return err != null ? Results.BadRequest(new { Error = err }) : Results.Ok(new { Episodes = episodes, Changes = changes, LosingResume = losing });
        });
        // e.g. /api/progress/diagnostics?q=mario : what each player's library listing held, for troubleshooting.
        app.MapGet("/api/progress/diagnostics", (string? q) => Results.Text(progress.DiagnosticsText(q ?? ""), "text/plain; charset=utf-8"));
        // The Compare panel: how two players' last reads line up (matched, only on one, same file name in another folder).
        app.MapGet("/api/progress/compare", (string a, string b) => Results.Ok(progress.Compare(a ?? "", b ?? "")));
        app.MapPost("/api/progress/read", (ProgressReadRequest req) =>
            progress.StartRead(req.DeviceIds ?? new List<string>(), stopping) is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        app.MapPost("/api/progress/reset", async (ProgressKeyRequest req) =>
            await progress.ResetFileAsync(req.Key ?? "") is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
        // Progress dragged between players on the tab, applied together: [{ key, from, to: [ids] }].
        app.MapPost("/api/progress/copy", async (List<ProgressCopyItem> items) =>
        {
            var errors = new List<string>();
            var cards = new List<object>();
            int n = 0;
            foreach (var it in items ?? new())
            {
                var (err, cardId) = await progress.CopyAsync(it.Key ?? "", it.From ?? "", it.To ?? new List<string>());
                if (err != null) { errors.Add(err); continue; }
                n += (it.To ?? new()).Count;
                cards.Add(new { it.Key, it.From, CardId = cardId }); // the page follows each copy by its Activity card
            }
            return errors.Count > 0 && n == 0 ? Results.BadRequest(new { Error = errors[0] }) : Results.Ok(new { Copies = n, Errors = errors, Cards = cards });
        });
        app.MapPost("/api/progress/reset-series", async (ProgressKeyRequest req) =>
            await progress.ResetSeriesAsync(req.Key ?? "") is { } err ? Results.BadRequest(new { Error = err }) : Results.Ok());
    }

    private sealed record ProgressReadRequest(List<string>? DeviceIds);
    private sealed record ProgressKeyRequest(string? Key);
    private sealed record ProgressCopyItem(string? Key, string? From, List<string>? To);
    private sealed record WatchSetRequest(string? Key, List<string>? DeviceIds, bool Watched);
    private sealed record BackupIcon(string Ext, string Data);
    private sealed record PinRequest(string? Pin);
    private sealed record Backup(string App, string Version, DateTime CreatedUtc, List<Device> Devices, Settings Settings, PlexConfig? Plex, Dictionary<string, BackupIcon>? Icons, JellyfinConfig? Jellyfin = null, List<KaleidescapePlayer>? Kaleidescapes = null, List<AppleTvDevice>? AppleTvs = null);
    private sealed record ProgressBatchRequest(string? From, List<string>? To, List<string>? Keys, bool IncludeNotStarted, bool DryRun);
    private sealed record WatchBatchRequest(string? From, List<string>? To, string? Show, string? Players, string? Q, bool DryRun);
    private sealed record TidyRequest(List<string>? DeviceIds, bool DryRun);
    private sealed record WatchShowRequest(string? Show, List<string>? DeviceIds, bool Watched, bool DryRun);
    private sealed record AddDeviceRequest(string? Ip, int? Port);
    private sealed record UpdateDeviceRequest(string? Name, bool? Enabled);
    private sealed record AutostartRequest(bool Enabled);
    private sealed record PowerRequest(bool On, bool? LaunchHomeTheater);
}
