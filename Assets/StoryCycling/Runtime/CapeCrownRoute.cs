using System;
using UnityEngine;

namespace StoryCycling
{
    // Closed route defined by waypoints, sampled as a smooth Catmull-Rom loop.
    // One shared engine drives road geometry, rider, camera, elevation and validation,
    // so every route section is just a list of points plus a hill profile.
    public static class CapeCrownRoute
    {
        public const float RoadHalfWidth = 5f;
        public const float LaneOffset = -2.2f;
        public const float CoastalHillHeight = 8f;

        // Distance ranges (metres) along the route; heights scale with hillHeight.
        public static (float start, float end, float weight)[] Hills = new[]
        {
            (360f, 660f, .65f),
            (700f, 920f, .4f)
        };

        private static Vector2[] pts;
        private static float total;
        private const int SamplesPerSeg = 64;

        public static float Length => total;
        public static bool IsDefined => pts != null && pts.Length >= 3;
        public static Vector2[] Waypoints => pts;

        public static void Define(params Vector2[] waypoints)
        {
            if (waypoints == null || waypoints.Length < 3)
                throw new ArgumentException("Route needs at least 3 waypoints.");
            pts = waypoints;
            total = 0f;
            Vector2 prev = Point(0, 0f);
            for (int seg = 0; seg < pts.Length; seg++)
                for (int s = 1; s <= SamplesPerSeg; s++)
                {
                    Vector2 p = Point(seg, s / (float)SamplesPerSeg);
                    total += Vector2.Distance(prev, p);
                    prev = p;
                }
        }

        static Vector2 Point(int seg, float t)
        {
            int n = pts.Length;
            Vector2 p0 = pts[(seg - 1 + n) % n];
            Vector2 p1 = pts[seg];
            Vector2 p2 = pts[(seg + 1) % n];
            Vector2 p3 = pts[(seg + 2) % n];
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (3f * (p1 - p2) + p3 - p0) * t3);
        }

        static Vector2 Tangent(int seg, float t)
        {
            int n = pts.Length;
            Vector2 p0 = pts[(seg - 1 + n) % n];
            Vector2 p1 = pts[seg];
            Vector2 p2 = pts[(seg + 1) % n];
            Vector2 p3 = pts[(seg + 2) % n];
            float t2 = t * t;
            return 0.5f * ((p2 - p0) +
                2f * (2f * p0 - 5f * p1 + 4f * p2 - p3) * t +
                3f * (3f * (p1 - p2) + p3 - p0) * t2);
        }

        static void Locate(float d, out int seg, out float t)
        {
            float acc = 0f;
            Vector2 prev = Point(0, 0f);
            for (int s = 0; s < pts.Length; s++)
            {
                for (int k = 1; k <= SamplesPerSeg; k++)
                {
                    Vector2 p = Point(s, k / (float)SamplesPerSeg);
                    float len = Vector2.Distance(prev, p);
                    if (acc + len >= d)
                    {
                        float frac = (d - acc) / Mathf.Max(len, 1e-6f);
                        seg = s;
                        t = (k - 1 + frac) / SamplesPerSeg;
                        return;
                    }
                    acc += len;
                    prev = p;
                }
            }
            seg = pts.Length - 1;
            t = 1f;
        }

        public static void Sample(float distance, out Vector3 point, out Vector3 forward, float hillHeight = 0f)
        {
            float d = Mathf.Repeat(distance, total);
            Locate(d, out int seg, out float t);
            Vector2 p = Point(seg, t);
            Vector2 tan = Tangent(seg, t);
            if (tan.sqrMagnitude < 1e-8f) tan = Vector2.up;
            tan.Normalize();
            float y = Elevation(distance, hillHeight);
            point = new Vector3(p.x, y, p.y);
            forward = (new Vector3(tan.x, 0f, tan.y) + Vector3.up * Grade(distance, hillHeight)).normalized;
        }

        public static Vector3 Position(float distance, float offset = 0f, float height = 0f, float hillHeight = 0f)
        {
            Sample(distance, out Vector3 point, out Vector3 forward, hillHeight);
            return point + Vector3.Cross(Vector3.up, forward).normalized * offset + Vector3.up * height;
        }

        // Offset lanes travel a slightly different distance through bends (<5%), well
        // inside the speed-check tolerance; grade stretches the 3D path exactly.
        public static float Advance(float distance, float metres, float offset, float hillHeight = 0f)
        {
            float grade = Grade(distance, hillHeight);
            return distance + metres / Mathf.Sqrt(1f + grade * grade);
        }

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
            float d = Mathf.Repeat(distance, total);
            float e = 0f;
            for (int i = 0; i < Hills.Length; i++)
                e += Bump(d, Hills[i].start, Hills[i].end) * Hills[i].weight;
            return hillHeight * e;
        }

        public static float Grade(float distance, float hillHeight)
        {
            float d = Mathf.Repeat(distance, total);
            float g = 0f;
            for (int i = 0; i < Hills.Length; i++)
                g += BumpSlope(d, Hills[i].start, Hills[i].end) * Hills[i].weight;
            return hillHeight * g;
        }
    }
}
