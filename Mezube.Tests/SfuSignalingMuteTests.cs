using Mezube.Bot;
using Mezube.Sfu;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mezube.Tests;

public sealed class SfuSignalingMuteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_declares_mute_state_right_after_joined(bool paused)
    {
        // mezon-sfu creates sessions muted, so the publisher must declare its
        // state before answering the first offer.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = new BotOptions
            {
                SfuWebSocketUrl = $"ws://127.0.0.1:{port}/ws",
                SfuConnectTimeoutMs = 1000,
                SfuReconnectMaxAttempts = 0,
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient();
            var session = new SfuPublisherSession(options, http, NullLogger.Instance, 42);
            await session.PauseAsync(paused, timeout.Token);

            var start = session.StartAsync("token", _ => Task.FromResult("token"), timeout.Token);
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            using var server = await AcceptWebSocketAsync(tcp.GetStream(), timeout.Token);

            var join = await ReceiveJsonAsync(server, timeout.Token);
            Assert.Equal("join", join.GetProperty("type").GetString());
            Assert.Equal("speaker", join.GetProperty("role").GetString());

            await SendTextAsync(server, """{"type":"joined","room":"42"}""", timeout.Token);
            var mute = await ReceiveJsonAsync(server, timeout.Token);

            Assert.Equal("mute", mute.GetProperty("type").GetString());
            Assert.Equal(paused, mute.GetProperty("is_mute").GetBoolean());

            // No offer follows, so WebRTC never connects and StartAsync times out.
            await Assert.ThrowsAsync<TimeoutException>(() => start);
            await session.DisposeAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<WebSocket> AcceptWebSocketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var request = new List<byte>();
        var buffer = new byte[1];
        while (request.Count < 4 || !request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
            {
                throw new IOException("Client closed before the WebSocket handshake completed.");
            }

            request.Add(buffer[0]);
        }

        const string keyHeader = "Sec-WebSocket-Key:";
        var key = Encoding.ASCII.GetString(request.ToArray())
            .Split("\r\n")
            .First(line => line.StartsWith(keyHeader, StringComparison.OrdinalIgnoreCase))[keyHeader.Length..]
            .Trim();
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}
