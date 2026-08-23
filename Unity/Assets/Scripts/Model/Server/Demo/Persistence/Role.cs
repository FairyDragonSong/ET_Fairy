namespace ET.Server
{
    [ChildOf(typeof(Scene))]
    public sealed class Role: Entity, IAwake
    {
        public long AccountId { get; set; }
        public string AccountName { get; set; }
        public string Name { get; set; }
        public string MapName { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public long CreateTime { get; set; }
        public long LastLoginTime { get; set; }
        public long LastSaveTime { get; set; }
    }

    [ComponentOf(typeof(Unit))]
    public sealed class RoleSaveComponent: Entity, IAwake, IDestroy
    {
        public long Timer;
        public bool Saving;
    }
}
