using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Parses an Overpass OSM XML dump into buildings (way), areas (landuse/natural/parking),
    // point features (nodes: signs, lamps, benches, bins, hydrants, trees, rocks…) and
    // barrier lines (fence/hedge/wall). Everything projected into the SAME local ENU metre
    // frame as the GPX route so it lines up automatically. Pure data; no UnityEditor.
    public class OsmContext
    {
        public class Building
        {
            public List<Vector2> ring; public Vector2 centroid;
            public float heightM, width, depth, area; public Vector3 axisDir;
        }
        public class Area { public List<Vector2> ring; public string biome; }   // biome incl. "parking"
        public class Point { public Vector2 pos; public string kind; }           // kind = normalised feature
        public class Line { public List<Vector2> pts; public string kind; }      // fence | hedge | wall

        public readonly List<Building> Buildings = new List<Building>();
        public readonly List<Area> Areas = new List<Area>();
        public readonly List<Point> Points = new List<Point>();
        public readonly List<Line> Lines = new List<Line>();

        private const double R = 6371000.0; // must match GpxParser.ProjectToLocalMeters

        public static OsmContext Load(string osmXml, GeoPoint origin)
        {
            var ctx = new OsmContext();
            var doc = new XmlDocument();
            doc.LoadXml(osmXml);
            double lat0 = origin.Lat * Math.PI / 180.0, lon0 = origin.Lon * Math.PI / 180.0;

            Vector2 Project(double lat, double lon) => new Vector2(
                (float)(R * (lon * Math.PI / 180.0 - lon0) * Math.Cos(lat0)),
                (float)(R * (lat * Math.PI / 180.0 - lat0)));

            // --- Ways: buildings, areas, barrier lines ---
            foreach (XmlNode way in doc.SelectNodes("//*[local-name()='way']"))
            {
                var ring = new List<Vector2>();
                foreach (XmlNode nd in way.SelectNodes("*[local-name()='nd']"))
                    ring.Add(Project(Attr(nd, "lat"), Attr(nd, "lon")));
                if (ring.Count < 2) continue;

                var tags = ReadTags(way);
                if (tags.ContainsKey("building") && ring.Count >= 3)
                    ctx.Buildings.Add(MakeBuilding(ring, tags));
                else if (tags.TryGetValue("barrier", out string bar) && (bar == "fence" || bar == "hedge" || bar == "wall"))
                    ctx.Lines.Add(new Line { pts = ring, kind = bar });
                else
                {
                    string biome = AreaKind(tags);
                    if (biome != null && ring.Count >= 3) ctx.Areas.Add(new Area { ring = ring, biome = biome });
                }
            }

            // --- Nodes: point features ---
            foreach (XmlNode node in doc.SelectNodes("//*[local-name()='node']"))
            {
                var tags = ReadTags(node);
                string kind = PointKind(tags);
                if (kind == null) continue;
                ctx.Points.Add(new Point { pos = Project(Attr(node, "lat"), Attr(node, "lon")), kind = kind });
            }
            return ctx;
        }

        public string BiomeAt(Vector3 worldPos)
        {
            var p = new Vector2(worldPos.x, worldPos.z);
            foreach (var a in Areas) if (a.biome != "parking" && PointInPolygon(p, a.ring)) return a.biome;
            return "generic";
        }

        // --- tag → normalised kind ------------------------------------------------
        private static string PointKind(Dictionary<string, string> t)
        {
            if (t.TryGetValue("highway", out string hw))
                switch (hw) {
                    case "traffic_signals": return "traffic_signals";
                    case "street_lamp": return "street_lamp";
                    case "bus_stop": return "bus_stop";
                    case "stop": return "sign_stop";
                    case "give_way": return "sign_give_way";
                }
            if (t.TryGetValue("amenity", out string am))
                switch (am) {
                    case "bench": return "bench";
                    case "waste_basket": return "waste_basket";
                    case "post_box": return "mailbox";
                    case "parking_meter": return "parking_meter";
                }
            if (t.TryGetValue("emergency", out string em) && em == "fire_hydrant") return "fire_hydrant";
            if (t.TryGetValue("natural", out string na))
                switch (na) { case "tree": return "tree"; case "stone": case "rock": return "rock"; }
            return null;
        }

        private static string AreaKind(Dictionary<string, string> t)
        {
            if (t.TryGetValue("amenity", out string am) && am == "parking") return "parking";
            if (t.TryGetValue("landuse", out string lu))
                switch (lu) {
                    case "residential": case "commercial": case "retail": case "industrial": return "urban";
                    case "forest": return "forest";
                    case "farmland": case "meadow": case "grass": case "farmyard": case "orchard":
                    case "flowerbed": case "vineyard": case "recreation_ground": return "field";
                }
            if (t.TryGetValue("natural", out string na))
                switch (na) {
                    case "wood": return "forest";
                    case "scrub": case "heath": return "scrub";          // Fynbos: Büsche, kaum Bäume
                    case "grassland": return "field";
                    case "beach": case "sand": return "beach";
                    case "bare_rock": case "scree": case "cliff": return "rock";
                    case "water": case "wetland": return "water";
                }
            return null;
        }

        // --- building geometry (unchanged) ---------------------------------------
        private static Building MakeBuilding(List<Vector2> ring, Dictionary<string, string> tags)
        {
            Vector2 mean = Vector2.zero; foreach (var v in ring) mean += v; mean /= ring.Count;
            float sxx = 0, sxz = 0, szz = 0;
            foreach (var v in ring) { Vector2 d = v - mean; sxx += d.x * d.x; sxz += d.x * d.y; szz += d.y * d.y; }
            float angle = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
            Vector2 major = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), minor = new Vector2(-major.y, major.x);
            float aMin = float.MaxValue, aMax = float.MinValue, bMin = float.MaxValue, bMax = float.MinValue;
            foreach (var v in ring) { Vector2 d = v - mean; float pm = Vector2.Dot(d, major), pn = Vector2.Dot(d, minor);
                aMin = Mathf.Min(aMin, pm); aMax = Mathf.Max(aMax, pm); bMin = Mathf.Min(bMin, pn); bMax = Mathf.Max(bMax, pn); }
            return new Building {
                ring = ring, centroid = Centroid(ring), heightM = HeightFromTags(tags),
                width = Mathf.Max(1f, aMax - aMin), depth = Mathf.Max(1f, bMax - bMin),
                area = Mathf.Abs(ShoelaceArea(ring)), axisDir = new Vector3(major.x, 0f, major.y).normalized };
        }

        private static float HeightFromTags(Dictionary<string, string> t)
        {
            if (t.TryGetValue("height", out string h) && TryLeadingFloat(h, out float m)) return m;
            if (t.TryGetValue("building:levels", out string lv) && TryLeadingFloat(lv, out float lvl)) return Mathf.Max(3f, lvl * 3.2f);
            return 7f;
        }

        // --- helpers --------------------------------------------------------------
        private static Dictionary<string, string> ReadTags(XmlNode n)
        {
            var tags = new Dictionary<string, string>();
            foreach (XmlNode t in n.SelectNodes("*[local-name()='tag']"))
            { string k = t.Attributes?["k"]?.Value, v = t.Attributes?["v"]?.Value; if (k != null && v != null) tags[k] = v; }
            return tags;
        }
        private static double Attr(XmlNode n, string name)
        { double v = 0; var a = n.Attributes?[name]; if (a != null) double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v); return v; }
        private static bool TryLeadingFloat(string s, out float value)
        { value = 0f; int i = 0; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-')) i++;
          return i > 0 && float.TryParse(s.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        private static Vector2 Centroid(List<Vector2> ring)
        {
            float a = 0, cx = 0, cz = 0;
            for (int i = 0; i < ring.Count; i++) { Vector2 p0 = ring[i], p1 = ring[(i + 1) % ring.Count];
                float cr = p0.x * p1.y - p1.x * p0.y; a += cr; cx += (p0.x + p1.x) * cr; cz += (p0.y + p1.y) * cr; }
            if (Mathf.Abs(a) < 1e-4f) { Vector2 m = Vector2.zero; foreach (var v in ring) m += v; return m / ring.Count; }
            a *= 0.5f; return new Vector2(cx / (6f * a), cz / (6f * a));
        }
        private static float ShoelaceArea(List<Vector2> ring)
        { float a = 0; for (int i = 0; i < ring.Count; i++) { Vector2 p0 = ring[i], p1 = ring[(i + 1) % ring.Count]; a += p0.x * p1.y - p1.x * p0.y; } return a * 0.5f; }

        public static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                    (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                    inside = !inside;
            return inside;
        }
    }
}
