using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Eigenes, deterministisches Rauschen (Hash-basiertes Value-Noise) für Ökotope, Geländefarbe und Vegetation.
    // Bewusst NICHT Mathf.PerlinNoise: gleiche Ergebnisse in Editor, Tests und auf jeder Plattform.
    public static class EcoNoise
    {
        public static uint Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)seed * 0xC2B2AE3Du;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return h;
            }
        }

        public static float Hash01(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216f;

        // Glattes Value-Noise 0..1 (Periode 1 = eine Gitterzelle).
        public static float Value(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = Hash01(ix, iy, seed), b = Hash01(ix + 1, iy, seed), c = Hash01(ix, iy + 1, seed), d = Hash01(ix + 1, iy + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // Mehroktavig (fBm), 0..1, Mittelwert ~0,5. scale = Wellenlänge in Metern.
        public static float Fbm(float x, float z, float scale, int seed, int octaves = 3)
        {
            float sum = 0f, amp = 1f, norm = 0f, f = 1f / scale;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(x * f + o * 17.3f, z * f - o * 9.1f, seed + o * 101) * amp;
                norm += amp; amp *= .5f; f *= 2.03f;
            }
            return sum / norm;
        }

        // Anisotropes fBm (0..1): Muster in die Richtung angleDeg (Grad, 0 = Nord/+z, 90 = Ost/+x) gestreckt — Wellenlänge `along` in
        // dieser Richtung, `across` quer dazu. Die Richtung wird auf 8 feste Richtungen (22,5°) quantisiert und zwischen den beiden
        // nächsten weich gemischt: ändert sich die Hangrichtung langsam, ändert sich das Muster stetig (kein Rauschen durch Drehen
        // um einen fernen Ursprung). Das Muster ist 180°-periodisch: Richtung und Gegenrichtung ergeben dasselbe.
        public static float Aniso(float x, float z, float angleDeg, float along, float across, int seed, int octaves = 2)
        {
            float a = angleDeg % 180f; if (a < 0f) a += 180f;
            float f = a / 22.5f; int i0 = Mathf.FloorToInt(f) & 7, i1 = (i0 + 1) & 7; float t = f - Mathf.Floor(f);
            t = t * t * (3f - 2f * t);
            float v0 = AnisoFixed(x, z, i0 * 22.5f, along, across, seed, octaves);
            if (t < .001f) return v0;
            float v1 = AnisoFixed(x, z, i1 * 22.5f, along, across, seed, octaves);
            // Mischen zweier unabhängiger Felder senkt die Streuung (am stärksten bei t = 0,5): wieder auf volle Streuung bringen
            float k = 1f / Mathf.Sqrt((1f - t) * (1f - t) + t * t);
            return Mathf.Clamp01(.5f + (Mathf.Lerp(v0, v1, t) - .5f) * k);
        }

        // Wie Aniso, aber auf (fast) ebenem Gelände ist die Höhenlinienrichtung nicht definiert (zufällige Winkel -> rautenförmige Flecken):
        // dort gleitend in isotropes Rauschen gleicher Flächenwellenlänge überblenden.
        public static float AnisoSlope(float x, float z, float angleDeg, float slopeDeg, float along, float across, int seed, int octaves = 2)
        {
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2.5f, 10f, slopeDeg));
            if (w >= .999f) return Aniso(x, z, angleDeg, along, across, seed, octaves);
            float iso = Fbm(x, z, Mathf.Sqrt(along * across) * 1.1f, seed, octaves);
            if (w <= .001f) return iso;
            return Mathf.Lerp(iso, Aniso(x, z, angleDeg, along, across, seed, octaves), w);
        }

        private static float AnisoFixed(float x, float z, float angleDeg, float along, float across, int seed, int octaves)
        {
            float r = angleDeg * Mathf.Deg2Rad, ux = Mathf.Sin(r), uz = Mathf.Cos(r);    // Einheitsvektor entlang der Streckung
            float u = x * ux + z * uz, v = -x * uz + z * ux;
            float sum = 0f, amp = 1f, norm = 0f, fu = 1f / along, fv = 1f / across;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(u * fu + o * 17.3f, v * fv - o * 9.1f, seed + o * 101) * amp;
                norm += amp; amp *= .5f; fu *= 2.03f; fv *= 2.03f;
            }
            return sum / norm;
        }

        // Erhöht den Kontrast um 0,5 (macht aus fBm-Werten deutlichere Flecken).
        public static float Contrast(float v, float k) => Mathf.Clamp01(.5f + (v - .5f) * k);
    }
}
