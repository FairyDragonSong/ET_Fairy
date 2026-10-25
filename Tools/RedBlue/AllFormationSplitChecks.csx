// Unity MCP execute_code body; run one formation per call, using RedBlue.AllSplit.Formation=0..3.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play mode");
int formation = (int)(System.AppDomain.CurrentDomain.GetData("RedBlue.AllSplit.Formation") ?? 0);
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
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return System.BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
};
var results = new System.Collections.Generic.List<object>();
foreach (bool zeroDamage in new[] { true, false })
using (var baseline = new ET.RedBlue.CrowdSimulation(50000, formation, 7241, ET.RedBlue.CrowdExecution.BurstSingleThread))
using (var candidate = new ET.RedBlue.CrowdSimulation(50000, formation, 7241, ET.RedBlue.CrowdExecution.BurstPackedSingleThread))
{
    baseline.ZeroDamage = candidate.ZeroDamage = zeroDamage;
    var scheduler = new ET.RedBlue.CrowdStepScheduler();
    int frames = 0;
    while (candidate.Tick < 180 && !candidate.Finished)
    {
        // A missed display interval exercises pending-step completion and catch-up.
        double dt = frames++ == 120 ? .05 : 1.0 / 60;
        int completed = scheduler.Advance(candidate, dt, 1, true);
        for (int i = 0; i < completed; i++) baseline.Step();
        if (candidate.Tick != baseline.Tick) throw new System.Exception("Tick count differs");
        if (completed > 0 && candidate.Tick % 30 == 0 && hash(baseline) != hash(candidate)) throw new System.Exception("State differs at " + candidate.Tick);
        if (frames > 1000) throw new System.Exception("Split simulation stopped progressing");
    }
    if (hash(baseline) != hash(candidate)) throw new System.Exception("Final state differs");
    if (!baseline.AccelerationVerified || !candidate.AccelerationVerified) throw new System.Exception("Burst not verified");
    results.Add(new { formation, count = 50000, zeroDamage, ticks = candidate.Tick, frames, state = hash(candidate), allArraysAndRandomEqual = true });
}
string folder = System.IO.Path.GetFullPath("../Temp/RedBlueAllFormationChecks"); System.IO.Directory.CreateDirectory(folder);
System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "formation-" + formation + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented));
return new { formation, passed = true, cases = results.Count, note = "Editor correctness only; phone timing pending" };
