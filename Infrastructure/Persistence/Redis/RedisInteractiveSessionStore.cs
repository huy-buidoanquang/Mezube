using Mezube.Music.Interactive;
using StackExchange.Redis;

namespace Mezube.Infrastructure.Persistence.Redis;

public interface IInteractiveSessionStore
{
    Task SaveSearchPickAsync(long messageId, SearchPickSession session, CancellationToken cancellationToken = default);
    Task<SearchPickSession?> TryGetSearchPickAsync(long messageId, CancellationToken cancellationToken = default);
    /// <summary>Atomically read+delete so only one click can claim the picker.</summary>
    Task<SearchPickSession?> TakeSearchPickAsync(long messageId, CancellationToken cancellationToken = default);
    Task DeleteSearchPickAsync(long messageId, CancellationToken cancellationToken = default);

    Task SavePlaylistImportAsync(long messageId, PlaylistImportSession session, CancellationToken cancellationToken = default);
    Task<PlaylistImportSession?> TryGetPlaylistImportAsync(long messageId, CancellationToken cancellationToken = default);
    Task DeletePlaylistImportAsync(long messageId, CancellationToken cancellationToken = default);
}

public sealed class RedisInteractiveSessionStore : IInteractiveSessionStore
{
    private readonly RedisConnection _redis;

    public RedisInteractiveSessionStore(RedisConnection redis)
    {
        _redis = redis;
    }

    public Task SaveSearchPickAsync(long messageId, SearchPickSession session, CancellationToken cancellationToken = default)
        => SetAsync(RedisKeyNames.SearchPick(messageId), session, RedisKeyNames.InteractiveSessionTtl);

    public Task<SearchPickSession?> TryGetSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
        => GetAsync<SearchPickSession>(RedisKeyNames.SearchPick(messageId));

    public Task<SearchPickSession?> TakeSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
        => TakeAsync<SearchPickSession>(RedisKeyNames.SearchPick(messageId));

    public Task DeleteSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
        => _redis.Db.KeyDeleteAsync(RedisKeyNames.SearchPick(messageId));

    public Task SavePlaylistImportAsync(long messageId, PlaylistImportSession session, CancellationToken cancellationToken = default)
        => SetAsync(RedisKeyNames.PlaylistImport(messageId), session, RedisKeyNames.InteractiveSessionTtl);

    public Task<PlaylistImportSession?> TryGetPlaylistImportAsync(long messageId, CancellationToken cancellationToken = default)
        => GetAsync<PlaylistImportSession>(RedisKeyNames.PlaylistImport(messageId));

    public Task DeletePlaylistImportAsync(long messageId, CancellationToken cancellationToken = default)
        => _redis.Db.KeyDeleteAsync(RedisKeyNames.PlaylistImport(messageId));

    private async Task SetAsync<T>(string key, T value, TimeSpan ttl)
    {
        var json = RedisJson.Serialize(value);
        await _redis.Db.StringSetAsync(key, json, ttl).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string key)
    {
        var json = await _redis.Db.StringGetAsync(key).ConfigureAwait(false);
        if (json.IsNullOrEmpty)
        {
            return default;
        }

        return RedisJson.Deserialize<T>((string)json!);
    }

    private async Task<T?> TakeAsync<T>(string key)
    {
        var json = await _redis.Db.StringGetDeleteAsync(key).ConfigureAwait(false);
        if (json.IsNullOrEmpty)
        {
            return default;
        }

        return RedisJson.Deserialize<T>((string)json!);
    }
}
