# 登录与地图 SLI 告警处置

适用告警：`ETStageAvailabilityFastBurn`、`ETStageAvailabilitySlowBurn`、`ETMapTransferStuck`。

## 首次响应

1. 记录告警 fingerprint、stage、首次触发时间、当前部署代次和变更记录，不复制认证头或 Secret。
2. 核对对应实例的 `up`、`et_process_start_time_seconds` 和 `et_startup_ready`。实例离线或刚重启时，先按进程/发布事故处理，不能把计数器归零解释为恢复。
3. 对照 `stage` 定位范围：`realm` 检查账号数据库及 Gate Key RPC；`gate` 检查账号复核、会话审计和角色加载；`map-transfer` 检查 Location、目标 Map、跨服务 RPC 与传送失败日志。
4. 同时查看拒绝计数。拒绝升高但服务故障稳定时优先调查错密码、失效 Key、限流或攻击，不按可用性事故盲目扩容。

## 分级动作

- Fast burn 为 critical：暂停发布与流量提升，关联最近变更；达到既定回滚条件时使用签名发布状态和监督器执行回滚。不得跳过数据库兼容门禁。
- Slow burn 为 warning：建立事件记录，检查容量趋势、依赖延迟和单实例偏斜；在错误预算继续消耗前完成负责人确认。
- 传送在途超过两分钟：检查目标 Map 和 Location 服务，不要手工修改 Player 内存状态。若服务已不可恢复，先停止新流量并按会话/进程恢复流程处置。

## 恢复确认

恢复不能只看 firing 消失。必须确认目标持续可抓取、启动代次符合预期、成功计数恢复增长、服务错误率回落、地图在途归零，并收到相同 fingerprint 的 resolved 通知。将查询窗口、部署状态、相关日志关联 ID 和采取的动作写入事故记录。

内部 `99.5%` 是当前工程错误预算基线，不是对外 SLA。正式上线前由业务和运维负责人结合长稳容量结果、维护窗口及实际承诺签署最终 SLO；变更目标时必须同步更新规则、测试和上线记录。
