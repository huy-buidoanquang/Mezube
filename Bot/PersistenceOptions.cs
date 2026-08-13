namespace Mezube.Bot;

/// <summary>Postgres + Redis connection strings.</summary>
public sealed class PersistenceOptions
{
    public string PostgresConnectionString { get; set; } = string.Empty;
    public string RedisConnectionString { get; set; } = "localhost:6379";
    public string TracksDbPath { get; set; } = "data/tracks.db";
}
