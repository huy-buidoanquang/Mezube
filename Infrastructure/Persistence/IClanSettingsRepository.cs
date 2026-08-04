using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence;

public interface IClanSettingsRepository
{
    Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default);

    Task EnsureClanAsync(long clanId, long? ownerId = null, CancellationToken cancellationToken = default);

    Task UpsertOwnerIdAsync(long clanId, long ownerId, CancellationToken cancellationToken = default);

    Task UpsertDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default);

    Task UpsertDefaultStreamChannelAsync(long clanId, long? channelId, CancellationToken cancellationToken = default);

    Task UpsertVoteSkipAsync(
        long clanId,
        bool enabled,
        float? ratio = null,
        CancellationToken cancellationToken = default);
}
