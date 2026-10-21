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
}
