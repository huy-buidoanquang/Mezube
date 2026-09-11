using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Mezube.Bot;
using Mezube.Stn;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class StnSocketClientIntegrationTests
{
    [Fact]
    public async Task PublisherEnded_InvalidatesOpenSocket_AndNextConnectUsesOneNewGeneration()
    {
        await using var server = await LoopbackStnServer.StartAsync();
        await using var client = CreateClient(server.Origin);

        Assert.True(await client.EnsureConnectedAsync("jwt-one", 42, "bot"));
        var firstSocket = await server.WaitForConnectionAsync();
        await PublishAndAckAsync(client, firstSocket);

        var ended = client.WaitUntilTrackEndedAsync();
        await SendJsonAsync(firstSocket, """
            {"Key":"info","Value":"stream_publisher_ended"}
            """);

        var failure = await Assert.ThrowsAsync<StnPublisherException>(() => ended);
        Assert.Equal("publisher_ended", failure.Code);
        Assert.Equal(StnSessionState.Invalidated, client.State);

        Assert.True(await client.EnsureConnectedAsync("jwt-two", 42, "bot"));
        _ = await server.WaitForConnectionAsync();
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(StnSessionState.Ready, client.State);
    }

    [Fact]
    public async Task ConcurrentEnsureConnected_OpensOnlyOneSocket_AndCredentialChangeKeepsIt()
    {
        await using var server = await LoopbackStnServer.StartAsync();
        await using var client = CreateClient(server.Origin);

        var first = client.EnsureConnectedAsync("credential-one", 42, "bot");
        var second = client.EnsureConnectedAsync("credential-one", 42, "bot");
        var results = await Task.WhenAll(first, second);
        _ = await server.WaitForConnectionAsync();

        Assert.Single(results, value => value);
        Assert.Single(results, value => !value);
        Assert.False(await client.EnsureConnectedAsync("credential-two", 42, "bot"));
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task StopOnInvalidatedSession_DoesNotReconnect()
    {
        await using var server = await LoopbackStnServer.StartAsync();
        await using var client = CreateClient(server.Origin);

        Assert.True(await client.EnsureConnectedAsync("jwt", 42, "bot"));
        var socket = await server.WaitForConnectionAsync();
        await PublishAndAckAsync(client, socket);

        var ended = client.WaitUntilTrackEndedAsync();
        await SendJsonAsync(socket, """
            {"Key":"info","Value":"stream publish failed"}
            """);
        await Assert.ThrowsAsync<StnPublisherException>(() => ended);

        await client.StopPublisherAsync(1, 2);
        Assert.Equal(StnSessionState.Disconnected, client.State);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task StructuredPublishFailure_PreservesCorrelationAndRetryMetadata()
    {
        await using var server = await LoopbackStnServer.StartAsync();
        await using var client = CreateClient(server.Origin);

        Assert.True(await client.EnsureConnectedAsync("jwt", 42, "bot"));
        var socket = await server.WaitForConnectionAsync();
        await PublishAndAckAsync(client, socket);

        var ended = client.WaitUntilTrackEndedAsync();
        await SendJsonAsync(socket, """
            {"Key":"stream_publish_failed","Value":{"connection_id":"conn-42","phase":"source_open","code":"http_503","retryable":true,"message":"upstream unavailable"}}
            """);

        var failure = await Assert.ThrowsAsync<StnPublisherException>(() => ended);
        Assert.Equal("conn-42", failure.ConnectionId);
        Assert.Equal("source_open", failure.Phase);
        Assert.Equal("http_503", failure.Code);
        Assert.True(failure.Retryable);
        Assert.Equal(StnSessionState.Invalidated, client.State);
    }

    [Fact]
    public async Task StructuredPublishFailureBeforeAck_IsReturnedAsTypedRetryableFailure()
    {
        await using var server = await LoopbackStnServer.StartAsync();
        await using var client = CreateClient(server.Origin);

        Assert.True(await client.EnsureConnectedAsync("jwt", 42, "bot"));
        var socket = await server.WaitForConnectionAsync();
        var play = client.PlayAsync(1, 2, "https://cdn.example/track.ogg");
        _ = await ReceiveTextAsync(socket);

        await SendJsonAsync(socket, """
            {"Key":"stream_publish_failed","Value":{"connection_id":"conn-pre-ack","phase":"source_open","code":"source_http_503","retryable":true,"message":"source returned 503"}}
            """);

        var failure = await Assert.ThrowsAsync<StnPublisherException>(() => play);
        Assert.Equal("conn-pre-ack", failure.ConnectionId);
        Assert.Equal("source_http_503", failure.Code);
        Assert.True(failure.Retryable);
    }

    private static StnSocketClient CreateClient(string origin)
        => new(
            new BotOptions { StnBaseUrl = origin, BotDisplayName = "Mezube test" },
            NullLogger<StnSocketClient>.Instance);

    private static async Task PublishAndAckAsync(StnSocketClient client, WebSocket serverSocket)
    {
        var play = client.PlayAsync(1, 2, "https://cdn.example/track.ogg");
        var request = await ReceiveTextAsync(serverSocket);
        Assert.Contains("\"Key\":\"connect_publisher\"", request, StringComparison.Ordinal);
        await SendJsonAsync(serverSocket, """
            {"Key":"connect_publisher","Value":null}
            """);
        await play;
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static Task SendJsonAsync(WebSocket socket, string json)
        => socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    private sealed class LoopbackStnServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly Channel<WebSocket> _connections;
        private int _connectionCount;

        private LoopbackStnServer(WebApplication application, Channel<WebSocket> connections, string origin)
        {
            _application = application;
            _connections = connections;
            Origin = origin;
        }

        public string Origin { get; private set; }
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public static async Task<LoopbackStnServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            var connections = Channel.CreateUnbounded<WebSocket>();
            var server = new LoopbackStnServer(application, connections, string.Empty);

            application.UseWebSockets();
            application.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                Interlocked.Increment(ref server._connectionCount);
                await connections.Writer.WriteAsync(socket);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    socket.Dispose();
                }
            });

            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            server.Origin = addresses.Addresses.Single();
            return server;
        }

        public async Task<WebSocket> WaitForConnectionAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _connections.Reader.ReadAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
