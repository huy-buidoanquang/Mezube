namespace Mezube.Domain.Entities;

public sealed class TrackInfoEntity
{
    public long? TrackId { get; init; }
    public required string Title { get; init; }
    public required string MediaUrl { get; init; }
    public string? WebpageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? RequestedBy { get; init; }
    public long? RequestedByUserId { get; init; }
    public TimeSpan? Duration { get; init; }
    public string Source { get; init; } = "unknown";
    /// <summary>Source-scoped id (YouTube video id, URL hash, SoundCloud track id, …).</summary>
    public string? ExternalId { get; init; }
    /// <summary>Reported or approximate source media size in bytes (from yt-dlp probe or store).</summary>
    public long? SourceBytes { get; init; }
    /// <summary>True when store/prep already marked this track over the max size.</summary>
    public bool IsTooLarge { get; init; }

    public string DisplayDuration => Duration is { } d
        ? d.TotalHours >= 1
            ? d.ToString(@"h\:mm\:ss")
            : d.ToString(@"m\:ss")
        : "?:??";

    public TrackInfoEntity WithRequester(long userId, string? displayName)
        => new()
        {
            TrackId = TrackId,
            Title = Title,
            MediaUrl = MediaUrl,
            WebpageUrl = WebpageUrl,
            ThumbnailUrl = ThumbnailUrl,
            RequestedBy = displayName ?? RequestedBy,
            RequestedByUserId = userId,
            Duration = Duration,
            Source = Source,
            ExternalId = ExternalId,
            SourceBytes = SourceBytes,
            IsTooLarge = IsTooLarge,
        };
}
