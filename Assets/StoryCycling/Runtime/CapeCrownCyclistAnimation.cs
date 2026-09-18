using UnityEngine;

namespace StoryCycling
{
    // Visual rig only: no forces or steering are applied to the proven route anchor.
    public sealed class CapeCrownCyclistAnimation : MonoBehaviour
    {
        [SerializeField] private Transform visual;
        [SerializeField] private Transform[] thighs, shins, feet, cranks, pedals;
        private float phase, cadence, lean;
        public const float LegLength = .49f;
        public const float CrankRadius = .17f;
        public bool IsConfigured => visual != null && Valid(thighs) && Valid(shins) &&
            Valid(feet) && Valid(cranks) && Valid(pedals);

        private static bool Valid(Transform[] parts) => parts != null && parts.Length == 2 && parts[0] != null && parts[1] != null;

        public void Configure(Transform pivot, Transform[] upper, Transform[] lower,
            Transform[] shoes, Transform[] arms, Transform[] platforms)
        {
            visual = pivot; thighs = upper; shins = lower; feet = shoes;
            cranks = arms; pedals = platforms;
        }

        public void Tick(float speedKph, float distance, bool pedalling, float deltaTime)
        {
            if (!IsConfigured) return;
            // Cosmetic cadence until a real cadence channel is bridged; do not expose as telemetry.
            float target = pedalling && speedKph > .1f ? Mathf.Lerp(35, 95, Mathf.Clamp01(speedKph / 40)) : 0;
            cadence = Mathf.MoveTowards(cadence, target, 180 * deltaTime);
            phase = Mathf.Repeat(phase + cadence / 60 * Mathf.PI * 2 * deltaTime, Mathf.PI * 2);
            ApplyPose(phase);

            CapeCrownRoute.Sample(distance, out _, out Vector3 forward);
            CapeCrownRoute.Sample(distance + 6, out _, out Vector3 ahead);
            float curvature = Vector3.SignedAngle(forward, ahead, Vector3.up) * Mathf.Deg2Rad / 6;
            float metresPerSecond = speedKph / 3.6f;
            float targetLean = Mathf.Clamp(-Mathf.Atan(metresPerSecond * metresPerSecond * curvature / 9.81f) * Mathf.Rad2Deg, -24, 24);
            lean = Mathf.Lerp(lean, targetLean, 1 - Mathf.Exp(-5 * deltaTime));
            float sway = Mathf.Sin(phase) * .6f * Mathf.Clamp01(cadence / 60);
            visual.localRotation = Quaternion.Euler(0, 0, lean + sway);
        }

        public static Vector3 PedalPosition(int side, float angle) => new Vector3(side * .14f,
            .32f + CrankRadius * Mathf.Cos(angle), CrankRadius * Mathf.Sin(angle));

        public static Vector3 KneePosition(Vector3 hip, Vector3 ankle)
        {
            Vector3 delta = ankle - hip;
            Vector3 pole = Vector3.ProjectOnPlane(Vector3.forward, delta.normalized).normalized;
            float height = Mathf.Sqrt(Mathf.Max(0, LegLength * LegLength - delta.sqrMagnitude * .25f));
            return (hip + ankle) * .5f + pole * height;
        }

        public void ApplyPose(float angle)
        {
            if (!IsConfigured) return;
            for (int i = 0; i < 2; i++)
            {
                int side = i == 0 ? -1 : 1;
                Vector3 pedal = PedalPosition(side, angle + i * Mathf.PI);
                Vector3 ankle = pedal + Vector3.up * .07f;
                Vector3 hip = new Vector3(side * .13f, 1.02f, -.18f);
                Vector3 knee = KneePosition(hip, ankle);
                PoseTube(thighs[i], hip, knee);
                PoseTube(shins[i], knee, ankle);
                PoseTube(cranks[i], new Vector3(side * .14f, .32f, 0), pedal);
                feet[i].localPosition = ankle + Vector3.forward * .04f;
                pedals[i].localPosition = pedal;
            }
        }

        private static void PoseTube(Transform part, Vector3 a, Vector3 b)
        {
            part.localPosition = (a + b) * .5f;
            part.localRotation = Quaternion.FromToRotation(Vector3.up, b - a);
            Vector3 scale = part.localScale;
            scale.y = Vector3.Distance(a, b) * .5f;
            part.localScale = scale;
        }
    }
}
