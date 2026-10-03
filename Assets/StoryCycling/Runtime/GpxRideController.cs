using UnityEngine;

namespace StoryCycling
{
    // Fahrt auf einer GPX-Welt: Tempo vom Trainer (CapeCrownDevices, FTMS) bzw. Demo-Fahrt, Start/Pause/Ende über das Menü (CapeCrownMobileHud).
    // Offene Strecke: am Ziel endet die Runde (zurück ins Menü); geschlossene Runde (Start und Ziel < 30 m auseinander): weiterfahren.
    public sealed class GpxRideController : MonoBehaviour, IRideSession
    {
        [SerializeField] private string gpxRelPath = "Routes/Nordhoek.gpx";   // relativ zu StreamingAssets (vom Builder gesetzt)
        [SerializeField] private string routeLabel = "NORDHOEK";
        [SerializeField] private Transform rider;
        [SerializeField] private Transform rideCamera;
        [SerializeField] private Transform[] wheels;
        [SerializeField] private CapeCrownCyclistAnimation cyclistAnimation;
        // Fixed demo ceiling: do not serialize this. Existing scenes still carry the former
        // inspector value (100), which would otherwise silently clamp the HUD slider.
        private const float MaxSpeedKph = 200f;
        [Header("Speed feel camera")]
        [SerializeField] private float cruiseFov = 58f;
        [SerializeField] private float maxSpeedFov = 78f;
        [SerializeField] private float cruiseCameraDistance = 5.8f;
        [SerializeField] private float maxSpeedCameraDistance = 4.3f;
        [SerializeField] private float cruiseCameraHeight = 2.6f;
        [SerializeField] private float maxSpeedCameraHeight = 1.8f;
        [SerializeField] private float cruiseLookAhead = 7f;
        [SerializeField] private float maxSpeedLookAhead = 32f;
        [Header("Demo-Fahrt (ohne Trainer)")]
        [SerializeField] private float demoMinSpeed = 12f;
        [SerializeField] private float demoMaxSpeed = 60f;
        [SerializeField] private float demoSpeedPeriod = 120f;

        private CapeCrownDevices devices;
        private TrainerControl trainer;
        private float physV;                                   // m/s, Fahrphysik
        private CapeCrownMusic music;
        private Camera rideCameraComponent;
        private float speedKph, routeDistance, totalMetres, demoSpeed, demoElapsed, demoSpeedTarget;
        private bool demoManual, paused, closedLoop;
        private int laps;

        public bool Started { get; private set; }
        public bool IsPaused => paused;
        public string PauseReason { get; private set; } = "";
        public bool CanStart => !RuntimeWorldBuilder.Building && devices != null && (devices.FreshSpeed || devices.FreshPower);
        public bool IsDemo => devices != null && devices.IsTestFeed;
        public string RouteLabel => routeLabel;
        public string RouteDescription => $"GPX-Welt · {Length / 1000f:0.0} km" + (laps > 0 ? $"\nZiel erreicht · {laps}× gefahren" : "");
        public bool HasRouteList => true;                   // Routenwahl: CapeCrownRoutes.All (derzeit nur Nordhoek)
        public float RouteDistance => routeDistance;
        public float RouteLength => Length;
        public int CompletedLaps => laps;
        public float SpeedKph => speedKph;
        public float TotalMetres => totalMetres;
        public float Length => GpxRide.Length;

        public float CurrentGrade
        {
            get
            {
                if (!GpxRide.IsLoaded) return 0f;
                float d0 = Mathf.Max(0f, routeDistance - 15f), d1 = Mathf.Min(Length, routeDistance + 25f);
                return d1 - d0 > 1f ? (GpxRide.Spline.SamplePosition(d1).y - GpxRide.Spline.SamplePosition(d0).y) / (d1 - d0) : 0f;
            }
        }
        public float HeightAhead(float metres)
        {
            if (!GpxRide.IsLoaded) return 0f;
            float d = routeDistance + metres;
            d = closedLoop ? Mathf.Repeat(d, Length) : Mathf.Min(d, Length);
            return GpxRide.Spline.SamplePosition(d).y;
        }

        public bool TryStart()
        {
            if (!CanStart || !GpxRide.IsLoaded) return false;
            Started = true; paused = false; PauseReason = "";
            routeDistance = totalMetres = 0f;
            PlaceRider(); UpdateCamera(true);
            if (music != null) music.SetRiding(true);
            return true;
        }
        public void Pause(string reason = "Pausiert") { if (!Started) return; paused = true; speedKph = 0f; PauseReason = reason; }
        public bool Resume() { if (!CanStart) return false; paused = false; PauseReason = ""; return true; }
        public void EndRide() { Started = false; paused = false; speedKph = 0f; if (music != null) music.SetRiding(false); }
        public void StartDemo() { if (devices == null) return; demoSpeed = 0f; demoElapsed = 0f; demoManual = false; devices.StartDemoFeed(); }
        public void StopDemo() { if (devices == null) return; devices.StopDemoFeed(); demoSpeed = 0f; if (Started) EndRide(); }
        public void SetDemoSpeed(float kph) { demoManual = true; demoSpeedTarget = Mathf.Clamp(kph, 0f, MaxSpeedKph); }
        public void ResetDemoSpeed() { demoManual = false; demoElapsed = 0f; }
        public void SetSpeed(float kph) => SetDemoSpeed(kph);  // altes Test-HUD (GpxTestHud)

        private void OnApplicationFocus(bool focus) { if (!focus) Pause("App war im Hintergrund"); }
        private void OnApplicationPause(bool value) { if (value) Pause("App war im Hintergrund"); }

        private void Awake()
        {
            // Ältere GPX-Szenen (vor dem Menü-Umbau): Testpanel aus, Geräte + Originalmenü zur Laufzeit dazu. Neu gebaute Szenen haben beides fest drin.
            var test = GetComponent<GpxTestHud>(); if (test != null) test.enabled = false;
            devices = FindAnyObjectByType<CapeCrownDevices>();
            if (devices == null) devices = new GameObject("Cape Crown Devices").AddComponent<CapeCrownDevices>();
            if (FindAnyObjectByType<CapeCrownMusic>() == null) gameObject.AddComponent<CapeCrownMusic>();          // Fahrmusik auch in älteren Szenen
            if (FindAnyObjectByType<CapeCrownMobileHud>() == null) gameObject.AddComponent<CapeCrownMobileHud>();
            trainer = GetComponent<TrainerControl>(); if (trainer == null) trainer = gameObject.AddComponent<TrainerControl>();
        }

        private void Start()
        {
            music = FindAnyObjectByType<CapeCrownMusic>();
            GpxRide.Load(gpxRelPath);
            CheckLoop();
            if (rideCamera != null) rideCameraComponent = rideCamera.GetComponent<Camera>();
            if (cyclistAnimation != null) CapeCrownCyclistAnimation.ForwardOverride = GpxRideForward;
            PlaceRider();
            UpdateCamera(true);
        }

        private void CheckLoop()
        {
            if (!GpxRide.IsLoaded) return;
            Vector3 a = GpxRide.Spline.SamplePosition(0f), b = GpxRide.Spline.SamplePosition(Length); a.y = b.y = 0f;
            closedLoop = (a - b).sqrMagnitude < 30f * 30f;
        }

        // Weltbau auf dem Gerät fertig: Fahrlinie liegt jetzt auf dem OSM-Netz -> Fahrer an den Start, Kamera dazu
        public void OnRouteChanged()
        {
            routeDistance = 0f; CheckLoop();
            PlaceRider(); UpdateCamera(true);
        }

        private void OnDestroy() { CapeCrownCyclistAnimation.ForwardOverride = null; GpxRide.ClearRide(); }

        private Vector3 GpxRideForward(float d) { GpxRide.Sample(d, out _, out Vector3 f); return f; }

        private void Update()
        {
            if (!GpxRide.IsLoaded) return;
            if (devices != null && devices.IsTestFeed)
            {
                if (demoManual) demoSpeed = Mathf.MoveTowards(demoSpeed, demoSpeedTarget, 80f * Time.deltaTime);
                else
                {
                    demoElapsed += Time.deltaTime;
                    float cycle = Mathf.Repeat(demoElapsed, demoSpeedPeriod) / demoSpeedPeriod;
                    demoSpeed = Mathf.Lerp(demoMinSpeed, demoMaxSpeed, .5f - .5f * Mathf.Cos(cycle * Mathf.PI * 2f));
                }
                devices.TickDemo(demoSpeed);
            }
            if (Started && !paused && !CanStart) Pause("Trainerdaten fehlen – bitte Verbindung prüfen");
            // Tempo: mit Leistungsmesser (Trainer) aus der Fahrphysik (Leistung, Steigung, Luft- und Rollwiderstand) — wie bei Zwift, und nötig für ERG;
            // Demo-Fahrt bzw. Trainer ohne Leistungswert: Tempo direkt übernehmen
            bool riding = Started && !paused && CanStart;
            if (riding && !devices.IsTestFeed && devices.FreshPower && trainer != null) { physV = trainer.StepSpeed(physV, devices.Watts, CurrentGrade, Time.deltaTime); speedKph = physV * 3.6f; }
            else { speedKph = riding ? Mathf.Min(devices.Speed, MaxSpeedKph) : 0f; physV = speedKph / 3.6f; }
            bool pedalling = speedKph > .1f && (devices.FreshCadence ? devices.Cadence > 0f : devices.FreshPower && devices.Watts > 0f);

            float metres = speedKph / 3.6f * Time.deltaTime;
            totalMetres += metres;
            routeDistance += metres;
            if (routeDistance >= Length)
            {
                laps++;
                if (closedLoop) routeDistance = Mathf.Repeat(routeDistance, Length);
                else { routeDistance = Length; EndRide(); routeDistance = 0f; }      // Ziel: zurück ins Menü, nächste Runde startet am Anfang
            }
            if (Started) GpxRide.ReportRide(routeDistance, speedKph / 3.6f); else GpxRide.ClearRide();
            PlaceRider();
            if (cyclistAnimation != null)
            {
                cyclistAnimation.SetVisible(true);
                cyclistAnimation.Tick(speedKph, routeDistance, pedalling, Time.deltaTime, devices.FreshCadence ? devices.Cadence : -1);
            }
            if (wheels != null) foreach (var w in wheels) if (w != null) w.Rotate(Vector3.right, metres / .34f * Mathf.Rad2Deg, Space.Self);
        }

        private void PlaceRider()
        {
            if (rider == null || !GpxRide.IsLoaded) return;
            GpxRide.Sample(routeDistance, out _, out Vector3 forward);
            rider.SetPositionAndRotation(GpxRide.Position(routeDistance, GpxRide.LaneOffsetAt(routeDistance), .045f), Quaternion.LookRotation(forward, Vector3.up));
        }

        private void LateUpdate() => UpdateCamera(false);

        private void UpdateCamera(bool snap)
        {
            if (rider == null || rideCamera == null || !GpxRide.IsLoaded) return;
            // Actual progression remains exactly speed / 3.6 metres per second. These values only
            // strengthen optical speed cues at high speeds.
            float speedT = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(25f, MaxSpeedKph, speedKph));
            float cameraDistance = Mathf.Lerp(cruiseCameraDistance, maxSpeedCameraDistance, speedT);
            float cameraHeight = Mathf.Lerp(cruiseCameraHeight, maxSpeedCameraHeight, speedT);
            float lookAhead = Mathf.Lerp(cruiseLookAhead, maxSpeedLookAhead, speedT);
            Vector3 position = rider.position - rider.forward * cameraDistance + Vector3.up * cameraHeight;
            float lookD = Mathf.Min(routeDistance + lookAhead, GpxRide.Length);
            Vector3 look = GpxRide.Position(lookD, GpxRide.LaneOffsetAt(lookD), 1.15f);
            float blend = snap ? 1f : 1f - Mathf.Exp(-8f * Time.deltaTime);
            rideCamera.position = Vector3.Lerp(rideCamera.position, position, blend);
            rideCamera.rotation = Quaternion.Slerp(rideCamera.rotation, Quaternion.LookRotation(look - rideCamera.position, Vector3.up), blend);
            if (rideCameraComponent != null)
            {
                float targetFov = Mathf.Lerp(cruiseFov, maxSpeedFov, speedT);
                float fovBlend = snap ? 1f : 1f - Mathf.Exp(-6f * Time.deltaTime);
                rideCameraComponent.fieldOfView = Mathf.Lerp(rideCameraComponent.fieldOfView, targetFov, fovBlend);
            }
        }
    }
}
