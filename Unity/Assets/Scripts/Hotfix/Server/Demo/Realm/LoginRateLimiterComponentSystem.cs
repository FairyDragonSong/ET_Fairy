using System;
using System.Collections.Generic;

namespace ET.Server
{
    [EntitySystemOf(typeof(LoginRateLimiterComponent))]
    [FriendOf(typeof(LoginRateLimiterComponent))]
    public static partial class LoginRateLimiterComponentSystem
    {
        [EntitySystem]
        // 初始化下一次过期窗口清理时间，避免请求路径每次都扫描缓存。
        private static void Awake(this LoginRateLimiterComponent self)
        {
            // 首次清理时间安排在一个完整限流窗口之后，避免每次请求都遍历缓存。
            self.NextCleanupTime = TimeInfo.Instance.ServerNow() + GetWindowMilliseconds();
        }

        /// <summary>
        /// 尝试占用一次登录额度。请求需要先通过 IP 限流，再通过规范化账号限流。
        /// 账号格式无效时只统计 IP，具体格式错误交给认证流程返回。
        /// </summary>
        /// <param name="ipAddress">客户端 IP，作为 IP 限流键。</param>
        /// <param name="accountInput">客户端提交的原始账号名。</param>
        /// <param name="retryAfterMilliseconds">被限流时距离当前窗口结束的等待时间；允许请求时为 0。</param>
        /// <returns>两个限流条件都允许时返回 true，否则返回 false。</returns>
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

        /// <summary>
        /// 在指定键的固定时间窗口中消费一次额度，并计算超限后的剩余等待时间。
        /// </summary>
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

        /// <summary>
        /// 到达预定清理时间后，移除已过期的 IP 和账号窗口，防止缓存持续增长。
        /// </summary>
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

        /// <summary>
        /// 收集并删除字典中已经超过限流窗口的键；分两步处理以避免遍历时修改字典。
        /// </summary>
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

        /// <summary>
        /// 获取以毫秒表示的登录限流窗口，配置小于 1 秒时按 1 秒处理。
        /// </summary>
        private static long GetWindowMilliseconds()
        {
            return Math.Max(1, Options.Instance.LoginRateWindowSeconds) * 1000L;
        }
    }
}
