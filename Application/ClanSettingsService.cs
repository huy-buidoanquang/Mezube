using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence;

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
    private readonly IClanSettingsRepository _settings;

    public ClanSettingsService(IClanSettingsRepository settings)
    {
        _settings = settings;
    }

    public Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
        => _settings.TryGetAsync(clanId, cancellationToken);

    public async Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var row = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        return row?.DjRoleId is long id && id != 0 ? id : null;
    }

    public async Task<long?> GetOwnerIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var row = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        return row?.OwnerId is long id && id != 0 ? id : null;
    }

    public Task EnsureOwnerAsync(long clanId, long ownerId, CancellationToken cancellationToken = default)
        => _settings.UpsertOwnerIdAsync(clanId, ownerId, cancellationToken);

    public Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
        => _settings.UpsertDjRoleIdAsync(clanId, roleId, cancellationToken);

    public Task SetDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default)
        => _settings.UpsertDefaultStreamChannelAsync(clanId, channelId, cancellationToken);

    public Task SetVoteSkipAsync(
        long clanId,
        bool enabled,
        float? ratio = null,
        CancellationToken cancellationToken = default)
        => _settings.UpsertVoteSkipAsync(clanId, enabled, ratio, cancellationToken);
}
