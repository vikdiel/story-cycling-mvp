using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Cleans a raw GPS polyline before it becomes a spline:
    //   1) drops points closer than minSpacing (GPS jitter / Strava-densified points)
    //   2) resamples to uniform arc-length spacing (stable spline + even sampling)
    //   3) moving-average smooths elevation (Y) to remove barometric noise / bumpy grade
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
            ReconcileOverlaps(resampled);
            return resampled;
        }

        // GPX out-and-back segments can have slightly different barometric heights even
        // when their XY centreline is identical. Later passes inherit the first pass and
        // blend in/out, preventing a double road and rider height disagreement.
        public static int ReconcileOverlaps(List<Vector3> points, float radius = 8.5f, float minArcGap = 120f, float maxHeightDelta = 4f, float blend = 30f)
        {
            if (points == null || points.Count < 3) return 0;
            int count = points.Count;
            var arc = new float[count];
            for (int i = 1; i < count; i++) arc[i] = arc[i - 1] + Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].z), new Vector2(points[i].x, points[i].z));
            float cell = radius * 2f;
            var grid = new Dictionary<long, List<int>>();
            var corrections = new float[count];
            var matched = new bool[count];
            int corrected = 0;
            for (int i = 0; i < count; i++)
            {
                var p = points[i]; var direction = Direction(points, i);
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
                int best = -1; float bestDistance = radius * radius;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(Key(cx + dx, cz + dz), out var candidates)) continue;
                    foreach (int candidate in candidates)
                    {
                        if (arc[i] - arc[candidate] < minArcGap) continue;
                        if (Mathf.Abs(Vector2.Dot(direction, Direction(points, candidate))) < .8f) continue;
                        float x = p.x - points[candidate].x, z = p.z - points[candidate].z, distance = x * x + z * z;
                        if (distance >= bestDistance || Mathf.Abs(p.y - points[candidate].y) > maxHeightDelta) continue;
                        best = candidate; bestDistance = distance;
                    }
                }
                if (best >= 0) { corrections[i] = points[best].y - p.y; matched[i] = true; corrected++; }
                long key = Key(cx, cz);
                if (!grid.TryGetValue(key, out var bucket)) { bucket = new List<int>(); grid[key] = bucket; }
                bucket.Add(i);
            }
            for (int i = 0; i < count; i++)
            {
                float correction = corrections[i];
                if (!matched[i])
                {
                    float bestWeight = 0f;
                    for (int j = i - 1; j >= 0 && arc[i] - arc[j] <= blend; j--)
                        if (matched[j]) { float w = 1f - (arc[i] - arc[j]) / blend; if (w > bestWeight) { bestWeight = w; correction = corrections[j]; } }
                    for (int j = i + 1; j < count && arc[j] - arc[i] <= blend; j++)
                        if (matched[j]) { float w = 1f - (arc[j] - arc[i]) / blend; if (w > bestWeight) { bestWeight = w; correction = corrections[j]; } }
                    correction *= bestWeight * bestWeight * (3f - 2f * bestWeight);
                }
                if (Mathf.Abs(correction) > .0001f) { var p = points[i]; p.y += correction; points[i] = p; }
            }
            return corrected;
        }

        private static Vector2 Direction(List<Vector3> points, int index)
        {
            Vector3 a = points[Mathf.Max(0, index - 1)], b = points[Mathf.Min(points.Count - 1, index + 1)];
            var direction = new Vector2(b.x - a.x, b.z - a.z);
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector2.up;
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

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
