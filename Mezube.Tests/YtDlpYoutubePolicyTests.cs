using Mezube.Media;

namespace Mezube.Tests;

public sealed class YtDlpYoutubePolicyTests
{
    [Theory]
    [InlineData(0, null, "android,web,mweb")]
    [InlineData(0, "android,web,mweb", "android,web,mweb")]
    [InlineData(1, "android,web,mweb", "ios,tv,web_safari")]
    [InlineData(2, "android,web,mweb", "tv_embedded,web")]
    [InlineData(9, "android,web,mweb", "tv_embedded,web")]
    public void PlayerClientsForAttempt_rotates(int attempt, string? primary, string expected)
        => Assert.Equal(expected, YtDlpYoutubePolicy.PlayerClientsForAttempt(attempt, primary));

    [Fact]
    public void PlayerClientsForAttempt_skips_duplicate_primary()
        => Assert.Equal(
            "tv_embedded,web",
            YtDlpYoutubePolicy.PlayerClientsForAttempt(1, "ios,tv,web_safari"));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("ytsearch5:never gonna", true)]
    [InlineData("https://soundcloud.com/artist/track", false)]
    public void IsYoutubeSource(string source, bool expected)
        => Assert.Equal(expected, YtDlpYoutubePolicy.IsYoutubeSource(source));

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", true)]
    [InlineData("HTTP Error 429: Too Many Requests", true)]
    [InlineData("This content isn't available, try again later", false)]
    [InlineData("ERROR: Private video", false)]
    [InlineData("Sign in to confirm your age", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTransientDownloadFailure(string? stderr, bool expected)
        => Assert.Equal(expected, YtDlpYoutubePolicy.IsTransientDownloadFailure(stderr));
}
