// Offline analysis of CpuGpuProfile.csx recordings; capture must be finished.
UnityEditorInternal.ProfilerDriver.enabled = false;
bool cameraComparison = object.Equals(System.AppDomain.CurrentDomain.GetData("RedBlue.CpuGpuProfile.CameraComparison"), true);
var dir = System.IO.Path.GetFullPath(cameraComparison ? "../Temp/RedBlueCameraAB" : "../Temp/RedBlueProfile");
var names = cameraComparison ? new[] { "uncapped-three-cameras", "uncapped-demo-camera-only" } : new[] { "capped-full-front", "uncapped-full-front", "uncapped-full-dense", "uncapped-render-front", "uncapped-simulation-dense" };
int gpuColumn = (int)typeof(UnityEditor.Profiling.HierarchyFrameDataView).GetField("columnTotalGpuTime", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
int selfGpuColumn = (int)typeof(UnityEditor.Profiling.HierarchyFrameDataView).GetField("columnSelfGpuTime", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
var results = new System.Collections.Generic.List<object>();
foreach (string name in names)
{
    if (!UnityEditorInternal.ProfilerDriver.LoadProfile(System.IO.Path.Combine(dir, name + ".data"), false)) throw new System.Exception("Cannot load " + name);
    int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
    int first = System.Math.Max(UnityEditorInternal.ProfilerDriver.firstFrameIndex, last - 239), frames = 0;
    var gpuTotals = new System.Collections.Generic.Dictionary<string, double[]>();
    for (int frame = first; frame <= last; frame++)
    using (var view = UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView(frame, 0, UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, gpuColumn, false))
    {
        if (!view.valid) continue;
        frames++;
        var queue = new System.Collections.Generic.Queue<int>(); queue.Enqueue(view.GetRootItemID());
        while (queue.Count > 0)
        {
            int id = queue.Dequeue(); double gpu = view.GetItemColumnDataAsDouble(id, gpuColumn);
            if (gpu > 0)
            {
                string path = view.GetItemPath(id) ?? "(unnamed)"; double[] total;
                if (!gpuTotals.TryGetValue(path, out total)) gpuTotals[path] = total = new double[3];
                total[0] += gpu; total[1] += view.GetItemColumnDataAsDouble(id, selfGpuColumn); total[2] += view.GetItemColumnDataAsDouble(id, UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime);
            }
            var children = new System.Collections.Generic.List<int>(); view.GetItemChildren(id, children);
            foreach (int child in children) queue.Enqueue(child);
        }
    }
    var gpuRows = new System.Collections.Generic.List<object>();
    foreach (var pair in gpuTotals) gpuRows.Add(new { path = pair.Key, gpuMsPerFrame = pair.Value[0] / frames, gpuSelfMsPerFrame = pair.Value[1] / frames, cpuMsPerFrame = pair.Value[2] / frames });
    var threadRows = new System.Collections.Generic.List<object>();
    for (int thread = 0; thread < 512; thread++)
    {
        string threadName, group;
        using (var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(last, thread))
        { if (!view.valid) break; threadName = view.threadName; group = view.threadGroupName; }
        if (group != "Rendering" && group != "Job") continue;
        int sampled = 0; var totals = new System.Collections.Generic.Dictionary<string, double[]>();
        for (int frame = first; frame <= last; frame++)
        using (var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, thread))
        {
            if (!view.valid || view.threadName != threadName || view.threadGroupName != group) continue;
            sampled++;
            for (int sample = 0; sample < view.sampleCount; sample++)
            {
                string marker = view.GetSampleName(sample) ?? "(unnamed)"; double[] total;
                if (!totals.TryGetValue(marker, out total)) totals[marker] = total = new double[2];
                total[0] += view.GetSampleTimeMs(sample); total[1]++;
            }
        }
        var markerRows = new System.Collections.Generic.List<object>();
        foreach (var pair in totals) markerRows.Add(new { name = pair.Key, inclusiveMsPerFrame = pair.Value[0] / System.Math.Max(1, sampled), callsPerFrame = pair.Value[1] / System.Math.Max(1, sampled) });
        threadRows.Add(new { thread = threadName, group = group, frames = sampled, markers = markerRows });
    }
    results.Add(new { name = name, sampledFrames = frames, gpu = gpuRows, threads = threadRows });
}
string output = System.IO.Path.Combine(dir, "pass-analysis.json");
System.IO.File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented));
System.AppDomain.CurrentDomain.SetData("RedBlue.CpuGpuProfile.CameraComparison", false);
return new { phases = results.Count, path = output };
