using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Centripetal Catmull-Rom (alpha = 0.5) with arc-length parametrisation.
    // Centripetal parametrisation removes the overshoot / self-intersection that UNIFORM
    // Catmull-Rom produces on GPS tracks with uneven spacing and sharp corners.
    // Drop-in replacement: same public API as the previous RouteSpline.
    public class RouteSpline
    {
        private List<Vector3> knots = new List<Vector3>();
        private float[] tKnot;     // centripetal knot parameters
        private float[] arcDist;   // cumulative distance per table sample
        private float[] arcU;      // spline parameter u per table sample
        private const int TableSamples = 8;   // arc-table samples per segment
        private const float Alpha = 0.5f;     // 0.5 = centripetal

        public float Length { get; private set; }
        public int KnotCount => knots.Count;

        public void Define(IList<Vector3> pts)
        {
            knots = new List<Vector3>(pts);
            int n = knots.Count;
            if (n < 2) { Length = 0f; arcDist = null; return; }

            // Centripetal knot parameters: t[i+1] = t[i] + |P[i+1]-P[i]|^alpha
            tKnot = new float[n];
            tKnot[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                float d = Vector3.Distance(knots[i - 1], knots[i]);
                tKnot[i] = tKnot[i - 1] + Mathf.Pow(Mathf.Max(d, 1e-4f), Alpha);
            }

            // Arc-length table across the whole parameter range.
            int seg = n - 1;
            int samples = seg * TableSamples;
            arcDist = new float[samples + 1];
            arcU = new float[samples + 1];
            Vector3 prev = EvaluateU(tKnot[0]);
            arcDist[0] = 0f; arcU[0] = tKnot[0];
            float total = 0f;
            int k = 1;
            for (int s = 0; s < seg; s++)
                for (int j = 1; j <= TableSamples; j++)
                {
                    float u = Mathf.Lerp(tKnot[s], tKnot[s + 1], j / (float)TableSamples);
                    Vector3 p = EvaluateU(u);
                    total += Vector3.Distance(prev, p);
                    arcDist[k] = total; arcU[k] = u;
                    prev = p; k++;
                }
            Length = total;
        }

        private void LocateU(float d, out float u)
        {
            if (arcDist == null || arcDist.Length < 2) { u = 0f; return; }
            d = Mathf.Clamp(d, 0f, Length);
            int lo = 1, hi = arcDist.Length - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (arcDist[mid] < d) lo = mid + 1; else hi = mid; }
            float span = Mathf.Max(arcDist[lo] - arcDist[lo - 1], 1e-6f);
            float frac = (d - arcDist[lo - 1]) / span;
            u = Mathf.Lerp(arcU[lo - 1], arcU[lo], frac);
        }

        // Non-uniform Catmull-Rom (Barry-Goldman) at parameter u.
        private Vector3 EvaluateU(float u)
        {
            int n = knots.Count;
            int s = 0;
            while (s < n - 2 && u > tKnot[s + 1]) s++;
            int i0 = Mathf.Max(0, s - 1), i1 = s, i2 = Mathf.Min(n - 1, s + 1), i3 = Mathf.Min(n - 1, s + 2);
            Vector3 P0 = knots[i0], P1 = knots[i1], P2 = knots[i2], P3 = knots[i3];
            float t0 = tKnot[i0], t1 = tKnot[i1], t2 = tKnot[i2], t3 = tKnot[i3];

            // Guard degenerate params at the clamped endpoints (coincident neighbours).
            if (t1 <= t0) t0 = t1 - 1e-3f;
            if (t2 <= t1) t2 = t1 + 1e-3f;
            if (t3 <= t2) t3 = t2 + 1e-3f;
            u = Mathf.Clamp(u, t1, t2);

            Vector3 A1 = Interp(P0, P1, t0, t1, u);
            Vector3 A2 = Interp(P1, P2, t1, t2, u);
            Vector3 A3 = Interp(P2, P3, t2, t3, u);
            Vector3 B1 = Interp(A1, A2, t0, t2, u);
            Vector3 B2 = Interp(A2, A3, t1, t3, u);
            return Interp(B1, B2, t1, t2, u);
        }

        private static Vector3 Interp(Vector3 a, Vector3 b, float ta, float tb, float u)
        {
            float w = (u - ta) / Mathf.Max(tb - ta, 1e-6f);
            return a * (1f - w) + b * w;
        }

        public Vector3 SamplePosition(float distance)
        {
            if (arcDist == null) return knots.Count > 0 ? knots[0] : Vector3.zero;
            LocateU(distance, out float u);
            return EvaluateU(u);
        }

        // Tangent via central difference on position — robust and matches the arc-length frame.
        public Vector3 SampleTangent(float distance)
        {
            const float h = 0.5f;
            Vector3 a = SamplePosition(Mathf.Clamp(distance - h, 0f, Length));
            Vector3 b = SamplePosition(Mathf.Clamp(distance + h, 0f, Length));
            Vector3 t = b - a;
            return t.sqrMagnitude < 1e-8f ? Vector3.forward : t.normalized;
        }

        public Vector3 Sample(float distance, out Vector3 tangent)
        {
            tangent = SampleTangent(distance);
            return SamplePosition(distance);
        }
    }
}
