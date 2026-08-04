namespace Mezube.Infrastructure.Persistence.Redis;

public interface IVoteSkipStore
{
    Task<(long Votes, bool Added)> AddVoteAsync(
        long clanId,
        long playHistoryId,
        long userId,
        CancellationToken cancellationToken = default);

    Task ClearAsync(long clanId, long playHistoryId, CancellationToken cancellationToken = default);
}

public sealed class RedisVoteSkipStore : IVoteSkipStore
{
    private readonly RedisConnection _redis;

    public RedisVoteSkipStore(RedisConnection redis)
    {
        _redis = redis;
    }

    public async Task<(long Votes, bool Added)> AddVoteAsync(
        long clanId,
        long playHistoryId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var key = RedisKeyNames.VoteSkip(clanId, playHistoryId);
        var added = await _redis.Db.SetAddAsync(key, userId.ToString()).ConfigureAwait(false);
        await _redis.Db.KeyExpireAsync(key, RedisKeyNames.PlayerTtl).ConfigureAwait(false);
        var count = await _redis.Db.SetLengthAsync(key).ConfigureAwait(false);
        return (count, added);
    }

    public Task ClearAsync(long clanId, long playHistoryId, CancellationToken cancellationToken = default)
        => _redis.Db.KeyDeleteAsync(RedisKeyNames.VoteSkip(clanId, playHistoryId));
}
