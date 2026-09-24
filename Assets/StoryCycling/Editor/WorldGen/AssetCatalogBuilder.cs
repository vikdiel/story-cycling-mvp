using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Auto-scans the installed Synty packs and turns each prefab into an AssetEntry
    // with a best-effort category + default placement metadata. The user refines
    // biome tags/weights after generation; the generator may only use these entries.
    public static class AssetCatalogBuilder
    {
        public const string OutputRoot = "Assets/StoryCycling/WorldGenCatalog";
        public const string CatalogPath = OutputRoot + "/SyntyCatalog.asset";

        [MenuItem("Story Cycling/WorldGen/Build Catalog from Synty")]
        public static void BuildCatalog()
        {
            Directory.CreateDirectory(OutputRoot);
            // Rebuild deterministically: stale entry assets otherwise accumulate as "Name 1".
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string old in Directory.GetFiles(OutputRoot, "*.asset"))
                    AssetDatabase.DeleteAsset(old.Replace('\\', '/'));
            }
            finally { AssetDatabase.StopAssetEditing(); }
            var entries = new List<AssetEntry>();

            Scan(PolygonCityRoot, entries, "");
            Scan(PolygonGenericRoot, entries, "");
            Scan(NatureBiomesRoot, entries, "PNB_");

            // Persist one AssetEntry asset per prefab, deterministically ordered by path.
            entries.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            AssetDatabase.DeleteAsset(CatalogPath);
            var catalog = ScriptableObject.CreateInstance<AssetCatalog>();
            catalog.entries = new AssetEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++)
                catalog.entries[i] = SaveEntry(entries[i]);

            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"WorldGen catalog built: {entries.Count} entries at {CatalogPath}. " +
                      "Refine biomeTags/weights per entry; missing-asset categories are logged at generation time.");
        }

        private const string PolygonCityRoot = "Assets/Synty/PolygonCity/Prefabs";
        private const string PolygonGenericRoot = "Assets/Synty/PolygonGeneric/Prefabs";
        private const string NatureBiomesRoot = "Assets/Synty/PolygonNatureBiomes";

        private static void Scan(string root, List<AssetEntry> entries, string prefix)
        {
            if (!Directory.Exists(root)) return;
            foreach (string path in Directory.GetFiles(root, "*.prefab", SearchOption.AllDirectories))
            {
                string assetPath = path.Replace('\\', '/');
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null) continue;
                string lowerPath = assetPath.ToLowerInvariant();
                if (lowerPath.Contains("/scenes/") || lowerPath.Contains("/terrain/")) continue;

                var entry = ScriptableObject.CreateInstance<AssetEntry>();
                entry.name = prefix + Path.GetFileNameWithoutExtension(assetPath);
                entry.prefab = prefab;
                Classify(entry, assetPath);
                entries.Add(entry);
            }
        }

        private static void Classify(AssetEntry entry, string path)
        {
            // Best-effort categorisation from the Synty folder/name conventions.
            string lower = path.ToLowerInvariant();
            if (lower.Contains("/polygonnaturebiomes/"))
            {
                string file = Path.GetFileNameWithoutExtension(path);
                entry.category = file.StartsWith("SM_Env_") && IsVegetation(path)
                    ? AssetCategory.Vegetation
                    : file.StartsWith("SM_Env_") ? AssetCategory.GroundCover : AssetCategory.Prop;
                return;
            }
            if (lower.Contains("/buildings/"))
                entry.category = AssetCategory.Building;
            else if (lower.Contains("/environments/"))
                entry.category = IsVegetation(path) ? AssetCategory.Vegetation : AssetCategory.GroundCover;
            else if (lower.Contains("/vehicles/"))
                entry.category = AssetCategory.Prop;
            else if (lower.Contains("/characters/"))
                entry.category = AssetCategory.Prop;
            else if (IsRoadFurniture(path))
                entry.category = AssetCategory.RoadFurniture;
            else
                entry.category = AssetCategory.Prop;
        }

        private static bool IsVegetation(string path)
        {
            string lower = path.ToLowerInvariant();
            return lower.Contains("tree") || lower.Contains("bush") || lower.Contains("fern") ||
                   lower.Contains("flower") || lower.Contains("grass") || lower.Contains("shrub") ||
                   lower.Contains("plant") || lower.Contains("rock") || lower.Contains("palm") ||
                   lower.Contains("seaweed") || lower.Contains("driftwood");
        }

        private static bool IsRoadFurniture(string path)
        {
            string lower = path.ToLowerInvariant();
            return lower.Contains("lightpole") || lower.Contains("trafficlight") ||
                   lower.Contains("/sign_") || lower.Contains("hydrant") || lower.Contains("parkingmeter");
        }

        private static AssetEntry SaveEntry(AssetEntry entry)
        {
            string assetPath = OutputRoot + "/" + entry.name + ".asset";
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CreateAsset(entry, assetPath);
            return entry;
        }
    }
}
