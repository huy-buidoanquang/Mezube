using Mezube.Bot;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mezube.Infrastructure.Persistence.Redis;

public sealed class RedisConnection : IDisposable
{
    private readonly IConnectionMultiplexer _mux;
    private readonly ILogger<RedisConnection> _logger;
    private readonly bool _ownsMultiplexer;

    public static Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
        => ConnectionMultiplexer.ConnectAsync(CreateConfiguration(connectionString));

    public RedisConnection(BotOptions options, ILogger<RedisConnection> logger)
        : this(ConnectionMultiplexer.Connect(CreateConfiguration(options.RedisConnectionString)), logger, ownsMultiplexer: true)
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
        _logger.Log(
            _mux.IsConnected ? LogLevel.Information : LogLevel.Warning,
            "Redis initialized (connected={Connected}, endpoints={Endpoints})",
            _mux.IsConnected,
            string.Join(",", _mux.GetEndPoints().Select(e => e.ToString())));
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

    private static ConfigurationOptions CreateConfiguration(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Mezube:RedisConnectionString is required.");
        }

        var configuration = ConfigurationOptions.Parse(connectionString);
        // Keep the multiplexer alive so it can reconnect when Redis becomes available.
        configuration.AbortOnConnectFail = false;
        return configuration;
    }
}
