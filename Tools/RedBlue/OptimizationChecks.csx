// Unity MCP execute_code method body. Checks the accelerated kernels in Unity,
// including the exact pointers and compiled Burst jobs used by the demo.
var reports = new System.Collections.Generic.List<object>();
System.Action<bool, string> check = (value, message) => { if (!value) throw new System.InvalidOperationException(message); };
var executions = new ET.RedBlue.CrowdExecution[] { ET.RedBlue.CrowdExecution.ManagedReference, ET.RedBlue.CrowdExecution.BurstSingleThread, ET.RedBlue.CrowdExecution.BurstParallel };
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
foreach (var execution in executions)
{
    for (int formation = 0; formation < 4; formation++)
    {
        using (var a = new ET.RedBlue.CrowdSimulation(2000, formation, 7241, execution))
        using (var b = new ET.RedBlue.CrowdSimulation(2000, formation, 7241, execution))
        {
            for (int step = 0; step < 120; step++) { a.Step(); b.Step(); }
            if (execution != ET.RedBlue.CrowdExecution.ManagedReference) check(a.AccelerationVerified, "Burst was requested but ran managed code");
            int red = 0, blue = 0;
            for (int i = 0; i < a.Count; i++)
            {
                check(!float.IsNaN(a.X[i]) && !float.IsNaN(a.Y[i]) && a.X[i] >= 8 && a.X[i] <= 1792 && a.Y[i] >= 8 && a.Y[i] <= 1092, "Non-finite/out-of-bounds " + execution);
                check(a.X[i] == b.X[i] && a.Y[i] == b.Y[i] && a.Hp[i] == b.Hp[i], "Non-deterministic execution " + execution);
                check(a.Target[i] < 0 || a.Team[a.Target[i]] != a.Team[i], "Friendly fire target");
                if (a.Hp[i] > 0) { if (a.Team[i] == 0) red++; else blue++; }
            }
            check(a.Alive[0] == red && a.Alive[1] == blue, "Alive mismatch");
            reports.Add(new { test = "formation/replay/bounds/targets/counts", backend = a.Backend, formation = formation, alive = red + blue });
        }
    }
    using (var duel = new ET.RedBlue.CrowdSimulation(2, 0, 7241, execution))
    {
        duel.X[0] = 900; duel.X[1] = 905; duel.Y[0] = duel.Y[1] = 550;
        duel.Hp[0] = duel.Hp[1] = 1;
        System.Array.Clear((float[])duel.GetType().GetField("cooldown", flags).GetValue(duel), 0, 2);
        duel.Step();
        check(duel.Alive[0] == 0 && duel.Alive[1] == 0, "Simultaneous commit failed " + execution);
    }
    using (var pair = new ET.RedBlue.CrowdSimulation(2, 0, 7241, execution))
    {
        pair.ZeroDamage = true; pair.X[0] = 900; pair.X[1] = 900.1f; pair.Y[0] = pair.Y[1] = 550;
        float center = pair.X[0] + pair.X[1]; pair.Step();
        check(System.Math.Abs(pair.X[0] + pair.X[1] - center) < .001f, "Pair drift " + execution);
        check(pair.X[1] - pair.X[0] > .1f, "No separation " + execution);
    }
    var disposed = new ET.RedBlue.CrowdSimulation(2, 0, 7241, execution);
    disposed.Dispose(); disposed.Dispose(); bool rejected = false;
    check(!disposed.AccelerationVerified, "Disposed acceleration status must be false");
    try { disposed.Step(); } catch (System.ObjectDisposedException) { rejected = true; }
    check(rejected, "Use after dispose accepted");
    reports.Add(new { test = "simultaneous-death/pair-conservation/disposal", execution = execution.ToString() });
}
for (int formation = 0; formation < 4; formation++)
{
    using (var reference = new ET.RedBlue.CrowdSimulation(2000, formation, 7241, ET.RedBlue.CrowdExecution.ManagedReference))
    using (var accelerated = new ET.RedBlue.CrowdSimulation(2000, formation, 7241, ET.RedBlue.CrowdExecution.BurstSingleThread))
    {
        reference.Step(); accelerated.Step(); double maxError = 0;
        for (int i = 0; i < reference.Count; i++)
        {
            maxError = System.Math.Max(maxError, System.Math.Abs(reference.X[i] - accelerated.X[i]));
            maxError = System.Math.Max(maxError, System.Math.Abs(reference.Y[i] - accelerated.Y[i]));
            check(System.Math.Abs(reference.Hp[i] - accelerated.Hp[i]) < .001f, "Damage parity mismatch");
        }
        check(maxError < .001, "First-step parity mismatch");
        reports.Add(new { test = "managed/Burst first-step parity", formation = formation, maxPositionError = maxError });
    }
}
foreach (var execution in new ET.RedBlue.CrowdExecution[] { ET.RedBlue.CrowdExecution.BurstSingleThread, ET.RedBlue.CrowdExecution.BurstParallel })
{
    using (var a = new ET.RedBlue.CrowdSimulation(50000, 3, 7241, execution))
    using (var b = new ET.RedBlue.CrowdSimulation(50000, 3, 7241, execution))
    {
        a.ZeroDamage = b.ZeroDamage = true;
        for (int step = 0; step < 120; step++) { a.Step(); b.Step(); }
        for (int i = 0; i < a.Count; i++) check(a.X[i] == b.X[i] && a.Y[i] == b.Y[i] && a.Hp[i] > 0, "50k replay/life failure " + execution);
        check(a.Alive[0] + a.Alive[1] == 50000, "50k population loss");
        reports.Add(new { test = "50k sustained population and parallel replay", backend = a.Backend, gridMs = a.GridMs, separationMs = a.SeparationMs, decisionMs = a.DecisionMs, commitMs = a.CommitMs });
    }
}
return new { success = true, reports = reports };
