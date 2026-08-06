using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresPlaylistRepository : IPlaylistRepository
{
    private readonly PostgresDbConnectionFactory _db;

    public PostgresPlaylistRepository(PostgresDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<PlaylistEntity?> TryGetByNameAsync(
        long clanId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, clan_id, name, is_default, created_by, created_at, updated_at
            FROM playlists
            WHERE clan_id = @clan_id AND lower(name) = lower(@name)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("name", name);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPlaylist(reader)
            : null;
    }

    public async Task<PlaylistEntity?> TryGetDefaultAsync(long clanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, clan_id, name, is_default, created_by, created_at, updated_at
            FROM playlists
            WHERE clan_id = @clan_id AND is_default
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPlaylist(reader)
            : null;
    }

    public async Task<IReadOnlyList<long>> ListDefaultClanIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT clan_id
            FROM playlists
            WHERE is_default
            ORDER BY clan_id;
            """;
        var list = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    public async Task<IReadOnlyList<PlaylistEntity>> ListAsync(long clanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, clan_id, name, is_default, created_by, created_at, updated_at
            FROM playlists
            WHERE clan_id = @clan_id
            ORDER BY lower(name);
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        var list = new List<PlaylistEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadPlaylist(reader));
        }

        return list;
    }

    public async Task<PlaylistEntity> CreateAsync(
        long clanId,
        string name,
        long? createdBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO playlists (clan_id, name, created_by)
            VALUES (@clan_id, @name, @created_by)
            RETURNING id, clan_id, name, is_default, created_by, created_at, updated_at;
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("name", name.Trim());
        cmd.Parameters.AddWithValue("created_by", (object?)createdBy ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to create playlist.");
        }

        return ReadPlaylist(reader);
    }

    public async Task<bool> DeleteAsync(long clanId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM playlists
            WHERE clan_id = @clan_id AND lower(name) = lower(@name);
            """;
        cmd.Parameters.AddWithValue("clan_id", clanId);
        cmd.Parameters.AddWithValue("name", name);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task SetDefaultAsync(long clanId, long? playlistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText =
                    """
                    UPDATE playlists
                    SET is_default = FALSE, updated_at = now()
                    WHERE clan_id = @clan_id AND is_default;
                    """;
                clear.Parameters.AddWithValue("clan_id", clanId);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (playlistId is long id)
            {
                await using var set = connection.CreateCommand();
                set.Transaction = tx;
                set.CommandText =
                    """
                    UPDATE playlists
                    SET is_default = TRUE, updated_at = now()
                    WHERE id = @id AND clan_id = @clan_id;
                    """;
                set.Parameters.AddWithValue("id", id);
                set.Parameters.AddWithValue("clan_id", clanId);
                var updated = await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (updated == 0)
                {
                    throw new InvalidOperationException("Playlist not found for clan.");
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AddItemAsync(
        long playlistId,
        long trackId,
        long? addedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO playlist_items (playlist_id, position, track_id, added_by)
            VALUES (
                @playlist_id,
                COALESCE((SELECT MAX(position) + 1 FROM playlist_items WHERE playlist_id = @playlist_id), 0),
                @track_id,
                @added_by
            );
            UPDATE playlists SET updated_at = now() WHERE id = @playlist_id;
            """;
        cmd.Parameters.AddWithValue("playlist_id", playlistId);
        cmd.Parameters.AddWithValue("track_id", trackId);
        cmd.Parameters.AddWithValue("added_by", (object?)addedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlaylistItemEntity>> ListItemsAsync(
        long playlistId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT i.id, i.playlist_id, i.position, i.track_id, i.added_by, i.added_at,
                   t.id, t.source, t.external_id, t.title, t.webpage_url, t.thumbnail_url,
                   t.duration_seconds, t.playable_url, t.source_bytes, t.is_too_large
            FROM playlist_items i
            INNER JOIN tracks t ON t.id = i.track_id
            WHERE i.playlist_id = @playlist_id
            ORDER BY i.position;
            """;
        cmd.Parameters.AddWithValue("playlist_id", playlistId);
        var list = new List<PlaylistItemEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TimeSpan? duration = null;
            if (!reader.IsDBNull(12))
            {
                duration = TimeSpan.FromSeconds(reader.GetDouble(12));
            }

            list.Add(new PlaylistItemEntity
            {
                Id = reader.GetInt64(0),
                PlaylistId = reader.GetInt64(1),
                Position = reader.GetInt32(2),
                TrackId = reader.GetInt64(3),
                AddedBy = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                AddedAt = reader.GetFieldValue<DateTimeOffset>(5),
                Track = new TrackEntity
                {
                    Id = reader.GetInt64(6),
                    Source = reader.GetString(7),
                    ExternalId = reader.GetString(8),
                    Title = reader.GetString(9),
                    WebpageUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ThumbnailUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Duration = duration,
                    PlayableUrl = reader.IsDBNull(13) ? null : reader.GetString(13),
                    SourceBytes = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    IsTooLarge = reader.GetBoolean(15),
                },
            });
        }

        return list;
    }

    private static PlaylistEntity ReadPlaylist(Npgsql.NpgsqlDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            ClanId = reader.GetInt64(1),
            Name = reader.GetString(2),
            IsDefault = reader.GetBoolean(3),
            CreatedBy = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        };
}
