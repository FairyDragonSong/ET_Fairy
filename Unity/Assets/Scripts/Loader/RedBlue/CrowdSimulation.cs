using System;
using System.Diagnostics;

namespace ET.RedBlue
{
    /// <summary>模拟执行路径。保留参考实现与实验实现，便于核对状态和性能。</summary>
    public enum CrowdExecution
    {
        Auto,
        ManagedReference,
        BurstSingleThread,
        BurstParallel,
        BurstColoredSingleThread,
        BurstPackedSingleThread,
        BurstPackedBoundedSingleThread,
        BurstPackedSplitSingleThread
    }

    /// <summary>
    /// 保存全部单位状态并推进固定步长模拟。每个数组下标对应同一名士兵，避免逐单位创建 Unity 对象。
    /// 普通 tick 按建网格、避让、决策、统一提交的顺序执行；分帧模式只拆分执行时间，不拆分逻辑 tick。
    /// </summary>
    public sealed class CrowdSimulation : IDisposable
    {
        public const float Width = 1800, Height = 1100, StepSeconds = 1f / 30;
        public readonly int Count;
        public readonly float Radius;
        // PreviousX/Y 与 X/Y 是渲染插值的两端；Hp、Hit、Death 分别表示生命、受击闪烁、死亡淡出。
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
        public double TotalFlowMs { get; private set; }
        public float Time => Tick * StepSeconds;
        public bool Finished => Alive[0] == 0 || Alive[1] == 0;

        // 临时力与伤害先累积，再统一提交，避免先遍历的单位提前改变后续单位读取的状态。
        internal readonly float[] forceX, forceY, damage, cooldown;
        internal readonly byte[] budget;
        // heads/next 构成按阵营分开的寻敌链表；其余数组构成连续存放单位 ID 的避让分桶。
        internal readonly int[] heads, next, counts, offsets, cursors, ids;
        internal readonly int cols, rows, sepCols, sepRows;
        internal readonly float cell, diameter;
        internal readonly FlowField flow = new FlowField();
        private uint random;
        private bool disposed;
        public bool SplitStepPending { get; private set; }

        private double splitFlowMs;
        public static CrowdExecution DefaultExecution = CrowdExecution.BurstParallel;
        // 小游戏并行必须同时具备显式配置和实际原生工作线程，不能仅凭平台名称开启。
        public static bool AllowMiniGameParallel;
#if UNITY_5_3_OR_NEWER
        private readonly BurstCrowdKernel kernel;
        public string Backend => kernel != null ? kernel.Backend : "Managed reference";
        public bool AccelerationVerified => kernel != null && kernel.IsBurstCompiled;
        public bool SupportsSplitStep => kernel != null && kernel.SupportsSplitStep;

        /// <summary>返回实际执行过避让的线程索引，用于验证是否真的并行。</summary>
        public int[] SeparationThreadIndices() => kernel?.SeparationThreadIndices() ?? new int[0];
#else
        public string Backend => "Managed reference";
        public bool AccelerationVerified => false;
        public bool SupportsSplitStep => false;
        /// <summary>纯 .NET 参考实现没有 Unity Job 线程记录。</summary>
        public int[] SeparationThreadIndices() => new int[0];
#endif
        /// <summary>按固定种子生成单位；阵型编号依次为正面、岛屿、钳形、密集压力阵型。</summary>
        public CrowdSimulation(int count = 2000, int formation = 0, uint seed = 7241, CrowdExecution execution = CrowdExecution.Auto)
        {
            Count = Math.Max(2, Math.Min(50000, count));
            random = seed == 0 ? 1u : seed;
            Radius = Clamp((float)Math.Sqrt(20000f / Count) * 2.5f, 1.65f, 3.4f);
            diameter = Radius * 2;
            cell = Math.Max(8, (float)Math.Ceiling(diameter + 3.6f));
            cols = (int)Math.Ceiling(Width / cell);
            rows = (int)Math.Ceiling(Height / cell);
            sepCols = (int)Math.Ceiling(Width / diameter);
            sepRows = (int)Math.Ceiling(Height / diameter);
            X = new float[Count];
            Y = new float[Count];
            PreviousX = new float[Count];
            PreviousY = new float[Count];
            Vx = new float[Count];
            Vy = new float[Count];
            Angle = new float[Count];
            Hp = new float[Count];
            Hit = new float[Count];
            Death = new float[Count];
            Team = new byte[Count];
            Target = new int[Count];
            forceX = new float[Count];
            forceY = new float[Count];
            damage = new float[Count];
            cooldown = new float[Count];
            budget = new byte[Count];
            next = new int[Count];
            heads = new int[cols * rows * 2];
            counts = new int[sepCols * sepRows];
            offsets = new int[counts.Length + 1];
            cursors = new int[counts.Length];
            ids = new int[Count];
            int half = (Count + 1) / 2;
            for (int i = 0; i < Count; i++)
            {
                int t = formation == 3 ? i % 2 : i < half ? 0 : 1;
                int k = t == 0 ? i : i - half, total = t == 0 ? half : Count - half;
                Team[i] = (byte)t;
                Alive[t]++;
                Hp[i] = 100;
                Target[i] = -1;
                int c = (int)Math.Ceiling(Math.Sqrt(total * .79)), r = (total + c - 1) / c;
                float x = 65 + k % c / (float)c * 735 + t * 935;
                float y = 95 + k / c / (float)r * 910;
                if (formation == 1)
                {
                    // 当前 Islands 的实际布局为每个阵营六组；种子扰动只打散完全规则的坐标。
                    int group = k % 6, index = k / 6;
                    c = (int)Math.Ceiling(Math.Sqrt(Math.Ceiling(total / 6.0) * 1.5));
                    float spacing = Math.Min(7.8f, 220f / c);
                    x = 90 + group % 3 * 550 + t * 270 + index % c * spacing;
                    y = 120 + group / 3 * 500 + index / c * spacing;
                }
                else if (formation == 2)
                {
                    if (t == 0)
                    {
                        x = 690 + k % c / (float)c * 420;
                        y = 240 + k / c / (float)r * 620;
                    }
                    else
                    {
                        int local = k / 2;
                        c = (int)Math.Ceiling(Math.Sqrt(Math.Ceiling(total / 2.0) * 1.25));
                        r = Math.Max(1, (total + 2 * c - 1) / (2 * c));
                        x = 90 + local % c / (float)c * 460 + k % 2 * 1160;
                        y = 130 + local / c / (float)r * 840;
                    }
                }
                else if (formation == 3)
                {
                    c = (int)Math.Ceiling(Math.Sqrt(Count * 1.5));
                    x = 650 + i % c * 1.8f;
                    y = 360 + i / c * 1.8f;
                }

                X[i] = PreviousX[i] = Clamp(x + Random() * 1.4f, 8, Width - 8);
                Y[i] = PreviousY[i] = Clamp(y + Random() * 1.4f, 8, Height - 8);
                Angle[i] = t == 0 ? 0 : (float)Math.PI;
                cooldown[i] = Random() * .8f;
            }

            flow.Update(this);
#if UNITY_5_3_OR_NEWER
            if (execution == CrowdExecution.Auto)
                execution = Count >= 10000 ? DefaultExecution : CrowdExecution.BurstSingleThread;
            if (execution != CrowdExecution.ManagedReference && BurstCrowdKernel.Available)
            {
                // 桌面可使用分色 Jobs；小游戏还需独立验证工作线程能力。
                UnityEngine.RuntimePlatform platform = UnityEngine.Application.platform;
                bool desktop = platform == UnityEngine.RuntimePlatform.WindowsEditor
                    || platform == UnityEngine.RuntimePlatform.WindowsPlayer
                    || platform == UnityEngine.RuntimePlatform.OSXEditor
                    || platform == UnityEngine.RuntimePlatform.OSXPlayer
                    || platform == UnityEngine.RuntimePlatform.LinuxEditor
                    || platform == UnityEngine.RuntimePlatform.LinuxPlayer;
                bool nativeMiniGameWorkers = AllowMiniGameParallel && Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount > 0;
                kernel = new BurstCrowdKernel(
                    this, random,
                    execution == CrowdExecution.BurstParallel && (desktop || nativeMiniGameWorkers),
                    execution == CrowdExecution.BurstColoredSingleThread,
                    execution == CrowdExecution.BurstPackedSingleThread
                        || execution == CrowdExecution.BurstPackedBoundedSingleThread
                        || execution == CrowdExecution.BurstPackedSplitSingleThread,
                    execution == CrowdExecution.BurstPackedBoundedSingleThread);
            }
#endif
        }

        /// <summary>释放 Burst 内核固定的数组句柄；重复释放不产生额外操作。</summary>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
#if UNITY_5_3_OR_NEWER
            kernel?.Dispose();
#endif
        }

        /// <summary>推进确定性随机状态。调用次数和顺序也是模拟规则的一部分。</summary>
        private float Random()
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            return (random >> 8) / 16777216f;
        }

        /// <summary>将数值限制在闭区间内，供纯 C# 参考路径使用。</summary>
        private static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));

        /// <summary>将 Stopwatch 时间戳差换算成墙钟毫秒，不代表纯 CPU 指令执行时间。</summary>
        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        /// <summary>同步完成一个 1/30 秒 tick；存在未提交的分帧 tick 时禁止混用。</summary>
        public void Step()
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.Step");
            if (disposed)
                throw new ObjectDisposedException(nameof(CrowdSimulation));
            if (SplitStepPending)
                throw new InvalidOperationException("Complete the pending split step first");
            if (Finished)
                return;
#if UNITY_5_3_OR_NEWER
            if (kernel != null)
            {
                long flowStamp = Stopwatch.GetTimestamp();
                Tick++;
                if (Tick % 6 == 0)
                    flow.Update(this);
                double flowCost = Ms(flowStamp);
                kernel.Step(this, Tick, ZeroDamage);
                GridMs += flowCost;
                TotalFlowMs += flowCost;
                RecordProfile();
                return;
            }

#endif
            long stamp = Stopwatch.GetTimestamp();
            Tick++;
            if (Tick % 6 == 0)
                flow.Update(this);
            Array.Clear(counts, 0, counts.Length);
            Array.Clear(forceX, 0, Count);
            Array.Clear(forceY, 0, Count);
            Array.Clear(damage, 0, Count);
            Array.Clear(budget, 0, Count);
            for (int c = 0; c < heads.Length; c++)
                heads[c] = -1;
            for (int k = 0; k < Count; k++)
            {
                int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                PreviousX[i] = X[i];
                PreviousY[i] = Y[i];
                if (Hp[i] <= 0)
                {
                    Death[i] = Math.Max(0, Death[i] - StepSeconds / 4);
                    continue;
                }

                int c = (int)(Y[i] / cell) * cols + (int)(X[i] / cell) + Team[i] * cols * rows;
                next[i] = heads[c];
                heads[c] = i;
                counts[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++;
            }

            // 前缀和确定每个格子的连续 ID 区间；奇偶 tick 的遍历方向必须与 Burst 路径一致。
            int sum = 0;
            for (int c = 0; c < counts.Length; c++)
            {
                offsets[c] = cursors[c] = sum;
                sum += counts[c];
            }

            offsets[counts.Length] = sum;
            for (int k = 0; k < Count; k++)
            {
                int i = (Tick & 1) == 0 ? k : Count - 1 - k;
                if (Hp[i] > 0)
                    ids[cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++] = i;
            }

            GridMs = Ms(stamp);
            stamp = Stopwatch.GetTimestamp();
            for (int k = 0; k < counts.Length; k++)
            {
                int c = (Tick & 1) == 0 ? k : counts.Length - 1 - k;
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

            SeparationMs = Ms(stamp);
            stamp = Stopwatch.GetTimestamp();
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
                    // 寻敌上限为 24 名候选，遍历次序和严格小于比较必须与 Burst 路径一致。
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
                Hit[i] = Math.Max(0, Hit[i] - StepSeconds * 4);
                float tx, ty;
                if (target >= 0)
                {
                    tx = X[target] - X[i];
                    ty = Y[target] - Y[i];
                    if (cooldown[i] <= 0)
                    {
                        if (!ZeroDamage)
                            damage[target] += 24 + Random() * 12;
                        cooldown[i] = .55f + Random() * .35f;
                    }
                }
                else
                    flow.Direction(1 - Team[i], X[i], Y[i], out tx, out ty);
                float distance = Math.Max(.001f, (float)Math.Sqrt(tx * tx + ty * ty));
                float drive = target < 0 ? 32 * (.88f + i % 17 * .014f) : Math.Max(0, ((float)Math.Sqrt(nearest) - diameter) * 4);
                float dxv = tx / distance * drive + forceX[i] * 14, dyv = ty / distance * drive + forceY[i] * 14;
                float length = (float)Math.Sqrt(dxv * dxv + dyv * dyv), limit = length > 52.8f ? 52.8f / length : 1;
                Vx[i] += (dxv * limit - Vx[i]) / 3;
                Vy[i] += (dyv * limit - Vy[i]) / 3;
                if (target >= 0 || (i + Tick) % 4 == 0)
                    Angle[i] = (float)Math.Atan2(ty, tx);
            }

            DecisionMs = Ms(stamp);
            stamp = Stopwatch.GetTimestamp();
            // 决策读取本 tick 开始时的位置和生命值，红蓝双方在此统一提交移动与伤害。
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

            CommitMs = Ms(stamp);
            RecordProfile();
        }

        /// <summary>准备下一 tick 的流场、网格与前半避让；暂不增加 Tick 或提交移动。</summary>
        public void BeginSplitStep()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(CrowdSimulation));
            if (SplitStepPending || !SupportsSplitStep || Finished)
                throw new InvalidOperationException("Cannot begin split step");
#if UNITY_5_3_OR_NEWER
            using var profile = RedBlueProfiler.Measure("RedBlue.Step.Begin");
            long stamp = Stopwatch.GetTimestamp();
            if ((Tick + 1) % 6 == 0)
                flow.Update(this);
            splitFlowMs = Ms(stamp);
            kernel.BeginSplitStep(this, Tick + 1, ZeroDamage);
            SplitStepPending = true;
#endif
        }

        /// <summary>完成剩余避让、决策与提交后才增加 Tick，并累计完整 tick 的阶段耗时。</summary>
        public void CompleteSplitStep()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(CrowdSimulation));
            if (!SplitStepPending)
                throw new InvalidOperationException("No pending split step");
#if UNITY_5_3_OR_NEWER
            using var profile = RedBlueProfiler.Measure("RedBlue.Step.End");
            kernel.CompleteSplitStep(this);
            Tick++;
            GridMs += splitFlowMs;
            TotalFlowMs += splitFlowMs;
            SplitStepPending = false;
            RecordProfile();
#endif
        }

        /// <summary>累计已完成 tick 的阶段耗时；GridMs 已包含该 tick 的流场开销。</summary>
        private void RecordProfile()
        {
            TotalGridMs += GridMs;
            TotalSeparationMs += SeparationMs;
            TotalDecisionMs += DecisionMs;
            TotalCommitMs += CommitMs;
        }

        /// <summary>处理两个格子的单位对；同格只访问后续单位，避免重复计算同一对。</summary>
        private void Separate(int a, int b)
        {
            for (int p = offsets[a]; p < offsets[a + 1]; p++)
            {
                int i = ids[p];
                for (int q = a == b ? p + 1 : offsets[b]; q < offsets[b + 1] && budget[i] < 48; q++)
                {
                    // 每名单位最多尝试 48 次配对；即使对方预算耗尽，本方这次尝试仍计数。
                    int j = ids[q];
                    budget[i]++;
                    if (budget[j] >= 48)
                        continue;
                    budget[j]++;
                    float dx = X[i] - X[j], dy = Y[i] - Y[j], d2 = dx * dx + dy * dy;
                    if (d2 >= diameter * diameter)
                        continue;
                    if (d2 < .0001f)
                    {
                        dx = i < j ? .01f : -.01f;
                        dy = i < j ? .005f : -.005f;
                        d2 = .000125f;
                    }

                    float f = diameter / (float)Math.Sqrt(d2) - 1;
                    forceX[i] += dx * f;
                    forceY[i] += dy * f;
                    forceX[j] -= dx * f;
                    forceY[j] -= dy * f;
                }
            }
        }
    }
}
