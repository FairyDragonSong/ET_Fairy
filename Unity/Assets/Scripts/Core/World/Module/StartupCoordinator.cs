using System.Threading;

namespace ET
{
    public sealed class StartupCoordinator: Singleton<StartupCoordinator>, ISingletonAwake
    {
        private int state;

        public bool IsReady => Volatile.Read(ref this.state) == 1;
        public bool IsFailed => Volatile.Read(ref this.state) == 2;

        // 初始化当前组件的运行状态。
        public void Awake() { }
        // 将启动状态标记为就绪。
        public void MarkReady() => Interlocked.CompareExchange(ref this.state, 1, 0);
        // 将启动状态标记为失败。
        public void MarkFailed() => Interlocked.Exchange(ref this.state, 2);
    }
}
