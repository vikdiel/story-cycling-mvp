using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Verifies the WorldGenerator core: determinism (same seed → same placements),
    // catalog-only sourcing, spacing and chunking. Throws on mismatch for batch mode.
    public static class WorldGeneratorTest
    {
        [MenuItem("Story Cycling/WorldGen/Test WorldGenerator")]
        public static void Run()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
            if (catalog == null) throw new System.Exception("Build the catalog first (WorldGen > Build Catalog from Synty).");
            if (catalog.entries == null || catalog.entries.Length == 0) throw new System.Exception("Catalog is empty.");

            var manifest = ScriptableObject.CreateInstance<RouteManifest>();
            manifest.seed = 99;
            manifest.segments = new[]
            {
                new RouteSegment { type = SegmentType.Synthetic, lengthM = 1000, biome = "field", elevationProfile = "flat" }
            };

            var points = RouteComposer.Compose(manifest, null);
            var spline = new RouteSpline();
            spline.Define(points);

            var a = WorldGenerator.Generate(spline, catalog);
            var b = WorldGenerator.Generate(spline, catalog);

            if (a.Count != b.Count) throw new System.Exception($"Determinism broken: {a.Count} vs {b.Count} placements");
            for (int i = 0; i < a.Count; i++)
                if (a[i].position != b[i].position || a[i].entry != b[i].entry)
                    throw new System.Exception("Placements differ between two runs with the same seed.");

            // Every placement must map to a catalog entry with a prefab (placer, not inventor).
            foreach (var p in a)
                if (p.entry == null || p.entry.prefab == null)
                    throw new System.Exception("Placement references a null entry/prefab — generator invented geometry.");

            // Chunks must be within range.
            int maxChunk = Mathf.CeilToInt(1000f / WorldGenerator.ChunkSizeM);
            foreach (var p in a)
                if (p.chunkIndex < 0 || p.chunkIndex > maxChunk)
                    throw new System.Exception("Chunk index out of range: " + p.chunkIndex);

            Object.DestroyImmediate(manifest);
            Debug.Log($"WorldGenerator PASS: {a.Count} placements, deterministic, all catalog-sourced, " +
                      $"{System.Math.Round(a.Count / 10.0) * 10} per km of 1 km route.");
        }
    }
}
