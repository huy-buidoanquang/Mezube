using Microsoft.Extensions.Logging;
using Npgsql;

namespace Mezube.Infrastructure.Persistence.Postgres;

public static class PostgresMigrator
{
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var ensure = connection.CreateCommand())
        {
            ensure.CommandText =
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                  version TEXT PRIMARY KEY,
                  applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                """;
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var assembly = typeof(PostgresMigrator).Assembly;
        const string marker = ".Migrations.";
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                var start = n.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var fileName = n[start..];
                var version = Path.GetFileNameWithoutExtension(fileName);
                return (Resource: n, Version: version);
            })
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .ToList();

        foreach (var (resource, version) in resources)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE version = @v LIMIT 1;";
            check.Parameters.AddWithValue("v", version);
            var exists = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not null)
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing migration resource {resource}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = tx;
                    apply.CommandText = sql;
                    await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = tx;
                    mark.CommandText =
                        """
                        INSERT INTO schema_migrations (version) VALUES (@v)
                        ON CONFLICT (version) DO NOTHING;
                        """;
                    mark.Parameters.AddWithValue("v", version);
                    await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Applied Postgres migration {Version}", version);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }
}
