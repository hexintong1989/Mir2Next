using System.Text.RegularExpressions;

namespace Mirage.Domain.Accounts;

public sealed record Account(
    Guid Id,
    string NormalizedUserName,
    PasswordHash Password,
    DateTimeOffset CreatedAt);

public sealed record PasswordHash(
    string Algorithm,
    int Iterations,
    byte[] Salt,
    byte[] Value);

public static partial class AccountPolicy
{
    public static string NormalizeUserName(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidUserName(string normalizedUserName) =>
        UserNamePattern().IsMatch(normalizedUserName);

    public static bool IsValidPassword(string? password) =>
        password is { Length: >= 10 and <= 128 }
        && password.Any(char.IsLetter)
        && password.Any(char.IsDigit);

    [GeneratedRegex("^[a-z0-9_]{4,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
