using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Farbvarianten für die Synty-Autos.
    //
    // Befund: Alle fünf Autos (Sedan/Small/Medium/Muscle/Van) bekommen ihren Lack aus demselben Atlas PolygonCity_01_A
    // (Synty-Shader mit _Albedo_Map, je Fläche ein Farbfeld). Die UV-Dreiecke der Karosserie liegen bei x 427-434, y 931-942 px in
    // einem blauen Farbfeld (69,102,169) samt dunkleren Tönen (53,78,132), (39,51,86) ... — alle mit Farbton 220° +-8°, Sättigung >= 0,5.
    // Reifen, Kennzeichen, Lenkrad, Scheinwerfer und Innenraum treffen keine Pixel dieser Farbfamilie. Darum färbt eine Kopie des
    // Atlas, in der nur diese Farbfamilie ersetzt ist, genau den Lack um (Schattierungsverhältnis bleibt erhalten).
    // Die Materialkopie wird nur auf Autos gesetzt; alle übrigen Synty-Materialien bleiben unberührt.
    public static class CarPaint
    {
        public sealed class Colour
        {
            public string name; public Color32 color; public float weight;
            public Colour(string name, byte r, byte g, byte b, float weight) { this.name = name; color = new Color32(r, g, b, 255); this.weight = weight; }
        }

        // Verteilung wie im Straßenverkehr (Kapstadt): überwiegend Weiß/Silber/Grau/Schwarz, etwas Blau/Rot, wenige andere.
        public static readonly Colour[] Palette =
        {
            new Colour("white", 236, 236, 232, 28), new Colour("silver", 176, 180, 184, 17), new Colour("grey", 120, 124, 128, 11),
            new Colour("charcoal", 64, 68, 74, 6), new Colour("black", 30, 30, 33, 14),
            new Colour("blue", 58, 96, 170, 6), new Colour("darkblue", 30, 48, 100, 3),
            new Colour("red", 178, 34, 38, 7), new Colour("darkred", 104, 24, 30, 2),
            new Colour("green", 40, 86, 62, 2), new Colour("beige", 196, 178, 140, 2),
            new Colour("orange", 214, 110, 36, 1), new Colour("yellow", 224, 186, 44, 1),
        };

        public const byte ReferenceMax = 169;   // größter Kanal des Original-Lacks (69,102,169)

        // Lackfarbfamilie des Atlas: blau-dominant, Farbton 220° +-8°, Sättigung >= 0,5.
        public static bool IsPaint(Color32 c)
        {
            if (c.a == 0) return false;
            int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            if (max == 0 || max != c.b) return false;
            int d = max - min;
            if (d < 1 || d / (float)max < .5f) return false;
            float hue = 60f * (4f + (c.r - c.g) / (float)d);
            return Mathf.Abs(hue - 220f) <= 8f;
        }

        // Ersetzt die Lackfarbfamilie durch 'target', skaliert mit der Helligkeit des Originalpixels (Schattierung bleibt).
        public static Color32[] Recolour(Color32[] src, Color32 target, out int replaced)
        {
            var dst = new Color32[src.Length];
            replaced = 0;
            for (int i = 0; i < src.Length; i++)
            {
                Color32 c = src[i];
                if (!IsPaint(c)) { dst[i] = c; continue; }
                float k = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) / (float)ReferenceMax;
                dst[i] = new Color32(Scale(target.r, k), Scale(target.g, k), Scale(target.b, k), c.a);
                replaced++;
            }
            return dst;
        }
        private static byte Scale(byte v, float k) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * k), 0, 255);

        // Pixel der Atlas-Textur: erst exakt aus der PNG-Datei, sonst über einen RenderTexture-Blit (nicht lesbare/komprimierte Quelle).
        public static Color32[] ReadPixels(Texture2D tex, out int w, out int h)
        {
            w = tex.width; h = tex.height;
#if UNITY_EDITOR
            string path = Application.isPlaying ? null : UnityEditor.AssetDatabase.GetAssetPath(tex);
#else
            string path = null;
#endif
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (t.LoadImage(File.ReadAllBytes(path)))
                {
                    w = t.width; h = t.height;
                    var px = t.GetPixels32();
                    Object.DestroyImmediate(t);
                    return px;
                }
                Object.DestroyImmediate(t);
            }
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var res = copy.GetPixels32();
            Object.DestroyImmediate(copy);
            return res;
        }

        private static Texture AlbedoOf(Material m)
        {
            foreach (string p in new[] { "_Albedo_Map", "_BaseMap", "_MainTex" })
                if (m.HasProperty(p) && m.GetTexture(p) != null) return m.GetTexture(p);
            return m.mainTexture;
        }
        private static string AlbedoProp(Material m)
        {
            foreach (string p in new[] { "_Albedo_Map", "_BaseMap", "_MainTex" })
                if (m.HasProperty(p) && m.GetTexture(p) != null) return p;
            return "_MainTex";
        }

        // Häufigstes Material der Karosserie-Renderer aller Auto-Prefabs (= Atlas-Material der Synty-Autos).
        public static Material BaseMaterial(List<GameObject> carPrefabs)
        {
            var count = new Dictionary<Material, int>();
            foreach (var p in carPrefabs)
                foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == null || AlbedoOf(m) == null) continue;
                    count[m] = (count.TryGetValue(m, out int n) ? n : 0) + 1;
                }
            Material best = null; int bn = 0;
            foreach (var kv in count) if (kv.Value > bn) { bn = kv.Value; best = kv.Key; }
            return best;
        }

        // Baut die Varianten: je Palettenfarbe eine Atlas-Kopie (PNG-Asset) + Materialkopie (Asset, wie HouseMaterials()).
        // saveTexture(tex, name) legt die Textur als Asset ab, saveMaterial(mat) das Material.
        public static CarPaintSet Build(List<GameObject> carPrefabs, System.Func<Texture2D, string, Texture2D> saveTexture,
                                        System.Func<Material, Material> saveMaterial)
        {
            var set = new CarPaintSet();
            if (carPrefabs == null || carPrefabs.Count == 0) return set;
            Material baseMat = BaseMaterial(carPrefabs);
            if (baseMat == null) { Debug.LogWarning("Autolack: kein Atlas-Material an den Auto-Prefabs — alle Autos bleiben blau."); return set; }
            set.baseMaterial = baseMat;
            var atlas = AlbedoOf(baseMat) as Texture2D;
            int w = 0, h = 0;
            Color32[] px = atlas != null ? ReadPixels(atlas, out w, out h) : null;
            int paintPixels = 0;
            if (px != null) foreach (var c in px) if (IsPaint(c)) paintPixels++;

            var variants = new List<Material>(); var weights = new List<float>(); var names = new List<string>();
            if (px != null && paintPixels > 0)
            {
                string prop = AlbedoProp(baseMat);
                foreach (var col in Palette)
                {
                    var data = Recolour(px, col.color, out _);
                    var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = "CarPaint_" + col.name };
                    tex.SetPixels32(data); tex.Apply();
                    Texture2D saved = saveTexture(tex, "CarPaint_" + col.name);
                    var mat = new Material(baseMat) { name = "CarPaint_" + col.name };
                    mat.SetTexture(prop, saved);
                    if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", .6f);        // Lack glänzt etwas mehr als das Synty-Standardmaterial
                    variants.Add(saveMaterial(mat)); weights.Add(col.weight); names.Add(col.name);
                }
                Debug.Log($"Autolack: {variants.Count} Lackvarianten aus {atlas.name} ({paintPixels} Lack-Pixel der Farbfamilie umgefärbt).");
            }
            else
            {
                // Rückfall: die Synty-Atlas-Varianten (Materials/Alts/PolygonCity_0x_A/B/C) färben den ganzen Atlas anders.
#if UNITY_EDITOR
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets("PolygonCity_0 t:Material"))
                {
                    var m = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                    if (m != null && System.Text.RegularExpressions.Regex.IsMatch(m.name, @"^PolygonCity_0[1-4]_[ABC]$")) { variants.Add(m); weights.Add(1f); names.Add(m.name); }
                }
#endif
                Debug.LogWarning($"Autolack: keine Lack-Pixel im Atlas gefunden — Rückfall auf {variants.Count} Synty-Atlas-Varianten.");
            }
            set.variants = variants.ToArray(); set.weights = weights.ToArray(); set.names = names.ToArray();
            return set;
        }
    }
}
