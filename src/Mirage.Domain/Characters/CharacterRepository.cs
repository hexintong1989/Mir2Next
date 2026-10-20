namespace Mirage.Domain.Characters;

public enum AddCharacterOutcome
{
    Added,
    NameAlreadyExists,
    AccountLimitReached
}

public interface ICharacterRepository
{
    ValueTask<IReadOnlyList<GameCharacter>> ListByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    ValueTask<AddCharacterOutcome> TryAddAsync(
        GameCharacter character,
        int accountLimit,
        CancellationToken cancellationToken);
}

public sealed class InMemoryCharacterRepository : ICharacterRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameCharacter> _charactersByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<GameCharacter>> _charactersByAccount = [];

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
}
