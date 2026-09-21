using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Batch-verifiable test for GpxParser: parses an inline GPX, projects to local
    // metres and logs the result. Throws on any mismatch so batch mode returns non-zero.
    public static class GpxParserTest
    {
        [MenuItem("Story Cycling/WorldGen/Test GpxParser")]
        public static void Run()
        {
            const string gpx =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<gpx version=\"1.1\" creator=\"test\" xmlns=\"http://www.topografix.com/GPX/1/1\">" +
                "  <trk><trkseg>" +
                "    <trkpt lat=\"-34.0000\" lon=\"18.3500\"><ele>10.0</ele></trkpt>" +
                "    <trkpt lat=\"-34.0010\" lon=\"18.3500\"><ele>12.0</ele></trkpt>" +
                "    <trkpt lat=\"-34.0020\" lon=\"18.3510\"><ele>15.0</ele></trkpt>" +
                "  </trkseg></trk>" +
                "</gpx>";

            var pts = GpxParser.Parse(gpx);
            if (pts.Count != 3) throw new System.Exception("Expected 3 trackpoints, got " + pts.Count);
            if (System.Math.Abs(pts[0].Ele - 10.0) > 1e-6) throw new System.Exception("Elevation parse failed");

            var local = GpxParser.ProjectToLocalMeters(pts);
            if (local.Count != 3) throw new System.Exception("Projection count mismatch");
            if (local[0] != Vector3.zero) throw new System.Exception("First point must be origin, got " + local[0]);

            // 0.002 deg lat ≈ 222 m; the test track heads *south*, so z is negative.
            float span = Mathf.Abs(local[2].z);
            if (span < 200f || span > 240f) throw new System.Exception("Projection span off: " + span);

            float length = GpxParser.PathLength(local);
            Debug.Log($"GPX parse PASS: {pts.Count} points, projected path length {length:0.0} m, " +
                      $"span {span:0.0} m, first={local[0]}, last={local[2]}");
        }
    }
}
