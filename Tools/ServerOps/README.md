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
