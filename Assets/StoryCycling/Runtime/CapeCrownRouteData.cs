using UnityEngine;

namespace StoryCycling
{
    // Carries the route definition (waypoints + hill profile) into play mode.
    // The static route engine is rebuilt in Awake from this serialized data, because
    // the editor-only Define() call is not persisted in the scene.
    [DefaultExecutionOrder(-1000)]
    public sealed class CapeCrownRouteData : MonoBehaviour
    {
        [SerializeField] private Vector2[] waypoints;
        [SerializeField] private float[] hillStart, hillEnd, hillWeight;

        private void Awake()
        {
            if (waypoints == null || waypoints.Length < 3) return;
            CapeCrownRoute.Define(waypoints);
            int n = Mathf.Min(
                hillStart == null ? 0 : hillStart.Length,
                hillEnd == null ? 0 : hillEnd.Length,
                hillWeight == null ? 0 : hillWeight.Length);
            CapeCrownRoute.Hills = new (float, float, float)[0];
            if (n > 0)
            {
                var hills = new (float, float, float)[n];
                for (int i = 0; i < n; i++) hills[i] = (hillStart[i], hillEnd[i], hillWeight[i]);
                CapeCrownRoute.Hills = hills;
            }
        }
    }
}
