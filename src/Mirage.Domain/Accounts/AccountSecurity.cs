using System.Security.Cryptography;

namespace Mirage.Domain.Accounts;

public interface IPasswordHasher
{
    PasswordHash Hash(string password);
    bool Verify(string password, PasswordHash passwordHash);
}

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const string AlgorithmName = "PBKDF2-SHA256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = 210_000)
    {
        if (iterations is < 10_000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        _iterations = iterations;
    }

    public PasswordHash Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var value = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            _iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return new PasswordHash(AlgorithmName, _iterations, salt, value);
    }

    public bool Verify(string password, PasswordHash passwordHash)
    {
        if (!string.Equals(passwordHash.Algorithm, AlgorithmName, StringComparison.Ordinal)
            || passwordHash.Iterations is < 10_000 or > 1_000_000
            || passwordHash.Salt.Length != SaltSize
            || passwordHash.Value.Length != HashSize)
        {
            return false;
        }

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            password,
            passwordHash.Salt,
            passwordHash.Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return CryptographicOperations.FixedTimeEquals(candidate, passwordHash.Value);
    }
}
