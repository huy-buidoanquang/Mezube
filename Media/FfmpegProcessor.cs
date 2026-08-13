using System.ComponentModel;
using System.Diagnostics;
using Mezube.Bot;
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

    public async Task<string?> TranscodeToOggAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        Directory.CreateDirectory(_options.TempDir);
        var outputPath = Path.Combine(
            _options.TempDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".ogg");
        var outputSettings = new PreparedAudioSettings(
            _options.PreparedAudioBitrateKbps,
            _options.PreparedAudioSampleRate,
            _options.PreparedAudioChannels);

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // STN streaming expects Ogg Opus 48 kHz stereo (see mezon-media-station README).
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

    public async Task<string?> RemuxOpusToOggAsync(string inputPath, CancellationToken cancellationToken = default)
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

        var result = await ChildProcessRunner.RunAsync(
                psi,
                ChildProcessRunner.DefaultTranscodeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
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

            if (!process.WaitForExit(3000))
            {
                ChildProcessRunner.TryKill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            _logger.LogWarning("ffmpeg not on PATH — voice prepare cannot convert unsupported inputs to Ogg Opus.");
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
