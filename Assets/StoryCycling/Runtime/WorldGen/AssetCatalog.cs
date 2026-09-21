using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Single source of truth for the world generator: every AssetEntry + Biome + global seed.
    // The generator draws its entire selection from this one object.
    [CreateAssetMenu(fileName = "AssetCatalog", menuName = "Story Cycling/WorldGen/Asset Catalog")]
    public class AssetCatalog : ScriptableObject
    {
        public int seed = 12345;
        public AssetEntry[] entries;
        public Biome[] biomes;

        // A tag is a wildcard (matches any biome) only when no tag is set. This keeps
        // auto-scanned entries usable until the user refines their biome tags.
        public static bool MatchesBiome(AssetEntry e, string biomeTag)
        {
            if (e == null || e.biomeTags == null || e.biomeTags.Length == 0) return true;
            for (int i = 0; i < e.biomeTags.Length; i++)
                if (e.biomeTags[i] == biomeTag) return true;
            return false;
        }
    }
}
