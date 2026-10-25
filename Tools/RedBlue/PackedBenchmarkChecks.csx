// Exercise the exact phone benchmark sequence without changing the active scene.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play mode");
var rows = new System.Collections.Generic.List<ET.RedBlue.RedBlueCpuBenchmark.Result>();
bool profilerBefore = UnityEngine.Profiling.Profiler.enabled;
bool bounded = object.Equals(System.AppDomain.CurrentDomain.GetData("RedBlue.PackedCheck.Bounded"), true);
bool split = object.Equals(System.AppDomain.CurrentDomain.GetData("RedBlue.PackedCheck.Split"), true);
using (var benchmark = new ET.RedBlue.RedBlueCpuBenchmark(new ET.RedBlue.RedBlueCpuExperiment
    { variant = split ? "packed-split" : bounded ? "packed-bounded" : "packed-ab", buildId = "editor-sequence-check", comparePacked = true, comparePackedBounded = bounded, compareSplit = split }, result => rows.Add(result)))
{
    for (int step = 0; step < 3600; step++) benchmark.Advance();
    if (benchmark.Running || rows.Count != 6) throw new System.Exception("Expected six complete runs");
    string[] order = { "BurstSingleThread", "BurstPackedSingleThread", "BurstPackedSingleThread", "BurstSingleThread", "BurstSingleThread", "BurstPackedSingleThread" };
    if (bounded) order = new[] { "BurstPackedSingleThread", "BurstPackedBoundedSingleThread", "BurstPackedBoundedSingleThread", "BurstPackedSingleThread", "BurstPackedSingleThread", "BurstPackedBoundedSingleThread" };
    if (split) order = new[] { "BurstPackedSingleThread", "BurstPackedSplitSingleThread", "BurstPackedSplitSingleThread", "BurstPackedSingleThread", "BurstPackedSingleThread", "BurstPackedSplitSingleThread" };
    for (int i = 0; i < rows.Count; i++)
    {
        var row = rows[i];
        if (row.requestedExecution != order[i] || row.run != i / 2 || !row.burstVerified || row.profilerEnabled || row.separationWorkersObserved)
            throw new System.Exception("Invalid benchmark execution at " + i);
        if (row.stateHash != rows[0].stateHash)
            throw new System.Exception("Public state differs between baseline and packed runs");
    }
}
if (UnityEngine.Profiling.Profiler.enabled != profilerBefore) throw new System.Exception("Profiler state was not restored");
string file = System.IO.Path.GetFullPath(split ? "../Temp/RedBlueSplitChecks/benchmark-sequence.json" : bounded ? "../Temp/RedBluePackedBoundedChecks/benchmark-sequence.json" : "../Temp/RedBluePackedChecks/benchmark-sequence.json");
System.IO.File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(rows, Newtonsoft.Json.Formatting.Indented));
return new { complete = true, results = rows.Count, order = "AB / BA / AB", publicHashesMatch = true, profilerRestored = true,
    note = "Editor and phone can differ in floating-point execution; validate both modes on each platform." };
