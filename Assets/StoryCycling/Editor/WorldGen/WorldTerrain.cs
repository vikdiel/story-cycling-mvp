using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Echtes Gelände statt 180-m-Band: DEM-Höhen, entlang der Straße eingeschnitten
    // (Straße liegt nie in der Luft / im Hang), in LOD-Kacheln mit Skirts gebaut und über
    // eine Farbtextur nach Neigung, Höhe und OSM-Landnutzung eingefärbt. Das Meer ergibt
    // sich von selbst: Wasserfläche auf Meereshöhe, alles darunter ist Meeresboden.
    public sealed class WorldTerrain
    {
        public enum Biome : byte { Generic, Urban, Scrub, Forest, Field, Beach, Rock, Water }

        public readonly DemGrid Dem;
        public readonly RoadField Road;
        public readonly float Ele0;      // Höhe des ersten GPX-Punkts (lokales y = absolut - Ele0)
        // Terrarium-Küstendaten liegen oft geringfügig über dem echten Meeresspiegel.
        // Ein kleiner Offset erzeugt natürlichere, breitere Strände.
        public const float SeaLevelOffset = 1.5f;
        public float SeaY => -Ele0 + SeaLevelOffset;

        // Straßen-Einschnitt
        public const float FlatRadius = 11f;     // Fahrbahn + Bankett + 1 Zellendiagonale
        public const float InfluenceRadius = 11f + 50f;
        private const float RoadInset = 0.3f;    // Gelände knapp unter der Fahrbahn

        // Kacheln
        private const float ChunkSize = 320f;
        private const float NearRectDistance = 150f; // > InfluenceRadius, sonst Risse im Einschnitt

        // Farb-/Biomraster
        public const float RasterCell = 10f;
        private int rw, rh;
        private float rMinX, rMinZ;
        private Biome[] biomes;

        public WorldTerrain(DemGrid dem, RoadField road, float ele0, OsmContext osm)
        {
            Dem = dem; Road = road; Ele0 = ele0;
            rMinX = dem.MinX; rMinZ = dem.MinZ;
            rw = Mathf.CeilToInt((dem.MaxX - dem.MinX) / RasterCell) + 1;
            rh = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / RasterCell) + 1;
            biomes = new Biome[rw * rh];
            if (osm != null) RasterizeBiomes(osm);
        }

        // ---------------------------------------------------------------- Abfragen
        public float DemY(float x, float z) => Dem.Sample(x, z) - Ele0;

        public float Carve(float demY, float dist, float roadY)
        {
            if (dist <= FlatRadius) return roadY - RoadInset;
            float target = roadY - RoadInset, dh = demY - target;
            // Einschnitt (Hang höher) flacher auslaufen lassen, Aufschüttung (Hang tiefer) steiler,
            // damit an Klippenstraßen kein künstlicher Damm ins Meer wächst.
            float bank = dh > 0f ? Mathf.Clamp(dh * 1.2f, 8f, 50f) : Mathf.Clamp(-dh * 0.9f, 6f, 35f);
            float t = Mathf.Clamp01((dist - FlatRadius) / bank);
            t = t * t * (3f - 2f * t);
            return Mathf.Lerp(target, demY, t);
        }

        // Endgültige Geländehöhe inkl. Straßen-Einschnitt (für alle Platzierer).
        public float HeightAt(float x, float z)
        {
            float demY = DemY(x, z);
            if (Road.Nearest(x, z, InfluenceRadius, out int i, out float dist))
                return Carve(demY, dist, Road.Samples[i].pos.y);
            return demY;
        }

        // Tiefste Geländehöhe unter einer (gedrehten) Rechteck-Grundfläche -> nichts schwebt.
        public float LowestUnder(Vector3 center, Vector3 right, Vector3 forward, float halfW, float halfD)
        {
            float y = HeightAt(center.x, center.z);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 p = center + right * (sx * halfW) + forward * (sz * halfD);
                y = Mathf.Min(y, HeightAt(p.x, p.z));
            }
            return y;
        }

        public float SlopeDeg(float x, float z)
        {
            const float e = 8f;
            float dx = DemY(x + e, z) - DemY(x - e, z), dz = DemY(x, z + e) - DemY(x, z - e);
            return Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz) / (2f * e)) * Mathf.Rad2Deg;
        }

        public Biome BiomeAt(float x, float z)
        {
            int ix = Mathf.Clamp(Mathf.FloorToInt((x - rMinX) / RasterCell), 0, rw - 1);
            int iz = Mathf.Clamp(Mathf.FloorToInt((z - rMinZ) / RasterCell), 0, rh - 1);
            return biomes[iz * rw + ix];
        }

        public bool Contains(float x, float z) => x > Dem.MinX && x < Dem.MaxX && z > Dem.MinZ && z < Dem.MaxZ;

        // ---------------------------------------------------------------- Bauen
        public int BuildChunks(Transform parent, Material material, System.Func<Mesh, Mesh> save)
        {
            // Grobe Straßenpunkte für die Kachel-LOD-Wahl.
            var coarse = new List<Vector2>();
            for (int i = 0; i < Road.Samples.Count; i += 10) coarse.Add(new Vector2(Road.Samples[i].pos.x, Road.Samples[i].pos.z));

            int nx = Mathf.CeilToInt((Dem.MaxX - Dem.MinX) / ChunkSize);
            int nz = Mathf.CeilToInt((Dem.MaxZ - Dem.MinZ) / ChunkSize);
            float halfDiag = ChunkSize * 0.7072f;
            var near = new List<int>();
            int built = 0, tris = 0;
            var tier = new int[4];

            for (int cz = 0; cz < nz; cz++)
            for (int cx = 0; cx < nx; cx++)
            {
                float x0 = Dem.MinX + cx * ChunkSize, z0 = Dem.MinZ + cz * ChunkSize;
                Vector2 c = new Vector2(x0 + ChunkSize * 0.5f, z0 + ChunkSize * 0.5f);
                float centerDist = float.MaxValue;
                foreach (var p in coarse) centerDist = Mathf.Min(centerDist, (p - c).sqrMagnitude);
                float rectDist = Mathf.Max(0f, Mathf.Sqrt(centerDist) - halfDiag);
                if (rectDist > DemFetcher.Margin) continue;

                float cell = rectDist < NearRectDistance ? 5f : rectDist < 700f ? 10f : rectDist < 2000f ? 20f : 40f;
                Mesh mesh = BuildChunk(x0, z0, cell, cell <= 5f, near, out bool allUnderwater);
                if (allUnderwater) { Object.DestroyImmediate(mesh); continue; }
                mesh.name = $"Terrain_{cx}_{cz}";
                mesh = save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = ShadowCastingMode.Off;   // Hügelschatten außerhalb der Shadow-Distance sinnlos
                built++; tris += mesh.triangles.Length / 3;
                tier[cell <= 5f ? 0 : cell <= 10f ? 1 : cell <= 20f ? 2 : 3]++;
            }
            Debug.Log($"Terrain: {built} Kacheln, {tris / 1000}k Dreiecke (5m:{tier[0]} 10m:{tier[1]} 20m:{tier[2]} 40m:{tier[3]}).");
            return built;
        }

        private Mesh BuildChunk(float x0, float z0, float cell, bool carve, List<int> scratch, out bool allUnderwater)
        {
            int n = Mathf.RoundToInt(ChunkSize / cell);   // Zellen pro Seite
            int g = n + 3;                                // inkl. 1 Randring für Normalen
            var h = new float[g * g];
            float gx0 = x0 - cell, gz0 = z0 - cell;
            for (int j = 0; j < g; j++)
            for (int i = 0; i < g; i++)
                h[j * g + i] = DemY(gx0 + i * cell, gz0 + j * cell);

            if (carve)
            {
                var minDist = new float[g * g];
                var roadY = new float[g * g];
                for (int k = 0; k < minDist.Length; k++) minDist[k] = float.MaxValue;
                float R = InfluenceRadius;
                Road.Query(gx0 - R, gz0 - R, gx0 + (g - 1) * cell + R, gz0 + (g - 1) * cell + R, scratch);
                foreach (int si in scratch)
                {
                    Vector3 p = Road.Samples[si].pos;
                    int i0 = Mathf.Max(0, Mathf.FloorToInt((p.x - R - gx0) / cell)), i1 = Mathf.Min(g - 1, Mathf.CeilToInt((p.x + R - gx0) / cell));
                    int j0 = Mathf.Max(0, Mathf.FloorToInt((p.z - R - gz0) / cell)), j1 = Mathf.Min(g - 1, Mathf.CeilToInt((p.z + R - gz0) / cell));
                    for (int j = j0; j <= j1; j++)
                    {
                        float dz = gz0 + j * cell - p.z;
                        for (int i = i0; i <= i1; i++)
                        {
                            float dx = gx0 + i * cell - p.x;
                            float d2 = dx * dx + dz * dz;
                            int k = j * g + i;
                            if (d2 < minDist[k]) { minDist[k] = d2; roadY[k] = p.y; }
                        }
                    }
                }
                for (int k = 0; k < h.Length; k++)
                {
                    if (minDist[k] == float.MaxValue) continue;
                    float d = Mathf.Sqrt(minDist[k]);
                    if (d < R) h[k] = Carve(h[k], d, roadY[k]);
                }
            }

            allUnderwater = true;
            int v = n + 1;
            var verts = new List<Vector3>(v * v + 4 * v);
            var normals = new List<Vector3>(verts.Capacity);
            var uvs = new List<Vector2>(verts.Capacity);
            var tris = new List<int>(n * n * 6 + 4 * n * 6);
            float texW = rw * RasterCell, texH = rh * RasterCell;

            for (int j = 0; j < v; j++)
            for (int i = 0; i < v; i++)
            {
                int gi = i + 1, gj = j + 1;
                float y = h[gj * g + gi];
                if (y > SeaY - 1f) allUnderwater = false;
                float x = x0 + i * cell, z = z0 + j * cell;
                verts.Add(new Vector3(x, y, z));
                normals.Add(new Vector3(h[gj * g + gi - 1] - h[gj * g + gi + 1], 2f * cell, h[(gj - 1) * g + gi] - h[(gj + 1) * g + gi]).normalized);
                uvs.Add(new Vector2((x - rMinX) / texW, (z - rMinZ) / texH));
            }
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * v + i, b = a + 1, c = a + v, d = c + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }

            // Skirts: senkrechte Schürzen an allen Kanten verdecken Risse zwischen LOD-Stufen.
            AddSkirt(verts, normals, uvs, tris, v, 0, 1);            // Süd
            AddSkirt(verts, normals, uvs, tris, v, (v - 1) * v, 1);  // Nord
            AddSkirt(verts, normals, uvs, tris, v, 0, v);            // West
            AddSkirt(verts, normals, uvs, tris, v, v - 1, v);        // Ost

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddSkirt(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
                                     int v, int start, int stride)
        {
            const float depth = 25f;
            int baseIndex = verts.Count;
            for (int k = 0; k < v; k++)
            {
                int src = start + k * stride;
                Vector3 p = verts[src];
                verts.Add(new Vector3(p.x, p.y - depth, p.z));
                normals.Add(normals[src]);
                uvs.Add(uvs[src]);
            }
            for (int k = 0; k < v - 1; k++)
            {
                int top0 = start + k * stride, top1 = start + (k + 1) * stride;
                int bot0 = baseIndex + k, bot1 = baseIndex + k + 1;
                // beidseitig: Blickrichtung egal, keine Löcher durch falsche Wicklung
                tris.Add(top0); tris.Add(top1); tris.Add(bot0); tris.Add(top1); tris.Add(bot1); tris.Add(bot0);
                tris.Add(top0); tris.Add(bot0); tris.Add(top1); tris.Add(top1); tris.Add(bot0); tris.Add(bot1);
            }
        }

        public GameObject BuildWater(Transform parent, Material material, System.Func<Mesh, Mesh> save)
        {
            float cx = (Dem.MinX + Dem.MaxX) * 0.5f, cz = (Dem.MinZ + Dem.MaxZ) * 0.5f, s = 60000f;
            var mesh = new Mesh { name = "Ocean" };
            mesh.vertices = new[]
            {
                new Vector3(cx - s, SeaY, cz - s), new Vector3(cx + s, SeaY, cz - s),
                new Vector3(cx - s, SeaY, cz + s), new Vector3(cx + s, SeaY, cz + s)
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.RecalculateBounds();
            mesh = save(mesh);
            var go = new GameObject("Ocean", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        // ---------------------------------------------------------------- Farbtextur
        public Texture2D BuildColorTexture()
        {
            var px = new Color32[rw * rh];
            Color sandDry = new Color(.87f, .80f, .62f), sandWet = new Color(.70f, .64f, .50f);
            Color rock = new Color(.53f, .50f, .46f), rockDark = new Color(.38f, .36f, .35f);
            Color fynbos = new Color(.43f, .49f, .29f), fynbosDry = new Color(.60f, .57f, .37f);
            Color forest = new Color(.23f, .35f, .19f), field = new Color(.47f, .62f, .30f);
            Color scrubDark = new Color(.29f, .35f, .22f);
            Color urban = new Color(.53f, .56f, .43f), seabed = new Color(.18f, .40f, .40f);

            for (int j = 0; j < rh; j++)
            for (int i = 0; i < rw; i++)
            {
                float x = rMinX + (i + .5f) * RasterCell, z = rMinZ + (j + .5f) * RasterCell;
                float y = DemY(x, z), above = y - SeaY, slope = SlopeDeg(x, z);
                float n1 = Mathf.PerlinNoise(x * .004f + 17.3f, z * .004f + 3.1f);
                float n2 = Mathf.PerlinNoise(x * .03f + 5.7f, z * .03f + 11.9f);
                Biome b = biomes[j * rw + i];

                Color baseColor;
            switch (b)
            {
                    case Biome.Urban: baseColor = Color.Lerp(urban, field, n2 * .6f); break;
                    case Biome.Forest: baseColor = Color.Lerp(forest, fynbos, n2 * .35f); break;
                    case Biome.Field: baseColor = Color.Lerp(field, fynbos, n1 * .4f); break;
                    case Biome.Beach: baseColor = sandDry; break;
                    case Biome.Rock: baseColor = rock; break;
                default: baseColor = Color.Lerp(fynbos, fynbosDry, Mathf.SmoothStep(0f, 1f, n1 * .8f + Mathf.Clamp01(above / 900f))); break;
            }
            // Großflächige Busch- und Trockenflecken verhindern eine gleichförmige Rasenoptik.
            if (b != Biome.Beach && b != Biome.Rock && b != Biome.Urban)
            {
                float n3 = Mathf.PerlinNoise(x * .0021f + 41.3f, z * .0021f + 9.7f);
                float n4 = Mathf.PerlinNoise(x * .011f + 2.9f, z * .011f + 77.1f);
                baseColor = Color.Lerp(baseColor, scrubDark, Mathf.SmoothStep(0f, .75f, Mathf.InverseLerp(.52f, .8f, n3 * .7f + n4 * .3f)));
                baseColor = Color.Lerp(baseColor, fynbosDry, Mathf.SmoothStep(0f, .5f, Mathf.InverseLerp(.62f, .85f, n4)));
            }
            // Hänge -> Fels (Tafelberg / Zwölf Apostel / Chapman's Peak)
                float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 38f, slope));
                baseColor = Color.Lerp(baseColor, Color.Lerp(rock, rockDark, n2), rockW);
                // Strand an flachen Küstenstreifen
                float beachW = (1f - Mathf.InverseLerp(1.5f, 5f, above)) * (1f - Mathf.InverseLerp(8f, 16f, slope));
                baseColor = Color.Lerp(baseColor, Color.Lerp(sandWet, sandDry, Mathf.InverseLerp(0f, 2.5f, above)), Mathf.Clamp01(beachW));
                if (above < -0.5f) baseColor = Color.Lerp(sandWet, seabed, Mathf.InverseLerp(-0.5f, -8f, above));
                float v = .92f + n2 * .16f;
                baseColor = new Color(baseColor.r * v, baseColor.g * v, baseColor.b * v, 1f);
                px[j * rw + i] = baseColor;
            }
            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, true) { name = "TerrainColors", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ---------------------------------------------------------------- OSM-Biome rastern
        private void RasterizeBiomes(OsmContext osm)
        {
            // Reihenfolge = Priorität (später überschreibt früher).
            var order = new[] { Biome.Urban, Biome.Field, Biome.Scrub, Biome.Forest, Biome.Rock, Biome.Beach, Biome.Water };
            var xs = new List<float>();
            foreach (Biome wanted in order)
                foreach (var area in osm.Areas)
                {
                    if (ToBiome(area.biome) != wanted) continue;
                    var ring = area.ring;
                    float minZ = float.MaxValue, maxZ = float.MinValue;
                    foreach (var p in ring) { minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
                    int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - rMinZ) / RasterCell)), j1 = Mathf.Min(rh - 1, Mathf.CeilToInt((maxZ - rMinZ) / RasterCell));
                    for (int j = j0; j <= j1; j++)
                    {
                        float zc = rMinZ + (j + .5f) * RasterCell;
                        xs.Clear();
                        for (int a = 0, b = ring.Count - 1; a < ring.Count; b = a++)
                        {
                            Vector2 p = ring[a], q = ring[b];
                            if ((p.y > zc) == (q.y > zc)) continue;
                            xs.Add(p.x + (zc - p.y) / (q.y - p.y) * (q.x - p.x));
                        }
                        xs.Sort();
                        for (int k = 0; k + 1 < xs.Count; k += 2)
                        {
                            int i0 = Mathf.Max(0, Mathf.CeilToInt((xs[k] - rMinX) / RasterCell - .5f));
                            int i1 = Mathf.Min(rw - 1, Mathf.FloorToInt((xs[k + 1] - rMinX) / RasterCell - .5f));
                            for (int i = i0; i <= i1; i++) biomes[j * rw + i] = wanted;
                        }
                    }
                }
        }

        public static Biome ToBiome(string osmBiome)
        {
            switch (osmBiome)
            {
                case "urban": case "parking": return Biome.Urban;
                case "scrub": return Biome.Scrub;
                case "forest": return Biome.Forest;
                case "field": return Biome.Field;
                case "beach": return Biome.Beach;
                case "rock": return Biome.Rock;
                case "water": return Biome.Water;
                default: return Biome.Generic;
            }
        }
    }
}
