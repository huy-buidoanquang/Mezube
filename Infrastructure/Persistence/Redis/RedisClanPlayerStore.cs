using StackExchange.Redis;

namespace Mezube.Infrastructure.Persistence.Redis;

public sealed class RedisClanPlayerStore : IClanPlayerStore
{
    // KEYS[1]=player KEYS[2]=queue KEYS[3]=voteskip pattern base not used — vote key passed as ARGV
    // ARGV[1]=expected_play_history_id ARGV[2]=skip_loop (0/1) ARGV[3]=vote_key ARGV[4]=ttl_seconds
    private const string AdvanceLua =
        """
        local player = KEYS[1]
        local queue = KEYS[2]
        local expected = ARGV[1]
        local skip_loop = ARGV[2]
        local vote_key = ARGV[3]
        local ttl = tonumber(ARGV[4])

        local current_id = redis.call('HGET', player, 'play_history_id')
        if (not current_id) or (current_id ~= expected) then
          return cjson.encode({ok=false, reason='stale'})
        end

        if vote_key and vote_key ~= '' then
          redis.call('DEL', vote_key)
        end

        local loop_mode = redis.call('HGET', player, 'loop_mode') or 'off'
        local current_json = redis.call('HGET', player, 'current_json')

        if skip_loop == '1' then
          loop_mode = 'off'
        end

        if loop_mode == 'track' and current_json then
          redis.call('HSET', player,
            'play_history_id', '',
            'position_ms', '0',
            'position_epoch_ms', tostring(redis.call('TIME')[1] * 1000),
            'paused', '0',
            'is_playing', '1',
            'updated_at', tostring(redis.call('TIME')[1] * 1000))
          redis.call('EXPIRE', player, ttl)
          redis.call('EXPIRE', queue, ttl)
          return cjson.encode({ok=true, action='replay', current=current_json})
        end

        if loop_mode == 'queue' and current_json then
          redis.call('RPUSH', queue, current_json)
        end

        local next_json = redis.call('LPOP', queue)
        if next_json then
          redis.call('HSET', player,
            'current_json', next_json,
            'play_history_id', '',
            'position_ms', '0',
            'position_epoch_ms', tostring(redis.call('TIME')[1] * 1000),
            'paused', '0',
            'is_playing', '1',
            'updated_at', tostring(redis.call('TIME')[1] * 1000))
          local dur = cjson.decode(next_json)['durationSeconds']
          if dur then
            redis.call('HSET', player, 'duration_ms', tostring(math.floor(dur * 1000)))
          end
          redis.call('EXPIRE', player, ttl)
          redis.call('EXPIRE', queue, ttl)
          return cjson.encode({ok=true, action='next', next=next_json})
        end

        redis.call('HSET', player,
          'current_json', '',
          'play_history_id', '',
          'is_playing', '0',
          'paused', '0',
          'position_ms', '0',
          'updated_at', tostring(redis.call('TIME')[1] * 1000))
        redis.call('EXPIRE', player, ttl)
        redis.call('EXPIRE', queue, ttl)
        return cjson.encode({ok=true, action='empty'})
        """;

    private readonly RedisConnection _redis;

    public RedisClanPlayerStore(RedisConnection redis)
    {
        _redis = redis;
    }

    public async Task TouchTtlAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        var ttl = RedisKeyNames.PlayerTtl;
        await db.KeyExpireAsync(RedisKeyNames.Player(clanId), ttl).ConfigureAwait(false);
        await db.KeyExpireAsync(RedisKeyNames.Queue(clanId), ttl).ConfigureAwait(false);
        await db.SetAddAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> ListActiveClanIdsAsync(CancellationToken cancellationToken = default)
    {
        var members = await _redis.Db.SetMembersAsync(RedisKeyNames.ActiveClans).ConfigureAwait(false);
        var ids = new HashSet<long>();
        foreach (var m in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (long.TryParse((string?)m, out var clanId) && clanId != 0)
            {
                // Drop stale index entries whose player+queue keys are gone.
                var playerExists = await _redis.Db.KeyExistsAsync(RedisKeyNames.Player(clanId)).ConfigureAwait(false);
                var queueLen = await _redis.Db.ListLengthAsync(RedisKeyNames.Queue(clanId)).ConfigureAwait(false);
                if (!playerExists && queueLen == 0)
                {
                    await _redis.Db.SetRemoveAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
                    continue;
                }

                ids.Add(clanId);
            }
        }

        if (ids.Count > 0)
        {
            return ids.OrderBy(x => x).ToArray();
        }

        // One-time fallback if index empty but legacy keys remain (pre-migration).
        return await ListActiveClanIdsByScanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<long>> ListActiveClanIdsByScanAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<long>();
        foreach (var endpoint in _redis.Multiplexer.GetEndPoints())
        {
            var server = _redis.Multiplexer.GetServer(endpoint);
            if (!server.IsConnected)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"{RedisKeyNames.Prefix}player:*")
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (TryParseClanId((string?)key, "player", out var clanId))
                {
                    ids.Add(clanId);
                    await _redis.Db.SetAddAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
                }
            }

            await foreach (var key in server.KeysAsync(pattern: $"{RedisKeyNames.Prefix}queue:*")
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (TryParseClanId((string?)key, "queue", out var clanId))
                {
                    ids.Add(clanId);
                    await _redis.Db.SetAddAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
                }
            }
        }

        return ids.OrderBy(x => x).ToArray();
    }

    public async Task EnqueueAsync(long clanId, QueuedTrackPayload item, CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        await db.ListRightPushAsync(RedisKeyNames.Queue(clanId), RedisJson.Serialize(item)).ConfigureAwait(false);
        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> QueueLengthAsync(long clanId, CancellationToken cancellationToken = default)
        => await _redis.Db.ListLengthAsync(RedisKeyNames.Queue(clanId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<QueuedTrackPayload>> SnapshotQueueAsync(
        long clanId,
        CancellationToken cancellationToken = default)
    {
        var values = await _redis.Db.ListRangeAsync(RedisKeyNames.Queue(clanId)).ConfigureAwait(false);
        var list = new List<QueuedTrackPayload>(values.Length);
        foreach (var v in values)
        {
            var item = RedisJson.Deserialize<QueuedTrackPayload>((string?)v);
            if (item is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    public async Task<QueuedTrackPayload?> GetCurrentAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var json = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId), "current_json").ConfigureAwait(false);
        return json.IsNullOrEmpty ? null : RedisJson.Deserialize<QueuedTrackPayload>((string)json!);
    }

    public async Task SetCurrentAsync(
        long clanId,
        QueuedTrackPayload? current,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        var key = RedisKeyNames.Player(clanId);
        if (current is null)
        {
            await db.HashDeleteAsync(key, "current_json").ConfigureAwait(false);
            await db.HashSetAsync(key, "is_playing", "0").ConfigureAwait(false);
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.HashSetAsync(
                key,
                [
                    new HashEntry("current_json", RedisJson.Serialize(current)),
                    new HashEntry("is_playing", "1"),
                    new HashEntry("paused", "0"),
                    new HashEntry("position_ms", "0"),
                    new HashEntry("position_epoch_ms", now.ToString()),
                    new HashEntry(
                        "duration_ms",
                        current.DurationSeconds is { } d
                            ? ((long)(d * 1000)).ToString()
                            : "0"),
                    new HashEntry("mode", current.Mode),
                    new HashEntry("channel_id", current.ChannelId.ToString()),
                    new HashEntry("channel_label", current.ChannelLabel ?? string.Empty),
                    new HashEntry("room_name", current.RoomName ?? string.Empty),
                    new HashEntry("updated_at", now.ToString()),
                ]).ConfigureAwait(false);
        }

        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPlayerFieldAsync(
        long clanId,
        string field,
        RedisValueLike value,
        CancellationToken cancellationToken = default)
    {
        RedisValue rv = value.StringValue is not null
            ? value.StringValue
            : value.LongValue is { } l
                ? l.ToString()
                : value.BoolValue is { } b
                    ? (b ? "1" : "0")
                    : RedisValue.EmptyString;
        await _redis.Db.HashSetAsync(RedisKeyNames.Player(clanId), field, rv).ConfigureAwait(false);
        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> GetPlayerAsync(
        long clanId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _redis.Db.HashGetAllAsync(RedisKeyNames.Player(clanId)).ConfigureAwait(false);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            dict[(string)e.Name!] = (string?)e.Value ?? string.Empty;
        }

        return dict;
    }

    public Task SetLoopModeAsync(long clanId, LoopMode mode, CancellationToken cancellationToken = default)
        => SetPlayerFieldAsync(
            clanId,
            "loop_mode",
            mode switch
            {
                LoopMode.Track => "track",
                LoopMode.Queue => "queue",
                _ => "off",
            },
            cancellationToken);

    public async Task<LoopMode> GetLoopModeAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var v = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId), "loop_mode").ConfigureAwait(false);
        return ((string?)v)?.ToLowerInvariant() switch
        {
            "track" => LoopMode.Track,
            "queue" => LoopMode.Queue,
            _ => LoopMode.Off,
        };
    }

    public async Task SetPlayHistoryIdAsync(
        long clanId,
        long? historyId,
        CancellationToken cancellationToken = default)
    {
        await _redis.Db.HashSetAsync(
            RedisKeyNames.Player(clanId),
            "play_history_id",
            historyId?.ToString() ?? string.Empty).ConfigureAwait(false);
        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> GetPlayHistoryIdAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var v = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId), "play_history_id").ConfigureAwait(false);
        if (v.IsNullOrEmpty || !long.TryParse((string?)v, out var id) || id == 0)
        {
            return null;
        }

        return id;
    }

    public async Task SetPositionAsync(
        long clanId,
        long positionMs,
        long durationMs,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _redis.Db.HashSetAsync(
            RedisKeyNames.Player(clanId),
            [
                new HashEntry("position_ms", positionMs.ToString()),
                new HashEntry("position_epoch_ms", now.ToString()),
                new HashEntry("duration_ms", durationMs.ToString()),
                new HashEntry("paused", paused ? "1" : "0"),
                new HashEntry("updated_at", now.ToString()),
            ]).ConfigureAwait(false);
        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(long PositionMs, long DurationMs, bool Paused)> GetPositionAsync(
        long clanId,
        CancellationToken cancellationToken = default)
    {
        var fields = await _redis.Db.HashGetAsync(
            RedisKeyNames.Player(clanId),
            ["position_ms", "duration_ms", "paused"]).ConfigureAwait(false);
        long.TryParse((string?)fields[0], out var pos);
        long.TryParse((string?)fields[1], out var dur);
        var paused = (string?)fields[2] is "1" or "true";
        return (pos, dur, paused);
    }

    public async Task<long> EffectivePositionMsAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var fields = await _redis.Db.HashGetAsync(
            RedisKeyNames.Player(clanId),
            ["position_ms", "position_epoch_ms", "duration_ms", "paused"]).ConfigureAwait(false);
        long.TryParse((string?)fields[0], out var pos);
        long.TryParse((string?)fields[1], out var epoch);
        long.TryParse((string?)fields[2], out var dur);
        var paused = (string?)fields[3] is "1" or "true";
        long effective = pos;
        if (!paused && epoch > 0)
        {
            effective = pos + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - epoch);
        }

        if (dur > 0)
        {
            effective = Math.Clamp(effective, 0, dur);
        }

        return Math.Max(0, effective);
    }

    public async Task<AdvanceResult> TryAdvanceAsync(
        long clanId,
        long expectedPlayHistoryId,
        bool skipLoop,
        CancellationToken cancellationToken = default)
    {
        var voteKey = RedisKeyNames.VoteSkip(clanId, expectedPlayHistoryId);
        var raw = (string?)await _redis.Db.ScriptEvaluateAsync(
            AdvanceLua,
            [
                RedisKeyNames.Player(clanId),
                RedisKeyNames.Queue(clanId),
            ],
            [
                expectedPlayHistoryId.ToString(),
                skipLoop ? "1" : "0",
                voteKey,
                ((int)RedisKeyNames.PlayerTtl.TotalSeconds).ToString(),
            ]).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return AdvanceResult.Stale();
        }

        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        if (!ok)
        {
            return AdvanceResult.Stale();
        }

        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        QueuedTrackPayload? next = null;
        QueuedTrackPayload? current = null;
        if (root.TryGetProperty("next", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            next = RedisJson.Deserialize<QueuedTrackPayload>(n.GetString());
        }

        if (root.TryGetProperty("current", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            current = RedisJson.Deserialize<QueuedTrackPayload>(c.GetString());
        }

        return new AdvanceResult
        {
            Ok = true,
            Action = action,
            Next = next,
            Current = current,
        };
    }

    public async Task ClearSessionAsync(long clanId, CancellationToken cancellationToken = default)
    {
        var historyId = await GetPlayHistoryIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        var db = _redis.Db;
        var keys = new List<RedisKey>
        {
            RedisKeyNames.Player(clanId),
            RedisKeyNames.Queue(clanId),
        };
        if (historyId is long hid)
        {
            keys.Add(RedisKeyNames.VoteSkip(clanId, hid));
        }

        await db.KeyDeleteAsync(keys.ToArray()).ConfigureAwait(false);
        await db.SetRemoveAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
    }

    public async Task RemovePendingMatchingAsync(
        long clanId,
        Func<QueuedTrackPayload, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        var items = await SnapshotQueueAsync(clanId, cancellationToken).ConfigureAwait(false);
        var kept = new List<RedisValue>();
        var removed = false;
        foreach (var item in items)
        {
            if (!removed && predicate(item))
            {
                removed = true;
                continue;
            }

            kept.Add(RedisJson.Serialize(item));
        }

        var key = RedisKeyNames.Queue(clanId);
        var db = _redis.Db;
        await db.KeyDeleteAsync(key).ConfigureAwait(false);
        if (kept.Count > 0)
        {
            await db.ListRightPushAsync(key, kept.ToArray()).ConfigureAwait(false);
        }

        await TouchTtlAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseClanId(string? key, string entity, out long clanId)
    {
        clanId = 0;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var prefix = $"{RedisKeyNames.Prefix}{entity}:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return long.TryParse(key[prefix.Length..], out clanId) && clanId != 0;
    }
}
