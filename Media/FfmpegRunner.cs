using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mezube.Bot;

namespace Mezube.Media;

public sealed class FfmpegRunner
{
    private readonly BotOptions _options;
    private readonly ILogger<FfmpegRunner> _logger;
    private readonly Lazy<bool> _available;

    public FfmpegRunner(BotOptions options, ILogger<FfmpegRunner> logger)
    {
        _options = options;
        _logger = logger;
        _available = new Lazy<bool>(ProbeAvailable);
    }

    public bool IsAvailable => _available.Value;

    public async Task<string?> ConvertToOggAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        Directory.CreateDirectory(_options.TempDir);
        var outputPath = Path.Combine(
            _options.TempDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".ogg");

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // STN streaming expects Ogg Opus 48 kHz stereo (see mezon-media-station README).
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libopus");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("96k");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("ogg");
        psi.ArgumentList.Add(outputPath);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg not found at {Path}", _options.FfmpegPath);
            return null;
        }

        if (process is null)
        {
            _logger.LogError("Failed to start ffmpeg at {Path}", _options.FfmpegPath);
            return null;
        }

        using (process)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                _logger.LogWarning("ffmpeg convert failed: {Stderr}", stderr);
                return null;
            }
        }

        return outputPath;
    }

    private bool ProbeAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.FfmpegPath,
                Arguments = "-version",
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

            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            _logger.LogInformation("ffmpeg not on PATH — will upload native audio (m4a/webm) without convert.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg probe failed");
            return false;
        }
    }
}
