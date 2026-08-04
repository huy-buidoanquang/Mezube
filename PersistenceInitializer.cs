using Mezube.Infrastructure.Persistence.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mezube;

/// <summary>Applies Postgres migrations before the bot starts accepting events.</summary>
public sealed class PersistenceInitializer : IHostedService
{
    private readonly PostgresDbConnectionFactory _db;
    private readonly ILogger<PersistenceInitializer> _logger;

    public PersistenceInitializer(PostgresDbConnectionFactory db, ILogger<PersistenceInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize PostgreSQL");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
