using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence;

public interface IPlaylistRepository
{
    Task<PlaylistEntity?> TryGetByNameAsync(long clanId, string name, CancellationToken cancellationToken = default);

    Task<PlaylistEntity?> TryGetDefaultAsync(long clanId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> ListDefaultClanIdsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaylistEntity>> ListAsync(long clanId, CancellationToken cancellationToken = default);

    Task<PlaylistEntity> CreateAsync(
        long clanId,
        string name,
        long? createdBy,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(long clanId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears default for the clan, then sets <paramref name="playlistId"/> as default when non-null.
    /// </summary>
    Task SetDefaultAsync(long clanId, long? playlistId, CancellationToken cancellationToken = default);

    Task AddItemAsync(
        long playlistId,
        long trackId,
        long? addedBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaylistItemEntity>> ListItemsAsync(
        long playlistId,
        CancellationToken cancellationToken = default);
}
