using Mezube.Domain.Entities;

namespace Mezube.Music;

public sealed class CompositeTrackResolver : ITrackResolver
{
    private readonly IReadOnlyList<ITrackResolver> _resolvers;

    public CompositeTrackResolver(IEnumerable<ITrackResolver> resolvers)
    {
        _resolvers = resolvers.ToArray();
    }

    public bool CanResolve(string query) => _resolvers.Any(r => r.CanResolve(query));

    public async Task<TrackInfoEntity?> ResolveAsync(string query, string? requestedBy, CancellationToken cancellationToken = default)
    {
        foreach (var resolver in _resolvers)
        {
            if (!resolver.CanResolve(query))
            {
                continue;
            }

            var track = await resolver.ResolveAsync(query, requestedBy, cancellationToken).ConfigureAwait(false);
            if (track is not null)
            {
                return track;
            }
        }

        return null;
    }
}
