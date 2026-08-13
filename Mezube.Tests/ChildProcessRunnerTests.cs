using System.Diagnostics;
using Mezube.Media;

namespace Mezube.Tests;

public sealed class ChildProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_kills_process_tree_on_cancel()
    {
        var psi = CreateLongRunningPsi();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChildProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), cts.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"cancel took {started.Elapsed}");
    }

    private static ProcessStartInfo CreateLongRunningPsi()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "ping",
                ArgumentList = { "-n", "30", "127.0.0.1" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "30" },
        };
    }
}
