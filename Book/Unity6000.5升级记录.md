# Unity 6000.5 升级记录

## 1. 升级概况

| 项目 | 升级前 | 升级后 |
| --- | --- | --- |
| Unity Editor | 2022.3.17f1 | 6000.5.3f1（Unity 6.5） |
| Universal Render Pipeline | 14.0.10 | 17.5.0 |
| AI Navigation | 1.1.5 | 2.0.13 |
| UGUI | 1.0.0 | 2.5.0 |
| Timeline | 1.7.6 | 1.8.12 |
| HybridCLR | 4.0.15（未锁定 Git 分支） | 8.12.0（项目内嵌） |
| MemoryPack | 1.10.0（Git 依赖） | 1.10.0（项目内嵌） |
| YooAsset | 2.1.1（OpenUPM） | 2.1.1（项目内嵌） |

升级实施时间：2026-07-11 至 2026-07-12。

## 2. Package 本地化

升级时 Package Manager 无法稳定连接以下地址：

- `packages.unity.com`
- `package.openupm.com`
- GitHub

主要错误为 `ECONNRESET`、`ETIMEDOUT` 和 Git `RPC failed`。原 Unity 2022 项目看似可以正常使用，是因为依赖已经存在于 `Library/PackageCache`；Unity 大版本升级和重建 `Library` 后，需要重新解析和下载依赖，网络问题才暴露出来。

为保证项目依赖可复现，将以下包完整放入 `Unity/Packages`：

```text
Unity/Packages/com.code-philosophy.hybridclr
Unity/Packages/com.cysharp.memorypack
Unity/Packages/com.tuyoogame.yooasset
```

项目内嵌包版本由各目录的 `package.json` 和 `Packages/manifest.json` 固定，不再依赖 GitHub、Gitee 或 OpenUPM 才能打开项目。

## 3. Package 与编译兼容修改

### 3.1 Mathematics 迁移

Unity 6000.5 将原 `Unity.Mathematics` 程序集内容迁移到 `UnityEngine.MathematicsModule`。项目需要显式启用：

```json
"com.unity.mathematics": "1.4.0"
```

`Unity.Model` 和 `Unity.Hotfix` 原先设置了：

```json
"noEngineReferences": true
```

这会导致程序集无法引用新的 Mathematics Engine Module，因此 Unity 客户端的这两个 asmdef 已改为允许 Engine 引用。该修改只作用于 Unity asmdef，不改变 DotNet 服务端工程的源码组织。

### 3.2 F6 热更新程序集编译

Unity 6000.5 拆分了部分托管程序集。项目使用 `PlayerBuildInterface.CompilePlayerScripts` 执行 `ET/Compile (F6)` 时，需要额外引用：

- `Unity.Scripting.dll`
- `UnityEngine.dll`

安装目录含空格，因此传给 C# 编译器的引用路径必须使用引号。

Collections 6.5.0 在该自定义编译流程中还需要定义：

```text
ENABLE_UNITY_COLLECTIONS_CHECKS
```

上述兼容逻辑已加入 `Assets/Scripts/Editor/Assembly/AssemblyTool.cs`。修改热更新代码后仍然必须执行 F6，否则编辑器在 `EnableDll` 模式下会继续加载 `Assets/Bundles/Code` 中的旧 DLL。

## 4. Unity 6000.5 API 迁移

### 4.1 Object 实例标识

UIEffect 中用于材质 Hash 的：

```csharp
GetInstanceID()
```

已替换为：

```csharp
GetEntityId().GetHashCode()
```

### 4.2 AssetDatabase

以下接口已废弃并被 Unity 6000.5 视为编译错误：

```csharp
AssetDatabase.GetAssetPath(int instanceID)
EditorUtility.InstanceIDToObject(int instanceID)
```

日志跳转工具现在将 `instanceID` 转为 `UnityEngine.EntityId`，再调用新的 `GetAssetPath(EntityId)`。

### 4.3 I2 Localization 层级遍历

I2 Localization 使用的 `HierarchyProperty` 已废弃。场景根节点遍历已改为：

```csharp
SceneManager.GetSceneAt(sceneIndex).GetRootGameObjects()
```

### 4.4 YooAsset SceneHandle 冲突

Unity 6000.5 新增了 `UnityEngine.SceneManagement.SceneHandle`，与 `YooAsset.SceneHandle` 同名。资源释放代码已显式使用：

```csharp
YooAsset.SceneHandle
```

### 4.5 Vector3 与 float3 转换

Unity 6000.5 不再提供旧版 `Vector3` 与 `float3` 的隐式转换运算符。旧热更新 DLL 会在运行时持续抛出：

```text
MissingMethodException:
Unity.Mathematics.float3.op_Implicit(UnityEngine.Vector3)
```

以下场景已改成显式按 `x/y/z` 构造：

- 鼠标点击寻路坐标
- 单位 GameObject 位置同步
- 单位视图初始化
- 编辑器 `float3` 属性绘制

修改后重新执行 F6，确保新的 Hotfix/HotfixView DLL 被复制到 `Assets/Bundles/Code`。

### 4.6 DontDestroyOnLoad

Unity 6000.5 要求传入根 GameObject。以下对象原先可能是子节点：

- Unity Logs Viewer 的 `Reporter`
- YIUI 的 `UIRoot`

调用目标已统一改成：

```csharp
transform.root.gameObject
```

## 5. 升级验证

已在 Unity 6000.5.3f1 中完成以下验证：

1. Package Manager 成功识别 HybridCLR、MemoryPack、YooAsset 为项目内嵌包。
2. Unity 脚本编译成功，无 C# 编译错误。
3. `ET/Compile (F6)` 执行成功，日志输出 `Compile Finish!`。
4. 从 `Assets/Scenes/Init` 进入 Play Mode。
5. 依次执行启动界面的入口按钮。
6. 成功完成本地 ClientServer 登录并进入 `Map1`。

关键网络响应：

```text
R2C_Login       Error: 0
G2C_LoginGate   Error: 0
G2C_EnterMap    Error: 0
```

以最后一次 `Compile Finish!` 为日志边界，未再出现：

- C# 编译错误
- Mathematics `MissingMethodException`
- 业务运行时异常
- `DontDestroyOnLoad` 层级警告

## 6. 已知遗留问题

### 6.1 Odin Inspector

当前 Odin Inspector 预编译 DLL 使用了 Unity 已删除的内部 UI Toolkit 字段：

```text
UnityEngine.UIElements.UIElementsUtility.s_BeginContainerCallback
```

因此每次程序集重载可能产生以下编辑器异常：

```text
MissingFieldException
Exception while executing InitializeOnLoad for GUITimeHelper.Init
Exception while executing InitializeOnLoad for ImguiElementUtils.Init
```

这是旧版 Odin 与 Unity 6000.5 的二进制兼容问题，不是项目业务代码错误。Unity API Updater 无法完整修复预编译 DLL。

当前项目已对 `Sirenix.Reflection.Editor.dll` 应用临时兼容补丁，禁用了旧版 Odin 对 UI Toolkit Begin/End Container 内部回调的注册，以避免编辑器启动和程序集重载时重复弹出上述异常。补丁可通过以下脚本重新应用：

```powershell
.\Tools\Patch-Odin-Unity6000.5.ps1
```

该补丁只用于过渡，不代表当前 Odin 已正式支持 Unity 6000.5。后续必须使用有授权且明确支持 Unity 6000.5 的新版 Odin Inspector，整体替换 `Assets/Plugins/Sirenix`；升级完成并验证无误后，应移除二进制兼容补丁及其脚本。升级时需要重点回归 Odin Inspector、自定义 Odin 编辑器窗口、序列化数据读写和 UI Toolkit Inspector 显示。

### 6.2 Unity Services 网络错误

编辑器偶尔出现：

```text
UnityConnectWebRequestException: Token Exchange failed
```

该错误来自 Unity Services/账号网络连接，不影响本地 ClientServer 场景运行。

## 7. 后续操作注意事项

1. 修改 Model、ModelView、Hotfix 或 HotfixView 后，进入 Play Mode 前先执行 `ET/Compile (F6)`。
2. 更新内嵌 Package 时必须整体替换对应目录，并同步修改 `package.json`、`manifest.json` 和升级记录。
3. HybridCLR 8.12.0 升级后，正式 IL2CPP 出包前应重新执行 Installer、桥接函数生成、AOT 泛型引用和补充元数据生成。
4. 正式发布前仍需分别验证 Windows、Android 等实际目标平台的 IL2CPP 构建和真机运行。
5. 不要直接修改 `Library/PackageCache`；该目录是可重建缓存。
