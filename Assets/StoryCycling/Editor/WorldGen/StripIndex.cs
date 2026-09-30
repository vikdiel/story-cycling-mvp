using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Räumlicher Zugriff auf die Fahrbahnstreifen des Netzes (Abschnitt + Probenreihe): welcher Streifen liegt an einem Punkt,
    // wie weit ist der nächste Streifenrand, liegt ein Punkt im Kreuzungsanteil eines Abschnitts. Wird von RoadSurface
    // (Markierungen nur auf eigener Fahrbahn) und RoadChecks (Prüfung derselben Eigenschaften) genutzt.
    public sealed class StripIndex
    {
        public readonly RoadNet Net;
        public readonly RoadField Field;
        public readonly List<int> SegOf = new List<int>(), IdxOf = new List<int>();
        public readonly float MaxHalf = 4f;
        private readonly List<int> tmp = new List<int>();

        public StripIndex(RoadNet net)
        {
            Net = net;
            var all = new List<RoadField.Sample>();
            for (int si = 0; si < net.Segs.Count; si++)
                for (int k = 0; k < net.Segs[si].S.Count; k++)
                { var s = net.Segs[si].S[k]; all.Add(s); SegOf.Add(si); IdxOf.Add(k); MaxHalf = Mathf.Max(MaxHalf, s.half); }
            Field = new RoadField(all);
        }

        public static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

        // Entwurfsränder der Kreuzungsflächen (Bordsteinbögen, Knotenrundungen) -- gelten wie Streifenränder als Sollrand
        private readonly List<Vector2[]> rims = new List<Vector2[]>(); private readonly List<Vector4> rimBox = new List<Vector4>();
        public void AddRims(List<Vector2[]> list)
        {
            foreach (var r in list)
            {
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                foreach (var q in r) { x0 = Mathf.Min(x0, q.x); z0 = Mathf.Min(z0, q.y); x1 = Mathf.Max(x1, q.x); z1 = Mathf.Max(z1, q.y); }
                rims.Add(r); rimBox.Add(new Vector4(x0, z0, x1, z1));
            }
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        // Liegt q im Streifen (|quer| < half - margin) eines anderen Abschnitts als 'except'? margin < 0 weitet den Streifen.
        // ownK >= 0: auch der eigene Abschnitt zählt, aber nur weit entfernte Proben (|k - ownK| > ownRange) — z. B. die
        // Innenseite einer engen Kurve, wo der Streifen auf sich selbst zurückläuft. skip: diese Abschnitte zählen nicht.
        public bool Hit(Vector2 q, float margin, int except, out int seg, out int k0, int ownK = -1, int ownRange = 0, HashSet<int> skip = null)
        {
            seg = -1; k0 = -1;
            float R = MaxHalf + 3f;
            Field.Query(q.x - R, q.y - R, q.x + R, q.y + R, tmp);
            foreach (int i in tmp)
            {
                int si = SegOf[i]; if (si == except && ownK < 0) continue;
                if (skip != null && skip.Contains(si)) continue;
                var sg = Net.Segs[si]; int k = IdxOf[i];
                for (int d = 0; d < 2; d++)
                {
                    int a = k - d, b = a + 1;
                    if (a < 0 || b >= sg.S.Count) continue;
                    if (si == except && Mathf.Abs(a - ownK) <= ownRange) continue;
                    var sa = sg.S[a]; var sb = sg.S[b];
                    float ha = Mathf.Max(0f, sa.half - margin), hb = Mathf.Max(0f, sb.half - margin);
                    Vector2 pa = XZ(sa.pos), pb = XZ(sb.pos), na = XZ(sa.side), nb = XZ(sb.side);
                    Vector2 la = pa - na * ha, ra = pa + na * ha, lb = pb - nb * hb, rb = pb + nb * hb;
                    if (InTri(q, ra, rb, lb) || InTri(q, ra, lb, la)) { seg = si; k0 = a; return true; }
                }
            }
            return false;
        }

        // Kreuzungsanteil: abgeschnittenes Ende eines Abschnitts (innerhalb seiner Trim-Länge) oder Verbindungsstück
        // zwischen zwei Kreuzungsknoten (Internal). Ersatzfahrbahnen zählen nicht.
        public bool JunctionPart(Vector2 q, float tol)
        {
            Field.Query(q.x - 3f, q.y - 3f, q.x + 3f, q.y + 3f, tmp);
            foreach (int i in tmp)
            {
                var sg = Net.Segs[SegOf[i]]; int k = IdxOf[i]; var s = sg.S[k];
                if (sg.Fallback) continue;
                Vector2 d = q - XZ(s.pos);
                if (Mathf.Abs(Vector2.Dot(d, XZ(s.side))) > s.half - .05f || Mathf.Abs(Vector2.Dot(d, XZ(s.tangent).normalized)) > 1.1f) continue;
                if (sg.Internal || s.distance < sg.TrimA - tol || s.distance > sg.Length - sg.TrimB + tol) return true;
            }
            return false;
        }

        private static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float l2 = ab.sqrMagnitude;
            float t = l2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return (a + ab * t - p).magnitude;
        }

        // Abstand von q zum Sollrand aller Fahrbahnstreifen (Längskanten bei ±half, Stirnkanten an den Abschnittsenden)
        public float DistToBoundary(Vector2 q, float rad)
        {
            float R = rad + MaxHalf + 2f, best = float.MaxValue;
            Field.Query(q.x - R, q.y - R, q.x + R, q.y + R, tmp);
            foreach (int i in tmp)
            {
                var sg = Net.Segs[SegOf[i]]; int k = IdxOf[i]; var sa = sg.S[k];
                if ((XZ(sa.pos) - q).magnitude > sa.half + rad + 2.5f) continue;
                // beide Längskanten-Stücke an dieser Probe (k-1..k und k..k+1); Fallback-Abschnitte können lange Stücke haben
                for (int d = -1; d <= 0; d++)
                {
                    int a = k + d, b = a + 1; if (a < 0 || b >= sg.S.Count) continue;
                    var sa2 = sg.S[a]; var sb = sg.S[b];
                    Vector2 pa = XZ(sa2.pos), pb = XZ(sb.pos), na = XZ(sa2.side), nb = XZ(sb.side);
                    Vector2 la = pa - na * sa2.half, ra = pa + na * sa2.half, lb = pb - nb * sb.half, rb = pb + nb * sb.half;
                    best = Mathf.Min(best, Mathf.Min(DistSeg(q, la, lb), DistSeg(q, ra, rb)));
                }
                if (k == 0 || k == sg.S.Count - 1)
                {
                    Vector2 pa = XZ(sa.pos), na = XZ(sa.side);
                    best = Mathf.Min(best, DistSeg(q, pa - na * sa.half, pa + na * sa.half));
                }
            }
            for (int r = 0; r < rims.Count && best > .05f; r++)
            {
                var bx = rimBox[r];
                if (q.x < bx.x - EdgeReach || q.x > bx.z + EdgeReach || q.y < bx.y - EdgeReach || q.y > bx.w + EdgeReach) continue;
                var c = rims[r];
                for (int i = 0; i + 1 < c.Length; i++) best = Mathf.Min(best, DistSeg(q, c[i], c[i + 1]));
            }
            return best;
        }
        private const float EdgeReach = .5f;

        // Abstand zum nächsten Entwurfsrand einer Kreuzungsfläche (Bordsteinbogen, Knotenrundung) bis r, sonst float.MaxValue
        public float DistToRim(Vector2 q, float r)
        {
            float best = float.MaxValue;
            for (int i = 0; i < rims.Count; i++)
            {
                var bx = rimBox[i];
                if (q.x < bx.x - r || q.x > bx.z + r || q.y < bx.y - r || q.y > bx.w + r) continue;
                var c = rims[i];
                for (int j = 0; j + 1 < c.Length; j++) best = Mathf.Min(best, DistSeg(q, c[j], c[j + 1]));
            }
            return best <= r ? best : float.MaxValue;
        }
    }
}
