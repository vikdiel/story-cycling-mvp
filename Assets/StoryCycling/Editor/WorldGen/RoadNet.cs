using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Straßennetz als EIN zusammenhängendes Modell (Route und Querstraßen gleich behandelt):
    //   - Abschnitte = Straßenketten zwischen Kreuzungen (Knoten mit ≥ 3 Anschlüssen) bzw. Enden
    //   - jede Kreuzung bekommt eine Fläche; die Abschnitte enden exakt an deren Rand (keine Überlappung)
    //   - Höhen sitzen an den Knoten (Route: GPX-Profil, sonst Gelände) und sind entlang der Abschnitte
    //     auf eine Maximalsteigung begrenzt -> an Kreuzungen passt alles aufeinander, keine Rampen
    //   - Querschnitt je Probe aus Regeln: innerorts Gehweg, außerorts Randstreifen, Victoria Road
    //     außerorts mit breitem Seitenstreifen (Regex in der Route-Config)
    public sealed class RoadNet
    {
        public sealed class Node
        {
            public long Id; public Vector2 P; public float Y; public bool Fixed;
            public readonly List<int> Segs = new List<int>();
            public bool Signals, Stop;
        }

        public sealed class Segment
        {
            public int A, B;
            public readonly List<RoadField.Sample> S = new List<RoadField.Sample>();   // alle 2 m, A -> B
            public readonly List<bool> Urban = new List<bool>();
            public readonly List<int> Rank = new List<int>();                          // Straßenklasse je Probe
            public float TrimA, TrimB;
            public bool OnRoute;
            public float Length => S.Count > 0 ? S[S.Count - 1].distance : 0f;
        }

        public sealed class End { public int Seg; public bool AtA; public Vector2 Dir; public float Half, Outer, Trim; public bool Urban; }
        public sealed class Junction { public int Node; public readonly List<End> Ends = new List<End>(); }

        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Segment> Segs = new List<Segment>();
        public readonly List<Junction> Junctions = new List<Junction>();

        public const float ScopeDistance = 150f, MaxGrade = .12f, SampleStep = 2f;

        // ------------------------------------------------------------------ Aufbau
        public static RoadNet Build(OsmContext osm, List<Vector3> route, System.Func<float, float, float> demY,
                                    Regex wideShoulder, List<Vector2> buildings)
        {
            var net = new RoadNet();
            var routeHash = new PointHash(route, 10f);

            // 1) Knotengrad aus allen befahrbaren Wegen
            var degree = new Dictionary<long, int>();
            var pos = new Dictionary<long, Vector2>();
            var ways = new List<OsmContext.Street>();
            foreach (var st in osm.Streets)
            {
                if (st.tunnel || st.nodes == null || st.nodes.Count != st.pts.Count || st.pts.Count < 2) continue;
                ways.Add(st);
                for (int k = 0; k < st.pts.Count; k++)
                {
                    pos[st.nodes[k]] = st.pts[k];
                    int inc = (k == 0 || k == st.pts.Count - 1) ? 1 : 2;
                    degree[st.nodes[k]] = (degree.TryGetValue(st.nodes[k], out int d) ? d : 0) + inc;
                }
            }

            // 2) Ketten zwischen Knoten mit Grad != 2 (über Weggrenzen hinweg)
            var used = new Dictionary<long, HashSet<long>>();
            System.Func<long, long, bool> IsUsed = (u, v) => used.TryGetValue(u, out var hs) && hs.Contains(v);
            System.Action<long, long> MarkUsed = (u, v) =>
            {
                if (!used.TryGetValue(u, out var h1)) { h1 = new HashSet<long>(); used[u] = h1; } h1.Add(v);
                if (!used.TryGetValue(v, out var h2)) { h2 = new HashSet<long>(); used[v] = h2; } h2.Add(u);
            };
            var adj = new Dictionary<long, List<Link>>();
            foreach (var st in ways)
                for (int k = 0; k + 1 < st.nodes.Count; k++)
                {
                    long a = st.nodes[k], b = st.nodes[k + 1];
                    if (a == b) continue;
                    if (!adj.TryGetValue(a, out var la)) { la = new List<Link>(); adj[a] = la; }
                    if (!adj.TryGetValue(b, out var lb)) { lb = new List<Link>(); adj[b] = lb; }
                    la.Add(new Link { Other = b, Way = st }); lb.Add(new Link { Other = a, Way = st });
                }
            var chains = new List<Chain>();
            foreach (var start in adj.Keys)
            {
                if (degree[start] == 2) continue;                                     // Ketten starten an Kreuzung/Ende
                foreach (var first in adj[start])
                {
                    if (IsUsed(start, first.Other)) continue;
                    var ch = new Chain();
                    ch.Ids.Add(start); ch.Ways.Add(first.Way);
                    long prev = start, cur = first.Other;
                    MarkUsed(prev, cur);
                    ch.Ids.Add(cur); ch.Ways.Add(first.Way);
                    while (degree[cur] == 2 && ch.Ids.Count < 5000)
                    {
                        Link next = null;
                        foreach (var nl in adj[cur]) if (nl.Other != prev && !IsUsed(cur, nl.Other)) { next = nl; break; }
                        if (next == null) break;
                        MarkUsed(cur, next.Other);
                        prev = cur; cur = next.Other; ch.Ids.Add(cur); ch.Ways.Add(next.Way);
                    }
                    chains.Add(ch);
                }
            }

            // 3) Auf den Korridor um die Route zuschneiden; Schnittenden werden Sackgassen-Knoten
            var nodeIndex = new Dictionary<long, int>();
            System.Func<long, Vector2, int> NodeFor = (id, p) =>
            {
                if (nodeIndex.TryGetValue(id, out int ni)) return ni;
                net.Nodes.Add(new Node { Id = id, P = p });
                nodeIndex[id] = net.Nodes.Count - 1;
                return net.Nodes.Count - 1;
            };
            long cutId = -1;
            foreach (var ch in chains)
            {
                var pts = new List<Vector2>(); foreach (var id in ch.Ids) pts.Add(pos[id]);
                var dense = Densify(pts, ch.Ways, 5f, out var denseWay, out var denseSrc);
                var inScope = new bool[dense.Count];
                for (int i = 0; i < dense.Count; i++) inScope[i] = routeHash.Nearest(dense[i], ScopeDistance + 1f, out _) <= ScopeDistance;
                int r0 = -1;
                for (int i = 0; i <= dense.Count; i++)
                {
                    bool on = i < dense.Count && inScope[i];
                    if (on && r0 < 0) r0 = i;
                    if (!on && r0 >= 0)
                    {
                        int r1 = i - 1;
                        if (r1 - r0 >= 3)
                        {
                            int na = r0 == 0 ? NodeFor(ch.Ids[0], pos[ch.Ids[0]]) : NodeFor(cutId--, dense[r0]);
                            int nb = r1 == dense.Count - 1 ? NodeFor(ch.Ids[ch.Ids.Count - 1], pos[ch.Ids[ch.Ids.Count - 1]]) : NodeFor(cutId--, dense[r1]);
                            var seg = new Segment { A = na, B = nb };
                            var poly = dense.GetRange(r0, r1 - r0 + 1);
                            var pw = denseWay.GetRange(r0, r1 - r0 + 1);
                            net.pendingPoly.Add(poly); net.pendingWay.Add(pw);
                            net.Segs.Add(seg);
                            net.Nodes[na].Segs.Add(net.Segs.Count - 1);
                            net.Nodes[nb].Segs.Add(net.Segs.Count - 1);
                        }
                        r0 = -1;
                    }
                }
            }

            // 4) Knotenhöhen: auf der Route aus dem Routenprofil (fest), sonst Gelände; Steigung begrenzen
            foreach (var n in net.Nodes)
            {
                float d = routeHash.Nearest(n.P, 6f, out int ri);
                if (d <= 5f) { n.Y = route[ri].y; n.Fixed = true; }
                else n.Y = AvgDem(demY, n.P, 15f);
            }
            for (int iter = 0; iter < 60; iter++)
            {
                bool changed = false;
                for (int si = 0; si < net.Segs.Count; si++)
                {
                    var sg = net.Segs[si]; float len = PolyLen(net.pendingPoly[si]);
                    var a = net.Nodes[sg.A]; var b = net.Nodes[sg.B];
                    float lim = MaxGrade * len;
                    if (!b.Fixed && Mathf.Abs(b.Y - a.Y) > lim + .01f) { b.Y = Mathf.Clamp(b.Y, a.Y - lim, a.Y + lim); changed = true; }
                    if (!a.Fixed && Mathf.Abs(a.Y - b.Y) > lim + .01f) { a.Y = Mathf.Clamp(a.Y, b.Y - lim, b.Y + lim); changed = true; }
                }
                if (!changed) break;
            }

            // 5) Geometrie glätten, alle 2 m abtasten, Höhen und Querschnitt je Probe
            var bHash = new PointHash(ToV3(buildings), 20f);
            for (int si = 0; si < net.Segs.Count; si++) net.Sample(si, route, routeHash, demY, wideShoulder, bHash);

            // 6) Kreuzungen: Enden, Richtungen, Beschnitt
            for (int ni = 0; ni < net.Nodes.Count; ni++)
                if (net.Nodes[ni].Segs.Count >= 3) net.BuildJunction(ni);
            net.pendingPoly.Clear(); net.pendingWay.Clear();
            return net;
        }

        private sealed class Link { public long Other; public OsmContext.Street Way; }
        private sealed class Chain { public readonly List<long> Ids = new List<long>(); public readonly List<OsmContext.Street> Ways = new List<OsmContext.Street>(); }

        private readonly List<List<Vector2>> pendingPoly = new List<List<Vector2>>();
        private readonly List<List<OsmContext.Street>> pendingWay = new List<List<OsmContext.Street>>();

        private void Sample(int si, List<Vector3> route, PointHash routeHash, System.Func<float, float, float> demY,
                            Regex wide, PointHash buildings)
        {
            var sg = Segs[si];
            var raw = pendingPoly[si]; var rawWay = pendingWay[si];
            // Chaikin-Glättung (Endpunkte fest), dann gleichmäßig 2 m
            var sm = new List<Vector2>(raw); var smW = new List<OsmContext.Street>(rawWay);
            for (int it = 0; it < 2; it++) Chaikin(sm, smW);
            var p = Densify(sm, smW, SampleStep, out var pw, out _);
            int n = p.Count;
            var arc = new float[n];
            for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + Vector2.Distance(p[i - 1], p[i]);
            float len = arc[n - 1];
            var a = Nodes[sg.A]; var b = Nodes[sg.B];

            // Route-Abschnitt? (≥ 70 % der Proben nahe der Route)
            int near = 0; var routeY = new float[n];
            for (int i = 0; i < n; i++)
            {
                float d = routeHash.Nearest(p[i], 8f, out int ri);
                if (d <= 4f) near++;
                routeY[i] = ri >= 0 ? route[ri].y : float.NaN;
            }
            sg.OnRoute = near >= .7f * n;

            // Höhen: Route -> Routenprofil; sonst Gelände geglättet, in Steigungskegeln beider Enden
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = sg.OnRoute ? routeY[i] : AvgDem(demY, p[i], 8f);
            if (sg.OnRoute) FillNaN(y, i => AvgDem(demY, p[i], 8f));
            y = Smooth(y, sg.OnRoute ? 5 : 8);
            if (!sg.OnRoute)
                for (int i = 0; i < n; i++)
                {
                    // Gelände folgen, aber nie steiler als MaxGrade von beiden Knoten aus
                    float lo = Mathf.Max(a.Y - MaxGrade * arc[i], b.Y - MaxGrade * (len - arc[i]));
                    float hi = Mathf.Min(a.Y + MaxGrade * arc[i], b.Y + MaxGrade * (len - arc[i]));
                    y[i] = lo <= hi ? Mathf.Clamp(y[i], lo, hi) : Mathf.Lerp(a.Y, b.Y, arc[i] / Mathf.Max(len, .01f));
                }
            // Enden exakt auf Knotenhöhe: lineare Korrektur über den ganzen Abschnitt (glatt, keine Stufe)
            float dA = a.Y - y[0], dB = b.Y - y[n - 1];
            for (int i = 0; i < n; i++) y[i] += Mathf.Lerp(dA, dB, arc[i] / Mathf.Max(len, .01f));

            // Ortslage je Probe: Gebäude auf beiden Seiten in 45 m, Mindestlänge 120 m
            var urban = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 t = (p[Mathf.Min(n - 1, i + 1)] - p[Mathf.Max(0, i - 1)]).normalized;
                Vector2 left = new Vector2(-t.y, t.x);
                int l = 0, r = 0;
                foreach (var q in buildings.Within(p[i], 45f)) { if (Vector2.Dot(q - p[i], left) > 0f) l++; else r++; }
                urban[i] = l + r >= 4 && l >= 1 && r >= 1;
            }
            RemoveShortRuns(urban, 60); Invert(urban); RemoveShortRuns(urban, 60); Invert(urban);

            // Querschnitt je Probe
            var half = new float[n]; var inset = new float[n]; var rank = new int[n];
            for (int i = 0; i < n; i++)
            {
                var st = pw[i];
                rank[i] = Rank(st?.highway);
                float lanesHalf = st != null && st.lanes >= 3 ? st.lanes * 3.2f * .5f : rank[i] <= 3 ? 3.3f : 2.8f;
                string label = st == null ? "" : st.name + " " + st.refTag;
                if (urban[i]) { half[i] = lanesHalf + .2f; inset[i] = -1f; }
                else if (wide != null && wide.IsMatch(label)) { half[i] = lanesHalf + 2f; inset[i] = 2f; }
                else if (rank[i] <= 3) { half[i] = lanesHalf + .4f; inset[i] = .25f; }
                else { half[i] = lanesHalf; inset[i] = -1f; }
            }
            half = Smooth(half, 6);

            for (int i = 0; i < n; i++)
            {
                Vector2 t2 = (p[Mathf.Min(n - 1, i + 1)] - p[Mathf.Max(0, i - 1)]);
                Vector3 t = new Vector3(t2.x, 0f, t2.y).normalized;
                if (t.sqrMagnitude < 1e-6f) t = Vector3.forward;
                float dy = (i < n - 1 ? y[i + 1] - y[i] : y[i] - y[i - 1]);
                sg.S.Add(new RoadField.Sample
                {
                    pos = new Vector3(p[i].x, y[i], p[i].y),
                    tangent = (t * SampleStep + Vector3.up * dy).normalized,
                    side = Vector3.Cross(Vector3.up, t).normalized,
                    distance = arc[i], half = half[i], inset = inset[i]
                });
                sg.Urban.Add(urban[i]); sg.Rank.Add(rank[i]);
            }
        }

        // ------------------------------------------------------------------ Kreuzungen
        private void BuildJunction(int ni)
        {
            var node = Nodes[ni];
            var j = new Junction { Node = ni };
            var seen = new HashSet<int>();
            foreach (int si in node.Segs)
            {
                if (!seen.Add(si)) continue;
                var sg = Segs[si];
                if (sg.S.Count < 4) continue;
                bool atA = sg.A == ni;
                // bei Schleifen (A == B) beide Enden
                int k = SampleAt(sg, atA ? 6f : sg.Length - 6f);
                var sm = sg.S[k];
                Vector2 dir = (new Vector2(sm.pos.x, sm.pos.z) - node.P).normalized;
                bool urb = sg.Urban[atA ? 0 : sg.Urban.Count - 1];
                j.Ends.Add(new End { Seg = si, AtA = atA, Dir = dir, Half = sm.half, Outer = sm.half + (urb ? 2f : 1.6f), Urban = urb });
                if (sg.A == sg.B && atA)
                {
                    int k2 = SampleAt(sg, sg.Length - 6f); var s2 = sg.S[k2];
                    j.Ends.Add(new End { Seg = si, AtA = false, Dir = (new Vector2(s2.pos.x, s2.pos.z) - node.P).normalized, Half = s2.half, Outer = s2.half + (urb ? 2f : 1.6f), Urban = urb });
                }
            }
            if (j.Ends.Count < 3) return;
            j.Ends.Sort((u, v) => Mathf.Atan2(u.Dir.y, u.Dir.x).CompareTo(Mathf.Atan2(v.Dir.y, v.Dir.x)));
            int m = j.Ends.Count;
            var tL = new float[m]; var tR = new float[m];
            for (int i = 0; i < m; i++)
            {
                var e1 = j.Ends[i]; var e2 = j.Ends[(i + 1) % m];
                // linke Kante von e1 trifft rechte Kante von e2 (Drehsinn gegen den Uhrzeiger)
                if (EdgeIntersect(e1.Dir, Left(e1.Dir) * e1.Half, e2.Dir, Right(e2.Dir) * e2.Half, out float t1, out float t2))
                { tL[i] = t1; tR[(i + 1) % m] = t2; }
                else { tL[i] = e1.Half; tR[(i + 1) % m] = e2.Half; }
            }
            foreach (var e in j.Ends) e.Trim = 0f;
            for (int i = 0; i < m; i++)
            {
                var e = j.Ends[i]; var sg = Segs[e.Seg];
                float t = Mathf.Max(tL[i], tR[i]) + 1.5f;
                e.Trim = Mathf.Clamp(t, e.Half, Mathf.Min(35f, sg.Length * .45f));
                if (e.AtA) sg.TrimA = Mathf.Max(sg.TrimA, e.Trim); else sg.TrimB = Mathf.Max(sg.TrimB, e.Trim);
            }
            Junctions.Add(j);
        }

        // Schnitt zweier Kantenlinien:  o1 + d1*t1  ==  o2 + d2*t2  (Ursprung = Knoten)
        public static bool EdgeIntersect(Vector2 d1, Vector2 o1, Vector2 d2, Vector2 o2, out float t1, out float t2)
        {
            float den = d1.x * d2.y - d1.y * d2.x;
            t1 = t2 = 0f;
            if (Mathf.Abs(den) < .15f) return false;                     // fast parallel / entgegengesetzt
            Vector2 w = o2 - o1;
            t1 = (w.x * d2.y - w.y * d2.x) / den;
            t2 = (w.x * d1.y - w.y * d1.x) / den;
            return t1 > 0f && t2 > 0f && t1 < 45f && t2 < 45f;
        }

        public static Vector2 Left(Vector2 d) => new Vector2(-d.y, d.x);
        public static Vector2 Right(Vector2 d) => new Vector2(d.y, -d.x);

        // Probe eines Abschnitts an Bogenlänge s (interpoliert, mit Tangente/Seite)
        public static RoadField.Sample At(Segment sg, float s)
        {
            s = Mathf.Clamp(s, 0f, sg.Length);
            int k = SampleAt(sg, s);
            int k2 = Mathf.Min(sg.S.Count - 1, k + 1);
            var a = sg.S[k]; var b = sg.S[k2];
            float t = b.distance > a.distance ? Mathf.Clamp01((s - a.distance) / (b.distance - a.distance)) : 0f;
            var r = a;
            r.pos = Vector3.Lerp(a.pos, b.pos, t); r.distance = s;
            r.half = Mathf.Lerp(a.half, b.half, t);
            return r;
        }

        public static int SampleAt(Segment sg, float s)
        {
            int k = Mathf.Clamp(Mathf.FloorToInt(s / SampleStep), 0, sg.S.Count - 1);
            while (k > 0 && sg.S[k].distance > s) k--;
            while (k < sg.S.Count - 1 && sg.S[k + 1].distance <= s) k++;
            return k;
        }

        public float NodeHeight(int ni) => Nodes[ni].Y;

        // Alle Fahrbahnproben (getrimmt) + Kreuzungsscheiben: für Geländeeinschnitt/Abstandstests
        public RoadField Field()
        {
            var all = new List<RoadField.Sample>();
            foreach (var sg in Segs)
                foreach (var s in sg.S)
                    if (s.distance >= sg.TrimA - .01f && s.distance <= sg.Length - sg.TrimB + .01f) all.Add(s);
            foreach (var j in Junctions)
            {
                var n = Nodes[j.Node]; float r = 0f;
                foreach (var e in j.Ends) r = Mathf.Max(r, e.Trim);
                // Scheibe aus mehreren Proben (Nearest-Suche in 10-m-Zellen findet sie sicher)
                for (int k = 0; k < 8; k++)
                {
                    float ang = k * Mathf.PI / 4f;
                    var q = new Vector3(n.P.x + Mathf.Cos(ang) * r * .5f, n.Y, n.P.y + Mathf.Sin(ang) * r * .5f);
                    all.Add(new RoadField.Sample { pos = q, tangent = Vector3.forward, side = Vector3.right, half = r * .6f, inset = -1f });
                }
                all.Add(new RoadField.Sample { pos = new Vector3(n.P.x, n.Y, n.P.y), tangent = Vector3.forward, side = Vector3.right, half = r, inset = -1f });
            }
            return new RoadField(all);
        }

        // ------------------------------------------------------------------ Helfer
        public static int Rank(string hw)
        {
            switch (hw)
            {
                case "motorway": case "trunk": case "motorway_link": case "trunk_link": return 0;
                case "primary": case "primary_link": return 1;
                case "secondary": case "secondary_link": return 2;
                case "tertiary": case "tertiary_link": return 3;
                default: return 4;
            }
        }

        private static float AvgDem(System.Func<float, float, float> demY, Vector2 p, float r)
        {
            return (demY(p.x, p.y) * 2f + demY(p.x + r, p.y) + demY(p.x - r, p.y) + demY(p.x, p.y + r) + demY(p.x, p.y - r)) / 6f;
        }

        private static void FillNaN(float[] v, System.Func<int, float> fallback)
        {
            int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                if (!float.IsNaN(v[i])) continue;
                int l = i - 1; while (l >= 0 && float.IsNaN(v[l])) l--;
                int r = i + 1; while (r < n && float.IsNaN(v[r])) r++;
                v[i] = l >= 0 && r < n ? Mathf.Lerp(v[l], v[r], (i - l) / (float)(r - l)) : l >= 0 ? v[l] : r < n ? v[r] : fallback(i);
            }
        }

        private static float PolyLen(List<Vector2> p) { float l = 0f; for (int i = 1; i < p.Count; i++) l += Vector2.Distance(p[i - 1], p[i]); return l; }

        private static List<Vector3> ToV3(List<Vector2> v) { var r = new List<Vector3>(v.Count); foreach (var q in v) r.Add(new Vector3(q.x, 0f, q.y)); return r; }

        private static float[] Smooth(float[] v, int rad)
        {
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float s = 0f; int c = 0;
                for (int k = Mathf.Max(0, i - rad); k <= Mathf.Min(v.Length - 1, i + rad); k++) { s += v[k]; c++; }
                r[i] = s / c;
            }
            return r;
        }

        private static void RemoveShortRuns(bool[] v, int minRun)
        {
            for (int k = 0; k < v.Length;)
            {
                if (!v[k]) { k++; continue; }
                int e = k; while (e < v.Length && v[e]) e++;
                if (e - k < minRun) for (int m = k; m < e; m++) v[m] = false;
                k = e;
            }
        }
        private static void Invert(bool[] v) { for (int i = 0; i < v.Length; i++) v[i] = !v[i]; }

        private static void Chaikin(List<Vector2> p, List<OsmContext.Street> w)
        {
            if (p.Count < 3) return;
            var np = new List<Vector2> { p[0] }; var nw = new List<OsmContext.Street> { w[0] };
            for (int i = 0; i < p.Count - 1; i++)
            {
                Vector2 a = p[i], b = p[i + 1];
                if (i > 0) { np.Add(a * .75f + b * .25f); nw.Add(w[i + 1]); }
                if (i < p.Count - 2) { np.Add(a * .25f + b * .75f); nw.Add(w[i + 1]); }
            }
            np.Add(p[p.Count - 1]); nw.Add(w[w.Count - 1]);
            p.Clear(); p.AddRange(np); w.Clear(); w.AddRange(nw);
        }

        // Gleichmäßig verdichten; way[i] = Weg des Segments, das zu Punkt i führt
        private static List<Vector2> Densify(List<Vector2> pts, List<OsmContext.Street> way, float step,
                                             out List<OsmContext.Street> outWay, out List<int> src)
        {
            var r = new List<Vector2> { pts[0] }; outWay = new List<OsmContext.Street> { way[Mathf.Min(1, way.Count - 1)] }; src = new List<int> { 0 };
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int k = 1; k <= n; k++) { r.Add(Vector2.Lerp(a, b, k / (float)n)); outWay.Add(way[i]); src.Add(i); }
            }
            return r;
        }

        // Punkt-Hash für Nächste-Punkt-Suchen (Route, Gebäude)
        public sealed class PointHash
        {
            private readonly List<Vector3> pts; private readonly float cell;
            private readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
            public PointHash(List<Vector3> p, float cell)
            {
                pts = p; this.cell = cell;
                for (int i = 0; i < p.Count; i++)
                {
                    long k = Key(Mathf.FloorToInt(p[i].x / cell), Mathf.FloorToInt(p[i].z / cell));
                    if (!map.TryGetValue(k, out var l)) { l = new List<int>(); map[k] = l; }
                    l.Add(i);
                }
            }
            private static long Key(int x, int z) => ((long)x << 32) | (uint)z;
            public float Nearest(Vector2 q, float maxR, out int idx)
            {
                idx = -1; float best = maxR * maxR;
                int r = Mathf.CeilToInt(maxR / cell), cx = Mathf.FloorToInt(q.x / cell), cz = Mathf.FloorToInt(q.y / cell);
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (map.TryGetValue(Key(cx + dx, cz + dz), out var l))
                        foreach (int i in l)
                        {
                            float d2 = (pts[i].x - q.x) * (pts[i].x - q.x) + (pts[i].z - q.y) * (pts[i].z - q.y);
                            if (d2 < best) { best = d2; idx = i; }
                        }
                return idx >= 0 ? Mathf.Sqrt(best) : float.MaxValue;
            }
            public List<Vector2> Within(Vector2 q, float rad)
            {
                var res = new List<Vector2>();
                int r = Mathf.CeilToInt(rad / cell), cx = Mathf.FloorToInt(q.x / cell), cz = Mathf.FloorToInt(q.y / cell);
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    List<int> l;
                    if (!map.TryGetValue(Key(cx + dx, cz + dz), out l)) continue;
                    foreach (int i in l)
                    {
                        var v = new Vector2(pts[i].x, pts[i].z);
                        if ((v - q).sqrMagnitude <= rad * rad) res.Add(v);
                    }
                }
                return res;
            }
        }
    }
}
