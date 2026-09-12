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

        public void Awake() { }
        public void RegisterParticipant() => Interlocked.Increment(ref this.participants);
        public void Request(ShutdownReason shutdownReason = ShutdownReason.Normal)
        {
            Interlocked.Exchange(ref this.requested, 1);
            if (shutdownReason == ShutdownReason.EmergencyStop)
            {
                Interlocked.Exchange(ref this.reason, (int)shutdownReason);
            }
        }

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
