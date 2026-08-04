using System.Text.Json;
using System.Text.Json.Serialization;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Playback;

namespace Mezube.Infrastructure.Persistence.Redis;

public enum LoopMode
{
    Off,
    Track,
    Queue,
}

public sealed class QueuedTrackPayload
{
    public long? TrackId { get; set; }
    public string Source { get; set; } = "unknown";
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? WebpageUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double? DurationSeconds { get; set; }
    public string? PlayableUrl { get; set; }
    public string Mode { get; set; } = "voice";
    public long ChannelId { get; set; }
    public string? ChannelLabel { get; set; }
    public string? RoomName { get; set; }
    public long? RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }
    public long? ReplyMessageId { get; set; }
    public uint? ReplyCreateTimeSecs { get; set; }

    public static QueuedTrackPayload From(
        TrackInfoEntity track,
        string mode,
        PlaybackTarget target,
        long? replyMessageId,
        uint? replyCreateTimeSeconds)
        => new()
        {
            TrackId = track.TrackId,
            Source = track.Source,
            ExternalId = track.ExternalId ?? string.Empty,
            Title = track.Title,
            WebpageUrl = track.WebpageUrl,
            ThumbnailUrl = track.ThumbnailUrl,
            DurationSeconds = track.Duration?.TotalSeconds,
            PlayableUrl = PlayableUrlHelper.NullIfNotPrepared(track.MediaUrl),
            Mode = mode,
            ChannelId = target.ChannelId,
            ChannelLabel = target.ChannelLabel,
            RoomName = target.RoomName,
            RequestedByUserId = track.RequestedByUserId,
            RequestedByName = track.RequestedBy,
            ReplyMessageId = replyMessageId,
            ReplyCreateTimeSecs = replyCreateTimeSeconds,
        };

    public TrackInfoEntity ToTrackInfo()
        => new()
        {
            TrackId = TrackId,
            Title = Title,
            MediaUrl = PlayableUrlHelper.IsPreparedPlayableUrl(PlayableUrl)
                ? PlayableUrl!
                : !string.IsNullOrWhiteSpace(WebpageUrl) ? WebpageUrl! : ExternalId,
            WebpageUrl = WebpageUrl,
            ThumbnailUrl = ThumbnailUrl,
            RequestedBy = RequestedByName,
            RequestedByUserId = RequestedByUserId,
            Duration = DurationSeconds is { } d ? TimeSpan.FromSeconds(d) : null,
            Source = Source,
            ExternalId = ExternalId,
            IsTooLarge = false,
        };

    public PlaybackTarget ToTarget(long clanId)
        => new(clanId, ChannelId, RoomName, ChannelLabel);
}

public sealed class AdvanceResult
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Action { get; init; }
    public QueuedTrackPayload? Next { get; init; }
    public QueuedTrackPayload? Current { get; init; }

    public static AdvanceResult Stale() => new() { Ok = false, Reason = "stale" };
}

public static class RedisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
