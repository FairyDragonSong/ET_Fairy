// Control-flow check only: jump to each run's final tick. No timing or replay evidence is produced.
var rows = new System.Collections.Generic.List<ET.RedBlue.RedBlueCpuBenchmark.Result>();
var tick = typeof(ET.RedBlue.CrowdSimulation).GetField("<Tick>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
bool profilerBefore = UnityEngine.Profiling.Profiler.enabled;
using (var benchmark = new ET.RedBlue.RedBlueCpuBenchmark(new ET.RedBlue.RedBlueCpuExperiment
    { compareSplit = true, compareAllFormations = true }, result => rows.Add(result)))
{
    for (int i = 0; i < 24; i++)
    {
        if (!benchmark.Running || benchmark.Formation != i / 6) throw new System.Exception("Wrong formation progression");
        tick.SetValue(benchmark.Current, 599);
        benchmark.Advance();
    }
    if (benchmark.Running || rows.Count != 24) throw new System.Exception("Expected 24 results then completion");
    for (int i = 0; i < rows.Count; i++)
    {
        string expected = (i % 6 == 1 || i % 6 == 2 || i % 6 == 5) ? "BurstPackedSplitSingleThread" : "BurstPackedSingleThread";
        if (rows[i].formation != i / 6 || rows[i].run != i % 6 / 2 || rows[i].requestedExecution != expected)
            throw new System.Exception("Wrong AB / BA / AB sequence");
    }
}
if (UnityEngine.Profiling.Profiler.enabled != profilerBefore) throw new System.Exception("Profiler was not restored");
var report = new { controlFlowOnly = true, formations = 4, results = rows.Count, profilerRestored = true, timingAndReplayMeasured = false };
System.IO.Directory.CreateDirectory("../Temp/RedBlueAllFormationChecks");
System.IO.File.WriteAllText("../Temp/RedBlueAllFormationChecks/benchmark-routing.json", Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
return report;
