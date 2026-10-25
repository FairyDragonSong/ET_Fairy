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
    /// <summary>
    /// 在 Burst 中执行网格、避让、决策和提交。托管状态数组只固定一次，供参考实现与渲染共享。
    /// 每次阶段调用都同步完成 Job；packed 路径另用按格连续的临时数组改善访问局部性。
    /// </summary>
    internal sealed unsafe class BurstCrowdKernel : IDisposable
    {
        private readonly List<GCHandle> pins = new List<GCHandle>(30);
        private Buffers buffers;
        private readonly bool parallel;
        private readonly bool coloredSequential;
        private bool disposed;
        public string Backend => parallel ? "Burst + colored Jobs"
            : coloredSequential ? "Burst colored / single thread"
            : buffers.BoundedPacked != 0 ? "Burst packed bounded / single thread"
            : buffers.PackedX != null ? "Burst packed / single thread"
            : "Burst single thread";
        public bool IsBurstCompiled => !disposed && buffers.Compiled[0] == 1 && buffers.Compiled[1] == 1;
        public bool SupportsSplitStep => !parallel && !coloredSequential && buffers.PackedX != null;

        /// <summary>收集实际执行过避让的线程索引，不以配置值代替运行证据。</summary>
        public int[] SeparationThreadIndices()
        {
            var indices = new List<int>();
            if (!disposed)
                for (int i = 0; i < buffers.ThreadSlots; i++)
                    if (buffers.ThreadSeen[i] != 0)
                        indices.Add(i);
            return indices.ToArray();
        }

        public static bool Available => BurstCompiler.IsEnabled;

        /// <summary>固定托管数组并登记句柄，使 Job 使用期间的指针保持有效。</summary>
        private void* Pin(Array array)
        {
            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            pins.Add(handle);
            return handle.AddrOfPinnedObject().ToPointer();
        }

        /// <summary>绑定共享状态和临时缓冲；packed 仅允许保持原配对顺序的单线程模式。</summary>
        public BurstCrowdKernel(
            CrowdSimulation s, uint seed, bool useParallel,
            bool coloredSequential = false, bool packed = false, bool boundedPacked = false)
        {
            if (packed && (useParallel || coloredSequential))
                throw new ArgumentException("Packed experiment requires original-order single thread");
            parallel = useParallel;
            this.coloredSequential = coloredSequential;
            try
            {
                buffers = new Buffers
                {
                    Count = s.Count,
                    Radius = s.Radius,
                    diameter = s.Radius * 2,
                    BoundedPacked = boundedPacked ? 1 : 0,
                    cols = s.cols,
                    rows = s.rows,
                    sepCols = s.sepCols,
                    sepRows = s.sepRows,
                    cell = s.cell,
                    countsCount = s.counts.Length,
                    headsCount = s.heads.Length,
                    X = (float*)Pin(s.X),
                    Y = (float*)Pin(s.Y),
                    PreviousX = (float*)Pin(s.PreviousX),
                    PreviousY = (float*)Pin(s.PreviousY),
                    Vx = (float*)Pin(s.Vx),
                    Vy = (float*)Pin(s.Vy),
                    Angle = (float*)Pin(s.Angle),
                    Hp = (float*)Pin(s.Hp),
                    Hit = (float*)Pin(s.Hit),
                    Death = (float*)Pin(s.Death),
                    Team = (byte*)Pin(s.Team),
                    Target = (int*)Pin(s.Target),
                    Alive = (int*)Pin(s.Alive),
                    forceX = (float*)Pin(s.forceX),
                    forceY = (float*)Pin(s.forceY),
                    damage = (float*)Pin(s.damage),
                    cooldown = (float*)Pin(s.cooldown),
                    budget = (byte*)Pin(s.budget),
                    next = (int*)Pin(s.next),
                    heads = (int*)Pin(s.heads),
                    counts = (int*)Pin(s.counts),
                    offsets = (int*)Pin(s.offsets),
                    cursors = (int*)Pin(s.cursors),
                    ids = (int*)Pin(s.ids),
                    guideX = (float*)Pin(s.flow.GuideX),
                    guideY = (float*)Pin(s.flow.GuideY),
                    RandomState = (uint*)Pin(new uint[] { seed }),
                    Compiled = (int*)Pin(new int[2]),
                    ThreadSlots = Math.Max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount),
                    ThreadSeen = (int*)Pin(new int[Math.Max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount)]),
                    PackedX = packed ? (float*)Pin(new float[s.Count]) : null,
                    PackedY = packed ? (float*)Pin(new float[s.Count]) : null,
                    PackedForceX = packed ? (float*)Pin(new float[s.Count]) : null,
                    PackedForceY = packed ? (float*)Pin(new float[s.Count]) : null,
                    PackedBudget = packed ? (byte*)Pin(new byte[s.Count]) : null,
                    ActiveCells = packed ? (int*)Pin(new int[s.Count + 1]) : null
                };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>同步执行完整 tick 的四阶段，并分别记录墙钟耗时。</summary>
        public void Step(CrowdSimulation s, int tick, bool zeroDamage)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(BurstCrowdKernel));
            buffers.DeferPrevious = 0;
            buffers.Tick = tick;
            buffers.ZeroDamage = zeroDamage ? 1 : 0;
            long stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Grid"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 0
                }.Run();
            s.GridMs = Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Separation"))
            {
                if (parallel || coloredSequential)
                {
                    JobHandle handle = default;
                    int work = ((buffers.sepCols + 2) / 3) * ((buffers.sepRows + 2) / 3);
                    try
                    {
                        for (int color = 0; color < 9; color++)
                        {
                            var job = new SeparationJob
                            {
                                Data = buffers,
                                Color = (tick & 1) == 0 ? color : 8 - color
                            };
                            if (parallel)
                                handle = job.Schedule(work, 32, handle);
                            else
                                job.Run(work);
                        }
                    }
                    finally
                    {
                        handle.Complete();
                    }
                }
                else
                    new StageJob
                    {
                        Data = buffers,
                        Stage = 1
                    }.Run();
            }

            s.SeparationMs = Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Decision"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 2
                }.Run();
            s.DecisionMs = Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Commit"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 3
                }.Run();
            s.CommitMs = Elapsed(stamp);
        }

        /// <summary>执行网格和前半非空格避让；延迟修改插值端点，保留已提交的位置供渲染。</summary>
        public void BeginSplitStep(CrowdSimulation s, int tick, bool zeroDamage)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(BurstCrowdKernel));
            if (!SupportsSplitStep)
                throw new InvalidOperationException("Split steps require packed single thread");
            buffers.DeferPrevious = 1;
            buffers.Tick = tick;
            buffers.ZeroDamage = zeroDamage ? 1 : 0;
            long stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Grid"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 0
                }.Run();
            s.GridMs = Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Separation.Begin"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 4
                }.Run();
            s.SeparationMs = Elapsed(stamp);
        }

        /// <summary>按原顺序完成后半避让，回写临时结果后执行决策和统一提交。</summary>
        public void CompleteSplitStep(CrowdSimulation s)
        {
            long stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Separation.End"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 5
                }.Run();
            s.SeparationMs += Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Decision"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 2
                }.Run();
            s.DecisionMs = Elapsed(stamp);
            stamp = Stopwatch.GetTimestamp();
            using (RedBlueProfiler.Measure("RedBlue.Commit"))
                new StageJob
                {
                    Data = buffers,
                    Stage = 3
                }.Run();
            s.CommitMs = Elapsed(stamp);
            buffers.DeferPrevious = 0;
        }

        /// <summary>返回指定时间戳以来经过的墙钟毫秒。</summary>
        private static double Elapsed(long stamp) => (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;

        /// <summary>释放全部固定句柄；调用前的 Job 已同步结束，不存在跨生命周期的指针访问。</summary>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            for (int i = 0; i < pins.Count; i++)
                if (pins[i].IsAllocated)
                    pins[i].Free();
            pins.Clear();
            buffers = default;
            GC.SuppressFinalize(this);
        }

        // 显式释放遗漏时的兜底；正常路径由 CrowdSimulation.Dispose 负责。
        ~BurstCrowdKernel()
        {
            Dispose();
        }

        // 托管执行会写入 0；Burst 编译时移除此调用，从而留下真实的编译路径标记。
        [BurstDiscard]
        private static void MarkManaged(ref int compiled)
        {
            compiled = 0;
        }

        /// <summary>串行阶段入口：0 网格、1 避让、2 决策、3 提交、4/5 分帧避让。</summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct StageJob : IJob
        {
            public Buffers Data;
            public int Stage;
            /// <summary>记录 Burst 路径后执行指定阶段；末段避让结束时才回写 packed 结果。</summary>
            public void Execute()
            {
                int compiled = 1;
                MarkManaged(ref compiled);
                Data.Compiled[Stage == 1 || Stage >= 4 ? 1 : 0] = compiled;
                if (Stage >= 4)
                    Data.ThreadSeen[0] = 1;
                switch (Stage)
                {
                    case 0:
                        Data.BuildGrid();
                        break;
                    case 1:
                        Data.ThreadSeen[0] = 1;
                        if (Data.PackedX != null)
                            Data.SeparatePackedAll();
                        else
                            Data.SeparateAll();
                        break;
                    case 2:
                        Data.Decide();
                        break;
                    case 3:
                        Data.Commit();
                        break;
                    case 4:
                        Data.SeparatePackedRange(0, Data.ActiveCells[0] / 2);
                        break;
                    case 5:
                        Data.SeparatePackedRange(Data.ActiveCells[0] / 2, Data.ActiveCells[0]);
                        Data.ScatterPacked();
                        break;
                }
            }
        }

        /// <summary>处理一种颜色的起始格；九种颜色依次调度，同色格之间可并行。</summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct SeparationJob : IJobParallelFor
        {
            public Buffers Data;
            public int Color;
            [NativeSetThreadIndex]
            public int ThreadIndex;
            /// <summary>把任务索引映射到间隔三格的坐标，并记录实际工作线程。</summary>
            public void Execute(int index)
            {
                if ((index & 255) == 0 && (uint)ThreadIndex < (uint)Data.ThreadSlots)
                    Data.ThreadSeen[ThreadIndex] = 1;
                if (index == 0)
                {
                    int compiled = 1;
                    MarkManaged(ref compiled);
                    Data.Compiled[1] = compiled;
                }

                int width = (Data.sepCols + 2) / 3;
                int x = (index % width) * 3 + Color % 3, y = (index / width) * 3 + Color / 3;
                if (x < Data.sepCols && y < Data.sepRows)
                    Data.SeparateCell(y * Data.sepCols + x);
            }
        }

        /// <summary>
        /// Job 可直接访问的指针视图。原始单位数组以单位 ID 索引，Packed 数组以分桶后的槽位索引。
        /// 分色避让写当前格、右格及下一行三格；同色起点间隔三格，写区域不重叠，无需原子累加。
        /// </summary>
        private struct Buffers
        {
            public const float Width = 1800, Height = 1100, StepSeconds = 1f / 30;
            public int Count, Tick, ZeroDamage, cols, rows, sepCols, sepRows,
                countsCount, headsCount, ThreadSlots, BoundedPacked, DeferPrevious;
            public float Radius, diameter, cell;
            [NativeDisableUnsafePtrRestriction]
            public float* X;
            [NativeDisableUnsafePtrRestriction]
            public float* Y;
            [NativeDisableUnsafePtrRestriction]
            public float* PreviousX;
            [NativeDisableUnsafePtrRestriction]
            public float* PreviousY;
            [NativeDisableUnsafePtrRestriction]
            public float* Vx;
            [NativeDisableUnsafePtrRestriction]
            public float* Vy;
            [NativeDisableUnsafePtrRestriction]
            public float* Angle;
            [NativeDisableUnsafePtrRestriction]
            public float* Hp;
            [NativeDisableUnsafePtrRestriction]
            public float* Hit;
            [NativeDisableUnsafePtrRestriction]
            public float* Death;
            [NativeDisableUnsafePtrRestriction]
            public float* forceX;
            [NativeDisableUnsafePtrRestriction]
            public float* forceY;
            [NativeDisableUnsafePtrRestriction]
            public float* damage;
            [NativeDisableUnsafePtrRestriction]
            public float* cooldown;
            [NativeDisableUnsafePtrRestriction]
            public float* guideX;
            [NativeDisableUnsafePtrRestriction]
            public float* guideY;
            [NativeDisableUnsafePtrRestriction]
            public int* Target;
            [NativeDisableUnsafePtrRestriction]
            public int* Alive;
            [NativeDisableUnsafePtrRestriction]
            public int* next;
            [NativeDisableUnsafePtrRestriction]
            public int* heads;
            [NativeDisableUnsafePtrRestriction]
            public int* counts;
            [NativeDisableUnsafePtrRestriction]
            public int* offsets;
            [NativeDisableUnsafePtrRestriction]
            public int* cursors;
            [NativeDisableUnsafePtrRestriction]
            public int* ids;
            [NativeDisableUnsafePtrRestriction]
            public byte* Team;
            [NativeDisableUnsafePtrRestriction]
            public byte* budget;
            [NativeDisableUnsafePtrRestriction]
            public uint* RandomState;
            [NativeDisableUnsafePtrRestriction]
            public int* Compiled;
            [NativeDisableUnsafePtrRestriction]
            public int* ThreadSeen;
            [NativeDisableUnsafePtrRestriction]
            public float* PackedX;
            [NativeDisableUnsafePtrRestriction]
            public float* PackedY;
            [NativeDisableUnsafePtrRestriction]
            public float* PackedForceX;
            [NativeDisableUnsafePtrRestriction]
            public float* PackedForceY;
            [NativeDisableUnsafePtrRestriction]
            public byte* PackedBudget;
            [NativeDisableUnsafePtrRestriction]
            public int* ActiveCells;
            /// <summary>与参考路径一致的随机序列，状态保存在固定数组中并跨 Job 保留。</summary>
            private float Random()
            {
                uint value = RandomState[0];
                value ^= value << 13;
                value ^= value >> 17;
                value ^= value << 5;
                RandomState[0] = value;
                return (value >> 8) / 16777216f;
            }

            /// <summary>使用 Burst 支持的数学函数限制数值范围。</summary>
            private static float Clamp(float v, float lo, float hi) => math.clamp(v, lo, hi);

            /// <summary>对粗网格的引导点做双线性插值，与 FlowField.Direction 保持同一计算顺序。</summary>
            public void Direction(int team, float px, float py, out float dx, out float dy)
            {
                const int size = 48, cols = 38, rows = 23, cells = cols * rows;
                float gx = math.clamp(px / size - .5f, 0, cols - 1.001f), gy = math.clamp(py / size - .5f, 0, rows - 1.001f);
                int ix = (int)gx, iy = (int)gy, c = team * cells + iy * cols + ix;
                float u = gx - ix, v = gy - iy, a = (1 - u) * (1 - v), b = u * (1 - v), d = (1 - u) * v, e = u * v;
                dx = guideX[c] * a + guideX[c + 1] * b + guideX[c + cols] * d + guideX[c + cols + 1] * e - px;
                dy = guideY[c] * a + guideY[c + 1] * b + guideY[c + cols] * d + guideY[c + cols + 1] * e - py;
            }

            /// <summary>处理当前格及右侧、下一行的邻格组合，避免对称重复访问。</summary>
            public void SeparateCell(int c)
            {
                if (counts[c] == 0)
                    return;
                Separate(c, c);
                if (c % sepCols < sepCols - 1)
                    Separate(c, c + 1);
                if (c < sepCols * (sepRows - 1))
                {
                    if (c % sepCols > 0)
                        Separate(c, c + sepCols - 1);
                    Separate(c, c + sepCols);
                    if (c % sepCols < sepCols - 1)
                        Separate(c, c + sepCols + 1);
                }
            }

            /// <summary>清理本 tick 临时状态，建立寻敌链表和避让分桶，必要时聚集 packed 数据。</summary>
            public void BuildGrid()
            {
                UnsafeUtility.MemClear(counts, countsCount * sizeof(int));
                if (BoundedPacked == 0)
                {
                    UnsafeUtility.MemClear(forceX, Count * sizeof(float));
                    UnsafeUtility.MemClear(forceY, Count * sizeof(float));
                    UnsafeUtility.MemClear(budget, Count);
                }

                UnsafeUtility.MemClear(damage, Count * sizeof(float));
                for (int c = 0; c < headsCount; c++)
                    heads[c] = -1;
                for (int k = 0; k < Count; k++)
                {
                    int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                    if (DeferPrevious == 0)
                    {
                        PreviousX[i] = X[i];
                        PreviousY[i] = Y[i];
                    }

                    if (Hp[i] <= 0)
                    {
                        Death[i] = math.max(0, Death[i] - StepSeconds / 4);
                        if (BoundedPacked != 0)
                        {
                            forceX[i] = forceY[i] = 0;
                            budget[i] = 0;
                        }

                        continue;
                    }

                    int c = (int)(Y[i] / cell) * cols + (int)(X[i] / cell) + Team[i] * cols * rows;
                    next[i] = heads[c];
                    heads[c] = i;
                    counts[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++;
                }

                int sum = 0, active = 0;
                for (int c = 0; c < countsCount; c++)
                {
                    offsets[c] = cursors[c] = sum;
                    sum += counts[c];
                    if (ActiveCells != null && counts[c] != 0)
                        ActiveCells[++active] = c;
                }

                // ActiveCells[0] 是数量，其后按原格子顺序保存非空格索引。
                if (ActiveCells != null)
                    ActiveCells[0] = active;
                offsets[countsCount] = sum;
                for (int k = 0; k < Count; k++)
                {
                    int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                    if (Hp[i] <= 0)
                        continue;
                    ids[cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++] = i;
                }

                if (BoundedPacked != 0)
                {
                    // bounded 实验的存活原槽位稍后由 scatter 覆盖，死亡原槽位已在前面清零。
                    UnsafeUtility.MemClear(PackedForceX, sum * sizeof(float));
                    UnsafeUtility.MemClear(PackedForceY, sum * sizeof(float));
                    UnsafeUtility.MemClear(PackedBudget, sum);
                    // 聚集时连续写目标槽位；合并到 ID 分桶遍历会把写入重新打散。
                    for (int p = 0; p < sum; p++)
                    {
                        int i = ids[p];
                        PackedX[p] = X[i];
                        PackedY[p] = Y[i];
                    }
                }
                else if (PackedX != null)
                {
                    for (int p = 0; p < sum; p++)
                    {
                        int i = ids[p];
                        PackedX[p] = X[i];
                        PackedY[p] = Y[i];
                        PackedForceX[p] = PackedForceY[p] = 0;
                        PackedBudget[p] = 0;
                    }
                }
            }

            /// <summary>按奇偶 tick 的原遍历方向处理全部格子，作为非 packed 对照。</summary>
            public void SeparateAll()
            {
                for (int k = 0; k < countsCount; k++)
                {
                    int c = (Tick & 1) == 0 ? k : countsCount - 1 - k;
                    if (counts[c] == 0)
                        continue;
                    Separate(c, c);
                    if (c % sepCols < sepCols - 1)
                        Separate(c, c + 1);
                    if (c < sepCols * (sepRows - 1))
                    {
                        if (c % sepCols > 0)
                            Separate(c, c + sepCols - 1);
                        Separate(c, c + sepCols);
                        if (c % sepCols < sepCols - 1)
                            Separate(c, c + sepCols + 1);
                    }
                }
            }

            /// <summary>一次完成所有 packed 避让并回写，用于整 tick 对照。</summary>
            public void SeparatePackedAll()
            {
                SeparatePackedRange(0, ActiveCells[0]);
                ScatterPacked();
            }

            /// <summary>处理非空格序列的 [first, end) 范围；分段边界不改变配对和累加顺序。</summary>
            public void SeparatePackedRange(int first, int end)
            {
                // 非空格表由前缀和构造阶段按格号排列，奇数 tick 反向读取。
                int active = ActiveCells[0];
                for (int k = first; k < end; k++)
                {
                    int c = ActiveCells[(Tick & 1) == 0 ? k + 1 : active - k];
                    SeparatePacked(c, c);
                    if (c % sepCols < sepCols - 1)
                        SeparatePacked(c, c + 1);
                    if (c < sepCols * (sepRows - 1))
                    {
                        if (c % sepCols > 0)
                            SeparatePacked(c, c + sepCols - 1);
                        SeparatePacked(c, c + sepCols);
                        if (c % sepCols < sepCols - 1)
                            SeparatePacked(c, c + sepCols + 1);
                    }
                }
            }

            /// <summary>通过 ids 把按格连续的力和预算写回原单位槽位，供决策阶段使用。</summary>
            public void ScatterPacked()
            {
                for (int p = 0; p < offsets[countsCount]; p++)
                {
                    int i = ids[p];
                    forceX[i] = PackedForceX[p];
                    forceY[i] = PackedForceY[p];
                    budget[i] = PackedBudget[p];
                }
            }

            /// <summary>计算两个格子的 packed 单位对；保留原 48 次尝试预算和重合坐标处理。</summary>
            private void SeparatePacked(int a, int b)
            {
                int startB = offsets[b], endB = offsets[b + 1];
                if (startB == endB)
                    return;
                int endA = offsets[a + 1];
                if (BoundedPacked != 0)
                {
                    SeparatePackedBounded(
                        offsets[a], endA, startB, endB, a == b, diameter,
                        PackedX, PackedY, PackedForceX, PackedForceY, PackedBudget, ids);
                    return;
                }

                float diameter2 = diameter * diameter;
                for (int p = offsets[a]; p < endA; p++)
                {
                    int used = PackedBudget[p];
                    if (used >= 48)
                        continue;
                    float xi = PackedX[p], yi = PackedY[p], fx = PackedForceX[p], fy = PackedForceY[p];
                    for (int q = a == b ? p + 1 : startB; q < endB && used < 48; q++)
                    {
                        used++;
                        if (PackedBudget[q] >= 48)
                            continue;
                        PackedBudget[q]++;
                        float dx = xi - PackedX[q], dy = yi - PackedY[q], d2 = dx * dx + dy * dy;
                        if (d2 >= diameter2)
                            continue;
                        if (d2 < .0001f)
                        {
                            bool earlier = ids[p] < ids[q];
                            dx = earlier ? .01f : -.01f;
                            dy = earlier ? .005f : -.005f;
                            d2 = .000125f;
                        }

                        float f = diameter / math.sqrt(d2) - 1;
                        fx += dx * f;
                        fy += dy * f;
                        PackedForceX[q] -= dx * f;
                        PackedForceY[q] -= dy * f;
                    }

                    PackedBudget[p] = (byte)used;
                    PackedForceX[p] = fx;
                    PackedForceY[p] = fy;
                }
            }

            /// <summary>bounded 实验：预先计算预算允许的循环终点；仍保留原配对尝试计数。</summary>
            private static void SeparatePackedBounded(
                int startA, int endA, int startB, int endB, bool sameCell, float diameter,
                [NoAlias] float* px, [NoAlias] float* py,
                [NoAlias] float* forcesX, [NoAlias] float* forcesY,
                [NoAlias] byte* budgets, [NoAlias] int* unitIds)
            {
                // NoAlias 只说明参数来自不同数组；同一参数内的 p/q 仍共享该数组，不能视为独立分配。
                float diameter2 = diameter * diameter;
                for (int p = startA; p < endA; p++)
                {
                    int used = budgets[p];
                    if (used >= 48)
                        continue;
                    int first = sameCell ? p + 1 : startB;
                    int last = math.min(endB, first + 48 - used);
                    float xi = px[p], yi = py[p], fx = forcesX[p], fy = forcesY[p];
                    for (int q = first; q < last; q++)
                    {
                        if (budgets[q] >= 48)
                            continue;
                        budgets[q]++;
                        float dx = xi - px[q], dy = yi - py[q], d2 = dx * dx + dy * dy;
                        if (d2 >= diameter2)
                            continue;
                        if (d2 < .0001f)
                        {
                            bool earlier = unitIds[p] < unitIds[q];
                            dx = earlier ? .01f : -.01f;
                            dy = earlier ? .005f : -.005f;
                            d2 = .000125f;
                        }

                        float f = diameter / math.sqrt(d2) - 1;
                        fx += dx * f;
                        fy += dy * f;
                        forcesX[q] -= dx * f;
                        forcesY[q] -= dy * f;
                    }

                    budgets[p] = (byte)(used + last - first);
                    forcesX[p] = fx;
                    forcesY[p] = fy;
                }
            }

            /// <summary>保留或搜索敌人，累积攻击伤害，并根据目标、流场和避让力更新速度。</summary>
            public void Decide()
            {
                float attack = diameter + 3.6f, attack2 = attack * attack;
                for (int i = 0; i < Count; i++)
                {
                    if (Hp[i] <= 0)
                        continue;
                    int target = Target[i];
                    float nearest = attack2;
                    if (target >= 0 && Hp[target] > 0 && Team[target] != Team[i])
                    {
                        float dx = X[target] - X[i], dy = Y[target] - Y[i];
                        nearest = dx * dx + dy * dy;
                        if (nearest > attack2)
                            target = -1;
                    }
                    else
                        target = -1;
                    if (target < 0)
                    {
                        int cx = (int)(X[i] / cell), cy = (int)(Y[i] / cell), seen = 0;
                        nearest = attack2;
                        // 最多检查 24 名候选敌人；格子、链表和严格小于的选择顺序决定平距时的结果。
                        for (int oy = -1; oy <= 1 && seen < 24; oy++)
                            for (int ox = -1; ox <= 1 && seen < 24; ox++)
                            {
                                int gx = cx + ox, gy = cy + oy;
                                if (gx < 0 || gy < 0 || gx >= cols || gy >= rows)
                                    continue;
                                for (int j = heads[(1 - Team[i]) * cols * rows + gy * cols + gx]; j >= 0 && seen < 24; j = next[j])
                                {
                                    seen++;
                                    float dx = X[j] - X[i], dy = Y[j] - Y[i], d = dx * dx + dy * dy;
                                    if (d < nearest)
                                    {
                                        nearest = d;
                                        target = j;
                                    }
                                }
                            }
                    }

                    Target[i] = target;
                    cooldown[i] -= StepSeconds;
                    Hit[i] = math.max(0, Hit[i] - StepSeconds * 4);
                    float tx, ty;
                    if (target >= 0)
                    {
                        tx = X[target] - X[i];
                        ty = Y[target] - Y[i];
                        if (cooldown[i] <= 0)
                        {
                            // 关闭伤害时跳过伤害随机数，但仍更新攻击冷却；不能随意合并随机调用。
                            if (ZeroDamage == 0)
                                damage[target] += 24 + Random() * 12;
                            cooldown[i] = .55f + Random() * .35f;
                        }
                    }
                    else
                        Direction(1 - Team[i], X[i], Y[i], out tx, out ty);
                    float distance = math.max(.001f, math.sqrt(tx * tx + ty * ty));
                    float drive = target < 0 ? 32 * (.88f + i % 17 * .014f) : math.max(0, (math.sqrt(nearest) - diameter) * 4);
                    float dxv = tx / distance * drive + forceX[i] * 14, dyv = ty / distance * drive + forceY[i] * 14;
                    float length = math.sqrt(dxv * dxv + dyv * dyv), limit = length > 52.8f ? 52.8f / length : 1;
                    Vx[i] += (dxv * limit - Vx[i]) / 3;
                    Vy[i] += (dyv * limit - Vy[i]) / 3;
                    if (target >= 0 || (i + Tick) % 4 == 0)
                        Angle[i] = math.atan2(ty, tx);
                }
            }

            /// <summary>统一提交位置、生命与死亡计数；分帧模式在此更新渲染插值端点。</summary>
            public void Commit()
            {
                // 两个显示帧之间保留上一次提交的插值端点，直到本 tick 真正提交。
                if (DeferPrevious != 0)
                    for (int i = 0; i < Count; i++)
                    {
                        PreviousX[i] = X[i];
                        PreviousY[i] = Y[i];
                    }

                // 决策读旧位置和生命值；红蓝双方的移动与伤害统一生效。
                for (int i = 0; i < Count; i++)
                    if (Hp[i] > 0)
                    {
                        X[i] = Clamp(X[i] + Vx[i] * StepSeconds, 8, Width - 8);
                        Y[i] = Clamp(Y[i] + Vy[i] * StepSeconds, 8, Height - 8);
                        Hp[i] -= damage[i];
                        if (damage[i] > 0)
                            Hit[i] = 1;
                        if (Hp[i] <= 0)
                        {
                            Alive[Team[i]]--;
                            Death[i] = 1;
                        }
                    }
            }

            /// <summary>非 packed 避让：通过格内 ID 访问原状态，局部缓存当前单位的力与预算。</summary>
            private void Separate(int a, int b)
            {
                int startB = offsets[b], endB = offsets[b + 1];
                if (startB == endB)
                    return;
                int endA = offsets[a + 1];
                float diameter2 = diameter * diameter;
                for (int p = offsets[a]; p < endA; p++)
                {
                    int i = ids[p];
                    int used = budget[i];
                    if (used >= 48)
                        continue;
                    float xi = X[i], yi = Y[i], fx = forceX[i], fy = forceY[i];
                    // 格内 ID 唯一，同格从 p 后面开始，因此 j 不会等于 i，可安全局部缓存 i 的状态。
                    for (int q = a == b ? p + 1 : startB; q < endB && used < 48; q++)
                    {
                        int j = ids[q];
                        used++;
                        if (budget[j] >= 48)
                            continue;
                        budget[j]++;
                        float dx = xi - X[j], dy = yi - Y[j], d2 = dx * dx + dy * dy;
                        if (d2 >= diameter2)
                            continue;
                        if (d2 < .0001f)
                        {
                            dx = i < j ? .01f : -.01f;
                            dy = i < j ? .005f : -.005f;
                            d2 = .000125f;
                        }

                        float f = diameter / math.sqrt(d2) - 1;
                        fx += dx * f;
                        fy += dy * f;
                        forceX[j] -= dx * f;
                        forceY[j] -= dy * f;
                    }

                    budget[i] = (byte)used;
                    forceX[i] = fx;
                    forceY[i] = fy;
                }
            }
        }
    }
}
