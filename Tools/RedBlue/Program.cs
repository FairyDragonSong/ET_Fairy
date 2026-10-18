using ET.RedBlue;
using System.Diagnostics;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Valid(CrowdSimulation s)
{
    int[] alive = new int[2];
    for (int i = 0; i < s.Count; i++)
    {
        Check(float.IsFinite(s.X[i]) && float.IsFinite(s.Y[i]), "Non-finite position");
        Check(s.X[i] >= 8 && s.X[i] <= 1792 && s.Y[i] >= 8 && s.Y[i] <= 1092, "Out of bounds");
        if (s.Hp[i] > 0) alive[s.Team[i]]++;
        Check(s.Target[i] < 0 || s.Team[i] != s.Team[s.Target[i]], "Friendly target");
    }
    Check(alive[0] == s.Alive[0] && alive[1] == s.Alive[1], "Alive count mismatch");
}

for (int formation = 0; formation < 4; formation++)
{
    var a = new CrowdSimulation(2000, formation); var b = new CrowdSimulation(2000, formation);
    for (int k = 0; k < 120; k++) { a.Step(); b.Step(); }
    Valid(a);
    for (int i = 0; i < a.Count; i++) Check(a.X[i] == b.X[i] && a.Y[i] == b.Y[i] && a.Hp[i] == b.Hp[i], "Seed replay mismatch");
    Console.WriteLine($"PASS formation {formation}: bounds, counts, targets, deterministic replay");
}
var duel = new CrowdSimulation(2);
duel.X[0] = 900; duel.X[1] = 905; duel.Y[0] = duel.Y[1] = 550;
for (int k = 0; k < 600 && !duel.Finished; k++) duel.Step();
Check(duel.Finished, "Duel never resolved"); Valid(duel);
Console.WriteLine("PASS duel resolves and reports winner");

var simultaneous = new CrowdSimulation(2);
simultaneous.X[0] = 900; simultaneous.X[1] = 905; simultaneous.Y[0] = simultaneous.Y[1] = 550;
simultaneous.ZeroDamage = true;
for (int k = 0; k < 30; k++) simultaneous.Step();
simultaneous.Hp[0] = simultaneous.Hp[1] = 1; simultaneous.ZeroDamage = false;
// Force both cooldowns ready to isolate simultaneous commit from attack timing.
var cooldown = typeof(CrowdSimulation).GetField("cooldown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
Array.Clear((float[])cooldown.GetValue(simultaneous)!);
simultaneous.Step();
Check(simultaneous.Alive[0] == 0 && simultaneous.Alive[1] == 0, "Simultaneous attacks must permit a draw");
Console.WriteLine("PASS simultaneous lethal attacks commit together");

var pair = new CrowdSimulation(2) { ZeroDamage = true };
pair.X[0] = 900; pair.X[1] = 900.1f; pair.Y[0] = pair.Y[1] = 550;
float center = pair.X[0] + pair.X[1];
pair.Step();
Check(Math.Abs(pair.X[0] + pair.X[1] - center) < .001f, "Separation introduced a directional drift");
Check(pair.X[1] - pair.X[0] > .1f, "Overlapping units did not separate");
Console.WriteLine("PASS pair separation preserves center and resolves overlap");

foreach (int population in new[] { 2000, 10000, 20000, 50000 })
{
    var s = new CrowdSimulation(population, 3) { ZeroDamage = true };
    for (int k = 0; k < 10; k++) s.Step();
    var timer = new Stopwatch();
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    timer.Start();
    for (int k = 0; k < 90; k++) s.Step();
    timer.Stop(); long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Valid(s); Check(s.Alive[0] + s.Alive[1] == population, "Zero damage lost units");
    Check(bytes <= 128, $"Step allocated {bytes} bytes");
    Console.WriteLine($"PASS dense {population}: {timer.Elapsed.TotalMilliseconds / 90:F2} ms/step, {bytes} allocated bytes (desktop .NET, not phone)");
}
