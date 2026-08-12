using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>Expands a SoundCloud set/playlist via yt-dlp (audio from SoundCloud).</summary>
public sealed class SoundCloudSetImporter
{
    private readonly YtDlpProcessor _ytDlp;
    private readonly ITrackLibraryService _store;
    private readonly BotOptions _options;
    private readonly ILogger<SoundCloudSetImporter> _logger;

    public SoundCloudSetImporter(
        YtDlpProcessor ytDlp,
        ITrackLibraryService store,
        BotOptions options,
        ILogger<SoundCloudSetImporter> logger)
    {
        _ytDlp = ytDlp;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public bool CanImport(string query)
        => TrackIdentityHelper.IsSoundCloudSetUrl(query)
           || TrackIdentityHelper.IsSoundCloudShortUrl(query);

    public async Task<IReadOnlyList<TrackInfoEntity>> ImportAsync(
        string query,
        string? requestedBy,
        int maxTracks,
        CancellationToken cancellationToken = default)
    {
        var normalized = TrackIdentityHelper.NormalizeAbsoluteUrl(query);
        var entries = await _ytDlp.ResolvePlaylistAsync(normalized, requestedBy, maxTracks, cancellationToken)
            .ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return [];
        }

        var result = new List<TrackInfoEntity>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var webpage = entry.WebpageUrl ?? entry.MediaUrl;
                if (string.IsNullOrWhiteSpace(webpage)
                    || !webpage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var externalId = !string.IsNullOrWhiteSpace(entry.ExternalId)
                    ? entry.ExternalId!
                    : TrackIdentityHelper.ForDirectUrl(TrackIdentityHelper.NormalizeAbsoluteUrl(webpage));

                var cached = await _store.TryGetAsync(
                        TrackIdentityHelper.SourceSoundcloud,
                        externalId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cached is { IsTooLarge: true })
                {
                    continue;
                }

                if (cached?.HasPlayableUrl == true)
                {
                    result.Add(cached.ToTrackInfo(requestedBy));
                    continue;
                }

                if (entry.SourceBytes is long bytes && bytes > _options.MaxAudioBytes)
                {
                    continue;
                }

                var entity = new TrackEntity
                {
                    Source = TrackIdentityHelper.SourceSoundcloud,
                    ExternalId = externalId,
                    Title = entry.Title,
                    WebpageUrl = webpage,
                    ThumbnailUrl = entry.ThumbnailUrl ?? cached?.ThumbnailUrl,
                    Duration = entry.Duration ?? cached?.Duration,
                    PlayableUrl = cached?.PlayableUrl,
                    SourceBytes = entry.SourceBytes ?? cached?.SourceBytes,
                    IsTooLarge = false,
                };

                var trackId = await _store.UpsertMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
                result.Add(new TrackInfoEntity
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
                    IsTooLarge = false,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SoundCloud set item failed");
            }
        }

        _logger.LogInformation(
            "SoundCloud set import: {Count}/{Total} (cap={Cap})",
            result.Count,
            entries.Count,
            maxTracks);
        return result;
    }
}
