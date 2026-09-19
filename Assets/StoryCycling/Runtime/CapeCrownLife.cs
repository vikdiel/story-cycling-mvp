using UnityEngine;

namespace StoryCycling
{
    // Pooled street life: pedestrians walk the sidewalks and are only active near the
    // rider, so a populated section never exceeds a small active-object budget on iPad.
    public sealed class CapeCrownLife : MonoBehaviour
    {
        [SerializeField] private CapeCrownRideController ride;
        [SerializeField] private Transform[] walkers;
        [SerializeField] private float[] distances, offsets, speeds;
        [SerializeField] private int[] directions;
        private const float ActivationRadius = 90f;
        private Transform[][] bones;
        private static readonly string[] BoneNames={"UpperLeg_L","LowerLeg_L","Ankle_L","UpperLeg_R","LowerLeg_R","Ankle_R","Shoulder_L","Elbow_L","Hand_L","Shoulder_R","Elbow_R","Hand_R"};
        private void Awake()
        {
            if(walkers==null)return;
            bones=new Transform[walkers.Length][];
            for(int i=0;i<walkers.Length;i++)
            {
                bones[i]=new Transform[BoneNames.Length];
                if(walkers[i]==null)continue;
                foreach(var animator in walkers[i].GetComponentsInChildren<Animator>(true))animator.enabled=false;
                foreach(var t in walkers[i].GetComponentsInChildren<Transform>(true))
                    for(int b=0;b<BoneNames.Length;b++)if(t.name==BoneNames[b])bones[i][b]=t;
            }
        }
        private void PoseWalker(int i)
        {
            if(bones==null||i>=bones.Length)return;
            float phase=Time.time*5+ i*.7f;
            var b=bones[i];
            for(int side=0;side<2;side++)
            {
                float swing=Mathf.Sin(phase+side*Mathf.PI),bend=Mathf.Max(0,-swing);
                int leg=side*3,arm=6+side*3;
                Aim(b[leg],b[leg+1],walkers[i].TransformDirection(new Vector3(0,-1,swing*.35f)));
                Aim(b[leg+1],b[leg+2],walkers[i].TransformDirection(new Vector3(0,-1,-bend*.55f)));
                Aim(b[arm],b[arm+1],walkers[i].TransformDirection(new Vector3(side==0?-.1f:.1f,-1,-swing*.22f)));
                Aim(b[arm+1],b[arm+2],walkers[i].TransformDirection(new Vector3(0,-1,.18f-swing*.16f)));
            }
        }
        private static void Aim(Transform joint,Transform child,Vector3 direction)
        { if(joint!=null&&child!=null)joint.rotation=Quaternion.FromToRotation(child.position-joint.position,direction)*joint.rotation; }

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
                walkers[i].position = p + side * offsets[i] + Vector3.up * (.03f+Mathf.Abs(bob)*.3f);
                walkers[i].rotation = Quaternion.LookRotation(hf * directions[i], Vector3.up);
                PoseWalker(i);
            }
        }
    }
}
