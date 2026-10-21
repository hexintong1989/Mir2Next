# 实时世界协议 v1

生产形态入口：`ws://127.0.0.1:5090/ws/v1/world?ticket=<一次性票据>`。认证服验证账号对角色的所有权后，通过 `POST /api/v1/characters/{characterId}/entry-ticket` 签发30秒票据。

## 连接

Development 环境另有 `/ws/v1/world/dev`，查询参数为 `characterId`、`x`、`y`，仅用于底层调试。正式入口不接受客户端指定角色和出生点。票据绑定账号、角色、职业、区服、地图、存档坐标及战斗资源，采用HMAC-SHA256签名，并在世界服首次兑换后立即失效；进入世界前以数据库最新存档为准。

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

`sequence` 由移动、攻击、拾取、装备、技能和聊天共用，对每个角色必须单调递增。基础攻击由服务端检查目标存在、近战距离及攻击间隔。技能由服务端检查职业、距离、法力及该技能的冷却。消息必须是 UTF-8 JSON 文本且不超过 4096 字节。

## 服务端消息

- `welcome`：协议版本、地图、自身状态及该职业可用技能。
- `visibilityDelta`：`entered`、`updated`、`left` 三组 AOI 增量。
- `moveRejected`：非法移动的原因和当前权威位置。
- `monsterSnapshot`：观察者当前视野内的怪物完整列表。
- `monsterUpdated`：怪物受伤后的权威血量、伤害和死亡状态。
- `attackRejected`：攻击因目标、距离、冷却或序号不合法而被拒绝。
- `skillResult` / `skillRejected`：技能伤害、施法后法力与角色状态，或拒绝原因。
- `chatMessage` / `chatRejected`：带服务端角色ID和时间戳的同图视野聊天，或拒绝原因。
- `dropSnapshot`：视野内地面物品、击杀者保护截止帧及物品过期帧。
- `inventorySnapshot`：角色进入世界时的服务端背包快照。
- `pickupResult` / `pickupRejected`：拾取后的堆叠数量或拒绝原因。
- `equipmentSnapshot`：进入世界时的武器、防具和饰品槽快照。
- `equipmentChanged` / `equipmentRejected`：装备变更后的权威属性、背包和槽位，或拒绝原因。
- `protocolError`：JSON、消息类型或帧类型错误。

所有位置均以服务端状态为准。客户端只能发送移动意图，不得直接设置最终状态。
怪物快照也会由世界服约每秒主动同步，用于呈现自动移动和死亡后的刷新。
玩家位置状态包含职业、当前/最大生命、当前/最大法力、攻击、防御、攻击间隔、存活状态及复活保护截止帧；死亡角色的操作会被相应拒绝。法力每20帧恢复1点，复活时恢复满值。首批技能：战士 `power_strike`、法师 `fireball`、道士 `soul_talisman`、刺客 `shadow_strike`。

聊天单条最多120个UTF-16字符，禁止控制字符，同一角色至少间隔20帧（1秒），只广播给12格AOI内的玩家。
