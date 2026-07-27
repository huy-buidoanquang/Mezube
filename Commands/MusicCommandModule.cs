using Mezon.Net.Sdk.Commands;
using Mezube.Music;
using Mezube.Ui;

namespace Mezube.Commands;

public sealed class MusicCommandModule
{
    private readonly MusicPlayer _player;
    private readonly string _prefix;

    public MusicCommandModule(MusicPlayer player, string prefix)
    {
        _player = player;
        _prefix = prefix;
    }

    public void Register(CommandService commands)
    {
        commands.AddCommand("play", HandlePlayAsync).WithAlias("p");
        commands.AddCommand("stream", HandleStreamAsync).WithAlias("s");
        commands.AddCommand("skip", ctx => _player.SkipAsync(ctx, ctx.CancellationToken)).WithAlias("sk");
        commands.AddCommand("stop", ctx => _player.StopAsync(ctx, ctx.CancellationToken)).WithAlias("st");
        commands.AddCommand("queue", ctx => _player.ShowQueueAsync(ctx)).WithAlias("q");
        commands.AddCommand("nowplay", ctx => _player.ShowNowPlayingAsync(ctx)).WithAlias("np");
        commands.AddCommand("help", HandleHelpAsync);
    }

    private Task HandlePlayAsync(ICommandContext ctx)
    {
        var channelId = ChannelTargetParser.TryGetHashtagChannelId(ctx);
        var query = ChannelTargetParser.BuildQuery(ctx.Args);
        if (string.IsNullOrWhiteSpace(query))
        {
            return ctx.ReplyAsync(PlayerMessageBuilder.Error(
                "Missing track",
                $"Example: `{_prefix}play #voice_channel never gonna give you up`"));
        }

        return _player.PlayVoiceAsync(ctx, query, channelId, ctx.CancellationToken);
    }

    private Task HandleStreamAsync(ICommandContext ctx)
    {
        var channelId = ChannelTargetParser.TryGetHashtagChannelId(ctx);
        var query = ChannelTargetParser.BuildQuery(ctx.Args);
        if (string.IsNullOrWhiteSpace(query))
        {
            return ctx.ReplyAsync(PlayerMessageBuilder.Error(
                "Missing track",
                $"Example: `{_prefix}stream #stream_channel never gonna give you up`"));
        }

        return _player.PlayStreamingAsync(ctx, query, channelId, ctx.CancellationToken);
    }

    private Task HandleHelpAsync(ICommandContext ctx)
        => ctx.ReplyAsync(PlayerMessageBuilder.Help(_prefix));
}
