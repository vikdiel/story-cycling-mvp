using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // EIN Generator für alle Straßen: jeder Netz-Abschnitt (Route wie Querstraße) mit demselben
    // Querschnitt-System, jede Kreuzung als Fläche mit gerundeten Bordstein-/Randstreifen-Ecken.
    // Abschnitte enden exakt am Kreuzungsrand -> keine überlappenden Bänder, keine Lücken.
    public static class RoadNetMesher
    {
        private const float Bucket = 400f;

        public static void Build(RoadNet net, Transform parent, RoadMaterials mats, System.Func<Mesh, Mesh> save)
        {
            var buckets = new Dictionary<long, RoadProfile.Parts>();
            System.Func<Vector3, RoadProfile.Parts> PartsAt = p =>
            {
                long key = ((long)Mathf.FloorToInt(p.x / Bucket) << 32) | (uint)Mathf.FloorToInt(p.z / Bucket);
                if (!buckets.TryGetValue(key, out var parts)) { parts = new RoadProfile.Parts(); buckets[key] = parts; }
                return parts;
            };

            int segs = 0;
            foreach (var sg in net.Segs)
            {
                float s0 = sg.TrimA, s1 = sg.Length - sg.TrimB;
                if (s1 - s0 < 1f) continue;
                var sub = new List<RoadField.Sample>(); var urb = new List<bool>(); var rank = new List<int>();
                sub.Add(RoadNet.At(sg, s0)); urb.Add(sg.Urban[RoadNet.SampleAt(sg, s0)]); rank.Add(sg.Rank[RoadNet.SampleAt(sg, s0)]);
                for (int i = 0; i < sg.S.Count; i++)
                    if (sg.S[i].distance > s0 + .2f && sg.S[i].distance < s1 - .2f) { sub.Add(sg.S[i]); urb.Add(sg.Urban[i]); rank.Add(sg.Rank[i]); }
                sub.Add(RoadNet.At(sg, s1)); urb.Add(sg.Urban[RoadNet.SampleAt(sg, s1)]); rank.Add(sg.Rank[RoadNet.SampleAt(sg, s1)]);
                var parts = PartsAt(sub[sub.Count / 2].pos);
                RoadProfile.Emit(parts, sub, 0, sub.Count - 1,
                                 i => urb[i] ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural,
                                 i => urb[i] ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural,
                                 true, i => rank[i] <= 3, -1f);
                segs++;
            }
            foreach (var j in net.Junctions) Patch(net, j, PartsAt(new Vector3(net.Nodes[j.Node].P.x, 0f, net.Nodes[j.Node].P.y)));

            int count = 0;
            foreach (var kv in buckets)
            {
                if (kv.Value.IsEmpty) continue;
                Mesh mesh = kv.Value.ToMesh();
                mesh.name = $"RoadNet_{count:D3}";
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterials = mats.Ribbon;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                count++;
            }
            Debug.Log($"Straßennetz: {segs} Abschnitte, {net.Junctions.Count} Kreuzungsflächen in {count} Meshes.");
        }

        // Kreuzungsfläche (Asphalt) + Ecken (Gehweg bzw. Randstreifen) + Schürze, alles aus denselben Randpunkten
        public static void Patch(RoadNet net, RoadNet.Junction j, RoadProfile.Parts p)
        {
            var node = net.Nodes[j.Node];
            int m = j.Ends.Count;
            var R = new Vector3[m]; var L = new Vector3[m]; var OR = new Vector3[m]; var OL = new Vector3[m];
            var trimPos = new Vector3[m];
            for (int i = 0; i < m; i++)
            {
                var e = j.Ends[i]; var sg = net.Segs[e.Seg];
                var ts = RoadNet.At(sg, e.AtA ? e.Trim : sg.Length - e.Trim);
                trimPos[i] = ts.pos;
                // exakt dieselben Randpunkte wie die erste/letzte Querschnittsreihe des Abschnitts (Seitenvektor der Probe)
                Vector3 r3 = e.AtA ? ts.side : -ts.side, l3 = -r3;
                R[i] = ts.pos + r3 * ts.half; L[i] = ts.pos + l3 * ts.half;
                float outer = ts.half + (e.Urban ? 2f : 1.6f);
                OR[i] = ts.pos + r3 * outer; OL[i] = ts.pos + l3 * outer;
            }
            Vector3 center = new Vector3(node.P.x, node.Y, node.P.y);
            const int fil = 6;
            var poly = new List<Vector3>();
            var cornerInner = new List<Vector3[]>(); var cornerOuter = new List<Vector3[]>(); var cornerUrban = new List<bool>();
            for (int i = 0; i < m; i++)
            {
                int k = (i + 1) % m;
                var e1 = j.Ends[i]; var e2 = j.Ends[k];
                poly.Add(R[i]); poly.Add(L[i]);
                // Innenecke: quadratische Kurve L_i -> R_k, Kontrollpunkt = Schnitt der Fahrbahnkanten
                Vector3 ctrl = RoadNet.EdgeIntersect(e1.Dir, RoadNet.Left(e1.Dir) * e1.Half, e2.Dir, RoadNet.Right(e2.Dir) * e2.Half, out float t1, out _)
                    ? new Vector3(node.P.x + RoadNet.Left(e1.Dir).x * e1.Half + e1.Dir.x * t1, 0f, node.P.y + RoadNet.Left(e1.Dir).y * e1.Half + e1.Dir.y * t1)
                    : (L[i] + R[k]) * .5f;
                Vector3 octrl = RoadNet.EdgeIntersect(e1.Dir, RoadNet.Left(e1.Dir) * e1.Outer, e2.Dir, RoadNet.Right(e2.Dir) * e2.Outer, out float o1, out _)
                    ? new Vector3(node.P.x + RoadNet.Left(e1.Dir).x * e1.Outer + e1.Dir.x * o1, 0f, node.P.y + RoadNet.Left(e1.Dir).y * e1.Outer + e1.Dir.y * o1)
                    : (OL[i] + OR[k]) * .5f;
                var inner = new Vector3[fil + 1]; var outerPts = new Vector3[fil + 1];
                for (int s = 0; s <= fil; s++)
                {
                    float t = s / (float)fil;
                    inner[s] = Bez(L[i], ctrl, R[k], t); inner[s].y = Mathf.Lerp(L[i].y, R[k].y, t);
                    outerPts[s] = Bez(OL[i], octrl, OR[k], t); outerPts[s].y = inner[s].y;
                    if (s > 0 && s < fil) poly.Add(inner[s]);
                }
                cornerInner.Add(inner); cornerOuter.Add(outerPts); cornerUrban.Add(e1.Urban && e2.Urban);
            }

            // Asphaltfläche: Fächer vom Knoten (Umlauf gegen den Uhrzeiger -> Dreiecke umgekehrt für Unity)
            int c0 = p.V.Count;
            p.V.Add(center + Vector3.up * .02f); p.N.Add(Vector3.up); p.UV.Add(new Vector2(center.x / 4f, center.z / 6f));
            foreach (var q in poly) { p.V.Add(q + Vector3.up * .02f); p.N.Add(Vector3.up); p.UV.Add(new Vector2(q.x / 4f, q.z / 6f)); }
            for (int i = 0; i < poly.Count; i++)
            {
                int a = c0 + 1 + i, b = c0 + 1 + (i + 1) % poly.Count;
                p.T[0].Add(c0); p.T[0].Add(b); p.T[0].Add(a);
            }

            // Ecken: Gehweg (innerorts, erhöht mit Bordsteinkante) bzw. Randstreifen, dazu Schürze nach unten
            for (int c = 0; c < cornerInner.Count; c++)
            {
                var inner = cornerInner[c]; var outer = cornerOuter[c]; bool urban = cornerUrban[c];
                float top = urban ? .16f : -.06f, innerTop = urban ? .16f : .015f;
                int sub = urban ? 4 : 1;
                for (int s = 0; s < fil; s++)
                {
                    // Band zwischen Innen- und Außenkurve (Oberseite)
                    Strip(p, sub, inner[s] + Vector3.up * innerTop, inner[s + 1] + Vector3.up * innerTop,
                                  outer[s] + Vector3.up * top, outer[s + 1] + Vector3.up * top);
                    if (urban)   // Bordsteinkante: senkrecht von Fahrbahn (+2 cm) auf Gehweg (+16 cm)
                        Strip(p, 4, inner[s] + Vector3.up * .02f, inner[s + 1] + Vector3.up * .02f,
                                    inner[s] + Vector3.up * .16f, inner[s + 1] + Vector3.up * .16f);
                    // Schürze: 1 m nach außen, 1,4 m tief (verdeckt Geländekanten)
                    Vector3 d0 = (outer[s] - inner[s]); d0.y = 0f; d0 = d0.sqrMagnitude > 1e-4f ? d0.normalized : Vector3.zero;
                    Vector3 d1 = (outer[s + 1] - inner[s + 1]); d1.y = 0f; d1 = d1.sqrMagnitude > 1e-4f ? d1.normalized : Vector3.zero;
                    Strip(p, 1, outer[s] + Vector3.up * top, outer[s + 1] + Vector3.up * top,
                                outer[s] + d0 - Vector3.up * 1.4f, outer[s + 1] + d1 - Vector3.up * 1.4f);
                }
            }
        }

        // Viereck a-b (Innenkante) / c-d (Außenkante), beidseitig (Wicklung egal, keine Löcher)
        private static void Strip(RoadProfile.Parts p, int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = p.V.Count;
            Vector3 n = Vector3.Cross(b - a, c - a).normalized; if (n.y < 0f) n = -n;
            p.V.Add(a); p.V.Add(b); p.V.Add(c); p.V.Add(d);
            for (int k = 0; k < 4; k++) { p.N.Add(n.sqrMagnitude > .5f ? n : Vector3.up); p.UV.Add(new Vector2(p.V[i + k].x / 4f, p.V[i + k].z / 6f)); }
            p.T[sub].Add(i); p.T[sub].Add(i + 2); p.T[sub].Add(i + 1); p.T[sub].Add(i + 1); p.T[sub].Add(i + 2); p.T[sub].Add(i + 3);
            p.T[sub].Add(i); p.T[sub].Add(i + 1); p.T[sub].Add(i + 2); p.T[sub].Add(i + 1); p.T[sub].Add(i + 3); p.T[sub].Add(i + 2);
        }

        private static Vector3 Bez(Vector3 a, Vector3 c, Vector3 b, float t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;
        private static Vector3 V3(Vector2 v) => new Vector3(v.x, 0f, v.y);
    }

    // Fahrlinie auf dem Netz: jeder Punkt der gematchten Route wird auf die Mittellinie des gebauten
    // Abschnitts projiziert; in Kreuzungen wird eine glatte Kurve von Rand zu Rand gelegt.
    // Höhe und Fahrspur kommen aus dem Abschnitt -> der Fahrer fährt exakt auf der gezeichneten Straße.
    public static class RoadNetRoute
    {
        public sealed class Result
        {
            public readonly List<Vector3> Points = new List<Vector3>(); public readonly List<float> Lane = new List<float>(), Half = new List<float>(), Inset = new List<float>();
            public readonly List<bool> OffNet = new List<bool>();
            public float OnNetShare;
        }

        public static Result Build(RoadNet net, List<Vector3> matched)
        {
            // Hash aller Abschnittsproben
            var map = new Dictionary<long, List<int>>();
            var refs = new List<int>(); var refIdx = new List<int>();
            const float cell = 10f;
            for (int si = 0; si < net.Segs.Count; si++)
                for (int k = 0; k < net.Segs[si].S.Count; k++)
                {
                    var q = net.Segs[si].S[k].pos;
                    long key = ((long)Mathf.FloorToInt(q.x / cell) << 32) | (uint)Mathf.FloorToInt(q.z / cell);
                    if (!map.TryGetValue(key, out var l)) { l = new List<int>(); map[key] = l; }
                    l.Add(refs.Count); refs.Add(si); refIdx.Add(k);
                }

            // 1) Jeder Routenpunkt -> (Abschnitt, Bogenlänge); bleibt bei Gleichstand auf dem bisherigen Abschnitt
            int n = matched.Count;
            var seg = new int[n]; var arcS = new float[n];
            int prevSeg = -1, onNet = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 q = matched[i];
                int cx = Mathf.FloorToInt(q.x / cell), cz = Mathf.FloorToInt(q.z / cell);
                float best = 8f * 8f, bestPrev = float.MaxValue; int bs = -1, bk = -1, pk = -1;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (map.TryGetValue(((long)(cx + dx) << 32) | (uint)(cz + dz), out var l))
                        foreach (int r in l)
                        {
                            var sp = net.Segs[refs[r]].S[refIdx[r]].pos;
                            float d2 = (sp.x - q.x) * (sp.x - q.x) + (sp.z - q.z) * (sp.z - q.z);
                            if (d2 < best) { best = d2; bs = refs[r]; bk = refIdx[r]; }
                            if (refs[r] == prevSeg && d2 < bestPrev) { bestPrev = d2; pk = refIdx[r]; }
                        }
                if (prevSeg >= 0 && pk >= 0 && bestPrev <= best + 9f) { bs = prevSeg; bk = pk; }
                seg[i] = bs;
                arcS[i] = bs >= 0 ? net.Segs[bs].S[bk].distance : 0f;
                if (bs >= 0) { onNet++; prevSeg = bs; }
            }

            // 2) Punkte aufbauen; in Kreuzungszonen (innerhalb des Beschnitts) Bezier von Rand zu Rand
            var res = new Result();
            res.OnNetShare = onNet / (float)Mathf.Max(1, n);
            bool inJunction = false; Vector3 lastPos = Vector3.zero; float lastLane = 0f, lastHalf = 0f, lastInset = 0f; int jNode = -1;
            for (int i = 0; i < n; i++)
            {
                if (seg[i] < 0)
                {
                    // abseits des Netzes: gematchter Punkt
                    Emit(res, matched[i], -2f, 3f, -1f); res.OffNet[res.OffNet.Count - 1] = true; inJunction = false; continue;
                }
                var sg = net.Segs[seg[i]];
                float s = arcS[i];
                int nodeHere = s < sg.TrimA ? sg.A : s > sg.Length - sg.TrimB ? sg.B : -1;
                if (nodeHere >= 0 && net.Nodes[nodeHere].Segs.Count >= 3)
                {
                    if (!inJunction) { inJunction = true; jNode = nodeHere; }
                    continue;                                   // Punkt liegt in der Kreuzungsfläche
                }
                var smp = RoadNet.At(sg, s);
                float lane = Lane(smp, sg.Urban[RoadNet.SampleAt(sg, s)]);
                if (inJunction && res.Points.Count > 0)
                {
                    // Kurve durch die Kreuzung: Steuerpunkt = Knoten
                    var nd = net.Nodes[jNode];
                    Vector3 ctrl = new Vector3(nd.P.x, nd.Y, nd.P.y);
                    float dist = Vector3.Distance(lastPos, smp.pos);
                    int steps = Mathf.Max(2, Mathf.CeilToInt(dist / 3f));
                    for (int k = 1; k < steps; k++)
                    {
                        float t = k / (float)steps;
                        Vector3 b = (1 - t) * (1 - t) * lastPos + 2 * (1 - t) * t * ctrl + t * t * smp.pos;
                        Emit(res, b, Mathf.Lerp(lastLane, lane, t), Mathf.Lerp(lastHalf, smp.half, t), t < .5f ? lastInset : smp.inset);
                    }
                }
                inJunction = false;
                if (res.Points.Count > 0 && Vector3.Distance(res.Points[res.Points.Count - 1], smp.pos) < .5f) continue;
                Emit(res, smp.pos, lane, smp.half, smp.inset);
                lastPos = smp.pos; lastLane = lane; lastHalf = smp.half; lastInset = smp.inset;
            }
            BlendOffNet(res);
            return res;
        }

        // Seitenstreifen vorhanden -> in dessen Mitte; sonst ~1,2 m vom Rand
        public static float Lane(RoadField.Sample s, bool urban) => !urban && s.inset >= 1f ? -(s.half - s.inset * .5f) : -(s.half - 1.2f);

        private static void Emit(Result r, Vector3 p, float lane, float half, float inset)
        { r.Points.Add(p); r.Lane.Add(lane); r.Half.Add(half); r.Inset.Add(inset); r.OffNet.Add(false); }

        // Abschnitte abseits des Netzes (GPX-Höhen) nahtlos an die Netzhöhen anschließen
        private static void BlendOffNet(Result r)
        {
            int n = r.Points.Count;
            for (int i = 0; i < n;)
            {
                if (!r.OffNet[i]) { i++; continue; }
                int e = i; while (e < n && r.OffNet[e]) e++;
                float dS = i > 0 ? r.Points[i - 1].y - r.Points[i].y : 0f;
                float dE = e < n ? r.Points[e].y - r.Points[e - 1].y : dS;
                if (i == 0) dS = dE;
                for (int k = i; k < e; k++)
                {
                    float t = e - 1 > i ? (k - i) / (float)(e - 1 - i) : 0f;
                    var q = r.Points[k]; q.y += Mathf.Lerp(dS, dE, t); r.Points[k] = q;
                }
                i = e;
            }
        }
    }
}
