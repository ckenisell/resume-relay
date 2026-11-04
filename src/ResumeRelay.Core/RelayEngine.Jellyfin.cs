namespace ResumeRelay;

/// <summary>
/// Jellyfin users as players. Everything after reading the sessions (stops, the copy queue, Progress Reset, Watch Sync,
/// posters) is shared with Plex (RelayEngine.Plex.cs, through IMediaServer). One administrator's sign-in reads every
/// user's sessions and writes every user's progress; the users themselves sign in to nothing.
/// </summary>
public sealed partial class RelayEngine
{
    /// <summary>A client for the Jellyfin server with the administrator's sign-in (null when Jellyfin isn't set up).</summary>
    public JellyfinClient? JellyfinServer()
    {
        var c = _store.Jellyfin();
        return c.Ready ? new JellyfinClient(c.ServerUrl, c.Token, c.DeviceId) : null;
    }

    /// <summary>Is this session the user's? By Jellyfin user id, else by name.</summary>
    public static bool IsJellyfinUser(Device d, PlexSession s) =>
        (s.UserId.Length > 0 && string.Equals(s.UserId, d.JellyfinUserId, StringComparison.OrdinalIgnoreCase))
        || (s.UserId.Length == 0 && s.UserTitle.Length > 0 && string.Equals(s.UserTitle, d.JellyfinUserName, StringComparison.OrdinalIgnoreCase));

    private async Task PollJellyfinAsync(Device d, CancellationToken ct)
    {
        var c = _store.Jellyfin();
        var sessions = c.Ready ? await new JellyfinClient(c.ServerUrl, c.Token, c.DeviceId).SessionsAsync(TimeSpan.FromSeconds(4), ct) : null;
        await ApplySessionsAsync(d, sessions, x => IsJellyfinUser(d, x), c.Mappings, ct);
    }
}
