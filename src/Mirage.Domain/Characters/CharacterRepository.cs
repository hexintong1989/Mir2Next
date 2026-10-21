namespace Mirage.Domain.Characters;

public enum AddCharacterOutcome
{
    Added,
    NameAlreadyExists,
    AccountLimitReached
}

public interface ICharacterWorldStateStore
{
    ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken);
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
}

public sealed class InMemoryCharacterRepository : ICharacterRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameCharacter> _charactersByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<GameCharacter>> _charactersByAccount = [];

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
                    WorldStateSavedAt = state.SavedAt
                };
                characters[index] = updated;
                _charactersByName[updated.NormalizedName] = updated;
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }
}

public sealed class InMemoryCharacterWorldStateStore : ICharacterWorldStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CharacterWorldState> _states = [];

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
}
