namespace Mezube.Infrastructure.Persistence.Redis;

public interface IClanPlayerStore
{
    Task TouchTtlAsync(long clanId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> ListActiveClanIdsAsync(CancellationToken cancellationToken = default);

    Task EnqueueAsync(long clanId, QueuedTrackPayload item, CancellationToken cancellationToken = default);

    Task<long> QueueLengthAsync(long clanId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueuedTrackPayload>> SnapshotQueueAsync(long clanId, CancellationToken cancellationToken = default);

    Task<QueuedTrackPayload?> GetCurrentAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetCurrentAsync(long clanId, QueuedTrackPayload? current, CancellationToken cancellationToken = default);

    Task SetPlayerFieldAsync(long clanId, string field, RedisValueLike value, CancellationToken cancellationToken = default);

    Task<Dictionary<string, string>> GetPlayerAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetLoopModeAsync(long clanId, LoopMode mode, CancellationToken cancellationToken = default);

    Task<LoopMode> GetLoopModeAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetPlayHistoryIdAsync(long clanId, long? historyId, CancellationToken cancellationToken = default);

    Task<long?> GetPlayHistoryIdAsync(long clanId, CancellationToken cancellationToken = default);

    Task SetPositionAsync(long clanId, long positionMs, long durationMs, bool paused, CancellationToken cancellationToken = default);

    Task<(long PositionMs, long DurationMs, bool Paused)> GetPositionAsync(long clanId, CancellationToken cancellationToken = default);

    Task<long> EffectivePositionMsAsync(long clanId, CancellationToken cancellationToken = default);

    Task<AdvanceResult> TryAdvanceAsync(
        long clanId,
        long expectedPlayHistoryId,
        bool skipLoop,
        CancellationToken cancellationToken = default);

    Task ClearSessionAsync(long clanId, CancellationToken cancellationToken = default);

    Task RemovePendingMatchingAsync(
        long clanId,
        Func<QueuedTrackPayload, bool> predicate,
        CancellationToken cancellationToken = default);
}

/// <summary>Lightweight wrapper so callers need not reference StackExchange.Redis.</summary>
public readonly record struct RedisValueLike(string? StringValue, long? LongValue, bool? BoolValue)
{
    public static implicit operator RedisValueLike(string? v) => new(v, null, null);
    public static implicit operator RedisValueLike(long v) => new(null, v, null);
    public static implicit operator RedisValueLike(bool v) => new(null, null, v);
}
