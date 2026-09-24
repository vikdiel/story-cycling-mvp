using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // OSM side streets near the GPX route. They are sampled at the same cadence as the
    // main RoadField so terrain carving, placement clearance and mesh building agree.
    public sealed class StreetNetwork
    {
        public sealed class Street
        {
            public readonly List<RoadField.Sample> Samples = new List<RoadField.Sample>();
            public string Highway;
            public bool CenterLine, Roundabout;
        }
        public struct Junction { public Vector3 Mouth; public float Half; }
        public struct Island { public Vector3 Center; public float Radius; }

        public const float MaxDistance = 100f;
        public readonly List<Street> Streets = new List<Street>();
        public readonly List<Junction> Junctions = new List<Junction>();
        public readonly List<Island> Islands = new List<Island>();
        public RoadField Field { get; private set; }

        public static float HalfWidthFor(string highway)
        {
            switch (highway)
            {
                case "trunk": case "primary": return 3.6f;
                case "secondary": case "tertiary": case "trunk_link": case "primary_link": case "secondary_link": case "tertiary_link": return 3.1f;
                default: return 2.8f;
            }
        }

        public static StreetNetwork Build(OsmContext osm, RoadField main, WorldTerrain terrain)
        {
            var net = new StreetNetwork();
            if (osm == null) { net.Field = new RoadField(new List<RoadField.Sample>()); return net; }
            var all = new List<RoadField.Sample>();
            foreach (var way in osm.Streets)
            {
                if (way.bridge || way.tunnel || way.pts == null || way.pts.Count < 2) continue;
                var pts = Resample(way.pts, RoadField.Step);
                var street = new Street { Highway = way.highway, Roundabout = way.roundabout, CenterLine = !way.oneway && HalfWidthFor(way.highway) >= 3.1f };
                float arc = 0f;
                for (int i = 0; i < pts.Count; i++)
                {
                    main.Nearest(pts[i].x, pts[i].y, MaxDistance, out int mainIndex, out float dist);
                    if (mainIndex < 0) continue;
                    Vector2 a = pts[Mathf.Max(0, i - 1)], b = pts[Mathf.Min(pts.Count - 1, i + 1)];
                    Vector2 tangent2 = (b - a).normalized;
                    if (tangent2.sqrMagnitude < 1e-5f) continue;
                    if (street.Samples.Count > 0) arc += Vector2.Distance(pts[i], pts[i - 1]);
                    float y = terrain.DemY(pts[i].x, pts[i].y);
                    if (dist < RoadMeshBuilder.HalfWidth + 1.5f) y = main.Samples[mainIndex].pos.y - .03f;
                    Vector3 tangent = new Vector3(tangent2.x, 0f, tangent2.y);
                    street.Samples.Add(new RoadField.Sample
                    {
                        pos = new Vector3(pts[i].x, y, pts[i].y), tangent = tangent,
                        side = Vector3.Cross(Vector3.up, tangent).normalized, distance = arc, half = HalfWidthFor(way.highway)
                    });
                }
                if (street.Samples.Count < 7) continue;
                // Do not lay a second ribbon over the GPX centreline for a matching OSM way.
                int near = 0;
                foreach (var sample in street.Samples) if (main.Distance(sample.pos.x, sample.pos.z, 20f) < 5f) near++;
                if (near > street.Samples.Count * .8f) continue;
                net.Streets.Add(street);
                all.AddRange(street.Samples);
                AddJunctions(net, street, main);
                if (way.roundabout) net.AddIsland(street);
            }
            net.Field = new RoadField(all);
            Debug.Log($"Querstraßen: {net.Streets.Count} Abschnitte, {all.Count * RoadField.Step / 1000f:0.0} km, {net.Islands.Count} Kreisverkehr-Inseln.");
            return net;
        }

        private static void AddJunctions(StreetNetwork net, Street street, RoadField main)
        {
            foreach (int index in new[] { 0, street.Samples.Count - 1 })
            {
                var s = street.Samples[index];
                if (!main.Nearest(s.pos.x, s.pos.z, 12f, out int mi, out float d) || d > 9f) continue;
                var root = main.Samples[mi];
                float sign = Mathf.Sign(Vector3.Dot(s.pos - root.pos, root.side));
                if (Mathf.Abs(sign) < .1f) sign = 1f;
                net.Junctions.Add(new Junction { Mouth = root.pos + root.side * sign * RoadMeshBuilder.HalfWidth, Half = street.Samples[index].half + 5f });
            }
        }

        private void AddIsland(Street street)
        {
            Vector3 center = Vector3.zero;
            foreach (var s in street.Samples) center += s.pos;
            center /= street.Samples.Count;
            float radius = 0f;
            foreach (var s in street.Samples) radius += Vector2.Distance(new Vector2(center.x, center.z), new Vector2(s.pos.x, s.pos.z));
            radius /= street.Samples.Count;
            radius -= street.Samples[0].half + .6f;
            if (radius >= 2f && radius < 45f) Islands.Add(new Island { Center = center, Radius = radius });
        }

        public float IslandBaseY(Island island, RoadField main)
        {
            if (Field != null && Field.Nearest(island.Center.x, island.Center.z, island.Radius + 8f, out int i, out _)) return Field.Samples[i].pos.y;
            return main.Nearest(island.Center.x, island.Center.z, island.Radius + 8f, out int j, out _) ? main.Samples[j].pos.y : float.NaN;
        }

        private static List<Vector2> Resample(List<Vector2> pts, float step)
        {
            var result = new List<Vector2> { pts[0] };
            float carry = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i]; float length = Vector2.Distance(a, b);
                if (length < .001f) continue;
                for (float d = step - carry; d <= length; d += step) result.Add(Vector2.Lerp(a, b, d / length));
                carry = Mathf.Repeat(carry + length, step);
            }
            if (Vector2.Distance(result[result.Count - 1], pts[pts.Count - 1]) > .25f) result.Add(pts[pts.Count - 1]);
            return result;
        }
    }
}
