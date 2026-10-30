namespace ResumeRelay.Live;

/// <summary>
/// Live Poster: a TV or tablet (a "screen") hung outside a room shows the poster of what that room's players are
/// playing, over a background, and a slideshow (Coming Soon, Recently Watched, Highly Acclaimed) when nothing is.
/// Resume Relay makes each picture into a short 4K video (stick and TV apps draw their own graphics at 1080p, but
/// play video at full 4K), and the screen app just loops the video it's told to.
/// </summary>
public sealed class LiveConfig
{
    /// <summary>After a stop, the poster stays up this long before the slideshow comes back.</summary>
    public int StopWaitSeconds { get; set; } = 30;
    /// <summary>How long each slideshow poster shows.</summary>
    public int SlideSeconds { get; set; } = 15;
    /// <summary>Highly Acclaimed: movies and series rated at least this (IMDb when Plex has it).</summary>
    public double AcclaimedMin { get; set; } = 8.0;

    public LiveSet NowPlaying { get; set; } = new() { Enabled = true, BackgroundId = "builtin-nowshowing" };
    public LiveSet ComingSoon { get; set; } = new() { Enabled = true, BackgroundId = "builtin-comingsoon" };
    public LiveSet RecentlyWatched { get; set; } = new() { Enabled = true, BackgroundId = "builtin-recent" };
    public LiveSet Acclaimed { get; set; } = new() { Enabled = true, BackgroundId = "builtin-acclaimed" };

    /// <summary>Where Resume Relay can read the players' share for folder posters: the Zidoos' folder → a folder this
    /// computer can open. Empty: Plex's folder mapping is used (Plex runs on this computer), or on Windows the share's
    /// own \\server\share address.</summary>
    public List<LiveFolder> Folders { get; set; } = new();

    public List<LiveScreen> Screens { get; set; } = new();
    public List<LiveBackground> Backgrounds { get; set; } = new();
    public List<ComingSoonItem> Coming { get; set; } = new();
}

public sealed class LiveSet
{
    public bool Enabled { get; set; } = true;
    public string BackgroundId { get; set; } = "";
}

public sealed class LiveFolder
{
    public string ZidooFolder { get; set; } = "";
    public string LocalFolder { get; set; } = "";
}

public sealed class LiveScreen
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>The screen app's own id for itself (kept across restarts), and the secret it uses once paired.</summary>
    public string DeviceKey { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>"roku", "android", "web"…, and the model it reported.</summary>
    public string Platform { get; set; } = "";
    public string Model { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>The players (Zidoos and Plex users) whose Now Playing this screen shows.</summary>
    public List<string> Players { get; set; } = new();
    /// <summary>How it's mounted: "portrait" (a tablet held upright: the video is portrait), "left" (a TV turned so
    /// its top is on the left) or "right" (its top on the right). TVs and sticks always play landscape video, so for
    /// "left"/"right" the portrait picture is turned into a 3840×2160 frame.</summary>
    public string Mount { get; set; } = "right";
    /// <summary>"4k" or "1080" (for players that can't play 4K video).</summary>
    public string Resolution { get; set; } = "4k";
    /// <summary>"auto" (HEVC, except H.264 for web browsers), "hevc" or "h264". Rokus play 4K only as HEVC.</summary>
    public string Codec { get; set; } = "auto";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LiveBackground
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>File name inside live/backgrounds.</summary>
    public string File { get; set; } = "";
    /// <summary>"image" or "video".</summary>
    public string Kind { get; set; } = "image";
    public bool BuiltIn { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int DurationMs { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ComingSoonItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "";
    /// <summary>File name inside live/coming.</summary>
    public string File { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A title for a poster: what's playing, or one slide of a slideshow.</summary>
public sealed record PosterRequest(
    string SourceUrl,        // the Zidoos' path (smb://…) of one of its files; "" when unknown
    string PlexFile,         // Plex's path of that file, when it came from Plex
    string DeviceId,         // the player it's from (for the Zidoo fallback), "" for any
    bool IsEpisode,
    int Season,
    bool SeriesOnly,         // a whole series (Highly Acclaimed): the series poster, never a season's
    string Title,
    int PlexKey = 0,         // the Plex item (movie, episode or show), when known
    string PlexThumb = "");  // Plex's poster path for it (movie poster, or the series poster for TV)

/// <summary>A poster found for a title: the cached file, where it came from, and its size.</summary>
public sealed record PosterResult(string Path, string Source, int Width, int Height);
