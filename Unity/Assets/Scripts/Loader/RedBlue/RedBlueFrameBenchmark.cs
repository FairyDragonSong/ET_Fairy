using System;
using UnityEngine;

namespace ET.RedBlue
{
    /// <summary>统计正常 Update 的帧间隔，每五秒回传一次、每场景六个窗口；不是 Unity Profiler 帧采集。</summary>
    public sealed class RedBlueFrameBenchmark
    {
        /// <summary>一个窗口的整帧、模拟及场景条件证据，用于核验人口、频率和帧率门槛。</summary>
        [Serializable]
        public sealed class Result
        {
            public int schema = 1, count = 50000, formation = 1, seed = 7241, window, frames, steps, tick, alive, drawn;
            public string kind = "frame", buildId, sessionId, scenario, backend, renderer, unity, gpu;
            public int width, height, targetFps, activeCameras;
            public bool development, profilerEnabled, focusedThroughout = true, zeroDamage, burstVerified;
            public double seconds, fps, simulationHz, p95FrameMs, maxFrameMs, over20MsPercent, over33MsPercent;
            public double simulationMsPerFrame, submitMsPerFrame, restartMs;
            public bool splitTicks, pendingStep;
            public int simulationWorkFrames, multipleStepFrames, maxStepsPerFrame;
            public double maxSimulationMsPerFrame, maxSimulationDebtMs,
                flowMsPerTick, gridMsPerTick, separationMsPerTick, decisionMsPerTick, commitMsPerTick;
            public string note = "Normal full-frame intervals; no reduced tick rate or population. Five-second windows include startup. Not a Unity Profiler frame capture.";
        }

        private readonly RedBlueCpuExperiment config;
        private readonly Action<Result> completed;
        private readonly string sessionId;
        private readonly string scenario;
        private readonly double restartMs;
        private readonly int formation;
        private readonly double[] intervals = new double[8192];
        private int samples, steps, window, over20, over33, activeCameras;
        private double seconds, simMs, submitMs, max;
        private bool focused = true;
        private int simulationWorkFrames, multipleStepFrames, maxStepsPerFrame;
        private double maxSimulationMs, maxDebtMs, previousFlow, previousGrid, previousSeparation, previousDecision, previousCommit;
        public bool Complete => window >= 6;

        /// <summary>为刚重建的模拟开始新场景；阶段累计值从零开始记录差量。</summary>
        public RedBlueFrameBenchmark(
            RedBlueCpuExperiment config, string sessionId, string scenario,
            double restartMs, Action<Result> completed, int formation = 1)
        {
            this.config = config;
            this.sessionId = sessionId;
            this.scenario = scenario;
            this.restartMs = restartMs;
            this.completed = completed;
            this.formation = formation;
        }

        /// <summary>一次失焦即持续标记该场景不满足全程前台条件，不过滤或丢弃慢窗口。</summary>
        public void InvalidateFocus()
        {
            focused = false;
        }

        /// <summary>累计本帧数据；窗口到期时计算 P95 和阶段差量，回调后重置窗口计数。</summary>
        public void Observe(
            double dt, int stepCount, double simulationMs, double submitCost,
            CrowdSimulation s, CrowdRenderer renderer, bool drawing,
            bool splitTicks = false, double debtSeconds = 0)
        {
            if (Complete)
                return;
            if (samples < intervals.Length)
                intervals[samples] = dt * 1000;
            samples++;
            steps += stepCount;
            seconds += dt;
            simMs += simulationMs;
            submitMs += submitCost;
            if (simulationMs > 0)
                simulationWorkFrames++;
            if (stepCount > 1)
                multipleStepFrames++;
            maxStepsPerFrame = Math.Max(maxStepsPerFrame, stepCount);
            maxSimulationMs = Math.Max(maxSimulationMs, simulationMs);
            maxDebtMs = Math.Max(maxDebtMs, debtSeconds * 1000);
            activeCameras = Math.Max(activeCameras, Camera.allCamerasCount);
            max = Math.Max(max, dt * 1000);
            if (dt > .020)
                over20++;
            if (dt > 1.0 / 30)
                over33++;
            if (seconds < 5)
                return;
            int stored = Math.Min(samples, intervals.Length);
            Array.Sort(intervals, 0, stored);
            // 阶段累计值只在完整 tick 提交时更新；flow 是 grid 的子项，分析时不能重复相加。
            completed(new Result
            {
                buildId = config.buildId,
                sessionId = sessionId,
                scenario = scenario,
                window = window,
                formation = formation,
                frames = samples,
                steps = steps,
                tick = s.Tick,
                alive = s.Alive[0] + s.Alive[1],
                drawn = drawing ? renderer.Drawn : 0,
                backend = s.Backend,
                renderer = renderer.Backend,
                unity = Application.unityVersion,
                gpu = SystemInfo.graphicsDeviceName,
                width = Screen.width,
                height = Screen.height,
                targetFps = Application.targetFrameRate,
                activeCameras = activeCameras,
                development = Debug.isDebugBuild,
                profilerEnabled = Debug.isDebugBuild && UnityEngine.Profiling.Profiler.enabled,
                focusedThroughout = focused,
                zeroDamage = s.ZeroDamage,
                burstVerified = s.AccelerationVerified,
                seconds = seconds,
                fps = samples / seconds,
                simulationHz = steps / seconds,
                p95FrameMs = intervals[Math.Max(0, (int)Math.Ceiling(stored * .95) - 1)],
                maxFrameMs = max,
                over20MsPercent = over20 * 100.0 / samples,
                over33MsPercent = over33 * 100.0 / samples,
                simulationMsPerFrame = simMs / samples,
                submitMsPerFrame = submitMs / samples,
                restartMs = restartMs,
                splitTicks = splitTicks,
                pendingStep = s.SplitStepPending,
                simulationWorkFrames = simulationWorkFrames,
                multipleStepFrames = multipleStepFrames,
                maxStepsPerFrame = maxStepsPerFrame,
                maxSimulationMsPerFrame = maxSimulationMs,
                maxSimulationDebtMs = maxDebtMs,
                flowMsPerTick = steps == 0 ? 0 : (s.TotalFlowMs - previousFlow) / steps,
                gridMsPerTick = steps == 0 ? 0 : (s.TotalGridMs - previousGrid) / steps,
                separationMsPerTick = steps == 0 ? 0 : (s.TotalSeparationMs - previousSeparation) / steps,
                decisionMsPerTick = steps == 0 ? 0 : (s.TotalDecisionMs - previousDecision) / steps,
                commitMsPerTick = steps == 0 ? 0 : (s.TotalCommitMs - previousCommit) / steps
            });
            previousFlow = s.TotalFlowMs;
            previousGrid = s.TotalGridMs;
            previousSeparation = s.TotalSeparationMs;
            previousDecision = s.TotalDecisionMs;
            previousCommit = s.TotalCommitMs;
            window++;
            samples = steps = over20 = over33 = activeCameras = 0;
            seconds = simMs = submitMs = max = 0;
            simulationWorkFrames = multipleStepFrames = maxStepsPerFrame = 0;
            maxSimulationMs = maxDebtMs = 0;
        }
    }
}
