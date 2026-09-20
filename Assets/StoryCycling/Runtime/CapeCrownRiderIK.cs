using UnityEngine;

namespace StoryCycling
{
    // Lives on the character GameObject (same object as the Animator) so that
    // OnAnimatorIK actually fires; delegates the IK work to the owning rig.
    public sealed class CapeCrownRiderIK : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private CapeCrownCyclistAnimation owner;

        public void Bind(Animator a, CapeCrownCyclistAnimation ownerRef)
        {
            animator = a;
            owner = ownerRef;
        }

        private void Awake()
        {
            if (animator == null) animator = GetComponent<Animator>();
            if (owner == null) owner = GetComponentInParent<CapeCrownCyclistAnimation>();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            // Serialized references into the prefab instance can break on reload; resolve lazily.
            if (animator == null) animator = GetComponent<Animator>();
            if (owner == null) owner = GetComponentInParent<CapeCrownCyclistAnimation>();
            if (owner != null) owner.ApplyIK(animator);
        }
    }
}
