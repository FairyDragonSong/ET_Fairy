# ET8 框架完整学习指南

> 基于项目代码梳理的系统学习文档

---

## 目录

1. [框架概述](#1-框架概述)
2. [项目结构](#2-项目结构)
3. [核心架构-ECS系统](#3-核心架构-ecs系统)
4. [Entity实体系统](#4-entity实体系统)
5. [组件系统Component](#5-组件系统component)
6. [事件系统EventSystem](#6-事件系统eventsystem)
7. [协程与异步ETTask](#7-协程与异步ettask)
8. [Fiber纤程系统](#8-fiber纤程系统)
9. [网络通信系统](#9-网络通信系统)
10. [消息系统与协议](#10-消息系统与协议)
11. [Actor模型](#11-actor模型)
12. [Scene场景管理](#12-scene场景管理)
13. [配置表系统](#13-配置表系统)
14. [AI框架](#14-ai框架)
15. [帧同步LockStep](#15-帧同步lockstep)
16. [数值系统Numeric](#16-数值系统numeric)
17. [寻路系统Recast](#17-寻路系统recast)
18. [数据库模块DB](#18-数据库模块db)
19. [热更新机制](#19-热更新机制)
20. [双端共用代码](#20-双端共用代码)

---

## 1. 框架概述

### 1.1 ET框架简介

ET是一个基于C#的双端(客户端+服务端)游戏框架，主要特点：

- **双端共用代码**：客户端和服务端可共用大量逻辑代码
- **ECS架构**：Entity-Component-System设计模式
- **单线程多进程**：服务端采用单线程多进程架构
- **Actor模型**：支持分布式消息通信
- **协程异步**：基于async/await的协程系统
- **热更新**：支持代码热重载

### 1.2 技术栈

| 技术 | 说明 |
|------|------|
| Unity | 客户端引擎 |
| .NET Core | 服务端运行时 |
| MongoDB | 数据库 |
| MemoryPack | 序列化 |
| KCP/TCP | 网络协议 |
| Luban | 配置表工具 |

---

## 2. 项目结构

### 2.1 整体目录结构

```
ET/
├── Unity/                 # Unity客户端项目
│   └── Assets/
│       └── Scripts/
│           ├── Core/      # 核心框架代码
│           ├── Model/     # 数据层(纯数据)
│           ├── Hotfix/    # 热更逻辑层
│           ├── ModelView/ # 客户端表现层数据
│           ├── HotfixView/# 客户端表现层逻辑
│           ├── Loader/    # 加载器
│           ├── ThirdParty/# 第三方库
│           └── Editor/    # 编辑器工具
├── DotNet/                # 服务端项目
│   ├── App/              # 启动入口
│   ├── Core/             # 核心代码(与Unity共用)
│   ├── Model/            # 数据层
│   ├── Hotfix/           # 热更逻辑层
│   └── Loader/           # 加载器
├── Config/                # 配置文件
├── Book/                  # 文档
└── Tools/                 # 工具
```

### 2.2 代码分层说明

| 层级 | 位置 | 说明 |
|------|------|------|
| Core | Scripts/Core | 框架核心,不可热更 |
| Model | Scripts/Model | 数据定义,不可热更 |
| Hotfix | Scripts/Hotfix | 业务逻辑,可热更 |
| ModelView | Scripts/ModelView | 客户端表现数据 |
| HotfixView | Scripts/HotfixView | 客户端表现逻辑 |

### 2.3 客户端/服务端代码划分

```
Model/
├── Client/    # 仅客户端
├── Server/    # 仅服务端  
├── Share/     # 双端共用
└── Generate/  # 自动生成代码

Hotfix/
├── Client/    # 仅客户端
├── Server/    # 仅服务端
└── Share/     # 双端共用
```

---

## 3. 核心架构-ECS系统

### 3.1 ECS概念

ECS = Entity + Component + System

| 概念 | 说明 | ET中的实现 |
|------|------|-----------|
| Entity | 实体,游戏对象 | Entity类 |
| Component | 组件,纯数据 | 继承Entity的组件类 |
| System | 系统,纯逻辑 | XXXSystem静态类 |

### 3.2 ET的ECS特点

1. **一切皆Entity**：组件也是Entity，可以嵌套
2. **树状结构**：Entity可以有Children和Components
3. **数据逻辑分离**：Entity/Component存数据，System写逻辑

### 3.3 设计示例

```csharp
// 定义组件(纯数据)
public class MoveComponent : Entity, IAwake, IUpdate
{
    public float Speed;
    public Vector3 TargetPos;
}

// 定义System(纯逻辑)
[EntitySystemOf(typeof(MoveComponent))]
public static partial class MoveComponentSystem
{
    [EntitySystem]
    private static void Awake(this MoveComponent self)
    {
        self.Speed = 5f;
    }
    
    [EntitySystem]
    private static void Update(this MoveComponent self)
    {
        // 移动逻辑
    }
}
```

---

## 4. Entity实体系统

### 4.1 Entity基类

位置：`Core/Entity/Entity.cs`

```csharp
public abstract partial class Entity : DisposeObject, IPool
{
    public long InstanceId { get; }     // 实例ID,全局唯一
    public long Id { get; }             // 实体ID
    public Entity Parent { get; }       // 父实体
    public IScene IScene { get; }       // 所属Scene
    
    // 子实体集合
    public SortedDictionary<long, Entity> Children { get; }
    // 组件集合  
    public SortedDictionary<long, Entity> Components { get; }
}
```

### 4.2 Entity状态标记

```csharp
[Flags]
public enum EntityStatus : byte
{
    None = 0,
    IsFromPool = 1,      // 来自对象池
    IsRegister = 1 << 1, // 已注册到系统
    IsComponent = 1 << 2,// 是组件
    IsCreated = 1 << 3,  // 已创建
    IsNew = 1 << 4,      // 新创建(非反序列化)
}
```

### 4.3 Entity核心方法

```csharp
// 添加组件
entity.AddComponent<MoveComponent>();
entity.AddComponent<BuffComponent, int>(buffId);

// 获取组件
var move = entity.GetComponent<MoveComponent>();

// 移除组件
entity.RemoveComponent<MoveComponent>();

// 添加子实体
entity.AddChild<Unit>();
entity.AddChildWithId<Unit>(id);

// 获取子实体
var child = entity.GetChild<Unit>(id);

// 释放
entity.Dispose();
```

### 4.4 InstanceId的作用

1. **唯一标识**：区分不同实例,包括对象池复用的对象
2. **异步安全**：await后检查InstanceId判断对象是否已释放
3. **Actor定位**：通过InstanceId可找到Entity位置

```csharp
long instanceId = self.InstanceId;
await SomeAsync();
if (self.InstanceId != instanceId)
{
    // 对象已被释放或复用,直接返回
    return;
}
```

---

## 5. 组件系统Component

### 5.1 组件定义规范

```csharp
// 使用特性标记组件归属
[ComponentOf(typeof(Unit))]
public class MoveComponent : Entity, IAwake, IUpdate, IDestroy
{
    public float Speed;
    public Vector3 Target;
}

// 子实体标记
[ChildOf(typeof(UnitComponent))]
public class Unit : Entity, IAwake<int>
{
    public int ConfigId;
}
```

### 5.2 组件生命周期接口

| 接口 | 触发时机 |
|------|---------|
| IAwake | 组件创建时 |
| IAwake<T> | 带参数创建时 |
| IUpdate | 每帧更新 |
| ILateUpdate | 每帧延迟更新 |
| IDestroy | 销毁时 |
| IDeserialize | 反序列化后 |
| ISerialize | 序列化前 |

### 5.3 System的编写

```csharp
[EntitySystemOf(typeof(MoveComponent))]
[FriendOf(typeof(MoveComponent))]  // 可访问private成员
public static partial class MoveComponentSystem
{
    [EntitySystem]
    private static void Awake(this MoveComponent self)
    {
        // 初始化
    }
    
    [EntitySystem]
    private static void Update(this MoveComponent self)
    {
        // 每帧逻辑
    }
    
    [EntitySystem]
    private static void Destroy(this MoveComponent self)
    {
        // 清理
    }
    
    // 自定义方法
    public static void MoveTo(this MoveComponent self, Vector3 target)
    {
        self.Target = target;
    }
}
```

---

## 6. 事件系统EventSystem

### 6.1 系统事件类型

| 事件 | 说明 |
|------|------|
| AwakeSystem | 创建时触发 |
| UpdateSystem | 每帧触发 |
| LateUpdateSystem | 每帧延迟触发 |
| DestroySystem | 销毁时触发 |
| DeserializeSystem | 反序列化后触发 |
| SerializeSystem | 序列化前触发 |

### 6.2 自定义事件

定义事件:
```csharp
// 定义事件结构
public struct ChangePosition
{
    public Unit Unit;
    public float3 OldPos;
}
```

发布事件:
```csharp
EventSystem.Instance.Publish(scene, new ChangePosition() 
{ 
    Unit = this, 
    OldPos = oldPos 
});
```

订阅事件:
```csharp
[Event(SceneType.Client)]
public class ChangePosition_Handler : AEvent<Scene, ChangePosition>
{
    protected override async ETTask Run(Scene scene, ChangePosition args)
    {
        // 处理逻辑
        await ETTask.CompletedTask;
    }
}
```

### 6.3 事件的解耦作用

```
[数值变化] 
    ↓ 发布NumericChange事件
    ├─→ [UI血条更新]  订阅处理
    ├─→ [头顶血条更新] 订阅处理
    └─→ [成就系统检测] 订阅处理
```

---

## 7. 协程与异步ETTask

### 7.1 ETTask简介

ETTask是ET框架自定义的异步任务类，比Task更轻量。

位置：`ThirdParty/ETTask/`

### 7.2 基本使用

```csharp
// 异步方法定义
public static async ETTask<int> TestAsync()
{
    await TimerComponent.Instance.WaitAsync(1000);
    return 42;
}

// 调用
int result = await TestAsync();

// 不等待结果
TestAsync().Coroutine();
```

### 7.3 ETTask vs Task

| 特性 | ETTask | Task |
|------|--------|------|
| 内存分配 | 对象池复用 | 每次new |
| 线程 | 默认单线程 | 可能多线程 |
| 性能 | 更好 | 一般 |
| 游戏适用性 | 专门优化 | 通用 |

### 7.4 取消机制

```csharp
public async ETTask MoveAsync(ETCancellationToken token)
{
    while (!arrived)
    {
        // 检查取消
        if (token.IsCancel())
        {
            return;
        }
        
        await TimerComponent.Instance.WaitAsync(100, token);
    }
}
```

---

## 8. Fiber纤程系统

### 8.1 Fiber概念

Fiber(纤程)是ET8的核心概念，每个Fiber相当于一个独立的执行单元。

位置：`Core/Fiber/Fiber.cs`

```csharp
public class Fiber : IDisposable
{
    public int Id;                    // Fiber ID
    public int Zone;                  // 区服ID
    public Scene Root { get; }        // 根Scene
    public EntitySystem EntitySystem; // 实体系统
    public Mailboxes Mailboxes;       // 邮箱系统
    public ThreadSynchronizationContext ThreadSynchronizationContext;
}
```

### 8.2 Fiber的作用

1. **隔离执行环境**：每个Fiber有独立的Scene树
2. **消息邮箱**：Fiber间通过Mailbox通信
3. **线程安全**：Fiber内部单线程执行

### 8.3 Fiber生命周期

```csharp
// 创建Fiber
Fiber fiber = new Fiber(id, zone, sceneType, name);

// 每帧更新
fiber.Update();      // 执行Update系统
fiber.LateUpdate();  // 执行LateUpdate系统

// 销毁
fiber.Dispose();
```

### 8.4 多Fiber架构

```
进程
├── Fiber1 (Gate)
│   └── Scene (GateScene)
├── Fiber2 (Map)
│   └── Scene (MapScene)
└── Fiber3 (Location)
    └── Scene (LocationScene)
```

---

## 9. 网络通信系统

### 9.1 网络架构

位置：`Core/Network/`

| 类 | 说明 |
|---|------|
| AService | 网络服务抽象基类 |
| KService | KCP协议实现 |
| TService | TCP协议实现 |
| AChannel | 通道抽象基类 |
| KChannel | KCP通道 |
| TChannel | TCP通道 |

### 9.2 支持的协议

```csharp
public enum NetworkProtocol
{
    TCP,    // TCP协议
    UDP,    // UDP/KCP协议
}
```

### 9.3 KService核心

```csharp
public sealed class KService : AService
{
    // 创建服务(服务端监听)
    public KService(IPEndPoint ipEndPoint, NetworkProtocol protocol, ServiceType serviceType);
    
    // 创建服务(客户端连接)
    public KService(AddressFamily addressFamily, NetworkProtocol protocol, ServiceType serviceType);
    
    // 发送数据
    public override void Send(long channelId, MemoryBuffer memoryBuffer);
    
    // 创建连接
    public override void Create(long id, IPEndPoint ipEndPoint);
    
    // 移除连接
    public override void Remove(long id, int error = 0);
}
```

### 9.4 连接流程(KCP)

```
客户端                          服务端
   |                              |
   |------ SYN (请求连接) ------->|
   |                              |
   |<----- ACK (确认连接) --------|
   |                              |
   |====== MSG (数据传输) =======>|
   |<===== MSG (数据传输) ========|
   |                              |
   |------ FIN (断开连接) ------->|
```

---

## 10. 消息系统与协议

### 10.1 消息定义

位置：`Model/Generate/*/Message/`

```protobuf
// OuterMessage.proto
message C2R_Login // IRequest
{
    int32 RpcId = 1;
    string Account = 2;
    string Password = 3;
}

message R2C_Login // IResponse
{
    int32 RpcId = 1;
    int32 Error = 2;
    string Message = 3;
    string Address = 4;
    long Key = 5;
    long GateId = 6;
}
```

### 10.2 消息类型

| 类型 | 说明 | 基类 |
|------|------|------|
| IMessage | 普通消息 | - |
| IRequest | 请求消息 | IMessage |
| IResponse | 响应消息 | IMessage |
| ILocationMessage | 位置消息 | IMessage |
| ILocationRequest | 位置请求 | IRequest |

### 10.3 Session会话

```csharp
public sealed class Session : Entity, IAwake<AService>, IDestroy
{
    public AService AService { get; set; }
    public int RpcId { get; set; }
    public IPEndPoint RemoteAddress { get; set; }
    
    // 发送消息(不等待响应)
    public void Send(IMessage message);
    
    // 发送请求(等待响应)
    public async ETTask<IResponse> Call(IRequest request);
}
```

### 10.4 消息处理器

```csharp
// 普通消息处理
[MessageSessionHandler(SceneType.Gate)]
public class C2G_PingHandler : MessageSessionHandler<C2G_Ping, G2C_Ping>
{
    protected override async ETTask Run(Session session, C2G_Ping request, G2C_Ping response)
    {
        response.Time = TimeInfo.Instance.ServerNow();
        await ETTask.CompletedTask;
    }
}
```

---

## 11. Actor模型

### 11.1 Actor概念

Actor是一种消息通信模型，特点：
- 只通过消息通信
- 不需要知道对方位置
- 只需知道ActorId即可发消息

### 11.2 ET的Actor实现

| 对比 | ET | Erlang | Skynet |
|------|------|--------|--------|
| 架构 | 单线程多进程 | 单进程多线程 | 单进程多线程 |
| Actor | Entity | 进程 | Lua虚拟机 |
| ActorId | InstanceId | 进程Id | 服务地址 |

### 11.3 MailBoxComponent

Entity添加MailBoxComponent后即成为Actor：

```csharp
// 添加邮箱组件
session.AddComponent<MailBoxComponent, string>(MailboxType.GateSession);
```

### 11.4 Actor消息发送

```csharp
// 获取ActorId
ActorId actorId = entity.GetActorId();

// 发送Actor消息
MessageSender sender = root.GetComponent<MessageSender>();
sender.Send(actorId, message);

// Actor RPC调用
IResponse response = await sender.Call(actorId, request);
```

### 11.5 Actor消息处理

```csharp
// Actor消息处理器
[MessageHandler(SceneType.Map)]
public class Actor_TestHandler : MessageHandler<Unit, Actor_Test>
{
    protected override async ETTask Run(Unit unit, Actor_Test message)
    {
        Log.Debug(message.Info);
        await ETTask.CompletedTask;
    }
}
```

### 11.6 ActorLocation

用于定位Actor的位置：

```csharp
// 注册Actor位置
await LocationProxyComponent.Instance.Add(locationKey, actorId);

// 获取Actor位置
ActorId actorId = await LocationProxyComponent.Instance.Get(locationKey);

// 发送Location消息
await MessageLocationSender.Instance.Send(locationKey, message);
```

---

## 12. Scene场景管理

### 12.1 Scene定义

```csharp
public class Scene : Entity, IScene
{
    public Fiber Fiber { get; set; }
    public SceneType SceneType { get; set; }
    public string Name { get; set; }
}
```

### 12.2 SceneType场景类型

```csharp
public enum SceneType
{
    None,
    Main,       // 主场景
    NetClient,  // 网络客户端
    Gate,       // 网关
    Map,        // 地图
    Location,   // 位置服务
    Robot,      // 机器人
    Realm,      // 登录服
    Room,       // 房间
    Match,      // 匹配
    // ...
}
```

### 12.3 Scene树结构

```
Root Scene
├── UnitComponent
│   ├── Unit (Player1)
│   │   ├── MoveComponent
│   │   └── NumericComponent
│   └── Unit (Monster1)
├── TimerComponent
├── ObjectPoolComponent
└── MessageDispatcher
```

---

## 13. 配置表系统

### 13.1 Luban配置工具

配置文件位置：`Tools/Luban/`

### 13.2 配置表定义

Excel表格 → Luban生成 → C#类 + 二进制数据

### 13.3 生成的配置类

```csharp
// Config/UnitConfig.cs
public partial class UnitConfig
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Type { get; set; }
    public int Hp { get; set; }
}

// 配置表容器
public partial class UnitConfigCategory : Singleton<UnitConfigCategory>
{
    public Dictionary<int, UnitConfig> DataMap { get; }
    
    public UnitConfig Get(int id);
    public bool Contain(int id);
}
```

### 13.4 配置使用

```csharp
// 获取单个配置
UnitConfig config = UnitConfigCategory.Instance.Get(1001);

// 遍历所有配置
foreach (var kv in UnitConfigCategory.Instance.DataMap)
{
    UnitConfig cfg = kv.Value;
}
```

---

## 14. AI框架

### 14.1 AI设计理念

AI本质：根据当前状态，执行相应行为

- **状态判断**：Check当前条件
- **执行行为**：Run对应逻辑

### 14.2 AI节点结构

```csharp
public abstract class AAIHandler
{
    // 检测条件是否满足
    public abstract int Check(AIComponent aiComponent, AIConfig aiConfig);
    
    // 执行行为(协程)
    public abstract ETTask Execute(AIComponent aiComponent, AIConfig aiConfig, ETCancellationToken token);
}
```

### 14.3 AI示例-巡逻

```csharp
public class AI_XunLuo : AAIHandler
{
    public override int Check(AIComponent aiComponent, AIConfig aiConfig)
    {
        // 检测是否在巡逻范围内，周围是否有敌人
        if (!InPatrolArea()) return 0;
        if (HasEnemy()) return 0;
        return 1;
    }
    
    public override async ETTask Execute(AIComponent aiComponent, AIConfig aiConfig, ETCancellationToken token)
    {
        while (true)
        {
            Vector3 nextPoint = FindNextPoint();
            await MoveToAsync(nextPoint, token);
            
            if (token.IsCancel()) return;
            
            await TimerComponent.Instance.WaitAsync(2000, token);
            
            if (token.IsCancel()) return;
        }
    }
}
```

### 14.4 AI运行流程

```csharp
// AI主循环
while (true)
{
    await TimerComponent.Instance.WaitAsync(1000);
    
    // 遍历所有AI节点，找到满足条件的
    foreach (var node in aiNodes)
    {
        if (node.Check() > 0)
        {
            if (node != currentNode)
            {
                // 取消当前行为
                cancelToken.Cancel();
                
                // 执行新行为
                cancelToken = new ETCancellationToken();
                node.Execute(cancelToken).Coroutine();
                currentNode = node;
            }
            break;
        }
    }
}
```

---

## 15. 帧同步LockStep

### 15.1 帧同步概念

帧同步：所有客户端执行相同的输入序列，保证结果一致

### 15.2 核心类

位置：`Model/Share/LockStep/`

| 类 | 说明 |
|---|------|
| Room | 房间,管理帧同步 |
| LSWorld | 帧同步世界 |
| LSEntity | 帧同步实体基类 |
| LSUnit | 帧同步单位 |
| FrameBuffer | 帧缓存 |
| LSInput | 玩家输入 |

### 15.3 Room房间

```csharp
public class Room : Entity, IScene
{
    public FrameBuffer FrameBuffer { get; set; }    // 帧缓存
    public FixedTimeCounter FixedTimeCounter;        // 时间计算
    public List<long> PlayerIds { get; }             // 玩家列表
    public int PredictionFrame { get; set; }         // 预测帧
    public int AuthorityFrame { get; set; }          // 权威帧
    public Replay Replay { get; set; }               // 存档
    public LSWorld LSWorld { get; set; }             // 帧同步世界
}
```

### 15.4 LSWorld帧同步世界

```csharp
public partial class LSWorld : Entity, IScene
{
    public int Frame { get; set; }       // 当前帧
    public TSRandom Random { get; set; } // 确定性随机数
    
    // 每帧更新
    public void Update()
    {
        this.updater.Update();
        ++this.Frame;
    }
}
```

### 15.5 帧同步流程

```
1. 客户端收集输入 → 发送给服务器
2. 服务器收集所有客户端输入 → 广播给所有客户端
3. 客户端收到输入 → 执行游戏逻辑
4. 所有客户端执行相同输入 → 结果一致
```

### 15.6 确定性计算

使用TrueSync库保证浮点数计算确定性：

```csharp
// 使用定点数而非浮点数
FP speed = FP.FromFloat(5.0f);
TSVector3 position = TSVector3.zero;
```

---

## 16. 数值系统Numeric

### 16.1 NumericComponent

位置：`Model/Share/Module/Numeric/`

```csharp
public class NumericComponent : Entity
{
    public Dictionary<int, long> NumericDic { get; }
    
    // 获取数值
    public long this[int numericType] { get; set; }
    
    // 获取最终值(基础值+附加值)
    public long GetAsLong(int numericType);
    public float GetAsFloat(int numericType);
}
```

### 16.2 数值类型设计

```csharp
public static class NumericType
{
    public const int Max = 10000;
    
    // 属性 = 基础值 + 附加值
    // 基础值 Base: 1-999
    // 附加值 Add: 1000-1999
    // 最终值 Final: 与Base相同的ID
    
    public const int Hp = 1;
    public const int HpBase = Hp * 10 + 1;    // 11
    public const int HpAdd = Hp * 10 + 2;     // 12
    
    public const int Speed = 2;
    public const int SpeedBase = Speed * 10 + 1;
    public const int SpeedAdd = Speed * 10 + 2;
}
```

### 16.3 数值监听

```csharp
[NumericWatcher(SceneType.Client, NumericType.Hp)]
public class NumericWatcher_Hp : INumericWatcher
{
    public void Run(Unit unit, NumericChange args)
    {
        // HP变化时更新UI
        Log.Debug($"HP changed: {args.Old} -> {args.New}");
    }
}
```

---

## 17. 寻路系统Recast

### 17.1 Recast导航

使用DotRecast库实现导航网格寻路

位置：`ThirdParty/DotRecast/`

### 17.2 PathfindingComponent

```csharp
public class PathfindingComponent : Entity
{
    // 查找路径
    public List<Vector3> FindPath(Vector3 start, Vector3 end);
    
    // 射线检测
    public Vector3 Raycast(Vector3 start, Vector3 end);
}
```

### 17.3 使用示例

```csharp
// 获取寻路组件
var pathfinding = scene.GetComponent<PathfindingComponent>();

// 计算路径
List<Vector3> path = pathfinding.FindPath(startPos, endPos);

// 沿路径移动
foreach (var point in path)
{
    await MoveToAsync(point);
}
```

---

## 18. 数据库模块DB

### 18.1 DBComponent

位置：`Model/Server/Module/DB/`

使用MongoDB作为数据库。

```csharp
public class DBComponent : Entity
{
    // 保存
    public async ETTask Save<T>(T entity) where T : Entity;
    
    // 查询
    public async ETTask<T> Query<T>(long id) where T : Entity;
    
    // 查询多个
    public async ETTask<List<T>> Query<T>(Expression<Func<T, bool>> filter);
    
    // 删除
    public async ETTask Remove<T>(long id);
}
```

### 18.2 使用示例

```csharp
// 保存玩家数据
await dbComponent.Save(player);

// 查询玩家
Player player = await dbComponent.Query<Player>(playerId);

// 条件查询
var players = await dbComponent.Query<Player>(p => p.Level > 10);
```

---

## 19. 热更新机制

### 19.1 代码分层

| 层 | 热更 | 说明 |
|---|------|------|
| Core | ❌ | 框架核心 |
| Model | ❌ | 数据定义 |
| Hotfix | ✅ | 业务逻辑 |
| ModelView | ❌ | 表现数据 |
| HotfixView | ✅ | 表现逻辑 |

### 19.2 热更原理

1. Model层只定义数据，不含逻辑
2. Hotfix层通过扩展方法给Model添加逻辑
3. 热更时只需替换Hotfix程序集

### 19.3 HybridCLR

ET8支持使用HybridCLR实现热更新：
- 将Hotfix编译为DLL
- 运行时加载DLL执行

---

## 20. 双端共用代码

### 20.1 共用设计

```
Model/Share/     → 客户端+服务端共用数据
Hotfix/Share/    → 客户端+服务端共用逻辑
```

### 20.2 共用模块

| 模块 | 说明 |
|------|------|
| Unit | 游戏单位 |
| Move | 移动系统 |
| Numeric | 数值系统 |
| AI | AI系统 |
| Message | 消息定义 |
| Config | 配置表 |

### 20.3 条件编译

```csharp
#if SERVER
    // 仅服务端执行
#endif

#if UNITY
    // 仅客户端执行
#endif
```

---

## 附录A: 常用特性(Attribute)

| 特性 | 说明 |
|------|------|
| `[EntitySystemOf]` | 标记System对应的Entity |
| `[FriendOf]` | 允许访问私有成员 |
| `[ComponentOf]` | 标记组件归属 |
| `[ChildOf]` | 标记子实体归属 |
| `[MessageHandler]` | 消息处理器 |
| `[Event]` | 事件处理器 |
| `[NumericWatcher]` | 数值监听器 |
| `[ConsoleHandler]` | 控制台命令 |

---

## 附录B: 学习路线建议

### 初级阶段
1. 理解ECS架构思想
2. 掌握Entity/Component/System
3. 学习事件系统
4. 理解协程ETTask

### 中级阶段
5. 网络通信Session
6. 消息定义与处理
7. 配置表使用
8. Scene管理

### 高级阶段
9. Actor模型
10. Fiber纤程
11. 帧同步LockStep
12. 分布式架构

---

## 附录C: 相关资源

- **官方GitHub**: https://github.com/egametang/ET
- **官方文档**: Book目录下的md文件
- **QQ群**: 见README.md

---

*文档生成时间: 2026年3月*
*基于ET8框架代码分析*

---

## 目录

1. [框架概述](#1-框架概述)
2. [项目结构](#2-项目结构)
3. [核心架构-ECS系统](#3-核心架构-ecs系统)
4. [Entity实体系统](#4-entity实体系统)
5. [组件系统Component](#5-组件系统component)
6. [事件系统EventSystem](#6-事件系统eventsystem)
7. [协程与异步ETTask](#7-协程与异步ettask)
8. [Fiber纤程系统](#8-fiber纤程系统)
9. [网络通信系统](#9-网络通信系统)
10. [消息系统与协议](#10-消息系统与协议)
11. [Actor模型](#11-actor模型)
12. [Scene场景管理](#12-scene场景管理)
13. [配置表系统](#13-配置表系统)
14. [AI框架](#14-ai框架)
15. [帧同步LockStep](#15-帧同步lockstep)
16. [数值系统Numeric](#16-数值系统numeric)
17. [寻路系统Recast](#17-寻路系统recast)
18. [数据库模块DB](#18-数据库模块db)
19. [热更新机制](#19-热更新机制)
20. [双端共用代码](#20-双端共用代码)

---
