using Mezube.Infrastructure.Persistence;

namespace Mezube.Application;

public sealed class ClanSettingsService : IClanSettingsService
{
    private readonly IClanSettingsRepository _settings;

    public ClanSettingsService(IClanSettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var row = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        return row?.DjRoleId is long id && id != 0 ? id : null;
    }

    public Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
        => _settings.UpsertDjRoleIdAsync(clanId, roleId, cancellationToken);
}
