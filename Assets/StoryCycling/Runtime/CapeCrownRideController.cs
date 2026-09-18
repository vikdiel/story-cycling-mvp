using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    public sealed class CapeCrownRideController : MonoBehaviour
    {
        [SerializeField] private Transform rider;
        [SerializeField] private Transform rideCamera;
        [SerializeField] private Transform[] wheels;
        [SerializeField] private Text telemetry;
        [SerializeField] private CapeCrownCyclistAnimation cyclistAnimation;
        [SerializeField] private string routeLabel = "CAMPS BAY";
        [SerializeField] private float hillHeight;
        private CapeCrownDevices devices;
        private CapeCrownMusic music;
        private float routeDistance, totalMetres, trainingMetres, trainerSpeedKph, demoSpeed;
        private int laps;
        private bool paused;
        public string RouteLabel => routeLabel;
        public bool Started { get; private set; }
        public string PauseReason { get; private set; }
        public float RouteDistance => routeDistance;
        public float TotalMetres => totalMetres;
        public float TrainingMetres => trainingMetres;
        public int CompletedLaps => laps;
        public float HillHeight => hillHeight;
        public bool IsPaused => paused;
        public bool IsSimulation => devices != null && devices.IsTestFeed;
        public float TrainerSpeedKph => trainerSpeedKph;
        public bool CanStart => devices != null && devices.FreshSpeed;
        public bool IsDemo => devices != null && devices.IsTestFeed;
        public bool TryStart()
        {
            if(!CanStart)return false;
            Started=true;paused=false;PauseReason="";
            routeDistance=totalMetres=trainingMetres=0; laps=0;
            PlaceRider();UpdateCamera(true);
            if(music!=null)music.SetRiding(true);
            return true;
        }
        public void Pause(string reason="Pausiert") { if(!Started)return;paused=true;trainerSpeedKph=0;PauseReason=reason; }
        public bool Resume() { if(!CanStart)return false;paused=false;PauseReason="";return true; }
        public void EndRide() { Started=false;paused=false;trainerSpeedKph=0; if(music!=null)music.SetRiding(false); }
        public void StartDemo() { if(devices==null)return; demoSpeed=0; devices.StartDemoFeed(); }
        public void StopDemo() { if(devices==null)return; devices.StopDemoFeed(); demoSpeed=0; if(Started)EndRide(); }
        public void TogglePause() { if(paused)Resume();else Pause(); }
        private void OnApplicationFocus(bool focus) { if(!focus)Pause("App war im Hintergrund"); }
        private void OnApplicationPause(bool value) { if(value)Pause("App war im Hintergrund"); }
        private void Start()
        {
            devices=FindAnyObjectByType<CapeCrownDevices>();
            music=GetComponent<CapeCrownMusic>();
            if(telemetry!=null)telemetry.transform.root.gameObject.SetActive(false);
            PlaceRider();UpdateCamera(true);
        }
        private void Update()
        {
            if(devices!=null && devices.IsTestFeed)
            {
                demoSpeed=Mathf.MoveTowards(demoSpeed,30f,10f*Time.deltaTime);
                devices.TickDemo(demoSpeed);
            }
            if(Started && !paused && !CanStart)Pause("Trainerdaten fehlen – bitte Verbindung prüfen");
            trainerSpeedKph=Started && !paused && CanStart?devices.Speed:0;
            bool pedalling=trainerSpeedKph>.1f && (devices.FreshCadence?devices.Cadence>0:devices.FreshPower&&devices.Watts>0);
            float metres=trainerSpeedKph/3.6f*Time.deltaTime;
            totalMetres+=metres;
            if(!IsSimulation)trainingMetres+=metres;
            routeDistance=CapeCrownRoute.Advance(routeDistance,metres,CapeCrownRoute.LaneOffset,hillHeight);
            if(routeDistance>=CapeCrownRoute.Length) { laps+=Mathf.FloorToInt(routeDistance/CapeCrownRoute.Length);routeDistance=Mathf.Repeat(routeDistance,CapeCrownRoute.Length); }
            PlaceRider();
            if(cyclistAnimation!=null)cyclistAnimation.Tick(trainerSpeedKph,routeDistance,pedalling,Time.deltaTime,devices!=null&&devices.FreshCadence?devices.Cadence:-1);
            if(wheels!=null)foreach(var wheel in wheels)if(wheel!=null)wheel.Rotate(Vector3.right,metres/.34f*Mathf.Rad2Deg,Space.Self);
        }
        private void PlaceRider()
        {
            if(rider==null)return;
            CapeCrownRoute.Sample(routeDistance,out _,out Vector3 forward,hillHeight);
            rider.SetPositionAndRotation(CapeCrownRoute.Position(routeDistance,CapeCrownRoute.LaneOffset,.045f,hillHeight),Quaternion.LookRotation(forward,Vector3.up));
        }
        private void LateUpdate()=>UpdateCamera(false);
        private void UpdateCamera(bool snap)
        {
            if(rider==null||rideCamera==null)return;
            Vector3 position=rider.position-rider.forward*5.8f+Vector3.up*2.6f;
            float blend=snap?1:1-Mathf.Exp(-8*Time.deltaTime);
            rideCamera.position=Vector3.Lerp(rideCamera.position,position,blend);
            Vector3 look=CapeCrownRoute.Position(routeDistance+7,CapeCrownRoute.LaneOffset,1.15f,hillHeight);
            rideCamera.rotation=Quaternion.Slerp(rideCamera.rotation,Quaternion.LookRotation(look-rideCamera.position,Vector3.up),blend);
        }
    }
}
