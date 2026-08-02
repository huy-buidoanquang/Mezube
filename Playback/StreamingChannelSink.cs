using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Stn;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Playback;

public sealed class StreamingChannelSink : IPlaybackSink
{
    private readonly StnStreamingSessionManager _sessions;
    private readonly StreamingChannelSinkHolder _holder;
    private readonly TrackPrepService _prep;
    private readonly ILogger<StreamingChannelSink> _logger;

    public StreamingChannelSink(
        StnStreamingSessionManager sessions,
        StreamingChannelSinkHolder holder,
        TrackPrepService prep,
        ILogger<StreamingChannelSink> logger)
    {
        _sessions = sessions;
        _holder = holder;
        _prep = prep;
        _logger = logger;
    }

    public string Name => "streaming";

    public async Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
    {
        var client = _holder.GetClient();
        var total = Stopwatch.StartNew();
        var process = Stopwatch.StartNew();
        var playable = await _prep.EnsurePreparedAsync(client, track, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming media processed title={Title} clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs} url={Url}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            process.ElapsedMilliseconds,
            playable.MediaUrl);

        if (!StnMediaUrl.IsSupportedOpusSourceUrl(playable.MediaUrl))
        {
            throw new InvalidOperationException(
                $"Streaming play requires .ogg/.opus URL path, got: {playable.MediaUrl}");
        }

        var auth = Stopwatch.StartNew();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming auth token ready clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs}",
            target.ClanId,
            target.ChannelId,
            auth.ElapsedMilliseconds);

        var stn = _sessions.GetOrCreate(target.ChannelId);
        var connect = Stopwatch.StartNew();
        await stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming STN websocket ready clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs}",
            target.ClanId,
            target.ChannelId,
            connect.ElapsedMilliseconds);
        var play = Stopwatch.StartNew();
        await stn.PlayAsync(target.ClanId, target.ChannelId, playable.MediaUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming play pipeline completed title={Title} clan={ClanId} channel={ChannelId} wsSendElapsedMs={WsSendElapsedMs} totalElapsedMs={TotalElapsedMs}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            play.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
    }

    /// <summary>
    /// End the current URL only (skip / inter-track). Keeps the WS session and listeners.
    /// </summary>
    public async Task EndTrackAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var stn) || stn is null)
        {
            return;
        }

        try
        {
            await stn.EndTrackAsync(target.ClanId, target.ChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Streaming end-track failed clan={ClanId} channel={ChannelId}",
                target.ClanId,
                target.ChannelId);
        }
    }

    public async Task SetPausedAsync(PlaybackTarget target, bool paused, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var stn) || stn is null)
        {
            throw new InvalidOperationException("No active streaming session to pause.");
        }

        var client = _holder.GetClient();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        await stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
            .ConfigureAwait(false);
        await stn.SetPausedAsync(target.ClanId, target.ChannelId, paused, cancellationToken).ConfigureAwait(false);
    }

    public bool IsPaused(long streamChannelId)
        => _sessions.TryGet(streamChannelId, out var stn) && stn is not null && stn.IsPaused;

    /// <summary>
    /// Full session teardown (<c>stop_publisher</c>). Use for !stop / idle destroy / hard failure.
    /// </summary>
    public async Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(target.ChannelId, out var stn) || stn is null)
        {
            return;
        }

        var client = _holder.GetClient();
        try
        {
            var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
            await stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
                .ConfigureAwait(false);
            await stn.StopPublisherAsync(target.ClanId, target.ChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Streaming stop_publisher failed clan={ClanId} channel={ChannelId}; disposing session",
                target.ClanId,
                target.ChannelId);
        }
        finally
        {
            await _sessions.RemoveAndDisposeAsync(target.ChannelId).ConfigureAwait(false);
        }
    }

    public Task WaitUntilTrackEndedAsync(long streamChannelId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGet(streamChannelId, out var stn) || stn is null)
        {
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return stn.WaitUntilTrackEndedAsync(cancellationToken);
    }
}
