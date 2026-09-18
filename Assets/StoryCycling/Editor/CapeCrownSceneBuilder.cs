using System.IO;
using StoryCycling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCycling.Editor
{
    public static class CapeCrownSceneBuilder
    {
        private const string ScenePath = "Assets/StoryCycling/Scenes/CapeCrownDemo.unity";
        private const string PalmPath = "Assets/TooMooseGames/Nature Biomes Pack - Low Poly/Prefabs/Palm Tree O1.prefab";
        private const string RockPath = "Assets/TooMooseGames/Nature Biomes Pack - Low Poly/Prefabs/Rock 3 Sand.prefab";
        private const string ParasolPath = "Assets/TooMooseGames/Nature Biomes Pack - Low Poly/Prefabs/Beach Parasol 3.prefab";
        private const string BuildingPath = "Assets/Palmov Island/Low Poly Atmospheric Locations Pack/Models/Houses/Buildings/building.fbx";
        private const string BuildingTwoPath = "Assets/Palmov Island/Low Poly Atmospheric Locations Pack/Models/Houses/Buildings/building 2.fbx";
        private const string CarPath = "Assets/FREE_CartoonPack_Vehicles/Prefabs/Vehicle_BasicCar_Orange.prefab";

        [MenuItem("Story Cycling/Build Cape Crown Demo")]
        public static void BuildCapeCrownDemo()
        {
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();
            Transform rider = BuildRider();
            Transform camera = BuildCamera(rider);
            BuildRoad();
            BuildCoastline();
            BuildTown();
            BuildHud();

            GameObject director = new GameObject("Ride Director");
            CapeCrownRideController controller = director.AddComponent<CapeCrownRideController>();
            SerializedObject controllerData = new SerializedObject(controller);
            controllerData.FindProperty("rider").objectReferenceValue = rider;
            controllerData.FindProperty("rideCamera").objectReferenceValue = camera;
            controllerData.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = director;
            EditorGUIUtility.PingObject(director);
            Debug.Log("Cape Crown demo created. Press Play, then hold W or Up Arrow to ride.");
        }

        private static void BuildLighting()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.56f, 0.68f, 0.78f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.39f, 0.67f, 0.82f);
            RenderSettings.fogDensity = 0.0014f;

            GameObject sun = new GameObject("Golden Hour Sun");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.72f, 0.46f);
            light.intensity = 1.25f;
            sun.transform.rotation = Quaternion.Euler(39f, -28f, 0f);
        }

        private static Transform BuildCamera(Transform rider)
        {
            GameObject cameraObject = new GameObject("Ride Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 64f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 950f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = rider.position + new Vector3(0f, 3.5f, -7.5f);
            cameraObject.transform.LookAt(rider.position + Vector3.forward * 8f);
            return cameraObject.transform;
        }

        private static Transform BuildRider()
        {
            GameObject rider = new GameObject("Rider Bike");
            rider.transform.position = new Vector3(0f, 0.42f, 4f);

            Material frameMaterial = MaterialWithColor("Bike Frame", new Color(0.05f, 0.78f, 0.91f));
            Material tireMaterial = MaterialWithColor("Bike Tires", new Color(0.045f, 0.055f, 0.07f));
            Material riderMaterial = MaterialWithColor("Rider Jersey", new Color(0.96f, 0.29f, 0.16f));

            for (int side = -1; side <= 1; side += 2)
            {
                GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                wheel.name = side < 0 ? "Rear Wheel" : "Front Wheel";
                wheel.transform.SetParent(rider.transform, false);
                wheel.transform.localPosition = new Vector3(side * 0.78f, 0.46f, 0f);
                wheel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                wheel.transform.localScale = new Vector3(0.10f, 0.48f, 0.48f);
                wheel.GetComponent<Renderer>().sharedMaterial = tireMaterial;
            }

            GameObject frame = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            frame.name = "Bike Frame";
            frame.transform.SetParent(rider.transform, false);
            frame.transform.localPosition = new Vector3(0f, 0.82f, 0f);
            frame.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            frame.transform.localScale = new Vector3(0.08f, 0.82f, 0.08f);
            frame.GetComponent<Renderer>().sharedMaterial = frameMaterial;

            GameObject torso = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            torso.name = "Rider";
            torso.transform.SetParent(rider.transform, false);
            torso.transform.localPosition = new Vector3(0f, 1.32f, 0f);
            torso.transform.localRotation = Quaternion.Euler(0f, 0f, -18f);
            torso.transform.localScale = new Vector3(0.38f, 0.65f, 0.38f);
            torso.GetComponent<Renderer>().sharedMaterial = riderMaterial;
            return rider.transform;
        }

        private static void BuildRoad()
        {
            GameObject ground = Cube("Coastal Ground", new Vector3(0f, -0.28f, 380f), new Vector3(160f, 0.35f, 780f), new Color(0.77f, 0.55f, 0.27f));
            ground.isStatic = true;
            GameObject road = Cube("Chapman's Coast Road", new Vector3(0f, -0.05f, 380f), new Vector3(10f, 0.15f, 780f), new Color(0.08f, 0.10f, 0.13f));
            road.isStatic = true;

            for (int z = 0; z < 760; z += 12)
            {
                GameObject dash = Cube("Road Marking", new Vector3(0f, 0.04f, z), new Vector3(0.18f, 0.03f, 5.2f), Color.white);
                dash.isStatic = true;
            }
        }

        private static void BuildCoastline()
        {
            GameObject sea = Cube("Atlantic Ocean", new Vector3(-30f, -0.16f, 380f), new Vector3(56f, 0.08f, 780f), new Color(0.05f, 0.43f, 0.68f));
            sea.isStatic = true;
            for (int z = 20; z < 750; z += 44)
            {
                SpawnAsset(PalmPath, new Vector3(9f + (z % 3), 0f, z), new Vector3(0f, (z * 11) % 360, 0f), Vector3.one * 1.15f, "Palm");
                SpawnAsset(RockPath, new Vector3(-7f - (z % 4), 0f, z + 8f), new Vector3(0f, z % 360, 0f), Vector3.one * 1.3f, "Sandstone Rock");
            }
            for (int z = 95; z < 720; z += 150)
            {
                SpawnAsset(ParasolPath, new Vector3(-11f, 0f, z), Vector3.zero, Vector3.one * 1.15f, "Beach Parasol");
            }
        }

        private static void BuildTown()
        {
            for (int z = 50; z < 740; z += 55)
            {
                string path = ((z / 55) % 2 == 0) ? BuildingPath : BuildingTwoPath;
                float side = ((z / 55) % 3 == 0) ? -1f : 1f;
                SpawnAsset(path, new Vector3(side * 15f, 0f, z), new Vector3(0f, side < 0 ? 90f : -90f, 0f), Vector3.one * 2.1f, "Cape Town Building");
                if ((z / 55) % 3 == 1)
                {
                    SpawnAsset(CarPath, new Vector3(side * 3.2f, 0.15f, z + 8f), new Vector3(0f, side < 0 ? 0f : 180f, 0f), Vector3.one, "Parked Cartoon Car");
                }
            }
        }

        private static void BuildHud()
        {
            GameObject canvasObject = new GameObject("Ride HUD");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            GameObject labelObject = new GameObject("Chapter Label");
            labelObject.transform.SetParent(canvasObject.transform, false);
            UnityEngine.UI.Text label = labelObject.AddComponent<UnityEngine.UI.Text>();
            label.text = "CAPE CROWN  •  ANKUNFT";
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 28;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            label.alignment = TextAnchor.UpperLeft;
            RectTransform rect = label.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(42f, -42f);
            rect.sizeDelta = new Vector2(600f, 60f);
        }

        private static GameObject SpawnAsset(string path, Vector3 position, Vector3 rotation, Vector3 scale, string objectName)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"Missing expected asset: {path}");
                return null;
            }
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = objectName;
            instance.transform.position = position;
            instance.transform.eulerAngles = rotation;
            instance.transform.localScale = scale;
            return instance;
        }

        private static GameObject Cube(string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.transform.localScale = scale;
            cube.GetComponent<Renderer>().sharedMaterial = MaterialWithColor(name + " Material", color);
            return cube;
        }

        private static Material MaterialWithColor(string name, Color color)
        {
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.name = name;
            material.color = color;
            return material;
        }
    }
}
