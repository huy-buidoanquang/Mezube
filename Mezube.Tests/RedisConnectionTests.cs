using System.Net;
using System.Net.Sockets;
using Mezube.Bot;
using Mezube.Infrastructure.Persistence.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class RedisConnectionTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",abortConnect=true")]
    public async Task ConnectAsync_returns_disconnected_multiplexer_when_redis_is_unavailable(string abortConnect)
    {
        using var endpoint = ReserveUnavailableEndpoint();
        var connectionString = GetConnectionString(endpoint) + abortConnect;

        using var multiplexer = await RedisConnection.ConnectAsync(connectionString);

        Assert.False(multiplexer.IsConnected);
        Assert.Equal(3, multiplexer.GetDatabase().Database);
        Assert.Equal(endpoint.LocalEndPoint, Assert.Single(multiplexer.GetEndPoints()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",abortConnect=true")]
    public void Constructor_returns_disconnected_multiplexer_when_redis_is_unavailable(string abortConnect)
    {
        using var endpoint = ReserveUnavailableEndpoint();
        var options = new BotOptions { RedisConnectionString = GetConnectionString(endpoint) + abortConnect };

        using var connection = new RedisConnection(options, NullLogger<RedisConnection>.Instance);

        Assert.False(connection.Multiplexer.IsConnected);
        Assert.Equal(3, connection.Db.Database);
        Assert.Equal(endpoint.LocalEndPoint, Assert.Single(connection.Multiplexer.GetEndPoints()));
    }

    [Fact]
    public async Task ConnectAsync_rejects_missing_connection_string()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedisConnection.ConnectAsync(" "));

        Assert.Equal("Mezube:RedisConnectionString is required.", error.Message);
    }

    [Fact]
    public void Constructor_rejects_missing_connection_string()
    {
        var options = new BotOptions { RedisConnectionString = " " };

        var error = Assert.Throws<InvalidOperationException>(
            () => new RedisConnection(options, NullLogger<RedisConnection>.Instance));

        Assert.Equal("Mezube:RedisConnectionString is required.", error.Message);
    }

    private static Socket ReserveUnavailableEndpoint()
    {
        var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return endpoint;
    }

    private static string GetConnectionString(Socket endpoint)
        => $"{endpoint.LocalEndPoint},connectTimeout=100,connectRetry=0,syncTimeout=100,asyncTimeout=100,defaultDatabase=3";
}
