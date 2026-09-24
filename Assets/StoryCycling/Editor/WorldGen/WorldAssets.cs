using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Semantisches Asset-Matching: welche Prefabs (aus PolygonCity, PolygonGeneric und
    // POLYGON Nature Biomes) für welche Rolle in der Kapstadt-Welt taugen. Die Placer fragen
    // nur Rollen ab ("Palme", "Küstenbaum", "Findling") — neue Packs werden hier eingehängt.
    // Bewusst NICHT verwendet: Bodenplatten, Ranken, Lianen, Ruinen, Vulkan, Wasserfall,
    // tote Bäume, Kokos-Palmen, Bananen (passen nicht ans Kap).
    public sealed class WorldAssets
    {
        public List<GameObject> BroadTrees, Pines, Palms, CoastalTrees, ForestTrees;
        public List<GameObject> Bushes, LushBushes, PalmBushes, Ferns, Grass, Flowers;
        public List<GameObject> Rocks, Boulders, RockPiles, FlatRocks, Driftwood, Seaweed;
        public List<GameObject> Birds, Clouds;

        public static WorldAssets From(AssetCatalog c)
        {
            var a = new WorldAssets
            {
                BroadTrees   = WorldPlacement.Pool(c, @"^SM_(Gen_)?Env_Tree_\d+$", "Dead"),
                Pines        = WorldPlacement.Pool(c, @"Tree_Pine_\d+$"),
                Palms        = WorldPlacement.Pool(c, @"^SM_Env_Tree_Palm_\d+$"),
                CoastalTrees = WorldPlacement.Pool(c, @"Tree_Pohutukawa_\d+$"),
                ForestTrees  = WorldPlacement.Pool(c, @"^SM_Env_Tree_Forest_\d+$"),
                Bushes       = WorldPlacement.Pool(c, @"^SM_(Gen_)?Env_(Bush|Bush_Large|Shrub)_\d+$"),
                LushBushes   = WorldPlacement.Pool(c, @"Env_Bush_Tropical_\d+$"),
                PalmBushes   = WorldPlacement.Pool(c, @"Env_Bush_Palm_\d+$"),
                Ferns        = WorldPlacement.Pool(c, @"Env_Fern_\d+$"),
                Grass        = WorldPlacement.Pool(c, @"^SM_Env_Grass_(Med|Tall)_Clump_\d+$|^SM_Gen_Env_Grass_(Tall_)?\d+$"),
                Flowers      = WorldPlacement.Pool(c, @"Env_Flowers?_\d+$"),
                Rocks        = WorldPlacement.Pool(c, @"^SM_(Gen_)?Env_Rock_\d+$"),
                Boulders     = WorldPlacement.Pool(c, @"Env_Rock_Round_\d+$"),
                RockPiles    = WorldPlacement.Pool(c, @"Env_Rock_Pile_\d+$"),
                FlatRocks    = WorldPlacement.Pool(c, @"Env_Rock_Flat_\d+$"),
                Driftwood    = WorldPlacement.Pool(c, @"DriftWood_\d+$"),
                Seaweed      = WorldPlacement.Pool(c, @"Seaweed_Beach_\d+$"),
                Birds        = WorldPlacement.Pool(c, @"^FX_Birds_01$"),
                Clouds       = WorldPlacement.Pool(c, @"^SM_(Gen_)?Env_Cloud_\d+$"),
            };
            // Fallbacks, damit ohne Nature-Pack nichts leer bleibt.
            if (a.Boulders.Count == 0) a.Boulders = a.Rocks;
            if (a.CoastalTrees.Count == 0) a.CoastalTrees = a.BroadTrees;
            if (a.ForestTrees.Count == 0) a.ForestTrees = a.Pines;
            if (a.LushBushes.Count == 0) a.LushBushes = a.Bushes;
            return a;
        }

        public void Log()
        {
            var sb = new StringBuilder("Asset-Matching: ");
            System.Action<string, List<GameObject>> Add = (n, l) => sb.Append(n).Append('=').Append(l.Count).Append(' ');
            Add("Laubbaum", BroadTrees); Add("Kiefer", Pines); Add("Palme", Palms); Add("Küstenbaum", CoastalTrees);
            Add("Waldbaum", ForestTrees); Add("Busch", Bushes); Add("Tropenbusch", LushBushes); Add("Palmbusch", PalmBushes);
            Add("Farn", Ferns); Add("Gras", Grass); Add("Blumen", Flowers); Add("Fels", Rocks); Add("Findling", Boulders);
            Add("Felshaufen", RockPiles); Add("Felsplatte", FlatRocks); Add("Treibholz", Driftwood); Add("Seetang", Seaweed);
            Add("Vögel", Birds);
            Debug.Log(sb.ToString());
            if (Palms.Count == 0) Debug.LogWarning("Keine Palmen im Katalog — nach dem Import des Nature-Packs 'Build Catalog from Synty' ausführen.");
        }

        // Gewichtete Auswahl aus mehreren Rollen; leere Rollen werden übersprungen.
        public static GameObject Pick(System.Random rng, params (List<GameObject> pool, float weight)[] options)
        {
            float total = 0f;
            foreach (var o in options) if (o.pool != null && o.pool.Count > 0) total += o.weight;
            if (total <= 0f) return null;
            float r = (float)rng.NextDouble() * total;
            foreach (var o in options)
            {
                if (o.pool == null || o.pool.Count == 0) continue;
                r -= o.weight;
                if (r <= 0f) return o.pool[rng.Next(o.pool.Count)];
            }
            foreach (var o in options) if (o.pool != null && o.pool.Count > 0) return o.pool[rng.Next(o.pool.Count)];
            return null;
        }
    }
}
