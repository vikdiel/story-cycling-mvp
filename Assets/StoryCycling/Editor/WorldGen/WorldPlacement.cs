using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Belegte Kreisflächen (Landmarks, Gebäude), damit sich nichts ineinander schiebt.
    public sealed class Occupancy
    {
        private struct Circle { public float x, z, r; }
        private const float Cell = 20f;
        private readonly Dictionary<long, List<Circle>> cells = new Dictionary<long, List<Circle>>();
        private float maxR = 1f;

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        public void Add(float x, float z, float r)
        {
            maxR = Mathf.Max(maxR, r);
            long k = Key(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));
            if (!cells.TryGetValue(k, out List<Circle> l)) { l = new List<Circle>(); cells[k] = l; }
            l.Add(new Circle { x = x, z = z, r = r });
        }

        public bool IsFree(float x, float z, float r)
        {
            int span = Mathf.CeilToInt((r + maxR) / Cell);
            int cx = Mathf.FloorToInt(x / Cell), cz = Mathf.FloorToInt(z / Cell);
            for (int dx = -span; dx <= span; dx++)
            for (int dz = -span; dz <= span; dz++)
                if (cells.TryGetValue(Key(cx + dx, cz + dz), out List<Circle> l))
                    foreach (var c in l)
                    {
                        float rr = c.r + r;
                        if ((c.x - x) * (c.x - x) + (c.z - z) * (c.z - z) < rr * rr) return false;
                    }
            return true;
        }
    }

    public static class WorldPlacement
    {
        // Prefabs aus dem Katalog per Namensmuster (Regex, case-insensitive).
        public static List<GameObject> Pool(AssetCatalog catalog, string include, string exclude = null)
        {
            var inc = new Regex(include, RegexOptions.IgnoreCase);
            var exc = exclude != null ? new Regex(exclude, RegexOptions.IgnoreCase) : null;
            var list = new List<GameObject>();
            if (catalog == null || catalog.entries == null) return list;
            foreach (var e in catalog.entries)
                if (e != null && e.prefab != null && inc.IsMatch(e.prefab.name) && (exc == null || !exc.IsMatch(e.prefab.name)))
                    list.Add(e.prefab);
            return list;
        }

        public static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        // Instanziert, dreht, skaliert und setzt die Unterkante auf groundY - sink.
        public static GameObject Spawn(GameObject prefab, Transform parent, Vector3 pos, Quaternion rot, float scale,
                                       float groundY, float sink)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = prefab.name;
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = Vector3.one * scale;
            Bounds b = BoundsOf(go);
            go.transform.position += Vector3.up * (groundY - sink - b.min.y);
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            return go;
        }

        // Kleine Objekte in der Ferne gar nicht rendern (spart auf dem iPad viel).
        public static void CullWhenSmall(GameObject go, float screenHeight, bool castShadows)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            if (!castShadows) foreach (var r in renderers) r.shadowCastingMode = ShadowCastingMode.Off;
            var lod = go.GetComponent<LODGroup>();
            if (lod != null)
            {
                // Prefab hat schon LODs: nur die Culling-Schwelle der letzten Stufe anheben.
                LOD[] lods = lod.GetLODs();
                if (lods.Length > 0)
                {
                    int last = lods.Length - 1;
                    float prev = last > 0 ? lods[last - 1].screenRelativeTransitionHeight : 1f;
                    lods[last].screenRelativeTransitionHeight = Mathf.Min(prev * .9f, Mathf.Max(lods[last].screenRelativeTransitionHeight, screenHeight));
                    lod.SetLODs(lods);
                }
                return;
            }
            lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(screenHeight, renderers) });
            lod.RecalculateBounds();
        }

        public static GameObject Pick(List<GameObject> pool, System.Random rng) => pool[rng.Next(pool.Count)];
        public static float Range(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);
    }
}
