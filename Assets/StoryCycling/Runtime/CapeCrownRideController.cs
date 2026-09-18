using UnityEngine;

namespace StoryCycling
{
    /// <summary>
    /// Owns the virtual ride. A future iOS Bluetooth bridge calls SetTrainerSpeed;
    /// until then the inspector slider or W/Up Arrow lets us test the route.
    /// </summary>
    public sealed class CapeCrownRideController : MonoBehaviour
    {
        [SerializeField] private Transform rider;
        [SerializeField] private Transform rideCamera;
        [SerializeField, Range(0f, 70f)] private float trainerSpeedKph;
        [SerializeField] private bool keyboardSimulation = true;
        [SerializeField] private float cameraHeight = 3.5f;
        [SerializeField] private float cameraDistance = 7.5f;

        public float TrainerSpeedKph => trainerSpeedKph;

        public void SetTrainerSpeed(float speedKph)
        {
            trainerSpeedKph = Mathf.Clamp(speedKph, 0f, 90f);
        }

        private void Update()
        {
            if (keyboardSimulation)
            {
                float input = Input.GetAxis("Vertical");
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) input = 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) input = -1f;
                trainerSpeedKph = Mathf.Clamp(trainerSpeedKph + input * 22f * Time.deltaTime, 0f, 55f);
                trainerSpeedKph = Mathf.MoveTowards(trainerSpeedKph, 0f, 3f * Time.deltaTime);
            }

            if (rider == null) return;
            rider.position += Vector3.forward * (trainerSpeedKph / 3.6f) * Time.deltaTime;
            rider.Rotate(0f, 0f, Mathf.Sin(Time.time * trainerSpeedKph * 0.16f) * 0.08f);
        }

        private void LateUpdate()
        {
            if (rideCamera == null || rider == null) return;
            Vector3 targetPosition = rider.position + Vector3.up * cameraHeight - Vector3.forward * cameraDistance;
            rideCamera.position = Vector3.Lerp(rideCamera.position, targetPosition, 5f * Time.deltaTime);
            rideCamera.LookAt(rider.position + Vector3.up * 1.05f + Vector3.forward * 8f);
        }
    }
}
