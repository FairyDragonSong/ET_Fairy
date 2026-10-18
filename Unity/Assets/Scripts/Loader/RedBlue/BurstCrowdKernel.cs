using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ET.RedBlue
{
    // The public state remains shared with the managed reference and renderer.
    // Arrays are pinned once; jobs complete before Step returns, so no job ever
    // outlives its owner and no copy is needed between the two representations.
    internal sealed unsafe class BurstCrowdKernel : IDisposable
    {
        private readonly List<GCHandle> pins = new List<GCHandle>(30);
        private Buffers buffers;
        private readonly bool parallel;
        private bool disposed;
        public string Backend => parallel ? "Burst + colored Jobs" : "Burst single thread";
        public bool IsBurstCompiled => !disposed && buffers.Compiled[0] == 1 && buffers.Compiled[1] == 1;
        public static bool Available => BurstCompiler.IsEnabled;
        private void* Pin(Array array)
        {
            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            pins.Add(handle);
            return handle.AddrOfPinnedObject().ToPointer();
        }
        public BurstCrowdKernel(CrowdSimulation s, uint seed, bool useParallel)
        {
            parallel = useParallel;
            try
            {
                buffers = new Buffers
                {
                    Count = s.Count, Radius = s.Radius, diameter = s.Radius * 2,
                    cols = s.cols, rows = s.rows, sepCols = s.sepCols, sepRows = s.sepRows, cell = s.cell,
                    countsCount = s.counts.Length, headsCount = s.heads.Length,
                    X = (float*)Pin(s.X), Y = (float*)Pin(s.Y), PreviousX = (float*)Pin(s.PreviousX), PreviousY = (float*)Pin(s.PreviousY),
                    Vx = (float*)Pin(s.Vx), Vy = (float*)Pin(s.Vy), Angle = (float*)Pin(s.Angle), Hp = (float*)Pin(s.Hp),
                    Hit = (float*)Pin(s.Hit), Death = (float*)Pin(s.Death), Team = (byte*)Pin(s.Team), Target = (int*)Pin(s.Target), Alive = (int*)Pin(s.Alive),
                    forceX = (float*)Pin(s.forceX), forceY = (float*)Pin(s.forceY), damage = (float*)Pin(s.damage), cooldown = (float*)Pin(s.cooldown),
                    budget = (byte*)Pin(s.budget), next = (int*)Pin(s.next), heads = (int*)Pin(s.heads), counts = (int*)Pin(s.counts),
                    offsets = (int*)Pin(s.offsets), cursors = (int*)Pin(s.cursors), ids = (int*)Pin(s.ids),
                    guideX = (float*)Pin(s.flow.GuideX), guideY = (float*)Pin(s.flow.GuideY), RandomState = (uint*)Pin(new uint[] { seed }),
                    Compiled = (int*)Pin(new int[2])
                };
            }
            catch { Dispose(); throw; }
        }
        public void Step(CrowdSimulation s, int tick, bool zeroDamage)
        {
            if (disposed) throw new ObjectDisposedException(nameof(BurstCrowdKernel));
            buffers.Tick = tick; buffers.ZeroDamage = zeroDamage ? 1 : 0;
            long stamp = Stopwatch.GetTimestamp();
            new StageJob { Data = buffers, Stage = 0 }.Run();
            s.GridMs = Elapsed(stamp); stamp = Stopwatch.GetTimestamp();
            if (parallel)
            {
                JobHandle handle = default;
                int work = ((buffers.sepCols + 2) / 3) * ((buffers.sepRows + 2) / 3);
                try
                {
                    for (int color = 0; color < 9; color++)
                        handle = new SeparationJob { Data = buffers, Color = (tick & 1) == 0 ? color : 8 - color }.Schedule(work, 32, handle);
                }
                finally { handle.Complete(); }
            }
            else new StageJob { Data = buffers, Stage = 1 }.Run();
            s.SeparationMs = Elapsed(stamp); stamp = Stopwatch.GetTimestamp();
            new StageJob { Data = buffers, Stage = 2 }.Run();
            s.DecisionMs = Elapsed(stamp); stamp = Stopwatch.GetTimestamp();
            new StageJob { Data = buffers, Stage = 3 }.Run();
            s.CommitMs = Elapsed(stamp);
        }
        private static double Elapsed(long stamp) => (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int i = 0; i < pins.Count; i++) if (pins[i].IsAllocated) pins[i].Free();
            pins.Clear(); buffers = default; GC.SuppressFinalize(this);
        }
        ~BurstCrowdKernel() { Dispose(); }
        [BurstDiscard]
        private static void MarkManaged(ref int compiled) { compiled = 0; }
        [BurstCompile(CompileSynchronously = true)]
        private struct StageJob : IJob
        {
            public Buffers Data; public int Stage;
            public void Execute()
            {
                int compiled = 1; MarkManaged(ref compiled); Data.Compiled[Stage == 1 ? 1 : 0] = compiled;
                switch (Stage)
                {
                    case 0: Data.BuildGrid(); break;
                    case 1: Data.SeparateAll(); break;
                    case 2: Data.Decide(); break;
                    case 3: Data.Commit(); break;
                }
            }
        }
        [BurstCompile(CompileSynchronously = true)]
        private struct SeparationJob : IJobParallelFor
        {
            public Buffers Data; public int Color;
            public void Execute(int index)
            {
                if (index == 0) { int compiled = 1; MarkManaged(ref compiled); Data.Compiled[1] = compiled; }
                int width = (Data.sepCols + 2) / 3;
                int x = (index % width) * 3 + Color % 3, y = (index / width) * 3 + Color / 3;
                if (x < Data.sepCols && y < Data.sepRows) Data.SeparateCell(y * Data.sepCols + x);
            }
        }
        // A cell writes its own bin, right bin and three bins on the next row.
        // Origins with the same (x % 3, y % 3) cannot overlap those write regions.
        // Nine dependent color passes preserve equal/opposite forces without atomics.
        private struct Buffers
        {
            public const float Width = 1800, Height = 1100, StepSeconds = 1f / 30;
            public int Count, Tick, ZeroDamage, cols, rows, sepCols, sepRows, countsCount, headsCount;
            public float Radius, diameter, cell;
            [NativeDisableUnsafePtrRestriction] public float* X;
            [NativeDisableUnsafePtrRestriction] public float* Y;
            [NativeDisableUnsafePtrRestriction] public float* PreviousX;
            [NativeDisableUnsafePtrRestriction] public float* PreviousY;
            [NativeDisableUnsafePtrRestriction] public float* Vx;
            [NativeDisableUnsafePtrRestriction] public float* Vy;
            [NativeDisableUnsafePtrRestriction] public float* Angle;
            [NativeDisableUnsafePtrRestriction] public float* Hp;
            [NativeDisableUnsafePtrRestriction] public float* Hit;
            [NativeDisableUnsafePtrRestriction] public float* Death;
            [NativeDisableUnsafePtrRestriction] public float* forceX;
            [NativeDisableUnsafePtrRestriction] public float* forceY;
            [NativeDisableUnsafePtrRestriction] public float* damage;
            [NativeDisableUnsafePtrRestriction] public float* cooldown;
            [NativeDisableUnsafePtrRestriction] public float* guideX;
            [NativeDisableUnsafePtrRestriction] public float* guideY;
            [NativeDisableUnsafePtrRestriction] public int* Target;
            [NativeDisableUnsafePtrRestriction] public int* Alive;
            [NativeDisableUnsafePtrRestriction] public int* next;
            [NativeDisableUnsafePtrRestriction] public int* heads;
            [NativeDisableUnsafePtrRestriction] public int* counts;
            [NativeDisableUnsafePtrRestriction] public int* offsets;
            [NativeDisableUnsafePtrRestriction] public int* cursors;
            [NativeDisableUnsafePtrRestriction] public int* ids;
            [NativeDisableUnsafePtrRestriction] public byte* Team;
            [NativeDisableUnsafePtrRestriction] public byte* budget;
            [NativeDisableUnsafePtrRestriction] public uint* RandomState;
            [NativeDisableUnsafePtrRestriction] public int* Compiled;
            private float Random()
            {
                uint value = RandomState[0]; value ^= value << 13; value ^= value >> 17; value ^= value << 5;
                RandomState[0] = value; return (value >> 8) / 16777216f;
            }
            private static float Clamp(float v, float lo, float hi) => math.clamp(v, lo, hi);
            public void Direction(int team, float px, float py, out float dx, out float dy)
            {
                const int size = 48, cols = 38, rows = 23, cells = cols * rows;
                float gx = math.clamp(px / size - .5f, 0, cols - 1.001f), gy = math.clamp(py / size - .5f, 0, rows - 1.001f);
                int ix = (int)gx, iy = (int)gy, c = team * cells + iy * cols + ix;
                float u = gx - ix, v = gy - iy, a = (1-u)*(1-v), b = u*(1-v), d = (1-u)*v, e = u*v;
                dx = guideX[c]*a + guideX[c+1]*b + guideX[c+cols]*d + guideX[c+cols+1]*e - px;
                dy = guideY[c]*a + guideY[c+1]*b + guideY[c+cols]*d + guideY[c+cols+1]*e - py;
            }
            public void SeparateCell(int c)
            {
                if (counts[c] == 0) return;
                Separate(c, c);
                if (c % sepCols < sepCols - 1) Separate(c, c + 1);
                if (c < sepCols * (sepRows - 1))
                {
                    if (c % sepCols > 0) Separate(c, c + sepCols - 1);
                    Separate(c, c + sepCols);
                    if (c % sepCols < sepCols - 1) Separate(c, c + sepCols + 1);
                }
            }
            public void BuildGrid()
            {
            UnsafeUtility.MemClear(counts, countsCount * sizeof(int)); UnsafeUtility.MemClear(forceX, Count * sizeof(float)); UnsafeUtility.MemClear(forceY, Count * sizeof(float));
            UnsafeUtility.MemClear(damage, Count * sizeof(float)); UnsafeUtility.MemClear(budget, Count);
            for (int c = 0; c < headsCount; c++) heads[c] = -1;
            for (int k = 0; k < Count; k++)
            {
                int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                PreviousX[i] = X[i]; PreviousY[i] = Y[i];
                if (Hp[i] <= 0) { Death[i] = math.max(0, Death[i] - StepSeconds / 4); continue; }
                int c = (int)(Y[i] / cell) * cols + (int)(X[i] / cell) + Team[i] * cols * rows;
                next[i] = heads[c]; heads[c] = i;
                counts[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++;
            }
            int sum = 0;
            for (int c = 0; c < countsCount; c++) { offsets[c] = cursors[c] = sum; sum += counts[c]; }
            offsets[countsCount] = sum;
            for (int k = 0; k < Count; k++) { int i = (Tick & 1) == 0 ? k : Count - 1 - k; if (Hp[i] > 0) ids[cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++] = i; }

            }
            public void SeparateAll()
            {
            for (int k = 0; k < countsCount; k++)
            {
                int c = (Tick & 1) == 0 ? k : countsCount - 1 - k;
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

            }
            public void Decide()
            {
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
                Target[i] = target; cooldown[i] -= StepSeconds; Hit[i] = math.max(0, Hit[i] - StepSeconds * 4);
                float tx, ty;
                if (target >= 0)
                {
                    tx = X[target] - X[i]; ty = Y[target] - Y[i];
                    if (cooldown[i] <= 0) { if (ZeroDamage == 0) damage[target] += 24 + Random() * 12; cooldown[i] = .55f + Random() * .35f; }
                }
                else Direction(1 - Team[i], X[i], Y[i], out tx, out ty);
                float distance = math.max(.001f, math.sqrt(tx * tx + ty * ty));
                float drive = target < 0 ? 32 * (.88f + i % 17 * .014f) : math.max(0, (math.sqrt(nearest) - diameter) * 4);
                float dxv = tx / distance * drive + forceX[i] * 14, dyv = ty / distance * drive + forceY[i] * 14;
                float length = math.sqrt(dxv * dxv + dyv * dyv), limit = length > 52.8f ? 52.8f / length : 1;
                Vx[i] += (dxv * limit - Vx[i]) / 3; Vy[i] += (dyv * limit - Vy[i]) / 3;
                if (target >= 0 || (i + Tick) % 4 == 0) Angle[i] = math.atan2(ty, tx);
            }

            }
            public void Commit()
            {
            // All decisions above read old positions and HP; both armies commit together.
            for (int i = 0; i < Count; i++) if (Hp[i] > 0)
            {
                X[i] = Clamp(X[i] + Vx[i] * StepSeconds, 8, Width - 8); Y[i] = Clamp(Y[i] + Vy[i] * StepSeconds, 8, Height - 8);
                Hp[i] -= damage[i]; if (damage[i] > 0) Hit[i] = 1;
                if (Hp[i] <= 0) { Alive[Team[i]]--; Death[i] = 1; }
            }

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
                    float f = diameter / math.sqrt(d2) - 1;
                    forceX[i] += dx * f; forceY[i] += dy * f; forceX[j] -= dx * f; forceY[j] -= dy * f;
                }
            }
        }

        }
    }
}
