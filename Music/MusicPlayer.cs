using Mezon.Net.Core;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
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
    private readonly PlaybackAccess _access;
    private readonly TrackPrepService _prep;
    private readonly BotOptions _options;
    private readonly ILogger<MusicPlayer> _logger;
    private readonly ConcurrentDictionary<long, ClanPlayerState> _states = new();
    private readonly SemaphoreSlim _playSlots;

    public MusicPlayer(
        ITrackResolver resolver,
        StreamingChannelSink streamingSink,
        VoiceChannelSink voiceSink,
        BindStore binds,
        MusicVizAssets viz,
        PlaybackAccess access,
        TrackPrepService prep,
        BotOptions options,
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
        _logger = logger;
        _playSlots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPlayback));
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

        Mezon.Net.Sdk.Entities.TextChannel channel;
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
                    "Invalid request",
                    $"Channel {channelId} is not a streaming channel."))
                .ConfigureAwait(false);
            return;
        }

        var destination = PlayerMessageBuilder.FormatDestination("streaming", channel.Name);
        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing(destination))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;
        CommandReplyTracker.Remember(preparing.MessageId, preparingCreateTime, ctx.Channel);

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

        var target = new PlaybackTarget(clanId, channelId, ChannelLabel: channel.Name);
        var play = new QueuedPlay(track, target, preparing.MessageId, preparingCreateTime);
        if (state.IsPlaying)
        {
            if (state.Mode != PlaybackMode.Streaming)
            {
                await UpdateOrReplyAsync(
                        ctx,
                        preparing.MessageId,
                        PlayerMessageBuilder.Error(
                            "Mode conflict",
                            "This clan is playing voice. Use !play to queue, or !stop before !stream."),
                        preparingCreateTime)
                    .ConfigureAwait(false);
                return;
            }

            state.Queue.Enqueue(play);
            StartBackgroundPrep(ctx.Client, state, play);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count, channelId),
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
        state.Queue.Enqueue(play);
        state.Mode = PlaybackMode.Streaming;
        state.Target = target;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        state.HoldsPlaySlot = true;
        ResetPrepToken(state);
        StartBackgroundPrep(ctx.Client, state, play);
        _ = PumpAsync(state, clanId, cancellationToken);
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
                    "Invalid request",
                    "Join a voice channel, or mention it: !play #voice <query>."))
                .ConfigureAwait(false);
            return;
        }

        Mezon.Net.Sdk.Entities.TextChannel channel;
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
                    "Invalid request",
                    $"Channel {channelId} is not a voice channel."))
                .ConfigureAwait(false);
            return;
        }

        var destination = PlayerMessageBuilder.FormatDestination("voice", channel.Name);
        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Preparing(destination))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;
        CommandReplyTracker.Remember(preparing.MessageId, preparingCreateTime, ctx.Channel);

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

        var target = new PlaybackTarget(clanId, channelId, RoomName: channelId.ToString(), ChannelLabel: channel.Name);
        var play = new QueuedPlay(track, target, preparing.MessageId, preparingCreateTime);
        if (state.IsPlaying)
        {
            if (state.Mode != PlaybackMode.Voice)
            {
                await UpdateOrReplyAsync(
                        ctx,
                        preparing.MessageId,
                        PlayerMessageBuilder.Error(
                            "Mode conflict",
                            "This clan is streaming. Use !stream to queue, or !stop before !play."),
                        preparingCreateTime)
                    .ConfigureAwait(false);
                return;
            }

            state.Queue.Enqueue(play);
            StartBackgroundPrep(ctx.Client, state, play);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Queued(track, state.Queue.Count, channelId),
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
        state.Queue.Enqueue(play);
        state.Mode = PlaybackMode.Voice;
        state.Target = target;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        state.ControlMessageId = preparing.MessageId;
        state.ControlMessageCreateTimeSeconds = preparingCreateTime;
        state.ControlUserId = ctx.Author.Id;
        state.ClanId = clanId;
        state.HoldsPlaySlot = true;
        ResetPrepToken(state);
        StartBackgroundPrep(ctx.Client, state, play);
        _ = PumpAsync(state, clanId, cancellationToken);
    }

    private bool IsTooLarge(TrackInfoEntity track)
        => track.SourceBytes is long bytes && bytes > _options.MaxAudioBytes;

    private Task<bool> TryAcquirePlaySlotAsync()
        => _playSlots.WaitAsync(0);

    private void ReleasePlaySlot(ClanPlayerState state)
    {
        if (!state.HoldsPlaySlot)
        {
            return;
        }

        state.HoldsPlaySlot = false;
        _playSlots.Release();
    }

    private static void ResetPrepToken(ClanPlayerState state)
    {
        state.PrepCts?.Cancel();
        state.PrepCts?.Dispose();
        state.PrepCts = new CancellationTokenSource();
    }

    private void StartBackgroundPrep(MezonClient client, ClanPlayerState state, QueuedPlay play)
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

    private async Task HandlePrepTooLargeAsync(ClanPlayerState state, QueuedPlay play)
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
        if (!outcome.Allowed)
        {
            await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
            return;
        }

        await PublishControlOutcomeAsync(ctx, clanId, outcome.Content).ConfigureAwait(false);
    }

    public async Task StopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var outcome = await TryStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
        if (!outcome.Allowed)
        {
            await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
            return;
        }

        await PublishControlOutcomeAsync(ctx, clanId, outcome.Content).ConfigureAwait(false);
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
                "Only the track requester, DJ role, or clan owner can skip. (Vote-skip coming later.)"));
        }

        var skipped = await SkipInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(skipped
            ? PlayerMessageBuilder.Ok("Skipped", "Moved to the next track (if any).")
            : PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."));
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
                "Only DJ role or clan owner can stop playback."));
        }

        await StopInternalAsync(clanId, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(PlayerMessageBuilder.Ok("Stopped", "Playback stopped and queue cleared."));
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
        state.LastDestroyReason = PlayerDestroyReason.UserStop;
        state.PrepCts?.Cancel();
        // Wake the pump; it owns the single StopAsync for the active track.
        state.CancelTrack();
        state.IsPlaying = false;
        state.TrackStartedAt = null;
        ScheduleIdleDestroy(clanId, state);
    }

    private async Task PublishControlOutcomeAsync(
        ICommandContext ctx,
        long clanId,
        Mezon.Net.Client.MessageContent content)
    {
        var state = GetState(clanId);
        if (state.ControlMessageId is long messageId
            && state.NotifyClient is not null
            && state.NotifyChannelId is long channelId)
        {
            try
            {
                var channel = await state.NotifyClient.GetChannelAsync(channelId, ctx.CancellationToken)
                    .ConfigureAwait(false);
                await channel.UpdateMessageAsync(
                        messageId,
                        content,
                        hideEdited: true,
                        createTimeSeconds: state.ControlMessageCreateTimeSeconds)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update control message {MessageId} after skip/stop", messageId);
            }
        }

        await ctx.ReplyAsync(content).ConfigureAwait(false);
    }

    private async Task<bool> SkipStateAsync(ClanPlayerState state, CancellationToken cancellationToken)
    {
        // CancelTrack wakes the active pump (if any); it owns Stop + advance.
        // Only start a pump when nothing is driving the queue (idle with pending items).
        state.LastDestroyReason = PlayerDestroyReason.Skip;
        var needsPump = !state.IsPlaying;
        state.CancelTrack();
        if (needsPump)
        {
            _ = PumpAsync(state, state.ClanId ?? 0, cancellationToken);
        }

        return true;
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
            await ctx.ReplyAsync(PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."))
                .ConfigureAwait(false);
            return;
        }

        await _viz.EnsureAsync(ctx.Client, ctx.CancellationToken).ConfigureAwait(false);

        state.ControlUserId = ctx.Author.Id;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
        // Reply without controls first so we can stamp ControlMessageId = reply id for buttons.
        var seed = BuildNowPlayingContent(state, clanId, includeMusicViz: true, includeControls: false);
        var reply = await ctx.ReplyAsync(seed).ConfigureAwait(false);
        state.ControlMessageId = reply.MessageId;
        state.ControlMessageCreateTimeSeconds = reply.CreateTimeSeconds > 0 ? reply.CreateTimeSeconds : null;
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
                    "Usage: !setdj @role | roleId | none"))
                .ConfigureAwait(false);
            return;
        }

        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("off", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            await _access.SetDjRoleIdAsync(clanId, null, cancellationToken).ConfigureAwait(false);
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok("DJ role cleared", "Force skip/stop now require clan owner."))
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
                    "Pass a role mention, numeric role id, or exact role name. Use none to clear."))
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
        var label = string.IsNullOrWhiteSpace(roleTitle) ? $"{roleId}" : $"{roleTitle} ({roleId})";
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("DJ role set", $"Members with {label} can force-skip and stop."))
            .ConfigureAwait(false);
    }

    public async Task ShowSettingsAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var djRoleId = await _access.GetDjRoleIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        var djValue = djRoleId is long id ? $"{id}" : "none (owner only for force skip/stop)";
        await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                "Clan settings",
                $"DJ role: {djValue}"))
            .ConfigureAwait(false);
    }

    private static Mezon.Net.Client.MessageContent BuildNowPlayingContent(
        ClanPlayerState state,
        long clanId,
        bool includeMusicViz = false,
        bool includeControls = true)
    {
        var item = state.Queue.CurrentItem!;
        var position = state.GetElapsed();
        var mode = state.Mode == PlaybackMode.Voice ? "voice" : "streaming";
        var destination = PlayerMessageBuilder.FormatDestination(mode, item.Target.ChannelLabel);
        return PlayerMessageBuilder.NowPlaying(
            item.Track,
            state.Queue.Count,
            destination,
            position: position,
            controlMessageId: includeControls ? state.ControlMessageId : null,
            controlUserId: includeControls ? state.ControlUserId : null,
            clanId: clanId,
            includeMusicViz: false);
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

            return (track.WithRequester(ctx.Author.Id, ctx.Author.Username ?? ctx.Author.Id.ToString()), null);
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

    private async Task PumpAsync(ClanPlayerState state, long clanId, CancellationToken cancellationToken)
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
                    state.IsPlaying = false;
                    state.Target = null;
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
                    state.TrackStartedAt = DateTimeOffset.UtcNow;
                    if (item.ReplyMessageId is long replyId)
                    {
                        state.ControlMessageId = replyId;
                        state.ControlMessageCreateTimeSeconds = item.ReplyCreateTimeSeconds;
                    }

                    await SendNowPlayingAsync(state, includeMusicViz: true).ConfigureAwait(false);

                    try
                    {
                        await WaitForTrackEndAsync(state, track, mode, target, trackCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                    {
                    }

                    try
                    {
                        await sink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stop sink failed channel={ChannelId}", target.ChannelId);
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
                    state.TrackStartedAt = null;
                    state.ClearTrackCts();
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

    private async Task NotifyCopyrightBlockedAsync(ClanPlayerState state, QueuedPlay item)
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

    private void ScheduleIdleDestroy(long clanId, ClanPlayerState state)
    {
        state.ScheduleIdleDestroy(
            IdleSessionTtl,
            () =>
            {
                if (state.IsPlaying || state.Queue.Count > 0 || state.Queue.Current is not null)
                {
                    return;
                }

                if (_states.TryGetValue(clanId, out var current) && ReferenceEquals(current, state)
                    && _states.TryRemove(clanId, out _))
                {
                    state.LastDestroyReason = PlayerDestroyReason.IdleTimeout;
                    _logger.LogDebug(
                        "Idle player session destroyed clan={ClanId} reason={Reason}",
                        clanId,
                        state.LastDestroyReason);
                }
            });
    }

    private static readonly TimeSpan IdleSessionTtl = TimeSpan.FromSeconds(60);

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
        PlaybackMode mode,
        PlaybackTarget target,
        CancellationToken cancellationToken)
    {
        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var durationWait = track.Duration is { } d && d > TimeSpan.Zero
            ? WaitByDurationAsync(state, track, d, durationCts.Token)
            : Task.Delay(TimeSpan.FromMinutes(10), durationCts.Token);

        if (mode != PlaybackMode.Voice)
        {
            using var endedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var endedWait = _streamingSink.WaitUntilPublisherEndedAsync(endedCts.Token);
            var streamWinner = await Task.WhenAny(durationWait, endedWait).ConfigureAwait(false);
            if (streamWinner == endedWait)
            {
                durationCts.Cancel();
                await endedWait.ConfigureAwait(false);
                _logger.LogDebug(
                    "Streaming track ended by STN signal title={Title} channel={ChannelId}",
                    track.Title,
                    target.ChannelId);
                return;
            }

            endedCts.Cancel();
            await durationWait.ConfigureAwait(false);
            return;
        }

        var roomName = string.IsNullOrWhiteSpace(target.RoomName)
            ? target.ChannelId.ToString()
            : target.RoomName!;
        using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var statusWait = _voiceSink.WaitUntilTerminalAsync(roomName, statusCts.Token);
        var winner = await Task.WhenAny(durationWait, statusWait).ConfigureAwait(false);

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

    private async Task WaitByDurationAsync(
        ClanPlayerState state,
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

    private async Task NotifyUpNextAsync(ClanPlayerState state, QueuedPlay next, int secondsRemaining)
    {
        if (state.NotifyClient is null || state.NotifyChannelId is not long channelId)
        {
            return;
        }

        try
        {
            var channel = await state.NotifyClient.GetChannelAsync(channelId).ConfigureAwait(false);
            await channel.SendAsync(PlayerMessageBuilder.UpNext(next.Track, secondsRemaining, next.Target.ChannelId))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify up-next for {Title}", next.Track.Title);
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

    private ClanPlayerState GetState(long clanId)
        => _states.GetOrAdd(clanId, id => new ClanPlayerState { ClanId = id });

    private enum PlaybackMode
    {
        Streaming,
        Voice,
    }

    private enum PlayerDestroyReason
    {
        None,
        QueueEmpty,
        UserStop,
        Skip,
        StnFailed,
        IdleTimeout,
        ModeConflict,
        RoomConflict,
        CapacityExceeded,
    }

    private sealed class ClanPlayerState
    {
        private readonly SemaphoreSlim _pumpGate = new(1, 1);
        private CancellationTokenSource? _trackCts;
        private CancellationTokenSource? _idleCts;

        public MusicQueue Queue { get; } = new();
        public PlaybackTarget? Target { get; set; }
        public PlaybackMode Mode { get; set; } = PlaybackMode.Streaming;
        public bool IsPlaying { get; set; }
        public DateTimeOffset? TrackStartedAt { get; set; }
        public long? ClanId { get; set; }
        public long? ControlMessageId { get; set; }
        public uint? ControlMessageCreateTimeSeconds { get; set; }
        public long? ControlUserId { get; set; }
        public MezonClient? NotifyClient { get; set; }
        public long? NotifyChannelId { get; set; }
        public PlayerDestroyReason LastDestroyReason { get; set; }
        public bool HoldsPlaySlot { get; set; }
        public CancellationTokenSource? PrepCts { get; set; }

        public TimeSpan GetElapsed()
        {
            if (TrackStartedAt is not { } started)
            {
                return TimeSpan.Zero;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
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

        public void CancelIdleDestroy()
        {
            try
            {
                _idleCts?.Cancel();
            }
            catch
            {
            }

            _idleCts?.Dispose();
            _idleCts = null;
        }

        public void ScheduleIdleDestroy(TimeSpan delay, Action destroy)
        {
            CancelIdleDestroy();
            var cts = new CancellationTokenSource();
            _idleCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    destroy();
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }
}

