using System;


namespace ET.Server
{
    [MessageSessionHandler(SceneType.Gate)]
    public class C2G_LoginGateHandler : MessageSessionHandler<C2G_LoginGate, G2C_LoginGate>
    {
        protected override async ETTask Run(Session session, C2G_LoginGate request, G2C_LoginGate response)
        {
            Scene root = session.Root();
            string account = root.GetComponent<GateSessionKeyComponent>().Take(request.Key);
            if (account == null)
            {
                response.Error = ErrorCore.ERR_ConnectGateKeyError;
                response.Message = "Gate key验证失败!";
                return;
            }
            
            session.RemoveComponent<SessionAcceptTimeoutComponent>();

            PlayerComponent playerComponent = root.GetComponent<PlayerComponent>();
            using (await root.GetComponent<CoroutineLockComponent>().Wait(
                       CoroutineLockType.PlayerSession,
                       account.GetLongHashCode()))
            {
                try
                {
                    DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
                    var accounts = await database.Query<Account>(account.GetLongHashCode(), stored => stored.LoginName == account);
                    if (accounts.Count != 1 || accounts[0].Status != AccountStatus.Active)
                    {
                        response.Error = ErrorCore.ERR_InvalidAccountOrPassword;
                        response.Message = "登录凭据已失效";
                        return;
                    }
                }
                catch (Exception)
                {
                    response.Error = ErrorCore.ERR_AccountStorageUnavailable;
                    response.Message = "账号服务暂时不可用";
                    return;
                }
                Player player = playerComponent.GetByAccount(account);
                if (player == null)
                {
                    RoleLoadResult roleLoadResult = await RoleRepository.LoadOrCreate(root, account);
                    if (!roleLoadResult.IsSuccess)
                    {
                        response.Error = roleLoadResult.Error;
                        response.Message = roleLoadResult.Message;
                        return;
                    }

                    RoleSnapshot role = roleLoadResult.Role;
                    player = playerComponent.AddChildWithId<Player, string>(role.Id, account);
                    ApplyRoleSnapshot(player, role);
                    playerComponent.Add(player);
                    PlayerSessionComponent playerSessionComponent = player.AddComponent<PlayerSessionComponent>();
                    playerSessionComponent.AddComponent<MailBoxComponent, MailBoxType>(MailBoxType.GateSession);
                    await playerSessionComponent.AddLocation(LocationType.GateSession);

                    player.AddComponent<MailBoxComponent, MailBoxType>(MailBoxType.UnOrderedMessage);
                    await player.AddLocation(LocationType.Player);

                    BindSession(player, session);
                }
                else
                {
                    if (player.IsDisposed)
                    {
                        response.Error = ErrorCore.ERR_PlayerStateConflict;
                        response.Message = "玩家状态已变化，请重新登录";
                        return;
                    }

                    player.RemoveComponent<PlayerOfflineComponent>();
                    PlayerRoomComponent playerRoomComponent = player.GetComponent<PlayerRoomComponent>();
                    if (playerRoomComponent != null && playerRoomComponent.RoomActorId != default)
                    {
                        BindSession(player, session);
                        await CheckRoom(player, session);
                    }
                    else
                    {
                        DetachOldSession(player);
                        if (!await PlayerOfflineComponentSystem.TryReleaseMapState(root, player.Id))
                        {
                            response.Error = ErrorCore.ERR_RoleStorageUnavailable;
                            response.Message = "角色保存尚未完成，请稍后重试";
                            return;
                        }

                        RoleLoadResult roleLoadResult = await RoleRepository.LoadOrCreate(root, account);
                        if (!roleLoadResult.IsSuccess)
                        {
                            response.Error = roleLoadResult.Error;
                            response.Message = roleLoadResult.Message;
                            return;
                        }

                        ApplyRoleSnapshot(player, roleLoadResult.Role);
                        BindSession(player, session);
                    }
                }

                response.PlayerId = player.Id;
            }
        }

        private static async ETTask CheckRoom(Player player, Session session)
        {
            Fiber fiber = player.Fiber();
            await fiber.WaitFrameFinish();

            G2Room_Reconnect g2RoomReconnect = G2Room_Reconnect.Create();
            g2RoomReconnect.PlayerId = player.Id;
            using Room2G_Reconnect room2GateReconnect = (await fiber.Root.CallService<Room2G_Reconnect>(
                ServiceAddress.From(player.GetComponent<PlayerRoomComponent>().RoomActorId),
                g2RoomReconnect)).ResponseOrThrow();
            G2C_Reconnect g2CReconnect = G2C_Reconnect.Create();
            g2CReconnect.StartTime = room2GateReconnect.StartTime;
            g2CReconnect.Frame = room2GateReconnect.Frame;
            g2CReconnect.UnitInfos.AddRange(room2GateReconnect.UnitInfos);
            if (!session.IsDisposed && player.GetComponent<PlayerSessionComponent>().Session == session)
            {
                session.Send(g2CReconnect);
            }
        }

        private static void ApplyRoleSnapshot(Player player, RoleSnapshot role)
        {
            player.MapEntered = false;
            player.RoleName = role.Name;
            player.LastMapName = role.MapName;
            player.PositionX = role.Position.x;
            player.PositionY = role.Position.y;
            player.PositionZ = role.Position.z;
        }

        private static void BindSession(Player player, Session session)
        {
            PlayerSessionComponent playerSessionComponent = player.GetComponent<PlayerSessionComponent>();
            Session previousSession = playerSessionComponent.Session;
            SessionPlayerComponent sessionPlayerComponent = session.GetComponent<SessionPlayerComponent>() ??
                    session.AddComponent<SessionPlayerComponent>();
            sessionPlayerComponent.Player = player;
            playerSessionComponent.Session = session;

            if (previousSession != null && previousSession != session && !previousSession.IsDisposed)
            {
                previousSession.RemoveComponent<SessionPlayerComponent>();
                previousSession.Dispose();
            }
        }

        private static void DetachOldSession(Player player)
        {
            PlayerSessionComponent playerSessionComponent = player.GetComponent<PlayerSessionComponent>();
            Session previousSession = playerSessionComponent.Session;
            playerSessionComponent.Session = null;
            if (previousSession == null || previousSession.IsDisposed)
            {
                return;
            }

            previousSession.RemoveComponent<SessionPlayerComponent>();
            previousSession.Dispose();
        }
    }
}
