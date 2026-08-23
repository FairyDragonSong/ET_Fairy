

namespace ET.Server
{
	[MessageLocationHandler(SceneType.Map)]
	public class G2M_SessionDisconnectHandler : MessageLocationHandler<Unit, G2M_SessionDisconnect, M2G_SessionDisconnect>
	{
		protected override async ETTask Run(Unit unit, G2M_SessionDisconnect request, M2G_SessionDisconnect response)
		{
			RoleSaveComponent roleSaveComponent = unit.GetComponent<RoleSaveComponent>();
			for (int retry = 0; retry < 3; ++retry)
			{
				if (roleSaveComponent != null && await roleSaveComponent.SaveNow())
				{
					await unit.RemoveLocation(LocationType.Unit);
					unit.GetParent<UnitComponent>().Remove(unit.Id);
					return;
				}

				await unit.Root().GetComponent<TimerComponent>().WaitAsync(200);
			}

			response.Error = ErrorCore.ERR_RoleStorageUnavailable;
			response.Message = "final role save failed; map unit retained";
			Log.Error($"keep offline unit because final role save failed: {unit.Id}");
		}
	}
}
