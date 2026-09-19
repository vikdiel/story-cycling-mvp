using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StoryCycling.Editor
{
    // Runs the actual runtime scene selector, demo controls and full laps, from a fresh
    // play-mode domain. A batch scene save alone cannot prove any of these behaviours.
    [InitializeOnLoad]
    public static class CapeCrownCollectionReview
    {
        private const string Pending = "CapeCollectionReview";
        private const string Folder = "Logs/CollectionReview";
        private static bool running;
        private static int index, stage, shot;
        private static double since, deadline;
        private static float stopped;
        private static Vector3 startPosition;
        private static readonly float[] Views = { .025f, .06f, .18f, .46f, .76f };
        static CapeCrownCollectionReview()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += state => {
                if(state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending,false))
                {
                    SessionState.SetBool(Pending,false); running=true; index=stage=shot=0;
                    since=EditorApplication.timeSinceStartup;deadline=since+900;
                    Application.runInBackground=true;Time.timeScale=40;
                    int q=Array.IndexOf(QualitySettings.names,"Mobile");if(q>=0)QualitySettings.SetQualityLevel(q,true);
                }
            };
        }
        [MenuItem("Story Cycling/QA/Review All Routes")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Folder+"/result.txt","RUNNING\n");
            CapeCrownSceneBuilder.SetAllScenesInBuildSettings();
            EditorSceneManager.OpenScene(CapeCrownIPadBuild.ScenePath);
            SessionState.SetBool(Pending,true);EditorApplication.isPlaying=true;
        }
        private static void Require(bool condition,string text) { if(!condition)throw new InvalidOperationException(text); }
        private static CapeCrownRideController Ride => UnityEngine.Object.FindAnyObjectByType<CapeCrownRideController>();
        private static void Tick()
        {
            if(!running||!EditorApplication.isPlaying||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            try
            {
                Require(EditorApplication.timeSinceStartup<deadline,"Collection timeout");
                double elapsed=EditorApplication.timeSinceStartup-since;
                var entry=CapeCrownRoutes.All[index];
                var ride=Ride;if(ride==null)return;
                var ui=UnityEngine.Object.FindAnyObjectByType<CapeCrownMobileHud>();
                var root=GameObject.Find("Cape Crown Interface");if(root==null||(stage!=1&&elapsed<.6))return;
                if(stage==0)
                {
                    Require(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name==entry.sceneName,"Wrong loaded scene "+entry.sceneName);
                    Require(CapeCrownRoute.IsDefined&&CapeCrownRoute.Length>500,"Runtime route not restored");
                    Require(!ride.Started&&!ride.TryStart(),"Real ride starts without trainer");
                    Require(root.GetComponentsInChildren<Text>().Any(t=>t.text.Contains(entry.label.ToUpper().Split(' ')[0])||t.text.Contains(ride.RouteLabel.Split('•')[0].Trim())),"Route title missing");
                    Require(UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Count(c=>c.CompareTag("MainCamera"))==1,"Duplicate ride cameras");
                    foreach(var text in root.GetComponentsInChildren<Text>())Require(!text.text.Contains("0,63 km"),"Obsolete route length");
                    var life=UnityEngine.Object.FindAnyObjectByType<CapeCrownLife>();
                    Require(life!=null,"Life absent");
                    var serialized=new SerializedObject(life);
                    Require(serialized.FindProperty("ride").objectReferenceValue!=null&&serialized.FindProperty("walkers").arraySize>0,"Life references not persisted");
                    Capture(Folder+"/"+entry.sceneName+"-menu.png");
                    Click("Demo-Fahrt (ohne KICKR)");
                    Require(ride.Started&&ride.IsDemo,"Demo button did not start");
                    startPosition=UnityEngine.Object.FindAnyObjectByType<CapeCrownCyclistAnimation>().transform.position;
                    stage=1;shot=0;since=EditorApplication.timeSinceStartup;
                }
                else if(stage==1)
                {
                    if(shot<Views.Length&&ride.RouteDistance>=CapeCrownRoute.Length*Views[shot])
                    {Capture(Folder+"/"+entry.sceneName+"-ride-"+shot+".png");shot++;}
                    if(ride.CompletedLaps<1)return;
                    Require(ride.TotalMetres>500&&ride.TrainingMetres==0,"Demo progress invalid");
                    Require((UnityEngine.Object.FindAnyObjectByType<CapeCrownCyclistAnimation>().transform.position-startPosition).sqrMagnitude>.01,"Rider stuck");
                    Click("Pause");Require(ride.IsPaused,"Pause button failed");stopped=ride.TotalMetres;
                    stage=2;since=EditorApplication.timeSinceStartup;
                }
                else if(stage==2)
                {
                    Require(Mathf.Abs(ride.TotalMetres-stopped)<.001,"Paused ride advances");
                    Click("Weiterfahren");Require(!ride.IsPaused,"Resume failed");
                    stage=3;since=EditorApplication.timeSinceStartup;
                }
                else if(stage==3)
                {
                    Require(ride.TotalMetres>stopped,"Resume did not move");
                    ride.EndRide();ride.StopDemo();stage=4;since=EditorApplication.timeSinceStartup;
                }
                else if(stage==4)
                {
                    Click("Routen");stage=5;since=EditorApplication.timeSinceStartup;
                }
                else if(stage==5)
                {
                    Require(!ride.Started,"Ride not ended");
                    if(index==0)Capture(Folder+"/route-selector.png");
                    foreach(var route in CapeCrownRoutes.All)Require(Application.CanStreamedLevelBeLoaded(route.sceneName),"Scene omitted from build: "+route.sceneName);
                    File.AppendAllText(Folder+"/result.txt",$"PASS {entry.sceneName}: {CapeCrownRoute.Length:0}m, runtime scene restore, menu title, demo full lap, pause/resume, 0 training metres, {shot} ride captures, life serialization.\n");
                    index++;
                    if(index>=CapeCrownRoutes.All.Length)
                    {
                        File.AppendAllText(Folder+"/result.txt","PASS ALL "+CapeCrownRoutes.All.Length+" ROUTES. Mobile URP in Editor. Not physical iPad performance or BLE.\n");
                        running=false;Time.timeScale=1;EditorApplication.isPlaying=false;
                        if(Application.isBatchMode)EditorApplication.Exit(0);
                        return;
                    }
                    Click(CapeCrownRoutes.All[index].label);stage=0;since=EditorApplication.timeSinceStartup;
                }
            }
            catch(Exception e)
            {
                File.AppendAllText(Folder+"/result.txt","FAIL "+e+"\n");Debug.LogException(e);
                running=false;Time.timeScale=1;EditorApplication.isPlaying=false;
                if(Application.isBatchMode)EditorApplication.Exit(1);
            }
        }
        private static void Click(string name)
        {
            var target=GameObject.Find(name);Require(target!=null,"Button not visible: "+name);
            var button=target.GetComponent<Button>();Require(button!=null&&button.IsInteractable(),"Button disabled: "+name);
            Canvas.ForceUpdateCanvases();
            var rect=button.GetComponent<RectTransform>();
            Vector2 position=RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(rect.rect.center));
            var pointer=new PointerEventData(EventSystem.current){position=position,button=PointerEventData.InputButton.Left};
            var hits=new System.Collections.Generic.List<RaycastResult>();EventSystem.current.RaycastAll(pointer,hits);
            Require(hits.Count>0&&hits[0].gameObject.GetComponentInParent<Button>()==button,"Button obscured or outside view: "+name);
            ExecuteEvents.Execute(button.gameObject,pointer,ExecuteEvents.pointerClickHandler);
        }
        private static void Capture(string path)
        {
            var camera=Camera.main;Require(camera!=null,"No main camera");
            var target=RenderTexture.GetTemporary(1440,900,24);var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;
            var texture=new Texture2D(1440,900,TextureFormat.RGB24,false);
            var canvas=GameObject.Find("Cape Crown Interface").GetComponent<Canvas>();
            try
            {
                canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=.5f;Canvas.ForceUpdateCanvases();
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                texture.ReadPixels(new Rect(0,0,1440,900),0,0);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());
            }
            finally
            {canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.worldCamera=null;camera.targetTexture=oldTarget;RenderTexture.active=oldActive;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(texture);}
        }
    }
}
