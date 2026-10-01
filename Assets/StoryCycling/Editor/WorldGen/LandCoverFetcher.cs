using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace StoryCycling.WorldGen.Editor
{
    // Lädt die ESA-WorldCover-Karte (10 m, 2021, v200, CC BY 4.0) für die Route + Rand über den öffentlichen Terrascope-WMS
    // und legt sie als kleines gzip-Klassenraster im lokalen ENU-Frame der GPX ab (wie DemFetcher für die Höhen).
    // Einmal ausführen, danach offline. Fehlt die Datei, baut der Weltbauer mit OSM + Gelände-Heuristik weiter (kein Abbruch).
    //
    // WMS: Endpunkt und Ebenenname wechseln bei Terrascope gelegentlich (titiler.terrascope.be/wms, Ebene
    // "esa-worldcover-map-10m-2021-v2_map"; älter services.terrascope.be/wms/v2, Ebene "WORLDCOVER_2021_MAP"). Darum werden
    // die Endpunkte der Reihe nach probiert, der Ebenenname nötigenfalls aus GetCapabilities gelesen und die Achsenreihenfolge
    // (WMS 1.3.0 + EPSG:4326 = Breite,Länge; 1.1.1 und CRS:84 = Länge,Breite) per Probekachel geprüft statt geraten.
    // Die Kacheln liegen exakt auf dem nativen WorldCover-Raster (1/12000 Grad), dadurch entspricht jedes Bildpixel einem Quellpixel.
    public static class LandCoverFetcher
    {
        public const string Attribution = "ESA WorldCover 10 m 2021 v200, © ESA WorldCover project / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium, CC BY 4.0";

        public static string[] Endpoints =
        {
            "https://titiler.terrascope.be/wms",
            "https://services.terrascope.be/wms/v2",
            "https://mapproxy.terrascope.be/mapproxy/service",
        };
        public static string[] LayerCandidates = { "esa-worldcover-map-10m-2021-v2_map", "WORLDCOVER_2021_MAP" };

        public const float Cell = 10f;                 // Zielraster (m)
        public const double NativeRes = 1.0 / 12000.0;  // Grad je Pixel im WorldCover-Raster
        public static int TileSize = 1024;              // Pixel je WMS-Anfrage und Seite
        public const float Margin = DemFetcher.Margin;
        private const string CacheDir = "Library/StoryCyclingLandCoverCache";

        // Austauschbar für Tests (kein Netz nötig): HTTP-GET und PNG-Decoder.
        public sealed class Image { public int Width, Height; public byte[] Rgba; }   // Zeile 0 = OBEN
        public static Func<string, byte[]> HttpGet = DefaultHttpGet;
        public static Func<byte[], Image> DecodePng = DefaultDecodePng;
        public static int TimeoutSeconds = 120;        // je Anfrage; die Dienstprüfung nutzt kürzere Zeiten (toter Server soll nicht Stunden kosten)

        // Der Server hat geantwortet, aber die Anfrage abgelehnt (OGC ServiceException) -> nächste Variante/Ebene probieren.
        // Alles andere (Verbindung, Zeitüberschreitung) ist ein Transportfehler -> Endpunkt überspringen.
        public sealed class WmsServiceException : InvalidOperationException { public WmsServiceException(string m) : base(m) { } }
        public static string CachePath = CacheDir;     // null = kein Cache
        public static Action<string, float> Progress = (what, t) => EditorUtility.DisplayProgressBar("Landbedeckung", what, t);

        public enum WmsVariant { V130LatLon, V111LonLat, V130Crs84 }

        public static void Fetch(string gpxPath, string outPath)
        {
            if (!File.Exists(gpxPath)) throw new InvalidOperationException("GPX missing: " + gpxPath);
            var pts = GpxParser.Parse(File.ReadAllText(gpxPath));
            if (pts.Count < 2) throw new InvalidOperationException("GPX hat < 2 Punkte.");
            try
            {
                var grid = Build(pts);
                grid.Save(outPath);
                AssetDatabase.ImportAsset(outPath);
                var sh = grid.Shares(out float known);
                var sb = new StringBuilder();
                foreach (var e in LandCoverClass.Legend) if (sh[e.cls] >= .005f) sb.Append($"{e.name} {sh[e.cls]:P0}, ");
                Debug.Log($"Landbedeckung gespeichert: {outPath} ({grid.Width}×{grid.Height} @ {Cell} m, bekannt {known:P0}): {sb}" +
                          $"Quelle: {Attribution}. Jetzt 'Build Selected Route World'.");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // Kern (ohne Dateien): lokales Raster aus den GPX-Punkten. Wirft bei unbrauchbarer Antwort.
        public static LandCoverGrid Build(List<GeoPoint> pts)
        {
            var local = GpxParser.ProjectToLocalMeters(pts);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in local) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
            minX -= Margin; minZ -= Margin; maxX += Margin; maxZ += Margin;
            int gw = Mathf.CeilToInt((maxX - minX) / Cell) + 1, gh = Mathf.CeilToInt((maxZ - minZ) / Cell) + 1;

            double lat0 = pts[0].Lat * Math.PI / 180.0;
            LandCoverResample.LatLonOf(pts[0].Lat, pts[0].Lon, minX, minZ, out double latMin, out double lonMin);
            LandCoverResample.LatLonOf(pts[0].Lat, pts[0].Lon, maxX, maxZ, out double latMax, out double lonMax);

            // auf das native Raster ausrichten (+1 Pixel Rand)
            double west = Math.Floor(lonMin / NativeRes) * NativeRes - NativeRes, south = Math.Floor(latMin / NativeRes) * NativeRes - NativeRes;
            int rw = (int)Math.Ceiling((lonMax - west) / NativeRes) + 2, rh = (int)Math.Ceiling((latMax - south) / NativeRes) + 2;
            double north = south + rh * NativeRes;
            var raster = new LatLonClassRaster(rw, rh, west, north, NativeRes, NativeRes);

            // Server/Variante mit einer kleinen Probekachel auf Land (Route) bestimmen
            Progress("Dienst prüfen", .02f);
            var svc = Probe(ProbePoints(pts));
            Debug.Log($"Landbedeckung: WMS {svc.Endpoint}, Ebene '{svc.Layer}', {svc.Variant}; Raster {rw}×{rh} px ({rw * NativeRes * 111320 * Math.Cos(lat0) / 1000:0.0}×{rh * NativeRes * 111320 / 1000:0.0} km).");

            int tw = (rw + TileSize - 1) / TileSize, th = (rh + TileSize - 1) / TileSize, done = 0, total = tw * th;
            for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                Progress($"WMS-Kachel {++done}/{total}", .05f + .85f * done / total);
                int px0 = tx * TileSize, py0 = ty * TileSize;
                int w = Math.Min(TileSize, rw - px0), h = Math.Min(TileSize, rh - py0);
                double tWest = west + px0 * NativeRes, tNorth = north - py0 * NativeRes;
                string url = MapUrl(svc, tWest, tNorth - h * NativeRes, tWest + w * NativeRes, tNorth, w, h);
                var img = Load(url);
                if (img.Width != w || img.Height != h)
                    throw new InvalidOperationException($"WMS lieferte {img.Width}×{img.Height} statt {w}×{h} ({url}).");
                LandCoverResample.DecodeRgba(img.Rgba, w, h, raster, px0, py0);
            }
            Progress("Resample in lokalen Frame…", .92f);
            // Antialiasing/Interpolation am Klassenrand liefert Mischfarben (= unbekannt): vom Rand her mit der Nachbarklasse füllen.
            int filled = LandCoverResample.FillUnknown(raster, 2);
            var grid = LandCoverResample.ToLocalGrid(raster, pts[0].Lat, pts[0].Lon, minX, minZ, gw, gh, Cell);
            grid.Shares(out float knownShare);
            Debug.Log($"Landbedeckung: {knownShare:P0} des lokalen Rasters belegt ({filled} Randpixel aus Nachbarklassen ergänzt; offenes Meer bleibt leer).");
            // Offenes Meer ist oft "keine Daten": Küstenrouten haben legitim viel Unbekanntes. Darunter stimmt etwas nicht (Stil/Legende).
            if (knownShare < .12f)
                throw new InvalidOperationException($"Nur {knownShare:P0} der Landbedeckung erkannt (WMS-Stil/Legende geändert?) — nichts gespeichert.");
            return grid;
        }

        // ---------------------------------------------------------------- WMS
        public struct Service { public string Endpoint, Layer; public WmsVariant Variant; }

        public static string MapUrl(Service s, double west, double south, double east, double north, int w, int h)
        {
            var ci = CultureInfo.InvariantCulture;
            Func<double, string> f = v => v.ToString("0.########", ci);
            string common = $"SERVICE=WMS&REQUEST=GetMap&LAYERS={Uri.EscapeDataString(s.Layer)}&STYLES=&WIDTH={w}&HEIGHT={h}&FORMAT=image%2Fpng&TRANSPARENT=TRUE";
            string sep = s.Endpoint.Contains("?") ? "&" : "?";
            switch (s.Variant)
            {
                case WmsVariant.V111LonLat:
                    return $"{s.Endpoint}{sep}VERSION=1.1.1&{common}&SRS=EPSG:4326&BBOX={f(west)},{f(south)},{f(east)},{f(north)}";
                case WmsVariant.V130Crs84:
                    return $"{s.Endpoint}{sep}VERSION=1.3.0&{common}&CRS=CRS:84&BBOX={f(west)},{f(south)},{f(east)},{f(north)}";
                default:   // WMS 1.3.0 + EPSG:4326: Achsenreihenfolge Breite,Länge
                    return $"{s.Endpoint}{sep}VERSION=1.3.0&{common}&CRS=EPSG:4326&BBOX={f(south)},{f(west)},{f(north)},{f(east)}";
            }
        }

        // Routenpunkte für die Dienstprüfung: bevorzugt Land (Höhe >= 15 m), über die Strecke verteilt.
        public static List<GeoPoint> ProbePoints(List<GeoPoint> pts)
        {
            var result = new List<GeoPoint>();
            int n = pts.Count, minGap = Math.Max(1, n / 12);
            var order = new List<int>();
            for (int i = 0; i < n; i++) order.Add(i);
            order.Sort((a, b) => Math.Abs(a - n / 2).CompareTo(Math.Abs(b - n / 2)));
            var taken = new List<int>();
            foreach (int pass in new[] { 0, 1 })
                foreach (int i in order)
                {
                    if (result.Count >= 4) break;
                    if (pass == 0 && pts[i].Ele < 15.0) continue;
                    bool near = false;
                    foreach (int t in taken) if (Math.Abs(t - i) < minGap) { near = true; break; }
                    if (near) continue;
                    taken.Add(i); result.Add(pts[i]);
                }
            return result;
        }

        private static Service Probe(List<GeoPoint> probePoints)
        {
            var errors = new StringBuilder();
            const int n = 64;
            int oldTimeout = TimeoutSeconds;
            TimeoutSeconds = 25;
            try
            {
                foreach (string endpoint in Endpoints)
                {
                    var layers = new List<string>(LayerCandidates);
                    bool transportFailed = false;
                    try { foreach (string l in LayersFromCapabilities(endpoint)) if (!layers.Contains(l)) layers.Insert(0, l); }
                    catch (WmsServiceException e) { errors.Append($"\n  {endpoint}: GetCapabilities — {e.Message}"); }
                    catch (Exception e) { errors.Append($"\n  {endpoint}: GetCapabilities — {e.Message}"); transportFailed = true; }
                    foreach (string layer in layers)
                    foreach (WmsVariant variant in new[] { WmsVariant.V130LatLon, WmsVariant.V111LonLat, WmsVariant.V130Crs84 })
                    {
                        if (transportFailed) break;
                        var s = new Service { Endpoint = endpoint, Layer = layer, Variant = variant };
                        foreach (var pt in probePoints)
                        {
                            double west = Math.Floor(pt.Lon / NativeRes) * NativeRes - n / 2 * NativeRes, south = Math.Floor(pt.Lat / NativeRes) * NativeRes - n / 2 * NativeRes;
                            string url = MapUrl(s, west, south, west + n * NativeRes, south + n * NativeRes, n, n);
                            try
                            {
                                var img = Load(url, cache: false, attempts: 1);
                                if (img.Width != n || img.Height != n) { errors.Append($"\n  {endpoint} {layer} {variant}: Bild {img.Width}×{img.Height} statt {n}×{n}"); break; }
                                int known = 0;
                                for (int i = 0; i < n * n; i++)
                                    if (LandCoverResample.ClassOfPixel(img.Rgba, i * 4) != LandCoverClass.Unknown) known++;
                                if (known > n * n * .4) return s;
                                errors.Append($"\n  {endpoint} {layer} {variant}: nur {known * 100 / (n * n)} % bekannte Pixel bei {pt.Lat:0.000},{pt.Lon:0.000}");
                            }
                            catch (WmsServiceException e) { errors.Append($"\n  {endpoint} {layer} {variant}: {e.Message}"); break; }
                            catch (Exception e) { errors.Append($"\n  {endpoint} {layer} {variant}: {e.Message}"); transportFailed = true; break; }
                        }
                    }
                }
            }
            finally { TimeoutSeconds = oldTimeout; }
            throw new InvalidOperationException("Kein WorldCover-WMS erreichbar/brauchbar:" + errors +
                "\nTipp: Endpunkt/Ebene in LandCoverFetcher.Endpoints/LayerCandidates prüfen (GetCapabilities im Browser öffnen). " +
                "Ohne Landbedeckung baut der Weltbauer trotzdem (OSM + Gelände), nur weniger genau.");
        }

        // Ebenennamen der WorldCover-2021-Karte aus GetCapabilities (Name enthält worldcover + 2021 + map).
        public static List<string> LayersFromCapabilities(string endpoint)
        {
            string sep = endpoint.Contains("?") ? "&" : "?";
            var bytes = HttpGet($"{endpoint}{sep}SERVICE=WMS&REQUEST=GetCapabilities");
            string xml = Encoding.UTF8.GetString(bytes);
            var result = new List<string>();
            foreach (Match m in Regex.Matches(xml, @"<(?:\w+:)?Name>\s*([^<\s]+)\s*</(?:\w+:)?Name>"))
            {
                string n = m.Groups[1].Value, low = n.ToLowerInvariant();
                if (low.Contains("worldcover") && low.Contains("2021") && low.Contains("map") && !result.Contains(n)) result.Add(n);
            }
            return result;
        }

        private static Image Load(string url, bool cache = true, int attempts = 3)
        {
            byte[] png = null; string file = null;
            if (cache && CachePath != null)
            {
                file = Path.Combine(CachePath, Hash(url) + ".png");
                if (File.Exists(file)) png = File.ReadAllBytes(file);
            }
            if (png == null)
            {
                Exception last = null;
                for (int attempt = 0; attempt < attempts && png == null; attempt++)
                    try { png = HttpGet(url); } catch (Exception e) { last = e; }
                if (png == null) throw new InvalidOperationException("WMS-Anfrage fehlgeschlagen: " + (last != null ? last.Message : url));
                // ServiceException-XML (oder JSON) statt Bild: lesbare Meldung
                if (png.Length > 4 && (png[0] == '<' || png[0] == '{'))
                    throw new WmsServiceException("WMS-Fehler: " + Regex.Replace(Encoding.UTF8.GetString(png, 0, Math.Min(png.Length, 300)), @"\s+", " "));
                if (file != null) { Directory.CreateDirectory(CachePath); File.WriteAllBytes(file, png); }
            }
            var img = DecodePng(png);
            if (img == null) throw new InvalidOperationException("PNG nicht lesbar: " + url);
            return img;
        }

        private static string Hash(string s)
        {
            ulong h = 1469598103934665603UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        private static byte[] DefaultHttpGet(string url)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = TimeoutSeconds;
                var op = req.SendWebRequest();
                while (!op.isDone) System.Threading.Thread.Sleep(20);
                if (req.result == UnityWebRequest.Result.ProtocolError)
                {
                    // Der Server antwortet (z. B. 400 mit ServiceException/JSON): Anfrageform falsch, nicht Server tot.
                    string body = req.downloadHandler != null && req.downloadHandler.data != null
                        ? Regex.Replace(Encoding.UTF8.GetString(req.downloadHandler.data, 0, Math.Min(req.downloadHandler.data.Length, 200)), @"\s+", " ") : "";
                    throw new WmsServiceException($"HTTP {req.responseCode} {req.error} {body}");
                }
                if (req.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException($"{req.error} ({url})");
                return req.downloadHandler.data;
            }
        }

        private static Image DefaultDecodePng(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(png)) return null;
                Color32[] px = tex.GetPixels32();               // Unity: Zeile 0 = UNTEN
                int w = tex.width, h = tex.height;
                var rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[(h - 1 - y) * w + x]; int o = (y * w + x) * 4;
                    rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = c.a;
                }
                return new Image { Width = w, Height = h, Rgba = rgba };
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
