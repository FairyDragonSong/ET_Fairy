# ET框架核心机制备忘录

---

## 一、服务器启动流程

### 入口点

| 入口 | 文件 | 场景 |
|------|------|------|
| .NET 独立进程 | `DotNet/App/Program.cs` | 生产环境 |
| Unity MonoBehaviour | `Unity/Assets/Scripts/Loader/MonoBehaviour/Init.cs` | 编辑器开发 |

### 启动步骤

```
Program.Main()
  ├─ 1. Init.Start()
  │     解析 CLI 参数 (--AppType, --Process, --StartConfig)
  │     创建 World 单例: Logger, TimeInfo, FiberManager, CodeLoader
  │
  ├─ 2. CodeLoader.Awake()
  │     加载 Hotfix.dll（热更新程序集）
  │     通过反射调用 Entry.Start()
  │
  ├─ 3. Entry.StartAsync()
  │     注册 MongoDB 序列化
  │     创建全局单例: IdGenerater, OpcodeType, ObjectPool,
  │                   MessageQueue, NetServices, NavmeshComponent
  │     ConfigLoader.LoadAsync() ← 加载所有配置表(.bytes)
  │     创建 Main Fiber (SceneType.Main)
  │
  ├─ 4. FiberInit_Main 触发三阶段事件:
  │     ├─ EntryEvent1: 共享组件 (Timer, CoroutineLock, MailBox, ProcessInnerSender)
  │     ├─ EntryEvent2: 服务器启动 (创建NetInner Fiber + 各Scene Fiber)
  │     └─ EntryEvent3: 客户端初始化（服务器不执行）
  │
  └─ 5. 主循环: while(true) { Update(); LateUpdate(); }
```

### Watcher 模式

`--AppType=Watcher` 时，遍历 StartProcessConfig，为当前机器的每个进程 fork 子进程，实现一键启动。

---

## 二、项目引用与程序集结构

### DotNet.App 的引用链

```
DotNet.App.csproj
  ├── 引用 DotNet.Loader.csproj  (Init 类)
  └── 引用 DotNet.Model.csproj   (Entry 类)
```

### DotNet.Model 如何包含 Unity 目录的代码

通过 `<Compile Include>` 直接链接 Unity 目录下的源文件（不是复制）：

```xml
<Compile Include="..\..\Unity\Assets\Scripts\Model\Share\**\*.cs">
    <Link>Share\%(RecursiveDir)%(FileName)%(Extension)</Link>
</Compile>
```

### Entry.Init() 为什么是空方法

防止 .NET 裁剪 Model 程序集。因为实际调用 Entry.Start() 是通过反射完成的，如果没有静态引用，Model.dll 不会被加载。

---

## 三、ETTask.Coroutine() — Fire-and-Forget

### 作用

在同步方法中启动异步 ETTask，不等待完成。

### 调用链

```csharp
StartAsync().Coroutine()
  → ETTask.Coroutine()
    → InnerCoroutine().Coroutine()  // async ETVoid，内部 await this 驱动状态机
      → ETVoid.Coroutine()          // 空方法，消费返回值
```

### 类比

```csharp
// C# 原生 Task:
_ = DoSomethingAsync();

// ET 框架:
DoSomethingAsync().Coroutine();
```

区别：ET 不用 ThreadPool，而是用 Fiber 的 SynchronizationContext 在主循环 Update() 中推进状态机。

---

## 四、OpcodeType — 网络消息协议注册中心

### 文件位置

`Unity/Assets/Scripts/Core/Network/OpcodeType.cs`

### 核心数据

```csharp
// 消息类型 <-> opcode 双向映射
DoubleMap<Type, ushort> typeOpcode;

// Request类型 -> 对应的Response类型
Dictionary<Type, Type> requestResponse;
```

### 工作原理

- 初始化时扫描所有 `[Message(opcode)]` 标记的类
- 发送时：`GetOpcode(typeof(C2R_Login))` → 得到 ushort 写入包头
- 接收时：`GetType(opcode)` → 得到类型用于反序列化
- RPC 时：`GetResponseType(requestType)` → 知道响应该反序列化为什么类型

---

## 五、CodeTypes — 特性驱动的类型注册

### 文件位置

`Unity/Assets/Scripts/Core/World/Module/Code/CodeTypes.cs`

### 核心数据

```csharp
// key: 特性类型, value: 标记了该特性的所有类型
// 如果该类型没有直接打上特性，但是父类打了特性，那么这个类型也算打了这个特性
UnOrderMultiMapSet<Type, Type> types;
```

### 注册逻辑

```csharp
object[] objects = type.GetCustomAttributes(typeof(BaseAttribute), true);
// inherit=true：沿继承链向上搜索特性
foreach (object o in objects)
{
    this.types.Add(o.GetType(), type);  // key是特性类型，value是被标记的类
}
```

### EnableClassAttribute 的传播

`[EnableClass]` 标在两个关键位置：
- `ET.Object`（所有 Entity、SystemObject 的基类）
- `BaseAttribute`（所有自定义特性的基类）

因此所有继承自 `ET.Object` 的非抽象类都会被收集到 `EnableClassAttribute` 下。

---

## 六、Actor 模型

### 定义

**Actor = 一个挂了 MailBoxComponent 的 Entity**

### 三要素

| 要素 | 实现 | 说明 |
|------|------|------|
| 身份标识 | `ActorId = (Process, Fiber, InstanceId)` | 全局唯一定位 |
| 邮箱 | `MailBoxComponent` | 注册到 Fiber 的 Mailboxes |
| 消息处理 | `[MessageHandler]` | 按消息类型分发 |

### 消息投递流程

```
发送方 → ProcessInnerSender.Send(actorId, msg)
       → MessageQueue 入队（按目标 FiberId）
       → 目标 Fiber 的 ProcessInnerSender.Update() 取出
       → 按 InstanceId 查 Mailboxes
       → MailBoxComponent.Add() 分发处理
```

### MessageQueue

同进程内 Fiber 之间的消息投递队列：
```csharp
// key: 目标FiberId, value: 该Fiber的待处理消息队列
ConcurrentDictionary<int, ConcurrentQueue<MessageInfo>> messages;
```

---

## 七、客户端与服务器通信 vs Actor

| 通信方向 | 机制 | 走Actor？ |
|----------|------|-----------|
| 客户端 → 服务器 | Session + MessageSessionHandler | 否 |
| 服务器 → 客户端 | Session.Send() | 否 |
| 服务器内部（跨Fiber/跨进程） | ActorId + MessageQueue/NetInner | **是** |
| 服务器转发给客户端 | Actor 到 Gate，再由 Session 发出 | 中间是 Actor |

客户端完全不感知 Actor 的存在，只知道连上了一个 Gate 的 Session。

---

## 八、架构层次

```
Machine (物理机器, StartMachineConfig)
  └── Process (OS进程, StartProcessConfig)
        ├── Main Fiber (id=-1, 主线程)
        ├── NetInner Fiber (id=-2, 进程间通信)
        ├── Scene Fiber: Realm (登录验证)
        ├── Scene Fiber: Gate (网关)
        ├── Scene Fiber: Location (位置服务)
        ├── Scene Fiber: Map (游戏逻辑)
        └── ...
```

### 配置体系

| 配置 | 作用 |
|------|------|
| StartMachineConfig | 机器的内外网 IP、Watcher 端口 |
| StartProcessConfig | 进程 ID、所属机器、内网端口 |
| StartSceneConfig | Scene 的进程归属、Zone、SceneType、端口 |
| StartZoneConfig | Zone 的数据库连接信息 |

### 部署模式

| 模式 | 特点 |
|------|------|
| Localhost | 所有 Scene 在同一进程，本地开发 |
| Release | 每种 SceneType 独立进程，生产部署 |
| Benchmark | 压测配置 |
| RouterTest | 路由集群测试 |

---

## 九、各 SceneType 职责

| SceneType | 职责 |
|-----------|------|
| Realm | 登录验证，分配 Gate |
| Gate | 网关，管理客户端连接和玩家会话 |
| Location | Actor 位置服务，记录 Entity 所在 Fiber/进程 |
| Map | 游戏地图，处理游戏逻辑 |
| Match | 匹配服务（帧同步） |
| Router | 网络路由中继，NAT 穿透 |
| NetInner | 进程间内部通信 |
| Robot | 自动化测试机器人 |
