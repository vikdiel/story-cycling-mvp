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

        // Licht-Stimmung: später Nachmittag, Sonne im Nordwesten über dem Atlantik.
        private const float SunAzimuth = 300f, SunElevation = 36f;
        private static readonly Color Zenith = new Color(.24f, .52f, .80f);
        private static readonly Color Horizon = new Color(.87f, .86f, .80f);

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
                if (matched != null && cfg.useRoadNetwork)
                {
                    Progress("Straßennetz & Kreuzungen", .05f);
                    var centroids = new List<Vector2>(); foreach (var b in osm.Buildings) centroids.Add(b.centroid);
                    // Querstraßen auf das an die Route angepasste Höhenmodell setzen (wie das Gelände)
                    var demFix = new DemCorrection(dem, ele0, matched.Points);
                    net = RoadNet.Build(osm, matched.Points, (x, z) => dem.Sample(x, z) - ele0 - demFix.At(x, z),
                                        new System.Text.RegularExpressions.Regex(cfg.wideShoulderRoads), centroids);
                    var onNet = RoadNetRoute.Build(net, matched.Points);
                    Debug.Log($"Straßennetz: {net.Segs.Count} Abschnitte, {net.Junctions.Count} Kreuzungen; Fahrlinie {onNet.OnNetShare:P0} auf dem Netz.");
                    if (onNet.OnNetShare < .9f) { Debug.LogWarning("Fahrlinie liegt zu wenig auf dem Netz — alter Straßenbau."); net = null; }
                    else
                    {
                        matched.Points = onNet.Points; matched.Lane = onNet.Lane; matched.Half = onNet.Half; matched.Inset = onNet.Inset;
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

                Progress("Gelände einfärben", .1f);
                Texture2D terrainTex = SaveTexture(terrain.BuildColorTexture(), OutDir + "/TerrainColors.png", false, 4096);
                Material terrainMat = Mat("GpxTerrain", Color.white, .06f, terrainTex);

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
                if (net != null)
                {
                    // EIN Generator für Route, Querstraßen und Kreuzungen; Leitplanken weiterhin entlang der Route
                    RoadNetMesher.Build(net, Group("Road"), roadMats, SaveMesh,
                        t => !Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("Nordhoek bauen", $"Straßenoberfläche {t:P0}", t));
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

                var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
                if (catalog == null) Debug.LogWarning("WorldGen-Katalog fehlt — erst 'Build Catalog from Synty'. Keine Gebäude/Vegetation.");
                else
                {
                    var assets = WorldAssets.From(catalog);
                    assets.Log();
                    if (osm != null)
                    {
                        Progress("Gebäude (OSM)", .58f);
                        PlaceBuildings(osm, terrain, catalog, road, occupied, Group("Buildings"));
                        Progress("Details (OSM)", .66f);
                        OsmDetailPlacer.Place(road, terrain, osm, catalog, assets, occupied, Group("StreetDetails"), net);
                        RoadSigns.Place(road, terrain, streets, catalog, occupied, Group("RoadSigns"));
                    }
                    Progress("Vegetation & Küste", .74f);
                    VegetationPlacer.Place(terrain, assets, occupied, Group("Vegetation"));
                    VegetationPlacer.PlaceIslands(streets, road, assets, streetGroup);
                    if (net != null) VegetationPlacer.PlaceMedianPalms(net, assets, occupied, Group("MedianPalms"));
                    Progress("Hangvegetation", .82f);
                    SlopeVegetation.Place(terrain, assets, occupied, Group("SlopeVegetation"), Cfg.slopeVegetationDistance);
                    VegetationPlacer.PlaceBirds(terrain, assets, Group("Birds"));
                    VegetationPlacer.PlaceClouds(terrain, assets, Group("Clouds"));
                }

                Progress("Licht, Himmel, Grading", .9f);
                Lighting();

                StoryCycling.Editor.CapeCrownSceneBuilder.Generated = OutDir;
                Transform rider = StoryCycling.Editor.CapeCrownSceneBuilder.AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
                GameObject camGo = new GameObject("Ride Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                Camera cam = camGo.GetComponent<Camera>();
                cam.fieldOfView = 58; cam.nearClipPlane = .3f; cam.farClipPlane = 7000; cam.allowHDR = false;
                cam.clearFlags = CameraClearFlags.Skybox;
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                // Wasser-Shader (Uferschaum, Tiefenfarbe) braucht die Depth-Texture — nur für diese Kamera.
                camData.requiresDepthOption = CameraOverrideOption.On;
                ColorGrade();

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
        private static void Lighting()
        {
            float az = SunAzimuth * Mathf.Deg2Rad, el = SunElevation * Mathf.Deg2Rad;
            Vector3 toSun = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.color = new Color(1f, .91f, .77f); sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft; sun.shadowStrength = .8f;
            sun.transform.rotation = Quaternion.LookRotation(-toSun, Vector3.up);
            RenderSettings.sun = sun;

            Shader skyShader = Shader.Find("CapeCrown/CoastalSky");
            if (skyShader != null)
            {
                var sky = new Material(skyShader) { name = "GpxSky" };
                sky.SetColor("_Zenith", Zenith);
                sky.SetColor("_Horizon", Horizon);
                sky.SetVector("_SunDirection", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                RenderSettings.skybox = Save(sky);
            }
            else Debug.LogWarning("CoastalSky-Shader fehlt — Standard-Skybox.");

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.56f, .68f, .84f);
            RenderSettings.ambientEquatorColor = new Color(.74f, .72f, .66f);
            RenderSettings.ambientGroundColor = new Color(.36f, .34f, .29f);

            // Nebel = Horizontfarbe -> Ferne verschmilzt mit dem Himmel statt harter Kante.
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 350; RenderSettings.fogEndDistance = 6500;
            RenderSettings.fogColor = Horizon;
        }

        private static void ColorGrade()
        {
            var volume = new GameObject("Colour grade").AddComponent<Volume>();
            volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "GpxGrade";
            var grade = profile.Add<ColorAdjustments>(true);
            grade.postExposure.Override(.15f); grade.contrast.Override(12f); grade.saturation.Override(4f);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(.3f); bloom.threshold.Override(.95f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(.22f); vignette.smoothness.Override(.4f);
            string profilePath = OutDir + "/GpxGrade.asset";
            AssetDatabase.DeleteAsset(profilePath);
            AssetDatabase.CreateAsset(profile, profilePath);
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
