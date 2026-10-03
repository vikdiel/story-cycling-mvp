using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // ESA WorldCover (10 m, 2021, v200, CC BY 4.0): Landbedeckungsklassen und ihre Legendenfarben.
    // Quelle der Farben: WorldCover Product User Manual v2.0 (Tab. Legende). Die WMS-Karte liefert genau diese Farben.
    public static class LandCoverClass
    {
        public const byte Unknown = 0, Tree = 10, Shrub = 20, Grass = 30, Crop = 40, Built = 50, Bare = 60,
                          Snow = 70, Water = 80, Wetland = 90, Mangrove = 95, Moss = 100;

        public struct Entry { public byte cls, r, g, b; public string name; }

        public static readonly Entry[] Legend =
        {
            new Entry { cls = Tree,     r = 0x00, g = 0x64, b = 0x00, name = "Baumbestand" },
            new Entry { cls = Shrub,    r = 0xFF, g = 0xBB, b = 0x22, name = "Busch/Fynbos" },
            new Entry { cls = Grass,    r = 0xFF, g = 0xFF, b = 0x4C, name = "Grasland" },
            new Entry { cls = Crop,     r = 0xF0, g = 0x96, b = 0xFF, name = "Acker" },
            new Entry { cls = Built,    r = 0xFA, g = 0x00, b = 0x00, name = "Bebaut" },
            new Entry { cls = Bare,     r = 0xB4, g = 0xB4, b = 0xB4, name = "Fels/Sand/karg" },
            new Entry { cls = Snow,     r = 0xF0, g = 0xF0, b = 0xF0, name = "Schnee/Eis" },
            new Entry { cls = Water,    r = 0x00, g = 0x64, b = 0xC8, name = "Wasser" },
            new Entry { cls = Wetland,  r = 0x00, g = 0x96, b = 0xA0, name = "Feuchtgebiet" },
            new Entry { cls = Mangrove, r = 0x00, g = 0xCF, b = 0x75, name = "Mangroven" },
            new Entry { cls = Moss,     r = 0xFA, g = 0xE6, b = 0xA0, name = "Moos/Flechten" },
        };

        // Höchster Farbabstand (euklidisch, RGB) zur nächsten Legendenfarbe; darüber = Unknown (Beschriftung, Rand, Antialiasing).
        public const int MaxColorDistance = 40;

        // Nächste Legendenfarbe (robust gegen leichte Farbabweichungen, z. B. Komprimierung/Farbraum).
        public static byte Classify(byte r, byte g, byte b, out int distance)
        {
            int best = int.MaxValue; byte cls = Unknown;
            for (int i = 0; i < Legend.Length; i++)
            {
                int dr = r - Legend[i].r, dg = g - Legend[i].g, db = b - Legend[i].b, d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; cls = Legend[i].cls; }
            }
            distance = (int)Math.Sqrt(best);
            return distance <= MaxColorDistance ? cls : Unknown;
        }

        public static bool IsCode(byte v)
        {
            for (int i = 0; i < Legend.Length; i++) if (Legend[i].cls == v) return true;
            return false;
        }

        public static string Name(byte cls)
        {
            for (int i = 0; i < Legend.Length; i++) if (Legend[i].cls == cls) return Legend[i].name;
            return "unbekannt";
        }
    }

    // Landbedeckung im SELBEN lokalen ENU-Frame wie DEM/GPX/OSM (Ursprung = erster GPX-Punkt), ein Byte pro Zelle.
    // Datei: gzip( "SCLC1" | w | h | minX | minZ | cell | lat0 | lon0 | byte[w*h] ), Zeile 0 = Süden (wie DemGrid).
    public sealed class LandCoverGrid
    {
        public int Width, Height;
        public float MinX, MinZ, Cell;
        public double OriginLat, OriginLon;
        public byte[] Classes;

        public float MaxX => MinX + (Width - 1) * Cell;
        public float MaxZ => MinZ + (Height - 1) * Cell;

        // Klasse an einer lokalen Position (Nächster-Nachbar, außerhalb = Unknown).
        public byte Sample(float x, float z)
        {
            int ix = Mathf.FloorToInt((x - MinX) / Cell + .5f), iz = Mathf.FloorToInt((z - MinZ) / Cell + .5f);
            if (ix < 0 || iz < 0 || ix >= Width || iz >= Height) return LandCoverClass.Unknown;
            return Classes[iz * Width + ix];
        }

        public bool MatchesOrigin(GeoPoint origin) => Math.Abs(origin.Lat - OriginLat) < 1e-6 && Math.Abs(origin.Lon - OriginLon) < 1e-6;

        // Anteil je Klasse (nur bekannte Zellen), für Protokoll und Plausibilitätsprüfung.
        public float[] Shares(out float knownShare)
        {
            var count = new long[256]; long known = 0;
            for (int i = 0; i < Classes.Length; i++) { count[Classes[i]]++; if (Classes[i] != LandCoverClass.Unknown) known++; }
            var s = new float[256];
            for (int c = 0; c < 256; c++) s[c] = known > 0 ? count[c] / (float)known : 0f;
            knownShare = Classes.Length > 0 ? known / (float)Classes.Length : 0f;
            return s;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz))
            {
                w.Write("SCLC1");
                w.Write(Width); w.Write(Height);
                w.Write(MinX); w.Write(MinZ); w.Write(Cell);
                w.Write(OriginLat); w.Write(OriginLon);
                w.Write(Classes);
            }
        }

        public static LandCoverGrid Load(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (r.ReadString() != "SCLC1") throw new InvalidDataException("Kein Landbedeckungs-File: " + path);
                var g = new LandCoverGrid
                {
                    Width = r.ReadInt32(), Height = r.ReadInt32(),
                    MinX = r.ReadSingle(), MinZ = r.ReadSingle(), Cell = r.ReadSingle(),
                    OriginLat = r.ReadDouble(), OriginLon = r.ReadDouble()
                };
                g.Classes = r.ReadBytes(g.Width * g.Height);
                if (g.Classes.Length != g.Width * g.Height) throw new InvalidDataException("Landbedeckung abgeschnitten: " + path);
                return g;
            }
        }
    }

    // Klassenraster in Breiten-/Längengrad (Zeile 0 = Norden), wie es die WMS-Kacheln liefern.
    public sealed class LatLonClassRaster
    {
        public readonly int Width, Height;
        public readonly double West, North, DLon, DLat;    // Rand links/oben; Pixelgröße in Grad (positiv)
        public readonly byte[] Classes;

        public LatLonClassRaster(int width, int height, double west, double north, double dLon, double dLat)
        { Width = width; Height = height; West = west; North = north; DLon = dLon; DLat = dLat; Classes = new byte[width * height]; }

        public byte At(double lat, double lon)
        {
            int ix = (int)Math.Floor((lon - West) / DLon), iy = (int)Math.Floor((North - lat) / DLat);
            if (ix < 0 || iy < 0 || ix >= Width || iy >= Height) return LandCoverClass.Unknown;
            return Classes[iy * Width + ix];
        }
    }

    public static class LandCoverResample
    {
        private const double R = 6371000.0;     // muss zu GpxParser.ProjectToLocalMeters passen

        // Umkehrung von GpxParser.ProjectToLocalMeters: lokale Meter (x Ost, z Nord) -> Breite/Länge in Grad.
        public static void LatLonOf(double originLat, double originLon, double x, double z, out double lat, out double lon)
        {
            double lat0 = originLat * Math.PI / 180.0, lon0 = originLon * Math.PI / 180.0;
            lat = (lat0 + z / R) * 180.0 / Math.PI;
            lon = (lon0 + x / (R * Math.Cos(lat0))) * 180.0 / Math.PI;
        }

        // Klasse eines Pixels: nächste Legendenfarbe; ersatzweise ein reines Grau = Klassencode (Rohdaten-Ebene ohne Farbtabelle).
        public static byte ClassOfPixel(byte[] rgba, int o)
        {
            if (rgba[o + 3] < 128) return LandCoverClass.Unknown;
            byte r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
            byte cls = LandCoverClass.Classify(r, g, b, out _);
            if (cls == LandCoverClass.Unknown && r == g && g == b && LandCoverClass.IsCode(r)) cls = r;
            return cls;
        }

        // RGBA-Bild (Zeile 0 = oben) -> Klassen, schreibt in dst ab (x0,y0). Liefert die Zahl erkannter Pixel.
        public static int DecodeRgba(byte[] rgba, int w, int h, LatLonClassRaster dst, int x0, int y0)
        {
            int known = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte cls = ClassOfPixel(rgba, o);
                int dx = x0 + x, dy = y0 + y;
                if (dx >= 0 && dy >= 0 && dx < dst.Width && dy < dst.Height) dst.Classes[dy * dst.Width + dx] = cls;
                if (cls != LandCoverClass.Unknown) known++;
            }
            return known;
        }

        // Unbekannte Pixel mit bekannten Nachbarn (Mischfarben an Klassenrändern) bekommen die häufigste Nachbarklasse.
        // passes = wie viele Pixel tief vom Rand aus gefüllt wird. Liefert die Zahl gefüllter Pixel.
        public static int FillUnknown(LatLonClassRaster r, int passes)
        {
            int w = r.Width, h = r.Height, filled = 0;
            var cnt = new int[256];
            for (int pass = 0; pass < passes; pass++)
            {
                var src = (byte[])r.Classes.Clone();
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (src[y * w + x] != LandCoverClass.Unknown) continue;
                    int best = 0, bc = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx; if ((dx == 0 && dy == 0) || xx < 0 || xx >= w) continue;
                            byte c = src[yy * w + xx]; if (c == LandCoverClass.Unknown) continue;
                            if (++cnt[c] > bc) { bc = cnt[c]; best = c; }
                        }
                    }
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++) { int xx = x + dx; if (xx >= 0 && xx < w) cnt[src[yy * w + xx]] = 0; }
                    }
                    if (bc > 0) { r.Classes[y * w + x] = (byte)best; filled++; }
                }
            }
            return filled;
        }

        // Breiten-/Längenraster -> lokales ENU-Raster (Nächster-Nachbar; Klassen dürfen nicht gemittelt werden).
        public static LandCoverGrid ToLocalGrid(LatLonClassRaster src, double originLat, double originLon,
                                                float minX, float minZ, int width, int height, float cell)
        {
            var g = new LandCoverGrid { Width = width, Height = height, MinX = minX, MinZ = minZ, Cell = cell, OriginLat = originLat, OriginLon = originLon };
            g.Classes = new byte[width * height];
            for (int iz = 0; iz < height; iz++)
            for (int ix = 0; ix < width; ix++)
            {
                LatLonOf(originLat, originLon, minX + ix * (double)cell, minZ + iz * (double)cell, out double lat, out double lon);
                g.Classes[iz * width + ix] = src.At(lat, lon);
            }
            return g;
        }
    }
}
