using UnityEngine;

namespace StoryCycling
{
    // Visual rig only: no forces or steering are applied to the proven route anchor.
    public sealed class CapeCrownCyclistAnimation : MonoBehaviour
    {
        [SerializeField] private Transform visual;
        [SerializeField] private Transform[] thighs, shins, feet, cranks, pedals;
        [SerializeField] private Transform[] upperArms, foreArms;
        private float phase, cadence, lean;
        public const float LegLength = .49f;
        public const float CrankRadius = .17f;
        public bool IsConfigured => visual != null && Valid(thighs) && Valid(shins) &&
            Valid(feet) && Valid(cranks) && Valid(pedals);

        private static bool Valid(Transform[] parts) => parts != null && parts.Length == 2 && parts[0] != null && parts[1] != null;

        public void Configure(Transform pivot, Transform[] upper, Transform[] lower,
            Transform[] shoes, Transform[] arms, Transform[] platforms,
            Transform[] upperArms = null, Transform[] foreArms = null)
        {
            visual = pivot; thighs = upper; shins = lower; feet = shoes;
            cranks = arms; pedals = platforms;
            this.upperArms = upperArms; this.foreArms = foreArms;
        }

        public void MenuWave(float time)
        {
            if (!IsConfigured || upperArms == null || foreArms == null) return;
            if (upperArms.Length < 2 || foreArms.Length < 2 || upperArms[1] == null || foreArms[1] == null) return;
            float wave = Mathf.Sin(time * 7f);
            // Right arm raised + waving; left arm stays on the bars.
            Vector3 shoulder = new Vector3(.13f, 1.44f, .17f);
            Vector3 elbow = new Vector3(.30f, 1.34f, .10f);
            Vector3 hand = new Vector3(.34f + wave * .15f, 1.74f, .08f);
            PoseTube(upperArms[1], shoulder, elbow);
            PoseTube(foreArms[1], elbow, hand);
        }

        public void ResetArms()
        {
            if (upperArms == null || foreArms == null) return;
            for (int i = 0; i < 2; i++)
            {
                if (upperArms[i] == null || foreArms[i] == null) continue;
                int side = i == 0 ? -1 : 1;
                Vector3 shoulder = new Vector3(side * .13f, 1.44f, .17f);
                Vector3 elbow = new Vector3(side * .2f, 1.18f, .30f);
                Vector3 hand = new Vector3(side * .23f, .96f, .52f);
                PoseTube(upperArms[i], shoulder, elbow);
                PoseTube(foreArms[i], elbow, hand);
            }
        }

        public void Tick(float speedKph, float distance, bool pedalling, float deltaTime, float measuredCadence = -1)
        {
            if (!IsConfigured) return;
            ResetArms(); // arms return to the handlebars while riding
            // Measured cadence drives the rig; cosmetic fallback only when the trainer omits it.
            float target = pedalling && speedKph > .1f ? (measuredCadence >= 0 ? measuredCadence : Mathf.Lerp(35, 95, Mathf.Clamp01(speedKph / 40))) : 0;
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
