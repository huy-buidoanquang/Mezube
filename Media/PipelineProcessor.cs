using Mezon.Net.Sdk;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

public sealed record PipelineResult(string CdnUrl, long? SourceBytes);

/// <summary>
/// Prepare stages: download to temp → convert Ogg → multipart CDN upload → cleanup.
/// </summary>
public sealed class PipelineProcessor
{
    private readonly BotOptions _options;
    private readonly YtDlpProcessor _ytDlp;
    private readonly FfmpegProcessor _ffmpeg;
    private readonly MezonCdnUploader _uploader;
    private readonly ILogger<PipelineProcessor> _logger;

    public PipelineProcessor(
        BotOptions options,
        YtDlpProcessor ytDlp,
        FfmpegProcessor ffmpeg,
        MezonCdnUploader uploader,
        ILogger<PipelineProcessor> logger)
    {
        _options = options;
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _uploader = uploader;
        _logger = logger;
    }

    /// <summary>
    /// yt-dlp download → ffmpeg Ogg (temp) → <see cref="MezonCdnUploader.UploadMultipartFromStreamAsync"/>.
    /// </summary>
    public async Task<PipelineResult> RunPipelineAsync(
        MezonClient client,
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
            var uploadPath = await EnsureOggAsync(downloaded, cancellationToken).ConfigureAwait(false);
            var oggBytes = new FileInfo(uploadPath).Length;
            if (oggBytes > _options.MaxAudioBytes)
            {
                throw new AudioTooLargeException(track.Title, oggBytes, _options.MaxAudioBytes);
            }

            _logger.LogDebug(
                "Pipeline convert title={Title} outputExt={OutputExt} bytes={Bytes} elapsedMs={ElapsedMs}",
                track.Title,
                Path.GetExtension(uploadPath),
                oggBytes,
                convertStopwatch.ElapsedMilliseconds);

            var uploadStopwatch = Stopwatch.StartNew();
            await using var oggStream = File.OpenRead(uploadPath);
            var filename = Path.GetFileName(uploadPath);
            if (!filename.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                && !filename.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                filename = Path.ChangeExtension(filename, ".ogg") ?? $"{workId}.ogg";
            }

            var (cdnUrl, bytes) = await _uploader.UploadMultipartFromStreamAsync(
                    client,
                    oggStream,
                    filename,
                    "audio/ogg",
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Pipeline prepare ready title={Title} bytes={Bytes} elapsedMs={ElapsedMs} uploadMs={UploadMs} url={Url}",
                track.Title,
                bytes,
                total.ElapsedMilliseconds,
                uploadStopwatch.ElapsedMilliseconds,
                cdnUrl);

            return new PipelineResult(cdnUrl, bytes);
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

    private async Task<string> EnsureOggAsync(string inputPath, CancellationToken cancellationToken)
    {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        if (ext is ".ogg" or ".opus")
        {
            return inputPath;
        }

        if (!_ffmpeg.IsAvailable)
        {
            throw new MediaPrepException(
                "ffmpeg chưa có trên PATH — STN voice cần file .ogg. " +
                "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
        }

        if (ext is ".webm")
        {
            var copied = await _ffmpeg.RemuxOpusToOggAsync(inputPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(copied) && File.Exists(copied))
            {
                return copied;
            }
        }

        var oggPath = await _ffmpeg.TranscodeToOggAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(oggPath) || !File.Exists(oggPath))
        {
            throw new MediaPrepException("ffmpeg convert → ogg thất bại; không upload m4a/webm cho STN.");
        }

        return oggPath;
    }
}
