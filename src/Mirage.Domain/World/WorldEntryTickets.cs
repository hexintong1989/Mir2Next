using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mirage.Domain.World;

public sealed record WorldEntryTicketClaims(
    Guid AccountId,
    Guid CharacterId,
    string RealmId,
    string MapId,
    int PositionX,
    int PositionY,
    string Nonce,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record IssuedWorldEntryTicket(string Token, WorldEntryTicketClaims Claims);

public enum TicketValidationFailure
{
    None,
    Malformed,
    InvalidSignature,
    Expired,
    NotYetValid,
    WrongRealm,
    Replayed
}

public readonly record struct TicketValidationResult(
    TicketValidationFailure Failure,
    WorldEntryTicketClaims? Claims)
{
    public bool IsSuccess => Failure == TicketValidationFailure.None;
}

public sealed class HmacWorldEntryTicketProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;
    private readonly TimeSpan _lifetime;

    public HmacWorldEntryTicketProtector(byte[] signingKey, TimeSpan? lifetime = null)
    {
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("World entry signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
    }

    public IssuedWorldEntryTicket Issue(
        Guid accountId,
        Guid characterId,
        string realmId,
        DateTimeOffset now,
        string mapId = "local-field",
        int positionX = 64,
        int positionY = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var claims = new WorldEntryTicketClaims(
            accountId,
            characterId,
            realmId,
            mapId,
            positionX,
            positionY,
            ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            now,
            now.Add(_lifetime));
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions);
        var signature = HMACSHA256.HashData(_signingKey, payload);
        return new IssuedWorldEntryTicket(
            $"{ToBase64Url(payload)}.{ToBase64Url(signature)}",
            claims);
    }

    public TicketValidationResult Validate(string? token, string expectedRealmId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Failure(TicketValidationFailure.Malformed);
        }

        var parts = token.Split('.');
        if (parts.Length != 2 || !TryFromBase64Url(parts[0], out var payload)
            || !TryFromBase64Url(parts[1], out var signature))
        {
            return Failure(TicketValidationFailure.Malformed);
        }

        var expectedSignature = HMACSHA256.HashData(_signingKey, payload);
        if (signature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
        {
            return Failure(TicketValidationFailure.InvalidSignature);
        }

        WorldEntryTicketClaims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<WorldEntryTicketClaims>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure(TicketValidationFailure.Malformed);
        }

        if (claims is null
            || string.IsNullOrWhiteSpace(claims.Nonce)
            || string.IsNullOrWhiteSpace(claims.MapId))
        {
            return Failure(TicketValidationFailure.Malformed);
        }

        if (!string.Equals(claims.RealmId, expectedRealmId, StringComparison.Ordinal))
        {
            return Failure(TicketValidationFailure.WrongRealm);
        }

        if (claims.IssuedAt > now.AddSeconds(5))
        {
            return Failure(TicketValidationFailure.NotYetValid);
        }

        if (claims.ExpiresAt <= now)
        {
            return Failure(TicketValidationFailure.Expired);
        }

        return new TicketValidationResult(TicketValidationFailure.None, claims);
    }

    private static TicketValidationResult Failure(TicketValidationFailure failure) => new(failure, null);

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

public sealed class WorldEntryTicketReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _redeemed = new(StringComparer.Ordinal);

    public bool TryRedeem(WorldEntryTicketClaims claims, DateTimeOffset now)
    {
        foreach (var expired in _redeemed.Where(item => item.Value <= now))
        {
            _redeemed.TryRemove(expired.Key, out _);
        }

        return claims.ExpiresAt > now && _redeemed.TryAdd(claims.Nonce, claims.ExpiresAt);
    }
}
