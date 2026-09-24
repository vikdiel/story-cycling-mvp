using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace StoryCycling.WorldGen.Editor
{
    // Fetches OSM (buildings, landuse/natural, parking, barriers, and point features:
    // signs, lamps, benches, bins, hydrants, trees, rocks) in a corridor around the route.
    public static class OsmFetcher
    {
        private const string GpxPath = "Assets/StreamingAssets/Routes/Nordhoek.gpx";
        private const string OutPath = "Assets/StreamingAssets/Osm/Nordhoek.osm.xml";
        private const string Endpoint = "https://overpass-api.de/api/interpreter";
        private const int MaxCoords = 300;
        private const int StreetRadius = 160;   // Querstraßen/Kreuzungen/Kreisverkehre rund um die Route

        [MenuItem("Story Cycling/WorldGen/Fetch OSM for Nordhoek")]
        public static void Fetch() => Fetch(GpxPath, OutPath);

        // Für beliebige Strecken (RouteWorldConfig): GPX rein, Datei raus.
        public static void Fetch(string gpxPath, string outPath)
        {
            if (!File.Exists(gpxPath)) throw new System.Exception("GPX missing: " + gpxPath);
            var pts = GpxParser.Parse(File.ReadAllText(gpxPath));
            if (pts.Count < 2) throw new System.Exception("GPX has <2 points.");

            string c = BuildCoordList(pts, MaxCoords);
            string query =
                "[out:xml][timeout:300];\n(\n" +
                $"  way[\"building\"](around:200,{c});\n" +
                $"  way[\"landuse\"](around:400,{c});\n" +
                $"  way[\"natural\"](around:400,{c});\n" +
                $"  way[\"amenity\"=\"parking\"](around:60,{c});\n" +
                $"  way[\"barrier\"~\"fence|hedge|wall\"](around:60,{c});\n" +
                $"  way[\"highway\"~\"^(trunk|primary|secondary|tertiary|residential|unclassified|living_street|trunk_link|primary_link|secondary_link|tertiary_link)$\"](around:{StreetRadius},{c});\n" +
                $"  node[\"highway\"~\"traffic_signals|street_lamp|bus_stop|stop|give_way\"](around:40,{c});\n" +
                $"  node[\"amenity\"~\"bench|waste_basket|post_box|parking_meter\"](around:40,{c});\n" +
                $"  node[\"emergency\"=\"fire_hydrant\"](around:40,{c});\n" +
                $"  node[\"natural\"~\"tree|stone|rock\"](around:120,{c});\n" +
                ");\nout geom;";

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            EditorUtility.DisplayProgressBar("OSM", "Overpass-Anfrage läuft…", 0.3f);
            try
            {
                var form = new WWWForm(); form.AddField("data", query);
                using var req = UnityWebRequest.Post(Endpoint, form);
                var op = req.SendWebRequest();
                while (!op.isDone) System.Threading.Thread.Sleep(50);
                if (req.result != UnityWebRequest.Result.Success) throw new System.Exception("Overpass failed: " + req.error);
                File.WriteAllText(outPath, req.downloadHandler.text, new UTF8Encoding(false));
            }
            finally { EditorUtility.ClearProgressBar(); }

            AssetDatabase.Refresh();
            string xml = File.ReadAllText(outPath);
            Debug.Log($"OSM gespeichert: {outPath} ({xml.Length / 1024} kB, " +
                      $"ways ~{Count(xml, "<way ")}, nodes ~{Count(xml, "<node ")}). Jetzt 'Build Nordhoek GPX Ride'.");
        }

        private static string BuildCoordList(List<GeoPoint> pts, int maxCoords)
        {
            int stride = Mathf.Max(1, pts.Count / maxCoords);
            var sb = new StringBuilder();
            for (int i = 0; i < pts.Count; i += stride)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(pts[i].Lat.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                  .Append(pts[i].Lon.ToString("F6", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
        private static int Count(string s, string sub)
        { int c = 0, i = 0; while ((i = s.IndexOf(sub, i, System.StringComparison.Ordinal)) >= 0) { c++; i += sub.Length; } return c; }
    }
}
