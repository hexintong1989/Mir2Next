using System.Reflection;
using Npgsql;

namespace Mirage.Persistence.Postgres;

public static class PostgresMigrationRunner
{
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        await using (var createHistory = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version varchar(128) PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """))
        {
            await createHistory.ExecuteNonQueryAsync(cancellationToken);
        }

        var assembly = typeof(PostgresMigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var resource in resources)
        {
            var version = resource[..^4].Split('.').Last();
            await using var exists = dataSource.CreateCommand(
                "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $1)");
            exists.Parameters.AddWithValue(version);
            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing migration resource: {resource}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            await using var migration = dataSource.CreateCommand(sql);
            await migration.ExecuteNonQueryAsync(cancellationToken);

            await using var record = dataSource.CreateCommand(
                "INSERT INTO schema_migrations (version) VALUES ($1)");
            record.Parameters.AddWithValue(version);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
