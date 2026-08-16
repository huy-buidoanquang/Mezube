using Mezon.Net.Sdk;
using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Media;

/// <summary>
/// Ensures STN gets a stable HTTP URL (Mezon CDN), not an ephemeral googlevideo link.
/// Cache + persist live here; download/convert/upload stages live in <see cref="PipelineProcessor"/>.
/// </summary>
public sealed class PlayableMediaProcessor
{
    private static readonly TimeSpan PlayableCacheTtl = TimeSpan.FromMinutes(15);

    private readonly BotOptions _options;
    private readonly PipelineProcessor _pipeline;
    private readonly MezonCdnUploader _uploader;
    private readonly ITrackLibraryService _store;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlayableMediaProcessor> _logger;

    public PlayableMediaProcessor(
        BotOptions options,
        PipelineProcessor pipeline,
        MezonCdnUploader uploader,
        ITrackLibraryService store,
        IMemoryCache cache,
        ILogger<PlayableMediaProcessor> logger)
    {
        _options = options;
        _pipeline = pipeline;
        _uploader = uploader;
        _store = store;
        _cache = cache;
        _logger = logger;
    }

    public Task<TrackInfoEntity> ProcessTrackAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
        => ProcessTrackAsync(client, track, PreparedAssetKind.Audio, cancellationToken);

    public async Task<TrackInfoEntity> ProcessTrackAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var identity = ResolveIdentity(track);
        var maxBytes = kind == PreparedAssetKind.Video ? _options.MaxVideoBytes : _options.MaxAudioBytes;

        if (track.IsTooLarge
            || (track.SourceBytes is long reported && reported > maxBytes))
        {
            await MarkTooLargeIfPossibleAsync(identity, track, track.SourceBytes, cancellationToken)
                .ConfigureAwait(false);
            throw new AudioTooLargeException(
                track.Title,
                track.SourceBytes ?? 0,
                maxBytes);
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
                    maxBytes);
            }

            var cachedUrl = SelectStoredUrl(stored, kind);
            if (stored is not null && IsReadyUrl(cachedUrl, kind))
            {
                var cacheKey = PlayableCacheKey(kind, id.Source, id.ExternalId);
                if (_cache.TryGetValue(cacheKey, out string? fresh) && !string.IsNullOrWhiteSpace(fresh))
                {
                    await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                    var cachedHit = WithMediaUrl(stored.ToTrackInfo(track.RequestedBy), fresh);
                    return track.RequestedByUserId is long uid
                        ? cachedHit.WithRequester(uid, track.RequestedBy)
                        : cachedHit;
                }

                if (await _uploader.IsReachableAsync(cachedUrl!, cancellationToken).ConfigureAwait(false))
                {
                    _cache.Set(cacheKey, cachedUrl!, PlayableCacheTtl);
                    _logger.LogDebug(
                        "Using cached CDN media for {Source}/{Id} kind={Kind}: {Url} elapsedMs={ElapsedMs}",
                        id.Source,
                        id.ExternalId,
                        kind,
                        cachedUrl,
                        stopwatch.ElapsedMilliseconds);
                    await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                    var cached = WithMediaUrl(stored.ToTrackInfo(track.RequestedBy), cachedUrl!);
                    return track.RequestedByUserId is long uid
                        ? cached.WithRequester(uid, track.RequestedBy)
                        : cached;
                }

                _logger.LogWarning(
                    "Cached CDN media unreachable (will re-upload) {Source}/{Id} kind={Kind}",
                    id.Source,
                    id.ExternalId,
                    kind);
            }
            else if (kind == PreparedAssetKind.Audio
                     && stored is not null
                     && !string.IsNullOrWhiteSpace(stored.PlayableUrl)
                     && !PlayableUrlHelper.IsPreparedAudioUrl(stored.PlayableUrl))
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

        if (!NeedsRepackage(track.MediaUrl, kind))
        {
            if (!IsReadyUrl(track.MediaUrl, kind))
            {
                // Fall through to full prep — never persist a non-CDN URL as playable.
            }
            else if (identity is { } readyId)
            {
                await PersistPreparedUrlAsync(readyId.Source, readyId.ExternalId, track.MediaUrl, kind, cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(readyId.Source, readyId.ExternalId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Playable media already direct for {Title} kind={Kind} elapsedMs={ElapsedMs} url={Url}",
                    track.Title,
                    kind,
                    stopwatch.ElapsedMilliseconds,
                    track.MediaUrl);
                return track;
            }
            else
            {
                _logger.LogDebug(
                    "Playable media already direct for {Title} kind={Kind} elapsedMs={ElapsedMs} url={Url}",
                    track.Title,
                    kind,
                    stopwatch.ElapsedMilliseconds,
                    track.MediaUrl);
                return track;
            }
        }

        _logger.LogDebug("Preparing CDN media for {Title} kind={Kind}", track.Title, kind);
        PipelineResult prepared;
        try
        {
            prepared = await _pipeline.RunPipelineAsync(client, track, kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AudioTooLargeException ex)
        {
            await MarkTooLargeIfPossibleAsync(identity, track, ex.SizeBytes, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var hint = kind == PreparedAssetKind.Video ? "public .webm URL" : "public .ogg URL";
            throw new MediaPrepException(
                $"CDN upload failed for '{track.Title}'; cannot play without a {hint}.",
                ex);
        }

        _logger.LogInformation(
            "Playable CDN media ready for {Title} kind={Kind} elapsedMs={ElapsedMs}",
            track.Title,
            prepared.Kind,
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
                        PlayableUrl = prepared.Kind == PreparedAssetKind.Audio ? prepared.CdnUrl : null,
                        PlayableVideoUrl = prepared.Kind == PreparedAssetKind.Video ? prepared.CdnUrl : null,
                        SourceBytes = prepared.SourceBytes ?? track.SourceBytes,
                        IsTooLarge = track.IsTooLarge,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PersistPreparedUrlAsync(
                    saveId.Source,
                    saveId.ExternalId,
                    prepared.CdnUrl,
                    prepared.Kind,
                    cancellationToken)
                .ConfigureAwait(false);
            await _store.TouchPlayedAsync(saveId.Source, saveId.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            _cache.Set(
                PlayableCacheKey(prepared.Kind, saveId.Source, saveId.ExternalId),
                prepared.CdnUrl,
                PlayableCacheTtl);
        }

        return new TrackInfoEntity
        {
            Title = track.Title,
            MediaUrl = prepared.CdnUrl,
            WebpageUrl = track.WebpageUrl,
            ThumbnailUrl = track.ThumbnailUrl,
            RequestedBy = track.RequestedBy,
            RequestedByUserId = track.RequestedByUserId,
            Duration = track.Duration,
            Source = track.Source,
            ExternalId = identity?.ExternalId ?? track.ExternalId,
            SourceBytes = prepared.SourceBytes ?? track.SourceBytes,
            IsTooLarge = track.IsTooLarge,
        };
    }

    private Task PersistPreparedUrlAsync(
        string source,
        string externalId,
        string url,
        PreparedAssetKind kind,
        CancellationToken cancellationToken)
        => kind == PreparedAssetKind.Video
            ? _store.SetPlayableVideoUrlAsync(source, externalId, url, cancellationToken)
            : _store.SetPlayableUrlAsync(source, externalId, url, cancellationToken);

    private static string? SelectStoredUrl(TrackEntity? stored, PreparedAssetKind kind)
    {
        if (stored is null)
        {
            return null;
        }

        if (kind == PreparedAssetKind.Video)
        {
            if (PlayableUrlHelper.IsPreparedVideoUrl(stored.PlayableVideoUrl))
            {
                return stored.PlayableVideoUrl;
            }

            // SoundCloud (and other audio-only) streaming publishes Ogg; don't re-prep every play.
            if (string.Equals(stored.Source, TrackIdentityHelper.SourceSoundcloud, StringComparison.Ordinal)
                && PlayableUrlHelper.IsPreparedAudioUrl(stored.PlayableUrl))
            {
                return stored.PlayableUrl;
            }

            return null;
        }

        return stored.PlayableUrl;
    }

    private static bool IsReadyUrl(string? url, PreparedAssetKind kind)
        => kind == PreparedAssetKind.Video
            ? PlayableUrlHelper.IsPreparedStreamingUrl(url)
            : PlayableUrlHelper.IsPreparedAudioUrl(url);

    private static TrackInfoEntity WithMediaUrl(TrackInfoEntity track, string mediaUrl)
        => new()
        {
            TrackId = track.TrackId,
            Title = track.Title,
            MediaUrl = mediaUrl,
            WebpageUrl = track.WebpageUrl,
            ThumbnailUrl = track.ThumbnailUrl,
            RequestedBy = track.RequestedBy,
            RequestedByUserId = track.RequestedByUserId,
            Duration = track.Duration,
            Source = track.Source,
            ExternalId = track.ExternalId,
            SourceBytes = track.SourceBytes,
            IsTooLarge = track.IsTooLarge,
        };

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

    private static bool NeedsRepackage(string mediaUrl, PreparedAssetKind kind)
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
        var onMezonCdn = host.Contains("cdn.mezon", StringComparison.Ordinal)
                         || host.Contains("cdn.komu", StringComparison.Ordinal)
                         || host.Contains("cdn.nccsoft", StringComparison.Ordinal)
                         || host.Contains("r2.dev", StringComparison.Ordinal);
        if (!onMezonCdn)
        {
            return true;
        }

        return kind == PreparedAssetKind.Video
            ? !path.EndsWith(".webm")
            : !(path.EndsWith(".ogg") || path.EndsWith(".opus"));
    }

    private static string PlayableCacheKey(PreparedAssetKind kind, string source, string externalId)
        => $"playable:{kind}:{source}:{externalId}";
}
