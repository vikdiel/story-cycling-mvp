using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    public enum VegGroup : byte { Tree, Shrub, Rock, Ground }
    public enum VegTier : byte { Near, Mid, Far }

    // Arten der Kap-Vegetation. "Art" = ökologische Rolle (Fynbos-Hügel, Protea-Dickicht, Restio, Sandsteinfels ...), nicht ein Prefab:
    // der Builder löst jede Art auf Prefabs aus den vorhandenen Synty-Paketen auf und erzeugt getönte Materialkopien.
    public enum Sp : byte
    {
        Pine, Gum, Milkwood, ForestTree, GardenTree, Palm, ProteaTree,
        FynLow, FynThicket, Restio, Erica, DuneScrub, Fern, LushBush,
        GrassGreen, GrassDry, Flowers, Pebbles, Reed, Driftwood, Seaweed,
        SandBoulder, Cliff, DirtCliff, GraniteBoulder, Scree,
        FarConifer, FarBroad,
        Count
    }

    public sealed class VegSpecies
    {
        public Sp Id;
        public string Name;
        public VegGroup Group;
        public float Height, Radius;     // Maße des Prefabs bei Maßstab 1 (m): Höhe und Krone/Grundfläche (Radius)
        public float Foot;               // Platzbedarf (Radius, m) bei Maßstab 1 für den Überlappungstest
        public float Sink;               // Anteil der Höhe, der im Boden steckt
        public float MaxDraw;            // Sichtweite (m) dieser Art im Nahband-Feld
        public Color Base;               // mittlere Farbe des Modells im Synty-Atlas (sRGB) — Grundlage der Tönung
        public Color[] Tints;            // Soll-Farben der Varianten (sRGB); null = unverändert
        public string[] Roles;           // Prefab-Pools (Rollen aus VegetationBuilder.RolePool); leere Pools werden übersprungen
        public float[] Weights;          // Anteil je Rolle an den Varianten (Reihenfolge wie Roles); null = nur die erste nicht leere Rolle
        public int MaxVariants = 2;      // höchstens so viele Prefabs je Art: begrenzt die Zahl der Instanz-Batches

        public int TintCount => Tints != null ? Tints.Length : 1;

        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        public static readonly VegSpecies[] All = Build();
        public static VegSpecies Of(Sp s) => All[(int)s];

        private static VegSpecies[] Build()
        {
            var a = new VegSpecies[(int)Sp.Count];
            VegSpecies Add(Sp id, VegGroup g, float h, float r, float foot, float sink, float draw, Color baseCol, Color[] tints, params string[] roles) =>
                a[(int)id] = new VegSpecies { Id = id, Name = id.ToString(), Group = g, Height = h, Radius = r, Foot = foot, Sink = sink, MaxDraw = draw, Base = baseCol, Tints = tints, Roles = roles, MaxVariants = g == VegGroup.Tree || g == VegGroup.Rock ? 3 : 2 };
            Color bush = C(.325f, .35f, .255f), bushL = C(.33f, .355f, .26f), grassT = C(.375f, .435f, .29f), grass = C(.39f, .455f, .30f),
                  shrub = C(.36f, .41f, .275f), tree = C(.39f, .43f, .30f), pine = C(.34f, .35f, .265f), fern = C(.385f, .44f, .295f),
                  cliff = C(.298f, .294f, .282f), dirt = C(.42f, .369f, .306f), rock = C(.31f, .305f, .29f), pebble = C(.32f, .315f, .30f);

            // Bäume
            Add(Sp.Pine,       VegGroup.Tree,  9.5f, 1.0f, .5f, .02f, 900f, pine, new[] { C(.17f, .27f, .16f), C(.21f, .31f, .18f), C(.15f, .24f, .15f) }, "GenPine", "Pine");
            Add(Sp.Gum,        VegGroup.Tree,  8.3f, 2.2f, .7f, .02f, 900f, tree, new[] { C(.36f, .46f, .36f), C(.40f, .47f, .33f), C(.33f, .42f, .33f) }, "GenTree", "BroadTree");
            // Milkwood: Synty-Naturbaum (Pohutukawa, 10-30k Vertices) nur als Beimischung, Hauptteil getönte Generic-Bäume (350 Vertices); Waldbaum nur Generic;
            // Tönung wirkt nur auf Generic-Materialien (Atlas x _BaseColor), Naturbäume behalten ihre Blattfarbe.
            Add(Sp.Milkwood,   VegGroup.Tree,  7.3f, 3.2f, 1.3f, .03f, 900f, tree, new[] { C(.27f, .38f, .25f), C(.31f, .41f, .27f) }, "Coastal", "GenTree").Weights = new[] { .25f, .75f };
            Add(Sp.ForestTree, VegGroup.Tree, 18f, 4.7f, 2.4f, .02f, 900f, tree, new[] { C(.14f, .27f, .15f), C(.18f, .31f, .17f) }, "Forest", "GenTree").Weights = new[] { 0f, 1f };
            Add(Sp.GardenTree, VegGroup.Tree,  8.3f, 2.2f, .7f, .02f, 900f, tree, null, "GenTree", "BroadTree");
            Add(Sp.Palm,       VegGroup.Tree,  7.9f, 2.7f, .5f, .03f, 900f, C(.5f, .5f, .5f), null, "Palm", "Coastal");
            Add(Sp.ProteaTree, VegGroup.Tree,  8.3f, 2.2f, .7f, .02f, 900f, tree, new[] { C(.52f, .60f, .50f), C(.46f, .55f, .44f) }, "GenTree", "BroadTree");

            // Sträucher, Gräser
            Add(Sp.FynLow,     VegGroup.Shrub, 1.4f, 1.6f, 1.1f, .10f, 900f, bush, new[] { C(.34f, .39f, .25f), C(.44f, .47f, .37f), C(.58f, .50f, .30f) }, "GenBush", "Bush");
            Add(Sp.FynThicket, VegGroup.Shrub, 3.0f, 3.1f, 2.0f, .10f, 900f, bushL, new[] { C(.20f, .30f, .17f), C(.27f, .36f, .20f) }, "GenBushLarge", "GenBush", "Bush");
            Add(Sp.Restio,     VegGroup.Shrub, 1.5f, .5f, .45f, .05f, 420f, grassT, new[] { C(.72f, .64f, .40f), C(.60f, .56f, .34f) }, "GenTallGrass", "Grass");
            Add(Sp.Erica,      VegGroup.Shrub, .6f, .3f, .25f, .05f, 420f, shrub, new[] { C(.50f, .33f, .24f), C(.42f, .30f, .30f), C(.42f, .44f, .28f) }, "GenShrub", "GenBush");
            Add(Sp.DuneScrub,  VegGroup.Shrub, 1.2f, .7f, .55f, .05f, 700f, bushL, new[] { C(.52f, .53f, .32f), C(.42f, .46f, .30f) }, "GenBushPart", "GenBush");
            Add(Sp.Fern,       VegGroup.Shrub, .7f, .6f, .4f, .03f, 300f, fern, null, "GenFern", "Fern");
            Add(Sp.LushBush,   VegGroup.Shrub, 1.9f, 1.1f, .8f, .05f, 500f, bush, new[] { C(.22f, .36f, .18f) }, "LushBush", "GenBushLarge").Weights = new[] { .3f, .7f };
            Add(Sp.GrassGreen, VegGroup.Ground, .44f, .4f, .3f, .02f, 80f, grass, new[] { C(.42f, .55f, .30f), C(.36f, .50f, .27f) }, "GenGrass", "Grass");
            Add(Sp.GrassDry,   VegGroup.Ground, .44f, .4f, .3f, .02f, 80f, grass, new[] { C(.70f, .64f, .38f), C(.60f, .55f, .34f) }, "GenGrass", "Grass");
            Add(Sp.Flowers,    VegGroup.Ground, .55f, .6f, .3f, .02f, 70f, C(.5f, .4f, .2f), null, "GenFlowers", "Flowers");
            Add(Sp.Pebbles,    VegGroup.Ground, .19f, .4f, .3f, .3f, 60f, pebble, new[] { C(.55f, .49f, .40f) }, "Pebbles", "GenRock");
            Add(Sp.Reed,       VegGroup.Shrub, 1.5f, .5f, .45f, .05f, 420f, grassT, new[] { C(.55f, .50f, .31f), C(.42f, .46f, .29f) }, "GenTallGrass", "Grass");
            Add(Sp.Driftwood,  VegGroup.Ground, .5f, 1.0f, .8f, .1f, 90f, C(.5f, .5f, .5f), null, "Driftwood");
            Add(Sp.Seaweed,    VegGroup.Ground, .3f, .5f, .4f, .05f, 70f, C(.5f, .5f, .5f), null, "Seaweed");

            // Fels
            Add(Sp.SandBoulder,    VegGroup.Rock, 1.2f, 1.3f, 1.0f, .30f, 900f, rock, new[] { C(.60f, .50f, .38f), C(.52f, .46f, .40f), C(.64f, .45f, .31f) }, "GenRock", "Rock");
            Add(Sp.Cliff,          VegGroup.Rock, 12.9f, 4.5f, 3.0f, .30f, 900f, cliff, new[] { C(.60f, .50f, .38f), C(.66f, .52f, .36f), C(.52f, .47f, .42f) }, "Cliff", "RockCliff", "GenRock").Weights = new[] { .6f, .4f, 0f };
            Add(Sp.DirtCliff,      VegGroup.Rock, 9.2f, 5.8f, 3.5f, .30f, 900f, dirt, new[] { C(.62f, .42f, .28f), C(.56f, .46f, .34f) }, "DirtCliff", "Cliff", "GenRock");
            Add(Sp.GraniteBoulder, VegGroup.Rock, 2.3f, 1.5f, 1.2f, .30f, 900f, rock, null, "Boulder", "GenRock").Weights = new[] { .4f, .6f };
            Add(Sp.Scree,          VegGroup.Rock, .8f, .8f, .6f, .30f, 300f, rock, new[] { C(.58f, .50f, .40f), C(.50f, .45f, .40f) }, "GenRock", "Rock");

            // Ferne (nur Silhouetten, prozedurale Meshes)
            Add(Sp.FarConifer, VegGroup.Tree, 20f, 3.5f, 3f, 0f, 3400f, C(.5f, .5f, .5f), new[] { C(.11f, .20f, .13f), C(.14f, .24f, .15f), C(.09f, .17f, .11f) }, "Silhouette");
            Add(Sp.FarBroad,   VegGroup.Tree, 12f, 6f, 4f, 0f, 3400f, C(.5f, .5f, .5f), new[] { C(.15f, .27f, .15f), C(.19f, .31f, .17f), C(.13f, .23f, .13f) }, "Silhouette");
            return a;
        }
    }
}
