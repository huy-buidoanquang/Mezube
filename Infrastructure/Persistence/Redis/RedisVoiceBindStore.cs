namespace Mezube.Infrastructure.Persistence.Redis;

public interface IVoiceBindStore
{
    Task SetUserVoiceChannelAsync(long clanId, long userId, long voiceChannelId, CancellationToken cancellationToken = default);

    Task ClearUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default);

    Task<long?> TryGetUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default);

    Task<long> CountAsync(long clanId, CancellationToken cancellationToken = default);

    /// <summary>All voice binds across clans for L1 rehydrate on startup.</summary>
    Task<IReadOnlyList<(long ClanId, long UserId, long ChannelId)>> SnapshotAllAsync(
        CancellationToken cancellationToken = default);
}

public sealed class RedisVoiceBindStore : IVoiceBindStore
{
    private readonly RedisConnection _redis;

    public RedisVoiceBindStore(RedisConnection redis)
    {
        _redis = redis;
    }

    public async Task SetUserVoiceChannelAsync(
        long clanId,
        long userId,
        long voiceChannelId,
        CancellationToken cancellationToken = default)
    {
        var key = RedisKeyNames.Voice(clanId);
        await _redis.Db.HashSetAsync(key, userId.ToString(), voiceChannelId.ToString()).ConfigureAwait(false);
        await _redis.Db.KeyExpireAsync(key, RedisKeyNames.VoiceTtl).ConfigureAwait(false);
    }

    public async Task ClearUserVoiceChannelAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        await _redis.Db.HashDeleteAsync(RedisKeyNames.Voice(clanId), userId.ToString()).ConfigureAwait(false);
    }

    public async Task<long?> TryGetUserVoiceChannelAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var v = await _redis.Db.HashGetAsync(RedisKeyNames.Voice(clanId), userId.ToString()).ConfigureAwait(false);
        if (v.IsNullOrEmpty || !long.TryParse((string?)v, out var channelId))
        {
            return null;
        }

        return channelId;
    }

    public async Task<long> CountAsync(long clanId, CancellationToken cancellationToken = default)
        => await _redis.Db.HashLengthAsync(RedisKeyNames.Voice(clanId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<(long ClanId, long UserId, long ChannelId)>> SnapshotAllAsync(
        CancellationToken cancellationToken = default)
    {
        var list = new List<(long, long, long)>();
        foreach (var endpoint in _redis.Multiplexer.GetEndPoints())
        {
            var server = _redis.Multiplexer.GetServer(endpoint);
            if (!server.IsConnected)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"{RedisKeyNames.Prefix}voice:*")
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                var keyStr = (string?)key;
                if (string.IsNullOrWhiteSpace(keyStr))
                {
                    continue;
                }

                var prefix = $"{RedisKeyNames.Prefix}voice:";
                if (!keyStr.StartsWith(prefix, StringComparison.Ordinal)
                    || !long.TryParse(keyStr[prefix.Length..], out var clanId)
                    || clanId == 0)
                {
                    continue;
                }

                var entries = await _redis.Db.HashGetAllAsync(key).ConfigureAwait(false);
                foreach (var e in entries)
                {
                    if (long.TryParse((string?)e.Name, out var userId)
                        && long.TryParse((string?)e.Value, out var channelId)
                        && userId != 0
                        && channelId != 0)
                    {
                        list.Add((clanId, userId, channelId));
                    }
                }
            }
        }

        return list;
    }
}
