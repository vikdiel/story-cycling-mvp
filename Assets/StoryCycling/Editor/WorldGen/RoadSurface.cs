using System.Collections.Generic;
using LibTessDotNet.Double;
using UnityEngine;
using Mesh = UnityEngine.Mesh;

namespace StoryCycling.WorldGen.Editor
{
    // Straßenoberfläche als FLÄCHEN-VEREINIGUNG statt zusammengesetzter Einzelteile, je 200-m-Kachel:
    //   Asphalt  = Vereinigung aller Fahrbahnstreifen (unsere Breiten, auf denen auch die Fahrlinie liegt)
    //              + Kreuzungsflächen (osm2streets, sonst eigene gerundete Bordsteinecken),
    //              danach GESCHLOSSEN (Dilatation + Erosion um CloseR): füllt Kerben, Zähne und Keile schmaler als
    //              2·CloseR und rundet Innenecken; Mittelstreifen werden anschließend wieder ausgespart.
    //   Gehweg / Randstreifen = BAND um den fertigen Asphaltrand (Minkowski-Summe mit Kreisscheibe, minus Asphalt).
    //              Das Band folgt dem Asphaltrand damit exakt: keine Lücken, keine Streifen über Zufahrten, keine
    //              Zähne an Inseln; Kreisverkehr- und Spritzinseln bekommen automatisch einen sauberen Rand.
    //              Breite je Randkante nach nächster Fahrbahnprobe: innerorts Gehweg, außerorts Randstreifen,
    //              Mittelstreifenseite nur schmaler Bord.
    // Boolesche Operationen über Umlaufregeln von LibTess (GLU-Tesselator, doppelte Genauigkeit). Jede Kachel wird
    // mit Rand (Margin) gerechnet und erst am Ende exakt auf die Kachel beschnitten -> nahtlos zwischen Kacheln.
    public static class RoadSurface
    {
        private const float Bucket = 400f;
        private const float Tile = 200f;
        private const float CloseR = 1.5f;             // Schließradius (füllt Lücken < 3 m)
        private const float SidewalkW = 2f, ShoulderW = 1.6f, MedianW = .35f;
        private const float Margin = 2f * CloseR + SidewalkW + 2f;

        private sealed class Ctx
        {
            public RoadField Near; public List<bool> Urban; public List<byte> LK, RK;
        }

        // externalJunctions: Kreuzungsflächen von osm2streets (siehe Osm2StreetsGeometry), in Weltkoordinaten.
        // progress(Anteil) -> true = abbrechen
        // Ergebnis für den Prüfbericht (WorldCheck): 2D-Abdeckung von Asphalt und Gehweg/Randstreifen + Markierungen
        public sealed class Result
        {
            public readonly Coverage Asphalt = new Coverage(), Band = new Coverage();
            public float LaneLineM, CenterLineM, EdgeLineM; public int StopLines, YieldLines, FailedTiles;
        }

        // Dreiecke in der Ebene (x, z) mit Rasterindex — Punkt-in-Fläche-Abfragen für Prüfungen
        public sealed class Coverage
        {
            private const float C = 10f;
            private readonly List<Vector2> t = new List<Vector2>();
            private readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
            public int Count => t.Count / 3;
            private static long K(int x, int z) => ((long)x << 32) | (uint)z;
            public void Add(Vector2 a, Vector2 b, Vector2 c)
            {
                if (Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) < 2e-3f) return;   // entartet (Fläche ~0)
                int id = t.Count / 3; t.Add(a); t.Add(b); t.Add(c);
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / C), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / C);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / C), z1 = Mathf.FloorToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / C);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = K(x, z);
                        if (!map.TryGetValue(k, out var l)) { l = new List<int>(); map[k] = l; }
                        l.Add(id);
                    }
            }
            // Anzahl eigener Dreiecke, deren Schwerpunkt in der anderen Fläche liegt
            public int CentroidsInside(Coverage other)
            {
                int n = 0;
                for (int i = 0; i + 2 < t.Count; i += 3)
                {
                    Vector2 c = (t[i] + t[i + 1] + t[i + 2]) / 3f;
                    if (other.Inside(c.x, c.y)) n++;
                }
                return n;
            }

            public bool Inside(float x, float z)
            {
                if (!map.TryGetValue(K(Mathf.FloorToInt(x / C), Mathf.FloorToInt(z / C)), out var l)) return false;
                foreach (int i in l)
                {
                    Vector2 a = t[3 * i], b = t[3 * i + 1], c = t[3 * i + 2];
                    float d1 = (x - b.x) * (a.y - b.y) - (a.x - b.x) * (z - b.y);
                    float d2 = (x - c.x) * (b.y - c.y) - (b.x - c.x) * (z - c.y);
                    float d3 = (x - a.x) * (c.y - a.y) - (c.x - a.x) * (z - a.y);
                    bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
                    if (!(neg && pos)) return true;
                }
                return false;
            }
        }

        private static Result rec;

        public static Result Build(RoadNet net, Transform parent, RoadMaterials mats, System.Func<Mesh, Mesh> save,
                                 System.Func<float, bool> progress = null, List<Vector2[]> externalJunctions = null)
        {
            rec = new Result();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var asphalt = new List<Vector2[]>(); var junctions = new List<Vector2[]>(); var keepOut = new List<Vector2[]>();
            var samples = new List<RoadField.Sample>();
            var ctx = new Ctx { Urban = new List<bool>(), LK = new List<byte>(), RK = new List<byte>() };
            foreach (var sg in net.Segs)
            {
                for (int k = 0; k < sg.S.Count; k++)
                { samples.Add(sg.S[k]); ctx.Urban.Add(sg.Urban[k]); ctx.LK.Add(sg.LeftKind[k]); ctx.RK.Add(sg.RightKind[k]); }
                Strips(sg, 0, sg.S.Count - 1, k => 0f, k => 0f, Vector2.zero, asphalt);
                MedianKeepOut(sg, true, keepOut); MedianKeepOut(sg, false, keepOut);
            }
            int fillets = 0, o2sUsed = 0;
            if (externalJunctions != null)
            {
                // nur Kreuzungsflächen an echten Kreuzungen unseres Netzes; osm2streets legt auch an Knoten mit bloßem
                // Tag-Wechsel "Kreuzungen" an, deren (osm2streets-)Breite in Mittelstreifen ragen kann
                var centers = new List<Vector3>(); foreach (var j in net.Junctions) centers.Add(new Vector3(j.Center.x, 0f, j.Center.y));
                var hash = centers.Count > 0 ? new RoadNet.PointHash(centers, 20f) : null;
                foreach (var poly in externalJunctions)
                {
                    Vector2 c = Vector2.zero; foreach (var q in poly) c += q; c /= poly.Length;
                    float rad = 0f; foreach (var q in poly) rad = Mathf.Max(rad, (q - c).magnitude);
                    if (hash != null && hash.Nearest(c, rad + 8f, out _) < float.MaxValue) { junctions.Add(poly); o2sUsed++; }
                }
            }
            else foreach (var j in net.Junctions) fillets += AddFillets(net, j, junctions, Vector2.zero);
            ctx.Near = new RoadField(samples);

            // Kacheln: welche Stücke berühren welche Kachel inkl. Rand (über die Hüllrechtecke)
            var tiles = new Dictionary<long, TileSet>();
            Index(asphalt, 0, tiles); Index(keepOut, 1, tiles); Index(junctions, 2, tiles);
            var keys = new List<long>(); foreach (var kv in tiles) if (kv.Value.A.Count > 0) keys.Add(kv.Key);
            keys.Sort();

            var buckets = new Dictionary<long, RoadProfile.Parts>();
            System.Func<Vector3, RoadProfile.Parts> PartsAt = p =>
            {
                long key = ((long)Mathf.FloorToInt(p.x / Bucket) << 32) | (uint)Mathf.FloorToInt(p.z / Bucket);
                if (!buckets.TryGetValue(key, out var parts)) { parts = new RoadProfile.Parts(); buckets[key] = parts; }
                return parts;
            };
            shared.Clear();
            int done = 0, failed = 0, curbs = 0; long triCount = 0; bool cancelled = false;
            foreach (long key in keys)
            {
                if (progress != null && progress(done / (float)keys.Count)) { cancelled = true; break; }
                done++;
                var ts = tiles[key];
                int tx = (int)(key >> 32), tz = (int)(uint)key;
                Vector2 o = new Vector2((tx + .5f) * Tile, (tz + .5f) * Tile);      // Kachelmitte = lokaler Ursprung
                try
                {
                    float hm = Tile * .5f + Margin;
                    var a = Clip(asphalt, ts.A, o, hm); var k = Clip(keepOut, ts.K, o, hm); var jn = Clip(junctions, ts.J, o, hm);
                    triCount += ProcessTile(a, jn, k, o, ctx, PartsAt, ref curbs);
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"Straßenoberfläche: Kachel {tx}/{tz} übersprungen ({ex.GetType().Name}: {ex.Message})");
                }
            }

            // Markierungen aus dem Spurmodell: je Abschnitt bis zum Kreuzungsbeginn (Beschnitt), nie in Kreuzungen
            foreach (var sg in net.Segs)
            {
                if (sg.Internal) continue;
                float s0 = sg.TrimA, s1 = sg.Length - sg.TrimB;
                if (s1 - s0 < 2f) continue;
                var sub = new List<RoadField.Sample>(); var ks = new List<int>();
                sub.Add(RoadNet.At(sg, s0)); ks.Add(RoadNet.SampleAt(sg, s0));
                for (int i = 0; i < sg.S.Count; i++) if (sg.S[i].distance > s0 + .2f && sg.S[i].distance < s1 - .2f) { sub.Add(sg.S[i]); ks.Add(i); }
                sub.Add(RoadNet.At(sg, s1)); ks.Add(RoadNet.SampleAt(sg, s1));
                EmitLaneMarkings(PartsAt(sub[sub.Count / 2].pos), sg, sub, ks);
            }
            if (net.Rules.stopLines) EmitStopLines(net, PartsAt);

            int meshes = 0;
            foreach (var kv in buckets)
            {
                if (kv.Value.IsEmpty) continue;
                Mesh mesh = kv.Value.ToMesh();
                mesh.RecalculateNormals();
                mesh.name = $"RoadSurface_{meshes:D3}";
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterials = mats.Ribbon;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshes++;
            }
            shared.Clear();
            string state = cancelled ? " ABGEBROCHEN" : failed > 0 ? $" ({failed} Kacheln übersprungen)" : "";
            string src = externalJunctions != null ? $"{o2sUsed}/{externalJunctions.Count} Kreuzungsflächen (osm2streets)" : $"{fillets} Bordsteinecken (eigene)";
            rec.FailedTiles = failed;
            Debug.Log($"Straßenoberfläche{state}: {asphalt.Count} Fahrbahnstücke + {src}, {keepOut.Count} Mittelstreifen-Aussparungen, " +
                      $"{keys.Count} Kacheln, {triCount} Dreiecke, {curbs} Bordsteinkanten, {meshes} Meshes, {clock.ElapsedMilliseconds} ms.");
            var res = rec; rec = null;
            return res;
        }

        // ------------------------------------------------------------------ Markierungen (Südafrika)
        //   gelb durchgezogen: Randlinie außerorts an der Fahrstreifenkante; an Doppelfahrbahnen auch zur Mittelinsel
        //   weiß unterbrochen: Mittellinie zwischen den Richtungen (3 m / 9 m), Spurtrenner gleicher Richtung (3 m / 6 m)
        private static void EmitLaneMarkings(RoadProfile.Parts p, RoadNet.Segment sg, List<RoadField.Sample> s, List<int> ks)
        {
            int n = s.Count; if (n < 2) return;
            // Fahrstreifenkanten (Carriageway) je Probe
            System.Func<int, float> CwL = i => -s[i].half + sg.Lanes[ks[i]].Sh;
            System.Func<int, float> Cw = i => Mathf.Max(0f, 2f * (s[i].half - sg.Lanes[ks[i]].Sh));
            System.Func<int, int, bool> Same = (i, j) =>
            {
                var a = sg.Lanes[ks[i]]; var b = sg.Lanes[ks[j]];
                return a.L == b.L && a.R == b.R && a.Center == b.Center && Cw(i) > 2f && Cw(j) > 2f;
            };
            // gelbe Randlinien
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                System.Func<int, bool> median = i => (left ? sg.LeftKind[ks[i]] : sg.RightKind[ks[i]]) == RoadNet.KindMedian;
                System.Func<int, float> ins = i => s[i].inset >= 0f ? s[i].inset : Mathf.Max(0f, sg.Lanes[ks[i]].Sh - .12f);
                System.Func<int, bool> on = i => !sg.Fallback && (median(i) || (!sg.Urban[ks[i]] && s[i].inset >= 0f));
                float sgn = left ? -1f : 1f;
                rec.EdgeLineM += Line(p, s, i => sgn * (s[i].half - ins(i) - .125f), .15f, 2, 0f, 0f, i => on(i));
            }
            // weiße Linien: Mittellinie + Spurtrenner
            int maxN = 0; foreach (int k in ks) maxN = Mathf.Max(maxN, sg.Lanes[k].N);
            for (int b = 1; b < maxN; b++)
            {
                int bb = b;
                System.Func<int, bool> isCenter = i => sg.Lanes[ks[i]].Center && sg.Lanes[ks[i]].L == bb;
                System.Func<int, bool> isLane = i => bb < sg.Lanes[ks[i]].N && !(sg.Lanes[ks[i]].Dir == 0 && sg.Lanes[ks[i]].L == bb);
                System.Func<int, float> x = i => CwL(i) + Cw(i) * bb / Mathf.Max(1, sg.Lanes[ks[i]].N);
                rec.CenterLineM += Line(p, s, x, .14f, 3, 3f, 9f, i => i + 1 < n && Same(i, i + 1) && isCenter(i));
                rec.LaneLineM += Line(p, s, x, .12f, 3, 3f, 6f, i => i + 1 < n && Same(i, i + 1) && isLane(i) && !sg.Fallback);
            }
        }

        // Längsstreifen: x(i) = Mitte des Streifens quer zur Fahrbahn (rechts positiv), Breite w; gibt die Länge zurück
        private static float Line(RoadProfile.Parts p, List<RoadField.Sample> s, System.Func<int, float> x, float w, int sub,
                                  float dashOn, float period, System.Func<int, bool> allowed)
        {
            float len = 0f;
            for (int i = 0; i + 1 < s.Count; i++)
            {
                if (!allowed(i) || !allowed(i + 1)) continue;
                if (dashOn > 0f && Mathf.Repeat(s[i].distance, period) >= dashOn) continue;
                int b = p.V.Count;
                for (int k = 0; k < 2; k++)
                {
                    var q = s[i + k]; float c = x(i + k);
                    p.V.Add(q.pos + q.side * (c - w * .5f) + Vector3.up * .035f); p.N.Add(Vector3.up); p.UV.Add(Vector2.zero);
                    p.V.Add(q.pos + q.side * (c + w * .5f) + Vector3.up * .035f); p.N.Add(Vector3.up); p.UV.Add(Vector2.zero);
                }
                RoadProfile.Quad(p.T[sub], b, b + 1, b + 2, b + 3);
                len += s[i + 1].distance - s[i].distance;
            }
            return len;
        }

        // Haltelinien (untergeordnete Zufahrt, Ampel, Stoppschild) bzw. Wartelinien (Kreisverkehr-Zufahrt) quer über
        // die ankommenden Fahrstreifen am Kreuzungsrand. Linksverkehr: an Ende B kommen die Spuren L an, an Ende A die R.
        private static void EmitStopLines(RoadNet net, System.Func<Vector3, RoadProfile.Parts> PartsAt)
        {
            foreach (var j in net.Junctions)
            {
                bool signals = false, stop = false, ring = false; int major = 9;
                foreach (int ni in j.NodesIn) { signals |= net.Nodes[ni].Signals; stop |= net.Nodes[ni].Stop; }
                foreach (var e in j.Ends)
                {
                    var sg = net.Segs[e.Seg]; if (sg.S.Count == 0) continue;
                    ring |= sg.Roundabout;
                    major = Mathf.Min(major, sg.Rank[e.AtA ? 0 : sg.Rank.Count - 1]);
                }
                if (j.Ends.Count < 3 && !signals && !stop) continue;
                foreach (var e in j.Ends)
                {
                    var sg = net.Segs[e.Seg];
                    if (sg.Internal || sg.Roundabout || sg.Fallback || sg.S.Count < 2 || e.Trim < 1f) continue;
                    int k = e.AtA ? 0 : sg.S.Count - 1;
                    var li = sg.Lanes[k]; int rank = sg.Rank[k];
                    bool yield = ring;
                    if (!(yield || signals || stop || rank > major)) continue;
                    float at = e.AtA ? e.Trim + .25f : sg.Length - e.Trim - .25f;
                    if (at < .5f || at > sg.Length - .5f) continue;
                    var q = RoadNet.At(sg, at);
                    float cwl = -q.half + li.Sh, cw = 2f * (q.half - li.Sh);
                    if (cw < 2f || li.N == 0) continue;
                    float x0, x1;
                    if (li.Dir == 0) { float div = cwl + cw * li.L / li.N; if (e.AtA) { x0 = div; x1 = cwl + cw; } else { x0 = cwl; x1 = div; } }
                    else if ((li.Dir > 0) == !e.AtA) { x0 = cwl; x1 = cwl + cw; }
                    else continue;                                                        // Einbahn verlässt hier die Kreuzung
                    if (x1 - x0 < 1f) continue;
                    var p = PartsAt(q.pos);
                    Vector3 t = new Vector3(q.tangent.x, 0f, q.tangent.z).normalized;
                    float depth = yield ? .3f : .45f;
                    if (yield)
                    {
                        for (float xa = x0 + .1f; xa + .6f <= x1; xa += 1f) CrossBar(p, q, t, xa, xa + .6f, depth);
                        rec.YieldLines++;
                    }
                    else { CrossBar(p, q, t, x0 + .1f, x1 - .1f, depth); rec.StopLines++; }
                }
            }
        }

        private static void CrossBar(RoadProfile.Parts p, RoadField.Sample q, Vector3 t, float x0, float x1, float depth)
        {
            int b = p.V.Count;
            Vector3 up = Vector3.up * .036f, d = t * (depth * .5f);
            p.V.Add(q.pos + q.side * x0 - d + up); p.V.Add(q.pos + q.side * x1 - d + up);
            p.V.Add(q.pos + q.side * x0 + d + up); p.V.Add(q.pos + q.side * x1 + d + up);
            for (int k = 0; k < 4; k++) { p.N.Add(Vector3.up); p.UV.Add(Vector2.zero); }
            RoadProfile.Quad(p.T[3], b, b + 1, b + 2, b + 3);
        }

        private sealed class TileSet { public readonly List<int> A = new List<int>(), K = new List<int>(), J = new List<int>(); }

        private static void Index(List<Vector2[]> pieces, int kind, Dictionary<long, TileSet> tiles)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                var c = pieces[i];
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                foreach (var q in c) { x0 = Mathf.Min(x0, q.x); z0 = Mathf.Min(z0, q.y); x1 = Mathf.Max(x1, q.x); z1 = Mathf.Max(z1, q.y); }
                x0 -= Margin; z0 -= Margin; x1 += Margin; z1 += Margin;
                for (int tx = Mathf.FloorToInt(x0 / Tile); tx <= Mathf.FloorToInt(x1 / Tile); tx++)
                for (int tz = Mathf.FloorToInt(z0 / Tile); tz <= Mathf.FloorToInt(z1 / Tile); tz++)
                {
                    long key = ((long)tx << 32) | (uint)tz;
                    if (!tiles.TryGetValue(key, out var ts)) { ts = new TileSet(); tiles[key] = ts; }
                    (kind == 0 ? ts.A : kind == 1 ? ts.K : ts.J).Add(i);
                }
            }
        }

        // Stücke auf das Quadrat ±h um o beschneiden (Sutherland-Hodgman), lokale Koordinaten
        private static List<Vector2[]> Clip(List<Vector2[]> pieces, List<int> idx, Vector2 o, float h)
        {
            var res = new List<Vector2[]>(idx.Count);
            foreach (int i in idx)
            {
                var poly = new List<Vector2>(pieces[i].Length);
                foreach (var q in pieces[i]) poly.Add(q - o);
                poly = ClipEdge(poly, 0, -h); if (poly.Count < 3) continue;
                poly = ClipEdge(poly, 1, h); if (poly.Count < 3) continue;
                poly = ClipEdge(poly, 2, -h); if (poly.Count < 3) continue;
                poly = ClipEdge(poly, 3, h); if (poly.Count < 3) continue;
                res.Add(poly.ToArray());
            }
            return res;
        }

        // side 0: x >= v, 1: x <= v, 2: y >= v, 3: y <= v
        private static List<Vector2> ClipEdge(List<Vector2> p, int side, float v)
        {
            var r = new List<Vector2>(p.Count + 4);
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % p.Count];
                bool ina = Inside(a, side, v), inb = Inside(b, side, v);
                if (ina) r.Add(a);
                if (ina != inb)
                {
                    float t = side < 2 ? (v - a.x) / (b.x - a.x) : (v - a.y) / (b.y - a.y);
                    var q = a + (b - a) * t;
                    if (side < 2) q.x = v; else q.y = v;
                    r.Add(q);
                }
            }
            return r;
        }
        private static bool Inside(Vector2 q, int side, float v) => side == 0 ? q.x >= v : side == 1 ? q.x <= v : side == 2 ? q.y >= v : q.y <= v;

        // Kante liegt auf dem Rand des Quadrats ±h (künstliche Schnittkante, kein echter Straßenrand)
        private static bool OnBorder(Vector2 a, Vector2 b, float h)
        {
            const float e = 2e-3f;
            return (Mathf.Abs(a.x - h) < e && Mathf.Abs(b.x - h) < e) || (Mathf.Abs(a.x + h) < e && Mathf.Abs(b.x + h) < e) ||
                   (Mathf.Abs(a.y - h) < e && Mathf.Abs(b.y - h) < e) || (Mathf.Abs(a.y + h) < e && Mathf.Abs(b.y + h) < e);
        }

        private static long ProcessTile(List<Vector2[]> stripsIn, List<Vector2[]> junctionsIn, List<Vector2[]> keepOutIn, Vector2 o, Ctx ctx,
                                        System.Func<Vector3, RoadProfile.Parts> PartsAt, ref int curbs)
        {
            float hm = Tile * .5f + Margin, h = Tile * .5f;

            // 1) Asphalt vereinigen und schließen: D = A ⊕ Scheibe(r), dann D ⊖ Scheibe(r).
            //    Vor jedem Versatz Splitter (Fläche ~0, von LibTess gelegentlich ausgegeben) und Stachel entfernen —
            //    sonst behandelt die Erosion sie als Rand und fräst einen 2r breiten Graben mitten in die Fahrbahn.
            var all0 = new List<Vector2[]>(stripsIn); all0.AddRange(junctionsIn);
            var a0 = Clean(Contours(all0, WindingRule.NonZero), hm);
            var dil = new List<Vector2[]>(a0); dil.AddRange(Ccw(Band(a0, (p, q) => CloseR, hm)));
            var d = Clean(Contours(dil, WindingRule.NonZero, false), hm);
            var ero = Contours(Band(d, (p, q) => CloseR, hm), WindingRule.NonZero);
            var closed = new List<Vector2[]>(d); closed.AddRange(Reverse(ero));
            var fill = Contours(closed, WindingRule.Positive, false);
            // 2) Mittelstreifen wieder aussparen (das Schließen würde schmale Mittelstreifen zuschütten, Kreuzungsflächen
            //    können hineinragen). Unsere Fahrbahnstreifen werden nie beschnitten: Ergebnis = Streifen ∪ (geschlossen −
            //    Aussparung), so kann eine ungenau gemessene Mittelstreifenbreite (z. B. wo Fahrbahnen zusammenlaufen)
            //    keine Nachbarfahrbahn anschneiden.
            if (keepOutIn.Count > 0)
            {
                var ko = Contours(keepOutIn, WindingRule.NonZero);
                var l = new List<Vector2[]>(fill); l.AddRange(Reverse(ko));
                fill = Contours(l, WindingRule.Positive, false);
            }
            var un = new List<Vector2[]>(fill); un.AddRange(Ccw(stripsIn));
            var asp = Clean(Contours(un, WindingRule.NonZero, false), hm);

            // 3) Gehweg/Randstreifen: Band um den fertigen Asphaltrand, Breite je Kante nach nächster Fahrbahnprobe
            System.Func<Vector2, Vector2, float> BandW = (p, q) =>
            {
                Vector2 m = (p + q) * .5f + o;
                if (!ctx.Near.Nearest(m.x, m.y, 25f, out int i, out _)) return ShoulderW;
                var s = ctx.Near.Samples[i];
                float lat = (m.x - s.pos.x) * s.side.x + (m.y - s.pos.z) * s.side.z;
                byte kind = lat > 0f ? ctx.RK[i] : ctx.LK[i];
                if (kind == RoadNet.KindMedian) return MedianW;
                return ctx.Urban[i] ? SidewalkW : ShoulderW;
            };
            var outer = new List<Vector2[]>(asp); outer.AddRange(Ccw(Band(asp, BandW, hm)));
            var all = Contours(outer, WindingRule.NonZero, false);

            // 4) exakt auf die Kachel beschneiden (Schnitt mit dem Kachelquadrat: Umlaufzahl 2)
            var rect = new[] { new Vector2(-h, -h), new Vector2(h, -h), new Vector2(h, h), new Vector2(-h, h) };
            var aIn = new List<Vector2[]>(asp) { rect };
            var aTileC = Contours(aIn, WindingRule.AbsGeqTwo, false);
            var asphaltTris = Refine(Triangles(aIn, new List<Vector2[]>(), WindingRule.AbsGeqTwo), 15f);
            var bIn = new List<Vector2[]>(all); bIn.AddRange(Reverse(asp)); bIn.Add(rect);
            var bandTris = Refine(Triangles(bIn, new List<Vector2[]>(), WindingRule.AbsGeqTwo), 15f);
            var oIn = new List<Vector2[]>(all) { rect };
            var outline = Contours(oIn, WindingRule.AbsGeqTwo, false);

            // Band nach Ortslage aufteilen: innerorts erhöhter Gehweg, außerorts Randstreifen
            var urbanTris = new List<Vector2>(); var ruralTris = new List<Vector2>();
            for (int t = 0; t + 2 < bandTris.Count; t += 3)
            {
                Vector2 c = (bandTris[t] + bandTris[t + 1] + bandTris[t + 2]) / 3f + o;
                bool u = ctx.Near.Nearest(c.x, c.y, 25f, out int i, out _) && ctx.Urban[i];
                var dst = u ? urbanTris : ruralTris;
                dst.Add(bandTris[t]); dst.Add(bandTris[t + 1]); dst.Add(bandTris[t + 2]);
            }

            System.Func<Vector2, float, float, Vector3> Lift = (p2, baseOff, outerOff) =>
            {
                var w = new Vector3(p2.x + o.x, 0f, p2.y + o.y);
                if (ctx.Near.Nearest(w.x, w.z, 25f, out int i, out float dd))
                {
                    var s = ctx.Near.Samples[i];
                    float t = outerOff == baseOff ? 0f : Mathf.Clamp01((dd - s.half) / 2f);
                    w.y = s.pos.y + Mathf.Lerp(baseOff, outerOff, t);
                }
                return w;
            };
            if (rec != null)
            {
                for (int t = 0; t + 2 < asphaltTris.Count; t += 3) rec.Asphalt.Add(asphaltTris[t] + o, asphaltTris[t + 1] + o, asphaltTris[t + 2] + o);
                for (int t = 0; t + 2 < urbanTris.Count; t += 3) rec.Band.Add(urbanTris[t] + o, urbanTris[t + 1] + o, urbanTris[t + 2] + o);
                for (int t = 0; t + 2 < ruralTris.Count; t += 3) rec.Band.Add(ruralTris[t] + o, ruralTris[t + 1] + o, ruralTris[t + 2] + o);
            }
            cellOrigin = o;
            EmitTris(asphaltTris, 0, p => Lift(p, .02f, .02f), PartsAt);
            EmitTris(urbanTris, 4, p => Lift(p, .16f, .16f), PartsAt);
            EmitTris(ruralTris, 1, p => Lift(p, .015f, -.06f), PartsAt);

            // Bordsteinkanten (innerorts) entlang des Asphaltrands, Schürzen am Außenrand des Bandes (nicht an Kachelgrenzen)
            foreach (var c in aTileC)
                for (int i = 0; i < c.Length; i++)
                {
                    Vector2 a2 = c[i], b2 = c[(i + 1) % c.Length];
                    if (OnBorder(a2, b2, h)) continue;
                    Vector2 m2 = (a2 + b2) * .5f;
                    if (!ctx.Near.Nearest(m2.x + o.x, m2.y + o.y, 25f, out int si, out _) || !ctx.Urban[si]) continue;
                    Vector3 a = Lift(a2, .02f, .02f), b = Lift(b2, .02f, .02f);
                    Wall(PartsAt(a), 4, a, b, .14f, 0f);          // Asphalt liegt links der Umrisskante -> Kante zeigt zur Fahrbahn
                    curbs++;
                }
            foreach (var c in outline)
                for (int i = 0; i < c.Length; i++)
                {
                    if (OnBorder(c[i], c[(i + 1) % c.Length], h)) continue;
                    Vector3 a = Lift(c[i], .02f, .02f), b = Lift(c[(i + 1) % c.Length], .02f, .02f);
                    Wall(PartsAt(a), 1, b - Vector3.up * .08f, a - Vector3.up * .08f, 0f, -1.4f);   // nach außen sichtbar
                }
            return (asphaltTris.Count + urbanTris.Count + ruralTris.Count) / 3;
        }

        // Alle Punkte im Abstand <= w(Kante) vom Rand der Umrisse (ohne künstliche Kachelrand-Kanten):
        // je Kante ein beidseitiges Rechteck, je Knick ein Kreissektor auf der Außenseite des Knicks.
        // Vereinigt mit der Fläche ergibt das die Minkowski-Summe mit einer Kreisscheibe (Offset nach außen),
        // abgezogen von der Fläche die Erosion (Offset nach innen).
        private static List<Vector2[]> Band(List<Vector2[]> contours, System.Func<Vector2, Vector2, float> width, float hm)
        {
            const float FanMin = .02f;     // flachere Knicke: Rechtecke auf Gehrung verlängern statt Fächer (robuster)
            var res = new List<Vector2[]>();
            foreach (var c in contours)
            {
                int n = c.Length;
                if (n < 3) continue;
                var w = new float[n]; var nl = new Vector2[n]; var dir = new Vector2[n]; var ok = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = c[i], b = c[(i + 1) % n], dd = b - a;
                    float len = dd.magnitude;
                    ok[i] = len > 1e-4f && !OnBorder(a, b, hm);
                    if (!ok[i]) continue;
                    w[i] = width(a, b);
                    if (w[i] <= 0f) { ok[i] = false; continue; }
                    dir[i] = dd / len;
                    nl[i] = new Vector2(-dd.y, dd.x) / len;          // Normale nach links
                }
                var ang = new float[n];                              // Knick in c[i] zwischen Kante i-1 und Kante i
                for (int i = 0; i < n; i++)
                {
                    int p = (i + n - 1) % n;
                    ang[i] = ok[p] && ok[i] ? Mathf.Atan2(nl[p].x * nl[i].y - nl[p].y * nl[i].x, Vector2.Dot(nl[p], nl[i])) : 0f;
                }
                for (int i = 0; i < n; i++)
                {
                    if (!ok[i]) continue;
                    int nx = (i + 1) % n;
                    // Gehrung: bei flachen Knicken die Rechtecke um w·tan(φ/2) (+5 mm) über die Ecke hinaus verlängern —
                    // schließt den Keil zwischen den Rechtecken ohne (numerisch heikle) Fast-Null-Fächer
                    float e0 = Mathf.Abs(ang[i]) > 1e-6f && Mathf.Abs(ang[i]) < FanMin ? w[i] * Mathf.Tan(Mathf.Abs(ang[i]) * .5f) + .005f : 0f;
                    float e1 = Mathf.Abs(ang[nx]) > 1e-6f && Mathf.Abs(ang[nx]) < FanMin ? w[i] * Mathf.Tan(Mathf.Abs(ang[nx]) * .5f) + .005f : 0f;
                    Vector2 a = c[i] - dir[i] * e0, b = c[nx] + dir[i] * e1;
                    res.Add(new[] { a - nl[i] * w[i], b - nl[i] * w[i], b + nl[i] * w[i], a + nl[i] * w[i] });
                }
                for (int i = 0; i < n; i++)
                {
                    int p = (i + n - 1) % n;                         // Kante p endet in c[i], Kante i beginnt dort
                    if (!ok[p] || !ok[i] || Mathf.Abs(ang[i]) < FanMin) continue;
                    float r = Mathf.Max(w[p], w[i]);
                    // Linksknick: Lücke rechts (-nl), Rechtsknick: Lücke links (+nl)
                    float sgn = ang[i] > 0f ? -1f : 1f;
                    Vector2 n0 = nl[p] * sgn;
                    int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(ang[i]) / (Mathf.PI / 12f)));
                    var fan = new Vector2[steps + 2];
                    fan[0] = c[i];
                    float a0 = Mathf.Atan2(n0.y, n0.x);
                    for (int st = 0; st <= steps; st++)
                    {
                        float t = a0 + ang[i] * st / steps;
                        fan[st + 1] = c[i] + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r;
                    }
                    res.Add(fan);
                }
            }
            return res;
        }

        // Umrisse bereinigen: doppelte und nahezu kollineare Punkte sowie Stachel (Hin- und Rückweg auf derselben
        // Linie) entfernen, Splitter-Umrisse (mittlere Breite < 2 cm) verwerfen. Kachelrand-Punkte bleiben.
        private static List<Vector2[]> Clean(List<Vector2[]> contours, float hm)
        {
            var res = new List<Vector2[]>(contours.Count);
            foreach (var c in contours)
            {
                var l = new List<Vector2>(c);
                for (int pass = 0; pass < 12 && l.Count >= 3; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < l.Count && l.Count >= 3;)
                    {
                        Vector2 prev = l[(i + l.Count - 1) % l.Count], cur = l[i], next = l[(i + 1) % l.Count];
                        bool border = Mathf.Abs(Mathf.Abs(cur.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(cur.y) - hm) < 1e-2f;
                        bool drop = (cur - prev).sqrMagnitude < 1e-8f;
                        if (!drop && !border)
                        {
                            Vector2 d = next - prev; float len = d.magnitude;
                            float dev = len > 1e-4f ? Mathf.Abs((cur.x - prev.x) * d.y - (cur.y - prev.y) * d.x) / len : (cur - prev).magnitude;
                            bool spike = Vector2.Dot(cur - prev, next - cur) <= 0f;
                            drop = dev < .03f && (spike || (cur - prev).magnitude < 12f);
                        }
                        if (drop) { l.RemoveAt(i); changed = true; } else i++;
                    }
                    if (!changed) break;
                }
                if (l.Count < 3) continue;
                float area2 = 0f, perim = 0f;
                for (int i = 0, j = l.Count - 1; i < l.Count; j = i++) { area2 += l[j].x * l[i].y - l[i].x * l[j].y; perim += (l[i] - l[j]).magnitude; }
                if (Mathf.Abs(area2) * .5f < .02f * perim) continue;          // Splitter: mittlere Breite < 2 cm
                res.Add(l.ToArray());
            }
            return res;
        }

        // Mittelstreifen-Aussparung einer Fahrbahnseite: von Rand+0,3 m bis 0,3 m vor der Gegenfahrbahn,
        // nicht in Kreuzungsnähe (dort gehört die Fläche zur Kreuzung).
        private static void MedianKeepOut(RoadNet.Segment sg, bool left, List<Vector2[]> outList)
        {
            float from = sg.TrimA + 3f, to = sg.Length - sg.TrimB - 3f;
            System.Func<int, bool> On = k =>
            {
                var s = sg.S[k];
                float gap = left ? sg.LeftGap[k] : sg.RightGap[k];
                byte kind = left ? sg.LeftKind[k] : sg.RightKind[k];
                return kind == RoadNet.KindMedian && gap > .9f && s.distance >= from && s.distance <= to;
            };
            float sgn = left ? -1f : 1f;
            for (int k = 0; k + 1 < sg.S.Count; k++)
            {
                if (!On(k) || !On(k + 1)) continue;
                RoadField.Sample a = sg.S[k], b = sg.S[k + 1];
                float ga = left ? sg.LeftGap[k] : sg.RightGap[k], gb = left ? sg.LeftGap[k + 1] : sg.RightGap[k + 1];
                Vector2 pa = new Vector2(a.pos.x, a.pos.z), pb = new Vector2(b.pos.x, b.pos.z);
                Vector2 sa = new Vector2(a.side.x, a.side.z) * sgn, sb = new Vector2(b.side.x, b.side.z) * sgn;
                outList.Add(new[] { pa + sa * (a.half + .3f), pb + sb * (b.half + .3f),
                                    pb + sb * (b.half + gb - .3f), pa + sa * (a.half + ga - .3f) });
            }
        }

        // Streifen-Polygone eines Abschnitts (Proben k0..k1), bis 40 Proben je Stück; wo eine Kante in einer engen
        // Kurve rückwärts laufen würde (Selbstschnitt), einzelne Vierecke. Breite = half + wl / half + wr.
        private static void Strips(RoadNet.Segment sg, int k0, int k1, System.Func<int, float> wl, System.Func<int, float> wr, Vector2 o, List<Vector2[]> outList)
        {
            var left = new List<Vector2>(); var right = new List<Vector2>();
            System.Action Flush = () =>
            {
                if (left.Count >= 2)
                {
                    var poly = new Vector2[left.Count * 2];
                    for (int i = 0; i < left.Count; i++) { poly[i] = right[i]; poly[left.Count * 2 - 1 - i] = left[i]; }
                    outList.Add(poly);
                }
                left.Clear(); right.Clear();
            };
            for (int k = k0; k <= k1; k++)
            {
                var s = sg.S[k];
                Vector2 p = new Vector2(s.pos.x, s.pos.z) - o, sd = new Vector2(s.side.x, s.side.z);
                Vector2 l = p - sd * (s.half + wl(k)), r = p + sd * (s.half + wr(k));
                if (left.Count > 0)
                {
                    Vector2 t = p - (new Vector2(sg.S[k - 1].pos.x, sg.S[k - 1].pos.z) - o);
                    bool ok = Vector2.Dot(l - left[left.Count - 1], t) > .02f && Vector2.Dot(r - right[right.Count - 1], t) > .02f;
                    if (!ok)
                    {
                        // enge Kurve: bisheriges Stück abschließen, dieses Paar als Einzelviereck
                        Vector2 pl = left[left.Count - 1], pr = right[right.Count - 1];
                        Flush();
                        outList.Add(new[] { pr, r, l, pl });
                        left.Add(l); right.Add(r);
                        continue;
                    }
                }
                left.Add(l); right.Add(r);
                if (left.Count >= 40) { Flush(); left.Add(l); right.Add(r); }
            }
            Flush();
        }

        // Viereck zwischen zwei Proben, links wl / rechts wr breit (lokale 2D-Koordinaten)
        private static Vector2[] Quad(RoadField.Sample a, RoadField.Sample b, float wlA, float wrA, float wlB, float wrB, Vector2 o)
        {
            Vector2 pa = new Vector2(a.pos.x, a.pos.z) - o, pb = new Vector2(b.pos.x, b.pos.z) - o;
            Vector2 sa = new Vector2(a.side.x, a.side.z), sb = new Vector2(b.side.x, b.side.z);
            return new[] { pa - sa * wlA, pa + sa * wrA, pb + sb * wrB, pb - sb * wlB };
        }

        // Gerundete Bordsteinecken: Fläche zwischen zwei Fahrbahnkanten und einem Kreisbogen (Radius 3–4 m)
        private static int AddFillets(RoadNet net, RoadNet.Junction j, List<Vector2[]> asphalt, Vector2 o)
        {
            int m = j.Ends.Count, added = 0;
            if (m < 2) return 0;
            var L = new Vector2[m]; var R = new Vector2[m]; var D = new Vector2[m];
            for (int i = 0; i < m; i++)
            {
                var e = j.Ends[i]; var sg = net.Segs[e.Seg];
                var ts = RoadNet.At(sg, e.AtA ? e.Trim : sg.Length - e.Trim);
                Vector3 r3 = e.AtA ? ts.side : -ts.side;
                Vector2 p = new Vector2(ts.pos.x, ts.pos.z) - o, r2 = new Vector2(r3.x, r3.z);
                R[i] = p + r2 * ts.half; L[i] = p - r2 * ts.half;
                Vector3 d3 = e.AtA ? ts.tangent : -ts.tangent; D[i] = new Vector2(d3.x, d3.z).normalized;
            }
            for (int i = 0; i < m; i++)
            {
                int k = (i + 1) % m;
                if (!RoadNet.LineX(L[i], D[i], R[k], D[k], out float ta, out float tb)) continue;
                if (ta > .5f || tb > .5f || ta < -30f || tb < -30f) continue;           // Schnitt muss zur Kreuzung hin liegen
                float theta = Mathf.Acos(Mathf.Clamp(Vector2.Dot(D[i], D[k]), -1f, 1f));
                if (theta < .35f || theta > 2.8f) continue;
                float r = j.Ends[i].Urban || j.Ends[k].Urban ? 3f : 4f;
                float f = r / Mathf.Tan(theta * .5f);
                if (f > 20f) continue;
                Vector2 c = L[i] + D[i] * ta;
                Vector2 t1 = c + D[i] * f, t2 = c + D[k] * f;
                Vector2 bis = (D[i] + D[k]).normalized;
                Vector2 center = c + bis * (r / Mathf.Sin(theta * .5f));
                float a1 = Mathf.Atan2(t1.y - center.y, t1.x - center.x), a2 = Mathf.Atan2(t2.y - center.y, t2.x - center.x);
                float da = a2 - a1; while (da > Mathf.PI) da -= 2f * Mathf.PI; while (da < -Mathf.PI) da += 2f * Mathf.PI;
                var poly = new List<Vector2> { c, t1 };
                for (int s = 1; s < 6; s++) { float ang = a1 + da * s / 6f; poly.Add(center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r); }
                poly.Add(t2);
                asphalt.Add(poly.ToArray());
                added++;
            }
            return added;
        }

        // ------------------------------------------------------------------ LibTess
        private static Tess NewTess(List<Vector2[]> a, List<Vector2[]> b)
        {
            var tess = new Tess();
            foreach (var c in a) tess.AddContour(ToCV(c), ContourOrientation.Original);
            if (b != null) foreach (var c in b) tess.AddContour(ToCV(c), ContourOrientation.Original);
            return tess;
        }

        private static ContourVertex[] ToCV(Vector2[] c)
        {
            var v = new ContourVertex[c.Length];
            for (int i = 0; i < c.Length; i++) v[i].Position = new Vec3(c[i].x, c[i].y, 0);
            return v;
        }

        // Eingabe-Stücke werden auf gegen den Uhrzeigersinn normiert (Umlaufzahl +1)
        private static List<Vector2[]> Ccw(List<Vector2[]> l)
        {
            var r = new List<Vector2[]>(l.Count);
            foreach (var c in l)
            {
                if (c.Length < 3) continue;
                float a = 0f; for (int i = 0, j = c.Length - 1; i < c.Length; j = i++) a += c[j].x * c[i].y - c[i].x * c[j].y;
                if (Mathf.Abs(a) < 1e-4f) continue;
                if (a < 0f) { var cc = (Vector2[])c.Clone(); System.Array.Reverse(cc); r.Add(cc); } else r.Add(c);
            }
            return r;
        }

        // normalize = Eingabe sind lose Stücke (auf CCW bringen); false = bereits orientierte Umrisse (Löcher bleiben)
        public static List<Vector2[]> Contours(List<Vector2[]> pieces, WindingRule rule, bool normalize = true)
        {
            var tess = NewTess(normalize ? Ccw(pieces) : pieces, null);
            tess.Tessellate(rule, ElementType.BoundaryContours, 3, null, new Vec3(0, 0, 1));
            var res = new List<Vector2[]>();
            if (tess.Elements == null) return res;
            for (int e = 0; e < tess.ElementCount; e++)
            {
                int start = tess.Elements[e * 2], count = tess.Elements[e * 2 + 1];
                var c = new Vector2[count];
                for (int i = 0; i < count; i++) { var p = tess.Vertices[start + i].Position; c[i] = new Vector2((float)p.X, (float)p.Y); }
                res.Add(c);
            }
            return res;
        }

        private static List<Vector2[]> Reverse(List<Vector2[]> l)
        {
            var r = new List<Vector2[]>(l.Count);
            foreach (var c in l) { var cc = (Vector2[])c.Clone(); System.Array.Reverse(cc); r.Add(cc); }
            return r;
        }

        // Dreiecke der Region; 'minus' sind bereits orientierte Umrisse (umgedreht = abziehen)
        public static List<Vector2> Triangles(List<Vector2[]> plus, List<Vector2[]> minus, WindingRule rule)
        {
            var tess = NewTess(minus == null ? Ccw(plus) : plus, minus);
            tess.Tessellate(rule, ElementType.Polygons, 3, null, new Vec3(0, 0, 1));
            var res = new List<Vector2>();
            if (tess.Elements == null) return res;
            for (int e = 0; e < tess.ElementCount; e++)
                for (int k = 0; k < 3; k++)
                {
                    int idx = tess.Elements[e * 3 + k];
                    if (idx < 0) { res.RemoveRange(res.Count - k, k); break; }
                    var p = tess.Vertices[idx].Position;
                    res.Add(new Vector2((float)p.X, (float)p.Y));
                }
            return res;
        }

        // Rissfreie Unterteilung: jede Kante > maxEdge wird halbiert, Nachbardreiecke teilen den Mittelpunkt.
        // So folgt die Oberfläche auch auf langen geraden Stücken dem Höhenverlauf (Kuppen, Senken).
        public static List<Vector2> Refine(List<Vector2> flat, float maxEdge)
        {
            var verts = new List<Vector2>(); var index = new Dictionary<long, int>(); var tris = new List<int>();
            System.Func<Vector2, int> Id = q =>
            {
                long key = ((long)Mathf.RoundToInt(q.x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(q.y * 1000f);
                if (!index.TryGetValue(key, out int i)) { i = verts.Count; verts.Add(q); index[key] = i; }
                return i;
            };
            foreach (var q in flat) tris.Add(Id(q));
            float max2 = maxEdge * maxEdge;
            for (int pass = 0; pass < 8; pass++)
            {
                var mid = new Dictionary<long, int>();
                System.Func<int, int, long> EK = (u, v) => u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                for (int t = 0; t < tris.Count; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int u = tris[t + e], v = tris[t + (e + 1) % 3];
                        if ((verts[u] - verts[v]).sqrMagnitude <= max2) continue;
                        long k = EK(u, v);
                        if (!mid.ContainsKey(k)) { mid[k] = verts.Count; verts.Add((verts[u] + verts[v]) * .5f); }
                    }
                if (mid.Count == 0) break;
                var nt = new List<int>(tris.Count * 2);
                for (int t = 0; t < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    int mab = mid.TryGetValue(EK(a, b), out int x1) ? x1 : -1;
                    int mbc = mid.TryGetValue(EK(b, c), out int x2) ? x2 : -1;
                    int mca = mid.TryGetValue(EK(c, a), out int x3) ? x3 : -1;
                    int n = (mab >= 0 ? 1 : 0) + (mbc >= 0 ? 1 : 0) + (mca >= 0 ? 1 : 0);
                    if (n == 0) { nt.Add(a); nt.Add(b); nt.Add(c); continue; }
                    if (n == 3) { Tri(nt, a, mab, mca); Tri(nt, mab, b, mbc); Tri(nt, mca, mbc, c); Tri(nt, mab, mbc, mca); continue; }
                    // auf Fall "Kante a-b geteilt" (n=1) bzw. "a-b und b-c geteilt" (n=2) drehen
                    while (!(n == 1 ? mab >= 0 : (mab >= 0 && mbc >= 0)))
                    { int ta = a; a = b; b = c; c = ta; int tm = mab; mab = mbc; mbc = mca; mca = tm; }
                    if (n == 1) { Tri(nt, a, mab, c); Tri(nt, mab, b, c); }
                    else { Tri(nt, mab, b, mbc); Tri(nt, a, mab, mbc); Tri(nt, a, mbc, c); }
                }
                tris = nt;
            }
            var res = new List<Vector2>(tris.Count);
            foreach (int i in tris) res.Add(verts[i]);
            return res;
        }

        private static void Tri(List<int> l, int a, int b, int c) { l.Add(a); l.Add(b); l.Add(c); }

        // Dreiecke als Oberseite nach oben ausgeben; gleiche Punkte teilen sich einen Vertex (je Zelle und Submesh)
        private static Vector2 cellOrigin;
        private static readonly Dictionary<RoadProfile.Parts, Dictionary<long, int>> shared = new Dictionary<RoadProfile.Parts, Dictionary<long, int>>();
        private static void EmitTris(List<Vector2> tris, int sub, System.Func<Vector2, Vector3> lift, System.Func<Vector3, RoadProfile.Parts> partsAt)
        {
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector2 c2 = (tris[t] + tris[t + 1] + tris[t + 2]) / 3f;
                var p = partsAt(new Vector3(c2.x + cellOrigin.x, 0f, c2.y + cellOrigin.y));
                if (!shared.TryGetValue(p, out var map)) { map = new Dictionary<long, int>(); shared[p] = map; }
                int ia = V(p, map, sub, tris[t], lift), ib = V(p, map, sub, tris[t + 1], lift), ic = V(p, map, sub, tris[t + 2], lift);
                if (ia == ib || ib == ic || ia == ic) continue;
                Vector3 n = Vector3.Cross(p.V[ib] - p.V[ia], p.V[ic] - p.V[ia]);
                if (n.y > 0f) { p.T[sub].Add(ia); p.T[sub].Add(ib); p.T[sub].Add(ic); }
                else { p.T[sub].Add(ia); p.T[sub].Add(ic); p.T[sub].Add(ib); }
            }
        }

        private static int V(RoadProfile.Parts p, Dictionary<long, int> map, int sub, Vector2 q, System.Func<Vector2, Vector3> lift)
        {
            long key = ((long)Mathf.RoundToInt((q.x + cellOrigin.x) * 100f) * 73856093L) ^ ((long)Mathf.RoundToInt((q.y + cellOrigin.y) * 100f) * 19349663L) ^ ((long)sub << 58);
            if (map.TryGetValue(key, out int i)) return i;
            var w = lift(q);
            i = p.V.Count; p.V.Add(w); p.N.Add(Vector3.up); p.UV.Add(new Vector2(w.x / 4f, w.z / 6f));
            map[key] = i;
            return i;
        }

        // Senkrechte Wand a-b von +top bis +bottom, sichtbar von LINKS der Richtung a->b (einseitig)
        private static void Wall(RoadProfile.Parts p, int sub, Vector3 a, Vector3 b, float top, float bottom)
        {
            int i = p.V.Count;
            p.V.Add(a + Vector3.up * top); p.V.Add(b + Vector3.up * top); p.V.Add(a + Vector3.up * bottom); p.V.Add(b + Vector3.up * bottom);
            Vector3 n = Vector3.Cross(b - a, Vector3.up).normalized;
            for (int k = 0; k < 4; k++) { p.N.Add(n); p.UV.Add(new Vector2(p.V[i + k].x / 4f, p.V[i + k].z / 6f)); }
            // Flächennormale = Cross(b-a, up) = links von a->b
            p.T[sub].Add(i); p.T[sub].Add(i + 2); p.T[sub].Add(i + 1); p.T[sub].Add(i + 1); p.T[sub].Add(i + 2); p.T[sub].Add(i + 3);
        }
    }
}
