using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Vegetation in zwei Bändern statt einer einzelnen Reihe 7–22 m neben der Straße:
    //   Nahband  7.5–40 m : dicht, inkl. Gras/Blumen
    //   Mittelband 40–220 m: in Gruppen (Noise-Clumping), nur Bäume/Büsche/Felsen
    // Dichte je OSM-Biom, Felsen statt Bäumen an steilen Hängen, nichts auf Straße,
    // Strand oder in Gebäuden. Nur echte Pflanzen-Prefabs (keine Bodenkacheln, keine toten Bäume).
    public static class VegetationPlacer
    {
        private const float Cell = 8f;
        private const float NearBand = 40f, FarBand = 220f, RoadClear = 7.5f;
        public const int Budget = 12000;

        private enum Kind { Tree, Bush, Small, Rock }
        private struct Candidate { public Vector3 pos; public Kind kind; public bool pine; }

        // Wahrscheinlichkeit pro 8×8-m-Zelle: Baum, Busch, Klein, Fels (Nahband | Mittelband)
        private static readonly Dictionary<WorldTerrain.Biome, float[]> Near = new Dictionary<WorldTerrain.Biome, float[]>
        {
            { WorldTerrain.Biome.Urban,   new[] { .04f, .04f, .00f, .00f } },
            { WorldTerrain.Biome.Scrub,   new[] { .01f, .22f, .12f, .03f } },
            { WorldTerrain.Biome.Forest,  new[] { .30f, .08f, .04f, .01f } },
            { WorldTerrain.Biome.Field,   new[] { .02f, .03f, .15f, .00f } },
            { WorldTerrain.Biome.Generic, new[] { .02f, .15f, .08f, .02f } },
            { WorldTerrain.Biome.Beach,   new[] { .00f, .00f, .02f, .01f } },
            { WorldTerrain.Biome.Rock,    new[] { .00f, .04f, .00f, .12f } },
            { WorldTerrain.Biome.Water,   new[] { .00f, .00f, .00f, .00f } },
        };
        private static readonly Dictionary<WorldTerrain.Biome, float[]> Mid = new Dictionary<WorldTerrain.Biome, float[]>
        {
            { WorldTerrain.Biome.Urban,   new[] { .02f, .02f, 0f, .00f } },
            { WorldTerrain.Biome.Scrub,   new[] { .005f, .09f, 0f, .015f } },
            { WorldTerrain.Biome.Forest,  new[] { .22f, .04f, 0f, .01f } },
            { WorldTerrain.Biome.Field,   new[] { .01f, .015f, 0f, .00f } },
            { WorldTerrain.Biome.Generic, new[] { .01f, .06f, 0f, .012f } },
            { WorldTerrain.Biome.Beach,   new[] { .00f, .00f, 0f, .004f } },
            { WorldTerrain.Biome.Rock,    new[] { .00f, .02f, 0f, .07f } },
            { WorldTerrain.Biome.Water,   new[] { .00f, .00f, 0f, .00f } },
        };

        public static int Place(WorldTerrain terrain, AssetCatalog catalog, Occupancy occupied, Transform parent, int seed = 4242)
        {
            var trees = WorldPlacement.Pool(catalog, @"Env_Tree_\d+$", "Dead");
            var pines = WorldPlacement.Pool(catalog, @"Tree_Pine_\d+$");
            var bushes = WorldPlacement.Pool(catalog, @"Env_(Bush|Bush_Large|Shrub)_\d+$");
            var small = WorldPlacement.Pool(catalog, @"Env_(Grass|Grass_Tall|Flowers?|Fern)_\d+$");
            var rocks = WorldPlacement.Pool(catalog, @"Env_Rock_\d+$");
            if (trees.Count == 0 && pines.Count == 0 && bushes.Count == 0)
            { Debug.LogWarning("Vegetation: keine passenden Prefabs im Katalog."); return 0; }
            if (trees.Count == 0) trees = pines;
            if (pines.Count == 0) pines = trees;

            var rng = new System.Random(seed);
            var candidates = Collect(terrain, occupied, rng);

            // Budget: gleichmäßig ausdünnen statt am Streckenende abzuschneiden.
            float keep = candidates.Count > Budget ? Budget / (float)candidates.Count : 1f;
            var groups = new Dictionary<long, Transform>();
            int placed = 0;
            foreach (var c in candidates)
            {
                if (keep < 1f && rng.NextDouble() > keep) continue;
                List<GameObject> pool;
                switch (c.kind)
                {
                    case Kind.Tree: pool = c.pine ? pines : trees; break;
                    case Kind.Bush: pool = bushes; break;
                    case Kind.Small: pool = small; break;
                    default: pool = rocks; break;
                }
                if (pool.Count == 0) continue;

                float y = terrain.HeightAt(c.pos.x, c.pos.z);
                Transform group = Group(groups, parent, c.pos);
                Quaternion rot = Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f);
                float scale, sink, cull; bool shadows = true;
                switch (c.kind)
                {
                    case Kind.Tree: scale = WorldPlacement.Range(rng, .8f, 1.3f); sink = .15f; cull = .008f; break;
                    case Kind.Bush: scale = WorldPlacement.Range(rng, .7f, 1.35f); sink = .1f; cull = .015f; break;
                    case Kind.Small: scale = WorldPlacement.Range(rng, .8f, 1.4f); sink = .02f; cull = .04f; shadows = false; break;
                    default:
                        scale = WorldPlacement.Range(rng, .5f, 1.6f); sink = .3f * scale; cull = .012f;
                        rot = Quaternion.Euler(WorldPlacement.Range(rng, -15f, 15f), WorldPlacement.Range(rng, 0f, 360f), WorldPlacement.Range(rng, -15f, 15f));
                        break;
                }
                var go = WorldPlacement.Spawn(WorldPlacement.Pick(pool, rng), group, new Vector3(c.pos.x, y, c.pos.z), rot, scale, y, sink);
                WorldPlacement.CullWhenSmall(go, cull, shadows);
                placed++;
            }
            Debug.Log($"Vegetation: {placed} Objekte (Kandidaten {candidates.Count}, Budget {Budget}).");
            return placed;
        }

        private static List<Candidate> Collect(WorldTerrain terrain, Occupancy occupied, System.Random rng)
        {
            var dem = terrain.Dem;
            int w = Mathf.CeilToInt((dem.MaxX - dem.MinX) / Cell), h = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / Cell);
            // Grober Abstand zur Straße pro Zelle (Pinsel alle 8 m); exakt nur nahe der Fahrbahn.
            var dist2 = new float[w * h];
            for (int k = 0; k < dist2.Length; k++) dist2[k] = float.MaxValue;
            int rad = Mathf.CeilToInt(FarBand / Cell);
            var samples = terrain.Road.Samples;
            for (int s = 0; s < samples.Count; s += 4)
            {
                Vector3 p = samples[s].pos;
                int ci = Mathf.FloorToInt((p.x - dem.MinX) / Cell), cj = Mathf.FloorToInt((p.z - dem.MinZ) / Cell);
                for (int dj = -rad; dj <= rad; dj++)
                {
                    int j = cj + dj; if (j < 0 || j >= h) continue;
                    float dz = dem.MinZ + (j + .5f) * Cell - p.z;
                    for (int di = -rad; di <= rad; di++)
                    {
                        int i = ci + di; if (i < 0 || i >= w) continue;
                        float dx = dem.MinX + (i + .5f) * Cell - p.x;
                        float d2 = dx * dx + dz * dz;
                        int k = j * w + i;
                        if (d2 < dist2[k]) dist2[k] = d2;
                    }
                }
            }

            var result = new List<Candidate>();
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float approx = dist2[j * w + i];
                if (approx > FarBand * FarBand) continue;
                approx = Mathf.Sqrt(approx);
                float x = dem.MinX + (i + .5f + WorldPlacement.Range(rng, -.45f, .45f)) * Cell;
                float z = dem.MinZ + (j + .5f + WorldPlacement.Range(rng, -.45f, .45f)) * Cell;
                float roll = (float)rng.NextDouble();

                float dist = approx < 25f ? terrain.Road.Distance(x, z, 30f) : approx;  // exakt nur nahe der Straße
                if (dist < RoadClear || dist > FarBand) continue;
                bool nearBand = dist < NearBand;

                var biome = terrain.BiomeAt(x, z);
                float[] p = (nearBand ? Near : Mid)[biome];
                float n = Mathf.PerlinNoise(x / 90f + 31.7f, z / 90f + 7.3f);
                float clump = nearBand ? .5f + n : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.3f, .75f, n)) * 1.8f;
                float slope = terrain.SlopeDeg(x, z);
                float pt = p[0], pb = p[1], ps = p[2], pr = p[3];
                if (slope > 32f) { pt = 0f; pb *= .5f; ps = 0f; pr += .08f; }
                pt *= clump; pb *= clump; ps *= clump; pr *= nearBand ? 1f : .8f + n * .4f;

                Kind kind;
                if (roll < pt) kind = Kind.Tree;
                else if (roll < pt + pb) kind = Kind.Bush;
                else if (roll < pt + pb + ps) kind = Kind.Small;
                else if (roll < pt + pb + ps + pr) kind = Kind.Rock;
                else continue;

                if (terrain.DemY(x, z) < terrain.SeaY + .8f) continue;                 // nicht im Meer
                float clearance = kind == Kind.Tree ? 2f : kind == Kind.Small ? .4f : 1f;
                if (!occupied.IsFree(x, z, clearance)) continue;                        // nicht in Gebäuden/Landmarks

                float pineShare = biome == WorldTerrain.Biome.Forest ? .6f : biome == WorldTerrain.Biome.Urban ? .25f : .4f;
                result.Add(new Candidate { pos = new Vector3(x, 0f, z), kind = kind, pine = rng.NextDouble() < pineShare });
            }
            return result;
        }

        private static Transform Group(Dictionary<long, Transform> groups, Transform parent, Vector3 p)
        {
            int gx = Mathf.FloorToInt(p.x / 1000f), gz = Mathf.FloorToInt(p.z / 1000f);
            long key = ((long)gx << 32) | (uint)gz;
            if (!groups.TryGetValue(key, out Transform t))
            {
                t = new GameObject($"Vegetation_{gx}_{gz}").transform;
                t.SetParent(parent, false);
                groups[key] = t;
            }
            return t;
        }

        public static int PlaceClouds(WorldTerrain terrain, AssetCatalog catalog, Transform parent, int count = 36, int seed = 9001)
        {
            var clouds = WorldPlacement.Pool(catalog, @"Env_Cloud_\d+$");
            if (clouds.Count == 0) return 0;
            var rng = new System.Random(seed);
            var s = terrain.Road.Samples;
            for (int k = 0; k < count; k++)
            {
                Vector3 p = s[rng.Next(s.Count)].pos;
                var pos = new Vector3(p.x + WorldPlacement.Range(rng, -2500f, 2500f), terrain.SeaY + WorldPlacement.Range(rng, 420f, 650f),
                                      p.z + WorldPlacement.Range(rng, -2500f, 2500f));
                var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(WorldPlacement.Pick(clouds, rng), parent);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f));
                go.transform.localScale = Vector3.one * WorldPlacement.Range(rng, 25f, 50f);
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return count;
        }

        // Nature Biomes accents are intentionally sparse: palms only in low urban coastal
        // sections, birds only above water. They complement, rather than replace, the seeded bands.
        public static int PlaceCoastalAccents(WorldTerrain terrain, WorldAssets assets, Occupancy occupied, Transform parent, int seed = 318)
        {
            if (assets == null) return 0;
            var rng = new System.Random(seed);
            int placed = 0;
            if (assets.Palms != null)
            {
                var road = terrain.Road.Samples;
                for (int i = 0; i < road.Count; i += 10)
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 p = road[i].pos + road[i].side * (side * 7.5f);
                    if (terrain.BiomeAt(p.x, p.z) != WorldTerrain.Biome.Urban || terrain.DemY(p.x, p.z) - terrain.SeaY > 30f) continue;
                    if (terrain.Road.Distance(p.x, p.z, 10f) < RoadMeshBuilder.HalfWidth + 3f || !occupied.IsFree(p.x, p.z, 2.5f)) continue;
                    float y = terrain.HeightAt(p.x, p.z);
                    var go = WorldPlacement.Spawn(WorldPlacement.Pick(assets.Palms, rng), parent, new Vector3(p.x, y, p.z), Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .85f, 1.15f), y, .12f);
                    WorldPlacement.CullWhenSmall(go, .008f, true);
                    occupied.Add(p.x, p.z, 2.5f); placed++;
                }
            }
            if (assets.Birds != null && assets.Birds.Count > 0)
            {
                for (int i = 0; i < 10; i++)
                {
                    var r = terrain.Road.Samples[rng.Next(terrain.Road.Samples.Count)];
                    Vector3 p = r.pos + r.side * WorldPlacement.Range(rng, -160f, 160f);
                    if (terrain.DemY(p.x, p.z) - terrain.SeaY > 25f) continue;
                    var bird = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(WorldPlacement.Pick(assets.Birds, rng), parent);
                    bird.transform.position = new Vector3(p.x, Mathf.Max(r.pos.y, terrain.SeaY) + WorldPlacement.Range(rng, 16f, 38f), p.z);
                    bird.transform.rotation = Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f);
                    foreach (var renderer in bird.GetComponentsInChildren<Renderer>()) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    placed++;
                }
            }
            Debug.Log($"Küsten-Akzente: {placed} Palmen/Vögel.");
            return placed;
        }
    }
}
