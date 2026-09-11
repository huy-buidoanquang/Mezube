using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Music;
using Mezube.Stn;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Playback;

public sealed class StreamingChannelSink : IPlaybackSink
{
    /// <summary>
    /// Fresh STN WS + first connect_publisher can ack before Mezon emits StreamingJoined.
    /// Wait briefly; one reconnect_publisher usually settles presence after restart / new clan.
    /// </summary>
    private static readonly TimeSpan StreamingJoinedWait = TimeSpan.FromSeconds(2);

    private readonly StnStreamingSessionManager _sessions;
    private readonly StreamingChannelSinkHolder _holder;
    private readonly StnCredentialProvider _credentials;
    private readonly TrackPrepService _prep;
    private readonly ILogger<StreamingChannelSink> _logger;

    public StreamingChannelSink(
        StnStreamingSessionManager sessions,
        StreamingChannelSinkHolder holder,
        StnCredentialProvider credentials,
        TrackPrepService prep,
        ILogger<StreamingChannelSink> logger)
    {
        _sessions = sessions;
        _holder = holder;
        _credentials = credentials;
        _prep = prep;
        _logger = logger;
    }

    public string Name => "streaming";

    public async Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
        => await PlayAsync(target, track, PreparedAssetKind.Audio, cancellationToken).ConfigureAwait(false);

    public async Task PlayAsync(
        PlaybackTarget target,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        var client = _holder.GetClient();
        var total = Stopwatch.StartNew();
        var process = Stopwatch.StartNew();
        var playable = await _prep.EnsurePreparedAsync(client, track, kind, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming media processed title={Title} clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs} url={Url}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            process.ElapsedMilliseconds,
            playable.MediaUrl);

        if (!StnMediaUrl.IsSupportedStreamingSourceUrl(playable.MediaUrl))
        {
            throw new InvalidOperationException(
                $"Streaming play requires .ogg/.opus/.webm URL path, got: {playable.MediaUrl}");
        }

        // App-owned RT join (not event-path): stream channels are not covered by JoinClanChat alone.
        // Without this, STN connect_publisher can succeed while Mezon never emits StreamingJoined
        // on the first !s after bot restart / new clan.
        try
        {
            var channel = await client.GetChannelAsync(target.ChannelId, cancellationToken).ConfigureAwait(false);
            await channel.JoinAsync().ConfigureAwait(false);
            _logger.LogDebug(
                "Joined Mezon stream channel presence clan={ClanId} channel={ChannelId}",
                target.ClanId,
                target.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Stream channel JoinAsync failed clan={ClanId} channel={ChannelId}; continuing STN publish",
                target.ClanId,
                target.ChannelId);
        }

        var stn = _sessions.GetOrCreate(target.ChannelId);
        var play = Stopwatch.StartNew();
        await ConnectAndPublishWithRetryAsync(
                client,
                stn,
                target,
                playable.MediaUrl,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming play pipeline completed title={Title} clan={ClanId} channel={ChannelId} wsSendElapsedMs={WsSendElapsedMs} totalElapsedMs={TotalElapsedMs}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            play.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
    }

    private async Task ConnectAndPublishWithRetryAsync(
        Mezon.Net.Sdk.MezonClient client,
        StnSocketClient stn,
        PlaybackTarget target,
        string mediaUrl,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                bool freshWs;
                var connect = Stopwatch.StartNew();
                if (stn.IsConnected)
                {
                    freshWs = false;
                }
                else
                {
                    var credential = await _credentials.GetPrimaryAsync().ConfigureAwait(false);
                    try
                    {
                        freshWs = await stn.EnsureConnectedAsync(
                                credential.Value,
                                client.BotId,
                                client.Username,
                                cancellationToken,
                                credential.Kind,
                                attempt,
                                target.ChannelId)
                            .ConfigureAwait(false);
                    }
                    catch (StnConnectionException ex)
                        when (credential.Kind == StnCredentialKind.SessionId && ex.AllowsJwtFallback)
                    {
                        _logger.LogWarning(
                            "STN SID handshake rejected; retrying with refreshed JWT channel={ChannelId} statusCode={StatusCode}",
                            target.ChannelId,
                            ex.StatusCode);
                        var jwt = await _credentials.GetJwtAsync().ConfigureAwait(false);
                        freshWs = await stn.EnsureConnectedAsync(
                                jwt.Value,
                                client.BotId,
                                client.Username,
                                cancellationToken,
                                jwt.Kind,
                                attempt,
                                target.ChannelId)
                            .ConfigureAwait(false);
                    }
                }

                _logger.LogDebug(
                    "Streaming STN websocket ready clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs} freshWs={FreshWs} attempt={Attempt}",
                    target.ClanId,
                    target.ChannelId,
                    connect.ElapsedMilliseconds,
                    freshWs,
                    attempt);
                await PublishAndConfirmPresenceAsync(
                        client,
                        stn,
                        target,
                        mediaUrl,
                        retryIfNoJoined: freshWs,
                        cancellationToken,
                        attempt)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maximumAttempts && IsRetryableBeforePublishAck(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Transient STN failure before publish ack; replacing session and retrying channel={ChannelId} attempt={Attempt}",
                    target.ChannelId,
                    attempt);
                await stn.InvalidateAndDisconnectAsync("Transient failure before connect_publisher acknowledgement")
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(500 + Random.Shared.Next(0, 251)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableBeforePublishAck(Exception exception)
        => exception is StnConnectionException { Retryable: true }
           || exception is StnPublisherException { Retryable: true, Phase: not "streaming" };

    private async Task PublishAndConfirmPresenceAsync(
        Mezon.Net.Sdk.MezonClient client,
        StnSocketClient stn,
        PlaybackTarget target,
        string mediaUrl,
        bool retryIfNoJoined,
        CancellationToken cancellationToken,
        int attempt)
    {
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> onJoined = () =>
        {
            joined.TrySetResult();
            return Task.CompletedTask;
        };

        client.StreamingJoined += onJoined;
        try
        {
            await stn.PlayAsync(target.ClanId, target.ChannelId, mediaUrl, cancellationToken, attempt)
                .ConfigureAwait(false);

            if (!retryIfNoJoined)
            {
                return;
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(StreamingJoinedWait);
            try
            {
                await joined.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
                _logger.LogDebug(
                    "StreamingJoined observed after connect_publisher channel={ChannelId}",
                    target.ChannelId);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "No StreamingJoined within {WaitMs}ms after first connect_publisher channel={ChannelId}; retrying once",
                    (int)StreamingJoinedWait.TotalMilliseconds,
                    target.ChannelId);
            }

            // Reset waiter for the retry attempt.
            joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await stn.PlayAsync(target.ClanId, target.ChannelId, mediaUrl, cancellationToken, attempt)
                .ConfigureAwait(false);

            using var retryWaitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            retryWaitCts.CancelAfter(StreamingJoinedWait);
            try
            {
                await joined.Task.WaitAsync(retryWaitCts.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "StreamingJoined observed after connect_publisher retry channel={ChannelId}",
                    target.ChannelId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Still no StreamingJoined after retry channel={ChannelId}; STN ack ok but Mezon presence may be missing",
                    target.ChannelId);
            }
        }
        finally
        {
            client.StreamingJoined -= onJoined;
        }
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

        try
        {
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
            return Task.FromCanceled(cancellationToken.CanBeCanceled
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return stn.WaitUntilTrackEndedAsync(cancellationToken);
    }
}
