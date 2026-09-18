using UnityEngine;

namespace StoryCycling
{
    public sealed class CapeCrownMobileQuality : MonoBehaviour
    {
        private void Awake()
        {
            Application.targetFrameRate = 30; // Device profiling gate; not a claim of measured FPS.
            if (Application.isMobilePlatform)
            {
                int mobile = System.Array.IndexOf(QualitySettings.names, "Mobile");
                if (mobile >= 0) QualitySettings.SetQualityLevel(mobile, true);
            }
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }
        private void OnDestroy() => Screen.sleepTimeout = SleepTimeout.SystemSetting;
    }
}
