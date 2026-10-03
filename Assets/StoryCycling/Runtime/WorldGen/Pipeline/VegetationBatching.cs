using System;
using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Gruppiert Pflanzstellen zu GPU-Instancing-Batches: je (Band, Art, Prefab-Variante, Farbton, Raumzelle) ein Batch.
    // Die Zahl der Batches bestimmt auf dem iPad die Draw-Calls (je Prefab-Teil ein Aufruf je 1023 Instanzen) — deshalb sind
    // Varianten je Art und Farbtöne je Art begrenzt (VegSpecies.MaxVariants / Tints) und die Raumzellen groß.
    // Reine Logik ohne Unity-Assets, damit der Test-Harness dieselbe Aufteilung berechnet wie der Builder.
    public sealed class VegBatch
    {
        public VegTier tier; public Sp sp; public int variant, tint, bx, bz;
        public readonly List<int> items = new List<int>();
        public Bounds bounds;
    }

    public static class VegetationBatching
    {
        public static float BucketSize(VegTier t) => t == VegTier.Near ? 160f : t == VegTier.Mid ? 400f : 900f;

        // variantOf(art, zufallsbyte) -> Variantenindex; tintable(art, variante) -> Farbton wirkt (nur Generic-Materialien)
        public static List<VegBatch> Plan(IList<VegPlacement> items, Func<Sp, byte, int> variantOf, Func<Sp, int, bool> tintable)
        {
            var map = new Dictionary<(int, int, int, int, int, int), VegBatch>();
            var list = new List<VegBatch>();
            for (int i = 0; i < items.Count; i++)
            {
                var p = items[i];
                var spc = VegSpecies.Of(p.sp);
                int v = variantOf(p.sp, p.variant);
                int tint = tintable(p.sp, v) && spc.TintCount > 1 ? p.tint % spc.TintCount : 0;
                float bs = BucketSize(p.tier);
                int bx = Mathf.FloorToInt(p.x / bs), bz = Mathf.FloorToInt(p.z / bs);
                var key = ((int)p.tier, (int)p.sp, v, tint, bx, bz);
                if (!map.TryGetValue(key, out var b))
                {
                    b = new VegBatch { tier = p.tier, sp = p.sp, variant = v, tint = tint, bx = bx, bz = bz };
                    map[key] = b; list.Add(b);
                }
                float ext = Mathf.Max(6f, spc.Height * p.scale);
                var bb = new Bounds(new Vector3(p.x, p.y + ext * .5f, p.z), new Vector3(ext * 1.2f, ext, ext * 1.2f));
                if (b.items.Count == 0) b.bounds = bb; else b.bounds.Encapsulate(bb);
                b.items.Add(i);
            }
            return list;
        }
    }
}
