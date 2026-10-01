using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Ergebnis einer Pflanzstelle. Rein geometrisch: welches Prefab/Material daraus wird, entscheidet erst VegetationBuilder.
    public struct VegPlacement
    {
        public float x, y, z;          // y = Geländehöhe (Boden)
        public float yaw;              // Grad
        public float scale;            // relativ zur Nennhöhe der Art (VegSpecies.Height)
        public float leanX, leanZ;     // Neigung zum Hang: x/z-Komponenten der Zielnormalen (0, 0 = senkrecht)
        public Sp sp;
        public byte variant;           // 0..255 gleichverteilt; VegetationBuilder wählt daraus das Prefab nach Gewicht
        public byte tint;              // Index in VegSpecies.Tints (bereits modulo Anzahl)
        public VegTier tier;
        public int cluster;
    }

    public sealed class ScatterSettings
    {
        public float nearEnd = 150f;        // bis hier Nahband: dichte Gruppen, volle Modelle
        public float midEnd = 800f;         // bis hier Mittelband: größere Gruppen, gröbste LOD
        public float farEnd = 3400f;        // Ferne: nur Baumsilhouetten wo die Landbedeckung Bäume sagt
        public float density = 1f;          // Gesamtdichte (Nahband/Mittelband)
        public bool farSilhouettes = true;
        public int maxFar = 14000;
        public int seed = 7771;
    }

    public sealed class ScatterResult
    {
        public List<VegPlacement> Items = new List<VegPlacement>();
        public int Clusters, DroppedClusters, RejectedMembers;
        public double Seconds;
        public int[] PerTier = new int[3];
        public int[] PerSpecies = new int[(int)Sp.Count];
        public string Summary()
        {
            var sb = new System.Text.StringBuilder($"Vegetation: {Items.Count} Pflanzstellen in {Clusters} Gruppen (nah {PerTier[0]}, mittel {PerTier[1]}, fern {PerTier[2]}; verworfen {DroppedClusters} Gruppen/{RejectedMembers} Stellen, {Seconds:0.0} s): ");
            for (int i = 0; i < PerSpecies.Length; i++) if (PerSpecies[i] > 0) sb.Append((Sp)i).Append('=').Append(PerSpecies[i]).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    // Natürliche Vegetationsverteilung statt Zufallswürfen je Zelle:
    //   * Pflanzen wachsen in GRUPPEN (3-15 Pflanzen einer Art), keine Einzelstücke: ein Saatpunkt je Gitterzelle (verrauscht) wählt aus den
    //     Schichten seines Ökotops (EcotopeMap) die Art, Mitglieder liegen in einer Ellipse entlang der Höhenlinie (Felsgruppen, Restio-Bänder),
    //     in der Mitte größer, am Rand lichter; unterschreitet eine Gruppe nach den Prüfungen 3 Pflanzen, entfällt sie ganz.
    //   * Artenwahl und Farbton folgen denselben Feldern wie die Geländefarbe (TerrainPaint.FynFields): wo der Boden Dickicht (dunkel),
    //     Restio (strohfarben) oder Erika (rostbraun) zeigt, stehen auch diese Pflanzen.
    //   * Entfernungsbänder: Nahband (volle Modelle, dicht an der Straße), Mittelband (große Gruppen, Pflanzen größer, damit sie aus der
    //     Ferne lesbar sind), darüber nur Baumsilhouetten, wo die Landbedeckung Bäume sagt. Keine zufälligen Biotope in der Ferne.
    //   * Nie auf Fahrbahn/Planum (Bankett), Querstraßen, Gebäuden, Wasser; Pflanzen überlappen sich nicht.
    // Alles deterministisch (Hash-Rauschen) und unabhängig von der Thread-Reihenfolge.
    public static class VegetationScatter
    {
        private struct Rnd
        {
            private uint s;
            public Rnd(uint seed) { s = seed * 2654435761u + 0x9E3779B9u; if (s == 0u) s = 1u; Next(); Next(); }
            public float Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }
            public float Range(float a, float b) => a + (b - a) * Next();
            public int Int(int a, int b) => Mathf.Min(b, a + (int)(Next() * (b - a + 1)));
        }

        private static float S(float a, float b, float v) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, v));
        private static uint H(int a, int b, int seed) => EcoNoise.Hash(a, b, seed);

        // Standortgrößen an einem Saatpunkt
        private struct Env
        {
            public Eco eco; public int k; public float x, z, d, above, slope, north, ravine, wet, wd, contour, cut, n1, n2;
            public TerrainPaint.FynFields f;
        }

        private sealed class Layer
        {
            public Sp sp; public Func<Env, float> w; public int nMin, nMax; public float rMin, rMax, elong, sMin, sMax, maxD = 1e9f;
            public Func<Env, int> tint; public int minKeep = 3;
        }

        // Vorgaben je Art: wo sie wachsen darf (Ökotope, Neigung, Höhe über dem Meer), Mindestabstand, Neigung zum Hang
        private sealed class Rule { public ulong eco; public float slopeMin, slopeMax = 90f, aboveMin = .45f, aboveMax = 1e5f, spacing = .6f, lean; }

        private static ulong M(params Eco[] e) { ulong m = 0; foreach (var x in e) m |= 1ul << (int)x; return m; }
        private static readonly Rule[] Rules = BuildRules();
        private static readonly Layer[][] Layers = BuildLayers();
        private static readonly float[] Cover = BuildCover();

        private static Rule[] BuildRules()
        {
            var r = new Rule[(int)Sp.Count];
            ulong shrubEco = M(Eco.Fynbos, Eco.Restio, Eco.Sandstone, Eco.DuneScrub, Eco.Field, Eco.Garden, Eco.Woodland, Eco.CoastRock, Eco.RavineForest);
            r[(int)Sp.Pine] = new Rule { eco = M(Eco.Plantation, Eco.Woodland, Eco.Garden, Eco.Field), slopeMax = 32f, spacing = .75f };
            r[(int)Sp.Gum] = new Rule { eco = M(Eco.Plantation, Eco.Woodland, Eco.Garden, Eco.Field, Eco.RavineForest), slopeMax = 32f, spacing = .75f };
            r[(int)Sp.Milkwood] = new Rule { eco = M(Eco.Woodland, Eco.Garden, Eco.Field, Eco.DuneScrub, Eco.RavineForest, Eco.Vlei), slopeMax = 30f, spacing = .75f };
            r[(int)Sp.ForestTree] = new Rule { eco = M(Eco.RavineForest, Eco.Woodland), slopeMax = 42f, spacing = .7f };
            r[(int)Sp.GardenTree] = new Rule { eco = M(Eco.Garden, Eco.Field, Eco.Woodland), slopeMax = 26f, spacing = .8f };
            r[(int)Sp.Palm] = new Rule { eco = M(Eco.Garden), slopeMax = 16f, spacing = .9f };
            r[(int)Sp.ProteaTree] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.RavineForest, Eco.Woodland), slopeMax = 36f, spacing = .75f };
            r[(int)Sp.FynLow] = new Rule { eco = shrubEco, slopeMax = 58f, spacing = .5f };
            r[(int)Sp.FynThicket] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.RavineForest, Eco.Woodland, Eco.Sandstone), slopeMax = 50f, spacing = .55f };
            r[(int)Sp.Restio] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.Field, Eco.DuneScrub, Eco.Vlei, Eco.Sandstone), slopeMax = 52f, spacing = .5f };
            r[(int)Sp.Erica] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.Sandstone, Eco.DuneScrub), slopeMax = 56f, spacing = .5f };
            r[(int)Sp.DuneScrub] = new Rule { eco = M(Eco.DuneScrub, Eco.Beach, Eco.CoastRock, Eco.Fynbos, Eco.Woodland), slopeMax = 32f, aboveMin = 1.6f, spacing = .5f };
            r[(int)Sp.Fern] = new Rule { eco = M(Eco.RavineForest, Eco.Woodland, Eco.Garden, Eco.Fynbos), slopeMax = 40f, spacing = .5f };
            r[(int)Sp.LushBush] = new Rule { eco = M(Eco.RavineForest, Eco.Garden, Eco.Woodland), slopeMax = 38f, spacing = .55f };
            r[(int)Sp.GrassGreen] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.Field, Eco.Garden, Eco.DuneScrub, Eco.Woodland, Eco.Vlei), slopeMax = 36f, spacing = .4f };
            r[(int)Sp.GrassDry] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.Field, Eco.Garden, Eco.DuneScrub, Eco.Woodland, Eco.Beach), slopeMax = 36f, aboveMin = 1.2f, spacing = .4f };
            r[(int)Sp.Flowers] = new Rule { eco = M(Eco.Fynbos, Eco.Restio, Eco.Field, Eco.Garden, Eco.DuneScrub), slopeMax = 32f, spacing = .4f };
            r[(int)Sp.Pebbles] = new Rule { eco = M(Eco.Beach, Eco.CoastRock, Eco.DuneScrub, Eco.Sandstone, Eco.Fynbos), spacing = .3f, aboveMin = .6f };
            r[(int)Sp.Reed] = new Rule { eco = M(Eco.Vlei), slopeMax = 18f, aboveMin = 1.2f, spacing = .45f };
            r[(int)Sp.Driftwood] = new Rule { eco = M(Eco.Beach), slopeMax = 14f, aboveMin = .6f, aboveMax = 3.2f, spacing = .6f };
            r[(int)Sp.Seaweed] = new Rule { eco = M(Eco.Beach, Eco.CoastRock), slopeMax = 16f, aboveMin = .35f, aboveMax = 1.7f, spacing = .5f };
            ulong rockEco = M(Eco.Fynbos, Eco.Restio, Eco.Sandstone, Eco.CoastRock, Eco.DuneScrub, Eco.Field, Eco.Woodland, Eco.RavineForest);
            r[(int)Sp.SandBoulder] = new Rule { eco = rockEco, spacing = .85f, lean = .6f };
            r[(int)Sp.Cliff] = new Rule { eco = M(Eco.Sandstone, Eco.CoastRock, Eco.Fynbos, Eco.RavineForest), slopeMin = 26f, spacing = .8f, lean = .85f };
            r[(int)Sp.DirtCliff] = new Rule { eco = M(Eco.Sandstone, Eco.Fynbos, Eco.Restio, Eco.RavineForest, Eco.Field, Eco.Woodland, Eco.CoastRock, Eco.DuneScrub), slopeMin = 22f, spacing = .8f, lean = .85f };
            r[(int)Sp.GraniteBoulder] = new Rule { eco = M(Eco.CoastRock, Eco.Beach, Eco.DuneScrub, Eco.Fynbos), spacing = .9f, lean = .6f, aboveMin = .5f };
            r[(int)Sp.Scree] = new Rule { eco = M(Eco.Sandstone, Eco.Fynbos, Eco.CoastRock, Eco.Restio), slopeMin = 16f, spacing = .8f, lean = .7f };
            r[(int)Sp.FarConifer] = new Rule { eco = M(Eco.Plantation, Eco.Woodland, Eco.RavineForest) };
            r[(int)Sp.FarBroad] = new Rule { eco = M(Eco.Plantation, Eco.Woodland, Eco.RavineForest) };
            for (int i = 0; i < r.Length; i++) if (r[i] == null) r[i] = new Rule { eco = 0 };
            return r;
        }

        // Anteil der Fläche, auf dem das Ökotop überhaupt Gruppen trägt (Wiese/Felswand: wenig, Dickicht/Wald: voll)
        private static float[] BuildCover()
        {
            var c = new float[(int)Eco.Count];
            c[(int)Eco.Fynbos] = 1f; c[(int)Eco.Restio] = .85f; c[(int)Eco.Sandstone] = .9f; c[(int)Eco.RavineForest] = 1.15f; c[(int)Eco.Woodland] = 1f;
            c[(int)Eco.DuneScrub] = .9f; c[(int)Eco.CoastRock] = .7f; c[(int)Eco.Beach] = .45f; c[(int)Eco.Vlei] = 1f; c[(int)Eco.Field] = .35f; c[(int)Eco.Garden] = .55f;
            return c;
        }

        private static Layer L(Sp sp, Func<Env, float> w, int nMin, int nMax, float rMin, float rMax, float elong, float sMin, float sMax, float maxD = 1e9f, Func<Env, int> tint = null, int minKeep = 3)
            => new Layer { sp = sp, w = w, nMin = nMin, nMax = nMax, rMin = rMin, rMax = rMax, elong = elong, sMin = sMin, sMax = sMax, maxD = maxD, tint = tint, minKeep = minKeep };

        private static Layer[][] BuildLayers()
        {
            var t = new Layer[(int)Eco.Count][];
            Func<Env, int> fynTint = e => e.f.dry > .62f ? 2 : (e.f.grey > .45f ? 1 : 0);
            Func<Env, int> thickTint = e => e.f.thicket > .35f ? 0 : 1;
            Func<Env, int> restioTint = e => e.f.straw > .35f ? 0 : 1;
            Func<Env, int> noiseTint = e => (int)(e.n2 * 2.99f);
            Func<Env, int> noise1Tint = e => (int)(e.n1 * 2.99f);
            // Kaltflächen am Straßeneinschnitt: Erdwand mit Felsen (in jedem Ökotop außer Wasser/Siedlung/Strand)
            Layer cutFace = L(Sp.DirtCliff, e => .55f * S(2.5f, 6f, -e.cut) * S(18f, 34f, e.slope + 12f), 2, 4, 4f, 7f, 2.6f, .5f, .85f, 70f, e => e.n2 > .5f ? 1 : 0, 2);

            // Wiederbewuchs der Einschnittböschung: niedrige Büsche/Restio statt nackter Erde
            Layer cutScrub = L(Sp.FynLow, e => .45f * S(2f, 5f, -e.cut), 4, 8, 3f, 5.5f, 2.2f, .7f, 1.1f, 90f, fynTint);
            t[(int)Eco.Fynbos] = new[]
            {
                L(Sp.FynLow, e => .55f * (1f - .75f * e.f.thicket) * (1f - .8f * e.f.straw) * (1f - .7f * S(40f, 58f, e.slope)), 5, 12, 3.5f, 6.5f, 1.5f, .85f, 1.5f, 1e9f, fynTint),
                L(Sp.FynThicket, e => (.10f + 1.1f * e.f.thicket) * (1f - .85f * S(38f, 50f, e.slope)), 4, 10, 4f, 8f, 1.6f, 1.0f, 1.7f, 1e9f, thickTint),
                L(Sp.Restio, e => (.10f + .9f * e.f.straw) * (1f - .6f * S(40f, 52f, e.slope)), 7, 15, 3f, 5.5f, 1.8f, .9f, 1.5f, 1e9f, restioTint),
                L(Sp.Erica, e => .04f + 1.0f * e.f.rust, 8, 15, 2.5f, 5f, 1.6f, 1.3f, 2.3f, 1e9f, noiseTint),
                L(Sp.SandBoulder, e => .03f + .35f * S(18f, 36f, e.slope) + .15f * e.n2 * S(10f, 30f, e.slope), 3, 8, 3f, 6.5f, 2.4f, .8f, 2f, 1e9f, noise1Tint),
                L(Sp.Cliff, e => .12f * S(36f, 46f, e.slope), 2, 4, 4f, 7f, 3.2f, .45f, .75f, 1e9f, noise1Tint, 2),
                L(Sp.Scree, e => .15f * S(24f, 38f, e.slope), 6, 14, 3f, 6f, 2.4f, .7f, 1.5f, 1e9f, noiseTint),
                L(Sp.ProteaTree, e => .12f * (.25f + .75f * e.f.thicket) * S(0f, .3f, e.wet + e.ravine) * (e.above < 450f ? 1f : 0f) * (1f - S(28f, 36f, e.slope)), 3, 7, 3.5f, 6.5f, 1.3f, .55f, 1f, 1e9f, e => e.n2 > .5f ? 1 : 0),
                L(Sp.Flowers, e => .12f * (1f - S(24f, 32f, e.slope)), 6, 14, 2f, 3.5f, 1.4f, 1f, 1.8f, 60f),
                L(Sp.GrassDry, e => .12f * (1f - S(28f, 36f, e.slope)), 6, 14, 2f, 3.5f, 1.4f, .9f, 1.5f, 80f, noiseTint),
                cutFace, cutScrub,
            };
            t[(int)Eco.Restio] = new[]
            {
                L(Sp.Restio, e => .9f, 8, 15, 3f, 6f, 1.8f, 1f, 1.6f, 1e9f, restioTint),
                L(Sp.FynLow, e => .2f * (1f - S(36f, 46f, e.slope)), 5, 10, 3f, 6f, 1.5f, .85f, 1.4f, 1e9f, fynTint),
                L(Sp.Erica, e => .15f + .8f * e.f.rust, 8, 15, 2.5f, 5f, 1.6f, 1.3f, 2.3f, 1e9f, noiseTint),
                L(Sp.SandBoulder, e => .06f + .3f * S(18f, 36f, e.slope), 3, 7, 3f, 6f, 2.4f, .8f, 1.8f, 1e9f, noise1Tint),
                L(Sp.GrassDry, e => .15f, 6, 14, 2f, 3.5f, 1.4f, .9f, 1.5f, 80f, noiseTint),
                L(Sp.Flowers, e => .06f, 6, 12, 2f, 3.5f, 1.4f, 1f, 1.8f, 60f),
                cutFace, cutScrub,
            };
            t[(int)Eco.Sandstone] = new[]
            {
                L(Sp.Cliff, e => .50f * S(30f, 40f, e.slope), 2, 5, 4f, 9f, 3.4f, .55f, 1f, 1e9f, noise1Tint, 2),
                L(Sp.SandBoulder, e => .45f, 4, 10, 4f, 9f, 2.4f, 1f, 2.2f, 1e9f, noise1Tint),
                L(Sp.Scree, e => .22f * S(24f, 38f, e.slope) * (1f - S(40f, 52f, e.slope)), 6, 14, 3f, 6f, 2.4f, .7f, 1.5f, 1e9f, noiseTint),
                L(Sp.FynLow, e => .30f * (1f - .65f * S(40f, 58f, e.slope)), 3, 8, 2f, 4f, 1.6f, .8f, 1.3f, 1e9f, fynTint),
                L(Sp.Erica, e => .10f * (1f - .7f * S(40f, 56f, e.slope)), 5, 10, 2f, 4f, 1.6f, 1.2f, 2f, 1e9f, noiseTint),
                cutFace,
            };
            t[(int)Eco.RavineForest] = new[]
            {
                L(Sp.ForestTree, e => 1.0f, 4, 8, 6f, 10f, 1.3f, .6f, .9f, 1e9f, e => e.n2 > .5f ? 1 : 0, 3),
                L(Sp.LushBush, e => .35f, 4, 9, 3f, 6f, 1.4f, .8f, 1.3f, 1e9f),
                L(Sp.Fern, e => .40f, 6, 12, 2f, 4f, 1.4f, .9f, 1.5f, 90f),
                L(Sp.FynThicket, e => .15f, 4, 8, 3f, 6f, 1.4f, 1f, 1.6f, 1e9f, e => 0),
                L(Sp.SandBoulder, e => .05f, 3, 6, 3f, 5f, 2f, .8f, 1.6f, 1e9f, noise1Tint),
                cutFace, cutScrub,
            };
            t[(int)Eco.Woodland] = new[]
            {
                L(Sp.Milkwood, e => .55f, 3, 7, 4f, 7f, 1.3f, .8f, 1.3f, 1e9f, noiseTint, 3),
                L(Sp.FynThicket, e => .30f, 4, 9, 3f, 6f, 1.5f, 1f, 1.6f, 1e9f, thickTint),
                L(Sp.GardenTree, e => .15f, 3, 5, 3f, 5f, 1.3f, .9f, 1.3f),
                L(Sp.ForestTree, e => .10f, 4, 7, 4f, 7f, 1.3f, .55f, .8f, 1e9f, e => e.n2 > .5f ? 1 : 0),
                L(Sp.GrassGreen, e => .15f, 6, 14, 2f, 3.5f, 1.4f, .9f, 1.5f, 80f, noiseTint),
                L(Sp.LushBush, e => .10f, 3, 6, 2.5f, 4.5f, 1.4f, .8f, 1.3f),
                cutFace, cutScrub,
            };
            t[(int)Eco.Garden] = new[]
            {
                L(Sp.GardenTree, e => .25f, 2, 4, 3f, 5f, 1.2f, .9f, 1.4f, 1e9f, null, 2),
                L(Sp.Palm, e => .15f * (e.above < 30f && e.wd < 800f ? 1f : 0f), 2, 3, 3f, 5f, 1.2f, .9f, 1.2f, 1e9f, null, 2),
                L(Sp.LushBush, e => .30f, 3, 6, 2f, 3.5f, 1.3f, .8f, 1.3f),
                L(Sp.FynLow, e => .12f, 3, 6, 2f, 3f, 1.3f, .8f, 1.2f, 1e9f, e => 0),
                L(Sp.Flowers, e => .15f, 4, 8, 1.5f, 3f, 1.3f, 1f, 1.6f, 70f),
                L(Sp.GrassGreen, e => .20f, 6, 12, 2f, 3.5f, 1.3f, .9f, 1.4f, 80f, noiseTint),
            };
            t[(int)Eco.Field] = new[]
            {
                L(Sp.GrassGreen, e => .35f, 7, 15, 3f, 6f, 1.4f, .9f, 1.6f, 80f, noiseTint),
                L(Sp.GrassDry, e => .20f, 7, 15, 3f, 6f, 1.4f, .9f, 1.6f, 80f, noiseTint),
                L(Sp.GardenTree, e => .05f, 2, 5, 3f, 6f, 1.3f, .9f, 1.4f, 1e9f, null, 2),
                L(Sp.Milkwood, e => .03f, 3, 6, 4f, 7f, 1.3f, .8f, 1.3f, 1e9f, noiseTint),
                L(Sp.FynLow, e => .10f, 4, 9, 3f, 5f, 1.4f, .8f, 1.4f, 1e9f, fynTint),
                L(Sp.Flowers, e => .05f, 6, 12, 2f, 3.5f, 1.4f, 1f, 1.8f, 60f),
                L(Sp.SandBoulder, e => .02f, 3, 5, 2.5f, 4.5f, 1.8f, .8f, 1.5f, 1e9f, noise1Tint),
            };
            t[(int)Eco.Vlei] = new[]
            {
                L(Sp.Reed, e => 1.0f, 8, 15, 3f, 6f, 1.5f, 1f, 1.6f, 1e9f, noiseTint),
                L(Sp.GrassGreen, e => .25f, 6, 12, 2f, 4f, 1.4f, .9f, 1.5f, 80f, noiseTint),
                L(Sp.Milkwood, e => .02f, 3, 5, 4f, 6f, 1.3f, .8f, 1.2f, 1e9f, noiseTint),
            };
            t[(int)Eco.DuneScrub] = new[]
            {
                L(Sp.DuneScrub, e => .8f, 6, 14, 3f, 6f, 1.5f, 1f, 1.7f, 1e9f, e => e.n2 > .5f ? 1 : 0),
                L(Sp.FynLow, e => .2f, 4, 9, 3f, 5f, 1.5f, .8f, 1.3f, 1e9f, e => e.n2 > .6f ? 0 : 1),
                L(Sp.GrassDry, e => .2f, 6, 14, 2f, 3.5f, 1.4f, .9f, 1.5f, 80f, noiseTint),
                L(Sp.Erica, e => .05f, 6, 12, 2f, 4f, 1.5f, 1.3f, 2f, 1e9f, noiseTint),
                L(Sp.Pebbles, e => .05f, 5, 10, 2f, 4f, 1.3f, .9f, 1.5f, 60f),
                cutFace,
            };
            t[(int)Eco.CoastRock] = new[]
            {
                L(Sp.GraniteBoulder, e => .55f, 3, 8, 3f, 7f, 2.2f, 1f, 2.4f),
                L(Sp.Scree, e => .15f, 6, 12, 3f, 5f, 2f, .7f, 1.3f, 1e9f, noiseTint),
                L(Sp.DuneScrub, e => .30f * (1f - S(30f, 40f, e.slope)), 5, 10, 3f, 5f, 1.5f, 1f, 1.5f, 1e9f, e => e.n2 > .5f ? 1 : 0),
                L(Sp.Cliff, e => .10f * S(30f, 42f, e.slope), 2, 4, 4f, 7f, 3f, .5f, .8f, 1e9f, noise1Tint, 2),
                cutFace,
            };
            t[(int)Eco.Beach] = new[]
            {
                L(Sp.Seaweed, e => .25f * S(.3f, .6f, e.above) * (1f - S(1.2f, 1.7f, e.above)), 3, 8, 2f, 4f, 1.8f, .9f, 1.4f, 90f),
                L(Sp.Driftwood, e => .12f * S(.5f, 1.2f, e.above), 2, 4, 2f, 4f, 1.8f, .7f, 1.2f, 100f, null, 2),
                L(Sp.Pebbles, e => .12f, 5, 10, 2f, 4f, 1.4f, .9f, 1.5f, 70f),
                L(Sp.DuneScrub, e => .20f * S(2f, 3.2f, e.above), 4, 8, 2.5f, 4.5f, 1.4f, 1f, 1.5f, 1e9f, e => e.n2 > .5f ? 1 : 0),
                L(Sp.GrassDry, e => .20f * S(1.8f, 3f, e.above), 6, 12, 2f, 3.5f, 1.4f, .9f, 1.5f, 80f, noiseTint),
            };
            return t;
        }

        // ------------------------------------------------------------------------------------------------ Lauf
        private sealed class Cluster { public List<VegPlacement> m = new List<VegPlacement>(); public int minKeep = 3; public int id; }

        private sealed class Ctx
        {
            public WorldTerrain t; public EcotopeMap map; public Occupancy occ; public ScatterSettings s; public float seaY;
            public float Planum0;
            public Ctx(WorldTerrain t, EcotopeMap map, Occupancy occ, ScatterSettings s) { this.t = t; this.map = map; this.occ = occ; this.s = s; seaY = t.SeaY; }

            public float RouteDist(float x, float z)
            {
                float a = map.RoadDistAt(x, z);
                return a < 50f ? t.Road.Distance(x, z, 70f) : a;
            }

            // Fahrbahn + Randstreifen + Bankett der Route (Planum) und Querstraßen samt Gehweg: dort wächst nichts.
            public bool Corridor(float x, float z, float margin)
            {
                float rr = WorldTerrain.PlanumMax + margin + 1f;
                if (t.Road.Nearest(x, z, rr, out int i, out float dd) && dd < WorldTerrain.Planum(t.Road.Samples[i].half) + margin) return true;
                var st = t.Streets;
                if (st != null && st.Nearest(x, z, 14f + margin, out int j, out float ds) && ds < st.Samples[j].half + 2.8f + margin) return true;
                return false;
            }

            public Env MakeEnv(float x, float z, float d, Eco eco)
            {
                int k = map.Index(x, z);
                var e = new Env { eco = eco, k = k, x = x, z = z, d = d };
                e.above = map.Height[k] - seaY; e.slope = map.Slope[k]; e.north = Mathf.Cos(map.Aspect[k] * Mathf.Deg2Rad);
                e.ravine = map.Ravine[k]; e.wet = map.Wet[k]; e.wd = map.WaterDist[k]; e.contour = map.Contour[k]; e.cut = map.CutFill[k];
                e.n1 = EcoNoise.Fbm(x, z, 230f, 61, 2); e.n2 = EcoNoise.Fbm(x, z, 70f, 62, 2);
                if (eco == Eco.Fynbos || eco == Eco.Restio || eco == Eco.Sandstone) e.f = TerrainPaint.FynbosFieldsAt(map, k, x, z);
                return e;
            }
        }

        public static ScatterResult Run(WorldTerrain terrain, EcotopeMap map, Occupancy occupied, ScatterSettings settings = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = settings ?? new ScatterSettings();
            var ctx = new Ctx(terrain, map, occupied ?? new Occupancy(), s);
            var res = new ScatterResult();
            var all = new List<Cluster>();

            // Reihenfolge = Vorrang bei Überlappung: Forst, Straßensaum, Nahband, Mittelband, Ferne
            PlantationPass(ctx, all);
            VergePass(ctx, all);
            LatticePass(ctx, all, VegTier.Near, 15f, 1.6f, 40f, 1);
            LatticePass(ctx, all, VegTier.Mid, 34f, 1.2f, 0f, 2);
            Commit(ctx, all, res);
            if (s.farSilhouettes && s.farEnd > s.midEnd) FarPass(ctx, res);
            foreach (var p in res.Items) { res.PerTier[(int)p.tier]++; res.PerSpecies[(int)p.sp]++; }
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        // ------------------------------------------------------------------------------------------------ Saatpunkte
        // Dichte der Gruppen je Entfernung zur Route (Wahrscheinlichkeit = 1 - exp(-Summe der Schichtgewichte * Lambda * Deckung)).
        private static float Lambda(ScatterSettings s, VegTier tier, float d, float lam0, float decay)
        {
            if (tier == VegTier.Near)
            {
                float fade = 1f - S(s.nearEnd - 45f, s.nearEnd, d);
                return lam0 / (1f + (d / decay) * (d / decay)) * fade;
            }
            float inn = S(s.nearEnd - 70f, s.nearEnd + 10f, d);
            float outer = 1f - .35f * S(300f, s.midEnd, d);
            return lam0 * inn * outer;
        }

        private static void LatticePass(Ctx c, List<Cluster> all, VegTier tier, float cell, float lam0, float decay, int seedSalt)
        {
            var map = c.map; var s = c.s;
            float dMin = tier == VegTier.Near ? 0f : s.nearEnd - 70f, dMax = tier == VegTier.Near ? s.nearEnd : s.midEnd;
            int cols = Mathf.CeilToInt(map.W * EcotopeMap.Cell / cell), rows = Mathf.CeilToInt(map.H * EcotopeMap.Cell / cell);
            var rowRes = new List<Cluster>[rows];
            int seedBase = s.seed + seedSalt * 1013;
            Parallel.For(0, rows, j =>
            {
                List<Cluster> list = null;
                for (int i = 0; i < cols; i++)
                {
                    uint h = H(i, j, seedBase);
                    var rnd = new Rnd(h);
                    float x = map.MinX + (i + .15f + .7f * rnd.Next()) * cell, z = map.MinZ + (j + .15f + .7f * rnd.Next()) * cell;
                    if (!map.Inside(x, z)) continue;
                    float da = map.RoadDistAt(x, z);
                    if (da > dMax + cell || da < dMin - cell) continue;
                    float d = da < 50f ? c.t.Road.Distance(x, z, 70f) : da;
                    if (d < WorldTerrain.Planum(5.5f) + .5f || d > dMax) continue;
                    float lam = Lambda(s, tier, d, lam0, decay) * s.density * (1f + 1.1f * S(12f, 40f, map.SlopeAt(x, z)) + 1.5f * S(32f, 55f, map.SlopeAt(x, z)));   // steile Hänge füllen mehr Bildfläche
                    if (lam < .004f) continue;
                    Eco eco = map.AtWarped(x, z);
                    if (eco == Eco.Water || eco == Eco.Urban || eco == Eco.Plantation) continue;
                    var layers = Layers[(int)eco]; if (layers == null) continue;
                    var env = c.MakeEnv(x, z, d, eco);
                    float total = 0f; var ws = new float[layers.Length];
                    for (int q = 0; q < layers.Length; q++)
                    {
                        var ly = layers[q];
                        float w = d > ly.maxD || VegSpecies.Of(ly.sp).MaxDraw < d ? 0f : Mathf.Max(0f, ly.w(env));
                        ws[q] = w; total += w;
                    }
                    if (total <= 0f) continue;
                    float pc = 1f - Mathf.Exp(-total * lam * Cover[(int)eco]);
                    if (rnd.Next() > pc) continue;
                    float pick = rnd.Next() * total; int sel = 0;
                    for (int q = 0; q < ws.Length; q++) { pick -= ws[q]; if (pick <= 0f) { sel = q; break; } sel = q; }
                    var cl = BuildCluster(c, layers[sel], env, x, z, d, tier, ref rnd);
                    if (cl == null) continue;
                    if (list == null) list = new List<Cluster>();
                    list.Add(cl);
                }
                rowRes[j] = list;
            });
            foreach (var l in rowRes) if (l != null) all.AddRange(l);
        }

        private static Cluster BuildCluster(Ctx c, Layer ly, Env env, float cx, float cz, float d, VegTier tier, ref Rnd rnd)
        {
            var spc = VegSpecies.Of(ly.sp); var rule = Rules[(int)ly.sp];
            var cl = new Cluster { minKeep = ly.minKeep };
            int n = rnd.Int(ly.nMin, ly.nMax);
            float far = S(40f, 420f, d);
            float rm = Mathf.Lerp(1f, 1.8f, far);
            float gk = spc.Group == VegGroup.Shrub ? 1f : spc.Group == VegGroup.Rock ? .9f : spc.Group == VegGroup.Tree ? .45f : 0f;
            float sm = 1f + gk * 1.2f * S(100f, 500f, d);
            float R = rnd.Range(ly.rMin, ly.rMax) * rm;
            float ang = env.slope > 8f ? env.contour : rnd.Range(0f, 180f);
            float ux = Mathf.Sin(ang * Mathf.Deg2Rad), uz = Mathf.Cos(ang * Mathf.Deg2Rad);
            float ea = Mathf.Sqrt(ly.elong), eb = 1f / ea;
            float th0 = rnd.Range(0f, 6.2832f);
            byte tint = 0;
            int tc = spc.TintCount;
            if (ly.tint != null && tc > 1) tint = (byte)(((ly.tint(env) % tc) + tc) % tc);
            byte vbase = (byte)rnd.Int(0, 255);
            for (int q = 0; q < n; q++)
            {
                float rr = R * Mathf.Sqrt((q + .5f) / n), th = th0 + q * 2.399963f;
                float a = rr * Mathf.Cos(th) * ea + (rnd.Next() - .5f) * R * .30f, b = rr * Mathf.Sin(th) * eb + (rnd.Next() - .5f) * R * .30f;
                if (rr > R * .78f && rnd.Next() < .30f) continue;                  // Rand lichter
                float x = cx + a * ux - b * uz, z = cz + a * uz + b * ux;
                float rel = rr / Mathf.Max(R, .01f);
                float scale = rnd.Range(ly.sMin, ly.sMax) * (1.12f - .45f * rel) * sm;
                scale = Mathf.Clamp(scale, .3f, 3.6f);
                var p = new VegPlacement { x = x, z = z, yaw = rnd.Range(0f, 360f), scale = scale, sp = ly.sp, tier = tier, tint = tint };
                // zwei bis drei Varianten je Gruppe: Zufallszahl um die Gruppenbasis
                p.variant = (byte)((vbase + (rnd.Next() < .5f ? 0 : 85 + (int)(rnd.Next() * 3f) * 40)) & 255);
                if (rnd.Next() < .08f && tc > 1) p.tint = (byte)((tint + 1) % tc);
                if (!Validate(c, ref p, spc, rule, d)) continue;
                cl.m.Add(p);
            }
            return cl.m.Count >= cl.minKeep ? cl : null;
        }

        // Statische Prüfung (ohne Wissen über andere Pflanzen): Ökotop, Neigung, Gebäude, Straße, Wasser, Landmarks.
        private static bool Validate(Ctx c, ref VegPlacement p, VegSpecies spc, Rule rule, float routeD)
        {
            var map = c.map;
            if (!map.Inside(p.x, p.z)) return false;
            int k = map.Index(p.x, p.z);
            if (map.RoadDist[k] > c.s.midEnd) return false;                // jenseits des Mittelbands wächst hier nichts (nur Silhouetten, FarPass)
            if (((rule.eco >> map.Kind[k]) & 1ul) == 0ul) return false;
            float slope = map.Slope[k];
            if (slope < rule.slopeMin || slope > rule.slopeMax) return false;
            if (map.Building[k]) return false;
            float r = spc.Foot * p.scale;
            if (c.Corridor(p.x, p.z, .8f + Mathf.Min(r, 2.5f) * .3f)) return false;
            float y = c.t.HeightAt(p.x, p.z);
            float above = y - c.seaY;
            if (above < rule.aboveMin || above > rule.aboveMax) return false;
            if (!c.occ.IsFree(p.x, p.z, r)) return false;
            p.y = y;
            if (rule.lean > 0f)
            {
                const float e = 1.4f;
                float dx = c.t.HeightAt(p.x + e, p.z) - c.t.HeightAt(p.x - e, p.z), dz = c.t.HeightAt(p.x, p.z + e) - c.t.HeightAt(p.x, p.z - e);
                float gx = dx / (2f * e), gz = dz / (2f * e);
                float ny = 1f / Mathf.Sqrt(1f + gx * gx + gz * gz);
                p.leanX = -gx * ny * rule.lean; p.leanZ = -gz * ny * rule.lean;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ Straßensaum
        // Gras-/Blumen-/Buschgruppen am Rand der Planum (die Straße soll nicht in nacktem Boden enden).
        private static void VergePass(Ctx c, List<Cluster> all)
        {
            var samples = c.t.Road.Samples; var s = c.s; var map = c.map;
            int step = 5;                                               // alle 10 m
            int count = samples.Count / step + 1;
            var res = new List<Cluster>[count];
            Parallel.For(0, count, ci =>
            {
                int si = ci * step; if (si >= samples.Count) return;
                var sm = samples[si];
                for (int side = -1; side <= 1; side += 2)
                {
                    var rnd = new Rnd(H(si, side, s.seed + 31));
                    if (rnd.Next() > .55f * s.density) continue;
                    float off = WorldTerrain.Planum(sm.half) + .9f + 11f * Mathf.Pow(rnd.Next(), 1.7f);
                    float x = sm.pos.x + sm.side.x * side * off + (rnd.Next() - .5f) * 5f * sm.tangent.x, z = sm.pos.z + sm.side.z * side * off + (rnd.Next() - .5f) * 5f * sm.tangent.z;
                    if (!map.Inside(x, z)) continue;
                    Eco eco = map.At(x, z);
                    if (eco == Eco.Water || eco == Eco.Urban || eco == Eco.Plantation || eco == Eco.Sandstone) continue;
                    var env = c.MakeEnv(x, z, off, eco);
                    float pk = rnd.Next(); Sp sp; Layer ly;
                    bool green = eco == Eco.Field || eco == Eco.Garden || eco == Eco.Vlei || eco == Eco.Woodland || eco == Eco.RavineForest;
                    if (pk < .55f) { sp = green ? Sp.GrassGreen : Sp.GrassDry; ly = L(sp, null, 6, 11, 1.6f, 3.2f, 1.6f, .9f, 1.5f, 1e9f, e => (int)(e.n2 * 1.99f)); }
                    else if (pk < .72f && eco != Eco.Beach) { sp = Sp.Flowers; ly = L(sp, null, 5, 9, 1.4f, 2.6f, 1.5f, 1f, 1.8f); }
                    else if (pk < .88f && (eco == Eco.Fynbos || eco == Eco.Restio || eco == Eco.DuneScrub)) { sp = Sp.FynLow; ly = L(sp, null, 4, 7, 2f, 3.5f, 1.6f, .8f, 1.3f, 1e9f, e => e.f.dry > .62f ? 2 : (e.f.grey > .45f ? 1 : 0)); }
                    else if (eco == Eco.Fynbos || eco == Eco.Restio) { sp = Sp.Restio; ly = L(sp, null, 6, 11, 1.8f, 3.2f, 1.6f, .9f, 1.4f, 1e9f, e => 0); }
                    else if (eco == Eco.Beach) { sp = Sp.Pebbles; ly = L(sp, null, 5, 9, 1.6f, 3f, 1.5f, .9f, 1.5f); }
                    else { sp = green ? Sp.GrassGreen : Sp.GrassDry; ly = L(sp, null, 6, 11, 1.6f, 3.2f, 1.6f, .9f, 1.5f, 1e9f, e => (int)(e.n2 * 1.99f)); }
                    var cl = BuildCluster(c, ly, env, x, z, WorldTerrain.Planum(sm.half) + off, VegTier.Near, ref rnd);
                    if (cl == null) continue;
                    if (res[ci] == null) res[ci] = new List<Cluster>();
                    res[ci].Add(cl);
                }
            });
            foreach (var l in res) if (l != null) all.AddRange(l);
        }

        // ------------------------------------------------------------------------------------------------ Forst in Reihen
        private static void PlantationPass(Ctx c, List<Cluster> all)
        {
            var map = c.map; var s = c.s;
            int W = map.W, H_ = map.H;
            var rowRes = new List<Cluster>[H_];
            Parallel.For(0, H_, j =>
            {
                List<Cluster> list = null;
                for (int i = 0; i < W; i++)
                {
                    int k = j * W + i;
                    if (map.Kind[k] != (byte)Eco.Plantation) continue;
                    float d = map.RoadDist[k]; if (d > s.midEnd) continue;
                    float x0 = map.MinX + i * EcotopeMap.Cell, z0 = map.MinZ + j * EcotopeMap.Cell;
                    int sx = Mathf.FloorToInt((x0 + 5f) / 200f), sz = Mathf.FloorToInt((z0 + 5f) / 200f);
                    uint hs = H(sx, sz, s.seed + 77);
                    float angle = (hs & 1023) / 1023f * 180f;
                    bool pine = ((hs >> 10) & 7) < 5;
                    Sp sp = pine ? Sp.Pine : Sp.Gum;
                    float rowSp = pine ? 5.5f : 6.5f, treeSp = pine ? 3.8f : 4.5f;
                    float mul = 1f + .75f * S(s.nearEnd - 40f, s.nearEnd + 40f, d);       // im Mittelband weitere Pflanzung
                    rowSp *= mul; treeSp *= mul;
                    float ur = angle * Mathf.Deg2Rad, ux = Mathf.Sin(ur), uz = Mathf.Cos(ur), vx = -uz, vz = ux;
                    float offA = ((hs >> 13) & 255) / 255f * treeSp, offB = ((hs >> 21) & 255) / 255f * rowSp;
                    // Gitterindex-Bereich, der die 10-m-Zelle überdeckt
                    float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                    for (int cc = 0; cc < 4; cc++)
                    {
                        float px = x0 + (cc & 1) * EcotopeMap.Cell, pz = z0 + (cc >> 1) * EcotopeMap.Cell;
                        float u = px * ux + pz * uz, v = px * vx + pz * vz;
                        u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
                    }
                    int a0 = Mathf.FloorToInt((u0 - offA) / treeSp), a1 = Mathf.CeilToInt((u1 - offA) / treeSp);
                    int b0 = Mathf.FloorToInt((v0 - offB) / rowSp), b1 = Mathf.CeilToInt((v1 - offB) / rowSp);
                    Cluster cl = null;
                    var spc = VegSpecies.Of(sp); var rule = Rules[(int)sp];
                    for (int b = b0; b <= b1; b++)
                    for (int a = a0; a <= a1; a++)
                    {
                        var rnd = new Rnd(H(a + sx * 7919, b + sz * 6529, s.seed + 79));
                        float u = offA + (a + (rnd.Next() - .5f) * .35f) * treeSp, v = offB + (b + (rnd.Next() - .5f) * .12f) * rowSp;
                        float x = u * ux + v * vx, z = u * uz + v * vz;
                        if (x < x0 || x >= x0 + EcotopeMap.Cell || z < z0 || z >= z0 + EcotopeMap.Cell) continue;
                        float scale = (pine ? rnd.Range(1.15f, 1.6f) : rnd.Range(1.9f, 2.5f)) * (1f + .35f * S(100f, 500f, d));
                        var p = new VegPlacement { x = x, z = z, yaw = rnd.Range(0f, 360f), scale = scale, sp = sp, tier = d < s.nearEnd ? VegTier.Near : VegTier.Mid,
                                                   variant = (byte)(((hs >> 5) & 255u) ^ (uint)(rnd.Next() * 7.99f)), tint = (byte)((hs >> 3) % (uint)spc.TintCount) };
                        if (!Validate(c, ref p, spc, rule, d)) continue;
                        if (cl == null) cl = new Cluster { minKeep = 1 };
                        cl.m.Add(p);
                    }
                    if (cl != null) { if (list == null) list = new List<Cluster>(); list.Add(cl); }
                }
                rowRes[j] = list;
            });
            foreach (var l in rowRes) if (l != null) all.AddRange(l);
        }

        // ------------------------------------------------------------------------------------------------ Übernahme (sequenziell, deterministisch)
        private static void Commit(Ctx c, List<Cluster> all, ScatterResult res)
        {
            const float Cs = 8f;
            var grid = new Dictionary<long, List<int>>();
            var items = res.Items;
            Func<float, int> cell = v => Mathf.FloorToInt(v / Cs);
            Func<int, int, long> key = (a, b) => ((long)a << 32) | (uint)b;
            int clusterId = 0;
            var accepted = new List<VegPlacement>();
            foreach (var cl in all)
            {
                accepted.Clear();
                foreach (var p in cl.m)
                {
                    var spc = VegSpecies.Of(p.sp); float r = spc.Foot * p.scale; float sp1 = Rules[(int)p.sp].spacing;
                    bool ok = true;
                    int cx = cell(p.x), cz = cell(p.z), span = Mathf.CeilToInt((r + 3f) / Cs);
                    for (int dx = -span; dx <= span && ok; dx++)
                    for (int dz = -span; dz <= span && ok; dz++)
                        if (grid.TryGetValue(key(cx + dx, cz + dz), out var l))
                            foreach (int qi in l)
                            {
                                var q = items[qi]; float rq = VegSpecies.Of(q.sp).Foot * q.scale;
                                float lim = (r + rq) * Mathf.Min(sp1, Rules[(int)q.sp].spacing);
                                if ((q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z) < lim * lim) { ok = false; break; }
                            }
                    if (ok)
                        foreach (var q in accepted)
                        {
                            float rq = VegSpecies.Of(q.sp).Foot * q.scale; float lim = (r + rq) * sp1;
                            if ((q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z) < lim * lim) { ok = false; break; }
                        }
                    if (ok) accepted.Add(p); else res.RejectedMembers++;
                }
                if (accepted.Count < cl.minKeep) { res.DroppedClusters++; continue; }
                clusterId++;
                foreach (var a in accepted)
                {
                    var p = a; p.cluster = clusterId;
                    items.Add(p);
                    long kk = key(cell(p.x), cell(p.z));
                    if (!grid.TryGetValue(kk, out var l)) grid[kk] = l = new List<int>();
                    l.Add(items.Count - 1);
                }
            }
            res.Clusters = clusterId;
        }

        // ------------------------------------------------------------------------------------------------ Ferne: Baumsilhouetten
        // Nur wo die Landbedeckung (ESA WorldCover) Bäume sagt (ohne Landbedeckung: Forst/Wald-Ökotope). Eine Silhouette je ~46-m-Zelle als
        // Bestandsblock; im Inneren großer Wälder reicht die Geländefarbe. Keine anderen Pflanzen jenseits des Mittelbands.
        private static void FarPass(Ctx c, ScatterResult res)
        {
            var map = c.map; var s = c.s;
            const float cell = 46f;
            int cols = Mathf.CeilToInt(map.W * EcotopeMap.Cell / cell), rows = Mathf.CeilToInt(map.H * EcotopeMap.Cell / cell);
            var rowRes = new List<VegPlacement>[rows];
            Parallel.For(0, rows, j =>
            {
                List<VegPlacement> list = null;
                for (int i = 0; i < cols; i++)
                {
                    var rnd = new Rnd(H(i, j, s.seed + 5));
                    float x = map.MinX + (i + .1f + .8f * rnd.Next()) * cell, z = map.MinZ + (j + .1f + .8f * rnd.Next()) * cell;
                    if (!map.Inside(x, z)) continue;
                    int k = map.Index(x, z);
                    float d = map.RoadDist[k]; if (d <= s.midEnd || d > s.farEnd) continue;
                    var eco = (Eco)map.Kind[k];
                    float acc;
                    switch (eco) { case Eco.Plantation: acc = .85f; break; case Eco.RavineForest: acc = .6f; break; case Eco.Woodland: acc = .5f; break; default: continue; }
                    if (map.HasLandCover && map.Lc[k] != LandCoverClass.Tree && map.Lc[k] != LandCoverClass.Mangrove) continue;
                    if (map.Building[k] || map.Slope[k] > 40f) continue;
                    float mask = .55f + .8f * EcoNoise.Fbm(x, z, 140f, 63, 2);
                    if (rnd.Next() > acc * Mathf.Clamp01(mask)) continue;
                    var sp = eco == Eco.Plantation || (eco == Eco.RavineForest && rnd.Next() < .25f) ? Sp.FarConifer : Sp.FarBroad;
                    float h = map.Height[k] - c.seaY; if (h < .8f) continue;
                    var spc = VegSpecies.Of(sp);
                    var p = new VegPlacement { x = x, z = z, y = c.t.DemY(x, z), yaw = rnd.Range(0f, 360f), scale = rnd.Range(1.0f, 1.7f) * (1f + .3f * S(1500f, 3400f, d)), sp = sp, tier = VegTier.Far,
                                               variant = (byte)rnd.Int(0, 255), tint = (byte)rnd.Int(0, spc.TintCount - 1) };
                    if (list == null) list = new List<VegPlacement>();
                    list.Add(p);
                }
                rowRes[j] = list;
            });
            var far = new List<VegPlacement>();
            foreach (var l in rowRes) if (l != null) far.AddRange(l);
            if (far.Count > s.maxFar)
            {
                // gleichmäßig ausdünnen (deterministisch über Hash der Position)
                float keep = s.maxFar / (float)far.Count;
                far = far.FindAll(p => EcoNoise.Hash01(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z), s.seed + 9) < keep);
            }
            int id = res.Clusters;
            foreach (var p0 in far) { var p = p0; p.cluster = ++id; res.Items.Add(p); }
            res.Clusters = id;
        }
    }
}
