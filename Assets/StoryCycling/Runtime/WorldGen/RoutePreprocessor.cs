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
