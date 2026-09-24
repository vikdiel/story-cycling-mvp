using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen
{
    // Zeichnet viele Kopien weniger Meshes per GPU-Instancing (Baukasten-Häuser).
    // Gespeichert werden nur Matrizen (64 Byte pro Modul) statt kopierter Vertices:
    // 40.000 Module ≈ 2,5 MB statt mehrerer GB Mesh-Daten (Projekt speichert Assets als Text).
    // Gezeichnet wird pro Kamera: nur Batches in Sichtweite und im Sichtkegel.
    [ExecuteAlways]
    public sealed class InstancedMeshField : MonoBehaviour
    {
        [Serializable]
        public sealed class Batch
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            public Bounds bounds;
            public Matrix4x4[] matrices;
            // Kompakt (Hangvegetation): xyz + Drehung (Grad) und Skalierung; Matrizen entstehen beim Laden
            public Vector4[] packed;
            public float[] scales;
            [NonSerialized] public Matrix4x4[] expanded;

            public Matrix4x4[] Instances
            {
                get
                {
                    if (matrices != null && matrices.Length > 0) return matrices;
                    if (expanded == null && packed != null)
                    {
                        expanded = new Matrix4x4[packed.Length];
                        for (int i = 0; i < packed.Length; i++)
                            expanded[i] = Matrix4x4.TRS(new Vector3(packed[i].x, packed[i].y, packed[i].z),
                                                        Quaternion.Euler(0f, packed[i].w, 0f),
                                                        Vector3.one * (scales != null && i < scales.Length ? scales[i] : 1f));
                    }
                    return expanded;
                }
            }
        }

        public List<Batch> batches = new List<Batch>();
        public float drawDistance = 600f;
        public ShadowCastingMode shadows = ShadowCastingMode.On;

        private readonly Plane[] planes = new Plane[6];
        private const int MaxPerCall = 1023;

        private void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;
        private void OnDisable() => RenderPipelineManager.beginCameraRendering -= Draw;

        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            if (cam == null || batches == null) return;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            Vector3 cp = cam.transform.position;
            float far = Mathf.Min(drawDistance, cam.farClipPlane);
            float d2 = far * far;
            foreach (var b in batches)
            {
                if (b == null || b.mesh == null || b.material == null) continue;
                if (b.bounds.SqrDistance(cp) > d2 || !GeometryUtility.TestPlanesAABB(planes, b.bounds)) continue;
                var rp = new RenderParams(b.material)
                {
                    camera = cam,
                    worldBounds = b.bounds,
                    shadowCastingMode = shadows,
                    receiveShadows = true,
                    layer = gameObject.layer
                };
                var inst = b.Instances;
                if (inst == null || inst.Length == 0) continue;
                for (int start = 0; start < inst.Length; start += MaxPerCall)
                    Graphics.RenderMeshInstanced(rp, b.mesh, b.submesh, inst, Mathf.Min(MaxPerCall, inst.Length - start), start);
            }
        }
    }
}
