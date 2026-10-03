using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Straßenbegleitendes Grün und Küstendetails, die nicht aus der Ökotop-Verteilung kommen:
    //   Palmenallee: innerorts in Meeresnähe (Camps Bay, Sea Point) entlang der Route
    //   Mittelstreifen-Palmen, Kreisverkehr-Inseln, Vogelschwärme, Wolken
    // Die Landschaftsvegetation (Büsche, Bäume, Felsen, Gras) setzt VegetationScatter nach Ökotopen (Gruppen, Entfernungsbänder);
    // die frühere Zufallsverteilung je 8-m-Zelle ist entfernt.
    public static class VegetationPlacer
    {
        // Palmenallee (Camps Bay, Sea Point) als einzelne Objekte; Teil der Straßenarchitektur, nicht der Landschaft
        public static int PlaceAvenue(WorldTerrain terrain, WorldAssets a, Occupancy occupied, Transform parent, int seed = 4242)
            => PlacePalmAvenue(terrain, a, occupied, parent, new Dictionary<long, Transform>(), new System.Random(seed));

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
                    Vector3 p = s[k].pos + s[k].side * (side * (RoadMeshBuilder.HalfWidth + 3.6f));
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

        // Palmen im Mittelstreifen von Doppelfahrbahnen (Lücke > 1 m), alle 20 m
        public static int PlaceMedianPalms(RoadNet net, WorldAssets a, Occupancy occupied, Transform parent, int seed = 2024)
        {
            if (a.Palms.Count == 0 || net == null) return 0;
            var rng = new System.Random(seed);
            int placed = 0;
            foreach (var p in net.MedianPalmSpots)
            {
                if (!occupied.IsFree(p.x, p.z, 2f)) continue;
                var go = WorldPlacement.Spawn(WorldPlacement.Pick(a.Palms, rng), parent, p,
                    Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .9f, 1.1f), p.y + .12f, .05f);
                WorldPlacement.CullWhenSmall(go, .006f, true);
                occupied.Add(p.x, p.z, 2f);
                placed++;
            }
            Debug.Log($"Mittelstreifen: {placed} Palmen.");
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
                var go = WorldSpawn.Spawn(WorldPlacement.Pick(a.Birds, rng), parent);
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
                var go = WorldSpawn.Spawn(WorldPlacement.Pick(clouds, rng), parent);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f));
                go.transform.localScale = Vector3.one * WorldPlacement.Range(rng, 25f, 50f);
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return count;
        }
    }
}
