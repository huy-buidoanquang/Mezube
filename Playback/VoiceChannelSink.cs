using System.Collections.Concurrent;
using System.Diagnostics;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Music;
using Mezube.Stn;
using Microsoft.Extensions.Logging;

namespace Mezube.Playback;

/// <summary>
/// Voice pipeline log contract:
/// Information = transport/lifecycle milestones visible in normal operations.
/// Debug = timings, URLs, cleanup attempts, and other investigation detail.
/// Warning/Error = degraded behavior or failed playback actions.
/// </summary>
public sealed class VoiceChannelSink : IPlaybackSink
{
    private readonly StnRestClientV2 _voiceV2;
    private readonly StnWhipClient _whip;
    private readonly WhipFfmpegPublisher _whipPublisher;
    private readonly BotOptions _options;
    private readonly VoiceChannelSinkHolder _holder;
    private readonly TrackPrepService _prep;
    private readonly ILogger<VoiceChannelSink> _logger;
    private readonly ConcurrentDictionary<string, VoiceTransport> _roomTransport = new(StringComparer.Ordinal);

    public VoiceChannelSink(
        StnRestClientV2 voiceV2,
        StnWhipClient whip,
        WhipFfmpegPublisher whipPublisher,
        BotOptions options,
        VoiceChannelSinkHolder holder,
        TrackPrepService prep,
        ILogger<VoiceChannelSink> logger)
    {
        _voiceV2 = voiceV2;
        _whip = whip;
        _whipPublisher = whipPublisher;
        _options = options;
        _holder = holder;
        _prep = prep;
        _logger = logger;
    }

    public string Name => "voice";

    public async Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
    {
        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName!;

        var client = _holder.GetClient();
        var total = Stopwatch.StartNew();

        try
        {
            var stop = Stopwatch.StartNew();
            await StopAsync(target, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Voice pre-play stop completed room={Room} elapsedMs={ElapsedMs}", roomName, stop.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-play voice stop failed; continuing");
        }

        var process = Stopwatch.StartNew();
        var playable = await _prep.EnsurePreparedAsync(client, track, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Voice media processed title={Title} room={Room} elapsedMs={ElapsedMs} url={Url}",
            track.Title,
            roomName,
            process.ElapsedMilliseconds,
            playable.MediaUrl);
        if (!StnMediaUrl.IsSupportedOpusSourceUrl(playable.MediaUrl))
        {
            throw new InvalidOperationException(
                $"Voice play requires .ogg/.opus URL path, got: {playable.MediaUrl}");
        }

        var auth = Stopwatch.StartNew();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        _logger.LogDebug("Voice auth token ready room={Room} elapsedMs={ElapsedMs}", roomName, auth.ElapsedMilliseconds);
        var play = Stopwatch.StartNew();
        var participantIdentity = StnRestClientV2.NewPublisherIdentity(client.BotId);

        var useWhip = _options.StnWhipEnabled;
        if (useWhip && !_whipPublisher.IsAvailable)
        {
            _logger.LogWarning(
                "StnWhipEnabled=true but ffmpeg WHIP muxer is unavailable; falling back to voice v2 for room={Room}",
                roomName);
            useWhip = false;
        }

        string transport;
        if (useWhip)
        {
            _logger.LogInformation(
                "Voice transport selected transport=whip room={Room} title={Title} codecMode={CodecMode}",
                roomName,
                playable.Title,
                _options.WhipEncoderDisabled ? "copy" : "libopus");
            try
            {
                await PlayWhipAsync(
                        authToken,
                        roomName,
                        participantIdentity,
                        playable.MediaUrl,
                        playable.Title,
                        cancellationToken)
                    .ConfigureAwait(false);
                transport = "whip";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // WHIP often fails on ICE/DTLS from home NAT; voice v2 (CDN publish) still works.
                _logger.LogWarning(
                    ex,
                    "WHIP publish failed; falling back to voice v2 room={Room} title={Title}",
                    roomName,
                    playable.Title);
                await PlayVoiceV2Async(
                        authToken,
                        roomName,
                        participantIdentity,
                        playable.MediaUrl,
                        playable.Title,
                        cancellationToken)
                    .ConfigureAwait(false);
                transport = "v2";
            }
        }
        else
        {
            _logger.LogInformation(
                "Voice transport selected transport=v2 room={Room} title={Title}",
                roomName,
                playable.Title);
            await PlayVoiceV2Async(
                    authToken,
                    roomName,
                    participantIdentity,
                    playable.MediaUrl,
                    playable.Title,
                    cancellationToken)
                .ConfigureAwait(false);
            transport = "v2";
        }

        _logger.LogDebug(
            "Voice play pipeline completed transport={Transport} title={Title} room={Room} stnElapsedMs={StnElapsedMs} totalElapsedMs={TotalElapsedMs}",
            transport,
            track.Title,
            roomName,
            play.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
    }

    public async Task<string> WaitUntilTerminalAsync(string roomName, CancellationToken cancellationToken = default)
    {
        if (_roomTransport.TryGetValue(roomName, out var transport) && transport == VoiceTransport.Whip)
        {
            var status = await _whipPublisher.WaitUntilEndedAsync(roomName, cancellationToken).ConfigureAwait(false);
            _roomTransport.TryRemove(roomName, out _);
            return status;
        }

        var client = _holder.GetClient();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        return await _voiceV2.WaitUntilTerminalAsync(authToken, roomName, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName!;

        var client = _holder.GetClient();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);

        _roomTransport.TryRemove(roomName, out var transport);
        _logger.LogDebug("Voice stop requested room={Room} transport={Transport}", roomName, transport);

        try
        {
            await _whipPublisher.StopAsync(roomName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WHIP ffmpeg stop failed room={Room}", roomName);
        }

        if (_whip.TryGetSession(roomName, out _) || transport == VoiceTransport.Whip)
        {
            try
            {
                await _whip.StopAsync(authToken, roomName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WHIP STN stop failed room={Room}", roomName);
            }
        }

        try
        {
            await _voiceV2.StopAsync(authToken, roomName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice v2 stop failed room={Room}", roomName);
        }
    }

    private async Task PlayVoiceV2Async(
        string authToken,
        string roomName,
        string participantIdentity,
        string mediaUrl,
        string title,
        CancellationToken cancellationToken)
    {
        await _voiceV2.PlayUntilPublishingAsync(
                authToken,
                roomName,
                participantIdentity,
                _options.BotDisplayName,
                mediaUrl,
                title,
                cancellationToken)
            .ConfigureAwait(false);
        _roomTransport[roomName] = VoiceTransport.V2;
    }

    private async Task PlayWhipAsync(
        string authToken,
        string roomName,
        string participantIdentity,
        string mediaUrl,
        string title,
        CancellationToken cancellationToken)
    {
        var session = await _whip.StartAsync(
                authToken,
                roomName,
                participantIdentity,
                _options.BotDisplayName,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _whipPublisher.StartUntilPublishingAsync(
                    roomName,
                    mediaUrl,
                    session.WhipUrl,
                    session.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            _roomTransport[roomName] = VoiceTransport.Whip;
            _logger.LogInformation(
                "WHIP voice publishing title={Title} room={Room} session={Session}",
                title,
                roomName,
                session.SessionId);
        }
        catch
        {
            try
            {
                await _whipPublisher.StopAsync(roomName).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await _whip.StopAsync(authToken, roomName, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    private enum VoiceTransport
    {
        V2,
        Whip,
    }
}
