using Mezube.Playback;

namespace Mezube.Music;

/// <summary>
/// One live playback session per clan. Queue Redis keys stay
/// <c>player:{clanId}:{channelId}</c>; further requests join that bound channel
/// instead of starting a second SFU publisher.
/// </summary>
internal static class ClanSessionBinder
{
    public static readonly TimeSpan IdleDefaultResume = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan IdleDisconnect = TimeSpan.FromMinutes(10);

    public static bool IsLive(ClanPlaybackSession state)
        => state.IsPlaying || state.PumpRunning || state.Queue.TotalCount > 0 || state.Queue.Current is not null;

    public static PlaybackTarget? BoundTarget(ClanPlaybackSession state)
    {
        if (state.Target is { ChannelId: not 0 } target)
        {
            return target;
        }

        if (state.ClanId is long clanId && state.ChannelId != 0)
        {
            return new PlaybackTarget(clanId, state.ChannelId, ChannelLabel: state.Target?.ChannelLabel);
        }

        return null;
    }

    public static QueuedPlay Pin(ClanPlaybackSession state, QueuedPlay play)
    {
        if (!IsLive(state) || BoundTarget(state) is not { } bound)
        {
            return play;
        }

        return play.Target.ChannelId == bound.ChannelId ? play : play with { Target = bound };
    }

    public static IReadOnlyList<QueuedPlay> PinAll(ClanPlaybackSession state, IReadOnlyList<QueuedPlay> plays)
    {
        if (plays.Count == 0 || !IsLive(state))
        {
            return plays;
        }

        var pinned = new QueuedPlay[plays.Count];
        for (var i = 0; i < plays.Count; i++)
        {
            pinned[i] = Pin(state, plays[i]);
        }

        return pinned;
    }

    /// <summary>
    /// Queue empty: 5 min then default playlist if armed; otherwise 10 min then
    /// SFU disconnect. After a failed default resume, wait the remaining 5 min.
    /// !stop already tore the publisher down — keep a short in-memory TTL.
    /// </summary>
    public static TimeSpan IdleDelay(ClanPlaybackSession state)
    {
        if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
        {
            return IdleDefaultResume;
        }

        if (state.IdleAwaitingDisconnect)
        {
            return IdleDisconnect - IdleDefaultResume;
        }

        if (!state.DefaultAutoplayArmed)
        {
            return IdleDisconnect;
        }

        return IdleDefaultResume;
    }
}
