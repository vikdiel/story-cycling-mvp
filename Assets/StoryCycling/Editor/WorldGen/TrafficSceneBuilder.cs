using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Bäckt den Spurgraph des Hintergrundverkehrs (<route>.traffic.txt neben der GPX) und legt das "Traffic"-Objekt mit dem Autopool in die Szene.
    // Die Simulation läuft zur Laufzeit im TrafficSystem; hier entsteht nur Datei + Pool. Ohne Straßennetz/Asphalt gibt es keinen Verkehr.
    public static class TrafficSceneBuilder
    {
        public static void Build(RouteWorldConfig cfg, RoadNet net, RoadSurface.Result surf, AssetCatalog catalog, CarPaintSet carPaint, Transform rider)
        {
            string path = cfg.BakedTrafficPath;
            if (!cfg.buildTraffic || net == null || surf == null)
            {
                if (File.Exists(path)) AssetDatabase.DeleteAsset(path);                 // keine veraltete Datei zur Laufzeit
                Debug.Log(!cfg.buildTraffic ? "Verkehr: abgeschaltet (RouteWorldConfig.buildTraffic)." : "Verkehr: ohne Straßennetz nicht möglich — übersprungen.");
                return;
            }

            var opt = new TrafficGraphBuilder.Options
            {
                UrbanKmh = cfg.urbanSpeedKmh, RuralKmh = cfg.ruralSpeedKmh,
                OnAsphalt = (x, z) => surf.Asphalt.Inside(x, z)
            };
            var graph = TrafficGraphBuilder.Build(net, opt, OsmDetailPlacer.LastParked, out var st);
            string text = graph.ToText();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"Verkehr: {st}");
            Debug.Log($"Verkehr: Spurgraph {graph.Lanes.Count} Spuren, {graph.Hubs.Count} Knoten, {graph.Parked.Count} parkende Autos als Sperrpunkte -> {path} ({text.Length / 1024} KB).");
            if (st.LanePointsOffAsphalt > 0) Debug.LogWarning($"Verkehr: {st.LanePointsOffAsphalt} Spurpunkte liegen abseits des Asphalts.");

            var cars = catalog != null ? OsmDetailPlacer.CarPrefabs(catalog) : null;
            if (cars == null || cars.Count == 0 || cfg.trafficCars <= 0)
            {
                Debug.LogWarning("Verkehr: Spurgraph gebacken, aber keine Auto-Prefabs im Katalog (oder trafficCars = 0) — keine Autos in der Szene.");
                return;
            }

            var root = new GameObject("Traffic");
            var ts = root.AddComponent<TrafficSystem>();
            ts.trafficRelPath = path.Replace("\\", "/").Replace("Assets/StreamingAssets/", "");
            ts.rider = rider; ts.carPaint = carPaint; ts.carCount = cfg.trafficCars;
            ts.spawnMin = cfg.trafficSpawnMin; ts.spawnMax = cfg.trafficSpawnMax; ts.despawn = cfg.trafficDespawn;
            for (int i = 0; i < cfg.trafficCars; i++)
            {
                var car = (GameObject)PrefabUtility.InstantiatePrefab(cars[i % cars.Count], root.transform);
                car.name = "Car " + i;
                foreach (var c in car.GetComponentsInChildren<Collider>()) c.enabled = false;     // keine Physik
                WorldPlacement.CullWhenSmall(car, .012f, true);
                car.SetActive(false);                                                              // das TrafficSystem schaltet sie beim Spawn ein
            }
            Debug.Log($"Verkehr: {cfg.trafficCars} Autos im Pool (Spawn {cfg.trafficSpawnMin:0}–{cfg.trafficSpawnMax:0} m vor dem Rad, weg ab {cfg.trafficDespawn:0} m).");
        }
    }
}
