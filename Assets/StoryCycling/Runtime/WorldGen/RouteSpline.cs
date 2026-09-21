using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Open Catmull-Rom spline with arc-length parametrisation — the WorldGen equivalent
    // of CapeCrownRoute, but point-to-point (not a closed loop), for 50 km routes.
    public class RouteSpline
    {
        private List<Vector3> knots = new List<Vector3>();
        private float[] cumulative;
        private const int SamplesPerSeg = 16;

        public float Length { get; private set; }
        public int KnotCount => knots.Count;

        public void Define(IList<Vector3> pts)
        {
            knots = new List<Vector3>(pts);
            if (knots.Count < 2) { Length = 0f; cumulative = null; return; }
            int segCount = knots.Count - 1;
            cumulative = new float[segCount * SamplesPerSeg + 1];
            float total = 0f;
            Vector3 prev = knots[0];
            for (int s = 0; s < segCount; s++)
                for (int i = 1; i <= SamplesPerSeg; i++)
                {
                    Vector3 p = Point(s, i / (float)SamplesPerSeg);
                    total += Vector3.Distance(prev, p);
                    cumulative[s * SamplesPerSeg + i] = total;
                    prev = p;
                }
            Length = total;
        }

        // Catmull-Rom on segment s with end-point clamping (open spline).
        private Vector3 Point(int s, float t)
        {
            int n = knots.Count;
            Vector3 p0 = knots[Mathf.Max(0, s - 1)];
            Vector3 p1 = knots[s];
            Vector3 p2 = knots[Mathf.Min(n - 1, s + 1)];
            Vector3 p3 = knots[Mathf.Min(n - 1, s + 2)];
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (3f * (p1 - p2) + p3 - p0) * t3);
        }

        private void Locate(float d, out int seg, out float t)
        {
            if (cumulative == null || cumulative.Length < 2) { seg = 0; t = 0f; return; }
            int lo = 1, hi = cumulative.Length - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (cumulative[mid] < d) lo = mid + 1; else hi = mid; }
            int interval = lo - 1;
            float frac = (d - cumulative[interval]) / Mathf.Max(cumulative[lo] - cumulative[interval], 1e-6f);
            seg = interval / SamplesPerSeg;
            t = (interval % SamplesPerSeg + frac) / SamplesPerSeg;
        }

        public Vector3 SamplePosition(float distance)
        {
            float d = Mathf.Clamp(distance, 0f, Length);
            Locate(d, out int seg, out float t);
            return Point(seg, t);
        }

        public Vector3 SampleTangent(float distance)
        {
            float d = Mathf.Clamp(distance, 0f, Length);
            Locate(d, out int seg, out float t);
            int n = knots.Count;
            Vector3 p0 = knots[Mathf.Max(0, seg - 1)];
            Vector3 p1 = knots[seg];
            Vector3 p2 = knots[Mathf.Min(n - 1, seg + 1)];
            Vector3 p3 = knots[Mathf.Min(n - 1, seg + 2)];
            float t2 = t * t;
            Vector3 tan = 0.5f * ((p2 - p0) + 2f * (2f * p0 - 5f * p1 + 4f * p2 - p3) * t +
                3f * (3f * (p1 - p2) + p3 - p0) * t2);
            return tan.sqrMagnitude < 1e-8f ? Vector3.forward : tan.normalized;
        }

        public Vector3 Sample(float distance, out Vector3 tangent)
        {
            tangent = SampleTangent(distance);
            return SamplePosition(distance);
        }
    }
}
