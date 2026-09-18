using Mezube.Domain.Entities;
using Mezube.Playback;

namespace Mezube.Music;

/// <summary>
/// One queued play request. Target is pinned to the clan's live stream channel
/// at enqueue time so a second room cannot steal the publisher.
/// </summary>
public sealed record QueuedPlay(
    TrackInfoEntity Track,
    PlaybackTarget Target,
    long? ReplyMessageId = null,
    uint? ReplyCreateTimeSeconds = null,
    bool IsFromDefault = false,
    bool WantVideo = false);
