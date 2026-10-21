using System.Text;
using System.Text.RegularExpressions;
using Mirage.Shared.Contracts;

namespace Mirage.Domain.Characters;

public sealed record GameCharacter(
    Guid Id,
    Guid AccountId,
    string Name,
    string NormalizedName,
    CharacterArchetype Archetype,
    int Level,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionRequestedAt = null,
    DateTimeOffset? DeleteAfter = null)
{
    public bool IsPendingDeletion => DeletionRequestedAt is not null;
}

public static partial class CharacterPolicy
{
    public const int MaximumCharactersPerAccount = 4;

    public static string DisplayName(string? value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    public static string NormalizeName(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static bool IsValidName(string value) => CharacterNamePattern().IsMatch(value);

    [GeneratedRegex("^[\\p{L}\\p{N}_]{2,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterNamePattern();
}
