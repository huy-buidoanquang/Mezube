using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace Mezube.Application;

public interface IClanSettingsService
{
    Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default);

    Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default);

    Task<long?> GetOwnerIdAsync(long clanId, CancellationToken cancellationToken = default);

    Task EnsureOwnerAsync(long clanId, long ownerId, CancellationToken cancellationToken = default);

    Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default);

    Task SetDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default);

    Task SetVoteSkipAsync(long clanId, bool enabled, float? ratio = null, CancellationToken cancellationToken = default);
}

public sealed class ClanSettingsService : IClanSettingsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly IClanSettingsRepository _settings;
    private readonly IMemoryCache _cache;

    public ClanSettingsService(IClanSettingsRepository settings, IMemoryCache cache)
    {
        _settings = settings;
        _cache = cache;
    }

    public async Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(clanId);
        if (_cache.TryGetValue(key, out ClanSettingsEntity? cached))
        {
            return cached;
        }

        var row = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, row, CacheTtl);
        return row;
    }

    public async Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var row = await TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        return row?.DjRoleId is long id && id != 0 ? id : null;
    }

    public async Task<long?> GetOwnerIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var row = await TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        return row?.OwnerId is long id && id != 0 ? id : null;
    }

    public async Task EnsureOwnerAsync(long clanId, long ownerId, CancellationToken cancellationToken = default)
    {
        await _settings.UpsertOwnerIdAsync(clanId, ownerId, cancellationToken).ConfigureAwait(false);
        Invalidate(clanId);
    }

    public async Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
    {
        await _settings.UpsertDjRoleIdAsync(clanId, roleId, cancellationToken).ConfigureAwait(false);
        Invalidate(clanId);
    }

    public async Task SetDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default)
    {
        await _settings.UpsertDefaultStreamChannelAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
        Invalidate(clanId);
    }

    public async Task SetVoteSkipAsync(
        long clanId,
        bool enabled,
        float? ratio = null,
        CancellationToken cancellationToken = default)
    {
        await _settings.UpsertVoteSkipAsync(clanId, enabled, ratio, cancellationToken).ConfigureAwait(false);
        Invalidate(clanId);
    }

    private void Invalidate(long clanId) => _cache.Remove(CacheKey(clanId));

    private static string CacheKey(long clanId) => $"clan-settings:{clanId}";
}
