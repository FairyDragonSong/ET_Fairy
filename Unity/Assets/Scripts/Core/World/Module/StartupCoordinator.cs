using System.Threading;

namespace ET
{
    public sealed class StartupCoordinator: Singleton<StartupCoordinator>, ISingletonAwake
    {
        private int state;

        public bool IsReady => Volatile.Read(ref this.state) == 1;
        public bool IsFailed => Volatile.Read(ref this.state) == 2;

        public void Awake() { }
        public void MarkReady() => Interlocked.CompareExchange(ref this.state, 1, 0);
        public void MarkFailed() => Interlocked.Exchange(ref this.state, 2);
    }
}
