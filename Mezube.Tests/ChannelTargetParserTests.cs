using Mezube.Helpers;

namespace Mezube.Tests;

public sealed class ChannelTargetParserTests
{
    [Theory]
    [InlineData(new[] { "never", "gonna" }, "never gonna", false)]
    [InlineData(new[] { "-v", "never", "gonna" }, "never gonna", true)]
    [InlineData(new[] { "--video", "never" }, "never", true)]
    [InlineData(new[] { "never", "-v", "gonna" }, "never gonna", true)]
    [InlineData(new[] { "-V", "#radio", "https://youtu.be/dQw4w9WgXcQ" }, "https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData(new[] { "-v" }, "", true)]
    [InlineData(new[] { "https://youtu.be/dQw4w9WgXcQ" }, "https://youtu.be/dQw4w9WgXcQ", false)]
    public void ParsePlayArgs_strips_video_flag_and_hashtags(string[] args, string query, bool wantVideo)
    {
        var parsed = ChannelTargetParser.ParsePlayArgs(args);
        Assert.Equal(query, parsed.Query);
        Assert.Equal(wantVideo, parsed.WantVideo);
    }

    [Fact]
    public void BuildQuery_matches_ParsePlayArgs_query()
    {
        var args = new[] { "-v", "#stream", "rick" };
        Assert.Equal("rick", ChannelTargetParser.BuildQuery(args));
        Assert.Equal("rick", ChannelTargetParser.ParsePlayArgs(args).Query);
    }
}
