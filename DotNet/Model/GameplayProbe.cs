namespace ET.Server;

[ComponentOf(typeof(Scene))]
public sealed class GameplayProbe : Entity, IAwake
{
    public int Matches { get; set; }
    public int Starts { get; set; }
    public int Reconnects { get; set; }
    public int ReconnectUnits { get; set; }
    public long ReconnectStartTime { get; set; }
}
