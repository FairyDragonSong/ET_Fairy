using System;
using System.Collections.Generic;

namespace ET.Server
{
    [EntitySystemOf(typeof(LoginRateLimiterComponent))]
    [FriendOf(typeof(LoginRateLimiterComponent))]
    public static partial class LoginRateLimiterComponentSystem
    {
        [EntitySystem]
        private static void Awake(this LoginRateLimiterComponent self)
        {
            self.NextCleanupTime = TimeInfo.Instance.ServerNow() + GetWindowMilliseconds();
        }

        public static bool TryAcquire(
            this LoginRateLimiterComponent self,
            string ipAddress,
            string accountInput,
            out long retryAfterMilliseconds)
        {
            long now = TimeInfo.Instance.ServerNow();
            long windowMilliseconds = GetWindowMilliseconds();
            self.CleanupIfNeeded(now, windowMilliseconds);

            int ipLimit = Math.Max(1, Options.Instance.LoginIpLimit);
            if (!TryConsume(self.IpWindows, ipAddress, ipLimit, windowMilliseconds, now, out retryAfterMilliseconds))
            {
                return false;
            }

            if (!AccountInputValidator.TryNormalizeLoginName(accountInput, out string loginName))
            {
                retryAfterMilliseconds = 0;
                return true;
            }

            int accountLimit = Math.Max(1, Options.Instance.LoginAccountLimit);
            return TryConsume(
                self.AccountWindows,
                loginName,
                accountLimit,
                windowMilliseconds,
                now,
                out retryAfterMilliseconds);
        }

        private static bool TryConsume(
            Dictionary<string, LoginRateLimitWindow> windows,
            string key,
            int limit,
            long windowMilliseconds,
            long now,
            out long retryAfterMilliseconds)
        {
            if (!windows.TryGetValue(key, out LoginRateLimitWindow window) || now - window.StartTime >= windowMilliseconds)
            {
                windows[key] = new LoginRateLimitWindow { StartTime = now, Count = 1 };
                retryAfterMilliseconds = 0;
                return true;
            }

            if (window.Count >= limit)
            {
                retryAfterMilliseconds = Math.Max(1, windowMilliseconds - (now - window.StartTime));
                return false;
            }

            ++window.Count;
            windows[key] = window;
            retryAfterMilliseconds = 0;
            return true;
        }

        private static void CleanupIfNeeded(this LoginRateLimiterComponent self, long now, long windowMilliseconds)
        {
            if (now < self.NextCleanupTime)
            {
                return;
            }

            Cleanup(self.IpWindows, self.CleanupKeys, now, windowMilliseconds);
            Cleanup(self.AccountWindows, self.CleanupKeys, now, windowMilliseconds);
            self.NextCleanupTime = now + windowMilliseconds;
        }

        private static void Cleanup(
            Dictionary<string, LoginRateLimitWindow> windows,
            List<string> cleanupKeys,
            long now,
            long windowMilliseconds)
        {
            cleanupKeys.Clear();
            foreach ((string key, LoginRateLimitWindow window) in windows)
            {
                if (now - window.StartTime >= windowMilliseconds)
                {
                    cleanupKeys.Add(key);
                }
            }

            foreach (string key in cleanupKeys)
            {
                windows.Remove(key);
            }
            cleanupKeys.Clear();
        }

        private static long GetWindowMilliseconds()
        {
            return Math.Max(1, Options.Instance.LoginRateWindowSeconds) * 1000L;
        }
    }
}
