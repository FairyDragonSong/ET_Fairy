// Unity MCP execute_code method body. Runs outside Play mode, without changing scenes.
// Set AppDomain data RedBlue.SimCheck.Label to baseline or candidate before execution.
// Editor Burst timings are diagnostic; phone speedups require a new MiniGame capture.
if (UnityEditor.EditorApplication.isPlaying)
    throw new System.InvalidOperationException("Stop Editor Play mode before isolated simulation checks.");
string label = System.AppDomain.CurrentDomain.GetData("RedBlue.SimCheck.Label") as string ?? "baseline";
if (label != "baseline" && label != "candidate") throw new System.ArgumentException("Unknown label");
string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../../Temp/RedBlueSimOptimization"));
System.IO.Directory.CreateDirectory(folder);
var results = new System.Collections.Generic.List<object>();
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var fields = typeof(ET.RedBlue.CrowdSimulation).GetFields(flags);
System.Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
System.Func<ET.RedBlue.CrowdSimulation, string> hash = s =>
{
    using (var stream = new System.IO.MemoryStream())
    {
        foreach (var field in fields)
        {
            var array = field.GetValue(s) as System.Array;
            if (array == null) continue;
            var bytes = new byte[System.Buffer.ByteLength(array)];
            System.Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return System.BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
};
foreach (var execution in new[] { ET.RedBlue.CrowdExecution.BurstSingleThread, ET.RedBlue.CrowdExecution.BurstParallel })
for (int formation = 0; formation < 4; formation++)
foreach (bool zeroDamage in new[] { false, true })
using (var s = new ET.RedBlue.CrowdSimulation(2000, formation, 7241, execution))
{
    s.ZeroDamage = zeroDamage;
    for (int tick = 0; tick < 120; tick++) s.Step();
    if (!s.AccelerationVerified) throw new System.InvalidOperationException("Burst did not compile");
    results.Add(new { kind = "replay", execution = execution.ToString(), formation, zeroDamage, count = s.Count, tick = s.Tick, state = hash(s) });
}
for (int run = 0; run < 3; run++)
using (var s = new ET.RedBlue.CrowdSimulation(50000, 1, 7241, ET.RedBlue.CrowdExecution.BurstSingleThread))
{
    s.ZeroDamage = true;
    for (int tick = 0; tick < 300; tick++) s.Step();
    double grid = s.TotalGridMs, separation = s.TotalSeparationMs, decision = s.TotalDecisionMs, commit = s.TotalCommitMs;
    for (int tick = 0; tick < 300; tick++) s.Step();
    if (!s.AccelerationVerified) throw new System.InvalidOperationException("Burst did not compile");
    results.Add(new { kind = "50k-islands", execution = s.Backend, count = s.Count, tick = s.Tick, run, state = hash(s),
        gridMs = (s.TotalGridMs - grid) / 300, separationMs = (s.TotalSeparationMs - separation) / 300,
        decisionMs = (s.TotalDecisionMs - decision) / 300, commitMs = (s.TotalCommitMs - commit) / 300 });
}
var arrayResults = Newtonsoft.Json.Linq.JArray.FromObject(results);
string baselinePath = System.IO.Path.Combine(folder, "baseline.json");
if (label == "candidate")
{
    var baseline = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(baselinePath));
    var original = (Newtonsoft.Json.Linq.JArray)baseline["results"];
    if (original.Count != arrayResults.Count) throw new System.InvalidOperationException("Scenario count changed");
    for (int i = 0; i < original.Count; i++)
        if ((string)original[i]["state"] != (string)arrayResults[i]["state"])
            throw new System.InvalidOperationException("State differs from baseline at scenario " + i);
}
var report = new { label, unity = UnityEngine.Application.unityVersion, cpu = UnityEngine.SystemInfo.processorType,
    note = "Editor Burst; 50k Islands tick 301..600; state hashes cover all simulation arrays, including budgets/forces/targets; phone not measured",
    parityPassed = label == "candidate", results };
System.IO.File.WriteAllText(System.IO.Path.Combine(folder, label + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
return report;
