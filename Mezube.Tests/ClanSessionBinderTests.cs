using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Playback;

namespace Mezube.Tests;

public sealed class ClanSessionBinderTests
{
    [Fact]
    public void Pin_rewrites_further_requests_onto_the_live_channel()
    {
        using var state = LiveSession(clanId: 10, channelId: 21, label: "a");
        var play = Play(10, 22, "b");

        var pinned = ClanSessionBinder.Pin(state, play);

        Assert.Equal(21, pinned.Target.ChannelId);
        Assert.Equal("a", pinned.Target.ChannelLabel);
        Assert.Equal(10, pinned.Target.ClanId);
        Assert.Equal(22, play.Target.ChannelId);
    }

    [Fact]
    public void Pin_leaves_target_alone_when_clan_is_idle()
    {
        using var state = new ClanPlaybackSession
        {
            ClanId = 10,
            ChannelId = 21,
            Target = new PlaybackTarget(10, 21, ChannelLabel: "a"),
        };
        var play = Play(10, 22, "b");

        Assert.False(ClanSessionBinder.IsLive(state));
        Assert.Equal(22, ClanSessionBinder.Pin(state, play).Target.ChannelId);
    }

    [Fact]
    public void PinAll_does_not_touch_another_clan_session()
    {
        using var a = LiveSession(10, 21, "a");
        using var b = LiveSession(11, 21, "b");

        var pinnedA = ClanSessionBinder.PinAll(a, [Play(10, 99, "x")]);
        var pinnedB = ClanSessionBinder.PinAll(b, [Play(11, 21, "b")]);

        Assert.Equal(21, pinnedA[0].Target.ChannelId);
        Assert.Equal(10, pinnedA[0].Target.ClanId);
        Assert.Equal(21, pinnedB[0].Target.ChannelId);
        Assert.Equal(11, pinnedB[0].Target.ClanId);
        Assert.Equal(21, a.ChannelId);
        Assert.Equal(21, b.ChannelId);
    }

    [Fact]
    public void IdleDelay_is_five_minutes_when_default_playlist_is_armed()
    {
        using var state = new ClanPlaybackSession { DefaultAutoplayArmed = true };
        Assert.Equal(TimeSpan.FromMinutes(5), ClanSessionBinder.IdleDelay(state));
    }

    [Fact]
    public void IdleDelay_is_ten_minutes_when_no_default_playlist()
    {
        using var state = new ClanPlaybackSession { DefaultAutoplayArmed = false };
        Assert.Equal(TimeSpan.FromMinutes(10), ClanSessionBinder.IdleDelay(state));
    }

    [Fact]
    public void IdleDelay_after_failed_default_resume_waits_remaining_five_minutes()
    {
        using var state = new ClanPlaybackSession
        {
            DefaultAutoplayArmed = true,
            IdleAwaitingDisconnect = true,
        };
        Assert.Equal(TimeSpan.FromMinutes(5), ClanSessionBinder.IdleDelay(state));
    }

    [Fact]
    public void IdleDelay_after_stop_keeps_short_ttl_and_does_not_use_disconnect_window()
    {
        using var state = new ClanPlaybackSession
        {
            DefaultAutoplayArmed = false,
            LastDestroyReason = PlayerDestroyReason.UserStop,
        };
        Assert.Equal(TimeSpan.FromMinutes(5), ClanSessionBinder.IdleDelay(state));
    }

    [Fact]
    public void BoundTarget_stays_on_the_live_channel_not_a_second_room()
    {
        using var state = LiveSession(10, 21, "a");
        state.ChannelId = 21;

        var bound = ClanSessionBinder.BoundTarget(state);

        Assert.NotNull(bound);
        Assert.Equal(21, bound.ChannelId);
        Assert.Equal(10, bound.ClanId);
    }

    private static ClanPlaybackSession LiveSession(long clanId, long channelId, string label)
        => new()
        {
            ClanId = clanId,
            ChannelId = channelId,
            IsPlaying = true,
            Target = new PlaybackTarget(clanId, channelId, ChannelLabel: label),
        };

    private static QueuedPlay Play(long clanId, long channelId, string label)
        => new(
            new TrackInfoEntity { Title = label, MediaUrl = "https://example.com/" + label },
            new PlaybackTarget(clanId, channelId, ChannelLabel: label));
}
