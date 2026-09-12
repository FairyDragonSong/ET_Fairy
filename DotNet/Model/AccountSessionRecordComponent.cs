namespace ET.Server;

[ComponentOf(typeof(Session))]
public sealed class AccountSessionRecordComponent : Entity, IAwake, IDestroy
{
    public string RecordId;
}
