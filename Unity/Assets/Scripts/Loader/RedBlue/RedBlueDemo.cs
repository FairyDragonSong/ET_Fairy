using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEventSystem = UnityEngine.EventSystems.EventSystem;

namespace ET.RedBlue
{
    // A single view driver, opened by the existing ET AppStartInitFinish event.
    public sealed class RedBlueDemo : MonoBehaviour
    {
        private CrowdSimulation simulation;
        private CrowdRenderer rendererView;
        private Camera viewCamera;
        private GameObject canvasObject;
        private Text stats;
        private Font font;
        private readonly float[] frames = new float[300], sorted = new float[300];
        private int frameCursor, frameCount, count = 2000, formation, mode;
        private int sampleTick;
        private float accumulator, speed = 1, sampleTime, uiTime, drawMs, zoom = 1, finishedTime;
        private bool paused, zeroDamage, loop = true, suspended;
        private float previousPinch;
        private static readonly string[] ModeNames = { "Full battle", "Render only", "Simulation only" };
        private static readonly string[] FormationNames = { "Front", "Islands", "Pincer", "Dense stress" };

        public static void Open()
        {
            if (FindObjectOfType<RedBlueDemo>() != null) return;
            new GameObject("ET RedBlue Demo").AddComponent<RedBlueDemo>();
        }
        private void Awake()
        {
            Application.targetFrameRate = 60;
            viewCamera = new GameObject("RedBlue Camera").AddComponent<Camera>();
            viewCamera.transform.SetParent(transform); viewCamera.transform.position = new Vector3(900, 550, -10);
            viewCamera.orthographic = true; viewCamera.clearFlags = CameraClearFlags.SolidColor;
            viewCamera.backgroundColor = new Color(.055f, .075f, .10f);
            viewCamera.nearClipPlane = .1f; viewCamera.farClipPlane = 100; viewCamera.depth = 100;
            rendererView = new CrowdRenderer(); CreateUI(); Restart();
        }
        private void Restart()
        {
            simulation?.Dispose();
            simulation = new CrowdSimulation(count, formation) { ZeroDamage = zeroDamage };
            accumulator = sampleTime = uiTime = finishedTime = 0; sampleTick = frameCursor = frameCount = 0;
        }
        private void Update()
        {
            if (suspended || simulation == null) return;
            float dt = UnityEngine.Time.unscaledDeltaTime;
            frames[frameCursor++ % frames.Length] = dt; frameCount = Math.Min(frames.Length, frameCount + 1);
            if (!paused && mode != 1 && !simulation.Finished)
            {
                accumulator = Math.Min(accumulator + dt * speed, CrowdSimulation.StepSeconds * 8);
                int steps = 0;
                while (accumulator >= CrowdSimulation.StepSeconds && steps++ < 4)
                { simulation.Step(); accumulator -= CrowdSimulation.StepSeconds; }
            }
            if (simulation.Finished && !paused && mode != 1) finishedTime += dt;
            if (loop && finishedTime > 2 && !paused) Restart();
            if (simulation.Finished && !paused)
                for (int i = 0; i < simulation.Count; i++) simulation.Death[i] = Mathf.Max(0, simulation.Death[i] - dt * speed / 4);
            float scroll = Input.mouseScrollDelta.y;
            BaseInput input = UnityEventSystem.current?.currentInputModule?.inputOverride;
            int touches = input != null ? input.touchCount : Input.touchCount;
            if (touches >= 2)
            {
                Touch a = input != null ? input.GetTouch(0) : Input.GetTouch(0), b = input != null ? input.GetTouch(1) : Input.GetTouch(1);
                float distance = Vector2.Distance(a.position, b.position);
                if (previousPinch > 0) zoom = Mathf.Clamp(zoom * previousPinch / Mathf.Max(1, distance), .25f, 2);
                previousPinch = distance;
            }
            else previousPinch = 0;
            zoom = Mathf.Clamp(zoom - scroll * .05f, .25f, 2);
            viewCamera.orthographicSize = Mathf.Max(650, 950 / viewCamera.aspect) * zoom;
            long stamp = Stopwatch.GetTimestamp();
            if (mode != 2) rendererView.Draw(simulation, viewCamera, paused || mode == 1 || simulation.Finished ? 1 : Mathf.Clamp01(accumulator / CrowdSimulation.StepSeconds));
            drawMs = (float)((Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency);
            sampleTime += dt; uiTime += dt;
            if (uiTime >= .5f) UpdateStats();
        }
        private void UpdateStats()
        {
            uiTime = 0; Array.Copy(frames, sorted, frameCount); Array.Sort(sorted, 0, frameCount);
            float total = 0; for (int i = 0; i < frameCount; i++) total += frames[i];
            float hz = (simulation.Tick - sampleTick) / Mathf.Max(.001f, sampleTime);
            string outcome = simulation.Finished ? (simulation.Alive[0] == 0 ? simulation.Alive[1] == 0 ? "Draw" : "Blue wins" : "Red wins") : "Battle";
            stats.text = $"RED / BLUE  |  {outcome}{(paused ? " / PAUSED" : "")}\n" +
                $"Alive {simulation.Alive[0]:N0} / {simulation.Alive[1]:N0}    Drawn {(mode == 2 ? 0 : rendererView.Drawn):N0}\n" +
                $"FPS {frameCount / Mathf.Max(.001f, total):F1}   P95 {sorted[Mathf.Max(0, (int)Math.Ceiling(frameCount * .95) - 1)] * 1000:F1} ms\n" +
                $"Simulation {hz:F1} Hz / target {30 * speed:F0}   realtime {hz / (30 * speed):P0}\n" +
                $"Grid {simulation.GridMs:F2} / Separate {simulation.SeparationMs:F2} / Decide {simulation.DecisionMs:F2} / Commit {simulation.CommitMs:F2} ms\n" +
                $"Submit {drawMs:F2} ms   calls {(mode == 2 ? 0 : rendererView.DrawCalls)}   managed {GC.GetTotalMemory(false) / 1048576f:F1} MB\n" +
                $"{ModeNames[mode]} | {FormationNames[formation]} | {speed}x | damage {(zeroDamage ? "OFF" : "ON")} | loop {(loop ? "ON" : "OFF")}\n" +
                $"{simulation.Backend} | {rendererView.Backend}";
            sampleTime = 0; sampleTick = simulation.Tick;
        }
        private void CreateUI()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasObject = new GameObject("RedBlue Controls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform);
            Canvas canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30000;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 800); scaler.matchWidthOrHeight = 0;
            if (UnityEventSystem.current == null) { GameObject events = new GameObject("RedBlue Events", typeof(UnityEventSystem), typeof(StandaloneInputModule)); events.transform.SetParent(transform); }
            stats = Label("", 16, 14, 1248, 200, 18);
            int column = 0, row = 0;
            Action<string, UnityEngine.Events.UnityAction> button = (title, action) =>
            {
                int x = 16 + column * 156, y = 66 - row * 50;
                GameObject obj = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Button)); obj.transform.SetParent(canvasObject.transform, false);
                Place(obj.GetComponent<RectTransform>(), x, y, 148, 42);
                RectTransform buttonRect = obj.GetComponent<RectTransform>();
                buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = Vector2.zero;
                buttonRect.anchoredPosition = new Vector2(x, y);
                obj.GetComponent<Image>().color = new Color(.14f, .20f, .29f, .95f);
                obj.GetComponent<Button>().onClick.AddListener(action);
                Text text = Label(title, 0, 0, 148, 42, 18, obj.transform); text.alignment = TextAnchor.MiddleCenter;
                if (++column == 8) { column = 0; row++; }
            };
            foreach (int population in new[] { 2000, 10000, 20000, 50000 }) { int value = population; button($"{population:N0} units", () => { count = value; Restart(); }); }
            button("Pause / resume", () => paused = !paused); button("Restart", Restart);
            button("Speed 1 / 2x", () => { speed = speed == 1 ? 2 : 1; accumulator = sampleTime = 0; sampleTick = simulation.Tick; });
            button("Formation", () => { formation = (formation + 1) % 4; Restart(); });
            button("Mode", () => { mode = (mode + 1) % 3; Restart(); });
            button("Damage on/off", () => { zeroDamage = !zeroDamage; Restart(); });
            button("Loop on/off", () => loop = !loop);
            button("Zoom +", () => zoom = Mathf.Max(.25f, zoom * .8f)); button("Zoom -", () => zoom = Mathf.Min(2, zoom / .8f));
        }
        private Text Label(string value, int x, int y, int width, int height, int size, Transform parent = null)
        {
            GameObject obj = new GameObject("Label", typeof(RectTransform), typeof(Text)); obj.transform.SetParent(parent == null ? canvasObject.transform : parent, false);
            Place(obj.GetComponent<RectTransform>(), x, y, width, height);
            Text text = obj.GetComponent<Text>(); text.font = font; text.fontSize = size; text.color = Color.white; text.text = value; text.raycastTarget = false; return text;
        }
        private static void Place(RectTransform rect, int x, int y, int width, int height)
        { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
        private void OnApplicationPause(bool value) { suspended = value; accumulator = sampleTime = 0; if (simulation != null) sampleTick = simulation.Tick; }
        private void OnApplicationFocus(bool value) { suspended = !value; accumulator = sampleTime = 0; if (simulation != null) sampleTick = simulation.Tick; }
        private void OnDestroy() { simulation?.Dispose(); rendererView?.Dispose(); }
    }
}
