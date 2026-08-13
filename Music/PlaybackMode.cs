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
    StnFailed,
    /// <summary>Single-track media prep/download failed — keep STN session, skip to next.</summary>
    TrackFailed,
    /// <summary>Pump-owned seek restart; do not advance the queue.</summary>
    Seek,
    IdleTimeout,
    ModeConflict,
    RoomConflict,
    CapacityExceeded,
}
