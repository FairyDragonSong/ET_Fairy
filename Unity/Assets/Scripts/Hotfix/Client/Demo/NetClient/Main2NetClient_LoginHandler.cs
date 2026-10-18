using System;
using System.Net;
using System.Net.Sockets;

namespace ET.Client
{
    [MessageHandler(SceneType.NetClient)]
    public class Main2NetClient_LoginHandler: MessageHandler<Scene, Main2NetClient_Login, NetClient2Main_Login>
    {
        protected override async ETTask Run(Scene root, Main2NetClient_Login request, NetClient2Main_Login response)
        {
            Log.Warning("client login: network handler entered");
            string account = request.Account;
            string password = request.Password;
            string stage = "router-discovery";
            try
            {
                // 创建一个ETModel层的Session
                root.RemoveComponent<RouterAddressComponent>();
                // 获取路由跟realmDispatcher地址
                RouterAddressComponent routerAddressComponent =
                    root.AddComponent<RouterAddressComponent, string, int>(ConstValue.RouterHttpHost, ConstValue.RouterHttpPort);
                await routerAddressComponent.Init();
                Log.Warning("client login: router discovery complete");
                stage = "transport-create";
#if ((UNITY_WEBGL || UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME) && !UNITY_EDITOR) || ET_MINIGAME_HOTFIX
                const NetworkProtocol clientProtocol = NetworkProtocol.Websocket;
#else
                const NetworkProtocol clientProtocol = NetworkProtocol.UDP;
#endif
                root.AddComponent<NetComponent, AddressFamily, NetworkProtocol>(
                    routerAddressComponent.RouterManagerIPAddress.AddressFamily,
                    clientProtocol);
                root.GetComponent<FiberParentComponent>().ParentFiberId = request.OwnerFiberId;

                NetComponent netComponent = root.GetComponent<NetComponent>();

                IPEndPoint realmAddress = routerAddressComponent.GetRealmAddress(account);

                R2C_Login r2CLogin;
                stage = "realm-session";
                Log.Warning("client login: connecting to realm");
                using (Session session = await netComponent.CreateRouterSession(realmAddress, account, password))
                {
                    stage = "realm-login";
                    C2R_Login c2RLogin = C2R_Login.Create();
                    c2RLogin.Account = account;
                    c2RLogin.Password = password;
                    r2CLogin = (R2C_Login)await session.Call(c2RLogin);
                }

                if (r2CLogin.Error != 0)
                {
                    // Realm 认证失败时停止建连 Gate，并把原始错误返回主线程。
                    response.Error = r2CLogin.Error;
                    response.Message = r2CLogin.Message;
                    return;
                }

                // 创建一个gate Session,并且保存到SessionComponent中
                stage = "gate-session";
                Session gateSession = await netComponent.CreateRouterSession(NetworkHelper.ToIPEndPoint(r2CLogin.Address), account, password);
                gateSession.AddComponent<ClientSessionErrorComponent>();
                root.AddComponent<SessionComponent>().Session = gateSession;
                C2G_LoginGate c2GLoginGate = C2G_LoginGate.Create();
                c2GLoginGate.Key = r2CLogin.Key;
                c2GLoginGate.GateId = r2CLogin.GateId;
                stage = "gate-login";
                G2C_LoginGate g2CLoginGate = (G2C_LoginGate)await gateSession.Call(c2GLoginGate);
                if (g2CLoginGate.Error != 0)
                {
                    // Gate 登录失败同样透传，避免客户端误判为登录成功。
                    response.Error = g2CLoginGate.Error;
                    response.Message = g2CLoginGate.Message;
                    return;
                }

                Log.Debug("登陆gate成功!");

                response.PlayerId = g2CLoginGate.PlayerId;
            }
            catch (Exception exception)
            {
                // 仅记录失败阶段和异常类型，避免账号或密码进入日志。
                Log.Warning($"client login stage failed: {stage}, type={exception.GetType().Name}");
                throw;
            }
        }
    }
}
