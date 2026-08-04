using Mezon.Net.Sdk;
using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

/// <summary>
/// Ensures STN gets a stable HTTP URL (Mezon CDN), not an ephemeral googlevideo link.
/// Information = only when a new playable CDN asset is actually prepared.
/// Debug = cache hits and per-step download/convert/upload detail.
/// </summary>
public sealed class PlayableMediaProcessor
{
    private readonly BotOptions _options;
    private readonly YtDlpProcessor _ytDlp;
    private readonly FfmpegProcessor _ffmpeg;
    private readonly MezonCdnUploader _uploader;
    private readonly ITrackLibraryService _store;
    private readonly ILogger<PlayableMediaProcessor> _logger;

    public PlayableMediaProcessor(
        BotOptions options,
        YtDlpProcessor ytDlp,
        FfmpegProcessor ffmpeg,
        MezonCdnUploader uploader,
        ITrackLibraryService store,
        ILogger<PlayableMediaProcessor> logger)
    {
        _options = options;
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _uploader = uploader;
        _store = store;
        _logger = logger;
    }

    public async Task<TrackInfoEntity> ProcessTrackAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var pipelineStopwatch = Stopwatch.StartNew();
        var identity = ResolveIdentity(track);

        if (track.IsTooLarge
            || (track.SourceBytes is long reported && reported > _options.MaxAudioBytes))
        {
            await MarkTooLargeIfPossibleAsync(identity, track, track.SourceBytes, cancellationToken)
                .ConfigureAwait(false);
            throw new AudioTooLargeException(
                track.Title,
                track.SourceBytes ?? 0,
                _options.MaxAudioBytes);
        }

        if (identity is { } id)
        {
            var stored = await _store.TryGetAsync(id.Source, id.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is { IsTooLarge: true })
            {
                throw new AudioTooLargeException(
                    track.Title,
                    stored.SourceBytes ?? track.SourceBytes ?? 0,
                    _options.MaxAudioBytes);
            }

            if (stored is not null && PlayableUrlHelper.IsPreparedPlayableUrl(stored.PlayableUrl))
            {
                if (await _uploader.IsReachableAsync(stored.PlayableUrl!, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogDebug(
                        "Using cached CDN media for {Source}/{Id}: {Url} elapsedMs={ElapsedMs}",
                        id.Source,
                        id.ExternalId,
                        stored.PlayableUrl,
                        stopwatch.ElapsedMilliseconds);
                    await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                    var cached = stored.ToTrackInfo(track.RequestedBy);
                    return track.RequestedByUserId is long uid
                        ? cached.WithRequester(uid, track.RequestedBy)
                        : cached;
                }

                _logger.LogWarning(
                    "Cached CDN media unreachable (will re-upload) {Source}/{Id}",
                    id.Source,
                    id.ExternalId);
            }
            else if (stored is not null && !string.IsNullOrWhiteSpace(stored.PlayableUrl))
            {
                _logger.LogWarning(
                    "Ignoring invalid playable_url cache (not prepared CDN ogg/opus) {Source}/{Id}: {Url}",
                    id.Source,
                    id.ExternalId,
                    stored.PlayableUrl);
                try
                {
                    await _store.ClearPlayableUrlAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to clear invalid playable_url for {Source}/{Id}", id.Source, id.ExternalId);
                }
            }
        }

        if (!NeedsRepackage(track.MediaUrl))
        {
            if (!PlayableUrlHelper.IsPreparedPlayableUrl(track.MediaUrl))
            {
                // Fall through to full prep — never persist a non-CDN URL as playable.
            }
            else if (identity is { } readyId)
            {
                await _store.SetPlayableUrlAsync(
                        readyId.Source,
                        readyId.ExternalId,
                        track.MediaUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(readyId.Source, readyId.ExternalId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Playable media already direct for {Title} elapsedMs={ElapsedMs} url={Url}",
                    track.Title,
                    stopwatch.ElapsedMilliseconds,
                    track.MediaUrl);
                return track;
            }
            else
            {
                _logger.LogDebug(
                    "Playable media already direct for {Title} elapsedMs={ElapsedMs} url={Url}",
                    track.Title,
                    stopwatch.ElapsedMilliseconds,
                    track.MediaUrl);
                return track;
            }
        }

        _logger.LogDebug("Preparing CDN media for {Title}", track.Title);
        Directory.CreateDirectory(_options.TempDir);
        var workId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(_options.TempDir, $"mezube_{workId}");
        var source = !string.IsNullOrWhiteSpace(track.WebpageUrl) ? track.WebpageUrl! : track.MediaUrl;

        try
        {
            var downloadStopwatch = Stopwatch.StartNew();
            var downloaded = await _ytDlp.DownloadTrackAudioAsync(source, rawPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
            {
                throw new InvalidOperationException("yt-dlp download returned no file.");
            }

            var fileLength = new FileInfo(downloaded).Length;
            if (fileLength > _options.MaxAudioBytes)
            {
                await MarkTooLargeIfPossibleAsync(identity, track, fileLength, cancellationToken)
                    .ConfigureAwait(false);
                throw new AudioTooLargeException(track.Title, fileLength, _options.MaxAudioBytes);
            }

            _logger.LogDebug(
                "Prepare download completed title={Title} inputExt={InputExt} elapsedMs={ElapsedMs}",
                track.Title,
                Path.GetExtension(downloaded),
                downloadStopwatch.ElapsedMilliseconds);

            // STN voice/stream reliably plays Ogg (Komu NCC8 always converts to .ogg).
            // m4a/webm can make the bot appear in voice with playMedia ok but silence.
            var convertStopwatch = Stopwatch.StartNew();
            var uploadPath = await EnsureOggAsync(downloaded, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "Prepare convert resolved title={Title} inputExt={InputExt} outputExt={OutputExt} elapsedMs={ElapsedMs}",
                track.Title,
                Path.GetExtension(downloaded),
                Path.GetExtension(uploadPath),
                convertStopwatch.ElapsedMilliseconds);
            const string contentType = "audio/ogg";

            string cdnUrl;
            try
            {
                var uploadStopwatch = Stopwatch.StartNew();
                cdnUrl = await _uploader.UploadAsync(client, uploadPath, contentType, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug(
                    "Prepare upload completed title={Title} outputExt={OutputExt} cdnUrl={Url} elapsedMs={ElapsedMs}",
                    track.Title,
                    Path.GetExtension(uploadPath),
                    cdnUrl,
                    uploadStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // Voice/stream STN cannot fetch YouTube/googlevideo — do not fall back.
                throw new InvalidOperationException(
                    $"CDN upload failed for '{track.Title}'; cannot play without a public .ogg URL.",
                    ex);
            }

            _logger.LogInformation(
                "Playable CDN media ready for {Title} elapsedMs={ElapsedMs} totalPrepareMs={TotalPrepareMs}",
                track.Title,
                stopwatch.ElapsedMilliseconds,
                pipelineStopwatch.ElapsedMilliseconds);

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
                            SourceBytes = track.SourceBytes,
                            IsTooLarge = track.IsTooLarge,
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
                RequestedByUserId = track.RequestedByUserId,
                Duration = track.Duration,
                Source = track.Source,
                ExternalId = identity?.ExternalId ?? track.ExternalId,
                SourceBytes = track.SourceBytes,
                IsTooLarge = track.IsTooLarge,
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

        if (TrackIdentityHelper.TryParseYoutubeId(track.WebpageUrl ?? track.MediaUrl, out var ytId))
        {
            return (TrackIdentityHelper.SourceYoutube, ytId);
        }

        if (string.Equals(track.Source, TrackIdentityHelper.SourceUrl, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(track.MediaUrl))
        {
            return (TrackIdentityHelper.SourceUrl, TrackIdentityHelper.ForDirectUrl(track.MediaUrl));
        }

        return null;
    }

    private async Task MarkTooLargeIfPossibleAsync(
        (string Source, string ExternalId)? identity,
        TrackInfoEntity track,
        long? sourceBytes,
        CancellationToken cancellationToken)
    {
        if (identity is not { } id)
        {
            return;
        }

        try
        {
            await _store.MarkTooLargeAsync(id.Source, id.ExternalId, sourceBytes, track.Title, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist too-large flag for {Source}/{Id}",
                id.Source,
                id.ExternalId);
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
            throw new InvalidOperationException(
                "ffmpeg chưa có trên PATH — STN voice cần file .ogg (Komu cũng convert trước khi play). " +
                "Cài ffmpeg rồi restart bot, hoặc set MEZUBE_FFMPEG_PATH.");
        }

        var oggPath = await _ffmpeg.TranscodeToOggAsync(inputPath, cancellationToken).ConfigureAwait(false);
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
                         || host.Contains("cdn.nccsoft", StringComparison.Ordinal)
                         || host.Contains("r2.dev", StringComparison.Ordinal);
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
