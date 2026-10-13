using Mezube.Media;
using Mezube.Music;
using System.Net;

namespace Mezube.Tests;

public sealed class DirectUrlSsrfTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void IsPublicAddress_rejects_internal_addresses(string ip)
    {
        Assert.False(DirectUrlTrackResolver.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublicAddress_accepts_public_addresses(string ip)
    {
        Assert.True(DirectUrlTrackResolver.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/x.mp3")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/a.ogg")]
    [InlineData("http://[fd00::1]:5432/")]
    [InlineData("http://localhost:5432/")]
    [InlineData("http://2130706433/")]
    public async Task IsPublicHostAsync_rejects_internal_url_hosts(string url)
    {
        var uri = new Uri(url);
        Assert.False(await DirectUrlTrackResolver.IsPublicHostAsync(uri.IdnHost, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://8.8.8.8/a.mp3")]
    [InlineData("https://[2606:4700:4700::1111]/a.ogg")]
    public async Task IsPublicHostAsync_accepts_public_ip_literals(string url)
    {
        var uri = new Uri(url);
        Assert.True(await DirectUrlTrackResolver.IsPublicHostAsync(uri.IdnHost, CancellationToken.None));
    }

    private const string CdnBaseUrl = "https://cdn.komu.vn";

    [Theory]
    [InlineData("https://cdn.komu.vn.evil.example/x.normalized.ogg")]
    [InlineData("https://r2.dev.evil.example/x.normalized.ogg")]
    [InlineData("https://cdn.mezon.evil.example/x.ogg")]
    [InlineData("https://evilcdn.komu.vn/x.ogg")]
    [InlineData("https://pub-123.r2.dev/x.ogg")]
    [InlineData("http://127.0.0.1/x.ogg")]
    public void NeedsRepackage_is_true_for_hosts_other_than_cdn(string url)
    {
        Assert.True(PlayableMediaProcessor.NeedsRepackage(url, CdnBaseUrl));
    }

    [Theory]
    [InlineData("https://cdn.komu.vn/mezube/x.normalized.ogg")]
    [InlineData("https://CDN.KOMU.VN/mezube/x.opus")]
    public void NeedsRepackage_is_false_for_prepared_audio_on_cdn(string url)
    {
        Assert.False(PlayableMediaProcessor.NeedsRepackage(url, CdnBaseUrl));
    }

    [Fact]
    public void NeedsRepackage_is_true_for_non_ogg_on_cdn()
    {
        Assert.True(PlayableMediaProcessor.NeedsRepackage(
            "https://cdn.komu.vn/mezube/x.mp3",
            CdnBaseUrl));
    }
}
