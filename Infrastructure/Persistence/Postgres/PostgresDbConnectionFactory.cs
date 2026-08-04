using Mezube.Bot;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Mezube.Infrastructure.Persistence.Postgres;

public sealed class PostgresDbConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDbConnectionFactory> _logger;

    public PostgresDbConnectionFactory(BotOptions options, ILogger<PostgresDbConnectionFactory> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(options.PostgresConnectionString))
        {
            throw new InvalidOperationException("Mezube:PostgresConnectionString is required.");
        }

        var builder = new NpgsqlDataSourceBuilder(options.PostgresConnectionString);
        _dataSource = builder.Build();
    }

    public NpgsqlDataSource DataSource => _dataSource;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await PostgresMigrator.ApplyAsync(_dataSource, _logger, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("PostgreSQL persistence ready");
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
