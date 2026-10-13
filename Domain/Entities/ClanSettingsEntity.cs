namespace Mezube.Domain.Entities;

public sealed class ClanSettingsEntity
{
    public long ClanId { get; init; }
    public long? OwnerId { get; init; }
    public long? DjRoleId { get; init; }
    public long? DefaultStreamChannelId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
