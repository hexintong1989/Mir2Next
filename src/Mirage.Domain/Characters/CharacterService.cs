using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Shared.Contracts;

namespace Mirage.Domain.Characters;

public sealed class CharacterService(
    ICharacterRepository characters,
    IClock clock)
{
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
}
