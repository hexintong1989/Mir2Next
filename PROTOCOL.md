# 实时世界协议 v1

当前开发入口：`ws://127.0.0.1:5090/ws/v1/world`。在角色选择票据完成前，该入口仅在 Development 环境启用。

## 连接

开发查询参数：`characterId`、`x`、`y`。生产连接将改为一次性角色选择票据，客户端不能自行指定角色和出生点。

服务端首先发送 `welcome`，随后发送 `visibilityDelta` 初始视野。

## 客户端消息

```json
{"type":"move","sequence":1,"x":11,"y":10}
```

`sequence` 对每个角色必须单调递增。消息必须是 UTF-8 JSON 文本且不超过 4096 字节。

## 服务端消息

- `welcome`：协议版本、地图与自身状态。
- `visibilityDelta`：`entered`、`updated`、`left` 三组 AOI 增量。
- `moveRejected`：非法移动的原因和当前权威位置。
- `protocolError`：JSON、消息类型或帧类型错误。

所有位置均以服务端状态为准。客户端只能发送移动意图，不得直接设置最终状态。
