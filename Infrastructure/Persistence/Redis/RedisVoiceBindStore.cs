namespace Mezube.Infrastructure.Persistence.Redis;

public interface IVoiceBindStore
{
    Task SetUserVoiceChannelAsync(long clanId, long userId, long voiceChannelId, CancellationToken cancellationToken = default);

    Task ClearUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default);

    Task<long?> TryGetUserVoiceChannelAsync(long clanId, long userId, CancellationToken cancellationToken = default);

    Task<long> CountAsync(long clanId, CancellationToken cancellationToken = default);
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
}
