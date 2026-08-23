using System.Collections.Generic;

namespace ET.Server
{
    public struct LoginRateLimitWindow
    {
        public long StartTime;

        public int Count;
    }

    [ComponentOf(typeof(Scene))]
    public sealed class LoginRateLimiterComponent: Entity, IAwake
    {
        public readonly Dictionary<string, LoginRateLimitWindow> IpWindows = new();

        public readonly Dictionary<string, LoginRateLimitWindow> AccountWindows = new();

        public readonly List<string> CleanupKeys = new();

        public long NextCleanupTime;
    }
}
