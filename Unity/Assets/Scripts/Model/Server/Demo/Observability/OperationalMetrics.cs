using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ET.Server
{
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
            Append(text, "et_shutdown_requested", ShutdownCoordinator.Instance.IsRequested ? 1 : 0);
            return text.ToString();
        }

        private static void Append(StringBuilder text, string name, long value)
        {
            text.Append(name).Append(' ').Append(value).Append('\n');
        }
    }
}
