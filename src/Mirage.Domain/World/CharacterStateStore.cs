using Mirage.Domain.Characters;

namespace Mirage.Domain.World;

public interface ICharacterStateStore : ICharacterInventoryStore, ICharacterWorldStateStore
{
    ValueTask<bool> SaveInventoryAndWorldStateAsync(
        CharacterInventorySnapshot inventory,
        CharacterWorldState worldState,
        CancellationToken cancellationToken);
}

public sealed class InMemoryCharacterStateStore : ICharacterStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InMemoryCharacterInventoryStore _inventories = new();
    private readonly InMemoryCharacterWorldStateStore _worldStates = new();

    public ValueTask<bool> CanEnterWorldAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken) =>
        _worldStates.CanEnterWorldAsync(accountId, characterId, cancellationToken);

    public async ValueTask<CharacterInventorySnapshot?> LoadAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _inventories.LoadAsync(characterId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CharacterWorldState?> LoadWorldStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _worldStates.LoadWorldStateAsync(characterId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> SaveAsync(
        CharacterInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _inventories.SaveAsync(snapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> SaveWorldStateAsync(
        CharacterWorldState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _worldStates.SaveWorldStateAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> SaveWorldStatesAsync(
        IReadOnlyList<CharacterWorldState> states,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _worldStates.SaveWorldStatesAsync(states, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> SaveInventoryAndWorldStateAsync(
        CharacterInventorySnapshot inventory,
        CharacterWorldState worldState,
        CancellationToken cancellationToken)
    {
        if (inventory.CharacterId != worldState.CharacterId
            || inventory.SavedAt != worldState.SavedAt)
        {
            throw new ArgumentException("Combined snapshots must share character and version.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousInventory = await _inventories.LoadAsync(inventory.CharacterId, cancellationToken);
            var previousWorld = await _worldStates.LoadWorldStateAsync(worldState.CharacterId, cancellationToken);
            if (previousInventory?.SavedAt > inventory.SavedAt)
            {
                return false;
            }

            // The in-memory stores finish synchronously. After version checks, keep both
            // updates under one gate and do not interrupt halfway through the pair.
            await _inventories.SaveAsync(inventory, CancellationToken.None);
            if (previousWorld?.SavedAt is null || previousWorld.SavedAt <= worldState.SavedAt)
            {
                await _worldStates.SaveWorldStateAsync(worldState, CancellationToken.None);
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
