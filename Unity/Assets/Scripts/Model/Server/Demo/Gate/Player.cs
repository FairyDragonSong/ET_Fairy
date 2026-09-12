namespace ET.Server
{
    [ChildOf(typeof(PlayerComponent))]
    public sealed class Player : Entity, IAwake<string>
    {
        public string Account { get; set; }

        public string RoleName { get; set; }

        public string LastMapName { get; set; }

        public float PositionX { get; set; }

        public float PositionY { get; set; }

        public float PositionZ { get; set; }
        // Connection lifecycle state, reset only after old Map state has been released.
        public bool MapEntered { get; set; }
    }

    [ComponentOf(typeof(Player))]
    public sealed class PlayerOfflineComponent: Entity, IAwake, IDestroy
    {
        public long Timer;
    }
}
