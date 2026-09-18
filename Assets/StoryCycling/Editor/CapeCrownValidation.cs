using System;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.Editor
{
    public static class CapeCrownValidation
    {
        [MenuItem("Story Cycling/Validate Loop Geometry")]
        public static void ValidateFlatRoute() => ValidateRoute(0);

        public static void ValidateRoute(float hillHeight)
        {
            const float epsilon = .01f;
            float length = CapeCrownRoute.Length;
            float straight = 2f * CapeCrownRoute.HalfStraight;
            float arc = Mathf.PI * CapeCrownRoute.Radius;
            // Position and tangent must be continuous across all joins, including lap wrap.
            foreach (float join in new[] { 0f, straight, straight + arc, 2f * straight + arc, length, CapeCrownRoute.HillStart, CapeCrownRoute.HillEnd })
            {
                CapeCrownRoute.Sample(join - epsilon, out Vector3 before, out Vector3 beforeForward,hillHeight);
                CapeCrownRoute.Sample(join + epsilon, out Vector3 after, out Vector3 afterForward,hillHeight);
                Require(Vector3.Distance(before, after) < .025f, "Gap at route join " + join);
                Require(Vector3.Dot(beforeForward, afterForward) > .999f, "Heading discontinuity at " + join);
            }
            // Check full lap surface orientation, lane clearance and near-unit metres.
            for (float d = 0; d < length; d += .5f)
            {
                Require(Mathf.Abs(CapeCrownRoute.Grade(d,hillHeight)) <= .06f, "Grade exceeds 6 percent");
                Vector3 left = CapeCrownRoute.Position(d, -5f,0,hillHeight);
                Vector3 right = CapeCrownRoute.Position(d, 5f,0,hillHeight);
                Vector3 next = CapeCrownRoute.Position(d + .1f, -5f,0,hillHeight);
                Require(Vector3.Cross(next-left, right-left).y > 0f, "Downward road triangle");
                Require(Mathf.Abs(Vector3.Distance(left,right)-10f) < .001f, "Incorrect road width");
                Vector3 rider = CapeCrownRoute.Position(d, CapeCrownRoute.LaneOffset,0,hillHeight);
                Require(Vector3.Distance(left,rider) > 2f && Vector3.Distance(right,rider) > 2f, "Rider outside lane");
                float advanced = CapeCrownRoute.Advance(d,.01f,CapeCrownRoute.LaneOffset,hillHeight);
                float travelled = Vector3.Distance(rider,CapeCrownRoute.Position(advanced,CapeCrownRoute.LaneOffset,0,hillHeight));
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
            Debug.Log($"Loop geometry PASS (hill {hillHeight} m): four smooth joins, 10 m width, closed asphalt, left lane, metre-based speed.");
        }
        private static void Require(bool condition,string message)
        {
            if(!condition) throw new InvalidOperationException("Cape Crown geometry failed: " + message);
        }

        public static void ValidateCyclist(CapeCrownCyclistAnimation rig)
        {
            Require(rig != null && rig.IsConfigured, "Cyclist animation references missing (possibly batched away)");
            // Full crank cycle: knees must remain reachable, limb lengths constant and feet above asphalt.
            for (int step = 0; step < 72; step++)
            {
                float phase = step * Mathf.PI * 2 / 72;
                Vector3 left = CapeCrownCyclistAnimation.PedalPosition(-1, phase);
                Vector3 right = CapeCrownCyclistAnimation.PedalPosition(1, phase + Mathf.PI);
                Require(Mathf.Abs(left.y + right.y - .64f) < .001f && Mathf.Abs(left.z + right.z) < .001f,
                    "Pedals must be opposite, not move in lockstep");
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 pedal = side == -1 ? left : right;
                    Vector3 ankle = pedal + Vector3.up * .07f;
                    Vector3 hip = new Vector3(side * .13f, 1.02f, -.18f);
                    Vector3 knee = CapeCrownCyclistAnimation.KneePosition(hip, ankle);
                    Require(Vector3.Distance(hip, ankle) < CapeCrownCyclistAnimation.LegLength * 2, "Pedal out of leg reach");
                    Require(Mathf.Abs(Vector3.Distance(hip, knee) - CapeCrownCyclistAnimation.LegLength) < .001f, "Thigh stretches");
                    Require(Mathf.Abs(Vector3.Distance(knee, ankle) - CapeCrownCyclistAnimation.LegLength) < .001f, "Shin stretches");
                    Require(pedal.y > .1f, "Pedal clips asphalt");
                }
            }
            rig.ApplyPose(0);
            Debug.Log("Cyclist rig PASS: live transforms, opposing pedals, reachable constant-length limbs for full crank cycle.");
        }
    }
}
