using System;

namespace ET.Server;

[EnableClass]
public sealed class SelfAccountClosure
{
    public string OperationId { get; set; }
    public string LoginName { get; set; }
    public string ConfirmLoginName { get; set; }
    public string Password { get; set; }
    public long ExpiresAt { get; set; }
}
