using System.Net;
using System.Net.Sockets;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching;
using Mezon.Net.Sdk.Caching.Redis;
using Mezube.Bot;
using Mezube.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Mezube.Tests;

public sealed class MezonEntityCacheBridgeTests
{
    [Fact]
    public async Task AttachAsync_does_not_fail_when_redis_subscription_is_unavailable()
    {
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using var redis = await ConnectAsync(unavailableEndpoint);
        await using var client = new MezonClient(new MezonClientOptions());
        await using var bridge = CreateBridge(redis);

        Assert.False(redis.IsConnected);
        await bridge.AttachAsync(client);
        await bridge.AttachAsync(client);
        Assert.False(redis.IsConnected);
    }

    [Fact]
    public async Task AttachAsync_preserves_requested_cancellation()
    {
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using var redis = await ConnectAsync(unavailableEndpoint);
        await using var client = new MezonClient(new MezonClientOptions());
        await using var bridge = CreateBridge(redis);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => bridge.AttachAsync(client, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task AttachAsync_starts_successful_listener_once_and_disposes_it()
    {
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using var redis = await ConnectAsync(unavailableEndpoint);
        await using var client = new MezonClient(new MezonClientOptions());
        var listener = new SuccessfulListener();
        await using var bridge = CreateBridge(redis, listener);

        await bridge.AttachAsync(client);
        await bridge.AttachAsync(client);
        Assert.Equal(1, listener.StartCalls);

        await bridge.DisposeAsync();
        Assert.Equal(1, listener.DisposeCalls);
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(Socket unavailableEndpoint)
    {
        var configuration = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 100,
            ConnectRetry = 0,
            SyncTimeout = 100,
            AsyncTimeout = 100,
        };
        configuration.EndPoints.Add((IPEndPoint)unavailableEndpoint.LocalEndPoint!);
        return ConnectionMultiplexer.ConnectAsync(configuration);
    }

    private static MezonEntityCacheBridge CreateBridge(
        ConnectionMultiplexer redis,
        ICacheInvalidationListener? listener = null)
    {
        var options = new RedisEntitySnapshotStoreOptions
        {
            KeyPrefix = "mezon:test:snapshot",
            InvalidationChannel = "mezon:test:snapshot:invalidate",
        };
        listener ??= new RedisCacheInvalidationListener(
            redis, options, NullLogger<RedisCacheInvalidationListener>.Instance);
        var store = new RedisEntitySnapshotStore(
            redis, options, NullLogger<RedisEntitySnapshotStore>.Instance);

        return new MezonEntityCacheBridge(
            store,
            listener,
            new MezonSnapshotKeyFactory(new BotOptions { BotId = 42 }),
            NullLogger<MezonEntityCacheBridge>.Instance);
    }

    private sealed class SuccessfulListener : ICacheInvalidationListener
    {
        public int StartCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public event EventHandler<CacheKey>? Invalidated
        {
            add { }
            remove { }
        }

        public ValueTask StartListeningAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
