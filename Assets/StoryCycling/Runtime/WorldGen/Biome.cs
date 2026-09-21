using System;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Objects-per-100 m rule for one category within a biome.
    [Serializable]
    public class DensityRule
    {
        public AssetCategory category;
        [Tooltip("Objekte pro 100 m Strecke")]
        public float objectsPer100m = 20f;
    }

    // A biome groups ground cover + density rules and tags matching AssetEntry.biomeTags.
    [CreateAssetMenu(fileName = "Biome", menuName = "Story Cycling/WorldGen/Biome")]
    public class Biome : ScriptableObject
    {
        public string biomeTag = "generic";     // matched against AssetEntry.biomeTags
        public DensityRule[] densities;
    }
}
