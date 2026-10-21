using System.Threading.Channels;

namespace Mirage.Domain.World;

public sealed class WorldSimulation
{
    private readonly WorldInstance _world;
    private readonly Channel<IWorldCommand> _commands;
    private long _currentTick;

    public WorldSimulation(WorldInstance world, int queueCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _world = world;
        _commands = Channel.CreateBounded<IWorldCommand>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public long CurrentTick => Interlocked.Read(ref _currentTick);

    public async ValueTask<MoveResult> QueueMoveAsync(
        Guid characterId,
        long sequence,
        GridPosition target,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<MoveResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new MoveCommand(characterId, sequence, target, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<AttackResult> QueueAttackAsync(
        Guid characterId,
        long sequence,
        Guid monsterId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<AttackResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AttackCommand(characterId, sequence, monsterId, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<PickupResult> QueuePickupAsync(
        Guid characterId,
        long sequence,
        Guid dropId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<PickupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new PickupCommand(characterId, sequence, dropId, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<EquipmentResult> QueueEquipAsync(
        Guid characterId,
        long sequence,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<EquipmentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new EquipCommand(characterId, sequence, itemId, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<EquipmentResult> QueueUnequipAsync(
        Guid characterId,
        long sequence,
        EquipmentSlot slot,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<EquipmentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new UnequipCommand(characterId, sequence, slot, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public int Tick(int maximumCommands = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommands);
        var currentTick = Interlocked.Increment(ref _currentTick);
        var processed = 0;
        while (processed < maximumCommands && _commands.Reader.TryRead(out var command))
        {
            if (!command.CancellationToken.IsCancellationRequested)
            {
                command.Execute(_world, currentTick);
            }
            else
            {
                command.Cancel();
            }

            processed++;
        }

        _world.Advance(currentTick);
        return processed;
    }

    private interface IWorldCommand
    {
        CancellationToken CancellationToken { get; }
        void Execute(WorldInstance world, long currentTick);
        void Cancel();
    }

    private sealed record MoveCommand(
        Guid CharacterId,
        long Sequence,
        GridPosition Target,
        TaskCompletionSource<MoveResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Move(CharacterId, Sequence, Target));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record AttackCommand(
        Guid CharacterId,
        long Sequence,
        Guid MonsterId,
        TaskCompletionSource<AttackResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Attack(CharacterId, Sequence, MonsterId, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record PickupCommand(
        Guid CharacterId,
        long Sequence,
        Guid DropId,
        TaskCompletionSource<PickupResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Pickup(CharacterId, Sequence, DropId, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record EquipCommand(
        Guid CharacterId,
        long Sequence,
        string ItemId,
        TaskCompletionSource<EquipmentResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Equip(CharacterId, Sequence, ItemId));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record UnequipCommand(
        Guid CharacterId,
        long Sequence,
        EquipmentSlot Slot,
        TaskCompletionSource<EquipmentResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Unequip(CharacterId, Sequence, Slot));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }
}
