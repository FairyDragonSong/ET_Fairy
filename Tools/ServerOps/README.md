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

## 网络绑定与 Windows URL ACL

生产 RouterManager 和明文 Router WebSocket 只允许绑定回环地址，并由同机 TLS/WSS 入口代理对外服务。分别设置 `ET_ROUTER_MANAGER_BIND_IP=127.0.0.1` 和（启用 WebSocket 时）`ET_ROUTER_WEBSOCKET_BIND_IP=127.0.0.1`。公网 Router UDP/TCP 还要求显式 OuterIP、私网 InnerIP，并设置 `ET_ROUTER_PUBLIC_EXPOSURE_ACK=firewall-ddos-rate-limit-v1`；该确认值不替代真实网络控制证据。

只读审计和提升权限后的迁移示例：

```powershell
.\Set-ServerHttpUrlAcl.ps1 -Action Audit -Port 30300 -Path '/'
.\Set-ServerHttpUrlAcl.ps1 -Action Apply -Port 30300 -Path '/' `
  -ServiceIdentity '<ET服务账号>' -ConfirmUrl 'http://127.0.0.1:30300/'
```

每个启用的 WebSocket 端口使用 `/et/` 路径单独迁移。完整边界与验收项见 [020.8](../../Book/服务器上线升级/020.8-网络暴露面与入口防护门禁.md)。

## 签名灰度与回滚决策

新发布包包含数据库最低/最高可读版本、迁移目标、协议摘要和滚动兼容声明。先使用 `Test-ServerReleaseCompatibility.ps1` 对当前包与候选包分别验签，再由 `New-ServerRolloutPlan.ps1` 使用两个不同的仓库外审批密钥生成双签、限时、不可覆盖的灰度计划。审批密钥不得复用发布制品签名密钥。

每波监控观察必须由独立监控身份签名，再交给 `Get-ServerRolloutDecision.ps1`。退出码 0 表示 PROMOTE，2 表示 HOLD，3 表示 ROLLBACK，1 表示证据或执行错误。部署控制器只能根据已验证计划操作其中绑定的版本和哈希；完整参数、迁移顺序与生产边界见 [020.9](../../Book/服务器上线升级/020.9-签名灰度计划与回滚决策门禁.md)。

开发机可运行端到端自检；它会生成一次性签名包和密钥，并在 finally 删除整个专用临时目录：

```powershell
.\Test-ServerRollout.ps1
```

## 签名紧急停服

服务器可通过 `ET_EMERGENCY_STOP_FILE`、`ET_EMERGENCY_STOP_TRUST_FILE` 和 `ET_DEPLOYMENT_ID` 启用签名停服监测。生产信任表必须在 `ET_SECRET_ROOT` 下，命令文件必须位于应用目录外的受限控制目录。使用仓库外事故响应私钥生成命令：

```powershell
.\New-EmergencyStopCommand.ps1 -PrivateKeyFile '<仓库外私钥>' -KeyId incident-primary `
  -DeploymentId prod-cn-1 -ReasonCode security-incident -ValidMinutes 60 `
  -ConfirmDeploymentId prod-cn-1 -OutputFile 'D:\ET-Control\prod-cn-1\emergency-stop.json'
```

合法启动前命令和运行中优雅排空成功均退出 4；排空失败仍退出 2；配置、信任或启动命令无效退出 3。服务管理器不得对 2/3/4 无条件重启。完整信任 JSON、验证边界和恢复风险见 [020.10](../../Book/服务器上线升级/020.10-签名紧急停服与优雅排空.md)。本机自检：

```powershell
.\Test-EmergencyStop.ps1
```

## 不可变服务器槽位

使用受信发布公钥将已签名、干净来源的发布目录安装到仓库外 DeploymentRoot。解析后的根路径必须再次精确确认，已有槽位永不覆盖：

```powershell
.\Install-ServerReleaseSlot.ps1 -ReleaseDirectory '<签名发布目录>' `
  -DeploymentRoot 'D:\ET-Deployment' -ConfirmDeploymentRoot 'D:\ET-Deployment' `
  -TrustedPublicKeyFile '<受信发布公钥>' -ExpectedSigningKeyId '<Key ID>' `
  -ExpectedPackageSupplier '<发行组织>' -ExpectedSbomNamespaceBase 'https://<受信命名空间>'
```

监督器或人工启动前必须调用 `Test-InstalledServerSlot.ps1`，不能只检查原 ZIP。槽位结构、逐文件验证、ACL 与回滚边界见 [020.11](../../Book/服务器上线升级/020.11-不可变签名发布槽位.md)。槽位测试已经包含在 `Test-ServerRollout.ps1` 中。

## 签名部署状态

使用独立于发布、审批和监控身份的 RSA-3072+ 状态密钥初始化部署状态；私钥必须位于仓库外 SecretRoot。初始化只能执行一次：

```powershell
.\Set-ServerDeploymentState.ps1 -Action Bootstrap -DeploymentRoot '<部署根>' `
  -ConfirmDeploymentRoot '<解析后的同一绝对路径>' -DeploymentId '<部署 ID>' `
  -SecretRoot '<受限 Secret 根>' -StatePrivateKeyFile '<状态私钥>' `
  -StatePublicKeyFile '<状态公钥>' -StateSignerKeyId '<Key ID>' `
  -CurrentSlotDirectory '<当前槽位>' -CurrentTrustedPublicKeyFile '<当前发布公钥>' `
  -CurrentExpectedSigningKeyId '<发布 Key ID>' -ExpectedPackageSupplier '<发行组织>' `
  -ExpectedSbomNamespaceBase 'https://<受信命名空间>'
```

候选暂存还必须提供候选槽位、双签计划、请求/批准/监控公钥与 Key ID。每次读取或启动前调用 `Test-ServerDeploymentState.ps1`，它会校验当前签名和从第 1 代开始的完整审计链。完整参数、权限边界和故障恢复见 [020.12](../../Book/服务器上线升级/020.12-签名部署状态与审计链.md)。

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
