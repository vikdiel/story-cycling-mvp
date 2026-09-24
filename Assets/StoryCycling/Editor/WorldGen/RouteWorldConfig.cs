using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Eine Strecke = ein Config-Asset. Neue Strecke: Rechtsklick > Create > Story Cycling > Route World,
    // GPX eintragen, dann im Menü "Fetch DEM/OSM for Selected Route" und "Build Selected Route World".
    [System.Serializable]
    public struct GeoBox { public string name; public double minLat, maxLat, minLon, maxLon; }

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
        [Tooltip("Regex auf OSM name/ref: diese Straßen bekommen breite Seitenstreifen mit gelber Linie an der Fahrstreifenkante.")]
        public string wideShoulderRoads = "Victoria Road|^M6$";
        [Tooltip("Breiter Stil nur innerhalb dieser Gebiete (Breite/Länge). Leer = überall, wo der Name passt.")]
        public GeoBox[] wideShoulderZones = { new GeoBox { name = "Sea Point – Camps Bay", minLat = -33.953, maxLat = -33.895, minLon = 18.360, maxLon = 18.420 } };
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

        [MenuItem("Story Cycling/WorldGen/Build Selected Route World")]
        private static void Build() => GpxSceneBuilder.Build(Selected());
    }
}
