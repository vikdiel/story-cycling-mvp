using UnityEngine;

namespace StoryCycling
{
    // One metre-based, tangent-continuous stadium for geometry AND motion.
    public static class CapeCrownRoute
    {
        public const float Radius = 48f;
        public const float HalfStraight = 175f;
        public const float RoadHalfWidth = 5f;
        public const float LaneOffset = -2.2f;
        public static float Length => 4f * HalfStraight + 2f * Mathf.PI * Radius;

        // Two coastal climbs on the ~1 km loop. Heights scale with hillHeight; the
        // taller first climb is "Cape Crown", the shorter second is an inland rise.
        public const float CoastalHillHeight = 8f;
        public static readonly (float start, float end, float weight)[] Hills =
        {
            (360f, 660f, .65f),
            (700f, 920f, .4f)
        };

        static float Bump(float d, float start, float end)
        {
            if (d <= start || d >= end) return 0f;
            float t = (d - start) / (end - start);
            float s = Mathf.Sin(Mathf.PI * t);
            return s * s;
        }

        static float BumpSlope(float d, float start, float end)
        {
            if (d <= start || d >= end) return 0f;
            float t = (d - start) / (end - start);
            return Mathf.PI / (end - start) * Mathf.Sin(2f * Mathf.PI * t);
        }

        public static float Elevation(float distance, float hillHeight)
        {
            float d = Mathf.Repeat(distance, Length);
            float e = 0f;
            for (int i = 0; i < Hills.Length; i++)
                e += Bump(d, Hills[i].start, Hills[i].end) * Hills[i].weight;
            return hillHeight * e;
        }

        // Rise / horizontal centre-line metre. Smooth at every climb, summit and descent join.
        public static float Grade(float distance, float hillHeight)
        {
            float d = Mathf.Repeat(distance, Length);
            float g = 0f;
            for (int i = 0; i < Hills.Length; i++)
                g += BumpSlope(d, Hills[i].start, Hills[i].end) * Hills[i].weight;
            return hillHeight * g;
        }

        public static void Sample(float distance, out Vector3 point, out Vector3 forward, float hillHeight = 0f)
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
            point.y = Elevation(distance, hillHeight);
            forward = (forward + Vector3.up * Grade(distance, hillHeight)).normalized;
        }

        public static Vector3 Position(float distance, float offset = 0f, float height = 0f, float hillHeight = 0f)
        {
            Sample(distance, out Vector3 point, out Vector3 forward, hillHeight);
            return point + Vector3.Cross(Vector3.up, forward).normalized * offset + Vector3.up * height;
        }

        // Offset lanes have a different arc length. Maintain physical speed in bends.
        public static float Advance(float distance, float metres, float offset, float hillHeight = 0f)
        {
            float d = Mathf.Repeat(distance, Length);
            float s = 2f * HalfStraight;
            float arc = Mathf.PI * Radius;
            bool bend = (d >= s && d < s + arc) || d >= 2f * s + arc;
            float horizontalScale = bend ? (Radius + offset) / Radius : 1f;
            float grade = Grade(distance, hillHeight);
            return distance + metres / Mathf.Sqrt(horizontalScale * horizontalScale + grade * grade);
        }
    }
}
