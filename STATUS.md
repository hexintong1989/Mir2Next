# 当前状态

更新时间：2026-10-20

## 已完成

- 创建独立 `Mirage` 解决方案与 Git 仓库。
- 确定 .NET 8 服务端技术基线。
- 建立 Shared、Domain、AuthServer 与 Specs 项目。
- 实现账号注册、PBKDF2-SHA256 密码哈希、登录与随机会话令牌。
- 领域规格测试 5/5 通过，解决方案构建为 0 警告、0 错误。
- HTTP 冒烟测试通过：健康检查 200、注册 201、登录 200、重复账号 409。

## 下一步

- 为账号与会话引入 PostgreSQL 持久化和迁移。
- 增加会话验证、吊销与速率限制。
- 开始角色创建和区服目录协议。

## 恢复工作

```powershell
Set-Location D:\MirServer-01\Mir2Next
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet build Mirage.sln
dotnet run --project tests\Mirage.Specs
```

旧目录仅作为用户提供的历史资料保留，不作为新项目依赖。
