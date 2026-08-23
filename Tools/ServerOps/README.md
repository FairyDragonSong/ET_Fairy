# 服务器运维脚本

本目录只保存不含凭据的操作脚本。MongoDB URI 必须通过当前进程的 `ET_MONGO_URI` 环境变量注入。

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
