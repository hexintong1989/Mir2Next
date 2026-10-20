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
    private readonly ISessionStore _sessions;
    private readonly IClock _clock;
    private readonly PasswordHash _dummyPasswordHash;

    public AccountService(
        IAccountRepository accounts,
        IPasswordHasher passwordHasher,
        ISessionStore sessions,
        IClock clock)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _sessions = sessions;
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

    public async ValueTask<Result<IssuedSession>> LoginAsync(
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
            return Result<IssuedSession>.Failure(ErrorCode.InvalidCredentials, InvalidCredentialsMessage);
        }

        var session = await _sessions.IssueAsync(account, _clock.UtcNow, cancellationToken);
        return Result<IssuedSession>.Success(session);
    }

    public async ValueTask<Result<SessionPrincipal>> ValidateSessionAsync(
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindActiveAsync(
            accessToken ?? string.Empty,
            _clock.UtcNow,
            cancellationToken);
        return session is null
            ? Result<SessionPrincipal>.Failure(ErrorCode.InvalidSession, "会话无效或已过期。")
            : Result<SessionPrincipal>.Success(session);
    }

    public async ValueTask<Result<bool>> RevokeSessionAsync(
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        return await _sessions.RevokeAsync(accessToken ?? string.Empty, cancellationToken)
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(ErrorCode.InvalidSession, "会话无效或已过期。");
    }
}
