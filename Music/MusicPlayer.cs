using Mezon.Net.Core;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Media;
using Mezube.Music.Interactive;
using Mezube.Playback;
using Mezube.Ui;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    private readonly ITrackResolver _resolver;
    private readonly StreamingChannelSink _streamingSink;
    private readonly VoiceChannelSink _voiceSink;
    private readonly BindStore _binds;
    private readonly MusicVizAssets _viz;
    private readonly PlaybackAccess _access;
    private readonly TrackPrepService _prep;
    private readonly BotOptions _options;
    private readonly ILogger<MusicPlayer> _logger;
    private readonly ConcurrentDictionary<long, ClanPlaybackSession> _states = new();
    private readonly SemaphoreSlim _playSlots;
    private readonly IClanPlayerStore _playerStore;
    private readonly IPlayHistoryRepository _history;
    private readonly ITrackLibraryService _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ICommandChannelRepository _commandChannels;
    private readonly SoundCloudSetImporter _soundCloudSets;
    private readonly YoutubeTrackResolver _youtube;
    private readonly ExternalPlaylistImporter _externalPlaylists;
    private readonly IInteractiveSessionStore _sessions;

    public MusicPlayer(
        ITrackResolver resolver,
        StreamingChannelSink streamingSink,
        VoiceChannelSink voiceSink,
        BindStore binds,
        MusicVizAssets viz,
        PlaybackAccess access,
        TrackPrepService prep,
        BotOptions options,
        IClanPlayerStore playerStore,
        IPlayHistoryRepository history,
        ITrackLibraryService tracks,
        IPlaylistRepository playlists,
        ICommandChannelRepository commandChannels,
        SoundCloudSetImporter soundCloudSets,
        YoutubeTrackResolver youtube,
        ExternalPlaylistImporter externalPlaylists,
        IInteractiveSessionStore sessions,
        ILogger<MusicPlayer> logger)
    {
        _resolver = resolver;
        _streamingSink = streamingSink;
        _voiceSink = voiceSink;
        _binds = binds;
        _viz = viz;
        _access = access;
        _prep = prep;
        _options = options;
        _playerStore = playerStore;
        _history = history;
        _tracks = tracks;
        _playlists = playlists;
        _commandChannels = commandChannels;
        _soundCloudSets = soundCloudSets;
        _youtube = youtube;
        _externalPlaylists = externalPlaylists;
        _sessions = sessions;
        _logger = logger;
        _playSlots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPlayback));
    }

    /// <summary>
    /// Auto-mode for <c>!play</c>:
    /// - Hashtag present ? voice/stream by that channel's type.
    /// - Else if the message channel is voice/stream ? play there.
    /// - Else prefer <c>default_stream_channel_id</c>, then user's voice presence.
    /// </summary>
    public async Task PlayAutoAsync(
        ICommandContext ctx,
        string query,
        long? hashtagChannelId,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureCommandChannelAsync(ctx, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var (dest, error) = await TryResolvePlayDestinationAsync(ctx, hashtagChannelId, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            await ctx.ReplyAsync(error).ConfigureAwait(false);
            return;
        }

        var resolved = dest!.Value;
        if (resolved.Mode == PlaybackMode.Streaming)
        {
            await PlayStreamingAsync(ctx, query, resolved.Target.ChannelId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PlayVoiceAsync(ctx, query, resolved.Target.ChannelId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Same destination rules as <see cref="PlayAutoAsync"/>: hashtag → current channel → default stream → voice presence.
    /// </summary>
    private async Task<(ResolvedPlayDestination? Dest, Mezon.Net.Client.MessageContent? Error)> TryResolvePlayDestinationAsync(
        ICommandContext ctx,
        long? hashtagChannelId,
        CancellationToken cancellationToken)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;

        if (hashtagChannelId is long id)
        {
            Mezon.Net.Sdk.Entities.Channel channel;
            try
            {
                channel = await ctx.Client.GetChannelAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return (null, PlayerMessageBuilder.Awkward());
            }

            if (channel.Type == (int)ChannelType.Streaming)
            {
                return (ToStreaming(clanId, id, channel.Name), null);
            }

            if (channel.Type is (int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice)
            {
                return (ToVoice(clanId, id, channel.Name), null);
            }

            return (null, PlayerMessageBuilder.Error(
                "Pick a voice or stream channel",
                "Mention a #voice or #stream channel hashtag."));
        }

        if (ctx.Channel.Type == (int)ChannelType.Streaming)
        {
            return (ToStreaming(clanId, ctx.Channel.Id, ctx.Channel.Name), null);
        }

        if (ctx.Channel.Type is (int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice)
        {
            return (ToVoice(clanId, ctx.Channel.Id, ctx.Channel.Name), null);
        }

        var defaultStreamChannelId = await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken)
            .ConfigureAwait(false);
        if (defaultStreamChannelId is long ds)
        {
            Mezon.Net.Sdk.Entities.Channel channel;
            try
            {
                channel = await ctx.Client.GetChannelAsync(ds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return (null, PlayerMessageBuilder.Awkward());
            }

            if (channel.Type != (int)ChannelType.Streaming)
            {
                return (null, PlayerMessageBuilder.Error(
                    "Not a stream channel",
                    $"{PlayerMessageBuilder.FormatChannelMention(channel.Name, ds)} isn’t a streaming channel."));
            }

            return (ToStreaming(clanId, ds, channel.Name), null);
        }

        if (_binds.TryGetUserVoiceChannel(clanId, ctx.Author.Id, out var voiceId))
        {
            Mezon.Net.Sdk.Entities.Channel channel;
            try
            {
                channel = await ctx.Client.GetChannelAsync(voiceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return (null, PlayerMessageBuilder.Awkward());
            }

            if (channel.Type is not ((int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice))
            {
                return (null, PlayerMessageBuilder.Error(
                    "Not a voice channel",
                    $"{PlayerMessageBuilder.FormatChannelMention(channel.Name, voiceId)} isn’t a voice channel."));
            }

            return (ToVoice(clanId, voiceId, channel.Name), null);
        }

        return (null, PlayerMessageBuilder.Error(
            "Where should I play?",
            "Join a voice channel first, or tag a #voice / #stream channel."));

        static ResolvedPlayDestination ToStreaming(long clan, long channelId, string? label)
            => new(new PlaybackTarget(clan, channelId, ChannelLabel: label), PlaybackMode.Streaming);

        static ResolvedPlayDestination ToVoice(long clan, long channelId, string? label)
            => new(
                new PlaybackTarget(clan, channelId, RoomName: channelId.ToString(), ChannelLabel: label),
                PlaybackMode.Voice);
    }

    private readonly record struct ResolvedPlayDestination(PlaybackTarget Target, PlaybackMode Mode);

    public async Task PlayStreamingAsync(
        ICommandContext ctx,
        string query,
        long? streamChannelId,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureCommandChannelAsync(ctx, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

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
                    "Need a stream channel",
                    "Mention a #stream channel hashtag."))
                .ConfigureAwait(false);
            return;
        }

        Mezon.Net.Sdk.Entities.Channel channel;
        try
        {
            channel = await ctx.Client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetChannelAsync failed for stream {ChannelId}", channelId);
            await ctx.ReplyAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
            return;
        }

        if (channel.Type != (int)ChannelType.Streaming)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Not a stream channel",
                    $"{PlayerMessageBuilder.FormatChannelMention(channel.Name, channelId)} isn’t a streaming channel."))
                .ConfigureAwait(false);
            return;
        }

        var destination = PlayerMessageBuilder.FormatDestination("streaming", channel.Name);
        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing(destination))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;
        CommandReplyTracker.Remember(preparing.MessageId, preparingCreateTime, ctx.Channel);

        var target = new PlaybackTarget(clanId, channelId, ChannelLabel: channel.Name);
        if (_soundCloudSets.CanImport(query))
        {
            await EnqueueSoundCloudSetAsync(
                    ctx,
                    query,
                    target,
                    PlaybackMode.Streaming,
                    preparing.MessageId,
                    preparingCreateTime,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IsAbsoluteHttpUrl(query))
        {
            await HandleFreeTextPlayAsync(
                    ctx,
                    query,
                    target,
                    PlaybackMode.Streaming,
                    preparing.MessageId,
                    preparingCreateTime,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var (track, resolveError) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
        if (track is null)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    resolveError ?? PlayerMessageBuilder.TrackNotFound(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (IsTooLarge(track))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.CopyrightBlocked(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var state = GetState(clanId);
        if (state.Queue.TotalCount >= _options.MaxQueuePerClan)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.QueueFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var play = new QueuedPlay(track, target, preparing.MessageId, preparingCreateTime);
        if (state.IsPlaying)
        {
            if (state.Mode != PlaybackMode.Streaming)
            {
                await UpdateOrReplyAsync(
                        ctx,
                        preparing.MessageId,
                        PlayerMessageBuilder.ModeConflict(wantVoice: false),
                        preparingCreateTime)
                    .ConfigureAwait(false);
                return;
            }

            var interruptDefault = state.Queue.CurrentItem?.IsFromDefault == true;
            state.PlayingDefaultPlaylist = false;
            if (interruptDefault)
            {
                state.Queue.EnqueueFront(play);
                await PersistEnqueueAsync(clanId, play, "streaming").ConfigureAwait(false);
                StartBackgroundPrep(ctx.Client, state, play);
                state.LastDestroyReason = PlayerDestroyReason.Skip;
                state.CancelTrack();
                await UpdateOrReplyAsync(
                        ctx,
                        preparing.MessageId,
                        PlayerMessageBuilder.PlayingNext(
                            track.Title,
                            "Cut in ahead of the default playlist."),
                        preparingCreateTime)
                    .ConfigureAwait(false);
                return;
            }

            state.Queue.Enqueue(play);
            await PersistEnqueueAsync(clanId, play, "streaming").ConfigureAwait(false);
            StartBackgroundPrep(ctx.Client, state, play);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count, channel.Name),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (!await TryAcquirePlaySlotAsync().ConfigureAwait(false))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.PlaybackSlotsFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        state.CancelIdleDestroy();
        state.PlayingDefaultPlaylist = false;
        state.Queue.Enqueue(play);
        await PersistEnqueueAsync(clanId, play, "streaming").ConfigureAwait(false);
        state.Mode = PlaybackMode.Streaming;
        state.Target = target;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlMessageHasButtons = false;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        state.HoldsPlaySlot = true;
        ResetPrepToken(state);
        StartBackgroundPrep(ctx.Client, state, play);
        StartPump(state, clanId);
    }

    public async Task PlayVoiceAsync(
        ICommandContext ctx,
        string query,
        long? voiceChannelId,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureCommandChannelAsync(ctx, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        long channelId;
        if (voiceChannelId is long explicitId)
        {
            channelId = explicitId;
        }
        else if (_binds.TryGetUserVoiceChannel(clanId, ctx.Author.Id, out var fromPresence))
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
                    "Join a voice channel first",
                    $"Hop into voice, or tag it: {_options.CommandPrefix}play #voice <query>."))
                .ConfigureAwait(false);
            return;
        }

        Mezon.Net.Sdk.Entities.Channel channel;
        try
        {
            channel = await ctx.Client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetChannelAsync failed for voice {ChannelId}", channelId);
            await ctx.ReplyAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
            return;
        }

        if (channel.Type is not ((int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Not a voice channel",
                    $"{PlayerMessageBuilder.FormatChannelMention(channel.Name, channelId)} isn’t a voice channel."))
                .ConfigureAwait(false);
            return;
        }

        var destination = PlayerMessageBuilder.FormatDestination("voice", channel.Name);
        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing(destination))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;
        CommandReplyTracker.Remember(preparing.MessageId, preparingCreateTime, ctx.Channel);

        var target = new PlaybackTarget(clanId, channelId, RoomName: channelId.ToString(), ChannelLabel: channel.Name);
        if (_soundCloudSets.CanImport(query))
        {
            await EnqueueSoundCloudSetAsync(
                    ctx,
                    query,
                    target,
                    PlaybackMode.Voice,
                    preparing.MessageId,
                    preparingCreateTime,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IsAbsoluteHttpUrl(query))
        {
            await HandleFreeTextPlayAsync(
                    ctx,
                    query,
                    target,
                    PlaybackMode.Voice,
                    preparing.MessageId,
                    preparingCreateTime,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var (track, resolveError) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
        if (track is null)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    resolveError ?? PlayerMessageBuilder.TrackNotFound(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (IsTooLarge(track))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.CopyrightBlocked(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var state = GetState(clanId);
        if (state.Queue.TotalCount >= _options.MaxQueuePerClan)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.QueueFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var play = new QueuedPlay(track, target, preparing.MessageId, preparingCreateTime);
        if (state.IsPlaying)
        {
            if (state.Mode != PlaybackMode.Voice)
            {
                await UpdateOrReplyAsync(
                        ctx,
                        preparing.MessageId,
                        PlayerMessageBuilder.ModeConflict(wantVoice: true),
                        preparingCreateTime)
                    .ConfigureAwait(false);
                return;
            }

            state.PlayingDefaultPlaylist = false;
            state.Queue.Enqueue(play);
            await PersistEnqueueAsync(clanId, play, "voice").ConfigureAwait(false);
            StartBackgroundPrep(ctx.Client, state, play);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count, channel.Name),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (!await TryAcquirePlaySlotAsync().ConfigureAwait(false))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.PlaybackSlotsFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        state.CancelIdleDestroy();
        state.PlayingDefaultPlaylist = false;
        state.Queue.Enqueue(play);
        await PersistEnqueueAsync(clanId, play, "voice").ConfigureAwait(false);
        state.Mode = PlaybackMode.Voice;
        state.Target = target;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlMessageHasButtons = false;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        state.HoldsPlaySlot = true;
        ResetPrepToken(state);
        StartBackgroundPrep(ctx.Client, state, play);
        StartPump(state, clanId);
    }

    private bool IsTooLarge(TrackInfoEntity track)
        => track.SourceBytes is long bytes && bytes > _options.MaxAudioBytes;

    private Task<bool> TryAcquirePlaySlotAsync()
        => _playSlots.WaitAsync(0);

    private void ReleasePlaySlot(ClanPlaybackSession state)
    {
        if (!state.HoldsPlaySlot)
        {
            return;
        }

        state.HoldsPlaySlot = false;
        _playSlots.Release();
    }

    private static void ResetPrepToken(ClanPlaybackSession state)
    {
        state.PrepCts?.Cancel();
        state.PrepCts?.Dispose();
        state.PrepCts = new CancellationTokenSource();
    }

    private void StartBackgroundPrep(MezonClient client, ClanPlaybackSession state, QueuedPlay play)
    {
        var ct = state.PrepCts?.Token ?? CancellationToken.None;
        _prep.StartBackgroundPrep(client, play.Track, ct, ex =>
        {
            if (ex is not AudioTooLargeException)
            {
                return;
            }

            _ = HandlePrepTooLargeAsync(state, play);
        });
    }

    private async Task HandlePrepTooLargeAsync(ClanPlaybackSession state, QueuedPlay play)
    {
        try
        {
            state.Queue.TryRemovePending(item =>
                ReferenceEquals(item.Track, play.Track)
                || (item.Track.Source == play.Track.Source
                    && item.Track.ExternalId == play.Track.ExternalId
                    && item.ReplyMessageId == play.ReplyMessageId));

            if (play.ReplyMessageId is long messageId
                && state.NotifyClient is not null
                && state.NotifyChannelId is long channelId)
            {
                var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
                await channel.UpdateMessageAsync(
                        messageId,
                        PlayerMessageBuilder.CopyrightBlocked(),
                        hideEdited: true,
                        createTimeSeconds: play.ReplyCreateTimeSeconds)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply copyright block for {Title}", play.Track.Title);
        }
    }

    public async Task SkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var outcome = await TrySkipAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task StopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var outcome = await TryStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task PauseAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var outcome = await TrySetPausedAsync(ctx.Client, clanId, ctx.Author.Id, paused: true, cancellationToken)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task ResumeAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var outcome = await TrySetPausedAsync(ctx.Client, clanId, ctx.Author.Id, paused: false, cancellationToken)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task<ControlOutcome> TrySkipAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var state = GetState(clanId);
        var requesterId = state.Queue.CurrentItem?.Track.RequestedByUserId;
        if (!await _access.CanSkipAsync(client, clanId, userId, requesterId, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NotAllowed(
                "Only the person who queued this track, a DJ, or the clan owner can skip."));
        }

        var skipped = await SkipInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(skipped
            ? PlayerMessageBuilder.Ok("Skipped", "On to the next track.")
            : PlayerMessageBuilder.NothingPlaying());
    }

    public async Task<ControlOutcome> TryStopAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanStopAsync(client, clanId, userId, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NotAllowed(
                "Only a DJ or the clan owner can stop playback."));
        }

        await StopInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(PlayerMessageBuilder.Ok("Stopped", "Playback stopped and the queue is clear."));
    }

    public async Task<ControlOutcome> TrySetPausedAsync(
        MezonClient client,
        long clanId,
        long userId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        var state = GetState(clanId);
        if (state.Mode != PlaybackMode.Streaming)
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.Error(
                "Pause is for streams",
                    "Pause/resume works on stream playback. Voice pause isn’t available yet."));
        }

        if (!state.IsPlaying || state.Target is null)
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NothingPlaying());
        }

        var requesterId = state.Queue.CurrentItem?.Track.RequestedByUserId;
        if (!await _access.CanSkipAsync(client, clanId, userId, requesterId, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NotAllowed(
                "Only the person who queued this track, a DJ, or the clan owner can pause/resume."));
        }

        if (_streamingSink.IsPaused(state.Target.ChannelId) == paused)
        {
            return ControlOutcome.Ok(PlayerMessageBuilder.Status(
                paused ? "Already paused" : "Already playing",
                paused ? "It’s already on pause." : "It’s already playing."));
        }

        try
        {
            await _streamingSink.SetPausedAsync(state.Target, paused, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STN pause failed clan={ClanId} paused={Paused}", clanId, paused);
            return ControlOutcome.Denied(PlayerMessageBuilder.Error(
                "Couldn’t pause/resume",
                "Something went wrong changing playback — try again."));
        }

        return ControlOutcome.Ok(PlayerMessageBuilder.Ok(
            paused ? "Paused" : "Resumed",
            paused ? "Stream paused. Use !resume when you’re ready." : "Stream is playing again."));
    }

    public readonly record struct ControlOutcome(bool Allowed, Mezon.Net.Client.MessageContent Content)
    {
        public static ControlOutcome Ok(Mezon.Net.Client.MessageContent content) => new(true, content);
        public static ControlOutcome Denied(Mezon.Net.Client.MessageContent content) => new(false, content);
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
        state.PlayingDefaultPlaylist = false;
        state.DefaultAutoplayArmed = false;
        state.LastDestroyReason = PlayerDestroyReason.UserStop;
        state.PrepCts?.Cancel();
        await _playerStore.SetLoopModeAsync(clanId, LoopMode.Off, cancellationToken).ConfigureAwait(false);
        await ClearPersistedSessionAsync(clanId, cancellationToken).ConfigureAwait(false);
        // Cancel wakes WaitForTrackEnd; voice also stops immediately so STN/ffmpeg do not keep pushing.
        state.CancelTrack();
        await StopActiveVoiceSinkAsync(state, cancellationToken).ConfigureAwait(false);
        state.IsPlaying = false;
        ScheduleIdleDestroy(clanId, state);
    }

    private async Task<bool> SkipStateAsync(ClanPlaybackSession state, CancellationToken cancellationToken)
    {
        // CancelTrack wakes the active pump (if any); it owns Stop + advance.
        // Only start a pump when nothing is driving the queue (idle with pending items).
        state.LastDestroyReason = PlayerDestroyReason.Skip;
        var needsPump = !state.IsPlaying;
        state.CancelTrack();
        await StopActiveVoiceSinkAsync(state, cancellationToken).ConfigureAwait(false);
        if (needsPump)
        {
            StartPump(state, state.ClanId ?? 0);
        }

        return true;
    }

    private async Task StopActiveVoiceSinkAsync(ClanPlaybackSession state, CancellationToken cancellationToken)
    {
        if (state.Mode != PlaybackMode.Voice || state.Target is not { } target)
        {
            return;
        }

        try
        {
            await _voiceSink.StopAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Eager voice stop failed channel={ChannelId}", target.ChannelId);
        }
    }

    public Task ShowQueueAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        return ctx.ReplyAsync(PlayerMessageBuilder.QueueList(state.Queue.CurrentItem, state.Queue.Snapshot()));
    }

    public async Task ShowNowPlayingAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        if (state.Queue.Current is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NothingPlaying())
                .ConfigureAwait(false);
            return;
        }

        await _viz.EnsureAsync(ctx.Client, ctx.CancellationToken).ConfigureAwait(false);

        state.ControlUserId = ctx.Author.Id;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        // Seed without buttons so ControlMessageId = reply id, then attach Skip/Stop.
        var seed = BuildNowPlayingContent(state, clanId, includeMusicViz: true, includeControls: false);
        var reply = await ctx.ReplyAsync(seed).ConfigureAwait(false);
        state.ControlMessageId = reply.MessageId;
        state.ControlMessageCreateTimeSeconds = reply.CreateTimeSeconds > 0 ? reply.CreateTimeSeconds : null;
        state.ControlMessageHasButtons = true;
        var content = BuildNowPlayingContent(state, clanId, includeMusicViz: true, includeControls: true);
        await ctx.Channel.UpdateMessageAsync(
                reply.MessageId,
                content,
                hideEdited: true,
                createTimeSeconds: state.ControlMessageCreateTimeSeconds)
            .ConfigureAwait(false);
    }

    public async Task SetDjRoleAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanConfigureDjAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only the clan owner can configure the DJ role."))
                .ConfigureAwait(false);
            return;
        }

        var raw = string.Join(' ', ctx.Args).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Missing role",
                    $"Try {_options.CommandPrefix}setdj @role  ·  role id  ·  or none"))
                .ConfigureAwait(false);
            return;
        }

        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("off", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            await _access.SetDjRoleIdAsync(clanId, null, cancellationToken).ConfigureAwait(false);
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                    "DJ role cleared",
                    "Force skip/stop now need the clan owner."))
                .ConfigureAwait(false);
            return;
        }

        long? roleId = null;
        string? roleTitle = null;
        foreach (var mention in ctx.Message.Mentions)
        {
            if (mention.RoleId != 0)
            {
                roleId = mention.RoleId;
                roleTitle = string.IsNullOrWhiteSpace(mention.Rolename) ? null : mention.Rolename;
                break;
            }
        }

        if (roleId is null && long.TryParse(raw.TrimStart('@'), out var parsed) && parsed != 0)
        {
            roleId = parsed;
        }

        if (roleId is null)
        {
            try
            {
                var clan = await ctx.Client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
                var roles = await clan.ListRolesAsync(limit: 100).ConfigureAwait(false);
                var needle = raw.TrimStart('@');
                foreach (var role in roles.Roles.Roles)
                {
                    if (role.Title.Equals(needle, StringComparison.OrdinalIgnoreCase)
                        || role.Slug.Equals(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        roleId = role.Id;
                        roleTitle = role.Title;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve DJ role by name clan={ClanId}", clanId);
            }
        }

        if (roleId is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Role not found",
                    "Pass a role mention, numeric id, or exact name. Use none to clear."))
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(roleTitle))
        {
            try
            {
                var clan = await ctx.Client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
                var roles = await clan.ListRolesAsync(limit: 100).ConfigureAwait(false);
                foreach (var role in roles.Roles.Roles)
                {
                    if (role.Id == roleId.Value)
                    {
                        roleTitle = role.Title;
                        break;
                    }
                }
            }
            catch
            {
                // title is optional for confirmation
            }
        }

        await _access.SetDjRoleIdAsync(clanId, roleId, cancellationToken).ConfigureAwait(false);
        _ = _access.WarmDjRoleMembershipAsync(ctx.Client, clanId, roleId.Value, CancellationToken.None);
        var label = string.IsNullOrWhiteSpace(roleTitle) ? $"{roleId}" : $"{roleTitle} ({roleId})";
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "DJ role set",
                $"Members with {label} can force-skip and stop."))
            .ConfigureAwait(false);
    }

    public async Task ShowSettingsAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var djRoleId = await _access.GetDjRoleIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        var djValue = djRoleId is long id ? $"{id}" : "none (owner handles force skip/stop)";
        var loop = await _playerStore.GetLoopModeAsync(clanId, cancellationToken).ConfigureAwait(false);
        var channels = await _commandChannels.ListAsync(clanId, cancellationToken).ConfigureAwait(false);
        var channelText = channels.Count == 0
            ? "all channels"
            : string.Join(", ", await FormatChannelMentionsAsync(ctx.Client, channels, cancellationToken)
                .ConfigureAwait(false));
        var defaultPl = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
        var defaultText = defaultPl is null ? "none" : defaultPl.Name;
        await ctx.ReplyAsync(PlayerMessageBuilder.ClanSettings(
                djValue,
                loop.ToString().ToLowerInvariant(),
                defaultText,
                channelText))
            .ConfigureAwait(false);
    }

    public async Task<int> ArmDefaultAutoplayOnStartupAsync(
        MezonClient client,
        IReadOnlySet<long>? excludedClanIds = null,
        CancellationToken cancellationToken = default)
    {
        var clanIds = await _playlists.ListDefaultClanIdsAsync(cancellationToken).ConfigureAwait(false);
        var armed = 0;
        foreach (var clanId in clanIds)
        {
            if (excludedClanIds?.Contains(clanId) == true)
            {
                continue;
            }

            if (await ArmDefaultAutoplayForClanAsync(clanId, client, cancellationToken).ConfigureAwait(false))
            {
                armed++;
            }
        }

        return armed;
    }

    public async Task<bool> ArmDefaultAutoplayForClanAsync(
        long clanId,
        MezonClient client,
        CancellationToken cancellationToken = default)
    {
        var defaultPl = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (defaultPl is null)
        {
            return false;
        }

        if (await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken).ConfigureAwait(false) is not long)
        {
            _logger.LogDebug("Default autoplay not armed: no default stream channel clan={ClanId}", clanId);
            return false;
        }

        var state = GetState(clanId);
        state.ClanId = clanId;
        state.NotifyClient ??= client;
        state.DefaultAutoplayArmed = true;
        state.PlayingDefaultPlaylist = false;

        if (!state.IsPlaying && state.Queue.TotalCount == 0)
        {
            ScheduleIdleDestroy(clanId, state);
        }

        return true;
    }

    public async Task<IReadOnlySet<long>> RestoreSessionsOnStartupAsync(
        MezonClient client,
        CancellationToken cancellationToken = default)
    {
        var restored = new HashSet<long>();
        var clanIds = await _playerStore.ListActiveClanIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var clanId in clanIds)
        {
            if (await TryRestoreClanSessionAsync(clanId, client, cancellationToken).ConfigureAwait(false))
            {
                restored.Add(clanId);
            }
        }

        return restored;
    }

    private static Mezon.Net.Client.MessageContent BuildNowPlayingContent(
        ClanPlaybackSession state,
        long clanId,
        bool includeMusicViz = false,
        bool includeControls = false)
    {
        var item = state.Queue.CurrentItem!;
        var mode = state.Mode == PlaybackMode.Voice ? "voice" : "streaming";
        var destination = PlayerMessageBuilder.FormatDestination(mode, item.Target.ChannelLabel);
        return PlayerMessageBuilder.NowPlaying(
            item.Track,
            state.Queue.Count,
            destination,
            nextTitle: state.Queue.PeekNext()?.Track.Title,
            controlMessageId: includeControls ? state.ControlMessageId : null,
            controlUserId: includeControls ? state.ControlUserId : null,
            clanId: clanId,
            includeMusicViz: true);
    }

    private async Task<(TrackInfoEntity? Track, Mezon.Net.Client.MessageContent? Error)> TryResolveAsync(
        ICommandContext ctx,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, PlayerMessageBuilder.PlayUsage(
                string.IsNullOrWhiteSpace(_options.CommandPrefix) ? "!" : _options.CommandPrefix));
        }

        try
        {
            var track = await _resolver.ResolveAsync(
                query.Trim(),
                ctx.Author.Username ?? ctx.Author.Id.ToString(),
                cancellationToken).ConfigureAwait(false);
            if (track is null)
            {
                return (null, PlayerMessageBuilder.TrackNotFound());
            }

            return (track.WithRequester(ctx.Author.Id, ctx.Author.Username ?? ctx.Author.Id.ToString()), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolve failed for {Query}", query);
            return (null, PlayerMessageBuilder.Awkward());
        }
    }

    private async Task EnqueueSoundCloudSetAsync(
        ICommandContext ctx,
        string query,
        PlaybackTarget target,
        PlaybackMode mode,
        long preparingMessageId,
        uint? preparingCreateTime,
        CancellationToken cancellationToken)
    {
        var clanId = target.ClanId;
        var state = GetState(clanId);
        if (state.IsPlaying && state.Mode != mode)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.ModeConflict(wantVoice: mode == PlaybackMode.Voice),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var roomLeft = _options.MaxQueuePerClan - state.Queue.TotalCount;
        if (roomLeft <= 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.QueueFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        // Acquire before enqueue so a full slot pool never leaves orphaned Redis/memory items.
        var startingFresh = !state.IsPlaying;
        if (startingFresh && !await TryAcquirePlaySlotAsync().ConfigureAwait(false))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.PlaybackSlotsFull(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<TrackInfoEntity> imported;
        try
        {
            imported = await _soundCloudSets.ImportAsync(
                    query,
                    ctx.Author.Username ?? ctx.Author.Id.ToString(),
                    roomLeft,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (startingFresh)
            {
                _playSlots.Release();
            }

            _logger.LogError(ex, "SoundCloud set import failed");
            await UpdateOrReplyAsync(ctx, preparingMessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (imported.Count == 0)
        {
            if (startingFresh)
            {
                _playSlots.Release();
            }

            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("That SoundCloud set looked empty or had nothing I can play."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var modeKey = mode == PlaybackMode.Voice ? "voice" : "streaming";
        var requester = ctx.Author.Username ?? ctx.Author.Id.ToString();
        var interruptDefault = state.IsPlaying
            && mode == PlaybackMode.Streaming
            && state.Queue.CurrentItem?.IsFromDefault == true;

        var added = 0;
        for (var i = 0; i < imported.Count; i++)
        {
            if (state.Queue.TotalCount >= _options.MaxQueuePerClan)
            {
                break;
            }

            var info = imported[i].WithRequester(ctx.Author.Id, requester);
            if (IsTooLarge(info) || info.IsTooLarge)
            {
                continue;
            }

            // Do not attach preparingMessageId to queue items — avoids racing NP vs "set queued".
            var play = new QueuedPlay(info, target);
            if (interruptDefault && added == 0)
            {
                state.Queue.EnqueueFront(play);
            }
            else
            {
                state.Queue.Enqueue(play);
            }

            await PersistEnqueueAsync(clanId, play, modeKey).ConfigureAwait(false);
            StartBackgroundPrep(ctx.Client, state, play);
            added++;
        }

        if (added == 0)
        {
            if (startingFresh)
            {
                _playSlots.Release();
            }

            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("Every track in that set was too large or blocked."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        state.PlayingDefaultPlaylist = false;
        state.Mode = mode;
        state.Target = target;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        state.ClanId = clanId;
        state.ControlUserId = ctx.Author.Id;

        await UpdateOrReplyAsync(
                ctx,
                preparingMessageId,
                PlayerMessageBuilder.SoundCloudSetQueued(added, _options.MaxQueuePerClan, interruptDefault),
                preparingCreateTime)
            .ConfigureAwait(false);

        if (interruptDefault)
        {
            state.LastDestroyReason = PlayerDestroyReason.Skip;
            state.CancelTrack();
            return;
        }

        if (startingFresh)
        {
            state.CancelIdleDestroy();
            state.HoldsPlaySlot = true;
            state.ControlMessageId = preparingMessageId;
            state.ControlMessageCreateTimeSeconds = preparingCreateTime;
            state.ControlMessageHasButtons = false;
            ResetPrepToken(state);
            StartPump(state, clanId);
        }
    }

    private async Task<string> FormatChannelMentionAsync(
        MezonClient client,
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var channel = await client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
            return PlayerMessageBuilder.FormatChannelMention(channel.Name, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolve channel label failed channel={ChannelId}", channelId);
            return PlayerMessageBuilder.FormatChannelMention(null, channelId);
        }
    }

    private async Task<IReadOnlyList<string>> FormatChannelMentionsAsync(
        MezonClient client,
        IReadOnlyList<long> channelIds,
        CancellationToken cancellationToken)
    {
        var labels = new string[channelIds.Count];
        for (var i = 0; i < channelIds.Count; i++)
        {
            labels[i] = await FormatChannelMentionAsync(client, channelIds[i], cancellationToken)
                .ConfigureAwait(false);
        }

        return labels;
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

    /// <summary>
    /// Starts the clan pump with a lifetime independent of the command CancellationToken.
    /// Command CTs often cancel when the handler returns; pumps must outlive that.
    /// </summary>
    private void StartPump(ClanPlaybackSession state, long clanId)
        => _ = PumpAsync(state, clanId, CancellationToken.None);

    private async Task PumpAsync(ClanPlaybackSession state, long clanId, CancellationToken cancellationToken)
    {
        if (!await state.TryEnterPumpAsync().ConfigureAwait(false))
        {
            ReleasePlaySlot(state);
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = state.Queue.TryDequeueNext();
                var mode = state.Mode;
                if (item is null)
                {
                    if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                    {
                        state.IsPlaying = false;
                        ScheduleIdleDestroy(clanId, state);
                        return;
                    }

                    if (state.PlayingDefaultPlaylist)
                    {
                        if (await TryEnqueueNextDefaultTrackAsync(state, clanId, CancellationToken.None)
                                .ConfigureAwait(false))
                        {
                            continue;
                        }

                        state.PlayingDefaultPlaylist = false;
                    }

                    // Keep the STN publisher WS warm across an empty queue so a re-queue
                    // within IdleSessionTtl reuses the session (no ws_close / channel_closed).
                    // Teardown or default resume happens in ScheduleIdleDestroy after the idle TTL.
                    state.IsPlaying = false;
                    state.LastDestroyReason = PlayerDestroyReason.QueueEmpty;
                    ScheduleIdleDestroy(clanId, state);
                    return;
                }

                var track = item.Track;
                var target = item.Target;
                state.Target = target;
                state.CancelIdleDestroy();
                state.IsPlaying = true;
                using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                state.SetTrackCts(trackCts);
                var playStopwatch = Stopwatch.StartNew();

                try
                {
                    var sink = mode == PlaybackMode.Voice ? (IPlaybackSink)_voiceSink : _streamingSink;
                    _logger.LogDebug(
                        "Playback pipeline start mode={Mode} title={Title} channel={ChannelId} queuedNext={QueuedNext}",
                        mode,
                        track.Title,
                        target.ChannelId,
                        state.Queue.Count);
                    await sink.PlayAsync(target, track, trackCts.Token).ConfigureAwait(false);
                    _logger.LogDebug(
                        "Playback sink ready mode={Mode} title={Title} channel={ChannelId} elapsedMs={ElapsedMs}",
                        mode,
                        track.Title,
                        target.ChannelId,
                        playStopwatch.ElapsedMilliseconds);
                    if (item.ReplyMessageId is long replyId)
                    {
                        state.ControlMessageId = replyId;
                        state.ControlMessageCreateTimeSeconds = item.ReplyCreateTimeSeconds;
                        state.ControlMessageHasButtons = false;
                    }

                    await SendNowPlayingAsync(state, includeMusicViz: true).ConfigureAwait(false);

                    var modeKey = mode == PlaybackMode.Voice ? "voice" : "streaming";
                    state.PlayHistoryId = await BeginHistoryAsync(clanId, item, modeKey, trackCts.Token)
                        .ConfigureAwait(false);

                    try
                    {
                        await WaitForTrackEndAsync(state, track, mode, target, trackCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                    {
                    }

                    try
                    {
                        if (mode == PlaybackMode.Streaming)
                        {
                            // Keep the WS + listeners across tracks; only !stop tears the session down.
                            if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                            {
                                await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                            }
                            else
                            {
                                await _streamingSink.EndTrackAsync(target, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await sink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stop sink failed channel={ChannelId}", target.ChannelId);
                    }
                }
                catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                {
                    // !skip / !stop cancelled prepare/play. The command already replied �
                    // do NOT treat this as playback failure (that was sending a second bot message).
                    try
                    {
                        if (mode == PlaybackMode.Streaming)
                        {
                            if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                            {
                                await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                            }
                            else
                            {
                                await _streamingSink.EndTrackAsync(target, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await _voiceSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Teardown after skip/stop cancel ignored channel={ChannelId}", target.ChannelId);
                    }
                }
                catch (AudioTooLargeException)
                {
                    await NotifyCopyrightBlockedAsync(state, item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    state.LastDestroyReason = PlayerDestroyReason.StnFailed;
                    _logger.LogError(ex, "Playback failed for {Title} channel={ChannelId}", track.Title, target.ChannelId);
                    await NotifyPlaybackFailureAsync(state, track, ex).ConfigureAwait(false);
                    try
                    {
                        var sink = mode == PlaybackMode.Voice ? (IPlaybackSink)_voiceSink : _streamingSink;
                        await sink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception stopEx)
                    {
                        _logger.LogDebug(stopEx, "Stop after failure ignored channel={ChannelId}", target.ChannelId);
                    }

                    if (mode == PlaybackMode.Streaming && IsStnInfrastructureFailure(ex))
                    {
                        state.Queue.Clear(clearCurrent: true);
                        break;
                    }
                }
                finally
                {
                    state.ClearTrackCts();
                    var historyId = state.PlayHistoryId;
                    state.PlayHistoryId = null;
                    var endReason = state.LastDestroyReason switch
                    {
                        PlayerDestroyReason.Skip => PlayEndReason.Skip,
                        PlayerDestroyReason.UserStop => PlayEndReason.Stop,
                        PlayerDestroyReason.StnFailed => PlayEndReason.Error,
                        _ => PlayEndReason.Completed,
                    };
                    if (historyId is long hid)
                    {
                        var skipLoop = endReason is PlayEndReason.Skip or PlayEndReason.VoteSkip
                            or PlayEndReason.Stop or PlayEndReason.Error or PlayEndReason.TooLarge;
                        var advanced = await TryAdvancePersistedAsync(
                                clanId,
                                hid,
                                skipLoop,
                                endReason,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (advanced
                            && endReason == PlayEndReason.Completed
                            && !skipLoop
                            && !item.IsFromDefault)
                        {
                            var loop = await _playerStore.GetLoopModeAsync(clanId).ConfigureAwait(false);
                            if (loop == LoopMode.Track)
                            {
                                state.Queue.EnqueueFront(item);
                            }
                            else if (loop == LoopMode.Queue)
                            {
                                state.Queue.Enqueue(item);
                            }
                        }
                    }

                    if (state.LastDestroyReason != PlayerDestroyReason.UserStop)
                    {
                        state.LastDestroyReason = PlayerDestroyReason.None;
                    }
                }

                try
                {
                    await Task.Delay(_options.InterTrackDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            state.IsPlaying = false;
            state.ExitPump();
            ReleasePlaySlot(state);
            if (state.Queue.Count == 0 && state.Queue.Current is null)
            {
                ScheduleIdleDestroy(clanId, state);
            }
        }
    }

    private async Task NotifyCopyrightBlockedAsync(ClanPlaybackSession state, QueuedPlay item)
    {
        if (item.ReplyMessageId is long messageId
            && state.NotifyClient is not null
            && state.NotifyChannelId is long channelId)
        {
            try
            {
                var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
                await channel.UpdateMessageAsync(
                        messageId,
                        PlayerMessageBuilder.CopyrightBlocked(),
                        hideEdited: true,
                        createTimeSeconds: item.ReplyCreateTimeSeconds)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update copyright message for {Title}", item.Track.Title);
            }
        }

        await NotifyPlaybackFailureAsync(state, item.Track, new AudioTooLargeException(
                item.Track.Title,
                item.Track.SourceBytes ?? 0,
                _options.MaxAudioBytes))
            .ConfigureAwait(false);
    }

    private void ScheduleIdleDestroy(long clanId, ClanPlaybackSession state)
    {
        state.ScheduleIdleDestroy(
            IdleSessionTtl,
            () =>
            {
                if (state.IsPlaying || state.Queue.Count > 0 || state.Queue.Current is not null)
                {
                    return;
                }

                if (state.LastDestroyReason == PlayerDestroyReason.UserStop || !state.DefaultAutoplayArmed)
                {
                    TearDownIdleSession(clanId, state);
                    return;
                }

                _ = TryResumeDefaultAfterIdleAsync(clanId, state);
            });
    }

    private void TearDownIdleSession(long clanId, ClanPlaybackSession state)
    {
        if (!_states.TryGetValue(clanId, out var current) || !ReferenceEquals(current, state)
            || !_states.TryRemove(clanId, out _))
        {
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.IdleTimeout;
        var target = state.Target;
        var mode = state.Mode;
        state.Target = null;
        state.PlayingDefaultPlaylist = false;
        _logger.LogDebug(
            "Idle player session destroyed clan={ClanId} reason={Reason}",
            clanId,
            state.LastDestroyReason);

        if (mode == PlaybackMode.Streaming && target is { })
        {
            _ = TearDownStreamingIdleAsync(target);
        }
    }

    private async Task TryResumeDefaultAfterIdleAsync(long clanId, ClanPlaybackSession state)
    {
        try
        {
            if (state.IsPlaying || state.Queue.Count > 0 || state.Queue.Current is not null)
            {
                return;
            }

            if (state.LastDestroyReason == PlayerDestroyReason.UserStop || !state.DefaultAutoplayArmed)
            {
                TearDownIdleSession(clanId, state);
                return;
            }

            var started = await TryStartDefaultPlaylistAsync(state, clanId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!started)
            {
                TearDownIdleSession(clanId, state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Default playlist idle resume failed clan={ClanId}", clanId);
            TearDownIdleSession(clanId, state);
        }
    }

    /// <summary>
    /// Arms and starts default playlist autoplay on the clan default stream channel when idle.
    /// </summary>
    private async Task<bool> TryStartDefaultPlaylistAsync(
        ClanPlaybackSession state,
        long clanId,
        CancellationToken cancellationToken)
    {
        if (state.IsPlaying || state.Queue.TotalCount > 0)
        {
            return false;
        }

        state.PlayingDefaultPlaylist = true;
        state.DefaultAutoplayArmed = true;
        state.Mode = PlaybackMode.Streaming;
        state.ClanId = clanId;
        state.LastDestroyReason = PlayerDestroyReason.None;

        if (!state.HoldsPlaySlot && !await TryAcquirePlaySlotAsync().ConfigureAwait(false))
        {
            state.PlayingDefaultPlaylist = false;
            return false;
        }

        state.HoldsPlaySlot = true;
        state.CancelIdleDestroy();
        ResetPrepToken(state);

        if (!await TryEnqueueNextDefaultTrackAsync(state, clanId, cancellationToken).ConfigureAwait(false))
        {
            ReleasePlaySlot(state);
            state.PlayingDefaultPlaylist = false;
            return false;
        }

        StartPump(state, clanId);
        return true;
    }

    private async Task<bool> TryEnqueueNextDefaultTrackAsync(
        ClanPlaybackSession state,
        long clanId,
        CancellationToken cancellationToken)
    {
        var playlist = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (playlist is null)
        {
            return false;
        }

        var streamChannelId = await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken)
            .ConfigureAwait(false);
        if (streamChannelId is not long channelId)
        {
            _logger.LogDebug("Default playlist skipped: no default_stream_channel_id clan={ClanId}", clanId);
            return false;
        }

        var items = await _playlists.ListItemsAsync(playlist.Id, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return false;
        }

        string? channelLabel = null;
        if (state.NotifyClient is not null)
        {
            try
            {
                var channel = await state.NotifyClient.GetChannelAsync(channelId, cancellationToken)
                    .ConfigureAwait(false);
                channelLabel = channel.Name;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetChannelAsync failed for default stream {ChannelId}", channelId);
            }
        }

        var target = new PlaybackTarget(clanId, channelId, ChannelLabel: channelLabel);
        state.Target = target;
        state.Mode = PlaybackMode.Streaming;

        // Walk playlist once looking for a playable track from the cursor.
        for (var attempt = 0; attempt < items.Count; attempt++)
        {
            var index = state.DefaultPlaylistCursor % items.Count;
            if (state.DefaultPlaylistCursor < 0)
            {
                index = 0;
            }

            state.DefaultPlaylistCursor = index + 1;
            var entry = items[index];
            if (entry.Track is null || entry.Track.IsTooLarge)
            {
                continue;
            }

            if (entry.Track.SourceBytes is long bytes && bytes > _options.MaxAudioBytes)
            {
                continue;
            }

            var info = entry.Track.ToTrackInfo("Auto");
            var play = new QueuedPlay(info, target, IsFromDefault: true);
            state.Queue.Enqueue(play);
            await PersistEnqueueAsync(clanId, play, "streaming").ConfigureAwait(false);
            if (state.NotifyClient is not null)
            {
                StartBackgroundPrep(state.NotifyClient, state, play);
            }

            return true;
        }

        return false;
    }

    private async Task<bool> TryRestoreClanSessionAsync(
        long clanId,
        MezonClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _playerStore.GetCurrentAsync(clanId, cancellationToken).ConfigureAwait(false);
            var pending = await _playerStore.SnapshotQueueAsync(clanId, cancellationToken).ConfigureAwait(false);
            if (current is null && pending.Count == 0)
            {
                return false;
            }

            var state = GetState(clanId);
            state.CancelIdleDestroy();
            state.Queue.Clear();
            state.ClanId = clanId;
            state.NotifyClient = client;
            state.ControlMessageId = null;
            state.ControlMessageCreateTimeSeconds = null;
            state.ControlMessageHasButtons = false;
            state.ControlUserId = null;
            state.PlayHistoryId = null;
            state.PlayingDefaultPlaylist = false;
            state.DefaultAutoplayArmed = false;
            state.DefaultPlaylistCursor = 0;
            state.LastDestroyReason = PlayerDestroyReason.None;

            QueuedPlay? first = null;
            if (current is not null)
            {
                first = ToQueuedPlay(clanId, current);
                state.Mode = ParsePlaybackMode(current.Mode);
                state.Target = first.Target;
                state.Queue.Enqueue(first);
            }

            foreach (var item in pending)
            {
                state.Queue.Enqueue(ToQueuedPlay(clanId, item));
            }

            if (current is null && pending.Count > 0)
            {
                state.Mode = ParsePlaybackMode(pending[0].Mode);
                state.Target = pending[0].ToTarget(clanId);
            }

            await _history.CloseOpenForClanAsync(clanId, PlayEndReason.Restart, cancellationToken).ConfigureAwait(false);
            await _playerStore.SetPlayHistoryIdAsync(clanId, null, cancellationToken).ConfigureAwait(false);
            await _playerStore.SetPositionAsync(clanId, 0, 0, paused: false, cancellationToken).ConfigureAwait(false);

            if (!state.HoldsPlaySlot && !await TryAcquirePlaySlotAsync().ConfigureAwait(false))
            {
                _logger.LogWarning("Restore skipped for clan={ClanId}: playback slots full", clanId);
                state.Queue.Clear();
                return false;
            }

            state.HoldsPlaySlot = true;
            ResetPrepToken(state);
            if (state.NotifyClient is not null)
            {
                var snapshot = state.Queue.Snapshot();
                for (var i = 0; i < snapshot.Count; i++)
                {
                    StartBackgroundPrep(state.NotifyClient, state, snapshot[i]);
                }
            }

            _logger.LogInformation(
                "Restored playback session clan={ClanId} current={HasCurrent} pending={PendingCount} mode={Mode}",
                clanId,
                current is not null,
                pending.Count,
                state.Mode);
            StartPump(state, clanId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore playback session clan={ClanId}", clanId);
            try
            {
                await _history.CloseOpenForClanAsync(clanId, PlayEndReason.Restart, cancellationToken).ConfigureAwait(false);
                await _playerStore.ClearSessionAsync(clanId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(clearEx, "Failed to clear broken restored session clan={ClanId}", clanId);
            }

            return false;
        }
    }

    private static QueuedPlay ToQueuedPlay(long clanId, QueuedTrackPayload payload)
        => new(
            payload.ToTrackInfo(),
            payload.ToTarget(clanId),
            payload.ReplyMessageId,
            payload.ReplyCreateTimeSecs,
            payload.IsFromDefault);

    private static PlaybackMode ParsePlaybackMode(string? mode)
        => string.Equals(mode, "voice", StringComparison.OrdinalIgnoreCase)
            ? PlaybackMode.Voice
            : PlaybackMode.Streaming;

    private async Task TearDownStreamingIdleAsync(PlaybackTarget target)
    {
        try
        {
            await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Streaming session stop on idle timeout failed channel={ChannelId}",
                target.ChannelId);
        }
    }

    /// <summary>How long to keep clan player state + streaming publisher WS after the queue empties.</summary>
    private static readonly TimeSpan IdleSessionTtl = TimeSpan.FromMinutes(5);

    private async Task SendNowPlayingAsync(ClanPlaybackSession state, bool includeMusicViz)
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
            var content = BuildNowPlayingContent(
                state,
                clanId,
                includeMusicViz,
                includeControls: state.ControlMessageHasButtons);
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
        ClanPlaybackSession state,
        TrackInfoEntity track,
        PlaybackMode mode,
        PlaybackTarget target,
        CancellationToken cancellationToken)
    {
        if (mode != PlaybackMode.Voice)
        {
            // STN stream_track_ended is authoritative; duration is only used for up-next UX.
            using var endedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var endedWait = _streamingSink.WaitUntilTrackEndedAsync(target.ChannelId, endedCts.Token);
            var upNextTask = track.Duration is { } d && d > UpNextLead
                ? NotifyStreamingUpNextAsync(state, d, endedCts.Token)
                : Task.CompletedTask;

            await endedWait.ConfigureAwait(false);
            endedCts.Cancel();
            try
            {
                await upNextTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _logger.LogDebug(
                "Streaming track ended by STN signal title={Title} channel={ChannelId}",
                track.Title,
                target.ChannelId);
            return;
        }

        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var durationWait = track.Duration is { } voiceDuration && voiceDuration > TimeSpan.Zero
            ? WaitByDurationAsync(state, track, voiceDuration, durationCts.Token)
            : Task.Delay(TimeSpan.FromMinutes(10), durationCts.Token);

        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName!;
        using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var statusWait = _voiceSink.WaitUntilTerminalAsync(roomName, statusCts.Token);
        var winner = await Task.WhenAny(durationWait, statusWait).ConfigureAwait(false);

        // !skip / !stop: surface cancel so the pump teardown path runs consistently.
        cancellationToken.ThrowIfCancellationRequested();

        if (winner == statusWait)
        {
            durationCts.Cancel();
            var terminal = await statusWait.ConfigureAwait(false);
            if (terminal is "failed")
            {
                state.LastDestroyReason = PlayerDestroyReason.StnFailed;
            }

            return;
        }

        statusCts.Cancel();
        await durationWait.ConfigureAwait(false);
    }

    private async Task NotifyStreamingUpNextAsync(
        ClanPlaybackSession state,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var untilNotify = duration - UpNextLead;
        if (untilNotify > TimeSpan.Zero)
        {
            await Task.Delay(untilNotify, cancellationToken).ConfigureAwait(false);
        }

        var next = state.Queue.PeekNext();
        if (next is null)
        {
            return;
        }

        await NotifyUpNextAsync(state, next, (int)Math.Ceiling(UpNextLead.TotalSeconds)).ConfigureAwait(false);
    }

    private async Task WaitByDurationAsync(
        ClanPlaybackSession state,
        TrackInfoEntity track,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
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

    private async Task NotifyUpNextAsync(ClanPlaybackSession state, QueuedPlay next, int secondsRemaining)
    {
        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            await channel.SendAsync(PlayerMessageBuilder.UpNext(next.Track, secondsRemaining, next.Target.ChannelLabel))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify up-next for {Title}", next.Track.Title);
        }
    }

    private async Task NotifyPlaybackFailureAsync(ClanPlaybackSession state, TrackInfoEntity track, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            var content = ex is AudioTooLargeException
                ? PlayerMessageBuilder.CopyrightBlocked()
                : PlayerMessageBuilder.FromStnFailure(ex) ?? PlayerMessageBuilder.Awkward();
            await channel.SendAsync(content).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(notifyEx, "Failed to notify playback error for {Title}", track.Title);
        }
    }

    private static bool IsStnInfrastructureFailure(Exception ex)
    {
        if (ex is Stn.StnVoiceException)
        {
            return true;
        }

        var msg = ex.Message;
        return msg.Contains("502", StringComparison.Ordinal)
               || msg.Contains("status code '200'", StringComparison.Ordinal)
               || msg.Contains("STN streaming WebSocket", StringComparison.Ordinal);
    }

    private ClanPlaybackSession GetState(long clanId)
        => _states.GetOrAdd(clanId, id => new ClanPlaybackSession { ClanId = id });
}

