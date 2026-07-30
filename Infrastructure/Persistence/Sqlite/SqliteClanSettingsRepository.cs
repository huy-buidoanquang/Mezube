using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence.Sqlite;

public sealed class SqliteClanSettingsRepository : IClanSettingsRepository
{
    private readonly SqliteDbConnectionFactory _db;

    public SqliteClanSettingsRepository(SqliteDbConnectionFactory db)
    {
        _db = db;
    }

    public Task<ClanSettingsEntity?> TryGetAsync(long clanId, CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT clan_id, dj_role_id, updated_at
                FROM clan_settings
                WHERE clan_id = $clan_id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$clan_id", clanId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            long? djRoleId = null;
            if (!reader.IsDBNull(1))
            {
                var value = reader.GetInt64(1);
                if (value != 0)
                {
                    djRoleId = value;
                }
            }

            DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
            if (!reader.IsDBNull(2)
                && DateTimeOffset.TryParse(reader.GetString(2), out var parsed))
            {
                updatedAt = parsed;
            }

            return new ClanSettingsEntity
            {
                ClanId = reader.GetInt64(0),
                DjRoleId = djRoleId,
                UpdatedAt = updatedAt,
            };
        }, cancellationToken);

    public Task UpsertDjRoleIdAsync(long clanId, long? roleId, CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            if (roleId is null)
            {
                cmd.CommandText =
                    """
                    INSERT INTO clan_settings (clan_id, dj_role_id, updated_at)
                    VALUES ($clan_id, NULL, $now)
                    ON CONFLICT(clan_id) DO UPDATE SET
                        dj_role_id = NULL,
                        updated_at = excluded.updated_at;
                    """;
            }
            else
            {
                cmd.CommandText =
                    """
                    INSERT INTO clan_settings (clan_id, dj_role_id, updated_at)
                    VALUES ($clan_id, $dj_role_id, $now)
                    ON CONFLICT(clan_id) DO UPDATE SET
                        dj_role_id = excluded.dj_role_id,
                        updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("$dj_role_id", roleId.Value);
            }

            cmd.Parameters.AddWithValue("$clan_id", clanId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
}
