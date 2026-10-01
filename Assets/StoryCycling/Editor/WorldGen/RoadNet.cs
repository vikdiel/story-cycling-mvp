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
            public int OsmDegree;                    // Anzahl Wegenden an diesem OSM-Knoten (0 bei Schnittknoten am Korridorrand)
            public float RouteDist = float.MaxValue; // Abstand zur Route (nur bis ScopeDistance gemessen)
        }

        public sealed class Segment
        {
            public int A, B;
            public readonly List<RoadField.Sample> S = new List<RoadField.Sample>();   // alle 2 m, A -> B
            public readonly List<bool> Urban = new List<bool>();
            public readonly List<int> Rank = new List<int>();                          // Straßenklasse je Probe
            public float TrimA, TrimB;
            public Vector2 Dir0A, Dir0B;             // Richtung vom Knoten weg (~6 m), aus der OSM-Linie ohne Querverschiebung/Aufweitung
            public bool OnRoute;
            public bool Internal;                    // liegt komplett in einer Kreuzung (z. B. über den Mittelstreifen)
            public bool Oneway;
            public readonly List<byte> LeftKind = new List<byte>(), RightKind = new List<byte>();   // 0 normal, 2 Mittelstreifen
            public readonly List<float> LeftGap = new List<float>(), RightGap = new List<float>();   // Mittelstreifenbreite (m), sonst 0
            public readonly List<LaneInfo> Lanes = new List<LaneInfo>();                  // Spuraufteilung je Probe
            public readonly List<bool> Covered = new List<bool>();                         // unter einer Galerie
            public readonly List<float> MaxSpeed = new List<float>();                      // OSM-maxspeed je Probe in km/h (0 = nicht getaggt)
            public bool Roundabout;                  // Kreisfahrbahn
            public bool Fallback;                    // Ersatzfahrbahn entlang der Fahrlinie (kein OSM-Weg im Netz)
            public float Length => S.Count > 0 ? S[S.Count - 1].distance : 0f;
        }

        // Querschnitt einer Probe in Fahrtrichtung des Abschnitts (A -> B), Linksverkehr:
        //   |Sh| L Spuren (Verkehr A->B) | R Spuren (Verkehr B->A) |Sh|
        // Einbahn: alle Spuren in L (Dir +1, Verkehr A->B) bzw. R (Dir -1). W = Spurbreite, Sh = asphaltierter Rand.
        public struct LaneInfo
        {
            public byte L, R; public sbyte Dir; public float W, Sh; public bool Center, Tagged;
            public int N => L + R;
        }

        public sealed class End { public int Seg; public bool AtA; public int NodeIdx; public Vector2 Dir, Dir0; public float Half, Outer, Trim; public bool Urban; public Junction J; }
        // Kreuzung = ein oder mehrere dicht beieinander liegende OSM-Knoten (Doppelfahrbahnen, Abbiegespuren)
        public sealed class Junction
        {
            public int Node;                          // erster Knoten (Kompatibilität)
            public readonly List<int> NodesIn = new List<int>();
            public Vector2 Center; public float Y;
            public float Radius;                      // Ausdehnung der Kreuzung um die Mitte (größter Beschnitt + Knotenabstand)
            public readonly List<End> Ends = new List<End>();
        }
        public const byte KindNormal = 0, KindMedian = 2;

        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Segment> Segs = new List<Segment>();
        public readonly List<Junction> Junctions = new List<Junction>();
        // Mittelstreifen-Mitten (Lücke > 1 m) alle 20 m: Standorte für Palmen
        public readonly List<Vector3> MedianPalmSpots = new List<Vector3>();
        // Ketten(stücke) im Korridor, die NICHT ins Netz aufgenommen wurden (für den Prüfbericht)
        public sealed class DroppedChain { public Vector2 A, B; public float Length; public string Reason; }
        public readonly List<DroppedChain> Dropped = new List<DroppedChain>();
        // Sackgassenenden, die an eine andere Straße angeschlossen wurden (Kartierlücken, siehe ConnectDeadEnds)
        public readonly List<Vector2> GapLinks = new List<Vector2>();
        // Enden kurzer Sackgassen-Stummel (Zufahrten), die nicht ins Netz aufgenommen wurden (siehe PruneStubs)
        public readonly List<Vector2> PrunedStubs = new List<Vector2>();

        public const float ScopeDistance = 150f, MaxGrade = .12f, SampleStep = 2f;
        private const int ScopeMargin = 2;
        public const float RingArmStub = 15f;          // Länge der Zufahrtsstücke am Ring, wenn der Ring im Korridor liegt, die Zufahrt aber nicht

        // ------------------------------------------------------------------ Aufbau
        public static RoadNet Build(OsmContext osm, List<Vector3> route, System.Func<float, float, float> demY,
                                    Regex wideShoulder, List<Vector2> buildings,
                                    RoadRules rules = null, System.Func<Vector2, bool> wideZone = null)
        {
            var net = new RoadNet { rules = rules ?? new RoadRules(), wideZone = wideZone };
            var routeHash = new PointHash(route, 10f);

            // 1) Knotengrad aus allen befahrbaren Wegen
            var degree = new Dictionary<long, int>();
            var pos = new Dictionary<long, Vector2>();
            var ways = new List<OsmContext.Street>();
            foreach (var st in osm.Streets)
            {
                // Tunnel/Galerien gehören zum Netz (Chapman's Peak: tunnel=avalanche_protector) — sonst fehlt dort die Straße
                if (st.nodes == null || st.nodes.Count != st.pts.Count || st.pts.Count < 2) continue;
                ways.Add(st);
                for (int k = 0; k < st.pts.Count; k++)
                {
                    pos[st.nodes[k]] = st.pts[k];
                    int inc = (k == 0 || k == st.pts.Count - 1) ? 1 : 2;
                    degree[st.nodes[k]] = (degree.TryGetValue(st.nodes[k], out int d) ? d : 0) + inc;
                }
            }

            // 1b) Kartierlücken schließen: Sackgassen, die knapp vor einer anderen Straße enden, werden dort eingemündet
            ConnectDeadEnds(osm, ways, pos, degree, routeHash, net.GapLinks);
            PruneStubs(osm, ways, degree, routeHash, net.PrunedStubs);

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

            // 2b) Kreisverkehre: alle Ringstücke eines Kreisverkehrs (zusammenhängende Kreisfahrbahn-Wege) werden zusammen betrachtet; liegt
            // irgendein Teil im Korridor, wird der ganze Ring gebaut (nie vom Korridorrand angeschnitten) und jede Zufahrt behält
            // RingArmStub Meter, damit sie nicht bündig am Ring endet
            var ringComp = new Dictionary<OsmContext.Street, int>(); var ringActive = new List<bool>(); var ringNodeSet = new HashSet<long>();
            {
                var nodeWays = new Dictionary<long, List<OsmContext.Street>>();
                foreach (var st in ways)
                {
                    if (!st.roundabout) continue;
                    foreach (long id in st.nodes) { if (!nodeWays.TryGetValue(id, out var lw)) { lw = new List<OsmContext.Street>(); nodeWays[id] = lw; } lw.Add(st); }
                }
                foreach (var st in ways)
                {
                    if (!st.roundabout || ringComp.ContainsKey(st)) continue;
                    int c = ringActive.Count; bool active = false; var stack = new Stack<OsmContext.Street>(); stack.Push(st); ringComp[st] = c;
                    var compNodes = new List<long>();
                    while (stack.Count > 0)
                    {
                        var w = stack.Pop();
                        foreach (long id in w.nodes)
                        {
                            compNodes.Add(id);
                            if (!active && routeHash.Nearest(pos[id], ScopeDistance + 1f, out _) <= ScopeDistance) active = true;
                            foreach (var w2 in nodeWays[id]) if (!ringComp.ContainsKey(w2)) { ringComp[w2] = c; stack.Push(w2); }
                        }
                    }
                    ringActive.Add(active);
                    if (active) foreach (long id in compNodes) ringNodeSet.Add(id);
                }
            }

            // 3) Auf den Korridor um die Route zuschneiden; Schnittenden werden Sackgassen-Knoten
            var nodeIndex = new Dictionary<long, int>();
            System.Func<long, Vector2, int> NodeFor = (id, p) =>
            {
                if (nodeIndex.TryGetValue(id, out int ni)) return ni;
                net.Nodes.Add(new Node { Id = id, P = p, OsmDegree = degree.TryGetValue(id, out int dg) ? dg : 0,
                                         RouteDist = routeHash.Nearest(p, ScopeDistance + 1f, out _) });
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
                // Hysterese: der Korridorrand wird um ScopeMargin Stützpunkte (à 5 m) nach außen geschoben, damit kein Stück
                // an der Grenze in 1–3 Stützpunkte zerfällt (früher wurden Stücke < 4 Punkte verworfen: Verbinder zwischen zwei
                // Kreuzungsknoten und Schenkel am Korridorrand fehlten, die Kreuzung hatte dann einen abgeschnittenen Arm)
                var keep = (bool[])inScope.Clone();
                for (int i = 0; i < dense.Count; i++)
                    if (inScope[i])
                        for (int d = -ScopeMargin; d <= ScopeMargin; d++) if (i + d >= 0 && i + d < dense.Count) keep[i + d] = true;
                // Kreisverkehr: Ringpunkte aktiver Ringe bleiben immer, an Ringknoten anschließende Zufahrten behalten RingArmStub Meter
                for (int i = 0; i < dense.Count; i++)
                    if (denseWay[i] != null && denseWay[i].roundabout && ringComp.TryGetValue(denseWay[i], out int rc) && ringActive[rc]) keep[i] = true;
                if (ringNodeSet.Count > 0 && !(dense.Count > 1 && denseWay[1] != null && denseWay[1].roundabout))
                    for (int end = 0; end < 2; end++)
                    {
                        if (!ringNodeSet.Contains(end == 0 ? ch.Ids[0] : ch.Ids[ch.Ids.Count - 1])) continue;
                        float cum = 0f;
                        for (int q = 0; q < dense.Count; q++)
                        {
                            int i = end == 0 ? q : dense.Count - 1 - q;
                            if (q > 0) cum += Vector2.Distance(dense[i], dense[end == 0 ? i - 1 : i + 1]);
                            keep[i] = true;
                            if (cum >= RingArmStub) break;
                        }
                    }
                int r0 = -1;
                for (int i = 0; i <= dense.Count; i++)
                {
                    bool on = i < dense.Count && keep[i];
                    if (on && r0 < 0) r0 = i;
                    if (!on && r0 >= 0)
                    {
                        int r1 = i - 1;
                        if (r1 - r0 >= 1)                                            // mindestens ein Stück (2 Punkte)
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
                        else net.Dropped.Add(new DroppedChain { A = dense[r0], B = dense[r1], Length = PolyLen(dense.GetRange(r0, r1 - r0 + 1)), Reason = "Einzelpunkt" });
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
            // 6) Doppelfahrbahnen: Mittelstreifen-Seite erkennen (kein Randstreifen/Gehweg zur Gegenfahrbahn)
            // 7) Kreuzungen: dicht beieinander liegende Knoten zu EINER Kreuzung zusammenfassen
            // Die Kreuzungen bestimmen ihrerseits Ortslage und Breite der Zufahrten (eine Klasse je Kreuzung, durchgehende Straßen
            // ohne Randstufe): dazu wird nach der ersten Runde mit den Kreuzungsdaten neu abgetastet (Plan* -> Sample mit Vorgaben).
            var bHash = new PointHash(ToV3(buildings), 20f);
            System.Action sampleAll = () =>
            {
                for (int si = 0; si < net.Segs.Count; si++) net.Sample(si, route, routeHash, demY, wideShoulder, bHash);
                net.DetectMedians();
                net.BuildClusters();
            };
            sampleAll();
            for (int pass = 0; pass < 7; pass++)
            {
                if (pass == 0) net.PlanJunctionClasses(); else net.PlanThroughTapers();
                net.ResetSampling();
                sampleAll();
            }
            if (net.MedianMarked > 0) Debug.Log($"Straßennetz: Mittelstreifen an {net.MedianMarked * SampleStep / 1000f:0.0} km Doppelfahrbahn erkannt.");
            net.pendingPoly.Clear(); net.pendingWay.Clear();
            return net;
        }

        // OSM-Straßen enden oft wenige Meter vor der Straße, in die sie münden (Kartierlücke). Ein Sackgassenende (Knoten, den nur ein
        // Wegende nutzt; keine Barriere/noexit/Wendekreis; im Korridor) wird mit der nächsten anderen befahrbaren Straße verbunden, wenn es
        // höchstens GapReach (gleichnamig: GapReachNamed) von deren Mittellinie entfernt ist und die Straße dorthin zeigt (Richtung der
        // letzten ~10 m auf den Zielpunkt höchstens GapMaxAngle): Der Zielpunkt wird als neuer Knoten in die andere Straße eingefügt (bzw.
        // rastet auf einen vorhandenen Knoten bis GapSnap Abstand ein) und die Sackgasse dorthin verlängert -> echte Einmündung.
        private const float GapReach = 12f, GapReachNamed = 25f, GapSnap = 4f, GapMaxAngle = 50f, GapSnapAngle = 65f;
        private static void ConnectDeadEnds(OsmContext osm, List<OsmContext.Street> ways, Dictionary<long, Vector2> pos,
                                            Dictionary<long, int> degree, PointHash routeHash, List<Vector2> linked)
        {
            float cosMax = Mathf.Cos(GapMaxAngle * Mathf.Deg2Rad), cosSnap = Mathf.Cos(GapSnapAngle * Mathf.Deg2Rad);
            long synth = long.MinValue / 2;
            var box = new List<Vector4>();
            System.Func<OsmContext.Street, Vector4> Box = st =>
            {
                var b = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                foreach (var q in st.pts) { b.x = Mathf.Min(b.x, q.x); b.y = Mathf.Min(b.y, q.y); b.z = Mathf.Max(b.z, q.x); b.w = Mathf.Max(b.w, q.y); }
                return b;
            };
            foreach (var st in ways) box.Add(Box(st));
            var ends = new List<KeyValuePair<int, bool>>();
            for (int wi = 0; wi < ways.Count; wi++)
            {
                var st = ways[wi]; if (st.bridge || st.tunnel) continue;
                if (degree[st.nodes[0]] == 1) ends.Add(new KeyValuePair<int, bool>(wi, true));
                if (degree[st.nodes[st.nodes.Count - 1]] == 1) ends.Add(new KeyValuePair<int, bool>(wi, false));
            }
            foreach (var en in ends)
            {
                int wi = en.Key; bool atStart = en.Value; var st = ways[wi];
                long id = atStart ? st.nodes[0] : st.nodes[st.nodes.Count - 1];
                if (degree[id] != 1 || osm.DeadEndNodes.Contains(id)) continue;
                Vector2 e = atStart ? st.pts[0] : st.pts[st.pts.Count - 1];
                if (routeHash.Nearest(e, ScopeDistance + 1f, out _) > ScopeDistance) continue;
                // Richtung der letzten ~10 m, nach außen
                Vector2 far = e;
                for (int i = atStart ? 1 : st.pts.Count - 2; i >= 0 && i < st.pts.Count; i += atStart ? 1 : -1)
                { far = st.pts[i]; if (Vector2.Distance(e, far) >= 10f) break; }
                Vector2 dir = e - far; if (dir.sqrMagnitude < .01f) continue; dir.Normalize();

                // nächster Punkt einer anderen Straße im Blickfeld der Sackgasse (Abweichung von der Fahrtrichtung <= GapMaxAngle):
                // die Straße mündet dort gerade oder schräg ein, statt sich am Fußpunkt (Lot) um bis zu 90° abzuknicken.
                // Gleichnamige Straße (Kartierlücke innerhalb einer Straße) darf weiter entfernt sein.
                float bestD = GapReachNamed; int bj = -1, bs = -1; Vector2 bp = default;
                for (int wj = 0; wj < ways.Count; wj++)
                {
                    var o = ways[wj]; if (wj == wi || o.bridge || o.tunnel) continue;
                    float reach = st.name.Length > 0 && st.name == o.name ? GapReachNamed : GapReach;
                    var bb = box[wj]; if (e.x < bb.x - reach || e.x > bb.z + reach || e.y < bb.y - reach || e.y > bb.w + reach) continue;
                    for (int s = 0; s + 1 < o.pts.Count; s++)
                    {
                        Vector2 a = o.pts[s], b = o.pts[s + 1], ab = b - a; float len = ab.magnitude;
                        if (len < 1e-3f || DistSegPt(e, a, b) >= Mathf.Min(bestD, reach)) continue;
                        for (float u = 0f; ; u += .5f)
                        {
                            float uu = Mathf.Min(u, len); Vector2 p = a + ab * (uu / len); float d = Vector2.Distance(p, e);
                            if (d < Mathf.Min(bestD, reach) && (d <= .5f || Vector2.Dot((p - e) / d, dir) >= cosMax)) { bestD = d; bj = wj; bs = s; bp = p; }
                            if (uu >= len) break;
                        }
                    }
                }
                if (bj < 0) continue;

                // Ziel: vorhandener Knoten in Fangreichweite oder neuer Knoten im Fußpunkt
                var tgt = ways[bj]; long tid; Vector2 tp; bool snap = false; int sn = 0;
                float da = Vector2.Distance(tgt.pts[bs], bp), db = Vector2.Distance(tgt.pts[bs + 1], bp);
                sn = da <= db ? bs : bs + 1;
                if (Mathf.Min(da, db) <= GapSnap)
                {
                    Vector2 v = tgt.pts[sn] - e; float dl = v.magnitude;
                    snap = dl <= bestD + GapSnap && (dl < .5f || Vector2.Dot(v / dl, dir) >= cosSnap);
                }
                if (snap) { tid = tgt.nodes[sn]; tp = tgt.pts[sn]; if (st.nodes.Contains(tid)) continue; }
                else { tid = synth++; tp = bp; }

                // die Verlängerung darf keine dritte Straße kreuzen
                bool crosses = false;
                for (int wj = 0; wj < ways.Count && !crosses; wj++)
                {
                    var o = ways[wj]; if (wj == wi || wj == bj || o.bridge || o.tunnel) continue;
                    var bb = box[wj]; if (Mathf.Max(e.x, tp.x) < bb.x || Mathf.Min(e.x, tp.x) > bb.z || Mathf.Max(e.y, tp.y) < bb.y || Mathf.Min(e.y, tp.y) > bb.w) continue;
                    for (int s = 0; s + 1 < o.pts.Count && !crosses; s++) crosses = SegmentsCross(e, tp, o.pts[s], o.pts[s + 1]);
                }
                if (crosses) continue;

                if (!snap)
                {
                    var np2 = new List<Vector2>(tgt.pts); var nn2 = new List<long>(tgt.nodes);
                    np2.Insert(bs + 1, tp); nn2.Insert(bs + 1, tid);
                    ways[bj] = tgt.WithPath(np2, nn2); box[bj] = Box(ways[bj]);
                    pos[tid] = tp; degree[tid] = 2;
                }
                var np = new List<Vector2>(st.pts); var nn = new List<long>(st.nodes);
                if (atStart) { np.Insert(0, tp); nn.Insert(0, tid); } else { np.Add(tp); nn.Add(tid); }
                ways[wi] = st.WithPath(np, nn); box[wi] = Box(ways[wi]);
                degree[id] = 2; degree[tid] += 1;
                linked.Add(e);
            }
        }
        // Ein Sackgassen-Stummel hinter einer Einmündung (Zufahrt/Grundstückseinfahrt, OSM-Endknoten < StubMax vor dem Kreuzungsknoten) ist
        // kein Straßenstück: als eigener Schenkel ergäbe er einen Asphaltblock mit Bordstein rund um eine Fläche von wenigen Metern und
        // verzerrte die Kreuzung. Er wird abgeschnitten; die Einmündung wird zur Ecke bzw. Durchfahrt. Nicht angetastet werden Stummel an
        // der Route (Fahrlinie), an Barriere-/Wendekreisknoten und alles außerhalb des Korridors.
        public const float StubMax = 10f;
        private static void PruneStubs(OsmContext osm, List<OsmContext.Street> ways, Dictionary<long, int> degree, PointHash routeHash, List<Vector2> pruned)
        {
            for (int wi = 0; wi < ways.Count; wi++)
                for (int side = 0; side < 2; side++)
                {
                    var st = ways[wi]; if (st == null || st.bridge || st.tunnel) break;
                    int n = st.nodes.Count, ei = side == 0 ? 0 : n - 1, step = side == 0 ? 1 : -1;
                    long id = st.nodes[ei];
                    if (degree[id] != 1 || osm.DeadEndNodes.Contains(id)) continue;
                    Vector2 e = st.pts[ei];
                    if (routeHash.Nearest(e, ScopeDistance + 1f, out _) > ScopeDistance || routeHash.Nearest(e, 4f, out _) <= 4f) continue;
                    float len = 0f; int j = -1;
                    for (int k = ei + step; k >= 0 && k < n; k += step)
                    {
                        len += Vector2.Distance(st.pts[k - step], st.pts[k]);
                        if (len >= StubMax) break;
                        if (degree[st.nodes[k]] >= 3) { j = k; break; }
                    }
                    if (j < 0) continue;
                    int keep = side == 0 ? n - j : j + 1;                       // Punkte, die vom Weg übrig bleiben
                    if (keep < 2) ways[wi] = null;
                    else
                    {
                        var np = side == 0 ? st.pts.GetRange(j, keep) : st.pts.GetRange(0, keep);
                        var nn = side == 0 ? st.nodes.GetRange(j, keep) : st.nodes.GetRange(0, keep);
                        ways[wi] = st.WithPath(np, nn);
                    }
                    degree[st.nodes[j]] -= 1; degree[id] = 0;
                    pruned.Add(e);
                }
            ways.RemoveAll(w => w == null);
        }
        private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
            return d1 * d2 < 0f && d3 * d4 < 0f;
        }
        private static float DistSegPt(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float l2 = ab.sqrMagnitude, t = l2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return Vector2.Distance(p, a + ab * t);
        }
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private sealed class Link { public long Other; public OsmContext.Street Way; }
        private sealed class Chain { public readonly List<long> Ids = new List<long>(); public readonly List<OsmContext.Street> Ways = new List<OsmContext.Street>(); }

        private RoadRules rules = new RoadRules();
        public RoadRules Rules => rules;
        private System.Func<Vector2, bool> wideZone;
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
            // Kreisfahrbahnen zählen hier nicht (gegenüberliegende Ringstücke sähen wie eine Doppelfahrbahn aus)
            foreach (var w in pw) if (w != null && w.oneway && !w.roundabout) { sg.Oneway = true; break; }

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
            if (ovUrban != null && ovUrban[si] != null && ovUrban[si].Length == n) for (int i = 0; i < n; i++) urban[i] = ovUrban[si][i];

            // Querschnitt je Probe aus den OSM-Tags (Spuren, Richtung, Breite, Lage) + Streckenregeln
            var half = new float[n]; var inset = new float[n]; var rank = new int[n]; var shift = new float[n];
            var lanes = new LaneInfo[n]; var covered = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var st = pw[i];
                rank[i] = Rank(st?.highway);
                Vector2 tg = (p[Mathf.Min(n - 1, i + 1)] - p[Mathf.Max(0, i - 1)]);
                bool fwd = st == null || WayForward(st, p[i], tg);
                string label = st == null ? "" : st.name + " " + st.refTag;
                bool wideRoad = wide != null && wide.IsMatch(label) && (wideZone == null || wideZone(p[i]));
                lanes[i] = LanesFor(st, fwd, rank[i], urban[i], wideRoad, rules, out half[i], out inset[i], out shift[i]);
                covered[i] = st != null && st.tunnelKind == "avalanche_protector";
                if (st != null && st.roundabout) sg.Roundabout = true;
            }
            half = SmoothArc(half, arc, 12f);
            shift = SmoothArc(shift, arc, 12f);
            // Vorgaben aus den Kreuzungen (PlanThroughTapers): Breite/Mitte der Zufahrt an die durchgehende Gegenseite angleichen
            if (ovHalf != null && ovHalf[si] != null && ovHalf[si].Length == n)
                for (int i = 0; i < n; i++)
                {
                    float ov = ovHalf[si][i], h0 = half[i], h1 = h0 + ov;
                    if (ov < 0f) h1 = Mathf.Max(h1, Mathf.Min(h0, lanes[i].N * lanes[i].W * .5f));     // Verengen nie unter die Fahrspuren
                    half[i] = h1; shift[i] += ovShift[si][i];
                }
            // Lage-Tags (placement): OSM-Linie liegt nicht in der Fahrbahnmitte -> Proben auf die Mitte schieben
            // Tangenten aus den UNverschobenen Punkten: sonst geht in die Tangente der schon verschobene Vorgänger ein (bei 1,75 m
            // Versatz und 2 m Probenabstand kippt sie, Proben laufen rückwärts) und die Fahrbahn faltet sich zu einem Zickzack
            var p0 = new List<Vector2>(p);
            {
                int ia = 0, ib = n - 1; while (ia < n - 1 && arc[ia] < 6f) ia++; while (ib > 0 && arc[ib] > len - 6f) ib--;
                sg.Dir0A = (p0[ia] - p0[0]).normalized; sg.Dir0B = (p0[ib] - p0[n - 1]).normalized;
            }
            for (int i = 0; i < n; i++)
            {
                if (Mathf.Abs(shift[i]) < .01f) continue;
                Vector2 tg = (p0[Mathf.Min(n - 1, i + 1)] - p0[Mathf.Max(0, i - 1)]).normalized;
                p[i] = p0[i] + new Vector2(tg.y, -tg.x) * shift[i];       // rechts = (t.z, -t.x)
            }

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
                sg.Urban.Add(urban[i]); sg.Rank.Add(rank[i]); sg.Lanes.Add(lanes[i]); sg.Covered.Add(covered[i]);
                sg.MaxSpeed.Add(pw[i] != null ? pw[i].maxspeedKmh : 0f);
                sg.LeftKind.Add(KindNormal); sg.RightKind.Add(KindNormal); sg.LeftGap.Add(0f); sg.RightGap.Add(0f);
            }
        }

        // ------------------------------------------------------------------ Spurmodell
        // Läuft der Abschnitt an dieser Stelle in OSM-Wegrichtung? (Tangente gegen das nächste Wegstück)
        private static bool WayForward(OsmContext.Street st, Vector2 q, Vector2 tangent)
        {
            float best = float.MaxValue; Vector2 dir = Vector2.zero;
            for (int k = 0; k + 1 < st.pts.Count; k++)
            {
                Vector2 a = st.pts[k], ab = st.pts[k + 1] - a;
                float l2 = ab.sqrMagnitude; if (l2 < 1e-6f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / l2);
                float d = (a + ab * t - q).sqrMagnitude;
                if (d < best) { best = d; dir = ab; }
            }
            return Vector2.Dot(dir, tangent) >= 0f;
        }

        public static float LaneWidthFor(OsmContext.Street st, RoadRules rules)
        {
            if (st == null) return rules.laneWidthMajor;
            if (st.roundabout) return rules.roundaboutLaneWidth;
            string hw = st.highway ?? "";
            if (hw.EndsWith("_link")) return rules.linkLaneWidth;
            switch (hw)
            {
                case "motorway": case "trunk": case "primary": case "secondary": return rules.laneWidthMajor;
                case "tertiary": return rules.laneWidthMinor;
                default: return rules.laneWidthLocal;
            }
        }

        // Spuraufteilung, halbe Asphaltbreite, Randlinien-Einzug (-1 = keine gelbe Randlinie) und Querverschiebung
        // der Fahrbahnmitte gegenüber der OSM-Linie (placement-Tags), alles im Rahmen des Abschnitts (A -> B).
        public static LaneInfo LanesFor(OsmContext.Street st, bool fwd, int rank, bool urban, bool wideRoad, RoadRules rules,
                                        out float half, out float inset, out float shift)
        {
            var li = new LaneInfo(); shift = 0f;
            bool one = st != null && (st.oneway || st.roundabout);
            int n = st == null ? 2 : st.lanes > 0 ? st.lanes
                  : st.lanesForward + st.lanesBackward > 0 ? st.lanesForward + st.lanesBackward : one ? 1 : 2;
            n = Mathf.Clamp(n, 1, 8);
            int f, b;
            if (one) { bool rev = st.onewayReverse; f = rev ? 0 : n; b = rev ? n : 0; }
            else if (n == 1) { f = 1; b = 0; }                                        // einspurig, Begegnungsverkehr
            else
            {
                f = st != null && st.lanesForward > 0 ? Mathf.Min(st.lanesForward, n)
                  : st != null && st.lanesBackward > 0 ? Mathf.Max(0, n - st.lanesBackward) : (n + 1) / 2;
                b = n - f;
            }
            float lw = LaneWidthFor(st, rules);
            if (rules.useWidthTag && st != null && st.width > 0f)
            {
                float per = st.width / n;
                if (per >= 2.5f && per <= 5.5f) lw = per;                              // width = Fahrbahnbreite ohne Ränder
            }
            float sh;
            if (urban) { sh = rules.urbanGutter; inset = -1f; }
            else if (wideRoad) { sh = rules.wideShoulder; inset = Mathf.Max(0f, sh - .12f); }
            else if (rank <= 3) { sh = rules.ruralShoulder; inset = Mathf.Max(0f, sh - .12f); }
            else { sh = 0f; inset = -1f; }
            half = sh + n * lw * .5f;

            li.W = lw; li.Sh = sh; li.Tagged = st != null && (st.lanes > 0 || st.lanesForward > 0 || st.lanesBackward > 0);
            li.L = (byte)(fwd ? f : b); li.R = (byte)(fwd ? b : f);
            li.Dir = (sbyte)(!one ? 0 : ((fwd == !st.onewayReverse) ? 1 : -1));
            li.Center = !one && f > 0 && b > 0 && (rank <= 3 || li.Tagged || rules.markUntaggedLocalRoads);

            // placement: Lage der OSM-Linie, Spuren je Richtung von links gezählt (Wegrichtung)
            if (st != null)
            {
                float wl = n * lw; float pos = -1f;
                string pf = !string.IsNullOrEmpty(st.placementForward) ? st.placementForward : st.placement;
                if (!string.IsNullOrEmpty(pf)) pos = PlacementPos(pf, lw);
                else if (!string.IsNullOrEmpty(st.placementBackward)) { float pb = PlacementPos(st.placementBackward, lw); if (pb >= 0f) pos = wl - pb; }
                if (pos >= 0f)
                {
                    float sft = Mathf.Clamp(wl * .5f - pos, -wl * .5f, wl * .5f);    // Wegrichtung, nach rechts positiv
                    shift = fwd ? sft : -sft;
                }
            }
            return li;
        }

        // "left_of:2" -> Abstand der Linie vom linken Fahrbahnrand (ohne Randstreifen); -1 = unbekannt
        private static float PlacementPos(string v, float lw)
        {
            int c = v.IndexOf(':'); if (c < 0) return -1f;
            if (!int.TryParse(v.Substring(c + 1), out int j) || j < 1) return -1f;
            string k = v.Substring(0, c);
            float frac = k == "left_of" ? 0f : k == "middle_of" ? .5f : k == "right_of" ? 1f : -1f;
            return frac < 0f ? -1f : (j - 1 + frac) * lw;
        }

        // Klasse (Gehweg/Randstreifen) einer Ersatzfahrbahn: sie wird je Probe von der nächsten Straße übernommen und flackert dort,
        // wo zwei Straßen mit verschiedener Klasse gleich nah sind -> kürzeste Klassenstrecke < MinClassRunLength der Nachbarklasse zuschlagen
        private static void SmoothUrbanRuns(Segment sg)
        {
            for (int it = 0; it < 32; it++)
            {
                int n = sg.Urban.Count, bs = -1, be = -1; float bl = MinClassRunLength;
                for (int i = 0; i < n;)
                {
                    int e = i; while (e + 1 < n && sg.Urban[e + 1] == sg.Urban[i]) e++;
                    float len = sg.S[e].distance - sg.S[i].distance + SampleStep;
                    if ((i > 0 || e < n - 1) && len < bl) { bl = len; bs = i; be = e; }
                    i = e + 1;
                }
                if (bs < 0) return;
                for (int k = bs; k <= be; k++) sg.Urban[k] = !sg.Urban[k];
            }
        }

        // Fahrlinien-Abschnitte ohne Netz (GPX abseits jeder OSM-Straße) als Ersatzfahrbahn ins Netz aufnehmen,
        // damit die Route NIE ohne Straße ist. Überlappt 3 Punkte ins Netz (Vereinigung schließt nahtlos an).
        // Zusätzlich dort, wo die Fahrlinie zwar im Netz ist, aber neben allen Fahrbahnstreifen liegt (Bogen quer
        // durch eine Kreuzung): die Kurve des Radfahrers ist immer befahrbare Fläche.
        public int AddRouteFallback(List<Vector3> pts, List<float> half, List<float> inset, List<bool> off)
        {
            var all = new List<RoadField.Sample>(); var allUrban = new List<bool>();
            foreach (var g in Segs) for (int k = 0; k < g.S.Count; k++) { all.Add(g.S[k]); allUrban.Add(g.Urban[k]); }
            var field = new RoadField(all);
            var need = new bool[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                need[i] = off[i];
                if (need[i]) continue;
                if (!field.Nearest(pts[i].x, pts[i].z, 15f, out int si, out _)) { need[i] = true; continue; }
                var s = field.Samples[si]; Vector3 d = pts[i] - s.pos;
                Vector3 tf = new Vector3(s.tangent.x, 0f, s.tangent.z).normalized;
                need[i] = Mathf.Abs(Vector3.Dot(d, s.side)) > s.half - .3f || Mathf.Abs(Vector3.Dot(d, tf)) > 1.6f;
            }
            int added = 0;
            for (int i = 0; i < pts.Count;)
            {
                if (!need[i]) { i++; continue; }
                int e = i; while (e < pts.Count && need[e]) e++;
                int a0 = Mathf.Max(0, i - 3), a1 = Mathf.Min(pts.Count - 1, e + 2);
                if (a1 - a0 >= 1)
                {
                    var sg = new Segment { OnRoute = true, Fallback = true };
                    float dist = 0f;
                    for (int k = a0; k <= a1; k++)
                    {
                        if (k > a0) dist += Vector3.Distance(pts[k - 1], pts[k]);
                        Vector3 t = pts[Mathf.Min(a1, k + 1)] - pts[Mathf.Max(a0, k - 1)];
                        Vector3 tf = new Vector3(t.x, 0f, t.z).normalized; if (tf.sqrMagnitude < 1e-6f) tf = Vector3.forward;
                        float h = half[k] > .5f ? half[k] : 3.5f;
                        sg.S.Add(new RoadField.Sample { pos = pts[k], tangent = t.sqrMagnitude > 1e-6f ? t.normalized : tf,
                                                        side = Vector3.Cross(Vector3.up, tf).normalized, distance = dist, half = h, inset = inset[k] });
                        bool urb = field.Nearest(pts[k].x, pts[k].z, 30f, out int ui, out _) && allUrban[ui];
                        sg.Urban.Add(urb); sg.Rank.Add(1); sg.Covered.Add(false); sg.MaxSpeed.Add(0f);
                        sg.Lanes.Add(new LaneInfo { L = 1, R = 1, W = h, Sh = 0f });
                        sg.LeftKind.Add(KindNormal); sg.RightKind.Add(KindNormal); sg.LeftGap.Add(0f); sg.RightGap.Add(0f);
                    }
                    SmoothUrbanRuns(sg);
                    Nodes.Add(new Node { Id = -900000 - added * 2, P = new Vector2(pts[a0].x, pts[a0].z), Y = pts[a0].y, Fixed = true });
                    Nodes.Add(new Node { Id = -900001 - added * 2, P = new Vector2(pts[a1].x, pts[a1].z), Y = pts[a1].y, Fixed = true });
                    sg.A = Nodes.Count - 2; sg.B = Nodes.Count - 1;
                    Segs.Add(sg); Nodes[sg.A].Segs.Add(Segs.Count - 1); Nodes[sg.B].Segs.Add(Segs.Count - 1);
                    added++;
                }
                i = e;
            }
            return added;
        }

        // ------------------------------------------------------------------ Mittelstreifen
        public int MedianMarked;
        private List<(int, int, int, int)> medianPairs = new List<(int, int, int, int)>();      // Mittelstreifen-Partnerproben der letzten Abtastung
        private bool IsForced(int si, int k) => ovForce != null && ovForce[si] != null && k < ovForce[si].Length && ovForce[si][k] >= 0;
        private static float TravelSign(Segment sg, int k) => sg.Lanes[k].Dir < 0 ? -1f : 1f;      // Fahrtrichtung gegen die Kettenrichtung (Einbahn)
        private void DetectMedians()
        {
            var all = new List<Vector3>(); var who = new List<int>(); var idx = new List<int>();
            for (int si = 0; si < Segs.Count; si++)
                if (Segs[si].Oneway)
                    for (int k = 0; k < Segs[si].S.Count; k += 2) { all.Add(Segs[si].S[k].pos); who.Add(si); idx.Add(k); }
            MedianMarked = 0;
            if (all.Count == 0) return;
            var hash = new PointHash(all, 10f);
            int marked = 0;
            var urbanOr = new List<(int, int, int, int)>();          // (Abschnitt, Probe, Partnerabschnitt, Partnerprobe)
            var palms = new List<(int, int, Vector3)>();
            for (int si = 0; si < Segs.Count; si++)
            {
                var sg = Segs[si];
                if (!sg.Oneway) continue;
                for (int k = 0; k < sg.S.Count; k++)
                {
                    var sm = sg.S[k]; var q = new Vector2(sm.pos.x, sm.pos.z);
                    // Partner: andere Einbahn-Fahrbahn, entgegengesetzt, seitlich versetzt — der mit dem kleinsten
                    // Längsversatz (quer gegenüber), sonst wird die Mittelstreifenbreite dort überschätzt, wo die
                    // Fahrbahnen zusammenlaufen (Palmen/Aussparung landen dann auf der Gegenfahrbahn)
                    int best = -1; float bestAlong = float.MaxValue;
                    foreach (int oi in hash.WithinIdx(q, 25f))
                    {
                        if (who[oi] == si) continue;
                        var cand = Segs[who[oi]].S[idx[oi]];
                        // Gegenrichtung = Fahrtrichtung (die Kettenrichtung ist beliebig: beide Fahrbahnen einer Doppelfahrbahn können am selben Knoten beginnen)
                        if (Vector3.Dot(cand.tangent * TravelSign(Segs[who[oi]], idx[oi]), sm.tangent * TravelSign(sg, k)) > -.8f) continue;
                        float la = Vector3.Dot(cand.pos - sm.pos, sm.side);
                        if (Mathf.Abs(la) > sm.half + cand.half + 12f || Mathf.Abs(la) < sm.half) continue;
                        float along = Mathf.Abs(Vector3.Dot(cand.pos - sm.pos, sm.tangent));
                        if (along < bestAlong) { bestAlong = along; best = oi; }
                    }
                    // Mittelstreifen nur, wenn der Partner wirklich quer gegenüber liegt (Probenabstand 2 m je Fahrbahn, jede 2. Probe
                    // im Index -> bis ~2 m Längsversatz). Ein Partner nur "irgendwo daneben" (Fahrbahnen laufen erst zusammen /
                    // auseinander, Abbiegespur) ist keine Doppelfahrbahn — sonst bekämen diese Ränder Mittelstreifen-Bord und
                    // gelbe Randlinien mitten in der Kreuzung.
                    if (best >= 0 && bestAlong <= 2.5f)
                    {
                        int oi = best;
                        var os = Segs[who[oi]].S[idx[oi]];
                        float lat = Vector3.Dot(os.pos - sm.pos, sm.side);
                        float gap = Mathf.Abs(lat) - sm.half - os.half;
                        if (lat > 0f) { sg.RightKind[k] = KindMedian; sg.RightGap[k] = gap; }
                        else { sg.LeftKind[k] = KindMedian; sg.LeftGap[k] = gap; }
                        marked++;
                        urbanOr.Add((si, k, who[oi], idx[oi]));
                        // Palme mittig im Mittelstreifen: nur von einer der beiden Fahrbahnen aus (kleinerer Index),
                        // wenn die Lücke > 1 m ist, alle 20 m, nicht in Kreuzungsnähe
                        if (si < who[oi] && gap > 1f && Mathf.Floor(sm.distance / 20f) != Mathf.Floor((sm.distance - SampleStep) / 20f) &&
                            sm.distance > 25f && sm.distance < sg.Length - 25f)
                        {
                            Vector3 mid = sm.pos + sm.side * (Mathf.Sign(lat) * (sm.half + gap * .5f));
                            mid.y = (sm.pos.y + os.pos.y) * .5f;
                            palms.Add((si, k, mid));
                        }
                    }
                }
            }
            // Mittelstreifenläufe unter MedianMinRun, die nicht an einer Kreuzung enden, sind Flackern der Partnererkennung am Rand des
            // Suchbereichs (Fahrbahnen ~20 m auseinander): sie würden ockerfarbene Randstreifen durch kurze Bordstücke unterbrechen
            for (int si = 0; si < Segs.Count; si++)
            {
                var sg = Segs[si]; int n = sg.S.Count; if (n == 0 || !sg.Oneway) continue;
                bool junA = Nodes[sg.A].Segs.Count >= 3, junB = Nodes[sg.B].Segs.Count >= 3;
                for (int sd = 0; sd < 2; sd++)
                {
                    var kinds = sd == 0 ? sg.LeftKind : sg.RightKind; var gaps = sd == 0 ? sg.LeftGap : sg.RightGap;
                    for (int k = 0; k < n;)
                    {
                        if (kinds[k] != KindMedian) { k++; continue; }
                        int e = k; while (e + 1 < n && kinds[e + 1] == KindMedian) e++;
                        float len = sg.S[e].distance - sg.S[k].distance + SampleStep;
                        if (len < MedianMinRun && !((k == 0 && junA) || (e == n - 1 && junB)))
                            for (int q = k; q <= e; q++) { kinds[q] = KindNormal; gaps[q] = 0f; marked--; }
                        k = e + 1;
                    }
                }
            }
            foreach (var (psi, pk, pmid) in palms)
                if (Segs[psi].LeftKind[pk] == KindMedian || Segs[psi].RightKind[pk] == KindMedian) MedianPalmSpots.Add(pmid);
            urbanOr.RemoveAll(t => Segs[t.Item1].LeftKind[t.Item2] != KindMedian && Segs[t.Item1].RightKind[t.Item2] != KindMedian);
            // Beide Fahrbahnen einer Doppelfahrbahn gehören zur selben Ortslage: sonst ist das Bord des Mittelstreifens auf der
            // einen Seite Gehweg (hell), auf der anderen Randstreifen (ocker), und die Übergänge liegen versetzt
            // (Proben, deren Klasse die Kreuzungsplanung vorgibt, bleiben: die Planung hat sie für beide Fahrbahnen gleich gesetzt)
            var urb = new List<(int, int, bool)>(urbanOr.Count);
            foreach (var (a, ka, b, kb) in urbanOr)
                if (!IsForced(a, ka) && !IsForced(b, kb)) urb.Add((a, ka, Segs[a].Urban[ka] || Segs[b].Urban[kb]));
            foreach (var (a, ka, u) in urb) Segs[a].Urban[ka] = u;
            medianPairs = urbanOr;
            MedianMarked = marked;
        }

        // ------------------------------------------------------------------ Kreuzungen bestimmen Ortslage und Breite der Zufahrten
        // Vorgaben für die nächste Abtastung (je Abschnitt, je Probe); null = keine
        private bool[][] ovUrban; private sbyte[][] ovForce; private float[][] ovHalf, ovShift;
        public const float MinClassRunLength = 20f;                  // kürzeste Klassenstrecke (Gehweg <-> Randstreifen) je Abschnitt, in Metern (Abtastabstand 0,5-2 m)
        public const float JunctionClassZone = 24f;                  // die Kreuzungsklasse gilt vom Knoten bis Beschnitt + 24 m

        // Löscht Abtastung, Kreuzungen und Mittelstreifen (Vorgaben bleiben) für die nächste Runde
        private void ResetSampling()
        {
            foreach (var sg in Segs)
            {
                sg.S.Clear(); sg.Urban.Clear(); sg.Rank.Clear(); sg.LeftKind.Clear(); sg.RightKind.Clear(); sg.LeftGap.Clear(); sg.RightGap.Clear();
                sg.Lanes.Clear(); sg.Covered.Clear(); sg.MaxSpeed.Clear();
                sg.TrimA = sg.TrimB = 0f; sg.Internal = false; sg.Oneway = false; sg.Roundabout = false; sg.OnRoute = false;
            }
            Junctions.Clear(); MedianPalmSpots.Clear(); junctionOfNode = null;
        }

        // Eine Klasse je Kreuzung: Mehrheit der Zufahrten (Gleichstand: außerorts). Sie gilt für Eckenbögen und Kreuzungsränder sowie für
        // die Zufahrten vom Knoten bis Beschnitt + JunctionClassZone; danach ist keine Klassenstrecke kürzer als MinClassRunLength (außer zwischen zwei Kreuzungen, die sich die Strecke teilen).
        // So entstehen keine Gehweg-Flecken in ländlichen Kreuzungen (und umgekehrt): die Klasse wechselt nur als gerader Schnitt
        // quer zur Straße, außerhalb der Kreuzung.
        private void PlanJunctionClasses()
        {
            var cur = new bool[Segs.Count][]; var force = new sbyte[Segs.Count][]; var near = new float[Segs.Count][]; var locked = new bool[Segs.Count][];
            for (int si = 0; si < Segs.Count; si++)
            {
                cur[si] = Segs[si].Urban.ToArray(); force[si] = new sbyte[cur[si].Length]; near[si] = new float[cur[si].Length]; locked[si] = new bool[cur[si].Length];
                for (int k = 0; k < cur[si].Length; k++) { force[si][k] = -1; near[si][k] = float.MaxValue; }
            }
            var jc = new bool[Junctions.Count];
            for (int ji = 0; ji < Junctions.Count; ji++)
            {
                var j = Junctions[ji]; int u = 0, r = 0;
                foreach (var e in j.Ends)
                {
                    var sg = Segs[e.Seg]; float s = Mathf.Min(sg.Length, e.Trim + 8f);
                    if (sg.Urban[SampleAt(sg, e.AtA ? s : sg.Length - s)]) u++; else r++;
                }
                jc[ji] = u > r;
                foreach (var e in j.Ends)
                {
                    var sg = Segs[e.Seg]; float zone = Mathf.Min(sg.Length, e.Trim + JunctionClassZone);
                    for (int k = 0; k < sg.S.Count; k++)
                    {
                        float ds = e.AtA ? sg.S[k].distance : sg.Length - sg.S[k].distance;
                        if (ds <= zone && ds < near[e.Seg][k]) { near[e.Seg][k] = ds; force[e.Seg][k] = (sbyte)(jc[ji] ? 1 : 0); }
                        if (ds <= e.Trim + 8f) locked[e.Seg][k] = true;
                    }
                }
            }
            for (int si = 0; si < Segs.Count; si++)
            {
                var sg = Segs[si];
                if (sg.Internal) { int ji = JunctionOf(sg.A); if (ji >= 0) for (int k = 0; k < cur[si].Length; k++) force[si][k] = (sbyte)(jc[ji] ? 1 : 0); }
            }
            // Doppelfahrbahn: die Partnerprobe gegenüber erhält dieselbe Vorgabe (beide Fahrbahnen wechseln an derselben Stelle).
            // Widersprechen sich zwei Vorgaben: eine Probe im gesperrten Bereich ihrer Kreuzung behält ihre Klasse, sonst gilt die Vorgabe
            // der näheren Kreuzung (Gleichstand: Gehweg); sind beide gesperrt, behält jede ihre eigene.
            var pf = new List<(int, int, sbyte)>();
            foreach (var (a, ka, b, kb) in medianPairs)
            {
                if (ka >= force[a].Length || kb >= force[b].Length) continue;
                sbyte fa = force[a][ka], fb = force[b][kb];
                if ((fa < 0 && fb < 0) || fa == fb) continue;
                sbyte f;
                if (fa < 0) f = fb; else if (fb < 0) f = fa;
                else if (locked[a][ka] != locked[b][kb]) f = locked[a][ka] ? fa : fb;
                else if (locked[a][ka]) continue;
                else f = near[a][ka] < near[b][kb] ? fa : near[b][kb] < near[a][ka] ? fb : (sbyte)1;
                pf.Add((a, ka, f)); pf.Add((b, kb, f));
            }
            foreach (var (a, ka, f) in pf)
                if (!locked[a][ka]) force[a][ka] = f;
            for (int si = 0; si < Segs.Count; si++)
            {
                var sg = Segs[si];
                for (int k = 0; k < cur[si].Length; k++) if (force[si][k] >= 0) cur[si][k] = force[si][k] == 1;
            }
            // nicht vorgegebene Partnerproben: Ortslage vereinigen (wie DetectMedians)
            var orv = new List<(int, int, bool)>();
            foreach (var (a, ka, b, kb) in medianPairs)
                if (ka < force[a].Length && kb < force[b].Length && force[a][ka] < 0 && force[b][kb] < 0) orv.Add((a, ka, cur[a][ka] || cur[b][kb]));
            foreach (var (a, ka, u) in orv) cur[a][ka] = u;
            for (int si = 0; si < Segs.Count; si++)
            {
                MergeShortRuns(Segs[si], cur[si], locked[si], MinClassRunLength);
                SlideFlips(Segs[si], cur[si], locked[si]);
            }
            ovUrban = cur; ovForce = force;
        }

        // Ein Klassenwechsel (Gehweg <-> Randstreifen) mitten im Abschnitt liegt nicht im Scheitel einer engen Kurve: dort wäre der
        // Schnitt quer zur Straße an der Innenkante nur Bruchteile eines Meters lang und schief. Der Wechsel rutscht bis 14 m zur ruhigsten
        // Stelle (kleinste Richtungsänderung auf ±10 m), nie in gesperrte Proben (Kreuzungsnähe) und nie so, dass eine Strecke < 20 m entsteht.
        private void SlideFlips(Segment sg, bool[] cls, bool[] locked)
        {
            int n = cls.Length; const int win = 5; const float reach = 14f;
            System.Func<int, float> turn = k =>
            {
                if (k - win < 0 || k + win >= n) return 0f;
                Vector2 a = new Vector2(sg.S[k].pos.x - sg.S[k - win].pos.x, sg.S[k].pos.z - sg.S[k - win].pos.z);
                Vector2 b = new Vector2(sg.S[k + win].pos.x - sg.S[k].pos.x, sg.S[k + win].pos.z - sg.S[k].pos.z);
                return Vector2.Angle(a, b);
            };
            for (int f = 1; f + 1 < n; f++)
            {
                if (cls[f] == cls[f + 1] || turn(f) <= 20f) continue;
                // Läufe links/rechts des Wechsels
                int l0 = f; while (l0 > 0 && cls[l0 - 1] == cls[f]) l0--;
                int r1 = f + 1; while (r1 + 1 < n && cls[r1 + 1] == cls[f + 1]) r1++;
                int best = f; float bestT = turn(f);
                for (int g = Mathf.Max(1, f - 12); g <= Mathf.Min(n - 2, f + 12); g++)
                {
                    if (Mathf.Abs(sg.S[g].distance - sg.S[f].distance) > reach) continue;
                    // beide Läufe bleiben >= MinClassRunLength (Schnitt zwischen g und g + 1)
                    if (RunStart(sg, l0) + MinClassRunLength > (sg.S[g].distance + sg.S[g + 1].distance) * .5f || RunEnd(sg, r1) - MinClassRunLength < (sg.S[g].distance + sg.S[g + 1].distance) * .5f) continue;
                    bool ok = true; for (int k = Mathf.Min(f, g) + 1; k <= Mathf.Max(f, g); k++) if (locked[k]) ok = false;
                    if (!ok) continue;
                    float t = turn(g); if (t < bestT - 3f) { bestT = t; best = g; }
                }
                if (best == f) continue;
                bool left = cls[f];
                if (best > f) for (int k = f + 1; k <= best; k++) cls[k] = left; else for (int k = best + 1; k <= f; k++) cls[k] = cls[f + 1];
                f = Mathf.Max(f, best);
            }
        }

        // ---- durchgehende Straßen: zwei (fast) gegenüberliegende Zufahrten einer Kreuzung ("Durchgang", Abweichung <= 25° von gerade)
        // haben am Beschnitt dieselbe Querausdehnung: Randstufen und Mittenversatz werden über ThroughTaperLength in die Zufahrt
        // hinein ausgeglichen (Breite und seitliche Verschiebung der Fahrbahnmitte gleiten von der Kreuzung weg auf das eigene Maß zurück).
        public const float ThroughMaxDeviation = 25f, ThroughMaxOffset = 3f, ThroughTaperLength = 18f, ThroughPlateau = 3f, ThroughMaxTrim = 30f, ForkStation = .5f;

        public sealed class ThroughGroup
        {
            public End X; public readonly List<End> Ys = new List<End>();      // X: die einzelne Zufahrt, Ys: Gegenseite (1 = Paar, 2 = Gabelung)
            public Vector2 Axis, Org;                                         // Achse (von der Gegenseite nach X), Ursprung
            public float XLo, XHi, YLo, YHi, XFloor, YFloor;                  // Querausdehnung am Beschnitt (rechts/links der Achse), Mindesthalbbreite (Spurbreite)
            public bool Pair => Ys.Count == 1;
            public float XStation, YStation;                                  // Bogenlänge ab Knoten, an der die Querausdehnung gemessen wird
            public bool XMobile, YMobile;                                     // lang genug für Plateau + Übergang (sonst bleibt die Zufahrt, wie sie ist)
            public bool Fixable => Pair ? XMobile || YMobile : XMobile && YMobile;
        }

        private Vector2 TrimPoint(End e, float station)
        {
            var sg = Segs[e.Seg]; var ts = At(sg, e.AtA ? station : sg.Length - station);
            return new Vector2(ts.pos.x, ts.pos.z);
        }

        private void LateralExtent(End e, Vector2 left, Vector2 org, float station, out float lo, out float hi, out float floorHalf)
        {
            var sg = Segs[e.Seg]; float s = e.AtA ? station : sg.Length - station;
            var ts = At(sg, s); var li = sg.Lanes[SampleAt(sg, s)];
            Vector2 p = new Vector2(ts.pos.x, ts.pos.z), side = new Vector2(ts.side.x, ts.side.z);
            float c = Vector2.Dot(p - org, left), h = ts.half * Mathf.Abs(Vector2.Dot(side, left));
            lo = c - h; hi = c + h; floorHalf = li.N * li.W * .5f * Mathf.Abs(Vector2.Dot(side, left));
        }

        // Die Querausdehnung wird am Knoten gemessen (ForkStation): dort stoßen die Abschlusskanten der Streifen aneinander -- und der Ort ist
        // unabhängig von den Breiten (der Beschnitt wandert mit ihnen und ließe die Messung von Abtastrunde zu Abtastrunde springen);
        // bei einer Gabelung öffnen sich die beiden Gegenzufahrten erst weiter draußen (am Beschnitt, bis 25 m, wären ihre Ränder weit auseinander)
        private ThroughGroup MakeGroup(End x, List<End> ys)
        {
            var g = new ThroughGroup { X = x };
            g.Ys.AddRange(ys);
            g.XStation = ForkStation; g.YStation = ForkStation;
            Vector2 dy = Vector2.zero, py = Vector2.zero;
            foreach (var y in ys) { dy += y.Dir0; py += TrimPoint(y, ForkStation); }
            dy /= ys.Count; py /= ys.Count;
            // Achse aus den OSM-Richtungen (ohne Verschiebung/Aufweitung): stabil gegen die Anpassungen, die hier gemessen werden
            g.Axis = (x.Dir0 - dy).normalized; g.Org = (TrimPoint(x, g.XStation) + py) * .5f;
            Vector2 left = Left(g.Axis);
            LateralExtent(x, left, g.Org, g.XStation, out g.XLo, out g.XHi, out g.XFloor);
            g.YLo = float.MaxValue; g.YHi = float.MinValue;
            foreach (var y in ys)
            {
                LateralExtent(y, left, g.Org, ForkStation, out float lo, out float hi, out float fl);
                g.YLo = Mathf.Min(g.YLo, lo); g.YHi = Mathf.Max(g.YHi, hi); g.YFloor = Mathf.Max(g.YFloor, fl);
            }
            return g;
        }

        // Zufahrt mit Beschnitt <= 20 m (weiter draußen liegt der Querschnitt nicht mehr "an" der Kreuzung), lang genug für Beschnitt + Plateau +
        // halber Übergang, keine Kreisfahrbahn/Ersatzfahrbahn
        private bool ThroughLeg(End e)
        {
            var sg = Segs[e.Seg];
            // Fahrbahnen einer Doppelfahrbahn gleichen sich nicht der Gegenseite an: ihr Querschnitt endet am Mittelstreifen (PlanMedianGaps)
            return !sg.Roundabout && !sg.Fallback && !sg.Internal && sg.S.Count > 1 && sg.Length >= ForkStation + 2f && !IsMedianLeg(e);
        }

        public List<ThroughGroup> ThroughGroups(Junction j)
        {
            var res = new List<ThroughGroup>();
            var E = new List<End>();
            foreach (var e in j.Ends) if (ThroughLeg(e)) E.Add(e);
            int n = E.Count; if (n < 2) return res;
            var cand = new List<int>[n]; for (int i = 0; i < n; i++) cand[i] = new List<int>();
            float cosDev = Mathf.Cos(ThroughMaxDeviation * Mathf.Deg2Rad);
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    // gegenüberliegend (<= 25° von gerade) und die OSM-Linien fast fluchtend (Knotenversatz quer zur Achse <= 3 m); beides aus
                    // der OSM-Geometrie, unabhängig von den (angepassten) Breiten -> die Zuordnung ist in jeder Abtastrunde dieselbe
                    if (Vector2.Dot(E[a].Dir0, E[b].Dir0) > -cosDev) continue;
                    Vector2 left = Left((E[a].Dir0 - E[b].Dir0).normalized);
                    if (Mathf.Abs(Vector2.Dot(Nodes[E[b].NodeIdx].P - Nodes[E[a].NodeIdx].P, left)) > ThroughMaxOffset) continue;
                    cand[a].Add(b); cand[b].Add(a);
                }
            for (int a = 0; a < n; a++)
            {
                var ys = new List<End>();
                if (cand[a].Count == 1 && cand[cand[a][0]].Count == 1 && cand[a][0] > a) ys.Add(E[cand[a][0]]);
                else if (cand[a].Count == 2 && cand[cand[a][0]].Count == 1 && cand[cand[a][1]].Count == 1 &&
                         Vector2.Dot(E[cand[a][0]].Dir0, E[cand[a][1]].Dir0) > .6f) { ys.Add(E[cand[a][0]]); ys.Add(E[cand[a][1]]); }
                else continue;
                var grp = MakeGroup(E[a], ys);
                // Paar: das Plateau reicht bis hinter den Beschnitt, der Übergang beginnt erst dahinter -- die Zufahrt muss dafür lang genug sein
                // (eine zu kurze bleibt, wie sie ist, die andere gleicht sich an). Gabelung: Plateau nur 3 m
                System.Func<End, bool> mobile = e => Segs[e.Seg].Length >= (ys.Count == 1 ? Mathf.Min(e.Trim, ThroughMaxTrim) : 0f) + ThroughPlateau + ThroughTaperLength * .5f;
                grp.XMobile = mobile(E[a]); grp.YMobile = true; foreach (var y in ys) grp.YMobile &= mobile(y);
                if (!grp.Fixable) { if (ys.Count == 1) res.Add(grp); continue; }
                if (ys.Count == 2)
                {
                    // Gabelung: nur wenn sich die beiden Zufahrten am Knoten berühren/überlappen (Aufspaltung in einer Kreuzung).
                    // Eine echte Doppelfahrbahn mit Mittelstreifen (Lücke > 1 m) bleibt, wie sie ist.
                    Vector2 lf = Left(grp.Axis);
                    LateralExtent(ys[0], lf, grp.Org, ForkStation, out float l0, out float h0, out _); LateralExtent(ys[1], lf, grp.Org, ForkStation, out float l1, out float h1, out _);
                    if (Mathf.Max(l0, l1) - Mathf.Min(h0, h1) > 1f) continue;
                }
                res.Add(grp);
            }
            return res;
        }

        private static long EndKey(End e) => (long)e.Seg * 2 + (e.AtA ? 1 : 0);
        public const float ThroughMaxHalfAdd = 3f, ThroughMaxShiftAdd = 2.5f;

        // Mittelstreifen bis an die Kreuzung: die Fahrbahnen einer Doppelfahrbahn laufen in OSM am Knoten zusammen (und werden dort durch
        // die Kreuzungsplanung breiter); der Mittelstreifen endete dann als Keil weit vor der Kreuzung und ließ eine offene Fläche.
        public const float MedianMinGap = 1.4f, MedianGapFull = 12f, MedianGapFade = 30f, MedianMinRun = 20f, MedianGapSlope = .12f;

        private bool MedianMarkAt(End e)
        {
            var sg = Segs[e.Seg]; if (!sg.Oneway || sg.S.Count < 2) return false;
            float target = e.AtA ? ForkStation : sg.Length - ForkStation;
            int k = 0; float best = float.MaxValue;
            for (int i = 0; i < sg.S.Count; i++) { float d = Mathf.Abs(sg.S[i].distance - target); if (d < best) { best = d; k = i; } }
            return sg.LeftKind[k] == KindMedian || sg.RightKind[k] == KindMedian;
        }

        // Gegenfahrbahn einer Doppelfahrbahn an dieser Kreuzung: gleichgerichtete Einbahn-Zufahrt an einem ANDEREN Knoten, 2-18 m seitlich versetzt,
        // beide mit Mittelstreifen. (Gemeinsamer Knoten = Aufspaltung/Zusammenführung einer Straße, keine Doppelfahrbahn an einer Kreuzung.)
        private End MedianPartner(End e)
        {
            if (e.J == null || !MedianMarkAt(e)) return null;
            Vector2 left = Left(e.Dir0), p0 = Nodes[e.NodeIdx].P;
            foreach (var f in e.J.Ends)
            {
                if (f == e || f.NodeIdx == e.NodeIdx || Vector2.Dot(e.Dir0, f.Dir0) < .9f) continue;
                var fs = Segs[f.Seg]; if (!fs.Oneway || fs.Internal || fs.S.Count < 2) continue;
                Vector2 d = Nodes[f.NodeIdx].P - p0;
                float lat = Mathf.Abs(Vector2.Dot(d, left)), along = Mathf.Abs(Vector2.Dot(d, e.Dir0));
                if (lat < 5f || lat > 18f || along > 12f) continue;
                if (MedianMarkAt(f)) return f;
            }
            return null;
        }
        private bool IsMedianLeg(End e) => MedianPartner(e) != null;

        // Für die Prüfung (RoadChecks.MedianReach): Doppelfahrbahn-Zufahrt e -> Mitte des Mittelstreifens und Lücke zwischen den Fahrbahnrändern
        // im Abstand 'station' hinter dem Beschnitt (Haltelinie) der Zufahrt bzw. ihres Partners (der spätere zählt); false = keine Doppelfahrbahn / kein Partner quer gegenüber
        public bool MedianProbe(End e, float station, out Vector2 mid, out float gap)
        {
            mid = default; gap = 0f;
            var sg = Segs[e.Seg]; if (sg.Internal || sg.Roundabout || sg.Fallback || sg.S.Count < 2) return false;
            var f = MedianPartner(e); if (f == null) return false;
            // der Mittelstreifen beginnt dort, wo beide Fahrbahnen da sind: hinter dem späteren der beiden Beschnitte (Knoten der Fahrbahnen können versetzt liegen)
            float later = Mathf.Max(e.Trim, Vector2.Dot(Nodes[f.NodeIdx].P - Nodes[e.NodeIdx].P, e.Dir0) + f.Trim);
            float s0 = e.AtA ? later + station : sg.Length - (later + station);
            if (s0 < 0f || s0 > sg.Length) return false;
            var sm = At(sg, s0); var fs = Segs[f.Seg];
            int bj = -1; float bd = 3f;
            for (int j = 0; j < fs.S.Count; j++)
            {
                float al = Mathf.Abs(Vector3.Dot(fs.S[j].pos - sm.pos, sm.tangent));
                if (al < bd && Mathf.Abs(Vector3.Dot(fs.S[j].pos - sm.pos, sm.side)) < 25f) { bd = al; bj = j; }
            }
            if (bj < 0) return false;
            float lat = Vector3.Dot(fs.S[bj].pos - sm.pos, sm.side);
            if (Mathf.Abs(lat) < sm.half) return false;
            gap = Mathf.Abs(lat) - sm.half - fs.S[bj].half;
            Vector3 m3 = sm.pos + sm.side * (Mathf.Sign(lat) * (sm.half + gap * .5f));
            mid = new Vector2(m3.x, m3.z);
            return true;
        }

        // Je Zufahrt einer Doppelfahrbahn: Mittelstreifenlücke von der Kreuzung bis ~MedianGapFull m hinter den Beschnitt mindestens
        // MedianMinGap, danach in MedianGapFade m auslaufend. Jede Fahrbahn räumt ihre Mittelstreifenseite um die halbe Fehlbreite:
        // zuerst durch schmalere Halbbreite (nie unter die Fahrspuren), der Rest durch seitliche Verschiebung nach außen (Außenkante bleibt).
        private void PlanMedianGaps()
        {
            foreach (var jn in Junctions)
                foreach (var e in jn.Ends)
                {
                    var sg = Segs[e.Seg]; int si = e.Seg, n = sg.S.Count;
                    if (sg.Internal || sg.Roundabout || sg.Fallback || n < 2) continue;
                    var f = MedianPartner(e); if (f == null) continue;
                    var fs = Segs[f.Seg];
                    // Lücke zur Partnerfahrbahn quer gegenüber (aus der Geometrie der beiden Zufahrten, nicht aus den Markierungen: dort kann
                    // der Partner von Probe zu Probe wechseln)
                    var gap = new float[n]; var side = new float[n]; var ok = new bool[n];
                    bool divided = false;
                    for (int k = 0; k < n; k++)
                    {
                        float st = e.AtA ? sg.S[k].distance : sg.Length - sg.S[k].distance; if (st > 90f) continue;
                        var sm = sg.S[k]; int bj = -1; float bd = 3f;
                        for (int j = 0; j < fs.S.Count; j++)
                        {
                            float al = Mathf.Abs(Vector3.Dot(fs.S[j].pos - sm.pos, sm.tangent));
                            if (al < bd && Mathf.Abs(Vector3.Dot(fs.S[j].pos - sm.pos, sm.side)) < 25f) { bd = al; bj = j; }
                        }
                        if (bj < 0) continue;
                        float lat = Vector3.Dot(fs.S[bj].pos - sm.pos, sm.side);
                        if (Mathf.Abs(lat) < sm.half) continue;
                        gap[k] = Mathf.Abs(lat) - sm.half - fs.S[bj].half; side[k] = Mathf.Sign(lat); ok[k] = true;
                        if (gap[k] >= 1f) divided = true;
                    }
                    if (!divided) continue;
                    var dd = new float[n]; var sd = new float[n];
                    for (int k = 0; k < n; k++)
                    {
                        if (!ok[k]) continue;
                        float st = e.AtA ? sg.S[k].distance : sg.Length - sg.S[k].distance;
                        float w = 1f - Mathf.SmoothStep(0f, 1f, (st - (e.Trim + MedianGapFull)) / MedianGapFade);
                        float d = (MedianMinGap - gap[k]) * w;                       // beide Fahrbahnen zusammen zu räumen
                        if (w <= 0f || d < .04f) continue;
                        dd[k] = d; sd[k] = side[k];
                    }
                    // Wo der Partner nicht mehr quer gegenüber liegt (Spitze des Mittelstreifens, Fahrbahnen laufen zusammen), darf die Räumung
                    // nicht abrupt enden: Lipschitz-Hülle mit MedianGapSlope (m je m), sonst springen Halbbreite und Mitte
                    for (int k = 1; k < n; k++)
                    {
                        float v = dd[k - 1] - MedianGapSlope * Mathf.Abs(sg.S[k].distance - sg.S[k - 1].distance);
                        if (v > dd[k]) { dd[k] = v; sd[k] = sd[k - 1]; }
                    }
                    for (int k = n - 2; k >= 0; k--)
                    {
                        float v = dd[k + 1] - MedianGapSlope * Mathf.Abs(sg.S[k + 1].distance - sg.S[k].distance);
                        if (v > dd[k]) { dd[k] = v; sd[k] = sd[k + 1]; }
                    }
                    for (int k = 0; k < n; k++)
                    {
                        float d = dd[k] * .5f;                                       // je Fahrbahn zu räumen
                        if (d < .02f) continue;
                        float avail = Mathf.Max(0f, sg.S[k].half - sg.Lanes[k].N * sg.Lanes[k].W * .5f);
                        // Halbbreite -x plus Verschiebung x nach außen halten die Außenkante fest und rücken die Mittelstreifenkante um 2x;
                        // ein Rest y = d - 2x (Halbbreite am Fahrspurminimum) wandert als reine Verschiebung (Außenkante geht mit)
                        float x = Mathf.Min(d * .5f, avail), y = d - 2f * x;
                        ovHalf[si][k] = Mathf.Clamp(ovHalf[si][k] - x, -ThroughMaxHalfAdd, ThroughMaxHalfAdd);
                        ovShift[si][k] = Mathf.Clamp(ovShift[si][k] - sd[k] * (x + y), -ThroughMaxShiftAdd, ThroughMaxShiftAdd);
                    }
                }
        }

        // Ziel je Durchgang: Paar -> beide auf mittlere Breite/Mitte (nicht schmaler als die Fahrspuren); Gabelung -> die einzelne Zufahrt
        // übernimmt die Ausdehnung der Gegenseite (ist diese schmaler als die Fahrspuren der Zufahrt, weiten sich die äußeren Zufahrten
        // der Gegenseite auf). Die Abweichungen werden über die Zufahrt eingeblendet (Plateau bis Beschnitt + 3 m, dann 18-30 m Übergang)
        // und zu den Vorgaben der nächsten Abtastung addiert.
        private void PlanThroughTapers()
        {
            if (ovHalf == null)
            {
                ovHalf = new float[Segs.Count][]; ovShift = new float[Segs.Count][];
                for (int si = 0; si < Segs.Count; si++) { ovHalf[si] = new float[Segs[si].S.Count]; ovShift[si] = new float[Segs[si].S.Count]; }
            }
            PlanMedianGaps();
            System.Action<End, float, float, Vector2, float, float> add = (e, dHalf, dCentre, left, station, plateau) =>
            {
                var sg = Segs[e.Seg]; float s0 = e.AtA ? station : sg.Length - station;
                var ts = At(sg, s0); Vector2 side = new Vector2(ts.side.x, ts.side.z);
                float cs = Vector2.Dot(side, left); if (Mathf.Abs(cs) < .5f) return;
                float dh = dHalf / Mathf.Abs(cs), dsh = dCentre / cs;
                if (Mathf.Abs(dh) < .02f && Mathf.Abs(dsh) < .02f) return;
                float lh = Mathf.Clamp(12f * Mathf.Abs(dh), ThroughTaperLength, 48f), ls = Mathf.Clamp(12f * Mathf.Abs(dsh), ThroughTaperLength, 48f);
                for (int k = 0; k < sg.S.Count; k++)
                {
                    float ds = e.AtA ? sg.S[k].distance : sg.Length - sg.S[k].distance;
                    float wh = ds <= plateau ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (ds - plateau) / lh);
                    float wsh = ds <= plateau ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (ds - plateau) / ls);
                    ovHalf[e.Seg][k] = Mathf.Clamp(ovHalf[e.Seg][k] + dh * wh, -ThroughMaxHalfAdd, ThroughMaxHalfAdd);
                    ovShift[e.Seg][k] = Mathf.Clamp(ovShift[e.Seg][k] + dsh * wsh, -ThroughMaxShiftAdd, ThroughMaxShiftAdd);
                }
            };
            foreach (var jn in Junctions)
              foreach (var g in ThroughGroups(jn))
              {
                Vector2 left = Left(g.Axis);
                float cX = (g.XLo + g.XHi) * .5f, hX = (g.XHi - g.XLo) * .5f, cY = (g.YLo + g.YHi) * .5f, hY = (g.YHi - g.YLo) * .5f;
                if (g.Pair)
                {
                    // beide beweglich: auf die Mitte; nur eine: sie übernimmt die Ausdehnung der festen (nie schmaler als ihre Fahrspuren)
                    if (g.XMobile && g.YMobile)
                    {
                        float hs = Mathf.Max((hX + hY) * .5f, Mathf.Max(g.XFloor, g.YFloor)), cs = (cX + cY) * .5f;
                        add(g.X, hs - hX, cs - cX, left, g.XStation, g.X.Trim + ThroughPlateau); add(g.Ys[0], hs - hY, cs - cY, left, g.YStation, g.Ys[0].Trim + ThroughPlateau);
                    }
                    else if (g.XMobile) add(g.X, Mathf.Max(hY, g.XFloor) - hX, cY - cX, left, g.XStation, g.X.Trim + ThroughPlateau);
                    else if (g.YMobile) add(g.Ys[0], Mathf.Max(hX, g.YFloor) - hY, cX - cY, left, g.YStation, g.Ys[0].Trim + ThroughPlateau);
                }
                else
                {
                    float hs = Mathf.Max(hY, g.XFloor);
                    add(g.X, hs - hX, cY - cX, left, g.XStation, g.X.Trim + ThroughPlateau);
                    if (hs > hY + .02f)
                    {
                        // Gegenseite schmaler als die Fahrspuren von X: die beiden Zufahrten weiten sich nur an ihrer Außenseite bis zum Rand von X auf
                        // (Innenkanten bleiben, sonst lägen die Randlinien der einen mitten auf der anderen)
                        float tLo = cY - hs, tHi = cY + hs;
                        End leftEnd = null, rightEnd = null; float bestL = float.MinValue, bestR = float.MaxValue;
                        foreach (var y in g.Ys)
                        {
                            float lat = Vector2.Dot(y.Dir0, left);
                            if (lat > bestL) { bestL = lat; leftEnd = y; }
                            if (lat < bestR) { bestR = lat; rightEnd = y; }
                        }
                        if (leftEnd == rightEnd) { rightEnd = null; foreach (var y in g.Ys) if (y != leftEnd) rightEnd = y; }
                        LateralExtent(leftEnd, left, g.Org, g.YStation, out _, out float hiL, out _);
                        LateralExtent(rightEnd, left, g.Org, g.YStation, out float loR, out _, out _);
                        float needL = Mathf.Max(0f, tHi - hiL), needR = Mathf.Max(0f, loR - tLo);
                        add(leftEnd, needL * .5f, needL * .5f, left, g.YStation, ThroughPlateau);
                        add(rightEnd, needR * .5f, -needR * .5f, left, g.YStation, ThroughPlateau);
                    }
                }
            }
            PlanInternalLinks();
        }

        // Verbindungsstücke (Internal) zwischen zwei Knoten einer Kreuzung: ihre Enden übernehmen die Querausdehnung der dort in gleicher Linie
        // liegenden Zufahrten (bei Gabelung deren Vereinigung); dazwischen wird linear übergeblendet. Sonst springt der Fahrbahnrand am
        // Knoten um den Unterschied der Spuraufteilungen (Versatz der Fahrbahnmitte) der angrenzenden OSM-Wege.
        private void PlanInternalLinks()
        {
            var atNode = new Dictionary<int, List<End>>();
            foreach (var jn in Junctions) foreach (var e in jn.Ends) { if (!atNode.TryGetValue(e.NodeIdx, out var l)) atNode[e.NodeIdx] = l = new List<End>(); l.Add(e); }
            float cosDev = Mathf.Cos(ThroughMaxDeviation * Mathf.Deg2Rad);
            foreach (var sg in Segs)
            {
                if (!sg.Internal || sg.S.Count < 2 || sg.A == sg.B || sg.Length < .5f) continue;
                int n = sg.S.Count; Vector2 dirI = (Nodes[sg.B].P - Nodes[sg.A].P).normalized, left = Left(dirI);
                var dC = new float[2]; var dH = new float[2]; var has = new bool[2];
                for (int end = 0; end < 2; end++)
                {
                    int node = end == 0 ? sg.A : sg.B;
                    if (!atNode.TryGetValue(node, out var legs)) continue;
                    float lo = float.MaxValue, hi = float.MinValue; Vector2 org = Nodes[node].P;
                    foreach (var e in legs)
                    {
                        var lsg = Segs[e.Seg]; if (lsg.Internal || lsg.Roundabout || lsg.Fallback || lsg.S.Count < 2 || lsg.Length < ForkStation + 2f) continue;
                        float dot = Vector2.Dot(e.Dir0, dirI);
                        if (end == 0 ? dot > -cosDev : dot < cosDev) continue;              // in gleicher Linie: A-Ende -> Zufahrt läuft entgegen, B-Ende -> weiter
                        LateralExtent(e, left, org, ForkStation, out float l0, out float h0, out _);
                        lo = Mathf.Min(lo, l0); hi = Mathf.Max(hi, h0);
                    }
                    if (lo > hi) continue;
                    var s0 = sg.S[end == 0 ? 0 : n - 1];
                    float cs = Mathf.Abs(Vector2.Dot(new Vector2(s0.side.x, s0.side.z), left));
                    float c = Vector2.Dot(new Vector2(s0.pos.x, s0.pos.z) - org, left), h = s0.half * cs;
                    dC[end] = (lo + hi) * .5f - c; dH[end] = (hi - lo) * .5f - h; has[end] = true;
                }
                if (!has[0] && !has[1]) continue;
                int si = Segs.IndexOf(sg);
                for (int k = 0; k < n; k++)
                {
                    float t = Mathf.Clamp01(sg.S[k].distance / sg.Length);
                    float c = dC[0] * (1f - t) + dC[1] * t, h = dH[0] * (1f - t) + dH[1] * t;
                    float cs = Vector2.Dot(new Vector2(sg.S[k].side.x, sg.S[k].side.z), left); if (Mathf.Abs(cs) < .5f) continue;
                    ovHalf[si][k] = Mathf.Clamp(ovHalf[si][k] + h / Mathf.Abs(cs), -ThroughMaxHalfAdd, ThroughMaxHalfAdd);
                    ovShift[si][k] = Mathf.Clamp(ovShift[si][k] + c / cs, -ThroughMaxShiftAdd, ThroughMaxShiftAdd);
                }
            }
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
                // Kreisfahrbahn-Stücke nie zusammenfassen: sonst wird der ganze Kreisverkehr EINE Kreuzung und die
                // Fahrlinie schneidet quer über die Insel
                if (sg.A != sg.B && IsJ(sg.A) && IsJ(sg.B) && sg.Length <= 25f && !sg.Roundabout)
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
                    if (sg.Internal || sg.S.Count < 2) continue;
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
                        j.Ends.Add(new End { Seg = si, AtA = atA, NodeIdx = ni, Dir = dir, Dir0 = atA ? sg.Dir0A : sg.Dir0B, Half = sm.half, Outer = sm.half + (urb ? 2f : 1.6f), Urban = urb, J = j });
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
            foreach (var e in j.Ends) j.Radius = Mathf.Max(j.Radius, e.Trim + (Nodes[e.NodeIdx].P - j.Center).magnitude);
            foreach (int ni in nodes) j.Radius = Mathf.Max(j.Radius, (Nodes[ni].P - j.Center).magnitude + 6f);
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

        // gleitender Mittelwert über ±rad Meter Bogenlänge (die Proben liegen nicht überall im 2-m-Abstand: an Wegwechseln dichter)
        private static float[] SmoothArc(float[] v, float[] arc, float rad)
        {
            int n = v.Length; var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f, wsum = 0f;
                for (int k = i; k >= 0 && arc[i] - arc[k] <= rad; k--) { float w = (arc[Mathf.Min(n - 1, k + 1)] - arc[Mathf.Max(0, k - 1)]) * .5f + .01f; sum += v[k] * w; wsum += w; }
                for (int k = i + 1; k < n && arc[k] - arc[i] <= rad; k++) { float w = (arc[Mathf.Min(n - 1, k + 1)] - arc[Mathf.Max(0, k - 1)]) * .5f + .01f; sum += v[k] * w; wsum += w; }
                r[i] = sum / wsum;
            }
            return r;
        }

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

        // Läufe gleicher Klasse kürzer als minRun werden der Nachbarklasse zugeschlagen (kürzester zuerst); ein einzelner Lauf bleibt.
        // Beginn/Ende (Abschnittsposition in m) des Laufs, der bei Probe k beginnt / mit Probe k endet
        private static float RunStart(Segment sg, int k) => k <= 0 ? 0f : (sg.S[k - 1].distance + sg.S[k].distance) * .5f;
        private static float RunEnd(Segment sg, int k) => k >= sg.S.Count - 1 ? sg.Length : (sg.S[k].distance + sg.S[k + 1].distance) * .5f;

        // Läufe gleicher Klasse kürzer als minLen (Meter) werden der Nachbarklasse zugeschlagen, kürzeste zuerst; ein Lauf mit gesperrten
        // Proben (Kreuzungsnähe, deren Klasse feststeht) bleibt stehen
        private static void MergeShortRuns(Segment sg, bool[] v, bool[] locked, float minLen)
        {
            var skip = new HashSet<int>();
            while (true)
            {
                int bestStart = -1, bestEnd = -1; float bestLen = float.MaxValue; int runs = 0;
                for (int k = 0; k < v.Length;)
                {
                    int e = k; while (e < v.Length && v[e] == v[k]) e++;
                    runs++;
                    float len = RunEnd(sg, e - 1) - RunStart(sg, k);
                    if (len < minLen && len < bestLen && !skip.Contains(k))
                    {
                        bool lk = false; for (int m = k; m < e && !lk; m++) lk = locked[m];
                        if (lk) skip.Add(k); else { bestLen = len; bestStart = k; bestEnd = e; }
                    }
                    k = e;
                }
                if (runs <= 1 || bestStart < 0) return;
                for (int m = bestStart; m < bestEnd; m++) v[m] = !v[m];
                skip.Clear();
            }
        }

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
