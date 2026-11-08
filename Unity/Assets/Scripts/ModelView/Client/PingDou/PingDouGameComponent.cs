namespace ET.Client
{
    [ComponentOf(typeof(Scene))]
    public sealed class PingDouGameComponent : Entity, IAwake, IUpdate, IDestroy
    {
        public PingDouView View;
        public PingDouSession Session;
    }
}
