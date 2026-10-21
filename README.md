# Mirage

Mirage 是一个从零实现、服务端权威的多人在线角色扮演游戏项目。项目不使用第三方商业引擎或受保护的服务端、登录器、客户端代码与资源。

当前服务端已具备账号、角色和首版实时世界闭环：

- 注册账号
- PBKDF2-SHA256 密码哈希
- 登录认证
- 加密随机会话令牌
- 可执行规格测试
- 角色创建、列表和带 24 小时冷静期的删除保护
- 一次性角色选择票据与 WebSocket 权威移动

角色删除采用软删除申请。已登录账号需向
`POST /api/v1/characters/{characterId}/deletion` 提交
`{"confirmationName":"角色名"}`，待删除角色不能签发世界票据；冷静期内可调用
`DELETE /api/v1/characters/{characterId}/deletion` 撤销。

## 本地运行

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet build Mirage.sln
dotnet run --project tests\Mirage.Specs
dotnet run --project src\Mirage.AuthServer --urls http://127.0.0.1:5080
dotnet run --project src\Mirage.WorldServer --urls http://127.0.0.1:5090
```

健康检查：认证服 `GET http://127.0.0.1:5080/health`，世界服 `GET http://127.0.0.1:5090/health`。

## PostgreSQL 模式

默认不设置连接字符串时使用内存仓储，适合本地快速开发。设置以下环境变量后，服务会切换到 PostgreSQL 仓储；不要把真实密码提交到配置文件。

```powershell
$env:ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=mirage;Username=mirage;Password=<password>"
$env:Database__ApplyMigrations="true"
dotnet run --project src\Mirage.AuthServer
```

生产环境应由部署流程执行迁移，并将 `Database__ApplyMigrations` 保持为 `false`。

## 管理员安全审计

认证服会记录账号注册、登录、注销及认证限流事件，但不会保存密码或会话令牌。审计查询默认关闭；部署时通过密钥显式启用：

```powershell
$env:AdminAudit__ApiKey="<至少32字符的随机密钥>"
```

管理员使用请求头 `X-Admin-Audit-Key` 调用
`GET /api/v1/admin/security-events`。可选查询参数为 `accountId`、`eventType`、`before` 和 `limit`，其中 `limit` 范围为 1 至 200。未配置密钥时接口返回 503，密钥错误时返回 401。审计来源地址直接取服务器连接信息；只有在部署层正确配置受信任代理后，才应转发客户端地址。

设计边界与后续路线见 [SPEC.md](SPEC.md)、[ROADMAP.md](ROADMAP.md)、[PROTOCOL.md](PROTOCOL.md) 和 [DECISIONS.md](DECISIONS.md)。
