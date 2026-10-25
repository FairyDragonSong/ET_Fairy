using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEventSystem = UnityEngine.EventSystems.EventSystem;

namespace ET.RedBlue
{
    /// <summary>
    /// 由 ET 启动事件打开的演示入口，负责界面、模拟调度和渲染提交。
    /// CPU compare 使用独立固定 tick 测试；Frame compare 通过正常 Update 测量整帧间隔。
    /// </summary>
    public sealed class RedBlueDemo : MonoBehaviour
    {
        private CrowdSimulation simulation;
        private CrowdRenderer rendererView;
        private Camera viewCamera;
        private GameObject canvasObject;
        private GameObject logicBackground;
        private Text stats;
        private Font font;
        private readonly float[] frames = new float[300], sorted = new float[300];
        private int frameCursor, frameCount, count = 2000, formation, mode;
        private int sampleTick;
        private float accumulator, speed = 1, sampleTime, uiTime, drawMs, zoom = 1, finishedTime;
        private bool paused, zeroDamage, loop = true, suspended;
        private float previousPinch;
        private RedBlueCpuBenchmark cpuBenchmark;
        private RedBlueCpuExperiment cpuExperiment;
        private RedBlueFrameBenchmark frameBenchmark;
        private string frameSession;
        private int frameScenario;
        private bool frameProfilerWasEnabled;
        private readonly List<Camera> suppressedCameras = new List<Camera>();
        private bool camerasSuppressed;
        private CrowdExecution execution = CrowdExecution.Auto;
        private readonly CrowdStepScheduler stepScheduler = new CrowdStepScheduler();
        private bool splitTicks;
        private static readonly string[] FrameScenarios =
        {
            "sim-baseline",
            "sim-packed",
            "sim-baseline-repeat",
            "sim-packed-repeat",
            "sim-battle-packed"
        };
        private static readonly string[] SplitFrameScenarios =
        {
            "sim-packed",
            "sim-split-packed",
            "sim-split-packed-repeat",
            "sim-packed-repeat",
            "full-packed",
            "full-split-packed"
        };
        private static readonly string[] AllFormationScenarios =
        {
            "sim-baseline",
            "sim-split-packed",
            "full-baseline",
            "full-split-packed"
        };
        private string[] ActiveFrameScenarios => cpuExperiment.compareAllFormations ? AllFormationScenarios
            : cpuExperiment.compareSplit ? SplitFrameScenarios
            : FrameScenarios;
        private int FrameScenarioCount => ActiveFrameScenarios.Length * (cpuExperiment.compareAllFormations ? FormationNames.Length : 1);

        private static readonly string[] ModeNames =
        {
            "Full battle",
            "Render only",
            "Simulation only"
        };
        private static readonly string[] FormationNames =
        {
            "Front",
            "Islands",
            "Pincer",
            "Dense stress"
        };
        private static readonly string[] ProfileNames =
        {
            "RedBlue.Frame.Full",
            "RedBlue.Frame.RenderOnly",
            "RedBlue.Frame.SimulationOnly"
        };
        /// <summary>切换到固定的 50k 手机测试场景；sustained=true 时关闭伤害以保持人口。</summary>
        public void ProfileScenario(int profileMode, int profileFormation = 1, bool sustained = true)
        {
            count = 50000;
            formation = Mathf.Clamp(profileFormation, 0, 3);
            mode = Mathf.Clamp(profileMode, 0, 2);
            speed = 1;
            zoom = 1;
            paused = false;
            zeroDamage = sustained;
            loop = false;
            Restart();
            UnityEngine.Debug.Log($"REDBLUE_PROFILE_SCENARIO: count={count}, formation={formation}/{FormationNames[formation]}, mode={mode}/{ModeNames[mode]}, seed=7241, damage={!zeroDamage}, speed=1, targetFps={Application.targetFrameRate}, screen={Screen.width}x{Screen.height}, device={SystemInfo.deviceModel}, gpu={SystemInfo.graphicsDeviceName}, development={UnityEngine.Debug.isDebugBuild}, simulation={simulation.Backend}, renderer={rendererView.Backend}");
        }

        /// <summary>创建唯一演示实例，避免启动事件重复触发时叠加场景。</summary>
        public static void Open()
        {
            if (FindObjectOfType<RedBlueDemo>() != null)
                return;
            new GameObject("ET RedBlue Demo").AddComponent<RedBlueDemo>();
        }

        /// <summary>加载实验配置，创建相机和界面，并开始默认场景。</summary>
        private void Awake()
        {
            cpuExperiment = RedBlueCpuExperiment.Load();
            CrowdSimulation.AllowMiniGameParallel = cpuExperiment.allowMiniGameParallel;
            // QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            viewCamera = new GameObject("RedBlue Camera").AddComponent<Camera>();
            viewCamera.transform.SetParent(transform);
            viewCamera.transform.position = new Vector3(900, 550, -10);
            viewCamera.orthographic = true;
            viewCamera.clearFlags = CameraClearFlags.SolidColor;
            viewCamera.backgroundColor = new Color(.055f, .075f, .10f);
            viewCamera.nearClipPlane = .1f;
            viewCamera.farClipPlane = 100;
            viewCamera.depth = 100;
            rendererView = new CrowdRenderer();
            CreateUI();
            Restart();
        }

        /// <summary>结束当前测试、释放旧模拟，再按当前阵型和实验开关重建状态。</summary>
        private void Restart()
        {
            StopFrameBenchmark();
            cpuBenchmark?.Dispose();
            cpuBenchmark = null;
            simulation?.Dispose();
            CrowdExecution selected = execution == CrowdExecution.Auto
                && (cpuExperiment.preferPacked || cpuExperiment.preferPackedBounded)
                && count == 50000
                    ? (cpuExperiment.preferPackedBounded
                        ? CrowdExecution.BurstPackedBoundedSingleThread
                        : CrowdExecution.BurstPackedSingleThread)
                    : execution;
            simulation = new CrowdSimulation(count, formation, 7241, selected)
            {
                ZeroDamage = zeroDamage
            };
            splitTicks = cpuExperiment.splitSimulationTicks && count == 50000;
            stepScheduler.Reset();
            SetLogicOnlyRendering(mode == 2);
            accumulator = sampleTime = uiTime = finishedTime = 0;
            sampleTick = frameCursor = frameCount = 0;
        }

        /// <summary>纯逻辑模式禁用现有相机；退出时恢复记录的相机，并用 UI 背景避免残影。</summary>
        private void SetLogicOnlyRendering(bool logicOnly)
        {
            if (logicOnly == camerasSuppressed)
                return;
            camerasSuppressed = logicOnly;
            if (logicBackground != null)
                logicBackground.SetActive(logicOnly);
            if (logicOnly)
            {
                foreach (Camera camera in Camera.allCameras)
                {
                    suppressedCameras.Add(camera);
                    camera.enabled = false;
                }
            }
            else
            {
                foreach (Camera camera in suppressedCameras)
                    if (camera != null)
                        camera.enabled = true;
                suppressedCameras.Clear();
            }
        }

        /// <summary>推进测试或常规模拟，处理视角输入，提交渲染并采集本帧统计。</summary>
        private void Update()
        {
            if (suspended || simulation == null)
                return;
            if (frameBenchmark != null)
            {
                if (UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled)
                    UnityEngine.Profiling.Profiler.enabled = false;
                if (frameBenchmark.Complete)
                {
                    if (++frameScenario < FrameScenarioCount)
                        BeginFrameScenario();
                    else
                    {
                        StopFrameBenchmark();
                        execution = CrowdExecution.Auto;
                        UnityEngine.Debug.Log("REDBLUE_FRAME_SUITE_COMPLETE:" + frameSession);
                    }
                }
            }

            using var profile = RedBlueProfiler.Measure(ProfileNames[mode]);
            float dt = UnityEngine.Time.unscaledDeltaTime;
            frames[frameCursor++ % frames.Length] = dt;
            frameCount = Math.Min(frames.Length, frameCount + 1);
            // CPU 测试独占模拟，跳过下方正常调度和单位绘制；其 FPS 不能代表完整场景。
            if (cpuBenchmark != null)
            {
                CrowdSimulation previous = simulation;
                cpuBenchmark.Advance();
                simulation = cpuBenchmark.Current;
                formation = cpuBenchmark.Formation;
                if (!ReferenceEquals(previous, simulation))
                {
                    sampleTick = 0;
                    sampleTime = 0;
                }

                sampleTime += dt;
                uiTime += dt;
                if (uiTime >= .5f)
                    UpdateStats();
                return;
            }

            // 返回值只计完整提交的 tick；分帧首段耗时也属于本显示帧的模拟成本。
            int frameSteps = 0;
            long simulationStamp = Stopwatch.GetTimestamp();
            if (!paused && mode != 1 && !simulation.Finished)
            {
                frameSteps = stepScheduler.Advance(simulation, dt, speed, splitTicks);
                accumulator = (float)stepScheduler.Accumulator;
            }

            double simulationCost = (Stopwatch.GetTimestamp() - simulationStamp) * 1000.0 / Stopwatch.Frequency;
            if (simulation.Finished && !paused && mode != 1)
                finishedTime += dt;
            if (loop && finishedTime > 2 && !paused)
                Restart();
            if (simulation.Finished && !paused)
                for (int i = 0; i < simulation.Count; i++)
                    simulation.Death[i] = Mathf.Max(0, simulation.Death[i] - dt * speed / 4);
            float scroll = Input.mouseScrollDelta.y;
            BaseInput input = UnityEventSystem.current?.currentInputModule?.inputOverride;
            int touches = input != null ? input.touchCount : Input.touchCount;
            if (touches >= 2)
            {
                Touch a = input != null ? input.GetTouch(0) : Input.GetTouch(0), b = input != null ? input.GetTouch(1) : Input.GetTouch(1);
                float distance = Vector2.Distance(a.position, b.position);
                if (previousPinch > 0)
                    zoom = Mathf.Clamp(zoom * previousPinch / Mathf.Max(1, distance), .25f, 2);
                previousPinch = distance;
            }
            else
                previousPinch = 0;
            zoom = Mathf.Clamp(zoom - scroll * .05f, .25f, 2);
            viewCamera.orthographicSize = Mathf.Max(650, 950 / viewCamera.aspect) * zoom;
            long stamp = Stopwatch.GetTimestamp();
            if (mode != 2)
                rendererView.Draw(
                    simulation, viewCamera,
                    paused || mode == 1 || simulation.Finished ? 1 : Mathf.Clamp01(accumulator / CrowdSimulation.StepSeconds));
            drawMs = (float)((Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency);
            sampleTime += dt;
            uiTime += dt;
            if (uiTime >= .5f)
                UpdateStats();
            frameBenchmark?.Observe(
                dt, frameSteps, simulationCost, drawMs,
                simulation, rendererView, mode != 2, splitTicks, stepScheduler.Accumulator);
        }

        /// <summary>刷新滚动 FPS、阶段耗时及模拟频率；界面显示值与正式回传窗口分开统计。</summary>
        private void UpdateStats()
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.StatsUI");
            uiTime = 0;
            Array.Copy(frames, sorted, frameCount);
            Array.Sort(sorted, 0, frameCount);
            float total = 0;
            for (int i = 0; i < frameCount; i++)
                total += frames[i];
            float hz = (simulation.Tick - sampleTick) / Mathf.Max(.001f, sampleTime);
            string outcome = simulation.Finished
                ? (simulation.Alive[0] == 0 ? simulation.Alive[1] == 0 ? "Draw" : "Blue wins" : "Red wins")
                : "Battle";
            stats.text = $"RED / BLUE  |  {outcome}{(paused ? " / PAUSED" : "")}\n"
                + $"Alive {simulation.Alive[0]:N0} / {simulation.Alive[1]:N0}    Drawn {(mode == 2 ? 0 : rendererView.Drawn):N0}\n"
                + $"FPS {frameCount / Mathf.Max(.001f, total):F1}   P95 {sorted[Mathf.Max(0, (int)Math.Ceiling(frameCount * .95) - 1)] * 1000:F1} ms\n"
                + $"Simulation {hz:F1} Hz / target {30 * speed:F0}   realtime {hz / (30 * speed):P0}\n"
                + $"Grid {simulation.GridMs:F2} / Separate {simulation.SeparationMs:F2} / Decide {simulation.DecisionMs:F2} / Commit {simulation.CommitMs:F2} ms\n"
                + $"Submit {drawMs:F2} ms   calls {(mode == 2 ? 0 : rendererView.DrawCalls)}   managed {GC.GetTotalMemory(false) / 1048576f:F1} MB\n"
                + $"{ModeNames[mode]} | {FormationNames[formation]} | {speed}x | damage {(zeroDamage ? "OFF" : "ON")} | loop {(loop ? "ON" : "OFF")}\n"
                + $"{simulation.Backend} (verified {simulation.AccelerationVerified}) | {rendererView.Backend}"
                + (cpuBenchmark == null ? "" : "\n" + cpuBenchmark.Status)
                + (frameBenchmark == null ? "" : $"\nFrame compare {frameScenario + 1}/{FrameScenarioCount}");
            sampleTime = 0;
            sampleTick = simulation.Tick;
        }

        /// <summary>创建演示控制按钮和统计面板，注册场景与性能测试入口。</summary>
        private void CreateUI()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasObject = new GameObject("RedBlue Controls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            // 纯逻辑模式没有相机清屏，因此覆盖一层不透明背景。
            logicBackground = new GameObject("Logic background", typeof(RectTransform), typeof(Image));
            logicBackground.transform.SetParent(canvasObject.transform, false);
            RectTransform backgroundRect = logicBackground.GetComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;
            Image background = logicBackground.GetComponent<Image>();
            background.color = viewCamera.backgroundColor;
            background.raycastTarget = false;
            logicBackground.SetActive(false);
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0;
            if (UnityEventSystem.current == null)
            {
                GameObject events = new GameObject("RedBlue Events", typeof(UnityEventSystem), typeof(StandaloneInputModule));
                events.transform.SetParent(transform);
            }

            stats = Label("", 16, 14, 1248, 200, 18);
            int column = 0, row = 0;
            Action<string, UnityEngine.Events.UnityAction> button = (title, action) =>
            {
                int x = 16 + column * 156, y = 116 - row * 50;
                GameObject obj = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Button));
                obj.transform.SetParent(canvasObject.transform, false);
                Place(obj.GetComponent<RectTransform>(), x, y, 148, 42);
                RectTransform buttonRect = obj.GetComponent<RectTransform>();
                buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = Vector2.zero;
                buttonRect.anchoredPosition = new Vector2(x, y);
                obj.GetComponent<Image>().color = new Color(.14f, .20f, .29f, .95f);
                obj.GetComponent<Button>().onClick.AddListener(action);
                Text text = Label(title, 0, 0, 148, 42, 18, obj.transform);
                text.alignment = TextAnchor.MiddleCenter;
                if (++column == 8)
                {
                    column = 0;
                    row++;
                }
            };
            foreach (int population in new[] { 2000, 10000, 20000, 50000 })
            {
                int value = population;
                button($"{population:N0} units", () =>
                {
                    count = value;
                    Restart();
                });
            }

            button("Pause / resume", () => paused = !paused);
            button("Restart", Restart);
            button("Speed 1 / 2x", () =>
            {
                speed = speed == 1 ? 2 : 1;
                stepScheduler.Reset();
                accumulator = sampleTime = 0;
                sampleTick = simulation.Tick;
            });
            button("Formation", () =>
            {
                formation = (formation + 1) % 4;
                Restart();
            });
            button("Mode", () =>
            {
                mode = (mode + 1) % 3;
                Restart();
            });
            button("Damage on/off", () =>
            {
                zeroDamage = !zeroDamage;
                Restart();
            });
            button("Loop on/off", () => loop = !loop);
            button("Zoom +", () => zoom = Mathf.Max(.25f, zoom * .8f));
            button("Zoom -", () => zoom = Mathf.Min(2, zoom / .8f));
            button("50k Islands full", () => ProfileScenario(0));
            button("50k Islands sim", () => ProfileScenario(2));
            button("50k Islands draw", () => ProfileScenario(1));
            button("CPU compare", StartCpuBenchmark);
            button("Frame compare", StartFrameBenchmark);
        }

        /// <summary>建立新的整帧测试会话，从第一个场景开始自动轮换。</summary>
        private void StartFrameBenchmark()
        {
            StopFrameBenchmark();
            frameProfilerWasEnabled = UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled;
            frameSession = Guid.NewGuid().ToString("N");
            frameScenario = 0;
            BeginFrameScenario();
        }

        /// <summary>停止整帧测试并恢复进入测试前的 Profiler 开关。</summary>
        private void StopFrameBenchmark()
        {
            if (frameBenchmark == null)
                return;
            frameBenchmark = null;
            execution = CrowdExecution.Auto;
            if (UnityEngine.Debug.isDebugBuild)
                UnityEngine.Profiling.Profiler.enabled = frameProfilerWasEnabled;
        }

        /// <summary>按阶段索引选择阵型和执行路径；重开后再覆盖 split 开关以保留同包对照。</summary>
        private void BeginFrameScenario()
        {
            frameBenchmark = null;
            string scenario = ActiveFrameScenarios[frameScenario % ActiveFrameScenarios.Length];
            execution = scenario.Contains("packed") ? CrowdExecution.BurstPackedSingleThread : CrowdExecution.BurstSingleThread;
            count = 50000;
            formation = cpuExperiment.compareAllFormations ? frameScenario / ActiveFrameScenarios.Length : 1;
            mode = scenario == "draw-only" ? 1 : scenario.StartsWith("sim-") ? 2 : 0;
            speed = zoom = 1;
            paused = false;
            zeroDamage = !scenario.Contains("battle");
            loop = false;
            if (UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled)
                UnityEngine.Profiling.Profiler.enabled = false;
            long stamp = Stopwatch.GetTimestamp();
            Restart();
            double restartCost = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
            splitTicks = scenario.Contains("split");
            frameBenchmark = new RedBlueFrameBenchmark(cpuExperiment, frameSession, scenario, restartCost, result =>
            {
                string json = JsonUtility.ToJson(result);
                UnityEngine.Debug.Log("REDBLUE_FRAME_BENCHMARK:" + json);
                if (!string.IsNullOrEmpty(cpuExperiment.resultUrl))
                    StartCoroutine(UploadCpuBenchmark(json, cpuExperiment.resultUrl.Replace("/benchmark", "/frame-benchmark")));
            }, formation);
            UnityEngine.Debug.Log("REDBLUE_FRAME_SCENARIO:" + scenario + ";formation=" + formation + ";session=" + frameSession);
        }

        /// <summary>启动自带模拟实例的 CPU 测试，关闭单位渲染并暂停普通场景推进。</summary>
        private void StartCpuBenchmark()
        {
            StopFrameBenchmark();
            cpuBenchmark?.Dispose();
            simulation?.Dispose();
            count = 50000;
            formation = 1;
            mode = 2;
            speed = 1;
            paused = true;
            zeroDamage = true;
            loop = false;
            SetLogicOnlyRendering(true);
            accumulator = sampleTime = uiTime = 0;
            sampleTick = frameCursor = frameCount = 0;
            cpuBenchmark = new RedBlueCpuBenchmark(cpuExperiment, result =>
            {
                string json = JsonUtility.ToJson(result);
                UnityEngine.Debug.Log("REDBLUE_CPU_BENCHMARK:" + json);
                if (!string.IsNullOrEmpty(cpuExperiment.resultUrl))
                    StartCoroutine(UploadCpuBenchmark(json));
            });
            simulation = cpuBenchmark.Current;
        }

        /// <summary>在计时段之外回传 JSON；上传失败时完整结果仍保留在日志中。</summary>
        private System.Collections.IEnumerator UploadCpuBenchmark(string json, string url = null)
        {
            using (var request = new UnityEngine.Networking.UnityWebRequest(url ?? cpuExperiment.resultUrl, "POST"))
            {
                request.timeout = 15;
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();
                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    UnityEngine.Debug.LogError("CPU benchmark upload failed: " + request.error + "; JSON remains in Player log");
                else
                    UnityEngine.Debug.Log("CPU benchmark upload succeeded: " + cpuExperiment.variant);
            }
        }

        /// <summary>创建不拦截点击的文本控件，坐标沿用面板的固定参考分辨率。</summary>
        private Text Label(string value, int x, int y, int width, int height, int size, Transform parent = null)
        {
            GameObject obj = new GameObject("Label", typeof(RectTransform), typeof(Text));
            obj.transform.SetParent(parent == null ? canvasObject.transform : parent, false);
            Place(obj.GetComponent<RectTransform>(), x, y, width, height);
            Text text = obj.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.text = value;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>以父节点左上角为锚点放置控件。</summary>
        private static void Place(RectTransform rect, int x, int y, int width, int height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>暂停时标记当前测试失焦，并清除调度时间，避免恢复后补算后台时长。</summary>
        private void OnApplicationPause(bool value)
        {
            if (value)
                frameBenchmark?.InvalidateFocus();
            suspended = value;
            stepScheduler.Reset();
            accumulator = sampleTime = 0;
            if (simulation != null)
                sampleTick = simulation.Tick;
        }

        /// <summary>同步焦点状态；一次失焦会使当前场景的后续回传标记为非全程前台。</summary>
        private void OnApplicationFocus(bool value)
        {
            if (!value)
                frameBenchmark?.InvalidateFocus();
            suspended = !value;
            stepScheduler.Reset();
            accumulator = sampleTime = 0;
            if (simulation != null)
                sampleTick = simulation.Tick;
        }

        /// <summary>恢复相机及 Profiler 状态，释放测试、模拟和渲染资源。</summary>
        private void OnDestroy()
        {
            SetLogicOnlyRendering(false);
            StopFrameBenchmark();
            cpuBenchmark?.Dispose();
            simulation?.Dispose();
            rendererView?.Dispose();
        }
    }
}
