using Mezube.Helpers;

namespace Mezube.Tests;

public sealed class TrackIdentityHelperTests
{
    [Theory]
    [InlineData("soundcloud.com", true)]
    [InlineData("www.soundcloud.com", true)]
    [InlineData("m.soundcloud.com", true)]
    [InlineData("on.soundcloud.com", true)]
    [InlineData("api.soundcloud.com", true)]
    [InlineData("notsoundcloud.com", false)]
    [InlineData("evil-soundcloud.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSoundCloudHost(string? host, bool expected)
        => Assert.Equal(expected, TrackIdentityHelper.IsSoundCloudHost(host));

    [Theory]
    [InlineData("https://soundcloud.com/artist/track", true)]
    [InlineData("http://m.soundcloud.com/x/y", true)]
    [InlineData("https://on.soundcloud.com/AbCd", true)]
    [InlineData("ftp://soundcloud.com/x", false)]
    [InlineData("not-a-url", false)]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", false)]
    public void IsSoundCloudUrl(string input, bool expected)
        => Assert.Equal(expected, TrackIdentityHelper.IsSoundCloudUrl(input));

    [Theory]
    [InlineData("https://soundcloud.com/artist/sets/playlist-name", true)]
    [InlineData("https://soundcloud.com/artist/sets/foo/tracks", true)]
    [InlineData("https://soundcloud.com/artist/track", false)]
    [InlineData("https://soundcloud.com/artist/sets", false)]
    [InlineData("https://on.soundcloud.com/AbCdEf", false)]
    public void IsSoundCloudSetUrl(string input, bool expected)
        => Assert.Equal(expected, TrackIdentityHelper.IsSoundCloudSetUrl(input));

    [Theory]
    [InlineData("https://on.soundcloud.com/AbCdEf", true)]
    [InlineData("https://soundcloud.com/artist/track", false)]
    [InlineData("https://soundcloud.com/artist/sets/x", false)]
    public void IsSoundCloudShortUrl(string input, bool expected)
        => Assert.Equal(expected, TrackIdentityHelper.IsSoundCloudShortUrl(input));

    [Fact]
    public void NormalizeAbsoluteUrl_strips_tracking()
    {
        var a = TrackIdentityHelper.NormalizeAbsoluteUrl(
            "https://soundcloud.com/a/b?si=1&utm_source=x&foo=1#frag");
        var b = TrackIdentityHelper.NormalizeAbsoluteUrl(
            "https://soundcloud.com/a/b?foo=1");
        Assert.Equal(b, a);
        Assert.DoesNotContain("si=", a);
        Assert.DoesNotContain("utm_", a);
        Assert.DoesNotContain("#", a);
    }

    [Fact]
    public void ForDirectUrl_is_stable()
    {
        var url = "https://cdn.example.com/song.ogg?x=1";
        Assert.Equal(TrackIdentityHelper.ForDirectUrl(url), TrackIdentityHelper.ForDirectUrl(url));
        Assert.NotEqual(
            TrackIdentityHelper.ForDirectUrl(url),
            TrackIdentityHelper.ForDirectUrl("https://cdn.example.com/other.ogg"));
    }

    [Theory]
    [InlineData("https://soundcloud.com/a/b", null, null, "https://soundcloud.com/a/b")]
    [InlineData(null, "https://soundcloud.com/c/d", null, "https://soundcloud.com/c/d")]
    [InlineData("artist/track", null, null, "https://soundcloud.com/artist/track")]
    [InlineData(null, "artist/track", null, "https://soundcloud.com/artist/track")]
    [InlineData(null, null, "123456", "https://api.soundcloud.com/tracks/123456")]
    [InlineData(null, "987654", null, "https://api.soundcloud.com/tracks/987654")]
    [InlineData(null, null, null, null)]
    public void ResolveSoundCloudEntryUrl(string? webpage, string? url, string? id, string? expected)
        => Assert.Equal(expected, TrackIdentityHelper.ResolveSoundCloudEntryUrl(webpage, url, id));

    [Theory]
    [InlineData("dQw4w9WgXcQ", true, "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true, "dQw4w9WgXcQ")]
    [InlineData("https://soundcloud.com/a/b", false, "")]
    public void TryParseYoutubeId(string input, bool ok, string id)
    {
        Assert.Equal(ok, TrackIdentityHelper.TryParseYoutubeId(input, out var parsed));
        Assert.Equal(id, parsed);
    }
}
