# 微信小游戏 Unity Profiler 流水线

本流程使用标准 Unity Profiler。手机运行 Development 小游戏，通过局域网主动连接电脑编辑器；电脑保存原始 `.data`，同时输出热点 JSON、Markdown 和采集元数据。普通游戏场景也能使用，不依赖红蓝 Demo。

## 第一次使用

1. 手机和电脑连接同一局域网，先停下 Unity Editor 的 Play。切换构建目标为 MiniGame。
2. 打开 `ET/WeChat/Profiler Pipeline`，指定独立导出目录，点击 `Build Development + Autoconnect`。工具完成 ET 编译、匹配的 HybridCLR AOT 生成、YooAsset 构建、微信导出和资源发布。状态文件在仓库 `Temp/wechat-build-status.txt`。
3. 执行下方 `Start-ProfilerSession.ps1`，为新包启动独立资源服务并同步 `game.js` 的 SDK CDN 地址；确认返回的资源版本匹配。然后在微信开发者工具中导入返回的 `minigame` 目录。普通应用自行使用的热更新/网络服务器地址仍需在项目配置中设置。
4. 点击 `Open Unity Profiler`，保持窗口打开。手机微信扫码预览，进入游戏。游戏会主动连接构建时写入的电脑地址；在 Profiler 目标下拉框选择手机 MiniGame Player。
5. 复现目标场景，预热后，在流水线窗口填写场景名、手机型号、微信版本和测试条件，选择本次导出的 `profiler-build.json`，点击 `Capture connected Player`。
6. 默认录制 5 秒，结束后自动保存到 `Temp/WeChatProfiler/<UTC时间>-<场景名>/`。窗口日志会显示路径。

手机尚未连接时，可以点击 `Wait for Player / scenario, then capture`，先让电脑等待，再扫码进入游戏。默认等待最多 180 秒；只有收到连续 3 个新远端帧才开始正式采集，不采用旧缓冲或 Editor 帧。可在 `Required CPU marker` 中填写场景标记：红蓝仅模拟为 `RedBlue.Frame.SimulationOnly`，完整战斗为 `RedBlue.Frame.Full`，仅渲染为 `RedBlue.Frame.RenderOnly`。要求标记时，等待阶段会过滤其他场景；正式采集时若标记消失，记录标为部分采集。可随时取消等待。此入口只需要更新电脑侧 Editor 脚本，无需重新导出手机包。

启动本次调试包资源服务：

```powershell
& Tools/WeChat/Start-ProfilerSession.ps1 `
  -ExportRoot Unity/BuildArtifacts/WeChat-Profiler-20261020 `
  -HostAddress 192.168.1.8 -ResourcePort 8081
```

脚本绑定指定局域网 IP，资源根目录限定为此包的 `webgl`；使用独立端口避免误加载上次发布包的 AOT 和资源。返回 PID、资源 URL、资源版本与导入目录，并保存 `profiler-session.json`。该文件和可选的 `profiler-route.json` 应与构建清单一起归档。端口已有服务时拒绝覆盖；关闭服务使用返回的 PID 执行 `Stop-Process -Id <PID>`。

产物：`capture.data` 是可用 Unity Profiler Load 打开的标准记录；`capture.json` 记录连接目标、有效性、接收帧数和环形缓冲截断情况；`profiler-build.json` 标识构建；`report.json` 保存逐帧 CPU 时间和各线程的 Marker 汇总；`report.md` 列出热点。

采集器拒绝 Editor 目标，开始后最多等 15 秒接收远端帧。断线、切换目标、连续 3 秒没有后续帧、手动提前停止、窗口关闭时标记为部分采集，不能当完整结果验收。开始采集会清空当前 Profiler 缓冲，需要的旧数据先另存。采集期间自动关闭 Deep Profile，启用 CPU、Rendering、Memory；结束后恢复原模块与录制设置。GPU 默认未启用，GPU 时间缺失表示不可用，不能算作 0。

## 连接不上时

先运行本地诊断：

```powershell
& Tools/WeChat/Test-ProfilerConnection.ps1 `
  -ExportRoot Unity/BuildArtifacts/WeChat-Profiler-20261020 `
  -HostAddress 192.168.1.8
```

诊断列出当前电脑 IP、Editor 监听端口、引擎 websockify 桥接进程及其 WebSocket 监听端口、构建配置和框架中的 WebSocket 地址。它只能检查电脑，不能证明手机已连通。确认连接成功的依据是手机 Player 出现在 Unity Profiler，且远端帧持续到达。引擎 websockify 的 WebSocket 入口和 Editor 原生 TCP 入口是两层，手机连接前者，不应把原生 TCP 端口填给小游戏。

优先检查：手机与电脑同网段、Wi-Fi 没有设备隔离；Windows 防火墙允许编辑器在当前局域网接收连接；CDN 资源服务可从手机访问；开发者工具真机调试的网络设置允许当前开发用局域网地址。不要把同一个 HTTP 资源端口当成 Profiler 端口。

VPN、VMware 等虚拟网卡可能让构建选择错误的电脑地址。确定框架实际使用的 Profiler 原端口和当前编辑器 Profiler 的监听端口后，可在独立 Development 导出目录上执行：

```powershell
& Tools/WeChat/Route-ProfilerConnection.ps1 `
  -ExportRoot Unity/BuildArtifacts/WeChat-Profiler-20261020 `
  -HostAddress 192.168.1.8 -OriginalPort <构建内的Profiler端口> -Port <当前Editor的Profiler端口>
```

脚本仅匹配指定原端口的框架 WebSocket，生成 `.profiler-original` 备份、路由清单和哈希；控制台输出 `[UnityProfiler]` 连接日志。端口不要照抄其他引擎版本或 UPR Desktop 的端口。重路由前恢复备份或重新导出。若 SDK 框架结构不匹配，脚本拒绝修改。重新预览后验证远端帧。

默认流程使用引擎标准生成的 Development Wasm。2026-10-22 用户要求对优化候选增加 `-Oz --strip-debug` 兼容处理，执行记录见本文末尾；采集清单必须记录这项后处理，不能与未处理包直接归因比较。此前发布型兼容包不能直接补勾选选项变成 Profiler 包。如果调试包模拟器启动等待，仍应区分模拟器与手机实测；不能把模拟器结果当手机性能。

## 离线分析与优化对比

在窗口停止录制后，使用 `Analyze existing .data` 选择任意标准 Unity Profiler 文件。不需要启动游戏；加载会替换 Profiler 当前缓冲。也可通过 Unity MCP 执行 `Tools/WeChat/AnalyzeProfiler.csx`，先设置 `AppDomain` 数据 `WeChat.Profiler.Input` 为文件绝对路径。

优化前后分别采样，保持同一手机、微信版本、屏幕分辨率、镜头、人数、阵型、倍速、伤害开关、运行后端与预热时间；不要把不同构建类型直接比较。生成差异报告：

```powershell
python Tools/WeChat/Compare-ProfilerReports.py `
  Temp/WeChatProfiler/<baseline>/report.json `
  Temp/WeChatProfiler/<candidate>/report.json `
  --output Temp/WeChatProfiler/comparison.md
```

报告提供各线程 mean/P50/P95/P99/max 和 Marker self 时间差异。CPU 帧包含等待；嵌套 Marker 的 inclusive 时间重叠，不能相加。采集器会按 60 FPS 估算临时增大 Frame Count，最多 2000 帧；原始记录和分析写完后恢复原设置。高于 60 FPS 的长录制或其他缓冲限制仍可能导致截断，需增大 Frame Count 或缩短单次采集。报告保留帧范围，采集清单显式标记截断和实际捕获容量；不能把保留的最后几秒说成整个长测。

Development 和 Profiler 会影响性能。先用调试包定位热点，再用独立发布包复测真实 FPS/P95、模拟推进速度和持续运行表现。常规 GC 分配调用栈可在 Unity Profiler 自身的 Call Stacks 设置中按需开启；第一轮关闭 Deep Profile，避免给 5 万人场景额外加入大量干扰。

## 红蓝对抗的固定回归场景

手机底部新增三个按钮：`50k Islands full`、`50k Islands sim`、`50k Islands draw`。它们设置 50,000 人、第二阵型（零基编号 1）、种子 7241、1 倍速、默认镜头、关闭伤害和循环。这样人数保持固定，便于测量持续压力。仍可用原来的 Damage 按钮打开伤害，采集正常战斗作为独立场景；不要混用两种结果。

`draw` 模式冻结初始状态，主要用于测静态渲染基线，不包含动态状态纹理每步上传；它与完整战斗的差值包含模拟和动态上传，不能全部归因于模拟。`sim` 模式保留场景其他相机与 UI，只取消人群绘制，属于此工程内的对照模式。

推荐先采 `islands-50k-full`，再采 `islands-50k-sim`、`islands-50k-draw`。每组重置后预热相同时间。当前阵型代码是每方 6 组；以场景日志和当前构建为准，不用口头的组数代替场景编号。

CPU Timeline 中可查：`RedBlue.Frame.*`、`RedBlue.Step`、`RedBlue.FlowField`、Burst 后端的 `RedBlue.Grid` / `Separation` / `Decision` / `Commit`、`RedBlue.DrawSubmit`、`RedBlue.StateUpload`、`RedBlue.PackState`、`RedBlue.CountVisible`、`RedBlue.StatsUI`。渲染提交耗时是 CPU 提交时间，不能当作 GPU 时间。界面同时显示 Burst 实际编译验证标记；后端名称本身不足以证明 Burst 已实际执行。

## 自动化入口与维护

`Tools/RedBlue/ExportWeChat.csx` 现在调用公共构建器。设置 `RedBlue.Export.Profile=true` 会强制完整准备和 Development + Autoconnect；`RedBlue.Export.RequireDemo=false` 可构建原应用。普通发布默认仍使用原脚本的发布模式。若首次完整导出更新了裁剪后的 AOT，构建器自动复制最终 AOT、重建资源并额外导出一次；再次失败时保留失败信息，不无限重试。构建器在 finally 重新读取 SDK 配置资源，再恢复 SDK 设置、Development、Autoconnect、Deep Profiling Support 和 IL2CPP 代码生成选项。HybridCLR / YooAsset 产物属于最后一次构建模式；切回发布包时应重新完整准备，不复用调试包依赖。

`Tools/WeChat/Test-ProfilerTools.ps1` 验证端口定向、其他 WebSocket 保留、备份和重复修改保护。模拟回归继续使用 `dotnet run --project Tools/RedBlue/RedBlue.Tests.csproj`。

官方参考：[Unity 目标平台 Profiler 与 WebGL 主动连接方式](https://docs.unity.cn/Documentation/Manual/profiler-profiling-applications.html)、[团结微信小游戏真机性能工具连接说明](https://uos.unity.cn/docs/upr/minigame-upr.html)。后者的 UPR 服务地址不能直接当成当前 Unity Editor 的地址。

## 2026-10-20 本机交付与验证

调试包已成功导出，最终预检通过。微信开发者工具导入 `Unity/BuildArtifacts/WeChat-Profiler-20261020/minigame`；构建 ID 为 `20261020T150608Z`，资源版本 `2026-10-20-2305`。当前资源服务 `http://192.168.1.8:8081`，PID `43652`；Profiler WebSocket 入口 `192.168.1.8:37100`。构建清单记录 SDK 原始 CDN，实际会话的 `8081` 地址及框架路由分别记录在导出目录的 `profiler-session.json` 和 `profiler-route.json`。这两个端口用途不同。

验证完成：Development + Autoconnect 包生成；最终 AOT 和资源预检；40 个 HTTP 文件（StreamingAssets 与启动 data）逐文件 SHA-256 一致；框架 JS 语法；端口路由隔离与备份；300 帧既有标准 `.data` 离线分析和同源零差异报告；Editor 目标拒绝检查；独立 .NET 模拟逻辑的确定性、范围、伤害与零分配回归。Editor 内另行检查到 FlowField / Step / Grid / Separation / Decision / Commit 标记，并确认该次桌面 Burst 编译标志为 true。构建完成后 SDK 与 Player 设置已恢复。

初次交付时尚未验证手机连接；后续手机采样和发现记录见下节。持续运行温升、发布包真实 FPS/P95 和优化收益仍需要单独实测。

验证脚本可重复运行：

```powershell
python Tools/WeChat/Verify-ProfilerExport.py `
  Unity/BuildArtifacts/WeChat-Profiler-20261020 `
  --output Temp/WeChatProfiler/export-http-validation.json
```

原始本地验证文件保存在 `Temp/WeChatProfiler/`，摘要在 `Tools/WeChat/Results/profiler-pipeline-20261020.json`。导出包与 `Temp` 都是本地构建产物，清理之前应归档。

## 2026-10-20 真机采集与 Burst 开关排查

手机已成功连接并完成两组标准 Profiler 采集：

| 组别 | 原始记录目录（仓库相对路径） | 接收帧数 | CPU 帧均值 | Step 合计/帧 | Step 次数/帧 |
| --- | --- | ---: | ---: | ---: | ---: |
| 完整战斗 | `Temp/WeChatProfiler/20261020T151803230Z-phone-islands-50k-full-first` | 5 | 1174.00 ms | 1161.20 ms | 4 |
| 仅模拟 | `Temp/WeChatProfiler/20261020T152824080Z-phone-islands-50k-sim-first` | 7 | 1487.43 ms | 1477.43 ms | 4 |

这两组样本较少，而且进入阵型后的战斗时长没有统一，只用于定位主要计算开销。不能将两组差值称作性能退化，也不能作为之前发布包的实际 FPS。当前 Development 包和之前经过兼容处理的发布型包构建条件不同。仅模拟组通过 `RedBlue.Frame.SimulationOnly` 标记校验，采集全程没有场景标记不匹配；原始 `.data`、JSON、报告和对应会话配置均已保存。

手机 Player 日志明确报告两组 `simulation=Managed reference`，屏幕 `2835x1316`，GPU `Maleoon 920`。Unity 返回的设备型号为 `wx 0.0`，不能据此推断具体手机型号或系统版本。两组都由 Step 模拟计算占绝大多数 CPU 时间；目前没有证据将主要开销归因于人群渲染提交。

检查本机安装的 `com.unity.burst@1.8.30-t2` 包源码，MiniGame 平台默认 `EnableBurstCompilation=false`；通过编辑器读取当前平台设置也确认为 false。现已创建 `ProjectSettings/BurstAotSettings_MiniGame.json`，启用 Burst 编译及优化，保留安全检查和额外调试关闭、`StripBurstManagedMethod=false`。仍沿用单线程小游戏路径，不额外打开 worker、渲染线程或 SIMD。

独立候选目标为 `Unity/BuildArtifacts/RedBlue-WeChat-Burst-20261020`。候选继续使用 Development + Autoconnect，避免把发布模式变化和 Burst 开关混在一起；构建已生成之前没有的 `lib_burst_generated.a`。原生库生成只证明编译产物存在，实际手机运行仍需核实后端和 `verified` 标记，再采同条件数据并以发布包完成流畅度复测。不能仅凭构建开关或原生库宣称手机优化成功。

候选已完成导出和最终预检，构建 ID `20261020T154440Z`；40 个 HTTP 文件哈希核对通过。开发者工具导入其 `minigame` 目录，项目显示名 `RedBlue-WeChat-Burst`，独立资源服务 `http://192.168.1.8:8082`（PID `38916`）。原生库与配置快照归档在候选根目录 `profiler-build-evidence`，构建清单补充了 Burst 配置与原生库哈希，验证摘要在 `Tools/WeChat/Results/burst-candidate-20261020.json`。手机第一轮仍选择 `50k Islands sim`，预期看到 `Burst single thread (verified True)`；尚未确认手机实际后端及收益。

## 2026-10-22 重启后真机 Burst 验证

资源服务重启后手机连接成功，链路为 `192.168.1.7 → 192.168.1.8:37100 → Editor 34999`；资源服务为 8082。手机日志报告 `Burst single thread`，Profiler 记录 `BurstCrowdKernel:StageJob (Burst)`，现已确认手机实际执行 Burst 编译代码。

采集目录 `Temp/WeChatProfiler/20261022T131014859Z-phone-burst-islands-50k-sim`：10 秒、398 帧、283 次模拟更新，valid=true、无场景不匹配、无环形缓冲截断。主线程平均 23.73 ms，P95 32 ms，最长 41 ms。每次模拟更新约 19.86 ms，其中 Separation 12.91 ms（65%）、Decision 4.67 ms（24%）、Grid 1.73 ms、Commit 0.41 ms。下一步优先测量密集格内候选访问和分离计算；现有分离与决策分别已有 48 / 24 次候选预算，不应按无上限遍历解释。

详细解释及限制见该目录 `analysis.md`；摘要保存在 `Tools/WeChat/Results/phone-burst-20261022.json`，手机场景日志归档为 `scenario-console.json`。旧 managed 数据每次 Step 约 369 ms，当前下降明显，但战斗年龄和采样数量不同，不作为严格加速倍率验收。本组仅模拟且含 Development/Profiler 开销，仍需完整战斗和发行版复测；GPU 时间尚不可用。

## 基于 sim 数据的第一轮源码优化

`BurstCrowdKernel.Buffers.Separate` 增加空目标格提前返回，缓存格范围与直径平方，并将当前单位的坐标、预算、力累加值保持在局部变量中。目标单位 j 与当前单位 i 不相同（同格从 p+1 开始、不同格 ID 不重叠），因此延后回写 i 不改变其他单位对它的访问。保留配对顺序、48 次预算、预算消耗规则、距离公式和双方等反向力；没有降低模拟频率或单位数量。并行路径仍沿用格着色避免写区域重叠。

通过 `Tools/RedBlue/SimOptimizationChecks.csx` 分别运行原版和候选：四阵型 × 伤害开关 × 两种 Burst 执行路径共 16 个回放，以及三轮 50k Islands（固定种子 7241、无伤害、预热 300 tick、测量 tick 301..600）。全部 19 个终点的所有模拟数组 SHA-256 与原版完全一致，包含位置、速度、目标、HP、预算、分离力和空间索引。此验证覆盖这些案例，不等价于任意输入的形式化证明。Editor 脚本编译无错误。

桌面 i9-14900HX / Editor Burst 三轮阶段均值的中位数：Separation 原版 4.906 ms、候选 4.486 ms，减少约 8.6%。其他阶段未修改，没有宣称其收益。首次原版运行其他阶段也明显更慢，作为环境波动样本保留在 `Temp/RedBlueSimOptimization/baseline-first.json`，不用于收益计算；随后原版与候选交替复测。完整状态哈希、计时与源码哈希归档到 `Tools/WeChat/Results/sim-separation-optimization-20261022.json`。

手机当前导出包仍是优化前版本；本轮没有重新导出，也没有手机候选采样。桌面收益不能直接换算成手机 FPS。下一轮应独立导出候选，在相同 tick 区间对比手机 Separation/Step，并补采 full。更大幅度的后续优化可检查有效格遍历、邻居数据局部性和密集格候选命中率；重排遍历或减少候选会影响模拟结果，需要单独验证，不能视作本次等价改动。

## 优化候选工程导出与监听（2026-10-22）

后续已导出独立工程 `Unity/BuildArtifacts/RedBlue-WeChat-SimOpt-20261022/minigame`，开发者工具名称 `RedBlue-WeChat-SimOpt`，构建 ID `20261022T133810Z`，资源版本 `2026-10-22-2130`。Development + Autoconnect、Burst 开启、Deep Profile 关闭。原生库与受测源码快照、桌面验证和 HTTP 验证存于导出根目录 `profiler-build-evidence/`。候选 kernel 源码 SHA-256 与桌面受测版本一致。

资源服务 `http://192.168.1.8:8083` 已启动，40 个 HTTP 文件 SHA-256 与本地一致，框架 JS 语法检查通过；Profiler WebSocket 37100 和 Editor TCP 34999 均监听。通过官方 CLI 已打开新工程。Unity Profiler Pipeline 已等待 `RedBlue.Frame.SimulationOnly`，等待上限 30 分钟；收到连续 3 帧该标记后自动采集 10 秒，标签 `phone-simopt-islands-50k-sim`。以上是本地就绪验证，候选手机启动及采样仍待扫码确认，不能把已监听称作已连接候选手机。

本轮会话摘要在 `Tools/WeChat/Results/simopt-export-20261022.json`；重启资源服务可执行：

```powershell
.\Tools\WeChat\Start-ProfilerSession.ps1 -ExportRoot Unity/BuildArtifacts/RedBlue-WeChat-SimOpt-20261022 -HostAddress 192.168.1.8 -ResourcePort 8083
```

再次采集需在 Pipeline 窗口重新点击等待按钮；等待超时也需重新开启。构建可能修改 SDK slim 备份，本轮已将生成副本归档并恢复该原本干净的受控备份文件。

## 用户要求的 Wasm 兼容处理（2026-10-22）

优化候选加载后出现空场景，用户要求执行旧文档中的“skip debug”，实际命令为 `wasm-opt -Oz --strip-debug`。当前 Burst Wasm 包含 sign extension、nontrapping float-to-int、bulk memory 指令，需向优化器声明这些已有特性；旧命令直接执行会在输入校验阶段拒绝。实际成功命令：

```powershell
& 'D:\Program Files\TuanJieEdit\2022.3.62t11\Editor\Data\PlaybackEngines\WeixinMiniGameSupport\BuildTools\Emscripten\binaryen\bin\wasm-opt.exe' `
  'E:\github\ET\YIUI\Unity\BuildArtifacts\RedBlue-WeChat-SimOpt-20261022\webgl\Build\webgl.wasm' `
  -Oz --strip-debug --enable-sign-ext --enable-nontrapping-float-to-int --enable-bulk-memory `
  -o 'E:\github\ET\YIUI\Temp\RedBlueSimOptCompatibility\optimized.wasm'
```

接着用 Node 的 Brotli 压缩并同步分包与配置（本次已执行；脚本拒绝重复处理同一已修改导出）：

```powershell
& 'D:\Program Files\TuanJieEdit\2022.3.62t11\Editor\Data\PlaybackEngines\WeixinMiniGameSupport\BuildTools\Emscripten\node\node.exe' `
  Tools/WeChat/Apply-WasmCompatibility.cjs Unity/BuildArtifacts/RedBlue-WeChat-SimOpt-20261022 Temp/RedBlueSimOptCompatibility/optimized.wasm
```

原始 Wasm 131,901,241 字节，优化后 65,632,370 字节，Brotli 后 12,104,005 字节；新 `CODE_FILE_MD5=407387d0c6bf5492`。WebAssembly.validate 通过，name 段已移除，759 个导出名称及种类保留；优化器删除了一个不再需要的 `env._mmap_js` 导入（1035 → 1034），没有新增导入。额外 JS import object 属性不影响模块实例化；这些检查不替代完整手机启动。压缩往返字节一致，40 个 HTTP 文件核对通过。

原 `.wasm` 保留在原始 `webgl/Build`；原压缩代码、game.js 和构建清单保存在 `profiler-backups/wasm-compatibility`（不打进 minigame）。新清单 `wasmCompatibility` 记录完整哈希与命令，摘要为 `Tools/WeChat/Results/simopt-compatibility-20261022.json`。资源服务继续使用 8083；已重新开启 30 分钟等待，采集标签改为 `phone-simopt-compat-islands-50k-sim`，10 秒、Deep Profile 关闭。

还修复了一次桥接目标连接失败：重启 SDK websockify 时将目标从 localhost 改为显式 `127.0.0.1:34999`，监听端口仍为 37100；本机 WebSocket 验证收到有效 PlayerConnection magic `0x67A54E8F`。日志位于 `Temp/WeChatProfiler/bridge-ipv4/`。这不证明手机启动已恢复。用户需重新编译／预览生成新二维码，手机复测进入场景及 Profiler 实际采集；手机结果尚未确认。`-Oz` 也改变代码优化，后续评估算法收益需让基线与候选采用同样的后处理。

## 兼容候选真机采集完成（2026-10-22 22:00）

手机重新进入后已完成 10.030 秒 / 450 帧采集，目录 `Temp/WeChatProfiler/20261022T140029397Z-phone-simopt-compat-islands-50k-sim`。场景标记正确、无缓冲截断；日志确认 50k Islands sim 与 Burst single thread，采样含 StageJob (Burst)。兼容候选现在已有真实手机运行与采集证据。

窗口共 300 次 Step，约 29.91 Hz。主线程均值 22.25 ms，P95 30 ms，最长 42 ms；Step 18.21 ms/次，避让 11.40 ms（62.6%），决策 4.51 ms（24.8%），Grid 1.76 ms，Commit 0.44 ms。相对上次未后处理的 Burst 包，Step 观测减少 8.3%、避让减少 11.7%。因后处理和战斗年龄不同，这不是隔离算法改动后的严格收益验收。最长帧没有改善，不能宣称所有卡顿已解决。

详细解释见采集目录 `analysis.md`；结构化结果与对比在 `Tools/WeChat/Results/phone-simopt-compat-20261022.json`。下一步应先统一基线和候选后处理、固定 tick 区间，再继续测量密集格候选访问；full 和发行型复测仍待完成。

## CPU SIMD / Jobs 固定 tick 对照（2026-10-22）

新增 `CPU compare` 按钮。固定 50,000 单位、Islands、种子 7241、无伤害；每轮先预热 300 tick，再测量 300 tick，每个显示帧推进一次模拟。每种执行方式重复三轮，输出 Step 均值/P95/最大值、各阶段均值和终点状态 SHA-256。它独立于正常 sim 的时间累积器，解决采样时战斗年龄不同的问题；耗时是每次模拟更新，不等于 full 模式 FPS。

对照包使用相同源码与 Wasm 后处理：scalar（SIMD 关、线程关）、simd（SIMD 开、线程关）、threads（SIMD 关、线程开）。构建 helper 临时修改 MiniGame 设置，导出后恢复原设置；生成的 Resources 配置携带构建 ID、实验开关与回传地址。不要使用最终目录名不含 `Final` 的早期试验包。

线程包在真实存在 Jobs worker 时依次执行原单线程、着色单线程、着色 Jobs 三组。后两组的遍历顺序相同，终点哈希应一致；应比较这两组才能隔离并行收益。原单线程与着色路径的配对顺序不同，不能将其差异全部归因于线程。若平台没有 worker，界面和结果会保留实际执行情况，不伪装成多线程。

除了调度探针，还在真正的避让 Job 中记录运行过的线程索引；只有 `separationWorkersObserved=true` 才说明避让实际使用工作线程。静态 `burst-vectorization.json` 记录当前生成库内的 SIMD 指令；仅归档中出现 SIMD 不证明避让热点已向量化。每次最终导出前先用 `Archive-BurstExperimentCache.ps1` 归档旧 Burst 缓存，避免历史对象干扰判断。

电脑验证记录在 `Tools/WeChat/Results/cpu-editor-matched-control-20261022.json`：原单线程 6.331 ms，着色单线程 6.706 ms，着色 Jobs 3.060 ms；后两组状态哈希一致，Jobs 避让记录线程 0..31。这是单轮 Editor 验证，不能代表手机收益。

最终干净缓存 SIMD 导出的静态检查：归档有 2624 条向量指令，但映射到当前三个 RedBlue Job 入口和五个 kernel 辅助函数的向量指令均为 0（包括 Separate/SeparateAll、Decide、BuildGrid）。scalar 对应函数也为 0。Burst 包源码确认 SIMD 开关向 clang 传入 `-O3 -msimd128`；开关并没有使现有这些热点自动向量化。不能据其他对象含 SIMD 宣称避让已经 SIMD 加速。要取得直接的向量计算收益，需要另行改写热点并重新验证；本轮先保留开关 A/B 和 Jobs 实测。

团结 1.9.3 的线程构建需要 `PlayerSettings.SetRenderThread(NamedBuildTarget.WeixinMiniGame, true)`。实测仅设置旧 `MiniGame.threadsSupport=true` 时，`BuildPipeline.IsMiniGameBuildThreadedFromPlayerSettings()` 仍为 false，SDK 会因缺少 `webgl.worker.js` 导出失败。helper 现在同步新旧开关、校验实际设置，并在 finally 恢复；有效线程包目录为 `RedBlue-CPU-Jobs-Native-Final-20261022`，不要使用失败的 `RedBlue-CPU-Jobs-Final-20261022`。编译路径包含 `_wasm_mt`；这仍不替代真机工作线程证据。Profiler 路由脚本同时处理根 framework 与 `workers/response` 副本，分别备份和记录 SHA-256，已验证路由行为、JS 语法与重复修改保护。

手机操作：在对应最终工程中重新预览并扫码，点击 `CPU compare`，等待界面显示完成。scalar/simd 各三轮；支持 worker 的 threads 包共九轮。测试期间保持同一手机、温度、画面尺寸和 Profiler 状态，避免退到后台。每轮 JSON 自动 POST 到该包资源服务器 `/benchmark`，保存在 `Temp/WeChatCpuExperiments/uploads`；上传失败仍在玩家日志保留 `REDBLUE_CPU_BENCHMARK:` 完整 JSON。

资源服务器现在兼容静态 GET 和基准 POST。重启时使用 `Start-ProfilerSession.ps1`，避免换成不支持回传的 `python -m http.server`。汇总命令：

```powershell
python Tools/WeChat/Compare-CpuExperiments.py Temp/WeChatCpuExperiments/uploads --output Tools/WeChat/Results/cpu-phone-comparison-20261022.json
```

只有每组 run 0/1/2 齐全、固定 tick 与 Burst 验证有效的数据可验收。手机当前仍待新包实测；电脑和静态指令结果不可填写为手机结果。标准 Profiler 采集可以作为补充，等待标记为 `RedBlue.CpuBenchmark.Measure.Single`、`.Measure.ColoredSingle` 或 `.Measure.Jobs`，单次建议 3 秒，以免跨入下一轮预热。

### 手机 scalar / SIMD 实测（2026-10-22 23:51）

已收到手机 192.168.1.7 的完整三轮回传，GPU Maleoon 920，小游戏上报 device 为 `wx 0.0`，分辨率 2835×1316，两组 `profilerEnabled=false`、Burst 验证有效。scalar 有一个只完成两轮的早期 session，保留但不计入验收；另一个完整 session 三轮 Step 均值的中位数 16.600 ms，避让 11.380 ms。SIMD 完整三轮 Step 16.513 ms，避让 11.177 ms；两组 Step P95 均为 18 ms。

SIMD 相对 scalar 的 Step 差异仅约 0.52%，本次没有足够证据认定有稳定收益。两组三轮内部以及跨组的状态哈希均为 `D258122B36EEC5041311061242223858B0A56B03BAEFE623E94569D71902983E`。未进行反向顺序复测与温度量化，不能将约 0.5% 差异解释为 SIMD 效果。

这批结果通过 `/benchmark` 自动回传，不是标准 Profiler `.data` 采样；Player 当时未进入 Editor 的有效远程采样。为匹配已完成的 scalar 条件，后续 CPU 对比关闭 Editor Profiler 录制，监听通道和资源服务保留。标准 Profiler 连接需在独立采样中继续核验，不能宣称已经捕获这批手机 Profiler 帧。

三个最终包均完成 Wasm 兼容处理、40 个 HTTP 文件 SHA-256 校验；线程包的根 framework 和 worker framework 已路由且 JS 语法通过。开发者工具启动线程包时出现 `Module.arguments has been replaced with plain arguments_`，手机启动与实际 Jobs 收益仍待确认，不能把包导出成功视作微信线程可用。结果汇总为 `Tools/WeChat/Results/cpu-experiments-20261022.json` 和 `cpu-phone-comparison-20261022.json`。

用户随后确认手机启动失败。已增加 `Add-StartupDiagnostics.cjs`，在 SDK 重试之前观察 UnityModule 的同步异常和 ready Promise 异常，原样抛出，不替换共享内存或绕过检查。`ProfilerHttpServer.py` 新增 `/diagnostic`，记录在 uploads/diagnostics 子目录，避免混入性能样本。观察器测试确认首次同步/异步异常均被记录且原异常继续传播；线程导出后处理已集成此步骤。

电脑模拟器自动重编译后收到 attempt 1：`ReferenceError: SharedArrayBuffer is not defined`，attempt 2 才是 `Module.arguments` 引起的 `WXUncaughtException`。这是电脑来源 192.168.1.8 的证据，手机来源 192.168.1.7 的首次异常仍待重新扫码回传。手机失败已确定，但不能据电脑日志直接断言手机必然是同一原因。

2026-10-23 00:04 将诊断升级为 V2：额外记录 framework-loaded、game-entry、SDK launch 阶段、module-invoked / ready / pending，以及 manager-error 和微信全局异常。仅上报平台、微信/基础库版本与共享内存 API 是否存在，不采集完整系统信息。单元执行验证原始同步/异步异常身份和正常 ready 返回值均保留。手机曾成功 GET 线程包数据，但 V1 没有手机异常回传，因此 V2 用于区分尚未进入 UnityModule 与初始化失败，不把“没有异常日志”等同于初始化成功。

Unity 中国发布的 1.9.0 公告称微信并行渲染试用需要 Android 微信 >=8.0.66、基础库 >=3.16.0，并联系微信开启白名单：[官方公告](https://juejin.cn/post/7642308292731568137)。这是 2026-05 的并行渲染说明，当前 AppID 是否具有共享内存/线程能力仍需实际诊断与平台确认，不能直接推断已经或没有开通。没有以普通 ArrayBuffer 冒充 SharedArrayBuffer，也没有把手机启动失败计为多线程性能样本。

## 新版开发者工具登录恢复与共享内存诊断（2026-10-24）

用户更新后的版本为 2.02.2609231 RC，基础库 3.17.4。已开启服务端口并完成 Codex 连接授权。新报错中的插件加载失败与登录票据过期有关；重新扫码登录后，插件 1.3.14 正常加载，default 导出为 function，UnityManager 构造错误消失。资源服务 8086 已恢复，不需要新增 CDN。

V4 启动观察器增加一次共享内存能力探测：全局 SharedArrayBuffer 为 undefined，wx.getSharedArrayBuffer() 和 wx.getAtomics() 均返回 undefined；小尺寸 shared WebAssembly.Memory 本身可以创建，buffer 的 tag 为 [object SharedArrayBuffer]，且不是 ArrayBuffer。底层能创建共享内存并不代表小游戏已具备完整的 worker 共享能力。本机基础库的公开 API 实现受 useSharedArrayBuffer 与 enableWebWorkerSAB 权限共同控制，当前结果说明该运行上下文未提供能力，不能凭此确定远端 AppID 的实际权限状态。

首次 UnityModule 异常仍在 framework:3056 的 SharedArrayBuffer 引用；SDK 重试后才出现 Module.arguments 二次异常。没有修改权限、提取隐藏构造函数、伪造 SAB 或吞掉异常。V4 通过同步异常、异步异常身份和正常 ready 值保留验证。当前尚未获得多线程运行与收益数据；需要向微信/团结技术支持确认该 AppID 的共享内存/WebWorker 开通情况及模拟器支持条件。此轮仅依据开发者工具，未要求手机复测。

取证：Tools/WeChat/Results/cpu-jobs-devtools-20261024.json；Console 快照：Temp/WeChatCpuExperiments/console-after-login-20261024.json。旧 cpu-jobs-startup-20261022.json 保留为历史记录。

### 官方文档复核及结论修正（2026-10-24）

本节修正此前将模拟器 SharedArrayBuffer 失败作为权限诊断主要依据的结论。微信[团结小游戏多线程说明](https://developers.weixin.qq.com/minigame/dev/guide/game-engine/unity-webgl-transform/Design/Multithread.html)明确：SDK 最低 v0.1.33；开发者工具暂不支持新版多线程，需真机预览。当前[Worker V2 文档](https://developers.weixin.qq.com/minigame/dev/guide/base-ability/new-worker.html)要求 Android 微信 >=8.0.74、基础库 >=3.17.0、开通灰度；iOS 标为“能力支持中”。因此此前 8.0.66/3.16.0 的旧公告条件不能作为当前验收条件。

当前团结 1.9.3；本地 SDK 源码更新记录已包含 v0.1.34（package.json 仍写 0.1.1，不能据此误判为旧 SDK）；基础库 3.17.4 满足最低要求。AppID wxf15ab5190a932da3 的灰度资格未核实，开发者工具登录成功不等于已开通灰度，模拟器失败也不证明 AppID 无权限。当前无可读取的已登录小游戏后台页面；手机实际微信版本未记录。官方能力判断为 wx.env.isSupportStandardWorker，wx.getSharedArrayBuffer/getAtomics 不是该文档指定的判断方式。灰度申请入口见 Worker V2 文档。

另外发现明确适配差异：当前导出 framework:3056 使用 buffer instanceof SharedArrayBuffer，而新 Worker 文档明确不要这样判断，应比较 buffer?.constructor?.name。这是后续真机适配需要修正的点；修正判定不意味着运行环境自动支持共享内存或 Jobs。本轮仅调查，不修改实验执行逻辑。

官方团结说明主要覆盖并行渲染/RHI 渲染；不能据此承诺我们的 Burst Jobs 避让能在工作线程并行。真机满足准入后仍需验收实际 worker 执行、状态哈希和耗时。正式发布按[多包融合说明](https://developers.weixin.qq.com/minigame/dev/guide/game-engine/unity-webgl-transform/Design/MultiPackage.html)保留单线程默认包，依据 wx.env.isSupportStandardWorker 选择多线程包，不单独发布 threads 包。新版 Worker 文档已列出多 worker 能力，旧手册的双线程描述不能当作当前统一上限。

完整结构化结论与官方文档快照位置：Tools/WeChat/Results/wechat-thread-support-review-20261024.json。

### Worker V2 类型判断修复与真机入口（2026-10-24）

新增 `Tools/WeChat/Apply-SharedBufferCompatibility.cjs`。仅修改当前 native threads Final 包：主线程、workers/response 及 webgl/Build framework 各 5 处 `instanceof SharedArrayBuffer`，改为官方推荐的 constructor.name 比较。实际使用显式空值保护 `buffer != null && buffer.constructor != null && buffer.constructor.name === 'SharedArrayBuffer'`：首次直接使用官方示例的 `?.` 时，开发者工具预览打包解析器返回 `SyntaxError: Unexpected token .`，因此回滚后改为等价旧语法。未修改 `typeof SharedArrayBuffer` 的 pthread 可用性检查，未提供构造函数或伪造能力。保留原始模拟逻辑和 Wasm，Build ID 仍为 `20261022T153657Z-threads`；本次 JS 后处理用独立清单及 SHA-256 区分。

脚本会预先检查三个文件的匹配数量与 JS 语法；重复运行校验当前哈希后直接返回；若后续文件被修改，拒绝覆盖。备份在导出目录 `profiler-backups/shared-buffer-compatibility`，记录为 `shared-buffer-compatibility.json`。回滚命令：

```powershell
node Tools/WeChat/Apply-SharedBufferCompatibility.cjs Unity/BuildArtifacts/RedBlue-CPU-Jobs-Native-Final-20261022 --rollback
```

V5 启动观察器增加 `standardWorkerSupport`（直接读取官方 `wx.env.isSupportStandardWorker`，缺失为 null）、字段类型、手机系统、微信与基础库版本。它仅记录，不据此更改实验执行模式或推断 AppID 灰度资格。`Finish-CpuExperiment.ps1` 已集成 V5 升级与 SAB 修复；若将来需再次升级观察器，应先回滚 SAB 修复，再升级观察器并重新应用，避免覆盖哈希保护。

`node --test Tools/WeChat/Test-SharedBufferCompatibility.cjs` 四项测试通过：无全局 SAB 时跨上下文真实共享内存判定、未知生成结构拒绝、精确回滚/重复执行/后续修改保护、诊断保留同步及异步原异常与正常返回值。实际导出三个 framework 均通过语法解析，15 处替换完成，40 个 HTTP 文件 SHA-256 校验通过。

电脑 WLAN 仍为 192.168.1.8；原 8086 监听已停止，本轮使用 `Start-ProfilerSession.ps1` 恢复，资源地址为 `http://192.168.1.8:8086`。V5 已收到 platform=devtools、standardWorkerSupport=false 的诊断，仅证明开发者工具中的回传有效；不能判定手机权限或 Jobs 支持。手机版本、真机能力与灰度资格仍待验证；没有新增手机性能结果或标准 Profiler 帧采样。

修复可选链解析问题后，官方 CLI `auto_preview` 返回 success=true，预览包总大小 14,180,157 字节（主包 1,807,552、wasmcode 12,372,592）。真机预览已生成并推送；下一步由 Android 手机打开本次预览，成功进入后点击 `CPU compare`。尚未收到本次手机结果，预览打包成功不等于真机启动或 Jobs 执行成功。

### 真机启动失败取证（2026-10-24 11:15）

已收到 192.168.1.7 的两次真实手机启动（10:52、11:15）：Android 12、微信 8.0.79、基础库 3.17.5，均达到版本门槛，但 `wx.env.isSupportStandardWorker=false`。这确认当前真机上下文没有官方 Worker V2 能力；不能仅据此断言 AppID 灰度申请被拒绝，灰度开通状态仍需微信侧核实。

插件 default 为 function、framework 已加载且 UnityModule 已调用；第一条上报错误变为 `Assertion failed: WebAssembly Module should have been loaded by now!`，随后 UnityModule 等待超过 10 秒。对应主 framework:3829 的 `PThread.loadWasmModuleToWorker`，条件为 `wasmModule instanceof WebAssembly.Module`。日志尚不能区分 module 缺失和跨上下文类型判定失败，不能直接将该断言的具体成因归结为灰度。全局 SAB 在手机存在，仍不等于新版 worker 可用；本次未再报告旧的 SAB ReferenceError。

当前 threads-only 实验包不适用于这个能力返回 false 的环境，应使用已有单线程 Final 包作为回退，并按官方入口核实 Worker V2 灰度；能力满足后继续验收 Wasm 模块传递、实例化及实际 Jobs worker。没有新增 CPU benchmark 或标准 Profiler 帧。结构化取证：`Tools/WeChat/Results/cpu-jobs-phone-startup-20261024.json`。

### 单线程回退预览尝试（2026-10-24 11:18）

按用户要求尝试手机运行已有 `RedBlue-CPU-Scalar-Final-20261022`（Build ID `20261022T151844Z-scalar`），恢复 192.168.1.8:8084 服务并安装 V5 启动观察器；JS 语法和 40 个 HTTP 文件哈希校验通过。未更改模拟源码或 Wasm。开发者工具登录有效，官方 `auto_preview` 两次返回 `Error: socket hang up (code 10)`，改用 `create_preview_qrcode` 同样失败。没有成功的新预览或手机启动回传，不能声称单线程已在本次手机运行。

电脑系统代理为 127.0.0.1:10709，监听正常；直接和通过代理访问 servicewechat.com 均能完成 HTTPS 请求（根路径返回 404），这不能证明微信预览上传接口正常，也不足以归因代理。未修改系统代理或登录状态。恢复测试需先解决微信预览上传网络错误，然后再次推送单线程 Final 包；8084 资源服务保持运行。

用户关闭代理后，首次预览明确报 `tunneling socket could not be established, cause=connect ECONNREFUSED 127.0.0.1:10709`；虽然工具将其分类为 APPID_ERROR，实际错误是代理端口拒绝连接。通过开发者工具设置 UI 将“使用系统代理”切换为“不使用任何代理，勾选后直连网络”（仅修改开发者工具，未修改 Windows 系统代理）。立即重试仍 socket hang up；随后显式打开 scalar Final 工程窗口（此前只有 threads 工程窗口），再次 `auto_preview` 成功，总包 13,543,391 字节。不能将恢复全部归因于代理开关或工程开窗中的某一个动作。

11:22:59 收到 scalar 的 platform=devtools、module-ready；这只是模拟器启动成功。新预览已推送到微信，等待手机打开后的 Android 回传，尚不能记为本次手机运行成功。

### 单线程手机恢复及 CPU compare 完成（2026-10-24 11:25）

手机 192.168.1.7 在 11:23:56 回传 platform=android、module-ready，随后成功加载场景资源。用户点击 CPU compare 后，收到 session `b658b4410060471abadf06e1a4217567` 的完整 run 0/1/2：Step 均值分别为 16.496667 / 16.240 / 16.370 ms，三轮中位数 16.370 ms；避让中位数 11.210 ms，Grid 1.660 ms、决策 3.146667 ms、Commit 0.403333 ms，Step P95 的三轮中位数 18 ms（各轮 19/18/18 ms）。同日另有一个仅 run 0 的 session，保留但不计入完整三轮验收。

固定条件为 50k Islands、种子 7241、预热/测量各 300 tick、无伤害；三轮 Burst 验证均通过，jobWorkers=0，实际避让仅线程索引 0。状态 SHA-256 三轮均为 `D258122B36EEC5041311061242223858B0A56B03BAEFE623E94569D71902983E`，与此前 scalar/SIMD 基线一致。本次 `profilerEnabled=true`，此前有效对照为 false，不能把耗时差异计为优化收益，也不能将该标志或 benchmark POST 称为已经采集标准 Profiler 帧。手机运行和 benchmark 回传已经恢复；Jobs 真机支持仍未验证。

汇总存入 `Tools/WeChat/Results/cpu-phone-comparison-20261024.json`，历史 20261022 对照不覆盖。当前主要 CPU 热点仍是避让（约占 Step 的 68.5%）；后续单线程优化以此为优先，并保持 Profiler 状态一致做 A/B。

### 单线程网格连续布局候选（2026-10-24）

新增显式实验模式 `BurstPackedSingleThread`：BuildGrid 按原有 ids 顺序收集存活单位的位置，并清零连续的力和预算数组；避让使用连续槽位访问，结束后写回原 ID 数组。保留原始网格遍历、配对顺序、48 次预算、开方及重合位置 ID 决胜规则。50k 额外常驻暂存为 850,000 字节（约 0.81 MiB）；Grid 包含 gather，Separation 包含 scatter，总 Step 包含全部成本。正常运行默认模式未切换。

`PackedSeparationChecks.csx` 的 4 阵型 × 2 种伤害设置 × 2 种种子共 16 例、1,920 tick 全状态比较通过，包含重合坐标和死亡槽位；50k/600 tick 三轮也保持同平台状态一致。Editor 三轮配对计时不稳定，完整 benchmark 流程检查的三轮候选甚至均慢于基线，因此尚无收益结论，不能默认开启。结构化记录见 `Tools/WeChat/Results/cpu-packed-editor-20261024.json`。

`PackedBenchmarkChecks.csx` 已验证实际六轮 AB / BA / AB 顺序、run ID、Burst、无避让 worker、Profiler 关闭与结束恢复。Editor 两种模式公开状态哈希一致，但与历史手机哈希不同；首次检查错误地要求跨平台哈希相等，现已改为同平台 A/B 一致性。CPU 对照不能假设 Editor 与 Wasm 浮点逐位相同。

新增构建 variant `packed-ab`，仅用于 SIMD 关、threads 关的独立包。每显示帧一次、预热/测量各 300 tick、三轮每模式；每次 Advance 在计时之前关闭 Profiler，结束或 Dispose 恢复初始状态。结果汇总增加 `packedComparisons`，只比较同 build/session，验收三轮齐全、相同设备/分辨率/构建/Profiler 条件、Burst 与状态一致且无实际避让 worker；百分比为负表示更快，但条件有效不等于收益稳定。缺轮、哈希差异、GPU 条件差异、Profiler 开启、实际 worker 执行的拒收检查均通过。

注意：用户称 Islands 为 8 组，当前实现实际使用 `group = k % 6`（每队 6 组），历史手机对照也基于此实现。本轮保持阵型代码，避免把场景变化混入优化收益；真正 8 组应另建场景基线。新候选手机性能仍待实测，本节没有新增标准 Profiler 帧。

候选已导出至 `Unity/BuildArtifacts/RedBlue-CPU-Packed-AB-Final-20261024`，Build ID `20261024T034830Z-packed-ab`，资源端口 8087；SIMD=false、threads=false。原有最终包未覆盖。已完成原接口保持的 Wasm 兼容处理、V5 启动观察器、JS 语法和 40 个资源 HTTP SHA-256 校验；Burst 静态产物同时含原避让与 packed 入口，热点向量指令数为 0。原始 Editor 验证 JSON 已复制至该导出目录 `profiler-build-evidence`。后续手机回传必须匹配此 Build ID 和同一 session；预览成功不等于手机测试完成。

12:06 官方 CLI `auto_preview` 返回 success=true，包总大小 14,162,214 字节，已推送手机预览；开发者工具回传 module-ready（peer=192.168.1.8）。此时尚无该新包 Android 启动或六轮 benchmark 回传，已提示用户在手机进入后点击 CPU compare。资源服务 8087 保持运行，尚不能报告手机优化收益。

### 新包空场景与跨构建资源缓存修复（2026-10-24 12:16）

用户报告手机空场景。8087 已收到 Android 的 module-ready，但引擎启动后没有新场景 bundle 请求；模拟器复现读到 SDK 缓存中的旧 `build_info`、`.version` 和 `2026-10-22-2325` 清单，随后反复请求 `http://127.0.0.1:8080/CDN/MiniGame/v1.0/8b0fe3cfe4cd642c113323126373608e.bundle` 失败。说明仅切换 DATA_CDN 端口不能隔离同 AppID、同相对路径的 StreamingAssets 缓存。此前 40 文件 HTTP 校验只验证服务端内容，不能覆盖 SDK 客户端缓存。

最终修复为 `Apply-ExperimentCacheGuard.cjs`：通过 SDK 公开 `streamingUrlPrefixPath` 将资源路径设为 `/__redblue__/<Build ID>/StreamingAssets/...`，且版本入口 `.version` 和 `build_info` 不再自动缓存。资源服务器只为其配置的 Build ID 映射 StreamingAssets，其他 Build ID 返回 404；HTTP 验证脚本现在校验 SDK 实际使用的前缀路径。后处理流水线已集成，Wasm 和模拟代码未改变。原文件及 SHA-256 保存在导出目录 `profiler-backups/resource-cache-isolation` 与 `resource-cache-isolation.json`；回滚使用同脚本 `--rollback`。

曾在模拟器尝试管理器创建后调用 cleanAllCache，再启动游戏，但 SDK 已记住即将被清除的数据包路径，导致启动异常。此实验已在手机推送前完整回滚；最终方案不删除缓存，也不改启动时序。不要把该中间实验的 Module.arguments 二次异常算作手机问题或线程问题。

三项 Node 检查通过（路径隔离、可变元数据缓存策略、精确回滚/重复保护），实际新路径的 40 文件哈希通过，错误 Build ID 的 HTTP 404 检查通过。模拟器确认 YooAsset 初始化版本为 `2026-10-24-1155`，截图中红蓝双方及 CPU compare 正常显示，旧资源下载失败不再出现；此时修复后手机启动仍待验证。结构化记录：`Tools/WeChat/Results/cpu-packed-empty-scene-20261024.json`。

### 网格连续布局首轮手机 A/B（2026-10-24 12:20）

修复版 auto_preview 成功（总包 14,162,324 字节）。手机 192.168.1.7 在 12:18 通过构建前缀请求新版本清单与场景 bundle，随后 session `a62bda0d5799414d92b12e586bb48cee` 在 12:18:41 至 12:19:48 回传完整六轮 AB / BA / AB，确认空场景修复后已进入 CPU 测试。Build ID 仍为 `20261024T034830Z-packed-ab`，Wasm/模拟源码未变。

基线 Step 三轮均值分别为 16.193333 / 18.926667 / 16.360000 ms；packed 为 15.266667 / 21.173333 / 15.276667 ms。三轮中位数基线 16.360 ms，packed 15.276667 ms，降低约 6.62%（1.083333 ms）；避让中位数 11.173333 → 9.750 ms，降低约 12.74%；Grid 包含 gather，增加 1.673333 → 1.943333 ms；Step P95 的三轮中位数 18 → 17 ms。阶段中位数分别计算，不应相加当作 Step 中位数。

所有六轮都为 50k Islands、种子 7241、无伤害、预热/测量各 300 tick，SIMD=false、threads=false、Profiler=false、Burst 验证通过，实际避让线程只有 0。GPU Maleoon 920、分辨率 2835×1316；所有状态哈希均与历史手机基线一致，为 `D258122B36EEC5041311061242223858B0A56B03BAEFE623E94569D71902983E`。同构建/同会话条件检查有效，但这不代表温度/调频稳定。

按 run 配对的 Step 变化为 -5.72% / +11.87% / -6.62%：中间 BA 对两种模式多个阶段均明显变慢，packed 甚至退化，因此没有删除该对，也不能宣布稳定收益或默认开启。当前结论为值得复测的候选；需手机短暂静置后在同包再跑一组，保持前台且不切换其他应用，再决定是否保留。报告为 `Tools/WeChat/Results/cpu-phone-packed-ab-20261024.json`，原始六份回传已归档到导出目录 `profiler-build-evidence/phone`。这是 benchmark 回传，不是标准 Profiler 帧。

### 网格连续布局第二次手机 A/B（2026-10-24 12:23）

用户静置后再次点击同包 CPU compare，session `49deddc496994c2d9ea11b911f6c87f3` 在 12:21:47 至 12:22:48 收齐六轮 AB / BA / AB。基线 Step 三轮均值为 16.333333 / 16.320000 / 16.333333 ms；packed 为 15.273333 / 15.350000 / 15.260000 ms。三个配对分别降低 6.49% / 5.94% / 6.57%，本次反向 BA 对也有收益。

三轮中位数：Step 16.333333 → 15.273333 ms，降低 6.49%，节省 1.06 ms；Separation 11.153333 → 9.730000 ms，降低 12.76%；Grid 1.646667 → 1.940000 ms，增加 0.293333 ms；Step P95 的三轮中位数 18 → 17 ms。总 Step 已计入 gather/scatter，阶段中位数不能直接相加。计时为 Development 固定 tick benchmark，不能换算为发布版本整帧 FPS。

同 build/session 条件、固定 tick、Burst、Profiler=false、SIMD=false、threads=false、jobWorkers=0、实际避让线程仅 0 的检查均通过，来源均为手机 192.168.1.7。两次共十二轮状态哈希完全一致，且与历史手机基线一致。两次中位数收益分别为 6.62% 和 6.49%，复测支持该手机、当前 50k Islands 实现下约 6.5% 的 Step 收益；第一组中间退化对完整保留，没有排除样本，也没有温度/频率观测来解释原因。

保留 packed 候选及显式开关，正常默认仍不切换：Editor 流程曾三对均慢，当前未覆盖其他手机、正常伤害场景或真正每队 8 组的 Islands。本次不再要求重复点击，后续优先减少 packed 收集/写回成本或进一步优化避让访问，并沿用同包 A/B。第二次原始六份回传及更新汇总归档至同一导出目录 `profiler-build-evidence`；这是 benchmark 证据，没有新增标准 Profiler 帧。

### 模拟器 50k Islands / 55 FPS 目标（2026-10-24）

用户暂时取走手机，授权独立优化并在微信开发者工具模拟器验证第二个 Formation、50,000 单位保持 55 FPS。沿用当前第二个 Islands 实现，不通过减少单位、停掉模拟或降低固定 tick 频率验收。先前手机的 Step A/B 不是整帧 FPS 证据。

已加入 `RedBlueFrameBenchmark` 和 `Frame compare`，在正常 Update/渲染循环下依次测试 full-baseline / full-packed / draw-only / sim-packed / battle-packed，每场景 30 秒、每 5 秒一份回传。前三个绘制场景中 full 对照保持无伤害 50k 存活；battle-packed 验证正常伤害。记录帧间隔均值/P95/最大值、实际 tick 频率、可见与存活数量、模拟/提交成本、构造耗时、Profiler 与焦点状态。结果独立 POST `/frame-benchmark`，保存 `uploads/frames`，不混入固定 tick CPU 对照；没有新增标准 Profiler 帧。

`Analyze-FrameBenchmarks.py` 保留所有不完整及失败数据，只在六个窗口齐全、同上下文、前台、Profiler 关、Burst 有效、每个窗口至少 55 FPS、模拟约 30 Hz 时通过完整战斗门槛；无伤害 full 场景还要求每窗存活/绘制均为 50k。五项验收检查通过，覆盖单个慢窗口、缺失/重复窗口、减人数/减频/后台/Profiler 开启及只渲染误验收。四阵型 managed 回归亦通过。

旧 Development 包前台截图初步约 32～35 FPS full、约 43～50 FPS draw-only；取样非受控窗口，且随后有并行构建负载，不能作为正式前后百分比。后台曾显示约 3 FPS，确认页面隐藏，不作为游戏瓶颈基线。正在建立独立 Release 对照包，关闭 Development 和 Profiler 自动连接，保持 SIMD/threads 关闭；资源/HTTP 验证脚本增加显式 Release 支持，原 Development 默认门禁保留。目标尚未验收，后续记录实际回传及优化结果。

用户随后明确允许模拟器关闭单位渲染，只看逻辑。当前优先门槛调整为 logic-only：六个窗口均 >=55 FPS、模拟保持既有约 30 Hz、存活数持续 50k、drawn=0、前台且 Profiler 关闭。分析器新增独立 `logicOnly55FpsPassed`，与 full 的门槛并列保留；通过 logic-only 不代表 full 战斗渲染达标。后续优化优先处理逻辑开局和持续运行。

### Release 基线实测与关闭场景相机（2026-10-24）

Release 基线 `RedBlue-Frame-Release-Baseline-20261024`（Build ID `20261024T043236Z-frame-baseline`，端口 8088）已完成 session `13f41b9f7d954015a907173eb3d7b9a9` 全部 30 个窗口。运行分辨率 2532×1170，ANGLE D3D11 / RTX 4060；所有窗口前台、Profiler 关，SIMD/threads 关。各场景平均 / 最差五秒窗口 FPS：full-baseline 28.29 / 24.66，full-packed 29.36 / 25.64，draw-only 49.77 / 49.32，sim-packed 30.23 / 28.09，battle-packed 37.98 / 31.78。需要模拟的场景实际均约 30 Hz，无伤害场景保持 50k 存活。未达到 55 FPS，不计为完成，也没有新增标准 Profiler 帧。

sim-packed 每帧模拟计算约 11～16 ms，提交近零，但仍约 30 FPS。代码检查发现 Simulation only 只跳过 CrowdRenderer.Draw，MainCamera / YIUICamera 等仍启用，因此不能把其余帧时间全归因于模拟。新增模式切换时保存并关闭启用相机、退出或销毁时恢复的逻辑，保留 ScreenSpaceOverlay 控件；回传增加 activeCameras，逻辑验收进一步要求为零。新套件顺序为 sim-baseline / sim-packed / sim-baseline-repeat / sim-packed-repeat / sim-battle-packed，各 30 秒。

packed 候选同时增加非空格子列表，以原有升序或降序遍历，保持配对顺序。16 例 / 1,920 tick 全状态检查通过，50k / 600 tick 全状态哈希与基线相同，见 `Temp/RedBlueActiveCellChecks`。额外常驻暂存为 1,050,004 字节。Editor 配对 Step 6.441 → 6.506 ms，没有收益，不能据此声称模拟器更快。候选独立 Release 包正在构建；正常通用默认行为保留，实验 variant 才启用 packed。

新增导出后观察器 `Add-FrameLoopDiagnostics.cjs`，只在原 Browser.mainLoop.runIter 周围测量引擎主循环工作与相邻调用间隔，每五秒通过本机 /diagnostic 回传。保持原调度、返回和异常，备份及前后 SHA-256 可回滚；它是诊断遥测，不是 Unity Profiler 采样。两项测试验证时间归因、原异常/调度保持与未知框架拒绝。

关闭全部相机的候选包已完成 30 个窗口，session `a87263794a204fbb9b9961a019c7134d`。无伤害 baseline / packed / baseline-repeat / packed-repeat 平均分别 29.50 / 30.53 / 28.99 / 30.83 FPS，最差窗口 26.99 / 26.34 / 25.15 / 27.29 FPS，所有窗口 activeCameras=0、存活 50k、模拟约 30 Hz。原始回传、观察器和状态检查均归档于导出目录 profiler-build-evidence/simulator。

该轮发现额外测量干扰：Release 中每帧设置 Profiler.enabled=false 会报“Profiler is not supported in this build”，因此此轮仅作为诊断证据，不作为干净性能基准。原 Release 基线也使用了相同测试器，应同样保留这一限制。已在源码中以 Debug.isDebugBuild 门控所有测试器 Profiler 读写，并仅在需要时关闭。关闭相机后没有 framebuffer 清屏，UI 曾叠字；新增仅逻辑模式启用的全屏不透明 Overlay 背景，保留零场景相机。

主循环观察器确认一个窗口引擎工作均值 17.50 ms、相邻迭代 34.90 ms；空闲较轻场景工作约 2.48 ms、间隔约 20.12 ms，method=rAF、timingMode=1、timingValue=1。支持检查模拟器 rAF 额外等待，但尚不能仅以相关性宣布已解决。候选后处理 `Apply-SimulatorDeadline.cjs` 仅在 platform=devtools 且 60Hz rAF 时切到生成代码已有的 Emscripten 补偿式 timer（目标 1000/60 ms，扣除已用时间）；手机及非 60Hz 模式不变，有备份、SHA-256 和回滚。两项测试验证实际 runner 保持、扣除工作时间、Android/iOS/30Hz 不变及未知结构拒绝。新 Release 包会重新生成匹配的 HybridCLR 依赖后再测；尚未验收 55 FPS。

### Release 依赖模式漂移修复（2026-10-24）

重新生成后仍发现 `MethodBridge.cpp` 的 DEVELOPMENT=1。原因不是 SDK package.json 版本或 Burst：`AssemblyTool.DoCompile()` 调用 RefreshBuildType，从 GlobalConfig.BuildType=Debug 将 EditorUserBuildSettings.development 改回 true，后续 HybridCLR GenerateAll 因而生成 Development 依赖；SDK 最终导出时才恢复 Release，引发桥接标记不一致。旧引擎 Release 回传不受影响地保留，但不能据此声称整个依赖流水线已统一为 Release。

新增 AssemblyTool 显式编译模式重载，保留无参数菜单行为；性能构建使用显式 Debug/Release，不修改用户 GlobalConfig。构建入口检查 ET 编译没有改写所请求的模式，并在 HybridCLR 生成后及 SDK 导出后核对 MethodBridge DEVELOPMENT 标记，不匹配即拒绝。归档增加编译/构建源码、实际实验 JSON 与 MethodBridge.cpp，清单记录实际桥接标记及是否重新生成 ET 依赖。

`RedBlue-Logic-Deadline-Final-20261024`（Build ID `20261024T051454Z-logic-deadline`）已导出，但发现上述模式混用，清单已标为 diagnostic-only；没有为它启动资源服务、打开模拟器或采集性能结果。修复后的新包使用 `RedBlue-Logic-Deadline-Release-Final-20261024`、资源端口 8091，单独构建，不覆盖历史包。该 variant 的 CPU compare 将同时测试 baseline/packed，Release 中不再操作不支持的 Profiler 开关。

Finish-CpuExperiment 增加显式 `-ObserveFrameLoop` / `-SimulatorDeadline` 开关。观察器先应用，调度补偿后应用；回滚按相反顺序。调度补丁三项检查通过，另验证真实文件重复执行、后续修改拒绝和字节级回滚。分析器可用 `--build-id` / `--session-id` 输出特定包/会话，保留未完成及失败窗口。

### 一致 Release 包的调度对照（2026-10-24）

`RedBlue-Logic-Deadline-Release-Final-20261024` 已完成构建，Build ID `20261024T053244Z-logic-deadline`，资源端口 8091，资源版本 `2026-10-24-1344`。实际引擎与 HybridCLR MethodBridge 均为 Release，ET 依赖重新生成，SIMD/threads 关；40 个实际 HTTP 资源校验通过。后续只修改导出的 JavaScript 调度与测试入口，没有重新编译 Wasm 或改写模拟。

原 rAF session `5186f151f7bc4e289fc6cbb05646fd11`：baseline / packed / baseline-repeat 平均为 33.96 / 35.34 / 33.08 FPS，最差五秒窗口 32.28 / 32.18 / 30.42；packed-repeat 未完成，保留。报告 `frame-simulator-release-raf-20261024.json`。没有 Release Profiler 不支持错误，也没有标准 Profiler 帧。

V1 相对补偿 timer 的 session `95216720cde744528431af5f9650fd2d` 完整 30 窗：baseline / packed / baseline-repeat / packed-repeat 平均 54.12 / 55.78 / 55.45 / 56.60 FPS，最差 44.57 / 52.63 / 51.50 / 53.22，均未通过持续 50k 门槛。普通伤害组平均 59.24 FPS，但人数下降，不能作为持续 50k 的替代证据。baseline 首窗有 445ms 卡顿和 28.78Hz，期间出现 SDK 建议弹窗；完整保留，不剔除。报告 `frame-simulator-release-deadline-20261024.json`。

V2 改为跨帧单调 60Hz deadline，落后超过 100ms 则重置，避免无限追赶；只在 devtools 禁用 SDK 建议弹窗。session `82a151b3b3274a8988455add17be6e09` 前两组完整六窗均约 60 FPS、约 30Hz，但后半程发生约 1 FPS、甚至更长的 timer 限速。引擎 focusedThroughout 和 SDK visible 仍为 true，说明这两个标记不足以判断模拟器真实调度状态。该轮失败及不完整数据保留于 `frame-simulator-release-deadline-v2-20261024.json`，不能只选前两组宣布完成。

V3 在 deadline 之外保留一个空 rAF 回调，维持模拟器原生帧路径；空回调不调用 Unity runner、不推进模拟。手机及非 60Hz 路径保持原调度。四项检查覆盖 deadline、不重复推进逻辑、rAF 开关、平台分流、未知生成代码拒绝与字节级回滚。真实运行和完整套件仍须单独验收。

`Set-SimulatorBenchmark.cjs <ExportRoot> frame|cpu|sim|rollback` 为临时 devtools 测试启动器：等待 Module 可用后延迟十秒，通过原有 SendMessage 调用现有 benchmark 方法，带原文件备份及哈希校验；sim 调用现有“50k Islands sim”按钮的 Press。用于模拟器输入不可靠时自动启动；测量仍来自真实 Update 和固定 tick。正常交付前回滚启动器，防止重新打开自动进入测试。

### 模拟器纯逻辑 55 FPS 验收通过（2026-10-24）

V3 session `45b66264f5aa40548048a41e9e9a7b68` 已完成全部 30 个窗口。持续无伤害 50k 的四组共 120 秒、24 窗全部通过 `logicOnly55FpsPassed`：

| 场景 | 平均 FPS | 最低五秒 FPS | 实际模拟 Hz |
| --- | ---: | ---: | ---: |
| sim-baseline | 60.00 | 59.80 | 29.98 |
| sim-packed | 60.00 | 59.97 | 29.98 |
| sim-baseline-repeat | 60.00 | 59.96 | 29.98 |
| sim-packed-repeat | 60.00 | 59.96 | 30.00 |

每窗存活 50,000、drawn=0、activeCameras=0、Burst 验证通过、Profiler=false、development=false、targetFps=60、focusedThroughout=true；没有减少单位、固定模拟频率或速度。普通伤害组平均 60.00、最低 59.99 FPS，因人数下降不纳入持续 50k 门槛。单帧最大仍有 37ms、窗口 P95 最大 26ms，所以五秒 FPS 达标不表示每帧都低于 18.18ms。报告 `Tools/WeChat/Results/frame-simulator-release-deadline-v3-20261024.json`。

独立 CPU session `46254a1746b6427e867c172366f3d7e6` 完成 AB / BA / AB 六轮，每轮预热 300、测量 300 tick。baseline / packed 的三轮 Step 均值中位数为 15.4633 / 14.4667ms（减少 6.45%），避让 10.5167 / 9.46ms。三对 Step 变化 -3.21% / -16.23% / -8.47%，波动保留，不用单轮最大收益宣传。六轮状态哈希均为 `D258122B36EEC5041311061242223858B0A56B03BAEFE623E94569D71902983E`；同包/同 session、Burst、Release、Profiler 关闭、SIMD/threads 关闭、jobWorkers=0、实际避让线程仅 0 的检查均通过。报告 `cpu-simulator-release-deadline-v3-20261024.json`。这是模拟器结果，不能当作新手机性能数据，也不是标准 Profiler 帧。

帧率稳定的主要改善来自 devtools 调度；baseline 同样达到 60 FPS，不能把从约 35 FPS 到 60 FPS 全归因 packed 内核。V3 framework SHA-256 为 `8d418dabe5d21082d50c86370eb3b3a348ecc26b8a3ca231d2183a3b4818652c`。原 rAF / V1 / V2 / V3 的 23 / 30 / 23 / 30 份原始窗口、六份 CPU 回传、报告、调度工具和本包 loop 诊断已归档至该包 `profiler-build-evidence/simulator`，保留失败和不完整样本。

结束时已通过原按钮 Press 恢复普通“50k Islands sim”（伤害关闭、速度 1），并回滚导出的自动测试启动器，文件恢复原 SHA。原有调度与观察器可按相反顺序回滚。资源服务 8091 保持运行；重新开窗默认仍是原来的 2k 场景，可用 sim 启动器复现纯逻辑模式，操作后再 rollback。

独立遗留问题：本包模拟器原生触摸能到达 wx.onTouchStart，但 Unity 按钮没有可靠响应；当前使用受控 SendMessage 启动和切换，尚未定位输入故障，不能宣称 rAF pump 已修复输入。没有更改输入 SDK 或冒称手动按钮验收通过。此问题不改变已记录的真实 Update/tick 性能结果。当前验收仅覆盖本机模拟器纯逻辑，手机、完整战斗渲染和 Worker V2 均未因此通过。

### 最新单线程包推送手机预览（2026-10-24 15:02）

用户要求手机查看最新效果。复用 `RedBlue-Logic-Deadline-Release-Final-20261024` / Build ID `20261024T053244Z-logic-deadline`，没有重新编译或启用 threads/SIMD。原 192.168.1.8:8091 服务 PID 56004 仍在运行；首次监听查询未返回，后续复查和 40 文件 HTTP SHA-256 校验确认服务正常，未实际重启。开发者工具登录有效，skill 0.3.11 版本匹配，官方 `auto_preview` 返回 success=true，总包 13,269,777 字节，已推送开发者微信。

导出自动 benchmark 启动器保持回滚；手机默认仍从原场景进入。已提示手机点击 CPU compare 跑六轮 A/B，或点击 50k Islands full 查看渲染效果、Frame compare 测纯逻辑帧率。调度 V3 限定 devtools，Android 不启用。本记录节点尚无新手机启动或 benchmark 回传，不声明手机性能验收；本次包只带既有 frame-loop 观察器，没有新装启动观察器或采集标准 Profiler 帧。推送与资源校验记录：`Tools/WeChat/Results/phone-latest-preview-20261024.json`、`phone-latest-http-validation-20261024.json`。

### 最新手机六轮结果与优化空间（2026-10-24 15:05–15:06）

用户确认点击 full 和 CPU compare，收到手机 192.168.1.7、Maleoon 920、2835×1316 的 session `14c495b462cb4cad935adc407e9c69b1` 全部六轮，执行顺序 A0 / B0 / B1 / A1 / A2 / B2。Build ID 匹配最新 Release，Burst 验证通过，Profiler=false，SIMD/threads=false，jobWorkers=0，实际避让仅线程 0。全部终点状态哈希与手机历史基线一致，没有出现结果不一致。

| 对照轮 | baseline Step ms | packed Step ms | 耗时减少 |
| --- | ---: | ---: | ---: |
| 0 | 16.3733 | 15.2067 | 7.13% |
| 1 | 22.8833 | 15.4333 | 32.56% |
| 2 | 22.9567 | 20.8733 | 9.08% |

原始三轮中位数为 22.8833 / 15.4333ms，数学上减少 32.56%，但该数不能用作稳定收益结论。B1 后 baseline 的网格、避让、决策、Commit 一起变慢，最后 B2 也显著变慢；没有温度/频率/后台负载观测，不能断言已发生热降频。配置一致性检查通过不等于运行条件稳定。保留全部慢样本，三对均是 packed 更快；第一和第三对约 7.1% / 9.1% 仅为对应轮次结果，不能擅自删掉中间对再重算验收。

最新 packed 前两轮约 15.21 / 15.43ms，旧 packed 手机三轮中位数约 15.27ms。跨包、跨时段不能据此判断新旧优劣；当前没有独立证据证明新增 active-cell 遍历带来稳定额外收益。手机 latest 的 packed 中位阶段为 Grid 2.2533ms、Separation 9.48ms、Decision 3.2767ms、Commit 0.4133ms，约占 Step 15% / 61% / 21% / 3%。网格阶段包含每六 tick 一次的 FlowField 成本，不能全部归因 gather。

单线程仍有空间，优先顺序：先细分 packed gather/scatter 与重复清零成本，再对照减少冗余内存访问；避让仍为最大热点，需要另开诊断记录配对尝试、实际接触和预算耗尽，避免在正式计时里加入额外计数干扰；随后检查决策的距离计算和生成 Wasm 是否已消除重复计算。SIMD 存在预算与双向力写回依赖，先前开关无稳定收益，不承诺简单打开即可加速。近似数学、降低邻居预算、降低模拟频率应视为规则变化，不能混入相同状态的性能收益。

手机最新包尚无 `/frame-benchmark` 正式窗口。现有 frame-loop 诊断未记录场景、单位数量、绘制和伤害设置，还混有 CPU compare 结束后暂停的空闲段，不能将其中 60 FPS 宣称为 full 的持续帧率。当前一个 tick 在本轮 packed 的耗时约 15.2–20.9ms，模拟约 30Hz 时不是每显示帧都计算一个 tick，但计算帧仍需为引擎与绘制留余量；达到 55 FPS 的整帧预算约 18.18ms，后续应补全手机 full 的实际 Update/渲染采样。没有新标准 Profiler 帧。

用户报告 full 接触后僵持，核实该按钮沿用 ProfileScenario 的 sustained=true，默认 damage OFF，属于持续 50k 压力模式。已解释可切换 Damage on/off 开启伤害，用户表示知道原因即可；本轮未改变按钮源码或手机包。

原始六份 CPU、手机 loop 诊断及汇总归档至 `RedBlue-Logic-Deadline-Release-Final-20261024/profiler-build-evidence/phone-20261024-1505`。分析见 `Tools/WeChat/Results/cpu-phone-latest-20261024.json` 和 `cpu-phone-latest-analysis-20261024.json`。本轮仅整理证据和分析，未改模拟、输入或调度代码。

### Packed bounded 单线程候选（2026-10-24）

用户要求继续实际优化并查阅网上技术。直接读取 Unity 官方 [NoAlias](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/aliasing-noalias.html)、[Loop vectorization](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/optimization-loop-vectorization.html) 和 NVIDIA [Broad-phase collision detection](https://developer.nvidia.com/gpugems/gpugems3/part-v-physics-simulation/chapter-32-broad-phase-collision-detection-cuda)，正文保存于 `Temp/WeChatCpuExperiments/burst-noalias.html`、`burst-loop-vectorization.html`、`spatial-subdivision.html`。官方说明：NoAlias 可减少编译器因潜在内存重叠而保守重载，但必须证明分配独立；数据相关提前退出会阻碍自动向量化；空间分桶按格子连续处理并维持稳定排序适合当前等尺寸单位。资料没有承诺本项目或手机上的百分比收益。

新增显式 `BurstPackedBoundedSingleThread`，保留上一版 `BurstPackedSingleThread` 为同包对照。主要改动：

- 避让内循环用 `min(endB, first + 48 - used)` 计算固定终点，最终一次更新当前单位预算。即使对方预算已满，仍消耗当前单位的一次尝试，与原行为相同。
- 为该候选独立 helper 的六个指针参数标注 NoAlias；每个参数来自不同 pinned 数组，未假设同一数组的 p/q 两个索引独立。
- 活单位原始 force/budget 在 scatter 中覆盖，不再先整体清零；死亡槽位仍显式清零，伤害仍按原行为清零，packed scratch 连续清零。
- 保持配对遍历、奇偶 tick 顺序、48 次预算、平方根和所有浮点运算顺序。没有降低单位数、30Hz 模拟频率、速度或伤害规则，没有启用 SIMD/threads。

初试曾将位置 gather 合并进 ID 分桶遍历，导致 packed 目标地址分散，Editor Grid 反而慢约 0.08–0.16ms，Step 三对约 -0.99% / -1.23% / +1.45%。已撤去这项写法，保留连续目标写入。失败尝试原始结果在 `Temp/RedBluePackedBoundedChecks/fused-gather-trial`，不能与最终候选混算。

最终候选 16 例、1,920 tick 全数组和随机状态逐 tick 哈希相等；覆盖四种阵型、伤害开关、两种种子、重合坐标和死亡单位。另有 50k Islands / 600 tick 全状态相等，终点 `7B246FEFF2E9657AF727132B98507032C9CB5C145BE5038A6C58C59003EE8D96`。新 benchmark 分支完成 3,600 tick 六轮 AB/BA/AB、公开状态一致、实际避让线程仅 0、Profiler 状态恢复检查。

Editor 精确六轮测试（i9-14900HX）Step 三轮中位数旧 packed 9.9043ms / bounded 9.4622ms（-4.46%），Separation 6.5845 / 6.2952ms（-4.39%）。每对 Step 变化 -0.88% / -8.82% / +0.46%，有波动及一对变慢，不能宣称稳定 4.5% 或外推手机。报告 `Tools/WeChat/Results/cpu-editor-packed-bounded-20261024.json`，原始结果 `Temp/RedBluePackedBoundedChecks/benchmark-sequence.json`。

实验 variant `packed-bounded` 的 CPU compare 直接比较旧 packed / bounded，各三轮；常规 50k Islands 使用候选，通用项目默认仍保留。构建入口生成独立 Release 包 `RedBlue-Packed-Bounded-Release-Final-20261024`，端口 8092。当前阶段尚未收到该包手机结果，不能把此候选称为手机优化已验收，也没有采集新标准 Profiler 帧。

比较脚本同时支持旧 baseline/packed 和新 packed/bounded，识别缺轮及哈希不一致，不把新包旧 packed 错配为原始 baseline。新增模式、缺轮拒绝、哈希差异拒绝和历史手机报告兼容四项检查通过。

候选已实际导出：Build ID `20261024T072438Z-packed-bounded`，资源版本 `2026-10-24-1535`，HTTP 8092 / PID 27272，40 个文件 SHA-256 校验通过。引擎和 HybridCLR MethodBridge 均为 Release，ET 依赖重新生成；归档 kernel SHA-256 与测试版一致。沿用相同 `wasm-opt -Oz --strip-debug` 兼容处理，imports/exports 保持；RedBlue 的实际 Burst Wasm 无 SIMD 指令。保留 devtools-only V3 调度，手机仍使用原调度。

模拟器 session `07e1ea8182594366a88737c78a47a6da` 六轮完整，旧 packed / bounded Step 中位数 25.6933 / 27.3567ms（候选慢 6.47%），Separation 16.86 / 17.7467ms（慢 5.26%）。每对 Step 变化 -0.97% / +12.27% / +3.00%，两对变慢；当前多个阶段也比历史模拟器会话偏高，没有频率、温度和全局负载记录，不能跨时段归因或外推手机。六轮 Burst=true、Release、Profiler=false、SIMD/threads=false、jobWorkers=0、实际避让线程仅 0，终点公开状态均与历史 Wasm 一致。报告 `cpu-simulator-packed-bounded-20261024.json`，原始文件位于新包 `profiler-build-evidence/simulator/cpu`。本候选没有通过性能验收，不得把 Editor -4.46% 写成已实现的手机收益。

模拟器自动 CPU 启动器已按哈希回滚。官方 `auto_preview` 成功推送手机，包 13,374,773 字节；未安装手机自动测试入口，已请用户点 CPU compare 获取实际六轮新旧 packed 对照。当前推送成功不代表手机已完成性能验证，旧的通用默认没有切换到 bounded。汇总决策记录 `cpu-packed-bounded-review-20261024.json`。后续以手机实际结果决定保留、拆分或撤去该实验，未采集新标准 Profiler 帧。

### Packed bounded 手机结论（2026-10-24 22:40）

恢复 8092 服务（PID 55276）并重新校验 40 文件后，经官方 auto_preview 再次推送同一 Build ID，用户点击 CPU compare。收到手机 192.168.1.7 / Maleoon 920 / 2835×1316 的 session `5cfa0873297048ce95af3a097f85f645` 全六轮。Release、Burst=true、Profiler=false、SIMD/threads=false、jobWorkers=0、实际避让线程仅 0，所有公开终点状态哈希与历史 Wasm 一致。

旧 packed / bounded 三轮 Step 均值中位数 15.1767 / 15.2000ms，候选慢 0.15%，基本持平；每对变化 +0.46% / -3.12% / -0.22%。Grid 中位数 2.2033 / 2.0233ms（约少 0.18ms），Separation 9.4200 / 9.5933ms（约多 0.17ms），改善相互抵消。该手机实测不支持明显加速，不将 Editor 的 -4.46% 外推到手机，也不把这版提升为已验证更快的默认。报告 `cpu-phone-packed-bounded-20261024.json`，原始 CPU 和 loop 诊断归档于新包 `profiler-build-evidence/phone-20261024-2240`。

用户观察 FPS >=55：对应测试中间连续 12 个五秒 loop 窗口（本地 22:39:34–22:40:29）为 57.22–60.90 FPS，支持这段 CPU compare 接近 60 FPS。启动窗口为 42.58 FPS（含 900ms 最大间隔），完整保留；测试中也存在单次 90ms 间隔，因此五秒平均达标不代表无卡顿。CPU compare 每显示帧推进一个固定 tick，提前 return 跳过单位 Draw；六轮结束后不再推进模拟。结束后的约 60 FPS 是空闲状态，不能纳入持续 50k，更不能据此认定 full 渲染达到 55 FPS。当前仍无手机完整战斗正式 frame-benchmark 窗口，没有新 Unity Profiler 帧采集。

### 手机正常纯逻辑帧率与 split 实验（2026-10-24 晚）

用户点击 Frame compare，手机 session `03743de774b34a70bbd11601554c916a` 收齐 30 个五秒窗口。无伤害的四个纯逻辑阶段全部保持 50000 存活、drawn=0、activeCameras=0、前台、Release、Profiler=false、约 30Hz。原版两轮合计 50.3619 FPS，packed 两轮合计 51.8761 FPS；最低窗口分别 48.3871 / 49.5109 FPS，packed 最高窗口仅 54.3565 FPS，持续 50k 的 24 个窗口均未到 58 FPS。最后有伤害阶段出现 58.5649 FPS 时仅剩 12133 单位，不能纳入持续 50k 验收。报告 `Tools/WeChat/Results/frame-phone-packed-ab-20261024-2254.json`。

这里的约 20ms/tick 来自外层模拟段墙钟计时累计值除以已完成 tick 数，packed 两轮约 20.0044ms；它不是逐 tick 分布、不是纯 CPU 执行时间、不是标准 Profiler 帧，也不能证明每个 tick 恰好阻塞 20ms。与 CPU compare 约 15.2ms 的差异仍未归因，不能直接断言手机降频。新报告增加正常运行的流场、网格、避让、决策、提交及单帧模拟段最大耗时，网格指标仍包含流场，flowMsPerTick 是其中的细分，不能重复相加。

新增独立 variant `packed-split`，仍采用已验证的旧 packed 内核，不沿用手机未获益的 bounded 改法。按原遍历顺序将同一个 tick 的非空格避让分两段：首段流场/网格/前半避让，末段后半避让/回写/决策/提交。所有原有配对、预算、伤害、随机调用和 30Hz 步长保留。Tick 仅在末段提交后增加，渲染的 PreviousX/Y 延后至提交才更新；没有在两个模拟 tick 间分摊或降低更新频率。正常调度在相邻显示帧处理两段，积欠超过两个 tick 时仍以原四 tick 上限追赶，不靠丢弃工作提高 FPS。通用配置默认关闭，实验包的 50k Islands 默认开启，可通过同包 Frame compare 对照整 tick 与 split。

Editor 的 16 个组合（4 阵型、2 伤害、2 种子，含死亡/重合坐标）逐 tick 数组一致；三组 50k 各 600 tick 终点一致。30/48/58/60/90 显示帧率及一次 150ms 停顿的时钟测试没有持续落后；额外六轮 3600 tick 的 AB/BA/AB 序列、公开状态哈希、Burst/单线程及 Profiler 恢复检查通过。结果在 `Temp/RedBlueSplitChecks`，六轮报告 `Tools/WeChat/Results/cpu-editor-packed-split-20261024.json`。孤立 Editor 成对总 tick 仍约 10ms，两个部分均值约 4.2/5.7ms；主要目标是长帧分布，不能据此宣称总 CPU 减半或单 tick 已无优化空间。手机 58 FPS 尚待新包验证。

新 Frame compare 为六个 30 秒阶段：sim-packed、sim-split-packed、sim-split-packed-repeat、sim-packed-repeat、full-packed、full-split-packed；全阶段无伤害、维持 50k，总时长约三分钟。CPU compare 在同一显示帧内执行两段，用于独立验证 Wasm 终点状态和总 tick 耗时；不能用其帧率代替正常分帧测试。新增 58 FPS 验收要求每个窗口均 >=58，且约 30Hz、50k、前台、纯逻辑无摄像机/绘制条件成立；验收脚本七项检查通过。

独立包已成功导出并经官方 `auto_preview` 推送：`RedBlue-Packed-Split-Release-Final-20261024`，Build ID `20261024T153314Z-packed-split`，资源版本 `2026-10-24-2345`，HTTP 8093 / PID 71480，40 个文件校验通过，预览包 13235519 字节。引擎和 HybridCLR 均 Release，SIMD/threads=false，内核源码哈希与测试版一致；沿用 wasm-opt 兼容处理、资源前缀隔离和只读 frame-loop 观察器。本次没有修改手机 rAF 调度，也未安装模拟器自动测试入口。推送成功不代表手机已运行完成；尚待 CPU compare 的 Wasm 状态及 Frame compare 的正常持续 50k 窗口，未采集标准 Unity Profiler 帧。

### Packed split 手机验收（2026-10-25 00:08）

手机 192.168.1.7 / Maleoon 920 / 2835×1316 收到 CPU session `81071d8a1fc249cead5693b90dbcb51b` 全六轮。整 tick / split 总 Step 三轮中位数为 15.0267 / 15.1733ms（候选多 0.98%），每对变化 +2.76% / +0.98% / -0.75%，没有总 CPU 加速证据。所有公开终点哈希均为 `D258122B36EEC5041311061242223858B0A56B03BAEFE623E94569D71902983E`；Release、Burst=true、Profiler=false、SIMD/threads=false、实际避让线程仅 0。CPU compare 将两段放在同一显示帧，只用于总耗时和状态验证。

第一次 Frame compare session `e986e9c0292c437884a1e705fc85b939` 在第 22 个窗口后切后台。此前两个 split 纯逻辑阶段完整且超过 58 FPS，但后续 sim-packed-repeat 包含后台停顿、约 0.46 FPS 的窗口，明确判为无效并保留，不能删除异常后计算验收。该次约 62 FPS 是 Unity Update 间隔计数，不是面板刷新率证明。

用户重新点击 Frame compare，新 session `71eca6788ea64fec8e09145c2b9daf44` 的 36 个五秒窗口完整、均前台且有效，以下均为同一包内对照：

| 场景 | 整 tick 平均 FPS | split 平均 FPS | split 最低五秒 FPS |
| --- | ---: | ---: | ---: |
| 纯逻辑，两轮合并 | 50.93 | 59.70 | 59.45 |
| 带渲染，各一轮 30 秒 | 48.94 | 59.72 | 59.54 |

纯逻辑两个 split 阶段分别为 59.7566 / 59.6469 FPS，两轮共 12 个窗口全部通过 >=58 FPS；整 tick 两轮为 51.4253 / 50.4412 FPS。带渲染 split 六个窗口全部通过 >=58 FPS，模拟 29.9923Hz、全程 alive=drawn=50000、无伤害；纯逻辑 alive=50000、drawn=0、activeCameras=0。纯逻辑合计帧率提高 17.22%，带渲染提高 22.03%，这些是本机本轮帧率收益，不能换算成 CPU 降耗百分比。

带渲染最差窗口 P95 从约 30ms 降至 19ms，但 split 仍有单次约 39ms 长帧、单帧模拟段最大 32ms、最大模拟积欠约 51ms、6 个多 tick 追赶帧。因此验收含义是每个五秒平均达到 58 FPS，不是每帧均在 17.24ms 内，也不能承诺完全无卡顿。正常模拟墙钟累计 / 完成 tick：纯逻辑整 tick 两轮 20.0877 / 20.4417ms，split 21.6726 / 21.6619ms；带渲染 19.0322 / 19.9489ms。没有总工作量减半或总 CPU 时间减少的证据；网格阶段已包含流场，不能重复累加。

结论：该实验在这台手机的本轮持续 50k 测试通过纯逻辑及带渲染的 58 FPS 门槛，验证分摊一个 tick 到相邻显示帧能够改善帧率。通用默认仍未切换；带渲染尚需反向顺序复测、长时间运行及其他手机覆盖，单 tick 内核优化也仍有空间。本轮只归档和更新分析，没有重新构建或修改手机调度，未采集标准 Unity Profiler 帧。

报告为 `Tools/WeChat/Results/cpu-phone-packed-split-20261025.json`、`frame-phone-packed-split-20261025.json`，验收汇总更新于 `cpu-packed-split-review-20261024.json`。原始 CPU、两次 Frame 会话（含无效窗口）和只读 loop 诊断归档在新包 `profiler-build-evidence/phone-20261025`。

### 四阵型 packed split 实验（2026-10-25）

用户发现 Front 约 53 FPS。核实上一包的 packed 选择和 split 调度均有 `formation == 1`，Front 没有启用。按用户要求去掉阵型限制，仍限定 50000 单位及实验配置开关；Front、Islands、Pincer、Dense stress 均使用现有 packed 内核和分帧调度，未改伤害、配对预算、30Hz 或渲染实现。低于 50k 的入口保留现有行为，通用配置仍默认关闭实验开关。

新增 variant `packed-split-all`，独立目录 `RedBlue-Packed-Split-All-Release-Final-20261025`，端口 8094。`CPU compare` 对四阵型各跑六轮 packed 整 tick / packed split，总 24 轮，每种阵型分别 AB/BA/AB、300 tick 预热加 300 tick 测量；两个 split 部分仍在同一显示帧完成，只验证总 tick 耗时和状态。`Frame compare` 自动按四阵型依次跑 sim-baseline、sim-split-packed、full-baseline、full-split-packed，各 30 秒，共约八分钟、96 个五秒窗口。这里 baseline 为原始非 packed Burst 单线程，candidate 为 packed 加 split，因此得到的是两项合并收益，不能仅归因于 split。各模式仅一对固定顺序，边际差异还需反向复测。

修正 Frame 回传的 formation 字段，CPU 和 Frame 分析均按阵型独立分组，避免跨阵型补轮或误比较状态哈希。新增缺轮隔离、不同阵型不同哈希、58 FPS 门槛等检查，共 13 项通过；旧 Islands 的 CPU 六轮与完整 36 窗口数据重新分析仍得到相同数值。

Editor 四阵型各 50k、伤害开/关共八例，每例 180 tick，并包含显示帧停顿，原始非 packed 与 packed split 的所有模拟数组和随机状态一致。Front 每个提交 tick 检查，其余阵型每 30 tick 及终点检查；每显示帧均核对完成 tick 数。原始证据 `Temp/RedBlueAllFormationChecks/formation-0.json` 至 `formation-3.json`。另有 24 轮 CPU 切换和 Profiler 恢复的控制流检查，刻意直接跳到末 tick，所以该项不提供真实计时或完整回放证据，单独标记 `controlFlowOnly=true`。

验收记录 `Tools/WeChat/Results/cpu-packed-split-all-review-20261025.json`。新包手机结果尚未收到，不能从 Islands 的收益推断四种阵型都达到 60 FPS；也未新增标准 Unity Profiler 帧。

导出及推送完成：Build ID `20261024T164248Z-packed-split-all`，资源版本 `2026-10-25-0042`，HTTP 8094 / PID 72896，40 个 HTTP 文件校验通过，官方 auto_preview 成功，预览包 13265890 字节。归档源码与当前 Demo/CPU/Frame 源码哈希一致，Release、SIMD/threads=false、HybridCLR bridge Development=false，Wasm 兼容处理保留 imports/exports 且 Brotli 回环通过，没有修改手机 rAF 调度或安装自动测试入口。

首次 prepare=true 已完成 ET/HybridCLR Release 依赖生成及 Wasm 编译，但 SDK 在替换 `TextToolDatas/slim/backup.txt` 时遇到 Windows 文件共享冲突。文件随后解除占用，重试采用 prepare=false 复用同一轮已生成的依赖并成功，因此最终 manifest 的 etCompilationRegenerated=false 应结合此前生成步骤解读。构建 helper 现在仅对该路径及 Windows 32/33 共享错误有限重试转换，最多两次，不重编 Wasm；其他异常仍报失败。手机性能仍待用户运行八分钟四阵型 Frame compare 后核实。

### 代码整理与产物保留（2026-10-25）

红蓝运行时代码的 11 个 C# 文件统一换行和缩进，补充类、函数及关键算法的中文注释。与整理前快照逐项比较，Unity 和纯 .NET 两种条件编译下语法 token 均相同；没有改变模拟算法、遍历顺序、分帧规则或默认配置。本次没有迁移为逐士兵的 ET Entity，也没有重新导出手机包。

按用户要求，导出工程只保留 `Unity/BuildArtifacts/RedBlue-Packed-Split-All-Release-Final-20261025`（Build ID `20261024T164248Z-packed-split-all`），其余 15 个红蓝中间工程删除。最终包自身的构建证据和回滚备份保留；`HotUpdateDlls` 作为构建依赖保留。历史记录中的旧导出路径仅用于说明当时的测试环境，已不代表本地仍保留该包。

清理旧导出工程、实验缓存、独立测试编译目录和临时 Library 备份；保留 `Tools/WeChat/Results`、`Temp/WeChatCpuExperiments/uploads` 中的手机原始回传、官方 Markdown 快照，以及已有 Profiler 采样和四阵型正确性证据。benchmark 回传仍不等同于标准 Unity Profiler 帧。

提交前，现有纯 .NET 模拟测试（含四阵型、同时结算和 50k 密集场景）、13 项 Python CPU/Frame 对照检查、13 项 JavaScript 兼容与诊断检查，以及 Profiler 路由检查全部通过。此处不构成新的 Unity 构建或手机性能验收。
