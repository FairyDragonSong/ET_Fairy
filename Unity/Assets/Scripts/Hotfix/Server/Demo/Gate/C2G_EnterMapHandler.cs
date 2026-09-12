using System;

namespace ET.Server
{
    [MessageSessionHandler(SceneType.Gate)]
    public class C2G_EnterMapHandler : MessageSessionHandler<C2G_EnterMap, G2C_EnterMap>
    {
        protected override async ETTask Run(Session session, C2G_EnterMap request, G2C_EnterMap response)
        {
            OperationalMetrics.Instance.RecordMapEntryAttempt();
            bool outcomeRecorded = false;
            Player player = null;
            GateMapComponent gateMapComponent = null;
            try
            {
                SessionPlayerComponent sessionPlayerComponent = session.GetComponent<SessionPlayerComponent>();
                player = sessionPlayerComponent?.Player;
                if (player == null || player.IsDisposed || player.GetComponent<PlayerSessionComponent>().Session != session)
                {
                    response.Error = ErrorCore.ERR_PlayerStateConflict;
                    response.Message = "登录会话已失效，请重新登录";
                    OperationalMetrics.Instance.RecordMapEntryRejected();
                    outcomeRecorded = true;
                    return;
                }

                if (player.MapEntered || player.GetComponent<GateMapComponent>() != null)
                {
                    response.Error = ErrorCore.ERR_PlayerStateConflict;
                    response.Message = "正在进入地图，请勿重复请求";
                    OperationalMetrics.Instance.RecordMapEntryRejected();
                    outcomeRecorded = true;
                    return;
                }

                player.MapEntered = true;
                gateMapComponent = player.AddComponent<GateMapComponent>();
                gateMapComponent.Scene = await GateMapFactory.Create(
                    gateMapComponent,
                    player.Id,
                    IdGenerater.Instance.GenerateInstanceId(),
                    "GateMap");

                Unit unit = UnitFactory.Create(
                    gateMapComponent.Scene,
                    player.Id,
                    UnitType.Player,
                    new Unity.Mathematics.float3(player.PositionX, player.PositionY, player.PositionZ));

                string mapName = string.IsNullOrEmpty(player.LastMapName) ? "Map1" : player.LastMapName;
                StartSceneConfig startSceneConfig = StartSceneConfigCategory.Instance.GetBySceneName(session.Zone(), mapName);
                response.MyId = player.Id;

                // 先返回进入请求，再在帧尾完成传送；真实完成由传送终态指标记录。
                OperationalMetrics.Instance.StartMapTransfer();
                outcomeRecorded = true;
                TransferAndCleanup(player, gateMapComponent, unit, startSceneConfig.ActorId, startSceneConfig.Name).Coroutine();
            }
            catch
            {
                if (!outcomeRecorded)
                {
                    OperationalMetrics.Instance?.RecordMapEntryServiceError();
                    ResetFailedTransfer(player, gateMapComponent);
                }
                throw;
            }
        }

        internal static async ETTask TransferAndCleanup(
            Player player,
            GateMapComponent gateMapComponent,
            Unit unit,
            ActorId mapActorId,
            string mapName,
            Func<ETTask> transfer = null)
        {
            bool success = false;
            try
            {
                if (transfer == null) await TransferHelper.TransferAtFrameFinish(unit, mapActorId, mapName);
                else await transfer();
                success = true;
            }
            catch
            {
                if (!player.IsDisposed) player.MapEntered = false;
                throw;
            }
            finally
            {
                OperationalMetrics.Instance?.FinishMapTransfer(success);
                if (!player.IsDisposed && player.GetComponent<GateMapComponent>() == gateMapComponent)
                {
                    player.RemoveComponent<GateMapComponent>();
                }
            }
        }

        private static void ResetFailedTransfer(Player player, GateMapComponent gateMapComponent)
        {
            if (player == null || player.IsDisposed) return;
            player.MapEntered = false;
            if (gateMapComponent != null && player.GetComponent<GateMapComponent>() == gateMapComponent)
            {
                player.RemoveComponent<GateMapComponent>();
            }
        }
    }
}
