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
        private const string GpxPath = DemFetcher.GpxPath;
        private const string OsmPath = "Assets/StreamingAssets/Osm/Nordhoek.osm.xml";
        private const string ScenePath = "Assets/StoryCycling/Scenes/NordhoekGpxTest.unity";
        private const string OutDir = "Assets/StoryCycling/GeneratedGpx";
        private const string MeshStorePath = OutDir + "/NordhoekWorldMeshes.asset";
        private static int assetId;
        private static Mesh meshStore;

        // Licht-Stimmung: später Nachmittag, Sonne im Nordwesten über dem Atlantik.
        private const float SunAzimuth = 300f, SunElevation = 36f;
        private static readonly Color Zenith = new Color(.24f, .52f, .80f);
        private static readonly Color Horizon = new Color(.87f, .86f, .80f);

        [MenuItem("Story Cycling/WorldGen/Build Nordhoek GPX Ride")]
        public static void BuildNordhoek()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode first.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(GpxPath)) throw new InvalidOperationException("GPX missing: " + GpxPath);
            if (!File.Exists(DemFetcher.DemPath))
                throw new InvalidOperationException("Geländedaten fehlen: erst 'Story Cycling/WorldGen/Fetch DEM for Nordhoek' ausführen.");

            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            AssetDatabase.Refresh();
            assetId = 0;
            meshStore = null;
            AssetDatabase.DeleteAsset(MeshStorePath);
            // Alt-Meshes der früheren Ribbon-Version (gpx-XXX.asset) aufräumen; Materialien (.mat) bleiben.
            foreach (string old in Directory.GetFiles(OutDir, "gpx-*.asset"))
                AssetDatabase.DeleteAsset(old.Replace('\\', '/'));

            try
            {
                Progress("GPX + Gelände laden", .02f);
                var pts = GpxParser.Parse(File.ReadAllText(GpxPath));
                var local = RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts));
                var spline = new RouteSpline();
                spline.Define(local);

                var dem = DemGrid.Load(DemFetcher.DemPath);
                if (!dem.MatchesOrigin(pts[0]))
                    Debug.LogWarning("DEM wurde für einen anderen GPX-Start erzeugt — bitte 'Fetch DEM for Nordhoek' neu ausführen.");
                OsmContext osm = File.Exists(OsmPath) ? OsmContext.Load(File.ReadAllText(OsmPath), pts[0]) : null;
                if (osm == null) Debug.LogWarning("OSM fehlt — erst 'Fetch OSM for Nordhoek'. Gebäude/Details/Biome werden übersprungen.");

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Transform world = new GameObject("World").transform;
                Func<string, Transform> Group = n => { var t = new GameObject(n).transform; t.SetParent(world, false); return t; };

                Progress("Straßen-Index", .06f);
                var road = new RoadField(spline);
                var terrain = new WorldTerrain(dem, road, (float)pts[0].Ele, osm);
                RouteHeightField.Terrain = terrain.HeightAt;

                // Querstraßen/Kreuzungen/Kreisverkehre VOR dem Gelände: sie schneiden sich mit ein.
                Progress("Querstraßen & Kreisverkehre", .08f);
                var streets = StreetNetwork.Build(osm, road, terrain);
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
                    Shoulder = Mat("GpxShoulder", new Color(.55f, .51f, .44f), .05f),
                    Yellow = Mat("GpxLineYellow", new Color(.95f, .76f, .18f), .3f),
                    White = Mat("GpxLineWhite", new Color(.95f, .95f, .92f), .3f),
                    Rail = Mat("GpxGuardrail", new Color(.74f, .76f, .78f), .55f, null, .6f),
                    Sidewalk = Mat("GpxSidewalk", new Color(.72f, .71f, .68f), .08f),
                    IslandGrass = Mat("GpxIslandGrass", new Color(.33f, .50f, .22f), .05f),
                };
                new RoadMeshBuilder(road, terrain).Build(Group("Road"), roadMats, streets, SaveMesh);
                Transform streetGroup = Group("Streets");
                StreetMeshBuilder.Build(streets, terrain, road, streetGroup, roadMats, SaveMesh);

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
                        OsmDetailPlacer.Place(road, terrain, osm, catalog, assets, occupied, Group("StreetDetails"));
                    }
                    Progress("Vegetation & Küste", .74f);
                    VegetationPlacer.Place(terrain, assets, occupied, Group("Vegetation"));
                    VegetationPlacer.PlaceIslands(streets, road, assets, streetGroup);
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
            grade.postExposure.Override(.15f); grade.contrast.Override(10f); grade.saturation.Override(14f);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(.3f); bloom.threshold.Override(.95f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(.22f); vignette.smoothness.Override(.4f);
            const string profilePath = OutDir + "/GpxGrade.asset";
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

        private static void PlaceBuildings(OsmContext osm, WorldTerrain terrain, AssetCatalog catalog, RoadField road,
                                           Occupancy occupied, Transform parent)
        {
            if (Buildings == BuildingMode.Offices) { OsmBuildingPlacer.Place(road, terrain, osm, catalog, occupied, parent); return; }
            if (Buildings == BuildingMode.Synty)
            {
                var kit = SyntyModularBuildings.LoadKit(catalog);
                if (kit.Complete)
                {
                    SyntyModularBuildings.Build(osm, terrain, occupied, kit, Mat("HousePlinth", new Color(.60f, .58f, .54f), .05f),
                                                Mat("HouseFar", new Color(.74f, .62f, .52f), .05f), parent, SaveMesh);
                    return;
                }
                Debug.LogWarning("PolygonCity-Baukasten unvollständig im Katalog — prozedurale Häuser als Ersatz.");
            }
            ProceduralHouses.Build(osm, terrain, occupied, parent, HouseMaterials(), SaveMesh);
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
