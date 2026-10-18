// Unity MCP execute_code method body. Profiler overhead is included. No Deep Profile.
// Run one capture at a time. Enable CPU/GPU/Rendering Profiler areas before use;
// preserve and restore the original area settings in the calling workflow.
// For camera A/B, prepend: AppDomain.CurrentDomain.SetData("RedBlue.CpuGpuProfile.CameraComparison", true);
var demo = UnityEngine.Object.FindObjectOfType<ET.RedBlue.RedBlueDemo>();
if (demo == null) return "Start ET RedBlue Play mode first";
var domain = System.AppDomain.CurrentDomain;
bool cameraComparison = object.Equals(domain.GetData("RedBlue.CpuGpuProfile.CameraComparison"), true);
var cameraStates = new System.Collections.Generic.Dictionary<UnityEngine.Camera, bool>();
foreach (var camera in UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>())
    if (camera.name == "MainCamera" || camera.name == "YIUICamera") cameraStates[camera] = camera.enabled;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var type = demo.GetType();
var simField = type.GetField("simulation", flags);
var drawField = type.GetField("drawMs", flags);
int originalVsync = UnityEngine.QualitySettings.vSyncCount, originalTarget = UnityEngine.Application.targetFrameRate;
var names = cameraComparison ? new[] { "uncapped-three-cameras", "uncapped-demo-camera-only" } : new[] { "capped-full-front", "uncapped-full-front", "uncapped-full-dense", "uncapped-render-front", "uncapped-simulation-dense" };
var modes = cameraComparison ? new[] { 0, 0 } : new[] { 0, 0, 0, 1, 2 };
var formations = cameraComparison ? new[] { 0, 0 } : new[] { 0, 0, 3, 0, 3 };
var results = new System.Collections.Generic.List<object>();
var intervals = new System.Collections.Generic.List<double>(10000);
var submits = new System.Collections.Generic.List<double>(10000);
var timings = new System.Collections.Generic.List<UnityEngine.FrameTiming>(10000);
var timingBuffer = new UnityEngine.FrameTiming[1];
var dir = System.IO.Path.GetFullPath(cameraComparison ? "../Temp/RedBlueCameraAB" : "../Temp/RedBlueProfile");
System.IO.Directory.CreateDirectory(dir);
int phase = 0, lastFrame = -1, firstFrame = 0, tickBase = 0, missed = 0, gcBase = 0;
ulong lastTimestamp = 0;
double start = 0, sampleStart = 0, gridBase = 0, sepBase = 0, decideBase = 0, commitBase = 0;
bool sampling = false;
System.Func<System.Collections.Generic.List<double>, object> summarize = values =>
{
    if (values.Count == 0) return new { count = 0, mean = 0.0, p95 = 0.0, max = 0.0 };
    values.Sort(); double sum = 0; foreach (double value in values) sum += value;
    return new { count = values.Count, mean = sum / values.Count, p95 = values[System.Math.Max(0, (int)System.Math.Ceiling(values.Count * .95) - 1)], max = values[values.Count - 1] };
};
System.Func<string, object> captureProfiler = name =>
{
    UnityEditorInternal.ProfilerDriver.enabled = false;
    UnityEditorInternal.ProfilerDriver.SaveProfile(System.IO.Path.Combine(dir, name + ".data"));
    int first = UnityEditorInternal.ProfilerDriver.firstFrameIndex, last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
    var threads = new System.Collections.Generic.List<object>();
    for (int thread = 0; thread < 512; thread++)
    {
        string threadName;
        using (var data = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(last, thread))
        {
            if (!data.valid) break;
            threadName = data.threadName ?? "(unnamed)";
        }
        // Main/render/GPU detail; workers are retained in the raw profile.
        if (threadName != "Main Thread" && threadName != "Render Thread" && threadName != "RHI Thread" && !threadName.Contains("GPU")) continue;
        var totals = new System.Collections.Generic.Dictionary<string, double[]>();
        var cpu = new System.Collections.Generic.List<double>();
        var gpu = new System.Collections.Generic.List<double>();
        int frames = 0;
        for (int frame = System.Math.Max(first, last - 239); frame <= last; frame++)
        using (var data = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, thread))
        {
            if (!data.valid || data.threadName != threadName) continue;
            frames++; cpu.Add(data.frameTimeMs); if (data.frameGpuTimeMs > 0) gpu.Add(data.frameGpuTimeMs);
            for (int sample = 0; sample < data.sampleCount; sample++)
            {
                string marker = data.GetSampleName(sample) ?? "(unnamed)";
                double inclusive = data.GetSampleTimeMs(sample), self = inclusive;
                int end = sample + data.GetSampleChildrenCountRecursive(sample);
                for (int child = sample + 1; child <= end; child += 1 + data.GetSampleChildrenCountRecursive(child)) self -= data.GetSampleTimeMs(child);
                double[] total;
                if (!totals.TryGetValue(marker, out total)) totals[marker] = total = new double[3];
                total[0] += inclusive; total[1] += System.Math.Max(0, self); total[2]++;
            }
        }
        var markers = new System.Collections.Generic.List<object>();
        foreach (var item in totals) markers.Add(new { name = item.Key, inclusiveMsPerFrame = item.Value[0] / System.Math.Max(1, frames), selfMsPerFrame = item.Value[1] / System.Math.Max(1, frames), callsPerFrame = item.Value[2] / System.Math.Max(1, frames) });
        threads.Add(new { thread = threadName, sampledFrames = frames, frameTimeMs = summarize(cpu), gpuTimeMs = summarize(gpu), markers = markers });
    }
    return new { firstFrame = first, lastFrame = last, threads = threads };
};
System.Action<int> configure = p =>
{
    UnityEditorInternal.ProfilerDriver.enabled = false;
    UnityEngine.QualitySettings.vSyncCount = !cameraComparison && p == 0 ? originalVsync : 0;
    UnityEngine.Application.targetFrameRate = !cameraComparison && p == 0 ? originalTarget : -1;
    if (cameraComparison) foreach (var camera in cameraStates) if (camera.Key != null) camera.Key.enabled = p == 0 && camera.Value;
    type.GetField("count", flags).SetValue(demo, 50000);
    type.GetField("formation", flags).SetValue(demo, formations[p]);
    type.GetField("mode", flags).SetValue(demo, modes[p]);
    type.GetField("paused", flags).SetValue(demo, false);
    type.GetField("zeroDamage", flags).SetValue(demo, true);
    type.GetField("loop", flags).SetValue(demo, false);
    type.GetField("speed", flags).SetValue(demo, 1f);
    type.GetField("zoom", flags).SetValue(demo, 1f);
    type.GetMethod("Restart", flags).Invoke(demo, null);
    intervals.Clear(); submits.Clear(); timings.Clear(); lastTimestamp = 0; missed = 0; lastFrame = -1; sampling = false;
    start = UnityEditor.EditorApplication.timeSinceStartup;
};
var environment = new { utc = System.DateTime.UtcNow.ToString("o"), cpu = UnityEngine.SystemInfo.processorType, gpu = UnityEngine.SystemInfo.graphicsDeviceName, api = UnityEngine.SystemInfo.graphicsDeviceType.ToString(), unity = UnityEngine.Application.unityVersion, width = UnityEngine.Screen.width, height = UnityEngine.Screen.height, profiler = "CPU/GPU/Rendering on, Editor/Deep Profile off", seed = 7241, zeroDamage = true, warmupSeconds = 5, sampleSeconds = 15, cameraComparison = cameraComparison };
UnityEditor.EditorApplication.CallbackFunction callback = null;
callback = () =>
{
    try
    {
        if (demo == null || !UnityEditor.EditorApplication.isPlaying) throw new System.Exception("Demo stopped");
        int frame = UnityEngine.Time.frameCount; if (frame == lastFrame) return;
        double now = UnityEditor.EditorApplication.timeSinceStartup;
        var sim = (ET.RedBlue.CrowdSimulation)simField.GetValue(demo);
        UnityEngine.FrameTimingManager.CaptureFrameTimings();
        if (!sampling)
        {
            lastFrame = frame; if (now - start < 5) return;
            UnityEditorInternal.ProfilerDriver.ClearAllFrames(); UnityEditorInternal.ProfilerDriver.enabled = true;
            sampleStart = now; firstFrame = frame; tickBase = sim.Tick;
            gridBase = sim.TotalGridMs; sepBase = sim.TotalSeparationMs; decideBase = sim.TotalDecisionMs; commitBase = sim.TotalCommitMs;
            gcBase = System.GC.CollectionCount(0); sampling = true; return;
        }
        missed += System.Math.Max(0, frame - lastFrame - 1); lastFrame = frame;
        intervals.Add(UnityEngine.Time.unscaledDeltaTime * 1000.0); submits.Add((float)drawField.GetValue(demo));
        if (UnityEngine.FrameTimingManager.GetLatestTimings(1, timingBuffer) > 0 && timingBuffer[0].frameStartTimestamp != lastTimestamp)
        { lastTimestamp = timingBuffer[0].frameStartTimestamp; timings.Add(timingBuffer[0]); }
        if (now - sampleStart < 15) return;
        double seconds = now - sampleStart; int steps = System.Math.Max(1, sim.Tick - tickBase);
        var timingStats = new System.Collections.Generic.Dictionary<string, object>();
        foreach (string field in new[] { "cpuFrameTime", "cpuMainThreadFrameTime", "cpuMainThreadPresentWaitTime", "cpuRenderThreadFrameTime", "gpuFrameTime" })
        {
            var values = new System.Collections.Generic.List<double>(timings.Count);
            var info = typeof(UnityEngine.FrameTiming).GetField(field);
            foreach (var timing in timings) values.Add((double)info.GetValue(timing));
            timingStats[field] = summarize(values);
        }
        int sampleGc = System.GC.CollectionCount(0) - gcBase;
        var raw = captureProfiler(names[phase]);
        results.Add(new { name = names[phase], seconds = seconds, fps = (frame - firstFrame) / seconds, frames = intervals.Count, missedFrames = missed, frameMs = summarize(intervals), renderSubmitMs = summarize(submits), timing = timingStats, simulationHz = (sim.Tick - tickBase) / seconds, gridMsPerStep = (sim.TotalGridMs - gridBase) / steps, separationMsPerStep = (sim.TotalSeparationMs - sepBase) / steps, decisionMsPerStep = (sim.TotalDecisionMs - decideBase) / steps, commitMsPerStep = (sim.TotalCommitMs - commitBase) / steps, alive = sim.Alive[0] + sim.Alive[1], burstVerified = sim.AccelerationVerified, gc0 = sampleGc, draws = UnityEditor.UnityStats.drawCalls, triangles = UnityEditor.UnityStats.triangles, raw = raw });
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "results.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new { environment = environment, completedPhases = results.Count, results = results }, Newtonsoft.Json.Formatting.Indented));
        phase++;
        if (phase < names.Length) { configure(phase); return; }
        UnityEditor.EditorApplication.update -= callback;
        configure(0); UnityEngine.QualitySettings.vSyncCount = originalVsync; UnityEngine.Application.targetFrameRate = originalTarget;
        foreach (var camera in cameraStates) if (camera.Key != null) camera.Key.enabled = camera.Value;
        domain.SetData("RedBlue.CpuGpuProfile.CameraComparison", false);
        domain.SetData("RedBlue.CpuGpuProfile.Status", "complete");
    }
    catch (System.Exception e)
    {
        UnityEditor.EditorApplication.update -= callback; UnityEditorInternal.ProfilerDriver.enabled = false;
        UnityEngine.QualitySettings.vSyncCount = originalVsync; UnityEngine.Application.targetFrameRate = originalTarget;
        foreach (var camera in cameraStates) if (camera.Key != null) camera.Key.enabled = camera.Value;
        domain.SetData("RedBlue.CpuGpuProfile.CameraComparison", false);
        domain.SetData("RedBlue.CpuGpuProfile.Status", "failed");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "error.txt"), e.ToString());
    }
};
UnityEditorInternal.ProfilerDriver.profileEditor = false;
UnityEditorInternal.ProfilerDriver.deepProfiling = false;
configure(0); UnityEditor.EditorApplication.update += callback;
domain.SetData("RedBlue.CpuGpuProfile.Status", "running");
return names.Length + " CPU/GPU phases started; 5s warmup + 15s sample. Do not query MCP during sampling. Results: " + dir;
