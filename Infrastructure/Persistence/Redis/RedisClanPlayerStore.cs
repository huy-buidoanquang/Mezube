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

    public async Task TouchTtlAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        var ttl = RedisKeyNames.PlayerTtl;
        var key = new PlayerSessionKey(clanId, channelId);
        await db.KeyExpireAsync(RedisKeyNames.Player(clanId, channelId), ttl).ConfigureAwait(false);
        await db.KeyExpireAsync(RedisKeyNames.Queue(clanId, channelId), ttl).ConfigureAwait(false);
        await db.SetAddAsync(RedisKeyNames.ActiveSessions, key.IndexValue).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlayerSessionKey>> ListActiveSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<(long ClanId, long ChannelId), PlayerSessionKey>();
        var members = await _redis.Db.SetMembersAsync(RedisKeyNames.ActiveSessions).ConfigureAwait(false);
        foreach (var m in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PlayerSessionKey.TryParse((string?)m, out var key))
            {
                await _redis.Db.SetRemoveAsync(RedisKeyNames.ActiveSessions, m).ConfigureAwait(false);
                continue;
            }

            if (await SessionExistsAsync(key.ClanId, key.ChannelId).ConfigureAwait(false))
            {
                keys[(key.ClanId, key.ChannelId)] = key;
                continue;
            }

            await _redis.Db.SetRemoveAsync(RedisKeyNames.ActiveSessions, key.IndexValue).ConfigureAwait(false);
        }

        await CollectLegacySessionsAsync(keys, cancellationToken).ConfigureAwait(false);
        if (keys.Count > 0)
        {
            return keys.Values.OrderBy(x => x.ClanId).ThenBy(x => x.ChannelId).ToArray();
        }

        return await ListActiveSessionsByScanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CollectLegacySessionsAsync(
        Dictionary<(long ClanId, long ChannelId), PlayerSessionKey> keys,
        CancellationToken cancellationToken)
    {
        var legacyClans = await _redis.Db.SetMembersAsync(RedisKeyNames.ActiveClans).ConfigureAwait(false);
        foreach (var m in legacyClans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!long.TryParse((string?)m, out var clanId) || clanId == 0)
            {
                continue;
            }

            var channelId = await TryReadLegacyChannelIdAsync(clanId).ConfigureAwait(false);
            if (channelId is not long ch)
            {
                await _redis.Db.SetRemoveAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
                continue;
            }

            var key = new PlayerSessionKey(clanId, ch);
            keys[(clanId, ch)] = key;
            await _redis.Db.SetAddAsync(RedisKeyNames.ActiveSessions, key.IndexValue).ConfigureAwait(false);
        }
    }

    private async Task<long?> TryReadLegacyChannelIdAsync(long clanId)
    {
        var channel = await _redis.Db.HashGetAsync(RedisKeyNames.LegacyPlayer(clanId), "channel_id")
            .ConfigureAwait(false);
        if (long.TryParse((string?)channel, out var fromHash) && fromHash != 0)
        {
            return fromHash;
        }

        var current = await GetLegacyCurrentAsync(clanId).ConfigureAwait(false);
        if (current is { ChannelId: not 0 })
        {
            return current.ChannelId;
        }

        var pending = await _redis.Db.ListRangeAsync(RedisKeyNames.LegacyQueue(clanId), 0, 0).ConfigureAwait(false);
        if (pending.Length > 0)
        {
            var item = RedisJson.Deserialize<QueuedTrackPayload>((string?)pending[0]);
            if (item is { ChannelId: not 0 })
            {
                return item.ChannelId;
            }
        }

        var playerExists = await _redis.Db.KeyExistsAsync(RedisKeyNames.LegacyPlayer(clanId)).ConfigureAwait(false);
        var queueLen = await _redis.Db.ListLengthAsync(RedisKeyNames.LegacyQueue(clanId)).ConfigureAwait(false);
        return playerExists || queueLen > 0 ? null : null;
    }

    private async Task<QueuedTrackPayload?> GetLegacyCurrentAsync(long clanId)
    {
        var json = await _redis.Db.HashGetAsync(RedisKeyNames.LegacyPlayer(clanId), "current_json").ConfigureAwait(false);
        return json.IsNullOrEmpty ? null : RedisJson.Deserialize<QueuedTrackPayload>((string)json!);
    }

    private async Task<bool> SessionExistsAsync(long clanId, long channelId)
    {
        var playerExists = await _redis.Db.KeyExistsAsync(RedisKeyNames.Player(clanId, channelId)).ConfigureAwait(false);
        if (playerExists)
        {
            return true;
        }

        var queueLen = await _redis.Db.ListLengthAsync(RedisKeyNames.Queue(clanId, channelId)).ConfigureAwait(false);
        if (queueLen > 0)
        {
            return true;
        }

        return await LegacySessionMatchesAsync(clanId, channelId).ConfigureAwait(false);
    }

    private async Task<bool> LegacySessionMatchesAsync(long clanId, long channelId)
    {
        var legacyChannel = await TryReadLegacyChannelIdAsync(clanId).ConfigureAwait(false);
        return legacyChannel == channelId;
    }

    private async Task<IReadOnlyList<PlayerSessionKey>> ListActiveSessionsByScanAsync(
        CancellationToken cancellationToken)
    {
        var keys = new Dictionary<(long ClanId, long ChannelId), PlayerSessionKey>();
        foreach (var endpoint in _redis.Multiplexer.GetEndPoints())
        {
            var server = _redis.Multiplexer.GetServer(endpoint);
            if (!server.IsConnected)
            {
                continue;
            }

            await foreach (var redisKey in server.KeysAsync(pattern: $"{RedisKeyNames.Prefix}player:*")
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!TryParsePlayerKey((string?)redisKey, "player", out var key))
                {
                    continue;
                }

                keys[(key.ClanId, key.ChannelId)] = key;
                await _redis.Db.SetAddAsync(RedisKeyNames.ActiveSessions, key.IndexValue).ConfigureAwait(false);
            }

            await foreach (var redisKey in server.KeysAsync(pattern: $"{RedisKeyNames.Prefix}queue:*")
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!TryParsePlayerKey((string?)redisKey, "queue", out var key))
                {
                    continue;
                }

                keys[(key.ClanId, key.ChannelId)] = key;
                await _redis.Db.SetAddAsync(RedisKeyNames.ActiveSessions, key.IndexValue).ConfigureAwait(false);
            }
        }

        return keys.Values.OrderBy(x => x.ClanId).ThenBy(x => x.ChannelId).ToArray();
    }

    public async Task EnqueueAsync(
        long clanId,
        long channelId,
        QueuedTrackPayload item,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        await db.ListRightPushAsync(RedisKeyNames.Queue(clanId, channelId), RedisJson.Serialize(item))
            .ConfigureAwait(false);
        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueFrontAsync(
        long clanId,
        long channelId,
        QueuedTrackPayload item,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.Db;
        await db.ListLeftPushAsync(RedisKeyNames.Queue(clanId, channelId), RedisJson.Serialize(item))
            .ConfigureAwait(false);
        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    private const string EnsureCurrentLua =
        """
        local player = KEYS[1]
        local queue = KEYS[2]
        local ttl = tonumber(ARGV[1])
        local cur = redis.call('HGET', player, 'current_json')
        if cur and cur ~= '' then
          redis.call('EXPIRE', player, ttl)
          redis.call('EXPIRE', queue, ttl)
          return cur
        end
        local nxt = redis.call('LPOP', queue)
        if nxt then
          redis.call('HSET', player,
            'current_json', nxt,
            'is_playing', '1',
            'paused', '0',
            'position_ms', '0',
            'position_epoch_ms', tostring(redis.call('TIME')[1] * 1000),
            'updated_at', tostring(redis.call('TIME')[1] * 1000))
          redis.call('EXPIRE', player, ttl)
          redis.call('EXPIRE', queue, ttl)
          return nxt
        end
        return ''
        """;

    public async Task<QueuedTrackPayload?> EnsureCurrentAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var raw = (string?)await _redis.Db.ScriptEvaluateAsync(
            EnsureCurrentLua,
            [RedisKeyNames.Player(clanId, channelId), RedisKeyNames.Queue(clanId, channelId)],
            [((int)RedisKeyNames.PlayerTtl.TotalSeconds).ToString()]).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? null : RedisJson.Deserialize<QueuedTrackPayload>(raw);
    }

    public async Task<long> QueueLengthAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
        => await _redis.Db.ListLengthAsync(RedisKeyNames.Queue(clanId, channelId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<QueuedTrackPayload>> SnapshotQueueAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var values = await _redis.Db.ListRangeAsync(RedisKeyNames.Queue(clanId, channelId)).ConfigureAwait(false);
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

    public async Task<QueuedTrackPayload?> GetCurrentAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var json = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId, channelId), "current_json")
            .ConfigureAwait(false);
        return json.IsNullOrEmpty ? null : RedisJson.Deserialize<QueuedTrackPayload>((string)json!);
    }

    public async Task SetCurrentAsync(
        long clanId,
        long channelId,
        QueuedTrackPayload? current,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var db = _redis.Db;
        var key = RedisKeyNames.Player(clanId, channelId);
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

        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPlayerFieldAsync(
        long clanId,
        long channelId,
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
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        await _redis.Db.HashSetAsync(RedisKeyNames.Player(clanId, channelId), field, rv).ConfigureAwait(false);
        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> GetPlayerAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var entries = await _redis.Db.HashGetAllAsync(RedisKeyNames.Player(clanId, channelId)).ConfigureAwait(false);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            dict[(string)e.Name!] = (string?)e.Value ?? string.Empty;
        }

        return dict;
    }

    public Task SetLoopModeAsync(
        long clanId,
        long channelId,
        LoopMode mode,
        CancellationToken cancellationToken = default)
        => SetPlayerFieldAsync(
            clanId,
            channelId,
            "loop_mode",
            mode switch
            {
                LoopMode.Track => "track",
                LoopMode.Queue => "queue",
                _ => "off",
            },
            cancellationToken);

    public async Task<LoopMode> GetLoopModeAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var v = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId, channelId), "loop_mode")
            .ConfigureAwait(false);
        return ((string?)v)?.ToLowerInvariant() switch
        {
            "track" => LoopMode.Track,
            "queue" => LoopMode.Queue,
            _ => LoopMode.Off,
        };
    }

    public async Task SetPlayHistoryIdAsync(
        long clanId,
        long channelId,
        long? historyId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        await _redis.Db.HashSetAsync(
            RedisKeyNames.Player(clanId, channelId),
            "play_history_id",
            historyId?.ToString() ?? string.Empty).ConfigureAwait(false);
        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> GetPlayHistoryIdAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var v = await _redis.Db.HashGetAsync(RedisKeyNames.Player(clanId, channelId), "play_history_id")
            .ConfigureAwait(false);
        if (v.IsNullOrEmpty || !long.TryParse((string?)v, out var id) || id == 0)
        {
            return null;
        }

        return id;
    }

    public async Task SetPositionAsync(
        long clanId,
        long channelId,
        long positionMs,
        long durationMs,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _redis.Db.HashSetAsync(
            RedisKeyNames.Player(clanId, channelId),
            [
                new HashEntry("position_ms", positionMs.ToString()),
                new HashEntry("position_epoch_ms", now.ToString()),
                new HashEntry("duration_ms", durationMs.ToString()),
                new HashEntry("paused", paused ? "1" : "0"),
                new HashEntry("updated_at", now.ToString()),
            ]).ConfigureAwait(false);
        await TouchTtlAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(long PositionMs, long DurationMs, bool Paused)> GetPositionAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var fields = await _redis.Db.HashGetAsync(
            RedisKeyNames.Player(clanId, channelId),
            ["position_ms", "duration_ms", "paused"]).ConfigureAwait(false);
        long.TryParse((string?)fields[0], out var pos);
        long.TryParse((string?)fields[1], out var dur);
        var paused = (string?)fields[2] is "1" or "true";
        return (pos, dur, paused);
    }

    public async Task<long> EffectivePositionMsAsync(
        long clanId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var fields = await _redis.Db.HashGetAsync(
            RedisKeyNames.Player(clanId, channelId),
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
        long channelId,
        long expectedPlayHistoryId,
        bool skipLoop,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var voteKey = RedisKeyNames.VoteSkip(clanId, expectedPlayHistoryId);
        var raw = (string?)await _redis.Db.ScriptEvaluateAsync(
            AdvanceLua,
            [
                RedisKeyNames.Player(clanId, channelId),
                RedisKeyNames.Queue(clanId, channelId),
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

    public async Task ClearSessionAsync(long clanId, long channelId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(clanId, channelId).ConfigureAwait(false);
        var historyId = await GetPlayHistoryIdAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
        var db = _redis.Db;
        var keys = new List<RedisKey>
        {
            RedisKeyNames.Player(clanId, channelId),
            RedisKeyNames.Queue(clanId, channelId),
            RedisKeyNames.LegacyPlayer(clanId),
            RedisKeyNames.LegacyQueue(clanId),
        };
        if (historyId is long hid)
        {
            keys.Add(RedisKeyNames.VoteSkip(clanId, hid));
        }

        await db.KeyDeleteAsync(keys.ToArray()).ConfigureAwait(false);
        await db.SetRemoveAsync(RedisKeyNames.ActiveSessions, new PlayerSessionKey(clanId, channelId).IndexValue)
            .ConfigureAwait(false);
        await db.SetRemoveAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
    }

    private const string RemovePendingLua =
        """
        local queue = KEYS[1]
        local source = ARGV[1]
        local ext = ARGV[2]
        local ttl = tonumber(ARGV[3])
        local list = redis.call('LRANGE', queue, 0, -1)
        redis.call('DEL', queue)
        local removed = 0
        for i = 1, #list do
          local ok, obj = pcall(cjson.decode, list[i])
          if removed == 0 and ok and obj['source'] == source and obj['externalId'] == ext then
            removed = 1
          else
            redis.call('RPUSH', queue, list[i])
          end
        end
        if ttl then
          redis.call('EXPIRE', queue, ttl)
        end
        return removed
        """;

    public async Task RemovePendingMatchingAsync(
        long clanId,
        long channelId,
        Func<QueuedTrackPayload, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        var items = await SnapshotQueueAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
        QueuedTrackPayload? match = null;
        foreach (var item in items)
        {
            if (predicate(item))
            {
                match = item;
                break;
            }
        }

        if (match is null)
        {
            return;
        }

        await _redis.Db.ScriptEvaluateAsync(
            RemovePendingLua,
            [RedisKeyNames.Queue(clanId, channelId)],
            [
                match.Source,
                match.ExternalId,
                ((int)RedisKeyNames.PlayerTtl.TotalSeconds).ToString(),
            ]).ConfigureAwait(false);
    }

    private async Task EnsureMigratedAsync(long clanId, long channelId)
    {
        var player = RedisKeyNames.Player(clanId, channelId);
        var queue = RedisKeyNames.Queue(clanId, channelId);
        if (await _redis.Db.KeyExistsAsync(player).ConfigureAwait(false)
            || await _redis.Db.ListLengthAsync(queue).ConfigureAwait(false) > 0)
        {
            return;
        }

        if (!await LegacySessionMatchesAsync(clanId, channelId).ConfigureAwait(false))
        {
            return;
        }

        var db = _redis.Db;
        var legacyPlayer = RedisKeyNames.LegacyPlayer(clanId);
        var legacyQueue = RedisKeyNames.LegacyQueue(clanId);
        if (await db.KeyExistsAsync(legacyPlayer).ConfigureAwait(false)
            && !await db.KeyExistsAsync(player).ConfigureAwait(false))
        {
            await db.KeyRenameAsync(legacyPlayer, player).ConfigureAwait(false);
        }

        if (await db.KeyExistsAsync(legacyQueue).ConfigureAwait(false)
            && await db.ListLengthAsync(queue).ConfigureAwait(false) == 0)
        {
            await db.KeyRenameAsync(legacyQueue, queue).ConfigureAwait(false);
        }

        await db.SetRemoveAsync(RedisKeyNames.ActiveClans, clanId).ConfigureAwait(false);
        await db.SetAddAsync(RedisKeyNames.ActiveSessions, new PlayerSessionKey(clanId, channelId).IndexValue)
            .ConfigureAwait(false);
    }

    private static bool TryParsePlayerKey(string? key, string entity, out PlayerSessionKey session)
    {
        session = default;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var prefix = $"{RedisKeyNames.Prefix}{entity}:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return PlayerSessionKey.TryParse(key[prefix.Length..], out session);
    }
}
