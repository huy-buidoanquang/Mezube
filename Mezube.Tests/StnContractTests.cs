using Mezube.Helpers;
using Mezube.Stn;

namespace Mezube.Tests;

public sealed class StnContractTests
{
    [Theory]
    [InlineData("https://cdn.example/a.ogg", true)]
    [InlineData("https://cdn.example/a.opus", true)]
    [InlineData("https://cdn.example/a.webm", false)]
    [InlineData("https://youtube.com/watch?v=dQw4w9wgWcQ", false)]
    public void Opus_source_urls(string url, bool expected)
        => Assert.Equal(expected, StnMediaUrl.IsSupportedOpusSourceUrl(url));

    [Theory]
    [InlineData("https://cdn.example/a.ogg", true)]
    [InlineData("https://cdn.example/a.webm", true)]
    [InlineData("https://cdn.example/a.mp4", false)]
    public void Streaming_source_urls(string url, bool expected)
        => Assert.Equal(expected, StnMediaUrl.IsSupportedStreamingSourceUrl(url));

    [Fact]
    public void Playable_helper_splits_audio_and_video()
    {
        Assert.True(PlayableUrlHelper.IsPreparedAudioUrl("https://cdn/x.ogg"));
        Assert.False(PlayableUrlHelper.IsPreparedAudioUrl("https://cdn/x.webm"));
        Assert.True(PlayableUrlHelper.IsPreparedVideoUrl("https://cdn/x.webm"));
        Assert.True(PlayableUrlHelper.IsPreparedStreamingUrl("https://cdn/x.webm"));
        Assert.True(PlayableUrlHelper.IsPreparedPlayableUrl("https://cdn/x.webm"));
    }

    [Fact]
    public void Websocket_path_from_origin()
    {
        const string origin = "https://stn.mezon.ai";
        Assert.Equal("wss://stn.mezon.ai/ws", StnUrl.WebSocketBase(origin));
    }

    [Theory]
    [InlineData("https://stn.mezon.ai/api/v2/voice/play")]
    [InlineData("https://stn.mezon.ai/api/voice/play")]
    [InlineData("https://stn.mezon.ai/api/whip/start")]
    [InlineData("https://stn.mezon.ai/ws")]
    public void Normalize_strips_known_suffixes(string pasted)
        => Assert.Equal("https://stn.mezon.ai", StnUrl.NormalizeBase(pasted));
}
