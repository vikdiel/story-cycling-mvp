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

        public static void Load(string streamingAssetsRelPath)
        {
            string path = Path.Combine(Application.streamingAssetsPath, streamingAssetsRelPath);
            if (!File.Exists(path)) { Debug.LogError("GPX not found: " + path); Spline = null; return; }
            var pts = GpxParser.Parse(File.ReadAllText(path));
            var local = RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts));
            Spline = new RouteSpline();
            Spline.Define(local);
            Debug.Log($"GpxRide loaded {local.Count} points, {Spline.Length / 1000f:0.00} km");
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
