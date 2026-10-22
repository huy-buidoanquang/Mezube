using System.Diagnostics;

namespace Mezube.Streaming;

/// <summary>
/// Wall-clock RTP pacer for Opus at 48 kHz.
/// SIPSorcery <c>SendAudio(durationRtpUnits, payload)</c> advances the RTP timestamp
/// by <c>durationRtpUnits</c>; RFC 7587 says that clock is 48000 for Opus.
/// </summary>
internal sealed class OpusRtpPacer
{
    public const int SampleRate = 48000;
    private const uint FrameSamples = 960;

    /// <summary>One 20 ms frame. Behind this, send immediately (NetEq absorbs).</summary>
    public static readonly TimeSpan MaxSoftBehind = TimeSpan.FromTicks(960L * TimeSpan.TicksPerSecond / SampleRate);

    /// <summary>Two frames. Behind this, skip the debt in the RTP timeline instead of bursting.</summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromTicks(1920L * TimeSpan.TicksPerSecond / SampleRate);

    private readonly Stopwatch _clock = new();
    private long _sentSamples;

    public long SentSamples => _sentSamples;

    public TimeSpan Elapsed => _clock.Elapsed;

    public TimeSpan DueElapsed
        => TimeSpan.FromTicks(checked(_sentSamples * TimeSpan.TicksPerSecond / SampleRate));

    public TimeSpan Behind
    {
        get
        {
            var behind = Elapsed - DueElapsed;
            return behind > TimeSpan.Zero ? behind : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Time until <see cref="SentSamples"/> of audio should have been sent.
    /// Zero means the next packet is due immediately.
    /// </summary>
    public TimeSpan DueIn
    {
        get
        {
            var remaining = DueElapsed - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public void Start()
    {
        _sentSamples = 0;
        _clock.Restart();
    }

    public void Account(uint samples)
    {
        if (samples == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        _sentSamples = checked(_sentSamples + samples);
    }

    public uint LagSamples()
    {
        var excess = Behind - MaxCatchUp;
        if (excess <= TimeSpan.Zero)
        {
            return 0;
        }

        var frames = excess.Ticks * SampleRate / (FrameSamples * TimeSpan.TicksPerSecond);
        return checked((uint)(frames * FrameSamples));
    }

    public void Wait(CancellationToken cancellationToken)
    {
        while (DueIn > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HighResolutionWait.Until(_clock, DueElapsed, cancellationToken);
        }
    }
}
