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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    private readonly ITrackResolver _resolver;
    private readonly StreamingChannelSink _streamingSink;
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
    private readonly PlayEnqueueService _enqueue;
    private readonly IHostApplicationLifetime _lifetime;

    public MusicPlayer(
        ITrackResolver resolver,
        StreamingChannelSink streamingSink,
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
        PlayEnqueueService enqueue,
        IHostApplicationLifetime lifetime,
        ILogger<MusicPlayer> logger)
    {
        _resolver = resolver;
        _streamingSink = streamingSink;
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
        _enqueue = enqueue;
        _lifetime = lifetime;
        _logger = logger;
        _playSlots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPlayback));
        _lifetime.ApplicationStopping.Register(CancelAllSessions);
    }

    /// <summary>
    /// Auto-mode for <c>!play</c>: hashtag / current channel / clan default stream channel.
    /// Voice channels are not a STN publish target anymore.
    /// </summary>
    public async Task PlayAutoAsync(
        ICommandContext ctx,
        string query,
        long? hashtagChannelId,
        bool wantVideo = false,
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
        if (resolved.Mode != PlaybackMode.Streaming)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Streaming only",
                    "STN no longer publishes into voice channels. Tag a #stream channel."))
                .ConfigureAwait(false);
            return;
        }

        await PlayStreamingAsync(ctx, query, resolved.Target.ChannelId, wantVideo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Same destination rules as <see cref="PlayAutoAsync"/>: hashtag → current channel → default stream.
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
                return (null, PlayerMessageBuilder.Error(
                    "Streaming only",
                    "STN no longer publishes into voice channels. Tag a #stream channel."));
            }

            return (null, PlayerMessageBuilder.Error(
                "Pick a stream channel",
                "Mention a #stream channel hashtag."));
        }

        if (ctx.Channel.Type == (int)ChannelType.Streaming)
        {
            return (ToStreaming(clanId, ctx.Channel.Id, ctx.Channel.Name), null);
        }

        if (ctx.Channel.Type is (int)ChannelType.MezonVoice or (int)ChannelType.GmeetVoice)
        {
            return (null, PlayerMessageBuilder.Error(
                "Streaming only",
                "STN no longer publishes into voice channels. Tag a #stream channel, or set a default stream channel."));
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

        return (null, PlayerMessageBuilder.Error(
            "Where should I play?",
            "Tag a #stream channel, or set a default stream channel for this clan."));

        static ResolvedPlayDestination ToStreaming(long clan, long channelId, string? label)
            => new(new PlaybackTarget(clan, channelId, ChannelLabel: label), PlaybackMode.Streaming);
    }

    private readonly record struct ResolvedPlayDestination(PlaybackTarget Target, PlaybackMode Mode);

    public async Task PlayStreamingAsync(
        ICommandContext ctx,
        string query,
        long? streamChannelId,
        bool wantVideo = false,
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
                    wantVideo,
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

        if (IsTooLarge(track, wantVideo))
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.CopyrightBlocked(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var play = new QueuedPlay(track, target, preparing.MessageId, preparingCreateTime, WantVideo: wantVideo);
        var kind = await EnqueueOrStartAsync(
                GetState(clanId),
                play,
                PlaybackMode.Streaming,
                ctx.Client,
                ctx.Channel.Id,
                ctx.Author.Id,
                attachPreparingAsControl: true,
                cancellationToken)
            .ConfigureAwait(false);
        await ReplyEnqueueAsync(ctx, kind, play, channel.Name, preparing.MessageId, preparingCreateTime)
            .ConfigureAwait(false);
    }

    private bool IsTooLarge(TrackInfoEntity track, bool wantVideo)
    {
        var cap = wantVideo ? _options.MaxVideoBytes : _options.MaxAudioBytes;
        return track.IsTooLarge
               || (track.SourceBytes is long bytes && bytes > cap);
    }


    private Task<PlayEnqueueKind> EnqueueOrStartAsync(
        ClanPlaybackSession state,
        QueuedPlay play,
        PlaybackMode mode,
        MezonClient client,
        long notifyChannelId,
        long controlUserId,
        bool attachPreparingAsControl,
        CancellationToken cancellationToken)
        => EnqueueManyOrStartAsync(
            state,
            [play],
            mode,
            client,
            notifyChannelId,
            controlUserId,
            attachPreparingAsControl,
            cancellationToken);

    private Task<PlayEnqueueKind> EnqueueManyOrStartAsync(
        ClanPlaybackSession state,
        IReadOnlyList<QueuedPlay> plays,
        PlaybackMode mode,
        MezonClient client,
        long notifyChannelId,
        long controlUserId,
        bool attachPreparingAsControl,
        CancellationToken cancellationToken)
        => _enqueue.EnqueueManyOrStartAsync(
            state,
            plays,
            mode,
            client,
            notifyChannelId,
            controlUserId,
            attachPreparingAsControl,
            TryClaimPlaySlot,
            ResetPrepToken,
            StartPump,
            onTooLarge: (s, p) => _ = HandlePrepTooLargeAsync(s, p),
            releaseSlot: ReleasePlaySlot,
            cancellationToken);

    private static Task ReplyEnqueueAsync(
        ICommandContext ctx,
        PlayEnqueueKind kind,
        QueuedPlay play,
        string? channelLabel,
        long? messageId,
        uint? createTime)
    {
        var content = kind switch
        {
            PlayEnqueueKind.QueueFull => PlayerMessageBuilder.QueueFull(),
            PlayEnqueueKind.SlotsFull => PlayerMessageBuilder.PlaybackSlotsFull(),
            PlayEnqueueKind.ModeConflict => PlayerMessageBuilder.ModeConflict(),
            PlayEnqueueKind.CutIn => PlayerMessageBuilder.PlayingNext(
                play.Track.Title,
                "Cut in ahead of the default playlist."),
            PlayEnqueueKind.Queued => PlayerMessageBuilder.Queued(play.Track, 1, channelLabel),
            _ => null,
        };
        if (content is null)
        {
            return Task.CompletedTask;
        }

        return UpdateOrReplyAsync(ctx, messageId, content, createTime);
    }

    private static void ResetPrepToken(ClanPlaybackSession state)
    {
        state.PrepCts?.Cancel();
        state.PrepCts?.Dispose();
        state.PrepCts = new CancellationTokenSource();
    }

    private void StartBackgroundPrep(MezonClient client, ClanPlaybackSession state, QueuedPlay play)
    {
        _enqueue.StartBackgroundPrep(client, state, play, ex =>
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

            if (state.ClanId is long clanId)
            {
                try
                {
                    await _playerStore.RemovePendingMatchingAsync(
                            clanId,
                            p => p.Source == play.Track.Source && p.ExternalId == play.Track.ExternalId)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Redis too-large remove failed clan={ClanId}", clanId);
                }
            }

            if (play.ReplyMessageId is long messageId && state.NotifyClient is not null)
            {
                var channel = await state.ResolveNotifyChannelAsync().ConfigureAwait(false);
                if (channel is not null)
                {
                    await channel.UpdateMessageAsync(
                            messageId,
                            PlayerMessageBuilder.CopyrightBlocked(),
                            hideEdited: true,
                            createTimeSeconds: play.ReplyCreateTimeSeconds)
                        .ConfigureAwait(false);
                }
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
                    "Pause/resume only works while a stream is playing."));
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
        // Cancel wakes WaitForTrackEnd; pump owns STN stop.
        state.CancelTrack();
        state.IsPlaying = false;
        ScheduleIdleDestroy(clanId, state);
    }

    private Task<bool> SkipStateAsync(ClanPlaybackSession state, CancellationToken cancellationToken)
    {
        // CancelTrack wakes the active pump (if any); it owns Stop + advance.
        // Only start a pump when nothing is driving the queue (idle with pending items).
        state.LastDestroyReason = PlayerDestroyReason.Skip;
        var needsPump = !state.IsPlaying;
        state.CancelTrack();
        if (needsPump)
        {
            StartPump(state, state.ClanId ?? 0);
        }

        return Task.FromResult(true);
    }

    public Task ShowQueueAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!TryGetState(clanId, out var state))
        {
            return ctx.ReplyAsync(PlayerMessageBuilder.QueueList(null, []));
        }

        return ctx.ReplyAsync(PlayerMessageBuilder.QueueList(state.Queue.CurrentItem, state.Queue.Snapshot()));
    }

    public async Task ShowNowPlayingAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!TryGetState(clanId, out var state) || state.Queue.Current is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NothingPlaying())
                .ConfigureAwait(false);
            return;
        }

        await _viz.EnsureAsync(ctx.Client, ctx.CancellationToken).ConfigureAwait(false);

        state.ControlUserId = ctx.Author.Id;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        // Seed without viz/buttons so ControlMessageId = reply id, then attach Skip/Stop + viz.
        var seed = BuildNowPlayingContent(state, clanId, includeMusicViz: false, includeControls: false);
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
            includeMusicViz: includeMusicViz);
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
            _logger.LogError(ex, "SoundCloud set import failed");
            await UpdateOrReplyAsync(ctx, preparingMessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (imported.Count == 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("That SoundCloud set looked empty or had nothing I can play."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var requester = ctx.Author.Username ?? ctx.Author.Id.ToString();
        var plays = new List<QueuedPlay>();
        foreach (var info in imported)
        {
            var track = info.WithRequester(ctx.Author.Id, requester);
            if (IsTooLarge(track, wantVideo: false))
            {
                continue;
            }

            plays.Add(new QueuedPlay(track, target));
        }

        if (plays.Count == 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("Every track in that set was too large or blocked."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var kind = await EnqueueManyOrStartAsync(
                state,
                plays,
                mode,
                ctx.Client,
                ctx.Channel.Id,
                ctx.Author.Id,
                attachPreparingAsControl: true,
                cancellationToken)
            .ConfigureAwait(false);

        var interrupt = kind == PlayEnqueueKind.CutIn;
        var content = kind switch
        {
            PlayEnqueueKind.QueueFull => PlayerMessageBuilder.QueueFull(),
            PlayEnqueueKind.SlotsFull => PlayerMessageBuilder.PlaybackSlotsFull(),
            PlayEnqueueKind.ModeConflict => PlayerMessageBuilder.ModeConflict(),
            _ => PlayerMessageBuilder.SoundCloudSetQueued(plays.Count, _options.MaxQueuePerClan, interrupt),
        };
        await UpdateOrReplyAsync(ctx, preparingMessageId, content, preparingCreateTime).ConfigureAwait(false);
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
}
