using System;
using UnityEngine;

namespace ET.RedBlue
{
    /// <summary>选择状态纹理渲染或 Instancing 回退，并向演示层提供统一接口。</summary>
    public sealed class CrowdRenderer : IDisposable
    {
        public static bool ForceInstancing;
        private readonly StateTextureCrowdRenderer state;
        private readonly InstancedCrowdRenderer fallback;
        public int Drawn => state != null ? state.Drawn : fallback.Drawn;
        public int DrawCalls => state != null ? state.DrawCalls : fallback.DrawCalls;
        public string Backend => state != null ? "State texture / static mesh" : "Instancing reference";

        /// <summary>检查浮点纹理和 Shader 支持，必要时使用兼容渲染路径。</summary>
        public CrowdRenderer()
        {
            Shader shader = Resources.Load<Shader>("RedBlue/CrowdData");
            if (!ForceInstancing && SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) && shader != null && shader.isSupported)
                state = new StateTextureCrowdRenderer(shader);
            else
                fallback = new InstancedCrowdRenderer();
        }

        /// <summary>提交单位绘制；alpha 表示上一提交状态到当前提交状态之间的插值比例。</summary>
        public void Draw(CrowdSimulation simulation, Camera camera, float alpha)
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.DrawSubmit");
            if (state != null)
                state.Draw(simulation, camera, alpha);
            else
                fallback.Draw(simulation, camera, alpha);
        }

        /// <summary>释放实际创建的渲染后端。</summary>
        public void Dispose()
        {
            state?.Dispose();
            fallback?.Dispose();
        }
    }
}
