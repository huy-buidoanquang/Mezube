using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching;
using Mezube.Application;
using Mezube.Infrastructure.Caching;
using Mezube.Infrastructure.Caching.Snapshots;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Redis;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>
/// JMusicBot-style DJ gate + vote-skip (evaluate on each new vote with live voice count).
/// </summary>
public sealed class PlaybackAccess
{
    private readonly IClanSettingsService _settings;
    private readonly ICommandChannelRepository _commandChannels;
    private readonly IVoteSkipStore _votes;
    private readonly BindStore _binds;
    private readonly IEntitySnapshotStore _snapshots;
    private readonly MezonSnapshotKeyFactory _snapshotKeys;
    private readonly ILogger<PlaybackAccess> _logger;

    public PlaybackAccess(
        IClanSettingsService settings,
        ICommandChannelRepository commandChannels,
        IVoteSkipStore votes,
        BindStore binds,
        IEntitySnapshotStore snapshots,
        MezonSnapshotKeyFactory snapshotKeys,
        ILogger<PlaybackAccess> logger)
    {
        _settings = settings;
        _commandChannels = commandChannels;
        _votes = votes;
        _binds = binds;
        _snapshots = snapshots;
        _snapshotKeys = snapshotKeys;
        _logger = logger;
    }

    public bool CanQueue() => true;

    public Task<bool> IsCommandChannelAllowedAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
        => _commandChannels.IsAllowedAsync(clanId, channelId, cancellationToken);

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

    public Task<bool> IsDjOrOwnerAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
        => IsDjOrCreatorAsync(client, clanId, userId, cancellationToken);

    public Task<bool> IsClanOwnerAsync(
        MezonClient client,
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
        => IsClanCreatorAsync(client, clanId, userId, cancellationToken);

    public Task<long?> GetDjRoleIdAsync(long clanId, CancellationToken cancellationToken = default)
        => _settings.GetDjRoleIdAsync(clanId, cancellationToken);

    public Task SetDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
        => _settings.SetDjRoleIdAsync(clanId, roleId, cancellationToken);

    /// <summary>
    /// App-owned hydrate: fetch DJ role membership once (not from events / not on every connect for all roles).
    /// Writes L2 snapshot so later !stop/!skip can avoid REST when warm.
    /// </summary>
    public async Task WarmDjRoleMembershipAsync(
        MezonClient client,
        long clanId,
        long roleId,
        CancellationToken cancellationToken = default)
    {
        if (roleId == 0)
        {
            return;
        }

        try
        {
            var clan = await client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
            string? title = null;
            string? color = null;
            try
            {
                var roles = await clan.ListRolesAsync(limit: 100).ConfigureAwait(false);
                foreach (var role in roles.Roles.Roles)
                {
                    if (role.Id != roleId)
                    {
                        continue;
                    }

                    title = string.IsNullOrWhiteSpace(role.Title) ? null : role.Title;
                    color = string.IsNullOrWhiteSpace(role.Color) ? null : role.Color;
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ListRoles for DJ warm failed clan={ClanId} role={RoleId}", clanId, roleId);
            }

            var members = new HashSet<long>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var users = await clan.ListRoleUsersAsync(roleId, limit: 200, cursor: cursor)
                    .ConfigureAwait(false);
                foreach (var user in users.RoleUsers)
                {
                    if (user.Id != 0)
                    {
                        members.Add(user.Id);
                    }
                }

                if (string.IsNullOrEmpty(users.Cursor) || users.Cursor == cursor)
                {
                    break;
                }

                cursor = users.Cursor;
            }

            var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _snapshots.SetAsync(
                    _snapshotKeys.Role(roleId),
                    new RoleSnapshotDto
                    {
                        Id = roleId,
                        ClanId = clanId,
                        Title = title,
                        Color = color,
                        MemberIds = members.ToArray(),
                        Revision = revision,
                    },
                    new CacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                        Revision = revision,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Warmed DJ role L2 clan={ClanId} role={RoleId} members={Count}",
                clanId,
                roleId,
                members.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DJ role warm failed clan={ClanId} role={RoleId}", clanId, roleId);
        }
    }

    /// <summary>
    /// Records a vote and returns whether the threshold is met (caller should Advance CAS).
    /// </summary>
    public async Task<(bool ThresholdMet, long Votes, long Needed, string? Error)> TryVoteSkipAsync(
        long clanId,
        long playHistoryId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (settings is null || !settings.VoteSkipEnabled)
        {
            return (false, 0, 0, "Vote-skip is disabled for this clan.");
        }

        var (votes, _) = await _votes.AddVoteAsync(clanId, playHistoryId, userId, cancellationToken)
            .ConfigureAwait(false);
        var liveCount = await _binds.CountVoiceUsersAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (liveCount < 1)
        {
            liveCount = 1;
        }

        var needed = Math.Max(1, (long)Math.Ceiling(liveCount * settings.VoteSkipRatio));
        return (votes >= needed, votes, needed, null);
    }

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
            var members = await TryResolveDjRoleMembersAsync(client, clanId, roleId, cancellationToken).ConfigureAwait(false);
            if (members.Contains(userId))
            {
                return true;
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
        var ownerId = await TryResolveClanOwnerAsync(client, clanId, cancellationToken).ConfigureAwait(false);
        return ownerId is long owner && owner == userId;
    }

    private async Task<HashSet<long>> TryResolveDjRoleMembersAsync(
        MezonClient client,
        long clanId,
        long roleId,
        CancellationToken cancellationToken)
    {
        // L1 role cache first.
        if (client.Roles.TryGet(roleId, out var cachedRole) && cachedRole.MemberIds.Count > 0)
        {
            return cachedRole.MemberIds.ToHashSet();
        }

        if (client.Clans.TryGet(clanId, out var cachedClan)
            && cachedClan.Roles.TryGet(roleId, out var clanRole)
            && clanRole.MemberIds.Count > 0)
        {
            return clanRole.MemberIds.ToHashSet();
        }

        // L2 snapshot next.
        try
        {
            var snapshot = await _snapshots.GetAsync<RoleSnapshotDto>(_snapshotKeys.Role(roleId), cancellationToken)
                .ConfigureAwait(false);
            if (snapshot?.MemberIds is { Length: > 0 })
            {
                return snapshot.MemberIds.ToHashSet();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "L2 role membership lookup failed clan={ClanId} role={RoleId}", clanId, roleId);
        }

        // Source of truth last.
        var clan = await client.GetClanAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (clan.Roles.TryGet(roleId, out var afterFetch) && afterFetch.MemberIds.Count > 0)
        {
            return afterFetch.MemberIds.ToHashSet();
        }

        string? cursor = null;
        var members = new HashSet<long>();
        for (var page = 0; page < 20; page++)
        {
            var users = await clan.ListRoleUsersAsync(roleId, limit: 200, cursor: cursor)
                .ConfigureAwait(false);
            foreach (var user in users.RoleUsers)
            {
                if (user.Id != 0)
                {
                    members.Add(user.Id);
                }
            }

            if (string.IsNullOrEmpty(users.Cursor) || users.Cursor == cursor)
            {
                break;
            }

            cursor = users.Cursor;
        }

        if (members.Count > 0)
        {
            _ = WarmDjRoleMembershipAsync(client, clanId, roleId, CancellationToken.None);
            return members;
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
                if (user.Id != 0)
                {
                    members.Add(user.Id);
                }
            }
        }

        if (members.Count > 0)
        {
            _ = WarmDjRoleMembershipAsync(client, clanId, roleId, CancellationToken.None);
        }

        return members;
    }

    private async Task<long?> TryResolveClanOwnerAsync(
        MezonClient client,
        long clanId,
        CancellationToken cancellationToken)
    {
        // Nearest cache: persisted owner row.
        var cached = await _settings.GetOwnerIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (cached is long ownerId && ownerId != 0)
        {
            return ownerId;
        }

        // L1 cache without REST.
        if (client.Clans.TryGet(clanId, out var l1Clan) && l1Clan.CreatorId != 0)
        {
            await _settings.EnsureOwnerAsync(clanId, l1Clan.CreatorId, cancellationToken).ConfigureAwait(false);
            return l1Clan.CreatorId;
        }

        // L2 snapshot before REST.
        try
        {
            var snapshot = await _snapshots.GetAsync<ClanSnapshotDto>(_snapshotKeys.Clan(clanId), cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is { CreatorId: not 0 })
            {
                await _settings.EnsureOwnerAsync(clanId, snapshot.CreatorId, cancellationToken).ConfigureAwait(false);
                return snapshot.CreatorId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "L2 clan owner lookup failed clan={ClanId}", clanId);
        }

        // Source of truth last. Avoid GetClanAsync when L1 already has a stub.
        try
        {
            var list = await client.ListClanDescsAsync(new Mezon.Net.Models.ListClanDescParams())
                .ConfigureAwait(false);
            for (var i = 0; i < list.Clandesc.Count; i++)
            {
                var desc = list.Clandesc[i];
                if (desc.ClanId != clanId || desc.CreatorId == 0)
                {
                    continue;
                }

                await _settings.EnsureOwnerAsync(clanId, desc.CreatorId, cancellationToken).ConfigureAwait(false);
                return desc.CreatorId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clan creator check failed clan={ClanId}", clanId);
        }

        return null;
    }
}
