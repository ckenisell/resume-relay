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
    /// <summary>The MAC shown on the card (wired if the player has one).</summary>
    public string Mac { get; set; } = "";
    /// <summary>Both MACs are kept for Wake-on-LAN; the packet is sent to each, as Z-Keeper does.</summary>
    public string NetMac { get; set; } = "";
    public string WifiMac { get; set; } = "";
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
    /// <summary>The stop this copy belongs to, so its chip on the Activity card can be updated.</summary>
    public string EventId { get; set; } = "";
    /// <summary>Set once the copy had to wait (player off / Home Theater closed), for the "after it woke up" note.</summary>
    public string WaitedFor { get; set; } = "";

    public string Key => TargetId + "|" + SourceUrl.ToLowerInvariant();
}

public enum ActivityLevel { Info, Ok, Skip, Warn, Error }

public sealed class ActivityEntry
{
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public ActivityLevel Level { get; set; }
    public string Message { get; set; } = "";
    /// <summary>System messages (player online, settings changed) show as rows on the Activity tab.
    /// Copy messages are also kept here as a detailed journal, but the tab shows them as stop cards.</summary>
    public bool System { get; set; } = true;
}

/// <summary>One stop on one player, and what happened to its copy on each other player.
/// This is what the Activity tab shows as a card.</summary>
public sealed class StopEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "";
    /// <summary>"S02E01" for episodes, the year for films when known.</summary>
    public string Subtitle { get; set; } = "";
    public string FromName { get; set; } = "";
    public int PositionMs { get; set; }
    public int DurationMs { get; set; }
    /// <summary>True when the stop was at the end (the other players mark it watched).</summary>
    public bool Finished { get; set; }
    /// <summary>File name of the cached poster, or "" when none.</summary>
    public string Poster { get; set; } = "";
    public List<StopTarget> Targets { get; set; } = new();
}

public enum TargetState { Pending, Waiting, Ok, Skipped, Failed, WatchOnly }

public sealed class StopTarget
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public TargetState State { get; set; } = TargetState.Pending;
    public string Note { get; set; } = "";
}

public enum PowerAction { None, TurningOn, TurningOff }

/// <summary>Live, in-memory view of a player (not persisted).</summary>
public sealed class DeviceLive
{
    public bool Online { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? OfflineSinceUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public NowPlaying? Playing { get; set; }
    /// <summary>Null until checked; false when the player answers but its Home Theater app does not.</summary>
    public bool? HomeTheaterRunning { get; set; }
    public DateTime HomeTheaterCheckedUtc { get; set; }
    public PowerAction Power { get; set; }
    public DateTime PowerSinceUtc { get; set; }
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
    /// <summary>Poster file for the card, filled in the background once the title is identified.</summary>
    public string Poster { get; set; } = "";
    public string Subtitle { get; set; } = "";
}
