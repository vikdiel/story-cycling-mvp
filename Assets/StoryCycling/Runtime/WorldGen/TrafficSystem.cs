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
        private readonly RiderTrack track = new RiderTrack(); private bool warnedTrack;
        private bool warnedRider, warnedStatic, checkHave; private float checkT; private Vector3 checkRider, checkCam;     // Plausibilitätsprüfung (einmalige Warnung)

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
            if (sim == null || !enableTraffic) return;
            if (rider == null)
            {
                if (!warnedRider) { warnedRider = true; Debug.LogWarning("Verkehr: 'rider' ist nicht gesetzt — die Autos kennen den Radfahrer nicht und fahren ungebremst durch ihn hindurch."); }
                return;
            }
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
            CheckRiderMoves(dt, rp);
            // Die Anzeige läuft einen Simulationsschritt hinter dem Simulationsstand her (Interpolation A->B): der Radfahrer wird deshalb um einen Schritt
            // vorausgeschätzt, sonst fehlen bei 30 m/s Annäherung bis zu 1,5 m zwischen dem, was die Sim plant, und dem, was gezeigt wird.
            sim.SetRider(rp.x + riderVel.x * Step, rp.z + riderVel.y * Step, riderVel.x, riderVel.y);
            track.Apply(sim, Step);
            if (!warnedTrack && sim.TrackMismatch > 120) { warnedTrack = true; Debug.LogWarning("Verkehr: Die Fahrlinie der Route (GpxRide) passt nicht zum 'rider'-Transform (" + rider.name + ") — die Autos rechnen mit der geschätzten Bahn."); }

            acc += Mathf.Min(dt, .1f);
            int steps = 0;
            while (acc >= Step && steps < 3) { sim.Step(Step); acc -= Step; steps++; Capture(); }
            if (steps == 3 && acc > Step) acc = Step;
            Apply(Mathf.Clamp01(acc / Step));
        }

        // Plausibilität: bewegt sich die Kamera, aber nicht das 'rider'-Transform, ist das Objekt falsch verdrahtet (die Autos sähen dann einen stehenden Radfahrer
        // an der falschen Stelle und führen durch das echte Rad) -> einmalig warnen
        private void CheckRiderMoves(float dt, Vector3 rp)
        {
            if (warnedStatic) return;
            var cam = Camera.main; if (cam == null) return;
            Vector3 cp = cam.transform.position;
            if (!checkHave) { checkHave = true; checkT = 0f; checkRider = rp; checkCam = cp; return; }
            checkT += dt;
            if (checkT < 8f) return;
            float dRider = Vector3.Distance(rp, checkRider), dCam = Vector3.Distance(cp, checkCam);
            if (dCam > 25f && dRider < 2f) { warnedStatic = true; Debug.LogWarning("Verkehr: Die Kamera hat sich " + dCam.ToString("0") + " m bewegt, das 'rider'-Transform (" + rider.name + ") nur " + dRider.ToString("0.0") + " m — ist es das bewegte Rad? Sonst fahren die Autos durch das Rad."); }
            checkT = 0f; checkRider = rp; checkCam = cp;                                                              // nächstes 8-s-Fenster
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

        // Fahrlinie des Radfahrers voraus aus der Route (GpxRide.Position mit LaneOffsetAt): Stützpunkte im Abstand Sp werden beim Fahren einmal ausgewertet und in einem
        // Ringpuffer gehalten (die Spline-Auswertung ist teuer), Apply füllt daraus TrafficSim.TrackX/Z ohne Allokation. Ohne gemeldeten Fahrzustand: keine Linie.
        public sealed class RiderTrack
        {
            public const float Sp = 3f, Step = 3f; private const int K = 128;       // K Zweierpotenz
            private readonly float[] px = new float[K], pz = new float[K]; private readonly int[] pi = new int[K];
            private float loopLen = -1f; private bool loop;
            public RiderTrack() { for (int i = 0; i < K; i++) pi[i] = -1; }

            public void Apply(TrafficSim sim, float lead)
            {
                if (!GpxRide.IsLoaded || !GpxRide.RideActive) { sim.SetRiderTrack(0, Step, 0f); return; }
                float v = GpxRide.RideSpeed;
                sim.SetRiderTrack(Fill(GpxRide.RideDistance + v * lead, sim.TrackX, sim.TrackZ, TrafficSim.TrackMax), Step, v);
            }

            private void Point(int i, out float x, out float z)
            {
                int s = i & (K - 1);
                if (pi[s] != i) { float d = Mathf.Min(i * Sp, GpxRide.Length); Vector3 p = GpxRide.Position(d, GpxRide.LaneOffsetAt(d), 0f); px[s] = p.x; pz[s] = p.z; pi[s] = i; }
                x = px[s]; z = pz[s];
            }

            // ox/oz[k] = Fahrlinie bei Streckenlänge d0 + k * Step. Geschlossene Runde (Start und Ziel < 30 m auseinander): über das Streckenende hinaus weiter
            // ab dem Anfang (der Fahrer fährt dort mit Mathf.Repeat weiter), sonst endet die Linie am Streckenende.
            public int Fill(float d0, float[] ox, float[] oz, int max)
            {
                float len = GpxRide.Length; int n = 0;
                if (loopLen != len)
                {
                    loopLen = len; Vector3 a = GpxRide.Position(0f, GpxRide.LaneOffsetAt(0f), 0f), b = GpxRide.Position(len, GpxRide.LaneOffsetAt(len), 0f);
                    a.y = b.y = 0f; loop = (a - b).sqrMagnitude < 30f * 30f;
                }
                for (int k = 0; k < max; k++)
                {
                    float d = d0 + k * Step;
                    if (d >= len - Sp) { if (!loop) break; d -= len; if (d >= len - Sp) break; }
                    if (d < 0f) d = 0f;
                    float f = d / Sp; int i = (int)f; f -= i;
                    Point(i, out float x0, out float z0); Point(i + 1, out float x1, out float z1);
                    ox[k] = x0 + (x1 - x0) * f; oz[k] = z0 + (z1 - z0) * f; n++;
                }
                return n;
            }
        }

        private static float Hash01(int x)
        {
            uint h = (uint)x; h ^= h >> 16; h *= 0x7feb352dU; h ^= h >> 15; h *= 0x846ca68bU; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
