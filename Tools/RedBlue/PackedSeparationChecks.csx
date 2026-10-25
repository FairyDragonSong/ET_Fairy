// Unity MCP execute_code body. RedBlue.PackedCheck.Run=-1: replay; 0..2: paired 50k timings.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play mode first");
var value = System.AppDomain.CurrentDomain.GetData("RedBlue.PackedCheck.Run");
int run = value == null ? -1 : (int)value;
bool bounded = object.Equals(System.AppDomain.CurrentDomain.GetData("RedBlue.PackedCheck.Bounded"), true);
var control = bounded ? ET.RedBlue.CrowdExecution.BurstPackedSingleThread : ET.RedBlue.CrowdExecution.BurstSingleThread;
var candidate = bounded ? ET.RedBlue.CrowdExecution.BurstPackedBoundedSingleThread : ET.RedBlue.CrowdExecution.BurstPackedSingleThread;
var fields = typeof(ET.RedBlue.CrowdSimulation).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
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
            System.Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length); stream.Write(bytes, 0, bytes.Length);
        }
        var random = typeof(ET.RedBlue.CrowdSimulation).GetField("random", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var randomBytes = System.BitConverter.GetBytes((uint)random.GetValue(s)); stream.Write(randomBytes, 0, randomBytes.Length);
        using (var sha = System.Security.Cryptography.SHA256.Create()) return System.BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
};
var results = new System.Collections.Generic.List<object>();
if (run < 0)
{
    for (int formation = 0; formation < 4; formation++)
    foreach (bool zeroDamage in new[] { false, true })
    foreach (uint seed in new uint[] { 7241, 1957 })
    using (var baseline = new ET.RedBlue.CrowdSimulation(2000, formation, seed, control))
    using (var packed = new ET.RedBlue.CrowdSimulation(2000, formation, seed, candidate))
    {
        baseline.ZeroDamage = packed.ZeroDamage = zeroDamage;
        // Coincident pairs exercise the ID-based tie-break; low health exercises deaths.
        for (int i = 1; i < 30; i++)
        {
            baseline.X[i] = packed.X[i] = baseline.X[0]; baseline.Y[i] = packed.Y[i] = baseline.Y[0];
            baseline.Hp[i] = packed.Hp[i] = zeroDamage ? 0 : 1;
            if (zeroDamage) { baseline.Alive[baseline.Team[i]]--; packed.Alive[packed.Team[i]]--; }
        }
        for (int tick = 1; tick <= 120; tick++)
        {
            baseline.Step(); packed.Step();
            if (hash(baseline) != hash(packed)) throw new System.InvalidOperationException("Packed state differs: formation=" + formation + " seed=" + seed + " tick=" + tick);
        }
        if (!baseline.AccelerationVerified || !packed.AccelerationVerified) throw new System.InvalidOperationException("Burst not verified");
        results.Add(new { formation, seed, zeroDamage, ticks = 120, state = hash(packed), equal = true });
    }
}
else
{
    string expected = null;
    foreach (var execution in (run & 1) == 0
        ? new[] { control, candidate }
        : new[] { candidate, control })
    using (var s = new ET.RedBlue.CrowdSimulation(50000, 1, 7241, execution))
    {
        s.ZeroDamage = true;
        for (int tick = 0; tick < 300; tick++) s.Step();
        double grid = s.TotalGridMs, separation = s.TotalSeparationMs, decision = s.TotalDecisionMs, commit = s.TotalCommitMs;
        double total = 0; var times = new double[300];
        for (int tick = 0; tick < 300; tick++)
        {
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp(); s.Step();
            total += times[tick] = (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        string state = hash(s);
        if (expected != null && state != expected) throw new System.InvalidOperationException("50k packed state differs");
        expected = state;
        if (!s.AccelerationVerified) throw new System.InvalidOperationException("Burst not verified");
        System.Array.Sort(times);
        results.Add(new { run, execution = execution.ToString(), count = s.Count, tick = s.Tick, state, meanStepMs = total / 300, p95StepMs = times[284],
            gridMs = (s.TotalGridMs - grid) / 300, separationMs = (s.TotalSeparationMs - separation) / 300,
            decisionMs = (s.TotalDecisionMs - decision) / 300, commitMs = (s.TotalCommitMs - commit) / 300 });
    }
}
var report = new { schema = 1, run, bounded, control = control.ToString(), candidate = candidate.ToString(), unity = UnityEngine.Application.unityVersion, cpu = UnityEngine.SystemInfo.processorType,
    profilerEnabled = UnityEngine.Profiling.Profiler.enabled, scratchBytesAt50k = 1050004, results,
    note = "Editor Burst diagnostic only; packed gather/scatter included; phone A/B still required. All simulation arrays plus random state hashed." };
string folder = System.IO.Path.GetFullPath(System.AppDomain.CurrentDomain.GetData("RedBlue.PackedCheck.Output") as string ?? "../Temp/RedBluePackedChecks"); System.IO.Directory.CreateDirectory(folder);
System.IO.File.WriteAllText(System.IO.Path.Combine(folder, run < 0 ? "replay.json" : "paired-" + run + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
return new { run, scenarios = results.Count, report.note };
