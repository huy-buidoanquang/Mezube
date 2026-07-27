using Mezube.Domain.Entities;

namespace Mezube.Music;

public interface ITrackResolver
{
    bool CanResolve(string query);
    Task<TrackInfoEntity?> ResolveAsync(string query, string? requestedBy, CancellationToken cancellationToken = default);
}
