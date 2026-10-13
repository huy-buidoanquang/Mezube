using Mezon.Net.Core;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music;

namespace Mezube.Tests;

public sealed class SetDefaultStreamChannelTests
{
    [Fact]
    public void IsClanStreamChannel_streaming_channel_of_same_clan_is_accepted()
        => Assert.True(MusicPlayer.IsClanStreamChannel((int)ChannelType.Streaming, channelClanId: 1, clanId: 1));

    [Fact]
    public void IsClanStreamChannel_streaming_channel_of_other_clan_is_rejected()
        => Assert.False(MusicPlayer.IsClanStreamChannel((int)ChannelType.Streaming, channelClanId: 2, clanId: 1));

    [Theory]
    [InlineData((int)ChannelType.Channel)]
    [InlineData((int)ChannelType.MezonVoice)]
    [InlineData((int)ChannelType.GmeetVoice)]
    public void IsClanStreamChannel_non_streaming_channel_is_rejected(int channelType)
        => Assert.False(MusicPlayer.IsClanStreamChannel(channelType, channelClanId: 1, clanId: 1));

    [Fact]
    public async Task SetDefaultStreamChannelAsync_persists_and_caches_channel()
    {
        var settings = new FakeClanSettings();
        var binds = new BindStore(new BotOptions(), new NoVoiceBinds(), settings);

        await binds.SetDefaultStreamChannelAsync(1, 600);

        Assert.Equal([(1L, (long?)600L)], settings.Upserts);
        Assert.True(binds.TryGetDefaultStreamChannel(1, out var cached));
        Assert.Equal(600, cached);
    }

    [Fact]
    public async Task SetDefaultStreamChannelAsync_failed_write_leaves_cache_unset()
    {
        var binds = new BindStore(new BotOptions(), new NoVoiceBinds(), new FakeClanSettings { Fail = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => binds.SetDefaultStreamChannelAsync(1, 600));

        Assert.False(binds.TryGetDefaultStreamChannel(1, out _));
    }

    private sealed class FakeClanSettings : IClanSettingsRepository
    {
        public bool Fail { get; init; }

        public List<(long ClanId, long? ChannelId)> Upserts { get; } = [];

        public Task UpsertDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("db down");
            }

            Upserts.Add((clanId, channelId));
            return Task.CompletedTask;
        }

        public Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
            => Task.FromResult<ClanSettingsEntity?>(null);

        public Task EnsureClanAsync(long clanId, long? ownerId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpsertOwnerIdAsync(long clanId, long ownerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpsertDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoVoiceBinds : IVoiceBindStore
    {
        public Task SetUserVoiceChannelAsync(long clanId, long userId, long voiceChannelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long?> TryGetUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<(long ClanId, long UserId, long ChannelId)>> SnapshotAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
