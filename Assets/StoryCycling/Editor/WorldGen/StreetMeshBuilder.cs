using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Lightweight road ribbons for OSM side streets and planted roundabout islands.
    public static class StreetMeshBuilder
    {
        public static void Build(StreetNetwork network, Transform parent, Material asphalt, Material shoulder, Material island, System.Func<Mesh, Mesh> save)
        {
            int created = 0;
            foreach (var street in network.Streets)
            {
                if (street.Samples.Count < 2) continue;
                var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var asphaltTriangles = new List<int>(); var shoulderTriangles = new List<int>();
                for (int i = 0; i < street.Samples.Count; i++)
                {
                    var s = street.Samples[i];
                    vertices.Add(s.pos - s.side * (s.half + 1.2f) + Vector3.down * .06f);
                    vertices.Add(s.pos - s.side * s.half + Vector3.up * .01f);
                    vertices.Add(s.pos + s.side * s.half + Vector3.up * .01f);
                    vertices.Add(s.pos + s.side * (s.half + 1.2f) + Vector3.down * .06f);
                    uvs.Add(new Vector2(-1f, s.distance / 6f)); uvs.Add(new Vector2(0f, s.distance / 6f));
                    uvs.Add(new Vector2(1f, s.distance / 6f)); uvs.Add(new Vector2(2f, s.distance / 6f));
                }
                for (int i = 0; i < street.Samples.Count - 1; i++)
                {
                    int a = i * 4, b = a + 4;
                    Quad(shoulderTriangles, a, a + 1, b, b + 1); Quad(asphaltTriangles, a + 1, a + 2, b + 1, b + 2); Quad(shoulderTriangles, a + 2, a + 3, b + 2, b + 3);
                }
                var mesh = new Mesh { name = $"Street_{created:D3}", indexFormat = IndexFormat.UInt32, subMeshCount = 2 };
                mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(asphaltTriangles, 0); mesh.SetTriangles(shoulderTriangles, 1); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer)); go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh; go.GetComponent<MeshRenderer>().sharedMaterials = new[] { asphalt, shoulder }; go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                created++;
            }
            int islands = 0;
            foreach (var ring in network.Islands)
            {
                float y = network.IslandBaseY(ring, network.Field); if (float.IsNaN(y)) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Object.DestroyImmediate(go.GetComponent<Collider>()); go.name = $"RoundaboutIsland_{islands:D2}";
                go.transform.SetParent(parent, false); go.transform.position = new Vector3(ring.Center.x, y + .08f, ring.Center.z); go.transform.localScale = new Vector3(ring.Radius * 2f, .16f, ring.Radius * 2f); go.GetComponent<Renderer>().sharedMaterial = island; islands++;
            }
            Debug.Log($"Querstraßen-Meshes: {created}, Kreisverkehr-Inseln: {islands}.");
        }
        private static void Quad(List<int> triangles, int a, int b, int c, int d) { triangles.Add(a); triangles.Add(c); triangles.Add(b); triangles.Add(b); triangles.Add(c); triangles.Add(d); }
    }
}
