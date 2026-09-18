using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace StoryCycling.Editor
{
    public static partial class CapeCrownSceneBuilder
    {
        private static string Generated;
        private static float relief;
        private const string Root = "Assets/Synty/PolygonCity/Prefabs/";
        private static readonly string[] Buildings = {
            Root + "Buildings/SM_Bld_Shop_03.prefab",
            Root + "Buildings/SM_Bld_Apartment_Stack_01.prefab"
        };
        private const string Tree = Root + "Environments/SM_Env_Tree_02.prefab";
        private static int assetId;

        [MenuItem("Story Cycling/Build Cape Crown Loop")]
        public static void BuildLoop() => BuildWorld(false);

        [MenuItem("Story Cycling/Build Camps Bay Promenade")]
        public static void BuildCampsBay() => BuildWorld(true);

        [MenuItem("Story Cycling/Build Camps Bay Trainer Ride")]
        public static void BuildHillRide() => BuildWorld(true, true);

        private static void BuildWorld(bool campsBay, bool hill = false)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building.");
            foreach (string path in campsBay ? CampsBayAssets : new[] { Buildings[0], Buildings[1], Tree })
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                    throw new InvalidOperationException("Missing asset: " + path);
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            relief = hill ? CapeCrownRoute.CoastalHillHeight : 0;
            Generated = hill ? "Assets/StoryCycling/GeneratedTrainerRide" : campsBay ? "Assets/StoryCycling/GeneratedCampsBay" : "Assets/StoryCycling/GeneratedLoop";
            string scenePath = hill ? "Assets/StoryCycling/Scenes/CampsBayTrainerRide.unity" : campsBay ? "Assets/StoryCycling/Scenes/CampsBayPromenade.unity" : "Assets/StoryCycling/Scenes/CapeCrownLoop.unity";
            Directory.CreateDirectory(Generated);
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            AssetDatabase.Refresh();
            assetId = 0;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Material asphalt = Mat("Asphalt", new Color(.12f, .15f, .18f));
            Material white = Mat("RoadPaint", new Color(.94f, .91f, .78f));
            Material paving = Mat("Promenade", new Color(.68f, .64f, .54f));
            Material grass = Mat("CoastalGrass", new Color(.37f, .48f, .31f));
            Material sand = Mat("Sand", new Color(.77f, .69f, .49f));
            Material ocean = Mat("Atlantic", new Color(.08f, .39f, .52f));
            if (campsBay) BuildCampsBayEnvironment();
            else
            {
                Box("Island", new Vector3(0,-.65f,0), new Vector3(148,1,330), sand);
                Box("Atlantic", new Vector3(0,-1.3f,0), new Vector3(1800,.2f,1800), ocean);
                Box("Central green", new Vector3(0,-.07f,0), new Vector3(65,.1f,188), grass);
            }

            if (hill) BuildCoastalHill();

            // Every strip is sampled from the same path as the rider. No prefab pivots.
            Strip("Continuous asphalt", -4f, 4f, .02f, 0, CapeCrownRoute.Length, asphalt);
            Strip("Inner promenade", -8f, -4f, .005f, 0, CapeCrownRoute.Length, paving);
            Strip("Outer promenade", 4f, 8f, .005f, 0, CapeCrownRoute.Length, paving);
            Strip("Inner edge", -3.7f, -3.57f, .03f, 0, CapeCrownRoute.Length, white);
            Strip("Outer edge", 3.57f, 3.7f, .03f, 0, CapeCrownRoute.Length, white);
            int dashCount = Mathf.RoundToInt(CapeCrownRoute.Length / 8f);
            float dashSpacing = CapeCrownRoute.Length / dashCount;
            for (int i = 0; i < dashCount; i++)
                Strip("Centre dash " + i, -.07f, .07f, .035f, i * dashSpacing, i * dashSpacing + 3f, white);
            for (int i = 0; i < 8; i++)
                Box("Start stripe", new Vector3(44.5f + i, .04f, -75f), new Vector3(1,.015f,.6f), i % 2 == 0 ? white : asphalt);

            // Bounded, measured city frontage; keep the entire cycling corridor clear.
            if (!campsBay) for (int i = 0; i < 7; i++)
            {
                GroundPrefab(Buildings[i % 2], new Vector3(22,0,-66 + i * 22), 90, 16f, "City frontage");
                GroundPrefab(Tree, new Vector3(37,0,-66 + i * 22), 0, 4f, "Promenade tree");
                GroundPrefab(Tree, new Vector3(-37,0,-66 + i * 22), i * 40, 4f, "Coastal tree");
            }
            if (!campsBay) for (int i = 0; i < 12; i++)
                GroundPrefab(Tree, new Vector3(-15 + (i % 3)*11,0,-70 + (i/3)*44), i*31, 5f, "Park tree");

            Lighting();
            if (campsBay) CoastalSky();
            Transform rider = AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation);
            GameObject cameraObject = new GameObject("Ride Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.fieldOfView = 58;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 700;
            camera.allowHDR = false;
            cameraObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing=true;
            var volume=new GameObject("Coastal colour grade").AddComponent<Volume>();volume.isGlobal=true;
            var profile=ScriptableObject.CreateInstance<VolumeProfile>();
            var grade=profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            grade.postExposure.Override(.2f);grade.contrast.Override(12);grade.saturation.Override(12);
            var bloom=profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(.35f);bloom.threshold.Override(.92f);
            var vignette=profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.Override(.28f);vignette.smoothness.Override(.4f);
            profile=Save(profile);AssetDatabase.AddObjectToAsset(grade,profile);AssetDatabase.AddObjectToAsset(bloom,profile);AssetDatabase.AddObjectToAsset(vignette,profile);volume.sharedProfile=profile;
            camera.clearFlags = campsBay ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.48f,.72f,.87f);
            CapeCrownRoute.Sample(0, out _, out Vector3 forward);
            rider.SetPositionAndRotation(CapeCrownRoute.Position(0,CapeCrownRoute.LaneOffset,.045f), Quaternion.LookRotation(forward));
            camera.transform.position = rider.position - forward*5.8f + Vector3.up*2.6f;
            camera.transform.LookAt(rider.position + forward*7 + Vector3.up*1.15f);

            var director = new GameObject("Cape Crown Ride Director").AddComponent<CapeCrownRideController>();
            SerializedObject data = new SerializedObject(director);
            data.FindProperty("hillHeight").floatValue = relief;
            data.FindProperty("rider").objectReferenceValue = rider;
            data.FindProperty("cyclistAnimation").objectReferenceValue = animation;
            data.FindProperty("routeLabel").stringValue = hill ? "CAMPS BAY • OCEAN & HILL" : campsBay ? "CAMPS BAY • PROMENADE" : "CAPE CROWN • COASTAL LOOP";
            data.FindProperty("rideCamera").objectReferenceValue = camera.transform;
            data.FindProperty("telemetry").objectReferenceValue = null;
            var array = data.FindProperty("wheels");
            array.arraySize = wheels.Length;
            for (int i=0;i<wheels.Length;i++) array.GetArrayElementAtIndex(i).objectReferenceValue = wheels[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            if (campsBay)
            {
                new GameObject("Cape Crown Devices").AddComponent<CapeCrownDevices>();
                director.gameObject.AddComponent<CapeCrownMusic>();
                director.gameObject.AddComponent<CapeCrownMobileHud>().Configure(director);
                director.gameObject.AddComponent<CapeCrownMobileQuality>();
            }
            CapeCrownValidation.ValidateRoute(relief);
            CapeCrownValidation.ValidateCyclist(animation);
            if (campsBay) ValidateCampsBayPlacement();
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath,true) };
            // Reopen the actual saved scene: references must survive serialization, not just exist in memory.
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
            Debug.Log(scenePath + " saved and set as build scene. Trainer-driven ride. Start via menu after fresh KICKR data.");
        }

        // Existing instructions remain usable, but produce the new scene.
        [MenuItem("Story Cycling/Build Cape Crown Vertical Slice")]
        public static void BuildCapeCrownVerticalSlice() => BuildLoop();

        private static void Strip(string name, float left, float right, float height, float start, float end, Material material)
        {
            int count = Mathf.CeilToInt((end-start)/1.5f);
            var vertices = new Vector3[(count+1)*2];
            var triangles = new int[count*6];
            for (int i=0;i<=count;i++)
            {
                float d = Mathf.Lerp(start,end,i/(float)count);
                vertices[2*i] = CapeCrownRoute.Position(d,left,height,relief);
                vertices[2*i+1] = CapeCrownRoute.Position(d,right,height,relief);
                if (i==count) continue;
                int a=2*i, t=6*i;
                triangles[t]=a; triangles[t+1]=a+2; triangles[t+2]=a+1;
                triangles[t+3]=a+1; triangles[t+4]=a+2; triangles[t+5]=a+3;
            }
            Mesh mesh = new Mesh { name=name, vertices=vertices, triangles=triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mesh = Save(mesh);
            GameObject go = new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh=mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial=material;
        }

        private static Transform AnimatedCyclist(out Transform[] wheels, out CapeCrownCyclistAnimation animation)
        {
            Transform anchor = new GameObject("Cyclist • stylised").transform;
            Transform root = new GameObject("Visual lean pivot").transform;
            root.SetParent(anchor, false);
            animation = anchor.gameObject.AddComponent<CapeCrownCyclistAnimation>();
            Material frame=Mat("Bike frame",new Color(.05f,.75f,.80f));
            Material accent=Mat("Frame accent",new Color(.95f,.42f,.13f));
            Material tire=Mat("Tires",new Color(.04f,.05f,.06f));
            Material rim=Mat("Rims",new Color(.90f,.45f,.12f));
            Material spokes=Mat("Spokes",new Color(.62f,.68f,.72f));
            Material jersey=Mat("Jersey",new Color(.95f,.38f,.15f));
            Material sleeve=Mat("Sleeve",new Color(.96f,.94f,.86f));
            Material shorts=Mat("Shorts",new Color(.10f,.14f,.20f));
            Material skin=Mat("Skin",new Color(.55f,.32f,.20f));
            Material helmet=Mat("Helmet",new Color(.96f,.94f,.86f));
            Material dark=Mat("Saddle grips",new Color(.07f,.08f,.10f));
            Vector3 rear=new Vector3(0,.34f,-.53f), front=new Vector3(0,.34f,.53f);
            Vector3 crank=new Vector3(0,.32f,0), seat=new Vector3(0,.88f,-.18f), neck=new Vector3(0,.85f,.39f);
            wheels=new Transform[2];
            for(int i=0;i<2;i++)
            {
                Transform wheel=new GameObject(i==0?"Rear wheel":"Front wheel").transform;
                wheel.SetParent(root,false); wheel.localPosition=i==0?rear:front; wheels[i]=wheel;
                for(int j=0;j<32;j++)
                {
                    float a=j*Mathf.PI*2/32, b=(j+1)*Mathf.PI*2/32;
                    Tube(wheel,new Vector3(0,Mathf.Cos(a),Mathf.Sin(a))*.32f,new Vector3(0,Mathf.Cos(b),Mathf.Sin(b))*.32f,.024f,tire);
                    if(j%2==0) Tube(wheel,new Vector3(0,Mathf.Cos(a),Mathf.Sin(a))*.27f,new Vector3(0,Mathf.Cos(b),Mathf.Sin(b))*.27f,.012f,rim);
                    if(j%4==0) Tube(wheel,Vector3.zero,new Vector3(0,Mathf.Cos(a),Mathf.Sin(a))*.26f,.004f,spokes);
                }
            }
            // Complete diamond frame with a coloured fork and rear triangle.
            Tube(root,crank,neck,.03f,frame);
            Tube(root,seat,neck,.03f,frame);
            Tube(root,crank,seat,.03f,accent);
            Tube(root,rear,seat,.022f,accent);
            Tube(root,rear,crank,.022f,frame);
            Tube(root,front,neck,.03f,frame);
            // Chainring, saddle and drop bars.
            Part(root,PrimitiveType.Cylinder,crank+Vector3.right*.03f,new Vector3(.03f,.14f,.14f),spokes);
            Part(root,PrimitiveType.Cube,seat+Vector3.up*.05f,new Vector3(.15f,.06f,.26f),dark);
            Tube(root,neck,new Vector3(0,1.03f,.44f),.022f,frame);
            Tube(root,new Vector3(-.24f,1.0f,.46f),new Vector3(.24f,1.0f,.46f),.022f,dark);
            Tube(root,new Vector3(-.24f,1.0f,.46f),new Vector3(-.24f,.93f,.52f),.014f,dark);
            Tube(root,new Vector3(.24f,1.0f,.46f),new Vector3(.24f,.93f,.52f),.014f,dark);
            // Posed rider: leaning torso, sleeved arms to the drops, proper helmet.
            Vector3 hip=new Vector3(0,1.02f,-.18f), shoulder=new Vector3(0,1.44f,.17f);
            Tube(root,hip,shoulder,.14f,jersey);
            Tube(root,shoulder,new Vector3(0,1.50f,.16f),.11f,sleeve);
            Part(root,PrimitiveType.Sphere,new Vector3(0,1.62f,.23f),new Vector3(.20f,.22f,.22f),skin);
            Part(root,PrimitiveType.Sphere,new Vector3(0,1.70f,.22f),new Vector3(.30f,.17f,.32f),helmet);
            Part(root,PrimitiveType.Sphere,new Vector3(0,1.79f,.18f),new Vector3(.17f,.06f,.20f),accent);
            var thighs = new Transform[2];
            var shins = new Transform[2];
            var feet = new Transform[2];
            var cranks = new Transform[2];
            var pedals = new Transform[2];
            var moving = new List<Transform>(wheels);
            for(int side=-1;side<=1;side+=2)
            {
                float x=side*.13f;
                Vector3 elbow=new Vector3(side*.2f,1.18f,.30f), hand=new Vector3(side*.23f,.96f,.52f);
                Tube(root,shoulder+Vector3.right*x,elbow,.05f,sleeve); Tube(root,elbow,hand,.036f,skin);
                int index = side == -1 ? 0 : 1;
                Vector3 knee=new Vector3(x,.67f,.16f), foot=new Vector3(x,.20f,-.03f);
                thighs[index] = Tube(root,hip+Vector3.right*x,knee,.07f,shorts);
                shins[index] = Tube(root,knee,foot,.045f,skin);
                feet[index] = Part(root,PrimitiveType.Cube,foot,new Vector3(.12f,.08f,.25f),dark).transform;
                cranks[index] = Tube(root,crank,foot,.014f,spokes);
                pedals[index] = Part(root,PrimitiveType.Cube,foot,new Vector3(.17f,.025f,.11f),accent).transform;
                moving.AddRange(new[] { thighs[index], shins[index], feet[index], cranks[index], pedals[index] });
            }
            foreach (Transform wheel in wheels) BatchParts(wheel, null);
            BatchParts(root, moving.ToArray());
            animation.Configure(root, thighs, shins, feet, cranks, pedals);
            animation.ApplyPose(0);
            return anchor;
        }

        private static void BatchParts(Transform root, Transform[] exclude)
        {
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var originals = new List<GameObject>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
            {
                bool skip = false;
                if (exclude != null)
                    foreach (Transform branch in exclude)
                        if (filter.transform.IsChildOf(branch)) skip = true;
                if (skip) continue;
                Material material = filter.GetComponent<Renderer>().sharedMaterial;
                if (!groups.ContainsKey(material)) groups.Add(material, new List<CombineInstance>());
                groups[material].Add(new CombineInstance { mesh = filter.sharedMesh,
                    transform = root.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                originals.Add(filter.gameObject);
            }
            foreach (var group in groups)
            {
                Mesh mesh = new Mesh { name = root.name + " " + group.Key.name };
                mesh.CombineMeshes(group.Value.ToArray(), true, true);
                mesh.RecalculateBounds(); mesh = Save(mesh);
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(root, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterial = group.Key;
            }
            foreach (GameObject go in originals) UnityEngine.Object.DestroyImmediate(go);
        }

        private static Transform Tube(Transform parent,Vector3 a,Vector3 b,float radius,Material material)
        {
            GameObject go=Part(parent,PrimitiveType.Cylinder,(a+b)*.5f,new Vector3(radius*2,(b-a).magnitude*.5f,radius*2),material);
            go.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
            return go.transform;
        }
        private static GameObject Part(Transform parent,PrimitiveType type,Vector3 position,Vector3 scale,Material material)
        {
            GameObject go=GameObject.CreatePrimitive(type);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent,false); go.transform.localPosition=position; go.transform.localScale=scale;
            go.GetComponent<Renderer>().sharedMaterial=material;
            return go;
        }
        private static void Box(string name,Vector3 position,Vector3 scale,Material material)
        { Part(null,PrimitiveType.Cube,position,scale,material).name=name; }

        private static GameObject GroundPrefab(string path,Vector3 position,float yaw,float maximumFootprint,string name)
        {
            GameObject go=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            go.name=name; go.transform.rotation=Quaternion.Euler(0,yaw,0);
            Bounds bounds=BoundsOf(go);
            float factor=Mathf.Min(1f,maximumFootprint/Mathf.Max(bounds.size.x,bounds.size.z));
            go.transform.localScale*=factor;
            bounds=BoundsOf(go);
            go.transform.position+=position-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            foreach (Collider collider in go.GetComponentsInChildren<Collider>()) collider.enabled=false;
            return go;
        }
        private static Bounds BoundsOf(GameObject go)
        {
            Renderer[] renderers=go.GetComponentsInChildren<Renderer>();
            if(renderers.Length==0) throw new InvalidOperationException("Prefab has no renderers: "+go.name);
            Bounds bounds=renderers[0].bounds;
            foreach(Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }
        private static void Lighting()
        {
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.62f,.74f,.88f);
            RenderSettings.ambientEquatorColor=new Color(.82f,.68f,.52f);
            RenderSettings.ambientGroundColor=new Color(.45f,.38f,.30f);
            RenderSettings.fog=true; RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogStartDistance=160; RenderSettings.fogEndDistance=720;
            RenderSettings.fogColor=new Color(.92f,.78f,.64f);
            Light sun=new GameObject("Golden hour sun").AddComponent<Light>();
            sun.type=LightType.Directional; sun.color=new Color(1f,.76f,.48f); sun.intensity=1.45f;
            sun.shadows=LightShadows.Soft; sun.transform.rotation=Quaternion.Euler(18,-55,0);
        }
        private static Text Hud()
        {
            var canvas=new GameObject("Ride HUD",typeof(Canvas),typeof(CanvasScaler));
            canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler=canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution=new Vector2(1280,720);
            var panel=new GameObject("Telemetry",typeof(Image)); panel.transform.SetParent(canvas.transform,false);
            panel.GetComponent<Image>().color=new Color(.03f,.09f,.12f,.85f);
            RectTransform rect=panel.GetComponent<RectTransform>(); rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=new Vector2(24,-24); rect.sizeDelta=new Vector2(650,132);
            var label=new GameObject("Speed and lap",typeof(Text)); label.transform.SetParent(panel.transform,false);
            Text text=label.GetComponent<Text>(); text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize=22; text.color=Color.white; text.text="CAPE CROWN • COASTAL LOOP\nPlay → W/↑ oder Leertaste";
            RectTransform tr=text.rectTransform; tr.anchorMin=Vector2.zero; tr.anchorMax=Vector2.one;
            tr.offsetMin=new Vector2(18,12); tr.offsetMax=new Vector2(-12,-12);
            return text;
        }
        private static Material Mat(string name,Color color)
        {
            Shader shader=Shader.Find("Universal Render Pipeline/Lit");
            if(shader==null) throw new InvalidOperationException("URP Lit shader unavailable.");
            var mat=new Material(shader){name=name,color=color}; mat.SetFloat("_Smoothness",.12f); return Save(mat);
        }
        private static T Save<T>(T asset) where T : UnityEngine.Object
        {
            string extension=asset is Material?"mat":"asset";
            string path=$"{Generated}/loop-{assetId++:D3}.{extension}";
            T existing=AssetDatabase.LoadAssetAtPath<T>(path);
            if(existing!=null)
            {
                EditorUtility.CopySerialized(asset,existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(asset);
                return existing;
            }
            AssetDatabase.CreateAsset(asset,path);
            return asset;
        }
    }
}
