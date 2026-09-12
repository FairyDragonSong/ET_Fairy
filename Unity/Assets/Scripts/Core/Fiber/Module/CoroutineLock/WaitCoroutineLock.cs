using System;
using System.Threading;

namespace ET
{
    [Invoke(TimerCoreInvokeType.CoroutineTimeout)]
    public class WaitCoroutineLockTimer: ATimer<WaitCoroutineLock>
    {
        protected override void Run(WaitCoroutineLock waitCoroutineLock)
        {
            if (waitCoroutineLock.IsDisposed())
            {
                return;
            }
            waitCoroutineLock.SetException(new Exception("coroutine is timeout!"));
        }
    }
    
    public class WaitCoroutineLock
    {
        public static WaitCoroutineLock Create()
        {
            WaitCoroutineLock waitCoroutineLock = new WaitCoroutineLock();
            waitCoroutineLock.tcs = ETTask<CoroutineLock>.Create(true);
            waitCoroutineLock.tracking = 1;
            CoroutineLockMetrics.Start();
            return waitCoroutineLock;
        }
        
        private ETTask<CoroutineLock> tcs;
        private int tracking;

        public void AbandonTracking()
        {
            // Queue destruction stops observation; it does not complete the waiting task.
            if (Interlocked.Exchange(ref this.tracking, 0) == 1)
            {
                CoroutineLockMetrics.Finish(2);
            }
        }

        private void FinishTracking(bool failed)
        {
            if (Interlocked.Exchange(ref this.tracking, 0) == 1)
            {
                CoroutineLockMetrics.Finish(failed ? 1 : 0);
            }
        }

        public void SetResult(CoroutineLock coroutineLock)
        {
            if (this.tcs == null)
            {
                throw new NullReferenceException("SetResult tcs is null");
            }
            var t = this.tcs;
            this.tcs = null;
            this.FinishTracking(false);
            t.SetResult(coroutineLock);
        }

        public void SetException(Exception exception)
        {
            if (this.tcs == null)
            {
                throw new NullReferenceException("SetException tcs is null");
            }
            var t = this.tcs;
            this.tcs = null;
            this.FinishTracking(true);
            t.SetException(exception);
        }

        public bool IsDisposed()
        {
            return this.tcs == null;
        }

        public async ETTask<CoroutineLock> Wait()
        {
            return await this.tcs;
        }
    }

    // Process-wide counts only. No lock keys, actor IDs or runtime-type labels.
    public static class CoroutineLockMetrics
    {
        [StaticField] private static long waiting;
        [StaticField] private static long started;
        [StaticField] private static long acquired;
        [StaticField] private static long failed;
        [StaticField] private static long abandoned;

        internal static void Start()
        {
            Interlocked.Increment(ref waiting);
            Interlocked.Increment(ref started);
        }

        internal static void Finish(int outcome)
        {
            Interlocked.Decrement(ref waiting);
            if (outcome == 0) Interlocked.Increment(ref acquired);
            else if (outcome == 1) Interlocked.Increment(ref failed);
            else Interlocked.Increment(ref abandoned);
        }

        public static (long Waiting, long Started, long Acquired, long Failed, long Abandoned) GetSnapshot() =>
            (Interlocked.Read(ref waiting), Interlocked.Read(ref started), Interlocked.Read(ref acquired),
                Interlocked.Read(ref failed), Interlocked.Read(ref abandoned));
    }
}
