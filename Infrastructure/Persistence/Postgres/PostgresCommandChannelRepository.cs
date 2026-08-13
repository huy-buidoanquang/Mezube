using Microsoft.Extensions.Caching.Memory;

namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresCommandChannelRepository : ICommandChannelRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly PostgresDbConnectionFactory _db;
    private readonly IClanSettingsRepository _settings;
    private readonly IMemoryCache _cache;

    public PostgresCommandChannelRepository(
        PostgresDbConnectionFactory db,
        IClanSettingsRepository settings,
        IMemoryCache cache)
    {
        _db = db;
        _settings = settings;
        _cache = cache;
    }

    public async Task<IReadOnlyList<long>> ListAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var key = ListKey(clanId);
        if (_cache.TryGetValue(key, out IReadOnlyList<long>? cached) && cached is not null)
        {
            return cached;
        }

        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT channel_id
            FROM clan_command_channels
            WHERE clan_id = @clan_id
            ORDER BY channel_id;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        var list = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(reader.GetInt64(0));
        }

        _cache.Set(key, (IReadOnlyList<long>)list, CacheTtl);
        return list;
    }

    public async Task<bool> IsAllowedAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
    {
        var key = AllowKey(clanId, channelId);
        if (_cache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText =
            """
            SELECT (
                NOT EXISTS (SELECT 1 FROM clan_command_channels WHERE clan_id = @clan_id)
                OR EXISTS (
                    SELECT 1 FROM clan_command_channels
                    WHERE clan_id = @clan_id AND channel_id = @channel_id
                )
            );
            """;
        count.Parameters.AddWithValue("clan_id", clanId);
        count.Parameters.AddWithValue("channel_id", channelId);
        var allowedRaw = await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var allowed = allowedRaw is true || Convert.ToBoolean(allowedRaw);
        _cache.Set(key, allowed, CacheTtl);
        return allowed;
    }

    public async Task AddAsync(long clanId, long channelId, long? addedBy, CancellationToken cancellationToken = default)
    {
        await _settings.EnsureClanAsync(clanId, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clan_command_channels (clan_id, channel_id, added_by)
            VALUES (@clan_id, @channel_id, @added_by)
            ON CONFLICT (clan_id, channel_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("channel_id", channelId);
        cmd.Parameters.AddWithValue("added_by", (object?)addedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        BumpEpoch(clanId);
    }

    public async Task RemoveAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM clan_command_channels
            WHERE clan_id = @clan_id AND channel_id = @channel_id;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("channel_id", channelId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        BumpEpoch(clanId);
    }

    public async Task ClearAsync(long clanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM clan_command_channels WHERE clan_id = @clan_id;";
        cmd.Parameters.AddWithValue("clan_id", clanId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        BumpEpoch(clanId);
    }

    private string AllowKey(long clanId, long channelId) => $"cmd-allow:{clanId}:{channelId}:{ReadEpoch(clanId)}";

    private string ListKey(long clanId) => $"cmd-list:{clanId}:{ReadEpoch(clanId)}";

    private long ReadEpoch(long clanId)
        => _cache.TryGetValue(EpochKey(clanId), out long epoch) ? epoch : 0;

    private void BumpEpoch(long clanId)
        => _cache.Set(EpochKey(clanId), ReadEpoch(clanId) + 1);

    private static string EpochKey(long clanId) => $"cmd-ch-epoch:{clanId}";
}
