using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace ET.Client
{
    /// <summary>One 3D board, rendered into both views. Each cell keeps its world position through pan/zoom.</summary>
    public sealed class PingDouBoardView : IDisposable
    {
        private const int Layer = 30;
        private readonly PingDouAssets assets;
        private readonly GameObject root;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<Material> palette = new List<Material>();
        private readonly List<Drop> drops = new List<Drop>();
        private readonly Dictionary<char, Material> colorMaterials = new Dictionary<char, Material>();
        private MeshFilter[] beads;
        private Transform[] beadTransforms;
        private Transform hints, outline, paper, tool, iron, previewTemplate;
        private Mesh hintMesh, hintFillMesh;
        private Material hintFillMaterial, flashMaterial;
        private Color flashBase;
        private float flashAge;
        private float revealProgress;
        private int hintHash;
        private PingDouRound round;
        private Camera global, detail;
        private RawImage globalImage, detailImage;
        private Vector2 center;
        private float zoom = 2;
        private char selected;
        private readonly HashSet<int> reserved = new HashSet<int>();
        public Action<int> Settled;
        public float Zoom => zoom;
        public Camera DetailCamera => detail;
        public Camera GlobalCamera => global;

        private sealed class Drop
        {
            public int index;
            public float age;
            public Vector3 start, end;
        }

        public PingDouBoardView(PingDouAssets assets, RawImage globalImage, RawImage detailImage)
        {
            this.assets = assets;
            this.globalImage = globalImage;
            this.detailImage = detailImage;
            root = new GameObject("PingDou 3D stage")
            {
                layer = Layer
            };
            root.transform.position = new Vector3(1000, 1000, 0);
            global = CreateCamera("Global camera");
            detail = CreateCamera("Detail camera");
            detail.cullingMask |= 1 << 29;
        }

        private Camera CreateCamera(string name)
        {
            var obj = new GameObject(name)
            {
                layer = Layer
            };
            obj.transform.SetParent(root.transform, false);
            var cam = obj.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.nearClipPlane = .1f;
            cam.farClipPlane = 100;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.91f, .88f, .86f);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            return cam;
        }

        private Material Material(Color color, bool flat = false)
        {
            var mat = new Material(flat ? assets.flat : assets.plastic)
            {
                color = QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color
            };
            palette.Add(mat);
            return mat;
        }

        private Transform Object(string name, Mesh mesh, Material material, Vector3 position, Transform parent = null)
        {
            var obj = new GameObject(name)
            {
                layer = Layer
            };
            obj.transform.SetParent(parent != null ? parent : root.transform, false);
            obj.transform.localPosition = position;
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return obj.transform;
        }

        private Mesh Quads(List<Vector3> positions, List<Vector2> sizes)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            for (int n = 0; n < positions.Count; ++n)
            {
                Vector3 p = positions[n];
                Vector2 s = sizes[n] * .5f;
                int i = vertices.Count;
                vertices.Add(p + new Vector3(-s.x, -s.y));
                vertices.Add(p + new Vector3(-s.x, s.y));
                vertices.Add(p + new Vector3(s.x, s.y));
                vertices.Add(p + new Vector3(s.x, -s.y));
                indices.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            var mesh = new Mesh
            {
                name = "Board quads"
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        private Mesh Rectangle(float w, float h) => Quads(new List<Vector3> { Vector3.zero }, new List<Vector2> { new Vector2(w, h) });
        public Vector3 Cell(int index) => new Vector3(index % round.Pattern.size - (round.Pattern.size - 1) * .5f, (round.Pattern.size - 1) * .5f - index / round.Pattern.size, 0);
        public void Bind(PingDouRound value, char color)
        {
            var previousChildren = new List<GameObject>();
            foreach (Transform child in root.transform)
                if (child != global.transform && child != detail.transform)
                    previousChildren.Add(child.gameObject);
            foreach (var child in previousChildren)
            {
                child.SetActive(false);
                PingDouLifetime.Release(child);
            }

            foreach (var item in owned)
                PingDouLifetime.Release(item);
            owned.Clear();
            foreach (var mat in palette)
                PingDouLifetime.Release(mat);
            palette.Clear();
            colorMaterials.Clear();
            drops.Clear();
            reserved.Clear();
            round = value;
            revealProgress = round.Phase == PingDouPhase.Complete ? 1 : 0;
            selected = color;
            hintHash = int.MinValue;
            flashMaterial = null;
            int size = round.Pattern.size;
            beads = new MeshFilter[size * size];
            beadTransforms = new Transform[size * size];
            foreach (var c in round.Pattern.colors)
            {
                ColorUtility.TryParseHtmlString(c.hex, out var rgb);
                colorMaterials[c.key[0]] = Material(rgb);
            }

            var board = Material(new Color(.94f, .92f, .91f));
            var dark = Material(new Color(.26f, .20f, .19f), true);
            var peg = Material(new Color(.99f, .97f, .95f));
            var purple = Material(new Color(.61f, .30f, .88f), true);
            Object("Tray rim", Rectangle(size + 1.5f, size + 1.5f), Material(new Color(.76f, .70f, .70f)), new Vector3(0, 0, .14f));
            Object("Neutral board", Rectangle(size + .65f, size + .65f), board, new Vector3(0, 0, .04f));
            var gridPositions = new List<Vector3>();
            var gridSizes = new List<Vector2>();
            for (int i = 0; i <= size; ++i)
            {
                gridPositions.Add(new Vector3(i - size * .5f, 0, .015f));
                gridSizes.Add(new Vector2(.018f, size));
                gridPositions.Add(new Vector3(0, i - size * .5f, .015f));
                gridSizes.Add(new Vector2(size, .018f));
            }

            Object("Grid", Quads(gridPositions, gridSizes), Material(new Color(.76f, .72f, .71f), true), Vector3.zero);
            var combines = new List<CombineInstance>();
            for (int i = 0; i < beads.Length; ++i)
            {
                combines.Add(new CombineInstance { mesh = assets.peg, transform = Matrix4x4.TRS(Cell(i), Quaternion.identity, new Vector3(.28f, .28f, .20f)) });
                if (round.Key(i) == '.')
                    continue;
                var t = Object("Bead " + i, assets.beadHeatStages[0], colorMaterials[round.Key(i)], Cell(i));
                beadTransforms[i] = t;
                beads[i] = t.GetComponent<MeshFilter>();
                t.gameObject.SetActive(round.IsPlaced(i));
            }

            var combined = new Mesh
            {
                name = "Combined positioning pegs"
            };
            combined.indexFormat = IndexFormat.UInt32;
            combined.CombineMeshes(combines.ToArray());
            owned.Add(combined);
            Object("3D pegs", combined, peg, Vector3.zero);
            hintMesh = new Mesh
            {
                name = "Selected empty targets"
            };
            owned.Add(hintMesh);
            hints = Object("Purple targets", hintMesh, purple, new Vector3(0, 0, -.045f));
            hintFillMesh = new Mesh
            {
                name = "Soft selected color"
            };
            owned.Add(hintFillMesh);
            hintFillMaterial = new Material(assets.translucent);
            palette.Add(hintFillMaterial);
            Object("Selected target fill", hintFillMesh, hintFillMaterial, new Vector3(0, 0, -.03f));
            BuildOutline(dark);
            var paperMaterial = new Material(assets.translucent)
            {
                color = new Color(.97f, .93f, .80f, .17f)
            };
            palette.Add(paperMaterial);
            paper = Object("Heat paper", Rectangle(size + .4f, size + .4f), paperMaterial, new Vector3(0, 0, -.62f));
            paper.gameObject.SetActive(false);
            var edgeMaterial = Material(new Color(.85f, .76f, .64f), true);
            Object("Sheet top", Rectangle(size + .4f, .07f), edgeMaterial, new Vector3(0, size * .5f + .2f, 0), paper);
            Object("Sheet bottom", Rectangle(size + .4f, .07f), edgeMaterial, new Vector3(0, -size * .5f - .2f, 0), paper);
            Object("Sheet left", Rectangle(.07f, size + .4f), edgeMaterial, new Vector3(-size * .5f - .2f, 0, 0), paper);
            Object("Sheet right", Rectangle(.07f, size + .4f), edgeMaterial, new Vector3(size * .5f + .2f, 0, 0), paper);
            tool = Object("Placement pen", assets.pen, Material(new Color(.65f, .44f, .80f)), Vector3.zero);
            tool.localRotation = Quaternion.Euler(30, -50, 0);
            tool.gameObject.SetActive(false);
            var tip = Object("White pen tip", assets.pen, Material(new Color(.99f, .97f, .95f)), Vector3.zero, tool);
            tip.localScale = new Vector3(.85f, .85f, .22f);
            iron = Object("Iron", assets.iron, Material(new Color(.65f, .44f, .80f)), Vector3.zero);
            iron.gameObject.SetActive(false);
            var handle = Object("Iron handle", assets.beadHeatStages[0], Material(new Color(.98f, .96f, .94f)), new Vector3(0, -.15f, -.85f), iron);
            handle.localScale = new Vector3(.8f, 1.7f, .9f);
            tool.gameObject.layer = tip.gameObject.layer = iron.gameObject.layer = handle.gameObject.layer = 29;
            previewTemplate = new GameObject("Preview template")
            {
                layer = Layer
            }.transform;
            previewTemplate.SetParent(root.transform, false);
            foreach (var c in round.Pattern.colors)
            {
                ColorUtility.TryParseHtmlString(c.hex, out var rgb);
                rgb = Color.Lerp(rgb, Color.white, .48f);
                var positions = new List<Vector3>();
                var sizes = new List<Vector2>();
                for (int i = 0; i < beads.Length; ++i)
                    if (round.Key(i) == c.key[0])
                    {
                        positions.Add(Cell(i) + new Vector3(0, 0, .005f));
                        sizes.Add(Vector2.one * .92f);
                    }

                Object("Pattern " + c.name, Quads(positions, sizes), Material(rgb, true), Vector3.zero, previewTemplate);
            }

            ResetView();
            Sync();
        }

        private void BuildOutline(Material material)
        {
            var positions = new List<Vector3>();
            var sizes = new List<Vector2>();
            int size = round.Pattern.size;
            for (int i = 0; i < beads.Length; ++i)
            {
                char k = round.Key(i);
                if (k == '.')
                    continue;
                Vector3 c = Cell(i);
                if (i % size == 0 || round.Key(i - 1) != k)
                {
                    positions.Add(c + new Vector3(-.5f, 0, -.025f));
                    sizes.Add(new Vector2(.025f, 1));
                }

                if (i % size == size - 1 || round.Key(i + 1) != k)
                {
                    positions.Add(c + new Vector3(.5f, 0, -.025f));
                    sizes.Add(new Vector2(.025f, 1));
                }

                if (i < size || round.Key(i - size) != k)
                {
                    positions.Add(c + new Vector3(0, .5f, -.025f));
                    sizes.Add(new Vector2(1, .025f));
                }

                if (i >= size * (size - 1) || round.Key(i + size) != k)
                {
                    positions.Add(c + new Vector3(0, -.5f, -.025f));
                    sizes.Add(new Vector2(1, .025f));
                }
            }

            outline = Object("Thin color contours", Quads(positions, sizes), material, Vector3.zero);
        }

        public void Select(char color)
        {
            selected = color;
            RebuildHints();
        }

        private void RebuildHints()
        {
            if (round == null)
                return;
            int hash = (int)round.Phase * 31 + selected;
            for (int i = 0; i < beads.Length; ++i)
                if (IsTarget(i))
                    hash = unchecked(hash * 31 + i);
            if (hash == hintHash)
                return;
            hintHash = hash;
            var instances = new List<CombineInstance>();
            var fills = new List<CombineInstance>();
            for (int i = 0; i < beads.Length; ++i)
            {
                if (!IsTarget(i))
                    continue;
                instances.Add(new CombineInstance { mesh = assets.ring, transform = Matrix4x4.Translate(Cell(i)) });
                fills.Add(new CombineInstance { mesh = assets.targetFill, transform = Matrix4x4.Translate(Cell(i)) });
            }

            hintMesh.Clear();
            if (instances.Count > 0)
            {
                hintMesh.indexFormat = IndexFormat.UInt32;
                hintMesh.CombineMeshes(instances.ToArray());
            }

            hintFillMesh.Clear();
            if (fills.Count > 0)
            {
                hintFillMesh.indexFormat = IndexFormat.UInt32;
                hintFillMesh.CombineMeshes(fills.ToArray());
            }

            Color fill = round.Phase == PingDouPhase.Placing ? colorMaterials[selected].color : new Color(.62f, .3f, .8f);
            fill.a = .18f;
            hintFillMaterial.color = fill;
        }

        private bool IsTarget(int index) => round.Phase == PingDouPhase.Placing ? round.CanPlace(index, selected) && !reserved.Contains(index) : round.Phase == PingDouPhase.Ironing && round.IsPlaced(index) && round.Heat(index) < 1;
        public void FlashColor(char color)
        {
            if (flashMaterial != null)
                flashMaterial.color = flashBase;
            flashMaterial = colorMaterials[color];
            flashBase = flashMaterial.color;
            flashAge = .7f;
        }

        public void Sync()
        {
            for (int i = 0; i < beads.Length; ++i)
                if (beads[i] != null && round.IsPlaced(i))
                {
                    beads[i].gameObject.SetActive(true);
                    beads[i].sharedMesh = assets.beadHeatStages[Mathf.RoundToInt(round.Heat(i) * (assets.beadHeatStages.Length - 1))];
                }

            paper.gameObject.SetActive(round.Phase == PingDouPhase.Ironing || round.Phase == PingDouPhase.ReadyToReveal);
            iron.gameObject.SetActive(round.Phase == PingDouPhase.Ironing);
            if (round.Phase == PingDouPhase.Ironing)
                iron.localPosition = new Vector3(center.x, center.y, -.8f);
            outline.gameObject.SetActive(round.Phase < PingDouPhase.ReadyToReveal);
            RebuildHints();
        }

        public bool Queue(int index, char color)
        {
            if (!round.CanPlace(index, color) || !reserved.Add(index))
                return false;
            Vector3 end = Cell(index);
            drops.Add(new Drop { index = index, start = end + new Vector3(.72f, .55f, -2.7f), end = end });
            beadTransforms[index].gameObject.SetActive(true);
            beadTransforms[index].localPosition = drops[drops.Count - 1].start;
            beadTransforms[index].GetComponent<MeshRenderer>().enabled = true;
            tool.gameObject.SetActive(true);
            tool.localPosition = end + new Vector3(.72f, .55f, -2.7f);
            RebuildHints();
            return true;
        }

        public void Tick(float dt)
        {
            EnsureTexture(global, globalImage);
            EnsureTexture(detail, detailImage);
            PositionCameras();
            if (flashMaterial != null)
            {
                flashAge = Mathf.Max(0, flashAge - dt);
                flashMaterial.color = Color.Lerp(flashBase, Color.white, Mathf.Sin(flashAge / .7f * Mathf.PI) * .28f);
                if (flashAge == 0)
                {
                    flashMaterial.color = flashBase;
                    flashMaterial = null;
                }
            }

            for (int d = drops.Count - 1; d >= 0; --d)
            {
                var drop = drops[d];
                drop.age += dt;
                float t = Mathf.Clamp01(drop.age / .22f);
                var tr = beadTransforms[drop.index];
                tr.localPosition = Vector3.Lerp(drop.start, drop.end, t * t);
                if (t >= 1)
                {
                    float bounce = Mathf.Sin(Mathf.Clamp01((drop.age - .22f) / .10f) * Mathf.PI) * .13f;
                    tr.localPosition = drop.end + new Vector3(0, 0, -bounce);
                }

                if (drop.age < .32f)
                    continue;
                tr.localPosition = drop.end;
                drops.RemoveAt(d);
                reserved.Remove(drop.index);
                Settled?.Invoke(drop.index);
            }

            if (drops.Count == 0 && round.Phase != PingDouPhase.Ironing)
                tool.gameObject.SetActive(false);
            if (round.Phase == PingDouPhase.Complete)
            {
                revealProgress = Mathf.Clamp01(revealProgress + dt);
                float reveal = Mathf.SmoothStep(0, 1, revealProgress);
                float lift = 1.4f * reveal;
                Quaternion tilt = Quaternion.Euler(-8 * reveal, 0, 0);
                for (int i = 0; i < beadTransforms.Length; ++i)
                    if (beadTransforms[i] != null)
                    {
                        beadTransforms[i].localPosition = tilt * Cell(i) + new Vector3(0, 0, -lift);
                        beadTransforms[i].localRotation = tilt;
                    }
            }
        }

        private void EnsureTexture(Camera camera, RawImage image)
        {
            var rect = image.rectTransform.rect;
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width), 128, 1024), h = Mathf.Clamp(Mathf.RoundToInt(rect.height), 128, 1024);
            if (camera.targetTexture != null && camera.targetTexture.width == w && camera.targetTexture.height == h)
                return;
            if (camera.targetTexture != null)
            {
                camera.targetTexture.Release();
                PingDouLifetime.Release(camera.targetTexture);
            }

            var rt = new RenderTexture(w, h, 16)
            {
                name = "PingDou " + camera.name,
                antiAliasing = 1
            };
            rt.Create();
            camera.targetTexture = rt;
            image.texture = rt;
        }

        private void PositionCameras()
        {
            if (round == null)
                return;
            global.orthographicSize = (round.Pattern.size + 2) * .5f * Mathf.Max(1, 1 / global.aspect);
            detail.orthographicSize = (round.Pattern.size + 2) * .5f / zoom;
            global.transform.localPosition = new Vector3(0, 0, -40);
            global.transform.localRotation = Quaternion.identity;
            const float tilt = 20;
            detail.transform.localPosition = new Vector3(center.x, center.y + Mathf.Tan(tilt * Mathf.Deg2Rad) * 40, -40);
            detail.transform.localRotation = Quaternion.Euler(tilt, 0, 0);
        // Only the global pass sees the reference colors. Detail remains a neutral game board.
        }

        public void Render()
        {
            if (round == null)
                return;
            previewTemplate.gameObject.SetActive(true);
            global.Render();
            previewTemplate.gameObject.SetActive(false);
            detail.Render();
            global.enabled = false;
            detail.enabled = false;
        }

        public Vector2 WorldAt(Vector2 screen, Camera uiCamera)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(detailImage.rectTransform, screen, uiCamera, out var local);
            Rect r = detailImage.rectTransform.rect;
            var ray = detail.ViewportPointToRay(new Vector3((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height, 0));
            var plane = new Plane(Vector3.forward, root.transform.position);
            return plane.Raycast(ray, out float distance) ? (Vector2)(ray.GetPoint(distance) - root.transform.position) : center;
        }

        public int IndexAt(Vector2 world)
        {
            int size = round.Pattern.size;
            int x = Mathf.FloorToInt(world.x + size * .5f), y = Mathf.FloorToInt(size * .5f - world.y);
            return x < 0 || y < 0 || x >= size || y >= size ? -1 : y * size + x;
        }

        public void Pan(Vector2 delta)
        {
            center += delta;
            ClampCenter();
            PositionCameras();
        }

        public void ZoomAt(float factor, Vector2 screen, Camera uiCamera)
        {
            Vector2 before = WorldAt(screen, uiCamera);
            zoom = Mathf.Clamp(zoom * factor, 1, 5);
            PositionCameras();
            center += before - WorldAt(screen, uiCamera);
            ClampCenter();
            PositionCameras();
        }

        public void ResetView()
        {
            center = Vector2.zero;
            zoom = 2;
            PositionCameras();
        }

        private void ClampCenter()
        {
            float max = round.Pattern.size * .5f;
            center.x = Mathf.Clamp(center.x, -max, max);
            center.y = Mathf.Clamp(center.y, -max, max);
        }

        public Rect PreviewViewportBounds()
        {
            var plane = new Plane(Vector3.forward, root.transform.position);
            var lowRay = detail.ViewportPointToRay(Vector3.zero);
            var highRay = detail.ViewportPointToRay(Vector3.one);
            plane.Raycast(lowRay, out float lowDistance);
            plane.Raycast(highRay, out float highDistance);
            Vector3 low = global.WorldToViewportPoint(lowRay.GetPoint(lowDistance));
            Vector3 high = global.WorldToViewportPoint(highRay.GetPoint(highDistance));
            return Rect.MinMaxRect(Mathf.Clamp01(low.x), Mathf.Clamp01(low.y), Mathf.Clamp01(high.x), Mathf.Clamp01(high.y));
        }

        public void ShowIron(Vector2 world)
        {
            iron.gameObject.SetActive(true);
            iron.localPosition = new Vector3(world.x, world.y, -.8f);
        }

        public void CommitPending()
        {
            while (drops.Count > 0)
            {
                var d = drops[0];
                beadTransforms[d.index].localPosition = d.end;
                drops.RemoveAt(0);
                reserved.Remove(d.index);
                Settled?.Invoke(d.index);
            }
        }

        public void Dispose()
        {
            foreach (var cam in new[]
            {
                global,
                detail
            }

            )
                if (cam.targetTexture != null)
                {
                    cam.targetTexture.Release();
                    PingDouLifetime.Release(cam.targetTexture);
                }

            foreach (var item in owned)
                PingDouLifetime.Release(item);
            foreach (var mat in palette)
                PingDouLifetime.Release(mat);
            PingDouLifetime.Release(root);
        }
    }
}
