using System.Collections.Concurrent;
using Mezube.Bot;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Redis;

namespace Mezube.Music;

/// <summary>
/// Voice presence: L1 memory + Redis write-through. Default stream channel: memory + Postgres.
/// </summary>
public sealed class BindStore
{
    private readonly IVoiceBindStore _voice;
    private readonly IClanSettingsRepository _settings;
    private readonly ConcurrentDictionary<(long ClanId, long UserId), (long ChannelId, long Ticks)> _voiceByClanUser = new();
    private static readonly TimeSpan VoiceL1Ttl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<long, long> _defaultStreamByClan = new();

    public BindStore(BotOptions options, IVoiceBindStore voice, IClanSettingsRepository settings)
    {
        _voice = voice;
        _settings = settings;
        if (options.DefaultStreamChannelId != 0)
        {
            var clanKey = options.DefaultClanId != 0 ? options.DefaultClanId : 0;
            _defaultStreamByClan[clanKey] = options.DefaultStreamChannelId;
        }
    }

    public bool TryGetDefaultStreamChannel(long clanId, out long streamChannelId)
    {
        if (_defaultStreamByClan.TryGetValue(clanId, out streamChannelId))
        {
            return true;
        }

        return _defaultStreamByClan.TryGetValue(0, out streamChannelId);
    }

    public async Task<long?> TryGetDefaultStreamChannelAsync(long clanId, CancellationToken cancellationToken = default)
    {
        if (TryGetDefaultStreamChannel(clanId, out var cached))
        {
            return cached;
        }

        var row = await _settings.TryGetAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (row?.DefaultStreamChannelId is long id && id != 0)
        {
            _defaultStreamByClan[clanId] = id;
            return id;
        }

        return null;
    }

    public void SetDefaultStreamChannel(long clanId, long streamChannelId)
    {
        _defaultStreamByClan[clanId] = streamChannelId;
        _ = _settings.UpsertDefaultStreamChannelAsync(clanId, streamChannelId);
    }

    public void SetUserVoiceChannel(long clanId, long userId, long voiceChannelId)
    {
        _voiceByClanUser[(clanId, userId)] = (voiceChannelId, DateTime.UtcNow.Ticks);
        _ = _voice.SetUserVoiceChannelAsync(clanId, userId, voiceChannelId);
    }

    public void ClearUserVoiceChannel(long clanId, long userId)
    {
        _voiceByClanUser.TryRemove((clanId, userId), out _);
        _ = _voice.ClearUserVoiceChannelAsync(clanId, userId);
    }

    public bool TryGetUserVoiceChannel(long clanId, long userId, out long voiceChannelId)
    {
        voiceChannelId = 0;
        if (!_voiceByClanUser.TryGetValue((clanId, userId), out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow.Ticks - entry.Ticks > VoiceL1Ttl.Ticks)
        {
            _voiceByClanUser.TryRemove((clanId, userId), out _);
            return false;
        }

        voiceChannelId = entry.ChannelId;
        return true;
    }

    public async Task HydrateVoiceFromRedisAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _voice.SnapshotAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (clanId, userId, channelId) in snapshot)
        {
            _voiceByClanUser[(clanId, userId)] = (channelId, DateTime.UtcNow.Ticks);
        }
    }

    public Task<long> CountVoiceUsersAsync(long clanId, CancellationToken cancellationToken = default)
        => CountVoiceUsersAsync(clanId, playbackChannelId: null, cancellationToken);

    public Task<long> CountVoiceUsersAsync(
        long clanId,
        long? playbackChannelId,
        CancellationToken cancellationToken = default)
    {
        PruneExpiredVoice();
        var local = _voiceByClanUser.Count(k =>
            k.Key.ClanId == clanId
            && (playbackChannelId is null || k.Value.ChannelId == playbackChannelId.Value));
        if (local > 0)
        {
            return Task.FromResult((long)local);
        }

        return _voice.CountAsync(clanId, cancellationToken);
    }

    private void PruneExpiredVoice()
    {
        var cutoff = DateTime.UtcNow.Ticks - VoiceL1Ttl.Ticks;
        foreach (var (key, value) in _voiceByClanUser)
        {
            if (value.Ticks < cutoff)
            {
                _voiceByClanUser.TryRemove(key, out _);
            }
        }
    }
}
