namespace Mirage.Domain.World;

public sealed record CharacterInventorySnapshot(
    Guid CharacterId,
    IReadOnlyList<InventoryItem> Items,
    DateTimeOffset SavedAt,
    IReadOnlyList<EquippedItem>? Equipment = null);

public interface ICharacterInventoryStore
{
    ValueTask<CharacterInventorySnapshot?> LoadAsync(
        Guid characterId,
        CancellationToken cancellationToken);

    ValueTask<bool> SaveAsync(
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed class InMemoryCharacterInventoryStore : ICharacterInventoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CharacterInventorySnapshot> _snapshots = [];

    public ValueTask<CharacterInventorySnapshot?> LoadAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var snapshot = _snapshots.GetValueOrDefault(characterId);
            return ValueTask.FromResult(snapshot is null ? null : Clone(snapshot));
        }
    }

    public ValueTask<bool> SaveAsync(
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_snapshots.TryGetValue(snapshot.CharacterId, out var current)
                && current.SavedAt > snapshot.SavedAt)
            {
                return ValueTask.FromResult(false);
            }

            _snapshots[snapshot.CharacterId] = Clone(snapshot);
            return ValueTask.FromResult(true);
        }
    }

    private static CharacterInventorySnapshot Clone(CharacterInventorySnapshot snapshot) =>
        snapshot with
        {
            Items = snapshot.Items.ToArray(),
            Equipment = (snapshot.Equipment ?? []).ToArray()
        };
}
