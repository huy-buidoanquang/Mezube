using Mezube.Domain.Entities;
using Mezube.Music;

namespace Mezube.Tests;

public sealed class CompositeTrackResolverTests
{
    private sealed class FakeResolver(string needle, string title) : ITrackResolver
    {
        public bool CanResolve(string query) => query.Contains(needle, StringComparison.OrdinalIgnoreCase);

        public Task<TrackInfoEntity?> ResolveAsync(string query, string? requestedBy, CancellationToken cancellationToken = default)
            => Task.FromResult<TrackInfoEntity?>(new TrackInfoEntity
            {
                Title = title,
                MediaUrl = query,
                RequestedBy = requestedBy,
            });
    }

    [Fact]
    public async Task Prefers_first_matching_resolver()
    {
        var composite = new CompositeTrackResolver(
        [
            new FakeResolver("soundcloud.com", "sc"),
            new FakeResolver("youtube.com", "yt"),
            new FakeResolver("http", "direct"),
        ]);

        var track = await composite.ResolveAsync("https://soundcloud.com/a/b", "u");
        Assert.Equal("sc", track!.Title);
    }

    [Fact]
    public async Task Falls_through_when_first_cannot_resolve()
    {
        var composite = new CompositeTrackResolver(
        [
            new FakeResolver("soundcloud.com", "sc"),
            new FakeResolver("youtu", "yt"),
        ]);

        var track = await composite.ResolveAsync("https://youtu.be/dQw4w9WgXcQ", "u");
        Assert.Equal("yt", track!.Title);
    }

    [Fact]
    public async Task Returns_null_when_nothing_matches()
    {
        var composite = new CompositeTrackResolver([new FakeResolver("soundcloud.com", "sc")]);
        Assert.Null(await composite.ResolveAsync("plain text search", "u"));
    }
}
