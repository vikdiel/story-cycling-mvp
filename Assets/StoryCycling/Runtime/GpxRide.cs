using System.IO;
using UnityEngine;
using StoryCycling.WorldGen;

namespace StoryCycling
{
    // Loads and samples a GPX track projected to local ENU metres. Open (point-to-point),
    // unlike the closed CapeCrownRoute. Reuses the WorldGen parser + open spline.
    public static class GpxRide
    {
        // Linksverkehr: auf dem asphaltierten Seitenstreifen links der gelben Linie (wie Radfahrer am Kap)
        public const float LaneOffset = -4.4f;
        public static RouteSpline Spline { get; private set; }
        public static bool IsLoaded => Spline != null && Spline.Length > 0f;
        public static float Length => Spline != null ? Spline.Length : 0f;

        // Gebackene Route (vom WorldGen-Builder aus OSM erzeugt): <name>.route.txt neben der GPX.
        // Enthält die geglättete Fahrlinie auf den OSM-Straßenachsen und die Fahrspur je Punkt.
        private static float[] laneAt, cumDist;

        public static void Load(string streamingAssetsRelPath)
        {
            string path = Path.Combine(Application.streamingAssetsPath, streamingAssetsRelPath);
            string baked = Path.ChangeExtension(path, ".route.txt");
            laneAt = null; cumDist = null;
            if (File.Exists(baked) && BakedRoute.TryRead(File.ReadAllText(baked), out var bp, out var lanes))
            {
                Spline = new RouteSpline();
                Spline.Define(bp);
                laneAt = lanes.ToArray();
                cumDist = new float[bp.Count];
                for (int i = 1; i < bp.Count; i++) cumDist[i] = cumDist[i - 1] + Vector3.Distance(bp[i - 1], bp[i]);
                Debug.Log($"GpxRide: gebackene OSM-Route {bp.Count} Punkte, {Spline.Length / 1000f:0.00} km");
                return;
            }
            if (!File.Exists(path)) { Debug.LogError("GPX not found: " + path); Spline = null; return; }
            var pts = GpxParser.Parse(File.ReadAllText(path));
            var local = RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts));
            Spline = new RouteSpline();
            Spline.Define(local);
            Debug.Log($"GpxRide loaded {local.Count} points, {Spline.Length / 1000f:0.00} km");
        }

        // Seitlicher Versatz des Fahrers an Streckenposition d (Seitenstreifen bzw. Fahrspur-Rand).
        public static float LaneOffsetAt(float d)
        {
            if (laneAt == null || cumDist == null || laneAt.Length == 0) return LaneOffset;
            float total = cumDist[cumDist.Length - 1];
            float x = Spline != null && Spline.Length > 0f ? d / Spline.Length * total : d;
            int lo = 0, hi = cumDist.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (cumDist[mid] <= x) lo = mid; else hi = mid; }
            float t = cumDist[hi] > cumDist[lo] ? Mathf.Clamp01((x - cumDist[lo]) / (cumDist[hi] - cumDist[lo])) : 0f;
            return Mathf.Lerp(laneAt[lo], laneAt[hi], t);
        }

        public static void Sample(float d, out Vector3 point, out Vector3 forward)
        {
            point = Spline.SamplePosition(d);
            forward = Spline.SampleTangent(d);
        }

        public static Vector3 Position(float d, float offset, float height)
        {
            Vector3 p = Spline.SamplePosition(d);
            Vector3 t = Spline.SampleTangent(d);
            Vector3 right = Vector3.Cross(Vector3.up, t).normalized;
            return p + right * offset + Vector3.up * height;
        }
    }
}
