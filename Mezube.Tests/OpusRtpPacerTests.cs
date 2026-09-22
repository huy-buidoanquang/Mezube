using Mezube.Sfu;

namespace Mezube.Tests;

public sealed class OpusRtpPacerTests
{
    [Fact]
    public void Twenty_ms_opus_frame_is_exactly_960_samples_at_48k()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromTicks(960L * TimeSpan.TicksPerSecond / OpusRtpPacer.SampleRate));
        Assert.Equal(960u, OpusPacket.GetDurationSamples(new byte[] { 0x08 }));
    }

    [Fact]
    public void DueIn_tracks_cumulative_samples_not_per_packet_delay()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        Assert.InRange(pacer.DueIn.TotalMilliseconds, 18, 20);

        pacer.Account(960);
        Assert.InRange(pacer.DueIn.TotalMilliseconds, 38, 40);
    }

    [Fact]
    public async Task Behind_wall_clock_does_not_delay()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        await Task.Delay(50);

        Assert.Equal(TimeSpan.Zero, pacer.DueIn);
    }

    [Fact]
    public async Task Pause_freezes_the_playout_clock()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        pacer.Pause();
        var due = pacer.DueIn;
        await Task.Delay(50);

        Assert.InRange(Math.Abs(pacer.DueIn.TotalMilliseconds - due.TotalMilliseconds), 0, 3);
        Assert.False(pacer.IsRunning);
    }

    [Fact]
    public void Reset_starts_a_new_timeline()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960 * 10);
        pacer.Reset();
        pacer.Account(960);

        Assert.InRange(pacer.DueIn.TotalMilliseconds, 18, 20);
        Assert.Equal(960, pacer.SentSamples);
    }

    [Fact]
    public void Account_rejects_zero_samples()
    {
        var pacer = new OpusRtpPacer();
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.Account(0));
    }

    [Fact]
    public async Task ReleaseCatchUp_after_stall_keeps_the_next_frame_on_cadence()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        await Task.Delay(80);

        Assert.True(pacer.Behind > OpusRtpPacer.MaxCatchUp);
        Assert.Equal(TimeSpan.Zero, pacer.DueIn);
        Assert.True(pacer.ReleaseCatchUp());
        Assert.InRange(pacer.Behind.TotalMilliseconds, 0, 5);

        pacer.Account(960);
        Assert.InRange(pacer.DueIn.TotalMilliseconds, 18, 22);
    }

    [Fact]
    public async Task Soft_behind_does_not_drop_lag()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        await Task.Delay(25);

        Assert.True(pacer.Behind > TimeSpan.Zero);
        Assert.True(pacer.Behind <= OpusRtpPacer.MaxCatchUp);
        Assert.False(pacer.ReleaseCatchUp());
    }
}
