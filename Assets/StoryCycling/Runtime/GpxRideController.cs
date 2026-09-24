using UnityEngine;

namespace StoryCycling
{
    // Minimal ride controller for testing a GPX track. Demo-only: speed comes from an
    // adjustable field (HUD slider), no trainer/telemetry wiring. Loops the open track.
    public sealed class GpxRideController : MonoBehaviour
    {
        [SerializeField] private Transform rider;
        [SerializeField] private Transform rideCamera;
        [SerializeField] private Transform[] wheels;
        [SerializeField] private CapeCrownCyclistAnimation cyclistAnimation;
        [SerializeField] private float maxSpeedKph = 200f;
        [Header("Speed feel camera")]
        [SerializeField] private float cruiseFov = 58f;
        [SerializeField] private float maxSpeedFov = 78f;
        [SerializeField] private float cruiseCameraDistance = 5.8f;
        [SerializeField] private float maxSpeedCameraDistance = 4.3f;
        [SerializeField] private float cruiseCameraHeight = 2.6f;
        [SerializeField] private float maxSpeedCameraHeight = 1.8f;
        [SerializeField] private float cruiseLookAhead = 7f;
        [SerializeField] private float maxSpeedLookAhead = 32f;

        private float speedKph = 30f, routeDistance, totalMetres;
        private Camera rideCameraComponent;
        public float SpeedKph => speedKph;
        public float TotalMetres => totalMetres;
        public float Length => GpxRide.Length;

        public void SetSpeed(float kph) => speedKph = Mathf.Clamp(kph, 0f, maxSpeedKph);

        private void Start()
        {
            GpxRide.Load("Routes/Nordhoek.gpx");
            if (rideCamera != null) rideCameraComponent = rideCamera.GetComponent<Camera>();
            if (cyclistAnimation != null) CapeCrownCyclistAnimation.ForwardOverride = GpxRideForward;
            PlaceRider();
            UpdateCamera(true);
        }

        private void OnDestroy() => CapeCrownCyclistAnimation.ForwardOverride = null;

        private Vector3 GpxRideForward(float d) { GpxRide.Sample(d, out _, out Vector3 f); return f; }

        private void Update()
        {
            if (!GpxRide.IsLoaded) return;
            float metres = speedKph / 3.6f * Time.deltaTime;
            totalMetres += metres;
            routeDistance = Mathf.Repeat(routeDistance + metres, GpxRide.Length);
            PlaceRider();
            if (cyclistAnimation != null)
            {
                cyclistAnimation.SetVisible(true);
                cyclistAnimation.Tick(speedKph, routeDistance, speedKph > .1f, Time.deltaTime, -1);
            }
            if (wheels != null) foreach (var w in wheels) if (w != null) w.Rotate(Vector3.right, metres / .34f * Mathf.Rad2Deg, Space.Self);
        }

        private void PlaceRider()
        {
            if (rider == null) return;
            GpxRide.Sample(routeDistance, out _, out Vector3 forward);
            rider.SetPositionAndRotation(GpxRide.Position(routeDistance, GpxRide.LaneOffset, .045f), Quaternion.LookRotation(forward, Vector3.up));
        }

        private void LateUpdate() => UpdateCamera(false);

        private void UpdateCamera(bool snap)
        {
            if (rider == null || rideCamera == null) return;
            // Actual progression remains exactly speed / 3.6 metres per second. These values only
            // strengthen optical speed cues at high demo speeds.
            float speedT = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(25f, maxSpeedKph, speedKph));
            float cameraDistance = Mathf.Lerp(cruiseCameraDistance, maxSpeedCameraDistance, speedT);
            float cameraHeight = Mathf.Lerp(cruiseCameraHeight, maxSpeedCameraHeight, speedT);
            float lookAhead = Mathf.Lerp(cruiseLookAhead, maxSpeedLookAhead, speedT);
            Vector3 position = rider.position - rider.forward * cameraDistance + Vector3.up * cameraHeight;
            float lookD = Mathf.Min(routeDistance + lookAhead, GpxRide.Length);
            Vector3 look = GpxRide.Position(lookD, GpxRide.LaneOffset, 1.15f);
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
