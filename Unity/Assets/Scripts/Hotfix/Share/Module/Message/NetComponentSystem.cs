using System.Net;
using System.Net.Sockets;

namespace ET
{
    [EntitySystemOf(typeof(NetComponent))]
    [FriendOf(typeof(NetComponent))]
    public static partial class NetComponentSystem
    {
        [EntitySystem]
        private static void Awake(this NetComponent self, IPEndPoint address, NetworkProtocol protocol)
        {
            self.AService = new KService(address, protocol, ServiceType.Outer);
            self.AService.AcceptCallback = self.OnAccept;
            self.AService.ReadCallback = self.OnRead;
            self.AService.ErrorCallback = self.OnError;
        }
        
        [EntitySystem]
        private static void Awake(this NetComponent self, AddressFamily addressFamily, NetworkProtocol protocol)
        {
#if ((UNITY_WEBGL || UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME) && !UNITY_EDITOR) || ET_MINIGAME_HOTFIX
            if (protocol == NetworkProtocol.Websocket)
            {
                self.AService = new KService(
                    new MiniGameWebSocketTransport(),
                    protocol,
                    ServiceType.Outer);
            }
            else
#endif
            {
                self.AService = new KService(addressFamily, protocol, ServiceType.Outer);
            }
            self.AService.ReadCallback = self.OnRead;
            self.AService.ErrorCallback = self.OnError;
        }
        
        [EntitySystem]
        private static void Update(this NetComponent self)
        {
            // 初始化失败时不再让每帧更新重复抛出空引用。
            self.AService?.Update();
        }

        [EntitySystem]
        private static void Destroy(this NetComponent self)
        {
            // 允许回收尚未完成初始化的网络组件。
            self.AService?.Dispose();
        }

        private static void OnError(this NetComponent self, long channelId, int error)
        {
            Session session = self.GetChild<Session>(channelId);
            if (session == null)
            {
                return;
            }

            session.Error = error;
            session.Dispose();
        }

        // 这个channelId是由CreateAcceptChannelId生成的
        private static void OnAccept(this NetComponent self, long channelId, IPEndPoint ipEndPoint)
        {
            Session session = self.AddChildWithId<Session, AService>(channelId, self.AService);
            session.RemoteAddress = ipEndPoint;

            if (self.IScene.SceneType != SceneType.BenchmarkServer)
            {
                // 挂上这个组件，5秒就会删除session，所以客户端验证完成要删除这个组件。该组件的作用就是防止外挂一直连接不发消息也不进行权限验证
                session.AddComponent<SessionAcceptTimeoutComponent>();
                // 客户端连接，2秒检查一次recv消息，10秒没有消息则断开
                session.AddComponent<SessionIdleCheckerComponent>();
                // 只对真实外部客户端启用包数与字节数限流，基准连接不参与统计。
                session.AddComponent<SessionTrafficLimitComponent>();
            }
        }
        
        private static void OnRead(this NetComponent self, long channelId, MemoryBuffer memoryBuffer)
        {
            Session session = self.GetChild<Session>(channelId);
            if (session == null)
            {
                self.AService.Recycle(memoryBuffer);
                return;
            }

            // 反序列化前先限流，避免大包或高频消息消耗业务线程资源。
            SessionTrafficLimitComponent trafficLimit = session.GetComponent<SessionTrafficLimitComponent>();
            if (trafficLimit != null && !trafficLimit.TryConsume((int)memoryBuffer.Length))
            {
                self.AService.Recycle(memoryBuffer);
                Log.Warning($"outer session rate limited: {session.RemoteAddress}");
                session.Error = ErrorCore.ERR_OuterSessionRateLimited;
                session.Dispose();
                return;
            }
            session.LastRecvTime = TimeInfo.Instance.ClientNow();
            
            (ActorId _, object message) = MessageSerializeHelper.ToMessage(self.AService, memoryBuffer);
            self.AService.Recycle(memoryBuffer);
            
            LogMsg.Instance.Debug(self.Fiber(), message);
            
            EventSystem.Instance.Invoke((long)self.IScene.SceneType, new NetComponentOnRead() {Session = session, Message = message});
        }
        
        public static Session Create(this NetComponent self, IPEndPoint realIPEndPoint)
        {
            long channelId = NetServices.Instance.CreateConnectChannelId();
            Session session = self.AddChildWithId<Session, AService>(channelId, self.AService);
            session.RemoteAddress = realIPEndPoint;
            if (self.IScene.SceneType != SceneType.BenchmarkClient)
            {
                session.AddComponent<SessionIdleCheckerComponent>();
            }
            
            self.AService.Create(session.Id, session.RemoteAddress);

            return session;
        }

        public static Session Create(this NetComponent self, IPEndPoint routerIPEndPoint, IPEndPoint realIPEndPoint, uint localConn)
        {
            long channelId = localConn;
            Session session = self.AddChildWithId<Session, AService>(channelId, self.AService);
            session.RemoteAddress = realIPEndPoint;
            if (self.IScene.SceneType != SceneType.BenchmarkClient)
            {
                session.AddComponent<SessionIdleCheckerComponent>();
            }
            self.AService.Create(session.Id, routerIPEndPoint);
            return session;
        }
    }

    [EntitySystemOf(typeof(SessionTrafficLimitComponent))]
    [FriendOf(typeof(SessionTrafficLimitComponent))]
    // 按固定一秒窗口限制单个外部会话的消息数和累计字节数。
    public static partial class SessionTrafficLimitComponentSystem
    {
        private const long WindowMilliseconds = 1000;

        [EntitySystem]
        // 从会话建立时开始计算第一个流量窗口。
        private static void Awake(this SessionTrafficLimitComponent self)
        {
            self.WindowStartTime = TimeInfo.Instance.ClientNow();
        }

        // 尝试消费一个数据包额度；任一上限超出时拒绝整个数据包。
        public static bool TryConsume(this SessionTrafficLimitComponent self, int packetBytes)
        {
            long now = TimeInfo.Instance.ClientNow();
            // 窗口到期时同时重置包数和字节数，保证两类限制使用同一时间边界。
            if (now - self.WindowStartTime >= WindowMilliseconds)
            {
                self.WindowStartTime = now;
                self.MessageCount = 0;
                self.ByteCount = 0;
            }

            int messageLimit = System.Math.Max(1, Options.Instance.OuterMessageLimit);
            int bytesLimit = System.Math.Max(1, Options.Instance.OuterBytesLimit);
            if (self.MessageCount >= messageLimit || self.ByteCount + packetBytes > bytesLimit)
            {
                return false;
            }

            ++self.MessageCount;
            self.ByteCount += packetBytes;
            return true;
        }
    }
}
