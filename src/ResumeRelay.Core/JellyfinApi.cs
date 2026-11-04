namespace ResumeRelay;

/// <summary>
/// The Jellyfin card in Settings: find the server, sign in (user name and password, Quick Connect, or an API key), map
/// the folders, add users. The sign-in must be an administrator's: only an administrator can see what every user is
/// playing and write every user's progress.
/// </summary>
public static class JellyfinApi
{
    private sealed record ServerRequest(string? Url);
    private sealed record SignInRequest(string? User, string? Password);
    private sealed record KeyRequest(string? Key);
    private sealed record MappingRequest(List<PlexMapping>? Mappings);
    private sealed record AddUserRequest(string? Id);

    /// <summary>Everything the Jellyfin card shows (never the token). Also part of /api/state.</summary>
    public static object Summary(Store store)
    {
        var c = store.Jellyfin();
        return new
        {
            Server = c.ServerUrl.Length > 0 ? new { Id = c.ServerId, Name = c.ServerName, Url = c.ServerUrl, Version = c.ServerVersion } : null,
            SignedIn = c.Token.Length > 0, c.ApiKey, c.UserName,
            Linking = c.QuickSecret.Length > 0 ? c.QuickCode : null,
            c.Mappings,
            Users = store.Devices().Where(d => d.IsJellyfin).Select(d => new { d.Id, d.Name, d.JellyfinUserName }),
        };
    }

    private static JellyfinClient Server(JellyfinConfig c) => new(c.ServerUrl, c.Token, c.DeviceId);

    private static string NormUrl(string url)
    {
        string u = url.Trim().TrimEnd('/');
        if (u.Length == 0) return "";
        if (!u.Contains("://")) u = "http://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.IsDefaultPort && !url.Contains(':', StringComparison.Ordinal) && uri.Scheme == "http")
            u = $"http://{uri.Host}:8096{uri.AbsolutePath.TrimEnd('/')}";
        return u;
    }

    private static void ForgetUsers(Store store, RelayEngine engine)
    {
        foreach (var d in store.Devices().Where(d => d.IsJellyfin)) store.RemoveDevice(d.Id);
        engine.ForgetPlexIndex();
    }

    /// <summary>Keeps a sign-in when it's an administrator's; otherwise says why not.</summary>
    private static IResult Keep(Store store, JellyfinConfig c, JellyfinAuth a, string how)
    {
        if (!a.Admin)
            return Results.BadRequest(new { Error = $"{a.UserName} isn't an administrator on this Jellyfin server. Sign in with an administrator: only they can see what everyone is playing and copy everyone's progress." });
        c.Token = a.Token; c.ApiKey = false; c.UserId = a.UserId; c.UserName = a.UserName;
        c.QuickSecret = ""; c.QuickCode = "";
        store.SaveJellyfin(c);
        store.Log(ActivityLevel.Info, $"Signed in to Jellyfin ({c.ServerName}) as {a.UserName}{how}.");
        return Results.Ok(new { Done = true, a.UserName });
    }

    public static void Map(WebApplication app, Store store, RelayEngine engine)
    {
        app.MapGet("/api/jellyfin", () => Results.Ok(Summary(store)));

        // 1. The server: found on the network (Jellyfin answers a broadcast on port 7359), or typed in.
        app.MapGet("/api/jellyfin/discover", async (CancellationToken ct) =>
            Results.Ok((await JellyfinClient.DiscoverAsync(ct)).Select(f => new { f.Url, f.Id, f.Name })));

        app.MapPost("/api/jellyfin/server", async (ServerRequest req, CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            string url = NormUrl(req.Url ?? "");
            if (url.Length == 0) return Results.BadRequest(new { Error = "Type the Jellyfin server's address, for example 10.0.0.30:8096." });
            var info = await new JellyfinClient(url, "", c.DeviceId).PublicInfoAsync(TimeSpan.FromSeconds(6), ct);
            if (info == null) return Results.BadRequest(new { Error = $"No Jellyfin server answered at {url}. Is the address right, and is Jellyfin running?" });
            bool changed = c.ServerId.Length > 0 && c.ServerId != info.Id;
            if (changed) { ForgetUsers(store, engine); c.Token = ""; c.ApiKey = false; c.UserId = ""; c.UserName = ""; }
            c.ServerUrl = url; c.ServerId = info.Id; c.ServerName = info.Name; c.ServerVersion = info.Version;
            c.QuickSecret = ""; c.QuickCode = "";
            store.SaveJellyfin(c);
            engine.ForgetPlexIndex();
            store.Log(ActivityLevel.Info, $"Using the Jellyfin server {info.Name} (version {info.Version}) at {url}.");
            return Results.Ok(new { info.Name, Url = url, info.Version });
        });

        // 2. Sign in, one of three ways.
        app.MapPost("/api/jellyfin/signin", async (SignInRequest req, CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (c.ServerUrl.Length == 0) return Results.BadRequest(new { Error = "Choose the Jellyfin server first." });
            var (auth, err) = await new JellyfinClient(c.ServerUrl, "", c.DeviceId).SignInAsync((req.User ?? "").Trim(), req.Password ?? "", ct);
            return auth == null ? Results.BadRequest(new { Error = err }) : Keep(store, c, auth, "");
        });

        app.MapPost("/api/jellyfin/quick", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (c.ServerUrl.Length == 0) return Results.BadRequest(new { Error = "Choose the Jellyfin server first." });
            var api = new JellyfinClient(c.ServerUrl, "", c.DeviceId);
            if (!await api.QuickConnectEnabledAsync(ct))
                return Results.BadRequest(new { Error = "Quick Connect is turned off on this Jellyfin server (Dashboard → General → Quick Connect). Sign in with a user name and password instead, or turn it on." });
            var start = await api.QuickConnectStartAsync(ct);
            if (start == null) return Results.BadRequest(new { Error = "Jellyfin didn't start a Quick Connect sign-in. Try again in a moment." });
            c.QuickSecret = start.Value.Secret; c.QuickCode = start.Value.Code;
            store.SaveJellyfin(c);
            return Results.Ok(new { Code = c.QuickCode });
        });

        app.MapGet("/api/jellyfin/quick", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (c.QuickSecret.Length == 0) return Results.Ok(new { Done = c.Token.Length > 0, Expired = c.Token.Length == 0 });
            var (auth, expired) = await new JellyfinClient(c.ServerUrl, "", c.DeviceId).QuickConnectCheckAsync(c.QuickSecret, ct);
            if (expired) { c.QuickSecret = ""; c.QuickCode = ""; store.SaveJellyfin(c); return Results.Ok(new { Done = false, Expired = true }); }
            if (auth == null) return Results.Ok(new { Done = false, Code = c.QuickCode });
            return Keep(store, c, auth, " (Quick Connect)");
        });

        app.MapPost("/api/jellyfin/apikey", async (KeyRequest req, CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (c.ServerUrl.Length == 0) return Results.BadRequest(new { Error = "Choose the Jellyfin server first." });
            string key = (req.Key ?? "").Trim();
            if (key.Length < 16) return Results.BadRequest(new { Error = "Paste the API key (Jellyfin: Dashboard → API Keys)." });
            var users = await new JellyfinClient(c.ServerUrl, key, c.DeviceId).UsersAsync(ct);
            if (users == null) return Results.BadRequest(new { Error = "Jellyfin didn't accept that API key." });
            c.Token = key; c.ApiKey = true; c.UserId = ""; c.UserName = ""; c.QuickSecret = ""; c.QuickCode = "";
            store.SaveJellyfin(c);
            store.Log(ActivityLevel.Info, $"Connected to Jellyfin ({c.ServerName}) with an API key.");
            return Results.Ok(new { Done = true });
        });

        app.MapPost("/api/jellyfin/signout", () =>
        {
            var c = store.Jellyfin();
            ForgetUsers(store, engine);
            c.Token = ""; c.ApiKey = false; c.UserId = ""; c.UserName = ""; c.QuickSecret = ""; c.QuickCode = "";
            store.SaveJellyfin(c);
            store.Log(ActivityLevel.Info, "Signed out of Jellyfin. Jellyfin users were removed from the players.");
            return Results.Ok();
        });

        // 3. Folder mapping, with suggestions: Plex's folders (often the same computer), and ones worked out from a
        // file a Zidoo has played that Jellyfin also has.
        app.MapPost("/api/jellyfin/mapping", (MappingRequest req) =>
        {
            var c = store.Jellyfin();
            c.Mappings = (req.Mappings ?? new()).Select(m => new PlexMapping { PlexFolder = (m.PlexFolder ?? "").Trim().TrimEnd('/', '\\'), ZidooFolder = (m.ZidooFolder ?? "").Trim().TrimEnd('/') })
                .Where(m => m.PlexFolder.Length > 0 && m.ZidooFolder.Length > 0).ToList();
            store.SaveJellyfin(c);
            engine.ForgetPlexIndex();
            return Results.Ok(c.Mappings);
        });

        app.MapGet("/api/jellyfin/suggest", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (!c.Ready) return Results.BadRequest(new { Error = "Sign in to Jellyfin first." });
            var api = Server(c);
            var items = await api.AllItemsAsync(ct);
            var folders = await api.LibraryFoldersAsync(ct);
            if (items == null) return Results.BadRequest(new { Error = "The Jellyfin server didn't answer." });
            var byName = items.SelectMany(i => i.Files).GroupBy(f => PathMapper.FileName(f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var votes = new Dictionary<string, (PlexMapping M, int N)>();
            foreach (var e in store.Events(300).Where(e => e.SourceUrl.Length > 0 && e.FromKind is not ("plex" or "jellyfin")))
            {
                if (!byName.TryGetValue(PathMapper.FileName(e.SourceUrl), out var path)) continue;
                if (PlexPaths.Suggest(path, e.SourceUrl) is not { } m) continue;
                string k = (m.PlexFolder + "|" + m.ZidooFolder).ToLowerInvariant();
                votes[k] = (m, votes.TryGetValue(k, out var v) ? v.N + 1 : 1);
            }
            // Plex's folders, when Jellyfin's files are under them (Plex and Jellyfin on the same computer).
            var files = items.SelectMany(i => i.Files).ToList();
            foreach (var m in store.Plex().Mappings.Where(m => files.Any(f => PlexPaths.ToSourceUrl(f, new[] { m }) != null)))
            {
                string k = (m.PlexFolder + "|" + m.ZidooFolder).ToLowerInvariant();
                votes[k] = (m, votes.TryGetValue(k, out var v) ? v.N + 1000 : 1000);
            }
            return Results.Ok(new
            {
                Suggested = votes.Values.OrderByDescending(v => v.N).Select(v => v.M).ToList(),
                Folders = folders?.Select(f => new { Title = f.Name, Locations = f.Paths }) ?? Enumerable.Empty<object>(),
            });
        });

        // Checks the mapping: how many of Jellyfin's files it covers, and whether a Zidoo finds a few by that path.
        app.MapPost("/api/jellyfin/test", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (!c.Ready) return Results.BadRequest(new { Error = "Sign in to Jellyfin first." });
            var items = await Server(c).AllItemsAsync(ct);
            if (items == null) return Results.BadRequest(new { Error = "The Jellyfin server didn't answer." });
            var files = items.SelectMany(i => i.Files).ToList();
            var mapped = files.Select(f => (Path: f, Url: PlexPaths.ToSourceUrl(f, c.Mappings))).ToList();
            var outside = mapped.Where(x => x.Url == null).Select(x => Path.GetDirectoryName(x.Path.Replace('\\', '/'))?.Replace('\\', '/') ?? "")
                .GroupBy(x => x).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();
            var zidoo = store.Devices().FirstOrDefault(d => !d.IsServerUser && d.Enabled && engine.LiveOf(d.Id).Online);
            var samples = new List<object>();
            int found = 0, asked = 0;
            if (zidoo != null)
            {
                var api = new ZidooClient(zidoo.Ip, zidoo.Port);
                var rnd = new Random(42);
                foreach (var x in mapped.Where(x => x.Url != null).OrderBy(_ => rnd.Next()).Take(6))
                {
                    var (look, _) = await api.GetIdByPathnameAsync(x.Url!, ct);
                    if (look == Lookup.Failed) continue;
                    asked++;
                    if (look == Lookup.Found) found++;
                    samples.Add(new { Plex = x.Path, Zidoo = x.Url, Found = look == Lookup.Found });
                }
            }
            return Results.Ok(new { Total = files.Count, Mapped = mapped.Count(x => x.Url != null), Outside = outside, Player = zidoo?.Name, Asked = asked, Found = found, Samples = samples });
        });

        // 4. Users: everyone on the server, and who already syncs.
        app.MapGet("/api/jellyfin/users", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (!c.Ready) return Results.BadRequest(new { Error = "Sign in to Jellyfin first." });
            var users = await Server(c).UsersAsync(ct);
            if (users == null) return Results.BadRequest(new { Error = "Jellyfin didn't list its users. Is the sign-in an administrator's?" });
            var have = store.Devices().Where(d => d.IsJellyfin).ToDictionary(d => d.JellyfinUserId, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(users.Where(u => !u.Disabled).Select(u => new { u.Id, Title = u.Name, u.Admin, Added = have.ContainsKey(u.Id), DeviceId = have.GetValueOrDefault(u.Id)?.Id }));
        });

        app.MapPost("/api/jellyfin/users", async (AddUserRequest req, CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (!c.Ready) return Results.BadRequest(new { Error = "Sign in to Jellyfin first." });
            var users = await Server(c).UsersAsync(ct);
            var u = users?.FirstOrDefault(x => string.Equals(x.Id, req.Id, StringComparison.OrdinalIgnoreCase));
            if (u == null) return Results.BadRequest(new { Error = "That user isn't on the Jellyfin server any more." });
            var dev = new Device
            {
                Id = $"jellyfin:{c.ServerId}:{u.Id}",
                Kind = Device.JellyfinKind,
                Name = $"{u.Name} on Jellyfin",
                Model = "Jellyfin",
                JellyfinUserId = u.Id,
                JellyfinUserName = u.Name,
            };
            bool isNew = store.UpsertPlexUser(dev);
            engine.ForgetPlexIndex();
            if (isNew) store.Log(ActivityLevel.Info, $"Added the Jellyfin user {u.Name} ({c.ServerName}). Their resume points and watched marks now sync with the other players.");
            return Results.Ok(new { Id = dev.Id, Name = store.Device(dev.Id)?.Name });
        });

        // Troubleshooting: what Jellyfin says is playing, and which Jellyfin user each session matched.
        app.MapGet("/api/jellyfin/sessions", async (CancellationToken ct) =>
        {
            var c = store.Jellyfin();
            if (!c.Ready) return Results.Text("Sign in to Jellyfin first.");
            var api = Server(c);
            var sessions = await api.SessionsAsync(TimeSpan.FromSeconds(8), ct);
            var raw = await api.SessionsTextAsync(ct);
            var users = store.Devices().Where(d => d.IsJellyfin).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Jellyfin server {c.ServerName} (version {c.ServerVersion}) at {c.ServerUrl}; signed in {(c.ApiKey ? "with an API key" : "as " + c.UserName)}.");
            sb.AppendLine("Jellyfin users syncing: " + string.Join("; ", users.Select(d => $"{d.Name} (user {d.JellyfinUserId}, name \"{d.JellyfinUserName}\")")));
            sb.AppendLine();
            if (sessions == null) sb.AppendLine("The server didn't answer the request for what's playing.");
            else if (sessions.Count == 0) sb.AppendLine("Nothing is playing on the server right now (as far as it reports).");
            foreach (var x in sessions ?? new())
            {
                var who = users.FirstOrDefault(d => RelayEngine.IsJellyfinUser(d, x));
                sb.AppendLine($"Playing: {x.Item.Type} \"{(x.Item.IsEpisode ? x.Item.Show + " " + x.Item.Title : x.Item.Title)}\" at {RelayEngine.Clock(x.Item.ViewOffsetMs)}, {x.State}");
                sb.AppendLine($"  user id \"{x.UserId}\", name \"{x.UserTitle}\" -> {(who == null ? "matches NO Jellyfin user card" : "matches " + who.Name)}");
                sb.AppendLine($"  file: {(x.Item.Files.Count > 0 ? string.Join(" | ", x.Item.Files) : "(not listed in the session)")}");
            }
            sb.AppendLine();
            sb.AppendLine("== What the server sent (addresses and tokens removed):");
            sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(raw ?? "(no answer)",
                @"(""(RemoteEndPoint|DeviceId|AccessToken|Token)""\s*:\s*)""[^""]*""", "$1\"…\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });
    }
}
