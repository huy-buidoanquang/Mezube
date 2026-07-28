using Mezube.Domain.Entities;
using Mezube.Domain.Persistence;
using Mezube.Helpers;
using Mezube.Media;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed class YoutubeTrackResolver : ITrackResolver
{
    private readonly YtDlpProcessor _ytDlp;
    private readonly ITrackDb _store;
    private readonly ILogger<YoutubeTrackResolver> _logger;

    public YoutubeTrackResolver(
        YtDlpProcessor ytDlp,
        ITrackDb store,
        ILogger<YoutubeTrackResolver> logger)
    {
        _ytDlp = ytDlp;
        _store = store;
        _logger = logger;
    }

    public bool CanResolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (Uri.TryCreate(query, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var host = uri.Host.ToLowerInvariant();
            return host.Contains("youtube.com", StringComparison.Ordinal)
                   || host.Contains("youtu.be", StringComparison.Ordinal)
                   || host.Contains("music.youtube.com", StringComparison.Ordinal);
        }

        // Search queries that are not direct media URLs.
        return !LooksLikeDirectMediaUrl(query);
    }

    public async Task<TrackInfoEntity?> ResolveAsync(
        string query,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();

        if (TrackIdentityHelper.TryParseYoutubeId(trimmed, out var videoId))
        {
            var cached = await _store.TryGetAsync(
                    TrackIdentityHelper.SourceYoutube,
                    videoId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug(
                    "Track store hit youtube/{Id} playable={HasPlayable}",
                    videoId,
                    cached.HasPlayableUrl);
                return cached.ToTrackInfo(requestedBy);
            }
        }
        else
        {
            var aliasKey = TrackIdentityHelper.NormalizeQueryAlias(trimmed);
            var byAlias = await _store.TryGetByAliasAsync(aliasKey, cancellationToken).ConfigureAwait(false);
            if (byAlias is not null)
            {
                _logger.LogDebug(
                    "Track store alias hit for query → {Source}/{Id}",
                    byAlias.Source,
                    byAlias.ExternalId);
                return byAlias.ToTrackInfo(requestedBy);
            }
        }

        var resolved = await _ytDlp.ResolveTrackAsync(trimmed, requestedBy, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var externalId = resolved.ExternalId;
        if (string.IsNullOrWhiteSpace(externalId)
            && TrackIdentityHelper.TryParseYoutubeId(resolved.WebpageUrl ?? trimmed, out var parsedId))
        {
            externalId = parsedId;
            resolved = new TrackInfoEntity
            {
                Title = resolved.Title,
                MediaUrl = resolved.MediaUrl,
                WebpageUrl = resolved.WebpageUrl,
                ThumbnailUrl = resolved.ThumbnailUrl,
                RequestedBy = resolved.RequestedBy,
                Duration = resolved.Duration,
                Source = resolved.Source,
                ExternalId = externalId,
            };
        }

        if (!string.IsNullOrWhiteSpace(externalId))
        {
            // Prefer existing playable_url if another path already prepared this id.
            var existing = await _store.TryGetAsync(
                    TrackIdentityHelper.SourceYoutube,
                    externalId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing?.HasPlayableUrl == true)
            {
                if (!TrackIdentityHelper.TryParseYoutubeId(trimmed, out _))
                {
                    await _store.SetAliasAsync(
                            TrackIdentityHelper.NormalizeQueryAlias(trimmed),
                            TrackIdentityHelper.SourceYoutube,
                            externalId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return existing.ToTrackInfo(requestedBy);
            }

            await _store.UpsertMetadataAsync(
                    new TrackEntity
                    {
                        Source = TrackIdentityHelper.SourceYoutube,
                        ExternalId = externalId,
                        Title = resolved.Title,
                        WebpageUrl = resolved.WebpageUrl,
                        ThumbnailUrl = resolved.ThumbnailUrl,
                        Duration = resolved.Duration,
                        PlayableUrl = existing?.PlayableUrl,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!TrackIdentityHelper.TryParseYoutubeId(trimmed, out _))
            {
                await _store.SetAliasAsync(
                        TrackIdentityHelper.NormalizeQueryAlias(trimmed),
                        TrackIdentityHelper.SourceYoutube,
                        externalId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return resolved;
    }

    private static bool LooksLikeDirectMediaUrl(string query)
    {
        if (!Uri.TryCreate(query, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".mp3")
               || path.EndsWith(".ogg")
               || path.EndsWith(".opus")
               || path.EndsWith(".m4a")
               || path.EndsWith(".wav")
               || path.EndsWith(".flac")
               || path.EndsWith(".webm");
    }
}
