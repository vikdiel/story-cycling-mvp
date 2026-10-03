using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Geometrische Prüfungen der GEBAUTEN Straßenoberfläche, unabhängig vom Erzeugungscode: sie messen nur, was in den
    // Dreiecken/Umrissen (RoadSurface.Result) tatsächlich liegt, gegen das Straßennetz (RoadNet) als Soll.
    // Jeder Verstoß wird mit Weltkoordinaten (x = Ost, z = Nord, Ursprung = erster GPX-Punkt) gemeldet.
    //   1  Schenkel-Anschluss   jeder Schenkel jeder Kreuzung liegt auf Asphalt bis in den Knoten; keine fehlenden/gekappten Ketten
    //   2  Umrisstreue          Fahrbahnrand = Sollrand ± 0,3 m (Proben innen/außen + jeder Umrisspunkt erklärbar)
    //   3  Bandqualität         Breite ± 0,25 m, richtiges Material, keine Flecken, keine Splitter, keine spitzen Umrisswinkel < 30°
    //   4  Ortslage-Übergänge   Gehweg <-> Randstreifen nur als gerader Schnitt quer zur Straße
    //   5  Markierungen         auf eigener Fahrbahn, nicht in Kreuzungen, nicht auf fremden Fahrbahnen, Randlinie nie über Einmündungen
    //   6  Löcher/Splitter      keine kleinen Asphaltlöcher, keine Asphaltsplitter, keine Zacken (< 1 m) und keine Taschen (Kerben < 1,8 m
    //                           Öffnung) im Asphaltumriss, in denen Band als Zacke in die Fahrbahn ragt
    //   7  Sackgassen           kein Sackgassenende liegt < 8 m (Asphalt zu Asphalt) vor der Fahrbahn einer anderen, nicht angeschlossenen Straße
    //   8  Durchgänge           gegenüberliegende Zufahrten (<= 25° von gerade) haben am Beschnitt dieselben Fahrbahnränder (Stufe <= 0,3 m);
    //                           die Fahrbahnbreite ändert sich nirgends sprunghaft (<= 0,25 m je Meter)
    //   9  Klassenfolgen        Gehweg/Randstreifen wechseln am Asphaltrand nie in Stücken < 18 m (Stummel zwischen zwei Nachbarklassen);
    //                           ein Übergang steht auf beiden Straßenseiten an derselben Stelle (± 3,5 m)
    //  10  Ecken/Nasen         der Asphaltrand hat keine Kerbe/keinen Sprung (Linksknick >= 25° mit Rechtsknick >= 25° binnen 4 m) und keinen
    //                           Knick über 38° (Kreuzungsecken sind Bögen, Mittelstreifen- und Inselenden gerundet)
    //  11  Mittelstreifen      jede Doppelfahrbahn-Zufahrt hat 3 m hinter dem Beschnitt noch Mittelstreifen (kein Asphalt zwischen den Fahrbahnen)
    //  12  Kreisverkehre       jeder gebaute Ring ist geschlossen; eine Zufahrt am Ring ist mindestens 14 m lang oder echt angeschlossen
    public static class RoadChecks
    {
        public sealed class Finding { public string Check; public float X, Z; public string Msg; }

        public const float EdgeTol = .3f, SmoothTol = 4.5f, WidthTol = .25f, MinAngleDeg = 30f, CutAngleTolDeg = 15f, EdgeCutTolDeg = 20f, HoleMaxArea = 25f, SplinterMaxArea = 3f;
        public const int MaxFindingsPerCheck = 400;
        public const float UrbanW = 2f, RuralW = 1.6f, MedianKerbW = .35f;

        private sealed class Env
        {
            public RoadNet Net; public RoadSurface.Result S; public WorldCheck.Report R;
            public StripIndex X;
            public readonly List<Vector2> JC = new List<Vector2>(); public readonly List<float> JR = new List<float>();
            public RoadNet.PointHash JH; public float JRmax;
            public readonly List<int> Tmp = new List<int>();
            public readonly Dictionary<string, int> Sites = new Dictionary<string, int>(), Points = new Dictionary<string, int>();
            public readonly HashSet<string> Seen = new HashSet<string>();
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        }

        public static void Run(RoadNet net, RoadSurface.Result s, WorldCheck.Report r)
        {
            var E = new Env { Net = net, S = s, R = r, X = new StripIndex(net) };
            E.X.AddRims(s.Rims);
            var centers = new List<Vector3>();
            foreach (var j in net.Junctions)
            {
                float rr = j.Radius;
                E.JC.Add(j.Center); E.JR.Add(rr + 5f); E.JRmax = Mathf.Max(E.JRmax, rr + 5f);
                centers.Add(new Vector3(j.Center.x, 0f, j.Center.y));
            }
            E.JH = centers.Count > 0 ? new RoadNet.PointHash(centers, 20f) : null;

            LegConnectivity(E);
            OutlineFidelity(E);
            BandQuality(E);
            Transitions(E);
            ClassRuns(E);
            OutlineSteps(E);
            Markings(E);
            Holes(E);
            DeadEnds(E);
            ThroughLegs(E);
            WidthSteps(E);
            Corners(E);
            MedianReach(E);
            Rings(E);

            foreach (var kv in E.Sites)
            {
                if (kv.Value == 0) continue;
                var ex = new List<string>();
                foreach (var f in r.Findings) if (f.Check == kv.Key && ex.Count < 4) ex.Add($"({f.X:0}, {f.Z:0})");
                r.Warnings.Add($"{E.Names[kv.Key]}: {kv.Value} Stellen ({E.Points[kv.Key]} Messpunkte), z. B. {string.Join(" ", ex)}");
            }
        }


        // ------------------------------------------------------------------ 10) Ecken und Nasen
        // Richtungen aus Sehnen von mindestens CornerChord (15 cm) Randlänge, damit Mikrokanten (4-cm-Versätze) nicht als Knick zählen; Bögen
        // aus Schritten <= 15° (Rundungen der Glättung) bleiben unter der Schwelle.
        //  Knick    Rechtsknick (Nichtasphalt-Ecke) zwischen 38° und 135° zwischen zwei Kanten von mindestens 1 m (Rundungen bestehen aus kürzeren
        //           Kanten; schärfere Spitzen sind zusammenlaufende Fahrbahnen)
        //  Kerbe    Linksknick >= 25° und Rechtsknick >= 25° binnen 4 m Randlänge (Zahn, Sprung, Höcker), wenn der Rand dabei um 0,3-1,8 m
        //           von der Verbindung der Punkte 1 m vor und hinter der Stelle abweicht (größere Stufen sind echte Breitenwechsel); die rechteckigen
        //           Ecken am Ende einer Sackgasse (Abstand <= halbe Breite + 1,5 m vom Ende) zählen nicht
        public const float CornerKink = 38f, CornerKinkMax = 135f, CornerChord = .15f, CornerEdge = 1f, NotchTurn = 25f, NotchReach = 4f, NotchDev = .3f, NotchMaxDev = 1.8f, CornerZoneExtra = 3f;      // Zone wie beim Glätten: Radius + 8 m (E.JR = Radius + 5)
        private static void Corners(Env E)
        {
            Declare(E, "corner", "Ecke im Asphaltrand nicht glatt (Kerbe/Sprung/Höcker >= 0,3 m, Knick > 38°, eckiges Mittelstreifen-/Inselende)");
            // Sackgassenenden: der rechteckige Abschluss des Streifens (Ecken im Abstand der halben Breite vom Ende) ist keine Kerbe
            var stubEnds = new List<Vector2>(); var stubR = new List<float>();
            for (int ni = 0; ni < E.Net.Nodes.Count; ni++)
            {
                var nd = E.Net.Nodes[ni]; if (nd.Segs.Count != 1) continue;
                var sg = E.Net.Segs[nd.Segs[0]]; if (sg.S.Count < 2) continue;
                var se = sg.S[sg.A == ni ? 0 : sg.S.Count - 1]; stubEnds.Add(XZ(se.pos)); stubR.Add(se.half + 1.5f);
            }
            foreach (var loop in E.S.AsphaltLoops)
            {
                int n = loop.Length; if (n < 4) continue;
                float a2 = 0f; for (int i = 0, j = n - 1; i < n; j = i++) a2 += loop[j].x * loop[i].y - loop[i].x * loop[j].y;
                bool hole = a2 < 0f;                                       // Löcher (Mittelstreifen, Inseln) überall, Außenränder in Kreuzungszonen
                var art = new bool[n]; for (int i = 0; i < n; i++) art[i] = Artificial(loop[i], loop[(i + 1) % n]);
                var t = new float[n]; var eb = new float[n]; var ef = new float[n];        // Knick sowie Länge der beiden anliegenden Kanten (Mikrokanten zusammengefasst)
                for (int i = 0; i < n; i++)
                {
                    int p = (i + n - 1) % n, q = (i + 1) % n;
                    for (int g = 0; g < n && (loop[i] - loop[p]).magnitude < CornerChord && p != q; g++) p = (p + n - 1) % n;
                    for (int g = 0; g < n && (loop[q] - loop[i]).magnitude < CornerChord && p != q; g++) q = (q + 1) % n;
                    Vector2 d0 = loop[i] - loop[p], d1 = loop[q] - loop[i];
                    eb[i] = d0.magnitude; ef[i] = d1.magnitude;
                    t[i] = d0.sqrMagnitude < 1e-6f || d1.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1)) * Mathf.Rad2Deg;
                }
                for (int i = 0; i < n; i++)
                {
                    if (art[i] || art[(i + n - 1) % n] || (!hole && !InZone(E, loop[i], CornerZoneExtra))) continue;
                    if (t[i] < -CornerKink && t[i] > -CornerKinkMax && eb[i] >= CornerEdge && ef[i] >= CornerEdge) { Fail(E, "corner", loop[i], $"Knick {-t[i]:0}° im Asphaltrand (Ecke nicht gerundet)"); continue; }
                    if (t[i] < NotchTurn) continue;
                    bool atStub = false; for (int k = 0; k < stubEnds.Count && !atStub; k++) atStub = (stubEnds[k] - loop[i]).magnitude <= stubR[k];
                    if (atStub) continue;
                    // Gegenstück (Rechtsknick >= 25°) binnen NotchReach vor/hinter dem Linksknick; Abweichung von der Verbindung der Punkte 1 m davor/dahinter
                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        float acc = 0f; bool found = false; int j = i;
                        for (int g = 0; g < n; g++)
                        {
                            int nx = (j + dir + n) % n;
                            acc += (loop[nx] - loop[j]).magnitude;
                            if (acc > NotchReach || art[dir > 0 ? j : nx]) break;
                            if (t[nx] <= -NotchTurn) { found = true; j = nx; break; }
                            j = nx;
                        }
                        if (!found) continue;
                        Vector2 a = PointAlong(loop, dir > 0 ? i : j, -1, 1f), b = PointAlong(loop, dir > 0 ? j : i, 1, 1f);
                        Vector2 ab = b - a; float abl = ab.magnitude; if (abl < .3f) continue;
                        float dev = 0f;
                        for (int k = dir > 0 ? i : j, g = 0; g < n; g++)
                        {
                            dev = Mathf.Max(dev, Mathf.Abs(ab.x * (loop[k].y - a.y) - ab.y * (loop[k].x - a.x)) / abl);
                            if (k == (dir > 0 ? j : i)) break; k = (k + 1) % n;
                        }
                        if (dev >= NotchDev && dev <= NotchMaxDev) { Fail(E, "corner", loop[i], $"Kerbe/Sprung im Asphaltrand (Linksknick {t[i]:0}°, Rechtsknick {-t[j]:0}° nach {acc:0.0} m, Abweichung {dev:0.00} m)"); break; }
                    }
                }
            }
        }

        // Punkt im Randabstand dist vor (dir -1) bzw. hinter (dir +1) dem Punkt i
        private static Vector2 PointAlong(Vector2[] loop, int i, int dir, float dist)
        {
            int n = loop.Length, k = i; float acc = 0f;
            for (int g = 0; g < n; g++)
            {
                int nx = (k + dir + n) % n; float el = (loop[nx] - loop[k]).magnitude;
                if (acc + el >= dist) return Vector2.Lerp(loop[k], loop[nx], (dist - acc) / Mathf.Max(el, 1e-6f));
                acc += el; k = nx;
            }
            return loop[i];
        }

        // ------------------------------------------------------------------ 11) Mittelstreifen bis an die Kreuzung
        // Jede Zufahrt einer Doppelfahrbahn mit Lücke >= 1 m zum Partner: 3 m hinter dem Beschnitt (bei versetzten Knoten der spätere der beiden
        // Fahrbahnen) darf in der Mitte zwischen den Fahrbahnen kein Asphalt liegen. Kennzahl: Abstand der Nase vom Beschnitt.
        private static void MedianReach(Env E)
        {
            Declare(E, "medianReach", "Mittelstreifen endet vor der Kreuzung (Asphalt zwischen den Fahrbahnen 3 m hinter dem Beschnitt)");
            var net = E.Net; int legs = 0, ok = 0; float sum = 0f, max = 0f; int measured = 0;
            foreach (var jn in net.Junctions)
                foreach (var e in jn.Ends)
                {
                    if (!net.MedianProbe(e, MedianProbeStation, out Vector2 mid, out float gap) || gap < 1f) continue;
                    legs++;
                    if (E.S.Asphalt.Inside(mid.x, mid.y)) Fail(E, "medianReach", mid, $"Kreuzung {jn.Center.x:0}/{jn.Center.y:0}, Abschnitt {e.Seg}: Lücke {gap:0.0} m, aber bei Beschnitt + {MedianProbeStation:0} m Asphalt in der Mitte");
                    else ok++;
                    // Abstand der Mittelstreifen-Nase vom Beschnitt: erste Station (Raster 0,5 m), an der die Mitte zwischen den Fahrbahnen kein Asphalt ist
                    for (float st = 0f; st <= 12f; st += .5f)
                        if (net.MedianProbe(e, st, out Vector2 m2, out float g2) && g2 >= 1f && !E.S.Asphalt.Inside(m2.x, m2.y)) { sum += st; max = Mathf.Max(max, st); measured++; break; }
                }
            E.R.Metrics["Mittelstreifen"] = $"{ok}/{legs} Doppelfahrbahn-Zufahrten mit Mittelstreifen bis {MedianProbeStation:0} m an den Beschnitt; Nase im Mittel {(measured > 0 ? sum / measured : 0f):0.0} m, höchstens {max:0.0} m hinter dem Beschnitt (Beschnitt = der spätere der beiden Fahrbahnen)";
        }
        public const float MedianProbeStation = 3f;

        // ------------------------------------------------------------------ 12) Kreisverkehre: Ring geschlossen, Zufahrten nicht bündig
        public const float RingArmMin = 14f;
        private static void Rings(Env E)
        {
            Declare(E, "ringClosed", "Kreisverkehr: Ring nicht geschlossen (Ringstück endet offen) oder Zufahrt < 14 m bündig am Ring abgeschnitten");
            var net = E.Net; var cnt = new Dictionary<int, int>(); int open = 0, rings = 0;
            foreach (var sg in net.Segs)
            {
                if (!sg.Roundabout) continue;
                cnt[sg.A] = (cnt.TryGetValue(sg.A, out int a) ? a : 0) + 1; cnt[sg.B] = (cnt.TryGetValue(sg.B, out int b) ? b : 0) + 1;
            }
            foreach (var kv in cnt)
                if (kv.Value == 1) { open++; Fail(E, "ringClosed", net.Nodes[kv.Key].P, "Ringstück endet offen (Kreisverkehr nicht geschlossen)"); }
            int arms = 0, flush = 0;
            foreach (var sg in net.Segs)
            {
                if (sg.Roundabout || sg.Fallback || sg.S.Count < 2) continue;
                for (int end = 0; end < 2; end++)
                {
                    int ring = end == 0 ? sg.A : sg.B, far = end == 0 ? sg.B : sg.A;
                    if (!cnt.ContainsKey(ring)) continue;
                    arms++;
                    if (sg.Length < RingArmMin && net.Nodes[far].Segs.Count == 1 && net.Nodes[far].Id < 0)
                    { flush++; Fail(E, "ringClosed", net.Nodes[ring].P, $"Zufahrt {sg.Length:0.0} m lang, dann Schnittende: bündig am Ring abgeschnitten"); }
                }
            }
            // Ringe zählen: Zusammenhangskomponenten der Ringstücke
            var seen = new HashSet<int>();
            foreach (var kv in cnt)
            {
                if (!seen.Add(kv.Key)) continue;
                rings++; var st = new Stack<int>(); st.Push(kv.Key);
                while (st.Count > 0) { int x = st.Pop(); foreach (var sg in net.Segs) if (sg.Roundabout && (sg.A == x || sg.B == x)) { int y = sg.A == x ? sg.B : sg.A; if (seen.Add(y)) st.Push(y); } }
            }
            E.R.Metrics["Kreisringe"] = $"{rings} Kreisverkehre, {open} offene Ringenden, {arms} Zufahrten am Ring, davon {flush} bündig abgeschnitten (< {RingArmMin:0} m)";
        }

        // ------------------------------------------------------------------ Hilfen
        private static void Declare(Env E, string check, string name)
        {
            E.Sites[check] = 0; E.Points[check] = 0; E.Names[check] = name;
        }

        private static void Fail(Env E, string check, Vector2 at, string msg)
        {
            E.Points[check]++;
            string cell = check + ":" + Mathf.FloorToInt(at.x / 4f) + "/" + Mathf.FloorToInt(at.y / 4f);
            if (!E.Seen.Add(cell)) return;
            E.Sites[check]++;
            if (E.Sites[check] <= MaxFindingsPerCheck) E.R.Findings.Add(new Finding { Check = check, X = at.x, Z = at.y, Msg = msg });
        }

        private static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

        private static bool InZone(Env E, Vector2 q, float extra)
        {
            if (E.JH == null) return false;
            foreach (int i in E.JH.WithinIdx(q, E.JRmax + extra))
                if ((E.JC[i] - q).magnitude <= E.JR[i] + extra) return true;
            return false;
        }

        // Kante auf dem 200-m-Kachelraster = künstlicher Schnitt, kein Straßenrand
        private static bool OnTileGrid(Vector2 p)
        {
            const float e = 1.5e-2f;
            return Mathf.Abs(p.x - Mathf.Round(p.x / 200f) * 200f) < e || Mathf.Abs(p.y - Mathf.Round(p.y / 200f) * 200f) < e;
        }

        private static bool Artificial(Vector2 a, Vector2 b)
        {
            const float e = 4e-3f;
            float ax = Mathf.Round(a.x / 200f) * 200f, az = Mathf.Round(a.y / 200f) * 200f;
            if (Mathf.Abs(a.x - ax) < e && Mathf.Abs(b.x - ax) < e) return true;
            if (Mathf.Abs(a.y - az) < e && Mathf.Abs(b.y - az) < e) return true;
            return false;
        }

        private static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float l2 = ab.sqrMagnitude;
            float t = l2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return (a + ab * t - p).magnitude;
        }

        // ownK >= 0: auch die Streifen des eigenen Abschnitts erklären einen Punkt (Innenseite enger Kurven: der Streifen läuft auf sich
        // selbst zurück); nur das direkt zugehörige Viereck (Probe ownK) zählt nicht mit
        private static bool InStrip(Env E, Vector2 q, float margin, int except, out int seg, out int k0, int ownK = -1) => E.X.Hit(q, margin, except, out seg, out k0, ownK, 0);

        private sealed class EdgeIndex
        {
            private readonly List<Vector2> a = new List<Vector2>(), b = new List<Vector2>();
            private readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
            private const float C = 8f;
            private static long K(int x, int z) => ((long)x << 32) | (uint)z;
            public void Add(Vector2 p, Vector2 q)
            {
                int id = a.Count; a.Add(p); b.Add(q);
                int x0 = Mathf.FloorToInt(Mathf.Min(p.x, q.x) / C), x1 = Mathf.FloorToInt(Mathf.Max(p.x, q.x) / C);
                int z0 = Mathf.FloorToInt(Mathf.Min(p.y, q.y) / C), z1 = Mathf.FloorToInt(Mathf.Max(p.y, q.y) / C);
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                { if (!map.TryGetValue(K(x, z), out var l)) { l = new List<int>(); map[K(x, z)] = l; } l.Add(id); }
            }
            // Richtungen (a->b) aller Kanten, die in p (Toleranz tol) beginnen oder enden
            public void Incident(Vector2 p, float tol, List<Vector2> dirs)
            {
                int cx = Mathf.FloorToInt(p.x / C), cz = Mathf.FloorToInt(p.y / C);
                for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                    if (map.TryGetValue(K(cx + dx, cz + dz), out var l))
                        foreach (int i in l)
                            if ((a[i] - p).magnitude < tol || (b[i] - p).magnitude < tol)
                            { Vector2 d = (b[i] - a[i]); if (d.magnitude > 1e-4f) dirs.Add(d.normalized); }
            }
            // Richtung der nächsten Kante (bis maxR gesucht); false, wenn keine
            public bool NearestDir(Vector2 p, float maxR, out Vector2 dir)
            {
                float best = float.MaxValue; dir = Vector2.zero;
                int r = Mathf.CeilToInt(maxR / C), cx = Mathf.FloorToInt(p.x / C), cz = Mathf.FloorToInt(p.y / C);
                for (int dx = -r; dx <= r; dx++) for (int dz = -r; dz <= r; dz++)
                    if (map.TryGetValue(K(cx + dx, cz + dz), out var l))
                        foreach (int i in l)
                        { float d = DistSeg(p, a[i], b[i]); if (d < best && (b[i] - a[i]).magnitude > 1e-4f) { best = d; dir = (b[i] - a[i]).normalized; } }
                return best <= maxR;
            }
            // kleinster Abstand zu einer Kante (bis maxR gesucht)
            public float Nearest(Vector2 p, float maxR)
            {
                float best = float.MaxValue;
                int r = Mathf.CeilToInt(maxR / C), cx = Mathf.FloorToInt(p.x / C), cz = Mathf.FloorToInt(p.y / C);
                for (int dx = -r; dx <= r; dx++) for (int dz = -r; dz <= r; dz++)
                    if (map.TryGetValue(K(cx + dx, cz + dz), out var l))
                        foreach (int i in l) best = Mathf.Min(best, DistSeg(p, a[i], b[i]));
                return best;
            }
        }

        private static float Area2(Vector2[] c)
        {
            float a = 0f; for (int i = 0, j = c.Length - 1; i < c.Length; j = i++) a += c[j].x * c[i].y - c[i].x * c[j].y;
            return a;
        }

        // ------------------------------------------------------------------ 8) Durchgänge und Breitenverlauf
        public const float ThroughStepTol = .3f, WidthSlopeTol = .25f;
        private static void ThroughLegs(Env E)
        {
            Declare(E, "through", "durchgehende Straße: Randstufe > 0,3 m zwischen gegenüberliegenden Zufahrten einer Kreuzung");
            var net = E.Net; int groups = 0, fixed0 = 0;
            for (int ji = 0; ji < net.Junctions.Count; ji++)
                foreach (var g in net.ThroughGroups(net.Junctions[ji]))
                {
                    if (!g.Fixable) { fixed0++; continue; }                  // beide Zufahrten zu kurz zum Angleichen (dazwischen liegt die nächste Kreuzung)
                    groups++;
                    float step = Mathf.Max(Mathf.Abs(g.XLo - g.YLo), Mathf.Abs(g.XHi - g.YHi));
                    if (step > ThroughStepTol)
                        Fail(E, "through", g.Org, $"J{ji}: Randstufe {step:0.00} m (Abschnitt {g.X.Seg} [{g.XLo:0.0}..{g.XHi:0.0}] L{net.Segs[g.X.Seg].Length:0} T{g.X.Trim:0.0} gegen {(g.Pair ? "Abschnitt " + g.Ys[0].Seg + " L" + net.Segs[g.Ys[0].Seg].Length.ToString("0") + " T" + g.Ys[0].Trim.ToString("0.0") : "Gabelung")} [{g.YLo:0.0}..{g.YHi:0.0}])");
                }
            E.R.Metrics["Durchgänge"] = $"{groups} durchgehende Straßen an Kreuzungen geprüft (+{fixed0}, deren Zufahrten beide zu kurz zum Angleichen sind)";
        }

        private static void WidthSteps(Env E)
        {
            Declare(E, "widthStep", "Fahrbahnbreite ändert sich sprunghaft (> 0,25 m je Meter)");
            var net = E.Net;
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sg = net.Segs[si]; if (sg.Fallback || sg.Internal) continue;         // Verbindungsstücke: gehören zur Kreuzung (Aufweitung/Verengung zwischen den Zufahrten, bewusst linear)
                for (int k = 0, k2 = 0; k + 1 < sg.S.Count; k++)
                {
                    if (k2 <= k) k2 = k + 1;
                    while (k2 + 1 < sg.S.Count && sg.S[k2].distance - sg.S[k].distance < 2f) k2++;          // Steigung über >= 2 m messen
                    float ds = sg.S[k2].distance - sg.S[k].distance; if (ds < 1.5f) continue;
                    float slope = Mathf.Abs(sg.S[k2].half - sg.S[k].half) / ds;
                    if (slope > WidthSlopeTol) Fail(E, "widthStep", XZ(sg.S[k].pos), $"Abschnitt {si}: Halbbreite {sg.S[k].half:0.00} -> {sg.S[k2].half:0.00} m auf {ds:0.0} m");
                }
            }
        }

        // ------------------------------------------------------------------ 7) Sackgassen vor anderen Straßen
        // Ein echtes Sackgassenende (OSM-Endknoten, im Korridor) darf nicht knapp vor der Fahrbahn einer anderen Straße enden, auf die es
        // zuläuft, ohne angeschlossen zu sein (Kartierlücke). Gemessen wird am gebauten Netz: vom Mittelpunkt der Stirnkante in einem Fächer
        // von ±70° um die Fahrtrichtung nach außen, 8 m weit, ob dort der Asphaltstreifen eines anderen Abschnitts beginnt. Angeschlossen = der
        // eigene Abschnitt sowie bei kurzen Stummeln (< 16 m) die Straßen der Kreuzung am anderen Ende; parallel
        // vorbeilaufende Nachbarstraßen (Zufahrten) liegen außerhalb des Fächers und zählen nicht. Ebenso gemeldet: Stummel < StubMax hinter einer
        // Einmündung (Asphaltblock ohne Straßencharakter).
        public const float DeadEndGap = 8f, DeadEndCone = 70f;
        private static void DeadEnds(Env E)
        {
            Declare(E, "deadEnd", "Sackgasse endet < 8 m vor einer anderen, nicht angeschlossenen Fahrbahn bzw. Stummel < 10 m hinter einer Einmündung");
            var net = E.Net; int ends = 0;
            for (int ni = 0; ni < net.Nodes.Count; ni++)
            {
                var nd = net.Nodes[ni];
                if (nd.Segs.Count != 1 || nd.OsmDegree != 1 || nd.RouteDist > RoadNet.ScopeDistance - 20f) continue;
                int si = nd.Segs[0]; var sg = net.Segs[si]; if (sg.Fallback || sg.S.Count < 2) continue;
                bool atA = sg.A == ni; int other = atA ? sg.B : sg.A;
                var skip = new HashSet<int> { si };
                if (sg.Length < 2f * DeadEndGap)       // kurzer Stummel: die Straßen der Kreuzung am anderen Ende gelten als angeschlossen
                {
                    foreach (int s2 in net.Nodes[other].Segs) skip.Add(s2);
                    foreach (var j in net.Junctions) if (j.NodesIn.Contains(other)) foreach (int nj in j.NodesIn) foreach (int s2 in net.Nodes[nj].Segs) skip.Add(s2);
                }
                var e = sg.S[atA ? 0 : sg.S.Count - 1]; Vector2 c = XZ(e.pos), t = XZ(e.tangent).normalized; if (atA) t = -t; ends++;
                if (sg.Length < RoadNet.StubMax && net.Nodes[other].OsmDegree >= 3)
                { Fail(E, "deadEnd", c, $"Sackgassen-Stummel {sg.Length:0.0} m hinter Einmündung (Knoten {nd.Id}, Abschnitt {si})"); continue; }
                bool hit = false; int hs = -1;
                for (float ang = -DeadEndCone; ang <= DeadEndCone && !hit; ang += 10f)
                {
                    float ca = Mathf.Cos(ang * Mathf.Deg2Rad), sa = Mathf.Sin(ang * Mathf.Deg2Rad);
                    Vector2 d = new Vector2(t.x * ca - t.y * sa, t.x * sa + t.y * ca);
                    for (float s = 0f; s <= DeadEndGap && !hit; s += .5f) hit = E.X.Hit(c + d * s, 0f, -1, out hs, out _, -1, 0, skip);
                }
                if (hit) Fail(E, "deadEnd", c, $"Sackgasse (Knoten {nd.Id}, Abschnitt {si}) endet < {DeadEndGap:0} m vor Abschnitt {hs}, ohne angeschlossen zu sein");
            }
            E.R.Metrics["Sackgassen"] = $"{ends} Sackgassenenden im Korridor geprüft";
        }

        // ------------------------------------------------------------------ 1) Schenkel-Anschluss
        private static void LegConnectivity(Env E)
        {
            Declare(E, "leg", "Schenkel nicht auf Asphalt bis in den Knoten");
            Declare(E, "node", "Kreuzungsknoten nicht auf Asphalt");
            Declare(E, "degree", "Kreuzung mit fehlendem Schenkel (OSM-Grad > Netz-Grad)");
            Declare(E, "cut", "Straße mitten im Korridor gekappt (Sackgassenende ohne OSM-Ende)");
            Declare(E, "dropped", "verworfene Kette an Netzknoten");
            var net = E.Net; var asp = E.S.Asphalt; int legs = 0, ok = 0;
            for (int ji = 0; ji < net.Junctions.Count; ji++)
            {
                var j = net.Junctions[ji];
                foreach (int ni in j.NodesIn)
                {
                    var nd = net.Nodes[ni];
                    if (!asp.Inside(nd.P.x, nd.P.y)) Fail(E, "node", nd.P, $"J{ji}: Knoten nicht asphaltiert");
                    foreach (int si in nd.Segs)
                    {
                        var sg = net.Segs[si]; if (sg.S.Count < 2) continue;
                        for (int side = 0; side < 2; side++)
                        {
                            bool atA = side == 0;
                            if ((atA ? sg.A : sg.B) != ni) continue;
                            legs++;
                            float reach = Mathf.Min(sg.Length, (atA ? sg.TrimA : sg.TrimB) + 4f);
                            bool good = true;
                            for (float d = 0f; d <= reach + .01f && good; d += 1f)
                            {
                                var q = RoadNet.At(sg, atA ? d : sg.Length - d).pos;
                                if (!asp.Inside(q.x, q.z)) { good = false; Fail(E, "leg", XZ(q), $"J{ji} Abschnitt {si}: Mittellinie {d:0} m vom Knoten nicht asphaltiert"); }
                            }
                            if (good) ok++;
                        }
                    }
                }
            }
            for (int ni = 0; ni < net.Nodes.Count; ni++)
            {
                var nd = net.Nodes[ni];
                if (nd.RouteDist > RoadNet.ScopeDistance - 12f) continue;
                if (nd.Id >= 0 && nd.OsmDegree >= 3 && nd.Segs.Count < nd.OsmDegree)
                    Fail(E, "degree", nd.P, $"Knoten {nd.Id}: OSM-Grad {nd.OsmDegree}, im Netz {nd.Segs.Count} Schenkel");
                if (nd.Id < 0 && nd.Id > -900000 && nd.Segs.Count == 1)
                    Fail(E, "cut", nd.P, "Kette endet mitten im Korridor");
            }
            foreach (var d in net.Dropped)
                for (int ni = 0; ni < net.Nodes.Count; ni++)
                {
                    var nd = net.Nodes[ni];
                    if (nd.RouteDist > RoadNet.ScopeDistance - 12f) continue;
                    if ((nd.P - d.A).magnitude < 1.5f || (nd.P - d.B).magnitude < 1.5f)
                    { Fail(E, "dropped", (d.A + d.B) * .5f, $"verworfene Kette {d.Length:0.0} m ({d.Reason}) an Knoten {nd.Id}"); break; }
                }
            E.R.Metrics["Schenkel"] = $"{ok}/{legs} Schenkel bis in den Knoten asphaltiert";
        }

        // ------------------------------------------------------------------ 2) Umrisstreue
        private static void OutlineFidelity(Env E)
        {
            Declare(E, "edgeIn", "Fahrbahnrand innen (Rand − 0,3 m) nicht asphaltiert");
            Declare(E, "edgeOut", "Asphalt außerhalb Rand + 0,3 m (Ausbuchtung)");
            Declare(E, "outline", "Asphaltumriss > 0,3 m vom Sollrand (kein Streifenrand)");
            var net = E.Net; var asp = E.S.Asphalt; int probes = 0, pts = 0;
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sg = net.Segs[si]; int n = sg.S.Count;
                for (int k = 1; k + 1 < n; k += 3)
                {
                    var s = sg.S[k]; Vector2 p = XZ(s.pos);
                    if (InZone(E, p, 0f)) continue;
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        Vector2 nn = XZ(s.side) * sd;
                        Vector2 pin = p + nn * (s.half - EdgeTol), pout = p + nn * (s.half + EdgeTol);
                        probes++;
                        if (!asp.Inside(pin.x, pin.y)) Fail(E, "edgeIn", pin, $"Abschnitt {si} Probe {k}: Rand innen ohne Asphalt");
                        if (asp.Inside(pout.x, pout.y) && !InStrip(E, pout, -EdgeTol, si, out _, out _, k))
                            Fail(E, "edgeOut", pout, $"Abschnitt {si} Probe {k}: Asphalt {EdgeTol:0.0} m hinter dem Rand");
                    }
                }
            }
            // Kanten mit einem Punkt der Umrissglättung (Eckenbogen, Sprung, Zahn, Nase: RoadSurface.Result.Smoothed) weichen absichtlich vom
            // Streifenrand ab, höchstens SmoothTol (Bogenersatz <= 3,5 m dazu / 1,3 m weg, Nase <= halbe Mittelstreifenbreite)
            var sm = new List<Vector3>(E.S.Smoothed.Count); foreach (var q in E.S.Smoothed) sm.Add(new Vector3(q.x, 0f, q.y));
            var smH = sm.Count > 0 ? new RoadNet.PointHash(sm, 8f) : null;
            foreach (var loop in E.S.AsphaltLoops)
                for (int i = 0; i < loop.Length; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % loop.Length]; float len = (b - a).magnitude;
                    if (len < 1e-3f || Artificial(a, b)) continue;
                    bool smoothed = smH != null && (smH.Nearest(a, .03f, out _) < float.MaxValue || smH.Nearest(b, .03f, out _) < float.MaxValue);
                    int m = Mathf.Max(1, Mathf.CeilToInt(len));
                    for (int t = 0; t < m; t++)
                    {
                        Vector2 q = Vector2.Lerp(a, b, (t + .5f) / m);
                        if (InZone(E, q, 0f)) continue;
                        pts++;
                        if (smoothed ? E.X.DistToBoundary(q, SmoothTol + .5f) > SmoothTol : E.X.DistToBoundary(q, 3f) > EdgeTol) Fail(E, "outline", q, smoothed ? "geglätteter Umrisspunkt weicht > 4,5 m vom Streifenrand ab" : "Umrisspunkt nicht auf einem Streifenrand");
                    }
                }
            E.R.Metrics["Umrisstreue"] = $"{probes} Randproben (innen/außen ±{EdgeTol:0.0} m), {pts} Umrisspunkte außerhalb der Kreuzungszonen";
        }

        // Abweichung (0..90°) der Übergangsrichtung von der Sollrichtung am Asphaltrand: senkrecht zur Randkante, an einer Ecke des
        // Asphaltrands (Stummel, Bogenanfang) entlang der Winkelhalbierenden der Kantennormalen. Fußpunkt = der Endpunkt der Kette, der
        // auf dem Asphaltrand liegt; < 0: kein Fußpunkt.
        private static float EdgeDeviation(Vector2 first, Vector2 last, Vector2 dir, EdgeIndex aspEdges)
        {
            for (int end = 0; end < 2; end++)
            {
                Vector2 foot = end == 0 ? first : last;
                if (aspEdges.Nearest(foot, .3f) > .15f) continue;
                var ds = new List<Vector2>(); aspEdges.Incident(foot, .15f, ds);
                if (ds.Count == 0 && aspEdges.NearestDir(foot, .3f, out var nd)) ds.Add(nd);      // Fußpunkt mitten auf einer Kante
                if (ds.Count == 0) continue;
                Vector2 nsum = Vector2.zero;
                foreach (var d in ds) nsum += new Vector2(d.y, -d.x) * (Vector2.Dot(ds[0], d) >= 0f ? 1f : -1f);
                // Kanten laufen in beliebiger Richtung in den Index (a->b): für die Normalenmittelung dieselbe Laufrichtung wie ds[0]
                if (nsum.magnitude < .3f) continue;
                Vector2 want = nsum.normalized;
                return Mathf.Acos(Mathf.Clamp(Mathf.Abs(Vector2.Dot(dir, want)), 0f, 1f)) * Mathf.Rad2Deg;
            }
            return -1f;
        }

        // Liegt die Seite sd von Probe k innen in einer Kurve, deren Krümmungsradius (Kreis durch k-3, k, k+3) kleiner als dist ist?
        // Dort schneidet sich die Versatzlinie selbst (Spitze), die Bandbreite ist nicht mehr messbar.
        private static bool InnerCusp(RoadNet.Segment sg, int k, int sd, float dist)
        {
            if (k < 3 || k + 3 >= sg.S.Count) return false;
            Vector2 a = XZ(sg.S[k - 3].pos), b = XZ(sg.S[k].pos), c = XZ(sg.S[k + 3].pos);
            Vector2 ab = b - a, bc = c - b, ac = c - a;
            float cross = ab.x * bc.y - ab.y * bc.x;
            if (Mathf.Abs(cross) < 1e-4f) return false;
            float R = ab.magnitude * bc.magnitude * ac.magnitude / (2f * Mathf.Abs(cross));
            bool inner = (cross > 0f && sd < 0) || (cross < 0f && sd > 0);
            return inner && R < dist;
        }

        // ------------------------------------------------------------------ 3) Bandqualität
        private static void BandQuality(Env E)
        {
            Declare(E, "bandW", "Bandbreite/-material weicht > 0,25 m ab");
            Declare(E, "blob", "Band weiter als 2,25 m vom Asphaltrand (Fleck)");
            Declare(E, "sliver", "Splitter-Dreieck im Band (Höhe < 1 cm)");
            Declare(E, "spike", "spitzer Bandumriss (< 30°) außerhalb des Asphaltrands");
            var net = E.Net; var asp = E.S.Asphalt; int probes = 0;
            // Ecken des Asphaltrands (Knick > 20°): der Klassenübergang steht dort auf der Winkelhalbierenden, Querproben der Kante
            // in Eckennähe können in der Nachbarklasse landen
            var cornerPts = new List<Vector3>();
            foreach (var al in E.S.AsphaltLoops)
                for (int i = 0; i < al.Length; i++)
                {
                    Vector2 p0 = al[(i + al.Length - 1) % al.Length], p1 = al[i], p2 = al[(i + 1) % al.Length];
                    Vector2 d0 = p1 - p0, d1 = p2 - p1; if (d0.magnitude < 1e-3f || d1.magnitude < 1e-3f || Artificial(p0, p1) || Artificial(p1, p2)) continue;
                    if (Mathf.Abs(Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1))) > 20f * Mathf.Deg2Rad) cornerPts.Add(new Vector3(p1.x, 0f, p1.y));
                }
            var corners = cornerPts.Count > 0 ? new RoadNet.PointHash(cornerPts, 8f) : null;
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sg = net.Segs[si]; int n = sg.S.Count;
                for (int k = 3; k + 3 < n; k += 3)
                {
                    var s = sg.S[k]; Vector2 p = XZ(s.pos);
                    if (InZone(E, p, 0f)) continue;
                    if (sg.Urban[k] != sg.Urban[k - 3] || sg.Urban[k] != sg.Urban[k + 3]) continue;
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        byte kind = sd > 0 ? sg.RightKind[k] : sg.LeftKind[k];
                        bool med = kind == RoadNet.KindMedian, urb = sg.Urban[k];
                        float w = med ? MedianKerbW : urb ? UrbanW : RuralW;
                        Vector2 nn = XZ(s.side) * sd;
                        Vector2 pin = p + nn * (s.half + Mathf.Max(w - WidthTol, w * .5f)), pout = p + nn * (s.half + w + WidthTol);
                        if (InStrip(E, pin, 0f, si, out _, out _, k)) continue;
                        // Ausnahmen (Messung nicht aussagekräftig): Band gehört an einer Einmündung/Gabelung zur Fremdstraße (deren Band
                        // hat Vorrang und verdrängt dieses; Fremdstreifen näher als die Bandbreite, an Mittelstreifen < 1 m),
                        // Bordsteinbogen/Knotenrundung < 3 m, Innenseite einer Kurve enger als die Bandbreite
                        if (E.X.Hit(pin, med ? -1f : -(UrbanW + WidthTol), si, out _, out _) || E.X.DistToRim(pin, 3f) < float.MaxValue) continue;
                        if (InnerCusp(sg, k, sd, s.half + w + WidthTol)) continue;
                        probes++;
                        var mat = urb ? E.S.Urban : E.S.Rural;
                        if (!mat.Inside(pin.x, pin.y))
                        {
                            if (corners != null) { corners.Nearest(pin, 2.5f, out int ci); if (ci >= 0) continue; }
                            Fail(E, "bandW", pin, $"Abschnitt {si} Probe {k}: {(E.S.Band.Inside(pin.x, pin.y) ? "falsches Material" : "Band fehlt")} bei Rand + {w - WidthTol:0.00} m (soll {w:0.00} m {(urb ? "Gehweg" : "Randstreifen")})");
                        }
                        else if (!InStrip(E, pout, -2.6f, si, out _, out _, k) && (E.S.Band.Inside(pout.x, pout.y) || asp.Inside(pout.x, pout.y)))
                            Fail(E, "bandW", pout, $"Abschnitt {si} Probe {k}: Band breiter als {w + WidthTol:0.00} m (soll {w:0.00} m)");
                    }
                }
            }
            // Flecken: Bandecken weiter vom Asphaltrand als die größte Bandbreite
            var edges = new EdgeIndex();
            foreach (var loop in E.S.AsphaltLoops)
                for (int i = 0; i < loop.Length; i++)
                { Vector2 a = loop[i], b = loop[(i + 1) % loop.Length]; if ((b - a).sqrMagnitude > 1e-6f && !Artificial(a, b)) edges.Add(a, b); }
            var bd = E.S.Band; int nSl = 0, nSeam = 0;
            for (int t = 0; t < bd.Count; t++)
            {
                bd.Get(t, out var a, out var b, out var c);
                foreach (var v in new[] { a, b, c })
                    if (edges.Nearest(v, UrbanW + .5f) > UrbanW + WidthTol) Fail(E, "blob", v, "Bandecke weiter als 2,25 m vom Asphaltrand");
                float l = Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude));
                float area2 = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
                if (l > .4f && area2 / l < .01f)
                {
                    // Punkte auf dem Kachelraster (Nahtstellen) müssen mit der Nachbarkachel übereinstimmen und dürfen nicht verschoben werden
                    if (OnTileGrid(a) || OnTileGrid(b) || OnTileGrid(c)) { nSeam++; continue; }
                    nSl++; Fail(E, "sliver", (a + b + c) / 3f, $"Splitter {l:0.0} m lang, Höhe {area2 / l * 100f:0.0} cm");
                }
            }
            // spitze Umrisswinkel der Bandflächen (Innenwinkel links der Kante); Spitzen AUF dem Asphaltrand sind Asphaltgeometrie
            int nAng = 0, nIface = 0;
            for (int cls = 0; cls < 2; cls++)
                foreach (var loop in cls == 0 ? E.S.UrbanLoops : E.S.RuralLoops)
                {
                    int n = loop.Length;
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 p0 = loop[(i + n - 1) % n], p1 = loop[i], p2 = loop[(i + 1) % n];
                        if (Artificial(p0, p1) || Artificial(p1, p2)) continue;
                        Vector2 d0 = p1 - p0, d1 = p2 - p1; if (d0.magnitude < 1e-3f || d1.magnitude < 1e-3f) continue;
                        float turn = Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1));       // links positiv
                        float interior = (Mathf.PI - turn) * Mathf.Rad2Deg;
                        if (interior >= MinAngleDeg || interior <= 0f) continue;
                        nAng++;
                        if (edges.Nearest(p1, .08f) < .05f) continue;
                        // Spitze am Klassenübergang: die Nachbarklasse (Gehweg <-> Randstreifen, Breite 2,0 / 1,6 m) berührt die Spitze; sie
                        // entsteht dort, wo die Außenkante der einen Klasse (0,4 m weiter außen) tangential in die Rundung der anderen
                        // übergeht -- kein Vorsprung ins Freie, gezählt aber nicht gemeldet
                        var other = cls == 0 ? E.S.Rural : E.S.Urban; bool iface = false;
                        for (int a8 = 0; a8 < 8 && !iface; a8++)
                        { float an = a8 * Mathf.PI / 4f; iface = other.Inside(p1.x + Mathf.Cos(an) * .05f, p1.y + Mathf.Sin(an) * .05f); }
                        if (iface) { nIface++; continue; }
                        Fail(E, "spike", p1, $"Bandumriss mit Innenwinkel {interior:0}° ({(cls == 0 ? "Gehweg" : "Randstreifen")})");
                    }
                }
            E.R.Metrics["Band"] = $"{probes} Breitenproben, {bd.Count} Banddreiecke, {nSl} Splitter (+{nSeam} Nahtnadeln am Kachelrand), {nAng} Umrisswinkel < {MinAngleDeg:0}° (davon außerhalb des Asphaltrands: {E.Points["spike"]}, am Klassenübergang: {nIface})";
        }

        // ------------------------------------------------------------------ 4) Ortslage-Übergänge
        private static void Transitions(Env E)
        {
            Declare(E, "cutShape", "Übergang Gehweg/Randstreifen nicht gerade");
            Declare(E, "cutAngle", "Übergang Gehweg/Randstreifen nicht quer zur Straße");
            Declare(E, "cutAlign", "Übergang Gehweg/Randstreifen nur auf einer Straßenseite (Gegenseite wechselt nicht an derselben Stelle)");
            int chains = 0, aligned = 0, inZone = 0, nFork = 0;
            var rural = E.S.Rural; var asp = E.S.Asphalt;
            var aspEdges = new EdgeIndex();
            foreach (var al in E.S.AsphaltLoops)
                for (int i = 0; i < al.Length; i++)
                { Vector2 a = al[i], b = al[(i + 1) % al.Length]; if ((b - a).sqrMagnitude > 1e-6f && !Artificial(a, b)) aspEdges.Add(a, b); }
            foreach (var loop in E.S.UrbanLoops)
            {
                int n = loop.Length; var iface = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % n]; float len = (b - a).magnitude;
                    if (len < 1e-3f || Artificial(a, b)) continue;
                    Vector2 d = (b - a) / len, nrm = new Vector2(d.y, -d.x);
                    int hits = 0, tot = 0;
                    for (float t = Mathf.Min(.1f, len * .5f); t < len; t += .25f)
                    {
                        Vector2 p = a + d * t + nrm * .06f; tot++;
                        if (rural.Inside(p.x, p.y) && !asp.Inside(p.x, p.y)) hits++;
                    }
                    iface[i] = tot > 0 && hits * 2 > tot;
                }
                int start = 0; while (start < n && iface[start]) start++;
                if (start == n) continue;                                   // (nie: Schleife komplett Übergang)
                for (int step = 0; step < n;)
                {
                    int i = (start + step) % n;
                    if (!iface[i]) { step++; continue; }
                    int len = 0; while (step + len < n && iface[(start + step + len) % n]) len++;
                    Vector2 first = loop[i], last = loop[(start + step + len) % n];
                    Vector2 chord = last - first; float L = chord.magnitude;
                    step += len;
                    if (L < .5f) continue;               // Schmalstücke (Bordbreite .35 m) sind kein Übergang quer über ein Band
                    // In Kreuzungszonen bestimmt die Kreuzungsgeometrie (Stufe zwischen Fahrbahnbreiten, Rundungen) den Rand, dort gibt es
                    // keine gerade Straßenkante, zu der ein Übergang quer stehen könnte: nur gezählt
                    if (InZone(E, (first + last) * .5f, 0f)) { inZone++; continue; }
                    // Gabelung: Mitte des Übergangs liegt im Bandbereich zweier verschiedener Straßen (Band der einen verdrängt das der
                    // anderen entlang der Gabel) -- kein Querschnitt einer einzelnen Straße
                    Vector2 mid0 = (first + last) * .5f;
                    if (E.X.Hit(mid0, -(UrbanW + WidthTol), -1, out int f1, out _) && E.X.Hit(mid0, -(UrbanW + WidthTol), f1, out _, out _)) { nFork++; continue; }
                    chains++;
                    Vector2 dir = chord / L, mid = (first + last) * .5f; float dev = 0f;
                    for (int q = 0; q <= len; q++)
                    {
                        Vector2 v = loop[(i + q) % n] - first;
                        dev = Mathf.Max(dev, Mathf.Abs(v.x * dir.y - v.y * dir.x));
                    }
                    if (dev > Mathf.Max(.1f, .15f * L)) Fail(E, "cutShape", mid, $"Übergang {L:0.0} m lang, Abweichung von der Geraden {dev:0.00} m");
                    if (E.X.Field.Nearest(mid.x, mid.y, 12f, out int si, out _))
                    {
                        Vector2 tg = XZ(E.X.Field.Samples[si].tangent).normalized;
                        // beide Seiten schalten an derselben Stelle: auf der Gegenseite wechselt die Klasse innerhalb ± CutAlignReach entlang der Achse
                        {
                            var fs = E.X.Field.Samples[si]; Vector2 ap = XZ(fs.pos), nn = XZ(fs.side).normalized;
                            float sgn = Vector2.Dot(mid - ap, nn) >= 0f ? 1f : -1f;
                            int c0 = BandClassAt(E, ap - tg * CutAlignReach - nn * sgn * (fs.half + 1f)), c1 = BandClassAt(E, ap + tg * CutAlignReach - nn * sgn * (fs.half + 1f));
                            if (c0 >= 0 && c1 >= 0)
                            {
                                if (c0 == c1) Fail(E, "cutAlign", mid, $"Übergang einseitig: Gegenseite bleibt {(c0 == 0 ? "Gehweg" : "Randstreifen")} im Umkreis von ±{CutAlignReach:0.0} m");
                                else aligned++;
                            }
                        }
                        float ang = Mathf.Acos(Mathf.Clamp(Mathf.Abs(Vector2.Dot(dir, tg)), 0f, 1f)) * Mathf.Rad2Deg;
                        if (Mathf.Abs(90f - ang) > CutAngleTolDeg)
                        {
                            // in Kurven weicht die Achsrichtung von der Randrichtung ab: maßgeblich ist der Asphaltrand, an dem der Übergang
                            // beginnt (dort steht er im Entwurf senkrecht)
                            float devE = EdgeDeviation(first, last, dir, aspEdges);
                            if (devE < 0f || devE > EdgeCutTolDeg)
                                Fail(E, "cutAngle", mid, $"Übergang {L:0.0} m lang unter {ang:0}° zur Straßenachse, {(devE < 0f ? "kein Asphaltrand" : devE.ToString("0") + "° Abweichung von der Sollrichtung am Asphaltrand")} (soll 90° ± {CutAngleTolDeg:0}°)");
                        }
                    }
                }
            }
            E.R.Metrics["Übergänge"] = $"{chains} Übergangskanten Gehweg/Randstreifen auf freier Strecke ({aligned} mit Gegenkante an derselben Stelle; +{inZone} in Kreuzungszonen, +{nFork} an Gabelungen: nicht geprüft)";
        }

        public const float CutAlignReach = 3.5f, MinClassRunLength = 18f, ClassRunStep = .5f;

        // 0 = Gehweg (Urban), 1 = Randstreifen (Rural), -1 = kein Band
        private static int BandClassAt(Env E, Vector2 q)
        {
            if (E.S.Urban.Inside(q.x, q.y)) return 0;
            if (E.S.Rural.Inside(q.x, q.y)) return 1;
            return -1;
        }

        // ------------------------------------------------------------------ 4b) Klassenfolgen am Asphaltrand
        // Läuft man den Asphaltrand entlang, darf ein Stück einer Bandklasse (Gehweg/Randstreifen), das auf beiden Seiten an die jeweils
        // andere Klasse grenzt, nicht kürzer als MinClassRunLength sein (graue Stummel im ländlichen Randstreifen, grüne im Gehweg).
        private static void ClassRuns(Env E)
        {
            Declare(E, "classRun", "Bandklasse wechselt in Stücken < 18 m (Stummel zwischen zwei Nachbarklassen)");
            Declare(E, "jClass", "Kreuzung mit gemischter Bandklasse (Zufahrten teils Gehweg, teils Randstreifen)");
            int mixed = 0;
            foreach (var j in E.Net.Junctions)
            {
                bool u0 = false, r0 = false;
                foreach (var e in j.Ends) { if (e.Urban) u0 = true; else r0 = true; }
                if (u0 && r0) { mixed++; Fail(E, "jClass", j.Center, $"Kreuzung mit {j.Ends.Count} Zufahrten: {j.Ends.FindAll(x => x.Urban).Count} Gehweg, {j.Ends.FindAll(x => !x.Urban).Count} Randstreifen"); }
            }
            int loops = 0, runsTotal = 0; float shortest = float.MaxValue;
            foreach (var loop in E.S.AsphaltLoops)
            {
                int n = loop.Length; var pts = new List<Vector2>(); var cls = new List<int>(); var w = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % n]; float len = (b - a).magnitude;
                    if (len < 1e-3f) continue;
                    bool art = Artificial(a, b); Vector2 d = (b - a) / len, nrm = new Vector2(d.y, -d.x);     // Asphalt liegt links, Band rechts
                    for (float t = 0f; t < len; t += ClassRunStep)
                    {
                        float ww = Mathf.Min(ClassRunStep, len - t); Vector2 p = a + d * (t + ww * .5f), q = p + nrm * .3f;
                        pts.Add(p); w.Add(ww); cls.Add(art || E.S.Asphalt.Inside(q.x, q.y) ? -2 : BandClassAt(E, q));
                    }
                }
                int m = cls.Count; if (m < 4) continue;
                loops++;
                int start = -1; for (int i = 0; i < m && start < 0; i++) if (cls[i] != cls[(i + m - 1) % m]) start = i;
                if (start < 0) continue;                                     // ganze Schleife eine Klasse
                var run = new List<ClassRun>();
                for (int q = 0; q < m;)
                {
                    int i = (start + q) % m, e = q; float len = 0f;
                    while (e < m && cls[(start + e) % m] == cls[i]) { len += w[(start + e) % m]; e++; }
                    run.Add(new ClassRun { C = cls[i], L = len, P = pts[(start + (q + e - 1) / 2) % m] }); q = e;
                }
                // Lücken ohne Band (< 2 m: Bordstein-/Rundungsstücke) zwischen gleichen Klassen sind durchlässig
                for (bool again = true; again;)
                {
                    again = false;
                    for (int r = 0; r < run.Count && run.Count > 2; r++)
                    {
                        if (run[r].C != -1 || run[r].L >= 2f) continue;
                        int pr = (r + run.Count - 1) % run.Count, nx = (r + 1) % run.Count;
                        if (pr == nx || run[pr].C != run[nx].C || run[pr].C < 0) continue;
                        var merged = run[pr]; merged.L += run[r].L + run[nx].L; run[pr] = merged;
                        int hi = Mathf.Max(r, nx), lo = Mathf.Min(r, nx); run.RemoveAt(hi); run.RemoveAt(lo); again = true; break;
                    }
                }
                for (int r = 0; r < run.Count; r++)
                {
                    if (run[r].C < 0) continue;
                    runsTotal++;
                    int pr = (r + run.Count - 1) % run.Count, nx = (r + 1) % run.Count;
                    if (run.Count < 3 || run[pr].C != 1 - run[r].C || run[nx].C != 1 - run[r].C) continue;
                    if (run[r].L >= 1f) shortest = Mathf.Min(shortest, run[r].L);
                    if (run[r].L < 1f) continue;                            // < 1 m: Schnitt-/Eckengeometrie (cutShape, spike), kein Stück
                    if (run[r].L < MinClassRunLength)
                        Fail(E, "classRun", run[r].P, $"{(run[r].C == 0 ? "Gehweg" : "Randstreifen")}-Stück nur {run[r].L:0.0} m lang zwischen {(run[r].C == 0 ? "Randstreifen" : "Gehweg")} (soll >= {MinClassRunLength:0} m)");
                }
            }
            E.R.Metrics["Klassenfolgen"] = $"{E.Net.Junctions.Count} Kreuzungen ({mixed} mit gemischter Klasse), {runsTotal} Bandklassen-Läufe entlang {loops} Asphaltumrissen, kürzester eingeklemmter Lauf {(shortest < float.MaxValue ? shortest.ToString("0.0") + " m" : "-")}";
        }

        private struct ClassRun { public int C; public float L; public Vector2 P; }

        // ------------------------------------------------------------------ 4c) Stufen im Asphaltumriss
        // Ein Stufenknick ("Z"): zwei lange, fast parallele Randkanten, verbunden durch eine kurze Querkante -- die Fahrbahn springt seitlich.
        // Das ist die sichtbare Signatur von Breiten-/Versatzsprüngen an Kreuzungsschnitten (Durchgänge), unabhängig vom Entwurf gemessen.
        public const float StepMinJog = .5f, StepMaxRun = 4f, StepMinLeg = 6f;
        private static void OutlineSteps(Env E)
        {
            Declare(E, "outlineStep", $"Stufe im Asphaltrand (Versatz >= {StepMinJog:0.0} m über < {StepMaxRun:0} m Querkante)");
            int steps = 0;
            foreach (var loop in E.S.AsphaltLoops)
            {
                int n = loop.Length; if (n < 6) continue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % n], c = loop[(i + 2) % n], d = loop[(i + 3) % n];
                    if (Artificial(a, b) || Artificial(b, c) || Artificial(c, d)) continue;
                    Vector2 e0 = b - a, e1 = c - b, e2 = d - c;
                    float l0 = e0.magnitude, l1 = e1.magnitude, l2 = e2.magnitude;
                    if (l1 < .3f || l1 > StepMaxRun || l0 < StepMinLeg || l2 < StepMinLeg) continue;
                    float t0 = Mathf.Atan2(e0.x * e1.y - e0.y * e1.x, Vector2.Dot(e0, e1)) * Mathf.Rad2Deg;
                    float t1 = Mathf.Atan2(e1.x * e2.y - e1.y * e2.x, Vector2.Dot(e1, e2)) * Mathf.Rad2Deg;
                    if (t0 * t1 >= 0f || Mathf.Abs(t0) < 45f || Mathf.Abs(t0) > 135f || Mathf.Abs(t1) < 45f || Mathf.Abs(t1) > 135f) continue;
                    if (Vector2.Angle(e0, e2) > 30f) continue;
                    // Nase/Stummel (U statt Z): auf die kurze Querkante folgt nach kurzer Längskante der Rücksprung -- kein Versatz der Fahrbahn
                    if (l2 < 12f)
                    {
                        Vector2 e3 = loop[(i + 4) % n] - d; float l3 = e3.magnitude;
                        float t2 = Mathf.Atan2(e2.x * e3.y - e2.y * e3.x, Vector2.Dot(e2, e3)) * Mathf.Rad2Deg;
                        if (l3 >= .3f && l3 <= StepMaxRun && t1 * t2 > 0f && Vector2.Angle(e1, e3) > 150f) continue;
                    }
                    if (l0 < 12f)
                    {
                        Vector2 em = a - loop[(i + n - 1) % n]; float lm = em.magnitude;
                        float tm = Mathf.Atan2(em.x * e0.y - em.y * e0.x, Vector2.Dot(em, e0)) * Mathf.Rad2Deg;
                        if (lm >= .3f && lm <= StepMaxRun && tm * t0 > 0f && Vector2.Angle(e1, em) > 150f) continue;
                    }
                    // seitlicher Versatz der beiden Längskanten
                    Vector2 dir = (e0 / l0 + e2 / l2).normalized; float jog = Mathf.Abs((c - b).x * dir.y - (c - b).y * dir.x);
                    if (jog < StepMinJog) continue;
                    steps++;
                    Fail(E, "outlineStep", (b + c) * .5f, $"Stufe im Asphaltrand: Versatz {jog:0.00} m (Querkante {l1:0.0} m)");
                }
            }
            E.R.Metrics["Randstufen"] = $"{steps} Stufen im Asphaltumriss (Versatz >= {StepMinJog:0.0} m)";
        }

        // ------------------------------------------------------------------ 5) Markierungen
        private static void Markings(Env E)
        {
            Declare(E, "markOff", "Markierung nicht auf Asphalt (außerhalb der eigenen Fahrbahn)");
            Declare(E, "markJunction", "Markierung innerhalb einer Kreuzung (Abschnittsende/Verbindungsstück)");
            Declare(E, "markForeign", "Markierung auf der Fahrbahn einer anderen Straße");
            Declare(E, "markMouth", "gelbe Randlinie läuft über eine Einmündung");
            var net = E.Net; var asp = E.S.Asphalt;
            foreach (var m in E.S.Markings)
            {
                Vector2 c = (m.P[0] + m.P[1] + m.P[2] + m.P[3]) * .25f;
                bool on = true; foreach (var q in m.P) if (!asp.Inside(q.x, q.y)) on = false;
                if (!on) Fail(E, "markOff", c, $"Abschnitt {m.Seg} Art {m.Kind}: Ecke nicht auf Asphalt");
                // in einer Kreuzung / auf fremder Fahrbahn: Mitte und Ecken (Rand 5 cm eingezogen)
                var pts = new List<Vector2> { c };
                foreach (var q in m.P) pts.Add(c + (q - c) * .9f);
                bool foreign = false, junction = false;
                foreach (var q in pts)
                {
                    if (InStrip(E, q, .05f, m.Seg, out int hs, out int hk)) { foreign = true; }
                    // Kreuzungsanteil: abgeschnittenes Ende eines Abschnitts oder Verbindungsstück (auch der eigene)
                    if (E.X.JunctionPart(q, .5f)) { junction = true; break; }
                }
                if (junction) Fail(E, "markJunction", c, $"Abschnitt {m.Seg} Art {m.Kind}: liegt im Kreuzungsbereich");
                if (foreign) Fail(E, "markForeign", c, $"Abschnitt {m.Seg} Art {m.Kind}: liegt auf fremder Fahrbahn");
                if (m.Kind == RoadSurface.MarkEdge && m.Seg >= 0 && m.Seg < net.Segs.Count)
                {
                    var sg = net.Segs[m.Seg];
                    int best = -1; float bd = float.MaxValue;
                    for (int k = 0; k < sg.S.Count; k++) { float d = (XZ(sg.S[k].pos) - c).sqrMagnitude; if (d < bd) { bd = d; best = k; } }
                    if (best >= 0)
                    {
                        var s = sg.S[best]; Vector2 nn = XZ(s.side); float lat = Vector2.Dot(c - XZ(s.pos), nn);
                        Vector2 pout = c + nn * (Mathf.Sign(lat) * (s.half + .2f) - lat);
                        // Asphalt außen, der zu einer anderen Fahrbahn gehört (Nachbarfahrbahn einer Doppelfahrbahn ohne Lücke, Innenseite einer
                        // engen Kurve des eigenen Abschnitts), ist keine Einmündung -- dort ist die Randlinie richtig. Fahrbahnen, deren
                        // OSM-Achsen weniger als 0,6 m Lücke lassen, werden ohne Mittelstreifen zusammengefasst
                        bool otherCarriageway = E.X.Hit(pout, -.4f, m.Seg, out _, out _, best, 6);     // Lücke zur Nachbarfahrbahn < 0,6 m: geschlossen
                        if (asp.Inside(pout.x, pout.y) && !otherCarriageway) Fail(E, "markMouth", c, $"Abschnitt {m.Seg}: Randlinie hat außen weiter Asphalt (Einmündung/Kreuzung)");
                    }
                }
            }
            E.R.Metrics["Markierungen"] = $"{E.S.Markings.Count} Markierungsstücke geprüft";
        }

        // ------------------------------------------------------------------ 6) Löcher / Splitter
        private static void Holes(Env E)
        {
            Declare(E, "hole", $"kleines Asphaltloch (< {HoleMaxArea:0} m²)");
            Declare(E, "splinter", $"Asphaltsplitter (< {SplinterMaxArea:0} m²)");
            Declare(E, "nick", "Zacke im Asphaltumriss (beide Kanten < 1 m, Wendewinkel > 120°)");
            Declare(E, "slit", "Schlitz im Asphaltumriss (U-Kerbe, Grund < 0,6 m, Öffnung < 1 m)");
            Declare(E, "pocket", "Tasche im Asphaltumriss (Wendung >= 140 ° auf < 5 m, Öffnung < 1,8 m, Ränder biegen nach außen)");
            int pos = 0, neg = 0, medians = 0;
            // Mittelstreifen (Lücke zwischen den Richtungsfahrbahnen) ist gewollt: Löcher in 12 m Umkreis von Mittelstreifenproben
            // gelten als Teil davon (auch die auslaufende Spitze, an der der Partner nicht mehr quer gegenüber liegt)
            var med = new List<Vector2>();
            foreach (var sg in E.Net.Segs)
                for (int k = 0; k < sg.S.Count; k += 2)
                    if (sg.LeftKind[k] == RoadNet.KindMedian || sg.RightKind[k] == RoadNet.KindMedian) med.Add(new Vector2(sg.S[k].pos.x, sg.S[k].pos.z));
            foreach (var loop in E.S.AsphaltLoops)
            {
                bool border = false; for (int i = 0; i < loop.Length && !border; i++) border = Artificial(loop[i], loop[(i + 1) % loop.Length]);
                float a = Area2(loop) * .5f;
                if (a < 0f) neg++; else pos++;
                // Zacken: Fähnchen/Kerbe von < 1 m im Umriss (Naht zwischen Streifenrand und Ausrundung); Punkte auf dem Kachelraster
                // sind Schnittstellen der Kacheln und werden nicht gewertet
                for (int i = 0; i < loop.Length; i++)
                {
                    Vector2 p0 = loop[(i + loop.Length - 1) % loop.Length], p1 = loop[i], p2 = loop[(i + 1) % loop.Length];
                    Vector2 d0 = p1 - p0, d1 = p2 - p1; float l0 = d0.magnitude, l1 = d1.magnitude;
                    if (l0 < 1e-3f || l1 < 1e-3f || l0 >= 1f || l1 >= 1f || OnTileGrid(p0) || OnTileGrid(p1) || OnTileGrid(p2)) continue;
                    if (Vector2.Dot(d0, d1) < -.5f * l0 * l1) Fail(E, "nick", p1, $"Zacke im Asphaltumriss: Kanten {l0:0.00}/{l1:0.00} m, Wendewinkel {Mathf.Abs(Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1))) * Mathf.Rad2Deg:0}°");
                }
                // Schlitze: U-förmige Kerbe (zwei antiparallele Kanten < 4 m, Grund < 0,6 m, Öffnung < 1 m, Nichtasphalt innen)
                for (int i = 0; i < loop.Length && loop.Length > 5; i++)
                {
                    Vector2 v0 = loop[i], v1 = loop[(i + 1) % loop.Length], v2 = loop[(i + 2) % loop.Length], v3 = loop[(i + 3) % loop.Length];
                    Vector2 e1 = v1 - v0, e2 = v2 - v1, e3 = v3 - v2; float l1 = e1.magnitude, l2 = e2.magnitude, l3 = e3.magnitude;
                    if (l1 < 5e-2f || l2 < 5e-2f || l3 < 5e-2f || l2 >= .6f || l1 >= 4f || l3 >= 4f || (v3 - v0).magnitude >= 1f) continue;
                    if (OnTileGrid(v0) || OnTileGrid(v1) || OnTileGrid(v2) || OnTileGrid(v3)) continue;
                    if (e1.x * e2.y - e1.y * e2.x < 0f && e2.x * e3.y - e2.y * e3.x < 0f && Vector2.Dot(e1, e3) < -.6f * l1 * l3)
                        Fail(E, "slit", (v1 + v2) * .5f, $"Schlitz im Asphaltumriss: Grund {l2:0.00} m, Öffnung {(v3 - v0).magnitude:0.00} m, Tiefe {l1:0.0}/{l3:0.0} m");
                }
                PocketScan(E, loop);
                if (border) continue;
                Vector2 c = Vector2.zero; foreach (var q in loop) c += q; c /= loop.Length;
                if (a < 0f && -a < HoleMaxArea)
                {
                    bool isMedian = false; foreach (var m in med) if ((m - c).sqrMagnitude < 144f) { isMedian = true; break; }
                    if (isMedian) { medians++; continue; }
                    Fail(E, "hole", c, $"Loch {-a:0.0} m²");
                }
                if (a > 0f && a < SplinterMaxArea) Fail(E, "splinter", c, $"Asphaltinsel {a:0.0} m²");
            }
            E.R.Metrics["Umrissrichtung"] = $"{pos} Außenumrisse (Fläche > 0), {neg} Löcher (Fläche < 0), davon {medians} kleine Mittelstreifenlöcher (gewollt)";
        }

        // Taschen: eine Folge von Rechtsknicken (Nichtasphalt) mit Summe >= 140 ° auf weniger als 5 m Umrisslänge, deren beide Randpunkte
        // (Öffnung < 1,8 m) nach außen abbiegen (Linksknick >= 40 °): ein Rest an einer Kreuzungsecke, in dem Gehweg/Randstreifen als Zacke
        // in die Fahrbahn ragt. Spitze Zwickel zwischen Gabelästen laufen dagegen gerade aus (kein Abbiegen am Rand) und zählen nicht.
        private static void PocketScan(Env E, Vector2[] loop)
        {
            int n = loop.Length; if (n < 7) return;
            var t = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 d0 = loop[i] - loop[(i + n - 1) % n], d1 = loop[(i + 1) % n] - loop[i];
                t[i] = d0.sqrMagnitude < 1e-8f || d1.sqrMagnitude < 1e-8f ? 0f : Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1)) * Mathf.Rad2Deg;
            }
            for (int i = 0; i < n; i++)
            {
                if (t[i] > -5f) continue;
                float sum = 0f;
                for (int k = 0; k < 6 && k < n - 4; k++)
                {
                    int j = (i + k) % n; if (t[j] > -1f) break;
                    sum += t[j]; if (sum > -140f) continue;
                    int a = (i + n - 1) % n, b = (j + 1) % n;
                    if (t[a] < 40f || t[b] < 40f || (loop[a] - loop[b]).magnitude >= 1.8f) break;
                    float path = 0f; bool grid = false;
                    for (int m = a; ; m = (m + 1) % n)
                    {
                        int m2 = (m + 1) % n; path += (loop[m2] - loop[m]).magnitude; grid |= OnTileGrid(loop[m]);
                        if (m2 == b) { grid |= OnTileGrid(loop[b]); break; }
                    }
                    if (grid || path >= 5f) break;
                    Fail(E, "pocket", loop[i], $"Tasche im Asphaltumriss: Wendung {-sum:0}° auf {path:0.0} m, Öffnung {(loop[a] - loop[b]).magnitude:0.00} m");
                    break;
                }
            }
        }
    }
}
