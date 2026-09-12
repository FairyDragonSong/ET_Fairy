using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ET.Server
{
    public enum HandlerLatency { RealmLogin, GateLogin, PasswordChange, RoleSaveSuccess, RoleSaveFailure, MapOrderedMailbox, MapUnorderedMailbox, Count }
    public enum SecurityMetric
    {
        PasswordChangeAccepted, PasswordChangeUnconfirmed, PasswordChangeRateLimited,
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
        private long loginFailures;
        private long roleSaveSuccesses;
        private long roleSaveFailures;
        [StaticField] private static readonly double[] latencyBoundsMs={1,5,10,25,50,100,250,500,1000,2500,5000};
        private readonly long[,] handlerLatencyBuckets=new long[(int)HandlerLatency.Count,12];
        private readonly long[] handlerLatencyMicroseconds=new long[(int)HandlerLatency.Count];

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
                        HandlerLatency.RoleSaveSuccess=>"et_role_save_success_seconds",
                        HandlerLatency.RoleSaveFailure=>"et_role_save_failure_seconds",
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

        public void RecordSecurity(SecurityMetric metric)
        {
            if ((uint)metric >= (uint)SecurityMetric.Count) throw new ArgumentOutOfRangeException(nameof(metric));
            Interlocked.Increment(ref this.securityCounters[(int)metric]);
        }

        private static string SecurityName(SecurityMetric metric) => metric switch
        {
            SecurityMetric.PasswordChangeAccepted => "et_password_change_accepted_total",
            SecurityMetric.PasswordChangeUnconfirmed => "et_password_change_unconfirmed_total",
            SecurityMetric.PasswordChangeRateLimited => "et_password_change_rate_limited_total",
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

        public void Awake()
        {
            this.startedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public void RecordHttp(bool error)
        {
            Interlocked.Increment(ref this.httpRequests);
            if (error)
            {
                Interlocked.Increment(ref this.httpErrors);
            }
        }

        public void RecordLoginAttempt() => Interlocked.Increment(ref this.loginAttempts);
        public void RecordLoginSuccess() => Interlocked.Increment(ref this.loginSuccesses);
        public void RecordLoginFailure() => Interlocked.Increment(ref this.loginFailures);

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
            Append(text, "et_login_attempts_total", Interlocked.Read(ref this.loginAttempts));
            Append(text, "et_login_successes_total", Interlocked.Read(ref this.loginSuccesses));
            Append(text, "et_login_failures_total", Interlocked.Read(ref this.loginFailures));
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
            this.AppendHandlerLatency(text);
            Append(text, "et_startup_ready", StartupCoordinator.Instance.IsReady ? 1 : 0);
            return text.ToString();
        }

        private static void Append(StringBuilder text, string name, long value)
        {
            text.Append(name).Append(' ').Append(value).Append('\n');
        }
    }
}
