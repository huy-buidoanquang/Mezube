using Mezube.Helpers;

namespace Mezube.Domain.Entities;

public sealed class TrackEntity
{
    public long Id { get; init; }
    public required string Source { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public string? WebpageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public TimeSpan? Duration { get; init; }
    /// <summary>Mezon CDN .ogg/.opus after audio prep — never a YouTube/SoundCloud webpage URL.</summary>
    public string? PlayableUrl { get; init; }
    /// <summary>Mezon CDN .webm (Opus+VP8) after streaming video prep.</summary>
    public string? PlayableVideoUrl { get; init; }
    /// <summary>Persisted probe/download size when known.</summary>
    public long? SourceBytes { get; init; }
    /// <summary>True when this track exceeded the max audio size and must not be queued/played.</summary>
    public bool IsTooLarge { get; init; }

    public bool HasPlayableUrl => PlayableUrlHelper.IsPreparedAudioUrl(PlayableUrl);
    public bool HasPlayableVideoUrl => PlayableUrlHelper.IsPreparedVideoUrl(PlayableVideoUrl);

    public TrackInfoEntity ToTrackInfo(string? requestedBy)
    {
        var mediaUrl = HasPlayableUrl
            ? PlayableUrl!
            : !string.IsNullOrWhiteSpace(WebpageUrl) ? WebpageUrl! : ExternalId;

        return new TrackInfoEntity
        {
            TrackId = Id == 0 ? null : Id,
            Title = Title,
            MediaUrl = mediaUrl,
            WebpageUrl = WebpageUrl,
            ThumbnailUrl = ThumbnailUrl,
            RequestedBy = requestedBy,
            Duration = Duration,
            Source = Source,
            ExternalId = ExternalId,
            SourceBytes = SourceBytes,
            IsTooLarge = IsTooLarge,
        };
    }
}
