namespace ET.Server;

[ComponentOf(typeof(Scene))]
public sealed class AccountSessionMonitor : Entity, IAwake, IDestroy
{
    public long Timer;
    public bool Checking;
}
