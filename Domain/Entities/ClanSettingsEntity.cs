namespace Mezube.Domain.Entities;

public sealed class ClanSettingsEntity
{
    public long ClanId { get; init; }
    public long? OwnerId { get; init; }
    public long? DjRoleId { get; init; }
    public long? DefaultStreamChannelId { get; init; }
    public bool VoteSkipEnabled { get; init; }
    public float VoteSkipRatio { get; init; } = 0.5f;
    public DateTimeOffset UpdatedAt { get; init; }
}
