# PingDou 局内 Demo

基于 `tuanjie` 的独立 `PingDou` 分支。团结编辑器版本 **2022.3.62t11 / 1.9.3**。保留 ET、YIUI、YooAsset、HybridCLR 原有工程结构。

## 试玩

用团结打开仓库中的 `Unity` 工程，等待导入完成，使用 **ET → PingDou → Open gameplay (ET + YIUI)**，或打开 `Assets/Scenes/Init.unity` 后运行。

当前编辑器配置为 `EnableDll=false`、`EditorSimulateMode`，直接运行当前源码，不使用红蓝 Demo 的旧热更 DLL，也不需要启动登录服。正常入口为：

`Init → CodeLoader → ET Main Fiber → EntryEvent3_InitClient → YIUI MainPanel → PingDouGameComponent`

**ET → PingDou → Open art preview** 是独立美术预览，便于调整材质和交互；正式入口不使用该预览 MonoBehaviour。**Rebuild assets** 可重新生成网格、材质、UI prefab 和预览场景，覆盖这些生成资源，手工改过生成资源后应先备份。

## 操作与流程

- 上方完整预览与下方近景使用同一份格子数据、同一套 3D 豆子；紫框对应下方实际操作范围。
- 点击底部颜色盒，全部对应空槽出现浅色底与紫圈。单指或鼠标左键滑动放豆，快速滑动会补采样。颜色错误、空白与重复格子都忽略。
- 鼠标滚轮、加减按钮可缩放；移动模式或鼠标右键拖动画板。手机双指平移与捏合缩放，手势结束后需重新落指才能继续铺豆。提供视野复位。
- 豆子沿笔尖下落，触板轻弹；落稳后统一触发声音、得分与颜色完成事件。切色、松手和移动不重置累计 Combo。
- 拼完全部豆子后开始熨烫。按住拖动熨斗，局部豆子分 17 档变薄、收孔、扩边融合。未全部完成时最多显示 99%。可用收尾辅助完成，辅助局最高两星。
- 完成熨烫后揭纸，作品抬起并显示成品、三星目标、分数拆分、主题收集进度与下一张推荐。草莓下一幅为小樱桃。
- 暂停、失焦、关闭视图以及正常退出会保存。制作中每秒合并保存一次。重玩需二次点击确认，保留已经获得的图鉴纪录。

四幅图案均为 **24×24 等大格点**：草莓甜心 235 颗、小樱桃 188 颗、橘猫团团 272 颗、一朵好心情 254 颗。水果、猫咪、好心情分别按主题收集，展示真实数量，不使用概念图里的虚构 `3/4`。

## 计分

每颗基础 10 分。累计第 5、10、20 颗开始分别额外加 2、5、10 分，20 颗后不再增加档位。每种实际使用的颜色完成后独立奖励 50 分，同槽只结算一次。

草莓结算为基础 **2350** + Combo **2220** + 颜色完成 **200** = **4770**。第二星是本图累计目标 `min(20, 豆数)`；第三星要求自然完成熨烫、有效加热占比至少 55%。这些参数是首版可调规则，原始总分不作为跨图技术排名。

## 代码与资源

| 位置 | 职责 |
| --- | --- |
| `Scripts/Model/Client/PingDou/PingDouRound.cs` | 无 Unity 依赖的状态机、格子、计分、热量、评级、存档校验 |
| `Scripts/ModelView/Client/PingDou/PingDouSession.cs` | 应用命令、关卡切换、收集与保存协调 |
| `Scripts/HotfixView/Client/PingDou` | ET EntitySystem 的创建、更新、销毁与 YIUI 挂载 |
| `Scripts/ModelView/Client/PingDou` | 双相机、落豆与熨烫表现、输入映射、UGUI、存档适配 |
| `Scripts/Editor/PingDou` | 资源生成、画面验证、ET 正式入口冒烟检查 |
| `Resources/PingDou` | Demo 随包入口 prefab、资源引用表、图案数据与防裁剪 shader |
| `GameRes/PingDou` | 可复用网格、材质、UI 切片、字体、音效、原图网格参考 |
| `Book/PingDou/GUI` | 原始 GUI 概念稿与绘图说明，不进入游戏包 |
| `Book/PingDou/QA` | 团结实际渲染的验证截图 |

资源表直接引用 `GameRes` 中的资产；Demo 使用小型随包资源集，正式产品扩展大量主题时可将主题资源迁入 YooAsset 独立包。共用网格与材质，定位柱合并成一个静态网格，提示只在目标集合变化时重建；材质支持 GPU instancing。双视图各使用一张上限 1024 的 RenderTexture，退出时释放创建的纹理、网格、材质和舞台。

模型采用轴向正交视图，近景只沿横轴轻微俯视，网格保持横平竖直。拼豆、定位柱、放豆笔与熨斗均为真实网格；中性底板不铺参考图色。隔热纸为透明材质。字体由仓库现有思源黑体生成静态 550 字重子集，重命名为 PingDou Sans，并附 OFL 授权；正常使用工程无需安装额外建模或字体工具。

## 边界

本次完成的是 **使用真实 ET/YIUI 生命周期的本地局内 Demo**。单局、图鉴与最佳纪录保存在设备 `PlayerPrefs` 的 `PingDou.v1.*` 命名空间，不是服务端存档、跨设备同步或防作弊系统。没有新增登录、付费、排行榜、经营玩法或虚构在线接口。正式联网时应由服务端持有关卡版本与结算权，替换本地保存适配；不要直接上传或信任本机分数。

已做编辑器流程与截图验证；尚未进行微信真机性能和触摸验收。手机手势实现保留在正式输入路径，不能用桌面自动化结果代替真机验收。发布玩家包前仍需按原工程流程编译 ET 热更代码、构建 YooAsset 和对应平台资源；编辑器模拟模式配置不能直接用于线上包。

## 验证

```powershell
dotnet run --project Tools/PingDou/PingDou.Tests.csproj
./Tools/PingDou/Invoke-Validation.ps1
```

第一项不依赖 Unity，覆盖四幅图案的 3,039 项规则检查。第二项先执行规则检查，再启动团结生成实际 UI 与模型、检查落稳计分、缩放锚点、自然熨烫与结算、下一关和图鉴，保存画面；最后进入真实 `Init` 场景验证 ET/YIUI 挂载与更新、按钮操作。QA 存档使用独立命名空间。

日志位于仓库忽略的 `Temp/PingDou-editor.log` 与 `Temp/PingDou-entry.log`，成功标记分别为 `PINGDOU_VERIFICATION_PASS` 与 `PINGDOU_ET_ENTRY_PASS`。入口测试只验证交互命令，不改动玩家制作进度。

`Tools/PingDou/BuildFont.py` 仅在需要新增字符后重新生成字体时使用，额外依赖 `fonttools`；已提交的字体可直接运行。原始设计来源为用户提供的两段聊天、本机 `SpineEditor/docs` 与 `bead-studio`；复用图案和音效，新增小樱桃格点及本工程三维模型。
