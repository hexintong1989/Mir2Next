# Mirage

Mirage 是一个从零实现、服务端权威的多人在线角色扮演游戏项目。项目不使用 V8M2，也不复用第三方商业引擎或受保护的服务端、登录器、客户端代码与资源。

当前里程碑 M1 聚焦最小账号边界：

- 注册账号
- PBKDF2-SHA256 密码哈希
- 登录认证
- 加密随机会话令牌
- 可执行规格测试

## 本地运行

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet build Mirage.sln
dotnet run --project tests\Mirage.Specs
dotnet run --project src\Mirage.AuthServer --urls http://127.0.0.1:5080
```

健康检查：`GET http://127.0.0.1:5080/health`

## PostgreSQL 模式

默认不设置连接字符串时使用内存仓储，适合本地快速开发。设置以下环境变量后，服务会切换到 PostgreSQL 仓储；不要把真实密码提交到配置文件。

```powershell
$env:ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=mirage;Username=mirage;Password=<password>"
$env:Database__ApplyMigrations="true"
dotnet run --project src\Mirage.AuthServer
```

生产环境应由部署流程执行迁移，并将 `Database__ApplyMigrations` 保持为 `false`。

设计边界与后续路线见 [SPEC.md](SPEC.md)、[ROADMAP.md](ROADMAP.md) 和 [DECISIONS.md](DECISIONS.md)。
