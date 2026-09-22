using Mezube.Infrastructure.Persistence.Redis;

namespace Mezube.Tests;

public sealed class PlayerSessionKeyTests
{
    [Fact]
    public void Redis_player_and_queue_keys_are_isolated_per_channel()
    {
        Assert.Equal("mezon:music:player:10:21", RedisKeyNames.Player(10, 21));
        Assert.Equal("mezon:music:queue:10:21", RedisKeyNames.Queue(10, 21));
        Assert.NotEqual(RedisKeyNames.Player(10, 21), RedisKeyNames.Player(10, 22));
        Assert.NotEqual(RedisKeyNames.Queue(10, 21), RedisKeyNames.Queue(10, 22));
        Assert.NotEqual(RedisKeyNames.Player(10, 21), RedisKeyNames.LegacyPlayer(10));
        Assert.NotEqual(RedisKeyNames.Queue(10, 21), RedisKeyNames.LegacyQueue(10));
    }

    [Fact]
    public void TryParse_roundtrips_clan_and_channel()
    {
        var key = new PlayerSessionKey(10, 21);
        Assert.True(key.IsValid);
        Assert.Equal("10:21", key.IndexValue);
        Assert.True(PlayerSessionKey.TryParse(key.IndexValue, out var parsed));
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("10:")]
    [InlineData(":21")]
    [InlineData("0:21")]
    [InlineData("10:0")]
    [InlineData("10:21:99")]
    [InlineData("abc:21")]
    public void TryParse_rejects_legacy_and_malformed(string? value)
        => Assert.False(PlayerSessionKey.TryParse(value, out _));
}
