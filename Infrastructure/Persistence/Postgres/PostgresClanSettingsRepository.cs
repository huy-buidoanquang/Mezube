using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresClanSettingsRepository : IClanSettingsRepository
{
    private readonly PostgresDbConnectionFactory _db;

    public PostgresClanSettingsRepository(PostgresDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT clan_id, owner_id, dj_role_id, default_stream_channel_id,
                   vote_skip_enabled, vote_skip_ratio, updated_at
            FROM clan_settings
            WHERE clan_id = @clan_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ClanSettingsEntity
        {
            ClanId = reader.GetInt64(0),
            OwnerId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            DjRoleId = reader.IsDBNull(2) ? null : NullIfZero(reader.GetInt64(2)),
            DefaultStreamChannelId = reader.IsDBNull(3) ? null : NullIfZero(reader.GetInt64(3)),
            VoteSkipEnabled = reader.GetBoolean(4),
            VoteSkipRatio = reader.GetFloat(5),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        };
    }

    public async Task EnsureClanAsync(long clanId, long? ownerId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clan_settings (clan_id, owner_id, updated_at)
            VALUES (@clan_id, @owner_id, now())
            ON CONFLICT (clan_id) DO UPDATE SET
                owner_id = COALESCE(EXCLUDED.owner_id, clan_settings.owner_id),
                updated_at = CASE
                    WHEN EXCLUDED.owner_id IS NOT NULL AND clan_settings.owner_id IS DISTINCT FROM EXCLUDED.owner_id
                    THEN now()
                    ELSE clan_settings.updated_at
                END;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("owner_id", (object?)ownerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertOwnerIdAsync(long clanId, long ownerId, CancellationToken cancellationToken = default)
    {
        await EnsureClanAsync(clanId, ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clan_settings (clan_id, dj_role_id, updated_at)
            VALUES (@clan_id, @dj_role_id, now())
            ON CONFLICT (clan_id) DO UPDATE SET
                dj_role_id = EXCLUDED.dj_role_id,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("dj_role_id", (object?)roleId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertDefaultStreamChannelAsync(
        long clanId,
        long? channelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clan_settings (clan_id, default_stream_channel_id, updated_at)
            VALUES (@clan_id, @channel_id, now())
            ON CONFLICT (clan_id) DO UPDATE SET
                default_stream_channel_id = EXCLUDED.default_stream_channel_id,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("channel_id", (object?)channelId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertVoteSkipAsync(
        long clanId,
        bool enabled,
        float? ratio = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clan_settings (clan_id, vote_skip_enabled, vote_skip_ratio, updated_at)
            VALUES (@clan_id, @enabled, COALESCE(@ratio, 0.5), now())
            ON CONFLICT (clan_id) DO UPDATE SET
                vote_skip_enabled = EXCLUDED.vote_skip_enabled,
                vote_skip_ratio = COALESCE(@ratio, clan_settings.vote_skip_ratio),
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("enabled", enabled);
        cmd.Parameters.AddWithValue("ratio", (object?)ratio ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long? NullIfZero(long value) => value == 0 ? null : value;
}
