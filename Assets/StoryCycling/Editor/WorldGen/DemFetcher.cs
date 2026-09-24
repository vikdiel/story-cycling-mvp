using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace StoryCycling.WorldGen.Editor
{
    // Lädt echte Geländehöhen (AWS Open Data "Terrain Tiles", Terrarium-PNG, frei, ohne Key)
    // für die Route + Rand und resampelt sie in den lokalen ENU-Frame der GPX.
    // Einmal ausführen, danach offline: Ergebnis liegt als kleines gzip-Raster im Projekt.
    public static class DemFetcher
    {
        public const string GpxPath = "Assets/StreamingAssets/Routes/Nordhoek.gpx";
        public const string DemPath = "Assets/StoryCycling/WorldGenData/Nordhoek.dem.bytes";
        private const string TileUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        private const string CacheDir = "Library/StoryCyclingDemCache";
        private const int Zoom = 13;          // ~16 m/px bei 34° Süd, entspricht der Quelldaten-Auflösung
        public const float Margin = 5000f;    // Gelände rund um die Route (Berge im Hintergrund!)
        private const float Cell = 15f;
        private const double R = 6371000.0;   // muss zu GpxParser.ProjectToLocalMeters passen

        [MenuItem("Story Cycling/WorldGen/Fetch DEM for Nordhoek")]
        public static void Fetch() => Fetch(GpxPath, DemPath);

        // Für beliebige Strecken (RouteWorldConfig): GPX rein, Datei raus.
        public static void Fetch(string gpxPath, string outPath)
        {
            if (!File.Exists(gpxPath)) throw new InvalidOperationException("GPX missing: " + gpxPath);
            var pts = GpxParser.Parse(File.ReadAllText(gpxPath));
            if (pts.Count < 2) throw new InvalidOperationException("GPX hat < 2 Punkte.");
            var local = GpxParser.ProjectToLocalMeters(pts);

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in local)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            minX -= Margin; minZ -= Margin; maxX += Margin; maxZ += Margin;

            double lat0 = pts[0].Lat * Math.PI / 180.0, lon0 = pts[0].Lon * Math.PI / 180.0;
            Func<float, float, Vector2d> toLatLon = (x, z) => new Vector2d(
                (lat0 + z / R) * 180.0 / Math.PI,
                (lon0 + x / (R * Math.Cos(lat0))) * 180.0 / Math.PI);

            var grid = new DemGrid
            {
                MinX = minX, MinZ = minZ, Cell = Cell,
                Width = Mathf.CeilToInt((maxX - minX) / Cell) + 1,
                Height = Mathf.CeilToInt((maxZ - minZ) / Cell) + 1,
                OriginLat = pts[0].Lat, OriginLon = pts[0].Lon
            };
            grid.Decimetres = new short[grid.Width * grid.Height];

            // Benötigte Kacheln aus den vier Ecken bestimmen.
            Vector2d sw = toLatLon(minX, minZ), ne = toLatLon(maxX, maxZ);
            int tx0 = TileX(sw.y), tx1 = TileX(ne.y), ty0 = TileY(ne.x), ty1 = TileY(sw.x);
            var tiles = new Dictionary<long, float[]>();
            int total = (tx1 - tx0 + 1) * (ty1 - ty0 + 1), done = 0;
            Directory.CreateDirectory(CacheDir);
            try
            {
                for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    EditorUtility.DisplayProgressBar("DEM", $"Höhenkachel {++done}/{total}", done / (float)total);
                    tiles[Key(tx, ty)] = LoadTile(tx, ty);
                }

                for (int iz = 0; iz < grid.Height; iz++)
                {
                    if (iz % 64 == 0) EditorUtility.DisplayProgressBar("DEM", "Resample in lokalen Frame…", iz / (float)grid.Height);
                    for (int ix = 0; ix < grid.Width; ix++)
                    {
                        Vector2d ll = toLatLon(minX + ix * Cell, minZ + iz * Cell);
                        grid.Decimetres[iz * grid.Width + ix] = DemGrid.ToDecimetres(SampleMercator(tiles, ll.x, ll.y));
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            grid.Save(outPath);
            AssetDatabase.ImportAsset(outPath);
            Debug.Log($"DEM gespeichert: {outPath} ({grid.Width}×{grid.Height} @ {Cell} m, {total} Kacheln). " +
                      "Jetzt 'Build Nordhoek GPX Ride'.");
        }

        // ---- Web-Mercator / Terrarium --------------------------------------------------
        private struct Vector2d { public double x, y; public Vector2d(double x, double y) { this.x = x; this.y = y; } }

        private static double N => 1 << Zoom;
        private static int TileX(double lon) => (int)Math.Floor((lon + 180.0) / 360.0 * N);
        private static int TileY(double lat)
        {
            double r = lat * Math.PI / 180.0;
            return (int)Math.Floor((1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * N);
        }
        private static long Key(int tx, int ty) => ((long)tx << 32) | (uint)ty;

        private static float SampleMercator(Dictionary<long, float[]> tiles, double lat, double lon)
        {
            double r = lat * Math.PI / 180.0;
            double px = (lon + 180.0) / 360.0 * N * 256.0 - 0.5;
            double py = (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * N * 256.0 - 0.5;
            int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
            float tx = (float)(px - x0), ty = (float)(py - y0);
            float a = Pixel(tiles, x0, y0), b = Pixel(tiles, x0 + 1, y0);
            float c = Pixel(tiles, x0, y0 + 1), d = Pixel(tiles, x0 + 1, y0 + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        // Globale Pixelkoordinate (y von oben) -> Höhe. Fehlende Kachel = Meer (0 m).
        private static float Pixel(Dictionary<long, float[]> tiles, int gx, int gy)
        {
            int tx = gx >> 8, ty = gy >> 8;
            if (!tiles.TryGetValue(Key(tx, ty), out float[] t) || t == null) return 0f;
            return t[(gy & 255) * 256 + (gx & 255)];
        }

        // Liefert 256×256 Höhen, Zeile 0 = OBEN (wie im PNG).
        private static float[] LoadTile(int tx, int ty)
        {
            string cache = Path.Combine(CacheDir, $"{Zoom}_{tx}_{ty}.png");
            byte[] png;
            if (File.Exists(cache)) png = File.ReadAllBytes(cache);
            else
            {
                string url = string.Format(TileUrl, Zoom, tx, ty);
                using (var req = UnityWebRequest.Get(url))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone) System.Threading.Thread.Sleep(20);
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new InvalidOperationException($"DEM-Kachel fehlgeschlagen ({url}): {req.error}");
                    png = req.downloadHandler.data;
                }
                File.WriteAllBytes(cache, png);
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(png)) throw new InvalidOperationException("PNG nicht lesbar: " + cache);
                if (tex.width != 256 || tex.height != 256) throw new InvalidOperationException("Unerwartete Kachelgröße: " + cache);
                Color32[] px = tex.GetPixels32();                 // Unity: Zeile 0 = UNTEN
                var heights = new float[256 * 256];
                for (int row = 0; row < 256; row++)
                for (int col = 0; col < 256; col++)
                {
                    Color32 c = px[(255 - row) * 256 + col];
                    heights[row * 256 + col] = c.r * 256f + c.g + c.b / 256f - 32768f;
                }
                return heights;
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
