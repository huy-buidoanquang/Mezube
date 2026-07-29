namespace Mezube.Domain.Entities;

public sealed class TrackInfoEntity
{
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

    public string DisplayDuration => Duration is { } d
        ? d.TotalHours >= 1
            ? d.ToString(@"h\:mm\:ss")
            : d.ToString(@"m\:ss")
        : "?:??";

    public TrackInfoEntity WithRequester(long userId, string? displayName)
        => new()
        {
            Title = Title,
            MediaUrl = MediaUrl,
            WebpageUrl = WebpageUrl,
            ThumbnailUrl = ThumbnailUrl,
            RequestedBy = displayName ?? RequestedBy,
            RequestedByUserId = userId,
            Duration = Duration,
            Source = Source,
            ExternalId = ExternalId,
        };
}
