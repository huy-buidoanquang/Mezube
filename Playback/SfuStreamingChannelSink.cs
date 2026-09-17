using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Music;
using Mezube.Sfu;
using Mezon.Net.Sdk;
using Mezon.Net.Models;
using Microsoft.Extensions.Logging;

namespace Mezube.Playback;

public interface ISfuPublisherSink
{
    Task EndTrackAsync(PlaybackTarget target, CancellationToken cancellationToken = default);
    Task SetPausedAsync(PlaybackTarget target, bool paused, CancellationToken cancellationToken = default);
    Task WaitUntilTrackEndedAsync(long streamChannelId, CancellationToken cancellationToken = default);
}

/// <summary>Publishes prepared Ogg/Opus audio to one SFU room per stream channel.</summary>
public sealed class SfuStreamingChannelSink : IPlaybackSink, ISfuPublisherSink
{
    private readonly SfuPublisherSessionManager _sessions;
    private readonly SfuClientHolder _holder;
    private readonly TrackPrepService _prep;
    private readonly ILogger<SfuStreamingChannelSink> _logger;

    public SfuStreamingChannelSink(
        SfuPublisherSessionManager sessions,
        SfuClientHolder holder,
        TrackPrepService prep,
        ILogger<SfuStreamingChannelSink> logger)
    {
        _sessions = sessions;
        _holder = holder;
        _prep = prep;
        _logger = logger;
    }

    public string Name => "streaming-sfu";

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
            throw new InvalidOperationException("SFU stream playback accepts audio assets only.");
        }

        var client = _holder.GetClient();
        var playable = await _prep.EnsurePreparedAsync(client, track, PreparedAssetKind.Audio, cancellationToken).ConfigureAwait(false);
        if (!IsSupportedAudioUrl(playable.MediaUrl))
        {
            throw new InvalidOperationException("SFU stream playback requires an absolute .ogg or .opus URL.");
        }

        var token = await GenerateMeetTokenAsync(client, target.ChannelId, cancellationToken).ConfigureAwait(false);
        var session = _sessions.GetOrCreate(target.ChannelId);
        var trackId = BuildTrackId(track);
        await session.PlayAsync(
                trackId,
                playable.MediaUrl,
                token,
                ct => GenerateMeetTokenAsync(client, target.ChannelId, ct),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "SFU stream audio published title={Title} clan={ClanId} channel={ChannelId} track={TrackId}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            trackId);
    }

    public async Task EndTrackAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var session))
        {
            return;
        }

        await session.EndTrackAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPausedAsync(PlaybackTarget target, bool paused, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var session))
        {
            throw new InvalidOperationException("No active SFU publisher session to pause.");
        }

        await session.PauseAsync(paused, cancellationToken).ConfigureAwait(false);
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
            return Task.FromException(new InvalidOperationException("No active SFU publisher session."));
        }

        return session.WaitUntilTrackEndedAsync(cancellationToken);
    }

    private static string BuildTrackId(TrackInfoEntity track)
        => (track.TrackId?.ToString() ?? track.ExternalId ?? track.Title).Trim();

    private static bool IsSupportedAudioUrl(string mediaUrl)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> GenerateMeetTokenAsync(MezonClient client, long channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await client.GenerateMeetTokenAsync(
                new GenerateMeetTokenParams(channelId, string.Empty))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(response.Token))
        {
            throw new InvalidOperationException("Mezon returned an empty SFU meet token.");
        }

        return response.Token;
    }
}
