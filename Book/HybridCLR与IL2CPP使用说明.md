# HybridCLR 与 IL2CPP 使用说明

本文说明本项目 Unity 客户端中 IL2CPP、HybridCLR、YooAsset 与 ET 四个业务 DLL 的关系，并记录构建和排查时的注意事项。

## 1. 一句话概括

项目在 IL2CPP 平台上采用混合执行模式：首包的 Unity、ET 基础设施与第三方 AOT 程序集由 IL2CPP 编译为原生代码；客户端业务程序集作为 DLL 随资源包加载，由 HybridCLR 在运行时执行。

```text
AOT 首包代码                 热更新业务 DLL
Unity / Core / Loader 等       Model / Hotfix / ModelView / HotfixView
        │                               │
        └──── IL2CPP 原生执行 ──── HybridCLR 解释执行 ────┘
```

IL2CPP 是 Unity 的 Ahead Of Time（AOT）编译后端：C# 经 DLL、C++ 后被编译为目标平台的原生代码。它适合 Android、iOS、小游戏等平台，但普通 IL2CPP 不能直接完整执行发布后新下载的 C# DLL。

HybridCLR 改造 IL2CPP 运行时，提供动态程序集加载、元数据注册和解释执行能力，使上述平台可加载并执行新的 C# DLL。

## 2. 项目配置

### 2.1 IL2CPP

`Unity/ProjectSettings/ProjectSettings.asset` 中，Android、iPhone 和 Standalone 的 `scriptingBackend` 均配置为 IL2CPP。

### 2.2 HybridCLR 版本与热更新程序集

`Unity/Packages/manifest.json` 使用：

```json
"com.code-philosophy.hybridclr": "https://gitee.com/focus-creative-games/hybridclr_unity.git#v8.12.0"
```

`Unity/ProjectSettings/HybridCLRSettings.asset` 中的 `hotUpdateAssemblies` 为：

```text
Unity.Model
Unity.Hotfix
Unity.ModelView
Unity.HotfixView
```

因此，四个程序集当前都属于可作为 DLL 下发和加载的程序集；`Unity.Hotfix` 的名字表示 ET 的业务层划分，并不表示只有它可以热更新。

它们的职责划分大致为：

| 程序集 | 主要职责 |
| --- | --- |
| `Unity.Model` | 无表现层的模型、组件与基础业务数据 |
| `Unity.Hotfix` | 业务系统、网络、流程等逻辑 |
| `Unity.ModelView` | 依赖 Unity 表现层的基础模型 |
| `Unity.HotfixView` | UI、表现和交互逻辑 |

### 2.3 AOT 补充元数据程序集

同一配置文件中的 `patchAOTAssemblies` 列出 `Unity.Core.dll`、`Unity.Loader.dll`、`mscorlib.dll`、`System.dll`、`MemoryPack.dll`、`YIUIFramework.dll` 等程序集。

这些 DLL 不作为新的可执行热更新逻辑加载；它们的可执行代码已经在 Player 的原生库中。复制到资源包的 `.dll.bytes` 用于向 HybridCLR 补充类型、方法、泛型、反射等元数据。

## 3. 运行时加载流程

入口是 `Unity/Assets/Scripts/Loader/CodeLoader.cs`。

1. `DownloadAsync` 通过 `ResourcesComponent.LoadAllAssetsAsync<TextAsset>` 取得业务 DLL 和 AOT 元数据资源。
2. 在非 Editor 且启用 IL2CPP 时，`Start` 遍历 AOT DLL，调用：

   ```csharp
   RuntimeApi.LoadMetadataForAOTAssembly(bytes, HomologousImageMode.SuperSet);
   ```

3. 随后使用 `Assembly.Load` 加载 `Unity.Model`、`Unity.ModelView`、`Unity.Hotfix`、`Unity.HotfixView` 的 DLL/PDB。
4. 程序集交给 `CodeTypes` 扫描，最终通过 `ET.Entry.Start` 启动客户端。

这里的 AOT 元数据必须先于业务 DLL 加载，否则热更新代码访问 AOT 类型、泛型或反射信息时可能失败。

> 当前 `CodeLoader` 的行为是启动流程中加载程序集。它本身没有实现“已运行游戏中卸载旧 DLL 并替换为新 DLL”的业务流程；资源更新后通常在下一次启动时加载新版本。

## 4. 为什么用一个 DLL 名称却能取到全部 DLL

`CodeLoader` 使用：

```csharp
this.dlls = await ResourcesComponent.Instance.LoadAllAssetsAsync<TextAsset>("Unity.Model.dll");
this.aotDlls = await ResourcesComponent.Instance.LoadAllAssetsAsync<TextAsset>("mscorlib.dll");
```

这不表示 `Unity.Model.dll` 内嵌了其它 DLL。它只是 YooAsset 的资源定位入口。

`Unity/Assets/Settings/AssetBundleCollectorSetting.asset` 的配置是：

| 目录 | 地址规则 | 打包规则 |
| --- | --- | --- |
| `Assets/Bundles/Code` | `AddressByFileName` | `PackDirectory` |
| `Assets/Bundles/AotDlls` | `AddressByFileName` | `PackDirectory` |

`PackDirectory` 会将一个目录内的资源放入同一个 Bundle。YooAsset 先由 `Unity.Model.dll` 或 `mscorlib.dll` 找到对应 Bundle，随后 `LoadAllAssetsAsync` 取回其中全部 `TextAsset`，并以 `TextAsset.name` 作为字典 key。

因此 `dlls` 中包含四个业务 DLL 和 PDB，`aotDlls` 中包含该 AOT 元数据目录的所有 DLL。

如果以后把打包规则改为逐文件打包，这段“选一个文件作为目录包入口，再加载包内全部资源”的逻辑将不再成立，需要同步修改加载逻辑。

## 5. 构建期：HybridCLR 实际生成什么

完整的构建链如下：

```text
改 C# 代码
  ↓
F6 / ET Compile
  ↓  生成 4 个 DLL + PDB，复制到 Assets/Bundles/Code
HybridCLR → Generate → All
  ↓  生成桥接、泛型引用、防裁剪信息；得到当前平台裁剪后的 AOT DLL
HybridCLR → CopyAotDlls
  ↓  复制 AOT 元数据到 Assets/Bundles/AotDlls
YooAsset ForceRebuild
  ↓
IL2CPP Build Player / 出包
```

### 5.1 业务 DLL

`Unity/Assets/Scripts/Editor/Assembly/AssemblyTool.cs` 的 `CopyHotUpdateDlls` 将编译产物复制为：

```text
Assets/Bundles/Code/Unity.Model.dll.bytes
Assets/Bundles/Code/Unity.Hotfix.dll.bytes
Assets/Bundles/Code/Unity.ModelView.dll.bytes
Assets/Bundles/Code/Unity.HotfixView.dll.bytes
```

以及相应的 PDB 文件。

### 5.2 AOT 元数据 DLL

`Unity/Assets/Scripts/Editor/Plugins/HybridCLR/HybridCLREditor.cs` 的 `HybridCLR/CopyAotDlls`：

1. 从 `HybridCLRData/AssembliesPostIl2CppStrip/<BuildTarget>` 读取裁剪后的 AOT DLL；
2. 按 `patchAOTAssemblies` 选择需要的 DLL；
3. 复制到 `Assets/Bundles/AotDlls/<程序集名>.bytes`。

这些文件是元数据补充材料，不能理解为重新执行其中的 C# 代码。

### 5.3 泛型引用与防裁剪

`Generate All` 输出以下可见文件：

- `Unity/Assets/Scripts/Loader/Plugins/HybridCLR/Generated/AOTGenericReferences.cs`
- `Unity/Assets/Scripts/Loader/Plugins/HybridCLR/Generated/link.xml`

`AOTGenericReferences.cs` 记录分析到的 AOT 泛型类型和方法，例如项目中的 `AEvent<,>`、MemoryPack 的泛型调用、YooAsset 泛型加载方法等；目的是让 IL2CPP 构建时预生成或保留需要的泛型实例。

`link.xml` 同样由 HybridCLR 生成，配置输出路径为 `HybridCLRSettings.asset` 的 `outputLinkFile`。它告诉 Unity linker 保留热更新代码要访问的 AOT 类型和成员。不要直接修改该 Generated 文件，因为下次 `Generate All` 会覆盖它。若确有长期手写保留规则，应单独维护另一个 `link.xml`。

## 6. 桥接代码在哪里，何时运行

ET C# 层只负责加载 AOT 元数据和热更新 DLL；并没有手写的 `Bridge.Call()` 一类调用。

HybridCLR 的桥接代码是构建期生成的 C++，例如 Android 临时 IL2CPP 工程中的：

```text
HybridCLRData/StrippedAOTDllsTempProj/Android/
  unityLibrary/src/main/Il2CppOutputProject/IL2CPP/
    libil2cpp/hybridclr/generated/MethodBridge.cpp
```

其中包含解释器到 AOT 的 `Managed2Native`、AOT 到解释器的 `Native2Managed`、`ReversePInvoke` 等桥接表和函数。最终它会被编译进 Player 的原生库。

所以运行期调用链是：

```text
ET CodeLoader 加载 DLL / 元数据
  ↓
HybridCLR 解析热更新方法
  ↓
生成并编入 Player 的原生 MethodBridge 自动完成跨边界调用
```

`HybridCLRData/il2cpp_plus_repo/.../generated/MethodBridge.cpp` 中的短小空 stub 是源码模板；分析当前平台产物时，应看 `StrippedAOTDllsTempProj/<平台>` 下的实际生成文件。

## 7. async、泛型与 MissingMethodException

HybridCLR 官方说明支持 `async Task`，并不等于“HybridCLR 不支持 async/await 隐藏状态机”。不要把项目里的历史规避代码理解为功能不支持。

但在 AOT 与热更新交界处，仍需重点验证以下情况：

- 热更新程序集调用 AOT 泛型方法；
- 泛型方法自身使用 `async` / `await`，编译器生成隐藏状态机；
- 值类型（`struct`）作为泛型参数；
- 反射、闭包、迭代器、泛型虚方法等构建期较难完整推导的调用；
- DLL、AOT 元数据、YooAsset 包和 APK 不是同一轮构建产物。

本项目的 `Unity/Assets/Scripts/Core/World/Module/EventSystem/IEvent.cs` 对 `AEvent<S, A>.Handle` 有明确注释：不要把该泛型入口改为 `async`。这是项目在特定 IL2CPP/HybridCLR 构建链上出现泛型 async 状态机问题后的防御性约束。

因此应采用的结论是：**可以使用 async/await；但对跨 AOT/热更新边界的复杂泛型 async 调用，必须在目标 IL2CPP 平台验证。**

## 8. 日常开发检查表

### 改动热更新代码后

1. F6 / ET Compile；
2. `HybridCLR → Generate → All`；
3. `HybridCLR → CopyAotDlls`；
4. YooAsset 重建；
5. 在目标平台运行验证。

### 必须重新执行完整 HybridCLR 生成链的情况

- 切换 Android、iOS、微信小游戏等目标平台；
- 构建新的完整首包；
- 修改 AOT 程序集、泛型使用、程序集引用或 `HybridCLRSettings`；
- 更新 Unity/团结引擎或 HybridCLR 版本；
- 切换 Debug/Release 或 Development 等会影响 IL2CPP 构建结果的选项。

不同平台的条件宏、程序集、裁剪结果、桥接需求和 AOT 元数据均可能不同，不能把一个平台生成的 `AotDlls` 用于另一个平台。

### 出现 MissingMethodException 时

按以下顺序检查：

1. 异常是否出现 `d__`：这通常指向编译器生成的 async 状态机；
2. 是否是热更新代码第一次使用某个新泛型组合；
3. DLL、`Generate All` 产物、`CopyAotDlls`、YooAsset 包、APK 是否来自同一轮构建；
4. 当前目标平台的 AOT 元数据 DLL 是否齐全；
5. 是否可以将 AOT 边界上的泛型 async 入口改为同步入口，并把 await 下沉到非泛型辅助方法；
6. 必须在目标 IL2CPP 真机/小游戏环境复现和验证，不能只以 Unity Editor 成功作为结论。

## 9. 相关项目文件

| 文件 | 说明 |
| --- | --- |
| `Unity/ProjectSettings/HybridCLRSettings.asset` | 热更新 DLL、AOT 补充元数据 DLL、生成输出配置 |
| `Unity/Packages/manifest.json` | HybridCLR 包版本 |
| `Unity/Assets/Scripts/Loader/CodeLoader.cs` | 运行时下载、元数据加载和 DLL 加载入口 |
| `Unity/Assets/Scripts/Loader/Resource/ResourcesComponent.cs` | YooAsset 的 `LoadAllAssetsAsync` 封装 |
| `Unity/Assets/Settings/AssetBundleCollectorSetting.asset` | Code/AotDlls 的目录收集和打包规则 |
| `Unity/Assets/Scripts/Editor/Assembly/AssemblyTool.cs` | 编译并复制业务 DLL/PDB |
| `Unity/Assets/Scripts/Editor/Plugins/HybridCLR/HybridCLREditor.cs` | `CopyAotDlls` 菜单实现 |
| `Unity/Assets/Scripts/Loader/Plugins/HybridCLR/Generated/AOTGenericReferences.cs` | HybridCLR 生成的 AOT 泛型引用信息 |
| `Unity/Assets/Scripts/Loader/Plugins/HybridCLR/Generated/link.xml` | HybridCLR 生成的防裁剪规则 |
