using UnityEngine;

namespace StoryCycling
{
    // Drives a real Synty humanoid rider through Unity's humanoid IK:
    // feet follow the pedals, hands grip the bars, plus a fixed cycling crouch.
    // Visual rig only: no forces or steering are applied to the proven route anchor.
    public sealed class CapeCrownCyclistAnimation : MonoBehaviour
    {
        [SerializeField] private Transform visual;       // "Visual lean pivot"
        [SerializeField] private Animator animator;
        [SerializeField] private Transform[] cranks, pedals;

        private float phase, cadence, lean;
        private static readonly Vector3 SeatedPos = new Vector3(0f, .93f, -.18f);

        // Retained for the validation maths and the procedural crank visuals.
        public const float LegLength = .49f;
        public const float CrankRadius = .17f;

        // Local-space (visual pivot) targets, matching the existing bike geometry.
        private Vector3 lhHand = new Vector3(-.23f, .96f, .52f);
        private Vector3 rhHand = new Vector3(.23f, .96f, .52f);
        private Vector3 lElbow = new Vector3(-.22f, 1.10f, .28f);
        private Vector3 rElbow = new Vector3(.22f, 1.10f, .28f);
        private Vector3 lKneeHint = new Vector3(-.28f, .72f, .40f);
        private Vector3 rKneeHint = new Vector3(.28f, .72f, .40f);

        public bool IsConfigured => visual != null && animator != null && Valid(cranks) && Valid(pedals);

        private static bool Valid(Transform[] parts) => parts != null && parts.Length == 2 && parts[0] != null && parts[1] != null;

        private void Awake()
        {
            // Safety net: re-resolve the Animator at runtime in case the editor-serialised
            // reference into the prefab instance did not survive the scene reload.
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
        }

        public void Configure(Transform pivot, Animator a, Transform[] crankSet, Transform[] pedalSet)
        {
            visual = pivot; animator = a; cranks = crankSet; pedals = pedalSet;
        }

        public void ResetArms()
        {
            lhHand = new Vector3(-.23f, .96f, .52f);
            rhHand = new Vector3(.23f, .96f, .52f);
        }

        public void SetVisible(bool visible)
        {
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator != null) animator.gameObject.SetActive(visible);
        }

        private void LateUpdate()
        {
            if (!IsConfigured) return;
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;
            // The Synty head is rigged facing backward; yaw it 180° around its own
            // up axis so the face points along the direction of travel. A local-space
            // yaw is robust against the rig's non-standard muscle-space axes, which
            // is why SetBoneLocalRotation (muscle space) kept mis-pitching the head.
            head.Rotate(0f, 180f, 0f, Space.Self);
        }

        public void Tick(float speedKph, float distance, bool pedalling, float deltaTime, float measuredCadence = -1)
        {
            if (!IsConfigured) return;
            animator.transform.localPosition = SeatedPos;
            animator.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            ResetArms();
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
            if (cranks == null || pedals == null) return;
            for (int i = 0; i < 2; i++)
            {
                int side = i == 0 ? -1 : 1;
                Vector3 pedal = PedalPosition(side, angle + i * Mathf.PI);
                Vector3 bracket = new Vector3(side * .14f, .32f, 0);
                if (cranks[i] != null) PoseTube(cranks[i], bracket, pedal);
                if (pedals[i] != null) pedals[i].localPosition = pedal;
            }
        }

        public void ApplyIK(Animator a)
        {
            if (!IsConfigured) return;
            float angle = phase;
            // Feet onto the pedals, knees biased forward/out.
            for (int i = 0; i < 2; i++)
            {
                int side = i == 0 ? -1 : 1;
                Vector3 pedal = visual.TransformPoint(PedalPosition(side, angle + i * Mathf.PI));
                AvatarIKGoal foot = i == 0 ? AvatarIKGoal.LeftFoot : AvatarIKGoal.RightFoot;
                a.SetIKPositionWeight(foot, 1f);
                a.SetIKRotationWeight(foot, 1f);
                a.SetIKPosition(foot, pedal + visual.up * .02f);
                a.SetIKRotation(foot, visual.rotation);
                AvatarIKHint knee = i == 0 ? AvatarIKHint.LeftKnee : AvatarIKHint.RightKnee;
                a.SetIKHintPositionWeight(knee, 1f);
                a.SetIKHintPosition(knee, visual.TransformPoint(i == 0 ? lKneeHint : rKneeHint));
            }
            // Hands onto the drops.
            a.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
            a.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
            a.SetIKPosition(AvatarIKGoal.LeftHand, visual.TransformPoint(lhHand));
            a.SetIKRotation(AvatarIKGoal.LeftHand, visual.rotation);
            a.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
            a.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
            a.SetIKPosition(AvatarIKGoal.RightHand, visual.TransformPoint(rhHand));
            a.SetIKRotation(AvatarIKGoal.RightHand, visual.rotation);
            a.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 1f);
            a.SetIKHintPosition(AvatarIKHint.LeftElbow, visual.TransformPoint(lElbow));
            a.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 1f);
            a.SetIKHintPosition(AvatarIKHint.RightElbow, visual.TransformPoint(rElbow));
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
