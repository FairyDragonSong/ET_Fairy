namespace ET.Server
{
    [EntitySystemOf(typeof(SessionPlayerComponent))]
    [FriendOf(typeof(SessionPlayerComponent))]
    public static partial class SessionPlayerComponentSystem
    {
        [EntitySystem]
        private static void Destroy(this SessionPlayerComponent self)
        {
            if(self.MetricsBound)
            {
                self.MetricsBound=false;
                OperationalMetrics.Instance?.RecordGateSession(false);
            }
            Scene root = self.Root();
            if (root.IsDisposed)
            {
                return;
            }
            Player player = self.Player;
            if (player == null || player.IsDisposed)
            {
                return;
            }

            PlayerSessionComponent playerSessionComponent = player.GetComponent<PlayerSessionComponent>();
            if (playerSessionComponent.Session != self.GetParent<Session>())
            {
                return;
            }

            playerSessionComponent.Session = null;
            PlayerOfflineComponentSystem.SaveAndRetainOfflinePlayer(root, player, player.InstanceId).Coroutine();
        }
        
        [EntitySystem]
        private static void Awake(this SessionPlayerComponent self)
        {
            self.MetricsBound=false;
        }

        // 记录当前会话绑定，供断线与替换流程判定。
        public static void TrackBinding(this SessionPlayerComponent self)
        {
            if(self.MetricsBound)return;
            OperationalMetrics.Instance.RecordGateSession(true);
            self.MetricsBound=true;
        }
    }

    [EntitySystemOf(typeof(PlayerOfflineComponent))]
    [FriendOf(typeof(PlayerOfflineComponent))]
    public static partial class PlayerOfflineComponentSystem
    {
        private const int OfflineRetentionTime = 30_000;

        [Invoke(TimerInvokeType.PlayerOffline)]
        public class PlayerOfflineTimer: ATimer<PlayerOfflineComponent>
        {
            // 离线宽限期结束后触发角色保存与资源回收。
            protected override void Run(PlayerOfflineComponent self)
            {
                self.RemoveOfflinePlayer().Coroutine();
            }
        }

        [EntitySystem]
        // 初始化当前组件的运行状态。
        private static void Awake(this PlayerOfflineComponent self)
        {
            self.Timer = self.Root().GetComponent<TimerComponent>().NewOnceTimer(
                TimeInfo.Instance.ServerNow() + OfflineRetentionTime,
                TimerInvokeType.PlayerOffline,
                self);
        }

        [EntitySystem]
        // 释放当前组件持有的状态与未完成工作。
        private static void Destroy(this PlayerOfflineComponent self)
        {
            self.Root().GetComponent<TimerComponent>()?.Remove(ref self.Timer);
        }

        // 宽限期结束后保存并释放离线玩家。
        private static async ETTask RemoveOfflinePlayer(this PlayerOfflineComponent self)
        {
            Player player = self.GetParent<Player>();
            long instanceId = player.InstanceId;
            Scene root = self.Root();
            using (await root.GetComponent<CoroutineLockComponent>().Wait(
                       CoroutineLockType.PlayerSession,
                       player.Account.GetLongHashCode()))
            {
                if (self.IsDisposed || player.InstanceId != instanceId)
                {
                    return;
                }

                PlayerSessionComponent playerSessionComponent = player.GetComponent<PlayerSessionComponent>();
                if (playerSessionComponent.Session != null)
                {
                    return;
                }

                await playerSessionComponent.RemoveLocation(LocationType.GateSession);
                if (player.InstanceId != instanceId || playerSessionComponent.Session != null)
                {
                    return;
                }

                await player.RemoveLocation(LocationType.Player);
                if (player.InstanceId != instanceId || playerSessionComponent.Session != null)
                {
                    return;
                }

                player.GetParent<PlayerComponent>().Remove(player);
            }
        }

        // 保存离线玩家，同时保留可恢复的地图状态。
        public static async ETTask SaveAndRetainOfflinePlayer(Scene root, Player player, long playerInstanceId)
        {
            using (await root.GetComponent<CoroutineLockComponent>().Wait(
                       CoroutineLockType.PlayerSession,
                       player.Account.GetLongHashCode()))
            {
                if (player.InstanceId != playerInstanceId || player.GetComponent<PlayerSessionComponent>().Session != null)
                {
                    return;
                }

                PlayerRoomComponent playerRoomComponent = player.GetComponent<PlayerRoomComponent>();
                if (playerRoomComponent != null && playerRoomComponent.RoomActorId != default)
                {
                    return;
                }

                if (!await TryReleaseMapState(root, player.Id))
                {
                    return;
                }

                if (player.InstanceId == playerInstanceId &&
                    player.GetComponent<PlayerSessionComponent>().Session == null &&
                    player.GetComponent<PlayerOfflineComponent>() == null)
                {
                    player.AddComponent<PlayerOfflineComponent>();
                }
            }
        }

        // 仅在玩家仍离线时释放地图状态。
        public static async ETTask<bool> TryReleaseMapState(Scene root, long playerId)
        {
            RpcCallResult<M2G_SessionDisconnect> result = await root.CallEntity<M2G_SessionDisconnect>(
                EntityAddress.From(LocationType.Unit, playerId),
                G2M_SessionDisconnect.Create());
            bool released = result.IsSuccess || result.FailureKind == RpcFailureKind.TargetNotFound;
            if (!released)
            {
                Log.Error($"keep player because map final save was not confirmed: {playerId}, " +
                          $"failure={result.FailureKind}, error={result.Error}, message={result.Message}");
            }
            result.Response?.Dispose();
            return released;
        }
    }
}
