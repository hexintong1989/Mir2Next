using Npgsql;

namespace Mirage.Persistence.Postgres;

public static class PostgresStorage
{
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new NpgsqlDataSourceBuilder(connectionString).Build();
    }
}
