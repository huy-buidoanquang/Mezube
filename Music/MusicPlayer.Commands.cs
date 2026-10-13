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

        var preferred = await TryPreferredStreamChannelIdAsync(ctx, cancellationToken).ConfigureAwait(false);
        var kind = TryResolveControlSession(clanId, preferred, out var state);
        if (kind == ControlSessionResolve.Ambiguous)
        {
            await ctx.ReplyAsync(AmbiguousChannelMessage()).ConfigureAwait(false);
            return;
        }

        var channelId = kind == ControlSessionResolve.Found && state is not null
            ? SessionChannelId(state)
            : preferred ?? 0;
        if (channelId == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Which stream?",
                    "Loop is per #stream. Run this in that stream channel, or tag it."))
                .ConfigureAwait(false);
            return;
        }

        var arg = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "cycle";
        var current = await _playerStore.GetLoopModeAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
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
        await _playerStore.SetLoopModeAsync(clanId, channelId, next, cancellationToken).ConfigureAwait(false);
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
}
