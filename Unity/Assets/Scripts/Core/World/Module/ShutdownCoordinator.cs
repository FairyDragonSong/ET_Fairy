using System.Threading;

namespace ET
{
    public enum ShutdownReason
    {
        Normal = 0,
        EmergencyStop = 1,
    }

    public sealed class ShutdownCoordinator: Singleton<ShutdownCoordinator>, ISingletonAwake
    {
        private int requested;
        private int participants;
        private int completed;
        private int failures;
        private int reason;

        public bool IsRequested => Volatile.Read(ref this.requested) != 0;
        public bool IsCompleted => this.IsRequested && Volatile.Read(ref this.completed) >= Volatile.Read(ref this.participants);
        public bool IsSuccessful => this.IsCompleted && Volatile.Read(ref this.failures) == 0;
        public int ParticipantCount => Volatile.Read(ref this.participants);
        public int CompletedCount => Volatile.Read(ref this.completed);
        public ShutdownReason Reason => (ShutdownReason)Volatile.Read(ref this.reason);

        // 初始化当前组件的运行状态。
        public void Awake() { }
        // 登记一个需要参与优雅停机的组件。
        public void RegisterParticipant() => Interlocked.Increment(ref this.participants);
        // 发起停机请求，并保留更高优先级的失败原因。
        public void Request(ShutdownReason shutdownReason = ShutdownReason.Normal)
        {
            Interlocked.Exchange(ref this.requested, 1);
            if (shutdownReason == ShutdownReason.EmergencyStop)
            {
                Interlocked.Exchange(ref this.reason, (int)shutdownReason);
            }
        }

        // 登记参与者完成，并汇总停机结果。
        public void CompleteParticipant(bool success)
        {
            if (!success)
            {
                Interlocked.Increment(ref this.failures);
            }
            Interlocked.Increment(ref this.completed);
        }
    }
}
