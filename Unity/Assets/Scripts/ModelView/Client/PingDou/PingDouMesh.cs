using System.Collections.Generic;
using UnityEngine;

namespace ET.Client
{
    /// <summary>Shared lathed meshes: real front lip, inner wall, side wall and underside.</summary>
    public static class PingDouMesh
    {
        public static Mesh Peg()
        {
            return Lathe("Solid peg", new[] { new Vector2(0, 0), new Vector2(.44f, 0), new Vector2(.5f, -.05f), new Vector2(.5f, -.45f), new Vector2(.43f, -.53f), new Vector2(0, -.53f) });
        }

        public static Mesh Pen()
        {
            return Lathe("Placement pen", new[] { new Vector2(0, 0), new Vector2(.10f, -.1f), new Vector2(.27f, -.8f), new Vector2(.34f, -1), new Vector2(.34f, -3.8f), new Vector2(.26f, -4), new Vector2(0, -4) });
        }

        private static Mesh Lathe(string name, Vector2[] profile)
        {
            const int segments = 24;
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            for (int p = 0; p < profile.Length; ++p)
                for (int s = 0; s <= segments; ++s)
                {
                    float a = s * Mathf.PI * 2 / segments;
                    vertices.Add(new Vector3(Mathf.Cos(a) * profile[p].x, Mathf.Sin(a) * profile[p].x, profile[p].y));
                    if (p == profile.Length - 1 || s == segments)
                        continue;
                    int i = p * (segments + 1) + s;
                    indices.AddRange(new[] { i, i + segments + 1, i + 1, i + 1, i + segments + 1, i + segments + 2 });
                }

            var mesh = new Mesh
            {
                name = name
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Iron()
        {
            Vector2[] outline =
            {
                new Vector2(0, 1.7f),
                new Vector2(-.85f, .65f),
                new Vector2(-1, -.9f),
                new Vector2(-.7f, -1.2f),
                new Vector2(.7f, -1.2f),
                new Vector2(1, -.9f),
                new Vector2(.85f, .65f)
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i < outline.Length; ++i)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                int n = vertices.Count;
                vertices.Add(Vector3.zero + Vector3.back * .55f);
                vertices.Add(new Vector3(a.x, a.y, -.55f));
                vertices.Add(new Vector3(b.x, b.y, -.55f));
                triangles.AddRange(new[] { n, n + 1, n + 2 });
                n = vertices.Count;
                vertices.Add(new Vector3(a.x, a.y, -.55f));
                vertices.Add(new Vector3(a.x, a.y, 0));
                vertices.Add(new Vector3(b.x, b.y, 0));
                vertices.Add(new Vector3(b.x, b.y, -.55f));
                triangles.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
            }

            var mesh = new Mesh
            {
                name = "Iron sole and body"
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Bead(float heat)
        {
            const int segments = 32;
            float h = Mathf.Lerp(.52f, .13f, heat);
            float inner = Mathf.Lerp(.205f, .008f, heat);
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            // Profile goes around the cross-section, including bevels. z points into the board.
            Vector2[] profile =
            {
                new Vector2(.90f, 0),
                new Vector2(1, .12f),
                new Vector2(1, .85f),
                new Vector2(.92f, 1),
                new Vector2(inner / .43f + .07f, 1),
                new Vector2(inner / .43f, .88f),
                new Vector2(inner / .43f, .12f),
                new Vector2(inner / .43f + .07f, 0)
            };
            for (int p = 0; p <= profile.Length; ++p)
                for (int s = 0; s <= segments; ++s)
                {
                    float a = s * Mathf.PI * 2 / segments;
                    float x = Mathf.Cos(a), y = Mathf.Sin(a);
                    Vector2 point = profile[p % profile.Length];
                    float circular = .43f;
                    float square = .505f / Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                    float outer = Mathf.Lerp(circular, square, heat);
                    float r = p % profile.Length < 4 ? point.x * outer : point.x * circular;
                    vertices.Add(new Vector3(x * r, y * r, -.04f - h * point.y));
                    if (p == profile.Length || s == segments)
                        continue;
                    int i = p * (segments + 1) + s;
                    indices.AddRange(new[] { i, i + segments + 1, i + 1, i + 1, i + segments + 1, i + segments + 2 });
                }

            var mesh = new Mesh
            {
                name = "Bead heat " + heat
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Ring(float outer, float inner)
        {
            const int segments = 32;
            var vertices = new Vector3[(segments + 1) * 2];
            var indices = new int[segments * 6];
            for (int i = 0; i <= segments; ++i)
            {
                float a = i * Mathf.PI * 2 / segments;
                vertices[i * 2] = new Vector3(Mathf.Cos(a) * outer, Mathf.Sin(a) * outer, 0);
                vertices[i * 2 + 1] = new Vector3(Mathf.Cos(a) * inner, Mathf.Sin(a) * inner, 0);
                if (i == segments)
                    continue;
                int k = i * 6, v = i * 2;
                indices[k] = v;
                indices[k + 1] = v + 1;
                indices[k + 2] = v + 2;
                indices[k + 3] = v + 2;
                indices[k + 4] = v + 1;
                indices[k + 5] = v + 3;
            }

            var mesh = new Mesh
            {
                name = "Ring"
            };
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
