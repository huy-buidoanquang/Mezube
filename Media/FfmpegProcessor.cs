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

    /// <summary>
    /// WebM Opus + VP8, GOP 2s at <paramref name="fps"/> so STN <c>max_keyframe_gap_ms</c> (2500) passes.
    /// </summary>
    public async Task<string?> TranscodeToWebmAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        Directory.CreateDirectory(_options.TempDir);
        var outputPath = Path.Combine(
            _options.TempDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".webm");
        var audio = new PreparedAudioSettings(
            _options.PreparedAudioBitrateKbps,
            _options.PreparedAudioSampleRate,
            _options.PreparedAudioChannels);
        var height = Math.Clamp(_options.PreparedVideoHeight, 360, 1080);
        var fps = Math.Clamp(_options.PreparedVideoFps, 15, 30);
        var gop = fps * 2;
        var videoKbps = Math.Clamp(_options.PreparedVideoBitrateKbps, 200, 4000);

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
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale=-2:trunc(min(ih\\,{height})/2)*2");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(fps.ToString());
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libvpx");
        psi.ArgumentList.Add("-deadline");
        psi.ArgumentList.Add("realtime");
        psi.ArgumentList.Add("-cpu-used");
        psi.ArgumentList.Add("8");
        psi.ArgumentList.Add("-b:v");
        psi.ArgumentList.Add($"{videoKbps}k");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("32");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add(gop.ToString());
        psi.ArgumentList.Add("-keyint_min");
        psi.ArgumentList.Add(gop.ToString());
        psi.ArgumentList.Add("-auto-alt-ref");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-lag-in-frames");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libopus");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add($"{audio.BitrateKbps}k");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(audio.SampleRate.ToString());
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(audio.Channels.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("webm");
        psi.ArgumentList.Add(outputPath);

        _logger.LogDebug(
            "Preparing webm master inputExt={InputExt} height={Height} fps={Fps} gop={Gop} videoKbps={VideoKbps} audioKbps={AudioKbps}",
            Path.GetExtension(inputPath),
            height,
            fps,
            gop,
            videoKbps,
            audio.BitrateKbps);

        var convertStopwatch = Stopwatch.StartNew();
        ChildProcessResult result;
        try
        {
            result = await ChildProcessRunner.RunAsync(
                    psi,
                    ChildProcessRunner.DefaultVideoTranscodeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg not found at {Path}", _options.FfmpegPath);
            return null;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "ffmpeg webm transcode timed out for {Path}", inputPath);
            DownloadedMediaFiles.TryDelete(outputPath);
            return null;
        }

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogWarning("ffmpeg webm convert failed: {Stderr}", result.Stderr);
            DownloadedMediaFiles.TryDelete(outputPath);
            return null;
        }

        var outputInfo = new FileInfo(outputPath);
        _logger.LogDebug(
            "Prepared webm master ready path={Path} bytes={Bytes} elapsedMs={ElapsedMs}",
            outputPath,
            outputInfo.Length,
            convertStopwatch.ElapsedMilliseconds);

        return outputPath;
    }

    /// <summary>
    /// Docker ffmpeg is built without ffprobe — map the first video stream and grab one frame.
    /// </summary>
    public async Task<bool> HasVideoStreamAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        try
        {
            var result = await ChildProcessRunner.RunAsync(
                    psi,
                    TimeSpan.FromSeconds(20),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ffmpeg video-stream probe failed for {Path}", inputPath);
            return false;
        }
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
            _logger.LogWarning("ffmpeg not on PATH — cannot convert unsupported inputs to Ogg Opus / WebM.");
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
