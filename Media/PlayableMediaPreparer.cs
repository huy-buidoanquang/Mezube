using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Domain.Persistence;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

/// <summary>
/// Ensures STN gets a stable HTTP URL (Mezon CDN), not an ephemeral googlevideo link.
/// </summary>
public sealed class PlayableMediaPreparer
{
    private readonly BotOptions _options;
    private readonly YtDlpRunner _ytDlp;
    private readonly FfmpegRunner _ffmpeg;
    private readonly MezonCdnUploader _uploader;
    private readonly ITrackDb _store;
    private readonly ILogger<PlayableMediaPreparer> _logger;

    public PlayableMediaPreparer(
        BotOptions options,
        YtDlpRunner ytDlp,
        FfmpegRunner ffmpeg,
        MezonCdnUploader uploader,
        ITrackDb store,
        ILogger<PlayableMediaPreparer> logger)
    {
        _options = options;
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _uploader = uploader;
        _store = store;
        _logger = logger;
    }

    public async Task<TrackInfoEntity> EnsurePlayableAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var identity = ResolveIdentity(track);

        if (identity is { } id)
        {
            var stored = await _store.TryGetAsync(id.Source, id.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            if (stored?.HasPlayableUrl == true)
            {
                _logger.LogInformation(
                    "Using cached CDN media for {Source}/{Id}: {Url} elapsedMs={ElapsedMs}",
                    id.Source,
                    id.ExternalId,
                    stored.PlayableUrl,
                    stopwatch.ElapsedMilliseconds);
                await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                    .ConfigureAwait(false);
                return stored.ToTrackInfo(track.RequestedBy);
            }
        }

        if (!NeedsRepackage(track.MediaUrl))
        {
            if (identity is { } readyId)
            {
                await _store.SetPlayableUrlAsync(
                        readyId.Source,
                        readyId.ExternalId,
                        track.MediaUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(readyId.Source, readyId.ExternalId, cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Playable media already direct for {Title} elapsedMs={ElapsedMs} url={Url}",
                track.Title,
                stopwatch.ElapsedMilliseconds,
                track.MediaUrl);
            return track;
        }

        _logger.LogInformation("Preparing CDN media for {Title}", track.Title);
        Directory.CreateDirectory(_options.TempDir);
        var workId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(_options.TempDir, $"mezube_{workId}");
        var source = !string.IsNullOrWhiteSpace(track.WebpageUrl) ? track.WebpageUrl! : track.MediaUrl;

        try
        {
            var downloaded = await _ytDlp.DownloadAudioAsync(source, rawPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            {
                throw new InvalidOperationException("yt-dlp download returned no file.");
            }

            // STN voice/stream reliably plays Ogg (Komu NCC8 always converts to .ogg).
            // m4a/webm can make the bot appear in voice with playMedia ok but silence.
            var uploadPath = await EnsureOggAsync(downloaded, cancellationToken).ConfigureAwait(false);
            const string contentType = "audio/ogg";

            string cdnUrl;
            try
            {
                cdnUrl = await _uploader.UploadAsync(client, uploadPath, contentType, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "CDN upload failed for {Title}; falling back to direct media URL (may be blocked by STN).",
                    track.Title);
                return track;
            }

            _logger.LogInformation(
                "Playable CDN url ready for {Title}: {Url} elapsedMs={ElapsedMs}",
                track.Title,
                cdnUrl,
                stopwatch.ElapsedMilliseconds);

            if (identity is { } saveId)
            {
                await _store.UpsertMetadataAsync(
                        new TrackEntity
                        {
                            Source = saveId.Source,
                            ExternalId = saveId.ExternalId,
                            Title = track.Title,
                            WebpageUrl = track.WebpageUrl,
                            ThumbnailUrl = track.ThumbnailUrl,
                            Duration = track.Duration,
                            PlayableUrl = cdnUrl,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.SetPlayableUrlAsync(saveId.Source, saveId.ExternalId, cdnUrl, cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(saveId.Source, saveId.ExternalId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new TrackInfoEntity
            {
                Title = track.Title,
                MediaUrl = cdnUrl,
                WebpageUrl = track.WebpageUrl,
                ThumbnailUrl = track.ThumbnailUrl,
                RequestedBy = track.RequestedBy,
                Duration = track.Duration,
                Source = track.Source,
                ExternalId = identity?.ExternalId ?? track.ExternalId,
            };
        }
        finally
        {
            TryDelete(rawPath);
            TryDelete(Path.ChangeExtension(rawPath, ".opus"));
            TryDelete(Path.ChangeExtension(rawPath, ".ogg"));
            TryDelete(Path.ChangeExtension(rawPath, ".m4a"));
            TryDelete(Path.ChangeExtension(rawPath, ".webm"));
        }
    }

    private (string Source, string ExternalId)? ResolveIdentity(TrackInfoEntity track)
    {
        if (!string.IsNullOrWhiteSpace(track.ExternalId) && !string.IsNullOrWhiteSpace(track.Source)
            && track.Source is not "unknown")
        {
            return (track.Source, track.ExternalId);
        }

        if (TrackIdentity.TryParseYoutubeId(track.WebpageUrl ?? track.MediaUrl, out var ytId))
        {
            return (TrackIdentity.SourceYoutube, ytId);
        }

        if (string.Equals(track.Source, TrackIdentity.SourceUrl, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(track.MediaUrl))
        {
            return (TrackIdentity.SourceUrl, TrackIdentity.ForDirectUrl(track.MediaUrl));
        }

        return null;
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
            throw new InvalidOperationException(
                "ffmpeg chưa có trên PATH — STN voice cần file .ogg (Komu cũng convert trước khi play). " +
                "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
        }

        var oggPath = await _ffmpeg.ConvertToOggAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(oggPath) || !File.Exists(oggPath))
        {
            throw new InvalidOperationException("ffmpeg convert → ogg thất bại; không upload m4a/webm cho STN.");
        }

        return oggPath;
    }

    private static bool NeedsRepackage(string mediaUrl)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
        {
            return true;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("googlevideo.com", StringComparison.Ordinal)
            || host.Contains("youtube.com", StringComparison.Ordinal)
            || host.Contains("ytimg.com", StringComparison.Ordinal)
            || host.Contains("youtu.be", StringComparison.Ordinal))
        {
            return true;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        // Only skip when already a stable Mezon-family CDN ogg/opus URL.
        var onMezonCdn = host.Contains("cdn.mezon", StringComparison.Ordinal)
                         || host.Contains("cdn.komu", StringComparison.Ordinal)
                         || host.Contains("cdn.nccsoft", StringComparison.Ordinal);
        if (onMezonCdn && (path.EndsWith(".ogg") || path.EndsWith(".opus")))
        {
            return false;
        }

        // Direct stable ogg/opus elsewhere is fine; everything else must be repackaged.
        return !(path.EndsWith(".ogg") || path.EndsWith(".opus"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
