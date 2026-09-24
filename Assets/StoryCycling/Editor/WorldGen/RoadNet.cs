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
            public bool Internal;                    // liegt komplett in einer Kreuzung (z. B. über den Mittelstreifen)
            public bool Oneway;
            public readonly List<byte> LeftKind = new List<byte>(), RightKind = new List<byte>();   // 0 normal, 2 Mittelstreifen
            public float Length => S.Count > 0 ? S[S.Count - 1].distance : 0f;
        }

        public sealed class End { public int Seg; public bool AtA; public int NodeIdx; public Vector2 Dir; public float Half, Outer, Trim; public bool Urban; }
        // Kreuzung = ein oder mehrere dicht beieinander liegende OSM-Knoten (Doppelfahrbahnen, Abbiegespuren)
        public sealed class Junction
        {
            public int Node;                          // erster Knoten (Kompatibilität)
            public readonly List<int> NodesIn = new List<int>();
            public Vector2 Center; public float Y;
            public readonly List<End> Ends = new List<End>();
        }
        public const byte KindNormal = 0, KindMedian = 2;

        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Segment> Segs = new List<Segment>();
        public readonly List<Junction> Junctions = new List<Junction>();
        // Mittelstreifen-Mitten (Lücke > 1 m) alle 20 m: Standorte für Palmen
        public readonly List<Vector3> MedianPalmSpots = new List<Vector3>();

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
                float d = routeHash.Nearest(n.P, 40f, out int ri);
                float dem = AvgDem(demY, n.P, 15f);
                if (d <= 15f) { n.Y = route[ri].y; n.Fixed = true; }                 // Gegenfahrbahn, Einmündungen
                else if (d <= 35f) n.Y = Mathf.Lerp(route[ri].y, dem, Mathf.InverseLerp(15f, 35f, d));
                else n.Y = dem;
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

            // 6) Doppelfahrbahnen: Mittelstreifen-Seite erkennen (kein Randstreifen/Gehweg zur Gegenfahrbahn)
            net.DetectMedians();
            // 7) Kreuzungen: dicht beieinander liegende Knoten zu EINER Kreuzung zusammenfassen
            net.BuildClusters();
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
            foreach (var w in pw) if (w != null && w.oneway) { sg.Oneway = true; break; }

            // Höhen: Route -> Routenprofil; sonst Gelände geglättet, in Steigungskegeln beider Enden
            var y = new float[n];
            bool bridge = false; foreach (var w in pw) if (w != null && w.bridge) { bridge = true; break; }
            for (int i = 0; i < n; i++)
            {
                if (sg.OnRoute) { y[i] = routeY[i]; continue; }
                float dem = AvgDem(demY, p[i], 8f);
                float dR = routeHash.Nearest(p[i], 40f, out int rj);
                // bis 15 m exakt Routenhöhe (Doppelfahrbahn, Promenade), bis 35 m weich ins Gelände
                y[i] = !bridge && rj >= 0 ? Mathf.Lerp(route[rj].y, dem, Mathf.InverseLerp(15f, 35f, dR)) : dem;
            }
            if (sg.OnRoute) FillNaN(y, i => AvgDem(demY, p[i], 8f));
            y = Smooth(y, sg.OnRoute ? 5 : 8);
            if (!sg.OnRoute)
                for (int i = 0; i < n; i++)
                {
                    // Gelände folgen, aber nie steiler als MaxGrade von beiden Knoten aus
                    float lo = Mathf.Max(a.Y - MaxGrade * arc[i], b.Y - MaxGrade * (len - arc[i]));
                    float hi = Mathf.Min(a.Y + MaxGrade * arc[i], b.Y + MaxGrade * (len - arc[i]));
                    y[i] = lo <= hi ? Mathf.Clamp(y[i], lo, hi) : Mathf.Lerp(a.Y, b.Y, arc[i] / Mathf.Max(len, .01f));
                    // nahe der Route nicht vom Steigungskegel wegziehen lassen (Knoten sind dort ohnehin Routenhöhe)
                }
            // Brücken: das Gelände darunter (Fluss, Tal) zählt nicht -> gerade zwischen den Brückenenden
            if (bridge && !sg.OnRoute)
                for (int i = 0; i < n;)
                {
                    if (pw[i] == null || !pw[i].bridge) { i++; continue; }
                    int e = i; while (e < n && pw[e] != null && pw[e].bridge) e++;
                    float y0 = i > 0 ? y[i - 1] : (e < n ? y[e] : y[i]), y1 = e < n ? y[e] : y0;
                    for (int k = i; k < e; k++) y[k] = Mathf.Lerp(y0, y1, (k - i + 1f) / (e - i + 1f));
                    i = e;
                }
            // Enden exakt auf Knotenhöhe: lineare Korrektur über den ganzen Abschnitt (glatt, keine Stufe)
            float dA = a.Y - y[0], dB = b.Y - y[n - 1];
            for (int i = 0; i < n; i++) y[i] += Mathf.Lerp(dA, dB, arc[i] / Mathf.Max(len, .01f));
            // Gegenfahrbahn / Parallelweg direkt neben der Route: exakt Routenhöhe (hat Vorrang vor allem anderen)
            if (!sg.OnRoute)
                for (int i = 0; i < n; i++)
                {
                    float dR = routeHash.Nearest(p[i], 16f, out int rj);
                    if (rj < 0) continue;
                    float w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(9f, 15f, dR));
                    y[i] = Mathf.Lerp(y[i], route[rj].y, w);
                }

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
                sg.LeftKind.Add(KindNormal); sg.RightKind.Add(KindNormal);
            }
        }

        // ------------------------------------------------------------------ Mittelstreifen
        private void DetectMedians()
        {
            var all = new List<Vector3>(); var who = new List<int>(); var idx = new List<int>();
            for (int si = 0; si < Segs.Count; si++)
                if (Segs[si].Oneway)
                    for (int k = 0; k < Segs[si].S.Count; k += 2) { all.Add(Segs[si].S[k].pos); who.Add(si); idx.Add(k); }
            if (all.Count == 0) return;
            var hash = new PointHash(all, 10f);
            int marked = 0;
            for (int si = 0; si < Segs.Count; si++)
            {
                var sg = Segs[si];
                if (!sg.Oneway) continue;
                for (int k = 0; k < sg.S.Count; k++)
                {
                    var sm = sg.S[k]; var q = new Vector2(sm.pos.x, sm.pos.z);
                    foreach (int oi in hash.WithinIdx(q, 25f))
                    {
                        // Partner: andere Einbahn-Fahrbahn, entgegengesetzt, seitlich versetzt
                        if (who[oi] == si) continue;
                        var os = Segs[who[oi]].S[idx[oi]];
                        if (Vector3.Dot(os.tangent, sm.tangent) > -.8f) continue;
                        float lat = Vector3.Dot(os.pos - sm.pos, sm.side);
                        if (Mathf.Abs(lat) > sm.half + os.half + 12f || Mathf.Abs(lat) < sm.half) continue;
                        if (lat > 0f) sg.RightKind[k] = KindMedian; else sg.LeftKind[k] = KindMedian;
                        marked++;
                        // Palme mittig im Mittelstreifen: nur von einer der beiden Fahrbahnen aus (kleinerer Index),
                        // wenn die Lücke > 1 m ist, alle 20 m, nicht in Kreuzungsnähe
                        float gap = Mathf.Abs(lat) - sm.half - os.half;
                        if (si < who[oi] && gap > 1f && Mathf.Floor(sm.distance / 20f) != Mathf.Floor((sm.distance - SampleStep) / 20f) &&
                            sm.distance > 25f && sm.distance < sg.Length - 25f)
                        {
                            Vector3 mid = sm.pos + sm.side * (Mathf.Sign(lat) * (sm.half + gap * .5f));
                            mid.y = (sm.pos.y + os.pos.y) * .5f;
                            MedianPalmSpots.Add(mid);
                        }
                        break;
                    }
                }
            }
            if (marked > 0) Debug.Log($"Straßennetz: Mittelstreifen an {marked * SampleStep / 1000f:0.0} km Doppelfahrbahn erkannt.");
        }

        // ------------------------------------------------------------------ Kreuzungen (Cluster)
        private void BuildClusters()
        {
            // Union-Find: Kreuzungsknoten, die über kurze Abschnitte (≤ 25 m) verbunden sind, bilden eine Kreuzung
            var parent = new int[Nodes.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            System.Func<int, int> Find = null;
            Find = x => parent[x] == x ? x : (parent[x] = Find(parent[x]));
            System.Func<int, bool> IsJ = x => Nodes[x].Segs.Count >= 3;
            foreach (var sg in Segs)
                if (sg.A != sg.B && IsJ(sg.A) && IsJ(sg.B) && sg.Length <= 25f)
                { parent[Find(sg.A)] = Find(sg.B); sg.Internal = true; }

            var groups = new Dictionary<int, List<int>>();
            for (int ni = 0; ni < Nodes.Count; ni++)
            {
                if (!IsJ(ni)) continue;
                int r = Find(ni);
                if (!groups.TryGetValue(r, out var l)) { l = new List<int>(); groups[r] = l; }
                l.Add(ni);
            }
            foreach (var g in groups.Values) BuildCluster(g);
            junctionOfNode = new int[Nodes.Count];
            for (int i = 0; i < junctionOfNode.Length; i++) junctionOfNode[i] = -1;
            for (int ji = 0; ji < Junctions.Count; ji++) foreach (int ni in Junctions[ji].NodesIn) junctionOfNode[ni] = ji;
        }

        private int[] junctionOfNode;
        public int JunctionOf(int node) => junctionOfNode != null && node >= 0 && node < junctionOfNode.Length ? junctionOfNode[node] : -1;

        private void BuildCluster(List<int> nodes)
        {
            var j = new Junction { Node = nodes[0] };
            j.NodesIn.AddRange(nodes);
            foreach (int ni in nodes) { j.Center += Nodes[ni].P; j.Y += Nodes[ni].Y; }
            j.Center /= nodes.Count; j.Y /= nodes.Count;

            foreach (int ni in nodes)
                foreach (int si in Nodes[ni].Segs)
                {
                    var sg = Segs[si];
                    if (sg.Internal || sg.S.Count < 4) continue;
                    for (int endSide = 0; endSide < 2; endSide++)
                    {
                        bool atA = endSide == 0;
                        if ((atA ? sg.A : sg.B) != ni) continue;
                        bool dup = false; foreach (var e0 in j.Ends) if (e0.Seg == si && e0.AtA == atA) dup = true;
                        if (dup) continue;
                        int k = SampleAt(sg, atA ? 6f : sg.Length - 6f);
                        var sm = sg.S[k];
                        Vector2 dir = (new Vector2(sm.pos.x, sm.pos.z) - Nodes[ni].P).normalized;
                        int ek = atA ? 0 : sg.S.Count - 1;
                        bool urb = sg.Urban[ek];
                        j.Ends.Add(new End { Seg = si, AtA = atA, NodeIdx = ni, Dir = dir, Half = sm.half, Outer = sm.half + (urb ? 2f : 1.6f), Urban = urb });
                    }
                }
            if (j.Ends.Count < 3 && nodes.Count == 1) return;
            if (j.Ends.Count < 2) return;

            // Beschnitt: so weit, dass sich die AUSSENKANTEN (Randstreifen/Gehweg) benachbarter Zufahrten nicht mehr
            // überlappen und keine anderen Knoten der Kreuzung im Weg liegen
            foreach (var e in j.Ends)
            {
                var sg = Segs[e.Seg];
                Vector2 P = Nodes[e.NodeIdx].P;
                float t = e.Half;
                foreach (var o in j.Ends)
                {
                    if (o == e) continue;
                    Vector2 Q = Nodes[o.NodeIdx].P;
                    for (int s1 = -1; s1 <= 1; s1 += 2)
                    for (int s2 = -1; s2 <= 1; s2 += 2)
                    {
                        Vector2 pe = P + (s1 < 0 ? Left(e.Dir) : Right(e.Dir)) * e.Outer;
                        Vector2 po = Q + (s2 < 0 ? Left(o.Dir) : Right(o.Dir)) * o.Outer;
                        if (LineX(pe, e.Dir, po, o.Dir, out float te, out float to) && te > 0f && te < 40f && to > -5f && to < 40f &&
                            ((pe + e.Dir * te) - j.Center).magnitude < 40f)
                            t = Mathf.Max(t, te);
                    }
                }
                foreach (int ni in j.NodesIn)
                {
                    if (ni == e.NodeIdx) continue;
                    Vector2 w = Nodes[ni].P - P;
                    float proj = Vector2.Dot(w, e.Dir), lat = Mathf.Abs(e.Dir.x * w.y - e.Dir.y * w.x);
                    if (proj > 0f && lat < e.Outer + 3f) t = Mathf.Max(t, proj + 3f);
                }
                e.Trim = Mathf.Clamp(t + .5f, e.Half, Mathf.Min(35f, sg.Length * .45f));
                if (e.AtA) sg.TrimA = Mathf.Max(sg.TrimA, e.Trim); else sg.TrimB = Mathf.Max(sg.TrimB, e.Trim);
            }
            // Reihenfolge gegen den Uhrzeiger um die Kreuzungsmitte (nach Lage der Beschnittpunkte)
            j.Ends.Sort((u, v) => AngleAround(j.Center, u).CompareTo(AngleAround(j.Center, v)));
            Junctions.Add(j);
        }

        private float AngleAround(Vector2 c, End e)
        {
            var sg = Segs[e.Seg];
            var at = At(sg, e.AtA ? e.Trim : sg.Length - e.Trim);
            return Mathf.Atan2(at.pos.z - c.y, at.pos.x - c.x);
        }

        // Schnitt zweier Geraden p1 + d1*t1 = p2 + d2*t2 (absolut)
        public static bool LineX(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2, out float t1, out float t2)
        {
            float den = d1.x * d2.y - d1.y * d2.x;
            t1 = t2 = 0f;
            if (Mathf.Abs(den) < .15f) return false;
            Vector2 w = p2 - p1;
            t1 = (w.x * d2.y - w.y * d2.x) / den;
            t2 = (w.x * d1.y - w.y * d1.x) / den;
            return true;
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
            {
                if (sg.Internal) continue;
                foreach (var s in sg.S)
                    if (s.distance >= sg.TrimA - .01f && s.distance <= sg.Length - sg.TrimB + .01f) all.Add(s);
            }
            foreach (var j in Junctions)
            {
                float r = 0f;
                foreach (var e in j.Ends) r = Mathf.Max(r, e.Trim + (Nodes[e.NodeIdx].P - j.Center).magnitude);
                var n = new Node { P = j.Center, Y = j.Y };
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
            public List<int> WithinIdx(Vector2 q, float rad)
            {
                var res = new List<int>();
                int r = Mathf.CeilToInt(rad / cell), cx = Mathf.FloorToInt(q.x / cell), cz = Mathf.FloorToInt(q.y / cell);
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    List<int> l;
                    if (!map.TryGetValue(Key(cx + dx, cz + dz), out l)) continue;
                    foreach (int i in l)
                        if ((pts[i].x - q.x) * (pts[i].x - q.x) + (pts[i].z - q.y) * (pts[i].z - q.y) <= rad * rad) res.Add(i);
                }
                return res;
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
