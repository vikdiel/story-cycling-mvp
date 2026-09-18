using System;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.Editor
{
    public static class CapeCrownValidation
    {
        [MenuItem("Story Cycling/Validate Loop Geometry")]
        public static void ValidateRoute()
        {
            const float epsilon = .01f;
            float length = CapeCrownRoute.Length;
            float straight = 2f * CapeCrownRoute.HalfStraight;
            float arc = Mathf.PI * CapeCrownRoute.Radius;
            // Position and tangent must be continuous across all joins, including lap wrap.
            foreach (float join in new[] { 0f, straight, straight + arc, 2f * straight + arc, length })
            {
                CapeCrownRoute.Sample(join - epsilon, out Vector3 before, out Vector3 beforeForward);
                CapeCrownRoute.Sample(join + epsilon, out Vector3 after, out Vector3 afterForward);
                Require(Vector3.Distance(before, after) < .025f, "Gap at route join " + join);
                Require(Vector3.Dot(beforeForward, afterForward) > .999f, "Heading discontinuity at " + join);
            }
            // Check full lap surface orientation, lane clearance and near-unit metres.
            for (float d = 0; d < length; d += .5f)
            {
                Vector3 left = CapeCrownRoute.Position(d, -5f);
                Vector3 right = CapeCrownRoute.Position(d, 5f);
                Vector3 next = CapeCrownRoute.Position(d + .1f, -5f);
                Require(Vector3.Cross(next-left, right-left).y > 0f, "Downward road triangle");
                Require(Mathf.Abs(Vector3.Distance(left,right)-10f) < .001f, "Incorrect road width");
                Vector3 rider = CapeCrownRoute.Position(d, CapeCrownRoute.LaneOffset);
                Require(Vector3.Distance(left,rider) > 2f && Vector3.Distance(right,rider) > 2f, "Rider outside lane");
                float advanced = CapeCrownRoute.Advance(d,.01f,CapeCrownRoute.LaneOffset);
                float travelled = Vector3.Distance(rider,CapeCrownRoute.Position(advanced,CapeCrownRoute.LaneOffset));
                Require(Mathf.Abs(travelled-.01f) < .001f, "Speed changes in bends");
            }
            GameObject road = GameObject.Find("Continuous asphalt");
            if (road != null)
            {
                Mesh mesh = road.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] v = mesh.vertices;
                Require(Vector3.Distance(v[0], v[v.Length-2]) < .001f, "Asphalt seam left");
                Require(Vector3.Distance(v[1], v[v.Length-1]) < .001f, "Asphalt seam right");
                Require(AssetDatabase.Contains(mesh), "Road mesh is not persisted");
            }
            Debug.Log("Loop geometry PASS: four smooth joins, 10 m width, closed asphalt, left lane, metre-based speed.");
        }
        private static void Require(bool condition,string message)
        {
            if(!condition) throw new InvalidOperationException("Cape Crown geometry failed: " + message);
        }
    }
}
