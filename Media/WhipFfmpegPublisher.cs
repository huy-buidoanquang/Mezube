using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Media;

/// <summary>
/// Pushes Opus audio to a LiveKit WHIP endpoint via ffmpeg's <c>whip</c> muxer.
/// </summary>
public sealed class WhipFfmpegPublisher
{
    private readonly BotOptions _options;
    private readonly ILogger<WhipFfmpegPublisher> _logger;
    private readonly ConcurrentDictionary<string, ActivePublish> _byRoom = new(StringComparer.Ordinal);
    private readonly Lazy<bool> _whipSupported;

    public WhipFfmpegPublisher(BotOptions options, ILogger<WhipFfmpegPublisher> logger)
    {
        _options = options;
        _logger = logger;
        _whipSupported = new Lazy<bool>(ProbeWhipMuxer);
    }

    public bool IsAvailable => _whipSupported.Value;

    /// <summary>
    /// Starts ffmpeg WHIP publish and returns once the process is alive past handshake window
    /// (or sooner if stderr indicates a ready session). The background task runs until EOF/cancel.
    /// </summary>
    public async Task StartUntilPublishingAsync(
        string roomName,
        string mediaUrl,
        string whipUrl,
        string authorizationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(whipUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationToken);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "ffmpeg WHIP muxer is not available. Upgrade ffmpeg (needs muxer=whip) or disable Mezube:StnWhipEnabled.");
        }

        await StopAsync(roomName).ConfigureAwait(false);

        var endpoint = NormalizeLiveKitWhipUrl(whipUrl);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Real-time pace from CDN/local Ogg → Opus RTP over WHIP.
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("info");
        psi.ArgumentList.Add("-re");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaUrl);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libopus");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("96k");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-application");
        psi.ArgumentList.Add("audio");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("whip");
        psi.ArgumentList.Add("-authorization");
        psi.ArgumentList.Add(authorizationToken);
        psi.ArgumentList.Add("-handshake_timeout");
        psi.ArgumentList.Add("10000");
        psi.ArgumentList.Add(endpoint);

        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start ffmpeg at {_options.FfmpegPath}");
        }
        catch (Win32Exception ex)
        {
            lifetime.Dispose();
            throw new InvalidOperationException($"ffmpeg not found at {_options.FfmpegPath}", ex);
        }

        var active = new ActivePublish(process, lifetime, publishing, ended);
        _byRoom[roomName] = active;

        _ = Task.Run(() => PumpStderrAsync(roomName, process, publishing, lifetime.Token), CancellationToken.None);
        _ = Task.Run(() => AwaitExitAsync(roomName, process, publishing, ended, lifetime), CancellationToken.None);

        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var ready = publishing.Task;
            var failed = ended.Task;
            var winner = await Task.WhenAny(ready, failed).WaitAsync(readyTimeout.Token).ConfigureAwait(false);
            if (winner == failed)
            {
                var code = await failed.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"WHIP ffmpeg exited before publishing room={roomName} exit={code}");
            }

            await ready.ConfigureAwait(false);
            _logger.LogDebug("WHIP publishing ready room={Room} endpoint={Endpoint}", roomName, endpoint);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Handshake window elapsed; if process still alive treat as publishing.
            if (process.HasExited)
            {
                await StopAsync(roomName).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"WHIP ffmpeg failed during handshake room={roomName} exit={process.ExitCode}");
            }

            publishing.TrySetResult();
            _logger.LogDebug(
                "WHIP publishing assumed ready after handshake wait room={Room} endpoint={Endpoint}",
                roomName,
                endpoint);
        }
        catch
        {
            await StopAsync(roomName).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> WaitUntilEndedAsync(string roomName, CancellationToken cancellationToken = default)
    {
        if (!_byRoom.TryGetValue(roomName, out var active))
        {
            return "stopped";
        }

        try
        {
            var code = await active.Ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _byRoom.TryRemove(roomName, out _);
            return code == 0 ? "completed" : "failed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the entry so StopAsync can kill ffmpeg.
            return "stopped";
        }
    }

    public async Task StopAsync(string roomName)
    {
        if (!_byRoom.TryRemove(roomName, out var active))
        {
            return;
        }

        try
        {
            active.Lifetime.Cancel();
        }
        catch
        {
        }

        try
        {
            if (!active.Process.HasExited)
            {
                active.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WHIP ffmpeg kill failed room={Room}", roomName);
        }

        try
        {
            await active.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            active.Process.Dispose();
        }
        catch
        {
        }

        active.Lifetime.Dispose();
    }

    /// <summary>
    /// STN may return <c>.../whip</c>; LiveKit server WHIP is <c>.../whip/v1</c>.
    /// </summary>
    public static string NormalizeLiveKitWhipUrl(string whipUrl)
    {
        var trimmed = whipUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/whip/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/whip", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/v1";
        }

        return trimmed + "/whip/v1";
    }

    private async Task PumpStderrAsync(
        string roomName,
        Process process,
        TaskCompletionSource publishing,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("WHIP ffmpeg room={Room}: {Line}", roomName, line);
                }
                else
                {
                    _logger.LogTrace("WHIP ffmpeg room={Room}: {Line}", roomName, line);
                }

                if (!publishing.Task.IsCompleted && LooksPublishing(line))
                {
                    publishing.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WHIP ffmpeg stderr pump ended room={Room}", roomName);
        }
    }

    private async Task AwaitExitAsync(
        string roomName,
        Process process,
        TaskCompletionSource publishing,
        TaskCompletionSource<int> ended,
        CancellationTokenSource lifetime)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var code = process.ExitCode;
            if (code != 0 && !publishing.Task.IsCompleted)
            {
                // Ensure StartUntilPublishingAsync observes early failure.
                publishing.TrySetException(
                    new InvalidOperationException($"WHIP ffmpeg exited early room={roomName} exit={code}"));
            }

            ended.TrySetResult(code);
            _logger.LogDebug("WHIP ffmpeg exited room={Room} code={Code}", roomName, code);
        }
        catch (Exception ex)
        {
            ended.TrySetException(ex);
        }
        finally
        {
            try
            {
                lifetime.Cancel();
            }
            catch
            {
            }
        }
    }

    private static bool LooksPublishing(string line)
    {
        // ffmpeg whip muxer wording varies by version; accept common readiness hints.
        return line.Contains("WHIP", StringComparison.OrdinalIgnoreCase)
               && (line.Contains("publish", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("connected", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("established", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("DTLS", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("ICE", StringComparison.OrdinalIgnoreCase));
    }

    private bool ProbeWhipMuxer()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.FfmpegPath,
                ArgumentList = { "-hide_banner", "-muxers" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            var text = stdout + stderr;
            var ok = text.Contains("whip", StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                _logger.LogWarning("ffmpeg at {Path} has no whip muxer — WHIP voice disabled", _options.FfmpegPath);
            }

            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg whip muxer probe failed");
            return false;
        }
    }

    private sealed record ActivePublish(
        Process Process,
        CancellationTokenSource Lifetime,
        TaskCompletionSource Publishing,
        TaskCompletionSource<int> Ended);
}
