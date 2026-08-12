using Mezon.Net.Sdk.Commands;
using Mezube.Music;
using Mezube.Ui;

namespace Mezube.Helpers;

public sealed class MusicCommandModule
{
    private readonly MusicPlayer _player;
    private readonly PlaybackAccess _access;
    private readonly string _prefix;

    public MusicCommandModule(MusicPlayer player, PlaybackAccess access, string prefix)
    {
        _player = player;
        _access = access;
        _prefix = prefix;
    }

    public void Register(CommandService commands)
    {
        commands.AddCommand("play", HandlePlayAsync).WithAlias("p");
        commands.AddCommand("skip", ctx => _player.SkipAsync(ctx, ctx.CancellationToken)).WithAlias("sk");
        commands.AddCommand("voteskip", ctx => _player.VoteSkipAsync(ctx, ctx.CancellationToken)).WithAlias("vskip");
        commands.AddCommand("stop", ctx => _player.StopAsync(ctx, ctx.CancellationToken)).WithAlias("st");
        commands.AddCommand("pause", ctx => _player.PauseAsync(ctx, ctx.CancellationToken));
        commands.AddCommand("resume", ctx => _player.ResumeAsync(ctx, ctx.CancellationToken)).WithAlias("unpause");
        commands.AddCommand("queue", ctx => _player.ShowQueueAsync(ctx)).WithAlias("q");
        commands.AddCommand("nowplay", ctx => _player.ShowNowPlayingAsync(ctx)).WithAlias("np");
        commands.AddCommand("loop", ctx => _player.SetLoopAsync(ctx, ctx.CancellationToken));
        commands.AddCommand("seek", ctx => _player.SeekAsync(ctx, ctx.CancellationToken));
        commands.AddCommand("playlist", ctx => _player.PlaylistAsync(ctx, ctx.CancellationToken)).WithAlias("pl");
        commands.AddCommand("musicchannel", ctx => _player.MusicChannelAsync(ctx, ctx.CancellationToken))
            .WithAlias("mchannel");
        commands.AddCommand("setdj", ctx => _player.SetDjRoleAsync(ctx, ctx.CancellationToken));
        commands.AddCommand("settings", ctx => _player.ShowSettingsAsync(ctx, ctx.CancellationToken));
        commands.AddCommand("help", HandleHelpAsync);
    }

    private Task HandlePlayAsync(ICommandContext ctx)
    {
        var channelId = ChannelTargetParser.TryGetHashtagChannelId(ctx);
        var query = ChannelTargetParser.BuildQuery(ctx.Args);
        if (string.IsNullOrWhiteSpace(query))
        {
            return ctx.ReplyAsync(PlayerMessageBuilder.PlayUsage(_prefix));
        }

        return _player.PlayAutoAsync(ctx, query, channelId, ctx.CancellationToken);
    }

    private async Task HandleHelpAsync(ICommandContext ctx)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var userId = ctx.Author.Id;
        var isOwner = await _access.IsClanOwnerAsync(ctx.Client, clanId, userId, ctx.CancellationToken)
            .ConfigureAwait(false);
        var isDjOrOwner = isOwner
            || await _access.IsDjOrOwnerAsync(ctx.Client, clanId, userId, ctx.CancellationToken)
                .ConfigureAwait(false);

        await ctx.ReplyAsync(PlayerMessageBuilder.Help(_prefix, isDjOrOwner, isOwner)).ConfigureAwait(false);
    }
}
