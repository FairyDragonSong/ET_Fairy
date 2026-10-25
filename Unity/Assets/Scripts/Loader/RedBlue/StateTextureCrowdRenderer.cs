using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ET.RedBlue
{
    /// <summary>
    /// 用静态四边形网格和前后两张状态纹理绘制单位，在 Shader 中插值位置。
    /// 每名单位在每张纹理中占一个 RGBAFloat 像素；需要顶点纹理读取，不依赖计算着色器。
    /// </summary>
    internal sealed class StateTextureCrowdRenderer : IDisposable
    {
        private const int TextureWidth = 256, UnitsPerMesh = 8192;
        private readonly Material material;
        private Texture2D previous, current;
        private Color[] pixels;
        private Mesh[] meshes;
        private CrowdSimulation source;
        private int uploadedTick = -1, allocatedCount;
        private bool fading;
        private Vector3 lastCamera;
        private float lastHalfHeight, lastAspect;
        public int Drawn { get; private set; }
        public int DrawCalls { get; private set; }

        private static readonly int PreviousId = Shader.PropertyToID("_PreviousState"), CurrentId = Shader.PropertyToID("_CurrentState");
        private static readonly int AlphaId = Shader.PropertyToID("_Interpolation"), RadiusId = Shader.PropertyToID("_Radius");
        /// <summary>创建独立材质；人口相关的网格和纹理延迟到首次绘制时分配。</summary>
        public StateTextureCrowdRenderer(Shader shader)
        {
            material = new Material(shader)
            {
                name = "RedBlue state texture material"
            };
        }

        /// <summary>模拟提交后才上传新状态；显示帧间仅更新插值系数并复用静态网格。</summary>
        public void Draw(CrowdSimulation s, Camera camera, float alpha)
        {
            bool replaced = !ReferenceEquals(source, s);
            if (allocatedCount != s.Count)
                Allocate(s);
            if (replaced || uploadedTick != s.Tick || (s.Finished && fading))
            {
                using var upload = RedBlueProfiler.Measure("RedBlue.StateUpload");
                // 若一次显示帧推进多个 tick，旧纹理不再对应上一 tick，需从 PreviousX/Y 重建。
                bool skippedTicks = !replaced && s.Tick - uploadedTick > 1;
                if (replaced || skippedTicks)
                {
                    Pack(s, skippedTicks);
                    previous.SetPixelData(pixels, 0);
                    previous.Apply(false, false);
                }
                else
                {
                    Texture2D swap = previous;
                    previous = current;
                    current = swap;
                }

                Pack(s, false);
                current.SetPixelData(pixels, 0);
                current.Apply(false, false);
                material.SetTexture(PreviousId, previous);
                material.SetTexture(CurrentId, current);
                material.SetFloat(RadiusId, s.Radius * 1.25f);
                source = s;
                uploadedTick = s.Tick;
                CountVisible(s, camera);
            }

            if (lastCamera != camera.transform.position || lastHalfHeight != camera.orthographicSize || lastAspect != camera.aspect)
                CountVisible(s, camera);
            material.SetFloat(AlphaId, alpha);
            DrawCalls = meshes.Length;
            for (int i = 0; i < meshes.Length; i++)
                Graphics.DrawMesh(
                    meshes[i], Matrix4x4.identity, material, 0, camera, 0, null,
                    ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
        }

        /// <summary>按人口分配双缓冲纹理，并分批创建带单位纹理坐标的静态四边形网格。</summary>
        private void Allocate(CrowdSimulation s)
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.AllocateRenderBuffers");
            ReleaseBuffers();
            allocatedCount = s.Count;
            int height = (s.Count + TextureWidth - 1) / TextureWidth;
            previous = StateTexture(height);
            current = StateTexture(height);
            pixels = new Color[TextureWidth * height];
            meshes = new Mesh[(s.Count + UnitsPerMesh - 1) / UnitsPerMesh];
            for (int batch = 0; batch < meshes.Length; batch++)
            {
                int start = batch * UnitsPerMesh, count = Math.Min(UnitsPerMesh, s.Count - start);
                var vertices = new Vector3[count * 4];
                var uv = new Vector2[count * 4];
                var lookup = new Vector2[count * 4];
                var indices = new int[count * 6];
                for (int i = 0; i < count; i++)
                {
                    int id = start + i, v = i * 4, k = i * 6;
                    vertices[v] = new Vector3(-1, -1);
                    vertices[v + 1] = new Vector3(1, -1);
                    vertices[v + 2] = new Vector3(1, 1);
                    vertices[v + 3] = new Vector3(-1, 1);
                    uv[v] = Vector2.zero;
                    uv[v + 1] = Vector2.right;
                    uv[v + 2] = Vector2.one;
                    uv[v + 3] = Vector2.up;
                    Vector2 texel = new Vector2((id % TextureWidth + .5f) / TextureWidth, (id / TextureWidth + .5f) / height);
                    for (int corner = 0; corner < 4; corner++)
                        lookup[v + corner] = texel;
                    indices[k] = v;
                    indices[k + 1] = v + 2;
                    indices[k + 2] = v + 1;
                    indices[k + 3] = v;
                    indices[k + 4] = v + 3;
                    indices[k + 5] = v + 2;
                }

                var mesh = new Mesh
                {
                    name = "RedBlue static crowd " + batch
                };
                mesh.vertices = vertices;
                mesh.uv = uv;
                mesh.uv2 = lookup;
                mesh.triangles = indices;
                mesh.bounds = new Bounds(new Vector3(900, 550, 0), new Vector3(1840, 1140, 20));
                mesh.UploadMeshData(true);
                meshes[batch] = mesh;
            }
        }

        /// <summary>创建点采样、无 mipmap 的线性浮点数据纹理，避免颜色空间变换污染状态。</summary>
        private static Texture2D StateTexture(int height) => new Texture2D(TextureWidth, height, TextureFormat.RGBAFloat, false, true)
        {
            name = "RedBlue soldier state",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        /// <summary>打包位置、朝向和带阵营符号的生存状态；oldPosition 只切换位置来源。</summary>
        private void Pack(CrowdSimulation s, bool oldPosition)
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.PackState");
            // alpha 的正负区分阵营：绝对值 [1,2] 为存活加闪烁，[0,.55] 为死亡淡出，0 为不可见。
            fading = false;
            for (int i = 0; i < s.Count; i++)
            {
                float status = s.Hp[i] > 0 ? 1 + s.Hit[i] : s.Death[i] * .55f;
                if (s.Hp[i] <= 0 && s.Death[i] > 0)
                    fading = true;
                pixels[i] = new Color(
                    oldPosition ? s.PreviousX[i] : s.X[i], oldPosition ? s.PreviousY[i] : s.Y[i],
                    s.Angle[i], s.Team[i] == 0 ? status : -status);
            }
        }

        /// <summary>按相机边界统计可见人数用于面板；静态网格仍整体提交，不做逐单位 CPU 剔除。</summary>
        private void CountVisible(CrowdSimulation s, Camera camera)
        {
            using var profile = RedBlueProfiler.Measure("RedBlue.CountVisible");
            lastCamera = camera.transform.position;
            lastHalfHeight = camera.orthographicSize;
            lastAspect = camera.aspect;
            float halfWidth = lastHalfHeight * lastAspect;
            Drawn = 0;
            for (int i = 0; i < s.Count; i++)
                if ((s.Hp[i] > 0 || s.Death[i] > 0)
                    && Mathf.Abs(s.X[i] - lastCamera.x) <= halfWidth + 10
                    && Mathf.Abs(s.Y[i] - lastCamera.y) <= lastHalfHeight + 10)
                    Drawn++;
        }

        /// <summary>释放人口相关的网格和双纹理，清除上传状态与模拟引用。</summary>
        private void ReleaseBuffers()
        {
            if (meshes != null)
                for (int i = 0; i < meshes.Length; i++)
                    UnityEngine.Object.Destroy(meshes[i]);
            UnityEngine.Object.Destroy(previous);
            UnityEngine.Object.Destroy(current);
            meshes = null;
            previous = current = null;
            pixels = null;
            allocatedCount = 0;
            source = null;
            uploadedTick = -1;
        }

        /// <summary>释放所有动态创建的 Unity 渲染资源，包括独立材质。</summary>
        public void Dispose()
        {
            ReleaseBuffers();
            UnityEngine.Object.Destroy(material);
        }
    }
}
