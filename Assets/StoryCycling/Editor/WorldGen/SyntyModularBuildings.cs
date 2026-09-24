using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Setzt Häuser aus dem PolygonCity-Baukasten zusammen (so wie auf den Store-Bildern):
    //   Etage (5×5×3 m, Fassade vorn), Ecke (Fassade vorn + seitlich), Haustür, Laden, Dachabschluss.
    // Pro OSM-Grundriss: Raster aus 5-m-Zellen (leicht skaliert auf die echte Größe), Außenzellen mit
    // Fassade nach außen, Ecken als Eckmodule, Tür bzw. Läden im Erdgeschoss zur Straße, oben Dachmodule.
    // Alles wird pro 400-m-Zelle und Material zu EINEM Mesh verschmolzen (sonst ~30.000 GameObjects).
    // Maße/Fassadenrichtung werden zur Laufzeit aus den Meshes gemessen, nicht angenommen.
    public static class SyntyModularBuildings
    {
        public const float MaxDistance = 170f;
        private const float Cell = 5f, Bucket = 250f, MaxSlopeDrop = 9f;
        // LOD: voller Baukasten, solange die 250-m-Zelle > 35 % der Bildhöhe (~650 m) einnimmt, danach einfache Blöcke
        private const float Lod0Screen = .35f, Lod1Screen = .012f;

        public sealed class Module
        {
            public GameObject Prefab;
            public Vector3 CoreCenter;     // Mitte der 5×5-Grundzelle (lokal, y=0)
            public Vector3 Front, Side;    // Fassadenrichtungen lokal (Side nur bei Ecken sinnvoll)
            public float Height;
            public readonly List<(Mesh mesh, int sub, Material mat, Matrix4x4 local)> Parts = new List<(Mesh, int, Material, Matrix4x4)>();
        }

        public sealed class Kit
        {
            public Module[] Floor, Corner, Door, DoorCorner, Roof, RoofCorner, Shop, ShopCorner;
            public bool Complete => Floor.Length > 0 && Corner.Length > 0 && Roof.Length > 0 && RoofCorner.Length > 0;
        }

        public static Kit LoadKit(AssetCatalog catalog)
        {
            System.Func<string, string, Module[]> Load = (regex, exclude) =>
            {
                var list = new List<Module>();
                foreach (var p in WorldPlacement.Pool(catalog, regex, exclude))
                {
                    var m = Measure(p);
                    if (m != null) list.Add(m);
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Prefab.name, b.Prefab.name));
                return list.ToArray();
            };
            var kit = new Kit
            {
                Floor = Load(@"^SM_Bld_Apartment_0\d$", null),
                Corner = Load(@"^SM_Bld_Apartment_Corner_0\d$", null),
                Door = Load(@"^SM_Bld_Apartment_Door_0\d$", null),
                DoorCorner = Load(@"^SM_Bld_Apartment_Door_Corner_0\d$", null),
                Roof = Load(@"^SM_Bld_Apartment_Roof_0\d$", null),
                RoofCorner = Load(@"^SM_Bld_Apartment_Roof_Corner_0\d$", null),
                Shop = Load(@"^SM_Bld_Shop_0\d$", null),
                ShopCorner = Load(@"^SM_Bld_Shop_Corner_0\d$", null),
            };
            // Nur 1-Zellen-Läden (Shop_03 ist 10 m breit)
            kit.Shop = System.Array.FindAll(kit.Shop, m => Mathf.Abs(m.CoreCenter.x) < 3.5f);
            Debug.Log($"Baukasten: Etage {kit.Floor.Length}, Ecke {kit.Corner.Length}, Tür {kit.Door.Length}, Dach {kit.Roof.Length}/{kit.RoofCorner.Length}, " +
                      $"Laden {kit.Shop.Length}/{kit.ShopCorner.Length}");
            return kit;
        }

        // Maße, Grundzelle und Fassadenseiten eines Moduls aus seinen Meshes bestimmen.
        public static Module Measure(GameObject prefab)
        {
            var m = new Module { Prefab = prefab };
            var root = prefab.transform.worldToLocalMatrix;
            bool any = false;
            Bounds b = new Bounds();
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mesh == null || mr == null) continue;
                Matrix4x4 local = root * mf.transform.localToWorldMatrix;
                var mats = mr.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    m.Parts.Add((mesh, s, mats[Mathf.Min(s, mats.Length - 1)], local));
                Bounds mb = mesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    Vector3 w = local.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            if (!any) return null;
            Classify(m, b);
            return m;
        }

        // Grundzelle/Fassaden aus den Bounds (öffentlich für Tests).
        public static void Classify(Module m, Bounds b)
        {
            float x0 = Snap(b.min.x), x1 = Snap(b.max.x), z0 = Snap(b.min.z), z1 = Snap(b.max.z);
            if (x1 - x0 < Cell * .5f) x1 = x0 + Cell;
            if (z1 - z0 < Cell * .5f) z1 = z0 + Cell;
            m.CoreCenter = new Vector3((x0 + x1) * .5f, 0f, (z0 + z1) * .5f);
            // Fassade = Seite mit Überstand (Fensterbänke, Stufen, Gesims), gegenüber liegt die glatte Rückwand.
            m.Front = (b.max.z - z1) >= (z0 - b.min.z) ? Vector3.forward : Vector3.back;
            float px = b.max.x - x1, nx = x0 - b.min.x;
            m.Side = Mathf.Abs(px - nx) < .02f ? Vector3.zero : (px > nx ? Vector3.right : Vector3.left);
            m.Height = b.max.y > 2.6f && b.max.y < 3.4f ? 3f : b.max.y;
        }

        private static float Snap(float v) => Mathf.Round(v / Cell) * Cell;

        // ---------------------------------------------------------------- Bauen
        public sealed class BucketData
        {
            public long Vertices;
            public readonly Dictionary<Material, List<CombineInstance>> ByMat = new Dictionary<Material, List<CombineInstance>>();
            public readonly List<Vector3> PlinthV = new List<Vector3>();
            public readonly List<int> PlinthT = new List<int>();
            public readonly List<Vector3> FarV = new List<Vector3>();      // LOD1: ein Block pro Haus
            public readonly List<int> FarT = new List<int>();
        }

        public static int Build(OsmContext osm, WorldTerrain terrain, Occupancy occupied, Kit kit, Material plinthMat,
                                Material farMat, Transform parent, System.Func<Mesh, Mesh> save)
        {
            // Eck-Seite für uneindeutige Module (Laden-Ecken) von den Wohn-Ecken übernehmen
            Vector3 defaultSide = kit.Corner[0].Side != Vector3.zero ? kit.Corner[0].Side : Vector3.right;
            foreach (var arr in new[] { kit.Corner, kit.DoorCorner, kit.RoofCorner, kit.ShopCorner })
                foreach (var mo in arr) if (mo.Side == Vector3.zero) mo.Side = defaultSide;

            var buckets = new Dictionary<long, BucketData>();
            int built = 0, skipRoad = 0, skipSteep = 0, skipOverlap = 0, modules = 0;
            foreach (var b in osm.Buildings)
            {
                if (b.kind == "roof" || b.kind == "ruins" || b.kind == "construction") continue;
                Vector2 c2 = b.centroid;
                if (terrain.Road.Distance(c2.x, c2.y, MaxDistance + 1f) > MaxDistance) continue;
                if (terrain.DemY(c2.x, c2.y) < terrain.SeaY + .5f) continue;
                if (b.width < 3f || b.depth < 3f) continue;

                // Gebäuderahmen: Front (+Z) zeigt zur nächsten Straße, X entlang der Front
                Vector3 major = b.axisDir.sqrMagnitude > 1e-6f ? b.axisDir.normalized : Vector3.right;
                Vector3 minor = Vector3.Cross(Vector3.up, major);
                Vector3 toRoad = Vector3.forward;
                if (terrain.Road.Nearest(c2.x, c2.y, 250f, out int ri, out _))
                { toRoad = terrain.Road.Samples[ri].pos - new Vector3(c2.x, 0f, c2.y); toRoad.y = 0f; toRoad.Normalize(); }
                Vector3[] cand = { minor, -minor, major, -major };
                Vector3 front = cand[0]; float best = float.MinValue;
                foreach (var cd in cand) { float d = Vector3.Dot(cd, toRoad); if (d > best) { best = d; front = cd; } }
                bool frontIsMinor = Mathf.Abs(Vector3.Dot(front, minor)) > .5f;
                float W = frontIsMinor ? b.width : b.depth, D = frontIsMinor ? b.depth : b.width;
                // Große Grundrisse nicht voll ausbauen (Vertex-Budget iPad): max. 6 × 4 Zellen
                int n = Mathf.Clamp(Mathf.RoundToInt(W / Cell), 1, 6), m = Mathf.Clamp(Mathf.RoundToInt(D / Cell), 1, 4);
                float sx = Mathf.Clamp(W / (n * Cell), .85f, 1.2f), sz = Mathf.Clamp(D / (m * Cell), .85f, 1.2f);
                float hw = n * Cell * sx * .5f, hd = m * Cell * sz * .5f;
                Vector3 right = Vector3.Cross(Vector3.up, front);
                Vector3 center = new Vector3(c2.x, 0f, c2.y);

                // Fahrbahn/Querstraßen frei, nicht in andere Häuser
                var corners = new List<Vector2>();
                for (int k = 0; k < 4; k++)
                {
                    Vector3 q = center + right * ((k == 0 || k == 3) ? -hw : hw) + front * (k < 2 ? -hd : hd);
                    corners.Add(new Vector2(q.x, q.z));
                }
                if (!ClearOfRoads(corners, terrain)) { skipRoad++; continue; }
                float radius = Mathf.Min(hw, hd);
                if (!occupied.IsFree(c2.x, c2.y, radius * .7f)) { skipOverlap++; continue; }

                float gMin = float.MaxValue, gMax = float.MinValue;
                foreach (var q in corners) { float g = terrain.HeightAt(q.x, q.y); gMin = Mathf.Min(gMin, g); gMax = Mathf.Max(gMax, g); }
                float gc = terrain.HeightAt(c2.x, c2.y); gMin = Mathf.Min(gMin, gc); gMax = Mathf.Max(gMax, gc);
                if (gMax - gMin > MaxSlopeDrop) { skipSteep++; continue; }
                float baseY = gMax + .05f;

                uint h = Hash(c2);
                int floors = Floors(b, h);
                int style = (int)(h % (uint)kit.Floor.Length);
                bool shops = b.kind == "retail" || b.kind == "commercial" || b.kind == "shop" ||
                             (kit.Shop.Length > 0 && terrain.BiomeAt(c2.x, c2.y) == WorldTerrain.Biome.Urban &&
                              terrain.Road.Distance(c2.x, c2.y, 40f) < 30f && (h >> 5) % 3u == 0u);

                long key = ((long)Mathf.FloorToInt(c2.x / Bucket) << 32) | (uint)Mathf.FloorToInt(c2.y / Bucket);
                if (!buckets.TryGetValue(key, out BucketData bucket)) { bucket = new BucketData(); buckets[key] = bucket; }

                Matrix4x4 frame = Matrix4x4.TRS(new Vector3(center.x, baseY, center.z), Quaternion.LookRotation(front, Vector3.up), new Vector3(sx, 1f, sz));
                modules += Compose(bucket, frame, kit, n, m, floors, style, shops, h);
                if (baseY - gMin > .2f) Plinth(bucket.PlinthV, bucket.PlinthT, corners, gMin - .6f, baseY + .02f);
                Plinth(bucket.FarV, bucket.FarT, corners, baseY, baseY + floors * 3f + .4f, true);
                occupied.Add(c2.x, c2.y, Mathf.Sqrt(hw * hw + hd * hd) * .8f);
                built++;
            }

            int cells = 0;
            long totalVerts = 0; foreach (var kv in buckets) totalVerts += kv.Value.Vertices;
            foreach (var kv in buckets)
            {
                var cellRoot = new GameObject($"Buildings_{cells:D3}");
                cellRoot.transform.SetParent(parent, false);
                var near = new List<Renderer>();
                foreach (var mat in kv.Value.ByMat)
                {
                    var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = $"Buildings_{cells:D3}_{mat.Key.name}" };
                    mesh.CombineMeshes(mat.Value.ToArray(), true, true);
                    mesh.RecalculateBounds();
                    near.Add(Child(cellRoot.transform, save(mesh), mat.Key));
                }
                var plinths = new List<Renderer>();
                if (kv.Value.PlinthV.Count > 0)
                    plinths.Add(Child(cellRoot.transform, save(Simple($"Plinths_{cells:D3}", kv.Value.PlinthV, kv.Value.PlinthT)), plinthMat));
                var far = Child(cellRoot.transform, save(Simple($"BuildingsFar_{cells:D3}", kv.Value.FarV, kv.Value.FarT)), farMat);
                far.shadowCastingMode = ShadowCastingMode.Off;
                near.AddRange(plinths);
                var lod1 = new List<Renderer> { far }; lod1.AddRange(plinths);
                var group = cellRoot.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(Lod0Screen, near.ToArray()), new LOD(Lod1Screen, lod1.ToArray()) });
                group.RecalculateBounds();
                cells++;
            }
            Debug.Log($"Synty-Baukasten: {built} Häuser aus {modules} Modulen ({totalVerts / 1000}k Vertices) in {cells} Zellen " +
                      $"(weggelassen: Fahrbahn {skipRoad}, zu steil {skipSteep}, Überlappung {skipOverlap}).");
            return built;
        }

        private static int Floors(OsmContext.Building b, uint h)
        {
            if (b.heightTagged) return Mathf.Clamp(Mathf.RoundToInt(b.heightM / 3f), 1, 14);
            if (b.kind == "garage" || b.kind == "garages" || b.kind == "shed") return 1;
            float a = Mathf.Abs(b.area);
            if (a < 450f) return 2;
            return b.kind == "apartments" ? 4 : 3;
        }

        // Legt alle Module eines Hauses in den Bucket. Rückgabe: Anzahl Module.
        public static int Compose(BucketData bucket, Matrix4x4 frame, Kit kit, int n, int m, int floors, int style, bool shops, uint h)
        {
            int count = 0;
            int doorCol = n / 2;
            for (int f = 0; f <= floors; f++)
            {
                bool roof = f == floors;
                for (int j = 0; j < m; j++)
                for (int i = 0; i < n; i++)
                {
                    var outs = new List<Vector3>();
                    if (j == 0) outs.Add(Vector3.forward);
                    if (j == m - 1) outs.Add(Vector3.back);
                    if (i == 0) outs.Add(Vector3.left);
                    if (i == n - 1) outs.Add(Vector3.right);
                    if (outs.Count == 0 && !roof) continue;                 // Innenzellen: nur Dach

                    Vector3 cellCenter = new Vector3((i + .5f) * Cell - n * Cell * .5f, f * 3f, m * Cell * .5f - (j + .5f) * Cell);
                    Vector3 a = Vector3.zero, bb = Vector3.zero;
                    bool corner = outs.Count >= 2 && HasPerpendicular(outs, out a, out bb);
                    Module mod;
                    Quaternion rot;
                    if (corner)
                    {
                        bool ground = f == 0 && !roof;
                        Module[] pool = roof ? kit.RoofCorner
                                      : ground && shops && j == 0 && kit.ShopCorner.Length > 0 ? kit.ShopCorner
                                      : ground && j == 0 && i == doorCol && kit.DoorCorner.Length > 0 ? kit.DoorCorner
                                      : kit.Corner;
                        mod = pool[style % pool.Length];
                        if (!CornerRotation(mod, a, bb, out rot))
                        {
                            // kein passendes Eckmodul: normales Modul zur Hauptseite
                            mod = (roof ? kit.Roof : kit.Floor)[style % (roof ? kit.Roof.Length : kit.Floor.Length)];
                            rot = FaceRotation(mod, outs[0]);
                        }
                    }
                    else
                    {
                        Vector3 dir = outs.Count > 0 ? outs[0] : Vector3.forward;
                        bool ground = f == 0 && !roof && dir == Vector3.forward;
                        Module[] pool = roof ? kit.Roof
                                      : ground && shops && kit.Shop.Length > 0 ? kit.Shop
                                      : ground && i == doorCol && kit.Door.Length > 0 ? kit.Door
                                      : kit.Floor;
                        int pick = pool == kit.Shop ? (int)((h >> 3) + (uint)i) % pool.Length : style % pool.Length;
                        mod = pool[pick];
                        rot = FaceRotation(mod, dir);
                    }
                    Add(bucket, frame, mod, rot, cellCenter);
                    count++;
                }
            }
            return count;
        }

        private static bool HasPerpendicular(List<Vector3> outs, out Vector3 a, out Vector3 b)
        {
            for (int p = 0; p < outs.Count; p++)
            for (int q = p + 1; q < outs.Count; q++)
                if (Mathf.Abs(Vector3.Dot(outs[p], outs[q])) < .1f) { a = outs[p]; b = outs[q]; return true; }
            a = b = Vector3.zero;
            return false;
        }

        // Drehung um Y, die lokale Richtung 'from' auf 'to' bringt.
        private static Quaternion Yaw(Vector3 from, Vector3 to)
        {
            float ang = (Mathf.Atan2(to.x, to.z) - Mathf.Atan2(from.x, from.z)) * Mathf.Rad2Deg;
            return Quaternion.Euler(0f, ang, 0f);
        }

        public static Quaternion FaceRotation(Module mod, Vector3 dir) => Yaw(mod.Front, dir);

        public static bool CornerRotation(Module mod, Vector3 a, Vector3 b, out Quaternion rot)
        {
            if (mod.Side == Vector3.zero) { rot = Quaternion.identity; return false; }
            rot = Yaw(mod.Front, a);
            if (Vector3.Dot(rot * mod.Side, b) > .9f) return true;
            rot = Yaw(mod.Front, b);
            if (Vector3.Dot(rot * mod.Side, a) > .9f) return true;
            return false;
        }

        // Modul so setzen, dass seine Grundzelle auf cellCenter liegt.
        private static void Add(BucketData bucket, Matrix4x4 frame, Module mod, Quaternion rot, Vector3 cellCenter)
        {
            Vector3 pivot = cellCenter - rot * mod.CoreCenter;
            Matrix4x4 place = frame * Matrix4x4.TRS(pivot, rot, Vector3.one);
            foreach (var part in mod.Parts)
            {
                if (!bucket.ByMat.TryGetValue(part.mat, out List<CombineInstance> list)) { list = new List<CombineInstance>(); bucket.ByMat[part.mat] = list; }
                list.Add(new CombineInstance { mesh = part.mesh, subMeshIndex = part.sub, transform = place * part.local });
                bucket.Vertices += part.mesh.vertexCount / Mathf.Max(1, part.mesh.subMeshCount);
            }
        }

        public static Vector3 PlacedCellCenter(Module mod, Quaternion rot, Vector3 cellCenter)
        {
            Vector3 pivot = cellCenter - rot * mod.CoreCenter;
            return pivot + rot * mod.CoreCenter;
        }

        private static Renderer Child(Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return mr;
        }

        private static Mesh Simple(string name, List<Vector3> v, List<int> t)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = name };
            mesh.SetVertices(v); mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        // Wände eines Rechtecks von y0 bis y1 (Außenseite sichtbar); withTop = Deckel (LOD-Block).
        private static void Plinth(List<Vector3> V, List<int> T, List<Vector2> ring, float y0, float y1, bool withTop = false)
        {
            // Rechteck gegen den Uhrzeigersinn sortieren, dann Außenwände (beidseitig -> keine Wicklungsfrage)
            var r = new List<Vector2>(ring);
            float area = 0f;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) area += r[j].x * r[i].y - r[i].x * r[j].y;
            if (area < 0f) r.Reverse();
            for (int i = 0; i < r.Count; i++)
            {
                Vector2 a = r[i], c = r[(i + 1) % r.Count];
                int k = V.Count;
                V.Add(new Vector3(a.x, y0, a.y)); V.Add(new Vector3(c.x, y0, c.y));
                V.Add(new Vector3(c.x, y1, c.y)); V.Add(new Vector3(a.x, y1, a.y));
                // CCW-Grundriss: Außenseite rechts der Kante -> Dreiecke (k, k+2, k+1) zeigen nach außen
                T.Add(k); T.Add(k + 2); T.Add(k + 1);
                T.Add(k); T.Add(k + 3); T.Add(k + 2);
            }
            if (withTop)
            {
                // Deckel: CCW von oben = in Unity Rückseite -> Reihenfolge umkehren (0, 2, 1 / 0, 3, 2)
                int k = V.Count;
                foreach (var p in r) V.Add(new Vector3(p.x, y1, p.y));
                T.Add(k); T.Add(k + 2); T.Add(k + 1);
                T.Add(k); T.Add(k + 3); T.Add(k + 2);
            }
        }

        private static bool ClearOfRoads(List<Vector2> ring, WorldTerrain terrain)
        {
            var pts = new List<Vector2>(ring);
            for (int i = 0; i < ring.Count; i++) pts.Add((ring[i] + ring[(i + 1) % ring.Count]) * .5f);
            foreach (var p in pts)
            {
                if (terrain.Road.Distance(p.x, p.y, 12f) < RoadMeshBuilder.HalfWidth + 2.2f) return false;
                if (terrain.Streets != null && terrain.Streets.Nearest(p.x, p.y, 10f, out int i, out float d) &&
                    d < terrain.Streets.Samples[i].half + 1.2f) return false;
            }
            return true;
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
    }
}
