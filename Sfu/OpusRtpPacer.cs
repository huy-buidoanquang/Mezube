using System.Diagnostics;

namespace Mezube.Sfu;

/// <summary>
/// Wall-clock RTP pacer for Opus at 48 kHz.
/// SIPSorcery <c>SendAudio(durationRtpUnits, payload)</c> advances the RTP timestamp
/// by <c>durationRtpUnits</c>; RFC 7587 says that clock is 48000 for Opus.
/// mezon-sfu forwards audio immediately (no jitter buffer, no audio RTX). A
/// catch-up burst after GC/I/O is forwarded as a cluster; the browser JB then
/// overruns and underruns. This pacer never bursts: a stall bigger than two
/// frames is dropped as lag, then the 20 ms cadence resumes.
/// </summary>
internal sealed class OpusRtpPacer
{
    public const int SampleRate = 48000;

    /// <summary>One 20 ms frame. Behind this, send immediately (NetEq absorbs).</summary>
    public static readonly TimeSpan MaxSoftBehind = TimeSpan.FromTicks(960L * TimeSpan.TicksPerSecond / SampleRate);

    /// <summary>Two frames. Behind this, drop the debt instead of bursting.</summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromTicks(1920L * TimeSpan.TicksPerSecond / SampleRate);

    private readonly Stopwatch _clock = new();
    private TimeSpan _skipped;
    private long _sentSamples;

    public long SentSamples => _sentSamples;

    public bool IsRunning => _clock.IsRunning;

    public TimeSpan Elapsed
    {
        get
        {
            var elapsed = _clock.Elapsed - _skipped;
            return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }
    }

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
        _skipped = TimeSpan.Zero;
        _clock.Restart();
    }

    public void Reset() => Start();

    public void Pause()
    {
        if (_clock.IsRunning)
        {
            _clock.Stop();
        }
    }

    public void Resume()
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }
    }

    public void Account(uint samples)
    {
        if (samples == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        _sentSamples = checked(_sentSamples + samples);
    }

    /// <summary>
    /// If wall-clock is more than <see cref="MaxCatchUp"/> ahead of RTP, shift
    /// the origin so we do not dump the queued packets in one burst.
    /// Call after <see cref="Account"/> of a packet that was sent late.
    /// </summary>
    public bool ReleaseCatchUp()
    {
        var behind = Behind;
        if (behind <= MaxCatchUp)
        {
            return false;
        }

        _skipped += behind;
        return true;
    }

    public void Wait(CancellationToken cancellationToken)
    {
        while (DueIn > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_clock.IsRunning)
            {
                if (cancellationToken.WaitHandle.WaitOne(5))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                continue;
            }

            HighResolutionWait.Until(_clock, DueElapsed + _skipped, cancellationToken);
        }
    }
}
