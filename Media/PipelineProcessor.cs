using Mezon.Net.Sdk;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

public sealed record PipelineResult(
    string? CdnUrl,
    long? SourceBytes,
    PreparedAssetKind Kind,
    string? LocalPath = null);

/// <summary>
/// Prepare stages: download to temp → convert to SFU Ogg (audio) or WebM (legacy video).
/// Audio CDN upload is owned by <see cref="PlayableMediaProcessor"/> so playback is not blocked on it.
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

    public Task<PipelineResult> RunPipelineAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
        => RunPipelineAsync(client, track, PreparedAssetKind.Audio, cancellationToken);

    public async Task<PipelineResult> RunPipelineAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind == PreparedAssetKind.Video && !IsAudioOnlySource(track))
        {
            try
            {
                return await RunVideoPipelineAsync(client, track, cancellationToken).ConfigureAwait(false);
            }
            catch (AudioTooLargeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Video prep failed for {Title}; falling back to audio-only Ogg",
                    track.Title);
            }
        }

        return await RunAudioPipelineAsync(track, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipelineResult> RunAudioPipelineAsync(
        TrackInfoEntity track,
        CancellationToken cancellationToken)
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
            var uploadPath = await EnsureOggAsync(downloaded, preparedPath, cancellationToken).ConfigureAwait(false);
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

            return new PipelineResult(CdnUrl: null, oggBytes, PreparedAssetKind.Audio, uploadPath);
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

    private async Task<PipelineResult> RunVideoPipelineAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.TempDir);
        var workId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(_options.TempDir, $"mezube_{workId}");
        var source = !string.IsNullOrWhiteSpace(track.WebpageUrl) ? track.WebpageUrl! : track.MediaUrl;
        var total = Stopwatch.StartNew();

        try
        {
            var downloadStopwatch = Stopwatch.StartNew();
            var downloaded = await _ytDlp.DownloadTrackVideoAsync(source, rawPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            {
                throw new MediaPrepException("yt-dlp video download returned no file.");
            }

            var fileLength = new FileInfo(downloaded).Length;
            if (fileLength > _options.MaxVideoBytes)
            {
                throw new AudioTooLargeException(track.Title, fileLength, _options.MaxVideoBytes);
            }

            _logger.LogDebug(
                "Pipeline video download title={Title} inputExt={InputExt} elapsedMs={ElapsedMs}",
                track.Title,
                Path.GetExtension(downloaded),
                downloadStopwatch.ElapsedMilliseconds);

            if (!await _ffmpeg.HasVideoStreamAsync(downloaded, cancellationToken).ConfigureAwait(false))
            {
                throw new MediaPrepException("Downloaded media has no video stream.");
            }

            if (!_ffmpeg.IsAvailable)
            {
                throw new MediaPrepException(
                    "ffmpeg chưa có trên PATH — legacy video preparation needs WebM Opus+VP8. " +
                    "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
            }

            var convertStopwatch = Stopwatch.StartNew();
            var uploadPath = await _ffmpeg.TranscodeToWebmAsync(downloaded, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(uploadPath) || !File.Exists(uploadPath))
            {
                throw new MediaPrepException("ffmpeg convert → webm thất bại.");
            }

            var webmBytes = new FileInfo(uploadPath).Length;
            if (webmBytes > _options.MaxVideoBytes)
            {
                throw new AudioTooLargeException(track.Title, webmBytes, _options.MaxVideoBytes);
            }

            _logger.LogDebug(
                "Pipeline video convert title={Title} bytes={Bytes} elapsedMs={ElapsedMs}",
                track.Title,
                webmBytes,
                convertStopwatch.ElapsedMilliseconds);

            var uploadStopwatch = Stopwatch.StartNew();
            await using var webmStream = File.OpenRead(uploadPath);
            var filename = Path.GetFileName(uploadPath);
            if (!filename.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            {
                filename = Path.ChangeExtension(filename, ".webm") ?? $"{workId}.webm";
            }

            var (cdnUrl, bytes) = await _uploader.UploadMultipartFromStreamAsync(
                    client,
                    webmStream,
                    filename,
                    "video/webm",
                    cancellationToken,
                    _options.MaxVideoBytes)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Pipeline prepare ready title={Title} kind=video bytes={Bytes} elapsedMs={ElapsedMs} uploadMs={UploadMs}",
                track.Title,
                bytes,
                total.ElapsedMilliseconds,
                uploadStopwatch.ElapsedMilliseconds);

            return new PipelineResult(cdnUrl, bytes, PreparedAssetKind.Video);
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

    private async Task<string> EnsureOggAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var oggPath = await _ffmpeg.PrepareSfuOggAsync(inputPath, outputPath, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(oggPath) || !File.Exists(oggPath))
        {
            if (!_ffmpeg.IsAvailable)
            {
                throw new MediaPrepException(
                    "ffmpeg chưa có trên PATH — audio preparation needs an Ogg output. " +
                    "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
            }

            throw new MediaPrepException("ffmpeg convert → ogg thất bại; không upload a source container directly.");
        }

        return oggPath;
    }

    internal static bool IsAudioOnlySource(TrackInfoEntity track)
    {
        if (string.Equals(track.Source, TrackIdentityHelper.SourceSoundcloud, StringComparison.Ordinal))
        {
            return true;
        }

        var url = track.WebpageUrl ?? track.MediaUrl;
        return TrackIdentityHelper.IsSoundCloudUrl(url);
    }
}
