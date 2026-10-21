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
    ("怪物占用格会阻挡玩家移动", MonsterPositionBlocksMovement),
    ("近战攻击由世界帧结算并能击杀怪物", MeleeAttackExecutesOnWorldTick),
    ("攻击会校验距离、冷却和消息序号", AttackValidatesRangeCooldownAndSequence),
    ("怪物会在固定帧追踪附近玩家", MonsterMovesTowardNearbyPlayer),
    ("死亡怪物会按固定帧刷新", DefeatedMonsterRespawns),
    ("怪物死亡掉落具有击杀者保护期", MonsterDropHasOwnerProtection),
    ("拾取指令在世界帧结算并进入背包", PickupExecutesOnWorldTick),
    ("背包快照持久化拒绝旧版本覆盖", InventorySnapshotRejectsStaleSave),
    ("怪物会攻击相邻玩家并造成死亡", MonsterAttacksAndKillsAdjacentPlayer),
    ("死亡玩家会在固定帧复活", DeadPlayerRespawnsOnSchedule),
    ("无人拾取的地面物品会按固定帧过期", GroundDropExpires),
    ("四职业拥有不同的基础战斗属性", ArchetypesHaveDistinctCombatProfiles),
    ("装备会从背包转移并增加权威属性", EquipmentMovesFromInventoryAndAddsStats),
    ("装备与卸下指令都在世界帧执行", EquipmentCommandsExecuteOnWorldTick),
    ("装备随背包快照保存并在加载时重算属性", EquipmentPersistsWithInventorySnapshot),
    ("主动技能按职业距离法力冷却和序号结算", SkillValidatesAuthorityAndResources),
    ("技能进入世界帧并在固定帧恢复法力", SkillExecutesOnWorldTickAndManaRegenerates),
    ("聊天在世界帧校验内容频率和序号", ChatValidatesContentRateAndSequence),
    ("消耗品扣除背包并恢复受上限约束的资源", ConsumablesRestoreResourcesWithoutDuplication),
    ("角色仓储只返回账号拥有的角色", CharacterOwnershipIsEnforced),
    ("角色删除必须确认名称并经过冷静期", CharacterDeletionRequiresConfirmation),
    ("角色删除申请可以撤销", CharacterDeletionCanBeCancelled),
    ("待删除角色即使持有旧票据也不能进入世界", PendingDeletionBlocksWorldEntry),
    ("只有冷静期届满的角色会被清理", OnlyDueCharactersArePurged),
    ("角色世界状态可以持久化且拒绝旧写入", CharacterWorldStatePersistsLatestPosition),
    ("断线重进恢复生命法力并继续死亡倒计时", RestoredCombatResourcesAreAuthoritative),
    ("世界票据可验证且篡改会失败", WorldTicketRejectsTampering),
    ("世界票据携带服务端权威出生点", WorldTicketCarriesAuthoritativeSpawn),
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

static Task MonsterPositionBlocksMovement()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    Require(world.SpawnMonster(monsterId, "training_dummy", new GridPosition(2, 1), 1, 20), "怪物应生成");
    var blocked = world.Move(characterId, 1, new GridPosition(2, 1));
    Require(blocked.Failure == MoveFailure.Occupied, "怪物所在格应阻挡移动");
    return Task.CompletedTask;
}

static async Task MeleeAttackExecutesOnWorldTick()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(monsterId, "training_dummy", new GridPosition(2, 1), 1, 10);
    var simulation = new WorldSimulation(world);

    var pending = simulation.QueueAttackAsync(characterId, 1, monsterId).AsTask();
    Require(!pending.IsCompleted, "攻击不应绕过世界帧直接执行");
    simulation.Tick();
    var result = await pending;
    Require(result.IsSuccess && result.Damage == 10 && result.Defeated, "基础攻击应造成10点伤害并击杀目标");
    Require(world.MonsterSnapshot().Count == 0, "死亡怪物应从活动世界移除");
}

static Task AttackValidatesRangeCooldownAndSequence()
{
    var world = new WorldInstance("test", 20, 20);
    var characterId = Guid.NewGuid();
    var nearby = Guid.NewGuid();
    var distant = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(nearby, "dummy", new GridPosition(2, 1), 1, 100);
    world.SpawnMonster(distant, "dummy", new GridPosition(10, 10), 1, 100);

    var outOfRange = world.Attack(characterId, 1, distant, 1);
    var accepted = world.Attack(characterId, 2, nearby, 1);
    var cooldown = world.Attack(characterId, 3, nearby, 2);
    var stale = world.Attack(characterId, 2, nearby, 20);
    var afterCooldown = world.Attack(characterId, 4, nearby, 11);

    Require(outOfRange.Failure == AttackFailure.OutOfRange, "远距离目标应被拒绝");
    Require(accepted.IsSuccess, "相邻目标应可攻击");
    Require(cooldown.Failure == AttackFailure.Cooldown, "冷却期间应拒绝重复攻击");
    Require(stale.Failure == AttackFailure.StaleSequence, "旧序号攻击应被拒绝");
    Require(afterCooldown.IsSuccess, "冷却结束后应可再次攻击");
    return Task.CompletedTask;
}

static Task MonsterMovesTowardNearbyPlayer()
{
    var world = new WorldInstance("test", 20, 20);
    var characterId = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(5, 5));
    world.SpawnMonster(monsterId, "wolf", new GridPosition(10, 5), 1, 20);

    Require(world.Advance(WorldInstance.MonsterBehaviorIntervalTicks) == 1, "行为帧应移动怪物");
    Require(world.MonsterSnapshot().Single().Position == new GridPosition(9, 5), "怪物应向最近玩家移动一格");
    return Task.CompletedTask;
}

static Task DefeatedMonsterRespawns()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(monsterId, "slime", new GridPosition(2, 1), 1, 10);
    var defeated = world.Attack(characterId, 1, monsterId, 5);

    Require(defeated.Defeated && world.MonsterCount == 0, "怪物应先进入死亡状态");
    Require(world.Advance(104) == 0 && world.MonsterCount == 0, "刷新时间前不应复活");
    Require(world.Advance(105) == 1, "刷新帧应重新生成怪物");
    var respawned = world.MonsterSnapshot().Single();
    Require(respawned.Health == respawned.MaximumHealth, "刷新怪物应恢复满血");
    Require(respawned.Position == new GridPosition(2, 1), "刷新怪物应回到出生点");
    return Task.CompletedTask;
}

static Task MonsterDropHasOwnerProtection()
{
    var world = new WorldInstance("test", 10, 10);
    var owner = Guid.NewGuid();
    var other = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(owner, new GridPosition(1, 1));
    world.Join(other, new GridPosition(1, 2));
    world.SpawnMonster(monsterId, "slime", new GridPosition(2, 1), 1, 10, "slime_gel");
    var attack = world.Attack(owner, 1, monsterId, 5);
    var drop = attack.Drop!;

    Require(attack.Defeated && drop.ItemId == "slime_gel", "击杀应生成配置的掉落");
    Require(
        world.Pickup(other, 1, drop.DropId, 5).Failure == PickupFailure.Protected,
        "保护期内其他角色不能拾取");
    var publicPickup = world.Pickup(other, 2, drop.DropId, drop.OwnerProtectionUntilTick);
    Require(publicPickup.IsSuccess, "保护期结束后其他角色可以拾取");
    Require(world.InventorySnapshot(other).Single().ItemId == "slime_gel", "物品应进入拾取者背包");
    return Task.CompletedTask;
}

static async Task PickupExecutesOnWorldTick()
{
    var world = new WorldInstance("test", 10, 10);
    var owner = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(owner, new GridPosition(1, 1));
    world.SpawnMonster(monsterId, "slime", new GridPosition(2, 1), 1, 10, "slime_gel");
    var attack = world.Attack(owner, 1, monsterId, 1);
    var simulation = new WorldSimulation(world);

    var pending = simulation.QueuePickupAsync(owner, 2, attack.Drop!.DropId).AsTask();
    Require(!pending.IsCompleted, "拾取不应绕过世界帧");
    simulation.Tick();
    var result = await pending;
    Require(result.IsSuccess && result.InventoryItem?.Quantity == 1, "世界帧应完成拾取并更新背包");
    Require(world.DropSnapshot().Count == 0, "已拾取掉落应从地面移除");
}

static async Task InventorySnapshotRejectsStaleSave()
{
    var store = new InMemoryCharacterInventoryStore();
    var characterId = Guid.NewGuid();
    var savedAt = new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
    var latest = new CharacterInventorySnapshot(
        characterId,
        [new InventoryItem("wooden_token", 2)],
        savedAt);
    var stale = new CharacterInventorySnapshot(
        characterId,
        [new InventoryItem("wooden_token", 1)],
        savedAt.AddSeconds(-1));

    Require(await store.SaveAsync(latest, CancellationToken.None), "最新背包应保存成功");
    Require(!await store.SaveAsync(stale, CancellationToken.None), "旧背包不能覆盖新版本");
    var loaded = await store.LoadAsync(characterId, CancellationToken.None);
    Require(loaded?.Items.Single().Quantity == 2, "应读回最新背包数量");
}

static Task MonsterAttacksAndKillsAdjacentPlayer()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(Guid.NewGuid(), "wolf", new GridPosition(2, 1), 1, 20);

    for (var tick = WorldInstance.MonsterBehaviorIntervalTicks;
         tick <= 24 * WorldInstance.MonsterBehaviorIntervalTicks;
         tick += WorldInstance.MonsterBehaviorIntervalTicks)
    {
        world.Advance(tick);
    }

    var dead = world.FindPlayer(characterId)!;
    Require(!dead.IsAlive && dead.RespawnAtTick == 540, "连续怪物攻击应使玩家死亡并安排复活");
    Require(world.Move(characterId, 1, new GridPosition(1, 2)).Failure == MoveFailure.PlayerDead, "死亡玩家不能移动");
    return Task.CompletedTask;
}

static Task DeadPlayerRespawnsOnSchedule()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(Guid.NewGuid(), "wolf", new GridPosition(2, 1), 1, 20);
    for (var tick = 20; tick <= 480; tick += 20)
    {
        world.Advance(tick);
    }

    Require(world.Advance(539) == 0, "复活帧之前应保持死亡");
    Require(world.Advance(540) == 1, "到达复活帧应恢复玩家");
    var player = world.FindPlayer(characterId)!;
    Require(player.IsAlive && player.Health == player.MaximumHealth, "复活后应恢复满血");
    Require(player.Position == new GridPosition(1, 1), "复活后应回到进入世界的位置");
    return Task.CompletedTask;
}

static Task GroundDropExpires()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    var monsterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.SpawnMonster(monsterId, "slime", new GridPosition(2, 1), 1, 10, "slime_gel");
    var attack = world.Attack(characterId, 1, monsterId, 1);

    Require(world.Advance(attack.Drop!.ExpiresAtTick - 1) >= 0, "过期前世界帧应正常推进");
    Require(world.DropSnapshot().Count == 1, "过期前掉落应保留");
    world.Advance(attack.Drop.ExpiresAtTick);
    Require(world.DropSnapshot().Count == 0, "到期掉落应被清理");
    Require(
        world.Pickup(characterId, 2, attack.Drop.DropId, attack.Drop.ExpiresAtTick).Failure
            == PickupFailure.DropNotFound,
        "过期掉落不能拾取");
    return Task.CompletedTask;
}

static Task ArchetypesHaveDistinctCombatProfiles()
{
    var world = new WorldInstance("test", 20, 20);
    var warriorId = Guid.NewGuid();
    var mageId = Guid.NewGuid();
    var assassinId = Guid.NewGuid();
    world.JoinAtAvailablePosition(warriorId, new GridPosition(1, 1), CharacterArchetype.Warrior);
    world.JoinAtAvailablePosition(mageId, new GridPosition(3, 1), CharacterArchetype.Mage);
    world.JoinAtAvailablePosition(assassinId, new GridPosition(5, 1), CharacterArchetype.Assassin);
    var warrior = world.FindPlayer(warriorId)!;
    var mage = world.FindPlayer(mageId)!;
    var assassin = world.FindPlayer(assassinId)!;

    Require(warrior.MaximumHealth > mage.MaximumHealth, "战士基础生命应高于法师");
    Require(mage.AttackPower > warrior.AttackPower, "法师基础攻击应高于战士");
    Require(assassin.AttackCooldownTicks < warrior.AttackCooldownTicks, "刺客基础攻击间隔应更短");
    Require(warrior.Defense > mage.Defense, "战士基础防御应高于法师");
    return Task.CompletedTask;
}

static Task EquipmentMovesFromInventoryAndAddsStats()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.ReplaceInventory(characterId,
        [new InventoryItem("wooden_sword", 1), new InventoryItem("cloth_armor", 1)]);
    var basePlayer = world.FindPlayer(characterId)!;
    var weapon = world.Equip(characterId, 1, "wooden_sword");
    var armor = world.Equip(characterId, 2, "cloth_armor");

    Require(weapon.IsSuccess && armor.IsSuccess, "背包内装备应可穿戴");
    Require(armor.Player.AttackPower == basePlayer.AttackPower + 3, "武器应增加攻击");
    Require(armor.Player.Defense == basePlayer.Defense + 2, "防具应增加防御");
    Require(armor.Player.MaximumHealth == basePlayer.MaximumHealth + 10, "防具应增加最大生命");
    Require(world.InventorySnapshot(characterId).Count == 0, "已穿戴物品应离开背包");
    Require(world.EquipmentSnapshot(characterId).Count == 2, "装备槽应记录已穿戴物品");
    return Task.CompletedTask;
}

static async Task EquipmentCommandsExecuteOnWorldTick()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.ReplaceInventory(characterId, [new InventoryItem("wooden_token", 1)]);
    var simulation = new WorldSimulation(world);
    var equip = simulation.QueueEquipAsync(characterId, 1, "wooden_token").AsTask();
    Require(!equip.IsCompleted, "装备不应绕过世界帧");
    simulation.Tick();
    Require((await equip).IsSuccess, "世界帧应完成装备");
    var unequip = simulation.QueueUnequipAsync(characterId, 2, EquipmentSlot.Accessory).AsTask();
    simulation.Tick();
    Require((await unequip).IsSuccess, "世界帧应完成卸下");
    Require(world.InventorySnapshot(characterId).Single().ItemId == "wooden_token", "卸下物品应回到背包");
}

static async Task EquipmentPersistsWithInventorySnapshot()
{
    var store = new InMemoryCharacterInventoryStore();
    var characterId = Guid.NewGuid();
    var savedAt = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
    await store.SaveAsync(
        new CharacterInventorySnapshot(
            characterId,
            [new InventoryItem("venison", 2)],
            savedAt,
            [new EquippedItem(EquipmentSlot.Weapon, "wooden_sword")]),
        CancellationToken.None);
    var snapshot = await store.LoadAsync(characterId, CancellationToken.None);
    var world = new WorldInstance("test", 10, 10);
    world.Join(characterId, new GridPosition(1, 1));
    world.ReplaceInventory(characterId, snapshot!.Items);
    world.ReplaceEquipment(characterId, snapshot.Equipment ?? []);

    Require(world.EquipmentSnapshot(characterId).Single().ItemId == "wooden_sword", "应恢复武器槽");
    Require(world.FindPlayer(characterId)!.AttackPower == 15, "加载装备后应重算攻击属性");
}

static Task SkillValidatesAuthorityAndResources()
{
    var world = new WorldInstance("test", 20, 20);
    var mage = Guid.NewGuid();
    var target = Guid.NewGuid();
    var distant = Guid.NewGuid();
    world.JoinAtAvailablePosition(mage, new GridPosition(1, 1), CharacterArchetype.Mage);
    world.SpawnMonster(target, "dummy", new GridPosition(5, 1), 1, 200);
    world.SpawnMonster(distant, "dummy", new GridPosition(15, 15), 1, 200);

    Require(world.CastSkill(mage, 1, "missing", target, 1).Failure == SkillFailure.UnknownSkill,
        "未知技能不能施放");
    Require(world.CastSkill(mage, 2, "power_strike", target, 1).Failure == SkillFailure.WrongArchetype,
        "不能使用其他职业技能");
    Require(world.CastSkill(mage, 3, "fireball", distant, 1).Failure == SkillFailure.OutOfRange,
        "超出技能距离不能施放");
    var first = world.CastSkill(mage, 4, "fireball", target, 1);
    Require(first.IsSuccess && first.Damage == 24 && first.Player.Mana == 48,
        "法师火球应由服务端结算伤害并消耗法力");
    Require(world.CastSkill(mage, 5, "fireball", target, 2).Failure == SkillFailure.Cooldown,
        "技能冷却期间不能再次施放");
    Require(world.CastSkill(mage, 4, "fireball", target, 25).Failure == SkillFailure.StaleSequence,
        "旧消息序号不能重放施法");
    Require(world.CastSkill(mage, 6, "fireball", target, 25).IsSuccess,
        "冷却结束后可以再次施放");
    Require(world.CastSkill(mage, 7, "fireball", target, 49).IsSuccess,
        "第三次施法应成功");
    Require(world.CastSkill(mage, 8, "fireball", target, 73).IsSuccess,
        "第四次施法应成功");
    Require(world.CastSkill(mage, 9, "fireball", target, 97).IsSuccess,
        "第五次施法应成功");
    Require(world.CastSkill(mage, 10, "fireball", target, 121).Failure == SkillFailure.InsufficientMana,
        "法力不足不能施放");
    Require(world.FindPlayer(mage)!.Mana == 0, "失败施法不得扣除更多法力");
    return Task.CompletedTask;
}

static async Task SkillExecutesOnWorldTickAndManaRegenerates()
{
    var world = new WorldInstance("test", 10, 10);
    var warrior = Guid.NewGuid();
    var monster = Guid.NewGuid();
    world.Join(warrior, new GridPosition(1, 1));
    world.SpawnMonster(monster, "dummy", new GridPosition(2, 1), 1, 100);
    var simulation = new WorldSimulation(world);
    var pending = simulation.QueueCastSkillAsync(warrior, 1, "power_strike", monster).AsTask();
    Require(!pending.IsCompleted && world.MonsterSnapshot().Single().Health == 100,
        "施法必须等待世界帧结算");
    simulation.Tick();
    var result = await pending;
    Require(result.IsSuccess && result.Damage == 20 && result.Player.Mana == 22,
        "世界帧应结算技能伤害和法力");
    world.Advance(WorldInstance.ManaRegenerationIntervalTicks);
    Require(world.FindPlayer(warrior)!.Mana == 23, "固定帧应恢复一点法力");
}

static async Task ChatValidatesContentRateAndSequence()
{
    var world = new WorldInstance("test", 10, 10);
    var sender = Guid.NewGuid();
    world.Join(sender, new GridPosition(1, 1));
    var simulation = new WorldSimulation(world);
    var pending = simulation.QueueChatAsync(sender, 1, "  你好，世界  ").AsTask();
    Require(!pending.IsCompleted, "聊天应由世界帧处理");
    simulation.Tick();
    Require((await pending).Text == "你好，世界", "聊天内容应去除前后空白");
    Require(world.Chat(sender, 2, "重复", 2).Failure == ChatFailure.Cooldown,
        "聊天发送频率应受限");
    Require(world.Chat(sender, 2, "重放", 21).Failure == ChatFailure.StaleSequence,
        "失败消息的序号也不能重放");
    Require(world.Chat(sender, 3, new string('字', 121), 21).Failure == ChatFailure.InvalidText,
        "过长内容应被拒绝");
    Require(world.Chat(sender, 4, "bad\nline", 21).Failure == ChatFailure.InvalidText,
        "控制字符应被拒绝");
    Require(world.Chat(sender, 5, "再次发言", 21).IsSuccess,
        "冷却结束后可以继续发送合法消息");
}

static async Task ConsumablesRestoreResourcesWithoutDuplication()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.Join(characterId, new GridPosition(1, 1));
    world.ReplaceInventory(characterId,
        [new InventoryItem("venison", 2), new InventoryItem("mana_herb", 1)]);
    world.RestoreResources(characterId, 110, 20, 1);
    var simulation = new WorldSimulation(world);
    var pending = simulation.QueueUseItemAsync(characterId, 1, "venison").AsTask();
    Require(!pending.IsCompleted, "物品使用必须等待世界帧");
    simulation.Tick();
    var first = await pending;
    Require(first.IsSuccess && first.Player.Health == 120,
        "恢复生命不应超过职业上限");
    Require(first.Inventory.Single(item => item.ItemId == "venison").Quantity == 1,
        "只应扣除一件消耗品");
    Require(world.UseItem(characterId, 2, "venison").Failure == UseItemFailure.NoEffect,
        "生命已满时不应浪费道具");
    Require(world.InventorySnapshot(characterId).Single(item => item.ItemId == "venison").Quantity == 1,
        "无效果的使用不能扣除背包");
    var mana = world.UseItem(characterId, 3, "mana_herb");
    Require(mana.IsSuccess && mana.Player.Mana == 30,
        "法力药草应恢复法力并受上限约束");
    Require(mana.Inventory.All(item => item.ItemId != "mana_herb"),
        "最后一件消耗品应移出背包");
    Require(world.UseItem(characterId, 4, "mana_herb").Failure == UseItemFailure.ItemNotFound,
        "不能重复使用已耗尽的物品");
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

static async Task PendingDeletionBlocksWorldEntry()
{
    var repository = new InMemoryCharacterRepository();
    var service = new CharacterService(repository, new TestClock(DateTimeOffset.UtcNow));
    var accountId = Guid.NewGuid();
    var character = await service.CreateAsync(accountId, "entry_guard", CharacterArchetype.Warrior);
    var characterId = character.Value!.Id;
    Require(await repository.CanEnterWorldAsync(accountId, characterId, CancellationToken.None),
        "正常角色应允许进入世界");
    Require(!await repository.CanEnterWorldAsync(Guid.NewGuid(), characterId, CancellationToken.None),
        "票据账号必须仍拥有角色");
    await service.RequestDeletionAsync(accountId, characterId, "entry_guard");
    Require(!await repository.CanEnterWorldAsync(accountId, characterId, CancellationToken.None),
        "签发票据后申请删除仍应阻止连接");
    await service.CancelDeletionAsync(accountId, characterId);
    Require(await repository.CanEnterWorldAsync(accountId, characterId, CancellationToken.None),
        "撤销删除后允许重新进入");
}

static async Task OnlyDueCharactersArePurged()
{
    var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    var clock = new TestClock(now);
    var repository = new InMemoryCharacterRepository();
    var service = new CharacterService(repository, clock);
    var accountId = Guid.NewGuid();
    var due = await service.CreateAsync(accountId, "due_hero", CharacterArchetype.Warrior);
    var waiting = await service.CreateAsync(accountId, "wait_hero", CharacterArchetype.Mage);
    await service.RequestDeletionAsync(accountId, due.Value!.Id, "due_hero");
    clock.UtcNow = now.AddHours(12);
    await service.RequestDeletionAsync(accountId, waiting.Value!.Id, "wait_hero");

    var audits = await repository.PurgeDueDeletionsAsync(
        now.Add(CharacterService.DeletionDelay),
        100,
        CancellationToken.None);
    var remaining = await service.ListAsync(accountId);

    Require(audits.Count == 1 && audits[0].CharacterId == due.Value.Id, "只应清理已到期角色");
    Require(audits[0].Name == "due_hero" && audits[0].DeletedAt == now.AddHours(24), "审计应保留名称和删除时间");
    Require(remaining.Count == 1 && remaining[0].Id == waiting.Value.Id, "冷静期未满角色必须保留");
}

static async Task CharacterWorldStatePersistsLatestPosition()
{
    var repository = new InMemoryCharacterRepository();
    var service = new CharacterService(repository, new TestClock(DateTimeOffset.UtcNow));
    var accountId = Guid.NewGuid();
    var created = await service.CreateAsync(accountId, "traveler", CharacterArchetype.Assassin);
    var savedAt = new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);

    Require(
        await repository.SaveWorldStateAsync(
            new CharacterWorldState(created.Value!.Id, "local-field", 70, 71, savedAt, 52, 17),
            CancellationToken.None),
        "新世界状态应保存成功");
    Require(
        !await repository.SaveWorldStateAsync(
            new CharacterWorldState(created.Value.Id, "local-field", 1, 1, savedAt.AddSeconds(-1)),
            CancellationToken.None),
        "旧时间戳不应覆盖新世界状态");

    var loaded = await service.FindOwnedAsync(accountId, created.Value.Id);
    var latestState = await repository.LoadWorldStateAsync(created.Value.Id, CancellationToken.None);
    Require(loaded is not null, "应能读回角色");
    Require(loaded!.PositionX == 70 && loaded.PositionY == 71, "应读回最新权威坐标");
    Require(loaded.Health == 52 && loaded.Mana == 17, "应读回最新生命和法力");
    Require(loaded.WorldStateSavedAt == savedAt, "应记录最近保存时间");
    Require(latestState?.PositionX == 70 && latestState.Health == 52 && latestState.Mana == 17,
        "世界服应能读取最新状态而非沿用旧票据快照");
}

static Task RestoredCombatResourcesAreAuthoritative()
{
    var world = new WorldInstance("test", 10, 10);
    var characterId = Guid.NewGuid();
    world.JoinAtAvailablePosition(characterId, new GridPosition(1, 1), CharacterArchetype.Mage);
    var restored = world.RestoreResources(characterId, 23, 7, 10)!;
    Require(restored.Health == 23 && restored.Mana == 7, "重进时应恢复存档资源");
    Require(restored.MaximumHealth == 80 && restored.MaximumMana == 60,
        "职业基础上限仍由服务端决定");
    Require(world.RestoreResources(characterId, 999, 999, 11)!.Health == 80
        && world.FindPlayer(characterId)!.Mana == 60, "存档资源不能超过服务端上限");
    var dead = world.RestoreResources(characterId, 0, 2, 12)!;
    Require(!dead.IsAlive && dead.RespawnAtTick == 72,
        "死亡角色重进后应等待复活");
    Require(world.Join(Guid.NewGuid(), new GridPosition(1, 1)).IsSuccess,
        "死亡角色不应占用地图格子");
    return Task.CompletedTask;
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

static Task WorldTicketCarriesAuthoritativeSpawn()
{
    var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    var ticket = CreateTicketProtector().Issue(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "local",
        now,
        "local-field",
        77,
        78,
        CharacterArchetype.Assassin,
        42,
        9);
    var validation = CreateTicketProtector().Validate(ticket.Token, "local", now);
    Require(validation.IsSuccess, "带出生状态的票据应验证成功");
    Require(
        validation.Claims?.MapId == "local-field"
        && validation.Claims.PositionX == 77
        && validation.Claims.PositionY == 78
        && validation.Claims.Archetype == CharacterArchetype.Assassin
        && validation.Claims.Health == 42
        && validation.Claims.Mana == 9,
        "票据应携带服务端保存的地图、坐标、职业和资源");
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
