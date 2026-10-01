using System;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Lackvarianten der Synty-Autos: Materialkopien, deren Atlas nur an der Karosserie-Lackfläche umgefärbt ist
    // (CarPaint im Editor baut sie). Der Generator setzt sie bei parkenden Autos, TrafficSystem bei fahrenden.
    [Serializable]
    public sealed class CarPaintSet
    {
        [Tooltip("Originalmaterial der Synty-Autos (Atlas PolygonCity_01_A): Renderer mit diesem Material bekommen die Lackvariante.")]
        public Material baseMaterial;
        public Material[] variants = new Material[0];
        [Tooltip("Relative Häufigkeit je Variante (gleiche Reihenfolge wie 'variants').")]
        public float[] weights = new float[0];
        public string[] names = new string[0];

        public bool Usable => baseMaterial != null && variants != null && variants.Length > 0;

        // u in [0,1): Variante nach Gewicht.
        public int Pick(float u)
        {
            if (variants == null || variants.Length == 0) return -1;
            float sum = 0f;
            for (int i = 0; i < variants.Length; i++) sum += Weight(i);
            float t = Mathf.Clamp01(u) * sum, acc = 0f;
            for (int i = 0; i < variants.Length; i++)
            {
                acc += Weight(i);
                if (t < acc) return i;
            }
            return variants.Length - 1;
        }

        private float Weight(int i) => weights != null && i < weights.Length ? Mathf.Max(0f, weights[i]) : 1f;

        // Setzt 'variant' auf alle Renderer mit dem Originalmaterial (Glas, Reifen-Material usw. bleiben). Keine Allokation.
        public void Apply(Renderer[] renderers, int variantIndex)
        {
            if (!Usable || variantIndex < 0 || variantIndex >= variants.Length || variants[variantIndex] == null) return;
            var mat = variants[variantIndex];
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].sharedMaterial == baseMaterial) renderers[i].sharedMaterial = mat;
        }

        // Variante, die ein Renderer gerade trägt (oder -1 = Originalmaterial/fremd).
        public int IndexOf(Material m)
        {
            if (variants == null) return -1;
            for (int i = 0; i < variants.Length; i++) if (variants[i] == m) return i;
            return -1;
        }
    }
}
