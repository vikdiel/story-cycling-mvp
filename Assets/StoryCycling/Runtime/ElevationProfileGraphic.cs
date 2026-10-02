using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    // Höhenprofil im Radcomputer: eine Säule (UI-Image) je Abschnitt, nach Steigung eingefärbt, dunkle Kappe als Profillinie, Marke = Fahrerposition (links).
    // Bewusst aus Standard-Images statt eigenem UI-Mesh gebaut (rendert überall gleich zuverlässig wie der Rest der Anzeige).
    public sealed class ElevationProfileGraphic : MonoBehaviour
    {
        private RectTransform[] cols, caps; private Image[] colImg;
        private RectTransform marker;
        private float width, height, colW;
        private static readonly Color Line = new Color(.07f, .07f, .08f), Floor = new Color(.62f, .63f, .61f);

        public static Color GradeColor(float grade)
        {
            if (grade < -.005f) return new Color(.62f, .65f, .64f);   // bergab
            if (grade < .03f) return new Color(.45f, .74f, .38f);     // flach
            if (grade < .06f) return new Color(.95f, .76f, .19f);
            if (grade < .09f) return new Color(.95f, .55f, .16f);
            return new Color(.85f, .23f, .17f);
        }

        // Fläche = eigenes RectTransform (Anker/Pivot unten links oder oben links, feste Größe)
        public void Build(int segments)
        {
            var area = (RectTransform)transform;
            width = area.sizeDelta.x; height = area.sizeDelta.y; colW = width / segments;
            Bar("Grundlinie", 0f, 0f, width, 1.5f, Floor);
            cols = new RectTransform[segments]; caps = new RectTransform[segments]; colImg = new Image[segments];
            for (int i = 0; i < segments; i++)
            {
                cols[i] = Bar("Saeule " + i, i * colW, 0f, colW + .6f, 0f, GradeColor(0f)); colImg[i] = cols[i].GetComponent<Image>();
                caps[i] = Bar("Kante " + i, i * colW, 0f, colW + .6f, 3f, Line);
            }
            marker = Bar("Position", -3f, 0f, 9f, 9f, Line);
        }

        public void SetProfile(float[] h, float step)
        {
            if (cols == null || h == null || h.Length < cols.Length + 1) return;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < h.Length; i++) { if (float.IsNaN(h[i])) continue; lo = Mathf.Min(lo, h[i]); hi = Mathf.Max(hi, h[i]); }
            if (lo > hi) return;
            float range = Mathf.Max(hi - lo, 30f);                                    // flache Strecken sollen flach aussehen
            float bottom = lo - (range - (hi - lo)) * .5f - range * .12f, scale = height / (range * 1.25f);
            for (int i = 0; i < cols.Length; i++)
            {
                float a = float.IsNaN(h[i]) ? lo : h[i], b = float.IsNaN(h[i + 1]) ? a : h[i + 1];
                float y = Mathf.Clamp(((a + b) * .5f - bottom) * scale, 2f, height);
                cols[i].sizeDelta = new Vector2(colW + .6f, y);
                colImg[i].color = GradeColor((b - a) / Mathf.Max(.01f, step));
                caps[i].anchoredPosition = new Vector2(i * colW, y - 1.5f);
            }
            float y0 = Mathf.Clamp((h[0] - bottom) * scale, 2f, height);
            marker.anchoredPosition = new Vector2(-3f, y0 - 4.5f);
        }

        private RectTransform Bar(string name, float x, float y, float w, float hgt, Color c)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(transform, false); r.anchorMin = r.anchorMax = r.pivot = Vector2.zero; r.anchoredPosition = new Vector2(x, y); r.sizeDelta = new Vector2(w, hgt);
            var img = r.gameObject.AddComponent<Image>(); img.color = c; img.raycastTarget = false;
            return r;
        }
    }
}
