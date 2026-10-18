# ET/YIUI Android APK 打包指南

本文记录 ET/YIUI 项目在 Windows 环境下打出 Android APK 的完整流程，并汇总实际遇到的问题。本文以本项目已经成功出包的配置为准。

## 1. 已验证的环境

| 项目 | 已验证版本或路径 |
| --- | --- |
| Unity | 6000.3.19f1（Unity 6.3 LTS） |
| HybridCLR | 8.12.0 |
| YooAsset | 2.1.1 |
| JDK | JDK 17 64 位，使用 Android Studio 自带 JBR |
| Android SDK | `C:\Users\song\AppData\Local\Android\Sdk` |
| Android Platform | Android 35 |
| Android Build Tools | 34.0.0、36.0.0 |
| Android NDK | 27.2.12479018 |
| Android 架构 | ARM64 |

当前已验证的 APK 输出位置：

```text
E:\github\ET\YIUI\Release\ET.apk
```

`BuildHelper.cs` 固定使用 `Assets/Scenes/Init.unity` 作为入口场景，并将 Android 包输出为项目根目录下的 `Release/ET.apk`。

> 当前构建使用的是调试签名，只适合开发测试。正式上架前还需要配置正式包名、版本号和签名证书。

## 2. 首次配置 Android 环境

### 2.1 安装 Unity Android 模块

Unity Editor 必须安装 `Android Build Support`。如果 Unity Hub 能正常管理该 Editor，优先通过 Unity Hub 的“安装/添加模块”完成。

SDK、NDK 和 JDK 既可以使用 Unity Hub 安装的版本，也可以使用 Android Studio 安装的外部版本。本项目已验证外部工具链可以正常打包。

### 2.2 使用 Android Studio 安装 SDK 和 NDK

在 Android Studio 的 `SDK Manager` 中至少安装：

- Android SDK Platform 35
- Android SDK Platform-Tools
- Android SDK Command-line Tools（最新版本）
- Android SDK Build-Tools 36.0.0
- NDK (Side by side) 27.2.12479018

安装后检查目录：

```text
C:\Users\song\AppData\Local\Android\Sdk
├─ build-tools\36.0.0
├─ cmdline-tools\latest
├─ platform-tools
├─ platforms\android-35
└─ ndk\27.2.12479018
```

### 2.3 在 Unity 中指定外部工具

打开：

```text
Edit → Preferences → External Tools → Android
```

取消相应的 `Installed with Unity (recommended)`，然后指定：

```text
JDK: D:\Program Files\AndroStudio\jbr
SDK: C:\Users\song\AppData\Local\Android\Sdk
NDK: C:\Users\song\AppData\Local\Android\Sdk\ndk\27.2.12479018
```

Gradle 继续使用 Unity 自带版本：

```text
Gradle Installed with Unity (recommended): 勾选
```

注意：

- JDK 必须选择 JDK 根目录，目录下应存在 `bin\java.exe`，不能只选择 JRE 子目录。
- Unity 提示外部 SDK/NDK 不是推荐版本，并不等于无法打包；只要版本兼容即可。
- 如果提示 Command-line Tools 版本偏旧，优先通过 Android Studio 更新。临时选择 `Use Highest Installed` 也可以继续验证构建。

## 3. 项目打包前设置

### 3.1 切换 Android 平台

打开：

```text
File → Build Profiles → Android → Switch Platform
```

等待右下角资源导入和脚本编译全部结束，再执行后续步骤。

### 3.2 确认 APK 构建模式

在 Android Build Profile 中确认：

```text
Export Project: 关闭
Build App Bundle (Google Play): 关闭
```

- `Export Project` 开启时，Unity 会导出 Gradle 工程，而不是直接生成 APK。
- `Build App Bundle` 开启时会生成 AAB，而不是 APK。

如果以前开启过 `Export Project`，`Release/ET.apk` 可能是一个同名目录。关闭选项后，先删除该目录，再重新打包。

### 3.3 确认 ET 全局配置

检查：

```text
Assets/Resources/GlobalConfig.asset
```

当前成功配置为：

| 字段 | 值 | 说明 |
| --- | --- | --- |
| CodeMode | Client | `BuildPackage` 要求必须是客户端模式 |
| EnableDll | 开启 | 使用编译后的热更新 DLL |
| BuildType | Release | 与下文 `BuildOptions: None` 保持一致；如果改为 Debug，最终构建必须选择 Development |
| AppType | Demo | 当前项目运行类型 |
| EPlayMode | HostPlayMode | 不能使用 EditorSimulateMode 打 Player 包 |

`ET → Build Tool` 会检查 `CodeMode` 和 `EPlayMode`，配置不正确会直接停止构建。

`BuildType` 还会影响异步状态机等编译产物的结构，必须与最终 Player 的 `BuildOptions` 保持一致：

```text
GlobalConfig.BuildType = Release  ↔ BuildOptions = None
GlobalConfig.BuildType = Debug    ↔ BuildOptions 包含 Development
```

不要混用 `Debug + None` 或 `Release + Development`。HybridCLR 热更新 DLL 与 AOT DLL 的编译模式不一致时，可能在真机上出现 `MissingMethodException`。

### 3.4 检查 Android Player 设置

打开：

```text
Edit → Project Settings → Player → Android
```

当前项目主要配置：

```text
Product Name: ET
Package Name: com.test.ET
Version: 1.0
Bundle Version Code: 1
Minimum API Level: 26
Target API Level: Automatic / 已安装最高兼容版本
Target Architecture: ARM64
```

`com.test.ET` 仅适合测试。正式发布前应改为自己的唯一包名，例如 `com.company.game`。

## 4. 首次或全量打包流程

平台切换到 Android 后，严格按以下顺序执行。

### 步骤 1：安装或刷新 HybridCLR

打开：

```text
HybridCLR → Installer
```

确认安装状态正常；首次配置、切换 Unity 大版本或重建 `HybridCLRData` 后，需要重新执行安装。

### 步骤 2：编译 ET 热更新程序集

执行：

```text
ET → Compile
```

快捷键为 `F6`。Console 出现 `Compile Finish!` 后，确认以下目录包含最新文件：

```text
Unity/BuildArtifacts/HotUpdateDlls/
├─ Unity.Model.dll
├─ Unity.ModelView.dll
├─ Unity.Hotfix.dll
└─ Unity.HotfixView.dll

Unity/Assets/Bundles/Code/
├─ Unity.Model.dll.bytes
├─ Unity.ModelView.dll.bytes
├─ Unity.Hotfix.dll.bytes
├─ Unity.HotfixView.dll.bytes
└─ 对应的四个 .pdb.bytes 文件
```

### 步骤 3：生成 HybridCLR 数据

执行：

```text
HybridCLR → Generate → All
```

这一步必须在 `ET → Compile` 成功之后执行，否则可能找不到 `Unity.Model` 等热更新程序集。

如果弹出 Android SDK Command-line Tools 版本提示：

- 推荐选择 `Update Android SDK` 并完成更新；
- 临时验证可以选择 `Use Highest Installed`。

### 步骤 4：复制补充元数据 DLL

执行：

```text
HybridCLR → CopyAotDlls
```

脚本会从当前 Android 目标的裁剪程序集目录复制 HybridCLR 所需 AOT DLL 到：

```text
Unity/Assets/Bundles/AotDlls
```

### 步骤 5：构建 YooAsset 资源包

打开 YooAsset 的 AssetBundle Builder，确认：

```text
Package: DefaultPackage
Build Target: Android
Build Pipeline: BuiltinBuildPipeline
Build Mode: ForceRebuild
Copy Buildin File Option: Clear And Copy All
```

然后执行资源构建。构建完成后确认 Console 没有错误，并等待 Unity 完成资源刷新。

### 步骤 6：生成 APK

打开：

```text
ET → Build Tool
```

选择：

```text
PlatformType: Android
BuildOptions: None（要求 GlobalConfig.BuildType 为 Release）
```

如果确实需要开发调试包，则使用以下配对：

```text
GlobalConfig.BuildType: Debug
BuildOptions: Development
```

点击：

```text
BuildPackage
```

成功后 Unity 会打开 `Release` 目录，最终文件为：

```text
E:\github\ET\YIUI\Release\ET.apk
```

## 5. 后续日常打包

为了避免遗漏，发布测试包时可以继续执行完整流程：

```text
ET → Compile
HybridCLR → Generate → All
HybridCLR → CopyAotDlls
YooAsset Build
ET → Build Tool → BuildPackage
```

以下步骤通常不必每次执行：

- `HybridCLR → Installer`：仅首次安装、切换 Unity/HybridCLR 版本或 HybridCLR 本地数据损坏时执行。
- 仅 Gradle 下载超时后重试：直接重新执行 `BuildPackage`，无需重新生成 HybridCLR 和 YooAsset。

## 6. 常见错误与解决办法

### 6.1 Unity 6000.5 编译 HybridCLR C++ 失败

典型错误：

```text
MonoPosixHelper.cpp: '../external/zlib/zlib.h' file not found
unknown type name 'Il2CppSharedGenericObject'
```

原因是当前 HybridCLR 8.12.0 与 Unity 6000.5 的 IL2CPP 内部接口不完整兼容。单独修改 `MonoPosixHelper.cpp` 的 zlib 路径只能越过第一个错误，不能解决后续代码生成接口差异。

处理方法：

1. 使用 Unity 6000.3.19f1 打开项目；
2. 按 Unity 6000.3 的内置包版本调整 `Packages/manifest.json`；
3. 重新执行 `HybridCLR → Installer`；
4. 重新执行完整打包流程。

不要长期保留对 `HybridCLRData/LocalIl2CppData-WindowsEditor` 中生成文件的手工修改；该目录可能在 Installer 执行时被重新生成。

### 6.2 降级 Unity 后 Package Manager 提示包不存在

Unity 6000.3 已验证的关键包版本：

```json
"com.unity.mathematics": "1.3.3",
"com.unity.render-pipelines.universal": "17.3.0",
"com.unity.ugui": "2.0.0"
```

Unity 6000.5 中的 `com.unity.modules.physicscore2d` 不应保留在 Unity 6000.3 的 manifest 中。

如果提示连接 `packages.unity.com` 失败或 `ECONNRESET`，属于网络、代理或防火墙问题，不是包版本语法错误。

### 6.3 Generate All 找不到 Unity.Model

典型错误：

```text
resolve Hot update dll: Unity.Model failed
```

处理方法：

1. 确认当前平台已经切换为 Android；
2. 先执行 `ET → Compile`；
3. 确认 `Assets/Bundles/Code/Unity.Model.dll.bytes` 等文件已刷新；
4. 再执行 `HybridCLR → Generate → All`。

### 6.4 BuildPackage 输出的是 Android 工程

原因是 Android Build Profile 中开启了 `Export Project`。

关闭：

```text
Export Project
```

删除原来的 `Release/ET.apk` 同名目录后重新执行 `BuildPackage`。

### 6.5 Kotlin Duplicate class

典型错误：

```text
Execution failed for task ':launcher:checkReleaseDuplicateClasses'
kotlin-stdlib:1.8.22
kotlin-stdlib-jdk7/jdk8:1.6.21
```

本项目中的冲突由未使用的 `com.unity.purchasing` 及其 Android Billing 传递依赖引起，当前解决方式是从 `Packages/manifest.json` 移除 Unity Purchasing。

如果以后确实需要应用内购，应升级到依赖兼容的 Purchasing 版本，或通过自定义 Gradle 模板统一 Kotlin 依赖版本，不能简单忽略重复类错误。

### 6.6 detachedConfiguration1 / Read timed out

典型错误：

```text
Could not resolve all files for configuration ':unityLibrary:detachedConfiguration1'
Could not download intellij-core-32.0.0.jar
Could not download kotlin-compiler-32.0.0.jar
Could not download uast-32.0.0.jar
Read timed out
```

这是 Gradle 访问 Google Maven 下载构建工具时超时。网络恢复后直接重新执行 `BuildPackage` 即可，已经成功下载的文件会保留在 Gradle 缓存中。

如果经常超时，可以在以下文件中增加超时时间：

```text
C:\Users\song\.gradle\gradle.properties
```

```properties
systemProp.org.gradle.internal.http.connectionTimeout=600000
systemProp.org.gradle.internal.http.socketTimeout=600000
```

不要因为下载超时删除整个 `.gradle` 缓存，否则下次需要重新下载所有依赖。

### 6.7 BuildinFileManifest 的 meta 警告

如果提示 `.meta exists but its asset can't be found`，表示资源文件与 `.meta` 不匹配。它不是上述 Gradle 失败的原因，但应检查 `Assets/Resources/BuildinFileManifest.asset` 是否存在，并删除无主的旧 `.meta` 或重新生成资源清单。

### 6.8 真机启动时报 PublishAsync 状态机缺少构造函数

典型错误：

```text
init fiber fail: Main System.MissingMethodException:
MethodNotFind ET.EventSystem+<PublishAsync>d__4`2[ET.Scene,ET.EntryEvent1]::.ctor
```

这里缺少的不是 `EntryEvent1` 的构造函数，而是编译器为 `EventSystem.PublishAsync<Scene, EntryEvent1>` 生成的异步状态机类型的构造函数。

本项目中该问题的根因是热更新 DLL 与 AOT DLL 使用了不同的编译模式：

- Debug/Development 编译会把该异步状态机生成成 `class`，其中包含 `.ctor`；
- Release 编译会把该异步状态机生成成 `struct`，正常情况下没有 `.ctor`；
- 如果热更新 DLL 按 Debug 编译、APK 按 Release 编译，运行时会拿 Debug 代码请求 Release AOT 元数据中不存在的 `.ctor`，最终抛出 `MissingMethodException`。

`Assets/Bundles/AotDlls/Unity.Core.dll.bytes` 的生成链路如下：

```text
HybridCLR → Generate → All
  → 生成裁剪后的 HybridCLRData/AssembliesPostIl2CppStrip/Android/Unity.Core.dll
HybridCLR → CopyAotDlls
  → 复制为 Assets/Bundles/AotDlls/Unity.Core.dll.bytes
YooAsset Build
  → 将 .bytes 文件打入资源包
```

`.bytes` 后缀只是为了让 Unity 将 DLL 当作资源文件处理。Release 版本中没有上述 `.ctor` 是正常结果，不应手工修改或补构造函数；应统一整个构建链路的模式。

推荐按 Release 模式重新完整构建：

```text
1. GlobalConfig.BuildType = Release
2. ET → Compile（F6），等待 Compile Finish!
3. HybridCLR → Generate → All
4. HybridCLR → CopyAotDlls
5. YooAsset：BuiltinBuildPipeline + ForceRebuild + Clear And Copy All
6. ET → Build Tool：Android + BuildOptions None
7. 卸载手机旧 APK 或清除应用数据，再安装新 APK
```

如果要构建调试包，则必须同时使用：

```text
GlobalConfig.BuildType = Debug
BuildOptions = Development
```

如果 `BuildPackage` 另外报错：

```text
BuildFailedException: hot update assembly:Unity.Model doesn't exist
```

表示 `Unity/BuildArtifacts/HotUpdateDlls/Unity.Model.dll` 等热更新编译输出不存在。执行 `ET → Compile`，确认 Console 出现 `Compile Finish!` 且该目录中的四个热更新 DLL 均已生成，再继续 `Generate → All`。

## 7. 查看完整构建日志

Unity Console 经常只显示 `Gradle build failed` 或 `Could not resolve all files` 的摘要。Windows 上的完整日志位于：

```text
C:\Users\song\AppData\Local\Unity\Editor\Editor.log
```

上一次 Unity 会话的日志：

```text
C:\Users\song\AppData\Local\Unity\Editor\Editor-prev.log
```

也可以通过 Unity 菜单打开：

```text
Help → Open Editor Log
```

排查 Android 构建错误时，优先搜索：

```text
FAILURE: Build failed
What went wrong
Execution failed
Could not resolve
Could not download
Read timed out
Duplicate class
Caused by
```

## 8. 出包后的检查

开发测试包至少检查：

1. `Release/ET.apk` 是文件而不是目录；
2. APK 可以正常安装到 ARM64 Android 设备；
3. 首次启动能够进入 `Assets/Scenes/Init.unity`；
4. YooAsset 内置资源可以正常加载；
5. HybridCLR 热更新 DLL 和 AOT 补充元数据加载正常；
6. 联网模式下资源服务器地址和游戏服务器地址正确；
7. 覆盖安装前确认包名与签名和旧版本一致。

正式发布前还需要单独完成：正式包名、正式签名、版本号递增、Release 配置、隐私权限、ABI 策略、AAB 构建及商店合规检查。
