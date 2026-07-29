namespace Mezube.Domain.Persistence;

public interface IClanSettingsStore
{
    Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default);
}
