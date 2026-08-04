namespace Mezube.Infrastructure.Caching.Snapshots;

public sealed class ClanSnapshotDto
{
    public long ClanId { get; set; }
    public long CreatorId { get; set; }
    public string? Name { get; set; }
    public long Revision { get; set; }
}

public sealed class ChannelSnapshotDto
{
    public long Id { get; set; }
    public long ClanId { get; set; }
    public long ParentId { get; set; }
    public long CategoryId { get; set; }
    public int Type { get; set; }
    public bool IsPrivate { get; set; }
    public string? Name { get; set; }
    public string? MeetingCode { get; set; }
    public long Revision { get; set; }
}

public sealed class RoleSnapshotDto
{
    public long Id { get; set; }
    public long ClanId { get; set; }
    public string? Title { get; set; }
    public string? Color { get; set; }
    public long[] MemberIds { get; set; } = [];
    public long Revision { get; set; }
}

public sealed class UserSnapshotDto
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public long Revision { get; set; }
}
