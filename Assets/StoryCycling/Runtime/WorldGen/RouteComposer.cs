using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Composes manifest segments (GPX + synthetic) into ONE continuous open spline,
    // with tangent continuity at the seams (synthetic segments are generated to meet
    // the previous segment's end tangent). Pure data step; no geometry invented here.
    public static class RouteComposer
    {
        // Returns the composed point list (local ENU metres). GPX segments are resolved
        // through the provided loader (reads + projects the file); synthetic segments are
        // generated deterministically from the manifest seed.
        public static List<Vector3> Compose(RouteManifest manifest, System.Func<string, List<Vector3>> gpxLoader)
        {
            var all = new List<Vector3>();
            Vector3 lastTangent = Vector3.forward;

            foreach (var seg in manifest.segments)
            {
                List<Vector3> pts;
                if (seg.type == SegmentType.Gpx)
                {
                    if (gpxLoader == null) { Debug.LogWarning($"WorldGen: no GPX loader, skipping {seg.source}"); continue; }
                    pts = gpxLoader(seg.source);
                    if (pts == null || pts.Count < 2) { Debug.LogWarning($"WorldGen: GPX {seg.source} empty, skipping"); continue; }
                }
                else
                {
                    pts = Synthetic.Generate(manifest.seed, seg.lengthM, seg.elevationProfile, lastTangent);
                }

                if (all.Count == 0)
                {
                    all.AddRange(pts);
                }
                else
                {
                    // Align segment start to the previous end, preserving seam continuity.
                    Vector3 delta = all[all.Count - 1] - pts[0];
                    for (int i = 0; i < pts.Count; i++) pts[i] += delta;
                    all.RemoveAt(all.Count - 1); // drop duplicate seam point
                    all.AddRange(pts);
                }

                if (pts.Count >= 2) lastTangent = (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;
            }
            return all;
        }

        // Simple deterministic synthetic segment: gentle wander + a preset elevation profile.
        private static class Synthetic
        {
            public static List<Vector3> Generate(int seed, float lengthM, string profile, Vector3 startTangent)
            {
                var rng = new System.Random(seed);
                const float spacing = 20f;
                int n = Mathf.Max(2, Mathf.RoundToInt(lengthM / spacing) + 1);
                var pts = new List<Vector3>(n);

                // Build along a heading derived from the start tangent, with gentle S-wanders.
                float heading = Mathf.Atan2(startTangent.z, startTangent.x);
                Vector3 pos = Vector3.zero;
                pts.Add(pos);
                for (int i = 1; i < n; i++)
                {
                    float t = i / (float)(n - 1);
                    heading += (float)(rng.NextDouble() - 0.5) * 0.5f; // ±0.25 rad wander
                    float step = spacing;
                    pos += new Vector3(Mathf.Cos(heading), 0f, Mathf.Sin(heading)) * step;
                    float ele = Elevation(t, profile);
                    pts.Add(new Vector3(pos.x, ele, pos.z));
                }
                return pts;
            }

            private static float Elevation(float t, string profile)
            {
                switch (profile)
                {
                    case "hilly": return Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3f)) * 40f;
                    case "flat": return 0f;
                    case "rolling":
                    default: return Mathf.Sin(t * Mathf.PI * 2f) * 20f;
                }
            }
        }
    }
}
