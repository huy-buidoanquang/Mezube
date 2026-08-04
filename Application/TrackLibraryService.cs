using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence;

namespace Mezube.Application;

public interface ITrackLibraryService
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

public sealed class TrackLibraryService : ITrackLibraryService
{
    private readonly ITrackRepository _tracks;

    public TrackLibraryService(ITrackRepository tracks)
    {
        _tracks = tracks;
    }

    public Task<TrackEntity?> TryGetAsync(string source, string externalId, CancellationToken cancellationToken = default)
        => _tracks.TryGetAsync(source, externalId, cancellationToken);

    public Task<TrackEntity?> TryGetByIdAsync(long trackId, CancellationToken cancellationToken = default)
        => _tracks.TryGetByIdAsync(trackId, cancellationToken);

    public Task<TrackEntity?> TryGetByAliasAsync(string aliasKey, CancellationToken cancellationToken = default)
        => _tracks.TryGetByAliasAsync(aliasKey, cancellationToken);

    public Task<long> UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default)
        => _tracks.UpsertMetadataAsync(track, cancellationToken);

    public Task SetPlayableUrlAsync(
        string source,
        string externalId,
        string playableUrl,
        CancellationToken cancellationToken = default)
        => _tracks.SetPlayableUrlAsync(source, externalId, playableUrl, cancellationToken);

    public Task SetAliasAsync(
        string aliasKey,
        string source,
        string externalId,
        CancellationToken cancellationToken = default)
        => _tracks.SetAliasAsync(aliasKey, source, externalId, cancellationToken);

    public Task TouchPlayedAsync(string source, string externalId, CancellationToken cancellationToken = default)
        => _tracks.TouchPlayedAsync(source, externalId, cancellationToken);

    public Task ClearPlayableUrlAsync(string source, string externalId, CancellationToken cancellationToken = default)
        => _tracks.ClearPlayableUrlAsync(source, externalId, cancellationToken);

    public Task MarkTooLargeAsync(
        string source,
        string externalId,
        long? sourceBytes = null,
        string? title = null,
        CancellationToken cancellationToken = default)
        => _tracks.MarkTooLargeAsync(source, externalId, sourceBytes, title, cancellationToken);
}
