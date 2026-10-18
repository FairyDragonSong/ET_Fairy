using System.Collections.Generic;

namespace ET.Server
{
    [ComponentOf(typeof(Scene))]
    public class GateSessionKeyComponent : Entity, IAwake
    {
        public readonly Dictionary<long, GateLoginTicket> sessionKey = new();
    }

    [EnableClass]
    public sealed class GateLoginTicket
    {
        public string Account { get; }
        public long StateVersion { get; }
        // 将一次性 Gate 票据绑定到账号及其认证时的状态版本。
        public GateLoginTicket(string account, long stateVersion)
        {
            if (string.IsNullOrEmpty(account) || stateVersion < 0) throw new System.ArgumentException("Invalid Gate ticket");
            this.Account = account;
            this.StateVersion = stateVersion;
        }
    }
}
