using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Meshes für Querstraßen (gleiches Querprofil wie die Route, in 400-m-Zellen gebündelt)
    // und Kreisverkehr-Mittelinseln (Bordsteinring + Grünfläche).
    public static class StreetMeshBuilder
    {
        private const float BucketSize = 400f;

        public static void Build(StreetNetwork net, WorldTerrain terrain, RoadField main, Transform parent,
                                 RoadMaterials mats, System.Func<Mesh, Mesh> save)
        {
            var buckets = new Dictionary<long, RoadProfile.Parts>();
            foreach (var st in net.Streets)
            {
                var s = st.Samples;
                if (s.Count < 2) continue;
                float baseHalf = StreetNetwork.HalfWidthFor(st.Highway);
                var edges = new RoadProfile.Edge[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    bool flare = s[i].half > baseHalf + .05f;
                    edges[i] = flare ? RoadProfile.Edge.Mouth
                             : terrain.BiomeAt(s[i].pos.x, s[i].pos.z) == WorldTerrain.Biome.Urban ? RoadProfile.Edge.Urban
                             : RoadProfile.Edge.Rural;
                }
                RoadProfile.RemoveShortRuns(edges, RoadProfile.Edge.Urban, 8);

                Vector3 mid = s[s.Count / 2].pos;
                long key = ((long)Mathf.FloorToInt(mid.x / BucketSize) << 32) | (uint)Mathf.FloorToInt(mid.z / BucketSize);
                if (!buckets.TryGetValue(key, out RoadProfile.Parts parts)) { parts = new RoadProfile.Parts(); buckets[key] = parts; }
                bool center = st.CenterLine;
                RoadProfile.Emit(parts, s, 0, s.Count - 1, i => edges[i], i => edges[i], false,
                                 i => center && edges[i] != RoadProfile.Edge.Mouth);
            }

            int count = 0;
            foreach (var kv in buckets)
            {
                if (kv.Value.IsEmpty) continue;
                Mesh mesh = kv.Value.ToMesh();
                mesh.name = $"Streets_{count:D3}";
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterials = mats.Ribbon;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                count++;
            }

            int islands = 0;
            foreach (var isl in net.Islands)
            {
                float y = net.IslandBaseY(isl, main);
                if (float.IsNaN(y)) continue;
                Mesh m = IslandMesh(isl.Radius);
                m.name = $"RoundaboutIsland_{islands:D2}";
                m = save(m);
                var go = new GameObject(m.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(isl.Center.x, y, isl.Center.z);
                go.GetComponent<MeshFilter>().sharedMesh = m;
                go.GetComponent<MeshRenderer>().sharedMaterials = new[] { mats.IslandGrass, mats.Sidewalk };
                islands++;
            }
            Debug.Log($"Querstraßen-Meshes: {count} Zellen, {islands} Kreisverkehr-Inseln.");
        }

        // Scheibe (Grün, 0.18 m hoch) mit 0.5 m Bordsteinring und senkrechter Außenkante.
        private static Mesh IslandMesh(float radius)
        {
            const int seg = 40;
            const float top = .18f, curb = .5f;
            var v = new List<Vector3>(); var n = new List<Vector3>();
            var grass = new List<int>(); var stone = new List<int>();
            v.Add(new Vector3(0f, top, 0f)); n.Add(Vector3.up);
            for (int k = 0; k < seg; k++)
            {
                float a = k * Mathf.PI * 2f / seg;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                v.Add(dir * (radius - curb) + Vector3.up * top); n.Add(Vector3.up);   // 1 + 4k: innen Grünkante
                v.Add(dir * (radius - curb) + Vector3.up * top); n.Add(Vector3.up);   // 2 + 4k: Bordstein innen
                v.Add(dir * radius + Vector3.up * top); n.Add(Vector3.up);            // 3 + 4k: Bordstein außen oben
                v.Add(dir * radius + Vector3.down * .1f); n.Add(dir);                 // 4 + 4k: Außenkante unten
            }
            for (int k = 0; k < seg; k++)
            {
                int a = 1 + 4 * k, b = 1 + 4 * ((k + 1) % seg);
                // Draufsicht: Winkel steigt gegen den Uhrzeigersinn -> Reihenfolge für Oberseite (Unity: im Uhrzeigersinn)
                grass.Add(0); grass.Add(b); grass.Add(a);
                stone.Add(a + 1); stone.Add(b + 1); stone.Add(a + 2);
                stone.Add(b + 1); stone.Add(b + 2); stone.Add(a + 2);
                stone.Add(a + 2); stone.Add(b + 2); stone.Add(a + 3);
                stone.Add(b + 2); stone.Add(b + 3); stone.Add(a + 3);
            }
            var mesh = new Mesh { subMeshCount = 2 };
            mesh.SetVertices(v); mesh.SetNormals(n);
            mesh.SetTriangles(grass, 0); mesh.SetTriangles(stone, 1);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
