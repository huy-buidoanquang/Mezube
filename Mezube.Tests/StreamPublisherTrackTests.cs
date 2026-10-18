using Mezube.Bot;
using Mezube.Media;
using Mezube.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Mezube.Tests;

public sealed class StreamPublisherTrackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mezube-track-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();

    public StreamPublisherTrackTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Silence_is_sent_without_a_track_and_while_paused()
    {
        await using var session = NewSession();
        Assert.Same(StreamPublisherSession.SilenceFrame, session.NextPacket().Packet);

        session.BeginStream(WriteTrack("a", 3));
        session.SetPaused(true);
        await Task.Delay(50);

        Assert.Same(StreamPublisherSession.SilenceFrame, session.NextPacket().Packet);
    }

    [Fact]
    public async Task Track_packets_play_in_order_and_the_track_ends_after_the_last_one()
    {
        await using var session = NewSession();
        session.BeginStream(WriteTrack("a", 6));

        var played = await DrainAsync(session);

        Assert.Equal(Enumerable.Range(0, 6).Select(i => (byte)i), played.Select(packet => packet[1]));
        await session.WaitUntilTrackEndedAsync(CancellationToken.None);
        Assert.Same(StreamPublisherSession.SilenceFrame, session.NextPacket().Packet);
    }

    [Fact]
    public async Task Unreadable_track_ends_with_a_media_failure()
    {
        await using var session = NewSession();
        session.BeginStream(new StreamMediaSource(null, Path.Combine(_dir, "missing.ogg")));

        await DrainAsync(session);

        await Assert.ThrowsAsync<MediaPrepException>(() => session.WaitUntilTrackEndedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Replaced_track_is_resolved_and_only_the_new_track_plays()
    {
        await using var session = NewSession();
        session.BeginStream(WriteTrack("a", 3, firstByte: 100));
        var first = session.WaitUntilTrackEndedAsync(CancellationToken.None);

        session.BeginStream(WriteTrack("b", 3));
        await first;
        var played = await DrainAsync(session);

        Assert.All(played, packet => Assert.True(packet[1] < 100));
        Assert.Equal(3, played.Count);
    }

    [Fact]
    public async Task Same_file_played_again_after_a_skip_ends_on_its_own()
    {
        await using var session = NewSession();
        var media = WriteTrack("loop", 4);
        session.BeginStream(media);
        session.EndTrack();
        await session.WaitUntilTrackEndedAsync(CancellationToken.None);

        session.BeginStream(media);
        var played = await DrainAsync(session);

        Assert.Equal(4, played.Count);
        await session.WaitUntilTrackEndedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EndTrack_resolves_the_wait_and_switches_to_silence()
    {
        await using var session = NewSession();
        session.BeginStream(WriteTrack("a", 50));
        var ended = session.WaitUntilTrackEndedAsync(CancellationToken.None);

        session.EndTrack();

        await ended;
        Assert.Same(StreamPublisherSession.SilenceFrame, session.NextPacket().Packet);
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private StreamPublisherSession NewSession() => new(new BotOptions(), _http, NullLogger.Instance, 42);

    private StreamMediaSource WriteTrack(string name, int packets, int firstByte = 0)
    {
        var path = Path.Combine(_dir, name + ".ogg");
        File.WriteAllBytes(path, StreamOggTestFile.Stereo48k(
            Enumerable.Range(firstByte, packets).Select(i => new byte[] { 0x08, (byte)i }).ToArray()));
        return new StreamMediaSource(null, path);
    }

    private static async Task<List<byte[]>> DrainAsync(StreamPublisherSession session)
    {
        var played = new List<byte[]>();
        var ended = session.WaitUntilTrackEndedAsync(CancellationToken.None);
        var clock = Stopwatch.StartNew();
        while (!ended.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            var (packet, _) = session.NextPacket();
            if (ReferenceEquals(packet, StreamPublisherSession.SilenceFrame))
            {
                await Task.Delay(1);
            }
            else
            {
                played.Add(packet);
            }
        }

        Assert.True(ended.IsCompleted, "track did not end");
        return played;
    }
}
