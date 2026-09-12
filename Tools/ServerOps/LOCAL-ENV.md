# 本机 014 验收环境

安装目录：`D:\Program Files\ET\_Env`。使用 PowerShell 7 (`pwsh`) 运行。原有 `D:\Program Files\MongoDB\Server\5.0` 服务和数据保留。

## 布局与连接

- `server`：MongoDB Community 8.0.32。
- `shell`：mongosh 2.10.0。
- `tools`：Database Tools 100.18.0。
- `downloads`、`packages.json`：官方安装包和固定 SHA-256。
- `mongo-local`：三个节点的数据/日志、配置、随机凭据、内部 keyFile、私有 CA 和服务器证书。ACL 只允许当前用户与 SYSTEM。
- 副本集 `et-local-rs`，只监听 IPv4/IPv6 回环 `127.0.0.1`、`::1` 的 27101–27103 端口；成员地址为 `localhost`。
- 源测试库 `ET_LOCAL_ACCEPTANCE`，恢复库 `ET_LOCAL_ACCEPTANCE_DR_TEST`。
- 测试管理账号 `etlocal` 仅用于本机隔离环境，拥有测试所需管理权限；不是生产应用账号模板。

下载来源：[Server 官方元数据](https://downloads.mongodb.org/current.json)、[Shell 官方发布](https://github.com/mongodb-js/mongosh/releases/tag/v2.10.0)、[Database Tools 官方元数据](https://downloads.mongodb.org/tools/db/release.json)。安装脚本锁定版本和摘要，不随远端更新自动升级。

## 日常启动和连接

从仓库根目录执行：

```powershell
# 首次部署才执行 Install 和 Initialize，已经部署过不要再次 Initialize。
.\Tools\ServerOps\Install-LocalMongo.ps1
.\Tools\ServerOps\LocalMongo.ps1 -Action Initialize

# 日常使用：启动节点，并只向当前 PowerShell 注入 PATH 和连接配置。
. .\Tools\ServerOps\LocalMongo.ps1 -Action Start
.\Tools\ServerOps\LocalMongo.ps1 -Action Status

# 安全连接，不在命令行展开 URI。
mongosh --nodb --file .\Tools\ServerOps\Connect-Mongo.js

# 停止节点，保留数据；默认不注册开机启动服务。
.\Tools\ServerOps\LocalMongo.ps1 -Action Stop
```

`Use` 只加载当前会话的环境变量。工具路径不永久写入系统 PATH；密码和私钥不进入仓库。服务器证书一年有效，到期需在维护窗口更新。本机同盘三进程可以验证选举和进程故障，不能证明跨机器/机房容灾。

## ET 连接及验收

`Start-LocalRealmSmoke.ps1` 使用 `ET_MONGO_CA_FILE` 指定本环境 CA，由 ET 的 .NET 驱动验证证书链、有效期、用途和主机名，不依赖 Windows 用户信任是否保留，也不新增全局信任。私有 CA 模式不查询吊销列表，生产证书吊销与轮换策略需另行配置。CA 私钥生成后不保存。

```powershell
.\Tools\ServerOps\Test-Phase1.ps1
.\Tools\ServerOps\Start-LocalRealmSmoke.ps1
. .\Tools\ServerOps\LocalMongo.ps1 -Action Use
# 仅对空的本机测试库生成一次 20 组假数据；后续验收直接复用。
mongosh --nodb --quiet --file .\Tools\ServerOps\Connect-Mongo.js --file .\Tools\ServerOps\Seed-LocalMongo.js

# 必须先停掉针对源库的 ET 写入进程；本例 Realm 冒烟会自行退出。
.\Tools\ServerOps\Test-Preproduction.ps1 `
  -Database ET_LOCAL_ACCEPTANCE `
  -RestoreDatabase ET_LOCAL_ACCEPTANCE_DR_TEST `
  -ConfirmRestoreDatabase ET_LOCAL_ACCEPTANCE_DR_TEST `
  -SourceWritesStopped `
  -BackupOutputDirectory 'D:\Program Files\ET\_Env\mongo-local\backups' `
  -EvidenceOutputDirectory 'D:\Program Files\ET\_Env\mongo-local\evidence'

# 故障演练，只允许 et-local-rs。
mongosh --nodb --quiet --file .\Tools\ServerOps\Connect-Mongo.js --file .\Tools\ServerOps\Test-LocalMongoFailover.js

# 完整本机数据库故障/安全演练，要求已完成上述隔离恢复。
.\Tools\ServerOps\Test-LocalMongoRecovery.ps1
```

`Crash` 操作仅用于故障演练：强制终止本环境三个 mongod，然后用 `Start` 重启并执行恢复核对。不要对有效业务数据执行故障演练。

故障/安全演练会临时移除并恢复**本环境 CA** 在当前用户的信任，验证使用错误 CA 时连接失败；随后切主、强制重启三个节点，验证数据保持一致。最后改动并还原隔离恢复库的一个假数据字段，验证数量不变的内容损坏也会被检出。结果保存在 `mongo-local\evidence\local-recovery.json`。

备份工具使用受限临时配置文件传入 URI，调用后删除该文件。恢复目标必须再次确认，允许覆盖的 `_DR_TEST` 测试库仍需由操作者确认没有有效业务数据。

真实协议客户端验收入口为 `Test-Gameplay.ps1`，覆盖 Router、登录、地图、房间重连、服务重启、数据库中断/恢复、保存失败退出和 20 并发流程；`Test-RealmTls.ps1` 验证 .NET 正确/错误 CA。详情见 `Book/服务器上线升级/014.1-真实游戏链路验收.md`。这些测试使用独立的 `ET_LOCAL_GAMEPLAY` 库，故障测试会停止本机测试副本集，不要与其他测试并行执行。网络分区、目标在线量和生产容量验收仍须补充。
