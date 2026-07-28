using Mezube.Domain.Entities;
using Mezube.Playback;

namespace Mezube.Music;

/// <summary>
/// One queued play request. Target is fixed at enqueue time so a later item
/// can play in another voice/stream channel of the same clan after the current track ends
/// (still sequential — never concurrent rooms for one clan).
/// </summary>
public sealed record QueuedPlay(TrackInfoEntity Track, PlaybackTarget Target);
