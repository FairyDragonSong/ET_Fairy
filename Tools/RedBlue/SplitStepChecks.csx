// Unity MCP execute_code body. Set RedBlue.SplitCheck.Run=-1 for replay, 0..2 for 50k timings.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play mode");
var runValue = System.AppDomain.CurrentDomain.GetData("RedBlue.SplitCheck.Run");
int run = runValue == null ? -1 : (int)runValue;
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
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return System.BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
};
var results = new System.Collections.Generic.List<object>();
if (run < 0)
{
    for (int formation = 0; formation < 4; formation++)
    foreach (bool zeroDamage in new[] { false, true })
    foreach (uint seed in new uint[] { 7241, 1957 })
    using (var control = new ET.RedBlue.CrowdSimulation(2000, formation, seed, ET.RedBlue.CrowdExecution.BurstPackedSingleThread))
    using (var split = new ET.RedBlue.CrowdSimulation(2000, formation, seed, ET.RedBlue.CrowdExecution.BurstPackedSplitSingleThread))
    {
        control.ZeroDamage = split.ZeroDamage = zeroDamage;
        for (int i = 1; i < 30; i++)
        {
            control.X[i] = split.X[i] = control.X[0]; control.Y[i] = split.Y[i] = control.Y[0];
            control.Hp[i] = split.Hp[i] = zeroDamage ? 0 : 1;
            if (zeroDamage) { control.Alive[control.Team[i]]--; split.Alive[split.Team[i]]--; }
        }
        for (int tick = 1; tick <= 120; tick++)
        {
            var oldX = (float[])split.X.Clone(); var oldPrevious = (float[])split.PreviousX.Clone();
            int previousTick = split.Tick;
            split.BeginSplitStep();
            if (split.Tick != previousTick || !split.SplitStepPending) throw new System.Exception("Partial tick published");
            for (int i = 0; i < split.Count; i++)
                if (oldX[i] != split.X[i] || oldPrevious[i] != split.PreviousX[i]) throw new System.Exception("Interpolation endpoints changed before commit");
            split.CompleteSplitStep(); control.Step();
            if (split.Tick != control.Tick || hash(control) != hash(split)) throw new System.Exception("Split state differs at tick " + tick + " formation " + formation);
        }
        if (!control.AccelerationVerified || !split.AccelerationVerified) throw new System.Exception("Burst not verified");
        results.Add(new { formation, zeroDamage, seed, ticks = split.Tick, equal = true, state = hash(split) });
    }
    foreach (int fps in new[] { 30, 48, 58, 60, 90 })
    using (var s = new ET.RedBlue.CrowdSimulation(2000, 1, 7241, ET.RedBlue.CrowdExecution.BurstPackedSingleThread))
    {
        s.ZeroDamage = true;
        var scheduler = new ET.RedBlue.CrowdStepScheduler();
        double elapsed = 0;
        for (int frame = 0; frame < fps * 10; frame++)
        {
            double dt = frame == fps * 3 ? .15 : 1.0 / fps;
            scheduler.Advance(s, dt, 1, true); elapsed += dt;
        }
        if (System.Math.Abs(s.Tick - elapsed / ET.RedBlue.CrowdSimulation.StepSeconds) > 2) throw new System.Exception("Simulation fell behind at " + fps + " FPS");
        if (s.SplitStepPending) s.CompleteSplitStep();
        results.Add(new { fps, seconds = elapsed, ticks = s.Tick, realtime = true, debt = scheduler.Accumulator });
    }
}
else
{
    string expected = null;
    foreach (bool splitMode in (run & 1) == 0 ? new[] { false, true } : new[] { true, false })
    using (var s = new ET.RedBlue.CrowdSimulation(50000, 1, 7241, ET.RedBlue.CrowdExecution.BurstPackedSingleThread))
    {
        s.ZeroDamage = true;
        double total = 0, maxPhase = 0, firstPhase = 0, lastPhase = 0;
        for (int tick = 0; tick < 600; tick++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (splitMode)
            {
                s.BeginSplitStep();
                long middle = System.Diagnostics.Stopwatch.GetTimestamp(); s.CompleteSplitStep();
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                if (tick >= 300)
                {
                    firstPhase += (middle - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    lastPhase += (end - middle) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    maxPhase = System.Math.Max(maxPhase, System.Math.Max(middle - start, end - middle) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
            }
            else s.Step();
            if (tick >= 300) total += (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        string state = hash(s);
        if (expected != null && state != expected) throw new System.Exception("50k split state differs");
        expected = state;
        results.Add(new { run, splitMode, ticks = s.Tick, meanStepMs = total / 300, firstPhaseMs = firstPhase / 300, lastPhaseMs = lastPhase / 300, maxPhaseMs = maxPhase,
            flowMsPerTick = s.TotalFlowMs / 600, state, verified = s.AccelerationVerified });
    }
}
string folder = System.IO.Path.GetFullPath("../Temp/RedBlueSplitChecks"); System.IO.Directory.CreateDirectory(folder);
System.IO.File.WriteAllText(System.IO.Path.Combine(folder, run < 0 ? "replay.json" : "paired-" + run + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented));
return new { run, scenarios = results.Count, passed = true, note = "Editor correctness and phase diagnostics only; phone frame test still required" };
