# RedBlue Demo 实施与验证

来源：[团结引擎红蓝群体对抗微信小游戏技术方案与实施交接](https://chatgpt.com/space/page_eb1f77fa8c988191bc64159248ca2b93)。读取日期：2026-10-18。

按本次要求，直接在当前 ET/YIUI 工程开发。原有工作先提交到 `tuanjie` 的 `42a2b7a27`，随后创建 `redblue` 分支。参考算法位于 `D:\MyWork\MyProject\minigame_idea\RedBlue\src\simulation.js`；参考工程未修改。

## 启动方式

1. 使用工程已有的团结 1.9.3 / `2022.3.62t11` 打开 `Unity`。
2. 正常完成 Package Manager 加载后执行 `ET/Compile`（F6），更新 ET 热更 DLL；当前 `EnableDll` 已开启，不可沿用旧 DLL 验证入口。
3. 当前 `GlobalConfig.EnableRedBlueDemo` 已启用。打开 `Assets/Scenes/Init.unity`，进入 Play，从 ET 的 `AppStartInitFinish` 事件启动本地对抗。
4. `ET/RedBlue/Use Original Login` 关闭 Demo；`ET/RedBlue/Enable Demo` 再次启用。切换后重新进入 Play。
5. `ET/RedBlue/Validate Demo` 检查 Shader 导入和 5 万人零伤害模拟。

Demo 复用 ET 初始化与代码加载，跳过登录界面及 YIUI 登录资源。Demo 开启时，资源包在 Editor 使用模拟模式，在 Player 使用内置离线模式，不请求 CDN。Player 仍需要现有 ET 构建链生成的内置 YooAsset 包、代码 DLL 和 AOT 资源。

## 本次实现

- `Loader/RedBlue/CrowdSimulation.cs`：纯 C# 连续数组，每个士兵保留独立状态，固定 1/30 秒步长。预分配数组，Step 不分配托管内存。
- 连续分桶与前缀和处理分离，每对只处理一次、等量反向施力；48 次候选预算。独立阵营链表网格寻敌，24 次候选预算，复用有效目标。
- `FlowField.cs`：每六步更新共享多源 BFS，双线性插值引导。移动和攻击读取旧状态，统一提交移动与伤害。
- `CrowdRenderer.cs`：共享四边形、自定义 URP 无光照 Shader，511 人一批 GPU Instancing，无阴影；插值显示、受击闪白和死亡淡出。视野剔除只影响绘制，全部存活单位继续模拟。
- 不支持 Instancing 的设备降级逐次绘制，仅用于诊断；面板显示真实提交次数，不适合万人性能目标。
- `RedBlueDemo.cs`：单一管理器；2 千、1 万、2 万、5 万人数，正面、错落、包夹、密集阵型；暂停、1/2 倍速、重开、双指/滚轮/按钮缩放；胜负显示两秒后可循环重开。
- 只绘制、只计算、完整对抗模式；零伤害持续负载。面板显示 FPS、最近 300 帧 P95、实际模拟 Hz、相对当前倍率的推进比例、存活/绘制人数、阶段 CPU 时间、提交次数及托管内存。
- 调度单帧最多执行四步、累计最多保留八步；过载时模拟会变慢，面板如实显示推进比例。暂停及切后台清除待追赶时间。

## 已完成的验证

运行：

```powershell
dotnet run --project Tools/RedBlue/RedBlue.Tests.csproj -c Release
```

验证四种阵型的边界、存活统计、敌方目标、固定种子复现；双人对战能结束；同时致命攻击允许同归于尽；双人分离无单向漂移；四档人数密集零伤害保持人数，Step 稳态分配为零。

本机桌面 .NET 8，密集阵型预热 10 步、采样 90 步：2 千约 0.41ms/步，1 万约 2.09ms/步，2 万约 4.12ms/步，5 万约 7.17ms/步。短时微基准只衡量模拟，不包含团结 Mono/IL2CPP、GPU、微信或发热；不可作为手机性能结果。

新增模拟、渲染、控制界面、配置与 Editor 工具已使用本机团结引擎 API 和缓存依赖做独立 C# 编译检查。该检查不能替代完整工程编译、ET 热更编译或运行验收。

## 当前未通过的环节

团结完整批处理在 Package Manager 阶段失败：`The "path" argument must be of type string. Received undefined. No packages loaded.` 日志：`Temp/redblue-validation.log`、`Temp/redblue-validation-cwd.log`。尝试跳过包管理器后，uGUI/MemoryPack 等依赖未载入，现有项目源码编译失败；因此该方式不能用于验证 Demo。

尚未确认 Shader 实际编译、画面与触控效果、ET 全链路启动、微信导出和安卓/iPhone 真机结果。此次没有替换或升级引擎和依赖来绕过问题。

## 后续验收

先恢复正常包加载，执行 F6 与 Demo 验证，再检查 Editor 连续运行和触控。沿用现有 `ET/WeChat` 构建工具重新生成 YooAsset/代码/AOT/微信包，不能直接使用分支创建前的 `WXBuild` 产物。分别在安卓和 iPhone 微信运行三种模式，逐档记录人数、实际模拟比例、P95、内存、持续十分钟后的降频表现。

当前小兵使用 Shader 程序轮廓占位。共享动画图集、远景轮廓 LOD、静态分块网格加数据纹理的第二渲染路径，以及两条渲染路径的真机对比尚未实现。GPU 耗时、完整原生内存及 GC Profiler 指标需借助团结 Profiler/真机工具补测；面板的托管内存不代表总内存，提交次数也不等于 GPU 实测批次。五万人、完整 30Hz 与手机微信约 60 FPS 仍是待验证目标。
