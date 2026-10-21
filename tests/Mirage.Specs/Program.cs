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
    ("世界实例阻止玩家占用同一格", WorldRejectsOccupiedPosition),
    ("移动指令只在世界帧中执行", MovementExecutesOnWorldTick),
    ("AOI 只返回视野范围内玩家", AreaOfInterestFiltersDistantPlayers),
    ("AOI 增量识别进入更新与离开", AreaOfInterestTracksDeltas),
    ("离开地图会释放占用位置", LeavingWorldReleasesPosition),
    ("角色仓储只返回账号拥有的角色", CharacterOwnershipIsEnforced),
    ("角色删除必须确认名称并经过冷静期", CharacterDeletionRequiresConfirmation),
    ("角色删除申请可以撤销", CharacterDeletionCanBeCancelled),
    ("世界票据可验证且篡改会失败", WorldTicketRejectsTampering),
    ("世界票据校验区服和有效期", WorldTicketChecksRealmAndExpiry),
    ("世界票据只能兑换一次", WorldTicketCannotBeReplayed)
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

static async Task MovementExecutesOnWorldTick()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(2, 2));
    var simulation = new WorldSimulation(world, 8);

    var pending = simulation.QueueMoveAsync(characterId, 1, new GridPosition(3, 2)).AsTask();
    Require(!pending.IsCompleted, "移动不应在入队时立即执行");
    Require(simulation.Tick() == 1, "世界帧应处理一个指令");
    var result = await pending;
    Require(result.IsSuccess && result.Player.Position == new GridPosition(3, 2), "帧处理后应更新位置");
}

static Task AreaOfInterestFiltersDistantPlayers()
{
    var world = new WorldInstance("test", 100, 100);
    var observer = Guid.NewGuid();
    var nearby = Guid.NewGuid();
    var distant = Guid.NewGuid();
    world.Join(observer, new GridPosition(10, 10));
    world.Join(nearby, new GridPosition(13, 12));
    world.Join(distant, new GridPosition(30, 30));

    var visible = world.VisibleTo(observer, 5);
    Require(visible.Any(player => player.CharacterId == observer), "视野应包含观察者自身");
    Require(visible.Any(player => player.CharacterId == nearby), "附近玩家应可见");
    Require(visible.All(player => player.CharacterId != distant), "远处玩家不应进入视野");
    return Task.CompletedTask;
}

static Task AreaOfInterestTracksDeltas()
{
    var tracker = new AoiVisibilityTracker();
    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();
    var first = new WorldPlayer(firstId, new GridPosition(1, 1), 0);
    var second = new WorldPlayer(secondId, new GridPosition(2, 1), 0);

    var initial = tracker.Update([first]);
    var changed = tracker.Update([first with { Position = new GridPosition(1, 2) }, second], firstId);
    var departed = tracker.Update([second]);

    Require(initial.Entered.Count == 1, "首次视野应报告进入");
    Require(changed.Entered.Single().CharacterId == secondId, "新增可见玩家应报告进入");
    Require(changed.Updated.Single().CharacterId == firstId, "已可见的变化玩家应报告更新");
    Require(departed.Left.Single() == firstId, "不可见玩家应报告离开");
    return Task.CompletedTask;
}

static Task LeavingWorldReleasesPosition()
{
    var world = new WorldInstance("test", 10, 10);
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    world.Join(first, new GridPosition(5, 5));
    Require(world.Leave(first), "已加入玩家应能离开");
    Require(world.Join(second, new GridPosition(5, 5)).IsSuccess, "离开后原位置应可再次使用");
    return Task.CompletedTask;
}

static async Task CharacterOwnershipIsEnforced()
{
    var service = CreateCharacterService();
    var ownerId = Guid.NewGuid();
    var created = await service.CreateAsync(ownerId, "owner_hero", CharacterArchetype.Warrior);
    var owned = await service.FindOwnedAsync(ownerId, created.Value!.Id);
    var otherAccount = await service.FindOwnedAsync(Guid.NewGuid(), created.Value.Id);
    Require(owned?.Id == created.Value.Id, "账号应能找到自己的角色");
    Require(otherAccount is null, "其他账号不能选择该角色");
}

static async Task CharacterDeletionRequiresConfirmation()
{
    var clock = new TestClock(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
    var service = CreateCharacterService(clock);
    var accountId = Guid.NewGuid();
    var created = await service.CreateAsync(accountId, "Delete_Me", CharacterArchetype.Mage);
    var mismatch = await service.RequestDeletionAsync(accountId, created.Value!.Id, "someone_else");
    var requested = await service.RequestDeletionAsync(accountId, created.Value.Id, "delete_me");

    Require(
        mismatch.Error?.Code == ErrorCode.CharacterDeletionConfirmationMismatch,
        "错误角色名不能申请删除");
    Require(requested.IsSuccess, "正确角色名应能申请删除");
    var pendingCharacter = requested.Value!;
    Require(pendingCharacter.IsPendingDeletion, "正确角色名应进入待删除状态");
    Require(pendingCharacter.DeleteAfter == clock.UtcNow.Add(CharacterService.DeletionDelay), "应保留24小时冷静期");
}

static async Task CharacterDeletionCanBeCancelled()
{
    var service = CreateCharacterService();
    var accountId = Guid.NewGuid();
    var created = await service.CreateAsync(accountId, "keep_me", CharacterArchetype.Taoist);
    await service.RequestDeletionAsync(accountId, created.Value!.Id, "keep_me");
    var cancelled = await service.CancelDeletionAsync(accountId, created.Value.Id);
    var selected = await service.FindOwnedAsync(accountId, created.Value.Id);

    Require(cancelled.IsSuccess && !cancelled.Value!.IsPendingDeletion, "撤销后不应再处于待删除状态");
    Require(selected?.DeleteAfter is null, "撤销后删除时间应清空");
}

static Task WorldTicketRejectsTampering()
{
    var protector = CreateTicketProtector();
    var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    var ticket = protector.Issue(Guid.NewGuid(), Guid.NewGuid(), "local", now);
    var valid = protector.Validate(ticket.Token, "local", now.AddSeconds(1));
    var replacement = ticket.Token[0] == 'A' ? 'B' : 'A';
    var tampered = protector.Validate(replacement + ticket.Token[1..], "local", now.AddSeconds(1));
    Require(valid.IsSuccess, "未修改票据应验证成功");
    Require(tampered.Failure == TicketValidationFailure.InvalidSignature, "篡改票据应签名失败");
    return Task.CompletedTask;
}

static Task WorldTicketChecksRealmAndExpiry()
{
    var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    var protector = CreateTicketProtector(TimeSpan.FromSeconds(10));
    var ticket = protector.Issue(Guid.NewGuid(), Guid.NewGuid(), "local", now);
    Require(
        protector.Validate(ticket.Token, "another", now).Failure == TicketValidationFailure.WrongRealm,
        "票据不能跨区服使用");
    Require(
        protector.Validate(ticket.Token, "local", now.AddSeconds(11)).Failure == TicketValidationFailure.Expired,
        "过期票据应被拒绝");
    return Task.CompletedTask;
}

static Task WorldTicketCannotBeReplayed()
{
    var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    var ticket = CreateTicketProtector().Issue(Guid.NewGuid(), Guid.NewGuid(), "local", now);
    var guard = new WorldEntryTicketReplayGuard();
    Require(guard.TryRedeem(ticket.Claims, now), "首次兑换应成功");
    Require(!guard.TryRedeem(ticket.Claims, now), "重复兑换应失败");
    return Task.CompletedTask;
}

static AccountService CreateService(IClock? clock = null, TimeSpan? sessionLifetime = null) =>
    new(
        new InMemoryAccountRepository(),
        new Pbkdf2PasswordHasher(10_000),
        new InMemorySessionStore(sessionLifetime),
        clock ?? new TestClock(DateTimeOffset.UtcNow));

static CharacterService CreateCharacterService(IClock? clock = null) =>
    new(new InMemoryCharacterRepository(), clock ?? new TestClock(DateTimeOffset.UtcNow));

static HmacWorldEntryTicketProtector CreateTicketProtector(TimeSpan? lifetime = null) =>
    new(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), lifetime);

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
