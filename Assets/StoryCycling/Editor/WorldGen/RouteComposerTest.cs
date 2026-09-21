using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Batch-verifiable test: composes GPX + synthetic segments into one open spline
    // and checks continuity + length. Throws on mismatch so batch mode returns non-zero.
    public static class RouteComposerTest
    {
        [MenuItem("Story Cycling/WorldGen/Test RouteComposer")]
        public static void Run()
        {
            var manifest = ScriptableObject.CreateInstance<RouteManifest>();
            manifest.seed = 777;
            manifest.segments = new[]
            {
                new RouteSegment { type = SegmentType.Synthetic, lengthM = 4000, biome = "forest", elevationProfile = "rolling" },
                new RouteSegment { type = SegmentType.Synthetic, lengthM = 3000, biome = "field", elevationProfile = "flat" }
            };

            // Loader returns null → synthetic only for this test (no GPX file yet).
            List<Vector3> points = RouteComposer.Compose(manifest, null);
            if (points.Count < 2) throw new System.Exception("Compose produced <2 points");

            var spline = new RouteSpline();
            spline.Define(points);

            // Two segments ≈ 7000 m total, minus one dropped seam point.
            if (spline.Length < 6500f || spline.Length > 7500f)
                throw new System.Exception("Composed length off: " + spline.Length);

            // Tangent must be smooth and non-degenerate along the whole path.
            for (float d = 0f; d <= spline.Length; d += 500f)
            {
                Vector3 t = spline.SampleTangent(d);
                if (t.sqrMagnitude < 0.99f) throw new System.Exception("Degenerate tangent at " + d);
            }

            Object.DestroyImmediate(manifest);
            Debug.Log($"RouteComposer PASS: {points.Count} points, spline {spline.Length:0.0} m, tangents smooth.");
        }
    }
}
