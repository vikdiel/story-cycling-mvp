using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Steinschlaggalerien (OSM tunnel=avalanche_protector, z. B. Chapman's Peak Drive): Betondach über der Fahrbahn,
    // bergseitig eine Wand, talseitig Stützen. Die Bergseite ergibt sich aus dem ursprünglichen Gelände (DEM ohne
    // Routenkorrektur): dort, wo es neben der Straße ansteigt.
    public static class GalleryBuilder
    {
        private const float Clear = 5.2f, Slab = .55f, ColumnEvery = 7f, ColumnSize = .55f, MinLength = 6f;

        public static int Build(RoadNet net, System.Func<float, float, float> rawTerrainY, Transform parent,
                                RoadMaterials mats, System.Func<Mesh, Mesh> save)
        {
            int runs = 0; float total = 0f;
            foreach (var sg in net.Segs)
            {
                if (sg.Covered.Count != sg.S.Count) continue;
                for (int i = 0; i < sg.S.Count;)
                {
                    if (!sg.Covered[i]) { i++; continue; }
                    int e = i; while (e < sg.S.Count && sg.Covered[e]) e++;
                    int a = Mathf.Max(0, i - 1), b = Mathf.Min(sg.S.Count - 1, e);
                    float len = sg.S[b].distance - sg.S[a].distance;
                    if (len >= MinLength)
                    {
                        var parts = new RoadProfile.Parts();
                        BuildRun(parts, sg.S, a, b, rawTerrainY);
                        Mesh mesh = parts.ToMesh();
                        mesh.name = $"Gallery_{runs:D2}";
                        mesh = save(mesh);
                        var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                        go.transform.SetParent(parent, false);
                        go.GetComponent<MeshFilter>().sharedMesh = mesh;
                        go.GetComponent<MeshRenderer>().sharedMaterials = mats.Ribbon;
                        runs++; total += len;
                    }
                    i = e;
                }
            }
            if (runs > 0) Debug.Log($"Galerien: {runs} Bauwerke, {total:0} m überdachte Straße.");
            return runs;
        }

        private static void BuildRun(RoadProfile.Parts p, List<RoadField.Sample> s, int a, int b, System.Func<float, float, float> terrainY)
        {
            // Bergseite: +1 = rechts (side), -1 = links
            float score = 0f;
            for (int i = a; i <= b; i++)
            {
                var q = s[i]; float d = q.half + 20f;
                Vector3 r = q.pos + q.side * d, l = q.pos - q.side * d;
                score += terrainY(r.x, r.z) - terrainY(l.x, l.z);
            }
            float m = score >= 0f ? 1f : -1f;
            System.Func<int, float, float, Vector3> P = (i, x, h) => s[i].pos + s[i].side * (m * x) + Vector3.up * h;
            for (int i = a; i < b; i++)
            {
                float hm0 = s[i].half + 1.1f, hm1 = s[i + 1].half + 1.1f;        // Wand (Bergseite)
                float hs0 = -(s[i].half + 1.3f), hs1 = -(s[i + 1].half + 1.3f);  // Dachkante (Talseite)
                // Dach: Unterseite, Oberseite, talseitige Stirn
                Face(p, P(i, hs0, Clear), P(i + 1, hs1, Clear), P(i, hm0, Clear), P(i + 1, hm1, Clear));
                Face(p, P(i, hs0, Clear + Slab), P(i + 1, hs1, Clear + Slab), P(i, hm0 + .6f, Clear + Slab), P(i + 1, hm1 + .6f, Clear + Slab));
                Face(p, P(i, hs0, Clear), P(i + 1, hs1, Clear), P(i, hs0, Clear + Slab), P(i + 1, hs1, Clear + Slab));
                // Bergwand
                Face(p, P(i, hm0, -.4f), P(i + 1, hm1, -.4f), P(i, hm0, Clear + Slab), P(i + 1, hm1, Clear + Slab));
            }
            // Stützen talseitig
            float next = s[a].distance + 2f;
            for (int i = a; i <= b; i++)
            {
                if (s[i].distance < next) continue;
                next = s[i].distance + ColumnEvery;
                float x = -(s[i].half + .9f);
                Vector3 c = P(i, x, 0f), t = new Vector3(s[i].tangent.x, 0f, s[i].tangent.z).normalized, sd = s[i].side * m;
                float h = ColumnSize * .5f;
                Vector3[] q = { c - t * h - sd * h, c + t * h - sd * h, c + t * h + sd * h, c - t * h + sd * h };
                for (int k = 0; k < 4; k++)
                {
                    Vector3 u = q[k], v = q[(k + 1) % 4];
                    Face(p, u + Vector3.down * .4f, v + Vector3.down * .4f, u + Vector3.up * Clear, v + Vector3.up * Clear);
                }
            }
            // Stirnseiten (Portale): Dachquerschnitt an beiden Enden
            foreach (int i in new[] { a, b })
                Face(p, P(i, -(s[i].half + 1.3f), Clear), P(i, s[i].half + 1.1f, Clear),
                        P(i, -(s[i].half + 1.3f), Clear + Slab), P(i, s[i].half + 1.7f, Clear + Slab));
        }

        // beidseitige Fläche (eigene Vertices je Seite, damit die Normalen stimmen); Beton = Gehweg-Material (Teilmesh 4)
        private static void Face(RoadProfile.Parts p, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 n = Vector3.Cross(b - a, c - a); if (n.sqrMagnitude < 1e-8f) n = Vector3.up; n.Normalize();
            for (int side = 0; side < 2; side++)
            {
                int o = p.V.Count; Vector3 nn = side == 0 ? -n : n;      // Vorderseite von (a,c,b) zeigt wie cross(c-a, b-a)
                p.V.Add(a); p.V.Add(b); p.V.Add(c); p.V.Add(d);
                for (int k = 0; k < 4; k++) { p.N.Add(nn); p.UV.Add(Vector2.zero); }
                if (side == 0) { p.T[4].Add(o); p.T[4].Add(o + 2); p.T[4].Add(o + 1); p.T[4].Add(o + 1); p.T[4].Add(o + 2); p.T[4].Add(o + 3); }
                else { p.T[4].Add(o); p.T[4].Add(o + 1); p.T[4].Add(o + 2); p.T[4].Add(o + 1); p.T[4].Add(o + 3); p.T[4].Add(o + 2); }
            }
        }
    }
}
