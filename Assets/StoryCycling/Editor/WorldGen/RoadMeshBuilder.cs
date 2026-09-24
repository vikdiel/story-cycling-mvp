using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Baut die Straße als Mesh-Abschnitte (Frustum-Culling!) mit:
    //   - Entdoppelung: Strava-Hin-/Rückweg liegen exakt übereinander -> nur EINE Fahrbahn
    //     (vorher zwei deckungsgleiche Ribbons = Z-Fighting/Flackern auf ~46 % der Strecke)
    //   - Asphalt-Textur, gelbe Randlinien (Südafrika), weiße unterbrochene Mittellinie
    //   - Bankett + nach unten laufende Schürze (keine Lücke zum Gelände)
    //   - Leitplanken automatisch an der Talseite, wo das Gelände stark abfällt
    public sealed class RoadMeshBuilder
    {
        public const float HalfWidth = 4f;
        private const float ShoulderOuter = 5.6f;
        private const float PieceSamples = 250;          // 500 m pro Mesh-Abschnitt
        private readonly RoadField road;
        private readonly WorldTerrain terrain;

        public RoadMeshBuilder(RoadField road, WorldTerrain terrain) { this.road = road; this.terrain = terrain; }

        public void Build(Transform parent, Material asphalt, Material shoulder, Material yellow, Material white,
                          Material rail, System.Func<Mesh, Mesh> save)
        {
            bool[] draw = ComputeCoverage();
            int pieces = 0, railPieces = 0;
            var s = road.Samples;
            int i = 0;
            while (i < s.Count)
            {
                if (!draw[i]) { i++; continue; }
                int start = Mathf.Max(0, i - 1);                     // 1 Probe Überlappung gegen Risse
                int end = i;
                while (end + 1 < s.Count && draw[end + 1] && end - start < PieceSamples) end++;
                int last = Mathf.Min(s.Count - 1, end + 1);

                Mesh mesh = BuildPiece(start, last);
                mesh.name = $"Road_{pieces:D3}";
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterials = new[] { asphalt, shoulder, yellow, white };
                mr.shadowCastingMode = ShadowCastingMode.Off;
                pieces++;

                Mesh rails = BuildRails(start, last);
                if (rails != null)
                {
                    rails.name = $"Guardrail_{railPieces:D3}";
                    rails = save(rails);
                    var rg = new GameObject(rails.name, typeof(MeshFilter), typeof(MeshRenderer));
                    rg.transform.SetParent(parent, false);
                    rg.GetComponent<MeshFilter>().sharedMesh = rails;
                    rg.GetComponent<MeshRenderer>().sharedMaterial = rail;
                    railPieces++;
                }
                i = end + 1;
            }
            Debug.Log($"Straße: {pieces} Abschnitte, {railPieces} mit Leitplanke.");
        }

        // true = diese Probe wird gezeichnet; false = liegt auf einem bereits gezeichneten Abschnitt.
        private bool[] ComputeCoverage()
        {
            var s = road.Samples;
            var draw = new bool[s.Count];
            var emitted = new Dictionary<long, List<int>>();
            const float cell = 8f, near = 5f;
            int covered = 0;
            for (int i = 0; i < s.Count; i++)
            {
                Vector3 p = s[i].pos;
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
                bool isCovered = false;
                for (int dx = -1; dx <= 1 && !isCovered; dx++)
                for (int dz = -1; dz <= 1 && !isCovered; dz++)
                {
                    if (!emitted.TryGetValue(Key(cx + dx, cz + dz), out List<int> list)) continue;
                    foreach (int e in list)
                    {
                        var o = s[e];
                        if (Mathf.Abs(o.distance - s[i].distance) < 60f) continue;              // derselbe Abschnitt
                        if (Mathf.Abs(o.pos.y - p.y) > .6f) continue;                             // Brücke/Unterführung
                        if (Mathf.Abs(Vector3.Dot(o.side, s[i].side)) < .8f) continue;           // Kreuzung, nicht parallel
                        float d2 = (o.pos.x - p.x) * (o.pos.x - p.x) + (o.pos.z - p.z) * (o.pos.z - p.z);
                        if (d2 < near * near) { isCovered = true; break; }
                    }
                }
                draw[i] = !isCovered;
                if (isCovered) { covered++; continue; }
                long key = Key(cx, cz);
                if (!emitted.TryGetValue(key, out List<int> l)) { l = new List<int>(); emitted[key] = l; }
                l.Add(i);
            }
            // Kurze gezeichnete Inseln (GPS-Rauschen) innerhalb verdeckter Strecken entfernen.
            int runStart = -1;
            for (int i = 0; i <= s.Count; i++)
            {
                bool on = i < s.Count && draw[i];
                if (on && runStart < 0) runStart = i;
                if (!on && runStart >= 0)
                {
                    bool coveredBefore = runStart > 0 && !draw[runStart - 1];
                    bool coveredAfter = i < s.Count;
                    if (coveredBefore && coveredAfter && i - runStart < 6)
                        for (int k = runStart; k < i; k++) draw[k] = false;
                    runStart = -1;
                }
            }
            Debug.Log($"Straße entdoppelt: {covered * RoadField.Step / 1000f:0.0} km liegen auf bereits gebauter Fahrbahn.");
            return draw;
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private Mesh BuildPiece(int from, int to)
        {
            var s = road.Samples;
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tAsphalt = new List<int>();
            var tShoulder = new List<int>();
            var tYellow = new List<int>();
            var tWhite = new List<int>();

            // Querprofil (lateral, Höhe relativ zur Mittellinie). Asphalt -> Bankett -> Schürze.
            float[] lat = { -ShoulderOuter - 1f, -ShoulderOuter, -HalfWidth, HalfWidth, ShoulderOuter, ShoulderOuter + 1f };
            float[] hgt = { -1.4f, -.06f, .02f, .02f, -.06f, -1.4f };
            int cols = lat.Length;
            for (int i = from; i <= to; i++)
                for (int c = 0; c < cols; c++)
                {
                    v.Add(s[i].pos + s[i].side * lat[c] + Vector3.up * hgt[c]);
                    uv.Add(new Vector2(lat[c] / 4f, s[i].distance / 6f));
                }
            for (int r = 0; r < to - from; r++)
            {
                int a = r * cols;
                for (int c = 0; c < cols - 1; c++)
                {
                    var list = c == 2 ? tAsphalt : tShoulder;
                    Quad(list, a + c, a + c + 1, a + cols + c, a + cols + c + 1);
                }
            }

            // Gelbe Randlinien (durchgehend)
            AddStripe(v, uv, tYellow, from, to, -HalfWidth + .2f, -HalfWidth + .35f, .035f, 0f, 0f);
            AddStripe(v, uv, tYellow, from, to, HalfWidth - .35f, HalfWidth - .2f, .035f, 0f, 0f);
            // Weiße Mittellinie: 3 m Strich, 6 m Lücke
            AddStripe(v, uv, tWhite, from, to, -.07f, .07f, .035f, 3f, 9f);

            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, subMeshCount = 4 };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tAsphalt, 0);
            mesh.SetTriangles(tShoulder, 1);
            mesh.SetTriangles(tYellow, 2);
            mesh.SetTriangles(tWhite, 3);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Streifen entlang der Mittellinie. dashOn > 0 -> unterbrochen.
        private void AddStripe(List<Vector3> v, List<Vector2> uv, List<int> tris, int from, int to,
                               float left, float right, float lift, float dashOn, float dashPeriod)
        {
            var s = road.Samples;
            for (int i = from; i < to; i++)
            {
                if (dashOn > 0f && Mathf.Repeat(s[i].distance, dashPeriod) >= dashOn) continue;
                int b = v.Count;
                for (int k = 0; k < 2; k++)
                {
                    var p = s[i + k];
                    v.Add(p.pos + p.side * left + Vector3.up * lift);
                    v.Add(p.pos + p.side * right + Vector3.up * lift);
                    uv.Add(Vector2.zero); uv.Add(Vector2.zero);
                }
                Quad(tris, b, b + 1, b + 2, b + 3);
            }
        }

        // a,b = Reihe r (links->rechts), c,d = Reihe r+1. Oberseite zeigt nach oben.
        private static void Quad(List<int> t, int a, int b, int c, int d)
        {
            t.Add(a); t.Add(c); t.Add(b);
            t.Add(b); t.Add(c); t.Add(d);
        }

        // Leitplanke dort, wo das Gelände neben der Straße > 6 m abfällt (Chapman's Peak!).
        private Mesh BuildRails(int from, int to)
        {
            var s = road.Samples;
            int n = to - from + 1;
            var need = new int[n];   // -1 links, +1 rechts, 0 keine
            for (int k = 0; k < n; k++)
            {
                var p = s[from + k];
                for (int sideSign = -1; sideSign <= 1; sideSign += 2)
                {
                    Vector3 q1 = p.pos + p.side * (sideSign * 13f), q2 = p.pos + p.side * (sideSign * 22f);
                    float drop = p.pos.y - Mathf.Min(terrain.DemY(q1.x, q1.z), terrain.DemY(q2.x, q2.z));
                    if (drop > 6f) need[k] = sideSign;
                }
            }
            // Lücken < 12 m schließen, Stücke < 20 m verwerfen
            Smooth(need, 6, 10);

            var v = new List<Vector3>();
            var tris = new List<int>();
            for (int k = 0; k < n - 1; k++)
            {
                if (need[k] == 0 || need[k + 1] != need[k]) continue;
                float sideSign = need[k];
                var a = s[from + k]; var b = s[from + k + 1];
                Vector3 pa = a.pos + a.side * (sideSign * (ShoulderOuter - .3f));
                Vector3 pb = b.pos + b.side * (sideSign * (ShoulderOuter - .3f));
                // Planke (beidseitig), 0.55–0.85 m über Fahrbahn
                DoubleQuad(v, tris, pa + Vector3.up * .55f, pb + Vector3.up * .55f, pa + Vector3.up * .85f, pb + Vector3.up * .85f);
                // Pfosten alle 4 m
                if (Mathf.Repeat(a.distance, 4f) < RoadField.Step)
                    Post(v, tris, pa, a.tangent, a.side);
            }
            if (v.Count == 0) return null;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Smooth(int[] need, int maxGap, int minRun)
        {
            // Lücken füllen
            for (int k = 1; k < need.Length; k++)
            {
                if (need[k] != 0 || need[k - 1] == 0) continue;
                int e = k; while (e < need.Length && need[e] == 0) e++;
                if (e < need.Length && e - k <= maxGap && need[e] == need[k - 1])
                    for (int m = k; m < e; m++) need[m] = need[k - 1];
                k = e;
            }
            // kurze Stücke weg
            for (int k = 0; k < need.Length;)
            {
                if (need[k] == 0) { k++; continue; }
                int e = k; while (e < need.Length && need[e] == need[k]) e++;
                if (e - k < minRun) for (int m = k; m < e; m++) need[m] = 0;
                k = e;
            }
        }

        private static void DoubleQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i + 1); t.Add(i + 2); t.Add(i + 3);
            int j = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(j); t.Add(j + 1); t.Add(j + 2); t.Add(j + 1); t.Add(j + 3); t.Add(j + 2);
        }

        private static void Post(List<Vector3> v, List<int> t, Vector3 foot, Vector3 tangent, Vector3 side)
        {
            Vector3 f = new Vector3(tangent.x, 0f, tangent.z).normalized * .07f, r = side * .07f;
            Vector3 up = Vector3.up * .9f, down = Vector3.down * .4f;
            Vector3[] c = { -f - r, f - r, f + r, -f + r };
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = foot + c[k] + down, b = foot + c[(k + 1) % 4] + down;
                DoubleQuad(v, t, a, b, a - down + up, b - down + up);
            }
        }
    }
}
