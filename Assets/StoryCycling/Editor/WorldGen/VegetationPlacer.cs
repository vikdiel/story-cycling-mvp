using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Vegetation und Küstendetails:
    //   Nahband  7.5–40 m : dicht, inkl. Grasbüschel/Blumen
    //   Mittelband 40–220 m: in Gruppen (Noise-Clumping), Bäume/Büsche/Felsen
    //   Küstenstreifen: Granit-Findlinge an Felsküsten, Treibholz/Seetang am Strand
    //   Palmenallee: innerorts in Meeresnähe (Camps Bay, Sea Point) entlang der Route
    //   Kreisverkehr-Inseln: Palme bzw. Buschgruppe in der Mitte
    // Arten je Biom kommen aus WorldAssets (PolygonCity/Generic + Nature Biomes).
    public static class VegetationPlacer
    {
        private const float Cell = 8f;
        private const float NearBand = 40f, FarBand = 220f, RoadClear = 7.5f;
        public const int Budget = 14000;

        private enum Kind { Tree, Bush, Small, Rock, Boulder, Beach }
        private struct Candidate { public Vector3 pos; public Kind kind; public WorldTerrain.Biome biome; public float above; }

        // Wahrscheinlichkeit pro 8×8-m-Zelle: Baum, Busch, Klein, Fels (Nahband | Mittelband)
        private static readonly Dictionary<WorldTerrain.Biome, float[]> Near = new Dictionary<WorldTerrain.Biome, float[]>
        {
            { WorldTerrain.Biome.Urban,   new[] { .05f, .05f, .02f, .00f } },
            { WorldTerrain.Biome.Scrub,   new[] { .01f, .22f, .14f, .03f } },
            { WorldTerrain.Biome.Forest,  new[] { .30f, .08f, .05f, .01f } },
            { WorldTerrain.Biome.Field,   new[] { .02f, .03f, .18f, .00f } },
            { WorldTerrain.Biome.Generic, new[] { .02f, .15f, .10f, .02f } },
            { WorldTerrain.Biome.Beach,   new[] { .00f, .00f, .02f, .01f } },
            { WorldTerrain.Biome.Rock,    new[] { .00f, .04f, .00f, .12f } },
            { WorldTerrain.Biome.Water,   new[] { .00f, .00f, .00f, .00f } },
        };
        private static readonly Dictionary<WorldTerrain.Biome, float[]> Mid = new Dictionary<WorldTerrain.Biome, float[]>
        {
            { WorldTerrain.Biome.Urban,   new[] { .03f, .02f, 0f, .00f } },
            { WorldTerrain.Biome.Scrub,   new[] { .005f, .09f, 0f, .015f } },
            { WorldTerrain.Biome.Forest,  new[] { .22f, .04f, 0f, .01f } },
            { WorldTerrain.Biome.Field,   new[] { .01f, .015f, 0f, .00f } },
            { WorldTerrain.Biome.Generic, new[] { .01f, .06f, 0f, .012f } },
            { WorldTerrain.Biome.Beach,   new[] { .00f, .00f, 0f, .004f } },
            { WorldTerrain.Biome.Rock,    new[] { .00f, .02f, 0f, .07f } },
            { WorldTerrain.Biome.Water,   new[] { .00f, .00f, 0f, .00f } },
        };

        public static int Place(WorldTerrain terrain, WorldAssets a, Occupancy occupied, Transform parent, int seed = 4242)
        {
            var rng = new System.Random(seed);
            var groups = new Dictionary<long, Transform>();
            int placed = PlacePalmAvenue(terrain, a, occupied, parent, groups, rng);

            var candidates = Collect(terrain, occupied, rng);
            float keep = candidates.Count > Budget ? Budget / (float)candidates.Count : 1f;   // gleichmäßig ausdünnen
            var counts = new Dictionary<Kind, int>();
            foreach (var c in candidates)
            {
                if (keep < 1f && rng.NextDouble() > keep) continue;
                GameObject prefab = Choose(c, a, rng);
                if (prefab == null) continue;

                float y = terrain.HeightAt(c.pos.x, c.pos.z);
                Quaternion rot = Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f);
                float scale, sink, cull; bool shadows = true;
                switch (c.kind)
                {
                    case Kind.Tree: scale = WorldPlacement.Range(rng, .8f, 1.25f); sink = .15f; cull = .008f; break;
                    case Kind.Bush: scale = WorldPlacement.Range(rng, .7f, 1.3f); sink = .1f; cull = .015f; break;
                    case Kind.Small: scale = WorldPlacement.Range(rng, .9f, 1.5f); sink = .03f; cull = .035f; shadows = false; break;
                    case Kind.Beach: scale = WorldPlacement.Range(rng, .8f, 1.2f); sink = .08f; cull = .02f; shadows = false; break;
                    case Kind.Boulder:
                        scale = WorldPlacement.Range(rng, 1.2f, 3.2f); sink = .35f * scale; cull = .006f;
                        rot = Quaternion.Euler(WorldPlacement.Range(rng, -20f, 20f), WorldPlacement.Range(rng, 0f, 360f), WorldPlacement.Range(rng, -20f, 20f));
                        break;
                    default:
                        scale = WorldPlacement.Range(rng, .5f, 1.6f); sink = .3f * scale; cull = .012f;
                        rot = Quaternion.Euler(WorldPlacement.Range(rng, -15f, 15f), WorldPlacement.Range(rng, 0f, 360f), WorldPlacement.Range(rng, -15f, 15f));
                        break;
                }
                // Riesige Waldbäume (17 m) und Felshaufen (13 m) im Pack auf Kap-Maß bringen
                if (a.ForestTrees.Contains(prefab)) scale *= .6f;
                if (a.RockPiles.Contains(prefab)) scale *= .35f;
                var go = WorldPlacement.Spawn(prefab, Group(groups, parent, c.pos), new Vector3(c.pos.x, y, c.pos.z), rot, scale, y, sink);
                if (c.kind == Kind.Rock || c.kind == Kind.Boulder || c.kind == Kind.Beach)
                {
                    // Große Felsen ragen sonst trotz Mittelpunkt-Abstand in Fahrbahn/Querstraße.
                    Bounds bb = WorldPlacement.BoundsOf(go);
                    float r = Mathf.Max(bb.extents.x, bb.extents.z);
                    if (terrain.Road.Distance(bb.center.x, bb.center.z, r + 8f) < RoadMeshBuilder.HalfWidth + 1.8f + r ||
                        OnStreet(terrain, bb.center.x, bb.center.z, r))
                    { Object.DestroyImmediate(go); continue; }
                }
                WorldPlacement.CullWhenSmall(go, cull, shadows);
                counts[c.kind] = (counts.TryGetValue(c.kind, out int n) ? n : 0) + 1;
                placed++;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in counts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            Debug.Log($"Vegetation: {placed} Objekte ({sb}Kandidaten {candidates.Count}, Budget {Budget}).");
            return placed;
        }

        // Artenmix je Biom — hier entscheidet sich der "Kap-Look".
        private static GameObject Choose(Candidate c, WorldAssets a, System.Random rng)
        {
            bool coastal = c.above < 35f;
            switch (c.kind)
            {
                case Kind.Tree:
                    switch (c.biome)
                    {
                        case WorldTerrain.Biome.Urban:
                            return WorldAssets.Pick(rng, (a.Palms, coastal ? .45f : .12f), (a.BroadTrees, .3f), (a.CoastalTrees, .25f), (a.Pines, .1f));
                        case WorldTerrain.Biome.Forest:
                            return WorldAssets.Pick(rng, (a.ForestTrees, .35f), (a.Pines, .35f), (a.BroadTrees, .3f));
                        case WorldTerrain.Biome.Field:
                            return WorldAssets.Pick(rng, (a.BroadTrees, .5f), (a.CoastalTrees, .3f), (a.Pines, .2f));
                        default:
                            return WorldAssets.Pick(rng, (a.CoastalTrees, .3f), (a.Pines, .35f), (a.BroadTrees, .35f));
                    }
                case Kind.Bush:
                    switch (c.biome)
                    {
                        case WorldTerrain.Biome.Urban:
                            return WorldAssets.Pick(rng, (a.LushBushes, .35f), (a.PalmBushes, coastal ? .25f : .1f), (a.Bushes, .45f));
                        case WorldTerrain.Biome.Forest:
                            return WorldAssets.Pick(rng, (a.Bushes, .5f), (a.Ferns, .5f));
                        case WorldTerrain.Biome.Field:
                            return WorldAssets.Pick(rng, (a.Bushes, 1f));
                        default:   // Fynbos: niedrige Büsche + Farne als Restio-Ersatz
                            return WorldAssets.Pick(rng, (a.Bushes, .7f), (a.Ferns, .15f), (a.LushBushes, .15f));
                    }
                case Kind.Small:
                    return c.biome == WorldTerrain.Biome.Scrub || c.biome == WorldTerrain.Biome.Generic
                        ? WorldAssets.Pick(rng, (a.Grass, .55f), (a.Flowers, .35f), (a.Ferns, .1f))   // Fynbos blüht
                        : WorldAssets.Pick(rng, (a.Grass, .7f), (a.Flowers, .2f), (a.Ferns, .1f));
                case Kind.Boulder:
                    return WorldAssets.Pick(rng, (a.Boulders, .55f), (a.Rocks, .3f), (a.FlatRocks, .1f), (a.RockPiles, .05f));
                case Kind.Beach:
                    return WorldAssets.Pick(rng, (a.Driftwood, .4f), (a.Seaweed, .6f));
                default:
                    return WorldAssets.Pick(rng, (a.Rocks, .7f), (a.Boulders, .3f));
            }
        }

        private static List<Candidate> Collect(WorldTerrain terrain, Occupancy occupied, System.Random rng)
        {
            var dem = terrain.Dem;
            int w = Mathf.CeilToInt((dem.MaxX - dem.MinX) / Cell), h = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / Cell);
            // Grober Abstand zur Route pro Zelle (Pinsel alle 8 m); exakt nur nahe der Fahrbahn.
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

                float dist = approx < 25f ? terrain.Road.Distance(x, z, 30f) : approx;
                if (dist < RoadClear || dist > FarBand) continue;
                if (OnStreet(terrain, x, z, 1.5f)) continue;
                bool nearBand = dist < NearBand;
                var biome = terrain.BiomeAt(x, z);
                float above = terrain.DemY(x, z) - terrain.SeaY;
                float slope = terrain.SlopeDeg(x, z);

                // Küstenstreifen: direkt an der Wasserlinie
                if (above > -1.5f && above < 4f && biome != WorldTerrain.Biome.Urban)
                {
                    bool rocky = slope > 12f || biome == WorldTerrain.Biome.Rock;
                    float pc = rocky ? .22f : .03f;
                    Kind coastKind = rocky || roll < .004f ? Kind.Boulder : Kind.Beach;
                    if (coastKind == Kind.Beach && above < .3f) continue;                 // Treibholz nicht unter Wasser
                    if (roll < pc && occupied.IsFree(x, z, 1.5f))
                        result.Add(new Candidate { pos = new Vector3(x, 0f, z), kind = coastKind, biome = biome, above = above });
                    continue;
                }
                if (above < .8f) continue;                                                 // nicht im Meer

                float[] p = (nearBand ? Near : Mid)[biome];
                float n = Mathf.PerlinNoise(x / 90f + 31.7f, z / 90f + 7.3f);
                float clump = nearBand ? .5f + n : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.3f, .75f, n)) * 1.8f;
                float pt = p[0], pb = p[1], ps = p[2], pr = p[3];
                if (slope > 32f) { pt = 0f; pb *= .5f; ps = 0f; pr += .08f; }
                pt *= clump; pb *= clump; ps *= clump; pr *= nearBand ? 1f : .8f + n * .4f;

                Kind kind;
                if (roll < pt) kind = Kind.Tree;
                else if (roll < pt + pb) kind = Kind.Bush;
                else if (roll < pt + pb + ps) kind = Kind.Small;
                else if (roll < pt + pb + ps + pr) kind = Kind.Rock;
                else continue;

                float clearance = kind == Kind.Tree ? 2f : kind == Kind.Small ? .4f : 1f;
                if (!occupied.IsFree(x, z, clearance)) continue;                        // nicht in Gebäuden/Landmarks/Autos
                result.Add(new Candidate { pos = new Vector3(x, 0f, z), kind = kind, biome = biome, above = above });
            }
            return result;
        }

        // Liegt der Punkt auf einer Querstraße inkl. Gehweg (+ Rand)?
        public static bool OnStreet(WorldTerrain terrain, float x, float z, float margin)
        {
            if (terrain.Streets == null) return false;
            return terrain.Streets.Nearest(x, z, 10f, out int i, out float d) && d < terrain.Streets.Samples[i].half + 2.8f + margin;
        }

        // Palmen entlang der Route innerorts in Meeresnähe (Victoria Road, Camps Bay, Sea Point).
        private static int PlacePalmAvenue(WorldTerrain terrain, WorldAssets a, Occupancy occupied, Transform parent,
                                           Dictionary<long, Transform> groups, System.Random rng)
        {
            if (a.Palms.Count == 0) return 0;
            int placed = 0;
            var s = terrain.Road.Samples;
            for (int k = 0; k < s.Count; k += 9)                                      // ~18 m Abstand
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 p = s[k].pos + s[k].side * (side * 7.4f);
                    if (terrain.BiomeAt(p.x, p.z) != WorldTerrain.Biome.Urban) continue;
                    if (terrain.DemY(p.x, p.z) - terrain.SeaY > 30f || terrain.SlopeDeg(p.x, p.z) > 15f) continue;
                    if (terrain.Road.Distance(p.x, p.z, 10f) < RoadMeshBuilder.HalfWidth + 3f) continue;
                    if (OnStreet(terrain, p.x, p.z, 1f) || !occupied.IsFree(p.x, p.z, 2.5f)) continue;
                    float y = terrain.HeightAt(p.x, p.z);
                    var go = WorldPlacement.Spawn(WorldPlacement.Pick(a.Palms, rng), Group(groups, parent, p), new Vector3(p.x, y, p.z),
                        Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .9f, 1.15f), y, .1f);
                    WorldPlacement.CullWhenSmall(go, .006f, true);
                    occupied.Add(p.x, p.z, 3f);
                    placed++;
                }
            Debug.Log($"Palmenallee: {placed} Palmen.");
            return placed;
        }

        // Kreisverkehr-Mittelinseln bepflanzen.
        public static int PlaceIslands(StreetNetwork net, RoadField main, WorldAssets a, Transform parent, int seed = 77)
        {
            var rng = new System.Random(seed);
            int placed = 0;
            foreach (var isl in net.Islands)
            {
                float y = net.IslandBaseY(isl, main);
                if (float.IsNaN(y)) continue;
                y += .18f;
                GameObject centre = isl.Radius > 4f ? WorldAssets.Pick(rng, (a.Palms, 1f), (a.CoastalTrees, .5f)) : null;
                if (centre != null)
                {
                    WorldPlacement.Spawn(centre, parent, new Vector3(isl.Center.x, y, isl.Center.z), Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), 1f, y, .1f);
                    placed++;
                }
                int ring = Mathf.Clamp(Mathf.RoundToInt(isl.Radius * 1.2f), 3, 14);
                for (int k = 0; k < ring; k++)
                {
                    float ang = k * Mathf.PI * 2f / ring + WorldPlacement.Range(rng, -.2f, .2f);
                    float r = isl.Radius * WorldPlacement.Range(rng, .45f, .75f);
                    var pos = new Vector3(isl.Center.x + Mathf.Cos(ang) * r, y, isl.Center.z + Mathf.Sin(ang) * r);
                    GameObject b = WorldAssets.Pick(rng, (a.LushBushes, .5f), (a.Flowers, .3f), (a.PalmBushes, .2f));
                    if (b == null) continue;
                    WorldPlacement.Spawn(b, parent, pos, Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .6f, .9f), y, .05f);
                    placed++;
                }
            }
            return placed;
        }

        // Ein paar Vogelschwärme über der Küste (Partikel-Prefabs des Nature-Packs).
        public static int PlaceBirds(WorldTerrain terrain, WorldAssets a, Transform parent, int count = 14, int seed = 31)
        {
            if (a.Birds.Count == 0) return 0;
            var rng = new System.Random(seed);
            var s = terrain.Road.Samples;
            int placed = 0;
            for (int tries = 0; tries < count * 20 && placed < count; tries++)
            {
                var p = s[rng.Next(s.Count)];
                Vector3 q = p.pos + p.side * WorldPlacement.Range(rng, -150f, 150f);
                if (terrain.DemY(q.x, q.z) - terrain.SeaY > 25f) continue;                   // nur an der Küste
                var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(WorldPlacement.Pick(a.Birds, rng), parent);
                go.transform.position = new Vector3(q.x, Mathf.Max(p.pos.y, terrain.SeaY) + WorldPlacement.Range(rng, 25f, 60f), q.z);
                placed++;
            }
            return placed;
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

        public static int PlaceClouds(WorldTerrain terrain, WorldAssets a, Transform parent, int count = 36, int seed = 9001)
        {
            var clouds = a.Clouds;
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
    }
}
