namespace ResumeRelay;

/// <summary>The Plex card on the Players tab: sign in, pick the server, map the folders, add users.</summary>
public static class PlexApi
{
    private sealed record ServerRequest(string? ClientId);
    private sealed record MappingRequest(List<PlexMapping>? Mappings);
    private sealed record AddUserRequest(string? Uuid, string? Pin);

    /// <summary>Everything the Plex card shows (never the tokens). Also part of /api/state.</summary>
    public static object Summary(Store store)
    {
        var c = store.Plex();
        return new
        {
            c.SignedIn, c.Username, Linking = c.PinId > 0 ? c.PinCode : null, LinkUrl = "https://plex.tv/link",
            Server = c.Ready ? new { Id = c.ServerId, Name = c.ServerName, Url = c.ServerUrl } : null,
            c.Mappings,
            Users = store.Devices().Where(d => d.IsPlex).Select(d => new { d.Id, d.Name, d.PlexUserTitle, d.PlexOwner }),
        };
    }

    public static void Map(WebApplication app, Store store, RelayEngine engine)
    {
        app.MapGet("/api/plex", () => Results.Ok(Summary(store)));

        // Troubleshooting: what the Plex server says is playing, and which Plex user card each session matched.
        app.MapGet("/api/plex/sessions", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.Ready) return Results.Text("Choose the Plex server first.");
            var api = new PlexClient(c.ServerUrl, c.ServerToken, c.ClientId);
            var sessions = await api.SessionsAsync(TimeSpan.FromSeconds(8), ct);
            var raw = await api.SessionsTextAsync(ct);
            var users = store.Devices().Where(d => d.IsPlex).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Plex server {c.ServerName} at {c.ServerUrl}; signed in as {c.Username} (account {c.AccountId}).");
            sb.AppendLine("Plex users syncing: " + string.Join("; ", users.Select(d => $"{d.Name} (account {d.PlexUserId}{(d.PlexOwner ? ", owner" : "")}, name \"{d.PlexUserTitle}\")")));
            sb.AppendLine();
            if (sessions == null) sb.AppendLine("The server didn't answer the request for what's playing.");
            else if (sessions.Count == 0) sb.AppendLine("Nothing is playing on the server right now (as far as it reports).");
            foreach (var x in sessions ?? new())
            {
                var who = users.FirstOrDefault(d => RelayEngine.IsUser(d, x, c));
                sb.AppendLine($"Playing: {x.Item.Type} \"{(x.Item.IsEpisode ? x.Item.Show + " " + x.Item.Title : x.Item.Title)}\" (item {x.Item.Key}) at {RelayEngine.Clock(x.Item.ViewOffsetMs)}, {x.State}");
                sb.AppendLine($"  user id \"{x.UserId}\", name \"{x.UserTitle}\" -> {(who == null ? "matches NO Plex user card" : "matches " + who.Name)}");
                sb.AppendLine($"  file: {(x.Item.Files.Count > 0 ? string.Join(" | ", x.Item.Files) : "(not listed in the session)")}");
            }
            sb.AppendLine();
            sb.AppendLine("== What the server sent (addresses and tokens removed):");
            sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(raw ?? "(no answer)",
                @"(""(address|remotePublicAddress|machineIdentifier|token|accessToken|uuid)""\s*:\s*)""[^""]*""", "$1\"…\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        // 1. Sign in: a 4-character code to enter at plex.tv/link. The page then asks GET /api/plex/signin every few seconds.
        app.MapPost("/api/plex/signin", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            var pin = await new PlexTv(c.ClientId).CreatePinAsync(ct);
            if (pin == null) return Results.BadRequest(new { Error = "Couldn't reach plex.tv. Check this computer's internet connection and try again." });
            c.PinId = pin.Value.Id; c.PinCode = pin.Value.Code;
            store.SavePlex(c);
            return Results.Ok(new { Code = c.PinCode, LinkUrl = "https://plex.tv/link" });
        });

        app.MapGet("/api/plex/signin", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (c.SignedIn && c.PinId == 0) return Results.Ok(new { Done = true, c.Username });
            if (c.PinId == 0) return Results.Ok(new { Done = false, Expired = true });
            var tv = new PlexTv(c.ClientId);
            string? token = await tv.CheckPinAsync(c.PinId, ct);
            if (token == null) { c.PinId = 0; c.PinCode = ""; store.SavePlex(c); return Results.Ok(new { Done = false, Expired = true }); }
            if (token.Length == 0) return Results.Ok(new { Done = false, Code = c.PinCode });
            var acct = await tv.AccountAsync(token, ct);
            c.AccountToken = token;
            c.AccountId = acct?.Id ?? "";
            c.Username = acct?.Username is { Length: > 0 } u ? u : acct?.Title ?? "";
            c.PinId = 0; c.PinCode = "";
            store.SavePlex(c);
            store.Log(ActivityLevel.Info, $"Signed in to Plex as {c.Username}.");
            return Results.Ok(new { Done = true, c.Username });
        });

        app.MapPost("/api/plex/signout", () =>
        {
            var c = store.Plex();
            foreach (var d in store.Devices().Where(d => d.IsPlex)) store.RemoveDevice(d.Id);
            store.SavePlex(new PlexConfig { ClientId = c.ClientId, Mappings = c.Mappings });
            engine.ForgetPlexIndex();
            store.Log(ActivityLevel.Info, "Signed out of Plex. Plex users were removed from the players.");
            return Results.Ok();
        });

        // 2. The servers this account can use.
        app.MapGet("/api/plex/servers", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.SignedIn) return Results.BadRequest(new { Error = "Sign in to Plex first." });
            var servers = await new PlexTv(c.ClientId).ServersAsync(c.AccountToken, ct);
            if (servers == null) return Results.BadRequest(new { Error = "plex.tv didn't answer. Try again in a moment." });
            return Results.Ok(servers.Select(s => new { s.Name, s.ClientId, s.Owned, Selected = s.ClientId == c.ServerId }));
        });

        // Picks a server, and the first of its addresses that answers from here (local addresses first, relays last).
        app.MapPost("/api/plex/server", async (ServerRequest req, CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.SignedIn) return Results.BadRequest(new { Error = "Sign in to Plex first." });
            var servers = await new PlexTv(c.ClientId).ServersAsync(c.AccountToken, ct);
            var s = servers?.FirstOrDefault(x => x.ClientId == req.ClientId);
            if (s == null) return Results.BadRequest(new { Error = "That server isn't on this Plex account any more." });
            var order = s.Connections.OrderBy(x => x.Relay).ThenByDescending(x => x.Local).ThenBy(x => x.Uri.StartsWith("https") ? 1 : 0).ToList();
            foreach (var conn in order)
            {
                string? id = await new PlexClient(conn.Uri, s.AccessToken, c.ClientId).IdentityAsync(TimeSpan.FromSeconds(4), ct);
                if (id == null || id != s.ClientId) continue;
                bool changed = c.ServerId != s.ClientId;
                c.ServerId = s.ClientId; c.ServerName = s.Name; c.ServerUrl = conn.Uri; c.ServerToken = s.AccessToken;
                store.SavePlex(c);
                if (changed) foreach (var d in store.Devices().Where(d => d.IsPlex)) store.RemoveDevice(d.Id);
                engine.ForgetPlexIndex();
                store.Log(ActivityLevel.Info, $"Using the Plex server {s.Name} at {conn.Uri}.");
                return Results.Ok(new { s.Name, Url = conn.Uri });
            }
            return Results.BadRequest(new { Error = $"None of {s.Name}'s addresses answered from this computer. Is the Plex server running?" });
        });

        // 3. Folder mapping, and a suggestion made from a file a Zidoo has played that Plex also has.
        app.MapPost("/api/plex/mapping", (MappingRequest req) =>
        {
            var c = store.Plex();
            c.Mappings = (req.Mappings ?? new()).Select(m => new PlexMapping { PlexFolder = (m.PlexFolder ?? "").Trim().TrimEnd('/', '\\'), ZidooFolder = (m.ZidooFolder ?? "").Trim().TrimEnd('/') })
                .Where(m => m.PlexFolder.Length > 0 && m.ZidooFolder.Length > 0).ToList();
            store.SavePlex(c);
            engine.ForgetPlexIndex();
            return Results.Ok(c.Mappings);
        });

        app.MapGet("/api/plex/suggest", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.Ready) return Results.BadRequest(new { Error = "Choose the Plex server first." });
            var items = await new PlexClient(c.ServerUrl, c.ServerToken, c.ClientId).AllItemsAsync(ct);
            var sections = await new PlexClient(c.ServerUrl, c.ServerToken, c.ClientId).SectionsAsync(ct);
            if (items == null) return Results.BadRequest(new { Error = "The Plex server didn't answer." });
            var byName = items.SelectMany(i => i.Files).GroupBy(f => PathMapper.FileName(f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var votes = new Dictionary<string, (PlexMapping M, int N)>();
            foreach (var e in store.Events(300).Where(e => e.SourceUrl.Length > 0 && e.FromKind != "plex"))
            {
                if (!byName.TryGetValue(PathMapper.FileName(e.SourceUrl), out var plexPath)) continue;
                if (PlexPaths.Suggest(plexPath, e.SourceUrl) is not { } m) continue;
                string k = (m.PlexFolder + "|" + m.ZidooFolder).ToLowerInvariant();
                votes[k] = (m, votes.TryGetValue(k, out var v) ? v.N + 1 : 1);
            }
            return Results.Ok(new
            {
                Suggested = votes.Values.OrderByDescending(v => v.N).Select(v => v.M).ToList(),
                Folders = sections?.Where(s => s.Type is "movie" or "show").Select(s => new { s.Title, s.Locations }) ?? Enumerable.Empty<object>(),
            });
        });

        // Checks the mapping: how many of Plex's files it covers, and whether a Zidoo finds a few of them by that path.
        app.MapPost("/api/plex/test", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.Ready) return Results.BadRequest(new { Error = "Choose the Plex server first." });
            var items = await new PlexClient(c.ServerUrl, c.ServerToken, c.ClientId).AllItemsAsync(ct);
            if (items == null) return Results.BadRequest(new { Error = "The Plex server didn't answer." });
            var files = items.SelectMany(i => i.Files).ToList();
            var mapped = files.Select(f => (Plex: f, Url: PlexPaths.ToSourceUrl(f, c.Mappings))).ToList();
            var outside = mapped.Where(x => x.Url == null).Select(x => Path.GetDirectoryName(x.Plex.Replace('\\', '/'))?.Replace('\\', '/') ?? "")
                .GroupBy(x => x).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();

            var zidoo = store.Devices().FirstOrDefault(d => !d.IsPlex && d.Enabled && engine.LiveOf(d.Id).Online);
            var samples = new List<object>();
            int found = 0, asked = 0;
            if (zidoo != null)
            {
                var api = new ZidooClient(zidoo.Ip, zidoo.Port);
                var pick = mapped.Where(x => x.Url != null).ToList();
                var rnd = new Random(42);
                foreach (var x in pick.OrderBy(_ => rnd.Next()).Take(6))
                {
                    var (look, _) = await api.GetIdByPathnameAsync(x.Url!, ct);
                    if (look == Lookup.Failed) continue;
                    asked++;
                    if (look == Lookup.Found) found++;
                    samples.Add(new { Plex = x.Plex, Zidoo = x.Url, Found = look == Lookup.Found });
                }
            }
            return Results.Ok(new
            {
                Total = files.Count,
                Mapped = mapped.Count(x => x.Url != null),
                Outside = outside,
                Player = zidoo?.Name,
                Asked = asked,
                Found = found,
                Samples = samples,
            });
        });

        // 4. Users: the account's Plex Home users, and which of them already sync.
        app.MapGet("/api/plex/users", async (CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.SignedIn) return Results.BadRequest(new { Error = "Sign in to Plex first." });
            var tv = new PlexTv(c.ClientId);
            var users = await tv.HomeUsersAsync(c.AccountToken, ct);
            if (users == null || users.Count == 0)
            {
                var me = await tv.AccountAsync(c.AccountToken, ct);
                users = me == null ? new() : new() { new PlexHomeUser(me.Id, me.Uuid, me.Title.Length > 0 ? me.Title : me.Username, true, false) };
            }
            var have = store.Devices().Where(d => d.IsPlex).ToDictionary(d => d.PlexUserId);
            return Results.Ok(users.Select(u => new { u.Uuid, u.Title, u.Admin, u.Protected, Added = have.ContainsKey(u.Id), DeviceId = have.GetValueOrDefault(u.Id)?.Id }));
        });

        app.MapPost("/api/plex/users", async (AddUserRequest req, CancellationToken ct) =>
        {
            var c = store.Plex();
            if (!c.Ready) return Results.BadRequest(new { Error = "Choose the Plex server first." });
            var tv = new PlexTv(c.ClientId);
            var users = await tv.HomeUsersAsync(c.AccountToken, ct) ?? new();
            var me = await tv.AccountAsync(c.AccountToken, ct);
            var u = users.FirstOrDefault(x => x.Uuid == req.Uuid)
                    ?? (me != null && me.Uuid == req.Uuid ? new PlexHomeUser(me.Id, me.Uuid, me.Title.Length > 0 ? me.Title : me.Username, true, false) : null);
            if (u == null) return Results.BadRequest(new { Error = "That user isn't in this Plex Home any more." });

            bool owner = me != null && u.Id == me.Id;
            string serverToken;
            if (owner) serverToken = c.ServerToken;
            else
            {
                var (userToken, err) = await tv.SwitchUserAsync(c.AccountToken, u.Uuid, (req.Pin ?? "").Trim(), ct);
                if (userToken == null) return Results.BadRequest(new { Error = err, NeedsPin = u.Protected });
                var servers = await tv.ServersAsync(userToken, ct);
                var s = servers?.FirstOrDefault(x => x.ClientId == c.ServerId);
                if (s == null || s.AccessToken.Length == 0)
                    return Results.BadRequest(new { Error = $"{u.Title} doesn't have access to {c.ServerName}. Share the server with them in Plex first." });
                serverToken = s.AccessToken;
            }

            var dev = new Device
            {
                Id = $"plex:{c.ServerId}:{u.Id}",
                Kind = Device.PlexKind,
                Name = $"{u.Title} on Plex",
                Model = "Plex",
                PlexUserId = u.Id,
                PlexUserTitle = u.Title,
                PlexOwner = owner,
                PlexToken = serverToken,
            };
            bool isNew = store.UpsertPlexUser(dev);
            engine.ForgetPlexIndex();
            if (isNew) store.Log(ActivityLevel.Info, $"Added the Plex user {u.Title} ({c.ServerName}). Their resume points and watched marks now sync with the Zidoos.");
            return Results.Ok(new { Id = dev.Id, Name = store.Device(dev.Id)?.Name });
        });
    }
}
