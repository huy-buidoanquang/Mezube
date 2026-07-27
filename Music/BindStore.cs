using System.Collections.Concurrent;
using Mezube.Bot;

namespace Mezube.Music;

/// <summary>
/// In-memory voice presence + optional default stream channel from config.
/// Stream targets are normally chosen via channel hashtags.
/// </summary>
public sealed class BindStore
{
    private readonly BotOptions _options;
    private readonly ConcurrentDictionary<long, long> _voiceByUser = new();

    public BindStore(BotOptions options)
    {
        _options = options;
    }

    public bool TryGetDefaultStreamChannel(long clanId, out long streamChannelId)
    {
        if (_options.DefaultStreamChannelId == 0)
        {
            streamChannelId = 0;
            return false;
        }

        if (_options.DefaultClanId != 0 && _options.DefaultClanId != clanId)
        {
            streamChannelId = 0;
            return false;
        }

        streamChannelId = _options.DefaultStreamChannelId;
        return true;
    }

    public void SetUserVoiceChannel(long userId, long voiceChannelId)
        => _voiceByUser[userId] = voiceChannelId;

    public void ClearUserVoiceChannel(long userId)
        => _voiceByUser.TryRemove(userId, out _);

    public bool TryGetUserVoiceChannel(long userId, out long voiceChannelId)
        => _voiceByUser.TryGetValue(userId, out voiceChannelId);
}
