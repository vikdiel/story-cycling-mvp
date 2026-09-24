using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Häuser aus den ECHTEN OSM-Grundrissen statt PolygonCity-Bürotürmen:
    //   - Wände = extrudierter Grundriss, Fassadentextur mit Fensterraster (3 m × 3,1 m)
    //   - weiß/creme/hellgrau/sand verputzt, Walmdach (Ziegel/Anthrazit) oder Flachdach mit Attika
    //   - 1–3 Geschosse; OSM height/levels wird respektiert (Sea-Point-Wohntürme)
    //   - am Hang: Erdgeschoss auf dem HÖCHSTEN Geländepunkt, darunter ein Sockel bis zum tiefsten
    //     (nichts versinkt im Hang, nichts schwebt — typisch Camps Bay)
    //   - in 500-m-Zellen zu wenigen Meshes gebündelt (wenige Draw Calls fürs iPad)
    public static class ProceduralHouses
    {
        public const float MaxDistance = 170f;         // Abstand zur Route
        private const float Storey = 3.1f, Bucket = 500f, MaxSlopeDrop = 9f;

        // Submeshes: 0-3 Wandfarben, 4 Ziegeldach, 5 Anthrazitdach, 6 Flachdach, 7 Sockel
        public sealed class Materials { public Material[] Walls; public Material RoofTile, RoofDark, RoofFlat, Plinth; }

        private sealed class Parts
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] T = new List<int>[8];
            public Parts() { for (int i = 0; i < T.Length; i++) T[i] = new List<int>(); }
        }

        public static int Build(OsmContext osm, WorldTerrain terrain, Occupancy occupied, Transform parent,
                                Materials mats, System.Func<Mesh, Mesh> save)
        {
            var buckets = new Dictionary<long, Parts>();
            int built = 0, skippedRoad = 0, skippedSteep = 0, skippedFar = 0;
            foreach (var b in osm.Buildings)
            {
                if (b.kind == "roof" || b.kind == "ruins" || b.kind == "construction") continue;
                var ring = Clean(b.ring);
                if (ring == null) continue;
                Vector2 c = b.centroid;
                if (terrain.Road.Distance(c.x, c.y, MaxDistance + 1f) > MaxDistance) { skippedFar++; continue; }
                if (terrain.DemY(c.x, c.y) < terrain.SeaY + .5f) continue;
                if (!ClearOfRoads(ring, terrain)) { skippedRoad++; continue; }

                float gMin = float.MaxValue, gMax = float.MinValue;
                foreach (var p in ring) { float g = terrain.HeightAt(p.x, p.y); gMin = Mathf.Min(gMin, g); gMax = Mathf.Max(gMax, g); }
                float gc = terrain.HeightAt(c.x, c.y); gMin = Mathf.Min(gMin, gc); gMax = Mathf.Max(gMax, gc);
                if (gMax - gMin > MaxSlopeDrop) { skippedSteep++; continue; }

                uint hash = Hash(c);
                int storeys = Storeys(b, hash);
                float baseY = gMax + .15f, top = baseY + storeys * Storey;
                int wall = (int)(hash % 4u);

                long key = ((long)Mathf.FloorToInt(c.x / Bucket) << 32) | (uint)Mathf.FloorToInt(c.y / Bucket);
                if (!buckets.TryGetValue(key, out Parts parts)) { parts = new Parts(); buckets[key] = parts; }

                Walls(parts, ring, baseY, top, wall, true);
                if (baseY - gMin > .25f) Walls(parts, ring, gMin - .6f, baseY, 7, false);   // Sockel am Hang
                bool hip = ring.Count == 4 && storeys <= 2 && IsRectangle(ring) && (hash >> 3) % 10u < 6u;
                if (hip) HipRoof(parts, ring, top, (hash >> 7) % 3u == 0u ? 5 : 4);
                else FlatRoof(parts, ring, top);

                occupied.Add(c.x, c.y, Mathf.Sqrt(Mathf.Abs(b.area) / Mathf.PI) + 1f);
                built++;
            }

            var materialArray = new[] { mats.Walls[0], mats.Walls[1], mats.Walls[2], mats.Walls[3], mats.RoofTile, mats.RoofDark, mats.RoofFlat, mats.Plinth };
            int cells = 0;
            foreach (var kv in buckets)
            {
                var p = kv.Value;
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32, subMeshCount = p.T.Length, name = $"Houses_{cells:D3}" };
                mesh.SetVertices(p.V); mesh.SetNormals(p.N); mesh.SetUVs(0, p.UV);
                for (int i = 0; i < p.T.Length; i++) mesh.SetTriangles(p.T[i], i);
                mesh.RecalculateBounds();
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = materialArray;
                cells++;
            }
            Debug.Log($"Häuser (prozedural): {built} aus OSM-Grundrissen in {cells} Zellen " +
                      $"(weggelassen: Fahrbahn {skippedRoad}, zu steil {skippedSteep}, > {MaxDistance} m {skippedFar}).");
            return built;
        }

        private static int Storeys(OsmContext.Building b, uint hash)
        {
            if (b.heightTagged) return Mathf.Clamp(Mathf.RoundToInt(b.heightM / Storey), 1, 20);
            if (b.kind == "garage" || b.kind == "garages" || b.kind == "shed") return 1;
            float a = Mathf.Abs(b.area);
            if (a < 120f) return (hash >> 11) % 3u == 0u ? 2 : 1;
            if (a < 400f) return (hash >> 11) % 3u == 0u ? 1 : 2;
            return b.kind == "apartments" ? 4 : 3;
        }

        // Grundriss säubern: doppelte/kollineare Punkte raus, gegen den Uhrzeigersinn (Draufsicht x/z).
        private static List<Vector2> Clean(List<Vector2> raw)
        {
            if (raw == null || raw.Count < 3) return null;
            var r = new List<Vector2>();
            foreach (var p in raw) if (r.Count == 0 || Vector2.Distance(r[r.Count - 1], p) > .6f) r.Add(p);
            if (r.Count > 2 && Vector2.Distance(r[0], r[r.Count - 1]) < .6f) r.RemoveAt(r.Count - 1);
            for (int pass = 0; pass < 2 && r.Count > 3; pass++)
                for (int i = 0; i < r.Count && r.Count > 3; i++)
                {
                    Vector2 a = r[(i + r.Count - 1) % r.Count], b = r[i], c = r[(i + 1) % r.Count];
                    Vector2 d1 = (b - a).normalized, d2 = (c - b).normalized;
                    if (Vector2.Dot(d1, d2) > .985f) { r.RemoveAt(i); i--; }
                }
            if (r.Count < 3) return null;
            float area = SignedArea(r);
            if (Mathf.Abs(area) < 12f) return null;
            if (area < 0f) r.Reverse();
            return r;
        }

        private static float SignedArea(List<Vector2> r)
        {
            float a = 0f;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += (r[j].x * r[i].y - r[i].x * r[j].y);
            return a * .5f;
        }

        private static bool ClearOfRoads(List<Vector2> ring, WorldTerrain terrain)
        {
            foreach (var p in ring)
            {
                if (terrain.Road.Distance(p.x, p.y, 12f) < RoadMeshBuilder.HalfWidth + 2.2f) return false;
                if (terrain.Streets != null && terrain.Streets.Nearest(p.x, p.y, 10f, out int i, out float d) &&
                    d < terrain.Streets.Samples[i].half + 1.2f) return false;
            }
            return true;
        }

        private static bool IsRectangle(List<Vector2> r)
        {
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = (r[(i + 1) % 4] - r[i]).normalized, b = (r[(i + 2) % 4] - r[(i + 1) % 4]).normalized;
                if (Mathf.Abs(Vector2.Dot(a, b)) > .2f) return false;
            }
            return true;
        }

        // Dreieck mit gewünschter Normalenrichtung (Unity: Vorderseite im Uhrzeigersinn).
        private static void Tri(Parts p, int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 want, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, want) < 0f) { var t = b; b = c; c = t; var tu = ub; ub = uc; uc = tu; n = -n; }
            n = n.normalized;
            int i = p.V.Count;
            p.V.Add(a); p.V.Add(b); p.V.Add(c);
            p.N.Add(n); p.N.Add(n); p.N.Add(n);
            p.UV.Add(ua); p.UV.Add(ub); p.UV.Add(uc);
            p.T[sub].Add(i); p.T[sub].Add(i + 1); p.T[sub].Add(i + 2);
        }

        private static void Walls(Parts p, List<Vector2> ring, float y0, float y1, int sub, bool facadeUV)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a2 = ring[i], b2 = ring[(i + 1) % ring.Count];
                float len = Vector2.Distance(a2, b2);
                // CCW-Grundriss: Außennormale = rechts von der Kante
                Vector3 outward = new Vector3(b2.y - a2.y, 0f, -(b2.x - a2.x)).normalized;
                Vector3 a0 = new Vector3(a2.x, y0, a2.y), b0 = new Vector3(b2.x, y0, b2.y);
                Vector3 a1 = new Vector3(a2.x, y1, a2.y), b1 = new Vector3(b2.x, y1, b2.y);
                // Fensterraster: ganze Fensterfelder pro Wand (≈3 m), kurze Wandstücke ohne Fenster
                float u1 = !facadeUV ? 0f : len < 2f ? .12f : Mathf.Max(1f, Mathf.Round(len / 3f));
                float v1 = facadeUV ? (y1 - y0) / Storey : 0f;
                Tri(p, sub, a0, b0, b1, outward, new Vector2(0f, 0f), new Vector2(u1, 0f), new Vector2(u1, v1));
                Tri(p, sub, a0, b1, a1, outward, new Vector2(0f, 0f), new Vector2(u1, v1), new Vector2(0f, v1));
            }
        }

        private static void FlatRoof(Parts p, List<Vector2> ring, float top)
        {
            // Attika: 0,35 m Wandband in Dachfarbe, Dachfläche knapp darunter
            Walls(p, ring, top, top + .35f, 6, false);
            foreach (var t in Triangulate(ring))
            {
                Vector3 a = new Vector3(ring[t.x].x, top + .05f, ring[t.x].y), b = new Vector3(ring[t.y].x, top + .05f, ring[t.y].y),
                        c = new Vector3(ring[t.z].x, top + .05f, ring[t.z].y);
                Tri(p, 6, a, b, c, Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            }
        }

        private static void HipRoof(Parts p, List<Vector2> r, float top, int sub)
        {
            Vector2 center = (r[0] + r[1] + r[2] + r[3]) * .25f;
            Vector2 e0 = r[1] - r[0], e1 = r[2] - r[1];
            Vector2 u = e0.magnitude >= e1.magnitude ? e0.normalized : e1.normalized;
            float halfLong = Mathf.Max(e0.magnitude, e1.magnitude) * .5f, halfShort = Mathf.Min(e0.magnitude, e1.magnitude) * .5f;
            float ridgeHalf = Mathf.Max(0f, halfLong - halfShort);
            float h = top + halfShort * .6f;                       // ca. 30° Dachneigung
            Vector3 rA = new Vector3(center.x - u.x * ridgeHalf, h, center.y - u.y * ridgeHalf);
            Vector3 rB = new Vector3(center.x + u.x * ridgeHalf, h, center.y + u.y * ridgeHalf);
            const float eave = .4f;                                // Dachüberstand
            for (int i = 0; i < 4; i++)
            {
                Vector2 a2 = r[i], b2 = r[(i + 1) % 4];
                Vector2 ea = a2 + (a2 - center).normalized * eave, eb = b2 + (b2 - center).normalized * eave;
                Vector3 a = new Vector3(ea.x, top - .1f, ea.y), b = new Vector3(eb.x, top - .1f, eb.y);
                Vector2 mid2 = (a2 + b2) * .5f - center;
                Vector3 want = new Vector3(mid2.x, halfShort, mid2.y).normalized;
                bool longSide = Mathf.Abs(Vector2.Dot((b2 - a2).normalized, u)) > .7f;
                if (longSide)
                {
                    // Trapez: Firstpunkte in Kantenrichtung sortiert
                    float da = Vector2.Dot(a2 - center, u);
                    Vector3 ra = da < 0f ? rA : rB, rb = da < 0f ? rB : rA;
                    Tri(p, sub, a, b, rb, want, Vector2.zero, Vector2.zero, Vector2.zero);
                    Tri(p, sub, a, rb, ra, want, Vector2.zero, Vector2.zero, Vector2.zero);
                }
                else
                {
                    Vector3 rr = Vector2.Dot(mid2, u) < 0f ? rA : rB;
                    Tri(p, sub, a, b, rr, want, Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }
        }

        // Ear-Clipping für einfache Polygone (CCW); Fallback: Fächer.
        private static List<Vector3Int> Triangulate(List<Vector2> poly)
        {
            var res = new List<Vector3Int>();
            var idx = new List<int>(); for (int i = 0; i < poly.Count; i++) idx.Add(i);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 1000)
            {
                bool clipped = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    Vector2 a = poly[ia], b = poly[ib], c = poly[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-5f) continue;   // reflex
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (InTri(poly[j], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    res.Add(new Vector3Int(ia, ib, ic)); idx.RemoveAt(k); clipped = true; break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) res.Add(new Vector3Int(idx[0], idx[1], idx[2]));
            else if (idx.Count > 3) for (int k = 1; k + 1 < idx.Count; k++) res.Add(new Vector3Int(idx[0], idx[k], idx[k + 1]));
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

        private static uint Hash(Vector2 c)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(c.x * 10f) * 73856093u ^ (uint)Mathf.RoundToInt(c.y * 10f) * 19349663u;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return h;
            }
        }

        // Fassadentextur: weißer Putz mit dunklem Fenster pro 3 m × 3,1 m Feld (wird per Materialfarbe getönt).
        public static Texture2D FacadeTexture()
        {
            const int n = 64;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)n, v = y / (float)n;
                bool window = u > .22f && u < .78f && v > .32f && v < .80f;
                bool frame = !window && u > .19f && u < .81f && v > .29f && v < .83f;
                bool sill = u > .17f && u < .83f && v > .26f && v < .29f;
                Color col = window ? new Color(.20f, .27f, .33f) : frame ? new Color(.88f, .88f, .88f) : sill ? new Color(.80f, .80f, .80f) : new Color(.97f, .97f, .96f);
                if (window && v > .55f && v < .57f) col = new Color(.75f, .75f, .75f);   // Sprosse
                px[y * n + x] = col;
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Facade" };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
