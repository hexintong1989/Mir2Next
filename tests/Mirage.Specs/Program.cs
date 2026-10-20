using Mirage.Domain.Accounts;
using Mirage.Domain.Common;
using Mirage.Domain.Characters;
using Mirage.Shared.Contracts;
using Mirage.Domain.World;

var specifications = new (string Name, Func<Task> Run)[]
{
    ("注册会规范化用户名", RegisterNormalizesUserName),
    ("用户名不区分大小写且不可重复", DuplicateUserNameIsRejected),
    ("弱密码会被拒绝", WeakPasswordIsRejected),
    ("正确凭据会签发有效会话", LoginIssuesSession),
    ("未知账号与错误密码返回相同错误", InvalidCredentialsDoNotRevealAccount),
    ("会话可以验证并注销", SessionCanBeValidatedAndRevoked),
    ("过期会话会被拒绝", ExpiredSessionIsRejected),
    ("角色创建后可按账号列出", CharacterCanBeCreatedAndListed),
    ("角色名在全服不区分大小写且唯一", CharacterNameIsGloballyUnique),
    ("每账号最多创建四个角色", CharacterLimitIsEnforced),
    ("世界实例允许相邻移动", WorldAllowsAdjacentMovement),
    ("世界实例拒绝越界和跨格移动", WorldRejectsInvalidMovement),
    ("世界实例拒绝过期消息序号", WorldRejectsStaleSequence),
    ("世界实例阻止玩家占用同一格", WorldRejectsOccupiedPosition)
};

var failures = 0;
foreach (var specification in specifications)
{
    try
    {
        await specification.Run();
        Console.WriteLine($"PASS {specification.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {specification.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{specifications.Length - failures}/{specifications.Length} specifications passed.");
return failures == 0 ? 0 : 1;

static async Task RegisterNormalizesUserName()
{
    var service = CreateService();
    var result = await service.RegisterAsync("  Player_01  ", "correct-horse-9");
    Require(result.IsSuccess, "注册应成功");
    Require(result.Value!.NormalizedUserName == "player_01", "用户名应被修剪并转为小写");
}

static async Task DuplicateUserNameIsRejected()
{
    var service = CreateService();
    await service.RegisterAsync("player_02", "correct-horse-9");
    var duplicate = await service.RegisterAsync("PLAYER_02", "another-pass-8");
    Require(duplicate.Error?.Code == ErrorCode.AccountAlreadyExists, "重复用户名应被拒绝");
}

static async Task WeakPasswordIsRejected()
{
    var service = CreateService();
    var result = await service.RegisterAsync("player_03", "passwordonly");
    Require(result.Error?.Code == ErrorCode.WeakPassword, "无数字密码应被拒绝");
}

static async Task LoginIssuesSession()
{
    var clock = new TestClock(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
    var service = CreateService(clock);
    var account = await service.RegisterAsync("player_04", "correct-horse-9");
    var login = await service.LoginAsync("PLAYER_04", "correct-horse-9");

    Require(account.IsSuccess && login.IsSuccess, "注册和登录应成功");
    Require(login.Value!.AccountId == account.Value!.Id, "会话应属于登录账号");
    Require(login.Value.AccessToken.Length >= 40, "令牌应具有足够熵");
    Require(login.Value.ExpiresAt > clock.UtcNow, "会话应具有未来过期时间");
}

static async Task InvalidCredentialsDoNotRevealAccount()
{
    var service = CreateService();
    await service.RegisterAsync("player_05", "correct-horse-9");
    var missing = await service.LoginAsync("missing_05", "correct-horse-9");
    var wrongPassword = await service.LoginAsync("player_05", "wrong-password-1");

    Require(missing.Error?.Code == ErrorCode.InvalidCredentials, "未知账号应返回凭据错误");
    Require(wrongPassword.Error?.Code == ErrorCode.InvalidCredentials, "错误密码应返回凭据错误");
    Require(missing.Error?.Message == wrongPassword.Error?.Message, "两种错误不应泄漏账号存在性");
}

static async Task SessionCanBeValidatedAndRevoked()
{
    var service = CreateService();
    await service.RegisterAsync("player_06", "correct-horse-9");
    var login = await service.LoginAsync("player_06", "correct-horse-9");
    var active = await service.ValidateSessionAsync(login.Value!.AccessToken);

    Require(active.IsSuccess, "新签发的会话应有效");
    Require(active.Value!.AccountId == login.Value.AccountId, "会话主体应匹配账号");
    Require((await service.RevokeSessionAsync(login.Value.AccessToken)).IsSuccess, "注销应成功");
    Require(!(await service.ValidateSessionAsync(login.Value.AccessToken)).IsSuccess, "注销后的会话应失效");
}

static async Task ExpiredSessionIsRejected()
{
    var clock = new TestClock(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
    var service = CreateService(clock, TimeSpan.FromMinutes(5));
    await service.RegisterAsync("player_07", "correct-horse-9");
    var login = await service.LoginAsync("player_07", "correct-horse-9");
    clock.UtcNow = clock.UtcNow.AddMinutes(6);

    Require(!(await service.ValidateSessionAsync(login.Value!.AccessToken)).IsSuccess, "过期会话应失效");
}

static async Task CharacterCanBeCreatedAndListed()
{
    var service = CreateCharacterService();
    var accountId = Guid.NewGuid();
    var created = await service.CreateAsync(accountId, "  云中客  ", CharacterArchetype.Warrior);
    var characters = await service.ListAsync(accountId);

    Require(created.IsSuccess, "合法角色应创建成功");
    Require(characters.Count == 1, "账号应有一个角色");
    Require(characters[0].Name == "云中客" && characters[0].Level == 1, "角色初始数据应正确");
}

static async Task CharacterNameIsGloballyUnique()
{
    var service = CreateCharacterService();
    await service.CreateAsync(Guid.NewGuid(), "Blade_01", CharacterArchetype.Warrior);
    var duplicate = await service.CreateAsync(Guid.NewGuid(), "blade_01", CharacterArchetype.Mage);
    Require(duplicate.Error?.Code == ErrorCode.CharacterNameAlreadyExists, "全服重名应被拒绝");
}

static async Task CharacterLimitIsEnforced()
{
    var service = CreateCharacterService();
    var accountId = Guid.NewGuid();
    for (var index = 0; index < CharacterPolicy.MaximumCharactersPerAccount; index++)
    {
        var created = await service.CreateAsync(accountId, $"hero_{index}", CharacterArchetype.Taoist);
        Require(created.IsSuccess, "上限以内应允许创建");
    }

    var overflow = await service.CreateAsync(accountId, "hero_over", CharacterArchetype.Assassin);
    Require(overflow.Error?.Code == ErrorCode.CharacterLimitReached, "超过角色上限应被拒绝");
}

static Task WorldAllowsAdjacentMovement()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    Require(world.Join(characterId, new GridPosition(5, 5)).IsSuccess, "角色应成功进入地图");
    var movement = world.Move(characterId, 1, new GridPosition(6, 6));
    Require(movement.IsSuccess, "角色应能移动到相邻格");
    Require(movement.Player.Position == new GridPosition(6, 6), "权威位置应更新");
    return Task.CompletedTask;
}

static Task WorldRejectsInvalidMovement()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(0, 0));
    var outside = world.Move(characterId, 1, new GridPosition(-1, 0));
    var jump = world.Move(characterId, 2, new GridPosition(3, 3));
    Require(outside.Failure == MoveFailure.OutOfBounds, "越界移动应被拒绝");
    Require(jump.Failure == MoveFailure.InvalidStep, "跨格移动应被拒绝");
    Require(jump.Player.Position == new GridPosition(0, 0), "非法移动不应改变位置");
    return Task.CompletedTask;
}

static Task WorldRejectsStaleSequence()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(2, 2));
    world.Move(characterId, 5, new GridPosition(3, 2));
    var stale = world.Move(characterId, 4, new GridPosition(4, 2));
    Require(stale.Failure == MoveFailure.StaleSequence, "旧消息序号应被拒绝");
    Require(stale.Player.Position == new GridPosition(3, 2), "旧消息不应回滚位置");
    return Task.CompletedTask;
}

static Task WorldRejectsOccupiedPosition()
{
    var world = new WorldInstance("test", 10, 10);
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    world.Join(first, new GridPosition(1, 1));
    world.Join(second, new GridPosition(2, 1));
    var blocked = world.Move(first, 1, new GridPosition(2, 1));
    Require(blocked.Failure == MoveFailure.Occupied, "已占用格应拒绝进入");
    return Task.CompletedTask;
}

static AccountService CreateService(IClock? clock = null, TimeSpan? sessionLifetime = null) =>
    new(
        new InMemoryAccountRepository(),
        new Pbkdf2PasswordHasher(10_000),
        new InMemorySessionStore(sessionLifetime),
        clock ?? new TestClock(DateTimeOffset.UtcNow));

static CharacterService CreateCharacterService() =>
    new(new InMemoryCharacterRepository(), new TestClock(DateTimeOffset.UtcNow));

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class TestClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}
