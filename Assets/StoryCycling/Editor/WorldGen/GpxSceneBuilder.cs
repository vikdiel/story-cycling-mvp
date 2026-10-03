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

        // Baut die Szene einer beliebigen Strecke (RouteWorldConfig). Die Welt selbst (Gelände, Straßen, Gebäude, Vegetation) entsteht mit
        // RouteWorldBuild — hier im Editor (als Assets gespeichert) oder, mit 'buildWorldOnDevice', erst beim Start auf dem Gerät (RuntimeWorldBuilder).
        public static void Build(RouteWorldConfig cfg)
        {
            Cfg = cfg;
            string GpxPath = cfg.gpxPath, ScenePath = cfg.scenePath;
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
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
                RouteWorldResult world = null;
                if (!cfg.buildWorldOnDevice)
                {
                    var input = new RouteWorldInputs
                    {
                        Gpx = cfg.gpxPath, Osm = cfg.osmPath, Dem = cfg.demPath, LandCover = cfg.LandCoverFile,
                        Osm2StreetsJunctions = cfg.useOsm2StreetsJunctions ? Osm2Streets(cfg) : null,
                    };
                    world = new RouteWorldResult();
                    var it = RouteWorldBuild.Run(cfg, input, catalog, new EditorSink(), world, checks: true);
                    while (it.MoveNext()) { }
                    if (world.BakedRoute != null) File.WriteAllText(cfg.BakedRoutePath, world.BakedRoute);
                    else if (File.Exists(cfg.BakedRoutePath)) File.Delete(cfg.BakedRoutePath);     // keine veraltete Route zur Laufzeit
                    var sb = new System.Text.StringBuilder($"Weltbau im Editor: {world.TotalSeconds:0.0} s —");
                    foreach (var kv in world.Timings) sb.Append($" {kv.Key} {kv.Value:0.0} s ·");
                    Debug.Log(sb.ToString());
                }
                else PrepareDeviceBuild(cfg, catalog);
                AssetDatabase.Refresh();

                Progress("Licht, Himmel, Grading", .9f);
                var look = cfg.ActiveLighting;
                Debug.Log($"Licht: Stimmung '{look.name}' (Sonne {look.sunElevation:0}° hoch, Azimut {look.sunAzimuth:0}°, {look.sunTemperature:0} K).");
                Lighting(look);

                StoryCycling.Editor.CapeCrownSceneBuilder.Generated = OutDir;
                Transform rider = StoryCycling.Editor.CapeCrownSceneBuilder.AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
                if (world != null)
                {
                    Progress("Verkehr", .93f);
                    TrafficSceneBuilder.Build(cfg, world.Net, world.Surface, catalog, world.CarPaint, rider);
                }
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
                var labelProp = data.FindProperty("routeLabel");
                if (labelProp != null) labelProp.stringValue = Path.GetFileNameWithoutExtension(GpxPath).ToUpperInvariant();
                data.FindProperty("rider").objectReferenceValue = rider;
                data.FindProperty("rideCamera").objectReferenceValue = cam.transform;
                data.FindProperty("cyclistAnimation").objectReferenceValue = animation;
                var array = data.FindProperty("wheels");
                array.arraySize = wheels.Length;
                for (int i = 0; i < wheels.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = wheels[i];
                data.ApplyModifiedPropertiesWithoutUndo();

                // Originalmenü (Geräte verbinden, Start/Pause, Demo) + Radcomputer-Anzeige; Tempo kommt vom Trainer bzw. der Demo-Fahrt
                new GameObject("Cape Crown Devices").AddComponent<CapeCrownDevices>();
                director.gameObject.AddComponent<CapeCrownMusic>();
                var hud = director.gameObject.AddComponent<CapeCrownMobileHud>();
                var hudData = new SerializedObject(hud);
                hudData.FindProperty("gpxRide").objectReferenceValue = director;
                hudData.ApplyModifiedPropertiesWithoutUndo();
                if (cfg.buildWorldOnDevice) AddRuntimeBuilder(cfg, catalog, director.gameObject, rider);

                AssetDatabase.SaveAssets();
                EditorSceneManager.SaveScene(scene, ScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                Debug.Log(ScenePath + " saved. " + (world != null ? "GPX ride ready — length " + (world.Spline.Length / 1000f).ToString("0.00") + " km."
                                                                   : "Welt entsteht beim Start auf dem Gerät (Rohdaten: " + cfg.DeviceDataDir + ")."));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // osm2streets-Kreuzungsflächen (optionales Node-Werkzeug, siehe Tools/osm2streets/README.md) — nur im Editor
        private static List<Vector2[]> Osm2Streets(RouteWorldConfig cfg)
        {
            string o2sOut = cfg.Osm2StreetsPath;
            if (Osm2StreetsGeometry.NeedsRefresh(cfg.gpxPath, cfg.osmPath, o2sOut) &&
                Osm2StreetsGeometry.TryRun(cfg.gpxPath, cfg.osmPath, o2sOut, out string o2sMsg))
                Debug.Log(o2sMsg);
            var j = Osm2StreetsGeometry.TryLoad(o2sOut);
            Debug.Log(j != null ? $"osm2streets-Kreuzungsflächen geladen: {j.Count} aus {Path.GetFileName(o2sOut)}."
                                : "osm2streets-Geometrie nicht verfügbar — baue Fahrbahn/Kreuzungen mit der eigenen Flächenvereinigung.");
            return j;
        }

        // ------------------------------------------------------------------ Weltbau auf dem Gerät
        // Rohdaten nach StreamingAssets/WorldGen/<Route>, alte gebackene Dateien weg, Instancing an den Katalog-Materialien (sonst fehlt die
        // Instancing-Variante im Build und die Vegetation bleibt unsichtbar), URP-Lit-Vorlage mit Detail-Variante.
        private static void PrepareDeviceBuild(RouteWorldConfig cfg, AssetCatalog catalog)
        {
            string dir = cfg.DeviceDataDir;
            Directory.CreateDirectory(dir);
            File.Copy(cfg.gpxPath, dir + "/route.gpx", true);
            File.Copy(cfg.demPath, dir + "/dem.bytes", true);
            if (File.Exists(cfg.osmPath)) File.Copy(cfg.osmPath, dir + "/osm.xml", true); else Debug.LogWarning("OSM fehlt — die Welt auf dem Gerät hat keine Gebäude/Details.");
            if (File.Exists(cfg.LandCoverFile)) File.Copy(cfg.LandCoverFile, dir + "/landcover.bytes", true); else Debug.LogWarning("Landbedeckung fehlt — Gerät nutzt OSM + Gelände-Heuristik.");
            foreach (string stale in new[] { cfg.BakedRoutePath, cfg.BakedTrafficPath }) if (File.Exists(stale)) AssetDatabase.DeleteAsset(stale);
            int changed = 0;
            if (catalog != null && catalog.entries != null)
                foreach (var e in catalog.entries)
                {
                    if (e == null || e.prefab == null) continue;
                    foreach (var r in e.prefab.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (m != null && !m.enableInstancing && AssetDatabase.Contains(m)) { m.enableInstancing = true; EditorUtility.SetDirty(m); changed++; }
                }
            if (changed > 0) Debug.Log($"Weltbau auf dem Gerät: GPU-Instancing an {changed} Katalog-Materialien eingeschaltet.");
            long bytes = 0; foreach (var f in Directory.GetFiles(dir)) if (!f.EndsWith(".meta")) bytes += new FileInfo(f).Length;
            Debug.Log($"Weltbau auf dem Gerät: Rohdaten in {dir} ({bytes / 1048576f:0.0} MB).");
        }

        private static void AddRuntimeBuilder(RouteWorldConfig cfg, AssetCatalog catalog, GameObject host, Transform rider)
        {
            var template = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "GpxLitTemplate" };
            template.EnableKeyword("_DETAIL_MULX2");
            template.SetTexture("_DetailAlbedoMap", SaveTexture(TerrainPaint.BuildDetail(64), OutDir + "/TemplateDetail.png", true, 64));
            template = Save(template);
            var rb = host.AddComponent<RuntimeWorldBuilder>();
            var so = new SerializedObject(rb);
            so.FindProperty("config").objectReferenceValue = cfg;
            so.FindProperty("catalog").objectReferenceValue = catalog;
            so.FindProperty("litTemplate").objectReferenceValue = template;
            so.FindProperty("oceanSource").objectReferenceValue = FindOceanSource();
            so.FindProperty("rider").objectReferenceValue = rider;
            var names = so.FindProperty("landmarkNames"); var prefabs = so.FindProperty("landmarks");
            names.arraySize = prefabs.arraySize = RouteWorldBuild.Landmarks.Length;
            for (int i = 0; i < RouteWorldBuild.Landmarks.Length; i++)
            {
                string n = RouteWorldBuild.Landmarks[i].name;
                names.GetArrayElementAtIndex(i).stringValue = n;
                prefabs.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(LandmarkPath(n));
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string LandmarkPath(string name) => "Assets/WorldAssets/Landmarks/" + name + ".prefab";

        private static Material FindOceanSource()
        {
            foreach (string guid in AssetDatabase.FindAssets("Water_Ocean_Day t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m != null) return m;
            }
            return null;
        }

        // Ausgabe des Weltbaus im Editor: alles als Assets unter OutDir (Meshes gebündelt in einer Datei)
        private sealed class EditorSink : IWorldSink
        {
            public Mesh Mesh(Mesh mesh) => SaveMesh(mesh);
            public Texture2D Texture(Texture2D tex, string name, bool repeat, int maxSize) => SaveTexture(tex, OutDir + "/" + name + ".png", repeat, maxSize);
            public T Asset<T>(T asset) where T : UnityEngine.Object => Save(asset);
            public Material NewLit()
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable.");
                return new Material(shader);
            }
            public Material OceanSource() => FindOceanSource();
            public GameObject Landmark(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(LandmarkPath(name));
            public void Progress(string what, float t) => GpxSceneBuilder.Progress(what, t);
        }

        private static void Progress(string what, float t)
        {
            if (!Application.isBatchMode) EditorUtility.DisplayProgressBar("Welt bauen", what, t);
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
