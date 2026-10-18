using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Music;
using Mezube.Streaming;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;

namespace Mezube.Playback;

public interface IStreamPublisherSink
{
    Task EndTrackAsync(PlaybackTarget target, CancellationToken cancellationToken = default);
    Task SetPausedAsync(PlaybackTarget target, bool paused, CancellationToken cancellationToken = default);
    Task WaitUntilTrackEndedAsync(long streamChannelId, CancellationToken cancellationToken = default);
}

/// <summary>Publishes prepared Ogg/Opus audio over WHIP, one session per stream channel.</summary>
public sealed class StreamingChannelSink : IPlaybackSink, IStreamPublisherSink
{
    private readonly StreamPublisherSessionManager _sessions;
    private readonly StreamClientHolder _holder;
    private readonly TrackPrepService _prep;
    private readonly ILogger<StreamingChannelSink> _logger;

    public StreamingChannelSink(
        StreamPublisherSessionManager sessions,
        StreamClientHolder holder,
        TrackPrepService prep,
        ILogger<StreamingChannelSink> logger)
    {
        _sessions = sessions;
        _holder = holder;
        _prep = prep;
        _logger = logger;
    }

    public string Name => "streaming";

    public Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
        => PlayAsync(target, track, PreparedAssetKind.Audio, cancellationToken);

    public async Task PlayAsync(
        PlaybackTarget target,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind != PreparedAssetKind.Audio)
        {
            throw new InvalidOperationException("Stream playback accepts audio assets only.");
        }

        var client = _holder.GetClient();
        var playable = await _prep.EnsurePreparedAsync(
                client,
                track,
                PreparedAssetKind.Audio,
                cancellationToken)
            .ConfigureAwait(false);
        if (!StreamMediaSource.TryParse(playable, out var media))
        {
            throw new MediaPrepException("Stream playback requires a local .ogg/.opus file or an absolute .ogg/.opus URL.");
        }

        var session = _sessions.GetOrCreate(target.ChannelId);
        await session.StartAsync(
                ct => GetPublishGrantAsync(client, target.ChannelId, ct),
                cancellationToken)
            .ConfigureAwait(false);
        session.BeginStream(media);

        _logger.LogDebug(
            "Stream audio published title={Title} clan={ClanId} channel={ChannelId} source={Source}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            media.IsLocal ? "local" : "cdn");
    }

    public Task EndTrackAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGet(target.ChannelId, out var session))
        {
            session.EndTrack();
        }

        return Task.CompletedTask;
    }

    public Task SetPausedAsync(PlaybackTarget target, bool paused, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var session))
        {
            return Task.FromException(new InvalidOperationException("No active stream publisher session to pause."));
        }

        session.SetPaused(paused);
        return Task.CompletedTask;
    }

    public bool IsPaused(long streamChannelId)
        => _sessions.TryGet(streamChannelId, out var session) && session.IsPaused;

    public async Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var session))
        {
            return;
        }

        try
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _sessions.RemoveAndDisposeAsync(target.ChannelId).ConfigureAwait(false);
        }
    }

    public Task WaitUntilTrackEndedAsync(long streamChannelId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(streamChannelId, out var session))
        {
            return Task.FromException(new InvalidOperationException("No active stream publisher session."));
        }

        return session.WaitUntilTrackEndedAsync(cancellationToken);
    }

    private static Task<StreamPublishGrant> GetPublishGrantAsync(
        MezonClient client,
        long channelId,
        CancellationToken cancellationToken)
        => Task.FromException<StreamPublishGrant>(new StreamPublishException(
            StreamFailureKind.Unavailable,
            "Stream publishing needs a Mezon.Net.Sdk release with GetStreamPlayback."));
}
