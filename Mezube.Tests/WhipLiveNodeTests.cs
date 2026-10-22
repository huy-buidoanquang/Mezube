using Mezube.Bot;
using Mezube.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mezube.Tests;

public sealed class LiveNodeFactAttribute : FactAttribute
{
    public LiveNodeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEZUBE_WHIP_URL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEZUBE_WHIP_TOKEN")))
        {
            Skip = "Set MEZUBE_WHIP_URL and MEZUBE_WHIP_TOKEN to publish to a streaming node.";
        }
    }
}

public sealed class WhipLiveNodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mezube-live-" + Guid.NewGuid().ToString("N"));

    public WhipLiveNodeTests() => Directory.CreateDirectory(_dir);

    [LiveNodeFact]
    public async Task Node_ingests_continuous_audio_across_skip_and_pause()
    {
        var whipUrl = new Uri(Environment.GetEnvironmentVariable("MEZUBE_WHIP_URL")!);
        var token = Environment.GetEnvironmentVariable("MEZUBE_WHIP_TOKEN")!;
        var media = Environment.GetEnvironmentVariable("MEZUBE_WHIP_OGG") is { Length: > 0 } ogg
            ? new StreamMediaSource(null, ogg)
            : WriteTrack(3000);
        var statsUrl = new Uri(whipUrl, "/stats");
        using var http = new HttpClient();
        var before = await StatsAsync(http, statsUrl);
        var grants = 0;
        var samples = new List<NodeStats>();
        var session = new StreamPublisherSession(new BotOptions(), http, NullLogger.Instance, 42);
        NodeStats connected;
        NodeStats live;
        TimeSpan elapsed;
        try
        {
            await session.StartAsync(
                _ =>
                {
                    Interlocked.Increment(ref grants);
                    return Task.FromResult(new StreamPublishGrant(whipUrl, token));
                },
                CancellationToken.None);
            connected = await StatsAsync(http, statsUrl);
            var clock = Stopwatch.StartNew();

            session.BeginStream(media);
            await SampleAsync(http, statsUrl, samples, TimeSpan.FromSeconds(20));
            session.EndTrack();
            await SampleAsync(http, statsUrl, samples, TimeSpan.FromSeconds(5));
            session.BeginStream(media);
            await SampleAsync(http, statsUrl, samples, TimeSpan.FromSeconds(15));
            session.SetPaused(true);
            await SampleAsync(http, statsUrl, samples, TimeSpan.FromSeconds(20));
            session.SetPaused(false);
            await SampleAsync(http, statsUrl, samples, TimeSpan.FromSeconds(15));

            live = await StatsAsync(http, statsUrl);
            elapsed = clock.Elapsed;
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        var after = await StatsAsync(http, statsUrl);

        Assert.Equal(1, grants);
        Assert.Equal(before.SrtpFailed, after.SrtpFailed);
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].IngestedAudio > samples[i - 1].IngestedAudio, $"ingest stalled at second {i}");
        }

        var rate = (live.IngestedAudio - connected.IngestedAudio) / elapsed.TotalSeconds;
        Assert.InRange(rate, 45, 55);
        Assert.True(after.Sessions < live.Sessions, $"sessions {live.Sessions} -> {after.Sessions}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private StreamMediaSource WriteTrack(int packets)
    {
        var path = Path.Combine(_dir, "track.ogg");
        File.WriteAllBytes(path, StreamOggTestFile.Stereo48k(
            Enumerable.Range(0, packets).Select(i => new byte[] { 0x08, (byte)i }).ToArray()));
        return new StreamMediaSource(null, path);
    }

    private static async Task SampleAsync(HttpClient http, Uri statsUrl, List<NodeStats> samples, TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            samples.Add(await StatsAsync(http, statsUrl));
        }
    }

    private static async Task<NodeStats> StatsAsync(HttpClient http, Uri statsUrl)
        => (await http.GetFromJsonAsync<NodeStats>(statsUrl))!;

    private sealed record NodeStats(
        [property: JsonPropertyName("sessions")] int Sessions,
        [property: JsonPropertyName("srtp_failed")] long SrtpFailed,
        [property: JsonPropertyName("ingested_audio")] long IngestedAudio);
}
