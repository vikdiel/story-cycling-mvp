using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Builds a rideable test scene from the Nordhoek GPX: road strips along the real
    // track, the six authored landmarks at their key distances, rider + camera + slider HUD.
    public static class GpxSceneBuilder
    {
        private const string GpxPath = "Assets/StreamingAssets/Routes/Nordhoek.gpx";
        private const string ScenePath = "Assets/StoryCycling/Scenes/NordhoekGpxTest.unity";
        private const string OutDir = "Assets/StoryCycling/GeneratedGpx";
        private static int assetId;

        [MenuItem("Story Cycling/WorldGen/Build Nordhoek GPX Ride")]
        public static void BuildNordhoek()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode first.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(GpxPath)) throw new InvalidOperationException("GPX missing: " + GpxPath);

            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            AssetDatabase.Refresh();
            assetId = 0;

            var pts = GpxParser.Parse(File.ReadAllText(GpxPath));
            var local = RoutePreprocessor.Clean(GpxParser.ProjectToLocalMeters(pts));
            var spline = new RouteSpline();
            spline.Define(local);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Material asphalt = Mat("GpxAsphalt", new Color(.12f, .15f, .18f));
            Material white = Mat("GpxRoadPaint", new Color(.94f, .91f, .78f));
            Material terrain = Mat("GpxTerrain", new Color(.37f, .46f, .32f));
            Material ocean = Mat("GpxOcean", new Color(.08f, .39f, .52f));

            // Ground ribbon follows the track elevation so the climbing road never floats.
            Ribbon("Terrain ribbon", -90f, 90f, -.08f, 0f, spline.Length, terrain, spline);
            Ribbon("Asphalt", -4f, 4f, .02f, 0f, spline.Length, asphalt, spline);
            Ribbon("Inner edge", -3.7f, -3.57f, .03f, 0f, spline.Length, white, spline);
            Ribbon("Outer edge", 3.57f, 3.7f, .03f, 0f, spline.Length, white, spline);
            Box("Ocean", new Vector3(0, -6f, 0), new Vector3(40000, 4f, 40000), ocean);

            PlaceLandmarks(spline);
            PlaceFiller(spline, pts.Count > 0 ? pts[0] : default);

            Lighting();

            StoryCycling.Editor.CapeCrownSceneBuilder.Generated = OutDir;
            Transform rider = StoryCycling.Editor.CapeCrownSceneBuilder.AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
            GameObject camGo = new GameObject("Ride Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            Camera cam = camGo.GetComponent<Camera>();
            cam.fieldOfView = 58; cam.nearClipPlane = .1f; cam.farClipPlane = 4000; cam.allowHDR = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.48f, .72f, .87f);

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

        private static void PlaceLandmarks(RouteSpline spline)
        {
            // Approximate key distances along the 65 km route; refinable with real km markers.
            var landmarks = new (string name, float frac, float offset)[]
            {
                ("Landmark_HoutBayHarbour", .28f, -16f),
                ("Landmark_EastFort", .36f, 16f),
                ("Landmark_ChapmansLookout", .40f, -16f),
                ("Landmark_KakapoShipwreck", .52f, 16f),
                ("Landmark_SlangkopLighthouse", .60f, -16f),
                ("Landmark_ConstantiaManor", .88f, 16f)
            };
            foreach (var lm in landmarks)
            {
                string path = "Assets/WorldAssets/Landmarks/" + lm.name + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.LogWarning("Landmark missing: " + path); continue; }
                float d = lm.frac * spline.Length;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = lm.name;
                Vector3 p = spline.SamplePosition(d);
                Vector3 t = spline.SampleTangent(d);
                Vector3 right = Vector3.Cross(Vector3.up, t).normalized;
                Vector3 pos = p + right * lm.offset;
                pos.y += .05f;
                // Ground-anchor: snap the prefab's lowest bound to the terrain ribbon.
                Bounds b = BoundsOf(go);
                pos.y -= b.min.y - spline.SamplePosition(d).y;
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(t, Vector3.up);
            }
        }

        private static void Ribbon(string name, float left, float right, float height, float start, float end, Material material, RouteSpline spline)
        {
            int count = Mathf.CeilToInt((end - start) / 2f);
            var vertices = new Vector3[(count + 1) * 2];
            var triangles = new int[count * 6];
            for (int i = 0; i <= count; i++)
            {
                float d = Mathf.Lerp(start, end, i / (float)count);
                Vector3 p = spline.SamplePosition(d);
                Vector3 t = spline.SampleTangent(d);
                Vector3 side = Vector3.Cross(Vector3.up, t).normalized;
                vertices[2 * i] = p + side * left + Vector3.up * height;
                vertices[2 * i + 1] = p + side * right + Vector3.up * height;
                if (i == count) continue;
                int a = 2 * i, tri = 6 * i;
                triangles[tri] = a; triangles[tri + 1] = a + 2; triangles[tri + 2] = a + 1;
                triangles[tri + 3] = a + 1; triangles[tri + 4] = a + 2; triangles[tri + 5] = a + 3;
            }
            Mesh mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mesh = Save(mesh);
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void PlaceFiller(RouteSpline spline, GeoPoint origin)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AssetCatalog>(AssetCatalogBuilder.CatalogPath);
            if (catalog == null) { Debug.LogWarning("WorldGen catalog missing — no filler."); return; }

            // OSM laden (einmal per 'Fetch OSM for Nordhoek' geholt, dann offline)
            OsmContext osm = null;
            string osmPath = "Assets/StreamingAssets/Osm/Nordhoek.osm.xml";
            if (System.IO.File.Exists(osmPath))
            {
                osm = OsmContext.Load(System.IO.File.ReadAllText(osmPath), origin);
                OsmBuildingPlacer.Place(spline, osm, catalog);   // Gebäude an echten Footprints
                OsmDetailPlacer.Place(spline, osm, catalog);     // Schilder, Ampeln, Bänke, Bäume, Zäune, Parkautos
            }
            else Debug.LogWarning("OSM fehlt — erst 'Fetch OSM for Nordhoek'. Gebäude/Details werden übersprungen.");

            // Vegetation: Dichte aus OSM-Landnutzung (Wald dicht, Feld licht, Stadt kaum Bäume)
            var vegetation = new List<GameObject>();
            foreach (var e in catalog.entries)
                if (e != null && e.prefab != null && e.category == AssetCategory.Vegetation) vegetation.Add(e.prefab);
            if (vegetation.Count == 0) return;

            var rng = new System.Random(4242);
            for (float d = 0f; d < spline.Length; d += 10f)
            {
                Vector3 p = spline.SamplePosition(d);
                string biome = osm != null ? osm.BiomeAt(p) : "generic";
                float step = biome == "forest" ? 12f : biome == "field" ? 45f : biome == "urban" ? 60f : 30f;
                if (d % step >= 10f) continue;
                Vector3 t = spline.SampleTangent(d);
                Vector3 side = Vector3.Cross(Vector3.up, t).normalized;
                int dir = rng.Next(2) == 0 ? -1 : 1;
                float offset = 7f + (float)rng.NextDouble() * 15f;
                int dummy = 0;
                PlacePrefab(vegetation[rng.Next(vegetation.Count)], p + side * (dir * offset), t, rng, ref dummy);
            }
        }

        private static float SlopeDegrees(RouteSpline spline, float d)
        {
            Vector3 t = spline.SampleTangent(d);
            float horiz = new Vector2(t.x, t.z).magnitude;
            if (horiz < 1e-5f) return 0f;
            return Mathf.Atan(t.y / horiz) * Mathf.Rad2Deg;
        }

        private static void PlacePrefab(GameObject prefab, Vector3 pos, Vector3 tangent, System.Random rng, ref int placed)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = prefab.name;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(tangent, Vector3.up) * Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            // Ground-anchor: snap the prefab's lowest bound to the track elevation at this point.
            Bounds b = BoundsOf(go);
            go.transform.position += Vector3.up * (pos.y - b.min.y);
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            placed++;
        }

        private static void Box(string name, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name; go.transform.position = position; go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) throw new InvalidOperationException("Prefab has no renderers: " + go.name);
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        private static void Lighting()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.62f, .74f, .88f);
            RenderSettings.ambientEquatorColor = new Color(.82f, .68f, .52f);
            RenderSettings.ambientGroundColor = new Color(.45f, .38f, .30f);
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 500; RenderSettings.fogEndDistance = 4000;
            RenderSettings.fogColor = new Color(.92f, .78f, .64f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.color = new Color(1f, .76f, .48f); sun.intensity = 1.45f;
            sun.shadows = LightShadows.Soft; sun.transform.rotation = Quaternion.Euler(18, -55, 0);
        }

        private static Material Mat(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable.");
            var mat = new Material(shader) { name = name, color = color };
            mat.SetFloat("_Smoothness", .12f);
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
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
