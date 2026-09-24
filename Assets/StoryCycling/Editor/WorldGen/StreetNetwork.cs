using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Querstraßen aus OSM rund um die Route:
    //   - Wege, die auf der Route selbst liegen, werden verworfen (die baut RoadMeshBuilder)
    //   - Einmündungen: die Querstraße beginnt knapp in der Hauptfahrbahn, 3 cm tiefer
    //     (Hauptstraße liegt sichtbar oben, kein Z-Fighting) und weitet sich zum Trichter
    //   - Höhenprofil aus dem DEM, geglättet, an Einmündungen auf die Straßenhöhe gezogen
    //   - Kreisverkehre: Ring als Straße + Mittelinsel (Bordstein + Grün)
    //   - nur bis 100 m neben der Route (dort liegt das feine 5-m-Gelände für den Einschnitt)
    public sealed class StreetNetwork
    {
        public sealed class Street
        {
            public readonly List<RoadField.Sample> Samples = new List<RoadField.Sample>();
            public string Highway;
            public bool CenterLine, Roundabout;
            public bool JunctionAtStart, JunctionAtEnd;
        }
        public struct Junction { public Vector3 Mouth; public float Half; }   // Mouth = Punkt am Fahrbahnrand der Route
        public struct Island { public Vector3 Center; public float Radius; }

        public const float MaxDistance = 100f;       // + Einschnitt-Radius < 150 m (5-m-Geländekacheln)
        private const float JoinDistance = RoadMeshBuilder.HalfWidth - 1f;   // Querstraße beginnt 1 m innerhalb der Hauptfahrbahn
        private const float ParallelClear = 14f;     // parallel laufende Stücke dichter an der Route verwerfen
        private const float JunctionKeep = 16f;      // … außer direkt an der Einmündung
        private const float FlareLength = 7f, FlareWidth = 2.5f;
        private const float MinLength = 12f;

        public readonly List<Street> Streets = new List<Street>();
        public readonly List<Junction> Junctions = new List<Junction>();
        public readonly List<Island> Islands = new List<Island>();
        public RoadField Field { get; private set; }

        public static float HalfWidthFor(string highway)
        {
            switch (highway)
            {
                case "motorway": case "trunk": case "primary": return 3.6f;
                case "secondary": case "motorway_link": case "trunk_link": case "primary_link": return 3.3f;
                case "tertiary": case "secondary_link": case "tertiary_link": return 3.1f;
                default: return 2.8f;   // residential, unclassified, living_street
            }
        }

        private static int Priority(string highway)
        {
            switch (highway)
            {
                case "motorway": case "trunk": return 0;
                case "primary": case "primary_link": return 1;
                case "secondary": case "secondary_link": return 2;
                case "tertiary": case "tertiary_link": return 3;
                default: return 4;
            }
        }

        public static StreetNetwork Build(OsmContext osm, RoadField main, WorldTerrain terrain)
        {
            var net = new StreetNetwork();
            var ways = new List<OsmContext.Street>();
            if (osm != null)
                foreach (var w in osm.Streets) if (!w.bridge && !w.tunnel && w.pts.Count >= 2) ways.Add(w);
            // Wichtige Straßen zuerst: untergeordnete schließen sich an deren Höhe an.
            ways.Sort((a, b) => Priority(a.highway).CompareTo(Priority(b.highway)));

            var built = new List<RoadField.Sample>();
            var builtHash = new Dictionary<long, List<int>>();
            int dropped = 0;

            foreach (var way in ways)
            {
                float half = HalfWidthFor(way.highway);
                var pts = Resample(way.pts, RoadField.Step);
                int n = pts.Count;
                var dMain = new float[n];
                var nearMain = new int[n];
                for (int i = 0; i < n; i++)
                {
                    main.Nearest(pts[i].x, pts[i].y, MaxDistance + 20f, out nearMain[i], out dMain[i]);
                    if (nearMain[i] < 0) dMain[i] = float.MaxValue;
                }

                // Läufe außerhalb der Hauptfahrbahn
                int s0 = -1;
                for (int i = 0; i <= n; i++)
                {
                    bool on = i < n && dMain[i] > JoinDistance && dMain[i] <= MaxDistance;
                    if (on && s0 < 0) s0 = i;
                    if (!on && s0 >= 0)
                    {
                        bool startJ = s0 > 0 && dMain[s0 - 1] <= JoinDistance;
                        bool endJ = i < n && dMain[i] <= JoinDistance;
                        foreach (var run in SplitParallel(pts, dMain, s0, i - 1, startJ, endJ))
                        {
                            var st = net.MakeStreet(way, half, pts, dMain, nearMain, run.a, run.b, run.startJ, run.endJ,
                                                    main, terrain, built, builtHash);
                            if (st != null) net.Streets.Add(st); else dropped++;
                        }
                        s0 = -1;
                    }
                }
            }

            net.BuildIslands(osm, terrain);
            var all = new List<RoadField.Sample>();
            foreach (var st in net.Streets) all.AddRange(st.Samples);
            net.Field = new RoadField(all);
            float km = all.Count * RoadField.Step / 1000f;
            Debug.Log($"Querstraßen: {net.Streets.Count} Stücke, {km:0.0} km, {net.Junctions.Count} Einmündungen, " +
                      $"{net.Islands.Count} Kreisverkehr-Inseln (OSM-Wege {ways.Count}, zu kurz/verworfen {dropped}).");
            if (osm != null && osm.Streets.Count == 0)
                Debug.LogWarning("OSM-Dump enthält keine Straßen — 'Fetch OSM for Nordhoek' neu ausführen, dann gibt es Querstraßen.");
            return net;
        }

        private struct Run { public int a, b; public bool startJ, endJ; }

        // Stücke, die parallel dicht an der Route laufen (z. B. zweite Richtungsfahrbahn am Rand), abtrennen.
        private static IEnumerable<Run> SplitParallel(List<Vector2> pts, float[] dMain, int a, int b, bool startJ, bool endJ)
        {
            var arc = new float[b - a + 1];
            for (int i = a + 1; i <= b; i++) arc[i - a] = arc[i - a - 1] + Vector2.Distance(pts[i - 1], pts[i]);
            float total = arc[b - a];
            int r0 = -1;
            for (int i = a; i <= b + 1; i++)
            {
                bool keep = i <= b && (dMain[i] >= ParallelClear ||
                                       (startJ && arc[i - a] <= JunctionKeep) ||
                                       (endJ && total - arc[i - a] <= JunctionKeep));
                if (keep && r0 < 0) r0 = i;
                if (!keep && r0 >= 0)
                {
                    yield return new Run { a = r0, b = i - 1, startJ = startJ && r0 == a, endJ = endJ && i - 1 == b };
                    r0 = -1;
                }
            }
        }

        private Street MakeStreet(OsmContext.Street way, float half, List<Vector2> pts, float[] dMain, int[] nearMain,
                                  int a, int b, bool startJ, bool endJ, RoadField main, WorldTerrain terrain,
                                  List<RoadField.Sample> built, Dictionary<long, List<int>> builtHash)
        {
            var poly = new List<Vector2>();
            // Einmündung: exakt ab dem Punkt beginnen, an dem die Querstraße die Hauptfahrbahn verlässt.
            if (startJ) poly.Add(Cross(pts[a - 1], pts[a], dMain[a - 1], dMain[a]));
            for (int i = a; i <= b; i++) poly.Add(pts[i]);
            if (endJ) poly.Add(Cross(pts[b + 1], pts[b], dMain[b + 1], dMain[b]));

            float len = 0f;
            for (int i = 1; i < poly.Count; i++) len += Vector2.Distance(poly[i - 1], poly[i]);
            if (len < MinLength || poly.Count < 2) return null;

            // Höhenprofil: DEM geglättet (±15 m), Meer/Brücken ausgenommen.
            int n = poly.Count;
            var raw = new float[n];
            for (int i = 0; i < n; i++) raw[i] = terrain.DemY(poly[i].x, poly[i].y);
            var y = new float[n];
            bool closed = n > 6 && Vector2.Distance(poly[0], poly[n - 1]) < 1f;
            if (way.roundabout)
            {
                // Kreisverkehre sind praktisch eben: ganzer Ring auf eine Höhe (kein Absatz am Ringschluss).
                float mean = 0f; foreach (float r in raw) mean += r; mean /= n;
                for (int i = 0; i < n; i++) y[i] = mean;
            }
            else
                for (int i = 0; i < n; i++)
                {
                    float sum = 0f; int c = 0;
                    for (int k = i - 7; k <= i + 7; k++)
                    {
                        int kk = closed ? (k % (n - 1) + (n - 1)) % (n - 1) : k;     // geschlossene Wege zyklisch glätten
                        if (kk < 0 || kk >= n) continue;
                        sum += raw[kk]; c++;
                    }
                    y[i] = sum / c;
                }
            for (int i = 0; i < n; i++) if (y[i] < terrain.SeaY + .5f) return null;

            // An Einmündungen auf Hauptstraßenhöhe (−3 cm) ziehen; an bereits gebaute Querstraßen (−2 cm).
            var arc = new float[n];
            for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + Vector2.Distance(poly[i - 1], poly[i]);
            float total = arc[n - 1];
            if (startJ) BlendTo(y, arc, main.Samples[nearMain[a - 1]].pos.y - .03f, false, total);
            if (endJ) BlendTo(y, arc, main.Samples[nearMain[b + 1]].pos.y - .03f, true, total);
            // Kreuzung mit bereits gebauter Querstraße: dort exakt 2 cm unter ihr, sanft über 10 m angleichen.
            var hitArc = new List<float>(); var hitY = new List<float>();
            for (int i = 0; i < n; i++)
            {
                int hit = NearestBuilt(built, builtHash, poly[i], 6f);
                if (hit >= 0) { hitArc.Add(arc[i]); hitY.Add(built[hit].pos.y - .02f); }
            }
            if (hitArc.Count > 0)
                for (int i = 0; i < n; i++)
                {
                    int best = 0; float bd = float.MaxValue;
                    for (int h = 0; h < hitArc.Count; h++) { float d = Mathf.Abs(arc[i] - hitArc[h]); if (d < bd) { bd = d; best = h; } }
                    float w = 1f - Mathf.Clamp01(bd / 10f); w = w * w * (3f - 2f * w);
                    y[i] = Mathf.Lerp(y[i], hitY[best], w);
                }

            var st = new Street { Highway = way.highway, Roundabout = way.roundabout, JunctionAtStart = startJ, JunctionAtEnd = endJ,
                                  CenterLine = Priority(way.highway) <= 3 && !way.oneway };
            for (int i = 0; i < n; i++)
            {
                Vector2 t2 = (i < n - 1 ? poly[i + 1] - poly[i] : poly[i] - poly[i - 1]);
                Vector3 t = new Vector3(t2.x, 0f, t2.y).normalized;
                if (t.sqrMagnitude < 1e-6f) t = Vector3.forward;
                // Trichter an der Einmündung (Eckradius)
                float flare = 0f;
                if (startJ) flare = Mathf.Max(flare, 1f - Mathf.Clamp01(arc[i] / FlareLength));
                if (endJ) flare = Mathf.Max(flare, 1f - Mathf.Clamp01((total - arc[i]) / FlareLength));
                flare = flare * flare;
                st.Samples.Add(new RoadField.Sample
                {
                    pos = new Vector3(poly[i].x, y[i], poly[i].y), tangent = t,
                    side = Vector3.Cross(Vector3.up, t).normalized, distance = arc[i], half = half + FlareWidth * flare
                });
            }
            foreach (var s in st.Samples)
            {
                int idx = built.Count; built.Add(s);
                long k = HashKey(s.pos.x, s.pos.z);
                if (!builtHash.TryGetValue(k, out List<int> l)) { l = new List<int>(); builtHash[k] = l; }
                l.Add(idx);
            }

            // Einmündungen für die Hauptstraße merken (Randlinie/Bordstein dort öffnen).
            if (startJ) AddJunction(main, poly[0], poly[Mathf.Min(1, n - 1)], half);
            if (endJ) AddJunction(main, poly[n - 1], poly[Mathf.Max(0, n - 2)], half);
            return st;
        }

        private void AddJunction(RoadField main, Vector2 mouth, Vector2 inward, float half)
        {
            if (!main.Nearest(mouth.x, mouth.y, 10f, out int mi, out _)) return;
            var ms = main.Samples[mi];
            float side = Mathf.Sign(Vector3.Dot(new Vector3(inward.x - ms.pos.x, 0f, inward.y - ms.pos.z), ms.side));
            float open = half + FlareWidth + 1.5f;
            Junctions.Add(new Junction
            {
                Mouth = ms.pos + ms.side * (side * RoadMeshBuilder.HalfWidth),
                Half = Mathf.Sqrt(open * open + RoadMeshBuilder.HalfWidth * RoadMeshBuilder.HalfWidth)
            });
        }

        // Punkt zwischen p (auf der Route) und q (außerhalb), an dem der Abstand JoinDistance erreicht.
        private static Vector2 Cross(Vector2 p, Vector2 q, float dp, float dq)
        {
            float t = Mathf.Clamp01((JoinDistance - dp) / Mathf.Max(1e-3f, dq - dp));
            return p + (q - p) * t;
        }

        private static void BlendTo(float[] y, float[] arc, float target, bool fromEnd, float total)
        {
            const float blend = 25f;
            for (int i = 0; i < y.Length; i++)
            {
                float s = fromEnd ? total - arc[i] : arc[i];
                if (s >= blend) continue;
                float t = s / blend; t = t * t * (3f - 2f * t);
                y[i] = Mathf.Lerp(target, y[i], t);
            }
        }

        private static long HashKey(float x, float z) => ((long)Mathf.FloorToInt(x / 10f) << 32) | (uint)Mathf.FloorToInt(z / 10f);

        private static int NearestBuilt(List<RoadField.Sample> built, Dictionary<long, List<int>> hash, Vector2 p, float r)
        {
            int best = -1; float bd = r * r;
            int cx = Mathf.FloorToInt(p.x / 10f), cz = Mathf.FloorToInt(p.y / 10f);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (hash.TryGetValue(((long)(cx + dx) << 32) | (uint)(cz + dz), out List<int> l))
                    foreach (int i in l)
                    {
                        float d2 = (built[i].pos.x - p.x) * (built[i].pos.x - p.x) + (built[i].pos.z - p.y) * (built[i].pos.z - p.y);
                        if (d2 < bd) { bd = d2; best = i; }
                    }
            return best;
        }

        public static List<Vector2> Resample(List<Vector2> pts, float step)
        {
            var res = new List<Vector2> { pts[0] };
            float carry = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                float d = step - carry;
                while (d <= len) { res.Add(a + (b - a) * (d / len)); d += step; }
                carry = len - (d - step);
            }
            if (Vector2.Distance(res[res.Count - 1], pts[pts.Count - 1]) > .3f) res.Add(pts[pts.Count - 1]);
            return res;
        }

        // Kreisverkehr-Inseln: Ringe (auch in Teilstücken) nach Mittelpunkt gruppieren.
        private void BuildIslands(OsmContext osm, WorldTerrain terrain)
        {
            if (osm == null) return;
            var groups = new List<List<Vector2>>();
            var centers = new List<Vector2>();
            foreach (var w in osm.Streets)
            {
                if (!w.roundabout) continue;
                Vector2 c = Vector2.zero; foreach (var p in w.pts) c += p; c /= w.pts.Count;
                int g = -1;
                for (int i = 0; i < centers.Count; i++) if (Vector2.Distance(centers[i], c) < 40f) { g = i; break; }
                if (g < 0) { groups.Add(new List<Vector2>()); centers.Add(c); g = groups.Count - 1; }
                groups[g].AddRange(w.pts);
                Vector2 m = Vector2.zero; foreach (var p in groups[g]) m += p; centers[g] = m / groups[g].Count;
            }
            for (int g = 0; g < groups.Count; g++)
            {
                if (groups[g].Count < 5) continue;
                // Mittelpunkt robust: Mitte der Bounding-Box des Rings
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in groups[g]) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
                var c = new Vector2((minX + maxX) * .5f, (minZ + maxZ) * .5f);
                float r = 0f; foreach (var p in groups[g]) r += Vector2.Distance(p, c); r /= groups[g].Count;
                float islandR = r - 3.8f;
                if (islandR < 2f || r > 60f) continue;
                Islands.Add(new Island { Center = new Vector3(c.x, 0f, c.y), Radius = islandR });
            }
        }

        // Inselhöhe nach dem Bau aus der umgebenden Ringfahrbahn bestimmen.
        public float IslandBaseY(Island isl, RoadField main)
        {
            float sum = 0f; int c = 0;
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.PI * 2f / 12f;
                float x = isl.Center.x + Mathf.Cos(a) * (isl.Radius + 2f), z = isl.Center.z + Mathf.Sin(a) * (isl.Radius + 2f);
                if (Field != null && Field.Nearest(x, z, 6f, out int i, out _)) { sum += Field.Samples[i].pos.y; c++; }
                else if (main.Nearest(x, z, 6f, out int j, out _)) { sum += main.Samples[j].pos.y; c++; }
            }
            return c > 0 ? sum / c : float.NaN;
        }
    }
}
