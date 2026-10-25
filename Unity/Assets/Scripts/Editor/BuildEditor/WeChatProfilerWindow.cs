using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace ET
{
    public sealed class WeChatProfilerWindow : EditorWindow
    {
        [SerializeField] private string output = "BuildArtifacts/WeChat-Profiler", label = "islands-50k-full", device = "", buildManifest = "";
        private string status = "Build a Development player, open Profiler, then scan with phone WeChat.";
        [SerializeField] private float seconds = 5;
        private bool recording, oldEnabled, oldDeep, oldEditor;
        private bool[] oldAreas;
        private int target, arrived, firstArrived = -1, lastArrived = -1;
        private double deadline, started, lastFrameTime;
        private string connection, captureDirectory;
        private int oldFrameLimit, captureFrameLimit;
        private static readonly System.Reflection.PropertyInfo FrameLimitProperty = typeof(ProfilerDriver).Assembly
            .GetType("UnityEditor.Profiling.ProfilerUserSettings")?.GetProperty("frameCount",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        [SerializeField] private string requiredMarker = "";
        private bool armed, armEnabled, armEditor, armDeep, armCpu, scenarioMismatch;
        private int matchingFrames, waitingTarget;
        private double armDeadline;

        [MenuItem("ET/WeChat/Profiler Pipeline")]
        public static void Open() => GetWindow<WeChatProfilerWindow>("WeChat Profiler");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Standard Unity Profiler / phone WeChat", EditorStyles.boldLabel);
            output = EditorGUILayout.TextField("Build output (Unity relative)", output);
            using (new EditorGUI.DisabledScope(WeChatProfilerBuild.Running || recording || armed))
                if (GUILayout.Button("Build Development + Autoconnect (ET + HybridCLR + resources)"))
                    Run(() => { WeChatProfilerBuild.Queue(output, true, true); buildManifest = Path.GetFullPath(Path.Combine(output, "profiler-build.json")); status = "Build queued: " + WeChatProfilerBuild.StatusPath; });
            if (GUILayout.Button("Open Unity Profiler")) EditorApplication.ExecuteMenuItem("Window/Analysis/Profiler");
            EditorGUILayout.HelpBox("Keep Editor Play mode stopped. The phone connects to the Editor automatically. " +
                "Choose the MiniGame Player in Profiler. Deep Profile is disabled by default. " +
                "Default 5-second captures limit ring-buffer truncation; increase Profiler frame count for longer captures.", MessageType.Info);
            int current = ProfilerDriver.connectedProfiler;
            EditorGUILayout.LabelField("Connected target", ProfilerDriver.GetConnectionIdentifier(current) ?? "unknown");
            using (new EditorGUI.DisabledScope(recording || armed))
            {
                label = EditorGUILayout.TextField("Scenario label", label);
                device = EditorGUILayout.TextField("Phone / WeChat / conditions", device);
                buildManifest = EditorGUILayout.TextField("profiler-build.json path", buildManifest);
                if (GUILayout.Button("Select build manifest"))
                {
                    string selected = EditorUtility.OpenFilePanel("Select profiler-build.json", Path.GetFullPath(output), "json");
                    if (!string.IsNullOrEmpty(selected)) buildManifest = selected;
                }
                seconds = EditorGUILayout.Slider("Capture seconds", seconds, 1, 30);
                requiredMarker = EditorGUILayout.TextField("Required CPU marker (optional)", requiredMarker);
            }
            using (new EditorGUI.DisabledScope(recording || armed || WeChatProfilerBuild.Running))
            {
                if (GUILayout.Button("Capture connected Player → .data + JSON + Markdown")) Run(StartCapture);
                if (GUILayout.Button("Wait for Player / scenario, then capture")) Run(() => ArmCapture());
            }
            if (armed && GUILayout.Button("Cancel waiting")) StopWaiting();
            using (new EditorGUI.DisabledScope(!recording))
                if (GUILayout.Button("Stop and save current capture")) Run(() => Finish("stopped manually"));
            using (new EditorGUI.DisabledScope(recording || armed || ProfilerDriver.enabled))
                if (GUILayout.Button("Analyze existing .data (offline)")) Run(AnalyzeFile);
            EditorGUILayout.HelpBox(status, MessageType.None);
            if (WeChatProfilerBuild.Running && File.Exists(WeChatProfilerBuild.StatusPath))
                EditorGUILayout.LabelField(File.ReadAllText(WeChatProfilerBuild.StatusPath), EditorStyles.wordWrappedLabel);
        }

        // Wait on fresh remote frames, not a stale connection identifier or old buffer.
        public void ArmCapture(string expectedMarker = null, float timeoutSeconds = 180)
        {
            if (armed || recording || WeChatProfilerBuild.Running || EditorApplication.isPlaying)
                throw new InvalidOperationException("Stop the current capture/build/Play mode before arming.");
            if (!string.IsNullOrWhiteSpace(buildManifest) && !File.Exists(buildManifest))
                throw new FileNotFoundException("Build manifest not found: " + buildManifest);
            if (expectedMarker != null) requiredMarker = expectedMarker;
            armEnabled = ProfilerDriver.enabled; armEditor = ProfilerDriver.profileEditor;
            armDeep = ProfilerDriver.deepProfiling; armCpu = Profiler.GetAreaEnabled(ProfilerArea.CPU);
            matchingFrames = 0; waitingTarget = -1;
            armDeadline = EditorApplication.timeSinceStartup + Math.Max(1, timeoutSeconds);
            armed = true;
            ProfilerDriver.NewProfilerFrameRecorded += OnWaitingFrame;
            EditorApplication.update += WaitTick;
            ProfilerDriver.profileEditor = false; ProfilerDriver.deepProfiling = false;
            Profiler.SetAreaEnabled(ProfilerArea.CPU, true); ProfilerDriver.enabled = true;
            status = "Waiting for 3 fresh remote frames" + (string.IsNullOrEmpty(requiredMarker) ? "" : ": " + requiredMarker);
        }

        private static bool HasMarker(int frame, string marker)
        {
            if (string.IsNullOrEmpty(marker)) return true;
            using var view = ProfilerDriver.GetRawFrameDataView(frame, 0);
            if (!view.valid) return false;
            for (int i = 0; i < view.sampleCount; i++) if (view.GetSampleName(i) == marker) return true;
            return false;
        }
        private void OnWaitingFrame(int connectionId, int frame)
        {
            if (connectionId == -1) return;
            if (connectionId != waitingTarget) { waitingTarget = connectionId; matchingFrames = 0; }
            matchingFrames = HasMarker(frame, requiredMarker) ? matchingFrames + 1 : 0;
        }
        private void WaitTick()
        {
            if (EditorApplication.timeSinceStartup > armDeadline)
            { StopWaiting(); status = "Timed out waiting for remote Player / required scenario. No capture saved."; return; }
            if (matchingFrames >= 3)
            {
                int readyTarget = waitingTarget;
                StopWaiting();
                ProfilerDriver.connectedProfiler = readyTarget;
                Run(StartCapture);
            }
            Repaint();
        }
        private void StopWaiting()
        {
            ProfilerDriver.NewProfilerFrameRecorded -= OnWaitingFrame;
            EditorApplication.update -= WaitTick;
            if (!armed) return;
            ProfilerDriver.enabled = armEnabled; ProfilerDriver.profileEditor = armEditor;
            ProfilerDriver.deepProfiling = armDeep; Profiler.SetAreaEnabled(ProfilerArea.CPU, armCpu);
            armed = false;
            status = "Waiting cancelled.";
        }

        public void StartCapture()
        {
            if (recording || armed) throw new InvalidOperationException("A capture or waiting operation is already running.");
            target = ProfilerDriver.connectedProfiler;
            connection = ProfilerDriver.GetConnectionIdentifier(target);
            if (target == -1 || string.IsNullOrEmpty(connection) || connection.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("No remote Player selected. Select the phone MiniGame Player in Unity Profiler first.");
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Editor Play mode to avoid ambiguous capture provenance.");
            if (!string.IsNullOrWhiteSpace(buildManifest) && !File.Exists(buildManifest))
                throw new FileNotFoundException("Build manifest not found: " + buildManifest);
            string safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safeLabel)) safeLabel = "capture";
            captureDirectory = Path.GetFullPath(Path.Combine("../Temp/WeChatProfiler", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + safeLabel));
            Directory.CreateDirectory(captureDirectory);
            oldEnabled = ProfilerDriver.enabled; oldDeep = ProfilerDriver.deepProfiling; oldEditor = ProfilerDriver.profileEditor;
            oldAreas = new bool[Enum.GetValues(typeof(ProfilerArea)).Length];
            for (int i = 0; i < oldAreas.Length; i++) oldAreas[i] = Profiler.GetAreaEnabled((ProfilerArea)i);
            recording = true;
            try
            {
                ProfilerDriver.enabled = false;
                ProfilerDriver.deepProfiling = false; ProfilerDriver.profileEditor = false;
                oldFrameLimit = FrameLimitProperty?.GetValue(null) is int limit ? limit : 0;
                if (oldFrameLimit > 0)
                    FrameLimitProperty.SetValue(null, Math.Min(2000, Math.Max(oldFrameLimit, (int)Math.Ceiling(seconds * 60) + 60)));
                captureFrameLimit = FrameLimitProperty?.GetValue(null) is int actualLimit ? actualLimit : 0;
                for (int i = 0; i < oldAreas.Length; i++)
                    Profiler.SetAreaEnabled((ProfilerArea)i, i == (int)ProfilerArea.CPU || i == (int)ProfilerArea.Rendering || i == (int)ProfilerArea.Memory);
                ProfilerDriver.ClearAllFrames();
                arrived = 0; firstArrived = lastArrived = -1;
                scenarioMismatch = false;
                started = EditorApplication.timeSinceStartup; deadline = started + seconds;
                ProfilerDriver.NewProfilerFrameRecorded += OnFrame;
                EditorApplication.update += Tick;
                ProfilerDriver.enabled = true;
                status = "Waiting for remote frames: " + connection;
            }
            catch { Restore(); throw; }
        }

        private void OnFrame(int connectionId, int frameIndex)
        {
            if (connectionId != target) return;
            if (!HasMarker(frameIndex, requiredMarker)) scenarioMismatch = true;
            if (firstArrived < 0) { firstArrived = frameIndex; deadline = EditorApplication.timeSinceStartup + seconds; }
            lastArrived = frameIndex; arrived++;
            lastFrameTime = EditorApplication.timeSinceStartup;
        }
        private void Tick()
        {
            if (scenarioMismatch) { Run(() => Finish("required scenario marker missing; partial")); return; }
            if (ProfilerDriver.connectedProfiler != target) { Run(() => Finish("target changed/disconnected; partial")); return; }
            if (arrived == 0 && EditorApplication.timeSinceStartup - started > 15) { Run(() => Finish("no remote frames within 15 seconds")); return; }
            if (arrived > 0 && EditorApplication.timeSinceStartup - lastFrameTime > 3) { Run(() => Finish("remote frames stalled; partial")); return; }
            if (arrived > 0 && EditorApplication.timeSinceStartup >= deadline) Run(() => Finish("completed"));
            Repaint();
        }
        private void Finish(string reason)
        {
            if (!recording) return;
            try
            {
                ProfilerDriver.enabled = false;
                int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
                bool hasData = arrived > 0 && first >= 0 && last >= first;
                File.WriteAllText(Path.Combine(captureDirectory, "capture.json"), JsonConvert.SerializeObject(new
                {
                    schema = 1, utc = DateTime.UtcNow.ToString("o"), label, device, connection, target,
                    reason, valid = hasData && reason == "completed", requestedSeconds = seconds,
                    wallSeconds = EditorApplication.timeSinceStartup - started, receivedFrames = arrived,
                    firstArrived, lastArrived, retainedFirst = first, retainedLast = last,
                    ringBufferTruncated = hasData && first > firstArrived,
                    editorVersion = Application.unityVersion, deepProfiling = false,
                    gpuCaptureRequested = false, buildManifest, requiredMarker, scenarioMismatch, captureFrameLimit
                }, Formatting.Indented));
                if (!string.IsNullOrWhiteSpace(buildManifest) && File.Exists(buildManifest))
                    File.Copy(buildManifest, Path.Combine(captureDirectory, "profiler-build.json"));
                if (hasData)
                {
                    ProfilerDriver.SaveProfile(Path.Combine(captureDirectory, "capture.data"));
                    WeChatProfilerReport.AnalyzeLoaded(captureDirectory);
                }
                status = reason + "; received " + arrived + " frames; " + captureDirectory;
                Debug.Log("WECHAT_PROFILE: " + status);
            }
            finally { Restore(); }
        }
        private void Restore()
        {
            ProfilerDriver.NewProfilerFrameRecorded -= OnFrame;
            EditorApplication.update -= Tick;
            if (!recording) return;
            ProfilerDriver.enabled = false;
            if (oldFrameLimit > 0 && oldFrameLimit != captureFrameLimit) FrameLimitProperty?.SetValue(null, oldFrameLimit);
            if (oldAreas != null) for (int i = 0; i < oldAreas.Length; i++) Profiler.SetAreaEnabled((ProfilerArea)i, oldAreas[i]);
            ProfilerDriver.deepProfiling = oldDeep; ProfilerDriver.profileEditor = oldEditor; ProfilerDriver.enabled = oldEnabled;
            recording = false;
        }
        private void OnDisable() { StopWaiting(); if (recording) Run(() => Finish("window closed/domain reload; partial")); }
        private void AnalyzeFile()
        {
            string path = EditorUtility.OpenFilePanel("Load Unity Profiler .data", Path.GetFullPath("../Temp"), "data");
            if (string.IsNullOrEmpty(path)) return;
            if (!ProfilerDriver.LoadProfile(path, false)) throw new IOException("Failed to load " + path);
            status = WeChatProfilerReport.AnalyzeLoaded(Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-analysis"));
        }
        private void Run(Action action)
        {
            try { action(); }
            catch (Exception e) { status = e.Message; Debug.LogException(e); }
        }
    }
}
