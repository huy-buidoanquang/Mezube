namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresCommandChannelRepository : ICommandChannelRepository
{
    private readonly PostgresDbConnectionFactory _db;
    private readonly IClanSettingsRepository _settings;

    public PostgresCommandChannelRepository(PostgresDbConnectionFactory db, IClanSettingsRepository settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<IReadOnlyList<long>> ListAsync(long clanId, CancellationToken cancellationToken = default)
    {
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

        return list;
    }

    public async Task<bool> IsAllowedAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*)::int FROM clan_command_channels WHERE clan_id = @clan_id;";
        count.Parameters.AddWithValue("clan_id", clanId);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (total == 0)
        {
            return true;
        }

        await using var check = connection.CreateCommand();
        check.CommandText =
            """
            SELECT 1 FROM clan_command_channels
            WHERE clan_id = @clan_id AND channel_id = @channel_id
            LIMIT 1;
            """;
        check.Parameters.AddWithValue("clan_id", clanId);
        check.Parameters.AddWithValue("channel_id", channelId);
        return await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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
    }

    public async Task ClearAsync(long clanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM clan_command_channels WHERE clan_id = @clan_id;";
        cmd.Parameters.AddWithValue("clan_id", clanId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
