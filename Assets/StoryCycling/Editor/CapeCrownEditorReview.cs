using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling.Editor
{
    [InitializeOnLoad]
    public static class CapeCrownEditorReview
    {
        private const string Request="Temp/cape-review-request.txt";
        private static bool reviewing,qualityChanged;
        private static int stage,shot,previousQuality;
        private static double deadline, stageAt;
        private static float stoppedAt;
        private static readonly float[] checkpoints={35,245,397,525,621};
        static CapeCrownEditorReview() { EditorApplication.update+=Tick;EditorApplication.playModeStateChanged+=Changed; }
        private static void Changed(PlayModeStateChange s)
        {
            if(s==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool("CapeReviewPending",false))
            {
                SessionState.SetBool("CapeReviewPending",false);previousQuality=QualitySettings.GetQualityLevel();qualityChanged=true;
                int mobile=Array.IndexOf(QualitySettings.names,"Mobile");if(mobile>=0)QualitySettings.SetQualityLevel(mobile,true);
                reviewing=true;stage=shot=0;deadline=EditorApplication.timeSinceStartup+160;stageAt=EditorApplication.timeSinceStartup;Time.timeScale=6;Application.runInBackground=true;
            }
            if(s==PlayModeStateChange.ExitingPlayMode) { Time.timeScale=1;if(qualityChanged)QualitySettings.SetQualityLevel(previousQuality,true);qualityChanged=false;reviewing=false; }
        }
        [MenuItem("Story Cycling/QA/Review Trainer Experience")]
        public static void Review()
        {
            if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop play first.");
            EditorSceneManager.OpenScene(CapeCrownIPadBuild.ScenePath);
            CapeCrownValidation.ValidateRoute(CapeCrownRoute.CoastalHillHeight);
            Directory.CreateDirectory("Logs/ExperienceReview");File.WriteAllText("Logs/ExperienceReview/result.txt","RUNNING");
            CheckPackets();SessionState.SetBool("CapeReviewPending",true);EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            if(File.Exists(Request))
            {
                string command=File.ReadAllText(Request).Trim();File.Delete(Request);
                try
                {
                    if(command=="build")CapeCrownSceneBuilder.BuildHillRide();
                    else if(command=="prepare")CapeCrownIPadBuild.Prepare();
                    else if(command=="export")CapeCrownIPadBuild.Export();
                    else if(command=="review")Review();
                    else if(command=="stop")EditorApplication.isPlaying=false;
                    else if(command=="play")EditorApplication.isPlaying=true;
                    else if(command=="capture")Capture("Logs/ExperienceReview/current.png");
                    else if(command=="bounds")InspectPrefabs();
                    else throw new InvalidOperationException("Unknown review command.");
                    File.WriteAllText("Temp/cape-review-result.txt",command+" PASS "+DateTime.UtcNow.ToString("O"));
                }
                catch(Exception e){Debug.LogException(e);File.WriteAllText("Temp/cape-review-result.txt",command+" FAIL "+e);}
            }
            if(!reviewing||!EditorApplication.isPlaying)return;
            try
            {
                var ride=UnityEngine.Object.FindAnyObjectByType<CapeCrownRideController>();var devices=UnityEngine.Object.FindAnyObjectByType<CapeCrownDevices>();
                if(ride==null||devices==null)throw new Exception("Ride/devices absent.");
                if(EditorApplication.timeSinceStartup>deadline)throw new Exception("Full-lap timeout, stage "+stage);
                if(stage==0 && EditorApplication.timeSinceStartup-stageAt>1)
                {
                    Require(!ride.CanStart&&!ride.TryStart()&&ride.TotalMetres==0,"Ride started without trainer");
                    var audio=UnityEngine.Object.FindAnyObjectByType<CapeCrownMusic>();
                    var source=audio.GetComponent<AudioSource>();
                    Require(source.clip!=null && source.clip.length>60 && source.isPlaying,"Music source not playing");
                    float oldVolume=audio.Volume;bool oldMute=audio.Muted;
                    audio.ToggleMute();Require(audio.Muted!=oldMute,"Mute failed");audio.ToggleMute();
                    audio.SetVolume(.2f);Require(Mathf.Abs(audio.Volume-.2f)<.001,"Volume failed");audio.SetVolume(oldVolume);
                    Capture("Logs/ExperienceReview/menu.png");
                    UnityEngine.Object.FindAnyObjectByType<CapeCrownMobileHud>().ShowSettings();stage=1;stageAt=EditorApplication.timeSinceStartup;
                }
                else if(stage==1&&EditorApplication.timeSinceStartup-stageAt>.7)
                {
                    Capture("Logs/ExperienceReview/devices.png");
                    GameObject.Find("Schließen").GetComponent<Button>().onClick.Invoke();
                    devices.ReviewFeed(25);stage=2;stageAt=EditorApplication.timeSinceStartup;
                }
                else if(stage==2&&EditorApplication.timeSinceStartup-stageAt>.5)
                {
                    devices.ReviewFeed(25);GameObject.Find("Runde starten").GetComponent<Button>().onClick.Invoke();Require(ride.Started,"Start button failed");stage=3;
                }
                else if(stage==3)
                {
                    devices.ReviewFeed(25);
                    if(shot<checkpoints.Length&&ride.RouteDistance>=checkpoints[shot]){Capture("Logs/ExperienceReview/ride-"+shot+".png");shot++;}
                    if(ride.CompletedLaps>=1) { Require(ride.TrainingMetres==0,"Test feed earned training progress");devices.ReviewExpire();stage=4;stageAt=EditorApplication.timeSinceStartup; }
                }
                else if(stage==4&&EditorApplication.timeSinceStartup-stageAt>.5)
                {
                    Require(ride.IsPaused&&ride.TrainerSpeedKph==0,"Missing telemetry did not stop rider");
                    stoppedAt=ride.TotalMetres;Capture("Logs/ExperienceReview/disconnected.png");stage=5;stageAt=EditorApplication.timeSinceStartup;
                }
                else if(stage==5&&EditorApplication.timeSinceStartup-stageAt>.5)
                {
                    Require(Mathf.Abs(ride.TotalMetres-stoppedAt)<.001,"Distance advanced without data");
                    devices.ReviewFeed(0);Require(ride.Resume(),"Resume failed");stage=6;stageAt=EditorApplication.timeSinceStartup;
                }
                else if(stage==6&&EditorApplication.timeSinceStartup-stageAt>.5)
                {
                    Require(Mathf.Abs(ride.TotalMetres-stoppedAt)<.001,"Zero speed moved rider");
                    ride.Pause();devices.ReviewFeed(25);stage=7;stageAt=EditorApplication.timeSinceStartup;
                }
                else if(stage==7&&EditorApplication.timeSinceStartup-stageAt>.5)
                {
                    Require(Mathf.Abs(ride.TotalMetres-stoppedAt)<.001,"Pause moved rider");ride.EndRide();
                    File.WriteAllText("Logs/ExperienceReview/result.txt",$"PASS {ride.CompletedLaps} full lap; {ride.TotalMetres:0.0} m; {shot} ride views; start gate, menus, telemetry fixtures, stale stop, zero speed, pause, no test progress, music playback/mute/volume. Mobile URP. Editor-only test feed; NOT physical BLE, iPad FPS or Xcode compilation.\n");
                    reviewing=false;Time.timeScale=1;EditorApplication.isPlaying=false;
                }
            }
            catch(Exception e)
            {
                Directory.CreateDirectory("Logs/ExperienceReview");File.WriteAllText("Logs/ExperienceReview/result.txt","FAIL "+e);Debug.LogException(e);reviewing=false;Time.timeScale=1;EditorApplication.isPlaying=false;
            }
        }
        private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        private static void CheckPackets()
        {
            Require(CapeCrownTelemetry.TryBike(new byte[]{0x44,0,0xc4,9,160,0,210,0},out var full)&&full.speed==25&&full.power==210&&full.cadence==80,"FTMS speed/cadence/power");
            Require(CapeCrownTelemetry.TryBike(new byte[]{0x41,0,175,0},out var split)&&!split.hasSpeed&&split.power==175,"Split packet");
            Require(!CapeCrownTelemetry.TryBike(new byte[]{0x44,0,0xc4,9,160},out _),"Truncated FTMS accepted");
            Require(CapeCrownTelemetry.TryHeart(new byte[]{0,123},out int bpm)&&bpm==123,"8-bit HR");
            Require(CapeCrownTelemetry.TryHeart(new byte[]{1,190,0},out bpm)&&bpm==190,"16-bit HR");
            Require(!CapeCrownTelemetry.TryHeart(new byte[]{1,190},out _)&&!CapeCrownTelemetry.TryHeart(new byte[]{4,100},out _),"Truncated/no-contact HR");
        }
        private static void Capture(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Camera camera=Camera.main;var target=RenderTexture.GetTemporary(1440,900,24);var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;
            var texture=new Texture2D(1440,900,TextureFormat.RGB24,false);
            Canvas canvas=GameObject.Find("Cape Crown Interface")?.GetComponent<Canvas>();
            try
            {
                if(canvas!=null){canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=.5f;Canvas.ForceUpdateCanvases();}
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,1440,900),0,0);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());
            }
            finally
            {
                if(canvas!=null){canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.worldCamera=null;}
                camera.targetTexture=oldTarget;RenderTexture.active=oldActive;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        private static void InspectPrefabs()
        {
            string output="";
            foreach(string n in new[]{"SM_Bld_Shop_01","SM_Bld_Shop_03","SM_Bld_Shop_05","SM_Bld_Shop_06","SM_Bld_Apartment_Stack_01"})
            {
                var p=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Synty/PolygonCity/Prefabs/Buildings/"+n+".prefab");var go=UnityEngine.Object.Instantiate(p);var rs=go.GetComponentsInChildren<Renderer>();var b=rs[0].bounds;foreach(var r in rs)b.Encapsulate(r.bounds);output+=n+" "+b+"\n";UnityEngine.Object.DestroyImmediate(go);
            }
            File.WriteAllText("Logs/prefab-bounds.txt",output);
        }
    }
}
