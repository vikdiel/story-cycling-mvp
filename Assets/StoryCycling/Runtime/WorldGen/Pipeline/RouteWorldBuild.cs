using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace StoryCycling.WorldGen.Editor
{
    // Wohin die erzeugten Meshes/Texturen/Materialien gehen: im Editor als Assets (Szene wird gespeichert), auf dem Gerät nur in den Speicher.
    public interface IWorldSink
    {
        Mesh Mesh(Mesh mesh);
        Texture2D Texture(Texture2D tex, string name, bool repeat, int maxSize);
        T Asset<T>(T asset) where T : UnityEngine.Object;
        Material NewLit();                        // leeres URP-Lit-Material
        Material OceanSource();                   // Wasser-Material (POLYGON Nature Biomes) oder null
        GameObject Landmark(string name);         // Landmark-Prefab oder null
        void Progress(string what, float t);
    }

    // Rohdaten einer Strecke (Dateipfade): Editor = Projektpfade, Gerät = StreamingAssets/WorldGen/<Route>
    public sealed class RouteWorldInputs
    {
        public string Gpx, Osm, Dem, LandCover;
        public List<Vector2[]> Osm2StreetsJunctions;  // optional (Editor-Werkzeug)
    }

    public sealed class RouteWorldResult
    {
        public Transform World;
        public RoadNet Net;
        public RoadSurface.Result Surface;
        public RouteSpline Spline;
        public string BakedRoute;                        // Fahrlinie auf dem OSM-Netz (BakedRoute-Format), null = GPX-Linie
        public CarPaintSet CarPaint;
        public readonly List<KeyValuePair<string, double>> Timings = new List<KeyValuePair<string, double>>();
        public double TotalSeconds;
    }

    // Baut die Welt einer Strecke (Gelände, Straßen, Gebäude, Details, Vegetation) — derselbe Code im Editor und auf dem Gerät.
    // Als Iterator: nach jedem großen Schritt 'yield' (Gerät: Fortschritt anzeigen; Editor: einfach durchlaufen).
    // Licht, Fahrer, Kamera, Menü und Verkehrs-Pool gehören zur Szene und entstehen außerhalb (GpxSceneBuilder / RuntimeWorldBuilder).
    public static class RouteWorldBuild
    {
        public static IEnumerator Run(RouteWorldConfig cfg, RouteWorldInputs input, AssetCatalog catalog, IWorldSink sink, RouteWorldResult result, bool checks)
        {
            var total = Stopwatch.StartNew(); var step = Stopwatch.StartNew(); string stepName = null;
            Action<string, float> Step = (what, t) =>
            {
                if (stepName != null) result.Timings.Add(new KeyValuePair<string, double>(stepName, step.Elapsed.TotalSeconds));
                stepName = what; step.Restart();
                if (what != null) sink.Progress(what, t);
            };
            OsmDetailPlacer.LastParked.Clear();
            try
            {
                Step("GPX + Gelände laden", .02f);
                yield return null;
                var pts = GpxParser.Parse(System.IO.File.ReadAllText(input.Gpx));
                var dem = DemGrid.Load(input.Dem);
                if (!dem.MatchesOrigin(pts[0])) Debug.LogWarning("DEM wurde für einen anderen GPX-Start erzeugt — bitte 'Fetch DEM' neu ausführen.");
                OsmContext osm = !string.IsNullOrEmpty(input.Osm) && System.IO.File.Exists(input.Osm) ? OsmContext.Load(System.IO.File.ReadAllText(input.Osm), pts[0]) : null;
                if (osm == null) Debug.LogWarning("OSM fehlt — Gebäude/Details/Biome werden übersprungen.");

                // Fahrlinie: auf das OSM-Straßennetz gelegt (eine Straße für Hin/Rück, saubere Einmündungen)
                Step("Route auf OSM-Straßennetz legen", .04f);
                yield return null;
                float ele0 = (float)pts[0].Ele, seaY = -ele0 + WorldTerrain.SeaLevelOffset;
                RouteMatcher.Result matched = null;
                if (cfg.useOsmRoadNetwork && osm != null && osm.Streets.Count > 0)
                {
                    double lat0 = pts[0].Lat * Math.PI / 180.0, lon0 = pts[0].Lon * Math.PI / 180.0, Re = 6371000.0;
                    Func<float, float, bool> inZone = (x, z) =>
                    {
                        if (cfg.wideShoulderZones == null || cfg.wideShoulderZones.Length == 0) return true;
                        double lat = (lat0 + z / Re) * 180.0 / Math.PI, lon = (lon0 + x / (Re * Math.Cos(lat0))) * 180.0 / Math.PI;
                        foreach (var zb in cfg.wideShoulderZones)
                            if (lat >= zb.minLat && lat <= zb.maxLat && lon >= zb.minLon && lon <= zb.maxLon) return true;
                        return false;
                    };
                    matched = RouteMatcher.Match(GpxParser.ProjectToLocalMeters(pts), osm, (x, z) => dem.Sample(x, z) - ele0, seaY,
                                                 new System.Text.RegularExpressions.Regex(cfg.wideShoulderRoads), inZone);
                    Debug.Log($"Map-Matching: {matched.MatchedShare:P0} der GPX-Spur auf OSM-Straßen, {matched.WaysUsed} Wege, {matched.Points.Count} Punkte.");
                    if (matched.MatchedShare < .85f || matched.Points.Count < 10)
                    { Debug.LogWarning("Map-Matching unvollständig — nutze die GPX-Linie."); matched = null; }
                }
                var spline = new RouteSpline();
                RoadField road;
                RoadNet net = null;
                List<Vector3> driveLine = null;
                if (matched != null && cfg.useRoadNetwork)
                {
                    Step("Straßennetz & Kreuzungen", .05f);
                    yield return null;
                    var centroids = new List<Vector2>(); foreach (var b in osm.Buildings) centroids.Add(b.centroid);
                    var demFix = new DemCorrection(dem, ele0, matched.Points);
                    net = RoadNet.Build(osm, matched.Points, (x, z) => dem.Sample(x, z) - ele0 - demFix.At(x, z),
                                        new System.Text.RegularExpressions.Regex(cfg.wideShoulderRoads), centroids,
                                        cfg.roadRules, cfg.WideShoulderZone(pts[0].Lat, pts[0].Lon));
                    var onNet = RoadNetRoute.Build(net, matched.Points);
                    Debug.Log($"Straßennetz: {net.Segs.Count} Abschnitte, {net.Junctions.Count} Kreuzungen; Fahrlinie {onNet.OnNetShare:P0} auf dem Netz.");
                    if (onNet.OnNetShare < .9f) { Debug.LogWarning("Fahrlinie liegt zu wenig auf dem Netz — alter Straßenbau."); net = null; }
                    else
                    {
                        matched.Points = onNet.Points; matched.Lane = onNet.Lane; matched.Half = onNet.Half; matched.Inset = onNet.Inset;
                        int fb = net.AddRouteFallback(onNet.Points, onNet.Half, onNet.Inset, onNet.OffNet);
                        if (fb > 0) Debug.Log($"Straßennetz: {fb} Ersatzfahrbahn(en) für Fahrlinien-Stücke ohne OSM-Straße.");
                        driveLine = onNet.Points;
                    }
                }
                if (matched != null)
                {
                    if (!cfg.edgeLinesOnNormalRoads)
                        for (int i = 0; i < matched.Inset.Count; i++) if (matched.Inset[i] < 1f) matched.Inset[i] = -1f;   // -1 = keine Randlinie
                    result.BakedRoute = BakedRoute.Write(matched.Points, matched.Lane, matched.Half, matched.Inset);
                    spline.Define(matched.Points);
                    var cum = new float[matched.Points.Count];
                    for (int i = 1; i < cum.Length; i++) cum[i] = cum[i - 1] + Vector3.Distance(matched.Points[i - 1], matched.Points[i]);
                    road = new RoadField(spline, d => StyleAt(matched, cum, d / spline.Length * cum[cum.Length - 1]));
                }
                else
                {
                    result.BakedRoute = null;
                    spline.Define(RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts)));
                    road = new RoadField(spline);
                }
                result.Spline = spline; result.Net = net;

                Transform world = new GameObject("World").transform;
                result.World = world;
                Func<string, Transform> Group = n => { var t = new GameObject(n).transform; t.SetParent(world, false); return t; };

                Step("Gelände & Querstraßen", .08f);
                yield return null;
                var terrain = new WorldTerrain(dem, road, ele0, osm);
                RouteHeightField.Terrain = terrain.HeightAt;
                // Querstraßen/Kreuzungen/Kreisverkehre VOR dem Gelände: sie schneiden sich mit ein.
                var streets = net != null ? StreetNetwork.FromNet(net, osm, terrain) : StreetNetwork.Build(osm, road, terrain);
                terrain.Streets = streets.Field;

                Step("Landbedeckung & Ökotope", .12f);
                yield return null;
                LandCoverGrid landCover = LoadLandCover(input.LandCover, pts[0]);
                EcotopeMap eco = EcotopeMap.Build(terrain, osm, landCover);
                Debug.Log("Ökotope (Streifen 800 m um die Route): " + eco.CoverageText(800f) + (landCover == null ? " [ohne Landbedeckung: OSM + Gelände-Heuristik]" : " [mit ESA WorldCover]"));

                Step("Gelände einfärben", .18f);
                yield return null;
                Texture2D terrainTex = sink.Texture(terrain.BuildColorTexture(eco), "TerrainColors", false, 4096);
                Material terrainMat = Mat(sink, "GpxTerrain", Color.white, .06f, terrainTex);
                // Detailtextur (Bodenkorn, 6-m-Kachel) multipliziert über die 10-m-Farbtextur: im Nahbereich keine glatte Fläche mehr
                Texture2D detailTex = sink.Texture(TerrainPaint.BuildDetail(256), "TerrainDetail", true, 256);
                Vector2 ext = terrain.ColorTextureExtent;
                terrainMat.EnableKeyword("_DETAIL_MULX2");
                terrainMat.SetTexture("_DetailAlbedoMap", detailTex);
                terrainMat.SetTextureScale("_DetailAlbedoMap", new Vector2(ext.x / 6f, ext.y / 6f));
                terrainMat.SetFloat("_DetailAlbedoMapScale", 1f);
                WorldSpawn.MarkDirty(terrainMat);

                Step("Gelände-Kacheln & Meer", .24f);
                yield return null;
                terrain.BuildChunks(Group("Terrain"), terrainMat, sink.Mesh);
                terrain.BuildWater(world, OceanMaterial(sink), sink.Mesh);

                Step("Straßen", .36f);
                yield return null;
                Texture2D asphaltTex = sink.Texture(AsphaltTexture(), "Asphalt", true, 512);
                var roadMats = new RoadMaterials
                {
                    Asphalt = Mat(sink, "GpxAsphalt", Color.white, .18f, asphaltTex),
                    Shoulder = Mat(sink, "GpxShoulder", new Color(.62f, .45f, .33f), .05f),       // rotbrauner Schotter wie am Kap
                    Yellow = Mat(sink, "GpxLineYellow", new Color(.95f, .76f, .18f), .3f),
                    White = Mat(sink, "GpxLineWhite", new Color(.95f, .95f, .92f), .3f),
                    Rail = Mat(sink, "GpxGuardrail", new Color(.74f, .76f, .78f), .55f, null, .6f),
                    Sidewalk = Mat(sink, "GpxSidewalk", new Color(.72f, .71f, .68f), .08f),
                    IslandGrass = Mat(sink, "GpxIslandGrass", new Color(.33f, .50f, .22f), .05f),
                };
                Transform streetGroup = Group("Streets");
                if (net != null)
                {
                    // EIN Generator für Route, Querstraßen und Kreuzungen; Leitplanken weiterhin entlang der Route
                    var surf = RoadNetMesher.Build(net, Group("Road"), roadMats, sink.Mesh,
                        t => { sink.Progress($"Straßenoberfläche {t:P0}", .36f + .12f * t); return false; },
                        cfg.useOsm2StreetsJunctions ? input.Osm2StreetsJunctions : null);
                    if (cfg.buildGalleries) GalleryBuilder.Build(net, (x, z) => dem.Sample(x, z) - ele0, Group("Galleries"), roadMats, sink.Mesh);
                    result.Surface = surf;
                    if (checks) WorldCheck.Run(net, driveLine, surf, osm);
                    new RoadMeshBuilder(road, terrain).Build(Group("Guardrails"), roadMats, streets, sink.Mesh, railsOnly: true);
                    if (!RoadNetMesher.UseSurfaceUnion) StreetMeshBuilder.BuildIslandsOnly(streets, road, streetGroup, roadMats, sink.Mesh);
                }
                else
                {
                    new RoadMeshBuilder(road, terrain).Build(Group("Road"), roadMats, streets, sink.Mesh);
                    StreetMeshBuilder.Build(streets, terrain, road, streetGroup, roadMats, sink.Mesh);
                }

                var occupied = new Occupancy();
                foreach (var isl in streets.Islands) occupied.Add(isl.Center.x, isl.Center.z, isl.Radius + 1f);
                Step("Landmarks", .5f);
                yield return null;
                PlaceLandmarks(sink, spline, terrain, occupied, Group("Landmarks"));

                if (catalog == null) Debug.LogWarning("WorldGen-Katalog fehlt — erst 'Build Catalog from Synty'. Keine Gebäude/Vegetation.");
                else
                {
                    var assets = WorldAssets.From(catalog);
                    assets.Log();
                    Step("Autolack", .54f);
                    yield return null;
                    // Autolack: Varianten des Atlas (nur Karosserie umgefärbt) für parkende UND fahrende Autos
                    result.CarPaint = CarPaint.Build(OsmDetailPlacer.CarPrefabs(catalog), (tex, name) => sink.Texture(tex, name, false, 1024), m => sink.Asset(m));
                    if (osm != null)
                    {
                        Step("Gebäude (OSM)", .58f);
                        yield return null;
                        PlaceBuildings(sink, cfg, osm, terrain, catalog, road, occupied, Group("Buildings"));
                        Step("Details (OSM)", .68f);
                        yield return null;
                        OsmDetailPlacer.Place(road, terrain, osm, catalog, assets, occupied, Group("StreetDetails"), net, carPaint: result.CarPaint);
                        RoadSigns.Place(road, terrain, streets, catalog, occupied, Group("RoadSigns"));
                    }
                    Step("Vegetation", .76f);
                    yield return null;
                    VegetationPlacer.PlaceAvenue(terrain, assets, occupied, Group("PalmAvenue"));
                    VegetationPlacer.PlaceIslands(streets, road, assets, streetGroup);
                    if (net != null) VegetationPlacer.PlaceMedianPalms(net, assets, occupied, Group("MedianPalms"));
                    var scatterCfg = new ScatterSettings
                    {
                        nearEnd = cfg.vegetationNearDistance, midEnd = cfg.vegetationMidDistance, farEnd = cfg.farTreeDistance,
                        farSilhouettes = cfg.farTreeDistance > cfg.vegetationMidDistance, maxFar = cfg.farTreeMax, density = cfg.vegetationDensity,
                    };
                    var scatter = VegetationScatter.Run(terrain, eco, occupied, scatterCfg);
                    Debug.Log(scatter.Summary());
                    Step("Vegetation aufbauen", .86f);
                    yield return null;
                    VegetationBuilder.Build(scatter, assets, Group("Vegetation"), scatterCfg, sink.Mesh, m => sink.Asset(m));
                    VegetationPlacer.PlaceBirds(terrain, assets, Group("Birds"));
                    VegetationPlacer.PlaceClouds(terrain, assets, Group("Clouds"));
                }
                Step(null, 1f);
            }
            finally
            {
                RouteHeightField.Terrain = null;
                result.TotalSeconds = total.Elapsed.TotalSeconds;
            }
        }

        // Landbedeckung der Strecke; fehlt/passt sie nicht, baut die Welt mit OSM + Gelände-Heuristik weiter.
        private static LandCoverGrid LoadLandCover(string path, GeoPoint origin)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                Debug.LogWarning($"Landbedeckung fehlt ({path}) — erst 'Story Cycling/WorldGen/Fetch Land Cover for Selected Route' ausführen. Bis dahin: OSM + Gelände-Heuristik (weniger genau).");
                return null;
            }
            try
            {
                var g = LandCoverGrid.Load(path);
                if (!g.MatchesOrigin(origin))
                {
                    Debug.LogWarning("Landbedeckung wurde für einen anderen GPX-Start erzeugt — bitte 'Fetch Land Cover' neu ausführen. Nutze OSM + Gelände-Heuristik.");
                    return null;
                }
                Debug.Log($"Landbedeckung geladen: {g.Width}×{g.Height} @ {g.Cell} m (ESA WorldCover 10 m 2021, CC BY 4.0).");
                return g;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Landbedeckung nicht lesbar (" + e.Message + ") — nutze OSM + Gelände-Heuristik.");
                return null;
            }
        }

        private static Vector2 StyleAt(RouteMatcher.Result r, float[] cum, float x)
        {
            int lo = 0, hi = cum.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (cum[mid] <= x) lo = mid; else hi = mid; }
            float t = cum[hi] > cum[lo] ? Mathf.Clamp01((x - cum[lo]) / (cum[hi] - cum[lo])) : 0f;
            return new Vector2(Mathf.Lerp(r.Half[lo], r.Half[hi], t), Mathf.Lerp(r.Inset[lo], r.Inset[hi], t));
        }

        // ------------------------------------------------------------------ Landmarks
        // Ungefähre Distanzen entlang der Route (Anteil der Länge); mit echten km-Markern verfeinerbar. Prefabs: Assets/WorldAssets/Landmarks/<Name>.prefab
        public static readonly (string name, float frac)[] Landmarks =
        {
            ("Landmark_HoutBayHarbour", .28f), ("Landmark_EastFort", .36f), ("Landmark_ChapmansLookout", .40f),
            ("Landmark_KakapoShipwreck", .52f), ("Landmark_SlangkopLighthouse", .60f), ("Landmark_ConstantiaManor", .88f)
        };
        private static void PlaceLandmarks(IWorldSink sink, RouteSpline spline, WorldTerrain terrain, Occupancy occupied, Transform parent)
        {
            foreach (var lm in Landmarks)
            {
                var prefab = sink.Landmark(lm.name);
                if (prefab == null) { Debug.LogWarning("Landmark missing: " + lm.name); continue; }
                float d = lm.frac * spline.Length;
                Vector3 p = spline.SamplePosition(d);
                Vector3 t = spline.SampleTangent(d); t.y = 0f; t.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, t).normalized;

                var go = WorldSpawn.Spawn(prefab, parent);
                go.name = lm.name;
                go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(t, Vector3.up));
                Bounds b = WorldPlacement.BoundsOf(go);
                float half = Mathf.Max(b.extents.x, b.extents.z);
                float offset = Mathf.Max(16f, half + RoadMeshBuilder.HalfWidth + 4f);

                // Seite mit dem flacheren Gelände (nicht in die Klippe, nicht ins Meer).
                Vector3 l = p - right * offset, r = p + right * offset;
                float dl = Mathf.Abs(terrain.HeightAt(l.x, l.z) - p.y) + (terrain.DemY(l.x, l.z) < terrain.SeaY + 1f ? 100f : 0f);
                float dr = Mathf.Abs(terrain.HeightAt(r.x, r.z) - p.y) + (terrain.DemY(r.x, r.z) < terrain.SeaY + 1f ? 100f : 0f);
                Vector3 target = dl < dr ? l : r;

                // Mitte der Bounds auf das Ziel schieben, Unterkante auf den tiefsten Geländepunkt.
                go.transform.position += new Vector3(target.x - b.center.x, 0f, target.z - b.center.z);
                b = WorldPlacement.BoundsOf(go);
                float ground = terrain.LowestUnder(b.center, Vector3.right, Vector3.forward, b.extents.x, b.extents.z);
                go.transform.position += Vector3.up * (ground - .2f - b.min.y);
                foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
                occupied.Add(b.center.x, b.center.z, half + 3f);
            }
        }

        // ------------------------------------------------------------------ Gebäude
        // Gebäude-Modus: Synty = Häuser aus dem PolygonCity-Baukasten nach OSM-Grundriss, Procedural = eigene verputzte Kap-Häuser, Offices = Synty-Bürotürme
        private enum BuildingMode { Synty, Procedural, Offices }
        private const BuildingMode Buildings = BuildingMode.Synty;

        private static void PlaceBuildings(IWorldSink sink, RouteWorldConfig cfg, OsmContext osm, WorldTerrain terrain, AssetCatalog catalog, RoadField road,
                                           Occupancy occupied, Transform parent)
        {
            if (Buildings == BuildingMode.Offices) { OsmBuildingPlacer.Place(road, terrain, osm, catalog, occupied, parent); return; }
            var mats = HouseMaterials(sink);
            var built = new HashSet<OsmContext.Building>();
            float frontRow = cfg.frontRowDistance;
            // Große/hohe Gebäude (Wohntürme, Geschäftshäuser) teils als Synty-Glas-/Bürobauten: die Mischung macht's
            Func<OsmContext.Building, bool> tower = b =>
                (b.heightTagged && b.heightM >= 12f || Mathf.Abs(b.area) >= 450f &&
                 (b.kind == "apartments" || b.kind == "commercial" || b.kind == "office" || b.kind == "retail" || b.kind == "hotel")) &&
                HashPercent(b.centroid + Vector2.one * 3.7f) < cfg.glassTowerShare;
            OsmBuildingPlacer.Place(road, terrain, osm, catalog, occupied, parent, tower, built);
            if (Buildings == BuildingMode.Synty)
            {
                var kit = SyntyModularBuildings.LoadKit(catalog);
                if (kit.Complete)
                {
                    Func<OsmContext.Building, bool> front = b => !built.Contains(b) &&
                        road.Distance(b.centroid.x, b.centroid.y, frontRow + 1f) <= frontRow && HashPercent(b.centroid) < cfg.syntyModularShare;
                    SyntyModularBuildings.Build(osm, terrain, occupied, kit, mats.Plinth,
                                                Mat(sink, "HouseFar", new Color(.80f, .70f, .60f), .05f), parent, sink.Mesh, front, built);
                }
                else Debug.LogWarning("PolygonCity-Baukasten unvollständig im Katalog — nur prozedurale Häuser.");
            }
            ProceduralHouses.Build(osm, terrain, occupied, parent, mats, sink.Mesh, b => !built.Contains(b));
            if (cfg.backgroundFill) ProceduralHouses.BuildFill(terrain, occupied, parent, mats, sink.Mesh);
        }

        private static int HashPercent(Vector2 c)
        {
            unchecked { uint h = (uint)Mathf.RoundToInt(c.x * 7f) * 2654435761u ^ (uint)Mathf.RoundToInt(c.y * 7f) * 40503u; h ^= h >> 15; return (int)(h % 100u); }
        }

        private static ProceduralHouses.Materials HouseMaterials(IWorldSink sink)
        {
            Texture2D facade = sink.Texture(ProceduralHouses.FacadeTexture(), "Facade", true, 256);
            return new ProceduralHouses.Materials
            {
                Walls = new[]
                {
                    Mat(sink, "HouseWhite", new Color(.96f, .95f, .92f), .08f, facade),
                    Mat(sink, "HouseCream", new Color(.95f, .89f, .76f), .08f, facade),
                    Mat(sink, "HouseGrey", new Color(.84f, .84f, .82f), .08f, facade),
                    Mat(sink, "HouseSand", new Color(.90f, .82f, .68f), .08f, facade),
                },
                RoofTile = Mat(sink, "RoofTerracotta", new Color(.68f, .34f, .23f), .12f),
                RoofDark = Mat(sink, "RoofCharcoal", new Color(.28f, .29f, .31f), .15f),
                RoofFlat = Mat(sink, "RoofFlat", new Color(.66f, .66f, .64f), .05f),
                Plinth = Mat(sink, "HousePlinth", new Color(.60f, .58f, .54f), .05f),
            };
        }

        // ------------------------------------------------------------------ Meer
        // Wasser-Shader aus POLYGON Nature Biomes (Wellen, Uferschaum, Tiefenfarbe), Farben ans kühlere Atlantikwasser angepasst; ohne Pack: URP-Lit-Wasser.
        private static Material OceanMaterial(IWorldSink sink)
        {
            Material source = sink.OceanSource();
            if (source == null)
            {
                Debug.LogWarning("Water_Ocean_Day (Nature Biomes) nicht gefunden — einfaches Wasser.");
                return Mat(sink, "GpxOcean", new Color(.05f, .33f, .45f), .82f);
            }
            var mat = new Material(source) { name = "GpxOceanCape" };
            SetColorIfPresent(mat, "_Very_Deep_Color", new Color(.03f, .25f, .38f));
            SetColorIfPresent(mat, "_Water_Very_Deep_Color", new Color(.02f, .22f, .34f));
            SetColorIfPresent(mat, "_Distant_Water_Color", new Color(.02f, .16f, .28f));
            SetColorIfPresent(mat, "_Deep_Color", new Color(.10f, .42f, .48f));
            return sink.Asset(mat);
        }

        private static void SetColorIfPresent(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
        }

        // ------------------------------------------------------------------ Texturen & Materialien
        private static Texture2D AsphaltTexture()
        {
            const int n = 256;
            var rng = new System.Random(99);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // kachelbar: Sinus-Wolken + Körnung
                float u = x / (float)n * Mathf.PI * 2f, v = y / (float)n * Mathf.PI * 2f;
                float cloud = .5f + .25f * Mathf.Sin(u * 2f + Mathf.Sin(v * 3f)) * Mathf.Cos(v * 2f + Mathf.Sin(u));
                float grain = (float)rng.NextDouble();
                float g = .20f + cloud * .05f + (grain - .5f) * .07f + (grain > .985f ? .12f : 0f);
                px[y * n + x] = new Color(g, g * 1.02f, g * 1.06f, 1f);
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Asphalt" };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        public static Material Mat(IWorldSink sink, string name, Color color, float smoothness, Texture2D baseMap = null, float metallic = 0f)
        {
            var mat = sink.NewLit();
            mat.name = name; mat.color = color;
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            if (baseMap != null) { mat.SetTexture("_BaseMap", baseMap); mat.mainTexture = baseMap; }
            return sink.Asset(mat);
        }
    }
}
