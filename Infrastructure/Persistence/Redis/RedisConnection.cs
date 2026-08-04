using Mezube.Bot;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mezube.Infrastructure.Persistence.Redis;

public sealed class RedisConnection : IDisposable
{
    private readonly IConnectionMultiplexer _mux;
    private readonly ILogger<RedisConnection> _logger;
    private readonly bool _ownsMultiplexer;

    public RedisConnection(BotOptions options, ILogger<RedisConnection> logger)
        : this(Connect(options, logger), logger, ownsMultiplexer: true)
    {
    }

    public RedisConnection(
        IConnectionMultiplexer multiplexer,
        ILogger<RedisConnection> logger,
        bool ownsMultiplexer = false)
    {
        _mux = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _logger = logger;
        _ownsMultiplexer = ownsMultiplexer;
        _logger.LogInformation("Redis ready ({Endpoints})", string.Join(",", _mux.GetEndPoints().Select(e => e.ToString())));
    }

    public IDatabase Db => _mux.GetDatabase();

    public IConnectionMultiplexer Multiplexer => _mux;

    public void Dispose()
    {
        if (_ownsMultiplexer)
        {
            _mux.Dispose();
        }
    }

    private static IConnectionMultiplexer Connect(BotOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            throw new InvalidOperationException("Mezube:RedisConnectionString is required.");
        }

        var mux = ConnectionMultiplexer.Connect(options.RedisConnectionString);
        logger.LogInformation("Redis connected ({Endpoints})", options.RedisConnectionString);
        return mux;
    }
}
