using UnityEngine;

namespace StoryCycling
{
    // One metre-based, tangent-continuous stadium for geometry AND motion.
    public static class CapeCrownRoute
    {
        public const float Radius = 48f;
        public const float HalfStraight = 85f;
        public const float RoadHalfWidth = 5f;
        public const float LaneOffset = -2.2f;
        public static float Length => 4f * HalfStraight + 2f * Mathf.PI * Radius;

        public static void Sample(float distance, out Vector3 point, out Vector3 forward)
        {
            float d = Mathf.Repeat(distance, Length);
            float straight = 2f * HalfStraight;
            float arc = Mathf.PI * Radius;
            if (d < straight)
            {
                point = new Vector3(Radius, 0f, -HalfStraight + d);
                forward = Vector3.forward;
            }
            else if (d < straight + arc)
            {
                float a = (d - straight) / Radius;
                point = new Vector3(Radius * Mathf.Cos(a), 0f, HalfStraight + Radius * Mathf.Sin(a));
                forward = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
            else if (d < 2f * straight + arc)
            {
                point = new Vector3(-Radius, 0f, HalfStraight - (d - straight - arc));
                forward = Vector3.back;
            }
            else
            {
                float a = Mathf.PI + (d - 2f * straight - arc) / Radius;
                point = new Vector3(Radius * Mathf.Cos(a), 0f, -HalfStraight + Radius * Mathf.Sin(a));
                forward = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
        }

        public static Vector3 Position(float distance, float offset = 0f, float height = 0f)
        {
            Sample(distance, out Vector3 point, out Vector3 forward);
            return point + Vector3.Cross(Vector3.up, forward) * offset + Vector3.up * height;
        }

        // Offset lanes have a different arc length. Maintain physical speed in bends.
        public static float Advance(float distance, float metres, float offset)
        {
            float d = Mathf.Repeat(distance, Length);
            float s = 2f * HalfStraight;
            float arc = Mathf.PI * Radius;
            bool bend = (d >= s && d < s + arc) || d >= 2f * s + arc;
            return distance + metres * (bend ? Radius / (Radius + offset) : 1f);
        }
    }
}
