// Execute this method body with Unity MCP execute_code while the ET demo is playing.
// Results stay in AppDomain data until fetched; no scene or asset changes.
var domain = System.AppDomain.CurrentDomain;
var old = domain.GetData("RedBlue.Benchmark.Callback") as UnityEditor.EditorApplication.CallbackFunction;
if (old != null) UnityEditor.EditorApplication.update -= old;
var demo = UnityEngine.Object.FindObjectOfType<ET.RedBlue.RedBlueDemo>();
if (demo == null) return "Start the ET RedBlue demo first";
var type = demo.GetType();
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var results = new System.Collections.Generic.List<object>();
var reportPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../../Temp/redblue-editor-benchmark.json"));
System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath));
var intervals = new System.Collections.Generic.List<double>(4000);
var submits = new System.Collections.Generic.List<double>(4000);
int phase = 0, lastFrame = -1, firstFrame = 0, tickBase = 0, lastTick = 0, tickSamples = 0, missed = 0, multiStep = 0, gcBase = 0;
double stageStart = 0, sampleStart = 0, grid = 0, separate = 0, decide = 0, commit = 0;
double gridBase = 0, separationBase = 0, decisionBase = 0, commitBase = 0;
bool sampling = false;
var modes = new int[] { 0, 0, 1, 2 };
var formations = new int[] { 0, 3, 0, 3 };
var names = new string[] { "full-front", "full-dense", "render-only-front", "simulation-only-dense" };
System.Action<int> configure = p =>
{
    type.GetField("count", flags).SetValue(demo, 50000);
    type.GetField("formation", flags).SetValue(demo, formations[p]);
    type.GetField("mode", flags).SetValue(demo, modes[p]);
    type.GetField("paused", flags).SetValue(demo, false);
    type.GetField("zeroDamage", flags).SetValue(demo, true);
    type.GetField("loop", flags).SetValue(demo, false);
    type.GetField("speed", flags).SetValue(demo, 1f);
    type.GetField("zoom", flags).SetValue(demo, 1f);
    type.GetMethod("Restart", flags).Invoke(demo, null);
    sampling = false; intervals.Clear(); submits.Clear(); tickSamples = missed = multiStep = 0;
    grid = separate = decide = commit = 0; lastFrame = -1;
    stageStart = UnityEditor.EditorApplication.timeSinceStartup;
    domain.SetData("RedBlue.Benchmark.Status", names[p] + ": warming up 10s");
};
UnityEditor.EditorApplication.CallbackFunction callback = null;
callback = () =>
{
    if (demo == null || !UnityEditor.EditorApplication.isPlaying)
    {
        UnityEditor.EditorApplication.update -= callback;
        domain.SetData("RedBlue.Benchmark.Status", "cancelled: demo stopped");
        return;
    }
    int frame = UnityEngine.Time.frameCount;
    if (frame == lastFrame) return;
    double now = UnityEditor.EditorApplication.timeSinceStartup;
    var sim = (ET.RedBlue.CrowdSimulation)type.GetField("simulation", flags).GetValue(demo);
    if (!sampling)
    {
        lastFrame = frame;
        if (now - stageStart < 10) return;
        sampling = true; sampleStart = now; firstFrame = frame; tickBase = lastTick = sim.Tick;
        gridBase = sim.TotalGridMs; separationBase = sim.TotalSeparationMs; decisionBase = sim.TotalDecisionMs; commitBase = sim.TotalCommitMs;
        gcBase = System.GC.CollectionCount(0);
        domain.SetData("RedBlue.Benchmark.Status", names[phase] + ": sampling 20s");
        return;
    }
    missed += System.Math.Max(0, frame - lastFrame - 1); lastFrame = frame;
    intervals.Add(UnityEngine.Time.unscaledDeltaTime * 1000.0);
    submits.Add((float)type.GetField("drawMs", flags).GetValue(demo));
    if (sim.Tick != lastTick)
    {
        if (sim.Tick - lastTick > 1) multiStep++;
        lastTick = sim.Tick; tickSamples++;
        grid += sim.GridMs; separate += sim.SeparationMs; decide += sim.DecisionMs; commit += sim.CommitMs;
    }
    if (now - sampleStart < 20) return;
    intervals.Sort(); submits.Sort();
    double seconds = now - sampleStart, sum = 0, submitSum = 0;
    for (int i = 0; i < intervals.Count; i++) { sum += intervals[i]; submitSum += submits[i]; }
    int p95 = System.Math.Max(0, (int)System.Math.Ceiling(intervals.Count * .95) - 1);
    var view = (ET.RedBlue.CrowdRenderer)type.GetField("rendererView", flags).GetValue(demo);
    results.Add(new
    {
        name = names[phase], warmupSeconds = 10, sampleSeconds = seconds,
        simulationBackend = sim.Backend, accelerationVerified = sim.AccelerationVerified, renderBackend = view.Backend,
        fps = (frame - firstFrame) / seconds, sampledFrames = intervals.Count, missedFrames = missed,
        frameMeanMs = sum / intervals.Count, frameP95Ms = intervals[p95], frameMaxMs = intervals[intervals.Count - 1],
        simulationHz = (sim.Tick - tickBase) / seconds, realtimeRatio = (sim.Tick - tickBase) / (seconds * 30),
        alive = sim.Alive[0] + sim.Alive[1], drawn = modes[phase] == 2 ? 0 : view.Drawn,
        submitCalls = modes[phase] == 2 ? 0 : view.DrawCalls,
        submitMeanMs = submitSum / submits.Count, submitP95Ms = submits[p95],
        gridMs = (sim.TotalGridMs - gridBase) / System.Math.Max(1, sim.Tick - tickBase),
        separationMs = (sim.TotalSeparationMs - separationBase) / System.Math.Max(1, sim.Tick - tickBase),
        decisionMs = (sim.TotalDecisionMs - decisionBase) / System.Math.Max(1, sim.Tick - tickBase),
        commitMs = (sim.TotalCommitMs - commitBase) / System.Math.Max(1, sim.Tick - tickBase),
        sampledStepFrames = tickSamples, multiStepFrames = multiStep,
        gen0Collections = System.GC.CollectionCount(0) - gcBase,
        managedMemoryMB = System.GC.GetTotalMemory(false) / 1048576.0,
        width = UnityEngine.Screen.width, height = UnityEngine.Screen.height,
        nativeDrawCalls = UnityEditor.UnityStats.drawCalls, nativeBatches = UnityEditor.UnityStats.batches,
        nativeTriangles = UnityEditor.UnityStats.triangles
    });
    domain.SetData("RedBlue.Benchmark.Results", Newtonsoft.Json.JsonConvert.SerializeObject(results));
    System.IO.File.WriteAllText(reportPath, Newtonsoft.Json.JsonConvert.SerializeObject(new
    {
        environment = domain.GetData("RedBlue.Benchmark.Environment"),
        completedPhases = results.Count, results = results
    }, Newtonsoft.Json.Formatting.Indented));
    phase++;
    if (phase >= names.Length)
    {
        UnityEditor.EditorApplication.update -= callback;
        domain.SetData("RedBlue.Benchmark.Callback", null);
        domain.SetData("RedBlue.Benchmark.Status", "complete");
        return;
    }
    configure(phase);
};
domain.SetData("RedBlue.Benchmark.Results", "[]");
domain.SetData("RedBlue.Benchmark.Callback", callback);
domain.SetData("RedBlue.Benchmark.Environment", Newtonsoft.Json.JsonConvert.SerializeObject(new
{
    cpu = UnityEngine.SystemInfo.processorType, cores = UnityEngine.SystemInfo.processorCount,
    gpu = UnityEngine.SystemInfo.graphicsDeviceName, api = UnityEngine.SystemInfo.graphicsDeviceType.ToString(),
    unity = UnityEngine.Application.unityVersion, targetFPS = UnityEngine.Application.targetFrameRate,
    vsync = UnityEngine.QualitySettings.vSyncCount, zeroDamage = true, seed = 7241,
    dateUTC = System.DateTime.UtcNow.ToString("o"), note = "Windows Editor, maximum Game view; 10s warmup + 20s sample per phase. CPU stages include every simulation step via cumulative counters; Editor memory includes the editor."
}));
configure(0);
UnityEditor.EditorApplication.update += callback;
return "Benchmark started: four phases, 10 seconds warmup and 20 seconds sample each";
