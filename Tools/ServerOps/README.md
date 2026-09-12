# 服务器运维脚本

本目录只保存不含凭据的操作脚本。MongoDB URI 可由 Secret Manager 注入当前进程的 `ET_MONGO_URI`，生产服务器也支持从 `ET_SECRET_ROOT` 下由 `ET_MONGO_URI_FILE` 指定的受限文件读取；不得把真实 URI 写入仓库。

## 登录与生产启动模式

本地试用账号密码登录必须显式开启开发模式：

```powershell
.\App.exe --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1
```

商业模式（`--Develop=0`）默认关闭密码登录，并要求 MongoDB URI 来自 Secret 注入且同时具备认证、有效 TLS、Replica Set/SRV/负载均衡部署。按区可使用 `ET_MONGO_URI_ZONE_1` 或 `ET_MONGO_URI_FILE_ZONE_1` 以及 `ET_MONGO_DATABASE_ZONE_1`；未按区配置时使用 `ET_MONGO_URI` 或 `ET_MONGO_URI_FILE`、`ET_MONGO_DATABASE`。同一层级的 URI 值和文件不能同时配置。

只有完整客户端链路已经通过 WSS/TLS 等方式加密时，才可同时设置 `--PasswordLoginEnabled=1 --PasswordLoginTransportSecure=1`。该参数是部署方安全确认，不会自动给 UDP/KCP 增加加密；原生 UDP 登录不得把它设置为 1。生产密码登录还必须设置应用目录外的 `ET_SECRET_ROOT`，并通过 `ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE` 挂载 RSA-4096 私钥。完整约束见 [020.6](../../Book/服务器上线升级/020.6-生产Secret部署契约与启动门禁.md)。

轮换时可用 `ET_ACCOUNT_PASSWORD_KEYRING_FILE` 替代单私钥文件，并用 `ET_METRICS_PREVIOUS_TOKEN_FILE` 提供指标 Token 重叠窗口；具体 JSON、部署顺序和回滚要求见 [020.7](../../Book/服务器上线升级/020.7-Secret旧新凭据重叠轮换.md)。禁止同时设置密码单私钥与 Key Ring。

## 备份

```powershell
$env:ET_MONGO_URI = "mongodb://user:password@host:27017/?authSource=admin"
.\Backup-Mongo.ps1 -Database ET -OutputDirectory D:\Backups\ET
```

## 恢复演练

恢复前先停止写入。默认不删除已有集合；灾难恢复需要覆盖数据时必须显式传入 `-DropExisting`，并通过 `-ConfirmDatabaseName` 再次确认目标库名。

```powershell
$env:ET_MONGO_URI = "mongodb://user:password@host:27017/?authSource=admin"
.\Restore-Mongo.ps1 -Database ET_DR_TEST -BackupPath D:\Backups\ET\ET-20260823T010203Z -ConfirmDatabaseName ET_DR_TEST
```

生产备份必须写入服务器外的加密存储，并定期在隔离库执行恢复演练。脚本成功退出只代表工具执行成功，不替代恢复后的账号、角色数量和登录抽查。

## 账号与角色一致性审计

安装 MongoDB Shell 后，可在迁移前、恢复后和正式发布前执行：

```powershell
$env:ET_MONGO_URI = "mongodb://user:password@host:27017/?authSource=admin&tls=true"
.\Audit-Mongo.ps1 -Database ET
```

审计检查重复登录名、一个账号多个角色、账号缺少默认角色和孤儿角色；发现问题时退出码非 0。脚本只读，不会自动删除或修复业务数据。

## 014 预发布验收

本机已配置的隔离环境与启停命令见 [LOCAL-ENV.md](LOCAL-ENV.md)。本机 CA 通过 `ET_MONGO_CA_FILE` 传入，凭据通过受限配置文件或环境变量传入子进程。

在干净工作区和真实预发布 MongoDB Replica Set 上执行统一验收入口。目标恢复库必须与源库不同、以 `_DR_TEST` 结尾，并再次精确确认；脚本会覆盖该隔离恢复库，禁止填写生产库名。

```powershell
$env:ET_MONGO_URI = "mongodb://user:password@node1,node2,node3/?authSource=admin&tls=true&replicaSet=rs0&retryWrites=true"
.\Test-Preproduction.ps1 `
  -Database ET_PREPRODUCTION `
  -RestoreDatabase ET_PREPRODUCTION_DR_TEST `
  -ConfirmRestoreDatabase ET_PREPRODUCTION_DR_TEST `
  -SourceWritesStopped `
  -BackupOutputDirectory D:\Backups\ET-Acceptance `
  -EvidenceOutputDirectory D:\Evidence\ET-Acceptance
```

该入口执行第一阶段回归、MongoDB 认证/拓扑/会话检查、事务提交与回滚、索引及账号角色审计、备份、隔离恢复和恢复数据核对。证据 JSON 不包含连接串。MongoDB 切主、网络故障、多客户端重连和负载测试依赖具体部署平台，必须另外执行并附证据，不能由此脚本的 PASS 替代。

数据库范围备份须先停写源库，再传 `-SourceWritesStopped`；核验比较文档数量、全部文档内容摘要和索引。此开关是操作者确认，不会自动停止 ET 服务。
