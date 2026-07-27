using Mezon.Net.Core;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Playback;
using Mezube.Ui;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mezube.Music;

public sealed class MusicPlayer
{
    private readonly ITrackResolver _resolver;
    private readonly StreamingChannelSink _streamingSink;
    private readonly VoiceChannelSink _voiceSink;
    private readonly BindStore _binds;
    private readonly MusicVizAssets _viz;
    private readonly ILogger<MusicPlayer> _logger;
    private readonly ConcurrentDictionary<long, ClanPlayerState> _states = new();

    public MusicPlayer(
        ITrackResolver resolver,
        StreamingChannelSink streamingSink,
        VoiceChannelSink voiceSink,
        BindStore binds,
        MusicVizAssets viz,
        ILogger<MusicPlayer> logger)
    {
        _resolver = resolver;
        _streamingSink = streamingSink;
        _voiceSink = voiceSink;
        _binds = binds;
        _viz = viz;
        _logger = logger;
    }

    public async Task PlayStreamingAsync(
        ICommandContext ctx,
        string query,
        long? streamChannelId,
        CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        long channelId;
        if (streamChannelId is long fromHashtag)
        {
            channelId = fromHashtag;
        }
        else if (ctx.Channel.Type == (int)ChannelType.Streaming)
        {
            channelId = ctx.Channel.Id;
        }
        else if (_binds.TryGetDefaultStreamChannel(clanId, out var fromConfig))
        {
            channelId = fromConfig;
        }
        else
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Invalid request",
                    "Mention a stream channel hashtag."))
                .ConfigureAwait(false);
            return;
        }

        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing("streaming", channelId))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;

        Mezon.Net.Sdk.Entities.TextChannel channel;
        try
        {
            channel = await ctx.Client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetChannelAsync failed for stream {ChannelId}", channelId);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Awkward(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (channel.Type != (int)ChannelType.Streaming)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Error(
                        "Invalid request",
                        $"Channel {channelId} is not a streaming channel."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var (track, resolveError) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
        if (track is null)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    resolveError ?? PlayerMessageBuilder.Error("Not found", "No track matched that query."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var state = GetState(clanId);
        state.Queue.Enqueue(track);
        state.Mode = PlaybackMode.Streaming;
        state.Target = new PlaybackTarget(clanId, channelId);
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;

        if (state.IsPlaying)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        _ = PumpAsync(state, cancellationToken);
    }

    public async Task PlayVoiceAsync(
        ICommandContext ctx,
        string query,
        long? voiceChannelId,
        CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        long channelId;
        if (voiceChannelId is long explicitId)
        {
            channelId = explicitId;
        }
        else if (_binds.TryGetUserVoiceChannel(ctx.Author.Id, out var fromPresence))
        {
            channelId = fromPresence;
        }
        else if (ctx.Channel.Type is (int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice)
        {
            channelId = ctx.Channel.Id;
        }
        else
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Invalid request",
                    "Join a voice channel, or mention it: !play #voice <query>."))
                .ConfigureAwait(false);
            return;
        }

        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing("voice", channelId))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;

        Mezon.Net.Sdk.Entities.TextChannel channel;
        try
        {
            channel = await ctx.Client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetChannelAsync failed for voice {ChannelId}", channelId);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Awkward(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (channel.Type is not ((int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Error(
                        "Invalid request",
                        $"Channel {channelId} is not a voice channel."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var (track, resolveError) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
        if (track is null)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    resolveError ?? PlayerMessageBuilder.Error("Not found", "No track matched that query."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var state = GetState(clanId);
        state.Queue.Enqueue(track);
        state.Mode = PlaybackMode.Voice;
        state.Target = new PlaybackTarget(clanId, channelId, RoomName: channelId.ToString());
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;

        if (state.IsPlaying)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        _ = PumpAsync(state, cancellationToken);
    }

    public async Task SkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var skipped = await SkipInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(skipped
                ? PlayerMessageBuilder.Ok("Skipped", "Moved to the next track (if any).")
                : PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."))
            .ConfigureAwait(false);
    }

    public async Task StopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        await StopInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Stopped", "Playback stopped and queue cleared."))
            .ConfigureAwait(false);
    }

    public Task<bool> SkipInternalAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var state = GetState(clanId);
        if (!state.IsPlaying && state.Queue.Count == 0)
        {
            return Task.FromResult(false);
        }

        return SkipStateAsync(state, cancellationToken);
    }

    public async Task StopInternalAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var state = GetState(clanId);
        state.Queue.Clear();
        // Wake the pump; it owns the single StopAsync for the active track.
        state.CancelTrack();
        state.IsPlaying = false;
        state.TrackStartedAt = null;
        await state.StopNowPlayingProgressAsync().ConfigureAwait(false);
    }

    private async Task<bool> SkipStateAsync(ClanPlayerState state, CancellationToken cancellationToken)
    {
        // Do not StopCurrentSinkAsync here — CancelTrack lets the pump stop once, then advance.
        state.CancelTrack();
        _ = PumpAsync(state, cancellationToken);
        return true;
    }

    public Task ShowQueueAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        return ctx.ReplyAsync(PlayerMessageBuilder.QueueList(state.Queue.Current, state.Queue.Snapshot()));
    }

    public async Task ShowNowPlayingAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        if (state.Queue.Current is null)
        {
            await state.StopNowPlayingProgressAsync().ConfigureAwait(false);
            await ctx.ReplyAsync(PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."))
                .ConfigureAwait(false);
            return;
        }

        await _viz.EnsureAsync(ctx.Client, ctx.CancellationToken).ConfigureAwait(false);

        state.ControlMessageId = ctx.Message.Id;
        state.ControlUserId = ctx.Author.Id;
        var content = BuildNowPlayingContent(state, clanId, includeMusicViz: true);
        await ctx.ReplyAsync(content).ConfigureAwait(false);
        await state.StopNowPlayingProgressAsync().ConfigureAwait(false);
        // Temporarily disabled: live progress edits interrupt embed Animation.
        // state.NowPlayingProgress = FakeProgressBar.Start(...);
    }

    private static Mezon.Net.Client.MessageContent BuildNowPlayingContent(
        ClanPlayerState state,
        long clanId,
        bool includeMusicViz = false)
    {
        var track = state.Queue.Current!;
        var position = state.GetElapsed();
        return PlayerMessageBuilder.NowPlaying(
            track,
            state.Queue.Count,
            state.Mode.ToString().ToLowerInvariant(),
            position: position,
            controlMessageId: state.ControlMessageId,
            controlUserId: state.ControlUserId,
            clanId: clanId,
            includeMusicViz: includeMusicViz);
    }

    private async Task<(TrackInfoEntity? Track, Mezon.Net.Client.MessageContent? Error)> TryResolveAsync(
        ICommandContext ctx,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, PlayerMessageBuilder.Error(
                "Missing track",
                "Provide a YouTube URL or search text."));
        }

        try
        {
            var track = await _resolver.ResolveAsync(
                query.Trim(),
                ctx.Author.Username ?? ctx.Author.Id.ToString(),
                cancellationToken).ConfigureAwait(false);
            if (track is null)
            {
                return (null, PlayerMessageBuilder.Error("Not found", "No track matched that query."));
            }

            return (track, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolve failed for {Query}", query);
            return (null, PlayerMessageBuilder.Awkward());
        }
    }

    private static Task UpdateOrReplyAsync(
        ICommandContext ctx,
        long? messageId,
        Mezon.Net.Client.MessageContent content,
        uint? createTimeSeconds = null)
    {
        if (messageId is long id)
        {
            return ctx.Channel.UpdateMessageAsync(
                id,
                content,
                hideEdited: true,
                createTimeSeconds: createTimeSeconds);
        }

        return ctx.ReplyAsync(content);
    }

    private async Task PumpAsync(ClanPlayerState state, CancellationToken cancellationToken)
    {
        if (!await state.TryEnterPumpAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var track = state.Queue.TryDequeueNext();
                if (track is null || state.Target is null)
                {
                    state.IsPlaying = false;
                    return;
                }

                state.IsPlaying = true;
                using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                state.SetTrackCts(trackCts);
                var playStopwatch = Stopwatch.StartNew();

                try
                {
                    var sink = state.Mode == PlaybackMode.Voice ? (IPlaybackSink)_voiceSink : _streamingSink;
                    _logger.LogInformation(
                        "Playback pipeline start mode={Mode} title={Title} queuedNext={QueuedNext}",
                        state.Mode,
                        track.Title,
                        state.Queue.Count);
                    await sink.PlayAsync(state.Target, track, trackCts.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Playback sink ready mode={Mode} title={Title} elapsedMs={ElapsedMs}",
                        state.Mode,
                        track.Title,
                        playStopwatch.ElapsedMilliseconds);
                    state.TrackStartedAt = DateTimeOffset.UtcNow;
                    await SendNowPlayingAsync(state, includeMusicViz: true).ConfigureAwait(false);

                    try
                    {
                        await WaitForTrackEndAsync(state, track, trackCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                    {
                    }

                    try
                    {
                        await sink.StopAsync(state.Target, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stop sink failed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Playback failed for {Title}", track.Title);
                    await NotifyPlaybackFailureAsync(state, track, ex).ConfigureAwait(false);
                    if (state.Mode == PlaybackMode.Streaming && IsStnInfrastructureFailure(ex))
                    {
                        state.Queue.Clear(clearCurrent: true);
                        break;
                    }
                }
                finally
                {
                    state.TrackStartedAt = null;
                    state.ClearTrackCts();
                    await state.StopNowPlayingProgressAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            state.IsPlaying = false;
            state.ExitPump();
        }
    }

    private async Task SendNowPlayingAsync(ClanPlayerState state, bool includeMusicViz)
    {
        if (state.ControlMessageId is not long messageId || state.ClanId is not long clanId)
        {
            return;
        }

        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            await _viz.EnsureAsync(state.NotifyClient, CancellationToken.None).ConfigureAwait(false);
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            var content = BuildNowPlayingContent(state, clanId, includeMusicViz);
            await channel.UpdateMessageAsync(
                    messageId,
                    content,
                    hideEdited: true,
                    createTimeSeconds: state.ControlMessageCreateTimeSeconds)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send now playing UI for message {MessageId}", messageId);
        }
    }

    private static readonly TimeSpan UpNextLead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrackEndBuffer = TimeSpan.FromSeconds(2);

    private async Task WaitForTrackEndAsync(
        ClanPlayerState state,
        TrackInfoEntity track,
        CancellationToken cancellationToken)
    {
        if (track.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            return;
        }

        var untilNotify = duration > UpNextLead ? duration - UpNextLead : TimeSpan.Zero;
        var afterNotify = (duration > UpNextLead ? UpNextLead : duration) + TrackEndBuffer;

        if (untilNotify > TimeSpan.Zero)
        {
            await Task.Delay(untilNotify, cancellationToken).ConfigureAwait(false);
        }

        var next = state.Queue.PeekNext();
        if (next is not null)
        {
            var secondsRemaining = (int)Math.Ceiling(
                Math.Max(1, (duration > UpNextLead ? UpNextLead : duration).TotalSeconds));
            await NotifyUpNextAsync(state, next, secondsRemaining).ConfigureAwait(false);
        }

        if (afterNotify > TimeSpan.Zero)
        {
            await Task.Delay(afterNotify, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyUpNextAsync(ClanPlayerState state, TrackInfoEntity next, int secondsRemaining)
    {
        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            await channel.SendAsync(PlayerMessageBuilder.UpNext(next, secondsRemaining)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify up-next for {Title}", next.Title);
        }
    }

    private async Task NotifyPlaybackFailureAsync(ClanPlayerState state, TrackInfoEntity track, Exception ex)
    {
        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            await channel.SendAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(notifyEx, "Failed to notify playback error for {Title}", track.Title);
        }
    }

    private static bool IsStnInfrastructureFailure(Exception ex)
    {
        var msg = ex.ToString();
        return msg.Contains("502", StringComparison.Ordinal)
               || msg.Contains("status code '200'", StringComparison.Ordinal)
               || msg.Contains("STN streaming WebSocket", StringComparison.Ordinal);
    }

    private ClanPlayerState GetState(long clanId)
        => _states.GetOrAdd(clanId, _ => new ClanPlayerState());

    private enum PlaybackMode
    {
        Streaming,
        Voice,
    }

    private sealed class ClanPlayerState
    {
        private readonly SemaphoreSlim _pumpGate = new(1, 1);
        private CancellationTokenSource? _trackCts;

        public MusicQueue Queue { get; } = new();
        public PlaybackTarget? Target { get; set; }
        public PlaybackMode Mode { get; set; } = PlaybackMode.Streaming;
        public bool IsPlaying { get; set; }
        public DateTimeOffset? TrackStartedAt { get; set; }
        public long? ClanId { get; set; }
        public long? ControlMessageId { get; set; }
        public uint? ControlMessageCreateTimeSeconds { get; set; }
        public long? ControlUserId { get; set; }
        public FakeProgressBar? NowPlayingProgress { get; set; }
        public MezonClient? NotifyClient { get; set; }
        public long? NotifyChannelId { get; set; }

        public TimeSpan GetElapsed()
        {
            if (TrackStartedAt is not { } started)
            {
                return TimeSpan.Zero;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        public async Task StopNowPlayingProgressAsync()
        {
            var progress = NowPlayingProgress;
            NowPlayingProgress = null;
            if (progress is not null)
            {
                await progress.StopAsync().ConfigureAwait(false);
            }
        }

        public async Task<bool> TryEnterPumpAsync()
        {
            if (!await _pumpGate.WaitAsync(0).ConfigureAwait(false))
            {
                return false;
            }

            return true;
        }

        public void ExitPump() => _pumpGate.Release();

        public void SetTrackCts(CancellationTokenSource cts) => _trackCts = cts;

        public void ClearTrackCts() => _trackCts = null;

        public void CancelTrack()
        {
            try
            {
                _trackCts?.Cancel();
            }
            catch
            {
                // ignored
            }
        }
    }
}
