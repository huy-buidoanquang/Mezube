using Mezube.Helpers;

namespace Mezube.Tests;

public sealed class HashtagChannelIdTests
{
    private const string CdnLink = "https://cdn.komu.vn/1785968850517364736/2085204327911133184.mp3";

    [Fact]
    public void TryReadHashtagChannelId_ignores_link_preview_marker_past_end_of_text()
    {
        var text = "!play " + CdnLink;
        var json = "{\"t\":\"" + text + "\",\"mk\":["
                   + "{\"type\":\"lk\",\"s\":6,\"e\":" + text.Length + "},"
                   + "{\"type\":\"lk_ogp\",\"s\":" + text.Length + ",\"e\":" + (text.Length + 1) + ",\"url\":\"" + CdnLink + "\"}]}";

        Assert.Null(ChannelTargetParser.TryReadHashtagChannelId(json));
    }

    [Fact]
    public void TryReadHashtagChannelId_reads_channel_next_to_link_preview_marker()
    {
        var text = "!play #music " + CdnLink;
        var json = "{\"t\":\"" + text + "\","
                   + "\"hg\":[{\"channelId\":\"1840675209556791296\",\"s\":6,\"e\":12}],"
                   + "\"mk\":[{\"type\":\"lk\",\"s\":13,\"e\":" + text.Length + "},"
                   + "{\"type\":\"lk_ogp\",\"s\":" + text.Length + ",\"e\":" + (text.Length + 1) + ",\"url\":\"" + CdnLink + "\"}]}";

        Assert.Equal(1840675209556791296, ChannelTargetParser.TryReadHashtagChannelId(json));
    }

    [Theory]
    [InlineData("{\"t\":\"!play #music x\",\"hg\":[{\"channelId\":\"1840675209556791296\",\"s\":6,\"e\":12}]}", 1840675209556791296L)]
    [InlineData("{\"t\":\"!play #music x\",\"hg\":[{\"channelId\":1840675209556791296,\"s\":6,\"e\":12}]}", 1840675209556791296L)]
    [InlineData("{\"t\":\"x\",\"hg\":[{\"channelId\":\"1840675209556791296\",\"s\":40,\"e\":99}]}", 1840675209556791296L)]
    [InlineData("{\"t\":\"x\",\"hg\":[{\"channelId\":\"0\"},{\"channelId\":\"\"},{\"channelId\":\"1840675209556791296\"}]}", 1840675209556791296L)]
    public void TryReadHashtagChannelId_returns_first_valid_channel(string json, long expected)
        => Assert.Equal(expected, ChannelTargetParser.TryReadHashtagChannelId(json));

    [Theory]
    [InlineData("{\"t\":\"!play x\"}")]
    [InlineData("{\"t\":\"!play x\",\"hg\":[]}")]
    [InlineData("{\"t\":\"!play x\",\"hg\":[{\"channelId\":null}]}")]
    [InlineData("{\"t\":\"!play x\",\"hg\":{}}")]
    [InlineData("not json")]
    [InlineData("")]
    public void TryReadHashtagChannelId_returns_null_without_a_channel(string json)
        => Assert.Null(ChannelTargetParser.TryReadHashtagChannelId(json));
}
