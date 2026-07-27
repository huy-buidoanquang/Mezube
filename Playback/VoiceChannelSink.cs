using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Stn;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Playback;

public sealed class VoiceChannelSink : IPlaybackSink
{
    private readonly StnRestClientV2 _voiceV2;
    private readonly BotOptions _options;
    private readonly VoiceChannelSinkHolder _holder;
    private readonly PlayableMediaPreparer _preparer;
    private readonly ILogger<VoiceChannelSink> _logger;

    public VoiceChannelSink(
        StnRestClientV2 voiceV2,
        BotOptions options,
        VoiceChannelSinkHolder holder,
        PlayableMediaPreparer preparer,
        ILogger<VoiceChannelSink> logger)
    {
        _voiceV2 = voiceV2;
        _options = options;
        _holder = holder;
        _preparer = preparer;
        _logger = logger;
    }

    public string Name => "voice";

    public async Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
    {
        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName;

        var client = _holder.GetClient();
        var total = Stopwatch.StartNew();

        try
        {
            var stop = Stopwatch.StartNew();
            await StopAsync(target, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Voice pre-play stop completed room={Room} elapsedMs={ElapsedMs}", roomName, stop.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-play voice stop failed; continuing");
        }

        var prepare = Stopwatch.StartNew();
        var playable = await _preparer.EnsurePlayableAsync(client, track, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Voice media prepared title={Title} room={Room} elapsedMs={ElapsedMs} url={Url}",
            track.Title,
            roomName,
            prepare.ElapsedMilliseconds,
            playable.MediaUrl);
        if (!playable.MediaUrl.Contains(".ogg", StringComparison.OrdinalIgnoreCase)
            && !playable.MediaUrl.Contains(".opus", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Voice play requires .ogg/.opus URL, got: {playable.MediaUrl}");
        }

        var auth = Stopwatch.StartNew();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        _logger.LogInformation("Voice auth token ready room={Room} elapsedMs={ElapsedMs}", roomName, auth.ElapsedMilliseconds);
        var play = Stopwatch.StartNew();
        var participantIdentity = StnRestClientV2.NewPublisherIdentity(client.BotId);
        await _voiceV2.PlayUntilPublishingAsync(
            authToken,
            roomName,
            participantIdentity,
            _options.BotDisplayName,
            playable.MediaUrl,
            playable.Title,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Voice play pipeline completed title={Title} room={Room} stnElapsedMs={StnElapsedMs} totalElapsedMs={TotalElapsedMs}",
            track.Title,
            roomName,
            play.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
    }

    public async Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName;

        var client = _holder.GetClient();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        await _voiceV2.StopAsync(authToken, roomName, cancellationToken).ConfigureAwait(false);
    }
}
