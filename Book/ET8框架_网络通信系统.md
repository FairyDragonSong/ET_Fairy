# ET8 框架 · 双端网络通信系统详解

> 基于 `Unity/Assets/Scripts/Core/Network/`、`Model/Share/Module/Message/`、`Hotfix/**/Module/Message/`、`Core/Fiber/Module/Actor/` 等实际源码整理。
> 客户端（Unity，`Hotfix/Client`、`Model/Client`）与服务端（.NET，`Hotfix/Server`、`Model/Server`）复用同一套底层网络与消息抽象，这是 ET 所谓"双端共用"的基石之一。

---

## 0. 总览：一张图看懂 ET8 网络

```
┌──────────────────────────── 客户端 (Unity) ────────────────────────────┐
│                                                                        │
│  业务逻辑 (SceneType.Client / Main)                                    │
│     │  session.Call(C2M_XxxRequest) / session.Send(C2X_Xxx)            │
│     ▼                                                                  │
│  Session (Model/Share/Module/Message/Session.cs)                       │
│     │  维护 RpcId → RpcInfo(ETTask<IResponse>)                         │
│     ▼                                                                  │
│  NetComponent (Outer)  ──持有──►  AService (KService / TService / WService)
│     │  OnRead / OnAccept / OnError 回调                                │
│     ▼                                                                  │
│  KChannel / TChannel / WChannel   ◄── CircularBuffer + PacketParser    │
│     │                                                                  │
│     ▼  UDP/KCP or TCP or WebSocket (Outer: 2字节长度前缀)              │
│  ════════════════════════════════════════════════════════════════════  │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
                                     ▲ ▼
┌──────────────────────────── 服务端 (.NET) ─────────────────────────────┐
│                                                                        │
│  Gate 进程 · Fiber(Gate)                                               │
│    NetComponent (Outer, 给客户端用)                                    │
│    ProcessOuterSender (Inner, 给其它进程用, 4字节长度前缀+16字节ActorId)│
│       │                                                                │
│       ▼                                                                │
│    ProcessInnerSender + MessageQueue (同进程跨 Fiber 投递)             │
│       │                                                                │
│       ▼                                                                │
│    MailBoxComponent → MessageDispatcher → IMHandler                    │
│                                                                        │
│  Map 进程 · Fiber(Map) ─── 通过 ProcessOuterSender 用 Inner 通道互联   │
│  Location 进程 · Fiber(Location)                                       │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

ET 把"网络"拆成两层：

| 层 | 谁在说话 | 服务类型 | 典型协议 | 包头 | 代码 |
|---|---|---|---|---|---|
| **Outer 外网** | 客户端 ↔ Gate | `ServiceType.Outer` | KCP/TCP/WebSocket | 2字节 `ushort` 长度 + 2字节 opcode + body | `NetComponent` |
| **Inner 内网** | 进程 ↔ 进程（服务端之间） | `ServiceType.Inner` | KCP(UDP) 或 TCP | 4字节 `int` 长度 + 16字节 ActorId + 2字节 opcode + body | `ProcessOuterSender` |
| **同进程跨 Fiber** | Fiber ↔ Fiber（同一个 `.exe` 内） | 纯内存 | - | 无序列化 | `ProcessInnerSender` + `MessageQueue`（`ConcurrentQueue`） |

> 名字容易混淆，记住：`ProcessOuterSender` = 发到**别的进程**（走网络）；`ProcessInnerSender` = 发到**本进程内部**其它 Fiber（走内存队列）。

---

## 1. Core/Network 目录全景

路径：`Unity/Assets/Scripts/Core/Network/`（客户端服务端共用）

| 文件 | 作用 |
|---|---|
| `AService.cs` / `AChannel.cs` | 网络服务 / 通道抽象基类 |
| `KService.cs` / `KChannel.cs` | KCP（可靠 UDP）实现，共 ~1100 行 |
| `TService.cs` / `TChannel.cs` | TCP 实现 |
| `WService.cs` / `WChannel.cs` | WebSocket 实现（HttpListener + ClientWebSocket） |
| `IKcpTransport.cs` | KCP 的底层 transport 抽象（`UdpTransport` / `TcpTransport`） |
| `PacketParser.cs` | 定长前缀 + body 的二态状态机 |
| `MessageSerializeHelper.cs` | 消息 ↔ `MemoryBuffer` 的序列化/反序列化（MemoryPack） |
| `Circularbuffer.cs` | 8KB 分块环形缓冲，读写使用对象池复用 |
| `OpcodeType.cs` | `Type ↔ ushort opcode` 的双向映射 + Request→Response 映射 |
| `OpcodeRangeDefine.cs` | opcode 分段定义 |
| `MessageAttribute.cs` / `ResponseTypeAttribute.cs` | 消息类型标注 |
| `IMessage.cs` / `ILocationMessage.cs` / `Response.cs` | 消息接口 |
| `NetServices.cs` | `CreateAcceptChannelId` / `CreateConnectChannelId` 生成 channel id |
| `ErrorCore.cs` | 网络错误码（100000+） |

---

## 2. AService / AChannel：所有协议的公共契约

`Core/Network/AService.cs`（完整 84 行）：

```csharp
public abstract class AService : IDisposable
{
    public Action<long, IPEndPoint> AcceptCallback;     // 收到新连接
    public Action<long, MemoryBuffer> ReadCallback;     // 收到一条完整消息
    public Action<long, int> ErrorCallback;             // 发生错误

    public long Id { get; set; }
    public ServiceType ServiceType { get; protected set; }   // Outer / Inner

    public abstract void Update();                      // 由所属 Fiber 每帧调用
    public abstract void Create(long id, IPEndPoint ep); // 主动建立连接
    public abstract void Send(long channelId, MemoryBuffer buf);
    public abstract void Remove(long id, int error = 0);

    // MemoryBuffer 的小型对象池：最多池化 10 个 ≤1024B 的 buffer
    public MemoryBuffer Fetch(int size = 0) { ... }
    public void Recycle(MemoryBuffer memoryBuffer) { ... }
}
```

要点：

1. **只暴露三个回调**：`Accept / Read / Error`，上层（`NetComponent`/`ProcessOuterSender`）只关心"有新连接、收到消息、出错了"三件事。
2. **`MemoryBuffer` 对象池**是 ET 的小型 GC 优化，超过 1024B 的不入池，避免持有大块内存。
3. 所有"发送数据"都是通过 `long channelId` 定位通道——`channelId` 其实就是 `KChannel.LocalConn` 或 `TChannel.Id`，由 `NetServices` 生成。

`AChannel.cs`：

```csharp
public enum ChannelType { Connect, Accept }     // 主动连 / 被动接

public struct Packet
{
    public const int MinPacketSize = 2;
    public const int OpcodeLength  = 2;
    public const int ActorIdIndex  = 0;
    public const int ActorIdLength = 16;        // Inner 消息带的 ActorId
    public ushort Opcode;
    public long   ActorId;
    public MemoryStream MemoryStream;
}
```

---

## 3. 包格式（最容易踩坑的地方）

ET8 的"包"并不是一个统一结构，Outer 和 Inner **不一样**，二者在 `PacketParser` 和 `MessageSerializeHelper` 里是分支处理的。

### 3.1 Outer 外网包（客户端 ↔ Gate）

```
┌────────────┬──────────┬────────────────────────┐
│ Length(2B) │ Opcode(2)│       Message Body     │
│  ushort    │ ushort   │  (MemoryPack 序列化)   │
└────────────┴──────────┴────────────────────────┘
  └─ 长度不含自己；整包上限 65535 字节
```

### 3.2 Inner 内网包（服务器 ↔ 服务器）

```
┌────────────┬─────────────────────┬──────────┬────────────────────────┐
│ Length(4B) │  ActorId (16B)      │ Opcode(2)│       Message Body     │
│  int       │ Process(4)+Fiber(4) │ ushort   │  (MemoryPack 序列化)   │
│            │ + InstanceId(8)     │          │                        │
└────────────┴─────────────────────┴──────────┴────────────────────────┘
  └─ 4 字节长度，支持最大 ushort.MaxValue*16 = ~1MB 的单包
```

### 3.3 解包状态机 `PacketParser`

`PacketParser.cs` 用双状态循环，不停消费 `CircularBuffer` 里的字节直到凑不齐下一个包为止：

```csharp
public bool Parse(out MemoryBuffer memoryBuffer)
{
    while (true) switch (this.state)
    {
        case ParserState.PacketSize:
            if (service.ServiceType == ServiceType.Inner)
            {
                if (buffer.Length < 4) { memoryBuffer = null; return false; }
                buffer.Read(cache, 0, 4);
                packetSize = BitConverter.ToInt32(cache, 0);
                if (packetSize > ushort.MaxValue * 16 || packetSize < Packet.MinPacketSize)
                    throw new Exception("recv packet size error, 可能是外网探测端口");
            }
            else   // Outer
            {
                if (buffer.Length < 2) { memoryBuffer = null; return false; }
                buffer.Read(cache, 0, 2);
                packetSize = BitConverter.ToUInt16(cache, 0);
                if (packetSize < Packet.MinPacketSize)
                    throw new Exception("recv packet size error, 可能是外网探测端口");
            }
            state = ParserState.PacketBody;
            break;

        case ParserState.PacketBody:
            if (buffer.Length < packetSize) { memoryBuffer = null; return false; }
            memoryBuffer = service.Fetch(packetSize);      // 池里捞一个 MemoryBuffer
            buffer.Read(memoryBuffer, packetSize);
            memoryBuffer.Seek(0, SeekOrigin.Begin);
            state = ParserState.PacketSize;
            return true;
    }
}
```

### 3.4 序列化入口 `MessageSerializeHelper`

发送侧（`ToMemoryBuffer`）：

```csharp
// Inner: 在开头预留 16B 放 ActorId，再写 opcode+body
case ServiceType.Inner:
    opcode = MessageToStream(memoryBuffer, (MessageObject)message, Packet.ActorIdLength);
    memoryBuffer.GetBuffer().WriteTo(0, actorId);   // 16B 直接覆盖写入
    break;
// Outer: 直接 opcode + body
case ServiceType.Outer:
    opcode = MessageToStream(memoryBuffer, (MessageObject)message);
    break;
```

接收侧（`ToMessage`）：

```csharp
case ServiceType.Inner:
    actorId.Process    = BitConverter.ToInt32 (buffer, 0);
    actorId.Fiber      = BitConverter.ToInt32 (buffer, 4);
    actorId.InstanceId = BitConverter.ToInt64 (buffer, 8);
    ushort opcode      = BitConverter.ToUInt16(buffer, 16);
    memoryStream.Seek(18, SeekOrigin.Begin);
    message = Deserialize(OpcodeType.Instance.GetType(opcode), memoryStream);
    break;
```

所以 **Inner 包天然带路由信息**（目标 Fiber、目标 InstanceId），跨进程转发时可以直接按 ActorId 塞到对端相应 Fiber 的 `MessageQueue`，这是 ET Actor 模型跨进程工作的底层机制。

### 3.5 Opcode 分段

`OpcodeRangeDefine.cs`：

```csharp
public const ushort OuterMinOpcode = 10001;   // 客户端 ↔ 服务器（Outer proto）
public const ushort OuterMaxOpcode = 20000;

public const ushort InnerMinOpcode = 20001;   // 服务器内部（Inner proto）
public const ushort InnerMaxOpcode = 40000;

public const ushort MaxOpcode = 60000;
```

`OpcodeType` 在启动时扫描所有带 `[Message(opcode)]` 属性的类型并登记 `Type↔opcode`、`IRequest→IResponse` 映射：

```csharp
public void Awake()
{
    HashSet<Type> types = CodeTypes.Instance.GetTypes(typeof(MessageAttribute));
    foreach (Type type in types)
    {
        ushort opcode = type.GetCustomAttribute<MessageAttribute>().Opcode;
        if (opcode != 0) typeOpcode.Add(type, opcode);

        if (typeof(IRequest).IsAssignableFrom(type))
        {
            if (typeof(ILocationMessage).IsAssignableFrom(type))
                requestResponse.Add(type, typeof(MessageResponse));   // 统一响应
            else
            {
                var respAttr = type.GetCustomAttribute<ResponseTypeAttribute>();
                requestResponse.Add(type, CodeTypes.Instance.GetType($"ET.{respAttr.Type}"));
            }
        }
    }
}
```

序列化后端是 **MemoryPack**（`MemoryPackHelper`），走 `[MemoryPackable] partial` 代码生成，没有反射开销。

---

## 4. KService / KChannel：KCP（可靠 UDP）

文件：`Core/Network/KService.cs`（632 行）、`KChannel.cs`（~460 行）。

### 4.1 KCP 帧头（握手 + 数据包标识）

```csharp
public static class KcpProtocalType
{
    public const byte SYN = 1;    // 连接请求
    public const byte ACK = 2;    // 连接确认
    public const byte FIN = 3;    // 断开
    public const byte MSG = 4;    // 数据
    public const byte RouterReconnectSYN = 5; // Router 场景重连
    public const byte RouterReconnectACK = 6;
    public const byte RouterSYN = 7;
    public const byte RouterACK = 8;
}
```

### 4.2 握手流程

```
Client                                              Server
  │                                                   │
  │  ── [SYN(1B)][LocalConn(4B)][RemoteConn(4B)] ──► │   新建 KChannel, 分配 AcceptId
  │                                                   │
  │ ◄── [ACK(1B)][LocalConn(4B)][RemoteConn(4B)] ──  │
  │                                                   │
  │  触发 KChannel.HandleConnnect()                   │
  │  初始化 Kcp FSM (NoDelay, Window, MTU)            │
  │                                                   │
  │  ── [MSG(1B)][LocalConn(4B)] + kcp payload ───► │
  │ ◄── [MSG(1B)][LocalConn(4B)] + kcp payload ───  │
```

`CreateConnectChannelId` 用随机 uint；`CreateAcceptChannelId` 用 `int.MinValue` 开始的自增 id，使两个通道 id 空间天然不冲突。

### 4.3 Inner / Outer 两套 KCP 参数

`KChannel.InitKcp()`：

```csharp
switch (Service.ServiceType)
{
    case ServiceType.Inner:   // 内网——机房高速网
        kcp.SetNoDelay(1, 10, 2, 1);
        kcp.SetWindowSize(1024, 1024);   // 大窗口
        kcp.SetMtu(1400);                // 机房网通常不分片
        kcp.SetMinrto(30);
        break;
    case ServiceType.Outer:   // 外网——家宽/4G/5G
        kcp.SetNoDelay(1, 10, 2, 1);
        kcp.SetWindowSize(256, 256);     // 小窗口
        kcp.SetMtu(470);                 // 给运营商 NAT/隧道留余量
        kcp.SetMinrto(30);
        break;
}
```

### 4.4 大包分片（`MaxKcpMessageSize = 10000`）

超过 10000 字节的消息在 KCP 这一层再做一次切片：先发一个 8 字节的"头包"（前 4 字节是特殊标记 0，后 4 字节是总大小），然后连续发多个 ≤10000B 的数据包，接收侧 `HandleRecv` 拼回完整 `MemoryBuffer`。

### 4.5 主循环 `KService.Update()`

```csharp
public override void Update()
{
    uint timeNow = this.TimeNow;
    this.TimerOut(timeNow);              // 到期的 kcp.Update 任务
    this.CheckWaitAcceptChannel(timeNow);// 20s 未完成握手的连接超时清掉
    this.Recv();                         // 读 socket，根据 flag 分发 SYN/ACK/MSG
    this.UpdateChannel(timeNow);         // 驱动每个 KCP 状态机
    this.Transport.Update();
}
```

`Update()` 的调用来源见第 8 节 —— 由 `NetComponent` / `ProcessOuterSender` 在其所属 Fiber 的每帧里调一次。

---

## 5. TService / TChannel：TCP

使用 `SocketAsyncEventArgs` 异步完成通知，**所有完成事件都先推到 `ConcurrentQueue<TArgs>`**，由 Fiber 线程在 `Update()` 里排空，这样就把异步回调统一到 Fiber 线程，不需要锁。

`TService.cs` 关键结构：

```csharp
public enum TcpOp { StartSend, StartRecv, Connect }

public struct TArgs
{
    public TcpOp Op;
    public long ChannelId;
    public SocketAsyncEventArgs SocketAsyncEventArgs;
}

public ConcurrentQueue<TArgs> Queue = new();    // 事件总线

public TService(IPEndPoint ep, ServiceType svc)
{
    this.ServiceType = svc;
    this.acceptor = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    this.innArgs.Completed += this.OnComplete;
    this.acceptor.Bind(ep);
    this.acceptor.Listen(1000);
    this.AcceptAsync();     // 异步投递第一次 accept
}
```

`TChannel` 使用两个 `CircularBuffer`：

- `recvBuffer`：socket 收到的字节追加进来，`PacketParser.Parse` 从它里面拆包
- `sendBuffer`：先把"长度前缀 + body"写进去，再异步 `SendAsync`

发送时 Inner 写 4B int、Outer 写 2B ushort：

```csharp
switch (Service.ServiceType)
{
    case ServiceType.Inner:
        sendCache.WriteTo(0, messageSize);
        sendBuffer.Write(sendCache, 0, 4);  break;
    case ServiceType.Outer:
        sendCache.WriteTo(0, (ushort)messageSize);
        sendBuffer.Write(sendCache, 0, 2);  break;
}
sendBuffer.Write(stream.GetBuffer(), (int)stream.Position, messageSize);
```

---

## 6. WService / WChannel：WebSocket

给需要走 80/443 的平台（H5、iOS 某些审核场景）用的。

- 服务端：`HttpListener` + `GetContext().AcceptWebSocketAsync()`
- 客户端：`ClientWebSocket`
- 每个 `SendAsync` 发送一条完整二进制帧（`WebSocketMessageType.Binary`, `EndOfMessage=true`）
- 收完整消息后直接通过 `ReadCallback` 上抛，**不经过 `PacketParser`**（WebSocket 自带分帧）
- 上限 `ushort.MaxValue`，超出丢 `ERR_WebsocketMessageTooBig`

---

## 7. 客户端侧：NetComponent + Session

路径：`Model/Share/Module/Message/`（接口）、`Hotfix/Share/Module/Message/`（实现）。

### 7.1 NetComponent 生命周期

`NetComponentSystem.cs`：

```csharp
[EntitySystem]
private static void Awake(this NetComponent self, IPEndPoint address, NetworkProtocol protocol)
{
    self.AService = new KService(address, protocol, ServiceType.Outer);
    self.AService.AcceptCallback = self.OnAccept;
    self.AService.ReadCallback   = self.OnRead;
    self.AService.ErrorCallback  = self.OnError;
}

[EntitySystem]
private static void Update(this NetComponent self) => self.AService.Update();

// 客户端主动连接：channelId 用 CreateConnectChannelId 生成的随机 uint
public static Session Create(this NetComponent self, IPEndPoint ep)
{
    long channelId = NetServices.Instance.CreateConnectChannelId();
    Session session = self.AddChildWithId<Session, AService>(channelId, self.AService);
    session.RemoteAddress = ep;
    session.AddComponent<SessionIdleCheckerComponent>(); // 空闲检查
    self.AService.Create(session.Id, session.RemoteAddress);
    return session;
}

private static void OnRead(this NetComponent self, long channelId, MemoryBuffer buf)
{
    Session session = self.GetChild<Session>(channelId);
    if (session == null) return;
    session.LastRecvTime = TimeInfo.Instance.ClientNow();

    (ActorId _, object message) = MessageSerializeHelper.ToMessage(self.AService, buf);
    self.AService.Recycle(buf);

    // 全部通过 EventSystem 广播到该 SceneType 的订阅者，解耦业务
    EventSystem.Instance.Invoke((long)self.IScene.SceneType,
        new NetComponentOnRead { Session = session, Message = message });
}
```

### 7.2 Session：RPC 的核心

`Model/Share/Module/Message/Session.cs`：

```csharp
public readonly struct RpcInfo
{
    public Type RequestType { get; }
    private readonly ETTask<IResponse> tcs;

    public RpcInfo(Type requestType)
    {
        this.RequestType = requestType;
        this.tcs = ETTask<IResponse>.Create(true);
    }
    public void SetResult(IResponse r)    => tcs.SetResult(r);
    public void SetException(Exception e) => tcs.SetException(e);
    public async ETTask<IResponse> Wait() => await tcs;
}

public sealed class Session : Entity, IAwake<AService>, IDestroy
{
    public AService AService { get; set; }
    public int RpcId { get; set; }
    public readonly Dictionary<int, RpcInfo> requestCallbacks = new();
    public long LastRecvTime { get; set; }
    public long LastSendTime { get; set; }
    public int  Error        { get; set; }
    public IPEndPoint RemoteAddress { get; set; }
}
```

RPC 发送：

```csharp
public static async ETTask<IResponse> Call(this Session self, IRequest request, int time = 0)
{
    int rpcId = ++self.RpcId;
    RpcInfo rpcInfo = new(request.GetType());
    self.requestCallbacks[rpcId] = rpcInfo;
    request.RpcId = rpcId;
    self.Send(request);                   // 走底层 AService.Send

    if (time > 0)                         // 可选超时
    {
        async ETTask Timeout()
        {
            await self.Root().GetComponent<TimerComponent>().WaitAsync(time);
            if (self.requestCallbacks.Remove(rpcId, out RpcInfo action))
                action.SetException(new Exception($"session call timeout: {action.RequestType.FullName} {time}"));
        }
        Timeout().Coroutine();
    }
    return await rpcInfo.Wait();
}

public static void OnResponse(this Session self, IResponse response)
{
    if (self.requestCallbacks.Remove(response.RpcId, out RpcInfo action))
        action.SetResult(response);
}

public static void Send(this Session self, ActorId actorId, IMessage message)
{
    self.LastSendTime = TimeInfo.Instance.ClientNow();
    (ushort opcode, MemoryBuffer mb) = MessageSerializeHelper.ToMemoryBuffer(self.AService, actorId, message);
    self.AService.Send(self.Id, mb);
}
```

要点：

- 每个 Session 有自己的 RpcId 计数器（从 1 单调递增）
- `requestCallbacks` 把 RpcId 映射到 `ETTask<IResponse>`，收到响应时 `SetResult` 唤醒 awaiter
- Session 析构时剩余的挂起 RPC 全部以 `RpcException(self.Error, ...)` 的方式抛出，业务层能收到"连接断开"信号
- 空闲监测：`SessionIdleCheckerComponent`（客户端空闲太久自动断）、`SessionAcceptTimeoutComponent`（服务端接受后 5 秒不验证权限就断）

---

## 8. 服务端侧：三类消息发送器

### 8.1 NetComponent（Outer，给 Gate 用）

和客户端完全同一份 `NetComponentSystem.cs`，区别在于 SceneType —— Gate 进程下 `OnAccept` 会给每个新 Session 挂上 `SessionAcceptTimeoutComponent`（5 秒内没走完登录流程就踢掉）。

### 8.2 ProcessOuterSender（Inner，跨进程）

`Model/Server/Module/Message/ProcessOuterSender.cs` + `Hotfix/.../ProcessOuterSenderSystem.cs`：

```csharp
[ComponentOf(typeof(Scene))]
public class ProcessOuterSender : Entity, IAwake<IPEndPoint>, IUpdate, IDestroy
{
    public const long TIMEOUT_TIME = 40 * 1000;
    public int RpcId;
    public readonly Dictionary<int, MessageSenderStruct> requestCallback = new();
    public AService AService;
    public NetworkProtocol InnerProtocol = NetworkProtocol.KCP;
}

[EntitySystem]
private static void Awake(this ProcessOuterSender self, IPEndPoint address)
{
    self.AService = self.InnerProtocol switch
    {
        NetworkProtocol.TCP => new TService(address, ServiceType.Inner),
        NetworkProtocol.KCP => new KService(address, NetworkProtocol.UDP, ServiceType.Inner),
        _ => throw new ArgumentException()
    };
    self.AService.AcceptCallback = self.OnAccept;
    self.AService.ReadCallback   = self.OnRead;
    self.AService.ErrorCallback  = self.OnError;
}
```

收到对端进程消息后的关键分发逻辑：

```csharp
private static void OnRead(this ProcessOuterSender self, long channelId, MemoryBuffer mb)
{
    Session session = self.GetChild<Session>(channelId);
    (ActorId actorId, object message) = MessageSerializeHelper.ToMessage(self.AService, mb);
    self.AService.Recycle(mb);

    if (message is IResponse resp) { self.HandleIActorResponse(resp); return; }

    Fiber fiber = self.Fiber();
    int fromProcess = actorId.Process;
    actorId.Process = fiber.Process;          // 改写 Process 为本进程，下面塞本地队列

    switch (message)
    {
        case ILocationRequest:
        case IRequest:
            CallInner().Coroutine();
            async ETTask CallInner()
            {
                IRequest req = (IRequest)message;
                int rpcId = req.RpcId;
                // 中转：交给同进程的 ProcessInnerSender，它会塞到对应 Fiber 的 MessageQueue
                IResponse res = await fiber.Root.GetComponent<ProcessInnerSender>().Call(actorId, req, false);
                actorId.Process = fromProcess;   // 回包时 Process 还原成发起方
                res.RpcId = rpcId;
                self.Send(actorId, res);
                ((MessageObject)res).Dispose();
            }
            break;
        default:
            fiber.Root.GetComponent<ProcessInnerSender>().Send(actorId, (IMessage)message);
            break;
    }
}
```

跨进程发送：

```csharp
private static void SendInner(this ProcessOuterSender self, ActorId actorId, MessageObject message)
{
    if (actorId.Process == self.Fiber().Process)
        throw new Exception($"actor is the same process");

    StartProcessConfig cfg = StartProcessConfigCategory.Instance.Get(actorId.Process);
    Session session = self.Get(cfg.Id);        // 按进程号缓存 Session，懒建立
    actorId.Process = self.Fiber().Process;    // 写入本进程号，对端据此回包
    session.Send(actorId, message);
}
```

### 8.3 ProcessInnerSender（同进程跨 Fiber，零拷贝）

`Core/Fiber/Module/Actor/ProcessInnerSenderSystem.cs`：

```csharp
[EntitySystem]
private static void Awake(this ProcessInnerSender self)
{
    MessageQueue.Instance.AddQueue(self.Fiber().Id);   // 给本 Fiber 挂一个 ConcurrentQueue
}

[EntitySystem]
private static void Update(this ProcessInnerSender self)
{
    self.list.Clear();
    MessageQueue.Instance.Fetch(self.Fiber().Id, 1000, self.list);  // 每帧批量取 ≤1000 条
    foreach (MessageInfo info in self.list) self.HandleMessage(self.Fiber(), info);
}

private static void HandleMessage(this ProcessInnerSender self, Fiber fiber, in MessageInfo info)
{
    if (info.MessageObject is IResponse resp) { self.HandleIActorResponse(resp); return; }

    MailBoxComponent mb = fiber.Mailboxes.Get(info.ActorId.InstanceId);
    if (mb == null)
    {
        if (info.MessageObject is IRequest request)   // 找不到实体，回一个错误响应
        {
            IResponse err = MessageHelper.CreateResponse(request.GetType(), request.RpcId, ErrorCore.ERR_NotFoundActor);
            self.Reply(info.ActorId.Address, err);
        }
        return;
    }
    mb.Add(info.ActorId.Address, info.MessageObject);     // 投递到实体的 Mailbox
}
```

`MessageQueue`（`Core/World/Module/Actor/MessageQueue.cs`）：

```csharp
public class MessageQueue : Singleton<MessageQueue>, ISingletonAwake
{
    private readonly ConcurrentDictionary<int, ConcurrentQueue<MessageInfo>> messages = new();

    public bool Send(Address from, ActorId target, MessageObject msg)
    {
        if (!messages.TryGetValue(target.Address.Fiber, out var queue)) return false;
        queue.Enqueue(new MessageInfo {
            ActorId = new ActorId(from, target.InstanceId),
            MessageObject = msg
        });
        return true;
    }
    public void Fetch(int fiberId, int count, List<MessageInfo> list) { ... }
    public void AddQueue(int fiberId)    { messages[fiberId] = new ConcurrentQueue<MessageInfo>(); }
    public void RemoveQueue(int fiberId) { messages.TryRemove(fiberId, out _); }
}
```

**这里没有任何序列化** —— Fiber A 直接把 `MessageObject` 指针塞进 Fiber B 的队列。因此同进程跨 Fiber RPC 的延迟只有"一次入队 + Fiber 调度"，几乎零成本。

### 8.4 三层发送模型对比

| 场景 | 发送器 | Call/Send 会做什么 | 超时 |
|---|---|---|---|
| 客户端 → Gate | `Session` | 序列化 → AService.Send（网络） | 调用方传 |
| Fiber A → Fiber B（同进程） | `ProcessInnerSender` | `MessageQueue.Send` 入队 | 40s |
| Fiber A → 另一个进程 | `ProcessOuterSender` | 序列化 → Session.Send（内网网络）→ 对端 `ProcessOuterSender.OnRead` → 对端 `ProcessInnerSender.Call` → 对端 Fiber | 40s |
| 对某个 ActorId 已知 InstanceId 发 | `MessageLocationSender` | 封装 `MessageLocationRequest` 先查 LocationProxy，再委托上面两个 | 40s |

### 8.5 客户端的 ClientSenderComponent（进程内 Fiber 化网络栈）

客户端把"网络"也做成独立 Fiber（`SceneType.NetClient`），主逻辑 Fiber 通过 `ProcessInnerSender` 调用它。`Hotfix/Client/Demo/Main/ClientSenderComponentSystem.cs`：

```csharp
public static async ETTask<long> LoginAsync(this ClientSenderComponent self, string account, string password)
{
    self.fiberId = await FiberManager.Instance.Create(SchedulerType.ThreadPool, 0, SceneType.NetClient, "");
    self.netClientActorId = new ActorId(self.Fiber().Process, self.fiberId);

    Main2NetClient_Login login = Main2NetClient_Login.Create();
    login.OwnerFiberId = self.Fiber().Id;
    login.Account  = account;
    login.Password = password;
    NetClient2Main_Login resp = await self.Root().GetComponent<ProcessInnerSender>().Call(self.netClientActorId, login) as NetClient2Main_Login;
    return resp.PlayerId;
}

public static void Send(this ClientSenderComponent self, IMessage message)
{
    A2NetClient_Message wrap = A2NetClient_Message.Create();
    wrap.MessageObject = message;
    self.Root().GetComponent<ProcessInnerSender>().Send(self.netClientActorId, wrap);
}
```

这样 Unity 主线程即使 30fps 卡顿，网络线程依然能按自己的节奏处理 KCP。

---

## 9. 消息分发：MessageDispatcher + IMHandler

`Core/World/Module/Actor/MessageDispatcher.cs`：

```csharp
[Code]
public class MessageDispatcher : Singleton<MessageDispatcher>, ISingletonAwake
{
    private readonly Dictionary<Type, List<MessageDispatcherInfo>> messageHandlers = new();

    public void Awake()
    {
        // 扫描所有 [MessageHandler(SceneType.Xxx)] 以及 [MessageLocationHandler(...)] 类
        foreach (Type t in CodeTypes.Instance.GetTypes(typeof(MessageHandlerAttribute)))  Register(t);
        foreach (Type t in CodeTypes.Instance.GetTypes(typeof(MessageLocationHandlerAttribute))) Register(t);
    }

    public async ETTask Handle(Entity entity, Address fromAddress, MessageObject message)
    {
        if (!messageHandlers.TryGetValue(message.GetType(), out var list))
            throw new Exception($"not found message handler: {message}");

        SceneType sceneType = entity.IScene.SceneType;
        foreach (var info in list)
        {
            if (!info.SceneType.HasSameFlag(sceneType)) continue; // 按 SceneType 过滤
            await info.IMHandler.Handle(entity, fromAddress, message);
        }
    }
}
```

- **`SceneType` 过滤**：同一个消息类型可以在不同 Scene（Gate/Map/Realm/Client...）注册不同 Handler，运行时按 entity 所属 Scene 选择
- **代码生成**：ET 给每个 `*MessageHandler` 生成 `NetComponentOnReadInvoker_Xxx`（见 `Hotfix/Server/Demo/Gate/NetComponentOnReadInvoker_Gate.cs` 等），订阅 `NetComponentOnRead` 事件，把泛化 `object message` 拆回具体类型后调用 Handler

---

## 10. Actor 与 Location

- **ActorId** = `{ Process(int), Fiber(int), InstanceId(long) }`，共 16 字节，内嵌到 Inner 包头
- `MessageLocationSender`（`Model/Server/Module/ActorLocation/MessageLocationSender.cs`）：知道对方 InstanceId 但不知道当前位置，先去 LocationComponent 查找后再委托 `ProcessOuterSender`
- `ILocationMessage`（`Core/Network/ILocationMessage.cs`）：带位置信息的消息标记接口，所有这种请求的响应统一为 `MessageResponse`（`OpcodeType.Awake` 里有特判）
- **对端中转**：见 7.2 中 `ProcessOuterSender.OnRead` 的处理——跨进程 Request 会被转成本地 `ProcessInnerSender.Call`，回包再反向塞回去，这样每个 Fiber 的业务代码不用关心"消息到底来自本进程还是远端"

---

## 11. 内存与并发

### 11.1 `CircularBuffer`（`Core/Network/Circularbuffer.cs`）

- **`ChunkSize = 8192` 分块**：`Queue<byte[]> bufferQueue` 存激活块，`Queue<byte[]> bufferCache` 存回收块
- 消费时 `RemoveFirst()` 把块回收到 cache，避免频繁 new byte[8192] 触发 LOH
- 配合 `AService` 的 `MemoryBuffer` pool（≤1024B 池化 10 个），整个网络栈在稳定期几乎不分配

### 11.2 Fiber 调度

`Fiber` 的 `ThreadSynchronizationContext` 保证：

- `AService.Update()` 在它被 `AddComponent` 进的那个 Scene/Fiber 的线程上执行
- `ConcurrentQueue<TArgs>`（TCP）和 `ConcurrentQueue<MessageInfo>`（MessageQueue）是唯一的跨线程同步原语——**其余代码都是单线程的**，这是 ET 敢写"每个 Fiber 单线程即可"的根本原因

### 11.3 错误码

`Core/Network/ErrorCore.cs`，100000 段全是网络相关：

```csharp
ERR_KcpConnectTimeout        = 100205; // 20s 握手超时
ERR_KcpReadWriteTimeout      = 100207; // 30s 无数据
ERR_PeerDisconnect           = 100208; // FIN
ERR_SocketError              = 100210;
ERR_KcpWaitSendSizeTooLarge  = 100211;
ERR_WebsocketPeerReset       = 100218;
ERR_PacketParserError        = 110005;
ERR_NotFoundActor            = 110313;
ERR_MessageTimeout           = 110315;
```

`ERR_Exception = 200000`：小于此的 RPC 错误码会被 Session.Call / MessageSenderStruct 抛成 `RpcException`，大于的交给业务处理。

---

## 12. 一次客户端 RPC 的完整旅程（走读）

以登录后 "玩家移动" 这种需要跨 Gate→Map 的 Location RPC 为例：

1. **客户端**  
   `await session.Call(C2M_PathfindingResult { RpcId, ... })`  
   → `Session.Send` → `MessageSerializeHelper.ToMemoryBuffer(Outer)` → `KChannel.Send` → UDP

2. **Gate 进程**（Fiber: Gate）  
   `KService.Recv` → `NetComponent.OnRead` → `EventSystem.Invoke(SceneType.Gate, NetComponentOnRead)`  
   → 自动生成的 `NetComponentOnReadInvoker_Gate` 把 object 反射回 `C2M_PathfindingResult`  
   → 业务 Handler 调用 `MessageLocationSender.Call`  
   → 先查 Location，得到 ActorId(Map 进程, Map Fiber, MapUnit InstanceId)  
   → `ProcessOuterSender.Call` 把消息写成 **Inner 包（含 16B ActorId）** 发给 Map 进程

3. **Map 进程**（Fiber: NetInner，即接收内网 Service 的 Fiber）  
   `ProcessOuterSender.OnRead` → 判断是 Request → `ProcessInnerSender.Call` 入队到目标 Fiber

4. **Map 业务 Fiber**  
   `ProcessInnerSender.Update()` 从 `MessageQueue` 取出消息 → `MailBoxComponent.Add` → 业务 Handler 执行 → 生成 Response

5. **回包**：Handler 结束 → `ProcessInnerSender` 的 `MessageSenderStruct` 被 SetResult → `ProcessOuterSender.CallInner` 捕获到 Response → 反向 `Send` 到 Gate 进程

6. **Gate**：`ProcessOuterSender.OnRead` 识别 IResponse → `HandleIActorResponse` 匹配 RpcId → MessageLocationSender 收到结果

7. **Gate 把结果回包给客户端**：通常 Handler 执行 `await session.Reply(response)`（Outer 方向）

每一跳都走同一套 `Fetch/Recycle/CircularBuffer/PacketParser/MessageSerializeHelper`，串起来就是 ET8 的分布式 Actor 模型。

---

## 13. 小结 · 必记的抽象

1. **三个抽象面**：`AService`（网络）、`Session/MessageSenderStruct`（等响应）、`MessageDispatcher/IMHandler`（路由到业务）
2. **两套包格式**：Outer = 2B+opcode+body；Inner = 4B+16B ActorId+opcode+body
3. **三类发送器**：`Session`（客户端 RPC）、`ProcessOuterSender`（跨进程）、`ProcessInnerSender`（同进程跨 Fiber）
4. **一条规则**：线程由 Fiber 决定，跨线程只通过 `ConcurrentQueue`；`AService.Update()` 必须由拥有它的 Fiber 每帧调用
5. **MemoryPack + MemoryBuffer 池 + CircularBuffer 分块**，构成稳定期"近零分配"的网络栈

> 想进一步深挖，建议按 "KChannel.HandleRecv / HandleSend → KService.Recv → NetComponent.OnRead → Session.OnResponse" 这条线打 Debug 断点跑一趟，所有路径会立刻清晰。
