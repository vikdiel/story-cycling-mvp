using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // One placement decision (before instantiation) — pure, deterministic, testable.
    public struct Placement
    {
        public AssetEntry entry;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public float distanceM;   // arc-length along the route
        public int chunkIndex;    // 250 m chunk for streaming/culling
    }

    // Deterministic, catalog-only world generator. A *placer*, not an *inventor*:
    // it may only instantiate AssetEntry prefabs from the AssetCatalog. Missing assets
    // are logged and the spot is left empty — never substituted with invented geometry.
    public static class WorldGenerator
    {
        public const float ChunkSizeM = 250f;
        public const float SampleStepM = 2f;

        // Fills left+right bands along the spline. Biome filtering is coarse in v1
        // (empty biomeTags act as a wildcard); per-segment biome refinement is a
        // later milestone. Same seed → same placements.
        public static List<Placement> Generate(RouteSpline spline, AssetCatalog catalog)
        {
            var placements = new List<Placement>();
            var rng = new System.Random(catalog.seed);
            var byCategory = GroupByCategory(catalog);
            // Last-used distance per category, to enforce minSpacing along the route.
            var lastD = new Dictionary<AssetCategory, float>();

            float length = spline.Length;
            for (float d = 0f; d <= length; d += SampleStepM)
            {
                Vector3 pos = spline.SamplePosition(d);
                Vector3 tangent = spline.SampleTangent(d);
                float slopeDeg = SlopeDegrees(tangent);
                Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
                int chunk = Mathf.FloorToInt(d / ChunkSizeM);

                foreach (var kv in byCategory)
                {
                    AssetCategory cat = kv.Key;
                    if (lastD.TryGetValue(cat, out float prev) && d - prev < CategorySpacing(kv.Value)) continue;

                    var entry = PickWeighted(kv.Value, rng, slopeDeg);
                    if (entry == null) continue;

                    int side = rng.Next(2) == 0 ? -1 : 1;
                    float offset = Mathf.Lerp(entry.offsetFromRoad.x, entry.offsetFromRoad.y, (float)rng.NextDouble());
                    Vector3 p = pos + right * (side * offset);

                    float s = Mathf.Lerp(entry.scaleRange.x, entry.scaleRange.y, (float)rng.NextDouble());
                    Quaternion rot = Quaternion.LookRotation(tangent, Vector3.up) *
                        Quaternion.Euler(0f, entry.randomYRotation ? (float)rng.NextDouble() * 360f : 0f, 0f);

                    placements.Add(new Placement
                    {
                        entry = entry,
                        position = p,
                        rotation = rot,
                        scale = Vector3.one * s,
                        distanceM = d,
                        chunkIndex = chunk
                    });
                    lastD[cat] = d;
                }
            }
            return placements;
        }

        private static float SlopeDegrees(Vector3 tangent)
        {
            float horiz = new Vector2(tangent.x, tangent.z).magnitude;
            if (horiz < 1e-5f) return 0f;
            return Mathf.Atan(tangent.y / horiz) * Mathf.Rad2Deg;
        }

        private static float CategorySpacing(List<AssetEntry> entries)
        {
            float min = float.MaxValue;
            foreach (var e in entries) if (e != null && e.minSpacing > 0f) min = Mathf.Min(min, e.minSpacing);
            return min == float.MaxValue ? 3f : min;
        }

        private static AssetEntry PickWeighted(List<AssetEntry> entries, System.Random rng, float slopeDeg)
        {
            float total = 0f;
            foreach (var e in entries)
            {
                if (e == null || e.prefab == null) continue;
                if (slopeDeg < e.slopeRangeDeg.x || slopeDeg > e.slopeRangeDeg.y) continue;
                total += Mathf.Max(0.0001f, e.weight);
            }
            if (total <= 0f) return null;
            float roll = (float)rng.NextDouble() * total;
            foreach (var e in entries)
            {
                if (e == null || e.prefab == null) continue;
                if (slopeDeg < e.slopeRangeDeg.x || slopeDeg > e.slopeRangeDeg.y) continue;
                roll -= Mathf.Max(0.0001f, e.weight);
                if (roll <= 0f) return e;
            }
            return null;
        }

        private static Dictionary<AssetCategory, List<AssetEntry>> GroupByCategory(AssetCatalog catalog)
        {
            var map = new Dictionary<AssetCategory, List<AssetEntry>>();
            foreach (var e in catalog.entries)
            {
                if (e == null) continue;
                if (!map.TryGetValue(e.category, out var list)) { list = new List<AssetEntry>(); map[e.category] = list; }
                list.Add(e);
            }
            return map;
        }
    }
}
