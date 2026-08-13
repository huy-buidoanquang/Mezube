using System.Diagnostics;
using System.Text;

namespace Mezube.Media;

public readonly record struct ChildProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Spawn a child process, drain both pipes, kill the tree on cancel/timeout.
/// </summary>
public static class ChildProcessRunner
{
    public static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultTranscodeTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ChildProcessResult> RunAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return new ChildProcessResult(-1, string.Empty, "Failed to start process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Process '{psi.FileName}' exceeded {timeout}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ChildProcessResult(process.ExitCode, stdout, stderr);
    }

    public static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task DrainStdoutDiscardAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
