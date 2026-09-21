using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Places building prefabs from the catalog at the REAL OSM footprints: right position,
    // real orientation (footprint principal axis), plausible size (nearest prefab by area +
    // clamped scale), real density. "Harmonious by construction" instead of random scatter.
    // Uses the shared RouteHeightField (RouteHeightField.cs) for ground anchoring.
    public static class OsmBuildingPlacer
    {
        private struct Sized { public GameObject prefab; public float w, d, area; }

        public static int Place(RouteSpline spline, OsmContext ctx, AssetCatalog catalog)
        {
            var buildings = CollectBuildingPrefabs(catalog);
            if (buildings.Count == 0) { Debug.LogWarning("OSM: keine Building-Prefabs im Katalog."); return 0; }
            if (ctx.Buildings.Count == 0) { Debug.LogWarning("OSM: keine Gebäude im OSM-Dump (erst 'Fetch OSM')."); return 0; }

            var sizes = MeasurePrefabs(buildings);
            var ground = new RouteHeightField(spline, 5f);
            int placed = 0;

            foreach (var b in ctx.Buildings)
            {
                Sized pick = NearestBySize(sizes, b.area, b.width / Mathf.Max(1f, b.depth));
                var go = (GameObject)PrefabUtility.InstantiatePrefab(pick.prefab);
                go.name = pick.prefab.name;

                go.transform.rotation = Quaternion.LookRotation(b.axisDir, Vector3.up);

                float s = Mathf.Clamp(Mathf.Sqrt(b.area / Mathf.Max(1f, pick.area)), 0.6f, 1.6f);
                go.transform.localScale = Vector3.one * s;

                float y = ground.HeightAt(b.centroid.x, b.centroid.y);
                go.transform.position = new Vector3(b.centroid.x, y, b.centroid.y);

                Bounds bounds = BoundsOf(go);
                go.transform.position += Vector3.up * (y - bounds.min.y);

                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                placed++;
            }
            Debug.Log($"OSM-Gebäude platziert: {placed} an echten Footprints " +
                      $"(Prefabs {buildings.Count}, OSM-Gebäude {ctx.Buildings.Count}).");
            return placed;
        }

        private static List<GameObject> CollectBuildingPrefabs(AssetCatalog catalog)
        {
            var list = new List<GameObject>();
            foreach (var e in catalog.entries)
                if (e != null && e.prefab != null && e.category == AssetCategory.Building)
                    list.Add(e.prefab);
            return list;
        }

        private static List<Sized> MeasurePrefabs(List<GameObject> prefabs)
        {
            var result = new List<Sized>();
            foreach (var p in prefabs)
            {
                var tmp = (GameObject)PrefabUtility.InstantiatePrefab(p);
                Bounds b = BoundsOf(tmp);
                result.Add(new Sized { prefab = p, w = b.size.x, d = b.size.z, area = Mathf.Max(1f, b.size.x * b.size.z) });
                Object.DestroyImmediate(tmp);
            }
            return result;
        }

        private static Sized NearestBySize(List<Sized> sizes, float targetArea, float targetAspect)
        {
            Sized best = sizes[0]; float bestScore = float.MaxValue;
            foreach (var s in sizes)
            {
                float areaScore = Mathf.Abs(Mathf.Log(s.area / Mathf.Max(1f, targetArea)));
                float aspect = s.w / Mathf.Max(1f, s.d);
                float aspectScore = Mathf.Abs(Mathf.Log(aspect / Mathf.Max(0.1f, targetAspect))) * 0.5f;
                float score = areaScore + aspectScore;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            return best;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
