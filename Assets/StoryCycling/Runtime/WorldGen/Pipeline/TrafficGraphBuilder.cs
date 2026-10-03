using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Backt den Spurgraph des Hintergrundverkehrs aus dem RoadNet (keine neue Geometrie, nur Lesen):
    //   - je Abschnitt und Fahrtrichtung eine Polylinie je Spur (Linksverkehr, Querlage aus LaneInfo wie die Fahrbahnmarkierungen,
    //     Höhe aus den Proben); Spuranzahl je Abschnitt = Minimum über den befahrbaren Bereich (sonst wäre der Spurabstand < Breite)
    //   - je Kreuzung (Cluster) bzw. Knoten außerhalb: Verbinder als glatte Kurven von der ankommenden Spur zur abgehenden
    //     (Abbiegen links nur von der linken Spur, rechts nur von der rechten, geradeaus von allen; keine Wenden außer in Sackgassen;
    //     an Kreisverkehren nie Zufahrt -> Zufahrt, Ring in OSM-Richtung = im Uhrzeigersinn bei Linksverkehr)
    //   - Sackgassen: Wende-Verbinder (nahe der Route) bzw. Senke (am Korridorrand)
    //   - Spuren ohne Weiterfahrt werden entfernt (Kaskade), Verbinder, die nicht auf Asphalt liegen, verworfen
    public static class TrafficGraphBuilder
    {
        public sealed class Options
        {
            public float UrbanKmh = 50f, RuralKmh = 70f;
            public float MinLaneLength = 1.5f;              // kürzere befahrbare Abschnitte bekommen keine Spuren
            public float DeadEndInset = 6f, SinkInset = 1.6f;               // Sackgasse: Spuren enden so weit vor dem Knoten (Platz zum Wenden)
            public float SinkMinRouteDist = 90f;          // Sackgassenende weiter als das von der Route = Senke (Korridorrand)
            public float ConnectorMaxOffAsphalt = 0f;    // Anteil der Verbinderpunkte außerhalb des Asphalts, ab dem er verworfen wird
            public Func<float, float, bool> OnAsphalt;    // optional: Punkt-in-Asphalt (RoadSurface.Result.Asphalt.Inside)
            public float TurnKmh = 20f, StraightKmh = 40f, RingKmh = 25f, UTurnKmh = 10f;
        }

        public sealed class Stats
        {
            public int Segments, SegmentsUsed, SegmentsSkippedSingle, SegmentsSkippedShort, SegmentsSkippedFallback;
            public int RoadLanes, Connectors, Hubs, HubsJunction, HubsRing, HubsDeadEnd, HubsSink, HubsThrough;
            public int ConnectorsOffAsphalt, LanesPruned, ConnectorsPruned, LanePointsOffAsphalt, LanePoints, SourcesNoPredecessor;
            public float LaneKm, ConnectorKm;
            public override string ToString() =>
                $"{RoadLanes} Spuren ({LaneKm:0.0} km) auf {SegmentsUsed}/{Segments} Abschnitten, {Connectors} Verbinder ({ConnectorKm:0.0} km); " +
                $"Knoten {Hubs}: Kreuzung {HubsJunction}, Kreisverkehr {HubsRing}, Wendestelle {HubsDeadEnd}, Senke {HubsSink}, Durchgang {HubsThrough}; " +
                $"übersprungen: einspurig/zweirichtig {SegmentsSkippedSingle}, zu kurz {SegmentsSkippedShort}, Ersatzfahrbahn {SegmentsSkippedFallback}; " +
                $"verworfen: {ConnectorsOffAsphalt} Verbinder abseits Asphalt, {LanesPruned} Spuren + {ConnectorsPruned} Verbinder ohne Weiterfahrt";
        }

        // ------------------------------------------------------------------ Zwischenmodell
        private sealed class LaneRec
        {
            public int Id; public float[] X, Y, Z; public float Sx, Sz, Ex, Ez, SHx, SHz, EHx, EHz;    // Anfang/Ende, Richtungen
            public float Speed, Room; public int Seg; public bool Ring;
        }
        private sealed class SegLanes
        {
            public bool Ok; public int[] Fwd = new int[0], Bwd = new int[0];          // Spur-IDs in Fahrersicht von links nach rechts
            public float S0, S1; public bool DeadA, DeadB, SinkA, SinkB;
        }
        private sealed class Arm
        {
            public int Seg; public bool AtA; public int[] In, Out; public int Rank; public bool Ring;
            public float InHx, InHz, OutHx, OutHz;
        }
        private sealed class HubRec { public int Id; public float X, Y, Z; public byte Kind; public List<Arm> Arms = new List<Arm>(); public int Key; public int Node = -1; }

        public static TrafficGraph Build(RoadNet net, Options o, IList<Vector3> parked, out Stats st)
        {
            st = new Stats(); o = o ?? new Options();
            var g = new TrafficGraph();
            var recs = new List<LaneRec>();
            var segLanes = new SegLanes[net.Segs.Count];
            var weightOf = new Dictionary<int, float>();
            st.Segments = 0;

            // 1) Zulässige Abschnitte
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sg = net.Segs[si]; var sl = segLanes[si] = new SegLanes();
                if (sg.Internal || sg.S.Count < 2) continue;
                st.Segments++;
                if (sg.Fallback) { st.SegmentsSkippedFallback++; continue; }
                // Kreisfahrbahn: volle Länge (die Abschnitte zwischen den Ring-Knoten sind kürzer als die Beschnitte der Kreuzungsflächen)
                sl.S0 = sg.Roundabout ? 0f : sg.TrimA; sl.S1 = sg.Roundabout ? sg.Length : sg.Length - sg.TrimB;
                if (sl.S1 - sl.S0 < o.MinLaneLength) { st.SegmentsSkippedShort++; continue; }
                int k0 = RoadNet.SampleAt(sg, sl.S0), k1 = RoadNet.SampleAt(sg, sl.S1);
                bool shared = false;
                for (int k = k0; k <= k1; k++) { var li = sg.Lanes[k]; if (li.N <= 0 || (li.Dir == 0 && li.N == 1)) shared = true; }
                if (shared) { st.SegmentsSkippedSingle++; continue; }
                sl.Ok = true;
            }

            // 2) Knoten: Zufahrten je Kreuzung/Knoten aus den zulässigen Abschnitten
            var hubs = new List<HubRec>(); var hubOfKey = new Dictionary<int, HubRec>();
            Func<int, HubRec> HubFor = ni =>
            {
                int ji = net.JunctionOf(ni); int key = ji >= 0 ? ji : -1 - ni;
                if (hubOfKey.TryGetValue(key, out var h)) return h;
                h = new HubRec { Id = hubs.Count, Key = key, Node = ni };
                if (ji >= 0) { var j = net.Junctions[ji]; h.X = j.Center.x; h.Z = j.Center.y; h.Y = j.Y; }
                else { var n = net.Nodes[ni]; h.X = n.P.x; h.Z = n.P.y; h.Y = n.Y; }
                hubs.Add(h); hubOfKey[key] = h; return h;
            };
            for (int si = 0; si < net.Segs.Count; si++)
            {
                if (!segLanes[si].Ok) continue;
                var sg = net.Segs[si];
                for (int side = 0; side < 2; side++)
                {
                    bool atA = side == 0; int ni = atA ? sg.A : sg.B;
                    if (ni < 0) continue;
                    var h = HubFor(ni);
                    var arm = new Arm { Seg = si, AtA = atA, Ring = sg.Roundabout };
                    h.Arms.Add(arm);
                }
            }
            foreach (var h in hubs)
            {
                int n = h.Arms.Count;
                bool ring = false; foreach (var a in h.Arms) if (a.Ring) ring = true;
                if (n == 1)
                {
                    var nd = net.Nodes[h.Node];
                    bool sink = nd.OsmDegree == 0 || nd.RouteDist >= o.SinkMinRouteDist;
                    h.Kind = sink ? TrafficGraph.HubSink : TrafficGraph.HubDeadEnd;
                    var a = h.Arms[0]; var sl = segLanes[a.Seg];
                    if (!sink) { if (a.AtA) sl.DeadA = true; else sl.DeadB = true; }
                    else { if (a.AtA) sl.SinkA = true; else sl.SinkB = true; }
                }
                else h.Kind = ring ? TrafficGraph.HubRing : n == 2 && net.JunctionOf(h.Node) < 0 ? TrafficGraph.HubThrough : TrafficGraph.HubJunction;
            }

            // 3) Spuren je Abschnitt
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sl = segLanes[si]; if (!sl.Ok) continue;
                var sg = net.Segs[si];
                if (sl.DeadA) sl.S0 += o.DeadEndInset;
                if (sl.DeadB) sl.S1 -= o.DeadEndInset;
                if (sl.SinkA) sl.S0 += o.SinkInset;                                   // Autos am Korridorrand: Stoßstange bleibt auf dem Asphalt
                if (sl.SinkB) sl.S1 -= o.SinkInset;
                if (sl.S1 - sl.S0 < o.MinLaneLength) { sl.Ok = false; st.SegmentsSkippedShort++; continue; }
                int k0 = RoadNet.SampleAt(sg, sl.S0), k1 = RoadNet.SampleAt(sg, sl.S1);
                int lc = 99, rc = 99; float kmh = 0f; int urban = 0, nUrban = 0;
                var speeds = new Dictionary<int, int>();
                for (int k = k0; k <= k1; k++)
                {
                    var li = sg.Lanes[k]; lc = Math.Min(lc, li.L); rc = Math.Min(rc, li.R);
                    if (k < sg.MaxSpeed.Count && sg.MaxSpeed[k] > 0f) { int v = Mathf.RoundToInt(sg.MaxSpeed[k]); speeds[v] = (speeds.TryGetValue(v, out int c) ? c : 0) + 1; }
                    nUrban++; if (sg.Urban[k]) urban++;
                }
                if (lc + rc == 0) { sl.Ok = false; continue; }
                int best = 0; foreach (var kv in speeds) if (kv.Value > best) { best = kv.Value; kmh = kv.Key; }
                if (kmh <= 0f) kmh = urban * 2 > nUrban ? o.UrbanKmh : o.RuralKmh;
                if (sg.Roundabout) kmh = Mathf.Min(kmh, o.RingKmh);
                float w = Mathf.Max(0f, sl.S1 - sl.S0);
                int rank = sg.Rank[Mathf.Clamp(k0, 0, sg.Rank.Count - 1)];
                float weight = w * (rank <= 1 ? 3f : rank == 2 ? 2.5f : rank == 3 ? 2f : 1f) * (sg.OnRoute ? 3f : 1f);
                sl.Fwd = new int[lc]; sl.Bwd = new int[rc];
                for (int j = 0; j < lc; j++) { var r = MakeLane(sg, si, sl, true, j, kmh / 3.6f, recs); r.Ring = sg.Roundabout; sl.Fwd[j] = r.Id; }
                for (int j = 0; j < rc; j++) { var r = MakeLane(sg, si, sl, false, j, kmh / 3.6f, recs); r.Ring = sg.Roundabout; sl.Bwd[j] = r.Id; }
                weightOf[si] = weight;
                st.SegmentsUsed++;
            }

            // Arme: ankommende/abgehende Spuren und Richtungen
            foreach (var h in hubs)
                foreach (var a in h.Arms)
                {
                    var sl = segLanes[a.Seg]; var sg = net.Segs[a.Seg];
                    a.In = a.AtA ? sl.Bwd : sl.Fwd;           // Verkehr Richtung Knoten
                    a.Out = a.AtA ? sl.Fwd : sl.Bwd;
                    a.Rank = sg.Rank[a.AtA ? 0 : sg.Rank.Count - 1];
                    float ix = 0, iz = 0, ox = 0, oz = 0;
                    foreach (int id in a.In) { ix += recs[id].EHx; iz += recs[id].EHz; }
                    foreach (int id in a.Out) { ox += recs[id].SHx; oz += recs[id].SHz; }
                    Norm(ref ix, ref iz); Norm(ref ox, ref oz);
                    if (a.In.Length == 0) { ix = -ox; iz = -oz; }
                    if (a.Out.Length == 0) { ox = -ix; oz = -iz; }
                    a.InHx = ix; a.InHz = iz; a.OutHx = ox; a.OutHz = oz;
                }

            // 4) Verbinder
            var conns = new List<ConnRec>();
            foreach (var h in hubs) BuildHub(h, recs, conns, o, st, net);
            RemoveOffAsphalt(conns, recs, o, st);

            // 5) Kaskade: Spuren ohne Weiterfahrt entfernen (außer Senken)
            var alive = new bool[recs.Count]; for (int i = 0; i < alive.Length; i++) alive[i] = true;
            var connAlive = new bool[conns.Count]; for (int i = 0; i < connAlive.Length; i++) connAlive[i] = true;
            var sinkLane = new bool[recs.Count];
            foreach (var h in hubs) if (h.Kind == TrafficGraph.HubSink) foreach (var a in h.Arms) foreach (int id in a.In) sinkLane[id] = true;
            bool changed = true;
            var outOf = new List<int>[recs.Count]; for (int i = 0; i < outOf.Length; i++) outOf[i] = new List<int>();
            for (int ci = 0; ci < conns.Count; ci++) outOf[conns[ci].From].Add(ci);
            while (changed)
            {
                changed = false;
                for (int ci = 0; ci < conns.Count; ci++)
                    if (connAlive[ci] && (!alive[conns[ci].From] || !alive[conns[ci].To])) { connAlive[ci] = false; changed = true; }
                for (int li = 0; li < recs.Count; li++)
                {
                    if (!alive[li] || sinkLane[li]) continue;
                    bool any = false; foreach (int ci in outOf[li]) if (connAlive[ci]) { any = true; break; }
                    if (!any) { alive[li] = false; changed = true; }
                }
            }

            // 6) Graph aufbauen mit neuen IDs
            var newId = new int[recs.Count]; int nid = 0;
            var hubUsed = new bool[hubs.Count];
            for (int li = 0; li < recs.Count; li++) newId[li] = alive[li] ? nid++ : -1;
            int laneCount = nid;
            var connId = new int[conns.Count];
            for (int ci = 0; ci < conns.Count; ci++) connId[ci] = connAlive[ci] ? nid++ : -1;
            var lanes = new TrafficGraph.Lane[nid];
            var hubOfLane = new int[recs.Count]; for (int i = 0; i < hubOfLane.Length; i++) hubOfLane[i] = -1;
            foreach (var h in hubs) foreach (var a in h.Arms) foreach (int id in a.In) hubOfLane[id] = h.Id;
            for (int li = 0; li < recs.Count; li++)
            {
                if (!alive[li]) { st.LanesPruned++; continue; }
                var r = recs[li]; var sg = net.Segs[r.Seg];
                var nx = new List<int>(); foreach (int ci in outOf[li]) if (connAlive[ci]) nx.Add(connId[ci]);
                var l = new TrafficGraph.Lane
                {
                    Id = newId[li], Type = TrafficGraph.Road, Hub = hubOfLane[li], Speed = r.Speed, Room = r.Room, Weight = weightOf.TryGetValue(r.Seg, out float wt) ? wt : 1f,
                    Flags = (r.Ring ? TrafficGraph.FlagRing : 0) | (sg.OnRoute ? TrafficGraph.FlagRoute : 0) | (sinkLane[li] ? TrafficGraph.FlagSink : 0),
                    Next = nx.ToArray(), X = r.X, Y = r.Y, Z = r.Z
                };
                lanes[l.Id] = l; st.RoadLanes++; st.LaneKm += LengthOf(r.X, r.Z) / 1000f;
            }
            for (int ci = 0; ci < conns.Count; ci++)
            {
                if (!connAlive[ci]) { st.ConnectorsPruned++; continue; }
                var c = conns[ci];
                var l = new TrafficGraph.Lane
                {
                    Id = connId[ci], Type = TrafficGraph.Conn, Hub = c.Hub, Speed = c.Speed, Prio = c.Prio, Turn = c.Turn, Flags = c.Flags,
                    Next = new[] { newId[c.To] }, X = c.X, Y = c.Y, Z = c.Z
                };
                lanes[l.Id] = l; st.Connectors++; st.ConnectorKm += LengthOf(c.X, c.Z) / 1000f;
            }
            foreach (var h in hubs)
            {
                g.Hubs.Add(new TrafficGraph.Hub { Id = h.Id, X = h.X, Y = h.Y, Z = h.Z, Kind = h.Kind });
                st.Hubs++;
                switch (h.Kind)
                {
                    case TrafficGraph.HubJunction: st.HubsJunction++; break;
                    case TrafficGraph.HubRing: st.HubsRing++; break;
                    case TrafficGraph.HubDeadEnd: st.HubsDeadEnd++; break;
                    case TrafficGraph.HubSink: st.HubsSink++; break;
                    default: st.HubsThrough++; break;
                }
            }
            foreach (var l in lanes) g.Lanes.Add(l);
            // parkende Autos: nur die in Fahrspurnähe sind als Sperrpunkte für den Spawn interessant (hält die Datei klein)
            if (parked != null)
                foreach (var p in parked)
                {
                    bool near = false;
                    foreach (var l in g.Lanes)
                    {
                        if (l.Type != TrafficGraph.Road) continue;
                        for (int i = 0; i + 1 < l.X.Length && !near; i++)
                            if (SegDist(p.x, p.y, l.X[i], l.Z[i], l.X[i + 1], l.Z[i + 1]) < 6f) near = true;
                        if (near) break;
                    }
                    if (near) g.Parked.Add(p);
                }
            // Quellen: Straßenspuren ohne Vorgänger (Korridorrand, abgeschnittene Zufahrten)
            var hasPred = new bool[g.Lanes.Count];
            foreach (var l in g.Lanes) foreach (int nx in l.Next) hasPred[nx] = true;
            foreach (var l in g.Lanes) if (l.Type == TrafficGraph.Road && !hasPred[l.Id]) st.SourcesNoPredecessor++;
            g.Finish();
            return g;
        }

        private static float SegDist(float px, float pz, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            float t = l2 > 1e-8f ? Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / l2) : 0f;
            float qx = ax + dx * t - px, qz = az + dz * t - pz;
            return Mathf.Sqrt(qx * qx + qz * qz);
        }


        private static float LengthOf(float[] x, float[] z)
        {
            float s = 0f; for (int i = 1; i < x.Length; i++) { float dx = x[i] - x[i - 1], dz = z[i] - z[i - 1]; s += Mathf.Sqrt(dx * dx + dz * dz); }
            return s;
        }
        private static void Norm(ref float x, ref float z) { float l = Mathf.Sqrt(x * x + z * z); if (l > 1e-5f) { x /= l; z /= l; } else { x = 0f; z = 1f; } }

        // ------------------------------------------------------------------ Spur
        private static LaneRec MakeLane(RoadNet.Segment sg, int si, SegLanes sl, bool fwdGroup, int j, float speed, List<LaneRec> recs)
        {
            var stations = new List<float> { sl.S0 };
            for (int k = 0; k < sg.S.Count; k++) { float d = sg.S[k].distance; if (d > sl.S0 + .5f && d < sl.S1 - .5f) stations.Add(d); }
            stations.Add(sl.S1);
            var px = new List<float>(); var py = new List<float>(); var pz = new List<float>(); float room = 99f;
            foreach (float s in stations)
            {
                var ts = RoadNet.At(sg, s); var li = sg.Lanes[RoadNet.SampleAt(sg, s)];
                float inner = Mathf.Max(.5f, ts.half - li.Sh); int n = Mathf.Max(1, li.N);
                int k = fwdGroup ? j : n - 1 - j; k = Mathf.Clamp(k, 0, n - 1);
                float lat = -inner + 2f * inner * (k + .5f) / n;
                px.Add(ts.pos.x + ts.side.x * lat); py.Add(ts.pos.y); pz.Add(ts.pos.z + ts.side.z * lat);
                room = Mathf.Min(room, fwdGroup ? inner - lat : lat + inner);
            }
            // Knicke der Straßenmittellinie springen beim seitlichen Versatz um Dezimeter (innere Kurve): glätten, Enden bleiben exakt (Anschluss der Verbinder)
            if (!sg.Roundabout) SmoothXZ(px, pz, 4);
            if (!fwdGroup) { px.Reverse(); py.Reverse(); pz.Reverse(); }
            var keep = Simplify(px, py, pz);
            var r = new LaneRec { Id = recs.Count, Seg = si, Speed = speed, Room = room };
            int m = keep.Count; r.X = new float[m]; r.Y = new float[m]; r.Z = new float[m];
            for (int i = 0; i < m; i++) { r.X[i] = px[keep[i]]; r.Y[i] = py[keep[i]]; r.Z[i] = pz[keep[i]]; }
            int last = px.Count - 1;
            r.Sx = px[0]; r.Sz = pz[0]; r.Ex = px[last]; r.Ez = pz[last];
            Dir(px, pz, 0, 1, out r.SHx, out r.SHz); Dir(px, pz, last - 1, last, out r.EHx, out r.EHz);
            recs.Add(r);
            return r;
        }
        private static void SmoothXZ(List<float> x, List<float> z, int passes)
        {
            int n = x.Count; if (n < 4) return;
            var tx = new float[n]; var tz = new float[n];
            for (int p = 0; p < passes; p++)
            {
                for (int i = 1; i < n - 1; i++) { tx[i] = .25f * x[i - 1] + .5f * x[i] + .25f * x[i + 1]; tz[i] = .25f * z[i - 1] + .5f * z[i] + .25f * z[i + 1]; }
                for (int i = 1; i < n - 1; i++) { x[i] = tx[i]; z[i] = tz[i]; }
            }
        }

        private static void Dir(List<float> x, List<float> z, int a, int b, out float hx, out float hz)
        {
            hx = x[b] - x[a]; hz = z[b] - z[a]; Norm(ref hx, ref hz);
        }

        // Ramer-Douglas-Peucker (3 cm seitlich, 5 cm Höhe), Stützpunkte höchstens ~12 m auseinander
        private static List<int> Simplify(List<float> x, List<float> y, List<float> z)
        {
            int n = x.Count; var keep = new bool[n]; keep[0] = keep[n - 1] = true;
            var cum = new float[n]; for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Mathf.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));
            var stack = new Stack<(int, int)>(); stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b <= a + 1) continue;
                float maxD = -1f; int at = -1;
                for (int i = a + 1; i < b; i++)
                {
                    float t = cum[b] > cum[a] ? (cum[i] - cum[a]) / (cum[b] - cum[a]) : 0f;
                    float dx = x[i] - (x[a] + (x[b] - x[a]) * t), dz = z[i] - (z[a] + (z[b] - z[a]) * t);
                    // Abstand zur Sehne (senkrecht, xz) — nicht zum zeitgleichen Punkt
                    float ex = x[b] - x[a], ez = z[b] - z[a], el = Mathf.Sqrt(ex * ex + ez * ez);
                    float perp = el > 1e-4f ? Mathf.Abs((x[i] - x[a]) * ez - (z[i] - z[a]) * ex) / el : Mathf.Sqrt((x[i] - x[a]) * (x[i] - x[a]) + (z[i] - z[a]) * (z[i] - z[a]));
                    float dy = Mathf.Abs(y[i] - (y[a] + (y[b] - y[a]) * t)) * .6f;
                    float d = Mathf.Max(perp, dy);
                    if (d > maxD) { maxD = d; at = i; }
                }
                if (maxD > .03f || cum[b] - cum[a] > 12f)
                {
                    if (maxD <= .03f) at = (a + b) / 2;
                    keep[at] = true; stack.Push((a, at)); stack.Push((at, b));
                }
            }
            var res = new List<int>(); for (int i = 0; i < n; i++) if (keep[i]) res.Add(i);
            return res;
        }

        // ------------------------------------------------------------------ Verbinder
        private sealed class ConnRec
        {
            public int From, To, Hub; public float[] X, Y, Z; public float Speed; public byte Prio, Turn; public int Flags; public bool Drop;
        }

        private static void BuildHub(HubRec h, List<LaneRec> recs, List<ConnRec> conns, Options o, Stats st, RoadNet net)
        {
            if (h.Kind == TrafficGraph.HubSink) return;                      // Senke: Autos verschwinden am Ende
            int nArms = h.Arms.Count;
            bool ringHub = h.Kind == TrafficGraph.HubRing;
            int bestRank = int.MaxValue; foreach (var a in h.Arms) bestRank = Math.Min(bestRank, a.Rank);
            foreach (var x in h.Arms)
            {
                if (x.In.Length == 0) continue;
                var created = new bool[x.In.Length];
                var cand = new List<(Arm y, byte turn, float theta)>();
                foreach (var y in h.Arms)
                {
                    if (y.Out.Length == 0) continue;
                    if (ringHub && !x.Ring && !y.Ring) continue;                 // Zufahrt -> Zufahrt durch den Kreisverkehr gibt es nicht
                    bool same = ReferenceEquals(x, y);
                    if (same && nArms > 1) continue;
                    float theta = Mathf.Atan2(x.InHx * y.OutHz - x.InHz * y.OutHx, x.InHx * y.OutHx + x.InHz * y.OutHz) * Mathf.Rad2Deg;
                    byte turn = same ? TrafficGraph.TurnU : Mathf.Abs(theta) <= 35f ? TrafficGraph.TurnStraight : theta > 0f ? TrafficGraph.TurnLeft : TrafficGraph.TurnRight;
                    if (!same && Mathf.Abs(theta) > 150f) continue;              // Wende nur in Sackgassen
                    cand.Add((y, turn, theta));
                }
                foreach (var (y, turn, theta) in cand)
                {
                    int nI = x.In.Length, nO = y.Out.Length;
                    for (int i = 0; i < nI; i++)
                    {
                        int oi = -1;
                        if (ringHub || nArms == 2 || turn == TrafficGraph.TurnStraight) oi = nI == nO ? i : nI == 1 ? 0 : Mathf.RoundToInt(i * (nO - 1) / (float)Mathf.Max(1, nI - 1));
                        else if (turn == TrafficGraph.TurnLeft) oi = i == 0 ? 0 : -1;
                        else oi = i == nI - 1 ? nO - 1 : -1;                     // rechts und Wende: von der rechten (mittelnahen) Spur
                        if (oi < 0) continue;
                        oi = Mathf.Clamp(oi, 0, nO - 1);
                        AddConn(h, x, y, x.In[i], y.Out[oi], turn, bestRank, recs, conns, o, net);
                        created[i] = true;
                    }
                }
                // Spuren ohne Verbinder (z. B. mittlere Spur an einer Einmündung): nächstliegende Richtung nehmen
                for (int i = 0; i < x.In.Length; i++)
                {
                    if (created[i] || cand.Count == 0) continue;
                    var bestC = cand[0];
                    foreach (var c in cand) if (Mathf.Abs(c.theta) < Mathf.Abs(bestC.theta)) bestC = c;
                    int nO = bestC.y.Out.Length;
                    int oi = Mathf.Clamp(bestC.turn == TrafficGraph.TurnLeft ? 0 : bestC.turn == TrafficGraph.TurnRight ? nO - 1 : (nO == 1 ? 0 : Mathf.RoundToInt(i * (nO - 1) / (float)Mathf.Max(1, x.In.Length - 1))), 0, nO - 1);
                    AddConn(h, x, bestC.y, x.In[i], bestC.y.Out[oi], bestC.turn, bestRank, recs, conns, o, net);
                }
            }
        }

        private static void AddConn(HubRec h, Arm x, Arm y, int fromId, int toId, byte turn, int bestRank, List<LaneRec> recs, List<ConnRec> conns, Options o, RoadNet net)
        {
            if (fromId == toId) return;                                     // Kurzsegment mit beiden Enden im selben Knoten: keine Schleife auf sich selbst
            var a = recs[fromId]; var b = recs[toId];
            float p0x = a.Ex, p0z = a.Ez, p3x = b.Sx, p3z = b.Sz;
            float dx = p3x - p0x, dz = p3z - p0z, dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > 90f) return;
            float y0 = a.Y[a.Y.Length - 1], y3 = b.Y[0];
            float k0;
            if (turn == TrafficGraph.TurnU) k0 = Mathf.Max(2f, .667f * dist);
            else if (turn == TrafficGraph.TurnStraight) k0 = Mathf.Min(dist * .33f, 30f);          // (kein Mindestwert: bei dist ~ 0 entstünde eine Schleife)
            else k0 = Mathf.Min(dist * .4f, 40f);
            // Kurvenform: mehrere Grifflängen probieren, die mit den wenigsten Punkten (Mitte und Wagenkanten) abseits Asphalt nehmen
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            float bestScore = float.MaxValue;
            var fx = new List<float>(); var fy = new List<float>(); var fz = new List<float>();
            foreach (float fac in (o.OnAsphalt != null && dist > 4f && turn != TrafficGraph.TurnU ? CurveFactors : CurveFactors0))
            {
                float k = k0 * fac;
                float c1x = p0x + a.EHx * k, c1z = p0z + a.EHz * k, c2x = p3x - b.SHx * k, c2z = p3z - b.SHz * k;
                float approx = 0f; { float px = p0x, pz = p0z; for (int i = 1; i <= 8; i++) { float t = i / 8f; Bez(p0x, c1x, c2x, p3x, t, out float qx); Bez(p0z, c1z, c2z, p3z, t, out float qz); approx += Mathf.Sqrt((qx - px) * (qx - px) + (qz - pz) * (qz - pz)); px = qx; pz = qz; } }
                int n = Mathf.Max(4, Mathf.CeilToInt(approx) + 1);
                fx.Clear(); fy.Clear(); fz.Clear();
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)(n - 1);
                    Bez(p0x, c1x, c2x, p3x, t, out float qx); Bez(p0z, c1z, c2z, p3z, t, out float qz);
                    fx.Add(qx); fz.Add(qz); fy.Add(Mathf.Lerp(y0, y3, t));
                }
                float score = o.OnAsphalt != null ? Score(fx, fz, o.OnAsphalt) : 0f;
                if (score < bestScore) { bestScore = score; xs = new List<float>(fx); ys = new List<float>(fy); zs = new List<float>(fz); }
                if (score == 0f) break;
            }
            var keep = Simplify(xs, ys, zs);
            var c = new ConnRec { From = fromId, To = toId, Hub = h.Id, Turn = turn };
            c.X = new float[keep.Count]; c.Y = new float[keep.Count]; c.Z = new float[keep.Count];
            for (int i = 0; i < keep.Count; i++) { c.X[i] = xs[keep[i]]; c.Y[i] = ys[keep[i]]; c.Z[i] = zs[keep[i]]; }
            bool ringConn = h.Kind == TrafficGraph.HubRing && x.Ring && y.Ring;
            float kmh = turn == TrafficGraph.TurnU ? o.UTurnKmh : turn == TrafficGraph.TurnStraight ? o.StraightKmh : o.TurnKmh;
            if (turn == TrafficGraph.TurnLeft || turn == TrafficGraph.TurnRight)
            {
                // lange Abbieger (breite Doppelfahrbahn-Kreuzungen): Grundtempo wächst mit der Länge, die Kurvenkrümmung bremst die Spitze ohnehin
                float len = 0f; for (int i = 0; i + 1 < c.X.Length; i++) { float ex = c.X[i + 1] - c.X[i], ez = c.Z[i + 1] - c.Z[i]; len += Mathf.Sqrt(ex * ex + ez * ez); }
                kmh = Mathf.Lerp(o.TurnKmh, o.StraightKmh, Mathf.Clamp01((len - 20f) / 30f));
            }
            if (h.Kind == TrafficGraph.HubRing) kmh = Mathf.Min(kmh, o.RingKmh);
            float sp = Mathf.Min(kmh / 3.6f, Mathf.Min(a.Speed, b.Speed));
            if (h.Kind == TrafficGraph.HubThrough) sp = Mathf.Min(a.Speed, b.Speed);
            c.Speed = sp;
            c.Prio = (byte)(h.Kind == TrafficGraph.HubRing ? (x.Ring ? 3 : 0) : x.Rank == bestRank ? 2 : 0);
            c.Flags = (ringConn ? TrafficGraph.FlagRing : 0);
            conns.Add(c);
        }

        private static readonly float[] CurveFactors = { 1f, 1.5f, .7f, 2f, .5f }, CurveFactors0 = { 1f };

        // Punkte abseits Asphalt: Mitte zählt 3, die Wagenkanten (±0,9 m quer) je 1
        private static float Score(List<float> x, List<float> z, Func<float, float, bool> onAsphalt)
        {
            float bad = 0f; int n = x.Count;
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                float hx = x[b] - x[a], hz = z[b] - z[a]; Norm(ref hx, ref hz);
                if (!onAsphalt(x[i], z[i])) bad += 3f;
                if (!onAsphalt(x[i] + hz * .9f, z[i] - hx * .9f)) bad += 1f;
                if (!onAsphalt(x[i] - hz * .9f, z[i] + hx * .9f)) bad += 1f;
            }
            return bad;
        }

        private static void Bez(float p0, float c1, float c2, float p3, float t, out float v)
        {
            float u = 1f - t; v = u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p3;
        }

        private static void RemoveOffAsphalt(List<ConnRec> conns, List<LaneRec> recs, Options o, Stats st)
        {
            if (o.OnAsphalt == null) return;
            for (int ci = conns.Count - 1; ci >= 0; ci--)
            {
                var c = conns[ci]; int bad = 0, tot = 0;
                for (int i = 0; i + 1 < c.X.Length; i++)
                {
                    float seg = Mathf.Sqrt((c.X[i + 1] - c.X[i]) * (c.X[i + 1] - c.X[i]) + (c.Z[i + 1] - c.Z[i]) * (c.Z[i + 1] - c.Z[i]));
                    int m = Mathf.Max(1, Mathf.CeilToInt(seg * 4f));          // alle 25 cm
                    for (int k = 0; k < m; k++)
                    {
                        float t = k / (float)m; tot++;
                        if (!o.OnAsphalt(Mathf.Lerp(c.X[i], c.X[i + 1], t), Mathf.Lerp(c.Z[i], c.Z[i + 1], t))) bad++;
                    }
                }
                if (tot > 0 && bad / (float)tot > o.ConnectorMaxOffAsphalt) { conns.RemoveAt(ci); st.ConnectorsOffAsphalt++; }
            }
            foreach (var r in recs) for (int i = 0; i < r.X.Length; i++) { st.LanePoints++; if (!o.OnAsphalt(r.X[i], r.Z[i])) st.LanePointsOffAsphalt++; }
        }
    }
}
