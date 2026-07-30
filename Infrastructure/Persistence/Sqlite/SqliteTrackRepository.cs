using Mezube.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace Mezube.Infrastructure.Persistence.Sqlite;

public sealed class SqliteTrackRepository : ITrackRepository
{
    private readonly SqliteDbConnectionFactory _db;

    public SqliteTrackRepository(SqliteDbConnectionFactory db)
    {
        _db = db;
    }

    public Task<TrackEntity?> TryGetAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                       source_bytes, is_too_large
                FROM tracks
                WHERE source = $source AND external_id = $external_id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? ReadTrack(reader)
                : null;
        }, cancellationToken);

    public Task<TrackEntity?> TryGetByAliasAsync(
        string aliasKey,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.source, t.external_id, t.title, t.webpage_url, t.thumbnail_url, t.duration_seconds, t.playable_url,
                       t.source_bytes, t.is_too_large
                FROM track_aliases a
                INNER JOIN tracks t ON t.source = a.source AND t.external_id = a.external_id
                WHERE a.alias_key = $alias_key
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$alias_key", aliasKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? ReadTrack(reader)
                : null;
        }, cancellationToken);

    public Task UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO tracks (
                    source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                    source_bytes, is_too_large, created_at, updated_at
                ) VALUES (
                    $source, $external_id, $title, $webpage_url, $thumbnail_url, $duration_seconds, $playable_url,
                    $source_bytes, $is_too_large, $now, $now
                )
                ON CONFLICT(source, external_id) DO UPDATE SET
                    title = excluded.title,
                    webpage_url = COALESCE(excluded.webpage_url, tracks.webpage_url),
                    thumbnail_url = COALESCE(excluded.thumbnail_url, tracks.thumbnail_url),
                    duration_seconds = COALESCE(excluded.duration_seconds, tracks.duration_seconds),
                    playable_url = COALESCE(excluded.playable_url, tracks.playable_url),
                    source_bytes = COALESCE(excluded.source_bytes, tracks.source_bytes),
                    is_too_large = CASE
                        WHEN excluded.is_too_large = 1 OR tracks.is_too_large = 1 THEN 1
                        ELSE 0
                    END,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$source", track.Source);
            cmd.Parameters.AddWithValue("$external_id", track.ExternalId);
            cmd.Parameters.AddWithValue("$title", track.Title);
            cmd.Parameters.AddWithValue("$webpage_url", (object?)track.WebpageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumbnail_url", (object?)track.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$duration_seconds",
                track.Duration is { } d ? d.TotalSeconds : DBNull.Value);
            cmd.Parameters.AddWithValue("$playable_url", (object?)track.PlayableUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_bytes", (object?)track.SourceBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_too_large", track.IsTooLarge ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks
                SET playable_url = $playable_url, updated_at = $now
                WHERE source = $source AND external_id = $external_id;
                """;
            cmd.Parameters.AddWithValue("$playable_url", playableUrl);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO tracks (
                        source, external_id, title, playable_url, is_too_large, created_at, updated_at
                    ) VALUES (
                        $source, $external_id, $title, $playable_url, 0, $now, $now
                    );
                    """;
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddWithValue("$external_id", externalId);
                insert.Parameters.AddWithValue("$title", externalId);
                insert.Parameters.AddWithValue("$playable_url", playableUrl);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO track_aliases (alias_key, source, external_id)
                VALUES ($alias_key, $source, $external_id)
                ON CONFLICT(alias_key) DO UPDATE SET
                    source = excluded.source,
                    external_id = excluded.external_id;
                """;
            cmd.Parameters.AddWithValue("$alias_key", aliasKey);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task TouchPlayedAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks
                SET last_played_at = $now
                WHERE source = $source AND external_id = $external_id;
                """;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkTooLargeAsync(
        string source,
        string externalId,
        long? sourceBytes = null,
        string? title = null,
        CancellationToken cancellationToken = default)
        => _db.ExecuteAsync(async (connection, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks
                SET is_too_large = 1,
                    source_bytes = COALESCE($source_bytes, source_bytes),
                    updated_at = $now
                WHERE source = $source AND external_id = $external_id;
                """;
            cmd.Parameters.AddWithValue("$source_bytes", (object?)sourceBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO tracks (
                        source, external_id, title, source_bytes, is_too_large, created_at, updated_at
                    ) VALUES (
                        $source, $external_id, $title, $source_bytes, 1, $now, $now
                    );
                    """;
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddWithValue("$external_id", externalId);
                insert.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? externalId : title!);
                insert.Parameters.AddWithValue("$source_bytes", (object?)sourceBytes ?? DBNull.Value);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }, cancellationToken);

    private static TrackEntity ReadTrack(SqliteDataReader reader)
    {
        TimeSpan? duration = null;
        if (!reader.IsDBNull(5))
        {
            duration = TimeSpan.FromSeconds(reader.GetDouble(5));
        }

        long? sourceBytes = null;
        if (!reader.IsDBNull(7))
        {
            sourceBytes = reader.GetInt64(7);
        }

        return new TrackEntity
        {
            Source = reader.GetString(0),
            ExternalId = reader.GetString(1),
            Title = reader.GetString(2),
            WebpageUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
            ThumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
            Duration = duration,
            PlayableUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceBytes = sourceBytes,
            IsTooLarge = !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
        };
    }
}
