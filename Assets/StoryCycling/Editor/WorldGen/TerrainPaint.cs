using System.Threading.Tasks;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Geländefarbe aus Ökotop + Neigung + Höhe + Hangrichtung + Entwässerung + mehrskaligem Rauschen.
    // Kap-Palette nach Fotos (Zwölf Apostel, Chapman's Peak, Hout Bay): olivgrauer Fynbos mit ockerfarbenen/rostroten Flecken,
    // Nordhänge (Südhalbkugel: mehr Sonne) trockener/ockerfarben, Südhänge, Rinnen und Bachläufe grüner und dunkler,
    // Sandsteinbänder mit warmen Schichten und dunklen Wasserstreifen, heller Granit an der Küste, Straßeneinschnitte als nackte Erde.
    // Flecken folgen den Höhenlinien (anisotrope Rauschfelder), nicht runden "Tarnmustern".
    public static class TerrainPaint
    {
        private static readonly Color Sand = new Color(.88f, .81f, .64f), SandWet = new Color(.70f, .64f, .50f), Seabed = new Color(.18f, .40f, .40f);
        private static readonly Color FynGreen = new Color(.30f, .38f, .22f), FynGrey = new Color(.40f, .43f, .34f), FynOchre = new Color(.52f, .47f, .29f),
                                      FynStraw = new Color(.62f, .55f, .36f), FynRust = new Color(.45f, .32f, .22f), FynDark = new Color(.21f, .30f, .18f);
        private static readonly Color RockLight = new Color(.58f, .53f, .45f), RockWarm = new Color(.55f, .40f, .29f), RockDark = new Color(.33f, .31f, .28f);
        private static readonly Color Granite = new Color(.68f, .66f, .62f), GraniteDark = new Color(.43f, .41f, .39f), Lichen = new Color(.72f, .57f, .31f);
        private static readonly Color Forest = new Color(.13f, .24f, .14f), ForestLight = new Color(.21f, .32f, .17f);
        private static readonly Color Pine = new Color(.10f, .19f, .12f), PineLight = new Color(.15f, .26f, .15f), Clearfell = new Color(.46f, .41f, .28f);
        private static readonly Color Wood = new Color(.23f, .34f, .18f), WoodDry = new Color(.36f, .40f, .22f);
        private static readonly Color Lawn = new Color(.42f, .54f, .29f), Urban = new Color(.54f, .54f, .49f);
        private static readonly Color Pasture = new Color(.47f, .58f, .29f), PastureDry = new Color(.69f, .63f, .36f);
        private static readonly Color VleiGreen = new Color(.36f, .46f, .27f), VleiReed = new Color(.54f, .49f, .30f), VleiWater = new Color(.20f, .31f, .31f);
        private static readonly Color DuneOlive = new Color(.46f, .47f, .29f), DuneSand = new Color(.80f, .72f, .54f);
        private static readonly Color CutEarth = new Color(.46f, .33f, .24f), CutRock = new Color(.52f, .44f, .35f);

        public static Texture2D Build(EcotopeMap m)
        {
            int W = m.W, H = m.H;
            var col = new Color[W * H];
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++) col[j * W + i] = Paint(m, i, j);
            });
            // Weiche Übergänge: an Ökotop-Grenzen mit dem 5x5-Mittel mischen (Innenflächen behalten ihr Rauschen)
            var px = new Color32[W * H];
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    int k = j * W + i; Color c = col[k];
                    if (i >= 2 && j >= 2 && i < W - 2 && j < H - 2 && m.Kind[k] != (byte)Eco.Water)
                    {
                        int diff = 0; float r = 0, g = 0, b = 0, ws = 0;
                        for (int dj = -2; dj <= 2; dj++)
                        for (int di = -2; di <= 2; di++)
                        {
                            int kk = (j + dj) * W + i + di;
                            if (m.Kind[kk] == (byte)Eco.Water) continue;
                            float w = 1f / (1f + di * di + dj * dj);
                            if (m.Kind[kk] != m.Kind[k]) diff++;
                            r += col[kk].r * w; g += col[kk].g * w; b += col[kk].b * w; ws += w;
                        }
                        if (diff > 0 && ws > 0f) c = Color.Lerp(c, new Color(r / ws, g / ws, b / ws, 1f), Mathf.Clamp01(diff / 14f) * .9f);
                    }
                    px[k] = c;
                }
            });
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "TerrainColors", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // Farbe einer Zelle (öffentlich für Tests/Vorschau).
        public static Color Paint(EcotopeMap m, int i, int j)
        {
            int k = j * m.W + i;
            float x = m.MinX + (i + .5f) * EcotopeMap.Cell, z = m.MinZ + (j + .5f) * EcotopeMap.Cell;
            float h = m.Height[k], above = h - m.SeaY, slope = m.Slope[k], aspect = m.Aspect[k], ravine = m.Ravine[k];
            var eco = (Eco)m.Kind[k];

            // Mehrskaliges Rauschen: Großflächen (Hangzonen), mittlere Flecken entlang der Höhenlinien, Kleinstflecken
            float nL = EcoNoise.Fbm(x, z, 900f, 21, 3), nM = EcoNoise.Fbm(x, z, 170f, 22, 3), nS = EcoNoise.Fbm(x, z, 42f, 23, 2), nXS = EcoNoise.Fbm(x, z, 13f, 24, 2);
            float north = Mathf.Cos(aspect * Mathf.Deg2Rad);                          // +1 = Nordhang (sonnig)
            float sf = Mathf.Clamp01(slope / 18f);
            Color c;

            switch (eco)
            {
                case Eco.Water:
                    c = Color.Lerp(SandWet, Seabed, Mathf.InverseLerp(-.3f, -8f, above));
                    break;
                case Eco.Beach:
                    c = Color.Lerp(SandWet, Sand, Mathf.InverseLerp(0f, 2.5f, above));
                    c = Color.Lerp(c, Color.Lerp(Sand, DuneSand, .6f), nS * .5f);
                    break;
                case Eco.DuneScrub:
                {
                    float cover = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.38f, .66f, nS * .6f + nM * .4f + (above < 6f ? -.15f : 0f)));
                    c = Color.Lerp(DuneSand, DuneOlive, cover);
                    c = Color.Lerp(c, FynDark, Mathf.SmoothStep(0f, .5f, Mathf.InverseLerp(.62f, .85f, nXS)) * cover);
                    break;
                }
                case Eco.CoastRock:
                    c = Color.Lerp(Granite, GraniteDark, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.3f, .8f, nS)) * .7f);
                    c = Color.Lerp(c, Lichen, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.7f, .9f, nXS)) * .35f);
                    c = Color.Lerp(c, DuneOlive, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.55f, .8f, nM)) * (1f - sf) * .5f);
                    break;
                case Eco.Sandstone:
                {
                    c = Sandstone(x, z, h, slope, nL, nM);
                    // Kluftvegetation: auf weniger steilen Bändern wächst Fynbos in den Spalten
                    var f = Fields(m, k, x, z);
                    c = Color.Lerp(c, FynbosColor(f), Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(48f, 34f, slope)) * .55f * nS);
                    break;
                }
                case Eco.RavineForest:
                    c = Color.Lerp(Forest, ForestLight, Mathf.SmoothStep(0f, 1f, nM * .6f + nS * .4f));
                    c = Color.Lerp(c, FynDark, (1f - ravine) * .25f);
                    break;
                case Eco.Plantation:
                {
                    float age = Mathf.SmoothStep(0f, 1f, EcoNoise.Fbm(x, z, 160f, 31, 2));        // Bestandsalter: Schattierung je Abteilung
                    c = Color.Lerp(Pine, PineLight, age * .8f + nXS * .2f);
                    float clear = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.80f, .86f, EcoNoise.Fbm(x, z, 260f, 32, 2)));   // Kahlschlag
                    c = Color.Lerp(c, Clearfell, clear * .8f);
                    break;
                }
                case Eco.Woodland:
                    c = Color.Lerp(Wood, WoodDry, Mathf.SmoothStep(0f, 1f, nM) * .6f + north * sf * .15f);
                    break;
                case Eco.Urban:
                    c = Color.Lerp(Urban, Lawn, nS * .35f);
                    break;
                case Eco.Garden:
                    c = Color.Lerp(Lawn, Urban, .2f + nS * .35f);
                    c = Color.Lerp(c, Wood, Mathf.SmoothStep(0f, 1f, nM) * .35f);
                    break;
                case Eco.Field:
                {
                    float dry = Mathf.Clamp01(.35f + (nL - .5f) * 1.1f + north * sf * .3f + Mathf.InverseLerp(.4f, .7f, nM) * .3f);
                    c = Color.Lerp(Pasture, PastureDry, Mathf.SmoothStep(0f, 1f, dry));
                    c = Color.Lerp(c, FynGreen, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.6f, .85f, nS)) * .4f);
                    break;
                }
                case Eco.Vlei:
                    c = Color.Lerp(VleiGreen, VleiReed, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.4f, .75f, nM)));
                    c = Color.Lerp(c, VleiWater, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.72f, .85f, nS)) * .6f);
                    break;
                case Eco.Restio:
                {
                    // Grasiger Fynbos / Brandflächen: strohfarben mit grünen Inseln und ockerfarbenen Streifen
                    var f = Fields(m, k, x, z);
                    f.straw = Mathf.Clamp01(f.straw * .5f + .45f + (f.dry - .5f) * .5f);
                    f.thicket *= .5f;
                    c = FynbosColor(f);
                    break;
                }
                default:
                {
                    var f = Fields(m, k, x, z);
                    c = FynbosColor(f);
                    // Felsaufschlüsse auf steileren Hängen (Fynbos geht in Sandstein über), entlang der Höhenlinien gebändert
                    float band = EcoNoise.AnisoSlope(x, z, m.Contour[k], slope, 180f, 40f, 51, 2);
                    float outcrop = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 38f, slope)) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.38f, .66f, band * .75f + nS * .25f));
                    if (outcrop > 0f) c = Color.Lerp(c, Sandstone(x, z, h, slope, nL, nM), outcrop * .85f);
                    break;
                }
            }

            // Straßeneinschnitt/-damm: nackte Erde bzw. Fels am Böschungsfuß (kurz vor der Route, nur wo das Gelände wirklich verändert wurde)
            float cf = Mathf.Abs(m.CutFill[k]);
            if (cf > 1.2f && eco != Eco.Water && eco != Eco.Urban)
            {
                float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.2f, 5f, cf)) * (m.CutFill[k] < 0f ? .62f : .35f);
                Color cutCol = Color.Lerp(CutEarth, CutRock, Mathf.SmoothStep(0f, 1f, nS) * Mathf.Clamp01(slope / 30f + .2f));
                c = Color.Lerp(c, cutCol, w);
            }

            // Senken (Kloofs) dunkler, Kleinrauschen gegen Flächigkeit
            if (eco != Eco.Water && eco != Eco.Beach)
            {
                float shade = 1f - .16f * ravine;
                float grain = (EcoNoise.Hash01(i, j, 91) - .5f) * .07f;                 // Korn pro Texel (unter der Abtastgrenze der Rauschfelder)
                float v = (.90f + .20f * nXS + grain) * shade;
                c = new Color(c.r * v, c.g * v, c.b * v, 1f);
            }
            c.a = 1f;
            return c;
        }

        // Detailtextur für das Gelände (URP Lit, Detail-Albedo, Modus MulX2): neutrales Mittelgrau mit mehrskaligem, kachelbarem Rauschen.
        // Über der 10-m-Farbtextur liegt so Bodenkorn/Fleckung im Nahbereich; mit den Mips verschwindet sie in der Ferne zu neutral (Faktor 1),
        // der Mittelwert ist im LINEAREN Raum auf Faktor 1 normiert (sonst würde das Gelände mit der Entfernung heller/dunkler).
        public static Texture2D BuildDetail(int n = 256, float strength = .75f)
        {
            var lum = new float[n * n]; var warm = new float[n * n];
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float u = (i + .5f) / n, v = (j + .5f) / n;
                float a = PNoise(u, v, 4, 201) - .5f, b = PNoise(u, v, 8, 202) - .5f, c = PNoise(u, v, 16, 203) - .5f, d = PNoise(u, v, 32, 204) - .5f;
                float g = EcoNoise.Hash01(i, j, 205) - .5f;
                lum[j * n + i] = (a * .55f + b * .75f + c * .65f + d * .5f + g * .55f) * strength;
                warm[j * n + i] = PNoise(u, v, 6, 206) - .5f;
            }
            const float mid = 0.2158f;                                   // 128/255 sRGB in linear = Faktor 1 bei X2 (unity_ColorSpaceDouble)
            var lin = new float[n * n * 3];
            double[] sum = new double[3];
            for (int k = 0; k < n * n; k++)
            {
                float l = mid * Mathf.Clamp(1f + .75f * lum[k], .6f, 1.5f);
                float w = warm[k] * .12f * strength;
                lin[k * 3] = Mathf.Max(.002f, l * (1f + w)); lin[k * 3 + 1] = Mathf.Max(.002f, l); lin[k * 3 + 2] = Mathf.Max(.002f, l * (1f - w));
                for (int q = 0; q < 3; q++) sum[q] += lin[k * 3 + q];
            }
            var px = new Color32[n * n];
            float[] norm = { mid / (float)(sum[0] / (n * n)), mid / (float)(sum[1] / (n * n)), mid / (float)(sum[2] / (n * n)) };
            for (int k = 0; k < n * n; k++)
            {
                byte R(int q) => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(Mathf.Clamp01(lin[k * 3 + q] * norm[q]), 1f / 2.2f) * 255f), 0, 255);
                px[k] = new Color32(R(0), R(1), R(2), 255);
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "TerrainDetail", wrapMode = TextureWrapMode.Repeat };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // Kachelbares Value-Noise (Periode = Zellenzahl je Textur), u,v in 0..1
        private static float PNoise(float u, float v, int period, int seed)
        {
            float x = u * period, y = v * period;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            int x0 = ix % period, y0 = iy % period, x1 = (ix + 1) % period, y1 = (iy + 1) % period;
            float a = EcoNoise.Hash01(x0, y0, seed), b = EcoNoise.Hash01(x1, y0, seed), c = EcoNoise.Hash01(x0, y1, seed), d = EcoNoise.Hash01(x1, y1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // Felder, die die Fynbos-Farbe steuern. Die Vegetation (VegetationScatter) liest dieselben Felder, damit Büsche dort wachsen,
        // wo die Geländefarbe Dickicht (dunkel), Restio (strohfarben) oder Erika (rostbraun) zeigt.
        public struct FynFields { public float dry, thicket, straw, rust, grey; }

        public static FynFields FynbosFieldsAt(EcotopeMap m, int k, float x, float z) => Fields(m, k, x, z);

        // Farbe aus den Feldern (öffentlich: die Vegetation wählt Farbtöne passend zum Boden darunter)
        public static Color FynbosColor(FynFields f)
        {
            Color c = Color.Lerp(FynGreen, FynGrey, f.grey);
            c = Color.Lerp(c, FynOchre, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.42f, .85f, f.dry)));
            c = Color.Lerp(c, FynStraw, f.straw);
            c = Color.Lerp(c, FynRust, f.rust);
            return Color.Lerp(c, FynDark, f.thicket);
        }

        private static FynFields Fields(EcotopeMap m, int k, float x, float z)
        {
            float aspect = m.Aspect[k], slope = m.Slope[k], ravine = m.Ravine[k], wet = m.Wet[k], above = m.Height[k] - m.SeaY, contour = m.Contour[k];
            float north = Mathf.Cos(aspect * Mathf.Deg2Rad), sf = Mathf.Clamp01(slope / 18f);
            float nL = EcoNoise.Fbm(x, z, 900f, 21, 3);
            float nM = EcoNoise.AnisoSlope(x, z, contour, slope, 230f, 120f, 22, 3);          // Flecken ~230 x 120 m, schwach entlang der Höhenlinien gestreckt
            float nS = EcoNoise.AnisoSlope(x, z, contour, slope, 90f, 48f, 23, 2);
            float nXS = EcoNoise.Fbm(x, z, 16f, 24, 2);
            // Trockenheit: Nordhänge (Sonne) und Kuppen ocker, Südhänge/Rinnen grün; dazu großflächige Muster
            var f = new FynFields();
            f.dry = Mathf.Clamp01(.38f + .30f * north * sf + (nL - .5f) * .80f + (nM - .5f) * .55f + Mathf.Clamp01(above / 700f) * .08f - ravine * .20f - wet * .35f);
            f.grey = Mathf.SmoothStep(0f, 1f, nM) * .75f;
            f.straw = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.60f, .86f, nS * .55f + f.dry * .45f)) * .5f;                                       // Restio-Flächen
            f.rust = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.66f, .84f, nM * .5f + nXS * .5f)) * .4f * Mathf.Clamp01(f.dry + .2f);                  // Erika/Rostbraun
            f.thicket = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.50f, .92f, wet * .9f + ravine * .5f + (1f - north) * .2f * sf + (nM - .5f) * .5f)) * .65f;   // Protea-Dickicht
            return f;
        }

        private static Color Sandstone(float x, float z, float h, float slope, float nL, float nM)
        {
            // Horizontale Schichten (Sandstein) + senkrechte dunkle Wasserstreifen
            float layer = .5f + .5f * Mathf.Sin((h / 15f + nL * 3f) * 6.2831853f);
            Color c = Color.Lerp(RockLight, RockWarm, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.35f, .8f, layer)) * .85f);
            float streak = EcoNoise.Fbm(x * .35f + z * .9f, h * 2.6f, 60f, 41, 2);       // in Höhe gestreckt: senkrechte Streifen
            c = Color.Lerp(c, RockDark, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.58f, .82f, streak)) * .7f);
            return Color.Lerp(c, RockLight, Mathf.Clamp01(1f - slope / 55f) * .25f);
        }
    }
}
