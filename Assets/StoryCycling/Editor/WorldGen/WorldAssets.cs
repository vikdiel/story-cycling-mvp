using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Semantic roles keep placers independent of individual Synty pack folder layouts.
    public sealed class WorldAssets
    {
        public List<GameObject> BroadTrees, Pines, Palms, CoastalTrees, ForestTrees;
        public List<GameObject> Bushes, LushBushes, PalmBushes, Ferns, Grass, Flowers;
        public List<GameObject> Rocks, Boulders, RockPiles, FlatRocks, Driftwood, Seaweed;
        public List<GameObject> Birds, Clouds;

        public static WorldAssets From(AssetCatalog catalog)
        {
            var a = new WorldAssets
            {
                BroadTrees = WorldPlacement.Pool(catalog, @"^SM_(Gen_)?Env_Tree_\d+$", "Dead"),
                Pines = WorldPlacement.Pool(catalog, @"Tree_Pine_\d+$"),
                Palms = WorldPlacement.Pool(catalog, @"^SM_Env_Tree_Palm_\d+$"),
                CoastalTrees = WorldPlacement.Pool(catalog, @"Tree_Pohutukawa_\d+$"),
                ForestTrees = WorldPlacement.Pool(catalog, @"^SM_Env_Tree_Forest_\d+$"),
                Bushes = WorldPlacement.Pool(catalog, @"^SM_(Gen_)?Env_(Bush|Bush_Large|Shrub)_\d+$", "Dead|Part"),
                LushBushes = WorldPlacement.Pool(catalog, @"Env_Bush_Tropical_\d+$"),
                PalmBushes = WorldPlacement.Pool(catalog, @"Env_Bush_Palm_\d+$"),
                Ferns = WorldPlacement.Pool(catalog, @"Env_Fern_\d+$"),
                Grass = WorldPlacement.Pool(catalog, @"^SM_(Gen_)?Env_Grass.*\d+$"),
                Flowers = WorldPlacement.Pool(catalog, @"Env_Flowers?_\d+$"),
                Rocks = WorldPlacement.Pool(catalog, @"^SM_(Gen_)?Env_Rock_\d+$"),
                Boulders = WorldPlacement.Pool(catalog, @"Env_Rock_Round_\d+$"),
                RockPiles = WorldPlacement.Pool(catalog, @"Env_Rock_Pile_\d+$"),
                FlatRocks = WorldPlacement.Pool(catalog, @"Env_Rock_Flat_\d+$"),
                Driftwood = WorldPlacement.Pool(catalog, @"DriftWood_\d+$"),
                Seaweed = WorldPlacement.Pool(catalog, @"Seaweed_Beach_\d+$"),
                Birds = WorldPlacement.Pool(catalog, @"^FX_Birds_01$"),
                Clouds = WorldPlacement.Pool(catalog, @"^SM_(Gen_)?Env_Cloud_\d+$")
            };
            if (a.Boulders.Count == 0) a.Boulders = a.Rocks;
            if (a.CoastalTrees.Count == 0) a.CoastalTrees = a.BroadTrees;
            if (a.ForestTrees.Count == 0) a.ForestTrees = a.Pines;
            if (a.LushBushes.Count == 0) a.LushBushes = a.Bushes;
            return a;
        }

        public void Log()
        {
            var sb = new StringBuilder("Asset-Matching: ");
            Add(sb, "Laub", BroadTrees); Add(sb, "Kiefer", Pines); Add(sb, "Palme", Palms);
            Add(sb, "Busch", Bushes); Add(sb, "Farn", Ferns); Add(sb, "Fels", Rocks); Add(sb, "Vögel", Birds);
            Debug.Log(sb.ToString());
        }

        private static void Add(StringBuilder sb, string label, List<GameObject> list) => sb.Append(label).Append('=').Append(list?.Count ?? 0).Append(' ');

        public static GameObject Pick(System.Random rng, params (List<GameObject> pool, float weight)[] choices)
        {
            float total = 0f;
            foreach (var c in choices) if (c.pool != null && c.pool.Count > 0) total += c.weight;
            if (total <= 0f) return null;
            float roll = (float)rng.NextDouble() * total;
            foreach (var c in choices)
            {
                if (c.pool == null || c.pool.Count == 0) continue;
                roll -= c.weight;
                if (roll <= 0f) return c.pool[rng.Next(c.pool.Count)];
            }
            foreach (var c in choices) if (c.pool != null && c.pool.Count > 0) return c.pool[0];
            return null;
        }
    }
}
