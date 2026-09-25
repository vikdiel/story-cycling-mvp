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

        // Standard: vereinigte Oberfläche (robust für alle Kreuzungsformen). Die Vereinigung läuft kachelweise
        // (200 m, exakt beschnitten, mit Fortschrittsbalken/Abbruch) statt global über das ganze Netz — Nordhoek
        // ~6 s statt ~40 s; eine fehlerhafte Kachel wird übersprungen statt den Build zu blockieren.
        // false = alte Bänder + Kreuzungsflächen.
        public static bool UseSurfaceUnion = true;

        public static void Build(RoadNet net, Transform parent, RoadMaterials mats, System.Func<Mesh, Mesh> save,
                                 System.Func<float, bool> progress = null, List<Vector2[]> externalAsphalt = null)
        {
            if (UseSurfaceUnion) { RoadSurface.Build(net, parent, mats, save, progress, externalAsphalt); return; }
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
                if (sg.Internal) continue;                               // liegt in einer Kreuzungsfläche
                float s0 = sg.TrimA, s1 = sg.Length - sg.TrimB;
                if (s1 - s0 < 1f) continue;
                var sub = new List<RoadField.Sample>(); var le = new List<RoadProfile.Edge>(); var re = new List<RoadProfile.Edge>(); var rank = new List<int>();
                System.Action<RoadField.Sample, int> Add = (smp, k) =>
                {
                    sub.Add(smp); rank.Add(sg.Rank[k]);
                    le.Add(sg.LeftKind[k] == RoadNet.KindMedian ? RoadProfile.Edge.Median : sg.Urban[k] ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural);
                    re.Add(sg.RightKind[k] == RoadNet.KindMedian ? RoadProfile.Edge.Median : sg.Urban[k] ? RoadProfile.Edge.Urban : RoadProfile.Edge.Rural);
                };
                Add(RoadNet.At(sg, s0), RoadNet.SampleAt(sg, s0));
                for (int i = 0; i < sg.S.Count; i++)
                    if (sg.S[i].distance > s0 + .2f && sg.S[i].distance < s1 - .2f) Add(sg.S[i], i);
                Add(RoadNet.At(sg, s1), RoadNet.SampleAt(sg, s1));
                var parts = PartsAt(sub[sub.Count / 2].pos);
                RoadProfile.Emit(parts, sub, 0, sub.Count - 1, i => le[i], i => re[i], true, i => rank[i] <= 3 && !sg.Oneway, -1f);
                segs++;
            }
            var carriageway = Carriageways(net);
            foreach (var j in net.Junctions) Patch(net, j, PartsAt(new Vector3(j.Center.x, 0f, j.Center.y)), carriageway);

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

        // Kreuzungsfläche (Asphalt) + Ecken (Gehweg bzw. Randstreifen) + Schürze, alles aus denselben Randpunkten.
        // Funktioniert für einfache Kreuzungen und für Cluster (Doppelfahrbahn, Abbiegespuren).
        // Fahrbahnproben aller gebauten Abschnitte (für den Test "kein Gehweg/Randstreifen auf Asphalt")
        public static RoadField Carriageways(RoadNet net)
        {
            var all = new List<RoadField.Sample>();
            foreach (var sg in net.Segs)
            {
                if (sg.Internal) continue;
                foreach (var sm in sg.S) if (sm.distance >= sg.TrimA && sm.distance <= sg.Length - sg.TrimB) all.Add(sm);
            }
            return new RoadField(all);
        }

        private static RoadField guard;
        private static bool OnAsphalt(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (guard == null) return false;
            Vector3 m = (a + b + c + d) * .25f;
            return guard.Nearest(m.x, m.z, 10f, out int i, out float dist) && dist < guard.Samples[i].half - .4f;
        }

        public static void Patch(RoadNet net, RoadNet.Junction j, RoadProfile.Parts p, RoadField carriageway = null)
        {
            guard = carriageway;
            int m = j.Ends.Count;
            if (m < 2) return;
            var R = new Vector3[m]; var L = new Vector3[m]; var OR = new Vector3[m]; var OL = new Vector3[m];
            var dir = new Vector2[m];
            for (int i = 0; i < m; i++)
            {
                var e = j.Ends[i]; var sg = net.Segs[e.Seg];
                var ts = RoadNet.At(sg, e.AtA ? e.Trim : sg.Length - e.Trim);
                // exakt dieselben Randpunkte wie die erste/letzte Querschnittsreihe des Abschnitts
                Vector3 r3 = e.AtA ? ts.side : -ts.side, l3 = -r3;
                R[i] = ts.pos + r3 * ts.half; L[i] = ts.pos + l3 * ts.half;
                float outer = ts.half + (e.Urban ? 2f : 1.6f);
                OR[i] = ts.pos + r3 * outer; OL[i] = ts.pos + l3 * outer;
                Vector3 d3 = e.AtA ? ts.tangent : -ts.tangent; d3.y = 0f;
                dir[i] = new Vector2(d3.x, d3.z).normalized;            // vom Kreuzungsinneren nach außen
            }
            Vector3 center = new Vector3(j.Center.x, j.Y, j.Center.y);
            var poly = new List<Vector3>();
            var cornerInner = new List<List<Vector3>>(); var cornerOuter = new List<List<Vector3>>(); var cornerUrban = new List<bool>();
            for (int i = 0; i < m; i++)
            {
                int k = (i + 1) % m;
                poly.Add(R[i]); poly.Add(L[i]);
                var inner = Corner(L[i], dir[i], R[k], dir[k], center);
                var outer = Corner(OL[i], dir[i], OR[k], dir[k], center);
                Match(inner, outer);
                for (int s = 1; s < inner.Count - 1; s++) poly.Add(inner[s]);
                cornerInner.Add(inner); cornerOuter.Add(outer); cornerUrban.Add(j.Ends[i].Urban && j.Ends[k].Urban);
            }

            // Asphaltfläche per Ear-Clipping (auch bei nicht sternförmigen Clustern korrekt)
            int c0 = p.V.Count;
            foreach (var q in poly) { p.V.Add(q + Vector3.up * .02f); p.N.Add(Vector3.up); p.UV.Add(new Vector2(q.x / 4f, q.z / 6f)); }
            foreach (var t in Triangulate(poly))
            { p.T[0].Add(c0 + t.x); p.T[0].Add(c0 + t.z); p.T[0].Add(c0 + t.y); }   // CCW -> in Unity umdrehen

            // Ecken: Gehweg (innerorts, erhöht mit Bordsteinkante) bzw. Randstreifen, dazu Schürze nach unten
            for (int c = 0; c < cornerInner.Count; c++)
            {
                var inner = cornerInner[c]; var outer = cornerOuter[c]; bool urban = cornerUrban[c];
                float top = urban ? .16f : -.06f, innerTop = urban ? .16f : .015f;
                int sub = urban ? 4 : 1;
                for (int s = 0; s < inner.Count - 1; s++)
                {
                    // nie Gehweg/Randstreifen auf die Fahrbahn einer anderen Zufahrt legen
                    if (OnAsphalt(inner[s], inner[s + 1], outer[s], outer[s + 1])) continue;
                    Strip(p, sub, inner[s] + Vector3.up * innerTop, inner[s + 1] + Vector3.up * innerTop,
                                  outer[s] + Vector3.up * top, outer[s + 1] + Vector3.up * top);
                    if (urban)
                        Strip(p, 4, inner[s] + Vector3.up * .02f, inner[s + 1] + Vector3.up * .02f,
                                    inner[s] + Vector3.up * .16f, inner[s + 1] + Vector3.up * .16f);
                    Vector3 d0 = outer[s] - inner[s]; d0.y = 0f; d0 = d0.sqrMagnitude > 1e-4f ? d0.normalized : Vector3.zero;
                    Vector3 d1 = outer[s + 1] - inner[s + 1]; d1.y = 0f; d1 = d1.sqrMagnitude > 1e-4f ? d1.normalized : Vector3.zero;
                    Strip(p, 1, outer[s] + Vector3.up * top, outer[s + 1] + Vector3.up * top,
                                outer[s] + d0 - Vector3.up * 1.4f, outer[s + 1] + d1 - Vector3.up * 1.4f);
                }
            }
        }

        // Ecke zwischen linker Kante einer Zufahrt (a, Richtung da) und rechter Kante der nächsten (b, db):
        //  - Kanten schneiden sich HINTER beiden Beschnittpunkten -> gerundete Bordsteinecke (Bezier)
        //  - fast gerade gegenüber (T-Kreuzung Rückseite) -> gerade Linie
        //  - große Lücke ohne Schnitt -> Bogen um die Kreuzungsmitte (nie quer über die Fahrbahn)
        private static List<Vector3> Corner(Vector3 a, Vector2 da, Vector3 b, Vector2 db, Vector3 c)
        {
            var pts = new List<Vector3>();
            Vector2 a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z), c2 = new Vector2(c.x, c.z);
            const int fil = 6;
            if (RoadNet.LineX(a2, da, b2, db, out float ta, out float tb) && ta < .5f && tb < .5f && ta > -40f && tb > -40f)
            {
                Vector2 k2 = a2 + da * ta;
                for (int s = 0; s <= fil; s++)
                {
                    float t = s / (float)fil;
                    Vector2 q = (1 - t) * (1 - t) * a2 + 2 * (1 - t) * t * k2 + t * t * b2;
                    pts.Add(new Vector3(q.x, Mathf.Lerp(a.y, b.y, t), q.y));
                }
                return pts;
            }
            float angA = Mathf.Atan2(a2.y - c2.y, a2.x - c2.x), angB = Mathf.Atan2(b2.y - c2.y, b2.x - c2.x);
            float gap = angB - angA; while (gap < 0f) gap += Mathf.PI * 2f; while (gap >= Mathf.PI * 2f) gap -= Mathf.PI * 2f;
            bool straight = Vector2.Dot(da, db) < -.9f && gap < Mathf.PI * 1.1f;
            if (straight || gap < .6f)
            {
                pts.Add(a); pts.Add(b);
                return pts;
            }
            int steps = Mathf.Clamp(Mathf.CeilToInt(gap / .35f), 2, 18);
            float ra = (a2 - c2).magnitude, rb = (b2 - c2).magnitude;
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps, ang = angA + gap * t, r = Mathf.Lerp(ra, rb, t);
                pts.Add(new Vector3(c2.x + Mathf.Cos(ang) * r, Mathf.Lerp(a.y, b.y, t), c2.y + Mathf.Sin(ang) * r));
            }
            return pts;
        }

        // Innen- und Außenkurve auf gleiche Punktzahl bringen (für die Eckstreifen)
        private static void Match(List<Vector3> a, List<Vector3> b)
        {
            int n = Mathf.Max(a.Count, b.Count);
            Resample(a, n); Resample(b, n);
        }

        private static void Resample(List<Vector3> l, int n)
        {
            if (l.Count == n) return;
            var src = new List<Vector3>(l); l.Clear();
            for (int i = 0; i < n; i++)
            {
                float f = i / (float)(n - 1) * (src.Count - 1);
                int k = Mathf.Min(src.Count - 2, Mathf.FloorToInt(f));
                l.Add(Vector3.Lerp(src[k], src[k + 1], f - k));
            }
        }

        private static List<Vector3Int> Triangulate(List<Vector3> poly3)
        {
            var poly = new List<Vector2>(); foreach (var q in poly3) poly.Add(new Vector2(q.x, q.z));
            float area = 0f;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) area += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            var idx = new List<int>(); for (int i = 0; i < poly.Count; i++) idx.Add(i);
            if (area < 0f) idx.Reverse();                                      // sicher CCW
            var res = new List<Vector3Int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 4000)
            {
                bool clipped = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    Vector2 a = poly[ia], b = poly[ib], c = poly[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-5f) continue;
                    bool inside = false;
                    foreach (int q in idx)
                    {
                        if (q == ia || q == ib || q == ic) continue;
                        if (InTri(poly[q], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    res.Add(new Vector3Int(ia, ib, ic));                       // idx ist CCW -> Dreieck CCW
                    idx.RemoveAt(k); clipped = true; break;
                }
                if (!clipped) break;
            }
            if (idx.Count >= 3)
                for (int k = 1; k + 1 < idx.Count; k++) res.Add(new Vector3Int(idx[0], idx[k], idx[k + 1]));
            return res;
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
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
                int jHere = sg.Internal ? net.JunctionOf(sg.A)
                          : s < sg.TrimA ? net.JunctionOf(sg.A) : s > sg.Length - sg.TrimB ? net.JunctionOf(sg.B) : -1;
                if (jHere >= 0)
                {
                    if (!inJunction) { inJunction = true; jNode = jHere; }
                    continue;                                   // Punkt liegt in der Kreuzungsfläche
                }
                var smp = RoadNet.At(sg, s);
                float lane = Lane(smp, sg.Urban[RoadNet.SampleAt(sg, s)]);
                if (inJunction && res.Points.Count > 0)
                {
                    // Kurve durch die Kreuzung: Steuerpunkt = Schnitt von Ein- und Ausfahrtsrichtung
                    // (geradeaus -> gerade Linie, auch über Mittelstreifen-Kreuzungen)
                    var jn = net.Junctions[jNode];
                    Vector3 inDir = res.Points.Count > 1 ? lastPos - res.Points[res.Points.Count - 2] : Vector3.zero; inDir.y = 0f;
                    int dirSign = i + 1 < n && seg[i + 1] == seg[i] ? (arcS[i + 1] >= s ? 1 : -1) : 1;
                    Vector3 outDir = RoadNet.At(sg, s + dirSign * 2f).pos - smp.pos; outDir.y = 0f;
                    Vector3 ctrl = (lastPos + smp.pos) * .5f;
                    if (inDir.sqrMagnitude > 1e-4f && outDir.sqrMagnitude > 1e-4f &&
                        RoadNet.LineX(new Vector2(lastPos.x, lastPos.z), new Vector2(inDir.x, inDir.z).normalized,
                                      new Vector2(smp.pos.x, smp.pos.z), -new Vector2(outDir.x, outDir.z).normalized, out float t1, out float t2) &&
                        t1 > 0f && t2 > 0f && t1 < Vector3.Distance(lastPos, smp.pos) * 1.5f)
                    {
                        Vector3 cx = lastPos + inDir.normalized * t1;
                        ctrl = new Vector3(cx.x, (lastPos.y + smp.pos.y) * .5f, cx.z);
                    }
                    ctrl.y = Mathf.Lerp(ctrl.y, jn.Y, .5f);
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
