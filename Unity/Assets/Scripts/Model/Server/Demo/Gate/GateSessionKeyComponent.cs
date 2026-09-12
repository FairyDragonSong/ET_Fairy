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
        public GateLoginTicket(string account, long stateVersion)
        {
            if (string.IsNullOrEmpty(account) || stateVersion < 0) throw new System.ArgumentException("Invalid Gate ticket");
            this.Account = account;
            this.StateVersion = stateVersion;
        }
    }
}
