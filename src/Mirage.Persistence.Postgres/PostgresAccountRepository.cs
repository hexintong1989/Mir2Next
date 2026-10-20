using Mirage.Domain.Accounts;
using Npgsql;

namespace Mirage.Persistence.Postgres;

public sealed class PostgresAccountRepository(NpgsqlDataSource dataSource) : IAccountRepository
{
    public async ValueTask<Account?> FindByUserNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT account_id, normalized_user_name, password_algorithm,
                   password_iterations, password_salt, password_value, created_at
            FROM accounts
            WHERE normalized_user_name = $1
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(normalizedUserName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Account(
            reader.GetGuid(0),
            reader.GetString(1),
            new PasswordHash(
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetFieldValue<byte[]>(5)),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
    }

    public async ValueTask<bool> TryAddAsync(Account account, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO accounts (
                account_id, normalized_user_name, password_algorithm,
                password_iterations, password_salt, password_value, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(account.Id);
        command.Parameters.AddWithValue(account.NormalizedUserName);
        command.Parameters.AddWithValue(account.Password.Algorithm);
        command.Parameters.AddWithValue(account.Password.Iterations);
        command.Parameters.AddWithValue(account.Password.Salt);
        command.Parameters.AddWithValue(account.Password.Value);
        command.Parameters.AddWithValue(account.CreatedAt.UtcDateTime);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }
}
