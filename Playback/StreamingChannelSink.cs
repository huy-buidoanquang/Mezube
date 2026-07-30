using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Stn;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Playback;

public sealed class StreamingChannelSink : IPlaybackSink
{
    private readonly StnSocketClient _stn;
    private readonly StreamingChannelSinkHolder _holder;
    private readonly TrackPrepService _prep;
    private readonly ILogger<StreamingChannelSink> _logger;

    public StreamingChannelSink(
        StnSocketClient stn,
        StreamingChannelSinkHolder holder,
        TrackPrepService prep,
        ILogger<StreamingChannelSink> logger)
    {
        _stn = stn;
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
        try
        {
            var authToken = await client.GetAuthTokenAsync().ConfigureAwait(false);
            await _stn.EnsureConnectedAsync(authToken, client.BotId, client.Username, cancellationToken)
                .ConfigureAwait(false);
            await _stn.StopAsync(target.ClanId, target.ChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Streaming stop failed clan={ClanId} channel={ChannelId}; forcing STN disconnect",
                target.ClanId,
                target.ChannelId);
            await _stn.DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task WaitUntilPublisherEndedAsync(CancellationToken cancellationToken = default)
        => _stn.WaitUntilPublisherEndedAsync(cancellationToken);
}
