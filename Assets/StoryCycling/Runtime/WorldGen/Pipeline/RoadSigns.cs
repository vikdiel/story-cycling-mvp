using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Beschilderung aus der Streckengeometrie (OSM hat kaum Schilder):
    //   - Warndreieck ~60 m vor Kurven mit Radius < 110 m (außerorts), links am Fahrbahnrand (Linksverkehr)
    //   - Richtungstafeln an der Kurvenaußenseite enger Kehren (Radius < 65 m)
    //   - Straßennamenschild an jeder Einmündung einer Querstraße
    // Jede Fahrtrichtung bekommt ihre eigenen Schilder (auch auf der gemeinsamen Hin-/Rückweg-Straße).
    public static class RoadSigns
    {
        private const int Window = 10;                 // ±20 m für die Krümmung
        private const float WarnRadius = 110f, ChevronRadius = 65f;

        public static int Place(RoadField road, WorldTerrain terrain, StreetNetwork streets, AssetCatalog catalog,
                                Occupancy occupied, Transform parent)
        {
            var warn = WorldPlacement.Pool(catalog, @"^SM_Prop_Sign_Warning_\d+$");
            var chevron = WorldPlacement.Pool(catalog, @"^SM_Prop_Sign_Arrow_\d+$");
            var street = WorldPlacement.Pool(catalog, @"^SM_Prop_Sign_Street_\d+$");
            var s = road.Samples;
            var placed = new List<Vector3>();
            int warnings = 0, chevrons = 0, names = 0;

            // Krümmungsradius je Probe (vorzeichenbehaftet: >0 Rechtskurve)
            var radius = new float[s.Count];
            var turn = new float[s.Count];
            for (int k = 0; k < s.Count; k++)
            {
                int a = Mathf.Max(0, k - Window), b = Mathf.Min(s.Count - 1, k + Window);
                Vector3 ta = Flat(s[a].tangent), tb = Flat(s[b].tangent);
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ta, tb), -1f, 1f));
                float len = s[b].distance - s[a].distance;
                radius[k] = ang > 1e-3f ? len / ang : float.MaxValue;
                turn[k] = Mathf.Sign(Vector3.Cross(ta, tb).y);
            }

            for (int k = Window; k < s.Count - Window; k++)
            {
                bool bendStart = radius[k] < WarnRadius && radius[k - 1] >= WarnRadius;
                if (!bendStart) continue;
                // Scheitel und kleinster Radius der Kurve
                int apex = k; float minR = radius[k];
                int e = k; while (e < s.Count - 1 && radius[e] < WarnRadius) { if (radius[e] < minR) { minR = radius[e]; apex = e; } e++; }
                if (e - k < 5) continue;                                              // nur echte Kurven (> 10 m)

                // Warndreieck 60 m vorher, links (Linksverkehr) zum ankommenden Fahrer gedreht
                int w = Mathf.Max(0, k - 30);
                if (warn.Count > 0 && terrain.BiomeAt(s[w].pos.x, s[w].pos.z) != WorldTerrain.Biome.Urban &&
                    TrySpot(road, terrain, s[w], -1f, RoadMeshBuilder.HalfWidth + 1.4f, placed, 25f, out Vector3 wp))
                {
                    Spawn(warn[0], parent, wp, -Flat(s[w].tangent), s[w].pos.y);
                    warnings++;
                }

                // Richtungstafeln an der Außenseite enger Kurven
                if (minR < ChevronRadius && chevron.Count > 0)
                {
                    float outside = -turn[apex];                                       // Rechtskurve -> links außen
                    for (int c = -1; c <= 1; c++)
                    {
                        int ci = Mathf.Clamp(apex + c * 8, 0, s.Count - 1);
                        if (!TrySpot(road, terrain, s[ci], outside, RoadMeshBuilder.HalfWidth + 1.8f, placed, 12f, out Vector3 cp)) continue;
                        Spawn(chevron[0], parent, cp, -Flat(s[ci].tangent), s[ci].pos.y);
                        chevrons++;
                    }
                }
                k = e;
            }

            // Straßennamen an Einmündungen
            if (streets != null && street.Count > 0)
            {
                int idx = 0;
                foreach (var j in streets.Junctions)
                {
                    if (!road.Nearest(j.Mouth.x, j.Mouth.z, 8f, out int mi, out _)) continue;
                    var ms = s[mi];
                    float side = Mathf.Sign(Vector3.Dot(j.Mouth - ms.pos, ms.side));
                    // an die Ecke vor der Einmündung (gegen die Fahrtrichtung versetzt)
                    Vector3 at = ms.pos - Flat(ms.tangent) * (j.Half + 1.5f);
                    var shifted = ms; shifted.pos = at;
                    if (!TrySpot(road, terrain, shifted, side, RoadMeshBuilder.HalfWidth + 1.6f, placed, 10f, out Vector3 np)) continue;
                    Spawn(street[idx++ % street.Count], parent, np, -ms.side * side, ms.pos.y);
                    names++;
                }
            }
            Debug.Log($"Schilder: {warnings} Kurvenwarnungen, {chevrons} Richtungstafeln, {names} Straßennamen.");
            return warnings + chevrons + names;
        }

        private static Vector3 Flat(Vector3 t) { t.y = 0f; return t.sqrMagnitude > 1e-6f ? t.normalized : Vector3.forward; }

        private static bool TrySpot(RoadField road, WorldTerrain terrain, RoadField.Sample sm, float sideSign, float offset,
                                    List<Vector3> placed, float minSpacing, out Vector3 p)
        {
            p = sm.pos + sm.side * (sideSign * offset);
            if (road.Distance(p.x, p.z, 10f) < RoadMeshBuilder.HalfWidth + .8f) return false;
            if (VegetationPlacer.OnStreet(terrain, p.x, p.z, -2.3f)) return false;
            foreach (var q in placed) if ((q - p).sqrMagnitude < minSpacing * minSpacing) return false;
            placed.Add(p);
            return true;
        }

        // Schildvorderseite = lokal +Z (wie Stop/Vorfahrt, siehe OsmDetailPlacer.SignalFrontIsPositiveZ)
        private static void Spawn(GameObject prefab, Transform parent, Vector3 p, Vector3 facing, float roadY)
        {
            if (!OsmDetailPlacer.SignalFrontIsPositiveZ) facing = -facing;
            float y = roadY - .05f;
            WorldPlacement.Spawn(prefab, parent, new Vector3(p.x, y, p.z), Quaternion.LookRotation(facing, Vector3.up), 1f, y, .02f);
        }
    }
}
