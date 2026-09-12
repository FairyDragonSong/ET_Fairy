namespace ET.Server;

[EnableClass]
public sealed class AccountAdminCommand
{
    public string OperationId { get; set; }
    public string OperatorId { get; set; }
    public string Action { get; set; }
    public string Database { get; set; }
    public string DeploymentId { get; set; }
    public int Zone { get; set; }
    public long AccountId { get; set; }
    public long ConfirmAccountId { get; set; }
    public long ExpectedVersion { get; set; }
    public string ReasonCode { get; set; }
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
}

[EnableClass]
public sealed class AccountAdminEnvelope
{
    public string OperatorId { get; set; }
    public string Payload { get; set; }
    public string Signature { get; set; }
}

[EnableClass]
public sealed class AccountAdminOperator
{
    public string Id { get; set; }
    public string PublicKeyPem { get; set; }
    public string Database { get; set; }
    public string DeploymentId { get; set; }
    public int Zone { get; set; }
    public string[] Actions { get; set; }
    public long ExpiresAt { get; set; }
}
