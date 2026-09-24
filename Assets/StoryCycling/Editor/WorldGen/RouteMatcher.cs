using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Map-Matching: legt die GPX-Spur auf das OSM-Straßennetz (Hidden-Markov-Modell + Viterbi,
    // wie in Navigationssystemen). Ergebnis ist eine Fahrlinie EXAKT auf den OSM-Straßenachsen:
    //   - Hin- und Rückweg sind automatisch dieselbe Straße
    //   - Querstraßen hängen an denselben Knoten (keine Lücken an Einmündungen)
    //   - Straßenstil (Breite, Seitenstreifen, Fahrspur) je Abschnitt aus den OSM-Tags
    // Danach geglättet (Kurven statt Knoten-Ecken) und mit Höhen aus dem Gelände versehen.
    public static class RouteMatcher
    {
        public sealed class Result
        {
            public List<Vector3> Points = new List<Vector3>();
            public List<float> Half = new List<float>(), Inset = new List<float>(), Lane = new List<float>();
            public float MatchedShare;             // Anteil der GPX-Proben mit Straße in Reichweite
            public int WaysUsed;
        }

        // Straßenstil je OSM-Weg
        public struct Style { public float half, inset, lane; }

        private struct Edge { public int a, b, way; public float len; }
        private struct Cand { public int edge; public float t, dist; public Vector2 p; }

        private const float Step = 5f, Radius = 25f, Sigma = 7f, Beta = 4f, SearchLimit = 150f;
        // Abseits des OSM-Netzes (Wege, die nicht im Dump sind): folgt der GPX, kostet wie 15 m Abstand
        private const float OffRoadDist = 15f, SwitchPenalty = 6f;

        public static Result Match(List<Vector3> gpxLocal, OsmContext osm, System.Func<float, float, float> demY, float seaY, Regex wideShoulderRoads,
                                   System.Func<float, float, bool> inWideZone = null)
        {
            // ---------------- Graph
            var nodeIndex = new Dictionary<long, int>();
            var nodePos = new List<Vector2>();
            var edges = new List<Edge>();
            var adj = new List<List<int>>();
            for (int w = 0; w < osm.Streets.Count; w++)
            {
                var st = osm.Streets[w];
                if (st.nodes == null || st.nodes.Count != st.pts.Count) continue;
                int prev = -1;
                for (int k = 0; k < st.pts.Count; k++)
                {
                    long id = st.nodes[k] != 0 ? st.nodes[k] : -(w * 100000L + k + 1);
                    if (!nodeIndex.TryGetValue(id, out int ni)) { ni = nodePos.Count; nodeIndex[id] = ni; nodePos.Add(st.pts[k]); adj.Add(new List<int>()); }
                    if (prev >= 0 && prev != ni)
                    {
                        var e = new Edge { a = prev, b = ni, way = w, len = Vector2.Distance(nodePos[prev], nodePos[ni]) };
                        adj[prev].Add(edges.Count); adj[ni].Add(edges.Count); edges.Add(e);
                    }
                    prev = ni;
                }
            }
            var result = new Result();
            if (edges.Count == 0) return result;

            // Kanten-Hash
            const float cell = 25f;
            var hash = new Dictionary<long, List<int>>();
            for (int ei = 0; ei < edges.Count; ei++)
            {
                Vector2 a = nodePos[edges[ei].a], b = nodePos[edges[ei].b];
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / cell), z1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / cell);
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = ((long)x << 32) | (uint)z;
                    if (!hash.TryGetValue(key, out List<int> l)) { l = new List<int>(); hash[key] = l; }
                    l.Add(ei);
                }
            }

            // ---------------- Proben entlang der GPX-Spur (alle 5 m)
            var samples = new List<Vector2>();
            var sampleY = new List<float>();
            for (int i = 0; i < gpxLocal.Count; i++)
            {
                var p = new Vector2(gpxLocal[i].x, gpxLocal[i].z);
                if (samples.Count == 0) { samples.Add(p); sampleY.Add(gpxLocal[i].y); continue; }
                Vector2 last = samples[samples.Count - 1];
                float lastY = sampleY[sampleY.Count - 1];
                float d = Vector2.Distance(last, p);
                for (float s = Step; s <= d; s += Step) { samples.Add(last + (p - last) * (s / d)); sampleY.Add(Mathf.Lerp(lastY, gpxLocal[i].y, s / d)); }
            }

            var cands = new List<Cand>[samples.Count];
            int matched = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var list = new List<Cand>();
                Vector2 p = samples[i];
                var seen = new HashSet<int>();
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.y / cell);
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!hash.TryGetValue(((long)(cx + dx) << 32) | (uint)(cz + dz), out List<int> l)) continue;
                    foreach (int ei in l)
                    {
                        if (!seen.Add(ei)) continue;
                        Vector2 a = nodePos[edges[ei].a], b = nodePos[edges[ei].b];
                        Vector2 ab = b - a;
                        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                        Vector2 q = a + ab * t;
                        float dist = Vector2.Distance(p, q);
                        if (dist <= Radius) list.Add(new Cand { edge = ei, t = t, dist = dist, p = q });
                    }
                }
                list.Sort((u, v) => u.dist.CompareTo(v.dist));
                if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
                list.Add(new Cand { edge = -1, t = 0f, dist = OffRoadDist, p = p });   // Abseits-Zustand
                cands[i] = list;
            }

            // ---------------- Viterbi
            var dijkstraCache = new Dictionary<int, Dictionary<int, float>>();
            System.Func<int, Dictionary<int, float>> From = src =>
            {
                if (dijkstraCache.TryGetValue(src, out var got)) return got;
                var dist = new Dictionary<int, float> { [src] = 0f };
                var open = new List<int> { src };
                while (open.Count > 0)
                {
                    int bi = 0; for (int k = 1; k < open.Count; k++) if (dist[open[k]] < dist[open[bi]]) bi = k;
                    int n = open[bi]; open.RemoveAt(bi);
                    float dn = dist[n];
                    foreach (int ei in adj[n])
                    {
                        int m = edges[ei].a == n ? edges[ei].b : edges[ei].a;
                        float nd = dn + edges[ei].len;
                        if (nd > SearchLimit) continue;
                        if (!dist.TryGetValue(m, out float old) || nd < old) { if (!dist.ContainsKey(m)) open.Add(m); dist[m] = nd; }
                    }
                }
                dijkstraCache[src] = dist;
                return dist;
            };
            System.Func<Cand, Cand, float> NetDist = (c1, c2) =>
            {
                if (c1.edge < 0 || c2.edge < 0)
                    return Vector2.Distance(c1.p, c2.p) + (c1.edge != c2.edge ? SwitchPenalty : 0f);
                var e1 = edges[c1.edge]; var e2 = edges[c2.edge];
                if (c1.edge == c2.edge) return Mathf.Abs(c2.t - c1.t) * e1.len;
                float best = float.MaxValue;
                float[] d1 = { c1.t * e1.len, (1f - c1.t) * e1.len };
                float[] d2 = { c2.t * e2.len, (1f - c2.t) * e2.len };
                int[] n1 = { e1.a, e1.b }, n2 = { e2.a, e2.b };
                for (int x = 0; x < 2; x++)
                {
                    var map = From(n1[x]);
                    for (int y = 0; y < 2; y++)
                        if (map.TryGetValue(n2[y], out float dd)) best = Mathf.Min(best, d1[x] + dd + d2[y]);
                }
                return best;
            };

            var score = new float[samples.Count][];
            var back = new int[samples.Count][];
            int firstI = 0; while (firstI < samples.Count && cands[firstI].Count == 0) firstI++;
            if (firstI >= samples.Count) return result;
            for (int i = 0; i < samples.Count; i++) { score[i] = new float[cands[i].Count]; back[i] = new int[cands[i].Count]; }
            for (int k = 0; k < cands[firstI].Count; k++) score[firstI][k] = -Emission(cands[firstI][k].dist);
            int prevI = firstI;
            for (int i = firstI + 1; i < samples.Count; i++)
            {
                if (cands[i].Count == 0) continue;                       // Lücke im OSM-Netz: überspringen
                float straight = Vector2.Distance(samples[prevI], samples[i]);
                for (int k = 0; k < cands[i].Count; k++)
                {
                    float best = float.MinValue; int arg = 0;
                    for (int j = 0; j < cands[prevI].Count; j++)
                    {
                        float nd = NetDist(cands[prevI][j], cands[i][k]);
                        float trans = nd == float.MaxValue ? -60f : -Mathf.Abs(nd - straight) / Beta;
                        float v = score[prevI][j] + trans;
                        if (v > best) { best = v; arg = j; }
                    }
                    score[i][k] = best - Emission(cands[i][k].dist);
                    back[i][k] = arg;
                }
                // prevI-Verknüpfung merken: Rückverfolgung über 'prevOf'
                prevOf[i] = prevI;
                prevI = i;
            }

            // Rückverfolgung
            var chosen = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++) chosen[i] = -1;
            int lastI = prevI, bestK = 0;
            for (int k = 1; k < score[lastI].Length; k++) if (score[lastI][k] > score[lastI][bestK]) bestK = k;
            for (int i = lastI; i >= firstI;)
            {
                chosen[i] = bestK;
                if (i == firstI) break;
                int pi = prevOf[i];
                bestK = back[i][bestK];
                i = pi;
            }
            prevOf.Clear();
            for (int i = firstI; i < samples.Count; i++) if (chosen[i] >= 0 && cands[i][chosen[i]].edge >= 0) matched++;
            result.MatchedShare = matched / (float)Mathf.Max(1, samples.Count);

            // ---------------- Polylinie entlang der OSM-Achsen
            var line = new List<Vector2>();
            var lineWay = new List<int>();
            var lineY = new List<float>();
            Cand? prevCand = null;
            for (int i = firstI; i < samples.Count; i++)
            {
                if (chosen[i] < 0) continue;
                var c = cands[i][chosen[i]];
                if (prevCand.HasValue && prevCand.Value.edge != c.edge && prevCand.Value.edge >= 0 && c.edge >= 0)
                {
                    // Knoten zwischen den Kanten (kürzester Weg im Graph)
                    foreach (int n in PathNodes(prevCand.Value, c, edges, adj, nodePos))
                    { line.Add(nodePos[n]); lineWay.Add(edges[c.edge].way); lineY.Add(sampleY[i]); }
                }
                if (line.Count == 0 || Vector2.Distance(line[line.Count - 1], c.p) > .5f)
                { line.Add(c.p); lineWay.Add(c.edge >= 0 ? edges[c.edge].way : -1); lineY.Add(sampleY[i]); }
                prevCand = c;
            }
            int spurs = PruneSpurs(line, lineWay, lineY, samples);
            if (spurs > 0) Debug.Log($"Map-Matching: {spurs} Stichweg(e) ohne echte Wende in der GPX entfernt.");
            var usedWays = new HashSet<int>(lineWay); usedWays.Remove(-1);
            result.WaysUsed = usedWays.Count;

            // ---------------- Glätten: gleichmäßig 4 m, gleitender Mittelwert über ±12 m (rundet Knotenecken)
            var res = new List<Vector2>(); var resWay = new List<int>(); var resY = new List<float>();
            Resample(line, lineWay, lineY, 4f, res, resWay, resY);
            var smooth = new List<Vector2>(res.Count);
            for (int i = 0; i < res.Count; i++)
            {
                Vector2 sum = Vector2.zero; int c = 0;
                for (int k = Mathf.Max(0, i - 3); k <= Mathf.Min(res.Count - 1, i + 3); k++) { sum += res[k]; c++; }
                smooth.Add(sum / c);
            }

            // Höhen: GPX-Höhenprofil (glatt, straßenbezogen) statt DEM (an Klippen verrauscht), ±40 m geglättet,
            // Steigung auf 18 % begrenzt; Hin-/Rückweg auf derselben Straße bekommen dieselbe Höhe.
            var yArr = new float[smooth.Count];
            for (int i = 0; i < smooth.Count; i++)
            {
                float sum = 0f; int c = 0;
                for (int k = Mathf.Max(0, i - 10); k <= Mathf.Min(smooth.Count - 1, i + 10); k++) { sum += resY[k]; c++; }
                yArr[i] = sum / c;
            }
            const float maxGrade = .18f, seg = 4f;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 1; i < yArr.Length; i++) yArr[i] = Mathf.Clamp(yArr[i], yArr[i - 1] - maxGrade * seg, yArr[i - 1] + maxGrade * seg);
                for (int i = yArr.Length - 2; i >= 0; i--) yArr[i] = Mathf.Clamp(yArr[i], yArr[i + 1] - maxGrade * seg, yArr[i + 1] + maxGrade * seg);
            }
            var pts3 = new List<Vector3>(smooth.Count);
            for (int i = 0; i < smooth.Count; i++) pts3.Add(new Vector3(smooth[i].x, Mathf.Max(yArr[i], seaY + 1.5f), smooth[i].y));
            RoutePreprocessor.ReconcileOverlaps(pts3);          // gleiche Stelle -> gleiche Höhe (und Lage)
            result.Points.AddRange(pts3);

            // Stil je Punkt aus dem OSM-Weg, weich überblendet (±24 m)
            var styles = new Style[resWay.Count];
            for (int i = 0; i < resWay.Count; i++)
            {
                bool zone = inWideZone == null || inWideZone(smooth[i].x, smooth[i].y);
                styles[i] = StyleFor(resWay[i] >= 0 ? osm.Streets[resWay[i]] : null, zone ? wideShoulderRoads : null);
            }
            for (int i = 0; i < styles.Length; i++)
            {
                float h = 0f, s = 0f, l = 0f; int c = 0;
                for (int k = Mathf.Max(0, i - 6); k <= Mathf.Min(styles.Length - 1, i + 6); k++) { h += styles[k].half; s += styles[k].inset; l += styles[k].lane; c++; }
                result.Half.Add(h / c); result.Inset.Add(s / c); result.Lane.Add(l / c);
            }
            return result;
        }

        // Entfernt Stichwege (hinein und sofort zurück), die in der GPX keine echte Wende sind.
        private static int PruneSpurs(List<Vector2> line, List<int> way, List<float> ys, List<Vector2> gpx)
        {
            // echte Wenden der GPX (Richtungsumkehr > 150° innerhalb ±2 Proben)
            var turns = new List<Vector2>();
            for (int i = 2; i < gpx.Count - 2; i++)
            {
                Vector2 a = (gpx[i] - gpx[i - 2]).normalized, b = (gpx[i + 2] - gpx[i]).normalized;
                if (Vector2.Dot(a, b) < -.85f) turns.Add(gpx[i]);
            }
            int removed = 0;
            for (int i = 1; i < line.Count - 1; i++)
            {
                // Umkehr auch dann erkennen, wenn sie sich über zwei, drei Punkte verteilt
                bool tip = false;
                for (int w = 1; w <= 3 && !tip; w++)
                {
                    if (i - w < 0 || i + w >= line.Count) break;
                    Vector2 a = line[i] - line[i - w], b = line[i + w] - line[i];
                    // nur echte Richtungsumkehr über ≥ 3 m (kein Projektions-Zittern)
                    tip = a.sqrMagnitude > 9f && b.sqrMagnitude > 9f && Vector2.Dot(a.normalized, b.normalized) < -.85f;
                }
                if (!tip) continue;
                bool real = false;
                foreach (var t in turns) if ((t - line[i]).sqrMagnitude < 35f * 35f) { real = true; break; }
                if (real) continue;
                int k = 1;
                while (i - k - 1 >= 0 && i + k + 1 < line.Count && Vector2.Distance(line[i - k - 1], line[i + k + 1]) < 4f) k++;
                int from = i - k + 1, to = i + k - 1;            // Spitze und Hin-/Rückpunkte entfernen
                if (to < from) { from = i; to = i; }
                // nur Stichwege ab 6 m Länge und höchstens ~400 m (sonst ist es eine echte Streckenführung)
                float spurLen = from > 0 ? Vector2.Distance(line[from - 1], line[i]) : 0f;
                if (spurLen < 6f || to - from > 80) continue;
                int count = to - from + 1;
                line.RemoveRange(from, count); way.RemoveRange(from, count); ys.RemoveRange(from, count);
                removed++;
                i = Mathf.Max(0, from - 2);
            }
            return removed;
        }

        private static readonly Dictionary<int, int> prevOf = new Dictionary<int, int>();

        private static float Emission(float d) => d * d / (2f * Sigma * Sigma);

        public static Style StyleFor(OsmContext.Street st, Regex wide)
        {
            string label = st == null ? "" : st.name + " " + st.refTag;
            if (st != null && wide != null && wide.IsMatch(label))
                return new Style { half = 5.5f, inset = 2f, lane = -4.4f };           // Kap-Küstenstraße mit Seitenstreifen
            float lanesHalf = st != null && st.lanes >= 3 ? 1.5f * 3.3f : 3.3f;
            float half = st != null && st.width > 4f ? Mathf.Clamp(st.width * .5f, 3f, 7f) : lanesHalf + .3f;
            if (st != null && (st.highway == "residential" || st.highway == "living_street" || st.highway == "unclassified"))
                half = Mathf.Min(half, 3.1f);
            return new Style { half = half, inset = .25f, lane = -(half - 1.2f) };   // normale Straße, Randlinie am Rand
        }

        private static IEnumerable<int> PathNodes(Cand from, Cand to, List<Edge> edges, List<List<int>> adj, List<Vector2> nodePos)
        {
            var e1 = edges[from.edge]; var e2 = edges[to.edge];
            int[] starts = { e1.a, e1.b }, ends = { e2.a, e2.b };
            float[] ds = { from.t * e1.len, (1f - from.t) * e1.len }, de = { to.t * e2.len, (1f - to.t) * e2.len };
            List<int> best = null; float bestLen = float.MaxValue;
            for (int x = 0; x < 2; x++)
            {
                // Dijkstra mit Vorgängern
                var dist = new Dictionary<int, float> { [starts[x]] = ds[x] };
                var pred = new Dictionary<int, int>();
                var open = new List<int> { starts[x] };
                while (open.Count > 0)
                {
                    int bi = 0; for (int k = 1; k < open.Count; k++) if (dist[open[k]] < dist[open[bi]]) bi = k;
                    int n = open[bi]; open.RemoveAt(bi);
                    foreach (int ei in adj[n])
                    {
                        int m = edges[ei].a == n ? edges[ei].b : edges[ei].a;
                        float nd = dist[n] + edges[ei].len;
                        if (nd > SearchLimit + ds[x]) continue;
                        if (!dist.TryGetValue(m, out float old) || nd < old) { if (!dist.ContainsKey(m)) open.Add(m); dist[m] = nd; pred[m] = n; }
                    }
                }
                for (int y = 0; y < 2; y++)
                {
                    if (!dist.TryGetValue(ends[y], out float d)) continue;
                    float total = d + de[y];
                    if (total >= bestLen) continue;
                    var path = new List<int>();
                    for (int n = ends[y]; ; n = pred[n]) { path.Add(n); if (n == starts[x] || !pred.ContainsKey(n)) break; }
                    path.Reverse();
                    best = path; bestLen = total;
                }
            }
            return best ?? new List<int>();
        }

        private static void Resample(List<Vector2> line, List<int> way, List<float> ys, float step, List<Vector2> outP, List<int> outW, List<float> outY)
        {
            if (line.Count == 0) return;
            outP.Add(line[0]); outW.Add(way[0]); outY.Add(ys[0]);
            float carry = 0f;
            for (int i = 1; i < line.Count; i++)
            {
                Vector2 a = line[i - 1], b = line[i];
                float len = Vector2.Distance(a, b);
                if (len < 1e-4f) continue;
                float d = step - carry;
                while (d <= len) { outP.Add(a + (b - a) * (d / len)); outW.Add(way[i]); outY.Add(Mathf.Lerp(ys[i - 1], ys[i], d / len)); d += step; }
                carry = len - (d - step);
            }
        }
    }
}
