using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Setzt Gebäude-Prefabs an die ECHTEN OSM-Footprints. Gegenüber v1:
    //   - nur ganze Gebäude (vorher auch Dach-/Sockel-/Markisen-Bauteile -> schwebende Fragmente)
    //   - Höhe aus OSM (levels/height) fließt in die Auswahl ein (keine Bürotürme im Vorort)
    //   - lange Seite entlang der Footprint-Achse, Front zur nächsten Straße
    //   - nie auf der Fahrbahn, nicht ineinander, Unterkante am tiefsten Geländepunkt (kein Schweben am Hang)
    public static class OsmBuildingPlacer
    {
        // Synty-Gebäude: Frontseite (Türen/Schaufenster) zeigt entlang lokal +Z.
        // Falls die Eingänge im Build von der Straße WEG zeigen: auf false setzen.
        public const bool FrontIsPositiveZ = true;

        private const string WholeBuilding = @"^SM_Bld_(Shop|Apartment|OfficeOld_Small|OfficeOld_Large|OfficeSquare|OfficeRound|OfficeOctagon)_\d+$";
        private const float RoadClearance = RoadMeshBuilder.HalfWidth + 3f;

        private struct Sized { public GameObject prefab; public float w, d, h, area; public Vector3 pivotToCenter; }

        public static int Place(RoadField road, WorldTerrain terrain, OsmContext ctx, AssetCatalog catalog,
                                Occupancy occupied, Transform parent)
        {
            var sizes = MeasurePrefabs(WorldPlacement.Pool(catalog, WholeBuilding));
            if (sizes.Count == 0) { Debug.LogWarning("OSM: keine ganzen Gebäude-Prefabs im Katalog."); return 0; }
            if (ctx.Buildings.Count == 0) { Debug.LogWarning("OSM: keine Gebäude im OSM-Dump (erst 'Fetch OSM')."); return 0; }

            var nearby = new List<int>();
            Streets = terrain.Streets;
            int placed = 0, skippedRoad = 0, skippedOverlap = 0, skippedSea = 0;

            foreach (var b in ctx.Buildings)
            {
                Vector3 c = new Vector3(b.centroid.x, 0f, b.centroid.y);
                if (terrain.DemY(c.x, c.z) < terrain.SeaY + .5f) { skippedSea++; continue; }

                float targetH = Mathf.Clamp(b.heightM, 3f, 80f);
                Sized pick = NearestBySize(sizes, b.area, b.width / Mathf.Max(1f, b.depth), targetH);
                float scale = Mathf.Clamp(Mathf.Sqrt(b.area / Mathf.Max(1f, pick.area)), .75f, 1.3f);

                Quaternion rot = Orientation(pick, b.axisDir, c, road);
                Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;

                // Fahrbahn freihalten (bei Bedarf kleiner skalieren, sonst weglassen).
                if (!ClearOfRoad(road, nearby, c, right, fwd, pick.w * .5f * scale, pick.d * .5f * scale))
                {
                    scale = .75f;
                    if (!ClearOfRoad(road, nearby, c, right, fwd, pick.w * .5f * scale, pick.d * .5f * scale)) { skippedRoad++; continue; }
                }
                float radius = .5f * Mathf.Sqrt(pick.w * pick.w + pick.d * pick.d) * scale;
                if (!occupied.IsFree(c.x, c.z, radius * .55f)) { skippedOverlap++; continue; }

                float ground = terrain.LowestUnder(c, right, fwd, pick.w * .5f * scale, pick.d * .5f * scale);
                // Synty-Pivots sitzen oft an einer Ecke: so verschieben, dass die MITTE auf dem Footprint liegt.
                Vector3 pivot = c - rot * (pick.pivotToCenter * scale);
                WorldPlacement.Spawn(pick.prefab, parent, new Vector3(pivot.x, ground, pivot.z), rot, scale, ground, .15f);
                occupied.Add(c.x, c.z, radius * .55f);
                placed++;
            }
            Debug.Log($"OSM-Gebäude: {placed} platziert (Prefabs {sizes.Count}, OSM {ctx.Buildings.Count}; " +
                      $"weggelassen: Fahrbahn {skippedRoad}, Überlappung {skippedOverlap}, Meer {skippedSea}).");
            return placed;
        }

        // Lange Prefab-Seite entlang der Footprint-Hauptachse; von den möglichen Drehungen die,
        // deren Front am meisten zur nächsten Straße zeigt.
        private static Quaternion Orientation(Sized s, Vector3 axis, Vector3 c, RoadField road)
        {
            axis.y = 0f; axis = axis.sqrMagnitude < 1e-6f ? Vector3.forward : axis.normalized;
            Vector3 perp = Vector3.Cross(axis, Vector3.up);                  // LookRotation(perp): lokal +X = axis
            float aspect = Mathf.Max(s.w, s.d) / Mathf.Max(.1f, Mathf.Min(s.w, s.d));
            var options = new List<Vector3>();
            if (s.d >= s.w || aspect < 1.25f) { options.Add(axis); options.Add(-axis); }   // lange Seite = lokal Z
            if (s.w > s.d || aspect < 1.25f) { options.Add(perp); options.Add(-perp); }    // lange Seite = lokal X

            if (!road.Nearest(c.x, c.z, 150f, out int idx, out _)) return Quaternion.LookRotation(options[0], Vector3.up);
            Vector3 toRoad = road.Samples[idx].pos - c; toRoad.y = 0f; toRoad.Normalize();
            Vector3 best = options[0]; float bestDot = float.MinValue;
            foreach (var f in options)
            {
                float dot = Vector3.Dot(FrontIsPositiveZ ? f : -f, toRoad);
                if (dot > bestDot) { bestDot = dot; best = f; }
            }
            return Quaternion.LookRotation(best, Vector3.up);
        }

        private static bool ClearOfRoad(RoadField road, List<int> scratch, Vector3 c, Vector3 right, Vector3 fwd, float hw, float hd)
        {
            return ClearOf(road, scratch, c, right, fwd, hw, hd, RoadClearance) &&
                   (Streets == null || ClearOf(Streets, scratch, c, right, fwd, hw, hd, 2.5f));
        }

        private static RoadField Streets;
        private static bool ClearOf(RoadField field, List<int> scratch, Vector3 c, Vector3 right, Vector3 fwd, float hw, float hd, float clearance)
        {
            float reach = Mathf.Sqrt(hw * hw + hd * hd) + RoadClearance;
            field.Query(c.x - reach, c.z - reach, c.x + reach, c.z + reach, scratch);
            foreach (int i in scratch)
            {
                Vector3 d = field.Samples[i].pos - c;
                float lx = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, right)) - hw);
                float lz = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, fwd)) - hd);
                if (lx * lx + lz * lz < clearance * clearance) return false;
            }
            return true;
        }

        private static List<Sized> MeasurePrefabs(List<GameObject> prefabs)
        {
            var result = new List<Sized>();
            foreach (var p in prefabs)
            {
                var tmp = (GameObject)PrefabUtility.InstantiatePrefab(p);
                tmp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Bounds b = WorldPlacement.BoundsOf(tmp);
                Object.DestroyImmediate(tmp);
                // Nur echte Gebäude: mind. 4 m hoch und 25 m² Grundfläche.
                if (b.size.y < 4f || b.size.x * b.size.z < 25f) continue;
                result.Add(new Sized { prefab = p, w = b.size.x, d = b.size.z, h = b.size.y, area = b.size.x * b.size.z,
                                       pivotToCenter = new Vector3(b.center.x, 0f, b.center.z) });
            }
            return result;
        }

        private static Sized NearestBySize(List<Sized> sizes, float targetArea, float targetAspect, float targetH)
        {
            Sized best = sizes[0]; float bestScore = float.MaxValue;
            foreach (var s in sizes)
            {
                float areaScore = Mathf.Abs(Mathf.Log(s.area / Mathf.Max(1f, targetArea)));
                float aspect = Mathf.Max(s.w, s.d) / Mathf.Max(1f, Mathf.Min(s.w, s.d));
                float aspectScore = Mathf.Abs(Mathf.Log(aspect / Mathf.Max(1f, targetAspect < 1f ? 1f / targetAspect : targetAspect))) * .5f;
                float heightScore = Mathf.Abs(Mathf.Log(s.h / targetH)) * .8f;
                float score = areaScore + aspectScore + heightScore;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            return best;
        }
    }
}
