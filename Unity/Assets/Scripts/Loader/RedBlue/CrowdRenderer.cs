using System;
using UnityEngine;

namespace ET.RedBlue
{
    public sealed class CrowdRenderer : IDisposable
    {
        public static bool ForceInstancing;
        private readonly StateTextureCrowdRenderer state;
        private readonly InstancedCrowdRenderer fallback;
        public int Drawn => state != null ? state.Drawn : fallback.Drawn;
        public int DrawCalls => state != null ? state.DrawCalls : fallback.DrawCalls;
        public string Backend => state != null ? "State texture / static mesh" : "Instancing reference";
        public CrowdRenderer()
        {
            Shader shader = Resources.Load<Shader>("RedBlue/CrowdData");
            if (!ForceInstancing && SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) && shader != null && shader.isSupported)
                state = new StateTextureCrowdRenderer(shader);
            else fallback = new InstancedCrowdRenderer();
        }
        public void Draw(CrowdSimulation simulation, Camera camera, float alpha)
        {
            if (state != null) state.Draw(simulation, camera, alpha);
            else fallback.Draw(simulation, camera, alpha);
        }
        public void Dispose() { state?.Dispose(); fallback?.Dispose(); }
    }
}
