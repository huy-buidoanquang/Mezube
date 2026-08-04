namespace Mezube.Domain.Entities;

public sealed class PlaylistEntity
{
    public long Id { get; init; }
    public long ClanId { get; init; }
    public required string Name { get; init; }
    public long? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class PlaylistItemEntity
{
    public long Id { get; init; }
    public long PlaylistId { get; init; }
    public int Position { get; init; }
    public long TrackId { get; init; }
    public long? AddedBy { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public TrackEntity? Track { get; init; }
}
