# 实时世界协议 v1

生产形态入口：`ws://127.0.0.1:5090/ws/v1/world?ticket=<一次性票据>`。认证服验证账号对角色的所有权后，通过 `POST /api/v1/characters/{characterId}/entry-ticket` 签发30秒票据。

## 连接

Development 环境另有 `/ws/v1/world/dev`，查询参数为 `characterId`、`x`、`y`，仅用于底层调试。正式入口不接受客户端指定角色和出生点。票据绑定账号、角色、区服、地图与存档坐标，采用HMAC-SHA256签名，并在世界服首次兑换后立即失效。

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

`sequence` 由移动与攻击共用，对每个角色必须单调递增。基础攻击由服务端检查目标存在、近战距离、10帧冷却并结算伤害。消息必须是 UTF-8 JSON 文本且不超过 4096 字节。

## 服务端消息

- `welcome`：协议版本、地图与自身状态。
- `visibilityDelta`：`entered`、`updated`、`left` 三组 AOI 增量。
- `moveRejected`：非法移动的原因和当前权威位置。
- `monsterSnapshot`：观察者当前视野内的怪物完整列表。
- `monsterUpdated`：怪物受伤后的权威血量、伤害和死亡状态。
- `attackRejected`：攻击因目标、距离、冷却或序号不合法而被拒绝。
- `dropSnapshot`：视野内地面物品及其击杀者保护截止帧。
- `inventorySnapshot`：角色进入世界时的服务端背包快照。
- `pickupResult` / `pickupRejected`：拾取后的堆叠数量或拒绝原因。
- `protocolError`：JSON、消息类型或帧类型错误。

所有位置均以服务端状态为准。客户端只能发送移动意图，不得直接设置最终状态。
怪物快照也会由世界服约每秒主动同步，用于呈现自动移动和死亡后的刷新。
