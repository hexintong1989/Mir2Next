using System.Threading.Channels;

namespace Mirage.Domain.World;

public sealed class WorldSimulation
{
    private readonly WorldInstance _world;
    private readonly Channel<MoveCommand> _commands;

    public WorldSimulation(WorldInstance world, int queueCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _world = world;
        _commands = Channel.CreateBounded<MoveCommand>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

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

    public int Tick(int maximumCommands = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommands);
        var processed = 0;
        while (processed < maximumCommands && _commands.Reader.TryRead(out var command))
        {
            if (!command.CancellationToken.IsCancellationRequested)
            {
                command.Completion.TrySetResult(
                    _world.Move(command.CharacterId, command.Sequence, command.Target));
            }
            else
            {
                command.Completion.TrySetCanceled(command.CancellationToken);
            }

            processed++;
        }

        return processed;
    }

    private sealed record MoveCommand(
        Guid CharacterId,
        long Sequence,
        GridPosition Target,
        TaskCompletionSource<MoveResult> Completion,
        CancellationToken CancellationToken);
}
