namespace Mirage.Domain.Characters;

public enum AddCharacterOutcome
{
    Added,
    NameAlreadyExists,
    AccountLimitReached
}

public interface ICharacterWorldStateStore
{
    ValueTask<bool> CanEnterWorldAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken);

    ValueTask<CharacterWorldState?> LoadWorldStateAsync(
        Guid characterId,
        CancellationToken cancellationToken);

    ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken);

    ValueTask<int> SaveWorldStatesAsync(
        IReadOnlyList<CharacterWorldState> states,
        CancellationToken cancellationToken);
}

public static class CharacterWorldStateBatch
{
    public static IReadOnlyList<CharacterWorldState> CollapseLatest(
        IReadOnlyList<CharacterWorldState> states)
    {
        if (states.Count <= 1)
        {
            return states;
        }

        var latest = new Dictionary<Guid, CharacterWorldState>(states.Count);
        foreach (var state in states)
        {
            if (!latest.TryGetValue(state.CharacterId, out var current)
                || current.SavedAt <= state.SavedAt)
            {
                latest[state.CharacterId] = state;
            }
        }

        return latest.Values.ToArray();
    }
}

public interface ICharacterRepository : ICharacterWorldStateStore
{
    ValueTask<GameCharacter?> FindOwnedAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameCharacter>> ListByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    ValueTask<AddCharacterOutcome> TryAddAsync(
        GameCharacter character,
        int accountLimit,
        CancellationToken cancellationToken);

    ValueTask<GameCharacter?> SetDeletionScheduleAsync(
        Guid accountId,
        Guid characterId,
        DateTimeOffset? requestedAt,
        DateTimeOffset? deleteAfter,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CharacterDeletionAudit>> PurgeDueDeletionsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed class InMemoryCharacterRepository : ICharacterRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameCharacter> _charactersByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<GameCharacter>> _charactersByAccount = [];

    public ValueTask<bool> CanEnterWorldAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var allowed = _charactersByAccount.TryGetValue(accountId, out var characters)
                && characters.Any(character => character.Id == characterId && !character.IsPendingDeletion);
            return ValueTask.FromResult(allowed);
        }
    }

    public ValueTask<GameCharacter?> FindOwnedAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var character = _charactersByAccount.TryGetValue(accountId, out var characters)
                ? characters.FirstOrDefault(value => value.Id == characterId)
                : null;
            return ValueTask.FromResult(character);
        }
    }

    public ValueTask<IReadOnlyList<GameCharacter>> ListByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<GameCharacter> result = _charactersByAccount.TryGetValue(accountId, out var characters)
                ? characters.OrderBy(character => character.CreatedAt).ToArray()
                : [];
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<AddCharacterOutcome> TryAddAsync(
        GameCharacter character,
        int accountLimit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_charactersByName.ContainsKey(character.NormalizedName))
            {
                return ValueTask.FromResult(AddCharacterOutcome.NameAlreadyExists);
            }

            if (!_charactersByAccount.TryGetValue(character.AccountId, out var characters))
            {
                characters = [];
                _charactersByAccount.Add(character.AccountId, characters);
            }

            if (characters.Count >= accountLimit)
            {
                return ValueTask.FromResult(AddCharacterOutcome.AccountLimitReached);
            }

            _charactersByName.Add(character.NormalizedName, character);
            characters.Add(character);
            return ValueTask.FromResult(AddCharacterOutcome.Added);
        }
    }

    public ValueTask<GameCharacter?> SetDeletionScheduleAsync(
        Guid accountId,
        Guid characterId,
        DateTimeOffset? requestedAt,
        DateTimeOffset? deleteAfter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_charactersByAccount.TryGetValue(accountId, out var characters))
            {
                return ValueTask.FromResult<GameCharacter?>(null);
            }

            var index = characters.FindIndex(character => character.Id == characterId);
            if (index < 0)
            {
                return ValueTask.FromResult<GameCharacter?>(null);
            }

            var updated = characters[index] with
            {
                DeletionRequestedAt = requestedAt,
                DeleteAfter = deleteAfter
            };
            characters[index] = updated;
            _charactersByName[updated.NormalizedName] = updated;
            return ValueTask.FromResult<GameCharacter?>(updated);
        }
    }

    public ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var characters in _charactersByAccount.Values)
            {
                var index = characters.FindIndex(character => character.Id == state.CharacterId);
                if (index < 0)
                {
                    continue;
                }

                var current = characters[index];
                if (current.WorldStateSavedAt is not null && current.WorldStateSavedAt > state.SavedAt)
                {
                    return ValueTask.FromResult(false);
                }

                var updated = current with
                {
                    MapId = state.MapId,
                    PositionX = state.PositionX,
                    PositionY = state.PositionY,
                    WorldStateSavedAt = state.SavedAt,
                    Health = state.Health ?? current.Health,
                    Mana = state.Mana ?? current.Mana
                };
                characters[index] = updated;
                _charactersByName[updated.NormalizedName] = updated;
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<int> SaveWorldStatesAsync(
        IReadOnlyList<CharacterWorldState> states,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = CharacterWorldStateBatch.CollapseLatest(states);
        lock (_gate)
        {
            var savedCount = 0;
            foreach (var state in batch)
            {
                foreach (var characters in _charactersByAccount.Values)
                {
                    var index = characters.FindIndex(character => character.Id == state.CharacterId);
                    if (index < 0)
                    {
                        continue;
                    }

                    var current = characters[index];
                    if (current.WorldStateSavedAt is not null && current.WorldStateSavedAt > state.SavedAt)
                    {
                        break;
                    }

                    var updated = current with
                    {
                        MapId = state.MapId,
                        PositionX = state.PositionX,
                        PositionY = state.PositionY,
                        WorldStateSavedAt = state.SavedAt,
                        Health = state.Health ?? current.Health,
                        Mana = state.Mana ?? current.Mana
                    };
                    characters[index] = updated;
                    _charactersByName[updated.NormalizedName] = updated;
                    savedCount++;
                    break;
                }
            }

            return ValueTask.FromResult(savedCount);
        }
    }

    public ValueTask<CharacterWorldState?> LoadWorldStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var character = _charactersByAccount.Values.SelectMany(characters => characters)
                .FirstOrDefault(value => value.Id == characterId);
            return ValueTask.FromResult(character?.WorldStateSavedAt is { } savedAt
                ? new CharacterWorldState(character.Id, character.MapId,
                    character.PositionX, character.PositionY, savedAt,
                    character.Health, character.Mana)
                : null);
        }
    }

    public ValueTask<IReadOnlyList<CharacterDeletionAudit>> PurgeDueDeletionsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var due = _charactersByAccount.Values
                .SelectMany(characters => characters)
                .Where(character => character.DeleteAfter <= now)
                .OrderBy(character => character.DeleteAfter)
                .ThenBy(character => character.Id)
                .Take(maximumCount)
                .ToArray();
            var audits = new List<CharacterDeletionAudit>(due.Length);
            foreach (var character in due)
            {
                _charactersByName.Remove(character.NormalizedName);
                var accountCharacters = _charactersByAccount[character.AccountId];
                accountCharacters.RemoveAll(value => value.Id == character.Id);
                if (accountCharacters.Count == 0)
                {
                    _charactersByAccount.Remove(character.AccountId);
                }

                audits.Add(new CharacterDeletionAudit(
                    character.Id,
                    character.AccountId,
                    character.Name,
                    character.DeletionRequestedAt!.Value,
                    now));
            }

            return ValueTask.FromResult<IReadOnlyList<CharacterDeletionAudit>>(audits);
        }
    }
}

public sealed class InMemoryCharacterWorldStateStore : ICharacterWorldStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CharacterWorldState> _states = [];

    public ValueTask<bool> CanEnterWorldAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<CharacterWorldState?> LoadWorldStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_states.GetValueOrDefault(characterId));
        }
    }

    public ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_states.TryGetValue(state.CharacterId, out var current)
                && current.SavedAt > state.SavedAt)
            {
                return ValueTask.FromResult(false);
            }

            _states[state.CharacterId] = state;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<int> SaveWorldStatesAsync(
        IReadOnlyList<CharacterWorldState> states,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = CharacterWorldStateBatch.CollapseLatest(states);
        lock (_gate)
        {
            var savedCount = 0;
            foreach (var state in batch)
            {
                if (_states.TryGetValue(state.CharacterId, out var current)
                    && current.SavedAt > state.SavedAt)
                {
                    continue;
                }

                _states[state.CharacterId] = state;
                savedCount++;
            }

            return ValueTask.FromResult(savedCount);
        }
    }
}
