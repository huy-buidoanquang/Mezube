using Mezube.Domain.Entities;

namespace Mezube.Playback;

public interface IPlaybackSink
{
    string Name { get; }
    Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default);
    Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default);
}

/// <param name="ClanId">Clan owning the playback target.</param>
/// <param name="ChannelId">Stream channel id.</param>
/// <param name="RoomName">Publisher room key (channel id string). Kept for Redis payload compat.</param>
/// <param name="ChannelLabel">Human channel label for Destination UI.</param>
public sealed record PlaybackTarget(
    long ClanId,
    long ChannelId,
    string? RoomName = null,
    string? ChannelLabel = null);
