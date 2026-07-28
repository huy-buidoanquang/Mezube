using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Stn;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Playback;

public sealed class StreamingChannelSink : IPlaybackSink
{
    private readonly StnSocketClient _stn;
    private readonly StreamingChannelSinkHolder _holder;
    private readonly PlayableMediaPreparer _preparer;
    private readonly ILogger<StreamingChannelSink> _logger;

    public StreamingChannelSink(
        StnSocketClient stn,
        StreamingChannelSinkHolder holder,
        PlayableMediaPreparer preparer,
        ILogger<StreamingChannelSink> logger)
    {
        _stn = stn;
        _holder = holder;
        _preparer = preparer;
        _logger = logger;
    }

    public string Name => "streaming";

    public async Task PlayAsync(PlaybackTarget target, TrackInfoEntity track, CancellationToken cancellationToken = default)
    {
        var client = _holder.GetClient();
        var total = Stopwatch.StartNew();
        var prepare = Stopwatch.StartNew();
        var playable = await _preparer.EnsurePlayableAsync(client, track, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming media prepared title={Title} clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs} url={Url}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            prepare.ElapsedMilliseconds,
            playable.MediaUrl);
        var auth = Stopwatch.StartNew();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming auth token ready clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs}",
            target.ClanId,
            target.ChannelId,
            auth.ElapsedMilliseconds);
        var connect = Stopwatch.StartNew();
        await _stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming STN websocket ready clan={ClanId} channel={ChannelId} elapsedMs={ElapsedMs}",
            target.ClanId,
            target.ChannelId,
            connect.ElapsedMilliseconds);
        var play = Stopwatch.StartNew();
        await _stn.PlayAsync(target.ClanId, target.ChannelId, playable.MediaUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Streaming play pipeline completed title={Title} clan={ClanId} channel={ChannelId} wsSendElapsedMs={WsSendElapsedMs} totalElapsedMs={TotalElapsedMs}",
            track.Title,
            target.ClanId,
            target.ChannelId,
            play.ElapsedMilliseconds,
            total.ElapsedMilliseconds);
    }

    public async Task StopAsync(PlaybackTarget target, CancellationToken cancellationToken = default)
    {
        var client = _holder.GetClient();
        var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
        await _stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
            .ConfigureAwait(false);
        await _stn.StopAsync(target.ClanId, target.ChannelId, cancellationToken).ConfigureAwait(false);
    }
}
