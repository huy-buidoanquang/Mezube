using Mezon.Net.Sdk;
using Mezube.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>
/// JMusicBot-style DJ gate: everyone can queue; requester can skip own track;
/// DJ role / clan creator can force-skip and stop. Vote-skip is deferred.
/// </summary>
public sealed class PlaybackAccess
{
    private readonly IClanSettingsStore _settings;
    private readonly ILogger<PlaybackAccess> _logger;

    public PlaybackAccess(IClanSettingsStore settings, ILogger<PlaybackAccess> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool CanQueue() => true;

    public async Task<bool> CanSkipAsync(
        MezonClient client,
        long clanId,
        long userId,
        long? trackRequesterUserId,
        CancellationToken cancellationToken = default)
    {
        if (trackRequesterUserId is long requester && requester == userId)
        {
            return true;
        }

        return await IsDjOrCreatorAsync(client, clanId, userId, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> CanStopAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
        => IsDjOrCreatorAsync(client, clanId, userId, cancellationToken);

    public Task<bool> CanConfigureDjAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
        => IsClanCreatorAsync(client, clanId, userId, cancellationToken);

    public Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
        => _settings.GetDjRoleIdAsync(clanId, cancellationToken);

    public Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
        => _settings.SetDjRoleIdAsync(clanId, roleId, cancellationToken);

    private async Task<bool> IsDjOrCreatorAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (await IsClanCreatorAsync(client, clanId, userId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var djRoleId = await _settings.GetDjRoleIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (djRoleId is not long roleId)
        {
            return false;
        }

        try
        {
            var clan = await client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
            var users = await clan.ListRoleUsersAsync(roleId, limit: 200, options: null).ConfigureAwait(false);
            foreach (var user in users.RoleUsers)
            {
                if (user.Id == userId)
                {
                    return true;
                }
            }

            var roles = await clan.ListRolesAsync(limit: 100).ConfigureAwait(false);
            foreach (var role in roles.Roles.Roles)
            {
                if (role.Id != roleId)
                {
                    continue;
                }

                foreach (var user in role.RoleUserList.RoleUsers)
                {
                    if (user.Id == userId)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DJ role membership check failed clan={ClanId} role={RoleId}", clanId, roleId);
        }

        return false;
    }

    private async Task<bool> IsClanCreatorAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            var clan = await client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
            return clan.CreatorId != 0 && clan.CreatorId == userId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clan creator check failed clan={ClanId}", clanId);
            return false;
        }
    }
}
