using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Dichte Stichproben der Straßen-Mittellinie (alle 2 m) in einem Hash-Gitter.
    // Grundlage für: Gelände-Einschnitt, Abstandstests (nichts auf der Fahrbahn),
    // Ausrichtung von Gebäuden/Möbeln zur Straße und das Entdoppeln von Hin-/Rückweg.
    public sealed class RoadField
    {
        // half is the half carriageway width. The GPX route keeps the 4m default;
        // OSM side streets provide their own class-specific width.
        public struct Sample { public Vector3 pos; public Vector3 tangent; public Vector3 side; public float distance; public float half; }

        public const float Step = 2f;
        private const float CellSize = 10f;
        public readonly List<Sample> Samples = new List<Sample>();
        public float MinX = float.MaxValue, MaxX = float.MinValue, MinZ = float.MaxValue, MaxZ = float.MinValue;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        public RoadField(IEnumerable<Sample> samples)
        {
            foreach (var sample in samples) Insert(sample);
        }

        public RoadField(RouteSpline spline)
        {
            for (float d = 0f; d <= spline.Length; d += Step) Add(spline, d);
            if (Samples.Count == 0 || Samples[Samples.Count - 1].distance < spline.Length - 0.01f) Add(spline, spline.Length);
        }

        private void Add(RouteSpline spline, float d)
        {
            Vector3 p = spline.SamplePosition(d);
            Vector3 t = spline.SampleTangent(d);
            Vector3 flat = new Vector3(t.x, 0f, t.z);
            flat = flat.sqrMagnitude < 1e-8f ? Vector3.forward : flat.normalized;
            Insert(new Sample { pos = p, tangent = t, side = Vector3.Cross(Vector3.up, flat).normalized, distance = d, half = 4f });
        }

        private void Insert(Sample s)
        {
            if (s.half <= 0f) s.half = 4f;
            Vector3 p = s.pos;
            int index = Samples.Count;
            Samples.Add(s);
            long key = Key(Cell(p.x), Cell(p.z));
            if (!cells.TryGetValue(key, out List<int> list)) { list = new List<int>(); cells[key] = list; }
            list.Add(index);
            MinX = Mathf.Min(MinX, p.x); MaxX = Mathf.Max(MaxX, p.x);
            MinZ = Mathf.Min(MinZ, p.z); MaxZ = Mathf.Max(MaxZ, p.z);
        }

        private static int Cell(float v) => Mathf.FloorToInt(v / CellSize);
        private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        // Nächster Mittellinienpunkt innerhalb maxRadius (horizontal). Ringsuche mit Abbruch.
        public bool Nearest(float x, float z, float maxRadius, out int index, out float dist)
        {
            index = -1; dist = maxRadius;
            int cx = Cell(x), cz = Cell(z);
            int rings = Mathf.CeilToInt(maxRadius / CellSize) + 1;
            for (int ring = 0; ring <= rings; ring++)
            {
                if (index >= 0 && (ring - 1) * CellSize > dist) break;
                for (int dx = -ring; dx <= ring; dx++)
                for (int dz = -ring; dz <= ring; dz++)
                {
                    if (Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring) continue; // nur Ringrand
                    if (!cells.TryGetValue(Key(cx + dx, cz + dz), out List<int> list)) continue;
                    foreach (int i in list)
                    {
                        Vector3 p = Samples[i].pos;
                        float d = Mathf.Sqrt((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z));
                        if (d < dist) { dist = d; index = i; }
                    }
                }
            }
            return index >= 0;
        }

        public float Distance(float x, float z, float maxRadius)
        {
            return Nearest(x, z, maxRadius, out _, out float d) ? d : maxRadius;
        }

        // Alle Stichproben in einem achsparallelen Rechteck (für Brushing / Kollisionsprüfung).
        public void Query(float minX, float minZ, float maxX, float maxZ, List<int> result)
        {
            result.Clear();
            for (int cx = Cell(minX); cx <= Cell(maxX); cx++)
            for (int cz = Cell(minZ); cz <= Cell(maxZ); cz++)
                if (cells.TryGetValue(Key(cx, cz), out List<int> list))
                    foreach (int i in list)
                    {
                        Vector3 p = Samples[i].pos;
                        if (p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ) result.Add(i);
                    }
        }
    }
}
