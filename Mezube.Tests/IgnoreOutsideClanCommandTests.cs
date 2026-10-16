using System.Reflection;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Entities;
using Mezube.Bot;
using Microsoft.Extensions.Logging.Abstractions;
using ChannelDescription = Mezon.Net.Internal.Api.ChannelDescription;
using ClanDesc = Mezon.Net.Internal.Api.ClanDesc;
using MezonClient = Mezon.Net.Sdk.MezonClient;
using User = Mezon.Net.Sdk.Entities.User;

namespace Mezube.Tests;

public sealed class IgnoreOutsideClanCommandTests
{
    [Theory]
    [InlineData(0L, false)]
    [InlineData(1779484504377790464L, true)]
    public async Task IgnoreOutsideClanAsync_runs_commands_only_in_clan_channels(long clanId, bool expectedRun)
    {
        await using var client = new MezonClient(new MezonClientOptions());
        var context = new FakeCommandContext(client, NewChannel(client, clanId));
        var ran = false;

        await MezubeBot.IgnoreOutsideClanAsync(
            context,
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        Assert.Equal(expectedRun, ran);
    }

    private static Channel NewChannel(MezonClient client, long clanId)
    {
        var clan = (Clan)Activator.CreateInstance(
            typeof(Clan),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [client, new ClanDesc { ClanId = clanId }],
            culture: null)!;
        return (Channel)Activator.CreateInstance(
            typeof(Channel),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [client, new ChannelDescription { ChannelId = 1840675209556791296, ClanId = clanId }, clan],
            culture: null)!;
    }

    private sealed class FakeCommandContext(MezonClient client, Channel channel) : ICommandContext
    {
        public MezonClient Client => client;
        public Message Message => null!;
        public Channel Channel => channel;
        public Clan? Clan => null;
        public User Author => null!;
        public string Prefix => "!";
        public string Name => "play";
        public IReadOnlyList<string> Args => [];
        public CancellationToken CancellationToken => CancellationToken.None;

        public Task<ChannelMessageAckResponse> ReplyAsync(MessageContent content, RequestOptions? options = null)
            => throw new NotSupportedException();

        public Task<ChannelMessageAckResponse> ReplyTextAsync(string text, RequestOptions? options = null)
            => throw new NotSupportedException();

        public Task<ChannelMessageAckResponse> SendAsync(MessageContent content, RequestOptions? options = null)
            => throw new NotSupportedException();

        public Task<ChannelMessageAckResponse> SendTextAsync(string text, RequestOptions? options = null)
            => throw new NotSupportedException();
    }
}
