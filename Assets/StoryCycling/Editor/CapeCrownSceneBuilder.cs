using System.IO;
using StoryCycling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StoryCycling.Editor
{
    /// <summary>
    /// Creates the first playable Cape Crown slice using one coherent art kit:
    /// Synty's POLYGON City. It deliberately does not depend on removed free packs.
    /// </summary>
    public static class CapeCrownSceneBuilder
    {
        private const string ScenePath = "Assets/StoryCycling/Scenes/CapeCrownVerticalSlice.unity";
        private const string Root = "Assets/Synty/PolygonCity/Prefabs/";

        private const string Road = Root + "Environments/SM_Env_Road_YellowLines_01.prefab";
        private const string RoadPlain = Root + "Environments/SM_Env_Road_01.prefab";
        private const string Sidewalk = Root + "Environments/SM_Env_Sidewalk_Straight_01.prefab";
        private const string Ocean = Root + "Environments/SM_Env_Ocean_Tile_01.prefab";
        private const string Tree = Root + "Environments/SM_Env_Tree_02.prefab";
        private const string Skyline = Root + "Environments/Custom/SM_Env_Skyline_01.prefab";
        private const string Apartment = Root + "Buildings/SM_Bld_Apartment_Stack_01.prefab";
        private const string Shop = Root + "Buildings/SM_Bld_Shop_03.prefab";
        private const string Office = Root + "Buildings/SM_Bld_OfficeRound_01.prefab";
        private const string Station = Root + "Buildings/SM_Bld_Station_01.prefab";
        private const string Taxi = Root + "Vehicles/SM_Veh_Car_Taxi_01.prefab";
        private const string Sedan = Root + "Vehicles/SM_Veh_Car_Sedan_01.prefab";
        private const string Van = Root + "Vehicles/SM_Veh_Car_Van_01.prefab";
        private const string Pedestrian = Root + "Characters/Character_Male_Hoodie.prefab";
        private const string Rider = Root + "Characters/Character_Female_Jacket.prefab";
        private const string LightPole = Root + "Props/SM_Prop_LightPole_Lights_01.prefab";
        private const string Planter = Root + "Props/SM_Prop_Planter_01.prefab";
        private const string Billboard = Root + "Props/SM_Prop_Billboard_01.prefab";

        [MenuItem("Story Cycling/Build Cape Crown Vertical Slice")]
        public static void BuildCapeCrownVerticalSlice()
        {
            EnsureRequiredAssets();
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();
            Transform cyclist = BuildCyclist();
            Transform camera = BuildCamera(cyclist);
            BuildCoastalBoulevard();
            BuildCapeTownBlocks();
            BuildHud();

            GameObject director = new GameObject("Cape Crown Ride Director");
            CapeCrownRideController controller = director.AddComponent<CapeCrownRideController>();
            SerializedObject data = new SerializedObject(controller);
            data.FindProperty("rider").objectReferenceValue = cyclist;
            data.FindProperty("rideCamera").objectReferenceValue = camera;
            data.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = director;
            EditorGUIUtility.PingObject(director);
            Debug.Log("Cape Crown vertical slice created. Open the Game tab and hold W or Up Arrow to ride.");
        }

        private static void EnsureRequiredAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Road) == null)
            {
                throw new System.InvalidOperationException(
                    "POLYGON City is missing or is still importing. Wait for Unity to finish importing Assets/Synty/PolygonCity, then run this menu item again.");
            }
        }

        private static void BuildLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.30f, 0.59f, 0.86f);
            RenderSettings.ambientEquatorColor = new Color(0.82f, 0.54f, 0.36f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.15f, 0.19f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.47f, 0.69f, 0.81f);
            RenderSettings.fogDensity = 0.002f;

            GameObject sun = new GameObject("Cape Town Golden Hour");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.74f, 0.48f);
            light.intensity = 1.35f;
            sun.transform.rotation = Quaternion.Euler(38f, -38f, 0f);
        }

        private static Transform BuildCamera(Transform cyclist)
        {
            GameObject cameraObject = new GameObject("Ride Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 68f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 900f;
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = cyclist.position + new Vector3(0f, 2.7f, -6.8f);
            cameraObject.transform.LookAt(cyclist.position + new Vector3(0f, 1.2f, 13f));
            return cameraObject.transform;
        }

        private static Transform BuildCyclist()
        {
            GameObject cyclist = new GameObject("Player Cyclist");
            cyclist.transform.position = new Vector3(0f, 0.03f, 7f);
            GameObject rider = Spawn(Rider, cyclist.transform.position, new Vector3(0f, 180f, 0f), Vector3.one, "Cyclist");
            if (rider != null) rider.transform.SetParent(cyclist.transform, true);

            Material frame = MaterialWithColor("Cape Crown Frame", new Color(0.07f, 0.84f, 0.91f));
            Material tire = MaterialWithColor("Cape Crown Tires", new Color(0.03f, 0.04f, 0.06f));
            for (int side = -1; side <= 1; side += 2)
            {
                GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                wheel.name = side < 0 ? "Rear Wheel" : "Front Wheel";
                wheel.transform.SetParent(cyclist.transform, false);
                wheel.transform.localPosition = new Vector3(side * 0.68f, 0.39f, 0.08f);
                wheel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                wheel.transform.localScale = new Vector3(0.08f, 0.42f, 0.42f);
                wheel.GetComponent<Renderer>().sharedMaterial = tire;
            }
            GameObject frameBar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            frameBar.name = "Bike Frame";
            frameBar.transform.SetParent(cyclist.transform, false);
            frameBar.transform.localPosition = new Vector3(0f, 0.72f, 0.08f);
            frameBar.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            frameBar.transform.localScale = new Vector3(0.07f, 0.72f, 0.07f);
            frameBar.GetComponent<Renderer>().sharedMaterial = frame;
            return cyclist.transform;
        }

        private static void BuildCoastalBoulevard()
        {
            const int segments = 36;
            const float spacing = 10f;
            for (int i = 0; i < segments; i++)
            {
                float z = i * spacing;
                Spawn(i % 6 == 0 ? Road : RoadPlain, new Vector3(0f, 0f, z), Vector3.zero, Vector3.one, "Cape Coast Road");
                Spawn(Sidewalk, new Vector3(-6.2f, 0f, z), Vector3.zero, Vector3.one, "Atlantic Sidewalk");
                Spawn(Sidewalk, new Vector3(6.2f, 0f, z), new Vector3(0f, 180f, 0f), Vector3.one, "City Sidewalk");
                if (i % 3 == 0)
                {
                    Spawn(LightPole, new Vector3(7.4f, 0f, z + 2f), new Vector3(0f, 180f, 0f), Vector3.one, "Street Light");
                    Spawn(Tree, new Vector3(11.5f, 0f, z - 1f), new Vector3(0f, (i * 71) % 360, 0f), Vector3.one, "Cape Tree");
                }
                if (i % 4 == 0) Spawn(Planter, new Vector3(6.8f, 0f, z - 2f), Vector3.zero, Vector3.one, "Street Planter");
            }
            for (int z = 0; z < segments * spacing; z += 20)
                Spawn(Ocean, new Vector3(-19f, -0.15f, z), Vector3.zero, Vector3.one * 2.4f, "Atlantic Ocean");
        }

        private static void BuildCapeTownBlocks()
        {
            string[] buildings = { Apartment, Shop, Office, Station };
            for (int i = 0; i < 18; i++)
            {
                float z = 18f + i * 19f;
                Spawn(buildings[i % buildings.Length], new Vector3(17f, 0f, z), new Vector3(0f, 270f, 0f), Vector3.one, "Cape Town Building");
                if (i % 2 == 0) Spawn(buildings[(i + 1) % buildings.Length], new Vector3(30f, 0f, z + 4f), new Vector3(0f, 270f, 0f), Vector3.one, "Cape Town Background Building");
                if (i % 3 == 0) Spawn(Pedestrian, new Vector3(7.7f, 0f, z + 4f), new Vector3(0f, 180f, 0f), Vector3.one, "Pedestrian");
            }
            Spawn(Skyline, new Vector3(47f, 0f, 155f), new Vector3(0f, 270f, 0f), Vector3.one * 2.2f, "Cape Town Skyline");
            Spawn(Billboard, new Vector3(10f, 0f, 48f), new Vector3(0f, 270f, 0f), Vector3.one, "Cape Crown Billboard");
            Spawn(Taxi, new Vector3(2.1f, 0.02f, 54f), Vector3.zero, Vector3.one, "Cape Taxi");
            Spawn(Sedan, new Vector3(-2.1f, 0.02f, 104f), new Vector3(0f, 180f, 0f), Vector3.one, "Parked Sedan");
            Spawn(Van, new Vector3(2.1f, 0.02f, 178f), Vector3.zero, Vector3.one, "Support Van");
        }

        private static void BuildHud()
        {
            GameObject canvasObject = new GameObject("Cape Crown HUD");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            CreateLabel(canvas.transform, "CAPE CROWN", new Vector2(40f, -34f), 30, FontStyle.Bold);
            CreateLabel(canvas.transform, "ANKUNFT  •  KAPSTADT", new Vector2(40f, -72f), 17, FontStyle.Normal);
            CreateLabel(canvas.transform, "W / ↑  Testfahrt", new Vector2(-42f, -42f), 17, FontStyle.Bold, TextAnchor.UpperRight, new Vector2(1f, 1f));
        }

        private static void CreateLabel(Transform parent, string text, Vector2 position, int size, FontStyle style, TextAnchor alignment = TextAnchor.UpperLeft, Vector2? anchor = null)
        {
            GameObject labelObject = new GameObject(text);
            labelObject.transform.SetParent(parent, false);
            UnityEngine.UI.Text label = labelObject.AddComponent<UnityEngine.UI.Text>();
            label.text = text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.fontStyle = style;
            label.color = Color.white;
            label.alignment = alignment;
            RectTransform rect = label.GetComponent<RectTransform>();
            rect.anchorMin = anchor ?? new Vector2(0f, 1f);
            rect.anchorMax = anchor ?? new Vector2(0f, 1f);
            rect.pivot = anchor ?? new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(650f, 55f);
        }

        private static GameObject Spawn(string path, Vector3 position, Vector3 rotation, Vector3 scale, string objectName)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning("Missing Synty prefab: " + path);
                return null;
            }
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = objectName;
            instance.transform.position = position;
            instance.transform.eulerAngles = rotation;
            instance.transform.localScale = scale;
            return instance;
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
