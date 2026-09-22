using Mezube.Bot;
using Mezube.Helpers;
using Mezube.Sfu;
using SIPSorcery.Net;
using System.Text.Json;

namespace Mezube.Tests;

public sealed class SfuAudioContractTests
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
    public void Validate_accepts_required_sfu_configuration()
        => ValidOptions().Validate();

    [Theory]
    [InlineData("http://sfu.example.test/ws")]
    [InlineData("https://sfu.example.test/ws")]
    [InlineData("sfu.example.test/ws")]
    [InlineData("")]
    [InlineData("ws://")]
    public void Validate_rejects_non_websocket_sfu_endpoint(string endpoint)
    {
        var options = ValidOptions();
        options.SfuWebSocketUrl = endpoint;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Publisher_creates_audio_track_without_video_track()
    {
        using var peerConnection = SfuPublisherSession.CreateAudioPublisherPeerConnection();

        Assert.NotNull(peerConnection.AudioLocalTrack);
        Assert.Null(peerConnection.VideoLocalTrack);
    }

    [Fact]
    public void Full_sfu_offer_produces_same_m_lines_with_video_inactive()
    {
        using var peerConnection = SfuPublisherSession.CreateAudioPublisherPeerConnection();
        var answer = SfuPublisherSession.CreateAudioOnlyAnswer(peerConnection, FullSfuOffer);
        var offer = SDP.ParseSDPDescription(FullSfuOffer);
        var parsedAnswer = SDP.ParseSDPDescription(answer.sdp);

        Assert.Equal(offer.Media.Count, parsedAnswer.Media.Count);
        for (var i = 0; i < offer.Media.Count; i++)
        {
            Assert.Equal(offer.Media[i].Media, parsedAnswer.Media[i].Media);
            Assert.Equal(offer.Media[i].MediaID, parsedAnswer.Media[i].MediaID);
        }

        Assert.Equal(MediaStreamStatusEnum.SendOnly, parsedAnswer.Media[0].MediaStreamStatus);
        Assert.All(parsedAnswer.Media.Skip(1), media =>
            Assert.Equal(MediaStreamStatusEnum.Inactive, media.MediaStreamStatus));
    }

    [Fact]
    public void Joined_ice_servers_parse_stun_and_turn()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "joined",
              "room": "1",
              "iceServers": [
                { "urls": "stun:stun.l.google.com:19302" },
                { "urls": "turn:sfu.example.test:3478?transport=udp", "username": "u", "credential": "p" }
              ]
            }
            """);

        var servers = SfuPublisherSession.ParseIceServers(document.RootElement);

        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:stun.l.google.com:19302", servers[0].urls);
        Assert.Equal("turn:sfu.example.test:3478?transport=udp", servers[1].urls);
        Assert.Equal("u", servers[1].username);
        Assert.Equal("p", servers[1].credential);
    }

    [Fact]
    public void Joined_without_ice_servers_returns_empty()
    {
        using var document = JsonDocument.Parse("""{ "type": "joined", "room": "1" }""");

        Assert.Empty(SfuPublisherSession.ParseIceServers(document.RootElement));
    }

    [Fact]
    public void Publisher_applies_ice_servers_from_joined()
    {
        var servers = new[] { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } };
        using var peerConnection = SfuPublisherSession.CreateAudioPublisherPeerConnection(servers);

        Assert.NotNull(peerConnection.AudioLocalTrack);
        Assert.NotNull(peerConnection.getConfiguration()?.iceServers);
        Assert.Equal("stun:stun.l.google.com:19302", peerConnection.getConfiguration()!.iceServers[0].urls);
    }

    private static string FullSfuOffer => string.Join("\r\n", new[]
    {
        "v=0",
        "o=- 1 1 IN IP4 127.0.0.1",
        "s=-",
        "t=0 0",
        "a=group:BUNDLE 0 1 2 3 4 5",
        "a=ice-lite",
        "m=audio 9 UDP/TLS/RTP/SAVPF 111",
        "c=IN IP4 0.0.0.0",
        "a=mid:0",
        "a=recvonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:111 opus/48000/2",
        "m=video 9 UDP/TLS/RTP/SAVPF 96 97",
        "c=IN IP4 0.0.0.0",
        "a=mid:1",
        "a=recvonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:96 VP8/90000",
        "a=rtpmap:97 rtx/90000",
        "a=fmtp:97 apt=96",
        "m=video 9 UDP/TLS/RTP/SAVPF 98 99",
        "c=IN IP4 0.0.0.0",
        "a=mid:2",
        "a=recvonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:98 VP9/90000",
        "a=rtpmap:99 rtx/90000",
        "a=fmtp:99 apt=98",
        "m=audio 9 UDP/TLS/RTP/SAVPF 111",
        "c=IN IP4 0.0.0.0",
        "a=mid:3",
        "a=sendonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:111 opus/48000/2",
        "m=video 9 UDP/TLS/RTP/SAVPF 96 97",
        "c=IN IP4 0.0.0.0",
        "a=mid:4",
        "a=sendonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:96 VP8/90000",
        "a=rtpmap:97 rtx/90000",
        "a=fmtp:97 apt=96",
        "m=video 9 UDP/TLS/RTP/SAVPF 98 99",
        "c=IN IP4 0.0.0.0",
        "a=mid:5",
        "a=sendonly",
        "a=rtcp-mux",
        "a=ice-ufrag:sfuUfrag",
        "a=ice-pwd:sfuPasswordValueGoesHereXXXX",
        "a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF",
        "a=setup:actpass",
        "a=rtpmap:98 VP9/90000",
        "a=rtpmap:99 rtx/90000",
        "a=fmtp:99 apt=98",
        string.Empty
    });

    private static BotOptions ValidOptions()
        => new()
        {
            BotId = 1,
            Token = "test-token",
            SfuWebSocketUrl = "wss://sfu.example.test/ws",
            CdnBaseUrl = "https://cdn.example.test",
            PostgresConnectionString = "Host=localhost;Database=test",
            RedisConnectionString = "localhost:6379"
        };
}
