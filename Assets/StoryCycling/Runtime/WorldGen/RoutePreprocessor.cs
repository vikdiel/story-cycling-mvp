using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Cleans a raw GPS polyline before it becomes a spline:
    //   1) drops points closer than minSpacing (GPS jitter / Strava-densified points)
    //   2) resamples to uniform arc-length spacing (stable spline + even sampling)
    //   3) moving-average smooths elevation (Y) to remove barometric noise / bumpy grade
    //   4) reconciles overlapping passes (out-and-back): the return uses the SAME centreline and
    //      height as the outbound pass — one road, lanes by direction of travel
    // XZ corners are preserved — centripetal RouteSpline handles them without overshoot.
    public static class RoutePreprocessor
    {
        public static List<Vector3> Clean(List<Vector3> pts, float minSpacing = 3f,
                                          float resampleStep = 8f, int elevationWindow = 5)
        {
            if (pts == null || pts.Count < 2) return pts ?? new List<Vector3>();
            var dedup = Dedup(pts, minSpacing);
            var resampled = ResampleByArcLength(dedup, resampleStep);
            SmoothElevation(resampled, elevationWindow);
            if (ReconcileOverlaps(resampled) > 0) resampled = Dedup(resampled, 1f);   // Projektion kann Punkte zusammenschieben
            return resampled;
        }

        // Remove points closer than minSpacing to the last kept point.
        private static List<Vector3> Dedup(List<Vector3> pts, float minSpacing)
        {
            var outp = new List<Vector3> { pts[0] };
            float sq = minSpacing * minSpacing;
            for (int i = 1; i < pts.Count; i++)
                if ((pts[i] - outp[outp.Count - 1]).sqrMagnitude >= sq) outp.Add(pts[i]);
            if (outp.Count < 2) outp.Add(pts[pts.Count - 1]);
            return outp;
        }

        // Walk the polyline and emit a point every 'step' metres by linear interpolation.
        private static List<Vector3> ResampleByArcLength(List<Vector3> pts, float step)
        {
            var outp = new List<Vector3> { pts[0] };
            Vector3 prev = pts[0];
            float acc = 0f;
            int i = 1;
            while (i < pts.Count)
            {
                Vector3 cur = pts[i];
                float segLen = Vector3.Distance(prev, cur);
                if (segLen < 1e-5f) { i++; continue; }
                if (acc + segLen >= step)
                {
                    float t = (step - acc) / segLen;
                    Vector3 p = Vector3.Lerp(prev, cur, t);
                    outp.Add(p);
                    prev = p;      // continue emitting from the new point
                    acc = 0f;
                }
                else
                {
                    acc += segLen;
                    prev = cur;
                    i++;
                }
            }
            // keep the true end point
            if ((outp[outp.Count - 1] - pts[pts.Count - 1]).sqrMagnitude > 0.04f)
                outp.Add(pts[pts.Count - 1]);
            return outp;
        }

        // Later passes over the same road take the height of the earlier pass; the correction
        // fades out over 'blend' metres before/after the shared section (no steps).
        // Spätere Durchfahrten derselben Straße übernehmen Lage UND Höhe der ersten Durchfahrt
        // (Projektion auf deren Polylinie). Hin- und Rückweg teilen sich so exakt EINE Mittellinie;
        // die Fahrspur ergibt sich aus der Fahrtrichtung. Übergänge laufen über 'blend' Meter weich aus.
        public static int ReconcileOverlaps(List<Vector3> pts, float radius = 8.5f, float minArcGap = 100f, float blend = 60f, float maxDy = 3f)
        {
            int n = pts.Count;
            if (n < 3) return 0;
            var arc = new float[n];
            for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + Vector2.Distance(new Vector2(pts[i - 1].x, pts[i - 1].z), new Vector2(pts[i].x, pts[i].z));
            var src = new List<Vector3>(pts);                    // Originale (Referenz der ersten Durchfahrt)
            var outP = new List<Vector3>(pts);
            var delta = new Vector3[n];
            var matched = new bool[n];

            float cell = radius * 2f;
            var grid = new Dictionary<long, List<int>>();
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = src[i];
                Vector2 ti = Dir(src, i);
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
                int best = -1; float bestD = radius * radius;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(Key(cx + dx, cz + dz), out List<int> list)) continue;
                    foreach (int j in list)
                    {
                        if (arc[i] - arc[j] < minArcGap) continue;                         // derselbe Abschnitt
                        if (Mathf.Abs(Vector2.Dot(ti, Dir(src, j))) < .8f) continue;       // Kreuzung, nicht parallel
                        float d2 = (outP[j].x - p.x) * (outP[j].x - p.x) + (outP[j].z - p.z) * (outP[j].z - p.z);
                        // Direkt übereinander (< 4 m) = dieselbe Straße; weiter daneben nur bei kleiner Höhendifferenz
                        float dy = Mathf.Abs(outP[j].y - p.y);
                        if (dy > (d2 < 16f ? 8f : maxDy)) continue;
                        if (d2 < bestD) { bestD = d2; best = j; }
                    }
                }
                if (best >= 0)
                {
                    // auf die (bereits bereinigte) frühere Durchfahrt projizieren
                    Vector3 q = ProjectOnto(outP, best, p);
                    delta[i] = q - p; outP[i] = q; matched[i] = true; count++;
                }
                long key = Key(cx, cz);
                if (!grid.TryGetValue(key, out List<int> l)) { l = new List<int>(); grid[key] = l; }
                l.Add(i);
            }
            if (count == 0) return 0;

            // Übergänge: Verschiebung vor/nach gemeinsamen Abschnitten weich auslaufen lassen.
            for (int i = 0; i < n; i++)
            {
                if (matched[i]) { pts[i] = outP[i]; continue; }
                float w = 0f; Vector3 d = Vector3.zero;
                for (int k = i - 1; k >= 0 && arc[i] - arc[k] < blend; k--)
                    if (matched[k]) { float t = 1f - (arc[i] - arc[k]) / blend; if (t > w) { w = t; d = delta[k]; } break; }
                for (int k = i + 1; k < n && arc[k] - arc[i] < blend; k++)
                    if (matched[k]) { float t = 1f - (arc[k] - arc[i]) / blend; if (t > w) { w = t; d = delta[k]; } break; }
                pts[i] = src[i] + d * (w * w * (3f - 2f * w));
            }
            return count;
        }

        // Nächster Punkt auf den Segmenten (j-1,j) und (j,j+1) der Polylinie.
        private static Vector3 ProjectOnto(List<Vector3> line, int j, Vector3 p)
        {
            Vector3 best = line[j]; float bd = float.MaxValue;
            for (int s = Mathf.Max(0, j - 1); s <= Mathf.Min(line.Count - 2, j); s++)
            {
                Vector3 a = line[s], b = line[s + 1];
                Vector2 ab = new Vector2(b.x - a.x, b.z - a.z), ap = new Vector2(p.x - a.x, p.z - a.z);
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
                Vector3 q = Vector3.Lerp(a, b, t);
                float d = (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z);
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }

        private static Vector2 Dir(List<Vector3> pts, int i)
        {
            Vector3 a = pts[Mathf.Max(0, i - 1)], b = pts[Mathf.Min(pts.Count - 1, i + 1)];
            var d = new Vector2(b.x - a.x, b.z - a.z);
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector2.up;
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        // Centered moving average on elevation only.
        private static void SmoothElevation(List<Vector3> pts, int window)
        {
            if (window < 2 || pts.Count < window) return;
            int half = window / 2;
            var y = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                float sum = 0f; int cnt = 0;
                for (int j = -half; j <= half; j++)
                {
                    int k = i + j;
                    if (k < 0 || k >= pts.Count) continue;
                    sum += pts[k].y; cnt++;
                }
                y[i] = sum / cnt;
            }
            for (int i = 0; i < pts.Count; i++) { var p = pts[i]; p.y = y[i]; pts[i] = p; }
        }
    }
}
