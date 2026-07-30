namespace Mezube.Domain.Entities;

public sealed class TrackAliasEntity
{
    public required string AliasKey { get; init; }
    public required string Source { get; init; }
    public required string ExternalId { get; init; }
}
