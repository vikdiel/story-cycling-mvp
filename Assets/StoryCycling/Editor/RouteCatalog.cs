using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
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
            BuildCityCenter();  // Cape Town City Center (dense downtown loop)
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

        private static readonly string[] GenericBuildings = {
            Root + "Buildings/SM_Bld_Shop_01.prefab", Root + "Buildings/SM_Bld_Shop_02.prefab",
            Root + "Buildings/SM_Bld_Shop_03.prefab", Root + "Buildings/SM_Bld_Shop_04.prefab",
            Root + "Buildings/SM_Bld_Shop_05.prefab", Root + "Buildings/SM_Bld_Shop_06.prefab",
            Root + "Buildings/SM_Bld_Apartment_01.prefab", Root + "Buildings/SM_Bld_Apartment_03.prefab",
            Root + "Buildings/SM_Bld_OfficeSquare_01.prefab", Root + "Buildings/SM_Bld_OfficeSquare_03.prefab",
            Root + "Buildings/SM_Bld_Apartment_Corner_01.prefab", Root + "Buildings/SM_Bld_Shop_Corner_01.prefab"
        };
        private const string LampPole = Root + "Props/SM_Prop_LightPole_Lights_01.prefab";
        private const string TrafficLightPrefab = Root + "Props/SM_Prop_TrafficLight_01.prefab";
        private const string GiveWaySign = Root + "Props/SM_Prop_Sign_GiveWay_01.prefab";

        private static void BuildGenericEnvironment(int theme)
        {
            BuildReferenceEnvironment(Generated.Substring("Assets/StoryCycling/Generated".Length), theme);
        }

        private static void AddRouteData()
        {
            Vector2[] wp = CapeCrownRoute.Waypoints;
            (float start, float end, float weight)[] hills = CapeCrownRoute.Hills;
            var go = new GameObject("Route Data").AddComponent<CapeCrownRouteData>();
            var so = new SerializedObject(go);
            var wpProp = so.FindProperty("waypoints");
            wpProp.arraySize = wp.Length;
            for (int i = 0; i < wp.Length; i++) wpProp.GetArrayElementAtIndex(i).vector2Value = wp[i];
            var hs = so.FindProperty("hillStart");
            var he = so.FindProperty("hillEnd");
            var hw = so.FindProperty("hillWeight");
            hs.arraySize = he.arraySize = hw.arraySize = hills.Length;
            for (int i = 0; i < hills.Length; i++)
            {
                hs.GetArrayElementAtIndex(i).floatValue = hills[i].start;
                he.GetArrayElementAtIndex(i).floatValue = hills[i].end;
                hw.GetArrayElementAtIndex(i).floatValue = hills[i].weight;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
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
            AddRouteData();
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
            "Assets/StoryCycling/Scenes/CapeTownCityCenter.unity",
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

        public static void SetAllScenesInBuildSettings()
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>();
            foreach (string p in SectionScenes)
                {
                if (!File.Exists(p)) throw new BuildFailedException("Missing route scene: " + p + ". Run Story Cycling > Build All Routes.");
                list.Add(new EditorBuildSettingsScene(p, true));
            }
            EditorBuildSettings.scenes = list.ToArray();
            Debug.Log("Build settings now include " + list.Count + " section scenes.");
        }
    }
}
