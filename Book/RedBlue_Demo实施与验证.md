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
