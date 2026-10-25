using System;

namespace ET.RedBlue
{
    /// <summary>在粗网格上寻找各阵营最近的占用区域，为没有近战目标的单位提供移动方向。</summary>
    internal sealed class FlowField
    {
        private const int Size = 48, Cols = 38, Rows = 23, Cells = Cols * Rows;
        private readonly int[] counts = new int[Cells * 2], sources = new int[Cells], queue = new int[Cells];
        private readonly float[] x = new float[Cells * 2], y = new float[Cells * 2],
            guideX = new float[Cells * 2], guideY = new float[Cells * 2];
        internal float[] GuideX => guideX;
        internal float[] GuideY => guideY;

        /// <summary>统计存活单位的格内中心，再通过多源广度优先搜索把引导点传播到空格。</summary>
        public void Update(CrowdSimulation s)
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.FlowField");
            Array.Clear(counts, 0, counts.Length);
            Array.Clear(x, 0, x.Length);
            Array.Clear(y, 0, y.Length);
            for (int i = 0; i < s.Count; i++)
                if (s.Hp[i] > 0)
                {
                    int c = s.Team[i] * Cells + (int)(s.Y[i] / Size) * Cols + (int)(s.X[i] / Size);
                    counts[c]++;
                    x[c] += s.X[i];
                    y[c] += s.Y[i];
                }

            for (int t = 0; t < 2; t++)
            {
                int start = t * Cells, read = 0, write = 0;
                for (int c = 0; c < Cells; c++)
                {
                    sources[c] = -1;
                    if (counts[start + c] == 0)
                        continue;
                    x[start + c] /= counts[start + c];
                    y[start + c] /= counts[start + c];
                    sources[c] = c;
                    queue[write++] = c;
                }

                // 非空格同时作为搜索源；传播访问顺序固定，以保持同种子的回放一致。
                while (read < write)
                {
                    int c = queue[read++];
                    if (c % Cols > 0)
                        Visit(c - 1, c, ref write);
                    if (c % Cols < Cols - 1)
                        Visit(c + 1, c, ref write);
                    if (c >= Cols)
                        Visit(c - Cols, c, ref write);
                    if (c < Cells - Cols)
                        Visit(c + Cols, c, ref write);
                }

                for (int c = 0; c < Cells; c++)
                {
                    int source = sources[c];
                    guideX[start + c] = source < 0 ? (c % Cols + .5f) * Size : x[start + source];
                    guideY[start + c] = source < 0 ? (c / Cols + .5f) * Size : y[start + source];
                }
            }
        }

        /// <summary>首次到达相邻格时继承来源，并加入预分配队列。</summary>
        private void Visit(int next, int from, ref int write)
        {
            if (sources[next] >= 0)
                return;
            sources[next] = sources[from];
            queue[write++] = next;
        }

        /// <summary>双线性插值周围四格的引导点，返回从当前位置指向指定阵营的方向向量。</summary>
        public void Direction(int team, float px, float py, out float dx, out float dy)
        {
            float gx = Math.Max(0, Math.Min(Cols - 1.001f, px / Size - .5f)), gy = Math.Max(0, Math.Min(Rows - 1.001f, py / Size - .5f));
            int ix = (int)gx, iy = (int)gy, c = team * Cells + iy * Cols + ix;
            float u = gx - ix, v = gy - iy, a = (1 - u) * (1 - v), b = u * (1 - v), d = (1 - u) * v, e = u * v;
            dx = guideX[c] * a + guideX[c + 1] * b + guideX[c + Cols] * d + guideX[c + Cols + 1] * e - px;
            dy = guideY[c] * a + guideY[c + 1] * b + guideY[c + Cols] * d + guideY[c + Cols + 1] * e - py;
        }
    }
}
