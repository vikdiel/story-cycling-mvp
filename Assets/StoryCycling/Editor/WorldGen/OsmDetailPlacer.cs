using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // OSM-Punkte, Hecken und Parkplatz-Autos mit Gelände- und Fahrbahnbezug.
    public static class OsmDetailPlacer
    {
        public const bool PlaceFencesAndWalls = false;
        public const bool SignalFrontIsPositiveZ = true;
        public const bool LeftHandTraffic = true;

        private const float RoadBoundReach = 30f;
        private const float FurnitureReach = 20f;
        private const float ParkingReach = 150f;

        private sealed class Palette
        {
            public GameObject pole, arm, lamp, busStop, busSign, stop, giveWay, hydrant, meter, mailbox, bench, bin;
            public List<GameObject> heads, trees, rocks, bushes, cars;
        }

        public static int Place(RoadField road, WorldTerrain terrain, OsmContext ctx, AssetCatalog catalog,
                                Occupancy occupied, Transform parent, int seed = 777)
        {
            var pal = Load(catalog);
            RoadRef = road;
            TerrainRef = terrain;
            var rng = new System.Random(seed);
            var dedupe = new Dictionary<string, List<Vector2>>();
            int placed = 0, skippedSideStreet = 0;

            foreach (var pt in ctx.Points)
            {
                float x = pt.pos.x, z = pt.pos.y;
                bool hasRoad = road.Nearest(x, z, 60f, out int ri, out float dist);
                var rs = hasRoad ? road.Samples[ri] : default(RoadField.Sample);
                float lateral = hasRoad ? Vector3.Dot(new Vector3(x, 0f, z) - rs.pos, rs.side) : 0f;
                float sideSign = Mathf.Abs(lateral) > 2f ? Mathf.Sign(lateral) : (LeftHandTraffic ? -1f : 1f);

                switch (pt.kind)
                {
                    case "traffic_signals":
                    case "sign_stop":
                    case "sign_give_way":
                    case "bus_stop":
                        if (!hasRoad || dist > RoadBoundReach) { skippedSideStreet++; continue; }
                        if (!FirstWithin(dedupe, pt.kind, rs.pos, pt.kind == "bus_stop" ? 40f : 25f)) continue;
                        placed += PlaceRoadBound(pt.kind, rs, sideSign, pal, rng, parent, occupied);
                        break;

                    case "street_lamp":
                    case "fire_hydrant":
                    case "parking_meter":
                    case "mailbox":
                    case "bench":
                    case "waste_basket":
                    {
                        if (!hasRoad || dist > FurnitureReach) { skippedSideStreet++; continue; }
                        GameObject prefab = pt.kind == "street_lamp" ? pal.lamp : pt.kind == "fire_hydrant" ? pal.hydrant :
                            pt.kind == "parking_meter" ? pal.meter : pt.kind == "mailbox" ? pal.mailbox :
                            pt.kind == "bench" ? pal.bench : pal.bin;
                        if (prefab == null) continue;
                        float offset = pt.kind == "street_lamp" ? RoadMeshBuilder.HalfWidth + 1.1f :
                            Mathf.Max(Mathf.Abs(lateral), RoadMeshBuilder.HalfWidth + 1.8f);
                        Vector3 p = rs.pos + rs.side * (sideSign * offset);
                        if (!ClearOfAsphalt(p) || !occupied.IsFree(p.x, p.z, .5f)) continue;
                        Vector3 toRoad = -rs.side * sideSign;
                        float y = offset < WorldTerrain.FlatRadius ? rs.pos.y - .05f : terrain.HeightAt(p.x, p.z);
                        WorldPlacement.Spawn(prefab, parent, new Vector3(p.x, y, p.z), Quaternion.LookRotation(toRoad, Vector3.up), 1f, y, .02f);
                        placed++;
                        break;
                    }

                    case "tree":
                    case "rock":
                    {
                        var pool = pt.kind == "tree" ? pal.trees : pal.rocks;
                        if (pool.Count == 0) continue;
                        if (hasRoad && dist < (pt.kind == "tree" ? 6.5f : 5.5f)) continue;
                        if (OnSideStreet(x, z, .5f)) continue;
                        if (!occupied.IsFree(x, z, 1.5f)) continue;
                        float y = terrain.HeightAt(x, z);
                        var go = WorldPlacement.Spawn(WorldPlacement.Pick(pool, rng), parent, new Vector3(x, y, z),
                            Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .85f, 1.3f),
                            y, pt.kind == "tree" ? .15f : .25f);
                        WorldPlacement.CullWhenSmall(go, .01f, true);
                        placed++;
                        break;
                    }
                }
            }

            placed += PlaceHedges(road, terrain, ctx, pal, occupied, parent, rng);
            placed += PlaceParking(road, terrain, ctx, pal, occupied, parent, rng);
            Debug.Log($"OSM-Details: {placed} Objekte (Nebenstraßen-Objekte weggelassen: {skippedSideStreet}).");
            return placed;
        }

        private static int PlaceRoadBound(string kind, RoadField.Sample rs, float sideSign, Palette pal, System.Random rng,
                                          Transform parent, Occupancy occupied)
        {
            Vector3 flatT = new Vector3(rs.tangent.x, 0f, rs.tangent.z).normalized;
            Vector3 toRoad = -rs.side * sideSign;
            Vector3 trafficDir = (sideSign < 0f) == LeftHandTraffic ? flatT : -flatT;
            Vector3 facing = SignalFrontIsPositiveZ ? -trafficDir : trafficDir;
            float y = rs.pos.y - .05f;

            switch (kind)
            {
                case "traffic_signals":
                {
                    if (pal.pole == null || pal.arm == null || pal.heads.Count == 0) return 0;
                    Vector3 foot = rs.pos + rs.side * (sideSign * (RoadMeshBuilder.HalfWidth + 1.0f));
                    foot.y = y;
                    if (!ClearOfAsphalt(foot)) return 0;
                    Quaternion rot = Quaternion.LookRotation(toRoad, Vector3.up);
                    var mast = new GameObject("TrafficSignal").transform;
                    mast.SetParent(parent, false);
                    mast.SetPositionAndRotation(foot, rot);
                    Part(pal.pole, mast, Vector3.zero, Quaternion.identity);
                    Part(pal.arm, mast, Vector3.zero, Quaternion.identity);
                    Quaternion headRot = Quaternion.Inverse(rot) * Quaternion.LookRotation(facing, Vector3.up);
                    Part(WorldPlacement.Pick(pal.heads, rng), mast, new Vector3(0f, 4.12f, 3.2f), headRot);
                    Part(WorldPlacement.Pick(pal.heads, rng), mast, new Vector3(0f, 4.12f, 5.6f), headRot);
                    occupied.Add(foot.x, foot.z, .6f);
                    return 1;
                }
                case "sign_stop":
                case "sign_give_way":
                {
                    var prefab = kind == "sign_stop" ? pal.stop : pal.giveWay;
                    if (prefab == null) return 0;
                    Vector3 p = rs.pos + rs.side * (sideSign * (RoadMeshBuilder.HalfWidth + 1.2f));
                    if (!ClearOfAsphalt(p)) return 0;
                    WorldPlacement.Spawn(prefab, parent, new Vector3(p.x, y, p.z), Quaternion.LookRotation(facing, Vector3.up), 1f, y, .02f);
                    return 1;
                }
                case "bus_stop":
                {
                    if (pal.busStop == null) return 0;
                    Vector3 p = rs.pos + rs.side * (sideSign * (RoadMeshBuilder.HalfWidth + 2.6f));
                    if (!ClearOfAsphalt(p) || !occupied.IsFree(p.x, p.z, 2f)) return 0;
                    WorldPlacement.Spawn(pal.busStop, parent, new Vector3(p.x, y, p.z), Quaternion.LookRotation(toRoad, Vector3.up), 1f, y, .02f);
                    Vector3 signPos = rs.pos + rs.side * (sideSign * (RoadMeshBuilder.HalfWidth + 1.2f)) + trafficDir * -3.5f;
                    if (pal.busSign != null && ClearOfAsphalt(signPos))
                        WorldPlacement.Spawn(pal.busSign, parent, new Vector3(signPos.x, y, signPos.z), Quaternion.LookRotation(facing, Vector3.up), 1f, y, .02f);
                    occupied.Add(p.x, p.z, 2.6f);
                    return 1;
                }
            }
            return 0;
        }

        private static RoadField RoadRef;
        private static WorldTerrain TerrainRef;
        private static bool ClearOfAsphalt(Vector3 p) =>
            RoadRef.Distance(p.x, p.z, 10f) >= RoadMeshBuilder.HalfWidth + .8f && !OnSideStreet(p.x, p.z, .8f);
        private static bool OnSideStreet(float x, float z, float margin)
        {
            if (TerrainRef?.Streets == null || !TerrainRef.Streets.Nearest(x, z, 12f, out int i, out float d)) return false;
            return d < TerrainRef.Streets.Samples[i].half + 2.2f + margin;
        }

        private static void Part(GameObject prefab, Transform mast, Vector3 localPos, Quaternion localRot)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, mast);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = Vector3.one;
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
        }

        private static bool FirstWithin(Dictionary<string, List<Vector2>> seen, string kind, Vector3 p, float radius)
        {
            if (!seen.TryGetValue(kind, out List<Vector2> list)) { list = new List<Vector2>(); seen[kind] = list; }
            var q = new Vector2(p.x, p.z);
            foreach (var o in list) if ((o - q).sqrMagnitude < radius * radius) return false;
            list.Add(q);
            return true;
        }

        private static int PlaceHedges(RoadField road, WorldTerrain terrain, OsmContext ctx, Palette pal,
                                       Occupancy occupied, Transform parent, System.Random rng)
        {
            if (pal.bushes.Count == 0) return 0;
            int placed = 0;
            foreach (var ln in ctx.Lines)
            {
                if (ln.kind != "hedge" && !(PlaceFencesAndWalls && ln.kind == "fence")) continue;
                for (int i = 1; i < ln.pts.Count; i++)
                {
                    Vector2 a = ln.pts[i - 1], b = ln.pts[i];
                    float len = Vector2.Distance(a, b);
                    for (float d = 0f; d < len; d += 2.2f)
                    {
                        Vector2 p = a + (b - a) * (d / Mathf.Max(len, 1e-3f));
                float rd = road.Distance(p.x, p.y, 70f);
                if (rd < 6f || rd >= 70f || OnSideStreet(p.x, p.y, 0f) || !occupied.IsFree(p.x, p.y, .8f)) continue;
                        float y = terrain.HeightAt(p.x, p.y);
                        var go = WorldPlacement.Spawn(WorldPlacement.Pick(pal.bushes, rng), parent, new Vector3(p.x, y, p.y),
                            Quaternion.Euler(0f, WorldPlacement.Range(rng, 0f, 360f), 0f), WorldPlacement.Range(rng, .8f, 1.1f), y, .1f);
                        WorldPlacement.CullWhenSmall(go, .015f, true);
                        placed++;
                    }
                }
            }
            return placed;
        }

        private static int PlaceParking(RoadField road, WorldTerrain terrain, OsmContext ctx, Palette pal,
                                        Occupancy occupied, Transform parent, System.Random rng)
        {
            if (pal.cars.Count == 0) return 0;
            const float stallW = 2.9f, rowPitch = 7.5f, occupancy = .45f;
            const int perLot = 35, total = 800;
            int placed = 0, lots = 0;
            foreach (var area in ctx.Areas)
            {
                if (area.biome != "parking") continue;
                if (placed >= total) break;
                var ring = area.ring;
                Vector2 mean = Vector2.zero;
                foreach (var v in ring) mean += v;
                mean /= ring.Count;
                if (road.Distance(mean.x, mean.y, ParkingReach + 1f) > ParkingReach) continue;

                float sxx = 0, sxz = 0, szz = 0;
                foreach (var v in ring)
                {
                    Vector2 dd = v - mean;
                    sxx += dd.x * dd.x; sxz += dd.x * dd.y; szz += dd.y * dd.y;
                }
                float ang = .5f * Mathf.Atan2(2f * sxz, sxx - szz);
                Vector2 major = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)), minor = new Vector2(-major.y, major.x);
                float aMin = float.MaxValue, aMax = float.MinValue, bMin = float.MaxValue, bMax = float.MinValue;
                foreach (var v in ring)
                {
                    Vector2 dd = v - mean;
                    aMin = Mathf.Min(aMin, Vector2.Dot(dd, major)); aMax = Mathf.Max(aMax, Vector2.Dot(dd, major));
                    bMin = Mathf.Min(bMin, Vector2.Dot(dd, minor)); bMax = Mathf.Max(bMax, Vector2.Dot(dd, minor));
                }
                if ((aMax - aMin) < 6f || (bMax - bMin) < 5f) continue;

                int inLot = 0, row = 0;
                for (float b = bMin + 3f; b <= bMax - 2.5f && inLot < perLot; b += rowPitch, row++)
                for (float a = aMin + 1.6f; a <= aMax - 1.6f && inLot < perLot && placed < total; a += stallW)
                {
                    if (rng.NextDouble() > occupancy) continue;
                    Vector2 c = mean + major * a + minor * b;
                    Vector2 front = c + minor * 2.3f, back = c - minor * 2.3f;
                    if (!OsmContext.PointInPolygon(c, ring) || !OsmContext.PointInPolygon(front, ring) || !OsmContext.PointInPolygon(back, ring)) continue;
                if (road.Distance(c.x, c.y, 10f) < RoadMeshBuilder.HalfWidth + 3f || OnSideStreet(c.x, c.y, -1f)) continue;
                    if (terrain.SlopeDeg(c.x, c.y) > 10f || !occupied.IsFree(c.x, c.y, 1.2f)) continue;

                    Vector3 fwd = new Vector3(minor.x, 0f, minor.y) * (row % 2 == 0 ? 1f : -1f);
                    fwd = Quaternion.Euler(0f, WorldPlacement.Range(rng, -4f, 4f), 0f) * fwd;
                    float y = terrain.HeightAt(c.x, c.y);
                    var car = WorldPlacement.Spawn(WorldPlacement.Pick(pal.cars, rng), parent, new Vector3(c.x, y, c.y),
                        Quaternion.LookRotation(fwd, Vector3.up), 1f, y, .02f);
                    WorldPlacement.CullWhenSmall(car, .012f, true);
                    occupied.Add(c.x, c.y, 1.2f);
                    inLot++; placed++;
                }
                if (inLot > 0) lots++;
            }
            Debug.Log($"Parkplätze: {placed} Autos auf {lots} Flächen.");
            return placed;
        }

        private static Palette Load(AssetCatalog catalog)
        {
            System.Func<string, GameObject> One = regex =>
            {
                var l = WorldPlacement.Pool(catalog, regex);
                if (l.Count == 0) Debug.LogWarning("OSM-Detail: kein Prefab für " + regex);
                return l.Count > 0 ? l[0] : null;
            };
            return new Palette
            {
                pole = One(@"^SM_Prop_LightPole_Base_02$"),
                arm = One(@"^SM_Prop_LightPole_Arm_01$"),
                lamp = One(@"^SM_Prop_LightPole_Base_01$"),
                heads = WorldPlacement.Pool(catalog, @"^SM_Prop_TrafficLight_\d+$"),
                busStop = One(@"^SM_Prop_BusStop_\d+$"),
                busSign = One(@"^SM_Prop_Sign_Bustop_\d+$"),
                stop = One(@"^SM_Prop_Sign_Stop_\d+$"),
                giveWay = One(@"^SM_Prop_Sign_GiveWay_\d+$"),
                hydrant = One(@"Hydrant_\d+$"),
                meter = One(@"ParkingMeter"),
                mailbox = One(@"Mailbox"),
                bench = One(@"ParkBench_\d+$|Bench_\d+$"),
                bin = One(@"TrashCan|Trashbin|Bin_\d+$"),
                trees = WorldPlacement.Pool(catalog, @"Env_Tree_(Pine_)?\d+$", "Dead"),
                rocks = WorldPlacement.Pool(catalog, @"Env_Rock_\d+$"),
                bushes = WorldPlacement.Pool(catalog, @"Env_(Bush|Bush_Large|Shrub)_\d+$"),
                cars = WorldPlacement.Pool(catalog, @"^SM_Veh_Car_\w+_\d+$", "Police|Taxi|Ambo|Steering")
            };
        }
    }
}
