using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Höhenraster (DEM) im SELBEN lokalen ENU-Frame wie GPX/OSM (Ursprung = erster GPX-Punkt).
    // Werte sind ABSOLUTE Meereshöhen in Metern; der Szenen-Builder zieht die Höhe des
    // ersten GPX-Punkts ab (GpxParser rechnet y relativ zum Start).
    // Datei: gzip( "SCDEM1" | w | h | minX | minZ | cell | lat0 | lon0 | short[w*h] Dezimeter ).
    public sealed class DemGrid
    {
        public int Width, Height;
        public float MinX, MinZ, Cell;
        public double OriginLat, OriginLon;
        public short[] Decimetres;

        public float MaxX => MinX + (Width - 1) * Cell;
        public float MaxZ => MinZ + (Height - 1) * Cell;

        public float Raw(int ix, int iz)
        {
            ix = Mathf.Clamp(ix, 0, Width - 1);
            iz = Mathf.Clamp(iz, 0, Height - 1);
            return Decimetres[iz * Width + ix] * 0.1f;
        }

        // Bilinear, absolute Höhe in Metern. Außerhalb des Rasters: Randwert.
        public float Sample(float x, float z)
        {
            float fx = (x - MinX) / Cell, fz = (z - MinZ) / Cell;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float tx = fx - ix, tz = fz - iz;
            float a = Raw(ix, iz), b = Raw(ix + 1, iz), c = Raw(ix, iz + 1), d = Raw(ix + 1, iz + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz))
            {
                w.Write("SCDEM1");
                w.Write(Width); w.Write(Height);
                w.Write(MinX); w.Write(MinZ); w.Write(Cell);
                w.Write(OriginLat); w.Write(OriginLon);
                for (int i = 0; i < Decimetres.Length; i++) w.Write(Decimetres[i]);
            }
        }

        public static DemGrid Load(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (r.ReadString() != "SCDEM1") throw new InvalidDataException("Kein DEM-File: " + path);
                var g = new DemGrid
                {
                    Width = r.ReadInt32(), Height = r.ReadInt32(),
                    MinX = r.ReadSingle(), MinZ = r.ReadSingle(), Cell = r.ReadSingle(),
                    OriginLat = r.ReadDouble(), OriginLon = r.ReadDouble()
                };
                g.Decimetres = new short[g.Width * g.Height];
                for (int i = 0; i < g.Decimetres.Length; i++) g.Decimetres[i] = r.ReadInt16();
                return g;
            }
        }

        public static short ToDecimetres(float metres)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(metres * 10f), short.MinValue + 1, short.MaxValue);
        }

        public bool MatchesOrigin(GeoPoint origin)
        {
            return Math.Abs(origin.Lat - OriginLat) < 1e-6 && Math.Abs(origin.Lon - OriginLon) < 1e-6;
        }
    }
}
