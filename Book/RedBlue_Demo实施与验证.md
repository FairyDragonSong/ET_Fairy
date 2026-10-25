# RedBlue Demo 实施与验证

来源：[团结引擎红蓝群体对抗微信小游戏技术方案与实施交接](https://chatgpt.com/space/page_eb1f77fa8c988191bc64159248ca2b93)。读取日期：2026-10-18。

按本次要求，直接在当前 ET/YIUI 工程开发。原有工作先提交到 `tuanjie` 的 `42a2b7a27`，随后创建 `redblue` 分支。参考算法位于 `D:\MyWork\MyProject\minigame_idea\RedBlue\src\simulation.js`；参考工程未修改。

## 启动方式

1. 使用工程已有的团结 1.9.3 / `2022.3.62t11` 打开 `Unity`。
   也可运行 `Tools/RedBlue/Open-Editor.ps1`。该脚本在当前进程缺少 `ALLUSERSPROFILE` 时补充公共应用数据目录，避免团结包管理器在拼接全局配置路径时抛出异常；不修改系统环境变量。
2. 正常完成 Package Manager 加载后执行 `ET/Compile`（F6），更新 ET 热更 DLL；当前 `EnableDll` 已开启，不可沿用旧 DLL 验证入口。
3. 当前 `GlobalConfig.EnableRedBlueDemo` 已启用。打开 `Assets/Scenes/Init.unity`，进入 Play，从 ET 的 `AppStartInitFinish` 事件启动本地对抗。
4. `ET/RedBlue/Use Original Login` 关闭 Demo；`ET/RedBlue/Enable Demo` 再次启用。切换后重新进入 Play。
5. `ET/RedBlue/Validate Demo` 检查 Shader 导入和 5 万人零伤害模拟。

Demo 复用 ET 初始化与代码加载，跳过登录界面及 YIUI 登录资源。Demo 开启时，资源包在 Editor 使用模拟模式，在普通 Player 使用内置离线模式，在 WebGL 使用 WebPlayMode 的内置清单，跳过远端版本更新。Player 仍需要现有 ET 构建链生成的内置 YooAsset 包、代码 DLL 和 AOT 资源。

## 本次实现

- `Loader/RedBlue/CrowdSimulation.cs`：连续 SoA 数组，每个士兵保留独立状态，固定 1/30 秒步长。保留托管参考后端，Unity 下默认使用 Burst，桌面支持九色 Jobs 并行；预分配数组。
- 连续分桶与前缀和处理分离，每对只处理一次、等量反向施力；48 次候选预算。独立阵营链表网格寻敌，24 次候选预算，复用有效目标。
- `FlowField.cs`：每六步更新共享多源 BFS，双线性插值引导。移动和攻击读取旧状态，统一提交移动与伤害。
- `CrowdRenderer.cs`：默认静态分块网格＋双状态纹理，自定义 URP 无光照 Shader，5 万人 7 次提交；GPU 插值、受击闪白和死亡淡出。兼容回退保留 511 人一批的 GPU Instancing。全部存活单位持续独立模拟。
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

## 编辑器启动排查与运行验证

最初团结完整批处理在 Package Manager 阶段失败：`The "path" argument must be of type string. Received undefined. No packages loaded.` 日志：`Temp/redblue-validation.log`、`Temp/redblue-validation-cwd.log`。后续已定位为启动进程缺少 `ALLUSERSPROFILE`，团结包管理器的 `getGlobalConfigRoot()` 使用该变量拼接路径。补充进程环境后正常解析依赖；GitHub 克隆超时的三个包已从旧缓存恢复相同版本。旧 Library 保留在 `Temp/redblue-library-backup`，新 Library 已重新导入完成。

已在可见的团结编辑器中打开 `Init` 场景，执行 F6，Console 确认 `Compile Finish!` 且错误数为零。进入 Play 后经 ET 完整启动链正常进入红蓝 Demo，YooAsset 使用 EditorSimulateMode，士兵 Shader 正常显示，无 Demo 运行错误。已实际观察推进、接敌、伤亡，并通过鼠标检查人数切换、零伤害、暂停与恢复。当前使用放大的 Game 视图展示一万人正常对抗。

短时现场观察：一万人面板约 59.8 FPS、P95 17.3ms、实际模拟 30.0Hz；五万人零伤害保持双方各 25,000 人，面板约 40–47 FPS、P95 29–35ms、实际模拟约 30Hz、98 次绘制提交。均为本机 Windows DX11 编辑器的短时界面读数，不是正式持续压测或手机结果。编辑器托管内存包含编辑器自身，不能解释为游戏 Player 内存。

当时尚未完成手机触控、微信导出和安卓/iPhone 真机验收。程序轮廓仍为占位小兵；控制条遮挡问题在本轮优化中已通过移至底部处理。最新构建进度见末节。

## 2026-10-18 Unity MCP 五万人对照复测

已连接本地 `http://127.0.0.1:8090/mcp`，目标实例为本工程 `Unity@5f8466c7f258b63e`。连接初期发现 ET 回调将自己的同步上下文留给后续编辑器回调，MCP 注册和命令反复超时；`Init` 现已在 Start/Update/LateUpdate/退出回调的 finally 中恢复启动时捕获的 Unity 上下文。重新编译后，在编辑模式和 Play 模式通过 MCP 执行代码成功，返回 `UnityEngine.UnitySynchronizationContext`，测试结束后 MCP 仍可用，Console error 查询为零。

设备：i9-14900HX（32 逻辑处理器）、RTX 4060 Laptop、DX11、团结 `2022.3.62t11`。最大化 Game 视图，实际渲染 1549×871，VSync=1，目标显示帧率 60，模拟 30Hz、1 倍速、固定种子 7241。四组均零伤害、持续保持 50,000 人；完整模式和只绘制模式全部 50,000 人进入绘制。

每组预热 10 秒、采样 20 秒。首次测试的 MCP 执行查询造成一次采样内停顿，已舍弃该轮，最终复测在采样期间不发送 MCP 查询，结果在组间自动写文件。最终四组没有漏帧观测。

| 模式 | 平均 FPS | 帧间隔 P95 | 实际模拟 Hz | 存活/绘制人数 |
| --- | ---: | ---: | ---: | ---: |
| 正面完整对抗 | 20.07 | 70.87ms | 29.96 | 50,000 / 50,000 |
| 密集完整对抗 | 27.86 | 54.75ms | 30.00 | 50,000 / 50,000 |
| 只绘制正面阵型 | 59.93 | 23.13ms | 关闭 | 50,000 / 50,000 |
| 只计算密集阵型 | 46.34 | 27.50ms | 29.99 | 50,000 / 0 |

正面完整对抗的 CPU 阶段采样均值约为：网格 1.03ms、分离 13.52ms、决策 6.03ms、提交移动与伤害 2.49ms，合计约 23.1ms/步。密集完整对抗约 20.0ms/步。这里是“发生模拟推进的帧所暴露的最后一步”采样，不是逐步完整 Profiler 捕获；正面对抗有 187 帧执行多步追赶，所有步骤的精确分布还需补充 Profiler 标记。

渲染 CPU 组装与提交平均约 10.3–10.5ms/帧，98 次小兵批次；Unity 统计完整场景约 104 次 draw call，包含 UI。`CrowdRenderer.Draw` 时间包含逐兵可见性判断、矩阵/颜色组装和绘制调用，不是 GPU 时间。

结论：当前五万人能维持完整独立模拟接近 30Hz，但持续接敌时画面只有约 20–28 FPS，尚未达到完整对抗 60 FPS。先前编辑器短时观察到的 40–47 FPS 只适用于当时阶段，不能作为持续接敌结论。优先优化局部分离候选遍历与开方等计算，以及逐帧矩阵组装/上传；仅降低显示分辨率不足以证明能解决这些 CPU 成本。

采样期间各组 Gen0 collection 增量均为零；这不代表整个编辑器零分配。托管内存包含 Editor/MCP，不能当作 Player 或手机内存。尚无 GPU 实测时间、独立 IL2CPP Player、微信手机或十分钟发热数据。

原始数据：`Tools/RedBlue/Results/editor-20261018.json`。复测入口：`Tools/RedBlue/EditorBenchmark.csx`，在 Demo Play 模式下通过 MCP `execute_code` 执行完整方法体；约两分钟结束，输出 `Temp/redblue-editor-benchmark.json`。脚本会设置五万人零伤害、1 倍速并轮换三种模式，测试期间保持最大化 Game 视图且不操作编辑器。完成后可恢复完整对抗供查看。

## 后续验收

继续检查 Editor 长时间运行、阵型、倍率与触控。沿用现有 `ET/WeChat` 构建工具重新生成 YooAsset/代码/AOT/微信包，不能直接使用分支创建前的 `WXBuild` 产物。分别在安卓和 iPhone 微信运行三种模式，逐档记录人数、实际模拟比例、P95、内存、持续十分钟后的降频表现。

当前小兵使用 Shader 程序轮廓占位。静态分块网格加数据纹理路径已实现，见下节；共享动画图集、远景轮廓 LOD 和两条渲染路径的真机对比尚未实现。GPU 耗时、完整原生内存及 GC Profiler 指标需借助团结 Profiler/真机工具补测；面板的托管内存不代表总内存，提交次数也不等于 GPU 实测批次。五万人、完整 30Hz 与手机微信约 60 FPS 仍是待验证目标。

## 2026-10-18 性能优化实施

保留 ET 初始化、热更和原模拟参考实现，没有引入完整 Entities World。模拟采用 SoA 数组与 Burst Jobs：数组在模拟生命周期内固定一次供 Jobs 直接访问，Step 返回前完成全部任务，重开和退出释放固定句柄；没有每步托管/原生双向拷贝。当前不是 NativeArray 数据所有权迁移。网格、寻敌、战斗和统一提交使用 Burst，分离支持九色网格并行，同色任务写入区域互不重叠，每对单位等量反向施力。桌面一万人以上默认并行，小规模和微信/非桌面默认同步 Burst，不假定微信可使用工作线程。运行时检测实际 Burst 执行，保留 ManagedReference、BurstSingleThread 和 BurstParallel 对照入口。

渲染增加静态网格＋双 RGBAFloat 状态纹理，每块最多 8,192 人，5 万人 7 次小兵提交。位置、方向、阵营、受击和死亡状态每个模拟步上传，GPU 插值和旋转；消除原先逐帧逐兵矩阵构造。死亡淡出、跨多步插值和重开状态已处理。不依赖 Compute/Indirect；不支持该纹理或 Shader 的设备回退原 Instancing 路径，可用 CrowdRenderer.ForceInstancing 强制对照。当前静态块使用全战场包围盒，放大视野时仍可能处理块内屏外顶点；面板 Drawn 表示可见人数，不能解释为 GPU 只处理该人数。控制条已移至底部。

以下为与上一节相同设备、窗口尺寸、人数、种子、零伤害、30Hz 模拟、10 秒预热＋20 秒采样的结果。采样期间无 MCP 查询，均无漏帧观测。

| 完整模式 | 原版本 FPS / P95 | Burst 并行＋新渲染 FPS / P95 | Burst 单线程＋新渲染 FPS / P95 |
| --- | ---: | ---: | ---: |
| 正面对抗 | 20.07 / 70.87ms | 59.34 / 18.12ms | 59.15 / 21.64ms |
| 密集混战 | 27.86 / 54.75ms | 60.00 / 17.44ms | 59.99 / 21.85ms |

完整组均保持 50,000 存活和绘制，实际模拟约 30Hz。并行版本只绘制 59.38 FPS，只计算 60.01 FPS。正面渲染 CPU 组装/提交均值从约 10.3ms 降至 0.62ms/帧；小兵提交从 98 次降至 7 次，场景统计约 13 次含 UI。并行分离约 2.20ms/步（密集 1.55ms），单线程正面约 5.94ms/步。新脚本按累计阶段计数覆盖每一步，旧版为最后一步估计，阶段均值不能当作严格同口径的 Profiler 对比。新结果为 Editor 稳定短测，未证明手机 60 FPS；帧率受 60FPS/VSync 上限限制。单线程正面有一次 234ms 最大帧，编辑器 Gen0 增量 1，其余完整组为 0；仍需独立 Player 和持续稳定性定位。

回归入口 Tools/RedBlue/OptimizationChecks.csx：在编辑器 execute_code 执行，覆盖三后端四阵型、边界/目标/人数、固定种子重复、同时致命攻击、双人分离守恒、Dispose 幂等与释放后访问拒绝、跨后端首步误差以及两轮 5 万人 120 步。已全部通过，Burst 实际执行标记为真；Managed 与 Burst 首步位置最大误差 0.000122。不同后端的浮点和遍历顺序会导致长期战况不同，保证的是同后端重复，不提供跨设备锁步一致性。

原始结果：Tools/RedBlue/Results/editor-optimized-parallel-20261018.json、editor-optimized-single-20261018.json、optimization-checks-20261018.json。微信目标构建及真机验收记录另补。

## 微信构建链验证结果

本轮已实际切换 MiniGame，执行 ET/Compile、HybridCLR/Generate/All、CopyAotDlls、YooAsset ForceRebuild，再调用 SDK DoExport，返回 SUCCEED；导出后 Console error 数为 0。YooAsset 版本 2026-10-18-1305，35 个内置文件；新 Shader 已通过小游戏目标编译。编译响应文件确认定义 UNITY_WEBGL，因此 Demo 在该目标使用 WebPlayMode 的内置清单并跳过远端版本更新，普通登录构建仍保留原校验。SDK 的 Unity.data CDN 检查保留。

产物：Unity/BuildArtifacts/RedBlue-WeChat/minigame（微信开发者工具项目）与 webgl（数据/CDN 文件）。导出使用非 Development、关闭 AutoProfile、IL2CPP OptimizeSpeed、关闭渲染线程，模拟默认同步 Burst。构建成功不等于已在微信运行或已证明 Wasm Burst 加速，仍需真机验证 AccelerationVerified。导出入口 Tools/RedBlue/ExportWeChat.csx，可在 MiniGame、停止 Play、完成 ET/HybridCLR 编译后通过 MCP execute_code 执行；状态文件 Temp/redblue-wechat-export.txt。脚本使用隔离输出并恢复 SDK/代码生成设置，不上传文件。

当前为完整 ET 工程的验证包：原始 Wasm 89,325,814 字节，压缩代码 16,261,215 字节，Unity.data 45,853,019 字节。尚未做首包裁剪或微信提审包体积验收；SDK CDN 仍是本地地址，手机运行前必须配置可访问的合法 CDN 并部署数据文件。本次未上传、未发布、未进行安卓/iPhone 或十分钟发热验收。索引与关键产物 SHA256 见 Tools/RedBlue/Results/wechat-export-20261018.json。构建产物和原始日志留在本地忽略目录，Git 保留源代码、生成的 HybridCLR 引用及验证报告；内置资源清单应每次构建重新生成，不能复用旧包。

构建结束后已恢复 StandaloneWindows64 / Init 场景，重新编译桌面热更 DLL。首次恢复 Play 遇到消息类型映射不一致，退出并请求脚本域重载后 ET 正常启动；切目标后应等待 ET 编译和域刷新完成再进入 Play。最终画面为最大化 5 万人零伤害完整对抗，现场约 59 FPS、30Hz、7 次小兵提交，实际 Burst 标记为真，Console error 为 0。

## 2026-10-18 夜间 CPU / GPU 运行时分析

本轮使用实际 Unity Profiler CPU/GPU/Rendering 记录，关闭 Deep Profile、Profile Editor；固定 1920×1080、50,000 单位、种子 7241、零伤害、1 倍速、30Hz 模拟。各组预热 5 秒、采样 15 秒，采样期间不发送 MCP 查询。主线程与渲染线程全窗口数据来自 FrameTimingManager，GPU 游戏渲染范围来自 Profiler 层级 GPU 列 PlayerLoop，取各原始记录末尾 240 帧。包含采集开销，不可直接与上一轮未开启 Profiler、1549×871 的短测混比。

| 场景 | FPS | CPU 主线程均值 / P95 | CPU 渲染线程均值 | GPU 游戏渲染范围均值 | 模拟 Hz |
| --- | ---: | ---: | ---: | ---: | ---: |
| 正面完整，60FPS / VSync | 59.99 | 4.31 / 7.65ms | 0.71ms | 0.435ms | 30.00 |
| 正面完整，取消限帧 | 298.06 | 2.00 / 7.02ms | 0.66ms | 0.537ms | 30.00 |
| 密集完整，取消限帧 | 304.22 | 1.87 / 5.80ms | 0.66ms | 0.508ms | 30.00 |
| 只绘制，取消限帧 | 384.89 | 1.46 / 1.91ms | 0.61ms | 0.422ms | 关闭 |
| 只计算密集，取消限帧 | 361.13 | 1.61 / 4.17ms | 0.58ms | 0.412ms | 30.00 |

取消限帧不改变模拟频率：同样每秒 30 步被更多显示帧分摊，不能把主线程帧均值下降解释为模拟本身加速。只计算模式仍保留原场景相机、背景和控制 UI，因此 GPU 不为零。五组均保持 50,000 人，没有漏帧观测。密集组记录一次 267.64ms 最大帧，同时 Gen0 增量为 1；其他组为 0。这是 Editor 加采集器环境，尚未定位分配来源，也不能直接归因于模拟。

GPU 计时口径需区分：FrameTimingManager 的 GPU 值在正面限帧时约 0.302ms、取消限帧时约 3.215ms；RawFrameDataView.frameGpuTimeMs 分别约 11.558ms、0.827ms。两者在 Editor 中明显不一致，不能任选其一当成 Shader 忙碌时间。本轮进一步读取 GPU hierarchy 的 PlayerLoop、相机和 Pass 列，表格只报告该游戏渲染范围，不把整帧剩余区间解释为 GPU 着色瓶颈，也不声称这是硬件 GPU busy 百分比。Unity 的帧时序定义和延迟说明：https://docs.unity3d.com/2022.3/Documentation/Manual/frame-timing-manager.html 。

### 相机对照的明确收益

发现原场景 MainCamera（Skybox）、YIUICamera（Depth）与深度 100 的 RedBlue Camera（SolidColor）全部在渲染。原 MainCamera 的背景随后被 Demo 覆盖；Demo 自己使用 Overlay 控制界面。取消限帧完整对抗中，原 MainCamera GPU 范围均值约 0.335ms，RedBlue Camera 约 0.157ms，YIUICamera 约 0.029ms。

使用相同配置分别采样三相机与仅 Demo 相机，均 5 秒预热＋15 秒采样：

| 指标 | 原三相机 | 临时只保留 Demo 相机 |
| --- | ---: | ---: |
| 完整对抗 FPS | 300.33 | 415.65 |
| CPU 主线程均值 | 2.00ms | 1.13ms |
| CPU 渲染管线 DoRenderLoop 均值 | 1.125ms | 0.473ms |
| GPU 游戏渲染范围均值 | 0.537ms | 0.388ms |
| 场景 draw calls（含 UI） | 12 | 10 |
| 存活 / 模拟频率 | 50,000 / 30Hz | 50,000 / 30Hz |

FPS 约提高 38%，CPU 渲染管线范围约下降 58%，GPU 游戏渲染范围约下降 28%。这是短时顺序 A/B，不是跨设备收益承诺；不同帧率下 CPU 模拟均值的分摊也会变化。两组没有 GC 回收。测完已恢复两台旧相机、60FPS、VSync、Profiler 区域开关；未修改生产脚本和场景资产，音频预览设置也恢复。

### 后续优化顺序

1. 优先在 Demo 生命周期内停用不需要的 MainCamera / YIUICamera，退出 Demo 恢复状态。A/B 已证明收益，需验证 UI、镜头切换及原登录模式不受影响。
2. CPU 继续处理寻敌、网格和分离。限帧正面对抗每步网格（含共享 Flow 更新）0.63ms、分离 1.51ms、寻敌/决策 1.68ms、提交 0.15ms，合计约 3.97ms。优先评估按非空网格生成分离任务、减少九色阶段调度和同步；再把每单位决策与统一伤害归并拆开，避免直接并行写共享 damage / RNG 引入竞态。31 个 Job 工作线程的分离 marker 耗时合计约 8.89 core-ms/显示帧，这是各线程时间之和，不能与主线程墙钟时间相加。减少空任务有望改善总 CPU 工作量和手机功耗。
3. 渲染 CPU 原路径平均约 0.63ms/显示帧，发生状态更新的帧约 1.3ms，仍包含状态打包、可见统计和纹理提交。应给 Pack、CountVisible、SetPixelData、Apply 分别补标记后再改；可评估 Burst 打包、共用遍历、仅处理存活块。RHI 的纹理上传范围在取消限帧时约 0.010ms/帧，目前没有证据表明它是桌面首要瓶颈。
4. GPU 在本机有余量。手机端优先测试透明叠加、屏外/死亡块剔除、显示 LOD、相机附件和拷贝；状态纹理量化须验证坐标精度。先以真机 GPU Pass 数据验证，再决定是否换动画图集或渲染路径。当前证据不要求继续迁移完整 Entities / Compute 渲染。
5. 单独定位 267ms 尖峰，使用独立 IL2CPP Player 和真机采集分配调用栈、最长帧与持续温升；不能仅按平均 FPS 验收上线。

### 整机占用辅助采样与复现

恢复 60FPS 并关闭 Profiler 后，约 10 秒采样：整个 Tuanjie Editor 进程 CPU 均值约 6.35%（32 逻辑处理器口径，范围 4.11%–7.94%，平均约 2.03 核），全显卡占用约 45.2%（44%–47%）、GPU 温度 78–79℃、整卡功率约 51–54W。CPU 包含 Editor / ET / MCP 等整个进程，GPU 是 nvidia-smi 的整卡数据，包含其他应用，不能当作本 Demo 独占使用率；总 CPU 百分比低也不代表关键主线程没有瓶颈。它们不替代上面的帧/Pass 计时。

复现：Tools/RedBlue/CpuGpuProfile.csx 自动运行五组；调用前启用 CPU/GPU/Rendering Profiler 区域、关闭其他区域，测后恢复原值。相机 A/B 在脚本前设置 AppDomain.CurrentDomain.SetData("RedBlue.CpuGpuProfile.CameraComparison", true)。AnalyzeCpuGpuProfile.csx 离线读取各组记录；分析 A/B 同样在脚本前设置该标记。一次只运行一个采样任务，保持 Game 视图，不在采样期间查询或操作 Unity。

完整本地原始记录：Temp/RedBlueProfile/*.data、Temp/RedBlueCameraAB/*.data，可从 Unity Profiler Load 打开。Git 保留采样脚本以及 Tools/RedBlue/Results/cpu-gpu-editor-20261018.json、cpu-gpu-passes-20261018.json、cpu-gpu-camera-ab-20261018.json、cpu-gpu-camera-passes-20261018.json、cpu-gpu-hardware-20261018.json。当前没有新增手机 GPU 测量。


## 2026-10-19 微信小游戏局域网真机测试包

本次工程导出到 `Unity/BuildArtifacts/RedBlue-WeChat-20261019/minigame`，开发者工具导入此目录。资源根目录为同级 `webgl`，SDK DATA_CDN 已改为 `http://192.168.1.8:8080`。使用 `Tools/RedBlue/Start-LanResourceServer.ps1 -Background` 启动限定 webgl 目录的局域网服务；同一端口已有服务时脚本拒绝重复启动。电脑 IP 改变时，需同步更新 SDK CDN 并重新转换工程，同时修改服务绑定地址。

已完成 MiniGame 平台 ET 编译、HybridCLR GenerateAll、CopyAotDll、YooAsset 构建、微信 IL2CPP/Wasm 导出及导出后预检。首次导出更新了 Unity.Loader 补充元数据，随后同步最终 AOT、重建资源并再次构建；SDK 重复导出占用 slim/backup.txt 时释放终结器，再调用 DoExport(false) 转换现有 Player，不重建资源。最终结果 SUCCEED，导出后 ValidateBuild(true) 通过。资源版本为 2026-10-19-2332。

实际数据文件、Wasm 压缩文件和资源版本入口均 HTTP 200，下载 SHA-256 与本地一致；资源清单入口同样 200。用户已在手机浏览器确认 lan-check.txt 显示 RED_BLUE_RESOURCE_OK。完整核验记录在 `Tools/RedBlue/Results/wechat-export-20261019.json`。这只验证导出和下载链路，尚未验证手机小游戏启动、Burst 后端和真机性能。

本次保留用户当前 RedBlueDemo.cs：目标 60 FPS、默认 2,000 人，可切至 50,000 人。持续负载测试关闭 Damage，观察存活人数、FPS/P95、Simulation Hz 和后端名称。手机测试说明随导出目录交付。局域网 HTTP 用于当前开发测试，未发布公网。


### 2026-10-20 真机启动资源目录修复

用户手机能进入 Unity 场景，但 WebPlayMode 请求 `/StreamingAssets/build_info` 和 `/StreamingAssets/Bundles/DefaultPackage/PackageManifest_DefaultPackage.version` 返回 404，随后 Unity.Model.dll / mscorlib.dll 映射失败。检查发现转换后的 webgl/StreamingAssets 是空目录，原发布工具只复制热更新 CDN，遗漏 WebPlayMode 启动需要的内置清单与 Bundle。现已在 PublishYooAssetToLocalCDN 中补充同版本 StreamingAssets 发布，排除 .meta 文件；资源版本不匹配时拒绝发布。无需重编 Wasm。

已通过 Unity MCP 实际调用修复后的发布方法，发布 39 个文件、35,734,769 字节；逐文件 HTTP 下载与本地哈希校验全部通过，build_info、版本入口、清单和 Bundle 均返回 200。已反序列化实际发布的清单，验证 Unity.Model.dll 和 mscorlib.dll 的地址映射存在。用户随后确认手机可以运行。模拟器等待 wasm-instantiate 的处理结果见下一节。资源诊断及核验记录：Tools/RedBlue/Results/wechat-startup-diagnosis-20261020.json。

## 2026-10-20 开发者工具 Wasm 初始化卡住的排查与处理

### 现象与结论边界

Windows 微信开发者工具 Stable `2.01.2510290`、基础库 `3.16.3`，原工程停在 Unity 初始化界面，持续输出：

```text
still waiting runDependencies
["wasm-instantiate", "wasm-preloadAssets"]
```

这是启动依赖尚未完成的提示，不是具体失败原因；两个依赖名称不能直接解释成两个独立故障。此时尚未进入红蓝 Demo，与五万人模拟或 60 FPS 上限无关。同一份原包在手机微信可以进入游戏；退出代理、重启电脑后，开发者工具仍复现。此前 Network 检查未发现对应的失败或持续等待资源请求，不能仅凭此提示认定为 VPN/网络问题。

已验证的有效处理是：使用团结引擎自带 `wasm-opt`，对导出的原始 Wasm 执行 `-Oz --strip-debug`，压缩并替换到独立的完整小游戏测试工程。用户确认 `RedBlue-Optimized-Compatibility` 可以进入游戏。尚未取得开发者工具内部阻塞堆栈，不能将根因确定为某个引擎缺陷、固定大小限制、内存不足或单一 Wasm 特性。

### 对照实验

独立诊断工程不加载 Unity 插件、ET 启动流程或 CDN 游戏资源。以下微信结果由用户编译并提供运行日志：

| 测试 | 结果 |
| --- | --- |
| 空 Wasm；256 MiB 内存；1027 个导入函数 | 均成功 |
| 分包中的空 Wasm，原始格式及 Brotli 格式 | 均成功 |
| 不存在的 Wasm 路径 | 正常返回文件不存在错误 |
| 解压后 32、60、64、68 MiB 的空壳模块 | 均成功 |
| 解压后 85 MiB 的空壳模块 | 超过 20 秒仍未返回 |
| 原游戏 Wasm，空导入或桩导入 | 超过 20 秒仍未返回 |
| 原游戏 Wasm 仅移除 `name` 段 | 超过 20 秒仍未返回 |
| 完整游戏使用 `-Oz --strip-debug` 优化产物 | 用户确认可以进入游戏 |

`PENDING > 20s` 只表示该次调用超过观察窗口未返回，不等于已经捕获了编译失败。68 MiB 空壳成功，而约 67.55 MiB 的去名称游戏模块仍等待，说明体积不是唯一判断依据；空壳与真实代码的编译负载也不等价。桩导入成功仅能验证实例化，不代表 Unity 逻辑运行正确。

### 实际操作与产物变化

操作位置是 Windows PowerShell，不是 Unity 编辑器面板。在本次安装及导出目录下执行：

```powershell
& 'D:\Program Files\TuanJieEdit\2022.3.62t11\Editor\Data\PlaybackEngines\WeixinMiniGameSupport\BuildTools\Emscripten\binaryen\bin\wasm-opt.exe' `
  'E:\github\ET\YIUI\Unity\BuildArtifacts\RedBlue-WeChat-20261019\webgl\Build\webgl.wasm' `
  -Oz --strip-debug `
  -o 'E:\github\ET\YIUI\Temp\RedBlue-Wasm-Standalone\optimized.wasm'
```

输入为导出的原始、未压缩 `.wasm`，不要将 `.wasm.br` 直接当作此命令的输入。`-o` 写到独立文件，保留原始导出。安装版本或工程位置变化时相应调整路径。

| 项目 | 原始版本 | 优化版本 |
| --- | ---: | ---: |
| Wasm 未压缩字节数 | 89,323,698 | 63,018,447 |
| `name` 调试名称段负载字节数 | 18,490,518 | 无该段 |
| 代码指令段负载字节数 | 59,198,311 | 51,382,394 |
| 模块内定义的函数数 | 187,709 | 188,839 |
| 导入项 / 导出项 | 1027 / 754 | 1027 / 754 |
| 本次 Brotli 文件字节数 | 16,199,585 | 14,704,228 |

表中的段负载不包含段头，未压缩大小与下载大小应分开记录。函数数量略有增加，不能把成功解释成“大量删除函数”；优化器重写了等价代码结构。优化前后导入、导出描述一致，优化模块通过本机 Wasm 校验和桩导入实例化；这些检查不替代完整运行回归。

命令本身只产生 `optimized.wasm`，完整交付还需要：

1. 对优化结果进行 Brotli 压缩，得到微信代码分包使用的 `.wasm.br`。
2. 将压缩产物放入独立候选工程的 `minigame/wasmcode/`，使用新的文件标识。
3. 同步修改候选工程 `game.js` 的 `CODE_FILE_MD5`，确保它与实际 Wasm 文件名匹配。本次候选使用压缩文件 MD5 的前 16 位 `d97f07b99204bc85`；这是本次候选的生成方式，不应假定所有 SDK 版本都采用同一规则。
4. 编译完整小游戏，确认已进入场景，再做手机及玩法/性能回归。只运行优化命令、却没有替换分包和配置，微信仍会加载旧文件。

本次验证工程：`Temp/RedBlue-WeChat-Optimized/minigame`，开发者工具显示 `RedBlue-Optimized-Compatibility`。代码文件为 `wasmcode/d97f07b99204bc85.webgl.wasm.code.unityweb.wasm.br`。原工程 `Unity/BuildArtifacts/RedBlue-WeChat-20261019/minigame` 未覆盖；资源 CDN 与资源版本沿用原包。局部工具、诊断脚本与候选产物保存在本机忽略目录 `Temp/RedBlue-Wasm-Standalone` 和 `Temp/RedBlue-WeChat-Optimized`，不是已提交的正式构建入口，可能随清理而丢失。上述操作尚未接入 `Tools/RedBlue/ExportWeChat.csx` 或一键导出流程。

### 调试影响与待验证事项

`--strip-debug` 本身就会移除调试信息，包括 `name` 段，不需要在命令之外重复删除一次。它不会关闭 `Debug.Log` / `Debug.LogError`，也不会取消异常处理；但 Wasm 调试、函数名调用栈及源码定位能力会减弱。保存原始 Wasm、构建记录和对应调试材料用于排查，不能保证任意优化后的地址都能直接用优化前文件还原调用栈。

`-Oz` 以体积为优先目标，运行速度可能提高或降低，需要实际测量；它不主动降低人数、画质或修改玩法。本次只确认优化后的完整游戏在开发者工具可以进入场景，尚未证明优化包的手机回归、五万人性能或持续运行稳定性。

仅删除名称已验证无效；`-Oz` 优化并保留调试信息的组合（例如 `-Oz -g`）尚未测试。因此，当前使用已验证可启动的 `-Oz --strip-debug` 包查看日志，并归档原始材料；不能把无法启动的原包推荐成可用的开发者工具调试包。用户本轮决定先保留当前结果，不继续扩展实验。

## 2026-10-22：避让简化与数据布局探索

根据真机 sim 中避让约占 Step 63% 的结果，对当前 kernel 的临时 Editor-only 副本完成 31 组探索试验。没有替换生产 kernel，也没有导出新的手机包。两种阵型为 Islands / Dense，50,000 单位、种子 7241、无伤害；预热 300 tick，测量 301..600 tick。每组均检查实际 Burst 执行与坐标范围，原始算法试验副本的状态 SHA-256 与生产版既有 600 tick 回放一致。

### Islands 结果与取舍

以下是邻近同轮基线的桌面 Burst 测量，百分比仅指 Separation 阶段，不能直接换算手机 FPS：

| 试验 | 避让耗时观测变化 | 600 tick 平均最近邻重叠比例 | 说明 |
| --- | ---: | ---: | --- |
| 原版 48 候选 | 基线 | 0.443 | 既有算法 |
| 上限 40 | 减少约 3% | 0.483 | 收益小，仍改变轨迹 |
| 上限 32 | 减少约 17% | 0.507 | 重叠与速度变化增加 |
| 上限 24 | 减少约 29% | 0.551 | 较明显的质量代价 |
| 上限 16 | 减少约 45% | 0.617 | 重叠进一步加重 |
| 避让 30 → 15 Hz，跳过帧复用力 | 减少约 55% | 0.458 | 四阶段合计约省 41%，但存在明显的动态与尾部重叠代价 |
| 轻度重叠三次多项式近似，深度重叠保留 sqrt | 增加约 20% | 0.449 | 此实现没有收益 |
| 有理式近似，极近距离保留原式 | 增加约 2% | 0.545 | 没有可靠收益且重叠变差 |
| NoAlias 内存互不重叠标注 | 增加约 2% | 0.443 | 状态一致，收益不明确 |
| 同格坐标连续存放 | 减少约 5% | 0.443 | 状态一致，但建索引成本增加，四阶段合计只省约 2%；Dense 合计几乎无收益 |

质量指标在 tick 300 / 600，对固定均匀选取的 2000 个单位查询精确最近邻。重叠比例为 `max(0, 1 - 最近邻距离 / 碰撞直径)`，越大代表几何重叠越严重；它不是美术遮挡像素，也不是视觉验收。原版本身在拥挤战斗中已有较多重叠，因此不能将“50% 以上重叠单位占比下降”单独视作质量提高，还必须检查平均值和 P95。

15 Hz 方案在 Islands 的 P95 重叠从 0.684 增至 0.796，采样速度每 tick 变化的 RMS 从 7.41 增至 10.35（约 +40%）；这是抖动风险代理指标，不是已经完成的手机观感验收。Dense 原版接近静态，而降频版 RMS 明显增加，P95 重叠也从 0.588 增至 0.804，因此不建议把全场统一 15 Hz 直接作为默认替换。上限 24 的 Islands RMS 从 7.41 增至 13.62，降低预算同样存在明显运动代价。

当前预算消耗包括未重叠候选及对方预算耗尽时的尝试，不是有效接触数。Islands tick 600 原版平均预算消耗约 25.4、约 6.2% 单位达到 48；改成 24 后约 56.4% 达到上限。削减候选会影响配对、后续空间分布及决策成本，不是纯粹同比减少算术。15 Hz 试验最后 tick 跳过分离且预算刚清零，末帧预算为零，不能解释为整个窗口没有避让工作。

### 后续建议与复现

高收益路线需要接受行为近似。优先考虑按镜头缩放／区域分配避让精度，并将低频更新错开，近景或质量要求高的区域保持 30 Hz；这尚未实现或验证，不能提前承诺收益。本例后期接敌比例很高，单纯只降低未接敌单位频率可能收益有限。若用户选择性能优先，先在可切换候选中比较 full 模式的拥挤、运动稳定性和手机耗时，再决定质量预算。纯密度压力场可作为更大规模的算法候选，但无法直接保证逐单位防重叠，属于后续独立设计。

计时受到桌面 CPU 状态影响，较小差异不作收益验收；各轮原始结果已保留，未将所有跨轮计时合并成同条件均值。只有一个种子、无伤害场景、两个固定时间点的几何指标，不能替代长期、有伤害玩法回归和手机视觉检查。

工具：`Tools/RedBlue/Prepare-AvoidanceTrials.py` 根据当前 kernel 与 `AvoidanceTrialsRunner.cs.txt` 生成临时 `AvoidanceTrials.EditorOnly.cs`，刷新 Unity 后可通过 execute_code 调用 `ET.RedBlue.AvoidanceTrialsRunner.Run(variant, formation, round)`。variant 包括 baseline、budget16/24/32/40、half-rate、polynomial、rational、noalias、packed。全部运行结束后本轮已移除临时脚本与其 meta，刷新并确认无编译错误；生产 kernel SHA-256 仍为 `468b97ef693905131d4d9854e8932294b5b98df177c0a613df0b79712a9dd0ef`。试验代码副本与逐组 JSON 位于 `Temp/RedBlueAvoidanceTrials`；结构化归档为 `Tools/WeChat/Results/avoidance-trials-20261022.json`。

## 客户端模拟方向与 GPU 可行性重评

用户明确暂不要求服务器精确复算，只需客户端模拟；跨端确定性与定点数学改造暂缓。优化可以改变旧算法轨迹，但仍须评估拥挤、抖动和战斗表现。镜头是否允许影响逻辑此前有过顾虑，默认方案继续按单位状态／固定模拟 tick 调度，镜头仅影响显示；若要改变这一点再明确选择。

查阅团结引擎官方[性能优化指引](https://docs.unity.cn/cn/tuanjiemanual/Manual/Optimization.html)及[GPU Skinning 支持](https://docs.unity.cn/cn/tuanjiemanual/Manual/GpuSkinning.html)：当前微信小游戏/WebGL 路径不支持 Compute Shader；Transform Feedback 蒙皮可用不等于引擎提供通用人群避让计算接口。因此现阶段不能以普通 `.compute` 文件直接迁移 CPU kernel，也没有验证当前工程可使用 WebGPU。

普通 Shader 渲染到纹理仍是可研究路径：位置、速度等状态写入前后两张纹理，GPU 更新后直接给绘制采样。当前 `StateTextureCrowdRenderer` 已经使用静态网格和状态纹理并在 GPU 插值，但状态仍由 CPU 模拟及上传；这不是现有避让已经在 GPU 执行。浮点纹理可采样也不代表同格式可以渲染写入；应按真机实际格式与混合能力检查。[WebGL 浮点渲染目标规范](https://registry.khronos.org/webgl/extensions/EXT_color_buffer_float/)说明相关可渲染格式的扩展条件。

GPU 方案主要难点是构造邻居数据和状态同步。现有对两个单位同时累加分离力、维护预算的循环不适合直接转换为每像素独立输出。可用格级密度／压力场替代部分精确配对，再由每单位采样场计算局部运动，但会改变避让模型，需视觉与玩法验收。另一种保留邻居列表的纹理查询方案也需要解决列表更新，不能仅把每步所有 50k 单位互相遍历作为可扩展方案。

若 GPU 只算分离力然后每 tick 同步读回给 CPU 决策，数据同步可能抵消收益。更有希望的是让位置／速度／局部运动状态常驻 GPU，绘制直接使用；战斗判定若需要精确位置，必须同时设计 CPU/GPU 数据交换、低频统计或延迟容忍，不能默认 CPU 可以无成本读取最新 GPU 状态。客户端模拟需求放宽了跨端同步约束，并未自动授权取消精确战斗规则。

建议后续顺序：先补 50k Islands full 的整机基线与 GPU 负载判断；再用独立小原型验证当前手机的渲染纹理、状态反馈和 50k 局部运动净成本；结果有优势再扩展到密度场避让与战斗逻辑衔接。CPU Burst 保留为对照与兼容路径，自适应／错峰更新作为可独立验证的近期候选。GPU 原型、本机与真机兼容测试、收益测量均尚未实施，不能承诺帧率或可节省的具体毫秒数。

## GPU 迁移前的 CPU 技术路线复核

用户要求先重新判断 CPU 空间。实际通过 Editor 反射读取 `PlayerSettings.MiniGame / WeixinMiniGame`：`simdSupport=false`、`threadsSupport=false`；`ProjectSettings.asset` 中 `weixinMiniGameSIMDSupport: 0`、`weixinMiniGameThreadsSupport: 0` 与之相符。当前引擎版本号为 2022.3.62t11，编译定义含 `TUANJIE_1_9_3` 和 `TUANJIE_1_9_OR_NEWER`。此轮没有更改开关或重新构建。

本机 Burst 1.8.30-t2 的 `Editor/BurstAotCompiler.cs` 在当前有效编译条件下读取 `PlayerSettings.MiniGame.simdSupport`，并在编译 Wasm bitcode 时选择是否添加 `-msimd128`。因此 SIMD 有真实的本地编译路径，不只是依据新版本文档推断。官方[Math 库 Wasm SIMD 支持](https://docs.unity.cn/cn/tuanjiemanual/Manual/SIMD-support.html)也介绍 SIMD 构建和代码分包要求；不过该页版本较新，其引擎 Math/Skinning 案例不能直接当作本项目避让收益。必须检查候选的热循环是否生成有效向量指令，再测手机。

SIMD 优先级高于立即改写 GPU 模拟：先独立开启构建能力并保留相同源码和兼容后处理做对照；若热循环未自动向量化，再试批处理／显式向量化。当前随机读取邻居、分支、同时写 i/j 的分离力与预算，均会影响向量化；仅打开开关或使用 float4 不能预先承诺加速。后处理若输入包含 SIMD 指令，wasm-opt 及校验工具也必须正确接受对应特性，不能跳过校验或将启动失败误归因于算法。

多核属于独立验证路线。当前 `CrowdSimulation` 将 colored Jobs 限定为桌面路径，手机实际执行 Burst single thread；构建助手也关闭了渲染线程选项。SDK 将多线程选项与 `PlayerSettings` 的 threadsSupport 关联，但[官方线程 API 文档](https://docs.unity.cn/cn/tuanjiemanual/ScriptReference/PlayerSettings.WeixinMiniGame-threadsSupport.html)说明了原生线程和 C# 多线程限制，不能等同于 C# Task 可并行，或断言现有 Burst Jobs 已有可用 worker。需先在真机验证原生 worker、Job 的并行执行与总时间，再考虑放开平台限制；现有 9 色依赖调度也有同步成本，不是线性核数加速。

若平台允许并行或向量化收益受共享写限制，可以试每单位独立查询并只累加自己的避让力，减少对其他单位数组的写入与同步。该方案可能重复计算一部分邻居，且改变有限预算下的配对与轨迹，必须用既有质量指标验证。另可研究邻居列表复用／空间查询失效规则、活动格遍历，以及 CPU 格级密度压力近似；缓存不能只看本单位是否移动，还要处理周围单位移动、出生死亡和跨格失效。

后续建议调整为先做 CPU 专项能力验证：SIMD 对照优先，原生 worker / Jobs 能力单独探测，再根据结果选择向量化或避让写入模型改造；自适应更新和密度场属于有行为取舍的算法候选。已有坐标打包／NoAlias 探索收益较小或不明确，不再据此宣称缓存布局可以轻松大幅提速。主目标仍是避让约 11.4 ms 和决策约 4.5 ms，而不是仅约 0.44 ms 的 Commit。以上尚无新真机候选性能结果。
