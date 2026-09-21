using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Reports route stats for a GPX in StreamingAssets. Batch-verifiable, logs length
    // + total ascent. No geometry generated — pure data inspection.
    public static class GpxRouteReport
    {
        [MenuItem("Story Cycling/WorldGen/Report Nordhoek GPX")]
        public static void Run()
        {
            string path = "Assets/StreamingAssets/Routes/Nordhoek.gpx";
            if (!File.Exists(path)) throw new System.Exception("GPX not found: " + path);

            string xml = File.ReadAllText(path);
            var pts = GpxParser.Parse(xml);
            if (pts.Count < 2) throw new System.Exception("GPX parsed to <2 points: " + pts.Count);

            var local = GpxParser.ProjectToLocalMeters(pts);
            float length = GpxParser.PathLength(local);

            // Total ascent = sum of positive elevation deltas along the path.
            float ascent = 0f;
            for (int i = 1; i < local.Count; i++)
            {
                float dy = local[i].y - local[i - 1].y;
                if (dy > 0f) ascent += dy;
            }

            Debug.Log($"Nordhoek GPX: {pts.Count} points, length {length:0.0} m ({length / 1000f:0.00} km), " +
                      $"ascent {ascent:0.0} m, ele {local[0].y:0} m → {local[local.Count - 1].y:0} m.");
        }
    }
}
