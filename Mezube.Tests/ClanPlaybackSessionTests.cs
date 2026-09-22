using Mezube.Music;

namespace Mezube.Tests;

public sealed class ClanPlaybackSessionTests
{
    [Fact]
    public async Task TryEnterPump_second_caller_fails_without_releasing()
    {
        using var state = new ClanPlaybackSession();
        Assert.True(await state.TryEnterPumpAsync());
        Assert.True(state.PumpRunning);
        Assert.False(await state.TryEnterPumpAsync());
        Assert.True(state.PumpRunning);
        state.ExitPump();
        Assert.False(state.PumpRunning);
        Assert.True(await state.TryEnterPumpAsync());
        state.ExitPump();
    }

    [Fact]
    public async Task ScheduleIdleDestroy_cancels_previous_callback()
    {
        using var state = new ClanPlaybackSession();
        var first = 0;
        state.ScheduleIdleDestroy(TimeSpan.FromMilliseconds(80), 1, _ => Interlocked.Increment(ref first));
        state.ScheduleIdleDestroy(TimeSpan.FromMilliseconds(20), 2, _ => { });
        await Task.Delay(120);
        Assert.Equal(0, Volatile.Read(ref first));
    }

    [Fact]
    public async Task Idle_callback_ignored_after_generation_bump()
    {
        using var state = new ClanPlaybackSession();
        var gen = state.Generation;
        var accepted = 0;
        state.ScheduleIdleDestroy(TimeSpan.FromMilliseconds(30), gen, g =>
        {
            if (g == state.Generation)
            {
                Interlocked.Increment(ref accepted);
            }
        });
        state.BumpGeneration();
        await Task.Delay(80);
        Assert.Equal(0, Volatile.Read(ref accepted));
    }

    [Fact]
    public void Play_slot_claim_does_not_double_release()
    {
        using var slots = new SemaphoreSlim(1, 1);
        using var a = new ClanPlaybackSession();
        using var b = new ClanPlaybackSession();

        Assert.True(TryClaim(slots, a));
        Assert.False(TryClaim(slots, b));
        // Failed claim must not release the live slot.
        ReleaseIfHeld(slots, b);
        Assert.Equal(0, slots.CurrentCount);
        ReleaseIfHeld(slots, a);
        Assert.Equal(1, slots.CurrentCount);
    }

    [Fact]
    public void Two_channel_sessions_can_hold_slots_when_cap_allows()
    {
        using var slots = new SemaphoreSlim(2, 2);
        using var a = new ClanPlaybackSession { ChannelId = 1 };
        using var b = new ClanPlaybackSession { ChannelId = 2 };

        Assert.True(TryClaim(slots, a));
        Assert.True(TryClaim(slots, b));
        Assert.Equal(0, slots.CurrentCount);
        ReleaseIfHeld(slots, a);
        ReleaseIfHeld(slots, b);
        Assert.Equal(2, slots.CurrentCount);
    }

    [Theory]
    [InlineData(PlayerDestroyReason.UserStop)]
    [InlineData(PlayerDestroyReason.QueueEmpty)]
    [InlineData(PlayerDestroyReason.SfuFailed)]
    [InlineData(PlayerDestroyReason.TrackFailed)]
    [InlineData(PlayerDestroyReason.IdleTimeout)]
    [InlineData(PlayerDestroyReason.ModeConflict)]
    [InlineData(PlayerDestroyReason.RoomConflict)]
    [InlineData(PlayerDestroyReason.CapacityExceeded)]
    public void New_playback_clears_previous_terminal_reason(PlayerDestroyReason reason)
    {
        using var state = new ClanPlaybackSession { LastDestroyReason = reason };

        state.ResetTerminalDestroyReason();

        Assert.Equal(PlayerDestroyReason.None, state.LastDestroyReason);
    }

    [Theory]
    [InlineData(PlayerDestroyReason.None)]
    [InlineData(PlayerDestroyReason.Skip)]
    [InlineData(PlayerDestroyReason.Seek)]
    public void Reset_terminal_destroy_reason_preserves_active_control_reason(PlayerDestroyReason reason)
    {
        using var state = new ClanPlaybackSession { LastDestroyReason = reason };

        state.ResetTerminalDestroyReason();

        Assert.Equal(reason, state.LastDestroyReason);
    }

    [Fact]
    public void Natural_eof_never_selects_sfu_stop_even_if_reason_is_stale()
        => Assert.False(MusicPlayer.ShouldStopSfuAfterTrack(
            trackEndedNaturally: true,
            PlayerDestroyReason.UserStop));

    [Fact]
    public void Explicit_stop_cancellation_selects_sfu_stop()
        => Assert.True(MusicPlayer.ShouldStopSfuAfterTrack(
            trackEndedNaturally: false,
            PlayerDestroyReason.UserStop));

    private static bool TryClaim(SemaphoreSlim slots, ClanPlaybackSession state)
    {
        if (state.HoldsPlaySlot)
        {
            return true;
        }

        if (!slots.Wait(0))
        {
            return false;
        }

        state.HoldsPlaySlot = true;
        return true;
    }

    private static void ReleaseIfHeld(SemaphoreSlim slots, ClanPlaybackSession state)
    {
        if (!state.HoldsPlaySlot)
        {
            return;
        }

        state.HoldsPlaySlot = false;
        slots.Release();
    }
}
