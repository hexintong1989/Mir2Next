# 实时世界协议 v1

生产形态入口：`ws://127.0.0.1:5090/ws/v1/world?ticket=<一次性票据>`。认证服验证账号对角色的所有权后，通过 `POST /api/v1/characters/{characterId}/entry-ticket` 签发30秒票据。

## 连接

Development 环境另有 `/ws/v1/world/dev`，查询参数为 `characterId`、`x`、`y`，仅用于底层调试。正式入口不接受客户端指定角色、出生点、等级或经验。票据绑定账号、角色、职业、区服、地图、存档坐标、战斗资源及成长状态，采用HMAC-SHA256签名，并在世界服首次兑换后立即失效；进入世界前以数据库最新存档为准。

服务端首先发送 `welcome`，随后发送 `visibilityDelta` 初始视野。

## 客户端消息

```json
{"type":"move","sequence":1,"x":11,"y":10}
```

```json
{"type":"attack","sequence":2,"monsterId":"10000000-0000-0000-0000-000000000001"}
```

```json
{"type":"pickup","sequence":3,"dropId":"00000000-0000-0000-0000-000000000001"}
```

```json
{"type":"equip","sequence":4,"itemId":"wooden_token"}
{"type":"unequip","sequence":5,"slot":"Accessory"}
```

```json
{"type":"castSkill","sequence":6,"skillId":"power_strike","monsterId":"10000000-0000-0000-0000-000000000001"}
```

```json
{"type":"chat","sequence":7,"text":"大家好"}
```

```json
{"type":"useItem","sequence":8,"itemId":"venison"}
```

```json
{"type":"partyInvite","sequence":9,"targetCharacterId":"00000000-0000-0000-0000-000000000002"}
{"type":"partyAccept","sequence":10,"inviterCharacterId":"00000000-0000-0000-0000-000000000001"}
{"type":"partyLeave","sequence":11}
{"type":"partyChat","sequence":12,"text":"准备出发"}
```

`sequence` 由移动、攻击、拾取、装备、技能、聊天、物品使用和组队操作共用，对每个角色必须单调递增。基础攻击由服务端检查目标存在、近战距离及攻击间隔。技能由服务端检查职业、等级、距离、法力及该技能的冷却。消息必须是 UTF-8 JSON 文本且不超过 4096 字节。

## 服务端消息

- `welcome`：协议版本、地图、自身状态及该职业可用技能。
- `visibilityDelta`：`entered`、`updated`、`left` 三组 AOI 增量。
- `moveRejected`：非法移动的原因和当前权威位置。
- `monsterSnapshot`：观察者当前视野内的怪物完整列表。
- `monsterUpdated`：怪物受伤后的权威血量、伤害和死亡状态。
- `attackRejected`：攻击因目标、距离、冷却或序号不合法而被拒绝。
- `skillResult` / `skillRejected`：技能伤害、施法后法力与角色状态，或拒绝原因。
- `progression`：击杀所得经验、提升等级数和升级后的完整权威角色状态。
- `chatMessage` / `chatRejected`：带服务端角色ID和时间戳的同图视野聊天，或拒绝原因。
- `itemUsed` / `itemUseRejected`：使用消耗品后的权威生命、法力及背包快照，或拒绝原因。
- `partyInviteSent` / `partyInvite`：邀请确认，或向指定在线目标投递带过期帧的邀请。
- `partyUpdated` / `partyRejected`：队伍成员、队长变化后的完整快照，或组队操作拒绝原因。
- `partyChatMessage` / `partyChatRejected`：投递给全部在线队员的队伍频道消息，或拒绝原因。
- `dropSnapshot`：视野内地面物品、击杀者保护截止帧及物品过期帧。
- `inventorySnapshot`：角色进入世界时的服务端背包快照。
- `pickupResult` / `pickupRejected`：拾取后的堆叠数量或拒绝原因。
- `equipmentSnapshot`：进入世界时的武器、防具和饰品槽快照。
- `equipmentChanged` / `equipmentRejected`：装备变更后的权威属性、背包和槽位，或拒绝原因。
- `protocolError`：JSON、消息类型或帧类型错误。

所有位置均以服务端状态为准。客户端只能发送移动意图，不得直接设置最终状态。
怪物快照也会由世界服约每秒主动同步，用于呈现自动移动和死亡后的刷新。
玩家位置状态包含职业、等级、累计经验、下一级阈值、当前/最大生命、当前/最大法力、攻击、防御、攻击间隔、存活状态及复活保护截止帧；死亡角色的操作会被相应拒绝。法力每20帧恢复1点，复活时恢复满值。角色等级上限为60，击杀经验和升级后的职业属性均由服务端结算。

各职业1级基础技能分别为战士 `power_strike`、法师 `fireball`、道士 `soul_talisman`、刺客 `shadow_strike`；5级技能分别为 `whirlwind`、`lightning_bolt`、`poison_talisman`、`blade_flurry`。`welcome` 会返回技能的 `minimumLevel` 与当前 `unlocked` 状态，未达到门槛时施法返回 `LevelTooLow`。

聊天单条最多120个UTF-16字符，禁止控制字符，同一角色至少间隔20帧（1秒），只广播给12格AOI内的玩家。
首批消耗品：`venison` 恢复25点生命，`mana_herb` 恢复15点法力；资源已满时不消耗物品。

组队最多5人，邀请有效600帧（30秒）；已有队伍时仅队长可邀请。队长离队或断线时按加入顺序转移，队伍不足2人自动解散。队伍聊天不受AOI距离限制，但与本地聊天共用1秒发送冷却和文本安全规则。击杀怪物时，存活、在线且位于怪物12格内的队员参与经验分配；每增加一名有效成员增加10%总经验，再按有效人数均分，服务端向每名获奖角色分别发送 `progression` 并即时保存成长状态。组队状态当前属于世界服在线会话，不跨服务重启保存。
