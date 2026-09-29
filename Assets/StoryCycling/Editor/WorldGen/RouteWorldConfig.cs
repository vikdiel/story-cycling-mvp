using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Eine Strecke = ein Config-Asset. Neue Strecke: Rechtsklick > Create > Story Cycling > Route World,
    // GPX eintragen, dann im Menü "Fetch DEM/OSM for Selected Route" und "Build Selected Route World".
    [System.Serializable]
    public struct GeoBox { public string name; public double minLat, maxLat, minLon, maxLon; }

    // Straßenregeln der Strecke — ERGÄNZEN die OSM-Daten, ersetzen sie nicht: Spurzahl, Richtung und Breite kommen aus
    // den OSM-Tags (lanes, lanes:forward/backward, oneway, width, placement); diese Werte gelten nur, wo OSM nichts sagt,
    // plus unsere streckenspezifischen Zutaten (Seitenstreifen, Gehweg, Markierungsstil).
    [System.Serializable]
    public sealed class RoadRules
    {
        [Tooltip("Fahrstreifenbreite (m) für trunk/primary/secondary, wenn OSM keine Breite hat.")]
        public float laneWidthMajor = 3.5f;
        [Tooltip("Fahrstreifenbreite (m) für tertiary.")]
        public float laneWidthMinor = 3.2f;
        [Tooltip("Fahrstreifenbreite (m) für residential/unclassified/living_street/service.")]
        public float laneWidthLocal = 3.0f;
        [Tooltip("Kreisfahrbahn je Spur (m) — breiter als normale Spuren (Schleppkurven).")]
        public float roundaboutLaneWidth = 4.5f;
        [Tooltip("Einbahn-Zu-/Ausfahrten (links, Bypass) je Spur (m).")]
        public float linkLaneWidth = 3.6f;
        [Tooltip("OSM-width-Tag als Fahrbahnbreite verwenden, wenn plausibel.")]
        public bool useWidthTag = true;
        [Tooltip("Asphaltierter Seitenstreifen (m) auf den Straßen aus 'wideShoulderRoads' außerorts, mit gelber Linie.")]
        public float wideShoulder = 2f;
        [Tooltip("Asphaltierter Randstreifen (m) auf Hauptstraßen außerorts (gelbe Randlinie an der Fahrstreifenkante).")]
        public float ruralShoulder = .4f;
        [Tooltip("Rinne (m) zwischen Fahrstreifen und Bordstein innerorts.")]
        public float urbanGutter = .2f;
        [Tooltip("Mittellinie auch auf Wohnstraßen ohne lanes-Tag (in Südafrika meist unmarkiert).")]
        public bool markUntaggedLocalRoads = false;
        [Tooltip("Haltelinien an untergeordneten Einmündungen, Ampeln und Stoppschildern; Wartelinien an Kreisverkehren.")]
        public bool stopLines = true;
    }

    [CreateAssetMenu(fileName = "Route", menuName = "Story Cycling/Route World")]
    public sealed class RouteWorldConfig : ScriptableObject
    {
        [Header("Dateien")]
        public string routeName = "Nordhoek";
        public string gpxPath = "Assets/StreamingAssets/Routes/Nordhoek.gpx";
        public string osmPath = "Assets/StreamingAssets/Osm/Nordhoek.osm.xml";
        public string demPath = "Assets/StoryCycling/WorldGenData/Nordhoek.dem.bytes";
        public string scenePath = "Assets/StoryCycling/Scenes/NordhoekGpxTest.unity";

        [Header("Straße")]
        [Tooltip("Route auf das OSM-Straßennetz legen (empfohlen). Aus = GPX-Linie direkt.")]
        public bool useOsmRoadNetwork = true;
        [Tooltip("Straßennetz-Modell: Route und Querstraßen aus einem Guss, echte Kreuzungsflächen (empfohlen).")]
        public bool useRoadNetwork = true;
        [Tooltip("Optional: Kreuzungsflächen von osm2streets (Tools/osm2streets, braucht Node.js). Standard aus: das eigene " +
                 "Spurmodell ist gleichwertig, osm2streets erkennt bei Kapstadt den Linksverkehr nicht.")]
        public bool useOsm2StreetsJunctions = false;

        public string Osm2StreetsPath => Osm2StreetsGeometry.OutputPathFor(osmPath);
        [Tooltip("Regex auf OSM name/ref: diese Straßen bekommen breite Seitenstreifen mit gelber Linie an der Fahrstreifenkante.")]
        public string wideShoulderRoads = "Victoria Road|^M6$";
        [Tooltip("Breiter Stil nur innerhalb dieser Gebiete (Breite/Länge). Leer = überall, wo der Name passt.")]
        public GeoBox[] wideShoulderZones = { new GeoBox { name = "Sea Point – Camps Bay", minLat = -33.953, maxLat = -33.895, minLon = 18.360, maxLon = 18.420 } };
        [Tooltip("Spurbreiten, Seitenstreifen und Markierungen (ergänzen die OSM-Tags).")]
        public RoadRules roadRules = new RoadRules();
        [Tooltip("Steinschlaggalerien (OSM tunnel=avalanche_protector, z. B. Chapman's Peak) als Bauwerk über der Straße.")]
        public bool buildGalleries = true;
        [Tooltip("Normale Straßen: gelbe Randlinie direkt am Fahrbahnrand (Südafrika-Standard).")]
        public bool edgeLinesOnNormalRoads = true;

        [Header("Gebäude")]
        [Range(0, 100)] public int syntyModularShare = 70;     // erste Reihe: Baukasten vs. verputzte Villen
        [Range(0, 100)] public int glassTowerShare = 60;        // große/hohe OSM-Gebäude als Synty-Glasbauten
        public float frontRowDistance = 55f;
        public bool backgroundFill = true;

        [Header("Vegetation")]
        [Tooltip("Instanzierte Hangvegetation bis zu dieser Entfernung von der Straße (Berge nicht kahl).")]
        public float slopeVegetationDistance = 700f;

        // Liegt ein lokaler Punkt (m, Ursprung = erster GPX-Punkt) in einer der wideShoulderZones? Keine Zonen = überall.
        public System.Func<Vector2, bool> WideShoulderZone(double lat0, double lon0)
        {
            if (wideShoulderZones == null || wideShoulderZones.Length == 0) return p => true;
            const double R = 6371000.0;
            double c = System.Math.Cos(lat0 * System.Math.PI / 180.0);
            var zones = wideShoulderZones;
            return p =>
            {
                double lat = lat0 + p.y / R * 180.0 / System.Math.PI, lon = lon0 + p.x / (R * c) * 180.0 / System.Math.PI;
                foreach (var z in zones) if (lat >= z.minLat && lat <= z.maxLat && lon >= z.minLon && lon <= z.maxLon) return true;
                return false;
            };
        }

        public string BakedRoutePath => Path.ChangeExtension(gpxPath, ".route.txt");

        public const string DefaultPath = "Assets/StoryCycling/WorldGenData/Nordhoek.route.asset";

        public static RouteWorldConfig LoadOrCreateDefault()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<RouteWorldConfig>(DefaultPath);
            if (cfg != null) return cfg;
            cfg = CreateInstance<RouteWorldConfig>();
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath));
            AssetDatabase.CreateAsset(cfg, DefaultPath);
            AssetDatabase.SaveAssets();
            return cfg;
        }

        public static RouteWorldConfig Selected()
        {
            var cfg = Selection.activeObject as RouteWorldConfig;
            if (cfg == null) Debug.LogWarning("Keine Route-Config im Project-Fenster ausgewählt — nehme Nordhoek.");
            return cfg != null ? cfg : LoadOrCreateDefault();
        }

        [MenuItem("Story Cycling/WorldGen/Fetch DEM for Selected Route")]
        private static void FetchDem() { var c = Selected(); DemFetcher.Fetch(c.gpxPath, c.demPath); }

        [MenuItem("Story Cycling/WorldGen/Fetch OSM for Selected Route")]
        private static void FetchOsm() { var c = Selected(); OsmFetcher.Fetch(c.gpxPath, c.osmPath); }

        [MenuItem("Story Cycling/WorldGen/Build OSM2Streets Geometry for Selected Route")]
        private static void FetchOsm2Streets()
        {
            var c = Selected();
            if (Osm2StreetsGeometry.TryRun(c.gpxPath, c.osmPath, c.Osm2StreetsPath, out string msg)) Debug.Log(msg);
            else Debug.LogWarning(msg);
        }

        [MenuItem("Story Cycling/WorldGen/Build Selected Route World")]
        private static void Build() => GpxSceneBuilder.Build(Selected());
    }
}
