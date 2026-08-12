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
    IdleTimeout,
    ModeConflict,
    RoomConflict,
    CapacityExceeded,
}
