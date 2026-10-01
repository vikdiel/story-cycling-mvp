using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StoryCycling.WorldGen.Editor
{
    // Baut die befahrbare Nordhoek-Szene aus der GPX:
    //   echtes Gelände (DEM) mit eingeschnittener Straße, Meer auf Meereshöhe,
    //   entdoppelte Straße mit Markierungen + Leitplanken, OSM-Gebäude/-Details,
    //   Vegetation in Bändern, Himmel, Nebel in Horizontfarbe, Color-Grading.
    // Voraussetzung: einmal 'Fetch DEM for Nordhoek' (und optional 'Fetch OSM for Nordhoek').
    public static class GpxSceneBuilder
    {
        private static string OutDir = "Assets/StoryCycling/GeneratedGpx";
        private static string MeshStorePath => OutDir + "/WorldMeshes.asset";
        private static RouteWorldConfig Cfg;
        private static int assetId;
        private static Mesh meshStore;

        // Licht-Stimmung: alle Werte stehen je Stimmung (Morning / Afternoon / EveningSun) in RouteWorldConfig.lighting.

        [MenuItem("Story Cycling/WorldGen/Build Nordhoek GPX Ride")]
        public static void BuildNordhoek() => Build(RouteWorldConfig.LoadOrCreateDefault());

        // Baut die Welt für eine beliebige Strecke (RouteWorldConfig).
        public static void Build(RouteWorldConfig cfg)
        {
            Cfg = cfg;
            string GpxPath = cfg.gpxPath, OsmPath = cfg.osmPath, ScenePath = cfg.scenePath;
            OutDir = "Assets/StoryCycling/Generated/" + cfg.routeName;
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode first.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(GpxPath)) throw new InvalidOperationException("GPX missing: " + GpxPath);
            if (!File.Exists(cfg.demPath))
                throw new InvalidOperationException("Geländedaten fehlen: erst 'Fetch DEM' für diese Strecke ausführen.");

            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            AssetDatabase.Refresh();
            assetId = 0;
            meshStore = null;
            OsmDetailPlacer.LastParked.Clear();
            AssetDatabase.DeleteAsset(MeshStorePath);
            AssetDatabase.DeleteAsset("Assets/StoryCycling/GeneratedGpx/NordhoekWorldMeshes.asset");   // Altlast (mehrere GB Text)

            try
            {
                Progress("GPX + Gelände laden", .02f);
                var pts = GpxParser.Parse(File.ReadAllText(GpxPath));
                var dem = DemGrid.Load(cfg.demPath);
                if (!dem.MatchesOrigin(pts[0]))
                    Debug.LogWarning("DEM wurde für einen anderen GPX-Start erzeugt — bitte 'Fetch DEM' neu ausführen.");
                OsmContext osm = File.Exists(OsmPath) ? OsmContext.Load(File.ReadAllText(OsmPath), pts[0]) : null;
                if (osm == null) Debug.LogWarning("OSM fehlt — erst 'Fetch OSM'. Gebäude/Details/Biome werden übersprungen.");

                // Fahrlinie: auf das OSM-Straßennetz gelegt (eine Straße für Hin/Rück, saubere Einmündungen)
                Progress("Route auf OSM-Straßennetz legen", .04f);
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
                // Straßennetz: Route + Querstraßen als ein Modell mit echten Kreuzungen (Fahrlinie liegt darauf)
                RoadNet net = null;
                List<Vector2[]> o2sJunctions = null;
                List<Vector3> driveLine = null;
                if (matched != null && cfg.useRoadNetwork)
                {
                    Progress("Straßennetz & Kreuzungen", .05f);
                    var centroids = new List<Vector2>(); foreach (var b in osm.Buildings) centroids.Add(b.centroid);
                    // Querstraßen auf das an die Route angepasste Höhenmodell setzen (wie das Gelände)
                    var demFix = new DemCorrection(dem, ele0, matched.Points);
                    net = RoadNet.Build(osm, matched.Points, (x, z) => dem.Sample(x, z) - ele0 - demFix.At(x, z),
                                        new System.Text.RegularExpressions.Regex(cfg.wideShoulderRoads), centroids,
                                        cfg.roadRules, cfg.WideShoulderZone(pts[0].Lat, pts[0].Lon));
                    // osm2streets-Fahrbahnflächen (optionales Zusatzwerkzeug, siehe Tools/osm2streets/README.md):
                    // robuster für Doppelfahrbahnen, "Dog-Leg"-Kreuzungen, Kreisverkehre mit Bypass-Spuren als
                    // unsere eigene Ecken-Konstruktion. Fehlt node/npm install, baut die Pipeline automatisch
                    // ohne weiter — nie blockierend.
                    if (cfg.useOsm2StreetsJunctions)
                    {
                        string o2sOut = cfg.Osm2StreetsPath;
                        if (Osm2StreetsGeometry.NeedsRefresh(cfg.gpxPath, cfg.osmPath, o2sOut) &&
                            Osm2StreetsGeometry.TryRun(cfg.gpxPath, cfg.osmPath, o2sOut, out string o2sMsg))
                            Debug.Log(o2sMsg);
                        o2sJunctions = Osm2StreetsGeometry.TryLoad(o2sOut);
                        Debug.Log(o2sJunctions != null
                            ? $"osm2streets-Kreuzungsflächen geladen: {o2sJunctions.Count} aus {System.IO.Path.GetFileName(o2sOut)}."
                            : "osm2streets-Geometrie nicht verfügbar — baue Fahrbahn/Kreuzungen mit der eigenen Flächenvereinigung.");
                    }
                    var onNet = RoadNetRoute.Build(net, matched.Points);
                    Debug.Log($"Straßennetz: {net.Segs.Count} Abschnitte, {net.Junctions.Count} Kreuzungen; Fahrlinie {onNet.OnNetShare:P0} auf dem Netz.");
                    if (onNet.OnNetShare < .9f) { Debug.LogWarning("Fahrlinie liegt zu wenig auf dem Netz — alter Straßenbau."); net = null; }
                    else
                    {
                        matched.Points = onNet.Points; matched.Lane = onNet.Lane; matched.Half = onNet.Half; matched.Inset = onNet.Inset;
                        // wo die Fahrlinie kein Netz hat (GPX abseits jeder OSM-Straße): Ersatzfahrbahn — nie ohne Straße
                        int fb = net.AddRouteFallback(onNet.Points, onNet.Half, onNet.Inset, onNet.OffNet);
                        if (fb > 0) Debug.Log($"Straßennetz: {fb} Ersatzfahrbahn(en) für Fahrlinien-Stücke ohne OSM-Straße.");
                        driveLine = onNet.Points;
                    }
                }
                if (matched != null)
                {
                    if (!cfg.edgeLinesOnNormalRoads)
                        for (int i = 0; i < matched.Inset.Count; i++) if (matched.Inset[i] < 1f) matched.Inset[i] = -1f;   // -1 = keine Randlinie
                    File.WriteAllText(cfg.BakedRoutePath, BakedRoute.Write(matched.Points, matched.Lane, matched.Half, matched.Inset));
                    spline.Define(matched.Points);
                    var cum = new float[matched.Points.Count];
                    for (int i = 1; i < cum.Length; i++) cum[i] = cum[i - 1] + Vector3.Distance(matched.Points[i - 1], matched.Points[i]);
                    road = new RoadField(spline, d => StyleAt(matched, cum, d / spline.Length * cum[cum.Length - 1]));
                }
                else
                {
                    if (File.Exists(cfg.BakedRoutePath)) File.Delete(cfg.BakedRoutePath);     // keine veraltete Route zur Laufzeit
                    spline.Define(RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts)));
                    road = new RoadField(spline);
                }
                AssetDatabase.Refresh();

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Transform world = new GameObject("World").transform;
                Func<string, Transform> Group = n => { var t = new GameObject(n).transform; t.SetParent(world, false); return t; };

                Progress("Straßen-Index", .06f);
                var terrain = new WorldTerrain(dem, road, ele0, osm);
                RouteHeightField.Terrain = terrain.HeightAt;

                // Querstraßen/Kreuzungen/Kreisverkehre VOR dem Gelände: sie schneiden sich mit ein.
                Progress("Querstraßen & Kreisverkehre", .08f);
                var streets = net != null ? StreetNetwork.FromNet(net, osm, terrain) : StreetNetwork.Build(osm, road, terrain);
                terrain.Streets = streets.Field;

                // Landbedeckung (ESA WorldCover) + OSM + Gelände -> Ökotope; Geländefarbe und Vegetation lesen dieselbe Karte.
                Progress("Landbedeckung & Ökotope", .09f);
                LandCoverGrid landCover = LoadLandCover(cfg, pts[0]);
                EcotopeMap eco = EcotopeMap.Build(terrain, osm, landCover);
                Debug.Log("Ökotope (Streifen 800 m um die Route): " + eco.CoverageText(800f) + (landCover == null ? " [ohne Landbedeckung: OSM + Gelände-Heuristik]" : " [mit ESA WorldCover]"));

                Progress("Gelände einfärben", .1f);
                Texture2D terrainTex = SaveTexture(terrain.BuildColorTexture(eco), OutDir + "/TerrainColors.png", false, 4096);
                Material terrainMat = Mat("GpxTerrain", Color.white, .06f, terrainTex);
                // Detailtextur (Bodenkorn, 6-m-Kachel) multipliziert über die 10-m-Farbtextur: im Nahbereich keine glatte Fläche mehr
                Texture2D detailTex = SaveTexture(TerrainPaint.BuildDetail(256), OutDir + "/TerrainDetail.png", true, 256);
                Vector2 ext = terrain.ColorTextureExtent;
                terrainMat.EnableKeyword("_DETAIL_MULX2");
                terrainMat.SetTexture("_DetailAlbedoMap", detailTex);
                terrainMat.SetTextureScale("_DetailAlbedoMap", new Vector2(ext.x / 6f, ext.y / 6f));
                terrainMat.SetFloat("_DetailAlbedoMapScale", 1f);
                EditorUtility.SetDirty(terrainMat);

                Progress("Gelände-Kacheln", .2f);
                terrain.BuildChunks(Group("Terrain"), terrainMat, SaveMesh);
                terrain.BuildWater(world, OceanMaterial(), SaveMesh);

                Progress("Straßen", .4f);
                Texture2D asphaltTex = SaveTexture(AsphaltTexture(), OutDir + "/Asphalt.png", true, 512);
                var roadMats = new RoadMaterials
                {
                    Asphalt = Mat("GpxAsphalt", Color.white, .18f, asphaltTex),
                    Shoulder = Mat("GpxShoulder", new Color(.62f, .45f, .33f), .05f),       // rotbrauner Schotter wie am Kap
                    Yellow = Mat("GpxLineYellow", new Color(.95f, .76f, .18f), .3f),
                    White = Mat("GpxLineWhite", new Color(.95f, .95f, .92f), .3f),
                    Rail = Mat("GpxGuardrail", new Color(.74f, .76f, .78f), .55f, null, .6f),
                    Sidewalk = Mat("GpxSidewalk", new Color(.72f, .71f, .68f), .08f),
                    IslandGrass = Mat("GpxIslandGrass", new Color(.33f, .50f, .22f), .05f),
                };
                Transform streetGroup = Group("Streets");
                RoadSurface.Result roadSurface = null;
                if (net != null)
                {
                    // EIN Generator für Route, Querstraßen und Kreuzungen; Leitplanken weiterhin entlang der Route
                    var surf = RoadNetMesher.Build(net, Group("Road"), roadMats, SaveMesh,
                        t => !Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("Nordhoek bauen", $"Straßenoberfläche {t:P0}", t),
                        o2sJunctions);
                    if (cfg.buildGalleries) GalleryBuilder.Build(net, (x, z) => dem.Sample(x, z) - ele0, Group("Galleries"), roadMats, SaveMesh);
                    roadSurface = surf;
                    WorldCheck.Run(net, driveLine, surf, osm);
                    new RoadMeshBuilder(road, terrain).Build(Group("Guardrails"), roadMats, streets, SaveMesh, railsOnly: true);
                    // Kreisverkehr-Inseln ergeben sich aus der vereinigten Fläche (Loch im Asphalt) -> kein Extra-Mesh
                    if (!RoadNetMesher.UseSurfaceUnion) StreetMeshBuilder.BuildIslandsOnly(streets, road, streetGroup, roadMats, SaveMesh);
                }
                else
                {
                    new RoadMeshBuilder(road, terrain).Build(Group("Road"), roadMats, streets, SaveMesh);
                    StreetMeshBuilder.Build(streets, terrain, road, streetGroup, roadMats, SaveMesh);
                }

                var occupied = new Occupancy();
                foreach (var isl in streets.Islands) occupied.Add(isl.Center.x, isl.Center.z, isl.Radius + 1f);
                Progress("Landmarks", .5f);
                PlaceLandmarks(spline, terrain, occupied, Group("Landmarks"));

                CarPaintSet carPaint = null;
                var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
                if (catalog == null) Debug.LogWarning("WorldGen-Katalog fehlt — erst 'Build Catalog from Synty'. Keine Gebäude/Vegetation.");
                else
                {
                    var assets = WorldAssets.From(catalog);
                    assets.Log();
                    // Autolack: Varianten des Atlas (nur Karosserie umgefärbt) für parkende UND fahrende Autos
                    carPaint = CarPaint.Build(OsmDetailPlacer.CarPrefabs(catalog),
                        (tex, name) => SaveTexture(tex, OutDir + "/" + name + ".png", false, 1024), m => Save(m));
                    if (osm != null)
                    {
                        Progress("Gebäude (OSM)", .58f);
                        PlaceBuildings(osm, terrain, catalog, road, occupied, Group("Buildings"));
                        Progress("Details (OSM)", .66f);
                        OsmDetailPlacer.Place(road, terrain, osm, catalog, assets, occupied, Group("StreetDetails"), net, carPaint: carPaint);
                        RoadSigns.Place(road, terrain, streets, catalog, occupied, Group("RoadSigns"));
                    }
                    Progress("Vegetation & Küste", .74f);
                    VegetationPlacer.PlaceAvenue(terrain, assets, occupied, Group("PalmAvenue"));
                    VegetationPlacer.PlaceIslands(streets, road, assets, streetGroup);
                    if (net != null) VegetationPlacer.PlaceMedianPalms(net, assets, occupied, Group("MedianPalms"));
                    Progress("Pflanzengruppen (Ökotope)", .82f);
                    var scatterCfg = new ScatterSettings
                    {
                        nearEnd = Cfg.vegetationNearDistance, midEnd = Cfg.vegetationMidDistance, farEnd = Cfg.farTreeDistance,
                        farSilhouettes = Cfg.farTreeDistance > Cfg.vegetationMidDistance, maxFar = Cfg.farTreeMax, density = Cfg.vegetationDensity,
                    };
                    var scatter = VegetationScatter.Run(terrain, eco, occupied, scatterCfg);
                    Debug.Log(scatter.Summary());
                    VegetationBuilder.Build(scatter, assets, Group("Vegetation"), scatterCfg, SaveMesh, m => Save(m));
                    VegetationPlacer.PlaceBirds(terrain, assets, Group("Birds"));
                    VegetationPlacer.PlaceClouds(terrain, assets, Group("Clouds"));
                }

                Progress("Licht, Himmel, Grading", .9f);
                var look = cfg.ActiveLighting;
                Debug.Log($"Licht: Stimmung '{look.name}' (Sonne {look.sunElevation:0}° hoch, Azimut {look.sunAzimuth:0}°, {look.sunTemperature:0} K).");
                Lighting(look);

                StoryCycling.Editor.CapeCrownSceneBuilder.Generated = OutDir;
                Transform rider = StoryCycling.Editor.CapeCrownSceneBuilder.AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
                Progress("Verkehr", .93f);
                TrafficSceneBuilder.Build(cfg, net, roadSurface, catalog, carPaint, rider);
                GameObject camGo = new GameObject("Ride Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                Camera cam = camGo.GetComponent<Camera>();
                cam.fieldOfView = 58; cam.nearClipPlane = .3f; cam.farClipPlane = 7000; cam.allowHDR = false;
                cam.clearFlags = CameraClearFlags.Skybox;
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                // Wasser-Shader (Uferschaum, Tiefenfarbe) braucht die Depth-Texture — nur für diese Kamera.
                camData.requiresDepthOption = CameraOverrideOption.On;
                ColorGrade(look);

                var director = new GameObject("Gpx Ride Director").AddComponent<GpxRideController>();
                SerializedObject data = new SerializedObject(director);
                var gpxProp = data.FindProperty("gpxRelPath");
                if (gpxProp != null) gpxProp.stringValue = GpxPath.Replace("Assets/StreamingAssets/", "");
                data.FindProperty("rider").objectReferenceValue = rider;
                data.FindProperty("rideCamera").objectReferenceValue = cam.transform;
                data.FindProperty("cyclistAnimation").objectReferenceValue = animation;
                var array = data.FindProperty("wheels");
                array.arraySize = wheels.Length;
                for (int i = 0; i < wheels.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = wheels[i];
                data.ApplyModifiedPropertiesWithoutUndo();

                var hud = director.gameObject.AddComponent<GpxTestHud>();
                var hudData = new SerializedObject(hud);
                hudData.FindProperty("ride").objectReferenceValue = director;
                hudData.ApplyModifiedPropertiesWithoutUndo();

                AssetDatabase.SaveAssets();
                EditorSceneManager.SaveScene(scene, ScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                Debug.Log(ScenePath + " saved. GPX ride ready — length " + (spline.Length / 1000f).ToString("0.00") + " km.");
            }
            finally
            {
                RouteHeightField.Terrain = null;
                EditorUtility.ClearProgressBar();
            }
        }

        // Landbedeckung der Strecke; fehlt/passt sie nicht, baut die Welt mit OSM + Gelände-Heuristik weiter.
        private static LandCoverGrid LoadLandCover(RouteWorldConfig cfg, GeoPoint origin)
        {
            string path = cfg.LandCoverFile;
            if (!File.Exists(path))
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

        private static void Progress(string what, float t)
        {
            if (!Application.isBatchMode) EditorUtility.DisplayProgressBar("Nordhoek bauen", what, t);
        }

        // ------------------------------------------------------------------ Landmarks
        private static void PlaceLandmarks(RouteSpline spline, WorldTerrain terrain, Occupancy occupied, Transform parent)
        {
            // Ungefähre Distanzen entlang der Route; mit echten km-Markern verfeinerbar.
            var landmarks = new (string name, float frac)[]
            {
                ("Landmark_HoutBayHarbour", .28f), ("Landmark_EastFort", .36f), ("Landmark_ChapmansLookout", .40f),
                ("Landmark_KakapoShipwreck", .52f), ("Landmark_SlangkopLighthouse", .60f), ("Landmark_ConstantiaManor", .88f)
            };
            foreach (var lm in landmarks)
            {
                string path = "Assets/WorldAssets/Landmarks/" + lm.name + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.LogWarning("Landmark missing: " + path); continue; }
                float d = lm.frac * spline.Length;
                Vector3 p = spline.SamplePosition(d);
                Vector3 t = spline.SampleTangent(d); t.y = 0f; t.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, t).normalized;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
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

        // ------------------------------------------------------------------ Licht & Stimmung
        // Sonne, Himmel, Umgebungslicht und Dunst aus einer Stimmung (öffentlich für den Headless-Test).
        public static void Lighting(LightingSettings s)
        {
            Vector3 toSun = s.ToSun();

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            // Sonnenfarbe über die Farbtemperatur (URP nutzt sie, GraphicsSettings.lightsUseColorTemperature); 'color' ist nur ein
            // zusätzlicher Filter (Weiß = keiner) - nicht doppelt einfärben.
            sun.useColorTemperature = true; sun.colorTemperature = s.sunTemperature; sun.color = s.sunFilter;
            sun.intensity = s.sunIntensity;
            sun.shadows = LightShadows.Soft; sun.shadowStrength = s.shadowStrength;
            sun.transform.rotation = Quaternion.LookRotation(-toSun, Vector3.up);
            RenderSettings.sun = sun;

            Shader skyShader = Shader.Find("CapeCrown/CoastalSky");
            if (skyShader != null)
            {
                var sky = new Material(skyShader) { name = "GpxSky" };
                sky.SetColor("_Zenith", s.skyZenith);
                sky.SetColor("_Horizon", s.skyHorizon);
                sky.SetColor("_SunGlow", s.sunGlow);
                sky.SetFloat("_SunGlowStrength", s.sunGlowStrength);
                sky.SetVector("_SunDirection", new Vector4(toSun.x, toSun.y, toSun.z, 0f));   // dieselbe Richtung wie das Directional Light
                RenderSettings.skybox = Save(sky);
            }
            else Debug.LogWarning("CoastalSky-Shader fehlt — Standard-Skybox.");

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = s.ambientSky;
            RenderSettings.ambientEquatorColor = s.ambientEquator;
            RenderSettings.ambientGroundColor = s.ambientGround;

            // Warmer Dunst: Nebel = Horizontfarbe -> Ferne verschmilzt mit dem Himmel statt harter Kante.
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = s.fogStart; RenderSettings.fogEndDistance = s.fogEnd;
            RenderSettings.fogColor = s.fogColor;
        }

        // Post-Processing: ACES, Belichtung, wärmerer Weißabgleich, warme Lichter / kühle Schatten, Sättigung, Bloom, Vignette.
        // Alle Overrides werden als Unter-Assets im Profil gespeichert (sonst sind sie nach dem Szenen-Reload leer).
        public static void ColorGrade(LightingSettings s)
        {
            var volume = new GameObject("Colour grade").AddComponent<Volume>();
            volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "GpxGrade";

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var balance = profile.Add<WhiteBalance>(true);
            balance.temperature.Override(s.whiteBalanceTemperature); balance.tint.Override(s.whiteBalanceTint);
            var split = profile.Add<SplitToning>(true);
            split.shadows.Override(s.shadowTint); split.highlights.Override(s.highlightTint); split.balance.Override(s.splitBalance);
            var grade = profile.Add<ColorAdjustments>(true);
            grade.postExposure.Override(s.postExposure); grade.contrast.Override(s.contrast); grade.saturation.Override(s.saturation);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(s.bloomIntensity); bloom.threshold.Override(s.bloomThreshold);
            bloom.scatter.Override(s.bloomScatter); bloom.tint.Override(s.bloomTint);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(s.vignetteIntensity); vignette.smoothness.Override(s.vignetteSmoothness);

            string profilePath = OutDir + "/GpxGrade.asset";
            AssetDatabase.DeleteAsset(profilePath);
            AssetDatabase.CreateAsset(profile, profilePath);
            AssetDatabase.AddObjectToAsset(tone, profile);
            AssetDatabase.AddObjectToAsset(balance, profile);
            AssetDatabase.AddObjectToAsset(split, profile);
            AssetDatabase.AddObjectToAsset(grade, profile);
            AssetDatabase.AddObjectToAsset(bloom, profile);
            AssetDatabase.AddObjectToAsset(vignette, profile);
            volume.sharedProfile = profile;
        }

        // Gebäude-Modus:
        //   Synty      = Häuser aus dem PolygonCity-Baukasten (Etagen/Ecken/Türen/Läden/Dach) nach OSM-Grundriss
        //   Procedural = eigene verputzte Kap-Häuser aus den OSM-Grundrissen
        //   Offices    = alte Variante mit fertigen Synty-Bürotürmen
        private enum BuildingMode { Synty, Procedural, Offices }
        private const BuildingMode Buildings = BuildingMode.Synty;

        // Erste Reihe an der Route (≤ 55 m): gemischt Synty-Baukasten (~70 %) und verputzte Villen.
        // Dahinter: einfache prozedurale Häuser aus OSM-Grundrissen, dann Hintergrund-Füllung der Wohngebiete.
        private static float FrontRow => Cfg != null ? Cfg.frontRowDistance : 55f;

        private static void PlaceBuildings(OsmContext osm, WorldTerrain terrain, AssetCatalog catalog, RoadField road,
                                           Occupancy occupied, Transform parent)
        {
            if (Buildings == BuildingMode.Offices) { OsmBuildingPlacer.Place(road, terrain, osm, catalog, occupied, parent); return; }
            var mats = HouseMaterials();
            var built = new HashSet<OsmContext.Building>();
            // Große/hohe Gebäude (Wohntürme, Geschäftshäuser) teils als Synty-Glas-/Bürobauten: die Mischung macht's
            System.Func<OsmContext.Building, bool> tower = b =>
                (b.heightTagged && b.heightM >= 12f || Mathf.Abs(b.area) >= 450f &&
                 (b.kind == "apartments" || b.kind == "commercial" || b.kind == "office" || b.kind == "retail" || b.kind == "hotel")) &&
                HashPercent(b.centroid + Vector2.one * 3.7f) < Cfg.glassTowerShare;
            OsmBuildingPlacer.Place(road, terrain, osm, catalog, occupied, parent, tower, built);
            if (Buildings == BuildingMode.Synty)
            {
                var kit = SyntyModularBuildings.LoadKit(catalog);
                if (kit.Complete)
                {
                    System.Func<OsmContext.Building, bool> frontRow = b => !built.Contains(b) &&
                        road.Distance(b.centroid.x, b.centroid.y, FrontRow + 1f) <= FrontRow && HashPercent(b.centroid) < Cfg.syntyModularShare;
                    SyntyModularBuildings.Build(osm, terrain, occupied, kit, mats.Plinth,
                                                Mat("HouseFar", new Color(.80f, .70f, .60f), .05f), parent, SaveMesh, frontRow, built);
                }
                else Debug.LogWarning("PolygonCity-Baukasten unvollständig im Katalog — nur prozedurale Häuser.");
            }
            ProceduralHouses.Build(osm, terrain, occupied, parent, mats, SaveMesh, b => !built.Contains(b));
            if (Cfg.backgroundFill) ProceduralHouses.BuildFill(terrain, occupied, parent, mats, SaveMesh);
        }

        private static int HashPercent(Vector2 c)
        {
            unchecked { uint h = (uint)Mathf.RoundToInt(c.x * 7f) * 2654435761u ^ (uint)Mathf.RoundToInt(c.y * 7f) * 40503u; h ^= h >> 15; return (int)(h % 100u); }
        }

        private static ProceduralHouses.Materials HouseMaterials()
        {
            Texture2D facade = SaveTexture(ProceduralHouses.FacadeTexture(), OutDir + "/Facade.png", true, 256);
            return new ProceduralHouses.Materials
            {
                Walls = new[]
                {
                    Mat("HouseWhite", new Color(.96f, .95f, .92f), .08f, facade),
                    Mat("HouseCream", new Color(.95f, .89f, .76f), .08f, facade),
                    Mat("HouseGrey", new Color(.84f, .84f, .82f), .08f, facade),
                    Mat("HouseSand", new Color(.90f, .82f, .68f), .08f, facade),
                },
                RoofTile = Mat("RoofTerracotta", new Color(.68f, .34f, .23f), .12f),
                RoofDark = Mat("RoofCharcoal", new Color(.28f, .29f, .31f), .15f),
                RoofFlat = Mat("RoofFlat", new Color(.66f, .66f, .64f), .05f),
                Plinth = Mat("HousePlinth", new Color(.60f, .58f, .54f), .05f),
            };
        }

        // ------------------------------------------------------------------ Meer
        // Wasser-Shader aus POLYGON Nature Biomes (Wellen, Uferschaum, Tiefenfarbe); Farben ans
        // kühlere Atlantikwasser am Kap angepasst. Ohne Pack: schlichtes URP-Lit-Wasser.
        private static Material OceanMaterial()
        {
            Material source = null;
            foreach (string guid in AssetDatabase.FindAssets("Water_Ocean_Day t:Material"))
            {
                source = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (source != null) break;
            }
            if (source == null)
            {
                Debug.LogWarning("Water_Ocean_Day (Nature Biomes) nicht gefunden — einfaches Wasser.");
                return Mat("GpxOcean", new Color(.05f, .33f, .45f), .82f);
            }
            var mat = new Material(source) { name = "GpxOceanCape" };
            SetColorIfPresent(mat, "_Very_Deep_Color", new Color(.03f, .25f, .38f));
            SetColorIfPresent(mat, "_Water_Very_Deep_Color", new Color(.02f, .22f, .34f));
            SetColorIfPresent(mat, "_Distant_Water_Color", new Color(.02f, .16f, .28f));
            SetColorIfPresent(mat, "_Deep_Color", new Color(.10f, .42f, .48f));
            return Save(mat);
        }

        private static void SetColorIfPresent(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
        }

        // ------------------------------------------------------------------ Assets
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

        private static Texture2D SaveTexture(Texture2D tex, string path, bool repeat, int maxSize)
        {
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = repeat ? 4 : 1;
            importer.maxTextureSize = maxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Alle generierten Meshes als Unter-Assets EINER Datei (statt hunderter gpx-XXX.asset).
        private static Mesh SaveMesh(Mesh mesh)
        {
            if (meshStore == null)
            {
                meshStore = new Mesh { name = "NordhoekWorldMeshes" };
                AssetDatabase.CreateAsset(meshStore, MeshStorePath);
            }
            AssetDatabase.AddObjectToAsset(mesh, meshStore);
            return mesh;
        }

        private static Material Mat(string name, Color color, float smoothness, Texture2D baseMap = null, float metallic = 0f)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable.");
            var mat = new Material(shader) { name = name, color = color };
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            if (baseMap != null) { mat.SetTexture("_BaseMap", baseMap); mat.mainTexture = baseMap; }
            return Save(mat);
        }

        private static T Save<T>(T asset) where T : UnityEngine.Object
        {
            string extension = asset is Material ? "mat" : "asset";
            string path = OutDir + "/gpx-" + (assetId++).ToString("D3") + "." + extension;
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(asset, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(asset);
                return existing;
            }
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
