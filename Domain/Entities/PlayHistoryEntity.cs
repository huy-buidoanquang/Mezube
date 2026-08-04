namespace Mezube.Domain.Entities;

public sealed class PlayHistoryEntity
{
    public long Id { get; init; }
    public long ClanId { get; init; }
    public long TrackId { get; init; }
    public required string Mode { get; init; }
    public long ChannelId { get; init; }
    public long? RequestedByUserId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EndReason { get; init; }
}

public static class PlayEndReason
{
    public const string Completed = "completed";
    public const string Skip = "skip";
    public const string VoteSkip = "vote_skip";
    public const string Stop = "stop";
    public const string Error = "error";
    public const string TooLarge = "too_large";
    public const string Restart = "restart";
}
