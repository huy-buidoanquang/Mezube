using System.Globalization;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching;
using Mezon.Net.Sdk.Entities;
using Mezube.Infrastructure.Caching.Snapshots;
using Microsoft.Extensions.Logging;

namespace Mezube.Infrastructure.Caching;

/// <summary>
/// App-owned L2 glue: after Sdk L1 mutates from events, persist DTO snapshots in the background.
/// Never calls REST. Invalidation drops L1 entries.
/// </summary>
public sealed class MezonEntityCacheBridge : IAsyncDisposable
{
    private const int RoleEventStatusDeleted = 3;

    private readonly IEntitySnapshotStore _store;
    private readonly ICacheInvalidationListener _invalidation;
    private readonly MezonSnapshotKeyFactory _keys;
    private readonly ILogger<MezonEntityCacheBridge> _logger;
    private readonly CacheEntryOptions _defaultOptions;
    private MezonClient? _client;
    private int _attached;

    public MezonEntityCacheBridge(
        IEntitySnapshotStore store,
        ICacheInvalidationListener invalidation,
        MezonSnapshotKeyFactory keys,
        ILogger<MezonEntityCacheBridge> logger)
    {
        _store = store;
        _invalidation = invalidation;
        _keys = keys;
        _logger = logger;
        _defaultOptions = new CacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
        };
    }

    public async Task AttachAsync(MezonClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (Interlocked.Exchange(ref _attached, 1) == 1)
        {
            return;
        }

        _client = client;
        _invalidation.Invalidated += OnInvalidated;

        client.ChannelCreated += OnChannelCreatedAsync;
        client.ChannelUpdated += OnChannelUpdatedAsync;
        client.ChannelDeleted += OnChannelDeletedAsync;
        client.UserChannelAdded += OnUserChannelAddedAsync;
        client.UserChannelRemoved += OnUserChannelRemovedAsync;
        client.RoleChanged += OnRoleChangedAsync;
        client.RoleAssigned += OnRoleAssignedAsync;
        client.ClanJoined += OnClanJoinedAsync;

        await _invalidation.StartListeningAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Mezon L2 entity cache bridge attached (env={Env} account={AccountId})",
            _keys.EnvironmentName,
            _keys.AccountId);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _attached, 0) == 0)
        {
            return;
        }

        _invalidation.Invalidated -= OnInvalidated;
        if (_client is { } client)
        {
            client.ChannelCreated -= OnChannelCreatedAsync;
            client.ChannelUpdated -= OnChannelUpdatedAsync;
            client.ChannelDeleted -= OnChannelDeletedAsync;
            client.UserChannelAdded -= OnUserChannelAddedAsync;
            client.UserChannelRemoved -= OnUserChannelRemovedAsync;
            client.RoleChanged -= OnRoleChangedAsync;
            client.RoleAssigned -= OnRoleAssignedAsync;
            client.ClanJoined -= OnClanJoinedAsync;
        }

        await _invalidation.DisposeAsync().ConfigureAwait(false);
    }

    private void OnInvalidated(object? sender, CacheKey key)
    {
        var client = _client;
        if (client is null || !long.TryParse(key.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return;
        }

        switch (key.EntityType)
        {
            case MezonSnapshotKeyFactory.EntityClan:
                client.Clans.Remove(id);
                break;
            case MezonSnapshotKeyFactory.EntityChannel:
                client.Channels.Remove(id);
                break;
            case MezonSnapshotKeyFactory.EntityRole:
                client.Roles.Remove(id);
                break;
            case MezonSnapshotKeyFactory.EntityUser:
                client.Users.Remove(id);
                break;
        }
    }

    private Task OnChannelCreatedAsync(ChannelCreatedEventEventData evt)
    {
        ChannelCreatedEventResponse data = evt;
        _ = PersistAsync(_keys.Channel(data.ChannelId), new ChannelSnapshotDto
        {
            Id = data.ChannelId,
            ClanId = data.ClanId,
            ParentId = data.ParentId,
            CategoryId = data.CategoryId,
            Type = data.ChannelType,
            IsPrivate = data.ChannelPrivate != 0,
            Name = NullIfEmpty(data.ChannelLabel),
            Revision = NextRevision(),
        });
        return Task.CompletedTask;
    }

    private Task OnChannelUpdatedAsync(ChannelUpdatedEventEventData evt)
    {
        ChannelUpdatedEventResponse data = evt;
        if (data.ChannelId == 0)
        {
            return Task.CompletedTask;
        }

        // Prefer L1 if Sdk already upserted a richer channel.
        if (_client is not null && _client.Channels.TryGet(data.ChannelId, out var channel))
        {
            _ = PersistChannelEntityAsync(channel);
            return Task.CompletedTask;
        }

        _ = PersistAsync(_keys.Channel(data.ChannelId), new ChannelSnapshotDto
        {
            Id = data.ChannelId,
            ClanId = data.ClanId,
            ParentId = data.ParentId,
            CategoryId = data.CategoryId,
            Type = data.ChannelType,
            IsPrivate = data.ChannelPrivate,
            Name = NullIfEmpty(data.ChannelLabel),
            Revision = NextRevision(),
        });
        return Task.CompletedTask;
    }

    private Task OnChannelDeletedAsync(ChannelDeletedEventEventData evt)
    {
        ChannelDeletedEventResponse data = evt;
        _ = InvalidateAsync(_keys.Channel(data.ChannelId));
        return Task.CompletedTask;
    }

    private Task OnUserChannelAddedAsync(UserChannelAddedEventData evt)
    {
        UserChannelAddedResponse data = evt;
        var desc = data.ChannelDesc;
        if (desc.ChannelId == 0)
        {
            return Task.CompletedTask;
        }

        _ = PersistAsync(_keys.Channel(desc.ChannelId), new ChannelSnapshotDto
        {
            Id = desc.ChannelId,
            ClanId = desc.ClanId,
            ParentId = desc.ParentId,
            CategoryId = desc.CategoryId,
            Type = desc.Type,
            IsPrivate = desc.ChannelPrivate != 0,
            Name = NullIfEmpty(desc.ChannelLabel),
            MeetingCode = NullIfEmpty(desc.MeetingCode),
            Revision = NextRevision(),
        });
        return Task.CompletedTask;
    }

    private Task OnUserChannelRemovedAsync(UserChannelRemovedEventData evt)
    {
        UserChannelRemovedResponse data = evt;
        var botId = _client?.BotId ?? 0;
        var botRemoved = botId != 0 && data.UserIds.Any(id => id == botId);
        if (botRemoved && data.ChannelId != 0)
        {
            _ = InvalidateAsync(_keys.Channel(data.ChannelId));
        }

        return Task.CompletedTask;
    }

    private Task OnRoleChangedAsync(RoleEventEventData evt)
    {
        RoleEventResponse data = evt;
        var role = data.Role;
        if (role.Id == 0)
        {
            return Task.CompletedTask;
        }

        if (data.Status == RoleEventStatusDeleted)
        {
            _ = InvalidateAsync(_keys.Role(role.Id));
            return Task.CompletedTask;
        }

        _ = PersistRoleFromL1OrEventAsync(role.Id, role.ClanId, role.Title, role.Color, data.UserAddIds, data.UserRemoveIds);
        return Task.CompletedTask;
    }

    private Task OnRoleAssignedAsync(RoleAssignedEventEventData evt)
    {
        RoleAssignedEventResponse data = evt;
        if (data.RoleId == 0)
        {
            return Task.CompletedTask;
        }

        long.TryParse(data.ClanId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clanId);
        _ = PersistRoleFromL1OrEventAsync(
            data.RoleId,
            clanId,
            title: null,
            color: null,
            data.UserIdsAssigned,
            data.UserIdsRemoved);
        return Task.CompletedTask;
    }

    private Task OnClanJoinedAsync(ClanJoinEventData evt)
    {
        ClanJoinResponse joined = evt;
        if (_client is not null
            && _client.Clans.TryGet(joined.ClanId, out var clan)
            && clan.CreatorId != 0)
        {
            _ = PersistAsync(_keys.Clan(clan.Id), new ClanSnapshotDto
            {
                ClanId = clan.Id,
                CreatorId = clan.CreatorId,
                Name = clan.Name,
                Revision = NextRevision(),
            });
        }

        return Task.CompletedTask;
    }

    private async Task PersistRoleFromL1OrEventAsync(
        long roleId,
        long clanId,
        string? title,
        string? color,
        ProtoListView<long> addIds,
        ProtoListView<long> removeIds)
    {
        try
        {
            var key = _keys.Role(roleId);
            var members = new HashSet<long>();

            if (_client is not null && _client.Roles.TryGet(roleId, out var live))
            {
                foreach (var id in live.MemberIds)
                {
                    members.Add(id);
                }

                clanId = live.ClanId != 0 ? live.ClanId : clanId;
                title ??= live.Title;
                color ??= live.Color;
            }
            else
            {
                var existing = await _store.GetAsync<RoleSnapshotDto>(key).ConfigureAwait(false);
                if (existing?.MemberIds is { Length: > 0 })
                {
                    foreach (var id in existing.MemberIds)
                    {
                        members.Add(id);
                    }

                    title ??= existing.Title;
                    color ??= existing.Color;
                    if (clanId == 0)
                    {
                        clanId = existing.ClanId;
                    }
                }
            }

            foreach (var id in addIds)
            {
                if (id != 0)
                {
                    members.Add(id);
                }
            }

            foreach (var id in removeIds)
            {
                members.Remove(id);
            }

            var dto = new RoleSnapshotDto
            {
                Id = roleId,
                ClanId = clanId,
                Title = NullIfEmpty(title),
                Color = NullIfEmpty(color),
                MemberIds = members.ToArray(),
                Revision = NextRevision(),
            };
            await SetAsync(key, dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "L2 role snapshot persist failed role={RoleId}", roleId);
        }
    }

    private async Task PersistChannelEntityAsync(Channel channel)
    {
        try
        {
            await SetAsync(_keys.Channel(channel.Id), new ChannelSnapshotDto
            {
                Id = channel.Id,
                ClanId = channel.ClanId,
                ParentId = channel.ParentId,
                CategoryId = channel.CategoryId,
                Type = channel.Type,
                IsPrivate = channel.IsPrivate,
                Name = channel.Name,
                MeetingCode = NullIfEmpty(channel.MeetingCode),
                Revision = NextRevision(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "L2 channel snapshot persist failed channel={ChannelId}", channel.Id);
        }
    }

    private Task PersistAsync<TDto>(CacheKey key, TDto dto) where TDto : class
        => ObserveFaultAsync(SetAsync(key, dto), $"L2 Set {key.EntityType}/{key.Id}");

    private Task InvalidateAsync(CacheKey key)
        => ObserveFaultAsync(_store.InvalidateAsync(key).AsTask(), $"L2 Invalidate {key.EntityType}/{key.Id}");

    private async Task SetAsync<TDto>(CacheKey key, TDto dto) where TDto : class
    {
        var revision = dto switch
        {
            ClanSnapshotDto c => c.Revision,
            ChannelSnapshotDto c => c.Revision,
            RoleSnapshotDto r => r.Revision,
            UserSnapshotDto u => u.Revision,
            _ => NextRevision(),
        };
        var options = new CacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _defaultOptions.AbsoluteExpirationRelativeToNow,
            Revision = revision,
        };
        await _store.SetAsync(key, dto, options).ConfigureAwait(false);
    }

    private async Task ObserveFaultAsync(Task task, string name)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Name} failed", name);
        }
    }

    private static long NextRevision() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
