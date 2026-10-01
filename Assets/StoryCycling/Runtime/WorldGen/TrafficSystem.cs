using System;
using System.IO;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Hintergrundverkehr zur Laufzeit. Die Kinder dieses Objekts sind die vom Generator vorinstanziierten (inaktiven) Autos,
    // die Fahrspuren kommen aus der gebackenen Datei <route>.traffic.txt (TrafficGraph). Die Simulation selbst ist reines C# (TrafficSim);
    // dieses MonoBehaviour liest nur den Radfahrer, tickt die Simulation mit festen 20 Hz, interpoliert die Posen und färbt neu gespawnte Autos um.
    // Keine Physik, kein Collider, keine Allokation pro Frame (alle Puffer beim Start).
    [DisallowMultipleComponent]
    public sealed class TrafficSystem : MonoBehaviour
    {
        [Tooltip("Pfad der gebackenen Verkehrsdatei relativ zu StreamingAssets (vom Generator gesetzt).")]
        public string trafficRelPath = "Routes/Nordhoek.traffic.txt";
        [Tooltip("Radfahrer: Abstand für Spawn/Despawn, Ausweichen und Überholen.")]
        public Transform rider;
        [Tooltip("Lackvarianten der Autos (vom Generator gesetzt); leer = Originalfarbe.")]
        public CarPaintSet carPaint;
        [Range(0, 64)] public int carCount = 24;
        public float spawnMin = 120f, spawnMax = 450f, despawn = 500f;
        [Tooltip("0 = bei jedem Start anderer Zufall")] public int seed = 0;
        [Tooltip("Gesamtschalter zur Laufzeit (z. B. für schwache Geräte)")] public bool enableTraffic = true;

        private const float Step = 0.05f;                 // 20 Hz
        private TrafficSim sim;
        private Transform[] cars;
        private Renderer[][] paint;                       // je Auto: Renderer mit der Lackfläche
        private Vector3[] anchor;                         // Modellmitte (x, Unterkante, z) im Auto-Root
        private Vector3[] posA, posB; private Quaternion[] rotA, rotB;
        private int[] serial; private bool[] live, shown;
        private float acc;
        private bool haveRider; private Vector3 lastRider; private Vector2 riderVel;

        public int ActiveCars { get { int n = 0; if (live != null) foreach (var l in live) if (l) n++; return n; } }
        public TrafficSim Sim => sim;

        private void Start()
        {
            if (!enableTraffic) return;
            if (cars == null) Init();
        }

        public bool Init()
        {
            string path = Path.Combine(Application.streamingAssetsPath, trafficRelPath);
            if (!File.Exists(path)) { Debug.LogWarning("Verkehr: Datei fehlt (" + path + ") — kein Verkehr."); return false; }
            TrafficGraph graph;
            try { graph = TrafficGraph.Parse(File.ReadAllText(path)); }
            catch (Exception e) { Debug.LogWarning("Verkehr: Datei nicht lesbar — kein Verkehr. " + e.Message); return false; }
            if (graph == null || graph.Lanes.Count == 0) return false;

            int n = Mathf.Min(carCount, transform.childCount);
            if (n <= 0) { Debug.LogWarning("Verkehr: keine Auto-Objekte unter " + name); return false; }
            var tuning = new TrafficSim.Tuning { Cars = n, SpawnMin = spawnMin, SpawnMax = spawnMax, Despawn = despawn };
            sim = new TrafficSim(graph, tuning, seed != 0 ? seed : Environment.TickCount);

            cars = new Transform[n]; paint = new Renderer[n][]; anchor = new Vector3[n];
            posA = new Vector3[n]; posB = new Vector3[n]; rotA = new Quaternion[n]; rotB = new Quaternion[n];
            serial = new int[n]; live = new bool[n]; shown = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var t = transform.GetChild(i); cars[i] = t;
                MeasureCar(t, out Bounds b);
                anchor[i] = new Vector3(b.center.x, b.min.y, b.center.z);
                sim.SetCarSize(i, Mathf.Clamp(b.size.z, 3.2f, 6.5f), Mathf.Clamp(b.size.x, 1.5f, 2.3f), i);
                var list = new System.Collections.Generic.List<Renderer>();
                if (carPaint != null && carPaint.Usable)
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r.sharedMaterial == carPaint.baseMaterial) list.Add(r);
                paint[i] = list.ToArray();
                t.gameObject.SetActive(false);
            }
            return true;
        }

        // Modellgrenzen im Root-Raum aus den Meshes (funktioniert auch für inaktive Objekte, anders als Renderer.bounds)
        private static void MeasureCar(Transform root, out Bounds result)
        {
            bool any = false; result = new Bounds(Vector3.zero, new Vector3(1.85f, 1.5f, 4.4f));
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh; if (mesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>(); if (mr == null) continue;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix; Bounds mb = mesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var c = mb.center + new Vector3((k & 1) == 0 ? -mb.extents.x : mb.extents.x, (k & 2) == 0 ? -mb.extents.y : mb.extents.y, (k & 4) == 0 ? -mb.extents.z : mb.extents.z);
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; } else result.Encapsulate(p);
                }
            }
        }

        private void LateUpdate()
        {
            if (sim == null || rider == null || !enableTraffic) return;
            float dt = Time.deltaTime;
            Vector3 rp = rider.position;
            if (!haveRider || dt > .5f || (rp - lastRider).sqrMagnitude > 40f * 40f)
            {
                // Sprung (Schleife der Strecke, Pause, Neustart): alle Autos neu verteilen
                sim.Reset(); riderVel = Vector2.zero; acc = 0f;
                for (int i = 0; i < live.Length; i++) live[i] = false;
            }
            else if (dt > 1e-4f)
            {
                var v = new Vector2(rp.x - lastRider.x, rp.z - lastRider.z) / dt;
                riderVel = Vector2.Lerp(riderVel, v, 1f - Mathf.Exp(-8f * dt));
            }
            lastRider = rp; haveRider = true;
            sim.SetRider(rp.x, rp.z, riderVel.x, riderVel.y);

            acc += Mathf.Min(dt, .1f);
            int steps = 0;
            while (acc >= Step && steps < 3) { sim.Step(Step); acc -= Step; steps++; Capture(); }
            if (steps == 3 && acc > Step) acc = Step;
            Apply(Mathf.Clamp01(acc / Step));
        }

        private void Capture()
        {
            for (int i = 0; i < cars.Length; i++)
            {
                var c = sim.GetCar(i);
                if (!c.Active) { live[i] = false; continue; }
                var fwd = new Vector3(c.Hx, c.Hy, c.Hz);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                var rot = Quaternion.LookRotation(fwd, Vector3.up);
                var pos = new Vector3(c.X, c.Y, c.Z) - rot * anchor[i];
                if (!live[i] || serial[i] != c.Serial)
                {
                    serial[i] = c.Serial; live[i] = true;
                    posA[i] = posB[i] = pos; rotA[i] = rotB[i] = rot;
                    if (carPaint != null && carPaint.Usable && paint[i].Length > 0)
                    {
                        int v = carPaint.Pick(Hash01(c.Serial * 31 + i * 7919));
                        if (v >= 0 && v < carPaint.variants.Length && carPaint.variants[v] != null)
                            for (int k = 0; k < paint[i].Length; k++) paint[i][k].sharedMaterial = carPaint.variants[v];
                    }
                }
                else { posA[i] = posB[i]; rotA[i] = rotB[i]; posB[i] = pos; rotB[i] = rot; }
            }
        }

        private void Apply(float alpha)
        {
            for (int i = 0; i < cars.Length; i++)
            {
                if (!live[i]) { if (shown[i]) { cars[i].gameObject.SetActive(false); shown[i] = false; } continue; }
                if (!shown[i]) { cars[i].gameObject.SetActive(true); shown[i] = true; }
                cars[i].SetPositionAndRotation(Vector3.Lerp(posA[i], posB[i], alpha), Quaternion.Slerp(rotA[i], rotB[i], alpha));
            }
        }

        private static float Hash01(int x)
        {
            uint h = (uint)x; h ^= h >> 16; h *= 0x7feb352dU; h ^= h >> 15; h *= 0x846ca68bU; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
