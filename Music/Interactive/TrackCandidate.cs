using Mezube.Domain.Entities;
using Mezube.Helpers;

namespace Mezube.Music.Interactive;

/// <summary>Metadata-only candidate for search / playlist pickers (no download).</summary>
public sealed class TrackCandidate
{
    public required string Source { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public required string WebpageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public long? SourceBytes { get; init; }
    public long? TrackId { get; init; }

    public string Token => $"{Source}:{ExternalId}";

    public string DisplayDuration
    {
        get
        {
            if (DurationSeconds is not double s || s <= 0)
            {
                return "?:??";
            }

            var d = TimeSpan.FromSeconds(s);
            return d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
        }
    }

    public static TrackCandidate? FromTrack(TrackInfoEntity track)
    {
        if (string.IsNullOrWhiteSpace(track.ExternalId)
            || string.IsNullOrWhiteSpace(track.Source)
            || string.IsNullOrWhiteSpace(track.WebpageUrl ?? track.MediaUrl))
        {
            return null;
        }

        return new TrackCandidate
        {
            Source = track.Source,
            ExternalId = track.ExternalId!,
            Title = string.IsNullOrWhiteSpace(track.Title) ? track.ExternalId! : track.Title,
            WebpageUrl = track.WebpageUrl ?? track.MediaUrl,
            ThumbnailUrl = track.ThumbnailUrl,
            DurationSeconds = track.Duration?.TotalSeconds,
            SourceBytes = track.SourceBytes,
            TrackId = track.TrackId,
        };
    }

    public TrackInfoEntity ToTrackInfo(long userId, string? requestedBy)
        => new()
        {
            TrackId = TrackId,
            Title = Title,
            MediaUrl = WebpageUrl,
            WebpageUrl = WebpageUrl,
            ThumbnailUrl = ThumbnailUrl,
            RequestedBy = requestedBy,
            RequestedByUserId = userId,
            Duration = DurationSeconds is double s && s > 0 ? TimeSpan.FromSeconds(s) : null,
            Source = Source,
            ExternalId = ExternalId,
            SourceBytes = SourceBytes,
            IsTooLarge = false,
        };

    public static bool TryParseToken(string? token, out string source, out string externalId)
    {
        source = string.Empty;
        externalId = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var idx = token.IndexOf(':');
        if (idx <= 0 || idx >= token.Length - 1)
        {
            return false;
        }

        source = token[..idx];
        externalId = token[(idx + 1)..];
        return source is TrackIdentityHelper.SourceYoutube or TrackIdentityHelper.SourceSoundcloud
               && !string.IsNullOrWhiteSpace(externalId);
    }
}
