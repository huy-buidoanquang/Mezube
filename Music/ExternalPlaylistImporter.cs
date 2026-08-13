using Mezube.Application;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
using Mezube.Music.Interactive;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>Metadata import for YouTube playlists and SoundCloud sets (no download).</summary>
public sealed class ExternalPlaylistImporter
{
    private readonly YtDlpProcessor _ytDlp;
    private readonly ITrackLibraryService _store;
    private readonly BotOptions _options;
    private readonly ILogger<ExternalPlaylistImporter> _logger;

    public ExternalPlaylistImporter(
        YtDlpProcessor ytDlp,
        ITrackLibraryService store,
        BotOptions options,
        ILogger<ExternalPlaylistImporter> logger)
    {
        _ytDlp = ytDlp;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public bool CanImport(string query) => TrackIdentityHelper.IsExternalPlaylistUrl(query);

    public async Task<IReadOnlyList<TrackCandidate>> ImportCandidatesAsync(
        string query,
        string? requestedBy,
        int maxTracks,
        CancellationToken cancellationToken = default)
    {
        var normalized = TrackIdentityHelper.NormalizeAbsoluteUrl(query);
        IReadOnlyList<TrackInfoEntity> entries;
        try
        {
            entries = await _ytDlp.ResolvePlaylistAsync(normalized, requestedBy, maxTracks, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External playlist resolve failed");
            return [];
        }

        var result = new List<TrackCandidate>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.IsTooLarge
                    || (entry.SourceBytes is long bytes && bytes > _options.MaxAudioBytes))
                {
                    continue;
                }

                var webpage = entry.WebpageUrl ?? entry.MediaUrl;
                if (string.IsNullOrWhiteSpace(webpage)
                    || !webpage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = string.IsNullOrWhiteSpace(entry.Source)
                    ? TrackIdentityHelper.SourceYoutube
                    : entry.Source;
                var externalId = !string.IsNullOrWhiteSpace(entry.ExternalId)
                    ? entry.ExternalId!
                    : TrackIdentityHelper.ForDirectUrl(TrackIdentityHelper.NormalizeAbsoluteUrl(webpage));

                var cached = await _store.TryGetAsync(source, externalId, cancellationToken).ConfigureAwait(false);
                if (cached is { IsTooLarge: true })
                {
                    continue;
                }

                if (cached is not null)
                {
                    var fromCache = TrackCandidate.FromTrack(cached.ToTrackInfo(requestedBy));
                    if (fromCache is not null)
                    {
                        result.Add(fromCache);
                    }

                    continue;
                }

                var entity = new TrackEntity
                {
                    Source = source,
                    ExternalId = externalId,
                    Title = entry.Title,
                    WebpageUrl = webpage,
                    ThumbnailUrl = entry.ThumbnailUrl,
                    Duration = entry.Duration,
                    PlayableUrl = null,
                    SourceBytes = entry.SourceBytes,
                    IsTooLarge = false,
                };

                var trackId = await _store.UpsertMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
                result.Add(new TrackCandidate
                {
                    Source = source,
                    ExternalId = externalId,
                    Title = entity.Title,
                    WebpageUrl = webpage,
                    ThumbnailUrl = entity.ThumbnailUrl,
                    DurationSeconds = entity.Duration?.TotalSeconds,
                    SourceBytes = entity.SourceBytes,
                    TrackId = trackId,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External playlist item skipped");
            }
        }

        _logger.LogInformation(
            "External playlist import candidates: {Count}/{Total} (cap={Cap})",
            result.Count,
            entries.Count,
            maxTracks);
        return result;
    }
}
