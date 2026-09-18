using UnityEngine;

namespace StoryCycling
{
    // Pooled street life: pedestrians walk the sidewalks and are only active near the
    // rider, so a populated section never exceeds a small active-object budget on iPad.
    public sealed class CapeCrownLife : MonoBehaviour
    {
        private CapeCrownRideController ride;
        private Transform[] walkers;
        private float[] distances, offsets, speeds;
        private int[] directions;
        private const float ActivationRadius = 50f;

        public void Configure(CapeCrownRideController controller, Transform[] pedestrians,
            float[] dists, float[] offs, int[] dirs)
        {
            ride = controller;
            walkers = pedestrians;
            distances = dists;
            offsets = offs;
            directions = dirs;
            speeds = new float[dirs == null ? 0 : dirs.Length];
            for (int i = 0; i < speeds.Length; i++) speeds[i] = 1.1f + (i % 4) * 0.35f;
        }

        private void Update()
        {
            if (ride == null || walkers == null) return;
            float rider = ride.RouteDistance;
            float bob = Mathf.Sin(Time.time * 5f) * 0.045f;
            for (int i = 0; i < walkers.Length; i++)
            {
                if (walkers[i] == null) continue;
                float delta = Mathf.Abs(distances[i] - rider);
                float wrapped = Mathf.Min(delta, CapeCrownRoute.Length - delta);
                bool active = wrapped < ActivationRadius;
                if (walkers[i].gameObject.activeSelf != active) walkers[i].gameObject.SetActive(active);
                if (!active) continue;
                distances[i] = Mathf.Repeat(distances[i] + directions[i] * speeds[i] * Time.deltaTime, CapeCrownRoute.Length);
                CapeCrownRoute.Sample(distances[i], out Vector3 p, out Vector3 fwd, ride.HillHeight);
                Vector3 hf = new Vector3(fwd.x, 0f, fwd.z);
                if (hf.sqrMagnitude < 1e-6f) hf = Vector3.forward;
                hf.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, hf);
                walkers[i].position = p + side * offsets[i] + Vector3.up * bob;
                walkers[i].rotation = Quaternion.LookRotation(hf * directions[i], Vector3.up);
            }
        }
    }
}
