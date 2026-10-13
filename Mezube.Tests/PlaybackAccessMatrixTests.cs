using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching;
using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Infrastructure.Caching;
using Mezube.Infrastructure.Caching.Snapshots;
using Mezube.Music;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class PlaybackAccessMatrixTests
{
    private const long ClanId = 1;
    private const long DjRoleId = 99;
    private const long OwnerId = 10;
    private const long DjId = 20;
    private const long RequesterId = 30;
    private const long MemberId = 40;

    [Theory]
    [InlineData(RequesterId, true)]
    [InlineData(DjId, true)]
    [InlineData(OwnerId, true)]
    [InlineData(MemberId, false)]
    public async Task CanSkipAsync_allows_requester_dj_and_owner_only(long userId, bool expected)
    {
        await using var client = new MezonClient(new MezonClientOptions());

        Assert.Equal(expected, await NewAccess(DjRoleId).CanSkipAsync(client, ClanId, userId, RequesterId));
    }

    [Theory]
    [InlineData(RequesterId, false)]
    [InlineData(DjId, true)]
    [InlineData(OwnerId, true)]
    [InlineData(MemberId, false)]
    public async Task CanStopAsync_allows_dj_and_owner_only(long userId, bool expected)
    {
        await using var client = new MezonClient(new MezonClientOptions());

        Assert.Equal(expected, await NewAccess(DjRoleId).CanStopAsync(client, ClanId, userId));
    }

    [Theory]
    [InlineData(OwnerId, true)]
    [InlineData(DjId, false)]
    [InlineData(MemberId, false)]
    public async Task CanConfigureDjAsync_allows_owner_only(long userId, bool expected)
    {
        await using var client = new MezonClient(new MezonClientOptions());

        Assert.Equal(expected, await NewAccess(DjRoleId).CanConfigureDjAsync(client, ClanId, userId));
    }

    [Fact]
    public async Task CanStopAsync_denies_role_member_when_no_dj_role_is_set()
    {
        await using var client = new MezonClient(new MezonClientOptions());

        Assert.False(await NewAccess(djRoleId: null).CanStopAsync(client, ClanId, DjId));
    }

    private static PlaybackAccess NewAccess(long? djRoleId)
        => new(
            new FakeClanSettings(djRoleId),
            null!,
            null!,
            null!,
            new FakeSnapshotStore(new RoleSnapshotDto { Id = DjRoleId, ClanId = ClanId, MemberIds = [DjId] }),
            new MezonSnapshotKeyFactory(new BotOptions { BotId = 42 }),
            NullLogger<PlaybackAccess>.Instance);

    private sealed class FakeClanSettings(long? djRoleId) : IClanSettingsService
    {
        public Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
            => Task.FromResult(djRoleId);

        public Task<long?> GetOwnerIdAsync(long clanId, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(OwnerId);

        public Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task EnsureOwnerAsync(long clanId, long ownerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetVoteSkipAsync(long clanId, bool enabled, float? ratio = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSnapshotStore(RoleSnapshotDto role) : IEntitySnapshotStore
    {
        public ValueTask<TDto?> GetAsync<TDto>(CacheKey key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(role is TDto dto ? dto : default);

        public ValueTask SetAsync<TDto>(CacheKey key, TDto dto, CacheEntryOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask InvalidateAsync(CacheKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
