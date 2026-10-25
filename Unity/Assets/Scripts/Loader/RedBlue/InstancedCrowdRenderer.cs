using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ET.RedBlue
{
    /// <summary>兼容渲染路径：在 CPU 上生成插值变换和颜色，再分批提交同一单位网格。</summary>
    internal sealed class InstancedCrowdRenderer : IDisposable
    {
        private const int Batch = 511;
        private readonly Mesh quad;
        private readonly Material material;
        private readonly Matrix4x4[] matrices = new Matrix4x4[Batch];
        private readonly Vector4[] colors = new Vector4[Batch];
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private static readonly int ColorId = Shader.PropertyToID("_SoldierColor");
        public int Drawn { get; private set; }
        public int DrawCalls { get; private set; }

        /// <summary>加载可用 Shader，创建独立材质和所有单位共用的四边形。</summary>
        public InstancedCrowdRenderer()
        {
            Material template = Resources.Load<Material>("RedBlue/CrowdMaterial");
            Shader shader = template != null ? template.shader : Resources.Load<Shader>("RedBlue/Crowd");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("RedBlue/Crowd shader is unavailable on this graphics device.");
            material = template != null ? new Material(template) : new Material(shader);
            material.enableInstancing = true;
            quad = new Mesh
            {
                name = "RedBlue shared soldier quad"
            };
            quad.vertices = new[]
            {
                new Vector3(-1, -1),
                new Vector3(1, -1),
                new Vector3(1, 1),
                new Vector3(-1, 1)
            };
            quad.uv = new[]
            {
                Vector2.zero,
                Vector2.right,
                Vector2.one,
                Vector2.up
            };
            quad.triangles = new[]
            {
                0,
                2,
                1,
                0,
                3,
                2
            };
            quad.RecalculateBounds();
        }

        /// <summary>跳过不可见单位，计算插值位置、受击颜色与淡出透明度，填满批次后提交。</summary>
        public void Draw(CrowdSimulation s, Camera camera, float alpha)
        {
            Drawn = DrawCalls = 0;
            float halfHeight = camera.orthographicSize, halfWidth = halfHeight * camera.aspect;
            Vector3 center = camera.transform.position;
            int n = 0;
            for (int i = 0; i < s.Count; i++)
            {
                bool alive = s.Hp[i] > 0;
                if (!alive && s.Death[i] <= 0)
                    continue;
                float x = Mathf.Lerp(s.PreviousX[i], s.X[i], alpha), y = Mathf.Lerp(s.PreviousY[i], s.Y[i], alpha);
                if (Mathf.Abs(x - center.x) > halfWidth + 10 || Mathf.Abs(y - center.y) > halfHeight + 10)
                    continue;
                Color color = s.Team[i] == 0 ? new Color(.95f, .20f, .22f) : new Color(.16f, .53f, 1);
                color = Color.Lerp(color, Color.white, s.Hit[i] * .7f);
                color.a = alive ? 1 : s.Death[i] * .55f;
                float size = s.Radius * 1.25f;
                matrices[n] = Matrix4x4.TRS(
                    new Vector3(x, y, 0),
                    Quaternion.Euler(0, 0, s.Angle[i] * Mathf.Rad2Deg),
                    new Vector3(size * 1.35f, size, 1));
                colors[n++] = color;
                Drawn++;
                if (n == Batch)
                {
                    Flush(n, camera);
                    n = 0;
                }
            }

            if (n > 0)
                Flush(n, camera);
        }

        /// <summary>支持 Instancing 时合批提交；否则逐单位绘制，作为兼容诊断回退。</summary>
        private void Flush(int n, Camera camera)
        {
            if (SystemInfo.supportsInstancing)
            {
                properties.SetVectorArray(ColorId, colors);
                Graphics.DrawMeshInstanced(
                    quad, 0, material, matrices, n, properties,
                    ShadowCastingMode.Off, false, 0, camera, LightProbeUsage.Off);
                DrawCalls++;
            }
            else
            {
                // 兼容诊断路径的开销较高，DrawCalls 会如实记录逐单位提交数量。
                for (int i = 0; i < n; i++)
                {
                    properties.SetVector(ColorId, colors[i]);
                    Graphics.DrawMesh(
                        quad, matrices[i], material, 0, camera, 0, properties,
                        ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
                    DrawCalls++;
                }
            }
        }

        /// <summary>释放此后端持有的共享网格和独立材质。</summary>
        public void Dispose()
        {
            UnityEngine.Object.Destroy(quad);
            UnityEngine.Object.Destroy(material);
        }
    }
}
