namespace ET.Server
{
    [FriendOf(typeof(GateSessionKeyComponent))]
    public static partial class GateSessionKeyComponentSystem
    {
        // 保存一张限时、一次性的 Gate 登录票据。
        public static void Add(this GateSessionKeyComponent self, long key, string account, long stateVersion)
        {
            self.sessionKey.Add(key, new GateLoginTicket(account, stateVersion));
            self.TimeoutRemoveKey(key).Coroutine();
        }

        // 生成并登记一张 Gate 登录票据。
        public static long Issue(this GateSessionKeyComponent self, string account, long stateVersion)
        {
            long key;
            do
            {
                key = GenerateSecureKey();
            }
            while (key == 0 || self.sessionKey.ContainsKey(key));

            self.Add(key, account, stateVersion);
            return key;
        }

        // 使用加密随机数生成不可预测的登录密钥。
        public static long GenerateSecureKey()
        {
            System.Span<byte> bytes = stackalloc byte[sizeof(long)];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return System.BitConverter.ToInt64(bytes) & long.MaxValue;
        }

        /// <summary>
        /// 消费一次性登录 Key。无论后续登录流程是否成功，同一个 Key 都不能再次使用。
        /// Gate Fiber 内的消息按顺序执行，因此查询和删除在同一执行上下文中完成。
        /// </summary>
        public static GateLoginTicket Take(this GateSessionKeyComponent self, long key)
        {
            if (!self.sessionKey.TryGetValue(key, out GateLoginTicket account))
            {
                return null;
            }

            self.sessionKey.Remove(key);
            return account;
        }

        private static async ETTask TimeoutRemoveKey(this GateSessionKeyComponent self, long key)
        {
            await self.Root().GetComponent<TimerComponent>().WaitAsync(20000);
            self.sessionKey.Remove(key);
        }
    }
}
