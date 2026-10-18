using Mezube.Bot;
using Mezube.Helpers;
using Mezube.Streaming;
using SIPSorcery.Net;

namespace Mezube.Tests;

public sealed class StreamAudioContractTests
{
    [Theory]
    [InlineData("https://cdn.example.test/song.ogg", true)]
    [InlineData("https://cdn.example.test/song.opus", true)]
    [InlineData("https://cdn.example.test/song.OGG?signature=redacted", true)]
    [InlineData("http://localhost:8080/audio.opus", true)]
    [InlineData("https://cdn.example.test/song.webm", false)]
    [InlineData("https://cdn.example.test/song.mp3", false)]
    [InlineData("https://cdn.example.test/song.ogg.webm", false)]
    [InlineData("https://cdn.example.test/song", false)]
    [InlineData("ftp://cdn.example.test/song.ogg", false)]
    [InlineData("/local/song.ogg", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void IsPreparedAudioUrl_accepts_only_http_ogg_or_opus(string url, bool expected)
        => Assert.Equal(expected, PlayableUrlHelper.IsPreparedAudioUrl(url));

    [Fact]
    public void Validate_accepts_required_configuration()
        => ValidOptions().Validate();

    [Fact]
    public void Publisher_creates_audio_track_without_video_track()
    {
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection();

        Assert.NotNull(peerConnection.AudioLocalTrack);
        Assert.Null(peerConnection.VideoLocalTrack);
    }

    [Fact]
    public async Task Offer_has_one_sendonly_opus_section_the_streaming_server_accepts()
    {
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection();
        var offer = peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer);
        var sdp = SDP.ParseSDPDescription(offer.sdp);

        var audio = Assert.Single(sdp.Media);
        Assert.Equal(SDPMediaTypesEnum.audio, audio.Media);
        Assert.Equal(MediaStreamStatusEnum.SendOnly, audio.MediaStreamStatus);
        Assert.False(string.IsNullOrEmpty(audio.MediaID));
        Assert.Contains("a=setup:actpass", offer.sdp);
        Assert.Contains("a=fingerprint:sha-256", offer.sdp);
        Assert.Contains("opus/48000/2", offer.sdp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a=ssrc:", offer.sdp);
        Assert.DoesNotContain("m=video", offer.sdp);
    }

    [Fact]
    public async Task Node_answer_makes_the_publisher_ice_controlling_and_dtls_client()
    {
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection();
        var offer = peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer);

        var result = peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = WhipTestNode.CannedAnswer(offer.sdp),
        });

        Assert.Equal(SetDescriptionResultEnum.OK, result);
        Assert.Equal(IceRolesEnum.active, peerConnection.IceRole);
        Assert.True(peerConnection.GetRtpChannel().IsController);
    }

    private static BotOptions ValidOptions()
        => new()
        {
            BotId = 1,
            Token = "test-token",
            CdnBaseUrl = "https://cdn.example.test",
            PostgresConnectionString = "Host=localhost;Database=test",
            RedisConnectionString = "localhost:6379"
        };
}
