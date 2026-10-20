# 当前状态

更新时间：2026-10-20

## 已完成

- 创建独立 `Mirage` 解决方案与 Git 仓库。
- 确定 .NET 8 服务端技术基线。
- 建立 Shared、Domain、AuthServer 与 Specs 项目。
- 实现账号注册、PBKDF2-SHA256 密码哈希、登录与随机会话令牌。
- 领域规格测试 10/10 通过，解决方案构建为 0 警告、0 错误。
- HTTP 冒烟测试通过：健康检查 200、注册 201、登录 200、重复账号 409。
- 实现 Bearer 会话验证、注销、过期清理与按来源地址登录限速。
- 添加 PostgreSQL 账号及会话表的首个迁移脚本。
- 会话接口冒烟测试通过：校验 200、注销 204、注销后校验 401。
- 实现受会话保护的角色创建与列表、全服名称唯一、每账号四角色上限。
- 增加四职业协议枚举、首版区服目录接口和角色表迁移。
- 角色接口冒烟测试通过：未认证 401、创建 201、列表 200、区服目录 200。

## 下一步

- 为账号与会话引入 PostgreSQL 持久化和迁移。
- 接入 PostgreSQL 驱动并实现账号、会话仓储。
- 实现角色选择票据与删除保护。
- 拆分独立世界服并建立首个地图实例。

## 恢复工作

```powershell
Set-Location D:\MirServer-01\Mir2Next
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet build Mirage.sln
dotnet run --project tests\Mirage.Specs
```

旧目录仅作为用户提供的历史资料保留，不作为新项目依赖。
