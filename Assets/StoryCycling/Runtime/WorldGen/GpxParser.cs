using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // One track/route point in WGS84 + elevation (metres).
    public struct GeoPoint
    {
        public double Lat, Lon, Ele;
        public GeoPoint(double lat, double lon, double ele) { Lat = lat; Lon = lon; Ele = ele; }
    }

    // Parses GPX (<trkpt>/<rtept>/<wpt>) into points and projects them into a local
    // ENU metre frame centred on the first point: X = east, Y = elevation, Z = north.
    // Pure data step; no geometry is invented here.
    public static class GpxParser
    {
        public static List<GeoPoint> Parse(string gpxXml)
        {
            var points = new List<GeoPoint>();
            var doc = new XmlDocument();
            doc.LoadXml(gpxXml);

            // local-name() is namespace-agnostic: works for GPX 1.0, 1.1 and no-namespace files.
            foreach (XmlNode node in doc.SelectNodes("//*[local-name()='trkpt']"))
                points.Add(ReadPoint(node));
            if (points.Count == 0)
                foreach (XmlNode node in doc.SelectNodes("//*[local-name()='rtept']"))
                    points.Add(ReadPoint(node));
            if (points.Count == 0)
                foreach (XmlNode node in doc.SelectNodes("//*[local-name()='wpt']"))
                    points.Add(ReadPoint(node));
            return points;
        }

        private static GeoPoint ReadPoint(XmlNode node)
        {
            double lat = DoubleAttr(node, "lat");
            double lon = DoubleAttr(node, "lon");
            double ele = 0;
            XmlNode eleNode = node.SelectSingleNode("*[local-name()='ele']");
            if (eleNode != null) double.TryParse(eleNode.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out ele);
            return new GeoPoint(lat, lon, ele);
        }

        private static double DoubleAttr(XmlNode node, string name)
        {
            double v = 0;
            XmlAttribute attr = node.Attributes?[name];
            if (attr != null) double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        // Equirectangular projection around the first point. Error is negligible at the
        // ~50 km scale of a single route (< 0.1 %), so no full UTM needed for v1.
        public static List<Vector3> ProjectToLocalMeters(List<GeoPoint> points)
        {
            if (points == null || points.Count == 0) return new List<Vector3>();
            const double R = 6371000.0;
            double lat0 = points[0].Lat * Math.PI / 180.0;
            double lon0 = points[0].Lon * Math.PI / 180.0;
            double ele0 = points[0].Ele;
            var result = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                double lat = points[i].Lat * Math.PI / 180.0;
                double dLat = lat - lat0;
                double dLon = points[i].Lon * Math.PI / 180.0 - lon0;
                float x = (float)(R * dLon * Math.Cos(lat0)); // east
                float z = (float)(R * dLat);                   // north
                float y = (float)(points[i].Ele - ele0);       // up, relative to start
                result.Add(new Vector3(x, y, z));
            }
            return result;
        }

        // Arc-length in metres along the projected polyline — a quick sanity metric.
        public static float PathLength(List<Vector3> points)
        {
            float length = 0f;
            for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
            return length;
        }
    }
}
