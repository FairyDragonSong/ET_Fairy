using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ET.Server
{
    public enum HandlerLatency { RealmLogin, GateLogin, PasswordChange, AccountClose, RoleSaveSuccess, RoleSaveFailure, MapEntry, RoomReconnect, MapOrderedMailbox, MapUnorderedMailbox, Count }
    public enum LoginFailureKind { Rejected, ServiceError }
    public enum SecurityMetric
    {
        PasswordChangeAccepted, PasswordChangeUnconfirmed, PasswordChangeRateLimited,
        AccountCloseAccepted, AccountCloseUnconfirmed, AccountCloseRateLimited,
        JournalAdmitted, JournalAdmissionFailures, JournalEnded, JournalEndFailures, JournalEndSkipped,
        SessionStateRevoked, SessionLeaseExpired, SessionCheckFailures, Count
    }

    [Code]
    public sealed class OperationalMetrics: Singleton<OperationalMetrics>, ISingletonAwake
    {
        private long startedAtUnixSeconds;
        private long httpRequests;
        private long httpErrors;
        private long loginAttempts;
        private long loginSuccesses;
        private long loginRejections;
        private long loginServiceErrors;
        private long gateLoginAttempts;
        private long gateLoginSuccesses;
        private long gateLoginRejections;
        private long gateLoginServiceErrors;
        private long mapEntryAttempts;
        private long mapEntryAccepted;
        private long mapEntryRejections;
        private long mapEntryServiceErrors;
        private long mapTransfersInflight;
        private long mapTransferSuccesses;
        private long mapTransferFailures;
        private readonly object mapTransferSync = new();
        private long roleSaveSuccesses;
        private long roleSaveFailures;
        private readonly long[] mapMailboxInflight = new long[2];
        private readonly long[] mapMailboxExceptions = new long[2];

        // 登记一条开始处理的地图邮箱消息。
        public void StartMapMailbox(bool ordered)
        {
            Interlocked.Increment(ref this.mapMailboxInflight[ordered ? 0 : 1]);
        }

        // 结束地图邮箱消息并记录耗时与异常。
        public void FinishMapMailbox(bool ordered, bool exception)
        {
            int index = ordered ? 0 : 1;
            Interlocked.Decrement(ref this.mapMailboxInflight[index]);
            if (exception) Interlocked.Increment(ref this.mapMailboxExceptions[index]);
        }
        [StaticField] private static readonly double[] latencyBoundsMs={1,5,10,25,50,100,250,500,1000,2500,5000};
        private readonly long[,] handlerLatencyBuckets=new long[(int)HandlerLatency.Count,12];
        private readonly long[] handlerLatencyMicroseconds=new long[(int)HandlerLatency.Count];

        // 记录指定处理器的执行耗时分布。
        public void RecordHandlerDuration(HandlerLatency operation,double milliseconds)
        {
            if((uint)operation>=(uint)HandlerLatency.Count||double.IsNaN(milliseconds)||double.IsInfinity(milliseconds)||milliseconds<0||milliseconds>TimeSpan.MaxValue.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            lock(this.handlerLatencyBuckets)
            {
                for(int i=0;i<latencyBoundsMs.Length;i++)if(milliseconds<=latencyBoundsMs[i])this.handlerLatencyBuckets[(int)operation,i]++;
                this.handlerLatencyBuckets[(int)operation,latencyBoundsMs.Length]++;
                this.handlerLatencyMicroseconds[(int)operation]+=(long)(milliseconds*1000);
            }
        }

        // 输出各类处理器耗时直方图。
        private void AppendHandlerLatency(StringBuilder text)
        {
            lock(this.handlerLatencyBuckets)
            {
                for(int operation=0;operation<(int)HandlerLatency.Count;operation++)
                {
                    string name=(HandlerLatency)operation switch
                    {
                        HandlerLatency.RealmLogin=>"et_realm_login_handler_seconds",
                        HandlerLatency.GateLogin=>"et_gate_login_handler_seconds",
                        HandlerLatency.PasswordChange=>"et_password_change_handler_seconds",
                        HandlerLatency.AccountClose=>"et_account_close_handler_seconds",
                        HandlerLatency.RoleSaveSuccess=>"et_role_save_success_seconds",
                        HandlerLatency.RoleSaveFailure=>"et_role_save_failure_seconds",
                        HandlerLatency.MapEntry=>"et_map_entry_handler_seconds",
                        HandlerLatency.RoomReconnect=>"et_room_reconnect_handler_seconds",
                        HandlerLatency.MapOrderedMailbox=>"et_map_ordered_mailbox_seconds",
                        HandlerLatency.MapUnorderedMailbox=>"et_map_unordered_mailbox_seconds",
                        _=>throw new InvalidOperationException()
                    };
                    text.Append("# TYPE ").Append(name).Append(" histogram\n");
                    for(int i=0;i<=latencyBoundsMs.Length;i++)
                    {
                        string bound=i==latencyBoundsMs.Length?"+Inf":(latencyBoundsMs[i]/1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        text.Append(name).Append("_bucket{le=\"").Append(bound).Append("\"} ").Append(this.handlerLatencyBuckets[operation,i]).Append('\n');
                    }
                    Append(text,name+"_count",this.handlerLatencyBuckets[operation,latencyBoundsMs.Length]);
                    text.Append(name).Append("_sum ").Append((this.handlerLatencyMicroseconds[operation]/1000000.0).ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }
        private long gateBoundSessions;
        private long gateSessionBindings;
        private long gateSessionUnbindings;

        // 更新 Gate 在线会话数量及生命周期计数。
        public void RecordGateSession(bool bound)
        {
            if(bound)
            {
                Interlocked.Increment(ref this.gateBoundSessions);
                Interlocked.Increment(ref this.gateSessionBindings);
            }
            else
            {
                Interlocked.Decrement(ref this.gateBoundSessions);
                Interlocked.Increment(ref this.gateSessionUnbindings);
            }
        }
        private readonly long[] securityCounters = new long[(int)SecurityMetric.Count];

        // 累加指定安全事件指标。
        public void RecordSecurity(SecurityMetric metric)
        {
            if ((uint)metric >= (uint)SecurityMetric.Count) throw new ArgumentOutOfRangeException(nameof(metric));
            Interlocked.Increment(ref this.securityCounters[(int)metric]);
        }

        // 把安全事件类型映射为稳定的指标名称。
        private static string SecurityName(SecurityMetric metric) => metric switch
        {
            SecurityMetric.PasswordChangeAccepted => "et_password_change_accepted_total",
            SecurityMetric.PasswordChangeUnconfirmed => "et_password_change_unconfirmed_total",
            SecurityMetric.PasswordChangeRateLimited => "et_password_change_rate_limited_total",
            SecurityMetric.AccountCloseAccepted => "et_account_close_accepted_total",
            SecurityMetric.AccountCloseUnconfirmed => "et_account_close_unconfirmed_total",
            SecurityMetric.AccountCloseRateLimited => "et_account_close_rate_limited_total",
            SecurityMetric.JournalAdmitted => "et_session_journal_admitted_total",
            SecurityMetric.JournalAdmissionFailures => "et_session_journal_admission_failures_total",
            SecurityMetric.JournalEnded => "et_session_journal_ended_total",
            SecurityMetric.JournalEndFailures => "et_session_journal_end_failures_total",
            SecurityMetric.JournalEndSkipped => "et_session_journal_end_skipped_total",
            SecurityMetric.SessionStateRevoked => "et_session_state_revoked_total",
            SecurityMetric.SessionLeaseExpired => "et_session_lease_expired_total",
            SecurityMetric.SessionCheckFailures => "et_session_check_failures_total",
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };

        // 初始化当前组件的运行状态。
        public void Awake()
        {
            this.startedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // 记录 HTTP 请求及错误数量。
        public void RecordHttp(bool error)
        {
            Interlocked.Increment(ref this.httpRequests);
            if (error)
            {
                Interlocked.Increment(ref this.httpErrors);
            }
        }

        // 记录 Realm 登录尝试。
        public void RecordLoginAttempt() => Interlocked.Increment(ref this.loginAttempts);
        // 记录 Realm 登录成功。
        public void RecordLoginSuccess() => Interlocked.Increment(ref this.loginSuccesses);
        // 按失败类型记录 Realm 登录失败。
        public void RecordLoginFailure(LoginFailureKind kind)
        {
            switch (kind)
            {
                case LoginFailureKind.Rejected:
                    Interlocked.Increment(ref this.loginRejections);
                    break;
                case LoginFailureKind.ServiceError:
                    Interlocked.Increment(ref this.loginServiceErrors);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        // 记录 Gate 登录尝试。
        public void RecordGateLoginAttempt() => Interlocked.Increment(ref this.gateLoginAttempts);
        // 记录 Gate 登录成功。
        public void RecordGateLoginSuccess() => Interlocked.Increment(ref this.gateLoginSuccesses);
        // 按失败类型记录 Gate 登录失败。
        public void RecordGateLoginFailure(LoginFailureKind kind)
        {
            switch (kind)
            {
                case LoginFailureKind.Rejected:
                    Interlocked.Increment(ref this.gateLoginRejections);
                    break;
                case LoginFailureKind.ServiceError:
                    Interlocked.Increment(ref this.gateLoginServiceErrors);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        // 记录进入地图尝试。
        public void RecordMapEntryAttempt() => Interlocked.Increment(ref this.mapEntryAttempts);
        // 记录被业务规则拒绝的进图请求。
        public void RecordMapEntryRejected() => Interlocked.Increment(ref this.mapEntryRejections);
        // 记录因服务异常失败的进图请求。
        public void RecordMapEntryServiceError() => Interlocked.Increment(ref this.mapEntryServiceErrors);

        // 登记一次开始中的地图转移。
        public void StartMapTransfer()
        {
            lock (this.mapTransferSync)
            {
                ++this.mapEntryAccepted;
                ++this.mapTransfersInflight;
            }
        }

        // 结束地图转移并记录成功或失败。
        public void FinishMapTransfer(bool success)
        {
            lock (this.mapTransferSync)
            {
                if (this.mapTransfersInflight <= 0) throw new InvalidOperationException("No map transfer is in flight");
                --this.mapTransfersInflight;
                if (success) ++this.mapTransferSuccesses;
                else ++this.mapTransferFailures;
            }
        }

        // 记录角色保存结果与耗时。
        public void RecordRoleSave(bool success)
        {
            if (success)
            {
                Interlocked.Increment(ref this.roleSaveSuccesses);
            }
            else
            {
                Interlocked.Increment(ref this.roleSaveFailures);
            }
        }

        // 导出当前进程的 Prometheus 文本指标。
        public string ExportPrometheus()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Process process = Process.GetCurrentProcess();
            StringBuilder text = new();
            Append(text, "et_process_id", process.Id);
            var queues=MessageQueue.Instance?.GetSnapshot() ?? default;
            text.Append("# TYPE et_fiber_message_queues gauge\n");
            Append(text,"et_fiber_message_queues",queues.Queues);
            text.Append("# TYPE et_fiber_messages_pending gauge\n");
            Append(text,"et_fiber_messages_pending",queues.Pending);
            text.Append("# TYPE et_fiber_messages_max_pending_per_queue gauge\n");
            Append(text,"et_fiber_messages_max_pending_per_queue",queues.Maximum);
            var locks=CoroutineLockMetrics.GetSnapshot();
            text.Append("# TYPE et_coroutine_lock_waiting gauge\n");
            Append(text,"et_coroutine_lock_waiting",locks.Waiting);
            text.Append("# TYPE et_coroutine_lock_waits_started_total counter\n");
            Append(text,"et_coroutine_lock_waits_started_total",locks.Started);
            text.Append("# TYPE et_coroutine_lock_waits_acquired_total counter\n");
            Append(text,"et_coroutine_lock_waits_acquired_total",locks.Acquired);
            text.Append("# TYPE et_coroutine_lock_waits_failed_total counter\n");
            Append(text,"et_coroutine_lock_waits_failed_total",locks.Failed);
            text.Append("# TYPE et_coroutine_lock_waits_abandoned_total counter\n");
            Append(text,"et_coroutine_lock_waits_abandoned_total",locks.Abandoned);
            Append(text, "et_config_process_id", Options.Instance.Process);
            Append(text, "et_process_start_time_seconds", this.startedAtUnixSeconds);
            Append(text, "et_process_uptime_seconds", Math.Max(0, now - this.startedAtUnixSeconds));
            Append(text, "et_process_working_set_bytes", process.WorkingSet64);
            Append(text, "et_gc_managed_memory_bytes", GC.GetTotalMemory(false));
            Append(text, "et_http_requests_total", Interlocked.Read(ref this.httpRequests));
            Append(text, "et_http_errors_total", Interlocked.Read(ref this.httpErrors));
            text.Append("# TYPE et_login_attempts_total counter\n");
            Append(text, "et_login_attempts_total", Interlocked.Read(ref this.loginAttempts));
            text.Append("# TYPE et_login_successes_total counter\n");
            Append(text, "et_login_successes_total", Interlocked.Read(ref this.loginSuccesses));
            long loginRejectionsSnapshot=Interlocked.Read(ref this.loginRejections);
            long loginServiceErrorsSnapshot=Interlocked.Read(ref this.loginServiceErrors);
            text.Append("# TYPE et_login_failures_total counter\n");
            Append(text, "et_login_failures_total", loginRejectionsSnapshot+loginServiceErrorsSnapshot);
            text.Append("# TYPE et_login_rejections_total counter\n");
            Append(text, "et_login_rejections_total", loginRejectionsSnapshot);
            text.Append("# TYPE et_login_service_errors_total counter\n");
            Append(text, "et_login_service_errors_total", loginServiceErrorsSnapshot);
            long gateLoginRejectionsSnapshot=Interlocked.Read(ref this.gateLoginRejections);
            long gateLoginServiceErrorsSnapshot=Interlocked.Read(ref this.gateLoginServiceErrors);
            text.Append("# TYPE et_gate_login_attempts_total counter\n");
            Append(text, "et_gate_login_attempts_total", Interlocked.Read(ref this.gateLoginAttempts));
            text.Append("# TYPE et_gate_login_successes_total counter\n");
            Append(text, "et_gate_login_successes_total", Interlocked.Read(ref this.gateLoginSuccesses));
            text.Append("# TYPE et_gate_login_failures_total counter\n");
            Append(text, "et_gate_login_failures_total", gateLoginRejectionsSnapshot+gateLoginServiceErrorsSnapshot);
            text.Append("# TYPE et_gate_login_rejections_total counter\n");
            Append(text, "et_gate_login_rejections_total", gateLoginRejectionsSnapshot);
            text.Append("# TYPE et_gate_login_service_errors_total counter\n");
            Append(text, "et_gate_login_service_errors_total", gateLoginServiceErrorsSnapshot);
            text.Append("# TYPE et_map_entry_attempts_total counter\n");
            Append(text, "et_map_entry_attempts_total", Interlocked.Read(ref this.mapEntryAttempts));
            text.Append("# TYPE et_map_entry_rejections_total counter\n");
            Append(text, "et_map_entry_rejections_total", Interlocked.Read(ref this.mapEntryRejections));
            text.Append("# TYPE et_map_entry_service_errors_total counter\n");
            Append(text, "et_map_entry_service_errors_total", Interlocked.Read(ref this.mapEntryServiceErrors));
            lock (this.mapTransferSync)
            {
                text.Append("# TYPE et_map_entry_accepted_total counter\n");
                Append(text, "et_map_entry_accepted_total", this.mapEntryAccepted);
                text.Append("# TYPE et_map_transfers_inflight gauge\n");
                Append(text, "et_map_transfers_inflight", this.mapTransfersInflight);
                text.Append("# TYPE et_map_transfer_successes_total counter\n");
                Append(text, "et_map_transfer_successes_total", this.mapTransferSuccesses);
                text.Append("# TYPE et_map_transfer_failures_total counter\n");
                Append(text, "et_map_transfer_failures_total", this.mapTransferFailures);
            }
            Append(text, "et_role_save_successes_total", Interlocked.Read(ref this.roleSaveSuccesses));
            Append(text, "et_role_save_failures_total", Interlocked.Read(ref this.roleSaveFailures));
            text.Append("# TYPE et_gate_bound_sessions gauge\n");
            Append(text,"et_gate_bound_sessions",Interlocked.Read(ref this.gateBoundSessions));
            text.Append("# TYPE et_gate_session_bindings_total counter\n");
            Append(text,"et_gate_session_bindings_total",Interlocked.Read(ref this.gateSessionBindings));
            text.Append("# TYPE et_gate_session_unbindings_total counter\n");
            Append(text,"et_gate_session_unbindings_total",Interlocked.Read(ref this.gateSessionUnbindings));
            for (int i = 0; i < this.securityCounters.Length; i++)
            {
                string name = SecurityName((SecurityMetric)i);
                text.Append("# TYPE ").Append(name).Append(" counter\n");
                Append(text, name, Interlocked.Read(ref this.securityCounters[i]));
            }
            Append(text, "et_shutdown_requested", ShutdownCoordinator.Instance.IsRequested ? 1 : 0);
            Append(text, "et_shutdown_emergency", ShutdownCoordinator.Instance.Reason == ShutdownReason.EmergencyStop ? 1 : 0);
            this.AppendHandlerLatency(text);
            text.Append("# TYPE et_map_ordered_mailbox_inflight gauge\n");
            Append(text, "et_map_ordered_mailbox_inflight", Interlocked.Read(ref this.mapMailboxInflight[0]));
            text.Append("# TYPE et_map_unordered_mailbox_inflight gauge\n");
            Append(text, "et_map_unordered_mailbox_inflight", Interlocked.Read(ref this.mapMailboxInflight[1]));
            text.Append("# TYPE et_map_ordered_mailbox_exceptions_total counter\n");
            Append(text, "et_map_ordered_mailbox_exceptions_total", Interlocked.Read(ref this.mapMailboxExceptions[0]));
            text.Append("# TYPE et_map_unordered_mailbox_exceptions_total counter\n");
            Append(text, "et_map_unordered_mailbox_exceptions_total", Interlocked.Read(ref this.mapMailboxExceptions[1]));
            Append(text, "et_startup_ready", StartupCoordinator.Instance.IsReady ? 1 : 0);
            return text.ToString();
        }

        // 追加一条不可缺失的审计记录。
        private static void Append(StringBuilder text, string name, long value)
        {
            text.Append(name).Append(' ').Append(value).Append('\n');
        }
    }
}
