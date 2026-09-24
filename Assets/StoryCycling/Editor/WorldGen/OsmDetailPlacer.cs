using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Places OSM point features (signs, lamps, benches, bins, hydrants, trees, rocks),
    // barrier lines (fence/hedge/wall) and parking-lot cars — each resolved to a Synty
    // prefab by NAME KEYWORD against the catalog. Missing keywords are logged once as gaps.
    public static class OsmDetailPlacer
    {
        // OSM-kind -> Prefab-Namens-Keywords (erste Gruppe mit Treffer gewinnt).
        // Namen aus dem tatsächlichen Synty-Bestand (PolygonCity + PolygonGeneric).
        private static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            { "tree",            new[] { "Env_Tree", "Tree_Pine" } },
            { "rock",            new[] { "Env_Rock", "Rock" } },
            { "traffic_signals", new[] { "TrafficLight" } },
            { "street_lamp",     new[] { "LightPole_Lights", "LightPole" } },
            { "bus_stop",        new[] { "BusStop" } },
            { "bench",           new[] { "ParkBench", "Bench" } },
            { "waste_basket",    new[] { "TrashCan", "Trashbin", "Trash" } },
            { "fire_hydrant",    new[] { "Hydrant" } },
            { "sign_stop",       new[] { "Sign_Stop" } },
            { "sign_give_way",   new[] { "Sign_GiveWay" } },
            { "parking_meter",   new[] { "ParkingMeter" } },
            { "mailbox",         new[] { "Mailbox" } },
            // Linien:
            { "fence",           new[] { "Env_Fence", "Fence" } },
            { "hedge",           new[] { "Bush_Large", "Bush", "Shrub" } },  // kein Hedge-Asset -> Busch-Reihe
            { "wall",            new[] { "Env_Fence", "Fence" } },           // kein Mauer-Asset -> Zaun
            // Parkplatz-Füllung:
            { "car",             new[] { "Veh_Car" } },
        };

        // Nie verwenden: Bodenkacheln, Weg-/Flussstücke, Bauteile, tote Bäume, Klippen.
        private static readonly System.Text.RegularExpressions.Regex Excluded = new System.Text.RegularExpressions.Regex(
            "Dead|Ground|Pebbles|_Part|Path|Divider|River|Cliff|Mountain|Skyline|Cloud",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static Transform Parent;

        // Naturelemente bekommen Zufallsdrehung/-skalierung, Möbel richten sich zur Straße aus.
        private static readonly HashSet<string> Nature = new HashSet<string> { "tree", "rock" };

        public static int Place(RouteSpline spline, OsmContext ctx, AssetCatalog catalog, Transform parent = null, int seed = 777)
        {
            Parent = parent;
            var ground = new RouteHeightField(spline, 5f);
            var rng = new System.Random(seed);
            var warned = new HashSet<string>();
            int placed = 0;

            // --- Punkte ---
            foreach (var pt in ctx.Points)
            {
                var prefab = Resolve(catalog, pt.kind, rng, warned);
                if (prefab == null) continue;
                float y = ground.HeightAt(pt.pos.x, pt.pos.y);
                Vector3 pos = new Vector3(pt.pos.x, y, pt.pos.y);
                Quaternion rot = Nature.Contains(pt.kind)
                    ? Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f)
                    : Quaternion.LookRotation(Flat(ground.ForwardAt(pt.pos.x, pt.pos.y)), Vector3.up);
                float scale = Nature.Contains(pt.kind) ? 0.85f + (float)rng.NextDouble() * 0.5f : 1f;
                Instantiate(prefab, pos, rot, scale, ground);
                placed++;
            }

            // --- Barrier-Linien ---
            foreach (var ln in ctx.Lines)
            {
                var prefab = Resolve(catalog, ln.kind, rng, warned);
                if (prefab == null) continue;
                float step = ln.kind == "hedge" ? 2.5f : 4f;
                placed += PlaceAlongLine(ln.pts, prefab, step, ground, rng);
            }

            // --- Parkplätze mit Autos füllen ---
            foreach (var area in ctx.Areas)
            {
                if (area.biome != "parking") continue;
                var prefab = Resolve(catalog, "car", rng, warned);
                if (prefab == null) break;
                placed += ScatterInArea(area.ring, prefab, 6f, ground, rng);
            }

            Debug.Log($"OSM-Details platziert: {placed} " +
                      $"(Punkte {ctx.Points.Count}, Linien {ctx.Lines.Count}, Flächen {ctx.Areas.Count}).");
            return placed;
        }

        // --- Prefab per Keyword aus dem Katalog auflösen ---
        private static GameObject Resolve(AssetCatalog catalog, string kind, System.Random rng, HashSet<string> warned)
        {
            if (!Map.TryGetValue(kind, out var keywords))
                return WarnGap(kind, warned);

            foreach (var kw in keywords)
            {
                var hits = new List<GameObject>();
                foreach (var e in catalog.entries)
                    if (e != null && e.prefab != null && !Excluded.IsMatch(e.prefab.name) &&
                        e.prefab.name.IndexOf(kw, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        hits.Add(e.prefab);
                if (hits.Count > 0) return hits[rng.Next(hits.Count)];
            }
            return WarnGap(kind, warned);
        }

        private static GameObject WarnGap(string kind, HashSet<string> warned)
        {
            if (warned.Add(kind)) Debug.LogWarning($"OSM-Detail: kein Asset für '{kind}' — übersprungen (Lücke).");
            return null;
        }

        // --- Platzierungs-Modi ---
        private static int PlaceAlongLine(List<Vector2> pts, GameObject prefab, float step, RouteHeightField ground, System.Random rng)
        {
            int placed = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                Vector2 dir = (b - a) / Mathf.Max(len, 1e-4f);
                for (float d = 0; d < len; d += step)
                {
                    Vector2 p = a + dir * d;
                    float y = ground.HeightAt(p.x, p.y);
                    var rot = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y), Vector3.up);
                    Instantiate(prefab, new Vector3(p.x, y, p.y), rot, 1f, ground);
                    placed++;
                }
            }
            return placed;
        }

        private static int ScatterInArea(List<Vector2> ring, GameObject prefab, float step, RouteHeightField ground, System.Random rng)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in ring) { minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x); minZ = Mathf.Min(minZ, v.y); maxZ = Mathf.Max(maxZ, v.y); }
            int placed = 0, cap = 40;
            for (float x = minX; x <= maxX && placed < cap; x += step)
            for (float z = minZ; z <= maxZ && placed < cap; z += step)
            {
                var p = new Vector2(x + (float)(rng.NextDouble() - 0.5) * step * 0.4f,
                                    z + (float)(rng.NextDouble() - 0.5) * step * 0.4f);
                if (!OsmContext.PointInPolygon(p, ring)) continue;
                float y = ground.HeightAt(p.x, p.y);
                var rot = Quaternion.Euler(0f, (rng.Next(2) == 0 ? 0f : 180f) + (float)(rng.NextDouble() - 0.5) * 8f, 0f);
                Instantiate(prefab, new Vector3(p.x, y, p.y), rot, 1f, ground);
                placed++;
            }
            return placed;
        }

        private static void Instantiate(GameObject prefab, Vector3 pos, Quaternion rot, float scale, RouteHeightField ground)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Parent);
            go.name = prefab.name;
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = Vector3.one * scale;
            // Boden-Anker: tiefsten Renderer-Punkt auf y setzen.
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                go.transform.position += Vector3.up * (pos.y - b.min.y);
            }
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized; }
    }
}
