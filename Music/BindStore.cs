using System.Collections.Concurrent;
using Mezube.Bot;

namespace Mezube.Music;

/// <summary>
/// In-memory voice presence (per clan+user) + optional default stream channel map.
/// </summary>
public sealed class BindStore
{
    private readonly ConcurrentDictionary<(long ClanId, long UserId), long> _voiceByClanUser = new();
    private readonly ConcurrentDictionary<long, long> _defaultStreamByClan = new();

    public BindStore(BotOptions options)
    {
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

    public void SetDefaultStreamChannel(long clanId, long streamChannelId)
        => _defaultStreamByClan[clanId] = streamChannelId;

    public void SetUserVoiceChannel(long clanId, long userId, long voiceChannelId)
        => _voiceByClanUser[(clanId, userId)] = voiceChannelId;

    public void ClearUserVoiceChannel(long clanId, long userId)
        => _voiceByClanUser.TryRemove((clanId, userId), out _);

    public bool TryGetUserVoiceChannel(long clanId, long userId, out long voiceChannelId)
        => _voiceByClanUser.TryGetValue((clanId, userId), out voiceChannelId);
}
