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
    public async Task MusicChannelAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanConfigureDjAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only the clan owner can manage which channels accept music commands."))
                .ConfigureAwait(false);
            return;
        }

        var sub = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                {
                    var channels = await _commandChannels.ListAsync(clanId, cancellationToken).ConfigureAwait(false);
                    var body = channels.Count == 0
                        ? "Every channel can queue music."
                        : string.Join(", ", await FormatChannelMentionsAsync(ctx.Client, channels, cancellationToken)
                            .ConfigureAwait(false));
                    await ctx.ReplyAsync(PlayerMessageBuilder.MusicChannelsListed(body)).ConfigureAwait(false);
                    return;
                }
            case "clear":
                await _commandChannels.ClearAsync(clanId, cancellationToken).ConfigureAwait(false);
                await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                        "Music channels",
                        "Allowlist cleared — music commands work everywhere again."))
                    .ConfigureAwait(false);
                return;
            case "add":
            case "remove":
                {
                    var channelId = ChannelTargetParser.TryGetHashtagChannelId(ctx)
                        ?? (ctx.Args.Count > 1 && long.TryParse(ctx.Args[1], out var parsed) ? parsed : (long?)null);
                    if (channelId is not long id)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing channel",
                                $"Tag a channel, e.g. {_options.CommandPrefix}musicchannel {sub} #channel"))
                            .ConfigureAwait(false);
                        return;
                    }

                    if (sub == "add")
                    {
                        await _commandChannels.AddAsync(clanId, id, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                                "Channel allowed",
                                $"{mention} can now accept music commands."))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _commandChannels.RemoveAsync(clanId, id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                                "Channel removed",
                                $"{mention} is off the music allowlist."))
                            .ConfigureAwait(false);
                    }

                    return;
                }
            default:
                await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                        "Unknown option",
                        "Use add | remove | list | clear."))
                    .ConfigureAwait(false);
                return;
        }
    }
}
