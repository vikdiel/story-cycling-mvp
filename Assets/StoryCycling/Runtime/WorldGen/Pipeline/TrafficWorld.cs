using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Hintergrundverkehr einer Welt: Spurgraph aus dem Straßennetz + "Traffic"-Objekt mit Autopool (TrafficSystem). Editor und Gerät;
    // der Editor schreibt den Graph zusätzlich als Datei (TrafficSceneBuilder), auf dem Gerät bekommt das TrafficSystem ihn direkt.
    public static class TrafficWorld
    {
        public static TrafficSystem Build(RouteWorldConfig cfg, RoadNet net, RoadSurface.Result surf, AssetCatalog catalog, CarPaintSet carPaint, Transform rider, out TrafficGraph graph)
        {
            graph = null;
            if (!cfg.buildTraffic || net == null || surf == null)
            {
                Debug.Log(!cfg.buildTraffic ? "Verkehr: abgeschaltet (RouteWorldConfig.buildTraffic)." : "Verkehr: ohne Straßennetz nicht möglich — übersprungen.");
                return null;
            }
            var opt = new TrafficGraphBuilder.Options
            {
                UrbanKmh = cfg.urbanSpeedKmh, RuralKmh = cfg.ruralSpeedKmh,
                OnAsphalt = (x, z) => surf.Asphalt.Inside(x, z)
            };
            graph = TrafficGraphBuilder.Build(net, opt, OsmDetailPlacer.LastParked, out var st);
            Debug.Log($"Verkehr: {st}");
            Debug.Log($"Verkehr: Spurgraph {graph.Lanes.Count} Spuren, {graph.Hubs.Count} Knoten, {graph.Parked.Count} parkende Autos als Sperrpunkte.");
            if (st.LanePointsOffAsphalt > 0) Debug.LogWarning($"Verkehr: {st.LanePointsOffAsphalt} Spurpunkte liegen abseits des Asphalts.");

            var cars = catalog != null ? OsmDetailPlacer.CarPrefabs(catalog) : null;
            if (cars == null || cars.Count == 0 || cfg.trafficCars <= 0)
            {
                Debug.LogWarning("Verkehr: Spurgraph gebacken, aber keine Auto-Prefabs im Katalog (oder trafficCars = 0) — keine Autos in der Szene.");
                return null;
            }
            var root = new GameObject("Traffic");
            var ts = root.AddComponent<TrafficSystem>();
            ts.rider = rider; ts.carPaint = carPaint; ts.carCount = cfg.trafficCars;
            ts.spawnMin = cfg.trafficSpawnMin; ts.spawnMax = cfg.trafficSpawnMax; ts.despawn = cfg.trafficDespawn;
            for (int i = 0; i < cfg.trafficCars; i++)
            {
                var car = WorldSpawn.Spawn(cars[i % cars.Count], root.transform);
                car.name = "Car " + i;
                foreach (var c in car.GetComponentsInChildren<Collider>()) c.enabled = false;     // keine Physik
                WorldPlacement.CullWhenSmall(car, .012f, true);
                car.SetActive(false);                                                              // das TrafficSystem schaltet sie beim Spawn ein
            }
            Debug.Log($"Verkehr: {cfg.trafficCars} Autos im Pool (Spawn {cfg.trafficSpawnMin:0}–{cfg.trafficSpawnMax:0} m vor dem Rad, weg ab {cfg.trafficDespawn:0} m).");
            return ts;
        }
    }
}
