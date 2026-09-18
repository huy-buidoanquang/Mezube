using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mezube.Sfu;

/// <summary>
/// Waits until a Stopwatch deadline with ~1 ms error.
/// <see cref="Task.Delay"/> on Windows uses the ~15.6 ms timer interrupt; that
/// jitter is what turns a 20 ms Opus cadence into clustered arrivals after a
/// long listen. The SFU forwards audio immediately (no jitter buffer, no RTX),
/// so the publisher has to hit the deadline itself.
/// </summary>
internal static class HighResolutionWait
{
    private static readonly TimeSpan SpinWindow = TimeSpan.FromMilliseconds(1.5);

    public static void Until(Stopwatch clock, TimeSpan dueElapsed, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!clock.IsRunning)
            {
                return;
            }

            var remaining = dueElapsed - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining <= SpinWindow)
            {
                while (clock.Elapsed < dueElapsed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.SpinWait(64);
                }

                return;
            }

            var sleep = remaining - SpinWindow;
            if (sleep > TimeSpan.FromMilliseconds(8))
            {
                sleep = TimeSpan.FromMilliseconds(8);
            }

            SleepSlice(sleep, cancellationToken);
        }
    }

    private static void SleepSlice(TimeSpan sleep, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && TryWaitWindows(sleep, cancellationToken))
        {
            return;
        }

        var ms = (int)Math.Clamp(sleep.TotalMilliseconds, 1, 50);
        if (cancellationToken.WaitHandle.WaitOne(ms))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool TryWaitWindows(TimeSpan sleep, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var due100Ns = unchecked((long)(-Math.Max(1, sleep.Ticks / 10)));
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (handle == IntPtr.Zero)
        {
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!SetWaitableTimer(handle, in due100Ns, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                return false;
            }

            var timeoutMs = (int)Math.Clamp(sleep.TotalMilliseconds + 5, 1, 60_000);
            var signaled = WaitForSingleObject(handle, (uint)timeoutMs);
            cancellationToken.ThrowIfCancellationRequested();
            return signaled == WaitObject0 || signaled == WaitTimeout;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer,
        in long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
