using UnityEngine;
using UnityEngine.InputSystem;
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
        [SerializeField] private float demoMinSpeed = 12f;   // km/h — low end of the variable demo speed
        [SerializeField] private float demoMaxSpeed = 100f;  // km/h — top speed the demo reaches
        [SerializeField] private float demoSpeedPeriod = 120f; // seconds for one full speed cycle
        private float demoElapsed;
        private bool demoManual;
        private float demoSpeedTarget;
        public bool DemoManual => demoManual;
        private int laps;
        private bool paused;
        private float orbitYaw;
        private Vector2 lastDrag;
        private bool dragging;
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
        public void StartDemo() { if(devices==null)return; demoSpeed=0; demoElapsed=0; demoManual=false; devices.StartDemoFeed(); }
        public void StopDemo() { if(devices==null)return; devices.StopDemoFeed(); demoSpeed=0; if(Started)EndRide(); }
        public void TogglePause() { if(paused)Resume();else Pause(); }
        private void OnApplicationFocus(bool focus) { if(!focus)Pause("App war im Hintergrund"); }
        private void OnApplicationPause(bool value) { if(value)Pause("App war im Hintergrund"); }
        // Manual speed override from the demo HUD slider (0–100 km/h).
        public void SetDemoSpeed(float kph) { demoManual=true; demoSpeedTarget=Mathf.Clamp(kph,0f,demoMaxSpeed); }
        public void ResetDemoSpeed() { demoManual=false; demoElapsed=0; }
        private void Start()
        {
            devices=FindAnyObjectByType<CapeCrownDevices>();
            music=GetComponent<CapeCrownMusic>();
            if(telemetry!=null)telemetry.transform.root.gameObject.SetActive(false);
            PlaceRider();UpdateCamera(true);
        }
        private void Update()
        {
            UpdateOrbitInput();
            if(devices!=null && devices.IsTestFeed)
            {
                if(demoManual)
                {
                    // Slider-driven: glide toward the target speed.
                    demoSpeed=Mathf.MoveTowards(demoSpeed,demoSpeedTarget,80f*Time.deltaTime);
                }
                else
                {
                    // Variable demo speed: a smooth cycle from demoMinSpeed up to demoMaxSpeed
                    // (100 km/h) so the ride accelerates, cruises and eases off like real riding.
                    demoElapsed += Time.deltaTime;
                    float cycle = Mathf.Repeat(demoElapsed, demoSpeedPeriod) / demoSpeedPeriod;
                    demoSpeed = Mathf.Lerp(demoMinSpeed, demoMaxSpeed, 0.5f - 0.5f * Mathf.Cos(cycle * Mathf.PI * 2f));
                }
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
            if(cyclistAnimation!=null)
            {
                if(Started)
                {
                    cyclistAnimation.SetVisible(true);
                    cyclistAnimation.Tick(trainerSpeedKph,routeDistance,pedalling,Time.deltaTime,devices!=null&&devices.FreshCadence?devices.Cadence:-1);
                }
                else cyclistAnimation.SetVisible(false);
            }
            if(wheels!=null)foreach(var wheel in wheels)if(wheel!=null)wheel.Rotate(Vector3.right,metres/.34f*Mathf.Rad2Deg,Space.Self);
        }
        private void PlaceRider()
        {
            if(rider==null)return;
            CapeCrownRoute.Sample(routeDistance,out _,out Vector3 forward,hillHeight);
            Vector3 facing = Started ? forward : -forward; // menu: turn to face the camera
            rider.SetPositionAndRotation(CapeCrownRoute.Position(routeDistance,CapeCrownRoute.LaneOffset,.045f,hillHeight),Quaternion.LookRotation(facing,Vector3.up));
        }
        private void LateUpdate()=>UpdateCamera(false);
        private void UpdateOrbitInput()
        {
            if (!Started) { orbitYaw = 0f; dragging = false; return; }
            Vector2 cur; bool press;
            if (Touchscreen.current != null)
            {
                var touch = Touchscreen.current.primaryTouch;
                press = touch.press.isPressed;
                cur = touch.position.ReadValue();
            }
            else if (Mouse.current != null)
            {
                press = Mouse.current.leftButton.isPressed;
                cur = Mouse.current.position.ReadValue();
            }
            else { press = false; cur = Vector2.zero; }
            if (press)
            {
                if (!dragging) { dragging = true; lastDrag = cur; }
                else
                {
                    float dx = cur.x - lastDrag.x;
                    orbitYaw += dx * 0.3f;
                    lastDrag = cur;
                }
            }
            else dragging = false;
        }
        private void UpdateCamera(bool snap)
        {
            if(rider==null||rideCamera==null)return;
            Vector3 position, look;
            if(!Started)
            {
                position=rider.position+rider.forward*2.4f+rider.right*2.4f+Vector3.up*1.55f;
                look=rider.position+Vector3.up*1.1f;
            }
            else
            {
                position=rider.position-rider.forward*5.8f+Vector3.up*2.6f;
                look=CapeCrownRoute.Position(routeDistance+7,CapeCrownRoute.LaneOffset,1.15f,hillHeight);
            }
            float blend=snap?1:1-Mathf.Exp(-8*Time.deltaTime);
            if (Started && Mathf.Abs(orbitYaw) > .001f)
            {
                Vector3 pivot = rider.position + Vector3.up * 1.2f;
                position = pivot + Quaternion.Euler(0f, orbitYaw, 0f) * (position - pivot);
                look = pivot;
            }
            rideCamera.position=Vector3.Lerp(rideCamera.position,position,blend);
            rideCamera.rotation=Quaternion.Slerp(rideCamera.rotation,Quaternion.LookRotation(look-rideCamera.position,Vector3.up),blend);
        }
    }
}
