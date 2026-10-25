using System;

namespace ET.RedBlue
{
    /// <summary>把显示帧时间换算为 30Hz 逻辑 tick，支持整 tick 与相邻显示帧分段执行。</summary>
    public sealed class CrowdStepScheduler
    {
        public double Accumulator { get; private set; }

        /// <summary>清除累计时间；调用方在暂停、恢复、变速或重开时使用。</summary>
        public void Reset() => Accumulator = 0;

        /// <summary>推进本显示帧的模拟工作，返回本帧实际提交的完整 tick 数。</summary>
        public int Advance(CrowdSimulation simulation, double seconds, float speed, bool split)
        {
            double step = CrowdSimulation.StepSeconds;
            // 沿用累计时间最多八个 tick、单帧最多追赶四个 tick 的上限。
            Accumulator = Math.Min(Accumulator + Math.Max(0, seconds) * speed, step * 8);
            int completed = 0;
            bool spread = split && simulation.SupportsSplitStep;
            // 不支持分帧或积欠达到两个 tick 时走追赶路径；先完成已开始的 tick。
            if (!spread || Accumulator >= step * 2)
            {
                while (Accumulator >= step && completed < 4 && !simulation.Finished)
                {
                    if (simulation.SplitStepPending)
                        simulation.CompleteSplitStep();
                    else
                        simulation.Step();
                    Accumulator -= step;
                    completed++;
                }

                return completed;
            }

            if (simulation.Finished)
                return 0;
            // 首段可提前准备，末段必须等完整步长到期后提交；只有提交才扣除累计时间。
            if (simulation.SplitStepPending)
            {
                if (Accumulator >= step)
                {
                    simulation.CompleteSplitStep();
                    Accumulator -= step;
                    completed = 1;
                }
            }
            else if (Accumulator > 0)
                simulation.BeginSplitStep();
            return completed;
        }
    }
}
