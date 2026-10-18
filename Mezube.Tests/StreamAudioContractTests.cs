using Mezube.Bot;
using Mezube.Helpers;
using Mezube.Streaming;
using SIPSorcery.Net;
using System.Text.Json;

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
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection();

        Assert.NotNull(peerConnection.AudioLocalTrack);
        Assert.Null(peerConnection.VideoLocalTrack);
    }

    [Fact]
    public void Full_sfu_offer_produces_same_m_lines_with_video_inactive()
    {
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection();
        var answer = StreamPublisherSession.CreateAudioOnlyAnswer(peerConnection, FullSfuOffer);
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

    [Theory]
    [InlineData(6, 6, 6)]
    [InlineData(2, 1, 1)]
    public void Decoupled_sfu_offer_answer_keeps_every_m_line_with_only_mid_0_sending(int audio, int camera, int screen)
    {
        using var peerConnection = SfuPublisherSession.CreateAudioPublisherPeerConnection();
        var answer = SDP.ParseSDPDescription(
            SfuPublisherSession.CreateAudioOnlyAnswer(peerConnection, DecoupledSfuOffer(audio, camera, screen)).sdp);

        var expected = new[] { SDPMediaTypesEnum.audio, SDPMediaTypesEnum.video, SDPMediaTypesEnum.video }
            .Concat(Enumerable.Repeat(SDPMediaTypesEnum.audio, audio))
            .Concat(Enumerable.Repeat(SDPMediaTypesEnum.video, camera + screen))
            .ToArray();
        Assert.Equal(expected.Length, answer.Media.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], answer.Media[i].Media);
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), answer.Media[i].MediaID);
        }

        Assert.Equal(MediaStreamStatusEnum.SendOnly, answer.Media[0].MediaStreamStatus);
        Assert.NotEmpty(answer.Media[0].SsrcAttributes);
        Assert.All(answer.Media.Skip(1), media =>
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

        var servers = StreamPublisherSession.ParseIceServers(document.RootElement);

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

        Assert.Empty(StreamPublisherSession.ParseIceServers(document.RootElement));
    }

    [Fact]
    public void Publisher_applies_ice_servers_from_joined()
    {
        var servers = new[] { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } };
        using var peerConnection = StreamPublisherSession.CreateAudioPublisherPeerConnection(servers);

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

    private static string DecoupledSfuOffer(int audio, int camera, int screen)
    {
        const uint slotSsrcBase = 0x10000000;
        var total = 3 + audio + camera + screen;
        var lines = new List<string>
        {
            "v=0",
            "o=- 1792664000 2 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "a=group:BUNDLE " + string.Join(' ', Enumerable.Range(0, total)),
            "a=ice-lite",
        };

        void Section(int mid, string media, string payloads, string direction, IEnumerable<string> attributes)
        {
            lines.Add($"m={media} 7000 UDP/TLS/RTP/SAVPF {payloads}");
            lines.Add("c=IN IP4 127.0.0.1");
            lines.Add("a=ice-ufrag:sfuUfrag");
            lines.Add("a=ice-pwd:sfuPasswordValueGoesHereXXXX");
            lines.Add("a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF");
            lines.Add("a=setup:passive");
            if (mid == 0)
            {
                lines.Add("a=candidate:1 1 udp 2130706431 127.0.0.1 7000 typ host");
                lines.Add("a=end-of-candidates");
            }

            lines.Add($"a={direction}");
            lines.Add($"a=mid:{mid}");
            lines.Add("a=rtcp-mux");
            lines.AddRange(attributes);
        }

        string[] Opus() => ["a=rtpmap:111 opus/48000/2", "a=fmtp:111 minptime=10;useinbandfec=1;usedtx=1;maxaveragebitrate=32000"];
        string[] Vp8() => ["a=rtpmap:96 VP8/90000", "a=rtcp-fb:96 nack", "a=rtcp-fb:96 nack pli", "a=rtcp-fb:96 transport-cc", "a=rtpmap:97 rtx/90000", "a=fmtp:97 apt=96"];
        string[] Vp9() => ["a=rtpmap:98 VP9/90000", "a=rtcp-fb:98 nack", "a=rtcp-fb:98 nack pli", "a=rtcp-fb:98 transport-cc", "a=fmtp:98 profile-id=0", "a=rtpmap:99 rtx/90000", "a=fmtp:99 apt=98"];
        const string twcc = "a=extmap:6 http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01";
        const string sdesMid = "a=extmap:7 urn:ietf:params:rtp-hdrext:sdes:mid";
        string[] VideoSlot(string msid, int slot, uint offset)
        {
            var media = slotSsrcBase + (uint)(8 * slot) + offset;
            return
            [
                $"a=msid:{msid}",
                $"a=ssrc-group:FID {media} {media + 1}",
                $"a=ssrc:{media} cname:slot-{slot}",
                $"a=ssrc:{media} msid:{msid}",
                $"a=ssrc:{media + 1} cname:slot-{slot}",
                $"a=ssrc:{media + 1} msid:{msid}",
            ];
        }

        Section(0, "audio", "111", "recvonly", [twcc, sdesMid, "a=extmap:8 urn:ietf:params:rtp-hdrext:ssrc-audio-level", .. Opus()]);
        Section(1, "video", "96 97", "recvonly", [twcc, sdesMid, .. Vp8()]);
        Section(2, "video", "98 99 96 97", "recvonly", [twcc, sdesMid, .. Vp9(), .. Vp8()]);
        for (var slot = 0; slot < audio; slot++)
        {
            var ssrc = slotSsrcBase + (uint)(8 * slot);
            Section(3 + slot, "audio", "111", "sendonly",
            [
                sdesMid, .. Opus(),
                $"a=msid:audio-slot-{slot} audio-slot-{slot}",
                $"a=ssrc:{ssrc} cname:slot-{slot}",
                $"a=ssrc:{ssrc} msid:audio-slot-{slot} audio-slot-{slot}",
            ]);
        }

        for (var slot = 0; slot < camera; slot++)
        {
            Section(3 + audio + slot, "video", "96 97", "sendonly", [sdesMid, twcc, .. Vp8(), .. VideoSlot($"camera-slot-{slot} video-slot-{slot}", slot, 1)]);
        }

        for (var slot = 0; slot < screen; slot++)
        {
            Section(3 + audio + camera + slot, "video", "98 99 96 97", "sendonly", [sdesMid, twcc, .. Vp9(), .. Vp8(), .. VideoSlot($"screen-slot-{slot} screen-slot-{slot}", slot, 3)]);
        }

        lines.Add(string.Empty);
        return string.Join("\r\n", lines);
    }

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
