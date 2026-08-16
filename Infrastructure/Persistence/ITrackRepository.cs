using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence;

public interface ITrackRepository
{
    Task<TrackEntity?> TryGetAsync(string source, string externalId, CancellationToken cancellationToken = default);

    Task<TrackEntity?> TryGetByIdAsync(long trackId, CancellationToken cancellationToken = default);

    Task<TrackEntity?> TryGetByAliasAsync(string aliasKey, CancellationToken cancellationToken = default);

    Task<long> UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default);

    Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default);

    Task SetPlayableVideoUrlAsync(
        string source,
        string externalId,
        string playableVideoUrl,
        CancellationToken cancellationToken = default);

    Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default);

    Task TouchPlayedAsync(string source, string externalId, CancellationToken cancellationToken = default);

    Task ClearPlayableUrlAsync(string source, string externalId, CancellationToken cancellationToken = default);

    Task MarkTooLargeAsync(
        string source,
        string externalId,
        long? sourceBytes = null,
        string? title = null,
        CancellationToken cancellationToken = default);
}
