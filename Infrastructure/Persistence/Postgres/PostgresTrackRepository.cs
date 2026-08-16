using Mezube.Domain.Entities;
using Mezube.Helpers;
using Npgsql;

namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresTrackRepository : ITrackRepository
{
    private readonly PostgresDbConnectionFactory _db;

    public PostgresTrackRepository(PostgresDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<TrackEntity?> TryGetAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                   playable_video_url, source_bytes, is_too_large
            FROM tracks
            WHERE source = @source AND external_id = @external_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTrack(reader)
            : null;
    }

    public async Task<TrackEntity?> TryGetByIdAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                   playable_video_url, source_bytes, is_too_large
            FROM tracks
            WHERE id = @id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("id", trackId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTrack(reader)
            : null;
    }

    public async Task<TrackEntity?> TryGetByAliasAsync(
        string aliasKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.source, t.external_id, t.title, t.webpage_url, t.thumbnail_url, t.duration_seconds, t.playable_url,
                   t.playable_video_url, t.source_bytes, t.is_too_large
            FROM track_aliases a
            INNER JOIN tracks t ON t.id = a.track_id
            WHERE a.alias_key = @alias_key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("alias_key", aliasKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTrack(reader)
            : null;
    }

    public async Task<long> UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (
                source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                playable_video_url, source_bytes, is_too_large, created_at, updated_at
            ) VALUES (
                @source, @external_id, @title, @webpage_url, @thumbnail_url, @duration_seconds, @playable_url,
                @playable_video_url, @source_bytes, @is_too_large, now(), now()
            )
            ON CONFLICT (source, external_id) DO UPDATE SET
                title = EXCLUDED.title,
                webpage_url = COALESCE(EXCLUDED.webpage_url, tracks.webpage_url),
                thumbnail_url = COALESCE(EXCLUDED.thumbnail_url, tracks.thumbnail_url),
                duration_seconds = COALESCE(EXCLUDED.duration_seconds, tracks.duration_seconds),
                playable_url = COALESCE(EXCLUDED.playable_url, tracks.playable_url),
                playable_video_url = COALESCE(EXCLUDED.playable_video_url, tracks.playable_video_url),
                source_bytes = COALESCE(EXCLUDED.source_bytes, tracks.source_bytes),
                is_too_large = EXCLUDED.is_too_large OR tracks.is_too_large,
                updated_at = now()
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("source", track.Source);
        cmd.Parameters.AddWithValue("external_id", track.ExternalId);
        cmd.Parameters.AddWithValue("title", track.Title);
        cmd.Parameters.AddWithValue("webpage_url", (object?)track.WebpageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("thumbnail_url", (object?)track.ThumbnailUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "duration_seconds",
            track.Duration is { } d ? d.TotalSeconds : DBNull.Value);
        cmd.Parameters.AddWithValue(
            "playable_url",
            (object?)PlayableUrlHelper.NullIfNotAudio(track.PlayableUrl) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "playable_video_url",
            (object?)PlayableUrlHelper.NullIfNotVideo(track.PlayableVideoUrl) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("source_bytes", (object?)track.SourceBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("is_too_large", track.IsTooLarge);
        var id = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id);
    }

    public async Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default)
    {
        var prepared = PlayableUrlHelper.NullIfNotAudio(playableUrl)
            ?? throw new ArgumentException(
                "playable_url must be a prepared CDN .ogg/.opus URL.",
                nameof(playableUrl));

        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (source, external_id, title, playable_url, is_too_large, created_at, updated_at)
            VALUES (@source, @external_id, @title, @playable_url, FALSE, now(), now())
            ON CONFLICT (source, external_id) DO UPDATE SET
                playable_url = EXCLUDED.playable_url,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        cmd.Parameters.AddWithValue("title", externalId);
        cmd.Parameters.AddWithValue("playable_url", prepared);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPlayableVideoUrlAsync(
        string source,
        string externalId,
        string playableVideoUrl,
        CancellationToken cancellationToken = default)
    {
        var prepared = PlayableUrlHelper.NullIfNotVideo(playableVideoUrl)
            ?? throw new ArgumentException(
                "playable_video_url must be a prepared CDN .webm URL.",
                nameof(playableVideoUrl));

        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (source, external_id, title, playable_video_url, is_too_large, created_at, updated_at)
            VALUES (@source, @external_id, @title, @playable_video_url, FALSE, now(), now())
            ON CONFLICT (source, external_id) DO UPDATE SET
                playable_video_url = EXCLUDED.playable_video_url,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        cmd.Parameters.AddWithValue("title", externalId);
        cmd.Parameters.AddWithValue("playable_video_url", prepared);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var ensure = connection.CreateCommand();
        ensure.CommandText =
            """
            INSERT INTO tracks (source, external_id, title, is_too_large, created_at, updated_at)
            VALUES (@source, @external_id, @title, FALSE, now(), now())
            ON CONFLICT (source, external_id) DO NOTHING
            RETURNING id;
            """;
        ensure.Parameters.AddWithValue("source", source);
        ensure.Parameters.AddWithValue("external_id", externalId);
        ensure.Parameters.AddWithValue("title", externalId);
        var trackIdObj = await ensure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long trackId;
        if (trackIdObj is null || trackIdObj is DBNull)
        {
            await using var lookup = connection.CreateCommand();
            lookup.CommandText =
                "SELECT id FROM tracks WHERE source = @source AND external_id = @external_id LIMIT 1;";
            lookup.Parameters.AddWithValue("source", source);
            lookup.Parameters.AddWithValue("external_id", externalId);
            trackId = Convert.ToInt64(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            trackId = Convert.ToInt64(trackIdObj);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO track_aliases (alias_key, track_id)
            VALUES (@alias_key, @track_id)
            ON CONFLICT (alias_key) DO UPDATE SET track_id = EXCLUDED.track_id;
            """;
        cmd.Parameters.AddWithValue("alias_key", aliasKey);
        cmd.Parameters.AddWithValue("track_id", trackId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TouchPlayedAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tracks
            SET last_played_at = now()
            WHERE source = @source AND external_id = @external_id;
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearPlayableUrlAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tracks
            SET playable_url = NULL, updated_at = now()
            WHERE source = @source AND external_id = @external_id;
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTooLargeAsync(
        string source,
        string externalId,
        long? sourceBytes = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (source, external_id, title, source_bytes, is_too_large, created_at, updated_at)
            VALUES (@source, @external_id, @title, @source_bytes, TRUE, now(), now())
            ON CONFLICT (source, external_id) DO UPDATE SET
                is_too_large = TRUE,
                source_bytes = COALESCE(EXCLUDED.source_bytes, tracks.source_bytes),
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("external_id", externalId);
        cmd.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(title) ? externalId : title!);
        cmd.Parameters.AddWithValue("source_bytes", (object?)sourceBytes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrackEntity ReadTrack(NpgsqlDataReader reader)
    {
        TimeSpan? duration = null;
        if (!reader.IsDBNull(6))
        {
            duration = TimeSpan.FromSeconds(reader.GetDouble(6));
        }

        long? sourceBytes = null;
        if (!reader.IsDBNull(9))
        {
            sourceBytes = reader.GetInt64(9);
        }

        return new TrackEntity
        {
            Id = reader.GetInt64(0),
            Source = reader.GetString(1),
            ExternalId = reader.GetString(2),
            Title = reader.GetString(3),
            WebpageUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
            ThumbnailUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
            Duration = duration,
            PlayableUrl = PlayableUrlHelper.NullIfNotAudio(
                reader.IsDBNull(7) ? null : reader.GetString(7)),
            PlayableVideoUrl = PlayableUrlHelper.NullIfNotVideo(
                reader.IsDBNull(8) ? null : reader.GetString(8)),
            SourceBytes = sourceBytes,
            IsTooLarge = reader.GetBoolean(10),
        };
    }
}
