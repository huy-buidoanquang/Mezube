using Mezube.Domain.Entities;

namespace Mezube.Domain.Persistence;

public interface ITrackDb
{
    Task<TrackEntity?> TryGetAsync(string source, string externalId, CancellationToken cancellationToken = default);

    Task<TrackEntity?> TryGetByAliasAsync(string aliasKey, CancellationToken cancellationToken = default);

    Task UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default);

    Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default);

    Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default);

    Task TouchPlayedAsync(string source, string externalId, CancellationToken cancellationToken = default);
}
