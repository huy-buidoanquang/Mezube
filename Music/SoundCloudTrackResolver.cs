using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>
/// Resolves SoundCloud track URLs via yt-dlp (SoundCloud extractor — audio from SoundCloud, not YouTube).
/// Sets (<c>/sets/</c>) are handled by <see cref="SoundCloudSetImporter"/>.
/// </summary>
public sealed class SoundCloudTrackResolver : ITrackResolver
{
    private readonly YtDlpProcessor _ytDlp;
    private readonly ITrackLibraryService _store;
    private readonly BotOptions _options;
    private readonly ILogger<SoundCloudTrackResolver> _logger;

    public SoundCloudTrackResolver(
        YtDlpProcessor ytDlp,
        ITrackLibraryService store,
        BotOptions options,
        ILogger<SoundCloudTrackResolver> logger)
    {
        _ytDlp = ytDlp;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public bool CanResolve(string query)
    {
        if (!TrackIdentityHelper.IsSoundCloudUrl(query))
        {
            return false;
        }

        // Sets and on.soundcloud.com short links go through the playlist importer.
        return !TrackIdentityHelper.IsSoundCloudSetUrl(query)
               && !TrackIdentityHelper.IsSoundCloudShortUrl(query);
    }

    public async Task<TrackInfoEntity?> ResolveAsync(
        string query,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var normalized = TrackIdentityHelper.NormalizeAbsoluteUrl(query);
        var urlAlias = TrackIdentityHelper.NormalizeQueryAlias(normalized);

        var byAlias = await _store.TryGetByAliasAsync(urlAlias, cancellationToken).ConfigureAwait(false);
        if (byAlias is not null)
        {
            return byAlias.ToTrackInfo(requestedBy);
        }

        try
        {
            var resolved = await _ytDlp.ResolveTrackAsync(normalized, requestedBy, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                return null;
            }

            var externalId = !string.IsNullOrWhiteSpace(resolved.ExternalId)
                ? resolved.ExternalId!
                : TrackIdentityHelper.ForDirectUrl(normalized);

            var cached = await _store.TryGetAsync(
                    TrackIdentityHelper.SourceSoundcloud,
                    externalId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is { IsTooLarge: true } or { HasPlayableUrl: true })
            {
                await _store.SetAliasAsync(
                        urlAlias,
                        TrackIdentityHelper.SourceSoundcloud,
                        externalId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return cached.ToTrackInfo(requestedBy);
            }

            var webpage = resolved.WebpageUrl ?? normalized;
            var entity = new TrackEntity
            {
                Source = TrackIdentityHelper.SourceSoundcloud,
                ExternalId = externalId,
                Title = string.IsNullOrWhiteSpace(resolved.Title) ? externalId : resolved.Title,
                WebpageUrl = webpage,
                ThumbnailUrl = resolved.ThumbnailUrl,
                Duration = resolved.Duration,
                PlayableUrl = cached?.PlayableUrl,
                SourceBytes = resolved.SourceBytes ?? cached?.SourceBytes,
                IsTooLarge = cached?.IsTooLarge == true
                    || resolved.IsTooLarge
                    || (resolved.SourceBytes is long bytes && bytes > _options.MaxAudioBytes),
            };

            var trackId = await _store.UpsertMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
            await _store.SetAliasAsync(
                    urlAlias,
                    TrackIdentityHelper.SourceSoundcloud,
                    externalId,
                    cancellationToken)
                .ConfigureAwait(false);

            return new TrackInfoEntity
            {
                TrackId = trackId,
                Title = entity.Title,
                MediaUrl = webpage,
                WebpageUrl = webpage,
                ThumbnailUrl = entity.ThumbnailUrl,
                RequestedBy = requestedBy,
                Duration = entity.Duration,
                Source = TrackIdentityHelper.SourceSoundcloud,
                ExternalId = externalId,
                SourceBytes = entity.SourceBytes,
                IsTooLarge = entity.IsTooLarge,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SoundCloud resolve failed for {Query}", query);
            return null;
        }
    }
}
