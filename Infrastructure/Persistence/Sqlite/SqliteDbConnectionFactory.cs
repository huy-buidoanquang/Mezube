using Mezube.Bot;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mezube.Infrastructure.Persistence.Sqlite;

public sealed class SqliteDbConnectionFactory : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SqliteDbConnectionFactory> _logger;
    private bool _disposed;

    public SqliteDbConnectionFactory(BotOptions options, ILogger<SqliteDbConnectionFactory> logger)
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
        _logger.LogInformation("SQLite persistence ready at {Path}", fullPath);
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await action(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExecuteAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async (connection, ct) =>
        {
            await action(connection, ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
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
              source_bytes INTEGER,
              is_too_large INTEGER NOT NULL DEFAULT 0,
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

            CREATE TABLE IF NOT EXISTS clan_settings (
              clan_id INTEGER PRIMARY KEY,
              dj_role_id INTEGER,
              updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(connection, "tracks", "source_bytes", "INTEGER");
        EnsureColumn(connection, "tracks", "is_too_large", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string typeSql)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }
}
