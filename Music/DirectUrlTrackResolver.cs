using Mezube.Application;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed class DirectUrlTrackResolver : ITrackResolver
{
    private readonly ITrackLibraryService _store;
    private readonly ILogger<DirectUrlTrackResolver> _logger;

    public DirectUrlTrackResolver(ITrackLibraryService store, ILogger<DirectUrlTrackResolver> logger)
    {
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
        if (host.Contains("youtube.com", StringComparison.Ordinal)
            || host.Contains("youtu.be", StringComparison.Ordinal)
            || host.Contains("music.youtube.com", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public async Task<TrackInfoEntity?> ResolveAsync(
        string query,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var url = query.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // Prefer .ogg for STN when caller passes .mp3 (Komu convention).
        var mediaUrl = url.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? url[..^4] + ".ogg"
            : url;

        var externalId = TrackIdentityHelper.ForDirectUrl(mediaUrl);
        var cached = await _store.TryGetAsync(TrackIdentityHelper.SourceUrl, externalId, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            _logger.LogDebug("Track store hit url/{Id}", externalId);
            return cached.ToTrackInfo(requestedBy);
        }

        var title = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = mediaUrl;
        }

        var track = new TrackInfoEntity
        {
            Title = title,
            MediaUrl = mediaUrl,
            WebpageUrl = url,
            RequestedBy = requestedBy,
            Source = TrackIdentityHelper.SourceUrl,
            ExternalId = externalId,
        };

        await _store.UpsertMetadataAsync(
                new TrackEntity
                {
                    Source = TrackIdentityHelper.SourceUrl,
                    ExternalId = externalId,
                    Title = title,
                    WebpageUrl = url,
                    PlayableUrl = null,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return track;
    }
}
