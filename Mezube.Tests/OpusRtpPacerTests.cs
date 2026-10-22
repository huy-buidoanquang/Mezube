using Mezube.Streaming;

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
    public void Start_begins_a_new_timeline()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960 * 10);
        pacer.Start();
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
    public void LagSamples_is_zero_while_on_schedule()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960 * 10);

        Assert.Equal(0u, pacer.LagSamples());
    }

    [Fact]
    public async Task LagSamples_after_a_stall_moves_the_timeline_back_to_wall_clock()
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();
        pacer.Account(960);
        await Task.Delay(150);

        var lag = pacer.LagSamples();
        Assert.True(lag >= 960 * 4, $"lag {lag}");
        Assert.Equal(0u, lag % 960);

        pacer.Account(lag);
        Assert.InRange(pacer.Behind, TimeSpan.Zero, OpusRtpPacer.MaxCatchUp + TimeSpan.FromMilliseconds(25));
    }
}
