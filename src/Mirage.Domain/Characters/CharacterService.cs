using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Shared.Contracts;

namespace Mirage.Domain.Characters;

public sealed class CharacterService(
    ICharacterRepository characters,
    IClock clock)
{
    public static readonly TimeSpan DeletionDelay = TimeSpan.FromHours(24);

    public ValueTask<GameCharacter?> FindOwnedAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken = default) =>
        characters.FindOwnedAsync(accountId, characterId, cancellationToken);

    public ValueTask<IReadOnlyList<GameCharacter>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        characters.ListByAccountAsync(accountId, cancellationToken);

    public async ValueTask<Result<GameCharacter>> CreateAsync(
        Guid accountId,
        string? name,
        CharacterArchetype archetype,
        CancellationToken cancellationToken = default)
    {
        var displayName = CharacterPolicy.DisplayName(name);
        if (!CharacterPolicy.IsValidName(displayName))
        {
            return Result<GameCharacter>.Failure(
                ErrorCode.InvalidCharacterName,
                "角色名须为 2 至 12 位文字、字母、数字或下划线。");
        }

        if (!Enum.IsDefined(archetype))
        {
            return Result<GameCharacter>.Failure(
                ErrorCode.InvalidCharacterArchetype,
                "角色职业无效。");
        }

        var character = new GameCharacter(
            Guid.NewGuid(),
            accountId,
            displayName,
            CharacterPolicy.NormalizeName(displayName),
            archetype,
            1,
            clock.UtcNow);

        var outcome = await characters.TryAddAsync(
            character,
            CharacterPolicy.MaximumCharactersPerAccount,
            cancellationToken);

        return outcome switch
        {
            AddCharacterOutcome.Added => Result<GameCharacter>.Success(character),
            AddCharacterOutcome.NameAlreadyExists => Result<GameCharacter>.Failure(
                ErrorCode.CharacterNameAlreadyExists,
                "角色名已存在。"),
            AddCharacterOutcome.AccountLimitReached => Result<GameCharacter>.Failure(
                ErrorCode.CharacterLimitReached,
                $"每个账号最多创建 {CharacterPolicy.MaximumCharactersPerAccount} 个角色。"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }

    public async ValueTask<Result<GameCharacter>> RequestDeletionAsync(
        Guid accountId,
        Guid characterId,
        string? confirmationName,
        CancellationToken cancellationToken = default)
    {
        var character = await characters.FindOwnedAsync(accountId, characterId, cancellationToken);
        if (character is null)
        {
            return Result<GameCharacter>.Failure(ErrorCode.CharacterNotFound, "角色不存在。");
        }

        var normalizedConfirmation = CharacterPolicy.NormalizeName(
            CharacterPolicy.DisplayName(confirmationName));
        if (!string.Equals(normalizedConfirmation, character.NormalizedName, StringComparison.Ordinal))
        {
            return Result<GameCharacter>.Failure(
                ErrorCode.CharacterDeletionConfirmationMismatch,
                "输入的角色名与待删除角色不一致。");
        }

        if (character.IsPendingDeletion)
        {
            return Result<GameCharacter>.Success(character);
        }

        var requestedAt = clock.UtcNow;
        var updated = await characters.SetDeletionScheduleAsync(
            accountId,
            characterId,
            requestedAt,
            requestedAt.Add(DeletionDelay),
            cancellationToken);
        return updated is null
            ? Result<GameCharacter>.Failure(ErrorCode.CharacterNotFound, "角色不存在。")
            : Result<GameCharacter>.Success(updated);
    }

    public async ValueTask<Result<GameCharacter>> CancelDeletionAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken = default)
    {
        var character = await characters.FindOwnedAsync(accountId, characterId, cancellationToken);
        if (character is null)
        {
            return Result<GameCharacter>.Failure(ErrorCode.CharacterNotFound, "角色不存在。");
        }

        if (!character.IsPendingDeletion)
        {
            return Result<GameCharacter>.Failure(
                ErrorCode.CharacterDeletionNotPending,
                "角色当前没有待处理的删除申请。");
        }

        var updated = await characters.SetDeletionScheduleAsync(
            accountId,
            characterId,
            null,
            null,
            cancellationToken);
        return updated is null
            ? Result<GameCharacter>.Failure(ErrorCode.CharacterNotFound, "角色不存在。")
            : Result<GameCharacter>.Success(updated);
    }
}
