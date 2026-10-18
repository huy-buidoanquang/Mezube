using Mezon.Net.Sdk;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence;
using Mezube.Streaming;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mezube.Media;

/// <summary>
/// Ensures the SFU publisher gets a local Ogg Opus file (and a CDN URL for restart cache).
/// Download/convert live in <see cref="PipelineProcessor"/>; CDN persist is off the play path.
/// </summary>
public sealed class PlayableMediaProcessor
{
    private static readonly TimeSpan PlayableCacheTtl = TimeSpan.FromMinutes(15);

    private readonly BotOptions _options;
    private readonly PipelineProcessor _pipeline;
    private readonly MezonCdnUploader _uploader;
    private readonly ITrackRepository _store;
    private readonly IMemoryCache _cache;
    private readonly MediaConcurrencyGate _gate;
    private readonly ILogger<PlayableMediaProcessor> _logger;
    private readonly ConcurrentDictionary<string, byte> _cdnPersistInFlight = new(StringComparer.Ordinal);

    public PlayableMediaProcessor(
        BotOptions options,
        PipelineProcessor pipeline,
        MezonCdnUploader uploader,
        ITrackRepository store,
        IMemoryCache cache,
        MediaConcurrencyGate gate,
        ILogger<PlayableMediaProcessor> logger)
    {
        _options = options;
        _pipeline = pipeline;
        _uploader = uploader;
        _store = store;
        _cache = cache;
        _gate = gate;
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
        var maxBytes = _options.MaxAudioBytes;

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

        var localHit = await TryLocalPreparedAsync(identity, track, cancellationToken).ConfigureAwait(false);
        if (localHit is not null)
        {
            _logger.LogDebug(
                "Using local prepared Ogg for {Source}/{Id} elapsedMs={ElapsedMs}",
                identity?.Source,
                identity?.ExternalId,
                stopwatch.ElapsedMilliseconds);
            return localHit;
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

            var cachedUrl = stored?.PlayableUrl;
            if (stored is not null && IsPreparedStreamAudioUrl(cachedUrl))
            {
                var cacheKey = PlayableCacheKey(kind, id.Source, id.ExternalId);
                if (_cache.TryGetValue(cacheKey, out CachedPlayable? fresh) && fresh is not null)
                {
                    await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                    return ToPlayable(track, stored, fresh.Url, fresh.LocalPath);
                }

                if (await _uploader.IsReachableAsync(cachedUrl!, cancellationToken).ConfigureAwait(false))
                {
                    _cache.Set(cacheKey, new CachedPlayable(cachedUrl!, LocalPath: null), PlayableCacheTtl);
                    _logger.LogDebug(
                        "Using cached CDN media for {Source}/{Id} kind={Kind} elapsedMs={ElapsedMs}",
                        id.Source,
                        id.ExternalId,
                        kind,
                        stopwatch.ElapsedMilliseconds);
                    await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken)
                        .ConfigureAwait(false);
                    return ToPlayable(track, stored, cachedUrl!, localPath: null);
                }

                _logger.LogWarning(
                    "Cached CDN media unreachable (will re-upload) {Source}/{Id} kind={Kind}",
                    id.Source,
                    id.ExternalId,
                    kind);
            }
            else if (stored is not null
                     && !string.IsNullOrWhiteSpace(stored.PlayableUrl)
                     && !PlayableUrlHelper.IsPreparedAudioUrl(stored.PlayableUrl))
            {
                _logger.LogWarning(
                    "Ignoring invalid playable_url cache (not prepared CDN ogg/opus) {Source}/{Id}",
                    id.Source,
                    id.ExternalId);
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

        if (!NeedsRepackage(track.MediaUrl, _options.CdnBaseUrl))
        {
            if (!IsPreparedStreamAudioUrl(track.MediaUrl))
            {
                // Fall through to full prep — never persist a non-CDN URL as playable.
            }
            else if (identity is { } readyId)
            {
                await _store.SetPlayableUrlAsync(readyId.Source, readyId.ExternalId, track.MediaUrl, cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(readyId.Source, readyId.ExternalId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Playable media already direct for {Title} kind={Kind} elapsedMs={ElapsedMs}",
                    track.Title,
                    kind,
                    stopwatch.ElapsedMilliseconds);
                return track;
            }
            else
            {
                _logger.LogDebug(
                    "Playable media already direct for {Title} kind={Kind} elapsedMs={ElapsedMs}",
                    track.Title,
                    kind,
                    stopwatch.ElapsedMilliseconds);
                return track;
            }
        }

        _logger.LogDebug("Preparing CDN media for {Title} kind={Kind}", track.Title, kind);
        PipelineResult prepared;
        try
        {
            prepared = await _gate.RunAsync(
                    () => _pipeline.RunPipelineAsync(track, cancellationToken),
                    cancellationToken)
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
            throw new MediaPrepException(
                $"Media preparation failed for '{track.Title}'; cannot play without a SFU Ogg Opus file.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(prepared.LocalPath) || !File.Exists(prepared.LocalPath))
        {
            throw new MediaPrepException($"Media preparation failed for '{track.Title}'; cannot play without a SFU Ogg Opus file.");
        }

        var storedUrl = identity is { } saved
            ? (await _store.TryGetAsync(saved.Source, saved.ExternalId, cancellationToken).ConfigureAwait(false))?.PlayableUrl
            : null;
        var mediaUrl = IsPreparedStreamAudioUrl(storedUrl) ? storedUrl! : track.MediaUrl;
        _logger.LogInformation(
            "Playable local Ogg ready for {Title} kind={Kind} elapsedMs={ElapsedMs}",
            track.Title,
            kind,
            stopwatch.ElapsedMilliseconds);
        return WithMediaUrl(track, mediaUrl, prepared.LocalPath, identity?.ExternalId ?? track.ExternalId, prepared.SourceBytes);
    }

    /// <summary>
    /// Upload the local Ogg to CDN and persist <c>playable_url</c>. Must not be on the play critical path.
    /// No-op when the track already has a ready CDN URL or no local file.
    /// </summary>
    public void ScheduleCdnPersist(MezonClient client, TrackInfoEntity track, PreparedAssetKind kind)
    {
        if (IsPreparedStreamAudioUrl(track.MediaUrl))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(track.LocalMediaPath) || !File.Exists(track.LocalMediaPath))
        {
            return;
        }

        var persistKey = track.LocalMediaPath;
        if (!_cdnPersistInFlight.TryAdd(persistKey, 0))
        {
            return;
        }

        _ = PersistCdnAsync(client, track, kind, persistKey, CancellationToken.None);
    }

    private async Task PersistCdnAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        string persistKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var localPath = track.LocalMediaPath!;
            await using var oggStream = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous);
            var filename = Path.GetFileName(localPath);
            if (!filename.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                && !filename.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                filename = Path.ChangeExtension(filename, ".ogg") ?? "audio.normalized.ogg";
            }

            var (cdnUrl, _) = await _uploader.UploadMultipartFromStreamAsync(
                    client,
                    oggStream,
                    filename,
                    "audio/ogg",
                    cancellationToken,
                    _options.MaxAudioBytes)
                .ConfigureAwait(false);

            var identity = ResolveIdentity(track);
            if (identity is { } id)
            {
                await _store.UpsertMetadataAsync(
                        new TrackEntity
                        {
                            Source = id.Source,
                            ExternalId = id.ExternalId,
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
                await _store.SetPlayableUrlAsync(id.Source, id.ExternalId, cdnUrl, cancellationToken)
                    .ConfigureAwait(false);
                await _store.TouchPlayedAsync(id.Source, id.ExternalId, cancellationToken).ConfigureAwait(false);
                _cache.Set(
                    PlayableCacheKey(kind, id.Source, id.ExternalId),
                    new CachedPlayable(cdnUrl, localPath),
                    PlayableCacheTtl);
            }

            _logger.LogDebug("CDN persist ready for {Title}", track.Title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CDN persist failed for {Title}; local playback still valid", track.Title);
        }
        finally
        {
            _cdnPersistInFlight.TryRemove(persistKey, out _);
        }
    }

    private async Task<TrackInfoEntity?> TryLocalPreparedAsync(
        (string Source, string ExternalId)? identity,
        TrackInfoEntity track,
        CancellationToken cancellationToken)
    {
        var localPath = identity is { } id
            ? PreparedAudioCache.GetPath(_options.TempDir, id.Source, id.ExternalId)
            : track.LocalMediaPath;
        if (string.IsNullOrWhiteSpace(localPath)
            || !await OggOpusReader.IsStreamCompatibleFileAsync(localPath, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? cdnUrl = null;
        if (identity is { } storedId)
        {
            var stored = await _store.TryGetAsync(storedId.Source, storedId.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            cdnUrl = stored?.PlayableUrl;
            await _store.TouchPlayedAsync(storedId.Source, storedId.ExternalId, cancellationToken)
                .ConfigureAwait(false);
            if (IsPreparedStreamAudioUrl(cdnUrl))
            {
                _cache.Set(
                    PlayableCacheKey(PreparedAssetKind.Audio, storedId.Source, storedId.ExternalId),
                    new CachedPlayable(cdnUrl!, localPath),
                    PlayableCacheTtl);
            }
        }

        var mediaUrl = IsPreparedStreamAudioUrl(cdnUrl) ? cdnUrl! : track.MediaUrl;
        return WithMediaUrl(track, mediaUrl, localPath, identity?.ExternalId ?? track.ExternalId, track.SourceBytes);
    }

    private static TrackInfoEntity ToPlayable(
        TrackInfoEntity requested,
        TrackEntity stored,
        string mediaUrl,
        string? localPath)
    {
        var mapped = WithMediaUrl(stored.ToTrackInfo(requested.RequestedBy), mediaUrl, localPath);
        return requested.RequestedByUserId is long uid
            ? mapped.WithRequester(uid, requested.RequestedBy)
            : mapped;
    }

    private static bool IsPreparedStreamAudioUrl(string? rawUrl)
    {
        if (!PlayableUrlHelper.IsPreparedAudioUrl(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // The in-process SFU publisher consumes Ogg Opus 48 kHz stereo. Older
        // cached .ogg/.opus assets may have a different sample rate/channel
        // layout, so they must be prepared once instead of being trusted by extension.
        return uri.AbsolutePath.Contains(".normalized.", StringComparison.OrdinalIgnoreCase);
    }

    private static TrackInfoEntity WithMediaUrl(
        TrackInfoEntity track,
        string mediaUrl,
        string? localPath = null,
        string? externalId = null,
        long? sourceBytes = null)
        => new()
        {
            TrackId = track.TrackId,
            Title = track.Title,
            MediaUrl = mediaUrl,
            LocalMediaPath = localPath ?? track.LocalMediaPath,
            WebpageUrl = track.WebpageUrl,
            ThumbnailUrl = track.ThumbnailUrl,
            RequestedBy = track.RequestedBy,
            RequestedByUserId = track.RequestedByUserId,
            Duration = track.Duration,
            Source = track.Source,
            ExternalId = externalId ?? track.ExternalId,
            SourceBytes = sourceBytes ?? track.SourceBytes,
            IsTooLarge = track.IsTooLarge,
        };

    private sealed record CachedPlayable(string Url, string? LocalPath);

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

    internal static bool NeedsRepackage(string mediaUrl, string cdnBaseUrl)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri)
            || !Uri.TryCreate(cdnBaseUrl, UriKind.Absolute, out var cdn)
            || !string.Equals(uri.Host, cdn.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        return !(path.EndsWith(".ogg") || path.EndsWith(".opus"));
    }

    private static string PlayableCacheKey(PreparedAssetKind kind, string source, string externalId)
        => $"playable:{kind}:{source}:{externalId}";
}
