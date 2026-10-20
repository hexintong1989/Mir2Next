namespace Mirage.Domain.Common;

public enum ErrorCode
{
    InvalidUserName,
    WeakPassword,
    AccountAlreadyExists,
    InvalidCredentials,
    InvalidSession
}

public sealed record DomainError(ErrorCode Code, string Message);

public readonly record struct Result<T>
{
    private Result(T? value, DomainError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public T? Value { get; }
    public DomainError? Error { get; }
    public bool IsSuccess { get; }

    public static Result<T> Success(T value) => new(value, null, true);
    public static Result<T> Failure(ErrorCode code, string message) =>
        new(default, new DomainError(code, message), false);
}
