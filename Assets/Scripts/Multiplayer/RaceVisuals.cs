using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Shared materials and meshes for the race's code-built visuals (course, lasers, explosions).
    /// Built at runtime so the race needs no extra art wiring in the scene.
    /// </summary>
    public static class RaceVisuals
    {
        private static readonly Dictionary<Color, Material> s_unlit = new Dictionary<Color, Material>();
        private static readonly Dictionary<Color, Material> s_lit = new Dictionary<Color, Material>();
        private static readonly Dictionary<int, Mesh> s_torus = new Dictionary<int, Mesh>();

        /// <summary>Flat, glowing-looking color. Uses shaders that are always included in builds.</summary>
        public static Material Unlit(Color color)
        {
            if (s_unlit.TryGetValue(color, out var m) && m != null) return m;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Legacy Shaders/Diffuse");
            m = new Material(shader) { color = color, name = "RaceUnlit" };
            s_unlit[color] = m;
            return m;
        }

        /// <summary>Simple lit color (for rocks/debris so they read as 3D).</summary>
        public static Material Lit(Color color)
        {
            if (s_lit.TryGetValue(color, out var m) && m != null) return m;
            var shader = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            m = new Material(shader) { color = color, name = "RaceLit" };
            s_lit[color] = m;
            return m;
        }

        /// <summary>A ring mesh lying in the XY plane (so it faces +Z).</summary>
        public static Mesh Torus(float majorRadius, float minorRadius, int segments = 40, int sides = 8)
        {
            var key = (Mathf.RoundToInt(majorRadius * 100) * 397) ^ Mathf.RoundToInt(minorRadius * 1000);
            if (s_torus.TryGetValue(key, out var cached) && cached != null) return cached;

            var verts = new Vector3[(segments + 1) * (sides + 1)];
            var normals = new Vector3[verts.Length];
            var tris = new int[segments * sides * 6];

            int v = 0;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                var ringDir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                for (int j = 0; j <= sides; j++)
                {
                    float b = j / (float)sides * Mathf.PI * 2f;
                    var n = ringDir * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                    verts[v] = ringDir * majorRadius + n * minorRadius;
                    normals[v] = n;
                    v++;
                }
            }

            int t = 0;
            for (int i = 0; i < segments; i++)
            for (int j = 0; j < sides; j++)
            {
                int a0 = i * (sides + 1) + j;
                int a1 = a0 + sides + 1;
                tris[t++] = a0; tris[t++] = a1; tris[t++] = a0 + 1;
                tris[t++] = a0 + 1; tris[t++] = a1; tris[t++] = a1 + 1;
            }

            var mesh = new Mesh { name = "RaceTorus", vertices = verts, normals = normals, triangles = tris };
            mesh.RecalculateBounds();
            s_torus[key] = mesh;
            return mesh;
        }

        /// <summary>Creates a renderer-only object (no collider) with the given mesh and material.</summary>
        public static GameObject MeshObject(string name, Mesh mesh, Material material, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        /// <summary>Creates a Unity primitive with its collider removed (visual only).</summary>
        public static GameObject Primitive(PrimitiveType type, Material material, Transform parent)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col) Object.Destroy(col);
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
