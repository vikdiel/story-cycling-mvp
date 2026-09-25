using System.Collections.Generic;
using LibTessDotNet.Double;
using UnityEngine;
using Mesh = UnityEngine.Mesh;

namespace StoryCycling.WorldGen.Editor
{
    // Straßenoberfläche als FLÄCHEN-VEREINIGUNG statt zusammengesetzter Einzelteile:
    //   Asphalt   = Vereinigung aller Fahrbahn-Vierecke (+ gerundete Bordsteinecken an Kreuzungen)
    //   Gehweg    = Vereinigung der Außenstreifen innerorts   MINUS Asphalt
    //   Randstr.  = Vereinigung der Außenstreifen außerorts   MINUS Asphalt MINUS Gehweg
    // Kreuzungen, Kreisverkehre, Abbiegespuren, Doppelfahrbahnen (Mittelstreifen bleibt als Loch frei)
    // entstehen dadurch automatisch; Überlappungen und Lücken sind geometrisch ausgeschlossen.
    // Vereinigung/Differenz über Umlaufregeln von LibTess (GLU-Tesselator, doppelte Genauigkeit).
    public static class RoadSurface
    {
        private const float Bucket = 400f;

        private const float Tile = 200f;

        // externalAsphalt: von osm2streets vorberechnete Fahrbahn-/Kreuzungsflächen (siehe Osm2StreetsGeometry),
        // bereits in Weltkoordinaten. Robuster für Doppelfahrbahnen, "Dog-Leg"-Kreuzungen und Kreisverkehre mit
        // Bypass-Spuren als unsere eigene Ecken-Konstruktion. Gehweg/Randstreifen (unsere Regeln, z. B. der breite
        // Seitenstreifen auf der Victoria Road) bleiben unverändert unsere eigene Logik.
        // progress(Anteil) -> true = abbrechen
        public static void Build(RoadNet net, Transform parent, RoadMaterials mats, System.Func<Mesh, Mesh> save,
                                 System.Func<float, bool> progress = null, List<Vector2[]> externalAsphalt = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Alle Flächenstücke in Weltkoordinaten (2D: x, z)
            var asphalt = new List<Vector2[]>(); var outerU = new List<Vector2[]>(); var outerR = new List<Vector2[]>();
            var samples = new List<RoadField.Sample>(); var urbanFlag = new List<bool>();
            foreach (var sg in net.Segs)
            {
                for (int k = 0; k < sg.S.Count; k++) { samples.Add(sg.S[k]); urbanFlag.Add(sg.Urban[k]); }
                // Unsere eigenen Fahrbahnstreifen bauen wir IMMER mit (nicht nur ohne osm2streets): sie tragen
                // die streckenspezifischen Breiten, allen voran den breiten Seitenstreifen auf der Victoria
                // Road, der bei uns Teil der (dunklen) Asphaltfläche ist, nicht nur ein heller Randstreifen —
                // das kennt osm2streets nicht. Mit osm2streets-Geometrie werden dessen Flächen einfach dazu-
                // vereinigt: das liefert an Kreuzungen/Kreisverkehren/Doppelfahrbahnen die robustere Form.
                // Dort ziehen wir unsere eigenen Streifen etwas zurück (bis knapp vor den Kreuzungsrand) und
                // lassen die unmittelbare Kreuzungsfläche allein osm2streets — sonst können unsere geraden
                // Streifenenden, die nicht der wahren (oft leicht auffächernden) Straßenform folgen, als
                // kleine Zacken über die saubere Kreuzungsform hinausragen. Winzige interne Verbindungsstücke
                // innerhalb eines Kreuzungs-Clusters (sg.Internal) lässt osm2streets ohnehin allein abdecken.
                if (externalAsphalt != null)
                {
                    if (!sg.Internal)
                    {
                        float a0 = Mathf.Min(sg.TrimA * .5f, sg.TrimA), a1 = sg.Length - Mathf.Min(sg.TrimB * .5f, sg.TrimB);
                        if (a1 - a0 >= 1f)
                        {
                            int k0 = RoadNet.SampleAt(sg, a0), k1 = RoadNet.SampleAt(sg, a1);
                            if (k1 > k0) Strips(sg, k0, k1, k => 0f, k => 0f, Vector2.zero, asphalt);
                        }
                    }
                }
                else Strips(sg, 0, sg.S.Count - 1, k => 0f, k => 0f, Vector2.zero, asphalt);
                // Außenstreifen (Gehweg/Randstreifen): immer unsere eigene, streckenspezifische Regel,
                // unabhängig davon, woher die Asphaltfläche kommt.
                for (int k0 = 0; k0 < sg.S.Count - 1;)
                {
                    int k1 = k0; bool u = sg.Urban[k0];
                    while (k1 < sg.S.Count - 1 && sg.Urban[k1] == u) k1++;
                    Strips(sg, k0, k1, k => OuterW(sg, k, true), k => OuterW(sg, k, false), Vector2.zero, u ? outerU : outerR);
                    k0 = k1;
                }
            }
            int fillets = 0;
            // Kreuzungen: osm2streets-Flächen dazuvereinigen (robust) statt unserer eigenen Ecken-Konstruktion.
            if (externalAsphalt != null) asphalt.AddRange(externalAsphalt);
            else foreach (var j in net.Junctions) fillets += AddFillets(net, j, asphalt, Vector2.zero);
            var near = new RoadField(samples);

            // Kacheln: welche Stücke berühren welche Kachel (über die Hüllrechtecke)
            var tiles = new Dictionary<long, TileSet>();
            Index(asphalt, 0, tiles); Index(outerU, 1, tiles); Index(outerR, 2, tiles);
            var keys = new List<long>(tiles.Keys); keys.Sort();

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
                if (ts.A.Count == 0) continue;
                int tx = (int)(key >> 32), tz = (int)(uint)key;
                Vector2 o = new Vector2((tx + .5f) * Tile, (tz + .5f) * Tile);      // Kachelmitte = lokaler Ursprung
                try
                {
                    var a = Clip(asphalt, ts.A, o); var u = Clip(outerU, ts.U, o); var r = Clip(outerR, ts.R, o);
                    triCount += ProcessTile(a, u, r, o, near, urbanFlag, PartsAt, ref curbs);
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"Straßenoberfläche: Kachel {tx}/{tz} übersprungen ({ex.GetType().Name}: {ex.Message})");
                }
            }

            // Markierungen: je Abschnitt bis zum Kreuzungsbeginn (Beschnitt), nie in Kreuzungen
            foreach (var sg in net.Segs)
            {
                if (sg.Internal) continue;
                float s0 = sg.TrimA, s1 = sg.Length - sg.TrimB;
                if (s1 - s0 < 2f) continue;
                var sub = new List<RoadField.Sample>(); var le = new List<bool>(); var re = new List<bool>(); var rank = new List<int>();
                System.Action<RoadField.Sample, int> Add = (smp, k) =>
                {
                    sub.Add(smp); rank.Add(sg.Rank[k]);
                    le.Add(!sg.Urban[k] && sg.LeftKind[k] != RoadNet.KindMedian);
                    re.Add(!sg.Urban[k] && sg.RightKind[k] != RoadNet.KindMedian);
                };
                Add(RoadNet.At(sg, s0), RoadNet.SampleAt(sg, s0));
                for (int i = 0; i < sg.S.Count; i++) if (sg.S[i].distance > s0 + .2f && sg.S[i].distance < s1 - .2f) Add(sg.S[i], i);
                Add(RoadNet.At(sg, s1), RoadNet.SampleAt(sg, s1));
                RoadProfile.EmitMarkings(PartsAt(sub[sub.Count / 2].pos), sub, 0, sub.Count - 1,
                                         i => le[i], i => re[i], i => rank[i] <= 3 && !sg.Oneway);
            }

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
            string src = externalAsphalt != null ? $"+ {externalAsphalt.Count} osm2streets-Flächen" : $"+ {fillets} Bordsteinecken (eigene Vereinigung)";
            Debug.Log($"Straßenoberfläche{state}: {asphalt.Count} Fahrbahnstücke {src} in {keys.Count} Kacheln, " +
                      $"{triCount} Dreiecke, {curbs} Bordsteinkanten, {meshes} Meshes, {clock.ElapsedMilliseconds} ms.");
        }

        private sealed class TileSet { public readonly List<int> A = new List<int>(), U = new List<int>(), R = new List<int>(); }

        private static void Index(List<Vector2[]> pieces, int kind, Dictionary<long, TileSet> tiles)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                var c = pieces[i];
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                foreach (var q in c) { x0 = Mathf.Min(x0, q.x); z0 = Mathf.Min(z0, q.y); x1 = Mathf.Max(x1, q.x); z1 = Mathf.Max(z1, q.y); }
                for (int tx = Mathf.FloorToInt(x0 / Tile); tx <= Mathf.FloorToInt(x1 / Tile); tx++)
                for (int tz = Mathf.FloorToInt(z0 / Tile); tz <= Mathf.FloorToInt(z1 / Tile); tz++)
                {
                    long key = ((long)tx << 32) | (uint)tz;
                    if (!tiles.TryGetValue(key, out var ts)) { ts = new TileSet(); tiles[key] = ts; }
                    (kind == 0 ? ts.A : kind == 1 ? ts.U : ts.R).Add(i);
                }
            }
        }

        // Stücke exakt auf die Kachel beschneiden (Sutherland-Hodgman, Kachel = Rechteck ±Tile/2 um o)
        private static List<Vector2[]> Clip(List<Vector2[]> pieces, List<int> idx, Vector2 o)
        {
            var res = new List<Vector2[]>(idx.Count);
            float h = Tile * .5f;
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
                    if (side < 2) q.x = v; else q.y = v;                // exakt auf der Grenze (gleiche Punkte in beiden Kacheln)
                    r.Add(q);
                }
            }
            return r;
        }
        private static bool Inside(Vector2 q, int side, float v) => side == 0 ? q.x >= v : side == 1 ? q.x <= v : side == 2 ? q.y >= v : q.y <= v;

        private static bool OnTileBorder(Vector2 a, Vector2 b)
        {
            float h = Tile * .5f, e = 1e-3f;
            return (Mathf.Abs(a.x - h) < e && Mathf.Abs(b.x - h) < e) || (Mathf.Abs(a.x + h) < e && Mathf.Abs(b.x + h) < e) ||
                   (Mathf.Abs(a.y - h) < e && Mathf.Abs(b.y - h) < e) || (Mathf.Abs(a.y + h) < e && Mathf.Abs(b.y + h) < e);
        }

        private static long ProcessTile(List<Vector2[]> asphalt, List<Vector2[]> outerU, List<Vector2[]> outerR, Vector2 o,
                                        RoadField near, List<bool> urbanFlag, System.Func<Vector3, RoadProfile.Parts> PartsAt, ref int curbs)
        {
            var ua = Contours(asphalt, WindingRule.NonZero);
            var uu = Contours(outerU, WindingRule.NonZero);
            var ur = Contours(outerR, WindingRule.NonZero);
            var asphaltTris = Refine(Triangles(asphalt, null, WindingRule.NonZero), 15f);
            var urbanTris = Refine(Triangles(uu, Reverse(ua), WindingRule.Positive), 15f);
            var rev = Reverse(ua); rev.AddRange(Reverse(uu));
            var ruralTris = Refine(Triangles(ur, rev, WindingRule.Positive), 15f);
            var allOuter = new List<Vector2[]>(uu); allOuter.AddRange(ur);
            var outline = Contours(allOuter, WindingRule.NonZero, false);

            System.Func<Vector2, float, float, Vector3> Lift = (p2, baseOff, outerOff) =>
            {
                var w = new Vector3(p2.x + o.x, 0f, p2.y + o.y);
                if (near.Nearest(w.x, w.z, 20f, out int i, out float d))
                {
                    var s = near.Samples[i];
                    float t = outerOff == baseOff ? 0f : Mathf.Clamp01((d - s.half) / 2f);
                    w.y = s.pos.y + Mathf.Lerp(baseOff, outerOff, t);
                }
                return w;
            };
            cellOrigin = o;
            EmitTris(asphaltTris, 0, p => Lift(p, .02f, .02f), PartsAt);
            EmitTris(urbanTris, 4, p => Lift(p, .16f, .16f), PartsAt);
            EmitTris(ruralTris, 1, p => Lift(p, .015f, -.06f), PartsAt);

            // Bordsteinkanten (innerorts) entlang des Asphaltrands, Schürzen am Außenrand (nicht an Kachelgrenzen)
            foreach (var c in ua)
                for (int i = 0; i < c.Length; i++)
                {
                    Vector2 a2 = c[i], b2 = c[(i + 1) % c.Length];
                    if (OnTileBorder(a2, b2)) continue;
                    Vector2 m2 = (a2 + b2) * .5f;
                    if (!near.Nearest(m2.x + o.x, m2.y + o.y, 20f, out int si, out _) || !urbanFlag[si]) continue;
                    Vector3 a = Lift(a2, .02f, .02f), b = Lift(b2, .02f, .02f);
                    Wall(PartsAt(a), 4, a, b, .14f, 0f);          // Asphalt liegt links der Umrisskante -> Kante zeigt zur Fahrbahn
                    curbs++;
                }
            foreach (var c in outline)
                for (int i = 0; i < c.Length; i++)
                {
                    if (OnTileBorder(c[i], c[(i + 1) % c.Length])) continue;
                    Vector3 a = Lift(c[i], .02f, .02f), b = Lift(c[(i + 1) % c.Length], .02f, .02f);
                    Wall(PartsAt(a), 1, b - Vector3.up * .08f, a - Vector3.up * .08f, 0f, -1.4f);   // nach außen sichtbar
                }
            return (asphaltTris.Count + urbanTris.Count + ruralTris.Count) / 3;
        }

        // ------------------------------------------------------------------ Geometrie-Bausteine
        private static float OuterW(RoadNet.Segment sg, int k, bool left)
        {
            byte kind = left ? sg.LeftKind[k] : sg.RightKind[k];
            if (kind == RoadNet.KindMedian) return .35f;          // Mittelstreifen bleibt frei (Palmen)
            return sg.Urban[k] ? 2f : 1.6f;
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
