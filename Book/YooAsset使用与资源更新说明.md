# YooAsset 使用与资源更新说明

本文基于当前 ET/YIUI 项目源码和实际构建产物整理，说明 YooAsset 的资源目录、收集配置、构建流程、运行时初始化、资源加载、HybridCLR AOT 元数据、CDN 发布和热更新流程。

文档快照日期：2026-08-02。

## 一、当前项目概况

当前项目使用项目内嵌的 YooAsset：

```text
Unity/Packages/com.tuyoogame.yooasset
```

当前版本：

```text
2.1.1
```

截至 2026-08-02，YooAsset 官方最新正式版为 `3.0.5`。当前项目跨越了 `2.2`、`2.3` 和 `3.0`，属于主版本级差异，不能把升级理解为简单替换包文件。

项目目前只定义了一个逻辑资源包：

```text
DefaultPackage
```

当前体系已经完成：

- YooAsset 资源收集和 AssetBundle 构建。
- Android、MiniGame 等平台资源输出。
- 内置资源复制到 `StreamingAssets`。
- HostPlayMode 和 WebPlayMode 初始化。
- CDN 主地址和备用地址接入。
- 热更新 DLL、PDB、AOT 元数据加载。
- 场景、Prefab、配置和 YIUI 资源加载。
- 微信小游戏构建菜单和构建前预检。

当前尚未完成：

- 启动时查询远端资源版本。
- 下载并激活远端 Manifest。
- 计算新旧版本差异。
- 批量下载变更资源。
- 下载进度、失败重试和用户确认界面。
- 下载完成后再加载最新热更新 DLL 的完整闭环。

因此当前客户端可以加载内置资源，也可以按当前 Manifest 从 CDN 获取缺失的 Bundle，但不会主动发现 CDN 上发布的新 Manifest。

## 二、核心概念

### 2.1 ResourcePackage

`ResourcePackage` 是一组资源的逻辑管理容器，不是单个 `.bundle` 文件。

当前项目使用：

```text
DefaultPackage
```

它负责管理：

- 当前激活的 Manifest。
- 资源地址与资源路径的映射。
- AssetBundle 依赖关系。
- 内置文件、沙盒缓存和远端文件。
- 下载任务。
- 资源 Handle 和资源卸载。
- Package 版本更新。

### 2.2 PackageManifest

Manifest 是 ResourcePackage 的资源索引，记录：

- Package 名和 Package 版本。
- Address 到 AssetPath 的映射。
- AssetPath 所属 Bundle。
- Bundle 的 Hash、CRC 和文件大小。
- Bundle 依赖关系。
- Asset Tags。

当前文件示例：

```text
PackageManifest_DefaultPackage.version
PackageManifest_DefaultPackage_2026-07-23-2316.bytes
PackageManifest_DefaultPackage_2026-07-23-2316.hash
PackageManifest_DefaultPackage_2026-07-23-2316.json
```

### 2.3 AssetBundle

AssetBundle 是真正存储资源数据的物理文件。当前构建使用 Hash 文件名，例如：

```text
ae9c2aa042a2865d95492863f5710831.bundle
```

逻辑 Bundle 名、Hash 文件名和其中包含的资源由 Manifest 关联。

### 2.4 两层版本

项目中存在两层资源版本：

```text
ResourceAppVersion = v1.0
PackageVersion     = 2026-07-23-2316
```

- `ResourceAppVersion` 是项目自行管理的 CDN 发布目录。
- `PackageVersion` 是 YooAsset Manifest 的内部版本。

它们不是同一概念。

## 三、资源目录职责

### 3.1 Assets/Bundles

```text
Unity/Assets/Bundles
```

这是 ET 业务主要的 YooAsset 主资源目录：

```text
Assets/Bundles/
├─ AotDlls   HybridCLR AOT 补充元数据
├─ Code      热更新 DLL 和 PDB
├─ Config    游戏配置
├─ Scenes    游戏场景
├─ UI        ET 示例 UI
└─ Unit      单位 Prefab
```

这些目录都配置了 YooAsset Collector，可以进入 Manifest，并按地址主动加载。

注意区分：

```text
Unity/Assets/Bundles   原始资源输入
Unity/Bundles          YooAsset 构建输出
```

### 3.2 Assets/GameRes

```text
Unity/Assets/GameRes
```

主要存放 YIUI 和相关框架的业务资源：

```text
Assets/GameRes/
├─ Fonts
├─ I2Localization
├─ RedDot
└─ YIUI
```

当前 Collector 直接收集：

- `Assets/GameRes/YIUI`
- `Assets/GameRes/RedDot`
- `Assets/GameRes/I2Localization`

YIUI 使用自定义过滤规则：

- 收集 `Prefabs` 目录中的 Prefab。
- 不把 `.spriteatlas` 作为主资源收集。
- `Source` 目录中的 UI 编辑源 Prefab 通常不进入最终运行资源。
- 其他满足条件的资源正常收集。

`Fonts` 没有独立 Collector，但被 YIUI、TMP 或其他主资源引用时，可以作为依赖进入共享 Bundle。

### 3.3 Assets/Res

```text
Unity/Assets/Res
```

这是普通的原始美术和依赖资源目录，主要包含：

- FBX。
- 动画。
- 材质。
- 贴图。
- Animator Controller。
- 渲染配置。

该目录没有直接配置 YooAsset Collector，因此默认不是可寻址的主资源。

当 `Assets/Bundles` 或 `Assets/GameRes` 中的主 Prefab、场景引用这些素材时，YooAsset 会把需要的依赖放进主 Bundle或共享 Bundle。没有被任何主资源引用的文件不会因为位于 `Assets/Res` 就自动进入 YooAsset 产物。

### 3.4 Assets/Resources

```text
Unity/Assets/Resources
```

这是 Unity 原生特殊目录，当前包含：

```text
GlobalConfig.asset
YooAssetSettings.asset
BuildinFileManifest.asset
```

这些文件通过 Unity 原生接口加载：

```csharp
Resources.Load<GlobalConfig>("GlobalConfig");
```

它们不经过 YooAsset，原因是 YooAsset 初始化前或初始化过程中就必须读取这些配置。

特点：

- 随 Player 一起打包。
- 不依赖 YooAsset Manifest。
- 不适合大量业务资源。
- 不能通过当前 YooAsset 更新流程热更新。

### 3.5 Assets/StreamingAssets

```text
Unity/Assets/StreamingAssets/Bundles/DefaultPackage
```

用于存放随 Player 发布的内置 YooAsset Manifest 和 Bundle。

当前构建使用 `ClearAndCopyAll`，因此所有当前 Bundle 都会复制到这里。优点是首次启动不需要下载当前版本资源，缺点是 Player 包体更大。

## 四、当前资源收集配置

实际配置文件：

```text
Unity/Assets/Settings/AssetBundleCollectorSetting.asset
```

`AssetBundleCollectorConfig.xml` 与当前 `.asset` 内容存在历史差异，当前 YooAsset 2.1.1 运行时应以 `.asset` 为准。

### 4.1 当前 Collector

| 分组 | 收集目录 | Address Rule | Pack Rule | Filter Rule |
|---|---|---|---|---|
| AotDlls | `Assets/Bundles/AotDlls` | AddressByFileName | PackDirectory | CollectAll |
| Code | `Assets/Bundles/Code` | AddressByFileName | PackDirectory | CollectAll |
| Config | `Assets/Bundles/Config` | AddressByFileName | PackDirectory | CollectAll |
| UI | `Assets/Bundles/UI` | AddressByFileName | PackSeparately | CollectPrefab |
| Unit | `Assets/Bundles/Unit` | AddressByFileName | PackSeparately | CollectPrefab |
| Scene | `Assets/Bundles/Scenes` | AddressByFileName | PackSeparately | CollectScene |
| YIUI | `Assets/GameRes/YIUI` | AddressByFileName | PackDirectory | YIUIFilterRule |
| RedDot | `Assets/GameRes/RedDot` | AddressByFileName | PackDirectory | CollectAll |
| I2Localization | `Assets/GameRes/I2Localization` | AddressByFileName | PackDirectory | CollectAll |

### 4.2 Collector Type

#### MainAssetCollector

- 参与打包。
- 写入 Manifest 资源列表。
- 可以通过地址主动加载。
- 可以附加 Asset Tags。

#### StaticAssetCollector

- 参与打包。
- 不写入可加载资源列表。
- 不能通过地址主动加载。

#### DependAssetCollector

- 只作为依赖资源。
- 被 MainAsset 引用时才进入构建。
- 不能通过地址主动加载。

### 4.3 AddressByFileName

规则是去掉目录和最后一个扩展名：

```text
Assets/Bundles/AotDlls/mscorlib.dll.bytes
→ mscorlib.dll

Assets/Bundles/Code/Unity.Model.dll.bytes
→ Unity.Model.dll
```

当前 `LocationToLower = false`，地址区分大小写。

由于地址只使用文件名，不同目录中不要出现需要主动加载的同名资源，否则可能发生地址冲突。

### 4.4 PackDirectory

同一父目录中的文件打进同一个 Bundle。

例如 `Assets/Bundles/AotDlls` 中的全部 `.bytes` 会进入同一个逻辑 Bundle：

```text
defaultpackage_assets_bundles_aotdlls.bundle
```

最终发布时因为使用 HashName，会变成 Hash 文件名。

优点：

- Bundle 数量少。
- 请求次数少。
- 适合启动时必须成组加载的数据。

缺点：

- 修改其中一个文件会导致整个 Bundle Hash 改变。
- 客户端需要重新下载整个 Bundle。

### 4.5 PackSeparately

每个主资源独立生成一个逻辑 Bundle。

适合 UI、单位 Prefab、场景等需要独立更新和按需加载的内容。缺点是 Bundle 数量和请求次数会增加，共享依赖需要合理拆分。

### 4.6 Filter Rule

常用规则：

| 规则 | 含义 |
|---|---|
| CollectAll | 收集所有符合基础要求的资源 |
| CollectScene | 只收集 `.unity` 场景 |
| CollectPrefab | 只收集 `.prefab` |
| CollectSprite | 只收集 Sprite 类型贴图 |
| CollectShaderVariants | 只收集 ShaderVariantCollection |
| YIUIFilterRule | 当前项目的 YIUI 自定义规则 |

### 4.7 User Data

`User Data` 是传给自定义 AddressRule、PackRule 和 FilterRule 的参数。默认规则基本不使用它，当前留空是正常的。

### 4.8 Asset Tags

用于给资源添加下载标签，多个标签使用分号分隔：

```text
startup;aot;base
```

运行时可以按标签创建下载器：

```csharp
package.CreateResourceDownloader("startup", 8, 3);
```

当前所有 Collector 的 Asset Tags 都为空，因此目前不能按登录、战斗、语言等业务模块批量下载。

## 五、资源生成链路

### 5.1 ET 热更新程序集

执行：

```text
ET → Compile
```

或按 `F6`。

生成并复制到：

```text
Assets/Bundles/Code/
├─ Unity.Model.dll.bytes
├─ Unity.Model.pdb.bytes
├─ Unity.ModelView.dll.bytes
├─ Unity.ModelView.pdb.bytes
├─ Unity.Hotfix.dll.bytes
├─ Unity.Hotfix.pdb.bytes
├─ Unity.HotfixView.dll.bytes
└─ Unity.HotfixView.pdb.bytes
```

`.bytes` 使 Unity 把 DLL/PDB 当作二进制资源，由 YooAsset 收集，而不是当作普通托管插件直接加载。

### 5.2 HybridCLR 数据

IL2CPP 平台执行：

```text
HybridCLR → Generate → All
HybridCLR → CopyAotDlls
```

`CopyAotDlls` 根据：

```text
ProjectSettings/HybridCLRSettings.asset
```

中的 `patchAOTAssemblies`，把当前平台裁剪后的 AOT DLL 复制为：

```text
Assets/Bundles/AotDlls/<程序集名>.dll.bytes
```

### 5.3 完整顺序

```text
切换目标平台
→ ET Compile / F6
→ HybridCLR Generate All
→ HybridCLR CopyAotDlls
→ YooAsset Build
→ Build Player
```

不同平台的热更新 DLL、AOT 元数据、Shader、贴图和 Bundle 不能混用。

## 六、YooAsset 构建模式

| 模式 | 是否生成真实 Bundle | 是否复用缓存 | 是否可发布 | 用途 |
|---|---:|---:|---:|---|
| ForceRebuild | 是 | 否 | 是 | 正式发布、平台切换、排除缓存问题 |
| IncrementalBuild | 是 | 是 | 是 | 日常真实资源迭代 |
| DryRunBuild | 否 | 不适用 | 否 | 检查收集、分包和依赖 |
| SimulateBuild | 否 | 不适用 | 否 | Editor 模拟运行 |

### 6.1 ForceRebuild

- 删除当前目标平台和 Package 的历史构建根目录。
- 强制 Unity 重新生成全部 Bundle。
- 适合正式发布和关键配置变化。
- 本地旧版本目录可能被删除，发布历史需要单独归档。

### 6.2 IncrementalBuild

- 复用没有变化的 Bundle 构建缓存。
- 仍然生成完整的新版本发布目录。
- “增量”指构建过程复用缓存，不表示发布目录只包含差异文件。

### 6.3 DryRunBuild

- 分析收集、分包和依赖。
- 不生成可发布的真实 Bundle。
- 模拟 Hash、CRC 等数据不能用于运行时和 CDN。

### 6.4 SimulateBuild

- 不生成 AssetBundle。
- 生成 Editor 模拟 Manifest。
- 资源由 AssetDatabase 直接读取。
- Editor 成功不代表真机 Bundle、CDN 和缓存流程成功。

## 七、构建管线

### 7.1 BuiltinBuildPipeline

底层调用：

```csharp
BuildPipeline.BuildAssetBundles(...)
```

YooAsset 2.1.1 支持：

```text
ForceRebuild
IncrementalBuild
DryRunBuild
SimulateBuild
```

当前 Android 推荐流程和微信小游戏专用菜单都使用 BuiltinBuildPipeline。

### 7.2 ScriptableBuildPipeline

底层调用：

```csharp
ContentPipeline.BuildAssetBundles(...)
```

特点：

- 使用 Unity SBP 的任务和内容构建系统。
- 始终启用构建缓存。
- 支持缓存服务器。
- 支持生成 Link XML。
- 依赖、Shader 和 Bundle 内容布局可能与 Builtin 不同。

YooAsset 2.1.1 中只支持：

```text
IncrementalBuild
SimulateBuild
```

不支持 `ForceRebuild` 和 `DryRunBuild`。

旧文档 `Book/1.1运行指南.md` 使用的是：

```text
ScriptableBuildPipeline + IncrementalBuild
```

当前平台构建流程已经改为：

```text
BuiltinBuildPipeline + ForceRebuild
```

### 7.3 当前 Editor 与正式构建

当前 EditorSimulateMode 调用：

```csharp
EditorSimulateModeHelper.SimulateBuild(
    "ScriptableBuildPipeline",
    packageName);
```

这里只生成模拟 Manifest，不代表微信正式构建使用 SBP。

微信正式构建菜单直接实例化：

```csharp
BuiltinBuildParameters
BuiltinBuildPipeline
```

因此 YooAsset Builder 窗口里的 Pipeline 选择不会改变 `ET → WeChat → Build YooAsset Package` 的行为。

## 八、Android 与微信小游戏构建差异

| 项目 | Android | 微信小游戏 |
|---|---|---|
| BuildTarget | Android | MiniGame |
| 推荐 PlayMode | HostPlayMode | WebPlayMode |
| 资源输出 | `Bundles/Android/...` | `Bundles/MiniGame/...` |
| CDN 目录 | `CDN/Android/v1.0` | `CDN/MiniGame/v1.0` |
| 内置资源 | APK StreamingAssets | WebGL/小游戏 StreamingAssets |
| 缓存 | App 沙盒文件系统 | 微信/浏览器缓存体系 |
| 最终产物 | APK/AAB | 微信小游戏工程 |

两个平台即使原始资源相同，也可能产生不同的：

- 贴图格式。
- Shader 和变体。
- 序列化内容。
- Bundle Hash。
- AOT 裁剪程序集。
- IL2CPP 泛型和桥接数据。

因此 Android 和 MiniGame 的 YooAsset、HybridCLR 产物必须分别生成和发布。

### 8.1 微信小游戏专用菜单

菜单：

```text
ET → WeChat → Build YooAsset Package
```

固定参数：

```text
BuiltinBuildPipeline
ForceRebuild
ClearAndCopyAll
HashName
LZ4
EnableSharePackRule = true
VerifyBuildingResult = true
```

PackageVersion 使用时间生成，例如：

```text
2026-07-23-2316
```

菜单还会：

- 检查当前 BuildTarget 必须为 MiniGame。
- 构建 `DefaultPackage`。
- 复制内置资源。
- 生成 `BuildinFileManifest.asset`。
- 刷新 AssetDatabase。

Player 构建前还会预检：

- `CodeMode = Client`。
- `EPlayMode = WebPlayMode`。
- 首个场景是 `Assets/Scenes/Init.unity`。
- StreamingAssets 与 MiniGame 产物版本一致。
- 热更新 DLL/AOT DLL 存在且 Hash 一致。
- Release 使用 HTTPS/WSS。
- 微信 SDK CDN 配置有效。

### 8.2 微信的两套 CDN

微信小游戏存在两种 CDN 配置：

1. `MiniGameConfig.ProjectConf.CDN`
   - Unity 主包数据。
   - `.data`、`.wasm`、`.framework.js`、`.unityweb` 等转换产物。

2. `GlobalConfig.ResourceHostServer`
   - YooAsset Bundle 和 Manifest。
   - `CDN/MiniGame/v1.0`。

两者可以使用同一个域名，但目录和用途不同。

## 九、运行时配置

当前配置文件：

```text
Assets/Resources/GlobalConfig.asset
```

截至本文快照，主要值为：

| 字段 | 当前值 |
|---|---|
| CodeMode | Client |
| EnableDll | true |
| BuildType | Debug |
| AppType | Demo |
| EPlayMode | WebPlayMode |
| ResourceHostServer | `http://192.168.1.7:8080` |
| ResourceFallbackHostServer | 空 |
| ResourceAppVersion | `v1.0` |

CDN 地址规则：

```text
{ResourceHostServer}/CDN/{Platform}/{ResourceAppVersion}
```

例如：

```text
http://192.168.1.7:8080/CDN/MiniGame/v1.0
http://192.168.1.7:8080/CDN/Android/v1.0
http://192.168.1.7:8080/CDN/PC/v1.0
```

备用地址为空时，项目会再次使用主地址，因此当前实际上没有独立备用 CDN。

## 十、运行时初始化

游戏入口是：

```text
Assets/Scenes/Init.unity
```

启动顺序：

```text
创建 ET 基础单例
→ YooAssets.Initialize()
→ CreatePackage("DefaultPackage")
→ SetDefaultPackage
→ package.InitializeAsync(...)
→ 加载并激活 Manifest
→ 加载热更新 DLL 和 AOT 元数据
→ Assembly.Load
→ ET.Entry.Start
```

### 10.1 CreatePackage 不直接查找磁盘

```csharp
YooAssets.CreatePackage("DefaultPackage");
```

只会：

- 创建 `ResourcePackage`。
- 保存 `PackageName`。
- 注册到 YooAsset 全局 Package 列表。

真正使用名字查找目录和 Manifest 的是后续 `InitializeAsync()`。

### 10.2 Package 名如何定位文件

默认内置根目录：

```text
Application.streamingAssetsPath/Bundles
```

加上 PackageName：

```text
Application.streamingAssetsPath/Bundles/DefaultPackage
```

先读取：

```text
PackageManifest_DefaultPackage.version
```

假设内容是：

```text
2026-07-23-2316
```

再读取：

```text
PackageManifest_DefaultPackage_2026-07-23-2316.bytes
```

因此以下名称必须一致：

```text
Collector PackageName
= 构建参数 PackageName
= 输出目录名称
= Manifest 文件名中的 PackageName
= CreatePackage(packageName)
```

### 10.3 SetDefaultPackage

设置默认 Package 后，可以调用：

```csharp
YooAssets.LoadAssetAsync<T>(location);
```

否则需要显式使用：

```csharp
YooAssets.GetPackage("DefaultPackage")
    .LoadAssetAsync<T>(location);
```

### 10.4 PlayMode

#### EditorSimulateMode

- 生成模拟 Manifest。
- 使用 AssetDatabase 加载原始资源。
- 不生成或读取真实 Bundle。

#### OfflinePlayMode

- 只使用 StreamingAssets 内置资源。
- 不访问 CDN。

#### HostPlayMode

- 优先使用沙盒缓存 Manifest。
- 其次使用 StreamingAssets 内置 Manifest。
- 当前 Manifest 引用但本地缺失的 Bundle 可以从 CDN 下载。
- 适合 Android、Windows 和 iOS。

#### WebPlayMode

- 初始 Manifest 从 WebGL/小游戏 StreamingAssets 加载。
- 缺失 Bundle 可以通过远端服务加载。
- 缓存受 WebGL、浏览器和微信运行环境影响。
- 适合 WebGL 和微信小游戏。

## 十一、资源加载和生命周期

### 11.1 启动阶段 ResourcesComponent

`ResourcesComponent` 用于 ET 纤程系统建立前加载：

- 热更新 DLL。
- PDB。
- AOT 补充元数据。
- 启动配置。

它不是 Unity 原生 `Resources` 目录的封装，而是项目对 YooAsset 启动阶段加载的封装。

### 11.2 游戏阶段 ResourcesLoaderComponent

正常场景、Prefab 和业务资源应使用 `ResourcesLoaderComponent`：

- 相同 location 避免重复加载。
- 使用协程锁防止并发重复请求。
- 持有 YooAsset Handle。
- 组件销毁时统一释放资源。
- 资源生命周期跟随 ET Entity。

当前 `LoadSceneAsync(location, loadSceneMode)` 虽然接收 `loadSceneMode`，但调用 YooAsset 时没有传入该参数，实际使用 YooAsset 默认模式。需要多场景或 Additive 加载时应修正。

### 11.3 YIUI

`YIUILoadComponent` 把 YIUI 加载接口接入 YooAsset，支持：

- 同步加载。
- 异步加载。
- Handle 保存。
- 单个释放。
- 全部释放。
- Location 合法性检查。

## 十二、HybridCLR AOT 元数据

启动时 `CodeLoader.DownloadAsync()` 调用：

```csharp
ResourcesComponent.Instance
    .LoadAllAssetsAsync<TextAsset>("mscorlib.dll");
```

因为 `AotDlls` 使用 `PackDirectory`，全部 AOT `.bytes` 位于同一个 Bundle。`mscorlib.dll` 用于定位该 Bundle，`LoadAllAssetsAsync` 返回 Bundle 中的全部 TextAsset。

随后在 IL2CPP Player 中执行：

```csharp
RuntimeApi.LoadMetadataForAOTAssembly(
    textAsset.bytes,
    HomologousImageMode.SuperSet);
```

作用是给 HybridCLR 补充被裁剪或不完整的 AOT 元数据，支持热更新代码使用：

- 泛型类型和方法。
- 反射。
- 序列化。
- 异步状态机。
- 其他依赖完整类型信息的功能。

这不是用下载的 AOT DLL 替换 Player 中的原生代码，而是加载补充元数据。

当前实现存在两个注意点：

1. 代码依赖所有 AOT DLL 位于同一个 Bundle。如果把 AotDlls 改成 PackSeparately，需要同步修改加载方式。
2. 当前没有检查 `LoadMetadataForAOTAssembly` 返回值，建议记录每个程序集的加载结果。

## 十三、构建产物与 CDN

MiniGame 当前产物目录示例：

```text
Unity/Bundles/MiniGame/DefaultPackage/2026-07-23-2316
```

发布到：

```text
<CDN根目录>/CDN/MiniGame/v1.0
```

应该复制版本目录中的内容，而不是把版本目录本身再嵌套一层。

最终结构：

```text
CDN/MiniGame/v1.0/
├─ PackageManifest_DefaultPackage.version
├─ PackageManifest_DefaultPackage_2026-07-23-2316.bytes
├─ PackageManifest_DefaultPackage_2026-07-23-2316.hash
├─ PackageManifest_DefaultPackage_2026-07-23-2316.json
└─ <hash>.bundle
```

### 13.1 本地服务器目录映射

如果使用：

```powershell
python -m http.server 8080 --directory .\Unity\WXBuild\webgl
```

则：

```text
http://192.168.1.7:8080/CDN/MiniGame/v1.0
```

对应：

```text
E:\github\ET\YIUI\Unity\WXBuild\webgl\CDN\MiniGame\v1.0
```

本文整理时仓库实际只有：

```text
Unity/WXBuild/webgl/CDN/PC/v1.0
```

没有 `CDN/MiniGame/v1.0`，本地 MiniGame 联调前需要将 MiniGame 产物发布到正确目录。

### 13.2 推荐发布顺序

为避免客户端读取到半发布状态：

1. 上传新的 Hash Bundle。
2. 上传新版本 `.bytes`、`.hash`、`.json` Manifest。
3. 最后覆盖 `PackageManifest_DefaultPackage.version`。
4. 验证所有文件返回 HTTP 200。
5. 再开放客户端更新。

缓存建议：

- Hash Bundle：长时间强缓存。
- `.version`：不缓存或极短缓存。
- Manifest：短缓存。
- 回滚时保留旧 Bundle 和 Manifest，只切换 `.version` 指向。

## 十四、完整热更新流程

当前项目初始化 Package 后直接加载热更新 DLL，没有执行以下 API：

```csharp
UpdatePackageVersionAsync
UpdatePackageManifestAsync
CreateResourceDownloader
BeginDownload
```

因此完整启动流程尚未闭环。

推荐顺序：

```text
初始化 DefaultPackage
→ 查询远端 PackageVersion
→ 下载并激活新 Manifest
→ 计算需要下载的 Bundle
→ 显示文件数和总大小
→ 用户确认或按策略自动下载
→ 下载完成
→ 加载最新 AOT 元数据和热更新 DLL
→ Assembly.Load
→ 启动游戏
```

参考代码：

```csharp
ResourcePackage package = YooAssets.GetPackage("DefaultPackage");

UpdatePackageVersionOperation versionOperation =
    package.UpdatePackageVersionAsync();
await versionOperation.Task;

if (versionOperation.Status != EOperationStatus.Succeed)
    throw new Exception(versionOperation.Error);

UpdatePackageManifestOperation manifestOperation =
    package.UpdatePackageManifestAsync(versionOperation.PackageVersion);
await manifestOperation.Task;

if (manifestOperation.Status != EOperationStatus.Succeed)
    throw new Exception(manifestOperation.Error);

ResourceDownloaderOperation downloader =
    package.CreateResourceDownloader(
        downloadingMaxNumber: 8,
        failedTryAgain: 3);

if (downloader.TotalDownloadCount > 0)
{
    downloader.OnDownloadProgressCallback =
        (totalCount, currentCount, totalBytes, currentBytes) =>
        {
            // 更新下载界面。
        };

    downloader.OnDownloadErrorCallback =
        (fileName, error) =>
        {
            // 记录错误并提示重试。
        };

    downloader.BeginDownload();
    await downloader.Task;

    if (downloader.Status != EOperationStatus.Succeed)
        throw new Exception(downloader.Error);
}
```

这段流程必须发生在：

```csharp
CodeLoader.DownloadAsync()
```

之前，否则本次进程已经加载了内置旧 DLL，即使稍后下载新 DLL 也无法替换已经加载的程序集。

## 十五、推荐操作流程

### 15.1 Editor 日常运行

```text
GlobalConfig.EPlayMode = EditorSimulateMode
ET → Compile
打开 Init 场景
Play
```

### 15.2 Android 完整构建

```text
切换 BuildTarget = Android
GlobalConfig.CodeMode = Client
GlobalConfig.EPlayMode = HostPlayMode
确保 BuildType 与 BuildOptions 匹配

ET → Compile
HybridCLR → Generate → All
HybridCLR → CopyAotDlls
YooAsset：BuiltinBuildPipeline + ForceRebuild + ClearAndCopyAll
ET → Build Tool → BuildPackage
```

匹配关系：

```text
BuildType.Release ↔ BuildOptions.None
BuildType.Debug   ↔ BuildOptions.Development
```

### 15.3 微信小游戏完整构建

```text
切换 BuildTarget = MiniGame
GlobalConfig.CodeMode = Client
GlobalConfig.EPlayMode = WebPlayMode

ET → Compile
HybridCLR → Generate → All
HybridCLR → CopyAotDlls
ET → WeChat → Build YooAsset Package
发布产物到 CDN/MiniGame/v1.0
ET → WeChat → Validate Build
构建并转换微信小游戏
```

### 15.4 纯资源更新

完整热更新流程补齐后：

```text
修改资源或代码
→ F6
→ 必要时 Generate All + CopyAotDlls
→ 构建新 PackageVersion
→ 上传 Bundle
→ 上传版本 Manifest
→ 最后更新 .version
→ 客户端查询版本
→ 更新 Manifest
→ 下载差异 Bundle
→ 加载最新 DLL
→ 进入游戏
```

## 十六、当前风险和改进项

### 高优先级

1. 补齐远端版本检查、Manifest 更新和资源下载流程。
2. 更新必须发生在 AOT 元数据和热更新 DLL 加载之前。
3. 增加下载进度、失败重试、网络错误和磁盘空间处理。
4. 检查 `LoadMetadataForAOTAssembly` 返回值。

### 中优先级

1. 为 `startup`、`login`、`battle`、`language` 等资源规划 Asset Tags。
2. 配置真正独立的备用 CDN。
3. 根据首包大小调整 `ClearAndCopyAll`，只内置启动必需资源。
4. 修复 `ResourcesLoaderComponent.LoadSceneAsync` 未传递 `LoadSceneMode` 的问题。
5. 为资源 Handle 增加更明确的加载失败状态和错误日志。

### 文档和构建一致性

1. 旧 `1.1运行指南.md` 的 `ScriptableBuildPipeline + IncrementalBuild` 与当前平台流程不一致。
2. Android 和 MiniGame 应分别保留明确、唯一的推荐构建参数。
3. 构建产物和 CDN 发布应加入版本归档及回滚策略。
4. 不要混用其他平台生成的 HybridCLR、YooAsset 或 StreamingAssets 产物。

## 十七、关键源码索引

| 功能 | 文件 |
|---|---|
| YooAsset 包版本 | `Unity/Packages/com.tuyoogame.yooasset/package.json` |
| Package 创建和初始化 | `Unity/Assets/Scripts/Loader/Resource/ResourcesComponent.cs` |
| 游戏启动入口 | `Unity/Assets/Scripts/Loader/MonoBehaviour/Init.cs` |
| 全局运行配置 | `Unity/Assets/Resources/GlobalConfig.asset` |
| 全局配置定义 | `Unity/Assets/Scripts/Loader/MonoBehaviour/GlobalConfig.cs` |
| YooAsset 基础设置 | `Unity/Assets/Resources/YooAssetSettings.asset` |
| Collector 配置 | `Unity/Assets/Settings/AssetBundleCollectorSetting.asset` |
| YIUI 过滤规则 | `Unity/Assets/Editor/YIUIYooAsset/YIUIYooAssetExtension.cs` |
| 启动 DLL/AOT 加载 | `Unity/Assets/Scripts/Loader/CodeLoader.cs` |
| 游戏资源加载组件 | `Unity/Assets/Scripts/ModelView/Client/Module/Resource/ResourcesLoaderComponent.cs` |
| YIUI YooAsset 接入 | `Unity/Assets/Plugins/YIUIFramework/ET/Component/Load/YIUILoadComponent.cs` |
| 内置文件查询 | `Unity/Assets/Scripts/Loader/Resource/StreamingAssetsHelper/StreamingAssetsHelper.cs` |
| 微信构建工具 | `Unity/Assets/Scripts/Editor/BuildEditor/WeChatBuildTools.cs` |
| HybridCLR AOT 配置 | `Unity/ProjectSettings/HybridCLRSettings.asset` |
| AOT DLL 复制工具 | `Unity/Assets/Scripts/Editor/Plugins/HybridCLR/HybridCLREditor.cs` |
| Android 构建文档 | `Book/Android_APK打包指南.md` |
| 微信小游戏文档 | `Book/微信小游戏适配与构建指南.md` |

## 十八、总结

当前项目的 YooAsset 已经覆盖资源收集、构建、内置、CDN 地址、资源加载和 HybridCLR 程序集加载，但资源热更新链路仍缺少最关键的运行时版本更新阶段。

最核心的理解是：

```text
Collector 决定哪些资源进入 Package
PackRule 决定资源如何组成 Bundle
BuildPipeline 负责生成 Bundle
Manifest 记录地址、Bundle、依赖和版本
ResourcePackage 管理 Manifest、缓存、下载和加载
ResourcesLoaderComponent 管理业务资源生命周期
CodeLoader 在最新资源准备完成后加载 AOT 元数据和热更新 DLL
```

后续优先补齐：

```text
UpdatePackageVersion
→ UpdatePackageManifest
→ CreateResourceDownloader
→ Download
→ Load AOT/DLL
→ Start Game
```

完成这一步后，当前体系才真正形成从资源制作、构建、发布到客户端更新的完整闭环。
