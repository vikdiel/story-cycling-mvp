using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Spurgraph für den Hintergrundverkehr: Fahrspuren (Polylinien in Fahrtrichtung, Linksverkehr) und Verbinder durch die
    // Kreuzungen. Wird im Editor aus dem RoadNet gebacken (TrafficGraphBuilder), als <route>.traffic.txt nach StreamingAssets
    // geschrieben und zur Laufzeit von TrafficSystem geladen. Reine Daten + Text-Format, keine Unity-Objekte.
    public sealed class TrafficGraph
    {
        public const string Header = "# StoryCycling baked traffic v1";
        public const byte Road = 0, Conn = 1;                                   // Spurtyp
        public const byte TurnStraight = 0, TurnLeft = 1, TurnRight = 2, TurnU = 3;
        public const int FlagRing = 1, FlagUrban = 2, FlagSink = 4, FlagRoute = 8;
        public const byte HubJunction = 0, HubRing = 1, HubDeadEnd = 2, HubSink = 3, HubThrough = 4;
        public const float ConflictDistance = 2.4f;                             // Verbinder näher als das (Mittellinie zu Mittellinie) gelten als kreuzend

        public sealed class Hub
        {
            public int Id; public float X, Y, Z; public byte Kind;
            public int[] Conns = new int[0];                                    // Spur-IDs der Verbinder dieser Kreuzung
            public bool[] Conflict;                                             // n*n über Conns: Verbinder schließen sich aus
        }

        public sealed class Lane
        {
            public int Id; public byte Type;
            public int Hub = -1;                                                // Straße: Kreuzung am Spurende; Verbinder: seine Kreuzung
            public int Slot;                                                    // Verbinder: Index in Hub.Conns
            public float Speed;                                                 // Sollgeschwindigkeit m/s (Verbinder: Kurvengeschwindigkeit)
            public float Room;                                                  // Straße: Abstand Spurmitte -> rechter Fahrbahnrand (Fahrtrichtung)
            public float Weight;                                                // Straße: Gewicht beim Spawnen
            public byte Prio, Turn;                                             // Verbinder: Vorrang 0..3, Abbiegeart
            public int Flags;
            public int[] Next = new int[0];                                     // Straße: Verbinder; Verbinder: genau eine Straße
            public float[] X, Y, Z, S, Cap;
            public int N; public float Length;

            public bool Ring => (Flags & FlagRing) != 0;
            public bool Sink => (Flags & FlagSink) != 0;

            // Punkt und Richtung bei Bogenlänge s (geklemmt); hint = zuletzt benutzter Abschnitt (spart die Suche)
            public void Eval(float s, ref int hint, out float x, out float y, out float z, out float hx, out float hz)
            {
                int i = hint;
                if (i < 0 || i > N - 2) i = 0;
                while (i > 0 && S[i] > s) i--;
                while (i < N - 2 && S[i + 1] < s) i++;
                hint = i;
                float seg = S[i + 1] - S[i];
                float t = seg > 1e-4f ? Mathf.Clamp01((s - S[i]) / seg) : 0f;
                x = X[i] + (X[i + 1] - X[i]) * t; y = Y[i] + (Y[i + 1] - Y[i]) * t; z = Z[i] + (Z[i + 1] - Z[i]) * t;
                if (seg > 1e-4f) { hx = (X[i + 1] - X[i]) / seg; hz = (Z[i + 1] - Z[i]) / seg; }
                else { hx = 0f; hz = 1f; }
            }

            // Kleinster Kurvenradius-Grenzwert ab Punkt i (für die Vorausschau)
            public float CapAt(int i) => Cap[Mathf.Clamp(i, 0, N - 1)];
        }

        public readonly List<Hub> Hubs = new List<Hub>();
        public readonly List<Lane> Lanes = new List<Lane>();
        public readonly List<Vector3> Parked = new List<Vector3>();              // x, z, Gierwinkel (rad) der parkenden Autos
        public const float ChordHalf = 6f;                                       // halber Messabstand (m Bogenlänge) der Kurvenkrümmung auf Straßenspuren
        public float LateralAcc = 2.2f;                                          // zulässige Querbeschleunigung für Kurvengeschwindigkeiten (m/s²)

        // ------------------------------------------------------------------ Abschluss (Längen, Kurvenlimits, Konflikte)
        public void Finish()
        {
            foreach (var h in Hubs) h.Conns = new int[0];
            var perHub = new Dictionary<int, List<int>>();
            foreach (var l in Lanes)
            {
                l.N = l.X.Length;
                l.S = new float[l.N];
                for (int i = 1; i < l.N; i++)
                {
                    float dx = l.X[i] - l.X[i - 1], dz = l.Z[i] - l.Z[i - 1], dy = l.Y[i] - l.Y[i - 1];
                    l.S[i] = l.S[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
                }
                l.Length = l.S[l.N - 1];
                ComputeCap(l);
                if (l.Type == Conn && l.Hub >= 0)
                {
                    if (!perHub.TryGetValue(l.Hub, out var list)) { list = new List<int>(); perHub[l.Hub] = list; }
                    l.Slot = list.Count; list.Add(l.Id);
                }
            }
            foreach (var kv in perHub) Hubs[kv.Key].Conns = kv.Value.ToArray();
            foreach (var h in Hubs) ComputeConflicts(h);
        }

        // Menger-Krümmung je Stützpunkt -> Grenzgeschwindigkeit sqrt(a_quer / kappa)
        // Straßenspuren: Kreis durch drei Punkte im Abstand +-6 m Bogenlänge (glättet Geometrie-Rauschen im Dezimeterbereich,
        // das sonst einzelne Stützpunkte zu scheinbaren Haarnadeln macht); Verbinder sind glatte Kurven: Nachbarpunkte.
        private void ComputeCap(Lane l)
        {
            l.Cap = new float[l.N];
            for (int i = 0; i < l.N; i++) l.Cap[i] = 60f;
            if (l.Type == Road)
            {
                int h0 = 0, h2 = 0;
                for (int i = 1; i < l.N - 1; i++)
                {
                    float s = l.S[i], d = Mathf.Min(ChordHalf, Mathf.Min(s, l.Length - s));
                    if (d < 1.5f) continue;
                    l.Eval(s - d, ref h0, out float ax0, out _, out float az0, out _, out _);
                    l.Eval(s + d, ref h2, out float cx0, out _, out float cz0, out _, out _);
                    float ax = l.X[i] - ax0, az = l.Z[i] - az0, bx = cx0 - l.X[i], bz = cz0 - l.Z[i];
                    float la = Mathf.Sqrt(ax * ax + az * az), lb = Mathf.Sqrt(bx * bx + bz * bz);
                    if (la < .05f || lb < .05f) continue;
                    float cx = cx0 - ax0, cz = cz0 - az0, lc = Mathf.Sqrt(cx * cx + cz * cz);
                    float cross = Mathf.Abs(ax * bz - az * bx);
                    float kappa = lc > .05f ? 2f * cross / (la * lb * lc) : 0f;
                    if (kappa > 1e-4f) l.Cap[i] = Mathf.Clamp(Mathf.Sqrt(LateralAcc / kappa), 2.5f, 60f);
                }
                return;
            }
            for (int i = 1; i < l.N - 1; i++)
            {
                float ax = l.X[i] - l.X[i - 1], az = l.Z[i] - l.Z[i - 1], bx = l.X[i + 1] - l.X[i], bz = l.Z[i + 1] - l.Z[i];
                float la = Mathf.Sqrt(ax * ax + az * az), lb = Mathf.Sqrt(bx * bx + bz * bz);
                if (la < .05f || lb < .05f) continue;
                float cx = l.X[i + 1] - l.X[i - 1], cz = l.Z[i + 1] - l.Z[i - 1], lc = Mathf.Sqrt(cx * cx + cz * cz);
                float cross = Mathf.Abs(ax * bz - az * bx);
                float kappa = lc > .05f ? 2f * cross / (la * lb * lc) : 0f;
                if (kappa > 1e-4f) l.Cap[i] = Mathf.Clamp(Mathf.Sqrt(LateralAcc / kappa), 2.5f, 60f);
            }
            // ein Eckpunkt einer groben Polylinie gilt für die halbe Nachbarschaft mit: Minimum mit den Nachbarn (bis 6 m)
            var c2 = (float[])l.Cap.Clone();
            for (int i = 0; i < l.N; i++)
            {
                for (int j = i - 1; j >= 0 && l.S[i] - l.S[j] <= 6f; j--) c2[i] = Mathf.Min(c2[i], l.Cap[j]);
                for (int j = i + 1; j < l.N && l.S[j] - l.S[i] <= 6f; j++) c2[i] = Mathf.Min(c2[i], l.Cap[j]);
            }
            l.Cap = c2;
        }

        // Laufen zwei Pfade mit gemeinsamem Start erst auseinander (> 3,6 m) und nähern sich dann wieder (< ConflictDistance), kreuzen sie sich
        private static bool Recross(List<Vector2> a, List<Vector2> b)
        {
            bool diverged = false; float d2c = ConflictDistance * ConflictDistance, d2d = 3.6f * 3.6f;
            for (int i = 0; i < a.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < b.Count; j++) { float dx = a[i].x - b[j].x, dz = a[i].y - b[j].y; float d = dx * dx + dz * dz; if (d < best) best = d; }
                if (!diverged) { if (best > d2d) diverged = true; }
                else if (best < d2c) return true;
            }
            return false;
        }

        private void ComputeConflicts(Hub h)
        {
            int n = h.Conns.Length;
            h.Conflict = new bool[n * n];
            if (n < 2) return;
            // Polylinien im 1-m-Raster abtasten
            var pts = new List<Vector2>[n];
            for (int a = 0; a < n; a++)
            {
                var l = Lanes[h.Conns[a]]; var list = new List<Vector2>(); int hint = 0;
                for (float s = 0f; s <= l.Length + .01f; s += 1f)
                { l.Eval(Mathf.Min(s, l.Length), ref hint, out float x, out _, out float z, out _, out _); list.Add(new Vector2(x, z)); }
                // die ersten Meter der Ausfahrtspur gehören noch zum Kreuzungsbereich (Auto steht dort noch im Weg anderer Verbinder)
                var ex = Lanes[l.Next[0]]; int eh = 0;
                for (float s = 1f; s <= 8f && s <= ex.Length; s += 1f)
                { ex.Eval(s, ref eh, out float x, out _, out float z, out _, out _); list.Add(new Vector2(x, z)); }
                pts[a] = list;
            }
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    var la = Lanes[h.Conns[a]]; var lb = Lanes[h.Conns[b]];
                    bool conflict;
                    if (la.Next[0] == lb.Next[0]) conflict = true;                               // münden in dieselbe Spur
                    else if (Start(la) == Start(lb)) conflict = Recross(pts[a], pts[b]) || Recross(pts[b], pts[a]);   // gemeinsame Zufahrtspur: nur wenn sich die Pfade nach dem Auseinanderlaufen wieder annähern
                    else
                    {
                        conflict = false;
                        float d2 = ConflictDistance * ConflictDistance;
                        for (int i = 0; i < pts[a].Count && !conflict; i++)
                            for (int j = 0; j < pts[b].Count; j++)
                            {
                                float dx = pts[a][i].x - pts[b][j].x, dz = pts[a][i].y - pts[b][j].y;
                                if (dx * dx + dz * dz < d2) { conflict = true; break; }
                            }
                    }
                    h.Conflict[a * n + b] = h.Conflict[b * n + a] = conflict;
                }
        }

        private Dictionary<int, int> startOf;
        // Zufahrtspur eines Verbinders (die Straßenspur, deren Next ihn enthält)
        private int Start(Lane conn)
        {
            if (startOf == null)
            {
                startOf = new Dictionary<int, int>();
                foreach (var l in Lanes) if (l.Type == Road) foreach (int c in l.Next) startOf[c] = l.Id;
            }
            return startOf.TryGetValue(conn.Id, out int s) ? s : -1 - conn.Id;
        }
        public int StartLane(int conn) => Start(Lanes[conn]);

        // ------------------------------------------------------------------ Text-Format
        // hub <id> <x> <y> <z> <kind>
        // lane <id> <R|C> <hub> <speed m/s> <room> <weight> <prio> <turn> <flags> <next,next|-> <n> x y z x y z ...
        // parked <x> <z> <yaw>
        public string ToText()
        {
            var c = CultureInfo.InvariantCulture; var sb = new StringBuilder(1 << 20);
            sb.Append(Header).Append('\n');
            foreach (var h in Hubs)
                sb.Append("hub ").Append(h.Id).Append(' ').Append(h.X.ToString("0.##", c)).Append(' ').Append(h.Y.ToString("0.##", c)).Append(' ')
                  .Append(h.Z.ToString("0.##", c)).Append(' ').Append((int)h.Kind).Append('\n');
            foreach (var l in Lanes)
            {
                sb.Append("lane ").Append(l.Id).Append(' ').Append(l.Type == Road ? 'R' : 'C').Append(' ').Append(l.Hub).Append(' ')
                  .Append(l.Speed.ToString("0.##", c)).Append(' ').Append(l.Room.ToString("0.##", c)).Append(' ').Append(l.Weight.ToString("0.##", c)).Append(' ')
                  .Append((int)l.Prio).Append(' ').Append((int)l.Turn).Append(' ').Append(l.Flags).Append(' ');
                if (l.Next.Length == 0) sb.Append('-');
                else for (int i = 0; i < l.Next.Length; i++) { if (i > 0) sb.Append(','); sb.Append(l.Next[i]); }
                sb.Append(' ').Append(l.X.Length);
                for (int i = 0; i < l.X.Length; i++)
                    sb.Append(' ').Append(l.X[i].ToString("0.##", c)).Append(' ').Append(l.Y[i].ToString("0.##", c)).Append(' ').Append(l.Z[i].ToString("0.##", c));
                sb.Append('\n');
            }
            foreach (var p in Parked)
                sb.Append("parked ").Append(p.x.ToString("0.##", c)).Append(' ').Append(p.y.ToString("0.##", c)).Append(' ').Append(p.z.ToString("0.###", c)).Append('\n');
            return sb.ToString();
        }

        public static TrafficGraph Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Header)) return null;
            var c = CultureInfo.InvariantCulture; var g = new TrafficGraph();
            try
            {
                int pos = 0, len = text.Length;
                while (pos < len)
                {
                    int e = text.IndexOf('\n', pos); if (e < 0) e = len;
                    if (e > pos && text[pos] != '#')
                    {
                        var f = text.Substring(pos, e - pos).Trim().Split(' ');
                        if (f[0] == "hub")
                            g.Hubs.Add(new Hub { Id = int.Parse(f[1], c), X = float.Parse(f[2], c), Y = float.Parse(f[3], c), Z = float.Parse(f[4], c), Kind = byte.Parse(f[5], c) });
                        else if (f[0] == "lane")
                        {
                            var l = new Lane
                            {
                                Id = int.Parse(f[1], c), Type = f[2] == "R" ? Road : Conn, Hub = int.Parse(f[3], c), Speed = float.Parse(f[4], c),
                                Room = float.Parse(f[5], c), Weight = float.Parse(f[6], c), Prio = byte.Parse(f[7], c), Turn = byte.Parse(f[8], c), Flags = int.Parse(f[9], c)
                            };
                            if (f[10] != "-") { var nx = f[10].Split(','); l.Next = new int[nx.Length]; for (int i = 0; i < nx.Length; i++) l.Next[i] = int.Parse(nx[i], c); }
                            int n = int.Parse(f[11], c);
                            l.X = new float[n]; l.Y = new float[n]; l.Z = new float[n];
                            for (int i = 0; i < n; i++)
                            { l.X[i] = float.Parse(f[12 + 3 * i], c); l.Y[i] = float.Parse(f[13 + 3 * i], c); l.Z[i] = float.Parse(f[14 + 3 * i], c); }
                            if (l.Id != g.Lanes.Count || n < 2) return null;
                            g.Lanes.Add(l);
                        }
                        else if (f[0] == "parked") g.Parked.Add(new Vector3(float.Parse(f[1], c), float.Parse(f[2], c), float.Parse(f[3], c)));
                    }
                    pos = e + 1;
                }
                for (int i = 0; i < g.Hubs.Count; i++) if (g.Hubs[i].Id != i) return null;
                foreach (var l in g.Lanes)
                {
                    if (l.Hub >= g.Hubs.Count) return null;
                    foreach (int nx in l.Next) if (nx < 0 || nx >= g.Lanes.Count) return null;
                }
            }
            catch (Exception) { return null; }
            g.Finish();
            return g;
        }
    }
}
