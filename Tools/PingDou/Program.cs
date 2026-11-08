using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ET.Client;

internal static class Program
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        ++checks;
        if (!condition)
            throw new Exception(message);
    }

    public static void Main()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalog.json")));
        var patterns = JsonSerializer.Deserialize<PingDouPattern[]>(doc.RootElement.GetProperty("patterns").GetRawText(), new JsonSerializerOptions { IncludeFields = true });
        foreach (var pattern in patterns)
        {
            var round = new PingDouRound(pattern);
            int[] cells = Enumerable.Range(0, pattern.size * pattern.size).Where(i => round.Key(i) != '.').ToArray();
            Assert(!round.BeginIroning() && !round.Reveal(), "Illegal phase transitions");
            Assert(!round.TryPlace(-1, 'X', out _), "Out of range accepted");
            Assert(!round.TryPlace(cells[0], '?', out _), "Wrong color accepted");
            int[] totals =
            {
                10,
                20,
                30,
                40,
                52,
                64,
                76,
                88,
                100,
                115,
                130,
                145,
                160,
                175,
                190,
                205,
                220,
                235,
                250,
                270,
                290,
                310,
                330,
                350,
                370,
                390,
                410,
                430,
                450,
                470
            };
            for (int n = 0; n < cells.Length; ++n)
            {
                int i = cells[n];
                Assert(round.TryPlace(i, round.Key(i), out var placed), "Cell rejected");
                Assert(placed.Combo == n + 1, "Combo sequence");
                Assert(!round.TryPlace(i, round.Key(i), out _), "Repeated cell scored");
                if (n < totals.Length)
                    Assert(round.BaseScore + round.ComboScore == totals[n], "Tier boundary at " + (n + 1));
                if (n == 17)
                {
                    var restored = PingDouRound.Restore(pattern, round.Capture());
                    Assert(restored.Score == round.Score && restored.Placed == 18, "Partial save restore");
                    round = restored;
                }
            }

            Assert(round.Phase == PingDouPhase.ReadyToIron, "Round not ready");
            int usedColors = pattern.colors.Count(c => pattern.rows.Any(r => r.Contains(c.key)));
            Assert(round.ColorScore == usedColors * 50, "Color rewards duplicated/missing");
            Assert(round.ComboScore == (cells.Length - 19) * 10 + 60, "Complete combo formula");
            Assert(round.BeginIroning(), "Begin ironing");
            foreach (int i in cells.Take(cells.Length - 1))
                round.ApplyHeat(i, 1);
            round.ApplyHeat(cells.Last(), .99999f);
            Assert(round.HeatPercent == 99 && !round.Reveal(), "Rounded 100% prematurely unlocks");
            var partial = PingDouRound.Restore(pattern, round.Capture());
            Assert(partial.HeatPercent == 99 && partial.Phase == PingDouPhase.Ironing, "Partial heat restore");
            Assert(!partial.ApplyHeat(cells.Last(), float.NaN), "NaN heat accepted");
            partial.ApplyHeat(cells.Last(), 1);
            Assert(partial.HeatPercent == 100 && partial.Reveal() && partial.Stars == 3, "Natural finish rating");
            Assert(!partial.Reveal() && !partial.BeginIroning(), "Settlement repeated");
            Assert(PingDouRound.Restore(pattern, partial.Capture()).Stars == 3, "Result restore");
            Assert(round.FinishIroningAssisted() && round.Reveal() && round.Stars == 2, "Assisted result");
            var bad = round.Capture();
            bad.signature = "changed";
            Assert(PingDouRound.Restore(pattern, bad).Placed == 0, "Stale content restored");
            bad = round.Capture();
            bad.placed = new bool[1];
            Assert(PingDouRound.Restore(pattern, bad).Placed == 0, "Truncated save restored");
            var slow = new PingDouRound(pattern);
            foreach (int i in cells)
                slow.TryPlace(i, slow.Key(i), out _);
            slow.BeginIroning();
            slow.ApplyHeat(cells[0], 5000);
            foreach (int i in cells)
                slow.ApplyHeat(i, 1);
            slow.Reveal();
            Assert(slow.Stars == 2, "Repeated heating efficiency");
            Console.WriteLine(pattern.name + ": " + cells.Length + " beads, " + partial.Score + " points, round / persistence / heating checks passed");
        }

        Console.WriteLine("PASS: " + checks + " checks");
    }
}
