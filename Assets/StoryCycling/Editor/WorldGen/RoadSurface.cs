using System.Collections.Generic;
using LibTessDotNet.Double;
using UnityEngine;
using Mesh = UnityEngine.Mesh;

namespace StoryCycling.WorldGen.Editor
{
    // Straßenoberfläche als FLÄCHEN-VEREINIGUNG statt zusammengesetzter Einzelteile, je 200-m-Kachel:
    //   Asphalt  = Vereinigung aller Fahrbahnstreifen (unsere Breiten, auf denen auch die Fahrlinie liegt)
    //              + gerundete Bordsteinecken an den Kreuzungen (eigene Kreisbögen bzw. osm2streets-Flächen).
    //              KEIN Schließen/Glätten der Fläche: früher füllte ein Schließen (Dilatation + Erosion) Lücken < 3 m —
    //              das erzeugte Knubbel und Ausbuchtungen am Rand, schüttete schmale Mittelstreifen zu und ließ Kreuzungen
    //              ausfransen. Der Asphaltrand ist jetzt exakt der Streifenrand; nur kleine Löcher IN Kreuzungen werden gefüllt.
    //   Gehweg / Randstreifen = BAND um den fertigen Asphaltrand (Versatz nach außen um die Breite der Kante).
    //              Die KLASSE jeder Randkante (Gehweg, Randstreifen, Mittelstreifen-Bord) kommt aus ihrer HERKUNFT: dem
    //              Streifenrand (Abschnitt, Probe: Ortslage, Mittelstreifenseite) bzw. dem Bordsteinbogen, auf dem die Kante
    //              liegt — nicht aus der nächsten Straßenprobe. Jede Klasse wird für sich versetzt (ein Rechteck je Kante,
    //              Kreissektor an jeder Konvexecke); Gehweg hat Vorrang vor Randstreifen vor Bord. Der Wechsel Gehweg <->
    //              Randstreifen ist dadurch ein gerader Schnitt quer zur Straße; Keile und Halbklassen-Dreiecke entstehen nicht.
    // Boolesche Operationen über Umlaufregeln von LibTess (GLU-Tesselator, doppelte Genauigkeit). Jede Kachel wird
    // mit Rand (Margin) gerechnet und erst am Ende exakt auf die Kachel beschnitten -> nahtlos zwischen Kacheln.
    public static class RoadSurface
    {
        private const float Bucket = 400f;
        private const float Tile = 200f;
        private const float SidewalkW = 2f, ShoulderW = 1.6f, MedianW = .35f;
        private const float Margin = SidewalkW + 3f;
        private const float FilletOverlap = .3f;             // Überlappung der Bogenflächen mit den Zufahrtsstreifen
        private const float HoleFillArea = 25f;                // kleinere Lücken IN Kreuzungen werden gefüllt (m²)

        // Bandklassen (Reihenfolge = Vorrang)
        private const byte ClsUrban = 0, ClsRural = 1, ClsKerbUrban = 2, ClsKerbRural = 3, ClsNone = 255;
        private static readonly float[] ClsW = { SidewalkW, ShoulderW, MedianW, MedianW };
        private static byte Cls(bool median, bool urban) => median ? (urban ? ClsKerbUrban : ClsKerbRural) : (urban ? ClsUrban : ClsRural);

        private sealed class Ctx
        {
            public RoadField Near; public List<bool> Urban; public List<byte> LK, RK;
            public EdgeTags Tags;
            public RoadNet.PointHash ZoneHash; public List<Vector2> ZoneC = new List<Vector2>(); public List<float> ZoneR = new List<float>(); public float ZoneRMax;
        }

        // Bandklasse je Streifenrand-Kante, nach Herkunft (Streifen/Bordsteinbogen), im Rasterindex
        private sealed class EdgeTags
        {
            private const float C = 4f;
            private readonly List<Vector2> a = new List<Vector2>(), b = new List<Vector2>(); private readonly List<byte> cls = new List<byte>();
            private readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
            private static long K(int x, int z) => ((long)x << 32) | (uint)z;
            public void Add(Vector2 p, Vector2 q, byte c)
            {
                if ((q - p).sqrMagnitude < 1e-8f) return;
                int id = a.Count; a.Add(p); b.Add(q); cls.Add(c);
                int x0 = Mathf.FloorToInt(Mathf.Min(p.x, q.x) / C), x1 = Mathf.FloorToInt(Mathf.Max(p.x, q.x) / C);
                int z0 = Mathf.FloorToInt(Mathf.Min(p.y, q.y) / C), z1 = Mathf.FloorToInt(Mathf.Max(p.y, q.y) / C);
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                { if (!map.TryGetValue(K(x, z), out var l)) { l = new List<int>(); map[K(x, z)] = l; } l.Add(id); }
            }
            // Klasse der nächsten Kante innerhalb maxD (bei Gleichstand die mit Vorrang), sonst -1
            public int Find(Vector2 p, float maxD)
            {
                float best = maxD; int res = -1;
                int x0 = Mathf.FloorToInt((p.x - maxD) / C), x1 = Mathf.FloorToInt((p.x + maxD) / C);
                int z0 = Mathf.FloorToInt((p.y - maxD) / C), z1 = Mathf.FloorToInt((p.y + maxD) / C);
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                    if (map.TryGetValue(K(x, z), out var l))
                        foreach (int i in l)
                        {
                            Vector2 ab = b[i] - a[i]; float t = Mathf.Clamp01(Vector2.Dot(p - a[i], ab) / ab.sqrMagnitude);
                            float d = (a[i] + ab * t - p).magnitude;
                            if (d < best - 1e-3f || (d < best + 1e-3f && (res < 0 || cls[i] < res))) { best = Mathf.Min(best, d); res = cls[i]; }
                        }
                return res;
            }
        }

        // externalJunctions: Kreuzungsflächen von osm2streets (siehe Osm2StreetsGeometry), in Weltkoordinaten.
        // progress(Anteil) -> true = abbrechen
        // Ergebnis für den Prüfbericht (WorldCheck): 2D-Abdeckung von Asphalt und Gehweg/Randstreifen + Markierungen
        public sealed class Result
        {
            public readonly Coverage Asphalt = new Coverage(), Band = new Coverage();
            // Band nach Material getrennt (Gehweg = Submesh 4, Randstreifen = Submesh 1)
            public readonly Coverage Urban = new Coverage(), Rural = new Coverage();
            // Umrisse (Weltkoordinaten) je Fläche; Kanten auf dem 200-m-Kachelraster sind künstliche Schnitte
            public readonly List<Vector2[]> AsphaltLoops = new List<Vector2[]>(), UrbanLoops = new List<Vector2[]>(), RuralLoops = new List<Vector2[]>();
            public readonly List<Marking> Markings = new List<Marking>();
            // Entwurfsränder der Kreuzungsflächen (Bordsteinbögen, Knotenrundungen), die zu keinem Streifenrand gehören; Weltkoordinaten
            public readonly List<Vector2[]> Rims = new List<Vector2[]>();
            // Punkte, die die Umrissglättung (Ecken, Sprünge, Zähne, Nasen) neu gesetzt bzw. als Enden der Ersatzkante behalten hat; Weltkoordinaten
            public readonly List<Vector2> Smoothed = new List<Vector2>();
            public float LaneLineM, CenterLineM, EdgeLineM; public int StopLines, YieldLines, FailedTiles;
        }

        // Ein Markierungsviereck (Weltkoordinaten x/z): P0,P1 an der ersten Probe (links, rechts), P2,P3 an der zweiten
        public sealed class Marking { public byte Kind; public int Seg; public Vector2[] P; }
        public const byte MarkEdge = 0, MarkCenter = 1, MarkLane = 2, MarkBar = 3;

        // Dreiecke in der Ebene (x, z) mit Rasterindex — Punkt-in-Fläche-Abfragen für Prüfungen
        public sealed class Coverage
        {
            private const float C = 10f;
            private readonly List<Vector2> t = new List<Vector2>();
            private readonly Dictionary<long, List<int>> map = new Dictionary<long, List<int>>();
            public int Count => t.Count / 3;
            public void Get(int i, out Vector2 a, out Vector2 b, out Vector2 c) { a = t[3 * i]; b = t[3 * i + 1]; c = t[3 * i + 2]; }
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
            public int CentroidsInside(Coverage other, List<Vector2> examples = null)
            {
                int n = 0;
                for (int i = 0; i + 2 < t.Count; i += 3)
                {
                    Vector2 c = (t[i] + t[i + 1] + t[i + 2]) / 3f;
                    if (other.Inside(c.x, c.y, -.01f)) { n++; if (examples != null && examples.Count < 6) examples.Add(c); }   // mindestens 1 cm tief in der Fläche
                }
                return n;
            }

            // Punkt in der Fläche? Doppelte Genauigkeit und 2 mm Toleranz: Punkte genau auf Ecken/Kanten (Knoten liegen auf
            // Dreiecksecken der Kreuzungsflächen) fielen sonst durch das Vorzeichenrauschen der Float-Koordinaten
            public bool Inside(float x, float z, float tol = .002f)
            {
                if (!map.TryGetValue(K(Mathf.FloorToInt(x / C), Mathf.FloorToInt(z / C)), out var l)) return false;
                foreach (int i in l)
                {
                    Vector2 a = t[3 * i], b = t[3 * i + 1], c = t[3 * i + 2];
                    double a2 = ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)c.x - a.x) * ((double)b.y - a.y);
                    if (a2 == 0.0) continue;
                    double sg = a2 > 0.0 ? 1.0 : -1.0;
                    if (Edge(a, b, x, z, sg) >= -tol && Edge(b, c, x, z, sg) >= -tol && Edge(c, a, x, z, sg) >= -tol) return true;
                }
                return false;
            }
            // vorzeichenbehafteter Abstand des Punktes zur Kante p->q (innen positiv)
            private static double Edge(Vector2 p, Vector2 q, double x, double z, double sg)
            {
                double ex = (double)q.x - p.x, ez = (double)q.y - p.y, len = System.Math.Sqrt(ex * ex + ez * ez);
                return len < 1e-12 ? 0.0 : sg * (ex * (z - p.y) - ez * (x - p.x)) / len;
            }
        }

        private static Result rec;

        public static Result Build(RoadNet net, Transform parent, RoadMaterials mats, System.Func<Mesh, Mesh> save,
                                 System.Func<float, bool> progress = null, List<Vector2[]> externalJunctions = null)
        {
            rec = new Result();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var asphalt = new List<Vector2[]>(); var junctions = new List<Vector2[]>();
            var samples = new List<RoadField.Sample>();
            var ctx = new Ctx { Urban = new List<bool>(), LK = new List<byte>(), RK = new List<byte>(), Tags = new EdgeTags() };
            foreach (var sg in net.Segs)
            {
                for (int k = 0; k < sg.S.Count; k++)
                { samples.Add(sg.S[k]); ctx.Urban.Add(sg.Urban[k]); ctx.LK.Add(sg.LeftKind[k]); ctx.RK.Add(sg.RightKind[k]); }
                Strips(sg, 0, sg.S.Count - 1, k => 0f, k => 0f, Vector2.zero, asphalt);
                TagStrip(sg, ctx.Tags);
            }
            ctx.Near = new RoadField(samples);
            int fillets = 0, o2sUsed = 0;
            var centers = new List<Vector3>();
            foreach (var j in net.Junctions)
            {
                centers.Add(new Vector3(j.Center.x, 0f, j.Center.y));
                ctx.ZoneC.Add(j.Center); ctx.ZoneR.Add(j.Radius + 8f); ctx.ZoneRMax = Mathf.Max(ctx.ZoneRMax, j.Radius + 8f);
            }
            if (centers.Count > 0) ctx.ZoneHash = new RoadNet.PointHash(centers, 20f);
            if (externalJunctions != null)
            {
                // nur Kreuzungsflächen an echten Kreuzungen unseres Netzes; osm2streets legt auch an Knoten mit bloßem
                // Tag-Wechsel "Kreuzungen" an, deren (osm2streets-)Breite in Mittelstreifen ragen kann
                foreach (var poly in externalJunctions)
                {
                    Vector2 c = Vector2.zero; foreach (var q in poly) c += q; c /= poly.Length;
                    float rad = 0f; foreach (var q in poly) rad = Mathf.Max(rad, (q - c).magnitude);
                    if (ctx.ZoneHash != null && ctx.ZoneHash.Nearest(c, rad + 8f, out _) < float.MaxValue) { junctions.Add(poly); o2sUsed++; }
                }
            }
            else foreach (var j in net.Junctions) fillets += AddFillets(net, j, junctions, ctx.Tags, ctx.Near, rec.Rims);
            for (int ni = 0; ni < net.Nodes.Count; ni++) AddHub(net, ni, junctions, rec.Rims);

            // Kacheln: welche Stücke berühren welche Kachel inkl. Rand (über die Hüllrechtecke)
            var tiles = new Dictionary<long, TileSet>();
            Index(asphalt, 0, tiles); Index(junctions, 2, tiles);
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
                    var a = Clip(asphalt, ts.A, o, hm); var jn = Clip(junctions, ts.J, o, hm);
                    triCount += ProcessTile(a, jn, o, ctx, PartsAt, ref curbs);
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"Straßenoberfläche: Kachel {tx}/{tz} übersprungen ({ex.GetType().Name}: {ex.Message})");
                }
            }

            // Markierungen aus dem Spurmodell: je Abschnitt bis zum Kreuzungsbeginn (Beschnitt), nie in Kreuzungen; jedes Stück
            // wird gegen die fertige Fläche geprüft (auf eigener Fahrbahn, nicht in einer Kreuzung, nicht auf fremder Fahrbahn,
            // Randlinie nie über eine Einmündung) — siehe MarkOk
            mkAsphalt = rec.Asphalt; mkIndex = new StripIndex(net);
            for (int si = 0; si < net.Segs.Count; si++)
            {
                var sg = net.Segs[si];
                if (sg.Internal) continue;
                float s0 = sg.TrimA, s1 = sg.Length - sg.TrimB;
                if (s1 - s0 < 2f) continue;
                var sub = new List<RoadField.Sample>(); var ks = new List<int>();
                sub.Add(RoadNet.At(sg, s0)); ks.Add(RoadNet.SampleAt(sg, s0));
                for (int i = 0; i < sg.S.Count; i++) if (sg.S[i].distance > s0 + .2f && sg.S[i].distance < s1 - .2f) { sub.Add(sg.S[i]); ks.Add(i); }
                sub.Add(RoadNet.At(sg, s1)); ks.Add(RoadNet.SampleAt(sg, s1));
                EmitLaneMarkings(PartsAt(sub[sub.Count / 2].pos), sg, si, sub, ks);
            }
            if (net.Rules.stopLines) EmitStopLines(net, PartsAt);
            mkAsphalt = null; mkIndex = null;

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
            Debug.Log($"Straßenoberfläche{state}: {asphalt.Count} Fahrbahnstücke + {src}, " +
                      $"{keys.Count} Kacheln, {triCount} Dreiecke, {curbs} Bordsteinkanten, {meshes} Meshes, {clock.ElapsedMilliseconds} ms.");
            var res = rec; rec = null;
            return res;
        }

        // Markierungsprüfung (nur während Build): Asphalt der fertigen Fläche + Streifenindex des Netzes
        private static Coverage mkAsphalt; private static StripIndex mkIndex;

        // Ein Markierungsviereck (Ecken q, x/z) ist zulässig, wenn es vollständig auf Asphalt liegt, nicht auf der Fahrbahn
        // eines ANDEREN Abschnitts und nicht im Kreuzungsanteil (Abschnittsende/Verbindungsstück) irgendeines Abschnitts.
        // 'outward' (nur Randlinien): Punkt knapp außerhalb des Fahrbahnrands — dort darf kein Asphalt liegen (sonst läuft die
        // Randlinie über eine Einmündung oder zwischen überlappenden Fahrbahnen).
        private static bool MarkOk(int seg, Vector3[] q, bool hasOut, Vector2 outward)
        {
            if (mkIndex == null) return true;
            Vector2 c = Vector2.zero;
            foreach (var v in q) { c.x += v.x; c.y += v.z; }
            c *= .25f;
            var pts = new Vector2[5]; pts[4] = c;
            for (int i = 0; i < 4; i++)
            {
                var p = new Vector2(q[i].x, q[i].z);
                if (!mkAsphalt.Inside(p.x, p.y)) return false;
                pts[i] = c + (p - c) * .9f;
            }
            foreach (var p in pts)
            {
                if (mkIndex.Hit(p, 0f, seg, out _, out _)) return false;
                if (mkIndex.JunctionPart(p, 0f)) return false;
            }
            if (hasOut && mkAsphalt.Inside(outward.x, outward.y)) return false;
            return true;
        }

        // ------------------------------------------------------------------ Markierungen (Südafrika)
        //   gelb durchgezogen: Randlinie außerorts an der Fahrstreifenkante; an Doppelfahrbahnen auch zur Mittelinsel
        //   weiß unterbrochen: Mittellinie zwischen den Richtungen (3 m / 9 m), Spurtrenner gleicher Richtung (3 m / 6 m)
        private static void EmitLaneMarkings(RoadProfile.Parts p, RoadNet.Segment sg, int si, List<RoadField.Sample> s, List<int> ks)
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
                rec.EdgeLineM += Line(p, s, i => sgn * (s[i].half - ins(i) - .125f), .15f, 2, 0f, 0f, i => on(i), si, MarkEdge, sgn);
            }
            // weiße Linien: Mittellinie + Spurtrenner
            int maxN = 0; foreach (int k in ks) maxN = Mathf.Max(maxN, sg.Lanes[k].N);
            for (int b = 1; b < maxN; b++)
            {
                int bb = b;
                System.Func<int, bool> isCenter = i => sg.Lanes[ks[i]].Center && sg.Lanes[ks[i]].L == bb;
                System.Func<int, bool> isLane = i => bb < sg.Lanes[ks[i]].N && !(sg.Lanes[ks[i]].Dir == 0 && sg.Lanes[ks[i]].L == bb);
                System.Func<int, float> x = i => CwL(i) + Cw(i) * bb / Mathf.Max(1, sg.Lanes[ks[i]].N);
                rec.CenterLineM += Line(p, s, x, .14f, 3, 3f, 9f, i => i + 1 < n && Same(i, i + 1) && isCenter(i), si, MarkCenter);
                rec.LaneLineM += Line(p, s, x, .12f, 3, 3f, 6f, i => i + 1 < n && Same(i, i + 1) && isLane(i) && !sg.Fallback, si, MarkLane);
            }
        }

        // Längsstreifen: x(i) = Mitte des Streifens quer zur Fahrbahn (rechts positiv), Breite w; gibt die Länge zurück.
        // outSign != 0 (Randlinien): Seite (+1 rechts, -1 links), auf der außerhalb des Fahrbahnrands kein Asphalt liegen darf.
        private static float Line(RoadProfile.Parts p, List<RoadField.Sample> s, System.Func<int, float> x, float w, int sub,
                                  float dashOn, float period, System.Func<int, bool> allowed, int seg, byte kind, float outSign = 0f)
        {
            float len = 0f;
            var q4 = new Vector3[4];
            for (int i = 0; i + 1 < s.Count; i++)
            {
                if (!allowed(i) || !allowed(i + 1)) continue;
                if (dashOn > 0f && Mathf.Repeat(s[i].distance, period) >= dashOn) continue;
                for (int k = 0; k < 2; k++)
                {
                    var q = s[i + k]; float c = x(i + k);
                    q4[2 * k] = q.pos + q.side * (c - w * .5f) + Vector3.up * .035f;
                    q4[2 * k + 1] = q.pos + q.side * (c + w * .5f) + Vector3.up * .035f;
                }
                if (mkIndex != null)
                {
                    Vector2 outward = Vector2.zero;
                    if (outSign != 0f)
                    {
                        Vector3 mid = (s[i].pos + s[i + 1].pos) * .5f, sd = (s[i].side + s[i + 1].side).normalized;
                        float hf = (s[i].half + s[i + 1].half) * .5f + .2f;
                        outward = new Vector2(mid.x + sd.x * outSign * hf, mid.z + sd.z * outSign * hf);
                    }
                    if (!MarkOk(seg, q4, outSign != 0f, outward)) continue;
                }
                int b = p.V.Count;
                for (int k = 0; k < 4; k++) { p.V.Add(q4[k]); p.N.Add(Vector3.up); p.UV.Add(Vector2.zero); }
                RoadProfile.Quad(p.T[sub], b, b + 1, b + 2, b + 3);
                if (rec != null) rec.Markings.Add(new Marking { Kind = kind, Seg = seg, P = new[] { new Vector2(q4[0].x, q4[0].z), new Vector2(q4[1].x, q4[1].z),
                                                                                          new Vector2(q4[2].x, q4[2].z), new Vector2(q4[3].x, q4[3].z) } });
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
                    // Die Linie liegt knapp hinter dem Beschnitt; an Stellen, an denen dort noch ein Kreuzungsanteil (Probe im Beschnitt) liegt,
                    // rückt sie in 0,5-m-Schritten bis 2,25 m nach außen, bis sie zulässig ist
                    for (float off = .25f; off <= 2.3f; off += .5f)
                    {
                        float at = e.AtA ? e.Trim + off : sg.Length - e.Trim - off;
                        if (at < .5f || at > sg.Length - .5f) break;
                        var q = RoadNet.At(sg, at);
                        float cwl = -q.half + li.Sh, cw = 2f * (q.half - li.Sh);
                        if (cw < 2f || li.N == 0) break;
                        float x0, x1;
                        if (li.Dir == 0) { float div = cwl + cw * li.L / li.N; if (e.AtA) { x0 = div; x1 = cwl + cw; } else { x0 = cwl; x1 = div; } }
                        else if ((li.Dir > 0) == !e.AtA) { x0 = cwl; x1 = cwl + cw; }
                        else break;                                                       // Einbahn verlässt hier die Kreuzung
                        if (x1 - x0 < 1f) break;
                        var p = PartsAt(q.pos);
                        Vector3 t = new Vector3(q.tangent.x, 0f, q.tangent.z).normalized;
                        float depth = yield ? .3f : .45f;
                        bool any = false;
                        if (yield) { for (float xa = x0 + .1f; xa + .6f <= x1; xa += 1f) any |= CrossBar(p, q, t, xa, xa + .6f, depth, e.Seg); if (any) rec.YieldLines++; }
                        else if (CrossBar(p, q, t, x0 + .1f, x1 - .1f, depth, e.Seg)) { any = true; rec.StopLines++; }
                        if (any) break;
                    }
                }
            }
        }

        private static bool CrossBar(RoadProfile.Parts p, RoadField.Sample q, Vector3 t, float x0, float x1, float depth, int seg)
        {
            Vector3 up = Vector3.up * .036f, d = t * (depth * .5f);
            var q4 = new[] { q.pos + q.side * x0 - d + up, q.pos + q.side * x1 - d + up, q.pos + q.side * x0 + d + up, q.pos + q.side * x1 + d + up };
            if (mkIndex != null && !MarkOk(seg, new[] { q4[0], q4[1], q4[3], q4[2] }, false, Vector2.zero)) return false;
            int b = p.V.Count;
            for (int k = 0; k < 4; k++) { p.V.Add(q4[k]); p.N.Add(Vector3.up); p.UV.Add(Vector2.zero); }
            RoadProfile.Quad(p.T[3], b, b + 1, b + 2, b + 3);
            if (rec != null) rec.Markings.Add(new Marking { Kind = MarkBar, Seg = seg, P = new[] { new Vector2(q4[0].x, q4[0].z), new Vector2(q4[1].x, q4[1].z),
                                                                                       new Vector2(q4[2].x, q4[2].z), new Vector2(q4[3].x, q4[3].z) } });
            return true;
        }

        private sealed class TileSet { public readonly List<int> A = new List<int>(), J = new List<int>(); }

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
                    (kind == 0 ? ts.A : ts.J).Add(i);
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
                    // Schnittpunkt unabhängig von der Laufrichtung der Kante (kleinerer Endpunkt zuerst): zwei Stücke, die sich eine Kante
                    // teilen (Streifen an ihrer gemeinsamen Stirnkante), erhalten am Kachelrand bitgleiche Punkte -- sonst bleibt ein
                    // Haarspalt von ~1e-5 m entlang der ganzen Kante, an dem die Vereinigung nicht verschmilzt
                    Vector2 lo = a, hi = b; if (lo.x > hi.x || (lo.x == hi.x && lo.y > hi.y)) { lo = b; hi = a; }
                    float t = side < 2 ? (v - lo.x) / (hi.x - lo.x) : (v - lo.y) / (hi.y - lo.y);
                    var q = lo + (hi - lo) * t;
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

        private static long ProcessTile(List<Vector2[]> stripsIn, List<Vector2[]> junctionsIn, Vector2 o, Ctx ctx,
                                        System.Func<Vector3, RoadProfile.Parts> PartsAt, ref int curbs)
        {
            float hm = Tile * .5f + Margin, h = Tile * .5f;

            // 1) Asphalt = Vereinigung der Streifen und Kreuzungsstücke. Splitter (Fläche ~0, von LibTess gelegentlich
            //    ausgegeben) und Stachel werden entfernt; kleine Löcher IN Kreuzungen (nicht ausgerundete Ecken zwischen
            //    Zufahrten) werden gefüllt.
            var all0 = new List<Vector2[]>(stripsIn); all0.AddRange(junctionsIn);
            var asp = MergeMicroEdges(FillSmallJunctionHoles(Clean(Contours(all0, WindingRule.NonZero), hm), o, ctx), hm, o, ctx);

            // 2) Gehweg/Randstreifen: Randkanten nach Herkunft klassifizieren, jede Klasse für sich versetzen, Vorrang beachten
            var loops = new List<ClsLoop>(asp.Count);
            foreach (var c in asp) loops.Add(ClassifyLoop(c, o, ctx, hm));
            var region = new List<Vector2[]>[4];
            var covered = Reverse(asp);
            for (byte cl = 0; cl < 4; cl++)
            {
                var pieces = new List<Vector2[]>();
                foreach (var l in loops) BandPieces(l, cl, pieces);
                if (pieces.Count == 0) continue;
                var un = Contours(pieces, WindingRule.NonZero);
                var d = new List<Vector2[]>(un); d.AddRange(covered);
                region[cl] = DropTinyHoles(Clean(Contours(d, WindingRule.Positive, false), hm), .6f);   // Klasse − Asphalt − Klassen mit Vorrang
                covered.AddRange(Reverse(un));
            }

            // 3) exakt auf die Kachel beschneiden (Schnitt mit dem Kachelquadrat: Umlaufzahl 2)
            var rect = new[] { new Vector2(-h, -h), new Vector2(h, -h), new Vector2(h, h), new Vector2(-h, h) };
            var aIn = new List<Vector2[]>(asp) { rect };
            var aTileC = Contours(aIn, WindingRule.AbsGeqTwo, false);
            var asphaltTris = Refine(Triangles(aIn, new List<Vector2[]>(), WindingRule.AbsGeqTwo), 15f, h);
            var cls = new List<Vector2>[4];
            var oIn = new List<Vector2[]>(asp);
            for (int cl = 0; cl < 4; cl++)
            {
                cls[cl] = new List<Vector2>();
                if (region[cl] == null || region[cl].Count == 0) continue;
                var bin = new List<Vector2[]>(region[cl]) { rect };
                cls[cl] = Refine(Triangles(bin, new List<Vector2[]>(), WindingRule.AbsGeqTwo), 15f, h);
                oIn.AddRange(region[cl]);
            }
            oIn.Add(rect);
            var outline = Contours(oIn, WindingRule.AbsGeqTwo, false);
            // Gehweg = Klassen 0 und 2 (Ortslage), Randstreifen = Klassen 1 und 3
            var urbanTris = new List<Vector2>(cls[ClsUrban]); urbanTris.AddRange(cls[ClsKerbUrban]);
            var ruralTris = new List<Vector2>(cls[ClsRural]); ruralTris.AddRange(cls[ClsKerbRural]);

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
                // aufgezeichnet wird, was auch als Mesh ausgegeben wird (EmitTris verwirft im 1-cm-Raster entartete Dreiecke)
                foreach (var t3 in Emitted(asphaltTris, o)) rec.Asphalt.Add(t3.Item1 + o, t3.Item2 + o, t3.Item3 + o);
                foreach (var t3 in Emitted(urbanTris, o)) { rec.Band.Add(t3.Item1 + o, t3.Item2 + o, t3.Item3 + o); rec.Urban.Add(t3.Item1 + o, t3.Item2 + o, t3.Item3 + o); }
                foreach (var t3 in Emitted(ruralTris, o)) { rec.Band.Add(t3.Item1 + o, t3.Item2 + o, t3.Item3 + o); rec.Rural.Add(t3.Item1 + o, t3.Item2 + o, t3.Item3 + o); }
                RecordLoops(aTileC, rec.AsphaltLoops, o);
                RecordLoops(TileLoops(region[ClsUrban], region[ClsKerbUrban], rect), rec.UrbanLoops, o);
                RecordLoops(TileLoops(region[ClsRural], region[ClsKerbRural], rect), rec.RuralLoops, o);
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
                    byte k = ClassAt((a2 + b2) * .5f, o, ctx);
                    if (k != ClsUrban && k != ClsKerbUrban) continue;
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

        private static void RecordLoops(List<Vector2[]> loops, List<Vector2[]> dst, Vector2 o)
        {
            foreach (var c in loops)
            {
                if (c.Length < 3) continue;
                var w = new Vector2[c.Length]; for (int i = 0; i < c.Length; i++) w[i] = c[i] + o;
                dst.Add(w);
            }
        }

        // Umrisse der (auf die Kachel beschnittenen) Bandflächen zweier Klassen
        private static List<Vector2[]> TileLoops(List<Vector2[]> a, List<Vector2[]> b, Vector2[] rect)
        {
            var l = new List<Vector2[]>();
            if (a != null) l.AddRange(a);
            if (b != null) l.AddRange(b);
            if (l.Count == 0) return l;
            l.Add(rect);
            return Contours(l, WindingRule.AbsGeqTwo, false);
        }

        // Winzige Löcher (< maxArea) in Bandflächen (Grasfleck von 0,2 m² mitten im Gehweg einer Verkehrsinsel) schließen
        private static List<Vector2[]> DropTinyHoles(List<Vector2[]> loops, float maxArea)
        {
            var res = new List<Vector2[]>(loops.Count);
            foreach (var c in loops)
            {
                float a2 = 0f;
                for (int i = 0, j = c.Length - 1; i < c.Length; j = i++) a2 += c[j].x * c[i].y - c[i].x * c[j].y;
                if (a2 < 0f && -a2 * .5f < maxArea) continue;
                res.Add(c);
            }
            return res;
        }

        // Mikrokanten (< 30 cm: Versatz zwischen Streifenrand und Ausrundung bzw. zwischen zwei Streifen) zu einem Punkt verschmelzen
        // (Umriss verschiebt sich um höchstens 15 cm):
        // sonst entsteht an ihnen ein Fächer mit Umlaufwinkel bis 180 ° und der Übergang wird ein Kreisbogen statt einer Geraden.
        private const float Micro = .3f;
        private static List<Vector2[]> MergeMicroEdges(List<Vector2[]> loops, float hm, Vector2 o, Ctx ctx)
        {
            var res = new List<Vector2[]>(loops.Count);
            foreach (var c in loops)
            {
                var l = new List<Vector2>(c);
                for (int pass = 0; pass < 8 && l.Count > 3; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < l.Count && l.Count > 3;)
                    {
                        int j = (i + 1) % l.Count;
                        Vector2 a = l[i], b = l[j];
                        bool border = Mathf.Abs(Mathf.Abs(a.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(a.y) - hm) < 1e-2f ||
                                      Mathf.Abs(Mathf.Abs(b.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(b.y) - hm) < 1e-2f;
                        if (!border && (a - b).sqrMagnitude < Micro * Micro)
                        {
                            l[i] = (a + b) * .5f; l.RemoveAt(j);
                            if (j < i) i--;
                            changed = true;
                        }
                        else i++;
                    }
                    if (!changed) break;
                }
                // Haarrisse: zwei fast parallele lange Kanten, die in einem Punkt zusammenlaufen (Öffnung < 30 cm, Spitzenwinkel < 10 °),
                // entstehen, wo zwei Teilflächen nicht exakt aneinander stoßen; der Keil wird geschlossen (Punkt entfernen)
                for (int pass = 0; pass < 8 && l.Count > 3; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < l.Count && l.Count > 3;)
                    {
                        Vector2 a = l[(i + l.Count - 1) % l.Count], v = l[i], b = l[(i + 1) % l.Count];
                        Vector2 va = a - v, vb = b - v; float la = va.magnitude, lb = vb.magnitude;
                        bool border = Mathf.Abs(Mathf.Abs(v.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(v.y) - hm) < 1e-2f;
                        if (!border && la > 1f && lb > 1f && (a - b).magnitude < Micro &&
                            Vector2.Dot(va, vb) > Mathf.Cos(10f * Mathf.Deg2Rad) * la * lb) { l.RemoveAt(i); changed = true; }
                        else i++;
                    }
                    if (!changed) break;
                }
                // Zacken: ein Punkt, dessen beide Kanten kürzer als 1 m sind und der den Umriss um mehr als 120 ° zurückwendet
                // (Fähnchen/Kerbe von < 1 m an der Naht zwischen Streifenrand und Ausrundung) -> Punkt entfernen
                for (int pass = 0; pass < 8 && l.Count > 3; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < l.Count && l.Count > 3;)
                    {
                        Vector2 a = l[(i + l.Count - 1) % l.Count], v = l[i], b = l[(i + 1) % l.Count];
                        Vector2 d0 = v - a, d1 = b - v; float l0 = d0.magnitude, l1 = d1.magnitude;
                        bool border = Mathf.Abs(Mathf.Abs(v.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(v.y) - hm) < 1e-2f;
                        if (!border && l0 > 1e-4f && l1 > 1e-4f && l0 < 1f && l1 < 1f && Vector2.Dot(d0, d1) < -.5f * l0 * l1) { l.RemoveAt(i); changed = true; }
                        else i++;
                    }
                    if (!changed) break;
                }
                // Schlitze: eine U-förmige Kerbe von < 1 m Grund und < 1,3 m Öffnung (zwei antiparallele Kanten < 4 m, beide Ecken
                // Rechtsknicke = Nichtasphalt) ist eine Fuge zwischen zwei Streifenenden, kein Zwischenraum -> schließen
                for (int pass = 0; pass < 4 && l.Count > 5; pass++)
                {
                    bool changed = false;
                    for (int i = 0; i < l.Count && l.Count > 5; i++)
                    {
                        Vector2 v0 = l[i], v1 = l[(i + 1) % l.Count], v2 = l[(i + 2) % l.Count], v3 = l[(i + 3) % l.Count];
                        Vector2 e1 = v1 - v0, e2 = v2 - v1, e3 = v3 - v2; float l1 = e1.magnitude, l2 = e2.magnitude, l3 = e3.magnitude;
                        if (l1 < 5e-2f || l2 < 5e-2f || l3 < 5e-2f || l2 >= SlitMaxWidth || l1 >= 4f || l3 >= 4f || (v3 - v0).magnitude >= SlitMaxWidth + .3f) continue;
                        if (e1.x * e2.y - e1.y * e2.x >= 0f || e2.x * e3.y - e2.y * e3.x >= 0f || Vector2.Dot(e1, e3) > -.6f * l1 * l3) continue;
                        if (Mathf.Abs(Mathf.Abs(v1.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(v1.y) - hm) < 1e-2f ||
                            Mathf.Abs(Mathf.Abs(v2.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(v2.y) - hm) < 1e-2f) continue;
                        int i1 = (i + 1) % l.Count, i2 = (i + 2) % l.Count;
                        if (i1 > i2) { l.RemoveAt(i1); l.RemoveAt(i2); } else { l.RemoveAt(i2); l.RemoveAt(i1); }
                        changed = true; break;
                    }
                    if (!changed) break;
                }
                // Taschen: eine schmale Tasche im Umriss (aufeinanderfolgende Rechtsknicke mit Summe >= 140 °, Weg < 9 m, Sehne der
                // Randpunkte < 3 m, beide Randpunkte biegen nach außen ab) ist ein Rest zwischen Streifenende, Ausrundung und
                // Knotenscheibe an einer Kreuzungsecke -- sie würde als Zacke aus Gehweg/Randstreifen in der Fahrbahn stehen. Zuschütten.
                for (int pass = 0; pass < 8 && l.Count > 6; pass++)
                {
                    int p0 = 0, p1 = 0;
                    if (!FindPocket(l, hm, out p0, out p1)) break;
                    if (p0 <= p1) l.RemoveRange(p0, p1 - p0 + 1);
                    else { l.RemoveRange(p0, l.Count - p0); l.RemoveRange(0, p1 + 1); }
                }
                RemoveTeeth(l, hm, o);
                RemoveHooks(l, hm, o, ctx);
                RoundCorners(l, hm, o, ctx);
                RoundNoses(l, hm, o);
                RoundKinks(l, hm, o, ctx);
                res.Add(l.ToArray());
            }
            return res;
        }

        public const float SlitMaxWidth = 1f;      // Schlitz (Fuge zwischen Streifenenden) bis 1 m Breite und 4 m Tiefe wird geschlossen, Mittelstreifen sind >= 1,4 m
        public const float PocketCum = 140f, PocketMaxPath = 9f, PocketMaxChord = 3f, PocketFlare = 40f;

        // Tasche im Umriss suchen (Umlaufsinn: Fläche links, Rechtsknick = Nichtasphalt); liefert den Index-Bereich [first..last]
        // der zu entfernenden Punkte (kann über das Listenende hinausreichen: dann first > last). Kachelrandpunkte zählen nicht.
        private static bool FindPocket(List<Vector2> l, float hm, out int first, out int last)
        {
            int n = l.Count; first = last = 0;
            var turn = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 d0 = l[i] - l[(i + n - 1) % n], d1 = l[(i + 1) % n] - l[i];
                turn[i] = d0.sqrMagnitude < 1e-8f || d1.sqrMagnitude < 1e-8f ? 0f
                        : Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1)) * Mathf.Rad2Deg;
            }
            for (int i = 0; i < n; i++)
            {
                if (turn[i] > -5f) continue;
                float sum = 0f;
                for (int k = 0; k < 6 && k < n - 4; k++)
                {
                    int j = (i + k) % n;
                    if (turn[j] > -1f) break;
                    sum += turn[j];
                    if (sum > -PocketCum) continue;
                    int a = (i + n - 1) % n, b = (j + 1) % n;
                    if (turn[a] < PocketFlare || turn[b] < PocketFlare) break;
                    if ((l[a] - l[b]).magnitude >= PocketMaxChord) break;
                    float path = 0f; bool border = false;
                    for (int m = a; ; m = (m + 1) % n)
                    {
                        int m2 = (m + 1) % n; path += (l[m2] - l[m]).magnitude;
                        if (Mathf.Abs(Mathf.Abs(l[m].x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(l[m].y) - hm) < 1e-2f) border = true;
                        if (m2 == b) { if (Mathf.Abs(Mathf.Abs(l[b].x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(l[b].y) - hm) < 1e-2f) border = true; break; }
                    }
                    if (border || path >= PocketMaxPath) break;
                    first = i; last = j; return true;
                }
            }
            return false;
        }


        // ------------------------------------------------------------------ Nasen und Ecken
        private static void Mark(Vector2 worldPoint) { if (rec != null) rec.Smoothed.Add(worldPoint); }
        private static bool AtTileEdge(Vector2 p, float hm) => Mathf.Abs(Mathf.Abs(p.x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(p.y) - hm) < 1e-2f;

        private static float TurnAt(List<Vector2> l, int i)
        {
            int n = l.Count; Vector2 d0 = l[i] - l[(i + n - 1) % n], d1 = l[(i + 1) % n] - l[i];
            if (d0.sqrMagnitude < 1e-8f || d1.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Atan2(d0.x * d1.y - d0.y * d1.x, Vector2.Dot(d0, d1)) * Mathf.Rad2Deg;
        }

        private static bool InJunctionZone(Ctx ctx, Vector2 local, Vector2 o)
        {
            if (ctx.ZoneHash == null) return false;
            Vector2 w = local + o;
            foreach (int zi in ctx.ZoneHash.WithinIdx(w, ctx.ZoneRMax))
                if ((ctx.ZoneC[zi] - w).magnitude <= ctx.ZoneR[zi]) return true;
            return false;
        }

        // Zähne: ein kleiner Asphaltzahn (Linksknick >= 50 ° zwischen zwei Rechtsknicken, Höhe über der Grundlinie <= 1,3 m, Grundlinie <= 4,5 m)
        // im sonst glatten Rand -- Rest einer Knotenscheibe/eines Streifenendes am Inselrand -- wird abgeschnitten. Er würde als Höcker
        // des Randstreifens/Gehwegs auf der Insel stehen.
        public const float ToothMinTurn = 50f, ToothMaxHeight = 1.3f, ToothMaxBase = 4.5f, ToothFlank = 15f;
        private static void RemoveTeeth(List<Vector2> l, float hm, Vector2 o)
        {
            for (int pass = 0; pass < 16 && l.Count > 6; pass++)
            {
                bool changed = false;
                for (int i = 0; i < l.Count && l.Count > 6;)
                {
                    int n = l.Count, ia = (i + n - 1) % n, ib = (i + 1) % n;
                    Vector2 a = l[ia], v = l[i], b = l[ib];
                    Vector2 ab = b - a; float bl = ab.magnitude;
                    if (bl > 1e-4f && bl <= ToothMaxBase && TurnAt(l, i) >= ToothMinTurn && TurnAt(l, ia) <= -ToothFlank && TurnAt(l, ib) <= -ToothFlank &&
                        Mathf.Abs(ab.x * (v.y - a.y) - ab.y * (v.x - a.x)) / bl <= ToothMaxHeight &&
                        !AtTileEdge(a, hm) && !AtTileEdge(v, hm) && !AtTileEdge(b, hm)) { Mark(a + o); Mark(b + o); l.RemoveAt(i); changed = true; }
                    else i++;
                }
                if (!changed) break;
            }
        }

        // Haken und Nadeln: Kleinstformen von < 1 m an den Nähten zwischen Streifenenden, Ausrundungen und Knotenscheiben.
        //  - Nadel: ein Umkehrpunkt (Knick >= 150 °) mit einer Kante < 0,8 m und der anderen < 3 m entfällt.
        //  - Haken: ein Verbindungsstück <= 1,5 m zwischen einem Links- und einem Rechtsknick von je >= 60 ° (Streifenende mit Versatz zur Nachbarkante).
        //    Laufen die Randkanten davor/dahinter etwa parallel (Summe der Knicke < 15 °), werden sie durch eine flache Diagonale verbunden (Versatz
        //    <= 1,8 m, wie beim Sprung), sonst durch ihren Schnittpunkt (Gehrung), sofern der Schnittpunkt <= 3 m entfernt liegt und die Fläche
        //    des Dreiecks <= 2 m² ist (die Kerbe zwischen zwei Streifenenden wird zugeschüttet).
        public const float NeedleTurn = 150f, NeedleShort = .8f, NeedleLong = 3f;
        public const float HookMaxEdge = 1.5f, HookMinTurn = 60f, HookReach = 3f, HookMinArm = .4f, HookJogArm = 1.5f, HookParallel = 15f, HookMaxArea = 2f;
        private static void RemoveHooks(List<Vector2> l, float hm, Vector2 o, Ctx ctx)
        {
            for (int pass = 0; pass < 24 && l.Count > 8; pass++)
            {
                bool changed = false;
                for (int i = 0; i < l.Count && l.Count > 8 && !changed; i++)
                {
                    int n = l.Count, ip = (i + n - 1) % n, j = (i + 1) % n, jn = (i + 2) % n;
                    if (AtTileEdge(l[i], hm) || AtTileEdge(l[j], hm) || AtTileEdge(l[ip], hm) || AtTileEdge(l[jn], hm)) continue;
                    Vector2 A = l[i] - l[ip], E = l[j] - l[i], B = l[jn] - l[j];
                    float la = A.magnitude, e = E.magnitude, lb = B.magnitude;
                    float ti = TurnAt(l, i);
                    if (Mathf.Abs(ti) >= NeedleTurn && la > 1e-4f && e > 1e-4f && Mathf.Min(la, e) < NeedleShort && Mathf.Max(la, e) < NeedleLong)
                    { Mark(l[ip] + o); Mark(l[j] + o); l.RemoveAt(i); changed = true; continue; }
                    float tj = TurnAt(l, j);
                    if (e < 1e-3f || e > HookMaxEdge || la < HookMinArm || lb < HookMinArm) continue;
                    if (!((ti >= HookMinTurn && tj <= -HookMinTurn) || (ti <= -HookMinTurn && tj >= HookMinTurn))) continue;
                    Vector2 dA = A / la, dB = B / lb;
                    float net = Mathf.Abs(ti + tj);
                    if (net < HookParallel)
                    {
                        if (la < HookJogArm || lb < HookJogArm) continue;
                        if (TryRoundJog(l, i, j, 2, dA, dB, la, lb, o, ctx)) changed = true;
                        continue;
                    }
                    float cr = dA.x * dB.y - dA.y * dB.x; if (Mathf.Abs(cr) < 1e-3f) continue;
                    float s = (E.x * dB.y - E.y * dB.x) / cr, u = (E.x * dA.y - E.y * dA.x) / cr;
                    if (Mathf.Abs(s) > HookReach || Mathf.Abs(u) > HookReach || s < -la * .9f) continue;
                    Vector2 P = l[i] + dA * s;
                    // liegt der Schnittpunkt weiter hinten auf der Folgekante (u > 0), entfallen deren Punkte bis dorthin (Ausrundung der Nachbarkante)
                    int kEnd = j; float acc = 0f; bool ok = true;
                    while (u > 0f)
                    {
                        int kn = (kEnd + 1) % n; float el = (l[kn] - l[kEnd]).magnitude;
                        if (acc + el >= u) break;
                        if (AtTileEdge(l[kn], hm) || kn == i || Mathf.Abs(TurnAt(l, kn)) > 25f) { ok = false; break; }
                        acc += el; kEnd = kn;
                    }
                    if (!ok) continue;
                    var lobe = new List<Vector2> { l[i] };
                    for (int k = j; ; k = (k + 1) % n) { lobe.Add(l[k]); if (k == kEnd) break; }
                    lobe.Add(P);
                    float ar = 0f; for (int a = 0, b = lobe.Count - 1; a < lobe.Count; b = a++) ar += lobe[b].x * lobe[a].y - lobe[a].x * lobe[b].y;
                    if (Mathf.Abs(ar) * .5f > HookMaxArea || NearCentre(ctx, o, lobe)) continue;
                    int removed = ((kEnd - i + n) % n) + 1; if (removed > n - 5) continue;
                    var res = new List<Vector2>(n - removed + 1);
                    for (int k = (kEnd + 1) % n, m = 0; m < n - removed; k = (k + 1) % n, m++) res.Add(l[k]);
                    res.Add(P); l.Clear(); l.AddRange(res);
                    Mark(P + o); changed = true;
                }
                if (!changed) break;
            }
        }

        // Liegt ein Mittellinienpunkt (Probe des Netzes) in der Fläche 'lobe' (lokale Koordinaten) oder nahe an ihrem Rand? Dann ist die Fläche kein
        // Rest einer Naht, sondern Teil einer Fahrbahn (z. B. das Ende eines Schenkels) und bleibt unangetastet.
        public const float CentreGuard = .5f;
        private static bool NearCentre(Ctx ctx, Vector2 o, List<Vector2> lobe)
        {
            if (ctx == null || ctx.Near == null || lobe.Count < 3) return false;
            Vector2 mn = lobe[0], mx = lobe[0];
            foreach (var q in lobe) { mn = Vector2.Min(mn, q); mx = Vector2.Max(mx, q); }
            var ids = new List<int>(); ctx.Near.Query(mn.x + o.x - CentreGuard, mn.y + o.y - CentreGuard, mx.x + o.x + CentreGuard, mx.y + o.y + CentreGuard, ids);
            foreach (int si in ids)
            {
                var p3 = ctx.Near.Samples[si].pos; Vector2 p = new Vector2(p3.x, p3.z) - o;
                bool inside = false; float best = float.MaxValue;
                for (int a = 0, b = lobe.Count - 1; a < lobe.Count; b = a++)
                {
                    Vector2 va = lobe[a], vb = lobe[b];
                    if ((va.y > p.y) != (vb.y > p.y) && p.x < (vb.x - va.x) * (p.y - va.y) / (vb.y - va.y) + va.x) inside = !inside;
                    Vector2 d = vb - va; float l2 = d.sqrMagnitude;
                    float t = l2 < 1e-12f ? 0f : Mathf.Clamp01(Vector2.Dot(p - va, d) / l2);
                    best = Mathf.Min(best, (va + d * t - p).magnitude);
                }
                if (inside || best <= CentreGuard) return true;
            }
            return false;
        }

        // Nasen: das Ende eines Schlitzes im Umriss (Mittelstreifen: Endkante 0,5-8 m zwischen zwei etwa gegenläufigen Kanten, beide
        // Ecken Rechtsknicke von 55-125 °) wird gerundet: je Ecke ein Bogen mit Tangentenlänge = halbe Endkante (Halbkreis-Nase), die
        // Kantenpunkte innerhalb dieser Länge entfallen. Kachelrand und kurze Kanten (< 0,8 m) bleiben unberührt.
        public const float NoseMinWidth = .5f, NoseMaxWidth = 8f;

        private static void RoundNoses(List<Vector2> l, float hm, Vector2 o)
        {
            for (int pass = 0; pass < 24 && l.Count > 6; pass++)
            {
                bool changed = false;
                for (int i = 0; i < l.Count && !changed; i++)
                {
                    int n = l.Count, i0 = (i + n - 1) % n, i1 = (i + 1) % n, i2 = (i + 2) % n;
                    Vector2 e0 = l[i] - l[i0], e1 = l[i1] - l[i], e2 = l[i2] - l[i1];
                    float w = e1.magnitude; if (w < NoseMinWidth || w > NoseMaxWidth || e0.magnitude < .8f || e2.magnitude < .8f) continue;
                    if (Vector2.Dot(e0, e2) > -.5f * e0.magnitude * e2.magnitude) continue;
                    float t1 = TurnAt(l, i), t2 = TurnAt(l, i1);
                    if (t1 > -55f || t1 < -125f || t2 > -55f || t2 < -125f) continue;
                    if (AtTileEdge(l[i], hm) || AtTileEdge(l[i1], hm)) continue;
                    float f = w * .5f;
                    // Kantenpunkte innerhalb der Tangentenlänge entfallen, solange sie auf der Kante liegen (Knick < 8 °); ist die Kante
                    // vorher zu Ende (Knick), wird der Bogen dort kürzer (mindestens die halbe Tangentenlänge)
                    int kb = i0, kf = i2; float cb = e0.magnitude, cf = e2.magnitude; int rb = 0, rf = 0;
                    while (cb <= f + 1e-3f && rb < 12 && Mathf.Abs(TurnAt(l, kb)) <= 8f && !AtTileEdge(l[kb], hm)) { int kp = (kb + n - 1) % n; cb += (l[kb] - l[kp]).magnitude; kb = kp; rb++; }
                    while (cf <= f + 1e-3f && rf < 12 && Mathf.Abs(TurnAt(l, kf)) <= 8f && !AtTileEdge(l[kf], hm)) { int kn = (kf + 1) % n; cf += (l[kn] - l[kf]).magnitude; kf = kn; rf++; }
                    float f0 = Mathf.Min(f, cb * .95f), f1 = Mathf.Min(f, cf * .95f);
                    if (f0 < f * .5f || f1 < f * .5f || rb + rf + 4 >= n) continue;
                    var pts = new List<Vector2>();
                    AddCornerArc(pts, l[i], e0.normalized, e1.normalized, f0);
                    AddCornerArc(pts, l[i1], e1.normalized, e2.normalized, f1);
                    var res = new List<Vector2>(n);
                    for (int k = kf, m = 0; m < n - rb - rf - 2; k = (k + 1) % n, m++) res.Add(l[k]);
                    res.AddRange(pts); foreach (var q in pts) Mark(q + o);
                    l.Clear(); l.AddRange(res);
                    changed = true;
                }
                if (!changed) break;
            }
        }

        // Restliche Knicke: jede verbleibende Rechtsecke (Nichtasphalt-Ecke) zwischen 38 und 135 ° -- in Kreuzungszonen, bei Löchern (Mittelstreifen,
        // Inseln) überall -- wird einzeln mit einem Bogen gerundet. Tangentenlänge <= halbe Nachbarkante (jede Kante gibt jeder Ecke die Hälfte, die
        // Bögen überlappen so nie), Radius <= 3,5 m (Löcher 1,5 m), Abstand Bogenmitte-Ecke <= 2 m. Schärfere Spitzen (>= 135 °: zusammenlaufende Fahrbahnen)
        // bleiben.
        public const float KinkMax = 135f, KinkMaxDepth = 2f, KinkMinTangent = .12f;
        private static void RoundKinks(List<Vector2> l, float hm, Vector2 o, Ctx ctx)
        {
            int n = l.Count; if (n < 4) return;
            float a2 = 0f; for (int i = 0, j = n - 1; i < n; j = i++) a2 += l[j].x * l[i].y - l[i].x * l[j].y;
            bool hole = a2 < 0f; float rmax = hole ? IslandCornerRadius : CornerRadius;
            var f = new float[n]; var phi = new float[n]; bool any = false;
            var el = new float[n]; for (int i = 0; i < n; i++) el[i] = (l[(i + 1) % n] - l[i]).magnitude;
            for (int i = 0; i < n; i++)
            {
                float t = TurnAt(l, i);
                if (t > -CornerSharp || t < -KinkMax) continue;
                int ip = (i + n - 1) % n;
                if (AtTileEdge(l[i], hm)) continue;
                if (!hole && !InJunctionZone(ctx, l[i], o)) continue;
                float ph = -t * Mathf.Deg2Rad, th = Mathf.Tan(ph * .5f);
                float r = Mathf.Min(rmax, KinkMaxDepth / (1f / Mathf.Cos(ph * .5f) - 1f));
                float ff = Mathf.Min(r * th, .5f * Mathf.Min(el[ip], el[i]));
                if (ff < KinkMinTangent) continue;
                f[i] = ff; phi[i] = ph; any = true;
            }
            if (!any) return;
            var res = new List<Vector2>(n + 16);
            for (int i = 0; i < n; i++)
            {
                if (f[i] <= 0f) { res.Add(l[i]); continue; }
                Vector2 u = (l[i] - l[(i + n - 1) % n]).normalized, d = (l[(i + 1) % n] - l[i]).normalized;
                var pts = new List<Vector2>(); AddCornerArc(pts, l[i], u, d, f[i]);
                res.AddRange(pts); foreach (var q in pts) Mark(q + o);
            }
            l.Clear(); l.AddRange(res);
        }

        // Kreisbogen um eine Ecke v (Richtung vorher u, nachher d, Rechtsknick) mit Tangentenlänge f
        private static void AddCornerArc(List<Vector2> pts, Vector2 v, Vector2 u, Vector2 d, float f)
        {
            float phi = Mathf.Atan2(u.x * d.y - u.y * d.x, Vector2.Dot(u, d));            // < 0
            float r = f / Mathf.Tan(Mathf.Abs(phi) * .5f);
            Vector2 t1 = v - u * f, center = t1 + new Vector2(u.y, -u.x) * r;
            int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(phi) / (Mathf.PI / 12f)));
            Vector2 v0 = t1 - center;
            for (int k = 0; k <= steps; k++)
            {
                float a = phi * k / steps, c = Mathf.Cos(a), s = Mathf.Sin(a);
                pts.Add(center + new Vector2(v0.x * c - v0.y * s, v0.x * s + v0.y * c));
            }
        }

        public const float CornerRadius = 3.5f, IslandCornerRadius = 1.5f, CornerStraightTol = 10f, CornerSharp = 38f, CornerJog = 10f, CornerMaxWindow = 16f, CornerAnchor = 4f,
                           CornerMaxAdd = 3.5f, CornerMaxRemove = 1.3f;

        // Ecken in Kreuzungszonen: die Randstücke zwischen zwei geraden Kanten (Knick <= 10 °; Einmündung, Kreuzungsecke), die nicht glatt sind -- ein
        // Linksknick über 10 ° (Kerbe/Sprung) oder ein einzelner Knick über 38 ° (Fase, spitzer Winkel) -- werden durch einen tangentialen Kreisbogen
        // ersetzt. Nur Außenumrisse; das Ersetzen darf höchstens 3,5 m Asphalt hinzufügen bzw. 1,3 m wegnehmen.
        private static void RoundCorners(List<Vector2> l, float hm, Vector2 o, Ctx ctx)
        {
            if (ctx.ZoneHash == null) return;
            float a2 = 0f; for (int i = 0, j = l.Count - 1; i < l.Count; j = i++) a2 += l[j].x * l[i].y - l[i].x * l[j].y;
            float radius = a2 > 0f ? CornerRadius : IslandCornerRadius;         // Inseln (Löcher): kleinerer Radius
            for (int pass = 0; pass < 40; pass++)
            {
                int n = l.Count; if (n < 8) return;
                var t = new float[n]; for (int i = 0; i < n; i++) t[i] = TurnAt(l, i);
                // Fenster = Gruppen nicht gerader Punkte (|Knick| > 6 °), zwischen denen weniger als CornerAnchor Randlänge liegt
                var sig = new List<int>(); for (int i = 0; i < n; i++) if (Mathf.Abs(t[i]) > CornerStraightTol) sig.Add(i);
                if (sig.Count == 0) return;
                int first = -1;
                for (int q = 0; q < sig.Count && first < 0; q++)
                    if (PathLen(l, sig[(q + sig.Count - 1) % sig.Count], sig[q]) >= CornerAnchor) first = q;
                if (first < 0) return;
                bool changed = false;
                for (int qq = 0; qq < sig.Count && !changed;)
                {
                    int q0 = (first + qq) % sig.Count, cnt = 1;
                    while (qq + cnt < sig.Count && PathLen(l, sig[(q0 + cnt - 1) % sig.Count], sig[(q0 + cnt) % sig.Count]) < CornerAnchor) cnt++;
                    int w0 = sig[q0], w1 = sig[(q0 + cnt - 1) % sig.Count];
                    qq += cnt;
                    if (TryRoundCorner(l, t, w0, ((w1 - w0 + n) % n) + 1, hm, o, ctx, radius)) changed = true;
                }
                if (!changed) break;
            }
        }

        private static float PathLen(List<Vector2> l, int from, int to)
        {
            int n = l.Count; float len = 0f;
            for (int i = from; i != to; i = (i + 1) % n) len += (l[(i + 1) % n] - l[i]).magnitude;
            return len == 0f ? float.MaxValue : len;     // dieselbe Ecke = einziger Punkt der Schleife
        }

        private static bool TryRoundCorner(List<Vector2> l, float[] t, int w0, int cnt, float hm, Vector2 o, Ctx ctx, float radius)
        {
            int n = l.Count; if (cnt > 14) return false;
            int w1 = (w0 + cnt - 1) % n;
            bool bad = false;
            for (int k = 0; k < cnt; k++) { float tk = t[(w0 + k) % n]; if (tk > CornerJog || tk < -CornerSharp) bad = true; }
            if (!bad) return false;
            float path = 0f; for (int k = 0; k + 1 < cnt; k++) path += (l[(w0 + k + 1) % n] - l[(w0 + k) % n]).magnitude;
            if (path > CornerMaxWindow) return false;
            if (!InJunctionZone(ctx, l[(w0 + cnt / 2) % n], o)) return false;
            for (int k = -1; k <= cnt; k++) if (AtTileEdge(l[((w0 + k) % n + n) % n], hm)) return false;
            // Anker: gerade Randstücke (>= 4 m) vor und hinter dem Fenster
            int ia = w0; float la = 0f, lb = 0f; int guard = 0;
            while (la < CornerAnchor)
            {
                int ip = (ia + n - 1) % n;
                if (ia != w0 && Mathf.Abs(t[ia]) > CornerStraightTol) return false;
                la += (l[ia] - l[ip]).magnitude; ia = ip;
                if (++guard > 80 || ia == w1) return false;
            }
            int ib = w1; guard = 0;
            while (lb < CornerAnchor)
            {
                int inx = (ib + 1) % n;
                if (ib != w1 && Mathf.Abs(t[ib]) > CornerStraightTol) return false;
                lb += (l[inx] - l[ib]).magnitude; ib = inx;
                if (++guard > 80 || ib == w0 || ib == ia) return false;
            }
            Vector2 pa = l[w0], pb = l[w1];
            Vector2 dirA = (pa - l[ia]).normalized, dirB = (l[ib] - pb).normalized;
            float cr = dirA.x * dirB.y - dirA.y * dirB.x, dt = Vector2.Dot(dirA, dirB);
            float phi = Mathf.Atan2(cr, dt);
            if (Mathf.Abs(phi) < 25f * Mathf.Deg2Rad)
            {
                float mxL = 0f, mnR = 0f; for (int k = 0; k < cnt; k++) { float tk = t[(w0 + k) % n]; mxL = Mathf.Max(mxL, tk); mnR = Mathf.Min(mnR, tk); }
                return mxL > 30f && mnR < -30f && TryRoundJog(l, w0, w1, cnt, dirA, dirB, la, lb, o, ctx);       // S-Knick, keine flache Kurve
            }
            if (phi > -35f * Mathf.Deg2Rad || phi < -145f * Mathf.Deg2Rad) return false;
            Vector2 ab = pb - pa;
            float s = (ab.x * dirB.y - ab.y * dirB.x) / cr, u = (ab.x * dirA.y - ab.y * dirA.x) / cr;
            Vector2 c = pa + dirA * s;
            float tanh = Mathf.Tan(-phi * .5f);
            float r = radius, f = r * tanh;
            float fmax = Mathf.Min(la + s, lb - u);
            if (f > fmax) { f = fmax; r = f / tanh; }
            if (r < 1f) return false;
            float cd = float.MaxValue; for (int k = 0; k < cnt; k++) cd = Mathf.Min(cd, (l[(w0 + k) % n] - c).magnitude);
            if (cd > 5f) return false;
            Vector2 t1 = c - dirA * f, t2 = c + dirB * f;
            var arc = new List<Vector2>();
            Vector2 center = t1 + new Vector2(dirA.y, -dirA.x) * r, v0 = t1 - center;
            int steps = Mathf.Max(2, Mathf.CeilToInt(-phi / (Mathf.PI / 12f)));
            for (int k = 0; k <= steps; k++)
            {
                float a = phi * k / steps, cc = Mathf.Cos(a), sn = Mathf.Sin(a);
                arc.Add(center + new Vector2(v0.x * cc - v0.y * sn, v0.x * sn + v0.y * cc));
            }
            // Abweichung der ersetzten Punkte vom Bogen: liegt ein Randpunkt links des Bogens (Asphaltseite), kommt Asphalt hinzu (<= 3,5 m), sonst fällt welcher weg (<= 1,3 m)
            var ext = new List<Vector2>(arc.Count + 2) { t1 - dirA * 8f }; ext.AddRange(arc); ext.Add(t2 + dirB * 8f);   // Bogen samt Tangentenverlängerung
            for (int k = 0; k < cnt; k++)
            {
                Vector2 v = l[(w0 + k) % n]; float best = float.MaxValue, side = 0f;
                for (int m = 0; m + 1 < ext.Count; m++)
                {
                    Vector2 a0 = ext[m], a1 = ext[m + 1], d = a1 - a0; float len2 = d.sqrMagnitude; if (len2 < 1e-9f) continue;
                    float tt = Mathf.Clamp01(Vector2.Dot(v - a0, d) / len2); Vector2 qn = a0 + d * tt; float dist = (v - qn).magnitude;
                    if (dist < best) { best = dist; side = d.x * (v.y - a0.y) - d.y * (v.x - a0.x); }
                }
                if (best > (side >= 0f ? CornerMaxAdd : CornerMaxRemove)) return false;
            }
            // Vom Ersatz überdeckte Punkte der beiden Anker mitnehmen
            int i0 = w0, i1 = w1, gd = 0;
            while (gd++ < 60) { int k = (i0 + n - 1) % n; if (k == ia && Vector2.Dot(l[k] - t1, dirA) < -1e-3f) break; if (Vector2.Dot(l[k] - t1, dirA) > 1e-3f && k != i1) i0 = k; else break; }
            gd = 0;
            while (gd++ < 60) { int k = (i1 + 1) % n; if (Vector2.Dot(l[k] - t2, dirB) < -1e-3f && k != i0) i1 = k; else break; }
            int removed = ((i1 - i0 + n) % n) + 1; if (removed > n - 5) return false;
            var res = new List<Vector2>(n - removed + arc.Count);
            for (int k = (i1 + 1) % n, m = 0; m < n - removed; k = (k + 1) % n, m++) res.Add(l[k]);
            res.AddRange(arc); foreach (var q in arc) Mark(q + o);
            l.Clear(); l.AddRange(res);
            return true;
        }

        // Sprung/Höcker im Rand (S-Knick: die Kante verläuft vor und hinter dem Fenster parallel, aber um 0-1,8 m versetzt): die versetzten
        // Kanten werden durch eine flache Diagonale verbunden (4 x Versatz vor und hinter dem Fenster, 2,5-6 m), statt zwei 90°-Knicken.
        public const float JogMinOffset = 0f, JogMaxOffset = 1.8f;
        private static bool TryRoundJog(List<Vector2> l, int w0, int w1, int cnt, Vector2 dirA, Vector2 dirB, float la, float lb, Vector2 o, Ctx ctx)
        {
            int n = l.Count; Vector2 pa = l[w0], pb = l[w1];
            Vector2 dm = (dirA + dirB); if (dm.sqrMagnitude < 1e-6f) return false; dm.Normalize();
            Vector2 ab = pb - pa;
            float lat = dm.x * ab.y - dm.y * ab.x, lon = Vector2.Dot(ab, dm);
            if (Mathf.Abs(lat) < JogMinOffset || Mathf.Abs(lat) > JogMaxOffset || lon < -.5f) return false;
            float h = Mathf.Min(Mathf.Clamp(4f * Mathf.Abs(lat), 2.5f, 6f), la * .9f, lb * .9f);     // Höcker/Kerbe ohne Versatz: 2,5 m Anlauf
            Vector2 t1 = WalkPolyline(l, w0, h, -1, out int jb), t2 = WalkPolyline(l, w1, h, 1, out int jf);
            // Diagonale t1 -> t2: alle ersetzten Punkte müssen dicht an ihr liegen (<= |Versatz| + 0,3 m)
            Vector2 dd = t2 - t1; float dl = dd.magnitude; if (dl < 1e-3f) return false;
            for (int k = (jb + 1) % n; ; k = (k + 1) % n)
            {
                if (Mathf.Abs(dd.x * (l[k].y - t1.y) - dd.y * (l[k].x - t1.x)) / dl > Mathf.Abs(lat) + .3f) return false;
                if (k == jf) break;
            }
            int removed = ((jf - jb + n) % n);         // Punkte jb+1 .. jf
            if (removed > n - 5) return false;
            var lobe = new List<Vector2> { t1 }; for (int k = (jb + 1) % n; ; k = (k + 1) % n) { lobe.Add(l[k]); if (k == jf) break; }
            lobe.Add(t2);
            if (NearCentre(ctx, o, lobe)) return false;          // nie ein Streifenende (Mittellinienpunkt) wegschneiden
            var res = new List<Vector2>(n - removed + 2);
            for (int k = (jf + 1) % n, m = 0; m < n - removed; k = (k + 1) % n, m++) res.Add(l[k]);
            res.Add(t1); res.Add(t2); Mark(t1 + o); Mark(t2 + o);
            // die Liste muss bei der Kante t1 -> t2 wieder im Umlauf liegen: res = [Rest ab jf+1 ... bis jb, t1, t2] passt, wenn t1/t2 zwischen jb und jf+1 stehen
            l.Clear(); l.AddRange(res);
            return true;
        }

        // Punkt im Abstand dist längs des Randes ab Punkt 'from' (dir -1 = rückwärts, +1 = vorwärts); cut = Index des Kantenanfangs (dir +1) bzw. des
        // Kantenanfangs vor dem Punkt (dir -1): bei -1 liegt der Punkt auf Kante cut -> cut+1, bei +1 auf Kante cut -> cut+1
        private static Vector2 WalkPolyline(List<Vector2> l, int from, float dist, int dir, out int cut)
        {
            int n = l.Count; float acc = 0f; int k = from;
            for (int g = 0; g < n; g++)
            {
                int nx = dir < 0 ? (k + n - 1) % n : (k + 1) % n; float el = (l[nx] - l[k]).magnitude;
                if (acc + el >= dist)
                {
                    Vector2 p = Vector2.Lerp(l[k], l[nx], (dist - acc) / Mathf.Max(el, 1e-6f));
                    cut = dir < 0 ? nx : k; return p;
                }
                acc += el; k = nx;
            }
            cut = from; return l[from];
        }

        // Kleine Löcher (< HoleFillArea) innerhalb von Kreuzungszonen füllen: Löcher haben Umlaufsinn < 0
        private static List<Vector2[]> FillSmallJunctionHoles(List<Vector2[]> loops, Vector2 o, Ctx ctx)
        {
            if (ctx.ZoneHash == null) return loops;
            var res = new List<Vector2[]>(loops.Count);
            float hm = Tile * .5f + Margin;
            foreach (var c in loops)
            {
                float a2 = 0f; Vector2 ctr = Vector2.zero; bool border = false;
                for (int i = 0, j = c.Length - 1; i < c.Length; j = i++)
                {
                    a2 += c[j].x * c[i].y - c[i].x * c[j].y; ctr += c[i];
                    if (Mathf.Abs(Mathf.Abs(c[i].x) - hm) < 1e-2f || Mathf.Abs(Mathf.Abs(c[i].y) - hm) < 1e-2f) border = true;
                }
                if (a2 < 0f && -a2 * .5f < HoleFillArea && !border)
                {
                    ctr = ctr / c.Length + o;
                    bool inZone = false;
                    foreach (int zi in ctx.ZoneHash.WithinIdx(ctr, ctx.ZoneRMax))
                        if ((ctx.ZoneC[zi] - ctr).magnitude <= ctx.ZoneR[zi]) { inZone = true; break; }
                    if (inZone) continue;
                }
                res.Add(c);
            }
            return res;
        }

        // ------------------------------------------------------------------ Band nach Kantenklassen
        // Bandklasse eines Punktes am Asphaltrand (lokale Kachelkoordinaten): aus der Herkunft der Kante, sonst nächste Probe
        private static byte ClassAt(Vector2 local, Vector2 o, Ctx ctx)
        {
            Vector2 w = local + o;
            int c = ctx.Tags.Find(w, .3f);
            if (c >= 0) return (byte)c;
            if (!ctx.Near.Nearest(w.x, w.y, 25f, out int i, out _)) return ClsRural;
            var s = ctx.Near.Samples[i];
            float lat = (w.x - s.pos.x) * s.side.x + (w.y - s.pos.z) * s.side.z;
            byte kind = lat > 0f ? ctx.RK[i] : ctx.LK[i];
            return Cls(kind == RoadNet.KindMedian, ctx.Urban[i]);
        }

        // Umriss mit Klasse je Kante (ClsNone = künstlicher Rand der Kachelberechnung); Kanten mit wechselnder Klasse werden
        // an den Wechselstellen geteilt (Auflösung 0,5 m)
        private sealed class ClsLoop { public Vector2[] P; public byte[] K; }

        private static ClsLoop ClassifyLoop(Vector2[] c, Vector2 o, Ctx ctx, float hm)
        {
            var pts = new List<Vector2>(c.Length + 8); var ks = new List<byte>(c.Length + 8);
            int n = c.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = c[i], b = c[(i + 1) % n]; float len = (b - a).magnitude;
                if (len < 1e-4f) continue;
                if (OnBorder(a, b, hm)) { pts.Add(a); ks.Add(ClsNone); continue; }
                int probes = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
                byte first = ClassAt(a + (b - a) * (.5f / probes), o, ctx); bool same = true;
                for (int j = 1; j < probes && same; j++) same = ClassAt(a + (b - a) * ((j + .5f) / probes), o, ctx) == first;
                if (same) { pts.Add(a); ks.Add(first); continue; }
                int m = Mathf.Clamp(Mathf.CeilToInt(len / .5f), 2, 400); byte prev = ClsNone;
                for (int j = 0; j < m; j++)
                {
                    byte cj = ClassAt(a + (b - a) * ((j + .5f) / m), o, ctx);
                    if (j == 0 || cj != prev) { pts.Add(a + (b - a) * (j / (float)m)); ks.Add(cj); prev = cj; }
                }
            }
            MergeShortRuns(pts, ks);
            return new ClsLoop { P = pts.ToArray(), K = ks.ToArray() };
        }

        // Kurze Kantenfolge (< 3 m; zwischen zwei Mittelstreifen-Bordsteinkanten bis 12,8 m = Nase) zwischen zwei Kanten derselben anderen Klasse
        // (Stirnkante einer Mittelstreifenspitze, Kerbe zwischen zwei Zufahrten) übernimmt deren Klasse: sonst ragt ein 2 m tiefer Gehweg-Stummel in die Lücke
        private static void MergeShortRuns(List<Vector2> P, List<byte> K)
        {
            int n = P.Count; if (n < 4) return;
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                int s0 = 0; while (s0 < n && K[s0] == K[(s0 + n - 1) % n]) s0++;
                if (s0 == n) return;                                       // ein einziger Lauf
                for (int step = 0; step < n;)
                {
                    int i = (s0 + step) % n; byte c = K[i]; int len = 1;
                    while (step + len < n && K[(s0 + step + len) % n] == c) len++;
                    int j = (s0 + step + len) % n, p = (i + n - 1) % n;
                    step += len;
                    if (c == ClsNone || K[p] == ClsNone || K[j] == ClsNone || K[p] != K[j] || K[p] == c) continue;
                    float L = 0f; for (int q = 0; q < len; q++) L += (P[(i + q + 1) % n] - P[(i + q) % n]).magnitude;
                    if (L >= (K[p] == ClsKerbUrban || K[p] == ClsKerbRural ? NoseMaxWidth * 1.6f : 3f)) continue;     // Nase (Halbkreis <= 8 m Breite): ganz Bordstein
                    for (int q = 0; q < len; q++) K[(i + q) % n] = K[p];
                    changed = true;
                }
                if (!changed) break;
            }
        }

        // Bandstücke der Klasse 'cls' um einen Umriss (Asphalt links der Kante, außen = rechts): je Kante ein Rechteck der
        // Klassenbreite nach außen, an jeder Konvexecke (Linksknick) ein Kreissektor. Bei Klassenwechsel füllt die Klasse
        // mit Vorrang (kleinere Nummer, breiter) den Keil; der Schnitt bleibt eine Gerade quer zur Kante.
        private const float MaxMiter = 1.75f;          // Rechtskurven bis ~100° werden auf Gehrung geschnitten
        // Punkt auf der Winkelhalbierenden der Normalen n0, n1 im Abstand w von beiden Kantenlinien (Gehrungsecke)
        private static Vector2 BisectorOffset(Vector2 n0, Vector2 n1, float w)
        {
            Vector2 b = n0 + n1; float l = b.magnitude;
            if (l < 1e-4f) return n1 * w;
            b /= l;
            return b * (w / Mathf.Max(.3f, Vector2.Dot(b, n1)));
        }

        // Polygon auf die Seite sign*cross(c, x - o) >= 0 zuschneiden (Sutherland-Hodgman); null, wenn nichts übrig bleibt
        private static Vector2[] ClipHalfPlane(Vector2[] poly, Vector2 o, Vector2 c, float sign)
        {
            var r = new List<Vector2>(poly.Length + 2);
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Length];
                float da = sign * (c.x * (a.y - o.y) - c.y * (a.x - o.x)), db = sign * (c.x * (b.y - o.y) - c.y * (b.x - o.x));
                if (da >= 0f) r.Add(a);
                if ((da >= 0f) != (db >= 0f)) r.Add(a + (b - a) * (da / (da - db)));
            }
            return r.Count >= 3 ? r.ToArray() : null;
        }

        private static void BandPieces(ClsLoop L, byte cls, List<Vector2[]> res)
        {
            const float FanMin = .02f;     // flachere Knicke: Rechtecke auf Gehrung verlängern statt Fächer (robuster)
            var P = L.P; var K = L.K; int n = P.Length;
            if (n < 3) return;
            bool any = false; for (int i = 0; i < n; i++) if (K[i] == cls) { any = true; break; }
            if (!any) return;
            var dir = new Vector2[n]; var outN = new Vector2[n]; var ok = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 dd = P[(i + 1) % n] - P[i]; float len = dd.magnitude;
                ok[i] = K[i] != ClsNone && len > 1e-4f;
                if (!ok[i]) continue;
                dir[i] = dd / len; outN[i] = new Vector2(dd.y, -dd.x) / len;
            }
            var ang = new float[n];                                  // Knick in P[i] zwischen Kante i-1 und Kante i, links positiv
            for (int i = 0; i < n; i++)
            {
                int p = (i + n - 1) % n;
                ang[i] = ok[p] && ok[i] ? Mathf.Atan2(dir[p].x * dir[i].y - dir[p].y * dir[i].x, Vector2.Dot(dir[p], dir[i])) : 0f;
            }
            float w = ClsW[cls];
            int first = res.Count; var pieceEdge = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!ok[i] || K[i] != cls) continue;
                int p = (i + n - 1) % n, nx = (i + 1) % n;
                float e0 = ang[i] > 1e-6f && ang[i] < FanMin && K[p] == cls ? w * Mathf.Tan(ang[i] * .5f) + .005f : 0f;
                float e1 = ang[nx] > 1e-6f && ang[nx] < FanMin && K[nx] == cls ? w * Mathf.Tan(ang[nx] * .5f) + .005f : 0f;
                Vector2 a = P[i] - dir[i] * e0, b = P[nx] + dir[i] * e1;
                // Klassenwechsel in einer Rechtskurve (Rechtecke überlappen): beide Klassen enden auf derselben Geraden, der Winkel-
                // halbierenden der beiden Kantennormalen -- sonst besteht der Übergang aus zwei Stücken (je eine Stirnkante) mit Knick
                Vector2 aOut = a + outN[i] * w, bOut = b + outN[i] * w;
                if (K[p] != cls && ok[p] && ang[i] < -1e-4f && ang[i] > -MaxMiter) aOut = P[i] + BisectorOffset(outN[p], outN[i], w);
                if (K[nx] != cls && ok[nx] && ang[nx] < -1e-4f && ang[nx] > -MaxMiter) bOut = P[nx] + BisectorOffset(outN[i], outN[nx], w);
                res.Add(new[] { a, b, bOut, aOut }); pieceEdge.Add(i);
            }
            // Klassenwechsel in einer Rechtskurve: Rechtecke NAHER Kanten (kurze Kantenzüge, z. B. Bordsteinbogen) reichen sonst über die
            // Gehrungsgerade hinweg; auf die Seite der eigenen Klasse zuschneiden, damit der Übergang eine einzige Gerade bleibt
            for (int v = 0; v < n; v++)
            {
                int p = (v + n - 1) % n;
                if (!ok[p] || !ok[v] || K[p] == K[v] || !(ang[v] < -1e-4f && ang[v] > -MaxMiter)) continue;
                if (K[v] != cls && K[p] != cls) continue;
                Vector2 c = (outN[p] + outN[v]).normalized;
                Vector2 refDir = K[v] == cls ? dir[v] : -dir[p];
                float keep = Mathf.Sign(c.x * refDir.y - c.y * refDir.x);
                var near = new HashSet<int>(); float acc = 0f;
                for (int j = v; acc < 4f && near.Count < n; j = (j + 1) % n) { near.Add(j); acc += (P[(j + 1) % n] - P[j]).magnitude; }
                acc = 0f;
                for (int j = p; acc < 4f && near.Count < n; j = (j + n - 1) % n) { near.Add(j); acc += (P[(j + 1) % n] - P[j]).magnitude; }
                for (int q = 0; q < pieceEdge.Count; q++)
                {
                    int j = pieceEdge[q]; if (j == p || j == v || !near.Contains(j)) continue;
                    var poly = res[first + q]; if (poly == null) continue;
                    res[first + q] = ClipHalfPlane(poly, P[v], c, keep);
                }
            }
            for (int i = 0; i < n; i++)
            {
                int p = (i + n - 1) % n;
                if (!ok[p] || !ok[i] || ang[i] <= 1e-4f) continue;
                byte kp = K[p], ki = K[i];
                if ((kp == ki ? kp : (byte)Mathf.Min(kp, ki)) != cls) continue;
                if (kp == ki && ang[i] < FanMin) continue;
                int steps = Mathf.Max(1, Mathf.CeilToInt(ang[i] / (Mathf.PI / 12f)));
                var fan = new Vector2[steps + 2];
                fan[0] = P[i];
                float a0 = Mathf.Atan2(outN[p].y, outN[p].x);
                for (int st = 0; st <= steps; st++)
                {
                    float t = a0 + ang[i] * st / steps;
                    fan[st + 1] = P[i] + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * w;
                }
                res.Add(fan);
            }
            res.RemoveAll(x => x == null);
        }

        // Klassen der Streifenkanten eines Abschnitts (Herkunft): Gehweg/Randstreifen nach Ortslage, Bord zur Mittelstreifenseite
        private static void TagStrip(RoadNet.Segment sg, EdgeTags tags)
        {
            int n = sg.S.Count;
            for (int k = 0; k + 1 < n; k++)
            {
                var a = sg.S[k]; var b = sg.S[k + 1]; bool urban = sg.Urban[k];
                Vector2 pa = new Vector2(a.pos.x, a.pos.z), pb = new Vector2(b.pos.x, b.pos.z);
                Vector2 na = new Vector2(a.side.x, a.side.z), nb = new Vector2(b.side.x, b.side.z);
                tags.Add(pa - na * a.half, pb - nb * b.half, Cls(sg.LeftKind[k] == RoadNet.KindMedian, urban));
                tags.Add(pa + na * a.half, pb + nb * b.half, Cls(sg.RightKind[k] == RoadNet.KindMedian, urban));
            }
            for (int e = 0; e < 2 && n > 0; e++)             // Stirnkanten (Sackgasse, Korridorrand)
            {
                var a = sg.S[e == 0 ? 0 : n - 1]; Vector2 pa = new Vector2(a.pos.x, a.pos.z), na = new Vector2(a.side.x, a.side.z);
                tags.Add(pa - na * a.half, pa + na * a.half, Cls(false, sg.Urban[e == 0 ? 0 : n - 1]));
            }
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


        // Knotenscheibe: zwischen zwei benachbarten Zufahrten (nach Richtung geordnet) bleibt an der Außenseite eines Knicks
        // ein Keil zwischen den Stirnkanten der Streifen offen (deren Kanten treffen sich nur im Knoten). Das Dreieck
        // Knoten – linke Ecke der einen – rechte Ecke der nächsten Zufahrt schließt ihn für jeden Winkel (bei Winkeln < 180°
        // liegt es ohnehin in den Streifen).
        private static int AddHub(RoadNet net, int ni, List<Vector2[]> asphalt, List<Vector2[]> rims)
        {
            var nd = net.Nodes[ni]; if (nd.Segs.Count < 2) return 0;
            var leg = new List<Vector4>();                      // x,y = linke Ecke, z,w = rechte Ecke (Blick vom Knoten nach außen)
            var ang = new List<float>();
            Vector2 P = nd.P;
            foreach (int si in nd.Segs)
            {
                var sg = net.Segs[si]; if (sg.S.Count < 2) continue;
                for (int e = 0; e < 2; e++)
                {
                    bool atA = e == 0; if ((atA ? sg.A : sg.B) != ni) continue;
                    var s0 = sg.S[atA ? 0 : sg.S.Count - 1];
                    Vector2 pos = new Vector2(s0.pos.x, s0.pos.z), side = new Vector2(s0.side.x, s0.side.z) * (atA ? 1f : -1f);
                    Vector2 tg = new Vector2(s0.tangent.x, s0.tangent.z).normalized * (atA ? 1f : -1f);
                    Vector2 l = pos - side * s0.half, r = pos + side * s0.half;
                    leg.Add(new Vector4(l.x, l.y, r.x, r.y)); ang.Add(Mathf.Atan2(tg.y, tg.x));
                }
            }
            int n = leg.Count; if (n < 2) return 0;
            var order = new int[n]; for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (u, v) => ang[u].CompareTo(ang[v]));
            int added = 0;
            for (int q = 0; q < n; q++)
            {
                var a = leg[order[q]]; var b = leg[order[(q + 1) % n]];
                Vector2 la = new Vector2(a.x, a.y), rb = new Vector2(b.z, b.w);
                if (Mathf.Abs((la.x - P.x) * (rb.y - P.y) - (la.y - P.y) * (rb.x - P.x)) < 1e-3f) continue;
                // Außenseite eines Knicks (Lücke > 180°): Rundung um den Knoten statt Fase, Radius = halbe Breite -- das ist genau
                // die Versatzlinie der Straßenachse (Rundverbindung), die Fahrbahnbreite bleibt im Knick konstant
                Vector2 va = la - P, vb = rb - P; float ra = va.magnitude, rb2 = vb.magnitude;
                float angA = Mathf.Atan2(va.y, va.x), angB = Mathf.Atan2(vb.y, vb.x);
                float da = angB - angA; while (da < 0f) da += 2f * Mathf.PI; while (da >= 2f * Mathf.PI) da -= 2f * Mathf.PI;   // CCW von la nach rb
                float dirGap = ang[order[(q + 1) % n]] - ang[order[q]]; while (dirGap <= 0f) dirGap += 2f * Mathf.PI;
                if (dirGap > Mathf.PI + .05f && ra > .3f && rb2 > .3f)
                {
                    int steps = Mathf.Max(2, Mathf.CeilToInt(da / (Mathf.PI / 12f)));
                    var fan = new List<Vector2> { P, la };
                    for (int st = 1; st < steps; st++)
                    {
                        float t = st / (float)steps, an = angA + da * t, rr = Mathf.Lerp(ra, rb2, t);
                        fan.Add(P + new Vector2(Mathf.Cos(an), Mathf.Sin(an)) * rr);
                    }
                    fan.Add(rb);
                    asphalt.Add(fan.ToArray());
                    rims.Add(fan.GetRange(1, fan.Count - 1).ToArray());
                }
                else asphalt.Add(new[] { P, la, rb });
                added++;
            }
            return added;
        }

        // Gerundete Bordsteinecken: Fläche zwischen zwei Fahrbahnkanten und einem Kreisbogen (Radius 3–4 m)
        // Liegt der Punkt auf einem Fahrbahnstreifen (Querabstand < halbe Breite, Längsabstand < 2,2 m zur nächsten Probe)?
        private static bool OnRoad(RoadField near, Vector2 p)
        {
            if (!near.Nearest(p.x, p.y, 8f, out int i, out _)) return false;
            var s = near.Samples[i];
            float lat = (p.x - s.pos.x) * s.side.x + (p.y - s.pos.z) * s.side.z;
            var t = new Vector2(s.tangent.x, s.tangent.z).normalized;
            float along = (p.x - s.pos.x) * t.x + (p.y - s.pos.z) * t.y;
            return Mathf.Abs(lat) <= s.half + .05f && Mathf.Abs(along) <= 2.2f;
        }

        private static int AddFillets(RoadNet net, RoadNet.Junction j, List<Vector2[]> asphalt, EdgeTags tags, RoadField near, List<Vector2[]> rims)
        {
            int m = j.Ends.Count, added = 0;
            if (m < 2) return 0;
            var L = new Vector2[m]; var R = new Vector2[m]; var D = new Vector2[m];
            for (int i = 0; i < m; i++)
            {
                var e = j.Ends[i]; var sg = net.Segs[e.Seg];
                var ts = RoadNet.At(sg, e.AtA ? e.Trim : sg.Length - e.Trim);
                Vector3 r3 = e.AtA ? ts.side : -ts.side;
                Vector2 p = new Vector2(ts.pos.x, ts.pos.z), r2 = new Vector2(r3.x, r3.z);
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
                // Die Tangentenpunkte müssen auf vorhandener Fahrbahn liegen. Bei Ästen, deren Randlinien sich erst weit im Innern der
                // Kreuzung (über eine Verbindungsstrecke hinweg) schneiden, läge die Ausrundung sonst frei neben der Straße
                // (Asphaltinsel im Gehweg).
                if (!OnRoad(near, t1 + RoadNet.Right(D[i]) * FilletOverlap) || !OnRoad(near, t2 + RoadNet.Left(D[k]) * FilletOverlap)) continue;
                Vector2 bis = (D[i] + D[k]).normalized;
                Vector2 center = c + bis * (r / Mathf.Sin(theta * .5f));
                float a1 = Mathf.Atan2(t1.y - center.y, t1.x - center.x), a2 = Mathf.Atan2(t2.y - center.y, t2.x - center.x);
                float da = a2 - a1; while (da > Mathf.PI) da -= 2f * Mathf.PI; while (da < -Mathf.PI) da += 2f * Mathf.PI;
                // Bogenfläche zwischen den Kantenlinien; die geraden Seiten werden um FilletOverlap in die Streifen hineingezogen,
                // damit zwischen Bogenfläche und Streifen kein Haarspalt aus Rundungsfehlern (gekrümmte Zufahrten) bleibt
                Vector2 in1 = RoadNet.Right(D[i]) * FilletOverlap, in2 = RoadNet.Left(D[k]) * FilletOverlap;
                var arc = new List<Vector2> { t1 };
                for (int s = 1; s < 6; s++) { float ang = a1 + da * s / 6f; arc.Add(center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r); }
                arc.Add(t2);
                var poly = new List<Vector2> { c + in1 + in2, t1 + in1 };
                poly.AddRange(arc); poly.Add(t2 + in2);
                asphalt.Add(poly.ToArray());
                byte fc = Cls(false, j.Ends[i].Urban || j.Ends[k].Urban);
                for (int q = 0; q + 1 < arc.Count; q++) tags.Add(arc[q], arc[q + 1], fc);     // nur der Bogen liegt am Rand
                rims.Add(arc.ToArray());
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
        public static List<Vector2> Refine(List<Vector2> flat, float maxEdge, float border = 0f)
        {
            var verts = new List<Vector2>(); var index = new Dictionary<long, int>(); var tris = new List<int>();
            System.Func<Vector2, int> Id = q =>
            {
                long key = ((long)Mathf.RoundToInt(q.x * 1000f) << 32) ^ (uint)Mathf.RoundToInt(q.y * 1000f);
                if (!index.TryGetValue(key, out int i)) { i = verts.Count; verts.Add(q); index[key] = i; }
                return i;
            };
            for (int t = 0; t + 2 < flat.Count; t += 3)
            {
                int ia = Id(flat[t]), ib = Id(flat[t + 1]), ic = Id(flat[t + 2]);
                if (ia == ib || ib == ic || ia == ic) continue;              // durch die 1-mm-Rasterung entartet (Fläche 0)
                {
                    // kollineare Randpunkte liefern Dreiecke der Höhe ~0: entfernen (Spalt < 0,5 mm), sonst zerlegt die Kantenteilung
                    // sie in Nadeln entlang des Randes
                    Vector2 qa = flat[t], qb = flat[t + 1], qc = flat[t + 2];
                    double lm = System.Math.Sqrt(System.Math.Max((qb - qa).sqrMagnitude, System.Math.Max((qc - qb).sqrMagnitude, (qa - qc).sqrMagnitude)));
                    if (lm > 1e-6 && System.Math.Abs(Orient(qa, qb, qc)) / lm < 5e-4) continue;
                }
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
            }
            // Nadeln VOR der Kantenteilung entfernen: eine lange Innenkante, die an kollinearen Randpunkten vorbeiläuft (Ohr aus
            // LibTess), würde sonst beim Halbieren in eine Kette von Nadeln entlang des Randes zerfallen
            FlipToDelaunay(verts, tris, border);
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
            FlipToDelaunay(verts, tris, border);
            var res = new List<Vector2>(tris.Count);
            foreach (int i in tris) res.Add(verts[i]);
            return res;
        }

        // LibTess triangulates schmale Bänder mit langen Nadeln (Dreiecke aus fast kollinearen Punkten, Höhe < 1 cm) entlang der
        // dicht besetzten Seite. Kantenwechsel (Lawson) bis zur Delaunay-Eigenschaft: gleiche Fläche, gleiche Eckpunkte,
        // aber Dreiecke mit vernünftigen Winkeln. Nur Kanten, die genau zwei Dreiecke teilen, und nur bei konvexem Viereck.
        private static void FlipToDelaunay(List<Vector2> verts, List<int> tris, float border)
        {
            int T = tris.Count / 3; if (T < 2) return;
            double sum = 0;
            for (int t = 0; t < T; t++) sum += Orient(verts[tris[t * 3]], verts[tris[t * 3 + 1]], verts[tris[t * 3 + 2]]);
            bool cw = sum < 0;
            if (cw) for (int t = 0; t < T; t++) { int x = tris[t * 3 + 1]; tris[t * 3 + 1] = tris[t * 3 + 2]; tris[t * 3 + 2] = x; }
            var em = new Dictionary<long, int>(T * 3); var nb = new int[T * 3];
            for (int i = 0; i < nb.Length; i++) nb[i] = -1;
            System.Func<int, int, long> DK = (u, v) => ((long)u << 32) | (uint)v;
            for (int t = 0; t < T; t++)
                for (int e = 0; e < 3; e++) em[DK(tris[t * 3 + e], tris[t * 3 + (e + 1) % 3])] = t * 3 + e;
            for (int t = 0; t < T; t++)
                for (int e = 0; e < 3; e++)
                    if (em.TryGetValue(DK(tris[t * 3 + (e + 1) % 3], tris[t * 3 + e]), out int o) && o / 3 != t) nb[t * 3 + e] = o / 3;
            var stack = new Stack<int>(T * 3);
            for (int i = 0; i < nb.Length; i++) if (nb[i] >= 0) stack.Push(i);
            System.Action<int, int, int, int> Repoint = (x, u, v, to) =>          // Nachbar x: Kante u->v zeigt nun auf Dreieck 'to'
            {
                if (x < 0) return;
                for (int e = 0; e < 3; e++)
                    if (tris[x * 3 + e] == u && tris[x * 3 + (e + 1) % 3] == v) { nb[x * 3 + e] = to; return; }
            };
            int guard = T * 40;
            while (stack.Count > 0 && guard-- > 0)
            {
                int te = stack.Pop(); int t = te / 3, e = te % 3; int u = nb[te]; if (u < 0) continue;
                int a = tris[t * 3 + e], b = tris[t * 3 + (e + 1) % 3], c = tris[t * 3 + (e + 2) % 3];
                int f = -1; for (int i = 0; i < 3; i++) if (tris[u * 3 + i] == b && tris[u * 3 + (i + 1) % 3] == a) { f = i; break; }
                if (f < 0 || nb[u * 3 + f] != t) continue;
                int d = tris[u * 3 + (f + 2) % 3];
                Vector2 A = verts[a], B = verts[b], C = verts[c], D = verts[d];
                if (InCircle(A, B, C, D) <= 1e-9) continue;
                if (Orient(C, A, D) <= 1e-7 || Orient(D, B, C) <= 1e-7) continue;               // Viereck nicht konvex
                int nbCA = nb[t * 3 + (e + 2) % 3], nbBC = nb[t * 3 + (e + 1) % 3];
                int nbAD = nb[u * 3 + (f + 1) % 3], nbDB = nb[u * 3 + (f + 2) % 3];
                tris[t * 3] = c; tris[t * 3 + 1] = a; tris[t * 3 + 2] = d;
                tris[u * 3] = d; tris[u * 3 + 1] = b; tris[u * 3 + 2] = c;
                nb[t * 3] = nbCA; nb[t * 3 + 1] = nbAD; nb[t * 3 + 2] = u;
                nb[u * 3] = nbDB; nb[u * 3 + 1] = nbBC; nb[u * 3 + 2] = t;
                Repoint(nbAD, d, a, t); Repoint(nbBC, c, b, u);
                stack.Push(t * 3); stack.Push(t * 3 + 1); stack.Push(u * 3); stack.Push(u * 3 + 1);
            }
            // Nadeln, deren lange Kante Rand ist und deren Spitze knapp (< 1 cm) innerhalb liegt (Zwischenpunkt einer Kantenfolge):
            // die Spitze wird auf die lange Kante gesetzt (Verschiebung < 1 cm), das entartete Dreieck entfällt; die Spitze
            // bleibt als T-Punkt auf dem Rand. Punkte am Kachelrand bleiben unverändert (Nahtstelle zur Nachbarkachel).
            var gone = new bool[T];
            for (int t = 0; t < T; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector2 A = verts[i0], B = verts[i1], C = verts[i2];
                float l0 = (B - A).sqrMagnitude, l1 = (C - B).sqrMagnitude, l2 = (A - C).sqrMagnitude;
                int longest = l0 >= l1 && l0 >= l2 ? 0 : (l1 >= l2 ? 1 : 2);
                if (nb[t * 3 + longest] >= 0) continue;
                int ia = tris[t * 3 + longest], ib = tris[t * 3 + (longest + 1) % 3], ic = tris[t * 3 + (longest + 2) % 3];
                Vector2 pa = verts[ia], pb = verts[ib], pc = verts[ic];
                double lmax = System.Math.Sqrt(System.Math.Max(l0, System.Math.Max(l1, l2)));
                if (lmax < 1e-4 || System.Math.Abs(Orient(pa, pb, pc)) / lmax >= .009) continue;
                if (border > 0f && (Mathf.Abs(Mathf.Abs(pc.x) - border) < 2e-3f || Mathf.Abs(Mathf.Abs(pc.y) - border) < 2e-3f)) continue;
                Vector2 dd = (pb - pa) / (float)lmax; float tt = Vector2.Dot(pc - pa, dd);
                verts[ic] = pa + dd * Mathf.Clamp(tt, 0f, (float)lmax);
                gone[t] = true;
            }
            // "Ohren": Nadeldreieck (Höhe < 1 cm), dessen beide kürzeren Kanten Rand sind. Sein Wegfall verschiebt den Rand um
            // höchstens 1 cm (Sehne statt Zweipunktzug) und reißt keine Naht auf.
            int w = 0;
            for (int t = 0; t < T; t++)
            {
                Vector2 A = verts[tris[t * 3]], B = verts[tris[t * 3 + 1]], C = verts[tris[t * 3 + 2]];
                float l0 = (B - A).sqrMagnitude, l1 = (C - B).sqrMagnitude, l2 = (A - C).sqrMagnitude;
                int longest = l0 >= l1 && l0 >= l2 ? 0 : (l1 >= l2 ? 1 : 2);
                double lmax = Mathf.Sqrt(Mathf.Max(l0, Mathf.Max(l1, l2)));
                bool ear = lmax > 1e-4 && System.Math.Abs(Orient(A, B, C)) / lmax < .009 &&
                           nb[t * 3 + (longest + 1) % 3] < 0 && nb[t * 3 + (longest + 2) % 3] < 0 && nb[t * 3 + longest] >= 0;
                if (ear || gone[t]) continue;
                if (w != t) { tris[w * 3] = tris[t * 3]; tris[w * 3 + 1] = tris[t * 3 + 1]; tris[w * 3 + 2] = tris[t * 3 + 2]; }
                w++;
            }
            if (w < T) tris.RemoveRange(w * 3, (T - w) * 3);
            if (cw) for (int t = 0; t < w; t++) { int x = tris[t * 3 + 1]; tris[t * 3 + 1] = tris[t * 3 + 2]; tris[t * 3 + 2] = x; }
        }

        private static double Orient(Vector2 a, Vector2 b, Vector2 c) => ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

        private static double InCircle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            double adx = (double)a.x - d.x, ady = (double)a.y - d.y, bdx = (double)b.x - d.x, bdy = (double)b.y - d.y, cdx = (double)c.x - d.x, cdy = (double)c.y - d.y;
            return (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) + (bdx * bdx + bdy * bdy) * (cdx * ady - adx * cdy) + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
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

        private static IEnumerable<(Vector2, Vector2, Vector2)> Emitted(List<Vector2> tris, Vector2 o)
        {
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                long a = CellKey(tris[t], o), b = CellKey(tris[t + 1], o), c = CellKey(tris[t + 2], o);
                if (a == b || b == c || a == c) continue;
                yield return (tris[t], tris[t + 1], tris[t + 2]);
            }
        }
        private static long CellKey(Vector2 q, Vector2 o) => ((long)Mathf.RoundToInt((q.x + o.x) * 100f) * 73856093L) ^ ((long)Mathf.RoundToInt((q.y + o.y) * 100f) * 19349663L);

        private static int V(RoadProfile.Parts p, Dictionary<long, int> map, int sub, Vector2 q, System.Func<Vector2, Vector3> lift)
        {
            long key = CellKey(q, cellOrigin) ^ ((long)sub << 58);
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
