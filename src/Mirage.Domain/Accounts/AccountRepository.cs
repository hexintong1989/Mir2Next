using System.Collections.Concurrent;

namespace Mirage.Domain.Accounts;

public interface IAccountRepository
{
    ValueTask<Account?> FindByUserNameAsync(string normalizedUserName, CancellationToken cancellationToken);
    ValueTask<bool> TryAddAsync(Account account, CancellationToken cancellationToken);
}

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly ConcurrentDictionary<string, Account> _accounts =
        new(StringComparer.Ordinal);

    public ValueTask<Account?> FindByUserNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _accounts.TryGetValue(normalizedUserName, out var account);
        return ValueTask.FromResult(account);
    }

    public ValueTask<bool> TryAddAsync(Account account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_accounts.TryAdd(account.NormalizedUserName, account));
    }
}
