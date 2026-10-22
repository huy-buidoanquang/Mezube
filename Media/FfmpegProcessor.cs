using System.ComponentModel;
using System.Diagnostics;
using Mezube.Bot;
using Mezube.Streaming;
using Microsoft.Extensions.Logging;

namespace Mezube.Media;

public sealed class FfmpegProcessor
{
    private readonly BotOptions _options;
    private readonly ILogger<FfmpegProcessor> _logger;
    private readonly Lazy<bool> _available;

    public FfmpegProcessor(BotOptions options, ILogger<FfmpegProcessor> logger)
    {
        _options = options;
        _logger = logger;
        _available = new Lazy<bool>(ProbeAvailable);
    }

    public bool IsAvailable => _available.Value;

    /// <summary>
    /// Produce the stream wire format (Ogg Opus 48 kHz stereo) at <paramref name="outputPath"/>.
    /// Prefer copy, then remux (<c>-c:a copy</c>), then libopus transcode.
    /// Compatibility is decided by <see cref="OggOpusReader"/> — the same parser the publisher uses.
    /// </summary>
    public async Task<string?> PrepareStreamOggAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        if (await OggOpusReader.IsStreamCompatibleFileAsync(inputPath, cancellationToken).ConfigureAwait(false))
        {
            if (!PathsEqual(inputPath, outputPath))
            {
                File.Copy(inputPath, outputPath, overwrite: true);
            }

            _logger.LogDebug(
                "Preparing audio master via copy inputExt={InputExt} outputExt={OutputExt}",
                Path.GetExtension(inputPath),
                Path.GetExtension(outputPath));
            return outputPath;
        }

        if (!IsAvailable)
        {
            return null;
        }

        var remuxed = await RemuxOpusToOggAsync(inputPath, outputPath, cancellationToken).ConfigureAwait(false);
        if (remuxed is not null
            && await OggOpusReader.IsStreamCompatibleFileAsync(remuxed, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Preparing audio master via remux copy inputExt={InputExt} outputExt={OutputExt}",
                Path.GetExtension(inputPath),
                Path.GetExtension(outputPath));
            return remuxed;
        }

        DownloadedMediaFiles.TryDelete(outputPath);

        var transcoded = await TranscodeToOggAsync(inputPath, outputPath, cancellationToken).ConfigureAwait(false);
        if (transcoded is not null)
        {
            _logger.LogDebug(
                "Preparing audio master via libopus transcode inputExt={InputExt} outputExt={OutputExt}",
                Path.GetExtension(inputPath),
                Path.GetExtension(outputPath));
        }

        return transcoded;
    }

    public Task<string?> TranscodeToOggAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.TempDir);
        var outputPath = Path.Combine(
            _options.TempDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".normalized.ogg");
        return TranscodeToOggAsync(inputPath, outputPath, cancellationToken);
    }

    public async Task<string?> TranscodeToOggAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // The stream publisher consumes one canonical wire format. Keep these
        // values fixed even if the legacy media configuration is customized.
        var outputSettings = new PreparedAudioSettings(
            _options.PreparedAudioBitrateKbps,
            SampleRate: 48000,
            Channels: 2);

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Stream publisher input is normalized to Ogg Opus 48 kHz stereo.
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libopus");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add($"{outputSettings.BitrateKbps}k");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(outputSettings.SampleRate.ToString());
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(outputSettings.Channels.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("ogg");
        psi.ArgumentList.Add(outputPath);

        _logger.LogDebug(
            "Preparing audio master inputExt={InputExt} outputExt={OutputExt} bitrateKbps={BitrateKbps} sampleRate={SampleRate} channels={Channels}",
            Path.GetExtension(inputPath),
            Path.GetExtension(outputPath),
            outputSettings.BitrateKbps,
            outputSettings.SampleRate,
            outputSettings.Channels);

        var convertStopwatch = Stopwatch.StartNew();
        ChildProcessResult result;
        try
        {
            result = await ChildProcessRunner.RunAsync(
                    psi,
                    ChildProcessRunner.DefaultTranscodeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg not found at {Path}", _options.FfmpegPath);
            return null;
        }

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogWarning("ffmpeg convert failed: {Stderr}", result.Stderr);
            DownloadedMediaFiles.TryDelete(outputPath);
            return null;
        }

        var outputInfo = new FileInfo(outputPath);
        _logger.LogDebug(
            "Prepared audio master ready path={Path} bytes={Bytes} bitrateKbps={BitrateKbps} sampleRate={SampleRate} channels={Channels} elapsedMs={ElapsedMs}",
            outputPath,
            outputInfo.Length,
            outputSettings.BitrateKbps,
            outputSettings.SampleRate,
            outputSettings.Channels,
            convertStopwatch.ElapsedMilliseconds);

        return outputPath;
    }

    public Task<string?> RemuxOpusToOggAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.TempDir);
        var outputPath = Path.Combine(
            _options.TempDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".normalized.ogg");
        return RemuxOpusToOggAsync(inputPath, outputPath, cancellationToken);
    }

    public async Task<string?> RemuxOpusToOggAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("ogg");
        psi.ArgumentList.Add(outputPath);

        try
        {
            var result = await ChildProcessRunner.RunAsync(
                    psi,
                    ChildProcessRunner.DefaultTranscodeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (TimeoutException)
        {
            DownloadedMediaFiles.TryDelete(outputPath);
            return null;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

            if (!process.WaitForExit(3000))
            {
                ChildProcessRunner.TryKill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            _logger.LogWarning("ffmpeg not on PATH — cannot convert unsupported inputs to Ogg Opus.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg probe failed");
            return false;
        }
    }

    private sealed record PreparedAudioSettings(int BitrateKbps, int SampleRate, int Channels);
}
