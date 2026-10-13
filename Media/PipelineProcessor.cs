using Mezube.Bot;
using Mezube.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

public sealed record PipelineResult(long? SourceBytes, string LocalPath);

/// <summary>
/// Prepare stages: download to temp → convert to SFU Ogg.
/// CDN upload is owned by <see cref="PlayableMediaProcessor"/> so playback is not blocked on it.
/// </summary>
public sealed class PipelineProcessor
{
    private readonly BotOptions _options;
    private readonly YtDlpProcessor _ytDlp;
    private readonly FfmpegProcessor _ffmpeg;
    private readonly ILogger<PipelineProcessor> _logger;

    public PipelineProcessor(
        BotOptions options,
        YtDlpProcessor ytDlp,
        FfmpegProcessor ffmpeg,
        ILogger<PipelineProcessor> logger)
    {
        _options = options;
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<PipelineResult> RunPipelineAsync(
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.TempDir);
        var workId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(_options.TempDir, $"mezube_{workId}");
        var source = !string.IsNullOrWhiteSpace(track.WebpageUrl) ? track.WebpageUrl! : track.MediaUrl;
        var total = Stopwatch.StartNew();

        try
        {
            var downloadStopwatch = Stopwatch.StartNew();
            var downloaded = await _ytDlp.DownloadTrackAudioAsync(source, rawPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            {
                throw new MediaPrepException("yt-dlp download returned no file.");
            }

            var fileLength = new FileInfo(downloaded).Length;
            if (fileLength > _options.MaxAudioBytes)
            {
                throw new AudioTooLargeException(track.Title, fileLength, _options.MaxAudioBytes);
            }

            _logger.LogDebug(
                "Pipeline download title={Title} inputExt={InputExt} elapsedMs={ElapsedMs}",
                track.Title,
                Path.GetExtension(downloaded),
                downloadStopwatch.ElapsedMilliseconds);

            var convertStopwatch = Stopwatch.StartNew();
            var preparedPath = PreparedAudioCache.TryGetPath(_options.TempDir, track)
                               ?? PreparedAudioCache.NewFallbackPath(_options.TempDir);
            var uploadPath = await EnsureOggAsync(
                    _ffmpeg,
                    downloaded,
                    rawPath + ".normalized.ogg",
                    preparedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var oggBytes = new FileInfo(uploadPath).Length;
            if (oggBytes > _options.MaxAudioBytes)
            {
                DownloadedMediaFiles.TryDelete(uploadPath);
                throw new AudioTooLargeException(track.Title, oggBytes, _options.MaxAudioBytes);
            }

            _logger.LogInformation(
                "Pipeline prepare ready title={Title} kind=audio bytes={Bytes} elapsedMs={ElapsedMs} convertMs={ConvertMs}",
                track.Title,
                oggBytes,
                total.ElapsedMilliseconds,
                convertStopwatch.ElapsedMilliseconds);

            return new PipelineResult(oggBytes, uploadPath);
        }
        finally
        {
            DownloadedMediaFiles.DeletePrefixed(_options.TempDir, $"mezube_{workId}");
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                DownloadedMediaFiles.TryDelete(rawPath);
            }
        }
    }

    internal static async Task<string> EnsureOggAsync(
        FfmpegProcessor ffmpeg,
        string inputPath,
        string workPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var oggPath = await ffmpeg.PrepareSfuOggAsync(inputPath, workPath, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(oggPath) || !File.Exists(oggPath))
        {
            if (!ffmpeg.IsAvailable)
            {
                throw new MediaPrepException(
                    "ffmpeg chưa có trên PATH — audio preparation needs an Ogg output. " +
                    "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
            }

            throw new MediaPrepException("ffmpeg convert → ogg thất bại; không upload a source container directly.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Move(oggPath, outputPath, overwrite: true);
        return outputPath;
    }
}
