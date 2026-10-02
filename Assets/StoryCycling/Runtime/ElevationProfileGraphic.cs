using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    // Höhenprofil als UI-Fläche (Radcomputer-Stil): Säulen je Abschnitt, nach Steigung eingefärbt, dunkle Oberkante, Punkt = Fahrerposition (links).
    public sealed class ElevationProfileGraphic : MaskableGraphic
    {
        private float[] heights;
        private float step = 1f;
        private static readonly Color Line = new Color(.07f, .07f, .08f), Floor = new Color(.80f, .81f, .79f);

        public void SetProfile(float[] h, float stepMetres) { heights = h; step = Mathf.Max(.01f, stepMetres); SetVerticesDirty(); }

        public static Color GradeColor(float grade)
        {
            if (grade < -.005f) return new Color(.62f, .65f, .64f);   // bergab
            if (grade < .03f) return new Color(.45f, .74f, .38f);     // flach
            if (grade < .06f) return new Color(.95f, .76f, .19f);
            if (grade < .09f) return new Color(.95f, .55f, .16f);
            return new Color(.85f, .23f, .17f);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var h = heights; if (h == null || h.Length < 2) return;
            Rect r = GetPixelAdjustedRect();
            float lo = h[0], hi = h[0];
            for (int i = 1; i < h.Length; i++) { lo = Mathf.Min(lo, h[i]); hi = Mathf.Max(hi, h[i]); }
            float range = Mathf.Max(hi - lo, 30f);                                    // flache Strecken sollen flach aussehen
            float bottom = lo - (range - (hi - lo)) * .5f - range * .12f, scale = r.height / (range * 1.25f);
            Quad(vh, new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMin + 1f), new Vector2(r.xMax, r.yMin + 1f), new Vector2(r.xMax, r.yMin), Floor);
            int n = h.Length - 1;
            for (int i = 0; i < n; i++)
            {
                float x0 = r.xMin + r.width * i / n, x1 = r.xMin + r.width * (i + 1) / n;
                float y0 = r.yMin + (h[i] - bottom) * scale, y1 = r.yMin + (h[i + 1] - bottom) * scale;
                Quad(vh, new Vector2(x0, r.yMin), new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x1, r.yMin), GradeColor((h[i + 1] - h[i]) / step));
                Quad(vh, new Vector2(x0, y0 - 1.2f), new Vector2(x0, y0 + 1.2f), new Vector2(x1, y1 + 1.2f), new Vector2(x1, y1 - 1.2f), Line);
            }
            float yc = r.yMin + (h[0] - bottom) * scale;
            Quad(vh, new Vector2(r.xMin, yc - 5f), new Vector2(r.xMin, yc + 5f), new Vector2(r.xMin + 10f, yc + 5f), new Vector2(r.xMin + 10f, yc - 5f), Line);
        }

        private static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, col, Vector4.zero); vh.AddVert(b, col, Vector4.zero); vh.AddVert(c, col, Vector4.zero); vh.AddVert(d, col, Vector4.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
