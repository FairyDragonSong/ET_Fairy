using System;
using System.Threading;

namespace ET.Server;

[ComponentOf(typeof(Scene))]
public sealed class AccountSessionDrainComponent : Entity, IAwake
{
    public AccountSessionDrainState State;
}

// Independent of Entity lifetime: an outstanding database callback must retain its state.
[EnableClass]
public sealed class AccountSessionDrainState
{
    public readonly string GateInstanceId = Guid.NewGuid().ToString("N");
    public readonly SemaphoreSlim Slots = new(16, 16);
    public int Pending;
    public int Admissions;
    public int Closing;
    public int Failures;
}
