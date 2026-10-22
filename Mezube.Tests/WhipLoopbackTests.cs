using Mezube.Bot;
using Mezube.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace Mezube.Tests;

public sealed class WhipLoopbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mezube-whip-" + Guid.NewGuid().ToString("N"));

    public WhipLoopbackTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Rtp_stays_continuous_across_track_changes_and_pause()
    {
        var received = new ConcurrentQueue<(uint Ssrc, ushort Seq, uint Timestamp, byte[] Payload, TimeSpan At)>();
        var clock = Stopwatch.StartNew();
        RTCPeerConnection? nodePeer = null;
        await using var node = await WhipTestNode.StartAsync();
        node.Reply = async offer =>
        {
            var peer = new RTCPeerConnection(new RTCConfiguration { X_BindAddress = IPAddress.Loopback });
            peer.addTrack(new MediaStreamTrack(
                new List<AudioFormat> { new(AudioCodecsEnum.OPUS, 111, 48000, 2) },
                MediaStreamStatusEnum.RecvOnly));
            peer.OnRtpPacketReceived += (_, media, packet) =>
            {
                if (media == SDPMediaTypesEnum.audio)
                {
                    received.Enqueue((packet.Header.SyncSource, packet.Header.SequenceNumber, packet.Header.Timestamp, packet.Payload, clock.Elapsed));
                }
            };
            Assert.Equal(SetDescriptionResultEnum.OK, peer.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer
            }));
            peer.IceRole = IceRolesEnum.passive;
            var answer = peer.createAnswer();
            await peer.setLocalDescription(answer);
            await peer.Start();
            nodePeer = peer;
            return new WhipReply(201, answer.sdp, "/resource/7.1.feed");
        };

        using var http = new HttpClient();
        var session = new StreamPublisherSession(
            new BotOptions { StreamConnectTimeoutMs = 10000, StreamReconnectMaxAttempts = 0 },
            http,
            NullLogger.Instance,
            42);
        try
        {
            await session.StartAsync(
                _ => Task.FromResult(new StreamPublishGrant(node.Url("whip/42"), "t1")),
                CancellationToken.None);

            session.BeginStream(WriteTrack("a", 20, firstByte: 0));
            await session.WaitUntilTrackEndedAsync(CancellationToken.None);
            await Task.Delay(300);

            session.BeginStream(WriteTrack("b", 30, firstByte: 100));
            await Task.Delay(200);
            session.SetPaused(true);
            await Task.Delay(300);
            session.SetPaused(false);
            await session.WaitUntilTrackEndedAsync(CancellationToken.None);
            await Task.Delay(100);
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
            nodePeer?.close();
            nodePeer?.Dispose();
        }

        var packets = received.ToArray();
        Assert.True(packets.Length > 50, $"received {packets.Length} packets");
        Assert.Single(packets.Select(packet => packet.Ssrc).Distinct());
        for (var i = 1; i < packets.Length; i++)
        {
            Assert.Equal((ushort)(packets[i - 1].Seq + 1), packets[i].Seq);
            var step = unchecked(packets[i].Timestamp - packets[i - 1].Timestamp);
            Assert.True(step >= 960 && step % 960 == 0, $"timestamp step {step} at {i}");
            Assert.True(packets[i].At - packets[i - 1].At < TimeSpan.FromMilliseconds(150), $"gap at {i}");
        }

        var music = packets.Where(packet => !packet.Payload.SequenceEqual(StreamPublisherSession.SilenceFrame)).Select(packet => packet.Payload[1]).ToArray();
        Assert.Equal(Enumerable.Range(0, 20).Concat(Enumerable.Range(100, 30)).Select(i => (byte)i), music);
        var firstB = Array.FindIndex(packets, packet => packet.Payload is [0x08, 100]);
        var lastA = Array.FindIndex(packets, packet => packet.Payload is [0x08, 19]);
        Assert.True(firstB - lastA > 10, "no silence between tracks");
        var trackB = packets.Select((packet, index) => (packet, index))
            .Where(entry => entry.packet.Payload is [0x08, >= 100])
            .Select(entry => entry.index)
            .ToArray();
        Assert.True(trackB.Zip(trackB.Skip(1), (previous, next) => next - previous).Max() > 10, "no silence while paused");
        var mediaSeconds = unchecked(packets[^1].Timestamp - packets[0].Timestamp) / 48000.0;
        var wallSeconds = (packets[^1].At - packets[0].At).TotalSeconds;
        Assert.InRange(mediaSeconds - wallSeconds, -0.1, 0.1);
        Assert.Contains(node.Requests, request => request is { Method: "DELETE", Path: "/resource/7.1.feed" });
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private StreamMediaSource WriteTrack(string name, int packets, int firstByte)
    {
        var path = Path.Combine(_dir, name + ".ogg");
        File.WriteAllBytes(path, StreamOggTestFile.Stereo48k(
            Enumerable.Range(firstByte, packets).Select(i => new byte[] { 0x08, (byte)i }).ToArray()));
        return new StreamMediaSource(null, path);
    }
}
