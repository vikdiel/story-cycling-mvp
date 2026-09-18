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
        [SerializeField] private float trainerSpeedKph;
        [SerializeField] private bool keyboardSimulation = true;
        private float routeDistance;
        private float totalMetres;
        private bool demoCruise;
        private int laps;

        public float TrainerSpeedKph => trainerSpeedKph;
        public void SetTrainerSpeed(float speedKph)
        {
            keyboardSimulation = false;
            demoCruise = false;
            trainerSpeedKph = float.IsNaN(speedKph) || float.IsInfinity(speedKph) ? 0f : Mathf.Clamp(speedKph, 0f, 90f);
        }

        private void Start()
        {
            PlaceRider();
            UpdateCamera(true);
        }

        private void Update()
        {
            if (keyboardSimulation)
            {
                Keyboard k = Keyboard.current;
                if (k != null && k.spaceKey.wasPressedThisFrame) demoCruise = !demoCruise;
                bool accelerate = k != null && (k.wKey.isPressed || k.upArrowKey.isPressed);
                bool brake = k != null && (k.sKey.isPressed || k.downArrowKey.isPressed);
                if (brake) demoCruise = false;
                float target = brake ? 0f : accelerate ? 50f : demoCruise ? 25f : 0f;
                trainerSpeedKph = Mathf.MoveTowards(trainerSpeedKph, target, (brake ? 25f : accelerate ? 12f : 3f) * Time.deltaTime);
            }
            float metres = trainerSpeedKph / 3.6f * Time.deltaTime;
            totalMetres += metres;
            routeDistance = CapeCrownRoute.Advance(routeDistance, metres, CapeCrownRoute.LaneOffset);
            if (routeDistance >= CapeCrownRoute.Length)
            {
                laps += Mathf.FloorToInt(routeDistance / CapeCrownRoute.Length);
                routeDistance = Mathf.Repeat(routeDistance, CapeCrownRoute.Length);
            }
            PlaceRider();
            if (wheels != null)
                foreach (Transform wheel in wheels)
                    if (wheel != null) wheel.Rotate(Vector3.right, metres / 0.34f * Mathf.Rad2Deg, Space.Self);
            if (telemetry != null)
                telemetry.text = $"{trainerSpeedKph:0} km/h    {totalMetres / 1000f:0.00} km    Runde {laps + 1}\n" +
                    (keyboardSimulation ? "DEMO • W/↑ fahren · S/↓ bremsen · Leertaste 25 km/h" : "TRAINER");
        }

        private void PlaceRider()
        {
            if (rider == null) return;
            CapeCrownRoute.Sample(routeDistance, out _, out Vector3 forward);
            rider.SetPositionAndRotation(CapeCrownRoute.Position(routeDistance, CapeCrownRoute.LaneOffset, 0.045f), Quaternion.LookRotation(forward, Vector3.up));
        }

        private void LateUpdate() => UpdateCamera(false);
        private void UpdateCamera(bool snap)
        {
            if (rider == null || rideCamera == null) return;
            Vector3 position = rider.position - rider.forward * 5.8f + Vector3.up * 2.6f;
            float blend = snap ? 1f : 1f - Mathf.Exp(-8f * Time.deltaTime);
            rideCamera.position = Vector3.Lerp(rideCamera.position, position, blend);
            Vector3 look = CapeCrownRoute.Position(routeDistance + 7f, CapeCrownRoute.LaneOffset, 1.15f);
            rideCamera.rotation = Quaternion.Slerp(rideCamera.rotation, Quaternion.LookRotation(look - rideCamera.position, Vector3.up), blend);
        }
    }
}
