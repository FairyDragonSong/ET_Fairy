using System;
using System.Diagnostics;

namespace ET.RedBlue
{
    public enum CrowdExecution { Auto, ManagedReference, BurstSingleThread, BurstParallel }
    // Pure C#: one slot per soldier, no Unity objects or allocations in Step.
    public sealed class CrowdSimulation : IDisposable
    {
        public const float Width = 1800, Height = 1100, StepSeconds = 1f / 30;
        public readonly int Count;
        public readonly float Radius;
        public readonly float[] X, Y, PreviousX, PreviousY, Vx, Vy, Angle, Hp, Hit, Death;
        public readonly byte[] Team;
        public readonly int[] Target, Alive = new int[2];
        public int Tick { get; private set; }
        public bool ZeroDamage;
        public double GridMs, SeparationMs, DecisionMs, CommitMs;
        public double TotalGridMs { get; private set; }
        public double TotalSeparationMs { get; private set; }
        public double TotalDecisionMs { get; private set; }
        public double TotalCommitMs { get; private set; }
        public float Time => Tick * StepSeconds;
        public bool Finished => Alive[0] == 0 || Alive[1] == 0;
        internal readonly float[] forceX, forceY, damage, cooldown;
        internal readonly byte[] budget;
        internal readonly int[] heads, next, counts, offsets, cursors, ids;
        internal readonly int cols, rows, sepCols, sepRows;
        internal readonly float cell, diameter;
        internal readonly FlowField flow = new FlowField();
        private uint random;
        private bool disposed;
        public static CrowdExecution DefaultExecution = CrowdExecution.BurstParallel;
#if UNITY_5_3_OR_NEWER
        private readonly BurstCrowdKernel kernel;
        public string Backend => kernel != null ? kernel.Backend : "Managed reference";
        public bool AccelerationVerified => kernel != null && kernel.IsBurstCompiled;
#else
        public string Backend => "Managed reference";
        public bool AccelerationVerified => false;
#endif

        public CrowdSimulation(int count = 2000, int formation = 0, uint seed = 7241, CrowdExecution execution = CrowdExecution.Auto)
        {
            Count = Math.Max(2, Math.Min(50000, count)); random = seed == 0 ? 1u : seed;
            Radius = Clamp((float)Math.Sqrt(20000f / Count) * 2.5f, 1.65f, 3.4f);
            diameter = Radius * 2; cell = Math.Max(8, (float)Math.Ceiling(diameter + 3.6f));
            cols = (int)Math.Ceiling(Width / cell); rows = (int)Math.Ceiling(Height / cell);
            sepCols = (int)Math.Ceiling(Width / diameter); sepRows = (int)Math.Ceiling(Height / diameter);
            X = new float[Count]; Y = new float[Count]; PreviousX = new float[Count]; PreviousY = new float[Count];
            Vx = new float[Count]; Vy = new float[Count]; Angle = new float[Count]; Hp = new float[Count];
            Hit = new float[Count]; Death = new float[Count]; Team = new byte[Count]; Target = new int[Count];
            forceX = new float[Count]; forceY = new float[Count]; damage = new float[Count]; cooldown = new float[Count];
            budget = new byte[Count]; next = new int[Count]; heads = new int[cols * rows * 2];
            counts = new int[sepCols * sepRows]; offsets = new int[counts.Length + 1]; cursors = new int[counts.Length]; ids = new int[Count];
            int half = (Count + 1) / 2;
            for (int i = 0; i < Count; i++)
            {
                int t = formation == 3 ? i % 2 : i < half ? 0 : 1;
                int k = t == 0 ? i : i - half, total = t == 0 ? half : Count - half;
                Team[i] = (byte)t; Alive[t]++; Hp[i] = 100; Target[i] = -1;
                int c = (int)Math.Ceiling(Math.Sqrt(total * .79)), r = (total + c - 1) / c;
                float x = 65 + k % c / (float)c * 735 + t * 935;
                float y = 95 + k / c / (float)r * 910;
                if (formation == 1)
                {
                    int group = k % 6, index = k / 6;
                    c = (int)Math.Ceiling(Math.Sqrt(Math.Ceiling(total / 6.0) * 1.5));
                    float spacing = Math.Min(7.8f, 220f / c);
                    x = 90 + group % 3 * 550 + t * 270 + index % c * spacing;
                    y = 120 + group / 3 * 500 + index / c * spacing;
                }
                else if (formation == 2)
                {
                    if (t == 0) { x = 690 + k % c / (float)c * 420; y = 240 + k / c / (float)r * 620; }
                    else { int local = k / 2; c = (int)Math.Ceiling(Math.Sqrt(Math.Ceiling(total / 2.0) * 1.25)); r = Math.Max(1, (total + 2 * c - 1) / (2 * c)); x = 90 + local % c / (float)c * 460 + k % 2 * 1160; y = 130 + local / c / (float)r * 840; }
                }
                else if (formation == 3)
                {
                    c = (int)Math.Ceiling(Math.Sqrt(Count * 1.5)); x = 650 + i % c * 1.8f; y = 360 + i / c * 1.8f;
                }
                X[i] = PreviousX[i] = Clamp(x + Random() * 1.4f, 8, Width - 8);
                Y[i] = PreviousY[i] = Clamp(y + Random() * 1.4f, 8, Height - 8);
                Angle[i] = t == 0 ? 0 : (float)Math.PI; cooldown[i] = Random() * .8f;
            }
            flow.Update(this);
#if UNITY_5_3_OR_NEWER
            if (execution == CrowdExecution.Auto) execution = Count >= 10000 ? DefaultExecution : CrowdExecution.BurstSingleThread;
            if (execution != CrowdExecution.ManagedReference && BurstCrowdKernel.Available)
            {
                // Worker-thread capability must be verified independently on MiniGame.
                // Its baseline uses synchronous Burst jobs; desktop can use colored jobs.
                UnityEngine.RuntimePlatform platform = UnityEngine.Application.platform;
                bool desktop = platform == UnityEngine.RuntimePlatform.WindowsEditor || platform == UnityEngine.RuntimePlatform.WindowsPlayer
                    || platform == UnityEngine.RuntimePlatform.OSXEditor || platform == UnityEngine.RuntimePlatform.OSXPlayer
                    || platform == UnityEngine.RuntimePlatform.LinuxEditor || platform == UnityEngine.RuntimePlatform.LinuxPlayer;
                kernel = new BurstCrowdKernel(this, random, execution == CrowdExecution.BurstParallel && desktop);
            }
#endif
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
#if UNITY_5_3_OR_NEWER
            kernel?.Dispose();
#endif
        }

        private float Random() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return (random >> 8) / 16777216f; }
        private static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));
        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        public void Step()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CrowdSimulation));
            if (Finished) return;
#if UNITY_5_3_OR_NEWER
            if (kernel != null)
            {
                long flowStamp = Stopwatch.GetTimestamp(); Tick++;
                if (Tick % 6 == 0) flow.Update(this);
                double flowCost = Ms(flowStamp);
                kernel.Step(this, Tick, ZeroDamage); GridMs += flowCost; RecordProfile();
                return;
            }
#endif
            long stamp = Stopwatch.GetTimestamp(); Tick++;
            if (Tick % 6 == 0) flow.Update(this);
            Array.Clear(counts, 0, counts.Length); Array.Clear(forceX, 0, Count); Array.Clear(forceY, 0, Count);
            Array.Clear(damage, 0, Count); Array.Clear(budget, 0, Count);
            for (int c = 0; c < heads.Length; c++) heads[c] = -1;
            for (int k = 0; k < Count; k++)
            {
                int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                PreviousX[i] = X[i]; PreviousY[i] = Y[i];
                if (Hp[i] <= 0) { Death[i] = Math.Max(0, Death[i] - StepSeconds / 4); continue; }
                int c = (int)(Y[i] / cell) * cols + (int)(X[i] / cell) + Team[i] * cols * rows;
                next[i] = heads[c]; heads[c] = i;
                counts[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++;
            }
            int sum = 0;
            for (int c = 0; c < counts.Length; c++) { offsets[c] = cursors[c] = sum; sum += counts[c]; }
            offsets[counts.Length] = sum;
            for (int k = 0; k < Count; k++) { int i = (Tick & 1) == 0 ? k : Count - 1 - k; if (Hp[i] > 0) ids[cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++] = i; }
            GridMs = Ms(stamp); stamp = Stopwatch.GetTimestamp();
            for (int k = 0; k < counts.Length; k++)
            {
                int c = (Tick & 1) == 0 ? k : counts.Length - 1 - k;
                if (counts[c] == 0) continue;
                Separate(c, c);
                if (c % sepCols < sepCols - 1) Separate(c, c + 1);
                if (c < sepCols * (sepRows - 1))
                {
                    if (c % sepCols > 0) Separate(c, c + sepCols - 1);
                    Separate(c, c + sepCols);
                    if (c % sepCols < sepCols - 1) Separate(c, c + sepCols + 1);
                }
            }
            SeparationMs = Ms(stamp); stamp = Stopwatch.GetTimestamp();
            float attack = diameter + 3.6f, attack2 = attack * attack;
            for (int i = 0; i < Count; i++)
            {
                if (Hp[i] <= 0) continue;
                int target = Target[i]; float nearest = attack2;
                if (target >= 0 && Hp[target] > 0 && Team[target] != Team[i])
                { float dx = X[target] - X[i], dy = Y[target] - Y[i]; nearest = dx * dx + dy * dy; if (nearest > attack2) target = -1; }
                else target = -1;
                if (target < 0)
                {
                    int cx = (int)(X[i] / cell), cy = (int)(Y[i] / cell), seen = 0; nearest = attack2;
                    for (int oy = -1; oy <= 1 && seen < 24; oy++) for (int ox = -1; ox <= 1 && seen < 24; ox++)
                    {
                        int gx = cx + ox, gy = cy + oy;
                        if (gx < 0 || gy < 0 || gx >= cols || gy >= rows) continue;
                        for (int j = heads[(1 - Team[i]) * cols * rows + gy * cols + gx]; j >= 0 && seen < 24; j = next[j])
                        { seen++; float dx = X[j] - X[i], dy = Y[j] - Y[i], d = dx * dx + dy * dy; if (d < nearest) { nearest = d; target = j; } }
                    }
                }
                Target[i] = target; cooldown[i] -= StepSeconds; Hit[i] = Math.Max(0, Hit[i] - StepSeconds * 4);
                float tx, ty;
                if (target >= 0)
                {
                    tx = X[target] - X[i]; ty = Y[target] - Y[i];
                    if (cooldown[i] <= 0) { if (!ZeroDamage) damage[target] += 24 + Random() * 12; cooldown[i] = .55f + Random() * .35f; }
                }
                else flow.Direction(1 - Team[i], X[i], Y[i], out tx, out ty);
                float distance = Math.Max(.001f, (float)Math.Sqrt(tx * tx + ty * ty));
                float drive = target < 0 ? 32 * (.88f + i % 17 * .014f) : Math.Max(0, ((float)Math.Sqrt(nearest) - diameter) * 4);
                float dxv = tx / distance * drive + forceX[i] * 14, dyv = ty / distance * drive + forceY[i] * 14;
                float length = (float)Math.Sqrt(dxv * dxv + dyv * dyv), limit = length > 52.8f ? 52.8f / length : 1;
                Vx[i] += (dxv * limit - Vx[i]) / 3; Vy[i] += (dyv * limit - Vy[i]) / 3;
                if (target >= 0 || (i + Tick) % 4 == 0) Angle[i] = (float)Math.Atan2(ty, tx);
            }
            DecisionMs = Ms(stamp); stamp = Stopwatch.GetTimestamp();
            // All decisions above read old positions and HP; both armies commit together.
            for (int i = 0; i < Count; i++) if (Hp[i] > 0)
            {
                X[i] = Clamp(X[i] + Vx[i] * StepSeconds, 8, Width - 8); Y[i] = Clamp(Y[i] + Vy[i] * StepSeconds, 8, Height - 8);
                Hp[i] -= damage[i]; if (damage[i] > 0) Hit[i] = 1;
                if (Hp[i] <= 0) { Alive[Team[i]]--; Death[i] = 1; }
            }
            CommitMs = Ms(stamp);
            RecordProfile();
        }

        private void RecordProfile()
        {
            TotalGridMs += GridMs; TotalSeparationMs += SeparationMs;
            TotalDecisionMs += DecisionMs; TotalCommitMs += CommitMs;
        }

        private void Separate(int a, int b)
        {
            for (int p = offsets[a]; p < offsets[a + 1]; p++)
            {
                int i = ids[p];
                for (int q = a == b ? p + 1 : offsets[b]; q < offsets[b + 1] && budget[i] < 48; q++)
                {
                    int j = ids[q]; budget[i]++; if (budget[j] >= 48) continue; budget[j]++;
                    float dx = X[i] - X[j], dy = Y[i] - Y[j], d2 = dx * dx + dy * dy;
                    if (d2 >= diameter * diameter) continue;
                    if (d2 < .0001f) { dx = i < j ? .01f : -.01f; dy = i < j ? .005f : -.005f; d2 = .000125f; }
                    float f = diameter / (float)Math.Sqrt(d2) - 1;
                    forceX[i] += dx * f; forceY[i] += dy * f; forceX[j] -= dx * f; forceY[j] -= dy * f;
                }
            }
        }
    }
}
