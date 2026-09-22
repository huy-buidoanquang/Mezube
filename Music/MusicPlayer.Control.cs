using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    public async Task SkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, cancellationToken).ConfigureAwait(false);
        var outcome = await TrySkipAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken, preferred)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task StopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, cancellationToken).ConfigureAwait(false);
        var outcome = await TryStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken, preferred)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task PauseAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, cancellationToken).ConfigureAwait(false);
        var outcome = await TrySetPausedAsync(
                ctx.Client, clanId, ctx.Author.Id, paused: true, cancellationToken, preferred)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task ResumeAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, cancellationToken).ConfigureAwait(false);
        var outcome = await TrySetPausedAsync(
                ctx.Client, clanId, ctx.Author.Id, paused: false, cancellationToken, preferred)
            .ConfigureAwait(false);
        await ctx.ReplyAsync(outcome.Content).ConfigureAwait(false);
    }

    public async Task<ControlOutcome> TrySkipAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default,
        long? channelId = null,
        long? controlMessageId = null)
    {
        if (!TryPickControlSession(clanId, channelId, controlMessageId, out var state, out var resolveError))
        {
            return ControlOutcome.Denied(resolveError!);
        }

        var requesterId = state.Queue.CurrentItem?.Track.RequestedByUserId;
        if (!await _access.CanSkipAsync(client, clanId, userId, requesterId, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NotAllowed(
                "Only the person who queued this track, a DJ, or the clan owner can skip."));
        }

        var skipped = await SkipStateAsync(state, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(skipped
            ? PlayerMessageBuilder.Ok("Skipped", "On to the next track.")
            : PlayerMessageBuilder.NothingPlaying());
    }

    public async Task<ControlOutcome> TryStopAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default,
        long? channelId = null,
        long? controlMessageId = null)
    {
        if (!await _access.CanStopAsync(client, clanId, userId, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Denied(PlayerMessageBuilder.NotAllowed(
                "Only a DJ or the clan owner can stop playback."));
        }

        if (!TryPickControlSession(clanId, channelId, controlMessageId, out var state, out var resolveError))
        {
            return ControlOutcome.Denied(resolveError!);
        }

        await StopSessionAsync(state, cancellationToken).ConfigureAwait(false);
        return ControlOutcome.Ok(PlayerMessageBuilder.Ok("Stopped", "Playback stopped and the queue is clear."));
    }

    public async Task<ControlOutcome> TrySetPausedAsync(
        MezonClient client,
        long clanId,
        long userId,
        bool paused,
        CancellationToken cancellationToken = default,
        long? channelId = null)
    {
        if (!TryPickControlSession(clanId, channelId, controlMessageId: null, out var state, out var resolveError))
        {
            return ControlOutcome.Denied(resolveError!);
        }

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
            _logger.LogWarning(ex, "SFU pause failed clan={ClanId} paused={Paused}", clanId, paused);
            return ControlOutcome.Denied(PlayerMessageBuilder.Error(
                "Couldn’t pause/resume",
                "Something went wrong changing playback — try again."));
        }

        return ControlOutcome.Ok(PlayerMessageBuilder.Ok(
            paused ? "Paused" : "Resumed",
            paused ? "Stream paused. Use !resume when you’re ready." : "Stream is playing again."));
    }

    private bool TryPickControlSession(
        long clanId,
        long? channelId,
        long? controlMessageId,
        out ClanPlaybackSession state,
        out Mezon.Net.Client.MessageContent? error)
    {
        state = null!;
        error = null;
        if (controlMessageId is long messageId)
        {
            var byMessage = FindSessionByControlMessage(clanId, messageId);
            if (byMessage is not null)
            {
                state = byMessage;
                return true;
            }
        }

        var kind = TryResolveControlSession(clanId, channelId, out var resolved);
        if (kind == ControlSessionResolve.Ambiguous)
        {
            error = AmbiguousChannelMessage();
            return false;
        }

        if (kind == ControlSessionResolve.Nothing || resolved is null)
        {
            error = PlayerMessageBuilder.NothingPlaying();
            return false;
        }

        state = resolved;
        return true;
    }

    public readonly record struct ControlOutcome(bool Allowed, Mezon.Net.Client.MessageContent Content)
    {
        public static ControlOutcome Ok(Mezon.Net.Client.MessageContent content) => new(true, content);
        public static ControlOutcome Denied(Mezon.Net.Client.MessageContent content) => new(false, content);
    }

    public Task<bool> SkipInternalAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var kind = TryResolveControlSession(clanId, preferredChannelId: null, out var state);
        if (kind != ControlSessionResolve.Found || state is null || (!state.IsPlaying && state.Queue.Count == 0))
        {
            return Task.FromResult(false);
        }

        return SkipStateAsync(state, cancellationToken);
    }

    public async Task StopInternalAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var kind = TryResolveControlSession(clanId, preferredChannelId: null, out var state);
        if (kind != ControlSessionResolve.Found || state is null)
        {
            return;
        }

        await StopSessionAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopSessionAsync(ClanPlaybackSession state, CancellationToken cancellationToken)
    {
        var clanId = state.ClanId ?? 0;
        var channelId = SessionChannelId(state);
        state.Queue.Clear();
        state.PlayingDefaultPlaylist = false;
        state.DefaultAutoplayArmed = false;
        state.LastDestroyReason = PlayerDestroyReason.UserStop;
        state.PrepCts?.Cancel();
        if (clanId != 0 && channelId != 0)
        {
            await _playerStore.SetLoopModeAsync(clanId, channelId, LoopMode.Off, cancellationToken).ConfigureAwait(false);
            await CloseHistoryAsync(state.PlayHistoryId, PlayEndReason.Stop, cancellationToken).ConfigureAwait(false);
            await ClearPersistedSessionAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
        }

        state.CancelTrack();
        state.IsPlaying = false;
        ScheduleIdleDestroy(clanId, state);
    }

    public async Task ShowQueueAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, ctx.CancellationToken).ConfigureAwait(false);
        if (!TryPickControlSession(clanId, preferred, controlMessageId: null, out var state, out var error))
        {
            if (error is not null && error != PlayerMessageBuilder.NothingPlaying())
            {
                await ctx.ReplyAsync(error).ConfigureAwait(false);
                return;
            }

            await ctx.ReplyAsync(PlayerMessageBuilder.QueueList(null, [])).ConfigureAwait(false);
            return;
        }

        await ctx.ReplyAsync(PlayerMessageBuilder.QueueList(state.Queue.CurrentItem, state.Queue.Snapshot()))
            .ConfigureAwait(false);
    }

    public async Task ShowNowPlayingAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var preferred = await TryPreferredStreamChannelIdAsync(ctx, ctx.CancellationToken).ConfigureAwait(false);
        if (!TryPickControlSession(clanId, preferred, controlMessageId: null, out var state, out var error)
            || state.Queue.Current is null)
        {
            await ctx.ReplyAsync(error ?? PlayerMessageBuilder.NothingPlaying()).ConfigureAwait(false);
            return;
        }

        await _viz.EnsureAsync(ctx.Client, ctx.CancellationToken).ConfigureAwait(false);

        state.ControlUserId = ctx.Author.Id;
        state.NotifyClient = ctx.Client;
        state.NotifyChannelId = ctx.Channel.Id;
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
}
