using Mezube.Bot;
using Mezube.Music;
using Mezube.Sfu;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Mezube.Tests;

public sealed class MediaFailureClassificationTests
{
    [Theory]
    [InlineData("bad-ogg", true)]
    [InlineData("cdn-404", true)]
    [InlineData("missing-file", true)]
    [InlineData("sfu-unreachable", false)]
    [InlineData("sfu-lost-during-track", false)]
    public async Task Playback_failure_is_classified_by_source(string source, bool mediaFailure)
    {
        var failure = await CaptureFailureAsync(source);

        Assert.Equal(mediaFailure, MusicPlayer.IsMediaPrepFailure(failure));
        Assert.Equal(!mediaFailure, MusicPlayer.IsSfuInfrastructureFailure(failure));
    }

    private static async Task<Exception> CaptureFailureAsync(string source) => source switch
    {
        "bad-ogg" => SfuPublisherSession.ToTrackFailure(await CaptureAsync(async () =>
        {
            var packets = Channel.CreateBounded<byte[]>(4);
            packets.Writer.TryComplete(await CaptureAsync(async () =>
                await new OggOpusReader(new MemoryStream(new byte[64])).ReadPacketAsync(CancellationToken.None)));
            await packets.Reader.WaitToReadAsync();
        })),
        "cdn-404" => SfuPublisherSession.ToTrackFailure(await CaptureAsync(() =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            response.EnsureSuccessStatusCode();
            return Task.CompletedTask;
        })),
        "missing-file" => SfuPublisherSession.ToTrackFailure(await CaptureAsync(() =>
        {
            using var file = new FileStream(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ogg"),
                FileMode.Open,
                FileAccess.Read);
            return Task.CompletedTask;
        })),
        "sfu-unreachable" => await StartAgainstClosedPortAsync(),
        "sfu-lost-during-track" => SfuPublisherSession.ToTrackFailure(new InvalidOperationException(
            "SFU publisher reconnect is no longer available.",
            await StartAgainstClosedPortAsync())),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static async Task<Exception> StartAgainstClosedPortAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var options = new BotOptions
        {
            SfuWebSocketUrl = $"ws://127.0.0.1:{port}/ws",
            SfuConnectTimeoutMs = 15000,
            SfuReconnectMaxAttempts = 0,
        };
        using var http = new HttpClient();
        var session = new SfuPublisherSession(options, http, NullLogger.Instance, 42);
        try
        {
            return await CaptureAsync(() => session.StartAsync("token", _ => Task.FromResult("token"), CancellationToken.None));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        return exception;
    }
}
