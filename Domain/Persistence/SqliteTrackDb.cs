using Mezube.Bot;
using Mezube.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mezube.Domain.Persistence;

public sealed class SqliteTrackDb : ITrackDb, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SqliteTrackDb> _logger;
    private bool _disposed;

    public SqliteTrackDb(BotOptions options, ILogger<SqliteTrackDb> logger)
    {
        _logger = logger;
        var path = string.IsNullOrWhiteSpace(options.TracksDbPath)
            ? "data/tracks.db"
            : options.TracksDbPath;
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
        _logger.LogInformation("Track store ready at {Path}", fullPath);
    }

    public async Task<TrackEntity?> TryGetAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url
                FROM tracks
                WHERE source = $source AND external_id = $external_id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$external_id", externalId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadTrack(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrackEntity?> TryGetByAliasAsync(
        string aliasKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.source, t.external_id, t.title, t.webpage_url, t.thumbnail_url, t.duration_seconds, t.playable_url
                FROM track_aliases a
                INNER JOIN tracks t ON t.source = a.source AND t.external_id = a.external_id
                WHERE a.alias_key = $alias_key
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$alias_key", aliasKey);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadTrack(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO tracks (
                    source, external_id, title, webpage_url, thumbnail_url, duration_seconds, playable_url,
                    created_at, updated_at
                ) VALUES (
                    $source, $external_id, $title, $webpage_url, $thumbnail_url, $duration_seconds, $playable_url,
                    $now, $now
                )
                ON CONFLICT(source, external_id) DO UPDATE SET
                    title = excluded.title,
                    webpage_url = COALESCE(excluded.webpage_url, tracks.webpage_url),
                    thumbnail_url = COALESCE(excluded.thumbnail_url, tracks.thumbnail_url),
                    duration_seconds = COALESCE(excluded.duration_seconds, tracks.duration_seconds),
                    playable_url = COALESCE(excluded.playable_url, tracks.playable_url),
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
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows == 0)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO tracks (
                        source, external_id, title, playable_url, created_at, updated_at
                    ) VALUES (
                        $source, $external_id, $title, $playable_url, $now, $now
                    );
                    """;
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddWithValue("$external_id", externalId);
                insert.Parameters.AddWithValue("$title", externalId);
                insert.Parameters.AddWithValue("$playable_url", playableUrl);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TouchPlayedAsync(
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tracks (
              source TEXT NOT NULL,
              external_id TEXT NOT NULL,
              title TEXT NOT NULL,
              webpage_url TEXT,
              thumbnail_url TEXT,
              duration_seconds REAL,
              playable_url TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              last_played_at TEXT,
              PRIMARY KEY (source, external_id)
            );

            CREATE TABLE IF NOT EXISTS track_aliases (
              alias_key TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              external_id TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static TrackEntity ReadTrack(SqliteDataReader reader)
    {
        TimeSpan? duration = null;
        if (!reader.IsDBNull(5))
        {
            duration = TimeSpan.FromSeconds(reader.GetDouble(5));
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
        };
    }
}
