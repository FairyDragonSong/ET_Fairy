namespace ET.Server
{
    [ComponentOf(typeof(Scene))]
    public sealed class GracefulShutdownComponent: Entity, IAwake, IUpdate, IDestroy
    {
        public bool Started;
        public bool Reported;
    }
}
