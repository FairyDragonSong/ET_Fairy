# 服务器运维脚本

本目录只保存不含凭据的操作脚本。MongoDB URI 必须通过当前进程的 `ET_MONGO_URI` 环境变量注入。

## 登录与生产启动模式

本地试用账号密码登录必须显式开启开发模式：

```powershell
.\App.exe --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1
```

商业模式（`--Develop=0`）默认关闭密码登录，并要求 MongoDB URI 来自环境变量且同时具备认证、有效 TLS、Replica Set/SRV/负载均衡部署。按区可使用 `ET_MONGO_URI_ZONE_1`、`ET_MONGO_DATABASE_ZONE_1`；未按区配置时使用 `ET_MONGO_URI`、`ET_MONGO_DATABASE`。

只有完整客户端链路已经通过 WSS/TLS 等方式加密时，才可同时设置 `--PasswordLoginEnabled=1 --PasswordLoginTransportSecure=1`。该参数是部署方安全确认，不会自动给 UDP/KCP 增加加密；原生 UDP 登录不得把它设置为 1。生产凭据应由 Secret Manager 注入，不应写入仓库、启动脚本或日志。

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

在干净工作区和真实预发布 MongoDB Replica Set 上执行统一验收入口。目标恢复库必须与源库不同、以 `_DR_TEST` 结尾，并再次精确确认；脚本会覆盖该隔离恢复库，禁止填写生产库名。

```powershell
$env:ET_MONGO_URI = "mongodb://user:password@node1,node2,node3/?authSource=admin&tls=true&replicaSet=rs0&retryWrites=true"
.\Test-Preproduction.ps1 `
  -Database ET_PREPRODUCTION `
  -RestoreDatabase ET_PREPRODUCTION_DR_TEST `
  -ConfirmRestoreDatabase ET_PREPRODUCTION_DR_TEST `
  -BackupOutputDirectory D:\Backups\ET-Acceptance `
  -EvidenceOutputDirectory D:\Evidence\ET-Acceptance
```

该入口执行第一阶段回归、MongoDB 认证/拓扑/会话检查、事务提交与回滚、索引及账号角色审计、备份、隔离恢复和恢复数据核对。证据 JSON 不包含连接串。MongoDB 切主、网络故障、多客户端重连和负载测试依赖具体部署平台，必须另外执行并附证据，不能由此脚本的 PASS 替代。
