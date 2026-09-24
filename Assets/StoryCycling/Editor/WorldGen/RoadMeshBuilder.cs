using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Materialien aller Straßenteile (Hauptroute, Querstraßen, Inseln).
    public sealed class RoadMaterials
    {
        public Material Asphalt, Shoulder, Yellow, White, Sidewalk, Rail, IslandGrass;
        public Material[] Ribbon => new[] { Asphalt, Shoulder, Yellow, White, Sidewalk };
    }

    // Baut die Hauptroute als Mesh-Abschnitte (Frustum-Culling!) mit:
    //   - Entdoppelung: Strava-Hin-/Rückweg liegen exakt übereinander -> nur EINE Fahrbahn
    //   - Asphalt-Textur, gelbe Randlinien (Südafrika), weiße unterbrochene Mittellinie
    //   - außerorts Bankett + Schürze, innerorts Bordstein + Gehweg (OSM-Wohngebiet)
    //   - an Einmündungen von Querstraßen: Randlinie, Bordstein und Leitplanke offen
    //   - Leitplanken automatisch an der Talseite, wo das Gelände stark abfällt
    public sealed class RoadMeshBuilder
    {
        // Kap-Landstraße: 2 × 3,5 m Fahrstreifen + 2 m asphaltierter Seitenstreifen je Seite (gelbe Linie
        // an der Fahrstreifenkante), dahinter rotbrauner Schotter-Randstreifen.
        public const float HalfWidth = 5.5f;
        public const float ShoulderWidth = 2f;
        private const float ShoulderOuter = HalfWidth + 1.6f;
        private const float PieceSamples = 250;          // 500 m pro Mesh-Abschnitt
        private readonly RoadField road;
        private readonly WorldTerrain terrain;
        private RoadProfile.Edge[] leftEdge, rightEdge;

        public RoadMeshBuilder(RoadField road, WorldTerrain terrain) { this.road = road; this.terrain = terrain; }

        public void Build(Transform parent, RoadMaterials mats, StreetNetwork streets, System.Func<Mesh, Mesh> save, bool railsOnly = false)
        {
            bool[] draw = ComputeCoverage(out bool[] lower);
            ComputeEdges(streets);
            // Parallele zweite Fahrbahn (5–9 m daneben, gleiche Höhe): 3 cm tiefer, damit sich die
            // überlappenden Ränder nicht flackernd durchdringen — die erste liegt sichtbar oben.
            var s = new List<RoadField.Sample>(road.Samples);
            for (int k = 0; k < s.Count; k++) if (lower[k]) { var q = s[k]; q.pos.y -= .03f; s[k] = q; }
            int pieces = 0, railPieces = 0;
            int i = 0;
            while (i < s.Count)
            {
                if (!draw[i]) { i++; continue; }
                int start = Mathf.Max(0, i - 1);                     // 1 Probe Überlappung gegen Risse
                int end = i;
                while (end + 1 < s.Count && draw[end + 1] && end - start < PieceSamples) end++;
                int last = Mathf.Min(s.Count - 1, end + 1);

                if (!railsOnly)
                {
                    var parts = new RoadProfile.Parts();
                    RoadProfile.Emit(parts, s, start, last, k => leftEdge[k], k => rightEdge[k], true, k => true, -1f);   // Seitenstreifen je Probe
                    Mesh mesh = parts.ToMesh();
                    mesh.name = $"Road_{pieces:D3}";
                    mesh = save(mesh);
                    var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(parent, false);
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.GetComponent<MeshRenderer>();
                    mr.sharedMaterials = mats.Ribbon;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                }
                pieces++;

                Mesh rails = BuildRails(start, last);
                if (rails != null)
                {
                    rails.name = $"Guardrail_{railPieces:D3}";
                    rails = save(rails);
                    var rg = new GameObject(rails.name, typeof(MeshFilter), typeof(MeshRenderer));
                    rg.transform.SetParent(parent, false);
                    rg.GetComponent<MeshFilter>().sharedMesh = rails;
                    rg.GetComponent<MeshRenderer>().sharedMaterial = mats.Rail;
                    railPieces++;
                }
                i = end + 1;
            }
            int urban = 0, mouths = 0;
            for (int k = 0; k < s.Count; k++)
            {
                if (leftEdge[k] == RoadProfile.Edge.Urban || rightEdge[k] == RoadProfile.Edge.Urban) urban++;
                if (leftEdge[k] == RoadProfile.Edge.Mouth || rightEdge[k] == RoadProfile.Edge.Mouth) mouths++;
            }
            Debug.Log($"Straße: {pieces} Abschnitte, {railPieces} mit Leitplanke, Gehweg auf {urban * RoadField.Step / 1000f:0.0} km, " +
                      $"{mouths} Proben an Einmündungen.");
        }

        // Innerorts (OSM-Wohn-/Gewerbegebiet) Bordstein + Gehweg; an Einmündungen offen.
        private void ComputeEdges(StreetNetwork streets)
        {
            var s = road.Samples;
            leftEdge = new RoadProfile.Edge[s.Count];
            rightEdge = new RoadProfile.Edge[s.Count];
            for (int k = 0; k < s.Count; k++)
            {
                Vector3 l = s[k].pos - s[k].side * 9f, r = s[k].pos + s[k].side * 9f;
                leftEdge[k] = terrain.BiomeAt(l.x, l.z) == WorldTerrain.Biome.Urban ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural;
                rightEdge[k] = terrain.BiomeAt(r.x, r.z) == WorldTerrain.Biome.Urban ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural;
            }
            RoadProfile.RemoveShortRuns(leftEdge, RoadProfile.Edge.Urban, 15);
            RoadProfile.RemoveShortRuns(rightEdge, RoadProfile.Edge.Urban, 15);
            if (streets == null) return;
            var scratch = new List<int>();
            foreach (var j in streets.Junctions)
            {
                float r = j.Half;
                road.Query(j.Mouth.x - r, j.Mouth.z - r, j.Mouth.x + r, j.Mouth.z + r, scratch);
                foreach (int k in scratch)
                {
                    Vector3 d = j.Mouth - s[k].pos; d.y = 0f;
                    if (d.magnitude > r) continue;
                    if (Vector3.Dot(d, s[k].side) < 0f) leftEdge[k] = RoadProfile.Edge.Mouth; else rightEdge[k] = RoadProfile.Edge.Mouth;
                }
            }
        }

        // true = diese Probe wird gezeichnet; false = liegt auf einem bereits gezeichneten Abschnitt.
        private bool[] ComputeCoverage(out bool[] lower)
        {
            var s = road.Samples;
            var draw = new bool[s.Count];
            lower = new bool[s.Count];
            var emitted = new Dictionary<long, List<int>>();
            const float cell = 9f, near = 5f, beside = 9f;
            int covered = 0;
            for (int i = 0; i < s.Count; i++)
            {
                Vector3 p = s[i].pos;
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
                bool isCovered = false;
                for (int dx = -1; dx <= 1 && !isCovered; dx++)
                for (int dz = -1; dz <= 1 && !isCovered; dz++)
                {
                    if (!emitted.TryGetValue(Key(cx + dx, cz + dz), out List<int> list)) continue;
                    foreach (int e in list)
                    {
                        var o = s[e];
                        if (Mathf.Abs(o.distance - s[i].distance) < 60f) continue;              // derselbe Abschnitt
                        if (Mathf.Abs(o.pos.y - p.y) > .6f) continue;                             // Brücke/Unterführung
                        if (Mathf.Abs(Vector3.Dot(o.side, s[i].side)) < .8f) continue;           // Kreuzung, nicht parallel
                        float d2 = (o.pos.x - p.x) * (o.pos.x - p.x) + (o.pos.z - p.z) * (o.pos.z - p.z);
                        if (d2 < near * near) { isCovered = true; break; }
                        if (d2 < beside * beside) lower[i] = true;
                    }
                }
                draw[i] = !isCovered;
                if (isCovered) { covered++; continue; }
                long key = Key(cx, cz);
                if (!emitted.TryGetValue(key, out List<int> l)) { l = new List<int>(); emitted[key] = l; }
                l.Add(i);
            }
            // Kurze gezeichnete Inseln (GPS-Rauschen) innerhalb verdeckter Strecken entfernen.
            int runStart = -1;
            for (int i = 0; i <= s.Count; i++)
            {
                bool on = i < s.Count && draw[i];
                if (on && runStart < 0) runStart = i;
                if (!on && runStart >= 0)
                {
                    bool coveredBefore = runStart > 0 && !draw[runStart - 1];
                    bool coveredAfter = i < s.Count;
                    if (coveredBefore && coveredAfter && i - runStart < 6)
                        for (int k = runStart; k < i; k++) draw[k] = false;
                    runStart = -1;
                }
            }
            Debug.Log($"Straße entdoppelt: {covered * RoadField.Step / 1000f:0.0} km liegen auf bereits gebauter Fahrbahn.");
            return draw;
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        // Leitplanke dort, wo das Gelände neben der Straße > 6 m abfällt (Chapman's Peak!).
        private Mesh BuildRails(int from, int to)
        {
            var s = road.Samples;
            int n = to - from + 1;
            var need = new int[n];   // -1 links, +1 rechts, 0 keine
            for (int k = 0; k < n; k++)
            {
                var p = s[from + k];
                for (int sideSign = -1; sideSign <= 1; sideSign += 2)
                {
                    Vector3 q1 = p.pos + p.side * (sideSign * 13f), q2 = p.pos + p.side * (sideSign * 22f);
                    float drop = p.pos.y - Mathf.Min(terrain.DemY(q1.x, q1.z), terrain.DemY(q2.x, q2.z));
                    if (drop > 6f) need[k] = sideSign;
                }
                // an Einmündungen keine Leitplanke quer über die Querstraße
                if ((need[k] < 0 && leftEdge[from + k] == RoadProfile.Edge.Mouth) || (need[k] > 0 && rightEdge[from + k] == RoadProfile.Edge.Mouth)) need[k] = 0;
            }
            // Lücken < 12 m schließen, Stücke < 20 m verwerfen
            Smooth(need, 6, 10);

            var v = new List<Vector3>();
            var tris = new List<int>();
            for (int k = 0; k < n - 1; k++)
            {
                if (need[k] == 0 || need[k + 1] != need[k]) continue;
                float sideSign = need[k];
                var a = s[from + k]; var b = s[from + k + 1];
                Vector3 pa = a.pos + a.side * (sideSign * (a.half + 1.3f));
                Vector3 pb = b.pos + b.side * (sideSign * (b.half + 1.3f));
                // Planke (beidseitig), 0.55–0.85 m über Fahrbahn
                DoubleQuad(v, tris, pa + Vector3.up * .55f, pb + Vector3.up * .55f, pa + Vector3.up * .85f, pb + Vector3.up * .85f);
                // Pfosten alle 4 m
                if (Mathf.Repeat(a.distance, 4f) < RoadField.Step)
                    Post(v, tris, pa, a.tangent, a.side);
            }
            if (v.Count == 0) return null;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Smooth(int[] need, int maxGap, int minRun)
        {
            // Lücken füllen
            for (int k = 1; k < need.Length; k++)
            {
                if (need[k] != 0 || need[k - 1] == 0) continue;
                int e = k; while (e < need.Length && need[e] == 0) e++;
                if (e < need.Length && e - k <= maxGap && need[e] == need[k - 1])
                    for (int m = k; m < e; m++) need[m] = need[k - 1];
                k = e;
            }
            // kurze Stücke weg
            for (int k = 0; k < need.Length;)
            {
                if (need[k] == 0) { k++; continue; }
                int e = k; while (e < need.Length && need[e] == need[k]) e++;
                if (e - k < minRun) for (int m = k; m < e; m++) need[m] = 0;
                k = e;
            }
        }

        private static void DoubleQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i + 1); t.Add(i + 2); t.Add(i + 3);
            int j = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(j); t.Add(j + 1); t.Add(j + 2); t.Add(j + 1); t.Add(j + 3); t.Add(j + 2);
        }

        private static void Post(List<Vector3> v, List<int> t, Vector3 foot, Vector3 tangent, Vector3 side)
        {
            Vector3 f = new Vector3(tangent.x, 0f, tangent.z).normalized * .07f, r = side * .07f;
            Vector3 up = Vector3.up * .9f, down = Vector3.down * .4f;
            Vector3[] c = { -f - r, f - r, f + r, -f + r };
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = foot + c[k] + down, b = foot + c[(k + 1) % 4] + down;
                DoubleQuad(v, t, a, b, a - down + up, b - down + up);
            }
        }
    }

    // Gemeinsames Querprofil für Hauptroute und Querstraßen.
    //   Spalten links->rechts: Schürze, Außenkante, Bordstein/Bankettkante, Fahrbahnrand | gespiegelt
    //   Submeshes: 0 Asphalt, 1 Bankett, 2 gelbe Linie, 3 weiße Linie, 4 Gehweg/Bordstein
    public static class RoadProfile
    {
        public enum Edge : byte { Rural, Urban, Mouth, Median }

        public sealed class Parts
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] T = { new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>() };
            public bool IsEmpty => V.Count == 0;

            public Mesh ToMesh()
            {
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32, subMeshCount = T.Length };
                mesh.SetVertices(V);
                mesh.SetNormals(N);
                mesh.SetUVs(0, UV);
                for (int i = 0; i < T.Length; i++) mesh.SetTriangles(T[i], i);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static void Column(Edge e, float half, int col, out float lat, out float h)
        {
            // col 0..3 = links (negativ), Werte hier als Betrag
            switch (e)
            {
                case Edge.Median:   // Doppelfahrbahn: schmaler erhöhter Bordstein zum Mittelstreifen, kein Randstreifen
                    lat = col == 0 ? half + .8f : col == 1 ? half + .5f : col == 2 ? half + .05f : half; h = col == 0 ? -.4f : col == 3 ? .02f : .12f; break;
                case Edge.Urban:
                    lat = col == 0 ? half + 2.8f : col == 1 ? half + 2f : half; h = col == 0 ? -1.4f : col == 3 ? .02f : .16f; break;
                case Edge.Mouth:
                    lat = col == 0 ? half + 2.6f : col == 1 ? half + 1.6f : col == 2 ? half + .05f : half; h = col == 0 ? -1.4f : col == 1 ? -.04f : col == 2 ? .015f : .02f; break;
                default:
                    lat = col == 0 ? half + 2.6f : col == 1 ? half + 1.6f : col == 2 ? half + .05f : half; h = col == 0 ? -1.4f : col == 1 ? -.06f : col == 2 ? .015f : .02f; break;
            }
        }

        public static void Emit(Parts p, List<RoadField.Sample> s, int from, int to,
                                System.Func<int, Edge> left, System.Func<int, Edge> right, bool edgeLines,
                                System.Func<int, bool> centerLine, float edgeInset = 0f)
        {
            if (to <= from) return;
            int baseIndex = p.V.Count;
            const int cols = 8;
            for (int i = from; i <= to; i++)
            {
                var sm = s[i];
                Vector3 n = Normal(sm);
                for (int c = 0; c < cols; c++)
                {
                    bool isLeft = c < 4;
                    int col = isLeft ? c : 7 - c;
                    Column(isLeft ? left(i) : right(i), sm.half, col, out float lat, out float h);
                    if (isLeft) lat = -lat;
                    p.V.Add(sm.pos + sm.side * lat + Vector3.up * h);
                    p.N.Add(col == 0 ? (n + sm.side * (isLeft ? -1f : 1f)).normalized : n);
                    p.UV.Add(new Vector2(lat / 4f, sm.distance / 6f));
                }
            }
            for (int r = 0; r < to - from; r++)
            {
                int a = baseIndex + r * cols;
                Edge le = left(from + r), re = right(from + r);
                for (int c = 0; c < cols - 1; c++)
                {
                    int sub;
                    if (c == 3) sub = 0;
                    else if (c < 3) sub = le == Edge.Urban || le == Edge.Median ? 4 : 1;
                    else sub = re == Edge.Urban || re == Edge.Median ? 4 : 1;
                    Quad(p.T[sub], a + c, a + c + 1, a + cols + c, a + cols + c + 1);
                }
            }
            if (edgeLines)
            {
                // gelbe Linie an der Fahrstreifenkante (edgeInset = Breite des Seitenstreifens)
                bool per = edgeInset < 0f;
                float e0 = per ? 0f : edgeInset;
                Stripe(p, s, from, to, true, e0 + .05f, e0 + .2f, 2, 0f, 0f, i => left(i) == Edge.Rural, per);
                Stripe(p, s, from, to, false, e0 + .05f, e0 + .2f, 2, 0f, 0f, i => right(i) == Edge.Rural, per);
            }
            Stripe(p, s, from, to, null, -.07f, .07f, 3, 3f, 9f, centerLine);
        }

        // Streifen: side true = linker Rand, false = rechter Rand, null = Mitte.
        private static void Stripe(Parts p, List<RoadField.Sample> s, int from, int to, bool? side, float inner, float outer,
                                   int sub, float dashOn, float dashPeriod, System.Func<int, bool> allowed, bool perSampleInset = false)
        {
            for (int i = from; i < to; i++)
            {
                if (!allowed(i) || !allowed(i + 1)) continue;
                if (perSampleInset && (s[i].inset < 0f || s[i + 1].inset < 0f)) continue;      // Randlinie für diesen Abschnitt aus
                if (dashOn > 0f && Mathf.Repeat(s[i].distance, dashPeriod) >= dashOn) continue;
                int b = p.V.Count;
                for (int k = 0; k < 2; k++)
                {
                    var q = s[i + k];
                    float l, r;
                    float ins = perSampleInset ? q.inset : 0f;
                    if (side == null) { l = inner; r = outer; }
                    else if (side.Value) { l = -q.half + ins + inner; r = -q.half + ins + outer; }
                    else { l = q.half - ins - outer; r = q.half - ins - inner; }
                    Vector3 n = Normal(q);
                    p.V.Add(q.pos + q.side * l + Vector3.up * .035f); p.N.Add(n); p.UV.Add(Vector2.zero);
                    p.V.Add(q.pos + q.side * r + Vector3.up * .035f); p.N.Add(n); p.UV.Add(Vector2.zero);
                }
                Quad(p.T[sub], b, b + 1, b + 2, b + 3);
            }
        }

        private static Vector3 Normal(RoadField.Sample sm)
        {
            Vector3 t = sm.tangent.sqrMagnitude > 1e-6f ? sm.tangent.normalized : Vector3.forward;
            Vector3 n = Vector3.Cross(t, sm.side).normalized;
            return n.y > 0f ? n : Vector3.up;
        }

        // a,b = Reihe r (links->rechts), c,d = Reihe r+1. Oberseite zeigt nach oben.
        public static void Quad(List<int> t, int a, int b, int c, int d)
        {
            t.Add(a); t.Add(c); t.Add(b);
            t.Add(b); t.Add(c); t.Add(d);
        }

        public static void RemoveShortRuns(Edge[] e, Edge kind, int minRun)
        {
            for (int k = 0; k < e.Length;)
            {
                if (e[k] != kind) { k++; continue; }
                int end = k; while (end < e.Length && e[end] == kind) end++;
                if (end - k < minRun) for (int m = k; m < end; m++) e[m] = Edge.Rural;
                k = end;
            }
        }
    }
}
