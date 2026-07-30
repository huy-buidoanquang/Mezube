namespace Mezube.Application;

public interface IClanSettingsService
{
    Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default);
}
