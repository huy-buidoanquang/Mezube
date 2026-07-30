using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence;

public interface IClanSettingsRepository
{
    Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default);

    Task UpsertDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default);
}
