using Mezon.Net.Client;
using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music.Interactive;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    public async Task SetLoopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken).ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only a DJ or the clan owner can change loop mode."))
                .ConfigureAwait(false);
            return;
        }

        var arg = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "cycle";
        var current = await _playerStore.GetLoopModeAsync(clanId, cancellationToken).ConfigureAwait(false);
        LoopMode next = arg switch
        {
            "off" or "none" or "0" => LoopMode.Off,
            "track" or "song" or "one" or "1" => LoopMode.Track,
            "queue" or "all" or "2" => LoopMode.Queue,
            _ => current switch
            {
                LoopMode.Off => LoopMode.Track,
                LoopMode.Track => LoopMode.Queue,
                _ => LoopMode.Off,
            },
        };
        await _playerStore.SetLoopModeAsync(clanId, next, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Loop mode",
                next switch
                {
                    LoopMode.Track => "Looping the current track.",
                    LoopMode.Queue => "Looping the whole queue.",
                    _ => "Loop disabled.",
                }))
            .ConfigureAwait(false);
    }

    public async Task VoteSkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!TryGetState(clanId, out var state))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Nothing playing",
                    "There’s no track to vote on right now."))
                .ConfigureAwait(false);
            return;
        }

        var historyId = state.PlayHistoryId
            ?? await _playerStore.GetPlayHistoryIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (historyId is not long hid)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Nothing playing",
                    "There’s no track to vote on right now."))
                .ConfigureAwait(false);
            return;
        }

        var (met, votes, needed, error) = await _access.TryVoteSkipAsync(
                clanId,
                hid,
                ctx.Author.Id,
                state.Target?.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Vote-skip", error)).ConfigureAwait(false);
            return;
        }

        if (!met)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                    "Vote recorded",
                    [
                        new("Progress", $"{votes}/{needed}"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.Skip;
        await SkipStateAsync(state, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Skipping!",
                [
                    new("Votes", $"{votes}/{needed}"),
                    new("Next", "Moving on to the next track."),
                ]))
            .ConfigureAwait(false);
    }
}
