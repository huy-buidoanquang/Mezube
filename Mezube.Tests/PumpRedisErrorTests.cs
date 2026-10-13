using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using System.Reflection;

namespace Mezube.Tests;

public sealed class PumpRedisErrorTests
{
    [Fact]
    public async Task GetLoopModeOrOffAsync_returns_off_when_redis_throws()
    {
        var store = DispatchProxy.Create<IClanPlayerStore, ThrowingStore>();

        var loop = await MusicPlayer.GetLoopModeOrOffAsync(store, NullLogger.Instance, 1, 2);

        Assert.Equal(LoopMode.Off, loop);
    }

    public class ThrowingStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Redis blip");
    }
}
