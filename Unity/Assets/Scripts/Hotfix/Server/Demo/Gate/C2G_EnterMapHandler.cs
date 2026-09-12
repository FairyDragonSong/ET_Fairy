namespace ET.Server
{
	[MessageSessionHandler(SceneType.Gate)]
	public class C2G_EnterMapHandler : MessageSessionHandler<C2G_EnterMap, G2C_EnterMap>
	{
		protected override async ETTask Run(Session session, C2G_EnterMap request, G2C_EnterMap response)
		{
			SessionPlayerComponent sessionPlayerComponent = session.GetComponent<SessionPlayerComponent>();
			Player player = sessionPlayerComponent?.Player;
			if (player == null || player.IsDisposed || player.GetComponent<PlayerSessionComponent>().Session != session)
			{
				response.Error = ErrorCore.ERR_PlayerStateConflict;
				response.Message = "登录会话已失效，请重新登录";
				return;
			}

			if (player.MapEntered || player.GetComponent<GateMapComponent>() != null)
			{
				response.Error = ErrorCore.ERR_PlayerStateConflict;
				response.Message = "正在进入地图，请勿重复请求";
				return;
			}

			// 在Gate上动态创建一个Map Scene，把Unit从DB中加载放进来，然后传送到真正的Map中，这样登陆跟传送的逻辑就完全一样了
			player.MapEntered = true;
			GateMapComponent gateMapComponent = player.AddComponent<GateMapComponent>();
			gateMapComponent.Scene = await GateMapFactory.Create(gateMapComponent, player.Id, IdGenerater.Instance.GenerateInstanceId(), "GateMap");

			Scene scene = gateMapComponent.Scene;
			
			// 这里可以从DB中加载Unit
			Unit unit = UnitFactory.Create(
				scene,
				player.Id,
				UnitType.Player,
				new Unity.Mathematics.float3(player.PositionX, player.PositionY, player.PositionZ));
			
			string mapName = string.IsNullOrEmpty(player.LastMapName) ? "Map1" : player.LastMapName;
			StartSceneConfig startSceneConfig = StartSceneConfigCategory.Instance.GetBySceneName(session.Zone(), mapName);
			response.MyId = player.Id;

			// 等到一帧的最后面再传送，先让G2C_EnterMap返回，否则传送消息可能比G2C_EnterMap还早
			TransferAndCleanup(player, gateMapComponent, unit, startSceneConfig.ActorId, startSceneConfig.Name).Coroutine();
		}

		private static async ETTask TransferAndCleanup(
			Player player,
			GateMapComponent gateMapComponent,
			Unit unit,
			ActorId mapActorId,
			string mapName)
		{
			try
			{
				await TransferHelper.TransferAtFrameFinish(unit, mapActorId, mapName);
			}
			finally
			{
				if (!player.IsDisposed && player.GetComponent<GateMapComponent>() == gateMapComponent)
				{
					player.RemoveComponent<GateMapComponent>();
				}
			}
		}
	}
}
