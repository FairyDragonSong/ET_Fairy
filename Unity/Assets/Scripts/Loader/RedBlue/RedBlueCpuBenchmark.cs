using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace ET.RedBlue
{
    /// <summary>由实验包注入的执行路径和回传配置；没有配置资源时使用默认设置。</summary>
    [Serializable]
    public sealed class RedBlueCpuExperiment
    {
        public string variant = "standard", buildId = "unknown", resultUrl = "";
        public bool simd, threads, allowMiniGameParallel,
            comparePacked, preferPacked, comparePackedBounded, preferPackedBounded,
            compareSplit, splitSimulationTicks, compareAllFormations;
        /// <summary>读取 Resources 内的实验配置，不在运行时推断手机的线程能力。</summary>
        public static RedBlueCpuExperiment Load()
        {
            var asset = Resources.Load<TextAsset>("RedBlueCpuExperiment");
            return asset == null ? new RedBlueCpuExperiment() : JsonUtility.FromJson<RedBlueCpuExperiment>(asset.text);
        }
    }

    /// <summary>
    /// 每显示帧推进一个固定 tick，预热 300 tick 后测量 300 tick；不受真实帧间隔影响。
    /// 拥有独立模拟实例，分配、重置、哈希和上传均在 Step 计时之外，不代替正常整帧测试。
    /// </summary>
    public sealed class RedBlueCpuBenchmark : IDisposable
    {
        /// <summary>一次测量轮次的回传数据，包含阶段耗时、运行配置及终点状态哈希。</summary>
        [Serializable]
        public sealed class Result
        {
            public int schema = 1, count = 50000, formation = 1, seed = 7241, warmupTicks = 300, measuredTicks = 300;
            public string variant, buildId, sessionId, unity, device, gpu, requestedExecution, backend, stateHash;
            public int run, jobWorkers, screenWidth, screenHeight;
            public bool simdBuild, threadsBuild, burstVerified,
                probeObservedWorkerExecution, separationWorkersObserved, profilerEnabled, development;
            public int[] probeThreadIndices, separationThreadIndices;
            public double meanStepMs, p95StepMs, maxStepMs, gridMs, separationMs, decisionMs, commitMs;
            public string note = "Fixed tick CPU benchmark, zero damage; includes Step instrumentation; not normal-frame FPS";
        }

        public CrowdSimulation Current { get; private set; }
        public bool Running { get; private set; }
        public string Status { get; private set; }
        public int Formation => config.compareAllFormations ? formation : 1;

        private readonly RedBlueCpuExperiment config;
        private readonly Action<Result> completed;
        private readonly int repetitions;
        private readonly bool includeParallel;
        private readonly bool profilerWasEnabled;
        private readonly int[] threadIndices;
        private readonly double[] steps = new double[300];
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private int run, execution, formation;
        private double grid, separate, decide, commit, sum;
        private bool Paired => config.comparePacked || config.comparePackedBounded || config.compareSplit;
        private CrowdExecution SelectedExecution => config.compareSplit
            ? (execution == 0 ? CrowdExecution.BurstPackedSingleThread : CrowdExecution.BurstPackedSplitSingleThread)
            : config.comparePackedBounded
                ? (execution == 0 ? CrowdExecution.BurstPackedSingleThread : CrowdExecution.BurstPackedBoundedSingleThread)
                : execution == 0 ? CrowdExecution.BurstSingleThread
                : config.comparePacked ? CrowdExecution.BurstPackedSingleThread
                : execution == 1 ? CrowdExecution.BurstColoredSingleThread
                : CrowdExecution.BurstParallel;

        /// <summary>保存 Profiler 状态、探测工作线程，并准备第一轮模拟。</summary>
        public RedBlueCpuBenchmark(
            RedBlueCpuExperiment config, Action<Result> completed,
            int repetitions = 3, bool forceParallelForEditorTest = false)
        {
            this.config = config;
            this.completed = completed;
            this.repetitions = repetitions;
            if (Paired && (config.threads || forceParallelForEditorTest))
                throw new ArgumentException("Packed comparison requires a single-thread build");
            profilerWasEnabled = UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled;
            includeParallel = !Paired && (forceParallelForEditorTest || (config.allowMiniGameParallel && JobsUtility.JobWorkerCount > 0));
            threadIndices = ProbeWorkers();
            Running = true;
            Reset();
        }

        /// <summary>按当前阵型、轮次和执行路径重建模拟，清空本轮计时。</summary>
        private void Reset()
        {
            Current?.Dispose();
            Current = new CrowdSimulation(50000, Formation, 7241, SelectedExecution)
            {
                ZeroDamage = true
            };
            grid = separate = decide = commit = sum = 0;
            Status = $"CPU benchmark formation {Formation}: {ExecutionName()} {run + 1}/{repetitions}: warmup";
        }

        /// <summary>推进一次预热或测量；轮次结束时回调，再切换路径、轮次或阵型。</summary>
        public void Advance()
        {
            if (!Running)
                return;
            if (Paired && UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled)
                UnityEngine.Profiling.Profiler.enabled = false;
            bool measuring = Current.Tick >= 300;
            using (RedBlueProfiler.Measure(measuring
                ? (config.comparePackedBounded ? "RedBlue.CpuBenchmark.Measure.PackedBoundedAB"
                    : execution == 0 ? "RedBlue.CpuBenchmark.Measure.Single"
                    : config.comparePacked ? "RedBlue.CpuBenchmark.Measure.PackedSingle"
                    : execution == 1 ? "RedBlue.CpuBenchmark.Measure.ColoredSingle"
                    : "RedBlue.CpuBenchmark.Measure.Jobs")
                : "RedBlue.CpuBenchmark.Warmup"))
            {
                long start = Stopwatch.GetTimestamp();
                if (config.compareSplit && execution == 1)
                {
                    Current.BeginSplitStep();
                    Current.CompleteSplitStep();
                }
                else
                    Current.Step();
                double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (measuring)
                {
                    int index = Current.Tick - 301;
                    steps[index] = elapsed;
                    sum += elapsed;
                    grid += Current.GridMs;
                    separate += Current.SeparationMs;
                    decide += Current.DecisionMs;
                    commit += Current.CommitMs;
                }
            }

            Status = $"CPU benchmark formation {Formation}: {ExecutionName()} {run + 1}/{repetitions}: tick {Current.Tick}/600";
            if (Current.Tick < 600)
                return;
            Array.Sort(steps);
            var result = new Result
            {
                variant = config.variant,
                buildId = config.buildId,
                sessionId = sessionId,
                simdBuild = config.simd,
                threadsBuild = config.threads,
                formation = Formation,
                unity = Application.unityVersion,
                device = SystemInfo.deviceModel,
                gpu = SystemInfo.graphicsDeviceName,
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                run = run,
                requestedExecution = ExecutionName(),
                backend = Current.Backend,
                burstVerified = Current.AccelerationVerified,
                jobWorkers = JobsUtility.JobWorkerCount,
                probeThreadIndices = threadIndices,
                probeObservedWorkerExecution = Array.Exists(threadIndices, i => i > 0),
                separationThreadIndices = Current.SeparationThreadIndices(),
                separationWorkersObserved = Array.Exists(Current.SeparationThreadIndices(), i => i > 0),
                profilerEnabled = UnityEngine.Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled,
                development = UnityEngine.Debug.isDebugBuild,
                stateHash = HashState(Current),
                meanStepMs = sum / 300,
                p95StepMs = steps[284],
                maxStepMs = steps[299],
                gridMs = grid / 300,
                separationMs = separate / 300,
                decisionMs = decide / 300,
                commitMs = commit / 300
            };
            completed?.Invoke(result);
            if (Paired)
            {
                // 三轮依次为 AB / BA / AB；run 编号保持配对，四阵型分别重置轮次。
                if (execution == (run & 1))
                {
                    execution = 1 - execution;
                    Reset();
                    return;
                }

                if (++run < repetitions)
                {
                    Reset();
                    return;
                }

                if (config.compareAllFormations && formation < 3)
                {
                    formation++;
                    run = execution = 0;
                    Reset();
                    return;
                }

                Running = false;
                if (UnityEngine.Debug.isDebugBuild)
                    UnityEngine.Profiling.Profiler.enabled = profilerWasEnabled;
                Status = config.compareSplit ? "CPU benchmark complete: packed / packed split; results logged"
                    : config.comparePackedBounded ? "CPU benchmark complete: packed / packed bounded; results logged"
                    : "CPU benchmark complete: baseline / packed single thread; results logged";
                return;
            }

            if (++run < repetitions)
            {
                Reset();
                return;
            }

            if (execution < 2 && includeParallel)
            {
                execution++;
                run = 0;
                Reset();
                return;
            }

            Running = false;
            Status = $"CPU benchmark complete: {result.backend}, mean {result.meanStepMs:F2} ms; results logged";
        }

        /// <summary>返回请求的执行路径名称，与实际 Backend 和线程证据分开记录。</summary>
        private string ExecutionName() => SelectedExecution.ToString();

        /// <summary>按固定数组顺序计算公开模拟状态的 SHA-256，避免反射元数据或裁剪影响顺序。</summary>
        private static string HashState(CrowdSimulation s)
        {
            // 包含公开模拟数组；并非包含私有随机状态和所有临时数组的完整回放哈希。
            using (var stream = new System.IO.MemoryStream())
            {
                foreach (Array array in new Array[]
                {
                    s.X,
                    s.Y,
                    s.PreviousX,
                    s.PreviousY,
                    s.Vx,
                    s.Vy,
                    s.Angle,
                    s.Hp,
                    s.Hit,
                    s.Death,
                    s.Team,
                    s.Target,
                    s.Alive
                })
                {
                    byte[] bytes = new byte[Buffer.ByteLength(array)];
                    Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }

        /// <summary>用短时计算任务记录实际参与的 Job 线程，不参与模拟性能计时。</summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct WorkerProbe : IJobParallelFor
        {
            [NativeSetThreadIndex]
            public int ThreadIndex;
            public NativeArray<int> Indices;
            public NativeArray<uint> Work;
            /// <summary>执行确定性的整数计算并写入线程索引，防止工作负载被优化掉。</summary>
            public void Execute(int index)
            {
                uint x = (uint)index + 1;
                for (int i = 0; i < 20000; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 17;
                    x ^= x << 5;
                }

                Work[index] = x;
                Indices[index] = ThreadIndex;
            }
        }

        /// <summary>运行探测任务并去重线程索引；无工作线程时直接记录主线程 0。</summary>
        private static int[] ProbeWorkers()
        {
            if (JobsUtility.JobWorkerCount == 0)
                return new[]
                {
                    0
                };
            using (var indices = new NativeArray<int>(128, Allocator.TempJob))
            using (var work = new NativeArray<uint>(128, Allocator.TempJob))
            {
                new WorkerProbe
                {
                    Indices = indices,
                    Work = work
                }.Schedule(128, 1).Complete();
                var unique = new SortedSet<int>();
                for (int i = 0; i < indices.Length; i++)
                    unique.Add(indices[i]);
                var result = new int[unique.Count];
                unique.CopyTo(result);
                return result;
            }
        }

        /// <summary>结束测试并释放所属模拟，恢复配对测试开始前的 Profiler 状态。</summary>
        public void Dispose()
        {
            Running = false;
            Current?.Dispose();
            Current = null;
            if (Paired && UnityEngine.Debug.isDebugBuild)
                UnityEngine.Profiling.Profiler.enabled = profilerWasEnabled;
        }
    }
}
