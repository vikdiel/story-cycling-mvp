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

    // Tageszeit-Stimmung der Strecke. Alle Werte je Stimmung stehen in RouteWorldConfig.lighting (eine Tabelle, im Inspector
    // einstellbar); GpxSceneBuilder.Lighting/ColorGrade lesen nur daraus.
    public enum LightingPreset { Morning, Afternoon, EveningSun }

    [System.Serializable]
    public sealed class LightingSettings
    {
        public string name = "Nachmittag";
        [Header("Sonne")]
        [Tooltip("Himmelsrichtung, aus der die Sonne scheint (0 = N, 90 = O, 180 = S, 270 = W). 300 = Nordwest über dem Atlantik.")]
        [Range(0f, 360f)] public float sunAzimuth = 300f;
        [Tooltip("Höhe über dem Horizont (Grad).")]
        [Range(2f, 70f)] public float sunElevation = 24f;
        [Tooltip("Lichtfarbe als Farbtemperatur (Kelvin): 3500 = tiefes Abendgold, 4800 = später Nachmittag, 5600 = Tageslicht.")]
        [Range(2000f, 9000f)] public float sunTemperature = 4800f;
        [Tooltip("Zusätzlicher Farbfilter auf die Temperaturfarbe (Weiß = keiner).")]
        public Color sunFilter = Color.white;
        [Tooltip("Sonnenintensität (linear). Bei flacher Sonne fällt weniger Licht auf den Boden: niedrige Sonne braucht höhere Werte.")]
        [Range(0f, 4f)] public float sunIntensity = 1.55f;
        [Range(0f, 1f)] public float shadowStrength = .85f;

        [Header("Himmel & Dunst")]
        public Color skyZenith = new Color(.22f, .47f, .78f);
        public Color skyHorizon = new Color(.93f, .83f, .68f);
        [Tooltip("Farbe des Sonnenhofs im Himmel.")]
        public Color sunGlow = new Color(1f, .62f, .30f);
        [Range(0f, 1f)] public float sunGlowStrength = .22f;
        [Tooltip("Nebel = warmer Dunst; Ferne verschmilzt mit dem Horizont.")]
        public Color fogColor = new Color(.90f, .80f, .66f);
        public float fogStart = 300f, fogEnd = 6000f;

        [Header("Umgebungslicht (Trilight)")]
        public Color ambientSky = new Color(.50f, .62f, .80f);
        public Color ambientEquator = new Color(.72f, .66f, .58f);
        public Color ambientGround = new Color(.38f, .33f, .27f);

        [Header("Color Grading (ACES)")]
        [Tooltip("Belichtung in EV (negativ = dunkler).")]
        [Range(-2f, 2f)] public float postExposure = -.15f;
        [Tooltip("Weißabgleich Temperatur: positiv = wärmer.")]
        [Range(-100f, 100f)] public float whiteBalanceTemperature = 12f;
        [Range(-100f, 100f)] public float whiteBalanceTint = 0f;
        [Range(-100f, 100f)] public float contrast = 8f;
        [Range(-100f, 100f)] public float saturation = 10f;
        [Tooltip("Split-Toning: Schatten kühl, Lichter warm (50 % Grau = neutral).")]
        public Color shadowTint = new Color(.46f, .53f, .68f);
        public Color highlightTint = new Color(.62f, .53f, .42f);
        [Range(-100f, 100f)] public float splitBalance = 0f;
        [Header("Bloom / Vignette")]
        [Range(0f, 1f)] public float bloomIntensity = .18f;
        [Range(0f, 2f)] public float bloomThreshold = .9f;
        [Range(0f, 1f)] public float bloomScatter = .6f;
        public Color bloomTint = new Color(1f, .93f, .82f);
        [Range(0f, 1f)] public float vignetteIntensity = .2f;
        [Range(0f, 1f)] public float vignetteSmoothness = .4f;

        // Sonnenrichtung (Einheitsvektor ZUR Sonne) in Weltkoordinaten: +x Ost, +z Nord.
        public Vector3 ToSun()
        {
            float az = sunAzimuth * Mathf.Deg2Rad, el = sunElevation * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;
        }
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

        [Header("Licht & Stimmung")]
        [Tooltip("Tageszeit-Stimmung. Die Werte je Stimmung stehen in der Tabelle darunter (Reihenfolge Morning, Afternoon, EveningSun).")]
        public LightingPreset lightingPreset = LightingPreset.Afternoon;
        public LightingSettings[] lighting = DefaultLightingTable();

        [Header("Verkehr")]
        [Tooltip("Einfachen Hintergrundverkehr bauen (Spurgraph wird neben der Route gespeichert, Autos fahren zur Laufzeit).")]
        public bool buildTraffic = true;
        [Tooltip("Anzahl gleichzeitig aktiver Autos (Pool).")]
        [Range(0, 64)] public int trafficCars = 24;
        [Tooltip("Autos erscheinen nur in diesem Abstand vor dem Radfahrer (m) - nie im Sichtfeld.")]
        public float trafficSpawnMin = 120f, trafficSpawnMax = 450f;
        [Tooltip("Autos hinter dem Radfahrer werden jenseits dieser Entfernung (m) wieder entfernt.")]
        public float trafficDespawn = 500f;
        [Tooltip("Wunschgeschwindigkeit ohne OSM-maxspeed (km/h).")]
        public float urbanSpeedKmh = 50f, ruralSpeedKmh = 70f;

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

        public LightingSettings ActiveLighting
        {
            get
            {
                var table = lighting != null && lighting.Length > (int)lightingPreset ? lighting : DefaultLightingTable();
                return table[(int)lightingPreset];
            }
        }

        // Werkseinstellungen der drei Stimmungen (eine Tabelle). Nachmittag = Standard: Sonne tief aus Nordwest, warmes Licht,
        // kühle Schatten. Bei flacher Sonne fällt weniger Licht auf den Boden, deshalb steigt die Intensität mit sinkender Sonne.
        public static LightingSettings[] DefaultLightingTable() => new[]
        {
            new LightingSettings
            {
                name = "Morgen", sunAzimuth = 105f, sunElevation = 19f, sunTemperature = 5300f, sunIntensity = 1.45f, shadowStrength = .8f,
                skyZenith = new Color(.26f, .50f, .80f), skyHorizon = new Color(.88f, .83f, .78f),
                sunGlow = new Color(1f, .72f, .45f), sunGlowStrength = .16f,
                fogColor = new Color(.83f, .80f, .78f), fogStart = 260f, fogEnd = 5500f,
                ambientSky = new Color(.50f, .63f, .82f), ambientEquator = new Color(.68f, .66f, .62f), ambientGround = new Color(.36f, .34f, .30f),
                postExposure = -.1f, whiteBalanceTemperature = 6f, whiteBalanceTint = 0f, contrast = 6f, saturation = 8f,
                shadowTint = new Color(.46f, .54f, .70f), highlightTint = new Color(.58f, .52f, .44f), splitBalance = 0f,
                bloomIntensity = .16f, bloomThreshold = .9f, bloomScatter = .6f, bloomTint = new Color(1f, .95f, .88f),
                vignetteIntensity = .18f, vignetteSmoothness = .4f
            },
            new LightingSettings
            {
                name = "Nachmittag", sunAzimuth = 300f, sunElevation = 24f, sunTemperature = 4800f, sunIntensity = 1.55f, shadowStrength = .85f,
                skyZenith = new Color(.22f, .47f, .78f), skyHorizon = new Color(.93f, .83f, .68f),
                sunGlow = new Color(1f, .62f, .30f), sunGlowStrength = .22f,
                fogColor = new Color(.90f, .80f, .66f), fogStart = 300f, fogEnd = 6000f,
                ambientSky = new Color(.50f, .62f, .80f), ambientEquator = new Color(.72f, .66f, .58f), ambientGround = new Color(.38f, .33f, .27f),
                postExposure = -.15f, whiteBalanceTemperature = 12f, whiteBalanceTint = 0f, contrast = 8f, saturation = 10f,
                shadowTint = new Color(.46f, .53f, .68f), highlightTint = new Color(.62f, .53f, .42f), splitBalance = 0f,
                bloomIntensity = .18f, bloomThreshold = .9f, bloomScatter = .6f, bloomTint = new Color(1f, .93f, .82f),
                vignetteIntensity = .2f, vignetteSmoothness = .4f
            },
            new LightingSettings
            {
                name = "Abendsonne", sunAzimuth = 286f, sunElevation = 9f, sunTemperature = 3700f, sunIntensity = 2.1f, shadowStrength = .9f,
                skyZenith = new Color(.20f, .38f, .68f), skyHorizon = new Color(.96f, .72f, .48f),
                sunGlow = new Color(1f, .50f, .18f), sunGlowStrength = .34f,
                fogColor = new Color(.92f, .68f, .48f), fogStart = 240f, fogEnd = 5200f,
                ambientSky = new Color(.42f, .52f, .72f), ambientEquator = new Color(.70f, .55f, .44f), ambientGround = new Color(.34f, .27f, .22f),
                postExposure = -.2f, whiteBalanceTemperature = 14f, whiteBalanceTint = 3f, contrast = 10f, saturation = 12f,
                shadowTint = new Color(.42f, .48f, .66f), highlightTint = new Color(.66f, .52f, .38f), splitBalance = -8f,
                bloomIntensity = .24f, bloomThreshold = .85f, bloomScatter = .65f, bloomTint = new Color(1f, .88f, .72f),
                vignetteIntensity = .24f, vignetteSmoothness = .45f
            }
        };

        public string BakedRoutePath => Path.ChangeExtension(gpxPath, ".route.txt");
        public string BakedTrafficPath => Path.ChangeExtension(gpxPath, ".traffic.txt");

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
