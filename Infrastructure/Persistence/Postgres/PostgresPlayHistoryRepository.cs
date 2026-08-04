namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresPlayHistoryRepository : IPlayHistoryRepository
{
    private readonly PostgresDbConnectionFactory _db;

    public PostgresPlayHistoryRepository(PostgresDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<long> StartAsync(
        long clanId,
        long trackId,
        string mode,
        long channelId,
        long? requestedByUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO play_history (clan_id, track_id, mode, channel_id, requested_by_user_id, started_at)
            VALUES (@clan_id, @track_id, @mode, @channel_id, @requested_by, now())
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("track_id", trackId);
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("channel_id", channelId);
        cmd.Parameters.AddWithValue("requested_by", (object?)requestedByUserId ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> CloseAsync(long historyId, string endReason, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE play_history
            SET ended_at = now(), end_reason = @reason
            WHERE id = @id AND ended_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("id", historyId);
        cmd.Parameters.AddWithValue("reason", endReason);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task CloseOpenForClanAsync(
        long clanId,
        string endReason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE play_history
            SET ended_at = now(), end_reason = @reason
            WHERE clan_id = @clan_id AND ended_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("reason", endReason);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
