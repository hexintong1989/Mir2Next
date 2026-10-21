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

    public async ValueTask<SkillResult> QueueCastSkillAsync(
        Guid characterId,
        long sequence,
        string skillId,
        Guid monsterId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<SkillResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new SkillCommand(characterId, sequence, skillId, monsterId,
            completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<ChatResult> QueueChatAsync(
        Guid characterId,
        long sequence,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<ChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new ChatCommand(characterId, sequence, text, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<UseItemResult> QueueUseItemAsync(
        Guid characterId,
        long sequence,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<UseItemResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new UseItemCommand(characterId, sequence, itemId, completion, cancellationToken);
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

    public async ValueTask<PartyInviteResult> QueuePartyInviteAsync(
        Guid characterId,
        long sequence,
        Guid targetCharacterId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<PartyInviteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new PartyInviteCommand(
            characterId, sequence, targetCharacterId, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<PartyChangeResult> QueuePartyAcceptAsync(
        Guid characterId,
        long sequence,
        Guid inviterCharacterId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<PartyChangeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new PartyAcceptCommand(
            characterId, sequence, inviterCharacterId, completion, cancellationToken);
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<PartyChangeResult> QueuePartyLeaveAsync(
        Guid characterId,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<PartyChangeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new PartyLeaveCommand(characterId, sequence, completion, cancellationToken);
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

    private sealed record SkillCommand(
        Guid CharacterId,
        long Sequence,
        string SkillId,
        Guid MonsterId,
        TaskCompletionSource<SkillResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.CastSkill(CharacterId, Sequence, SkillId, MonsterId, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record ChatCommand(
        Guid CharacterId,
        long Sequence,
        string? Text,
        TaskCompletionSource<ChatResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.Chat(CharacterId, Sequence, Text, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record UseItemCommand(
        Guid CharacterId,
        long Sequence,
        string ItemId,
        TaskCompletionSource<UseItemResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.UseItem(CharacterId, Sequence, ItemId));

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

    private sealed record PartyInviteCommand(
        Guid CharacterId,
        long Sequence,
        Guid TargetCharacterId,
        TaskCompletionSource<PartyInviteResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.InviteToParty(
                CharacterId, Sequence, TargetCharacterId, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record PartyAcceptCommand(
        Guid CharacterId,
        long Sequence,
        Guid InviterCharacterId,
        TaskCompletionSource<PartyChangeResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.AcceptPartyInvite(
                CharacterId, Sequence, InviterCharacterId, currentTick));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed record PartyLeaveCommand(
        Guid CharacterId,
        long Sequence,
        TaskCompletionSource<PartyChangeResult> Completion,
        CancellationToken CancellationToken) : IWorldCommand
    {
        public void Execute(WorldInstance world, long currentTick) =>
            Completion.TrySetResult(world.LeaveParty(CharacterId, Sequence));

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }
}
