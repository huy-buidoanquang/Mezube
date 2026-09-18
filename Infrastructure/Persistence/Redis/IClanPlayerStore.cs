namespace Mezube.Infrastructure.Persistence.Redis;

public readonly record struct PlayerSessionKey(long ClanId, long ChannelId)
{
    public bool IsValid => ClanId != 0 && ChannelId != 0;

    public string IndexValue => $"{ClanId}:{ChannelId}";

    public static bool TryParse(string? value, out PlayerSessionKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var sep = value.IndexOf(':');
        if (sep <= 0 || sep == value.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(value.AsSpan(0, sep), out var clanId) || clanId == 0)
        {
            return false;
        }

        if (!long.TryParse(value.AsSpan(sep + 1), out var channelId) || channelId == 0)
        {
            return false;
        }

        key = new PlayerSessionKey(clanId, channelId);
        return true;
    }
}

public interface IClanPlayerStore
{
    Task TouchTtlAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerSessionKey>> ListActiveSessionsAsync(CancellationToken cancellationToken = default);

    Task EnqueueAsync(long clanId, long channelId, QueuedTrackPayload item, CancellationToken cancellationToken = default);

    Task EnqueueFrontAsync(long clanId, long channelId, QueuedTrackPayload item, CancellationToken cancellationToken = default);

    /// <summary>LPOP pending into current_json when current is empty (pending-only queue).</summary>
    Task<QueuedTrackPayload?> EnsureCurrentAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<long> QueueLengthAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueuedTrackPayload>> SnapshotQueueAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<QueuedTrackPayload?> GetCurrentAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task SetCurrentAsync(long clanId, long channelId, QueuedTrackPayload? current, CancellationToken cancellationToken = default);

    Task SetPlayerFieldAsync(long clanId, long channelId, string field, RedisValueLike value, CancellationToken cancellationToken = default);

    Task<Dictionary<string, string>> GetPlayerAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task SetLoopModeAsync(long clanId, long channelId, LoopMode mode, CancellationToken cancellationToken = default);

    Task<LoopMode> GetLoopModeAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task SetPlayHistoryIdAsync(long clanId, long channelId, long? historyId, CancellationToken cancellationToken = default);

    Task<long?> GetPlayHistoryIdAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task SetPositionAsync(long clanId, long channelId, long positionMs, long durationMs, bool paused, CancellationToken cancellationToken = default);

    Task<(long PositionMs, long DurationMs, bool Paused)> GetPositionAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<long> EffectivePositionMsAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task<AdvanceResult> TryAdvanceAsync(
        long clanId,
        long channelId,
        long expectedPlayHistoryId,
        bool skipLoop,
        CancellationToken cancellationToken = default);

    Task ClearSessionAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task RemovePendingMatchingAsync(
        long clanId,
        long channelId,
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
