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
        private Transform spine, chest, neck, head;
        private Quaternion spineBase, chestBase, neckBase, headBase;
        private bool bonesReady;

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

        public void MenuWave(float time)
        {
            if (!IsConfigured) return;
            float wave = Mathf.Sin(time * 7f);
            rhHand = new Vector3(.36f + wave * .13f, 1.72f, .10f);
        }

        public void ResetArms()
        {
            lhHand = new Vector3(-.23f, .96f, .52f);
            rhHand = new Vector3(.23f, .96f, .52f);
        }

        public void Tick(float speedKph, float distance, bool pedalling, float deltaTime, float measuredCadence = -1)
        {
            if (!IsConfigured) return;
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
            // Hands onto the drops (or raised in a wave while in the menu).
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

        private void LateUpdate()
        {
            if (!IsConfigured) return;
            EnsureBones();
            // Cycling crouch: lean the torso forward around the bike's X axis (visual.right),
            // keeping the head roughly level to the road. World-space deltas ignore the rig's
            // raw bone axes, which is why this beats SetBoneLocalRotation for the Synty spine.
            Vector3 pitch = visual.right;
            if (spine != null) spine.rotation = Quaternion.AngleAxis(12f, pitch) * spineBase;
            if (chest != null) chest.rotation = Quaternion.AngleAxis(42f, pitch) * chestBase;
            if (neck != null) neck.rotation = Quaternion.AngleAxis(-30f, pitch) * neckBase;
            if (head != null) head.rotation = Quaternion.AngleAxis(4f, pitch) * headBase;
        }

        private void EnsureBones()
        {
            if (bonesReady || animator == null) return;
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (spine != null) spineBase = spine.rotation;
            if (chest != null) chestBase = chest.rotation;
            if (neck != null) neckBase = neck.rotation;
            if (head != null) headBase = head.rotation;
            bonesReady = true;
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
