namespace ET.Server
{
    [FriendOf(typeof(GateSessionKeyComponent))]
    public static partial class GateSessionKeyComponentSystem
    {
        public static void Add(this GateSessionKeyComponent self, long key, string account)
        {
            self.sessionKey.Add(key, account);
            self.TimeoutRemoveKey(key).Coroutine();
        }

        /// <summary>
        /// 消费一次性登录 Key。无论后续登录流程是否成功，同一个 Key 都不能再次使用。
        /// Gate Fiber 内的消息按顺序执行，因此查询和删除在同一执行上下文中完成。
        /// </summary>
        public static string Take(this GateSessionKeyComponent self, long key)
        {
            if (!self.sessionKey.TryGetValue(key, out string account))
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
