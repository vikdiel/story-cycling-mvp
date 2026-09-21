using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Editor-side instantiator: composes the route, runs WorldGenerator, and instantiates
    // the catalog prefabs into a scene, grouped into 250 m chunk parents for streaming.
    // Strictly a placer: every GameObject maps to one catalog AssetEntry.
    public static class WorldGenSceneBuilder
    {
        [MenuItem("Story Cycling/WorldGen/Generate World (synthetic test)")]
        public static void GenerateSyntheticWorld()
        {
            if (EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play Mode first.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
            if (catalog == null) throw new System.InvalidOperationException("Build the catalog first (WorldGen > Build Catalog from Synty).");

            var manifest = ScriptableObject.CreateInstance<RouteManifest>();
            manifest.seed = 4242;
            manifest.segments = new[]
            {
                new RouteSegment { type = SegmentType.Synthetic, lengthM = 2000, biome = "urban", elevationProfile = "rolling" },
                new RouteSegment { type = SegmentType.Synthetic, lengthM = 2000, biome = "field", elevationProfile = "flat" }
            };

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("WorldGen Route").transform;
            var placements = Generate(manifest, catalog, root);

            string scenePath = "Assets/StoryCycling/Scenes/WorldGenTest.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"WorldGen scene saved: {scenePath} with {placements.Count} placements across " +
                      $"{Mathf.CeilToInt(4000f / WorldGenerator.ChunkSizeM)} chunks.");
        }

        // Returns the placement list; instantiates each prefab under its chunk parent.
        public static List<Placement> Generate(RouteManifest manifest, AssetCatalog catalog, Transform root)
        {
            var points = RouteComposer.Compose(manifest, null);
            var spline = new RouteSpline();
            spline.Define(points);

            var placements = WorldGenerator.Generate(spline, catalog);
            var chunkRoots = new Dictionary<int, Transform>();

            foreach (var p in placements)
            {
                if (p.entry == null || p.entry.prefab == null)
                {
                    Debug.LogWarning("WorldGen: entry without prefab, leaving spot empty.");
                    continue;
                }
                if (!chunkRoots.TryGetValue(p.chunkIndex, out var chunk))
                {
                    chunk = new GameObject($"Chunk {p.chunkIndex}").transform;
                    chunk.SetParent(root, false);
                    chunkRoots[p.chunkIndex] = chunk;
                }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(p.entry.prefab);
                go.name = p.entry.prefab.name;
                go.transform.SetParent(chunk, false);
                go.transform.position = p.position;
                go.transform.rotation = p.rotation;
                go.transform.localScale = p.scale;
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            }
            return placements;
        }
    }
}
