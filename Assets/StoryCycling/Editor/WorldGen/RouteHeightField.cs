using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Grobes Höhen-/Tangentenfeld entlang des Splines: für Boden-Anker (y am nächsten
    // Routenpunkt) und Ausrichtung (Straßen-Tangente). Von Gebäude- und Detail-Placer genutzt.
    public sealed class RouteHeightField
    {
        private struct S { public Vector3 pos; public Vector3 fwd; }
        private const float Cell = 25f;
        private readonly Dictionary<(int, int), List<S>> grid = new Dictionary<(int, int), List<S>>();

        public RouteHeightField(RouteSpline spline, float step)
        {
            for (float d = 0; d <= spline.Length; d += step)
            {
                var s = new S { pos = spline.SamplePosition(d), fwd = spline.SampleTangent(d) };
                var key = (Mathf.FloorToInt(s.pos.x / Cell), Mathf.FloorToInt(s.pos.z / Cell));
                if (!grid.TryGetValue(key, out var l)) { l = new List<S>(); grid[key] = l; }
                l.Add(s);
            }
        }

        private bool Nearest(float x, float z, out S best)
        {
            int cx = Mathf.FloorToInt(x / Cell), cz = Mathf.FloorToInt(z / Cell);
            float bd = float.MaxValue; best = default; bool found = false;
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((cx + dx, cz + dz), out var l))
                    foreach (var s in l)
                    {
                        float dd = (s.pos.x - x) * (s.pos.x - x) + (s.pos.z - z) * (s.pos.z - z);
                        if (dd < bd) { bd = dd; best = s; found = true; }
                    }
            return found;
        }

        public float HeightAt(float x, float z) => Nearest(x, z, out var s) ? s.pos.y : 0f;
        public Vector3 ForwardAt(float x, float z) => Nearest(x, z, out var s) ? s.fwd : Vector3.forward;
    }
}
