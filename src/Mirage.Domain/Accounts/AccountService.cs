using Mirage.Domain.Common;

namespace Mirage.Domain.Accounts;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class AccountService
{
    private const string InvalidCredentialsMessage = "用户名或密码错误。";
    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISessionIssuer _sessionIssuer;
    private readonly IClock _clock;
    private readonly PasswordHash _dummyPasswordHash;

    public AccountService(
        IAccountRepository accounts,
        IPasswordHasher passwordHasher,
        ISessionIssuer sessionIssuer,
        IClock clock)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _sessionIssuer = sessionIssuer;
        _clock = clock;
        _dummyPasswordHash = passwordHasher.Hash("timing-only-password-000");
    }

    public async ValueTask<Result<Account>> RegisterAsync(
        string? userName,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserName = AccountPolicy.NormalizeUserName(userName);
        if (!AccountPolicy.IsValidUserName(normalizedUserName))
        {
            return Result<Account>.Failure(
                ErrorCode.InvalidUserName,
                "用户名须为 4 至 24 位小写字母、数字或下划线。");
        }

        if (!AccountPolicy.IsValidPassword(password))
        {
            return Result<Account>.Failure(
                ErrorCode.WeakPassword,
                "密码须为 10 至 128 位，并同时包含字母和数字。");
        }

        var account = new Account(
            Guid.NewGuid(),
            normalizedUserName,
            _passwordHasher.Hash(password!),
            _clock.UtcNow);

        if (!await _accounts.TryAddAsync(account, cancellationToken))
        {
            return Result<Account>.Failure(ErrorCode.AccountAlreadyExists, "用户名已存在。");
        }

        return Result<Account>.Success(account);
    }

    public async ValueTask<Result<GameSession>> LoginAsync(
        string? userName,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserName = AccountPolicy.NormalizeUserName(userName);
        var account = await _accounts.FindByUserNameAsync(normalizedUserName, cancellationToken);
        var storedHash = account?.Password ?? _dummyPasswordHash;
        var passwordMatches = _passwordHasher.Verify(password ?? string.Empty, storedHash);

        if (account is null || !passwordMatches)
        {
            return Result<GameSession>.Failure(ErrorCode.InvalidCredentials, InvalidCredentialsMessage);
        }

        return Result<GameSession>.Success(_sessionIssuer.Issue(account, _clock.UtcNow));
    }
}
