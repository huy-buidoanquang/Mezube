using Mezube.Media;
using Mezube.Music;
using Mezube.Playback;
using Mezube.Streaming;
using Mezube.Ui;
using System.Text.Json;

namespace Mezube.Tests;

public sealed class StreamFailureTests
{
    [Theory]
    [InlineData(409, StreamFailureKind.ChannelBusy)]
    [InlineData(401, StreamFailureKind.Unavailable)]
    [InlineData(408, StreamFailureKind.Unavailable)]
    [InlineData(429, StreamFailureKind.Unavailable)]
    [InlineData(400, StreamFailureKind.Denied)]
    [InlineData(403, StreamFailureKind.Denied)]
    [InlineData(406, StreamFailureKind.Denied)]
    [InlineData(500, StreamFailureKind.Unavailable)]
    [InlineData(503, StreamFailureKind.Unavailable)]
    public void Whip_status_maps_to_a_failure_kind(int status, StreamFailureKind expected)
        => Assert.Equal(expected, StreamPublishException.ForWhipStatus(status));

    [Theory]
    [InlineData(3, StreamFailureKind.Denied)]
    [InlineData(5, StreamFailureKind.Denied)]
    [InlineData(7, StreamFailureKind.Denied)]
    [InlineData(400, StreamFailureKind.Denied)]
    [InlineData(403, StreamFailureKind.Denied)]
    [InlineData(404, StreamFailureKind.Denied)]
    [InlineData(13, StreamFailureKind.Unavailable)]
    [InlineData(14, StreamFailureKind.Unavailable)]
    [InlineData(500, StreamFailureKind.Unavailable)]
    [InlineData(503, StreamFailureKind.Unavailable)]
    public void Api_code_maps_to_a_failure_kind(int code, StreamFailureKind expected)
        => Assert.Equal(expected, StreamPublishException.ForApiCode(code));

    [Fact]
    public void Publish_grant_keeps_an_absolute_whip_url_and_its_token()
    {
        var grant = StreamingChannelSink.ToPublishGrant("https://streaming.example/whip/42", "jwt");

        Assert.Equal(new Uri("https://streaming.example/whip/42"), grant.WhipUrl);
        Assert.Equal("jwt", grant.Token);
    }

    [Theory]
    [InlineData("", "jwt")]
    [InlineData("/whip/42", "jwt")]
    [InlineData("wss://streaming.example/whip/42", "jwt")]
    [InlineData("https://streaming.example/whip/42", "")]
    public void Unusable_publish_grant_is_unavailable(string whipUrl, string token)
        => Assert.Equal(
            StreamFailureKind.Unavailable,
            Assert.Throws<StreamPublishException>(() => StreamingChannelSink.ToPublishGrant(whipUrl, token)).Kind);

    [Theory]
    [InlineData(StreamFailureKind.ChannelBusy, "Someone else is live in this channel.")]
    [InlineData(StreamFailureKind.Denied, "I can’t stream in that channel.")]
    [InlineData(StreamFailureKind.Unavailable, "Streaming is unavailable right now. Try again shortly.")]
    public void Stream_failure_has_its_own_message(StreamFailureKind kind, string expected)
        => Assert.Equal(expected, WhatHappened(PlayerMessageBuilder.PlaybackFailure(new StreamPublishException(kind, "x"))));

    [Fact]
    public void Stream_failure_wins_over_media_wording_in_inner_exceptions()
    {
        var failure = new StreamPublishException(
            StreamFailureKind.Denied,
            "x",
            new HttpRequestException("Response status code does not indicate success: 403 (Forbidden)."));

        Assert.Equal("I can’t stream in that channel.", WhatHappened(PlayerMessageBuilder.PlaybackFailure(failure)));
        Assert.False(MusicPlayer.IsMediaPrepFailure(failure));
        Assert.True(MusicPlayer.IsStreamInfrastructureFailure(failure));
    }

    [Fact]
    public void Missing_cdn_audio_is_reported_as_audio_missing()
    {
        var failure = new MediaPrepException(
            "Audio source could not be read.",
            new HttpRequestException("Response status code does not indicate success: 404 (Not Found)."));

        Assert.Equal(
            "That file isn’t on the CDN anymore. Queue it again so I can re-upload.",
            WhatHappened(PlayerMessageBuilder.PlaybackFailure(failure)));
    }

    private static string? WhatHappened(Mezon.Net.Client.MessageContent content)
        => JsonDocument.Parse(content.ToJson()).RootElement.GetProperty("embed")[0]
            .GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "What happened")
            .GetProperty("value").GetString();
}
