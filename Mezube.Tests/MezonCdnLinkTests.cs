using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence;
using Mezube.Music;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class MezonCdnLinkTests
{
    [Theory]
    [InlineData("cdn.mezon.ai", true)]
    [InlineData("cdn.komu.vn", true)]
    [InlineData("CDN.KOMU.VN", true)]
    [InlineData("cdn.mezon.ai.evil.example", false)]
    [InlineData("evilcdn.komu.vn", false)]
    [InlineData("pub-123.r2.dev", false)]
    public void IsMezonCdnHost_matches_only_mezon_cdn_hosts(string host, bool expected)
        => Assert.Equal(expected, DirectUrlTrackResolver.IsMezonCdnHost(host));

    [Theory]
    [InlineData("https://cdn.mezon.ai/0/1840682626818510848/1779513150169682000/1747814491095_0song.mp3", "1747814491095_0song")]
    [InlineData("https://cdn.komu.vn/1785968850517364736/2085204327911133184.mp3", "2085204327911133184")]
    [InlineData("https://cdn.komu.vn/1840673171137630208/2097582928409137152.wav", "2097582928409137152")]
    public async Task ResolveAsync_keeps_mezon_cdn_link_as_media_url(string url, string title)
    {
        var resolver = new DirectUrlTrackResolver(new FakeTrackStore(), NullLogger<DirectUrlTrackResolver>.Instance);

        Assert.True(resolver.CanResolve(url));
        var track = await resolver.ResolveAsync(url, "requester");

        Assert.NotNull(track);
        Assert.Equal(url, track!.MediaUrl);
        Assert.Equal(url, track.WebpageUrl);
        Assert.Equal(title, track.Title);
        Assert.Equal(TrackIdentityHelper.SourceUrl, track.Source);
    }

    private sealed class FakeTrackStore : ITrackRepository
    {
        public Task<TrackEntity?> TryGetAsync(string source, string externalId, CancellationToken cancellationToken = default)
            => Task.FromResult<TrackEntity?>(null);

        public Task<TrackEntity?> TryGetByAliasAsync(string aliasKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> UpsertMetadataAsync(TrackEntity track, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);

        public Task SetPlayableUrlAsync(string source, string externalId, string playableUrl, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetAliasAsync(string aliasKey, string source, string externalId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task TouchPlayedAsync(string source, string externalId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearPlayableUrlAsync(string source, string externalId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkTooLargeAsync(string source, string externalId, long? sourceBytes = null, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
