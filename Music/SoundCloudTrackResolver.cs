using Mezube.Application;
using Mezube.Domain;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>Resolves SoundCloud track/set URLs via yt-dlp into the shared track library.</summary>
public sealed class SoundCloudTrackResolver : ITrackResolver
{
    private readonly YtDlpProcessor _ytDlp;
    private readonly ITrackLibraryService _store;
    private readonly ILogger<SoundCloudTrackResolver> _logger;

    public SoundCloudTrackResolver(
        YtDlpProcessor ytDlp,
        ITrackLibraryService store,
        ILogger<SoundCloudTrackResolver> logger)
    {
        _ytDlp = ytDlp;
        _store = store;
        _logger = logger;
    }

    public bool CanResolve(string query)
    {
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "soundcloud.com" or "www.soundcloud.com" or "m.soundcloud.com"
               || host.EndsWith(".soundcloud.com", StringComparison.Ordinal);
    }

    public async Task<TrackInfoEntity?> ResolveAsync(
        string query,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var normalized = TrackIdentityHelper.NormalizeAbsoluteUrl(query);
        var externalId = TrackIdentityHelper.ForDirectUrl(normalized);

        var cached = await _store.TryGetAsync(TrackIdentityHelper.SourceSoundcloud, externalId, cancellationToken)
            .ConfigureAwait(false);
        if (cached is { IsTooLarge: true })
        {
            return cached.ToTrackInfo(requestedBy);
        }

        if (cached is { HasPlayableUrl: true })
        {
            return cached.ToTrackInfo(requestedBy);
        }

        try
        {
            var resolved = await _ytDlp.ResolveTrackAsync(normalized, requestedBy, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                return null;
            }

            var id = string.IsNullOrWhiteSpace(resolved.ExternalId) ? externalId : resolved.ExternalId!;
            var entity = new TrackEntity
            {
                Source = TrackIdentityHelper.SourceSoundcloud,
                ExternalId = id,
                Title = string.IsNullOrWhiteSpace(resolved.Title) ? id : resolved.Title,
                WebpageUrl = resolved.WebpageUrl ?? normalized,
                ThumbnailUrl = resolved.ThumbnailUrl,
                Duration = resolved.Duration,
                PlayableUrl = cached?.PlayableUrl,
                SourceBytes = resolved.SourceBytes ?? cached?.SourceBytes,
                IsTooLarge = cached?.IsTooLarge == true
                    || resolved.IsTooLarge
                    || (resolved.SourceBytes is long bytes && bytes > MezubeConstants.MaxAudioBytes),
            };

            var trackId = await _store.UpsertMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
            return new TrackEntity
            {
                Id = trackId,
                Source = entity.Source,
                ExternalId = entity.ExternalId,
                Title = entity.Title,
                WebpageUrl = entity.WebpageUrl,
                ThumbnailUrl = entity.ThumbnailUrl,
                Duration = entity.Duration,
                PlayableUrl = entity.PlayableUrl,
                SourceBytes = entity.SourceBytes,
                IsTooLarge = entity.IsTooLarge,
            }.ToTrackInfo(requestedBy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SoundCloud resolve failed for {Query}", query);
            return null;
        }
    }
}
