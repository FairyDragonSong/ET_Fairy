# ET8 框架 · 客户端点击登录 完整流程解析

> 入口：`HotfixView/Client/YIUISystem/Login/LoginPanelComponentSystem.cs · OnEventLoginAction`
> 涉及架构：**Realm + Gate + Router** 三件套 · **主 Fiber + NetClient Fiber** 双纤程
> 本文档基于实际代码走读整理。

---

## 一、总览图

```
【Main Fiber (SceneType.Main)】         【NetClient Fiber (SceneType.NetClient)】         【Router】           【Realm 服务】              【Gate 服务】

 点击登录按钮
    │
    │ OnEventLoginAction
    ▼
 LoginHelper.Login
    │ AddComponent<ClientSenderComponent>
    │ clientSenderComponent.LoginAsync
    │
    │ ①  FiberManager.Create(NetClient)  ────►  创建 NetClient Fiber
    │                                            挂 ProcessInnerSender/MailBox/Timer
    │
    │ ②  ProcessInnerSender.Call(
    │      Main2NetClient_Login)
    │     ─── 同进程内存队列 ──────────►  Main2NetClient_LoginHandler.Run
    │                                            │
    │                                            │ ③  AddComponent<NetComponent>(UDP/KCP)
    │                                            │
    │                                            │ ④  CreateRouterSession(realmAddress)
    │                                            │     ── RouterSYN ────────►  Router
    │                                            │     ◄── RouterACK ───────
    │                                            │
    │                                            │ ⑤  session.Call(C2R_Login)
    │                                            │     ─────────── KCP ───────────────────►  C2R_LoginHandler
    │                                            │                                                │
    │                                            │                                                │ ⑥  MessageSender.Call(R2G_GetLoginKey)
    │                                            │                                                │     Inner 网络 ────────────►  R2G_GetLoginKeyHandler
    │                                            │                                                │                                    │
    │                                            │                                                │                                    │ 生成 Key
    │                                            │                                                │                                    │ GateSessionKeyComponent.Add(key, account)
    │                                            │                                                │ ◄── G2R_GetLoginKey ──────────
    │                                            │ ◄── R2C_Login{Address, Key, GateId} ────
    │                                            │
    │                                            │ ⑦  关闭 Realm Session
    │                                            │     CreateRouterSession(GateAddress)
    │                                            │     ─────────────── KCP ─────────────────────────────────────────────►  C2G_LoginGateHandler
    │                                            │                                                                            │
    │                                            │                                                                            │ 验证 Key
    │                                            │                                                                            │ 创建 Player + MailBox
    │                                            │                                                                            │ AddLocation(Player)
    │                                            │ ◄── G2C_LoginGate{PlayerId} ─────────────────────────────────────
    │                                            │
    │                                            │ response.PlayerId = g2CLoginGate.PlayerId
    │ ◄────────────────────── NetClient2Main_Login{PlayerId} ───
    │
    │ PlayerComponent.MyId = playerId
    │ EventSystem.PublishAsync(LoginFinish)
    ▼
 LoginFinish_RemoveLoginUI   → 关闭登录面板
 LoginFinish_CreateLobbyUI   → 打开大厅面板
```

---

## 二、涉及文件速查

### 客户端

| 文件 | 作用 |
|---|---|
| `HotfixView/Client/YIUISystem/Login/LoginPanelComponentSystem.cs` | UI 按钮回调入口 |
| `Hotfix/Client/Demo/NetClient/LoginHelper.cs` | 登录业务封装 |
| `Hotfix/Client/Demo/Main/ClientSenderComponentSystem.cs` | 创建 NetClient Fiber 并桥接 |
| `Hotfix/Client/Demo/NetClient/FiberInit_NetClient.cs` | NetClient Fiber 启动时组件挂载 |
| `Hotfix/Client/Demo/NetClient/Main2NetClient_LoginHandler.cs` | **核心**：连 Realm → 拿 Key → 连 Gate |
| `Hotfix/Client/Demo/NetClient/Router/RouterHelper.cs` | Router 中转握手 |

### 服务端

| 文件 | 作用 |
|---|---|
| `Hotfix/Server/Demo/Realm/C2R_LoginHandler.cs` | Realm：鉴权 → 选 Gate → 申请 key |
| `Hotfix/Server/Demo/Gate/R2G_GetLoginKeyHandler.cs` | Gate：生成 key 并登记 |
| `Hotfix/Server/Demo/Gate/C2G_LoginGateHandler.cs` | Gate：验证 key → 创建 Player 实体 |
| `Model/Server/Demo/Gate/GateSessionKeyComponent.cs` | Gate 的 key 暂存组件 |

### UI 层

| 文件 | 作用 |
|---|---|
| `HotfixView/Client/Demo/UI/UILogin/LoginFinish_RemoveLoginUI.cs` | 监听 LoginFinish → 关登录面板 |
| `HotfixView/Client/Demo/UI/UILobby/LoginFinish_CreateLobbyUI.cs` | 监听 LoginFinish → 开大厅面板 |

---

## 三、为什么要这么绕？—— Realm + Gate + Router 三件套

```
┌────────────────────────────────────────────────────────────────┐
│                                                                │
│   客户端                                                        │
│     │                                                          │
│     │  登录洪峰时                                               │
│     ├─────────►  Realm 服 (水平扩容, 无状态, 只鉴权)            │
│     │                │                                         │
│     │                │ 拿到"Gate 地址 + 一次性 Key"后关连接     │
│     │                ▼                                         │
│     │          真正游戏连接                                     │
│     └─────────►  Gate 服 (长连接, 持有玩家实体)                 │
│                      │                                         │
│                      └────►  Map / Location / Chat / ...       │
│                                                                │
│   Router = 外网 UDP 中转器, 解决 NAT 重绑 + 断线重连             │
│                                                                │
└────────────────────────────────────────────────────────────────┘
```

- **Realm**：扛登录洪峰，水平扩容，验证完就释放连接
- **Gate**：玩家整局游戏的长连接入口，持有 Player 实体
- **Router**：UDP/KCP 的中转层，解决 4G/5G 下 NAT 地址变化问题，同时登录流量和游戏流量可以物理隔离

---

## 四、逐阶段源码解析

### 阶段 0 · UI 按钮回调

**文件**：`LoginPanelComponentSystem.cs` (41-47)

```csharp
private static async ETTask OnEventLoginAction(this LoginPanelComponent self)
{
    Log.Info($"登录");
    var banId = YIUIMgrComponent.Inst.BanLayerOptionForever();        // 禁止重复点击
    await LoginHelper.Login(self.Root(), self.Account, self.Password);
    YIUIMgrComponent.Inst.RecoverLayerOptionForever(banId);           // 恢复点击
}
```

---

### 阶段 1 · LoginHelper 挂载 ClientSender

**文件**：`LoginHelper.cs`

```csharp
public static async ETTask Login(Scene root, string account, string password)
{
    root.RemoveComponent<ClientSenderComponent>();                    // 防重复
    ClientSenderComponent clientSenderComponent = root.AddComponent<ClientSenderComponent>();

    long playerId = await clientSenderComponent.LoginAsync(account, password);

    root.GetComponent<PlayerComponent>().MyId = playerId;
    await EventSystem.Instance.PublishAsync(root, new LoginFinish());  // 广播事件
}
```

> **关键抽象**：`ClientSenderComponent` 是主 Fiber 和 NetClient Fiber 的"桥"。所有网络相关的活都委托给它。

---

### 阶段 2 · 创建 NetClient Fiber（跨 Fiber 消息）

**文件**：`ClientSenderComponentSystem.cs`

```csharp
public static async ETTask<long> LoginAsync(this ClientSenderComponent self, string account, string password)
{
    // 开一条独立 Fiber 专门管网络，不卡主线程渲染
    self.fiberId = await FiberManager.Instance.Create(
        SchedulerType.ThreadPool, 0, SceneType.NetClient, "");
    self.netClientActorId = new ActorId(self.Fiber().Process, self.fiberId);

    Main2NetClient_Login main2NetClientLogin = Main2NetClient_Login.Create();
    main2NetClientLogin.OwnerFiberId = self.Fiber().Id;               // 告诉对面"我是谁"
    main2NetClientLogin.Account  = account;
    main2NetClientLogin.Password = password;

    // ★ 同进程跨 Fiber → 走 ProcessInnerSender（内存队列，零拷贝）
    NetClient2Main_Login response = await self.Root()
        .GetComponent<ProcessInnerSender>()
        .Call(self.netClientActorId, main2NetClientLogin) as NetClient2Main_Login;

    return response.PlayerId;
}
```

新建 Fiber 时的初始化 (`FiberInit_NetClient.cs`)：

```csharp
[Invoke((long)SceneType.NetClient)]
public class FiberInit_NetClient: AInvokeHandler<FiberInit, ETTask>
{
    public override async ETTask Handle(FiberInit fiberInit)
    {
        Scene root = fiberInit.Fiber.Root;
        root.AddComponent<MailBoxComponent, MailBoxType>(MailBoxType.UnOrderedMessage);
        root.AddComponent<TimerComponent>();
        root.AddComponent<CoroutineLockComponent>();
        root.AddComponent<ProcessInnerSender>();
        root.AddComponent<FiberParentComponent>();
        await ETTask.CompletedTask;
    }
}
```

> **此处没有走网络**，`ProcessInnerSender.Call` 只是把消息对象塞进 NetClient Fiber 的 `MessageQueue`。

---

### 阶段 3 · NetClient Fiber 里发起登录 ⭐

**文件**：`Main2NetClient_LoginHandler.cs`

这是**真正走网络**的核心 Handler：

```csharp
[MessageHandler(SceneType.NetClient)]
public class Main2NetClient_LoginHandler: MessageHandler<Scene, Main2NetClient_Login, NetClient2Main_Login>
{
    protected override async ETTask Run(Scene root, Main2NetClient_Login request, NetClient2Main_Login response)
    {
        string account  = request.Account;
        string password = request.Password;

        // -------- 3.1 初始化路由配置 + 网络组件 --------
        root.RemoveComponent<RouterAddressComponent>();
        RouterAddressComponent routerAddressComponent =
            root.AddComponent<RouterAddressComponent, string, int>(
                ConstValue.RouterHttpHost, ConstValue.RouterHttpPort);
        await routerAddressComponent.Init();    // 通过 HTTP 拉一份 Router IP 列表

        root.AddComponent<NetComponent, AddressFamily, NetworkProtocol>(
            routerAddressComponent.RouterManagerIPAddress.AddressFamily, NetworkProtocol.UDP);
        root.GetComponent<FiberParentComponent>().ParentFiberId = request.OwnerFiberId;

        NetComponent netComponent = root.GetComponent<NetComponent>();
        IPEndPoint realmAddress   = routerAddressComponent.GetRealmAddress(account);

        // -------- 3.2 连 Realm，发 C2R_Login --------
        R2C_Login r2CLogin;
        using (Session session = await netComponent.CreateRouterSession(realmAddress, account, password))
        {
            C2R_Login c2RLogin = C2R_Login.Create();
            c2RLogin.Account  = account;
            c2RLogin.Password = password;
            r2CLogin = (R2C_Login)await session.Call(c2RLogin);   // ← RPC 等响应
        }   // ← using 结束，自动关 Realm 连接

        // -------- 3.3 连 Gate (拿 Key 验证) --------
        Session gateSession = await netComponent.CreateRouterSession(
            NetworkHelper.ToIPEndPoint(r2CLogin.Address), account, password);
        gateSession.AddComponent<ClientSessionErrorComponent>();
        root.AddComponent<SessionComponent>().Session = gateSession;   // 保存 Gate Session

        C2G_LoginGate c2GLoginGate = C2G_LoginGate.Create();
        c2GLoginGate.Key    = r2CLogin.Key;
        c2GLoginGate.GateId = r2CLogin.GateId;
        G2C_LoginGate g2CLoginGate = (G2C_LoginGate)await gateSession.Call(c2GLoginGate);

        Log.Debug("登陆gate成功!");

        response.PlayerId = g2CLoginGate.PlayerId;          // 回包给主 Fiber
    }
}
```

两次 `session.Call` 对应前文 RPC 机制：
- 生成 `RpcId`，挂 `ETTask<IResponse>` 进字典
- 发包后 `await` 挂起当前协程
- 服务端回包（带同 `RpcId`），本端 `SetResult` 唤醒

---

### 阶段 4 · Realm 处理 C2R_Login（跨进程 RPC）

**文件**：`C2R_LoginHandler.cs`

```csharp
[MessageSessionHandler(SceneType.Realm)]
public class C2R_LoginHandler : MessageSessionHandler<C2R_Login, R2C_Login>
{
    protected override async ETTask Run(Session session, C2R_Login request, R2C_Login response)
    {
        // 随机分配一个 Gate
        StartSceneConfig config = RealmGateAddressHelper.GetGate(session.Zone(), request.Account);
        Log.Debug($"gate address: {config}");

        // ★ 跨进程给 Gate 发消息（走 ProcessOuterSender + Inner 网络）
        R2G_GetLoginKey r2GGetLoginKey = R2G_GetLoginKey.Create();
        r2GGetLoginKey.Account = request.Account;
        G2R_GetLoginKey g2RGetLoginKey = (G2R_GetLoginKey) await session.Fiber().Root
            .GetComponent<MessageSender>()
            .Call(config.ActorId, r2GGetLoginKey);

        response.Address = config.InnerIPPort.ToString();
        response.Key     = g2RGetLoginKey.Key;
        response.GateId  = g2RGetLoginKey.GateId;

        CloseSession(session).Coroutine();      // 1 秒后关 Realm 连接
    }

    private async ETTask CloseSession(Session session)
    {
        await session.Root().GetComponent<TimerComponent>().WaitAsync(1000);
        session.Dispose();
    }
}
```

`MessageSender.Call(config.ActorId, ...)` 背后就是 `ProcessOuterSender` —— 走 **Inner 网络包（4B 长度 + 16B ActorId + opcode + body）**。

---

### 阶段 5 · Gate 生成 Key

**文件**：`R2G_GetLoginKeyHandler.cs`

```csharp
[MessageHandler(SceneType.Gate)]
public class R2G_GetLoginKeyHandler : MessageHandler<Scene, R2G_GetLoginKey, G2R_GetLoginKey>
{
    protected override async ETTask Run(Scene scene, R2G_GetLoginKey request, G2R_GetLoginKey response)
    {
        long key = RandomGenerator.RandInt64();
        scene.GetComponent<GateSessionKeyComponent>().Add(key, request.Account);   // ★ 预留 key
        response.Key    = key;
        response.GateId = scene.Id;
        await ETTask.CompletedTask;
    }
}
```

> **关键**：Gate 在客户端到达之前就已经知道"哪个账号要来、对应什么 key"。

---

### 阶段 6 · Gate 验证 Key，创建 Player

**文件**：`C2G_LoginGateHandler.cs`

```csharp
[MessageSessionHandler(SceneType.Gate)]
public class C2G_LoginGateHandler : MessageSessionHandler<C2G_LoginGate, G2C_LoginGate>
{
    protected override async ETTask Run(Session session, C2G_LoginGate request, G2C_LoginGate response)
    {
        Scene root = session.Root();
        string account = root.GetComponent<GateSessionKeyComponent>().Get(request.Key);
        if (account == null)
        {
            response.Error   = ErrorCore.ERR_ConnectGateKeyError;
            response.Message = "Gate key验证失败!";
            return;
        }

        session.RemoveComponent<SessionAcceptTimeoutComponent>();     // ★ 撤掉防外挂组件

        PlayerComponent playerComponent = root.GetComponent<PlayerComponent>();
        Player player = playerComponent.GetByAccount(account);
        if (player == null)
        {
            player = playerComponent.AddChild<Player, string>(account);
            playerComponent.Add(player);

            PlayerSessionComponent playerSessionComponent = player.AddComponent<PlayerSessionComponent>();
            playerSessionComponent.AddComponent<MailBoxComponent, MailBoxType>(MailBoxType.GateSession);
            await playerSessionComponent.AddLocation(LocationType.GateSession);         // ★ 登记 Location

            player.AddComponent<MailBoxComponent, MailBoxType>(MailBoxType.UnOrderedMessage);
            await player.AddLocation(LocationType.Player);                              // ★ 登记 Location

            session.AddComponent<SessionPlayerComponent>().Player = player;
            playerSessionComponent.Session = session;
        }
        else
        {
            // 已存在玩家 → 判断是否在战斗中 → 断线重连逻辑
            PlayerRoomComponent playerRoomComponent = player.GetComponent<PlayerRoomComponent>();
            if (playerRoomComponent.RoomActorId != default)
                CheckRoom(player, session).Coroutine();
            else
            {
                PlayerSessionComponent playerSessionComponent = player.GetComponent<PlayerSessionComponent>();
                playerSessionComponent.Session = session;
            }
        }

        response.PlayerId = player.Id;
    }
}
```

**三个关键动作**：

1. **撤掉 `SessionAcceptTimeoutComponent`** —— 5 秒内不完成登录就踢掉的防扫端口组件
2. **创建 Player Entity** —— 从此这个账号在服务端就是一个 Entity
3. **`AddLocation(LocationType.Player)`** —— 登记到 LocationService，别的进程（Map/Room）后续就能通过 playerId 找到这个实体 ← **ET Actor 模型的精髓**

---

### 阶段 7 · 回主 Fiber，广播登录完成

回到 `LoginHelper.cs`：

```csharp
root.GetComponent<PlayerComponent>().MyId = playerId;
await EventSystem.Instance.PublishAsync(root, new LoginFinish());
```

订阅 `LoginFinish` 的监听者：

| 文件 | 作用 |
|---|---|
| `LoginFinish_RemoveLoginUI.cs` | 关闭登录面板 |
| `LoginFinish_CreateLobbyUI.cs` | 打开大厅面板 |

---

## 五、涉及的所有通信类型（对应网络通信文档的三种方式）

| 步骤 | 发送器 | 通信类型 | 包格式 |
|---|---|---|---|
| 主 Fiber → NetClient Fiber | `ProcessInnerSender.Call` | 同进程内存队列 | 无（直接塞 C# 对象指针） |
| NetClient → Realm | `Session.Call` | **Outer** 网络（KCP） | 2B 长度 + 2B opcode + body |
| Realm → Gate（申请 key） | `MessageSender.Call`（= `ProcessOuterSender`） | **Inner** 网络 | 4B 长度 + 16B ActorId + 2B opcode + body |
| NetClient → Gate（登录） | `Session.Call` | **Outer** 网络（KCP） | 2B 长度 + 2B opcode + body |
| NetClient Fiber → 主 Fiber 回包 | `ProcessInnerSender` 自动回响应 | 同进程内存队列 | — |

**一次登录涵盖了三种通信方式的全部类型**，是理解整个网络模块最好的入门案例。

---

## 六、时序关键点

```
T0   UI 按钮 OnEventLoginAction                （Main Fiber）
T1   创建 NetClient Fiber                       （内存投递 Main2NetClient_Login）
T2   NetClient Fiber 挂载 NetComponent/RouterAddressComponent
     HTTP 拉 Router 列表
T3   CreateRouterSession(realmAddress)           —— Router 握手，建立到 Realm 的 KCP 通道
T4   session.Call(C2R_Login)                    —— 拿回 GateAddress + Key + GateId
     （Realm 内部再向 Gate 发 R2G_GetLoginKey，走 Inner 网络）
T5   CreateRouterSession(gateAddress)           —— 重新 Router 握手，建立到 Gate 的 KCP 通道
T6   gateSession.Call(C2G_LoginGate)            —— 验证 key、创建 Player 实体、挂 Location
T7   回主 Fiber，MyId = playerId                 —— 广播 LoginFinish 切大厅
```

---

## 七、Session 生命周期

| Session | 建立时机 | 销毁时机 | 所属 Fiber |
|---|---|---|---|
| **Realm Session** | 阶段 3.2 | 阶段 3.3 开始时 `using` 结束自动销毁 | NetClient Fiber |
| **Gate Session** | 阶段 3.3 | 登出 / 断线 / 超时 | NetClient Fiber，引用挂在 `SessionComponent` 上供全局访问 |

Realm Session 用完即弃是为了**让 Realm 尽快释放资源扛下一波登录洪峰**；Gate Session 会持续整局游戏。

---

## 八、Key 验证的安全设计

为什么不让客户端直接连 Gate，而是要经过 Realm 中转？

```
Realm：验证账号 + 密码    ←──  账号/密码只暴露给 Realm 集群
       │ 向 Gate 预登记一个随机 Key
       ▼
Gate：只认识 Key，不认识账号密码   ←──  Gate 大量暴露在外网
```

这样：
1. **Gate 不用接触账号密码**，降低敏感数据暴露面
2. **Key 是一次性的**（`GateSessionKeyComponent` 有过期清理）
3. **扫描 Gate 端口**拿不到账号，因为 `SessionAcceptTimeoutComponent` 会在 5 秒内踢掉不发 Key 的连接

---

## 九、一句话总结

> **点击登录 = "UI 回调 → 跨 Fiber 投递 → Router 中转 → Realm 鉴权拿 Key → Router 中转 → Gate 验证拿 PlayerId → 跨 Fiber 回包 → 广播 LoginFinish 切大厅"**
>
> 这个流程串起了 ET8 的三种通信方式（同进程内存队列 / Outer 网络 / Inner 网络）、RPC 机制、Location Actor 模型、Fiber 架构 —— 是理解整个框架最好的入门案例。
