using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.Editor
{
    public static partial class CapeCrownSceneBuilder
    {
        private static Vector2[] Stadium(float halfStraight, float radius) => new[] {
            new Vector2(radius, -halfStraight), new Vector2(radius, 0), new Vector2(radius, halfStraight),
            new Vector2(0, halfStraight + radius), new Vector2(-radius, halfStraight), new Vector2(-radius, 0), new Vector2(-radius, -halfStraight),
            new Vector2(0, -halfStraight - radius)
        };

        private static void SetHillsFrac(params (float s, float e, float w)[] fractions)
        {
            float L = CapeCrownRoute.Length;
            var hills = new (float, float, float)[fractions.Length];
            for (int i = 0; i < fractions.Length; i++)
                hills[i] = (fractions[i].s * L, fractions[i].e * L, fractions[i].w);
            CapeCrownRoute.Hills = hills;
        }

        [MenuItem("Story Cycling/Build All Routes")]
        public static void BuildAllRoutes()
        {
            BuildWorld(true, true);   // Cape Crown Promenade (Camps Bay)
            BuildBoKaap();            // Bo-Kaap Steps
            BuildGeneric("CliftonCove", "CLIFTON COVE", 5, 350f, 60f, 1f, (0.2f, 0.45f, 0.55f), (0.6f, 0.85f, 0.45f));
            BuildGeneric("ApostlesClimb", "THE APOSTLES CLIMB", 1, 500f, 70f, 8f, (0.15f, 0.45f, 0.7f), (0.6f, 0.9f, 0.55f));
            BuildGeneric("HoutBayHarbour", "HOUT BAY HARBOUR", 2, 400f, 65f, 2f, (0.25f, 0.5f, 0.5f));
            BuildGeneric("ConstantiaVines", "CONSTANTIA VINES", 3, 550f, 75f, 3f, (0.2f, 0.45f, 0.5f), (0.6f, 0.85f, 0.45f));
            BuildGeneric("KirstenboschLoop", "KIRSTENBOSCH LOOP", 4, 450f, 65f, 2f, (0.3f, 0.55f, 0.4f));
            BuildGeneric("FalseBaySands", "FALSE BAY SANDS", 5, 600f, 80f, 1f, (0.25f, 0.5f, 0.4f));
            BuildGeneric("CapePointHeadland", "CAPE POINT HEADLAND", 6, 650f, 85f, 5f, (0.1f, 0.4f, 0.65f), (0.55f, 0.85f, 0.6f));
            BuildGeneric("TableFoothills", "TABLE FOOTHILLS", 7, 500f, 70f, 3f, (0.2f, 0.5f, 0.55f), (0.65f, 0.9f, 0.4f));
            SetAllScenesInBuildSettings();
        }

        private static void BuildGeneric(string sceneName, string label, int theme, float halfStraight,
            float radius, float hillHeight, params (float s, float e, float w)[] hillFracs)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            relief = hillHeight;
            Generated = "Assets/StoryCycling/Generated" + sceneName;
            string scenePath = "Assets/StoryCycling/Scenes/" + sceneName + ".unity";
            Directory.CreateDirectory(Generated);
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            AssetDatabase.Refresh();
            assetId = 0;
            CapeCrownRoute.Define(Stadium(halfStraight, radius));
            SetHillsFrac(hillFracs);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildGenericEnvironment(theme);
            FinishRouteScene(scene, scenePath, label, true);
        }

        private static void BuildGenericEnvironment(int theme)
        {
            Color[] groundColors = {
                new Color(.52f, .58f, .38f), new Color(.58f, .52f, .44f), new Color(.52f, .48f, .42f),
                new Color(.48f, .60f, .36f), new Color(.42f, .58f, .46f), new Color(.72f, .68f, .52f),
                new Color(.45f, .52f, .38f), new Color(.55f, .54f, .50f)
            };
            Box("Section ground", new Vector3(0, -0.6f, 0), new Vector3(1600, 1, 1600), Mat("Section ground", groundColors[theme % groundColors.Length]));
            Color[] palette = {
                new Color(.96f, .42f, .56f), new Color(.30f, .72f, .52f), new Color(.36f, .55f, .86f),
                new Color(.95f, .76f, .26f), new Color(.44f, .80f, .74f), new Color(.90f, .50f, .30f),
                new Color(.72f, .52f, .82f)
            };
            Material trim = Mat("Section trim", new Color(.97f, .94f, .86f));
            float spacing = 11f;
            for (float d = 0; d < CapeCrownRoute.Length; d += spacing)
            {
                CapeCrownRoute.Sample(d, out Vector3 point, out Vector3 forward);
                Vector3 fwd = new Vector3(forward.x, 0, forward.z).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);
                for (int side = -1; side <= 1; side += 2)
                {
                    int idx = Mathf.RoundToInt(d / spacing) * 2 + (side < 0 ? 0 : 1);
                    float w = 4f + (idx % 3) * 2f;
                    float h = 3f + ((idx / 2) % 3) * 1.5f;
                    int colorIdx = idx % palette.Length;
                    Vector3 pos = point + right * (side * 11f) + Vector3.up * (h / 2f - 0.15f);
                    GameObject house = Part(null, PrimitiveType.Cube, pos, new Vector3(6f, h, w), Mat("Section house " + colorIdx, palette[colorIdx]));
                    house.transform.rotation = rot;
                    house.name = "Section house";
                    GameObject roof = Part(null, PrimitiveType.Cube, pos + Vector3.up * (h / 2f + 0.18f), new Vector3(6.4f, 0.35f, w + 0.4f), trim);
                    roof.transform.rotation = rot;
                    roof.name = "Section roof";
                }
            }
            // Scattered trees inland and seaward for depth.
            for (float d = 18f; d < CapeCrownRoute.Length; d += 22f)
            {
                int side = (Mathf.RoundToInt(d / 22f) % 2 == 0) ? -1 : 1;
                CapeCrownRoute.Sample(d, out Vector3 p, out Vector3 fwd);
                Vector3 hf = new Vector3(fwd.x, 0, fwd.z).normalized;
                Vector3 off = Vector3.Cross(Vector3.up, hf) * (side * 18f);
                AddCoastalProp(Tree, new Vector3(p.x + off.x, -0.14f, p.z + off.z), d * 31f, 5.5f, "Section tree");
            }
            AddParkedCars();
        }

        private static void FinishRouteScene(UnityEngine.SceneManagement.Scene scene, string scenePath, string routeLabel, bool skybox)
        {
            Material asphalt = Mat("Asphalt", new Color(.12f, .15f, .18f));
            Material white = Mat("RoadPaint", new Color(.94f, .91f, .78f));
            Strip("Continuous asphalt", -4f, 4f, .02f, 0, CapeCrownRoute.Length, asphalt);
            Strip("Inner edge", -3.7f, -3.57f, .03f, 0, CapeCrownRoute.Length, white);
            Strip("Outer edge", 3.57f, 3.7f, .03f, 0, CapeCrownRoute.Length, white);
            int dashCount = Mathf.RoundToInt(CapeCrownRoute.Length / 8f);
            float dashSpacing = CapeCrownRoute.Length / dashCount;
            for (int i = 0; i < dashCount; i++)
                Strip("Centre dash " + i, -.07f, .07f, .035f, i * dashSpacing, i * dashSpacing + 3f, white);
            Lighting();
            if (skybox) CoastalSky();
            Transform rider = AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
            GameObject cameraObject = new GameObject("Ride Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.fieldOfView = 58; camera.nearClipPlane = .1f; camera.farClipPlane = 900; camera.allowHDR = false;
            cameraObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing = true;
            var volume = new GameObject("Coastal colour grade").AddComponent<Volume>(); volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var grade = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            grade.postExposure.Override(.2f); grade.contrast.Override(12); grade.saturation.Override(12);
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(.35f); bloom.threshold.Override(.92f);
            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.Override(.28f); vignette.smoothness.Override(.4f);
            profile = Save(profile); AssetDatabase.AddObjectToAsset(grade, profile); AssetDatabase.AddObjectToAsset(bloom, profile); AssetDatabase.AddObjectToAsset(vignette, profile); volume.sharedProfile = profile;
            camera.clearFlags = skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.48f, .72f, .87f);
            CapeCrownRoute.Sample(0, out _, out Vector3 forward);
            rider.SetPositionAndRotation(CapeCrownRoute.Position(0, CapeCrownRoute.LaneOffset, .045f), Quaternion.LookRotation(forward));
            camera.transform.position = rider.position - forward * 5.8f + Vector3.up * 2.6f;
            camera.transform.LookAt(rider.position + forward * 7 + Vector3.up * 1.15f);
            var director = new GameObject("Cape Crown Ride Director").AddComponent<CapeCrownRideController>();
            SerializedObject data = new SerializedObject(director);
            data.FindProperty("hillHeight").floatValue = relief;
            data.FindProperty("rider").objectReferenceValue = rider;
            data.FindProperty("cyclistAnimation").objectReferenceValue = animation;
            data.FindProperty("routeLabel").stringValue = routeLabel;
            data.FindProperty("rideCamera").objectReferenceValue = camera.transform;
            data.FindProperty("telemetry").objectReferenceValue = null;
            var array = data.FindProperty("wheels");
            array.arraySize = wheels.Length;
            for (int i = 0; i < wheels.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = wheels[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            new GameObject("Cape Crown Devices").AddComponent<CapeCrownDevices>();
            director.gameObject.AddComponent<CapeCrownMusic>();
            director.gameObject.AddComponent<CapeCrownMobileHud>().Configure(director);
            director.gameObject.AddComponent<CapeCrownMobileQuality>();
            AddLife(director);
            CapeCrownValidation.ValidateRoute(relief);
            CapeCrownValidation.ValidateCyclist(animation);
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, scenePath);
            var reopened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            CapeCrownValidation.ValidateRoute(relief);
            bool foundRig = false;
            foreach (GameObject go in reopened.GetRootGameObjects())
            {
                CapeCrownCyclistAnimation rig = go.GetComponentInChildren<CapeCrownCyclistAnimation>();
                if (rig != null) { CapeCrownValidation.ValidateCyclist(rig); foundRig = true; }
                foreach (MeshFilter mesh in go.GetComponentsInChildren<MeshFilter>())
                    if (mesh.sharedMesh == null) throw new InvalidOperationException("Mesh lost after save: " + mesh.name);
                if (go.GetComponent<CapeCrownRideController>() != null) Selection.activeGameObject = go;
            }
            if (!foundRig) throw new InvalidOperationException("Cyclist rig lost after scene save.");
            Debug.Log(scenePath + " saved. Route length " + CapeCrownRoute.Length.ToString("0") + " m.");
        }

        private static readonly string[] SectionScenes = {
            "Assets/StoryCycling/Scenes/CampsBayTrainerRide.unity",
            "Assets/StoryCycling/Scenes/CliftonCove.unity",
            "Assets/StoryCycling/Scenes/ApostlesClimb.unity",
            "Assets/StoryCycling/Scenes/HoutBayHarbour.unity",
            "Assets/StoryCycling/Scenes/ConstantiaVines.unity",
            "Assets/StoryCycling/Scenes/BoKaapSteps.unity",
            "Assets/StoryCycling/Scenes/KirstenboschLoop.unity",
            "Assets/StoryCycling/Scenes/FalseBaySands.unity",
            "Assets/StoryCycling/Scenes/CapePointHeadland.unity",
            "Assets/StoryCycling/Scenes/TableFoothills.unity"
        };

        private static void SetAllScenesInBuildSettings()
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>();
            foreach (string p in SectionScenes)
                if (File.Exists(p)) list.Add(new EditorBuildSettingsScene(p, true));
            EditorBuildSettings.scenes = list.ToArray();
            Debug.Log("Build settings now include " + list.Count + " section scenes.");
        }
    }
}
