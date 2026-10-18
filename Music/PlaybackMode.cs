using Mezube.Playback;

namespace Mezube.Music;

public enum PlaybackMode
{
    Streaming,
    Voice,
}

public enum PlayerDestroyReason
{
    None,
    QueueEmpty,
    UserStop,
    Skip,
    StreamFailed,
    /// <summary>Single-track media prep/download failed - keep the SFU session, skip to next.</summary>
    TrackFailed,
    IdleTimeout,
    ModeConflict,
    RoomConflict,
    CapacityExceeded,
}
