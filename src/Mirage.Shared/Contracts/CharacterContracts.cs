namespace Mirage.Shared.Contracts;

public enum CharacterArchetype
{
    Warrior = 1,
    Mage = 2,
    Taoist = 3,
    Assassin = 4
}

public sealed record CreateCharacterRequest(string? Name, CharacterArchetype Archetype);

public sealed record CharacterResponse(
    Guid CharacterId,
    string Name,
    CharacterArchetype Archetype,
    int Level,
    DateTimeOffset CreatedAt);

public sealed record RealmResponse(
    string RealmId,
    string DisplayName,
    string Status,
    string Host,
    int Port,
    int MinimumProtocolVersion,
    int MaximumProtocolVersion);
