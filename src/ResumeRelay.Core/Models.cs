namespace ResumeRelay;

/// <summary>A Zidoo player the user has registered. Identified by MAC, never by IP.</summary>
public sealed class Device
{
    /// <summary>Stable identity: the wired MAC (lower-case, colon separated), else Wi-Fi MAC, else the player's UUID.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 9529;
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Mac { get; set; } = "";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Settings
{
    /// <summary>How often each player is asked what it is playing.</summary>
    public int PollSeconds { get; set; } = 3;

    /// <summary>Copies waiting for a sleeping player are dropped after this many days.</summary>
    public int PendingMaxDays { get; set; } = 14;

    /// <summary>When true, the relay only logs what it would copy. Fresh installs start here.</summary>
    public bool WatchOnly { get; set; } = true;
}

public sealed class PendingCopy
{
    public string TargetId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string FromName { get; set; } = "";
    public int PositionMs { get; set; }
    public int DurationMs { get; set; }
    public long LastWatchTimeMs { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptUtc { get; set; }
    public string LastNote { get; set; } = "";

    public string Key => TargetId + "|" + SourceUrl.ToLowerInvariant();
}

public enum ActivityLevel { Info, Ok, Skip, Warn, Error }

public sealed class ActivityEntry
{
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public ActivityLevel Level { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Live, in-memory view of a player (not persisted).</summary>
public sealed class DeviceLive
{
    public bool Online { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? OfflineSinceUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public NowPlaying? Playing { get; set; }
}

public sealed class NowPlaying
{
    public string Path { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public int PositionMs { get; set; }
    public int DurationMs { get; set; }
    public bool Paused { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
