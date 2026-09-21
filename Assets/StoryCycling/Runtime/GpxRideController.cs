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
        [SerializeField] private float maxSpeedKph = 100f;

        private float speedKph = 30f, routeDistance, totalMetres;
        public float SpeedKph => speedKph;
        public float TotalMetres => totalMetres;
        public float Length => GpxRide.Length;

        public void SetSpeed(float kph) => speedKph = Mathf.Clamp(kph, 0f, maxSpeedKph);

        private void Start()
        {
            GpxRide.Load("Routes/Nordhoek.gpx");
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
            Vector3 position = rider.position - rider.forward * 5.8f + Vector3.up * 2.6f;
            float lookD = Mathf.Min(routeDistance + 7f, GpxRide.Length);
            Vector3 look = GpxRide.Position(lookD, GpxRide.LaneOffset, 1.15f);
            float blend = snap ? 1f : 1f - Mathf.Exp(-8f * Time.deltaTime);
            rideCamera.position = Vector3.Lerp(rideCamera.position, position, blend);
            rideCamera.rotation = Quaternion.Slerp(rideCamera.rotation, Quaternion.LookRotation(look - rideCamera.position, Vector3.up), blend);
        }
    }
}
