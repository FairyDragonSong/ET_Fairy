# 主机与 MongoDB 告警处置

适用告警：`ETWindowsHostExporterDown`、`ETMongoDBExporterDown`、`ETWindowsHighCpu`、`ETWindowsHighMemory`、`ETWindowsDiskLow`、`ETMongoDBMemberUnhealthy`、`ETMongoDBPrimaryCardinalityInvalid`、`ETMongoDBReplicationLag`、`ETMongoDBConnectionSaturation`。

## 首次响应

1. 记录告警 fingerprint、实例、卷或副本成员、首次触发时间、部署代次和最近变更，不复制 MongoDB URI、环境变量或认证文件。
2. 同时核对 ET 业务 `/health`、各阶段 SLI、exporter `up` 和 `mongodb_up`。exporter 离线不等于主机或数据库已离线，但此时所有资源图表都可能是旧数据，不能用“曲线稳定”判定无故障。
3. 确认监控目标仍为预期回环/受控代理地址，服务身份和证书是否有效；不得为恢复采集而临时向公网开放 9182、9216 或 Prometheus 端口。
4. critical 告警暂停发布和扩流；涉及 Primary 异常、磁盘耗尽或内存压力时先保护数据与停止新流量，再决定回滚、故障转移或扩容。

## Windows 主机

- 高 CPU：按进程拆分 CPU，检查 ET 邮箱/锁积压、MongoDB、备份、杀毒和最近发布。持续饱和时先降载或摘除实例，不直接结束未知进程。
- 高内存：核对 ET 工作集、MongoDB cache、分页和提交限制。出现持续分页、OOM 或增长无回落时停止扩流，按优雅停机流程轮换实例并保留 dump/指标窗口。
- 磁盘低：确认告警卷，检查 ET 有界日志、MongoDB 数据/日志、Prometheus TSDB 和备份暂存。只清理由保留策略明确允许删除的文件；不得手工删除 MongoDB 数据文件或当前 oplog。
- exporter 离线：核对服务状态、受限服务账号、回环监听、端口冲突和本机 Prometheus 抓取错误。恢复后确认时间序列重新增长，而非只看到进程已启动。

## MongoDB

- `mongodb_up=0`：先检查 TLS CA/证书期限、DNS/地址、监控账号状态和副本集可达性；不得把账号提升为 root 来绕过认证错误。
- 成员不健康或 Primary 数量异常：执行只读副本集状态检查，确认多数派、选举、网络和磁盘；禁止手工修改 ET 角色数据或强制重配副本集。需要强制操作时按数据库事故审批执行。
- 复制延迟：核对 oplog 窗口、慢查询、磁盘延迟、网络和 secondary apply；生产含 arbiter/隐藏/延迟成员时使用已审批的拓扑过滤规则。
- 连接饱和：核对当前/可用连接、ET 实例数和连接池配置，排查连接泄漏。先限制新流量或摘除异常实例，不能只上调连接上限掩盖资源不足。

## 恢复确认

恢复至少同时满足：目标连续可抓取、`mongodb_up=1`、副本集恰有一个 Primary、数据承载成员健康、复制差和连接利用率回到阈值内、ET 登录/地图 SLI 恢复，并收到相同 fingerprint 的 resolved 通知。保存告警窗口、只读诊断、变更或故障转移记录；禁止在事故记录中粘贴 Secret。

当前阈值是初始工程基线。只能依据生产同规格长稳和演练证据，通过评审修改规则与测试；不得为了消除告警直接放宽阈值。
