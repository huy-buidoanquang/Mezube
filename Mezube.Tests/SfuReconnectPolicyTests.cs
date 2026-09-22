using Mezube.Bot;
using Mezube.Sfu;

namespace Mezube.Tests;

public sealed class SfuReconnectPolicyTests
{
    [Fact]
    public void Delay_grows_exponentially_until_the_configured_cap()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SfuReconnectPolicy.GetDelay(1, 1000, 30000, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), SfuReconnectPolicy.GetDelay(2, 1000, 30000, 0));
        Assert.Equal(TimeSpan.FromSeconds(4), SfuReconnectPolicy.GetDelay(3, 1000, 30000, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), SfuReconnectPolicy.GetDelay(6, 1000, 30000, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), SfuReconnectPolicy.GetDelay(20, 1000, 30000, 0));
    }

    [Fact]
    public void Jitter_never_exceeds_the_configured_backoff_cap()
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var delay = SfuReconnectPolicy.GetDelay(attempt, 1000, 30000, 5000);
            Assert.InRange(delay, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void Options_reject_unbounded_or_zero_delay_configuration()
    {
        var options = ValidOptions();
        options.SfuReconnectBackoffMs = 0;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_reject_a_maximum_backoff_below_the_base_delay()
    {
        var options = ValidOptions();
        options.SfuReconnectMaxBackoffMs = options.SfuReconnectBackoffMs - 1;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static BotOptions ValidOptions()
        => new()
        {
            BotId = 1,
            Token = "test-token",
            SfuWebSocketUrl = "wss://sfu.example.test/ws",
            CdnBaseUrl = "https://cdn.example.test",
            PostgresConnectionString = "Host=localhost;Database=test",
            RedisConnectionString = "localhost:6379"
        };
}
