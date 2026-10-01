using System;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Simulationskern des Hintergrundverkehrs: reines C# (keine Unity-Objekte, keine Physik), damit er ohne Editor testbar ist.
    //   - Autos fahren auf den Spuren des TrafficGraph (Linksverkehr), Längsdynamik IDM (Folgeabstand, Haltelinie, Kurven-/Abbiegetempo)
    //   - Kreuzungen: Reservierung je Verbinder (FIFO nach Ankunftszeit + Vorrang), kreuzende/einmündende Verbinder schließen sich aus,
    //     Kastenregel (nur einfahren, wenn dahinter Platz ist), Zeitlimit gegen Verklemmen
    //   - Radfahrer: Hindernis mit 1,75 m Abstand; Überholen nur mit seitlichem Versatz >= Abstand und freier Gegenspur, sonst hinterherfahren
    //   - zusätzlich geometrische Vorausschau entlang des Pfads (Kreise) als Sicherheitsnetz gegen alle anderen Autos
    //   - keine Allokation pro Schritt (alle Puffer vorab)
    public sealed class TrafficSim
    {
        public sealed class Car
        {
            public bool Active; public int Serial; public int Type;
            public int Lane, Prev1 = -1, Prev2 = -1, NextConn = -1, LaneHint, PrevHint;
            public float S, V, Off, OffTarget;
            public float Length = 4.4f, Width = 1.85f, Wheelbase = 2.7f;
            public float V0Factor = 1f, AccelFactor = 1f, TimeGap = 1.3f;
            public float X, Y, Z, Hx, Hy, Hz = 1f;                  // Mitte zwischen den Achsen (Boden), Fahrtrichtung inkl. Steigung
            public float Speed => V;
            public float Yaw => Mathf.Atan2(Hx, Hz);
            // Reservierungen der Kreuzungs-Verbinder auf dem Weg (Kette: kurze Zwischenspuren werden gemeinsam reserviert)
            public const int MaxRes = 5;
            public readonly int[] Res = new int[MaxRes]; public readonly byte[] ResState = new byte[MaxRes];       // 0 offen, 1 im Verbinder, 2 verlassen
            public readonly bool[] ResGranted = new bool[MaxRes]; public readonly float[] ResExitOdo = new float[MaxRes];
            public int ResN; public bool PhysBlocked, Eligible; public float ReqKey, ReqTime, GrantTime, Odo;
            public float StoppedFor; public bool Passing; public float Age;
            public float Accel;                                      // zuletzt berechnete Beschleunigung (Diagnose)
        }

        public struct Tuning
        {
            public int Cars; public float SpawnMin, SpawnMax, Despawn;
            public static Tuning Default => new Tuning { Cars = 24, SpawnMin = 120f, SpawnMax = 450f, Despawn = 500f };
        }

        // Fahrer/Fahrzeug-Konstanten
        public const float RiderClear = 1.75f;                 // Abstand Fahrradmitte -> Autokante (>= 1,5 m + Eckenreserve)
        public const float StopGap = 2.0f, Brake = 2.0f, BrakeLimit = 2.3f, MaxDecel = 9f, LateralRate = 1.2f;
        public const float ProbeStep = 1.5f, RequestDist = 100f, StopLine = 2.4f;

        private readonly TrafficGraph g;
        private readonly Car[] cars;
        private readonly System.Random rng;
        public Tuning Cfg;
        private float time, spawnTimer;
        public float Time => time;

        // Radfahrer
        public float RiderX, RiderZ, RiderVX, RiderVZ; public bool RiderSet;

        // Spawnkandidaten
        private readonly float[] laneCx, laneCz, laneR;
        private readonly int[] cand; private readonly float[] candCum; private int candCount; private float candX = float.NaN, candZ;

        // Zwischenpuffer
        private const int MaxProbe = 96;
        private readonly float[] px = new float[MaxProbe], pz = new float[MaxProbe], phx = new float[MaxProbe], phz = new float[MaxProbe];
        private int probeCount;
        private readonly float[] cxs, czs;                     // Kreise je Auto (3 pro Auto)
        private readonly float[] accel;
        private readonly int[] near = new int[64];
        private readonly int[] pathLane = new int[5];

        // Zähler für Prüfung/Diagnose
        public int ForcedStopTicks, LastForcedLane; public float LastForcedSpeed;
        public int Spawned, Despawned, StuckCulls, ForcedStops, SinkDespawns, PassStarted, PassAborted, GrantTimeouts, EmergencyBrakes;
        public float MaxStoppedFar;
        public Action<string> Log;                              // optionale Diagnose (Tests)

        public int CarCount => cars.Length;
        public Car GetCar(int i) => cars[i];
        public int ActiveCount { get { int n = 0; foreach (var c in cars) if (c.Active) n++; return n; } }
        public TrafficGraph Graph => g;

        public TrafficSim(TrafficGraph graph, Tuning cfg, int seed)
        {
            g = graph; Cfg = cfg; rng = new System.Random(seed);
            cars = new Car[Mathf.Max(0, cfg.Cars)];
            for (int i = 0; i < cars.Length; i++) cars[i] = new Car();
            cxs = new float[cars.Length * 3]; czs = new float[cars.Length * 3]; accel = new float[cars.Length];
            int n = g.Lanes.Count;
            laneCx = new float[n]; laneCz = new float[n]; laneR = new float[n]; cand = new int[n]; candCum = new float[n];
            for (int i = 0; i < n; i++)
            {
                var l = g.Lanes[i]; int h = 0;
                l.Eval(l.Length * .5f, ref h, out laneCx[i], out _, out laneCz[i], out _, out _);
                laneR[i] = l.Length * .5f + 1f;
            }
        }

        public void SetCarSize(int index, float length, float width, int type)
        {
            var c = cars[index]; c.Length = length; c.Width = width; c.Wheelbase = Mathf.Clamp(length * .6f, 2.3f, 3.2f); c.Type = type;
        }

        public void SetRider(float x, float z, float vx, float vz) { RiderX = x; RiderZ = z; RiderVX = vx; RiderVZ = vz; RiderSet = true; }

        public void Reset()
        {
            foreach (var c in cars) Deactivate(c, false);
            candX = float.NaN;
        }

        // ------------------------------------------------------------------ Schritt
        public void Step(float dt)
        {
            if (dt <= 0f) return;
            time += dt;
            if (RiderSet) ManageSpawns(dt);
            int n = cars.Length;
            for (int i = 0; i < n; i++)
            {
                var c = cars[i]; if (!c.Active) continue;
                float hx = c.Hx, hz = c.Hz; float hl = Mathf.Sqrt(hx * hx + hz * hz); if (hl > 1e-4f) { hx /= hl; hz /= hl; }
                float off = c.Length * .5f - c.Width * .5f;
                for (int k = 0; k < 3; k++) { cxs[i * 3 + k] = c.X + hx * off * (k - 1); czs[i * 3 + k] = c.Z + hz * off * (k - 1); }
            }
            for (int i = 0; i < n; i++) if (cars[i].Active) accel[i] = Decide(i, dt);
            for (int i = 0; i < n; i++) if (cars[i].Active) Integrate(cars[i], accel[i], dt);
        }

        // ------------------------------------------------------------------ Entscheidung
        private float Decide(int i, float dt)
        {
            var car = cars[i]; var lane = g.Lanes[car.Lane];
            float v = car.V; float distEnd = lane.Length - car.S;
            car.Age += dt;

            // --- Verbindungswahl / Reservierung
            if (lane.Type == TrafficGraph.Road)
            {
                if (car.NextConn < 0 && lane.Next.Length > 0) car.NextConn = ChooseNext(car.Lane);
                if (car.NextConn >= 0)
                {
                    int idx = ResIndex(car, car.NextConn);
                    if (idx < 0 && distEnd < RequestDist) { MakeRequest(car, distEnd); idx = ResIndex(car, car.NextConn); }
                    car.Eligible = distEnd < 20f + 3f * v;                              // nah genug, um jetzt an der Reihe zu sein
                    if (idx >= 0 && !car.ResGranted[idx]) { if (car.Eligible) TryGrant(i, car, idx); }
                    else if (idx >= 0 && car.ResGranted[idx] && car.ResState[idx] == 0 && distEnd > 3f && time - car.GrantTime > 18f)
                    { DropFrom(car, idx); GrantTimeouts++; }                       // Freigabe nicht genutzt (z. B. Stau davor): neu anstellen
                }
            }

            // --- Pfad-Stützpunkte voraus (mit seitlichem Versatz)
            float vEff = Mathf.Max(v, 4f);
            float dLook = Mathf.Min(MaxProbe * ProbeStep - 3f, 14f + v * 3f + v * v / (2f * Brake));
            BuildPath(car, dLook, vEff);

            // --- Sollgeschwindigkeit: Spurlimit, Kurvenlimits voraus, Abbiegetempo
            float vlim = SpeedLimit(car, lane) ;

            // --- Radfahrer: Überholentscheidung (seitlicher Versatz)
            DecideLateral(i, car, lane, distEnd, vlim);

            // --- nächstes Hindernis voraus (Autos + Radfahrer)
            float gap = float.MaxValue, lv = 0f;
            FindLeader(i, car, out gap, out lv);

            // --- Haltelinie
            float aStop = float.MaxValue;
            if (lane.Type == TrafficGraph.Road && (distEnd < RequestDist + 10f))
            {
                bool stop = false;
                if (car.NextConn >= 0) { int ri = ResIndex(car, car.NextConn); stop = ri >= 0 && !car.ResGranted[ri]; }
                else if (lane.Sink) stop = RiderSet && Dist2(car.X, car.Z, RiderX, RiderZ) < 80f * 80f;
                if (stop)
                {
                    float sg = Mathf.Max(.05f, distEnd - StopLine);
                    aStop = Idm(car, v, vlim, sg, v);
                }
            }
            float a = Idm(car, v, vlim, gap, v - lv);
            if (aStop < a) a = aStop;
            car.Accel = a;
            return Mathf.Clamp(a, -MaxDecel, 3f);
        }

        private float Idm(Car car, float v, float v0, float gap, float dv)
        {
            float aMax = 1.6f * car.AccelFactor;
            v0 = Mathf.Max(v0, .5f);
            float free = 1f - Mathf.Pow(v / v0, 4f);
            if (gap >= 1e6f) return aMax * free;
            float sStar = StopGap + Mathf.Max(0f, v * car.TimeGap + v * dv / (2f * Mathf.Sqrt(aMax * Brake)));
            float g2 = Mathf.Max(gap, .05f);
            return aMax * (free - (sStar / g2) * (sStar / g2));
        }

        // ------------------------------------------------------------------ Tempolimit voraus
        private float SpeedLimit(Car car, TrafficGraph.Lane lane)
        {
            float f = car.V0Factor;
            float lim = lane.Speed * (lane.Type == TrafficGraph.Road ? f : Mathf.Min(1f, f));
            ScanLane(lane, car.S, 0f, ref lim);
            float d0 = lane.Length - car.S;
            if (lane.Type == TrafficGraph.Road)
            {
                if (car.NextConn >= 0)
                {
                    var c = g.Lanes[car.NextConn];
                    Limit(ref lim, c.Speed, d0);
                    ScanLane(c, 0f, d0, ref lim);
                    var nl = g.Lanes[c.Next[0]];
                    Limit(ref lim, nl.Speed * f, d0 + c.Length);
                    ScanLane(nl, 0f, d0 + c.Length, ref lim);
                }
            }
            else
            {
                var nl = g.Lanes[lane.Next[0]];
                Limit(ref lim, nl.Speed * f, d0);
                ScanLane(nl, 0f, d0, ref lim);
            }
            return lim;
        }

        private static void Limit(ref float lim, float vTarget, float dist)
        {
            if (dist > 120f) return;
            float allowed = Mathf.Sqrt(vTarget * vTarget + 2f * BrakeLimit * Mathf.Max(0f, dist));
            if (allowed < lim) lim = allowed;
        }

        private void ScanLane(TrafficGraph.Lane l, float fromS, float baseDist, ref float lim)
        {
            // Stützpunkte ab fromS innerhalb von 100 m
            int i = 0; while (i < l.N - 1 && l.S[i + 1] < fromS) i++;
            for (; i < l.N; i++)
            {
                float d = baseDist + l.S[i] - fromS;
                if (d > 100f) break;
                if (l.Cap[i] < 59f) Limit(ref lim, l.Cap[i], d);
            }
        }

        // ------------------------------------------------------------------ Pfad (Stützpunkte voraus)
        private void BuildPath(Car car, float dLook, float vEff)
        {
            var lane = g.Lanes[car.Lane];
            int n = 0;
            pathLane[n++] = car.Lane;
            if (lane.Type == TrafficGraph.Road) { if (car.NextConn >= 0) { pathLane[n++] = car.NextConn; pathLane[n++] = g.Lanes[car.NextConn].Next[0]; } }
            else { int nl = lane.Next[0]; pathLane[n++] = nl; if (car.NextConn >= 0) { pathLane[n++] = car.NextConn; pathLane[n++] = g.Lanes[car.NextConn].Next[0]; } }
            probeCount = 0;
            int stage = 0; float s = car.S; int hint = car.LaneHint; float traveled = 0f;
            while (probeCount < MaxProbe && traveled <= dLook && stage < n)
            {
                var l = g.Lanes[pathLane[stage]];
                if (s > l.Length) { traveled += 0f; s -= l.Length; stage++; hint = 0; if (stage >= n) break; continue; }
                l.Eval(s, ref hint, out float x, out _, out float z, out float hx, out float hz);
                float off = stage == 0 ? OffsetAt(car, traveled, vEff) : 0f;
                px[probeCount] = x + hz * off; pz[probeCount] = z - hx * off; phx[probeCount] = hx; phz[probeCount] = hz;
                probeCount++;
                s += ProbeStep; traveled += ProbeStep;
            }
        }

        // erwarteter seitlicher Versatz nach 'dist' Metern (Bewegung mit der Querrate zum Zielversatz)
        private static float OffsetAt(Car car, float dist, float vEff)
        {
            float t = dist / vEff, step = LateralRate * t;
            float d = car.OffTarget - car.Off;
            if (d > step) return car.Off + step;
            if (d < -step) return car.Off - step;
            return car.OffTarget;
        }

        // ------------------------------------------------------------------ Hindernisse
        private void FindLeader(int me, Car car, out float gap, out float lv)
        {
            gap = float.MaxValue; lv = 0f;
            if (probeCount == 0) return;
            float reach = probeCount * ProbeStep + 8f;
            float reach2 = reach * reach;
            int nn = 0;
            for (int j = 0; j < cars.Length && nn < near.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                if (Dist2(car.X, car.Z, cars[j].X, cars[j].Z) < reach2) near[nn++] = j;
            }
            float rMe = car.Width * .5f;
            for (int k = 0; k < nn; k++)
            {
                int j = near[k]; var o = cars[j];
                float R = rMe + o.Width * .5f + .3f, R2 = R * R;
                for (int q = 0; q < probeCount; q++)
                {
                    float d = q * ProbeStep; if (d >= gap) break;
                    bool hit = false;
                    for (int c = 0; c < 3 && !hit; c++)
                    {
                        float dx = px[q] - cxs[j * 3 + c], dz = pz[q] - czs[j * 3 + c];
                        if (dx * dx + dz * dz < R2) hit = true;
                    }
                    if (!hit) continue;
                    float ohl = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (ohl < 1e-4f) ohl = 1f;
                    float cosA = (o.Hx * phx[q] + o.Hz * phz[q]) / ohl;
                    gap = d; lv = cosA > .3f ? o.V * cosA : 0f;
                    break;
                }
            }
            if (RiderSet)
            {
                float R = rMe + RiderClear, R2 = R * R;
                float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ);
                for (int q = 0; q < probeCount; q++)
                {
                    float d = q * ProbeStep; if (d >= gap) break;
                    float dx = px[q] - RiderX, dz = pz[q] - RiderZ;
                    bool hit = dx * dx + dz * dz < R2;
                    float rl = 0f;
                    if (hit) { float cosA = rs > .3f ? (RiderVX * phx[q] + RiderVZ * phz[q]) / rs : 0f; rl = cosA > .5f ? rs * cosA : 0f; }
                    else if (rs > .6f)
                    {
                        // quer laufender Radfahrer: Position in 1 s / 2 s
                        float cosA = (RiderVX * phx[q] + RiderVZ * phz[q]) / rs;
                        if (Mathf.Abs(cosA) < .8f)
                        {
                            for (int t = 1; t <= 2 && !hit; t++)
                            {
                                float ex = px[q] - (RiderX + RiderVX * t), ez = pz[q] - (RiderZ + RiderVZ * t);
                                float rr = rMe + (t == 1 ? 1.2f : .8f);
                                if (ex * ex + ez * ez < rr * rr) hit = true;
                            }
                        }
                    }
                    if (hit) { gap = d; lv = rl; break; }
                }
            }
        }

        // ------------------------------------------------------------------ Überholen des Radfahrers
        private void DecideLateral(int me, Car car, TrafficGraph.Lane lane, float distEnd, float vlim)
        {
            if (!RiderSet || lane.Type != TrafficGraph.Road)
            {
                car.Passing = false; car.OffTarget = 0f; return;
            }
            float halfW = car.Width * .5f;
            // Radfahrer relativ zur Spur: nächster Stützpunkt
            float dmin = float.MaxValue; int qmin = -1;
            for (int q = 0; q < probeCount; q++)
            {
                float dx = RiderX - px[q], dz = RiderZ - pz[q]; float d2 = dx * dx + dz * dz;
                if (d2 < dmin) { dmin = d2; qmin = q; }
            }
            float rlat = 0f; bool rider = false; float gapR = 0f;
            if (qmin >= 0 && dmin < 5f * 5f)
            {
                // seitliche Lage relativ zur Spurmitte (ohne den aktuellen Versatz): rechts positiv
                float off = OffsetAt(car, qmin * ProbeStep, Mathf.Max(car.V, 4f));
                rlat = (RiderX - px[qmin]) * phz[qmin] - (RiderZ - pz[qmin]) * phx[qmin] + off;
                gapR = qmin * ProbeStep; rider = true;
            }
            float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hl < 1e-4f) hl = 1f;
            float hx = car.Hx / hl, hz = car.Hz / hl;
            float lon = (RiderX - car.X) * hx + (RiderZ - car.Z) * hz;                  // > 0: Radfahrer vor der Mitte
            float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ);
            float rAlong = rs > .3f ? (RiderVX * hx + RiderVZ * hz) : 0f;
            float pass = RiderClear + halfW;
            float maxOff = lane.Room - halfW - .1f;

            if (car.Passing)
            {
                if (lon < -(car.Length * .5f + 4f) || !rider) { car.Passing = false; car.OffTarget = 0f; return; }
                if (distEnd < car.Length + 12f && lon > car.Length * .5f) { AbortPass(car); return; }
                float target = Mathf.Max(0f, rlat + pass);
                if (target > maxOff + .05f && lon > 0f) { AbortPass(car); return; }
                if (lon > 0f && !Clear(me, car, Mathf.Min(target, maxOff), gapR, halfW)) { AbortPass(car); return; }
                car.OffTarget = Mathf.Min(target, maxOff);
                return;
            }
            car.OffTarget = 0f;
            if (!rider || gapR > 50f || lon < 0f) return;
            if (Mathf.Abs(rlat - car.Off) > pass + .4f) return;                          // liegt gar nicht im Weg
            if (rAlong > vlim * .9f) return;                                           // nicht langsamer als wir
            if (distEnd < gapR + 45f) return;                                          // vor der Kreuzung nicht mehr überholen
            float tgt = Mathf.Max(0f, rlat + pass);
            if (tgt > maxOff) return;                                                  // kein Platz auf der Fahrbahn
            if (!Clear(me, car, tgt, gapR, halfW)) return;
            car.Passing = true; car.OffTarget = tgt; PassStarted++;
        }

        private void AbortPass(Car car) { car.Passing = false; car.OffTarget = 0f; PassAborted++; }

        // Freier Korridor für den Zielversatz? Gegenverkehr im Korridor bis 140 m, gleichgerichtete Autos bis Radfahrer + 40 m
        private bool Clear(int me, Car car, float target, float gapRider, float halfW)
        {
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                var o = cars[j];
                float dx = o.X - car.X, dz = o.Z - car.Z;
                if (dx * dx + dz * dz > 160f * 160f) continue;
                float ohl = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (ohl < 1e-4f) continue;
                float ohx = o.Hx / ohl, ohz = o.Hz / ohl;
                // nächster Pfadpunkt (Bogenposition q) und seitliche Lage relativ zur Spur
                float best = float.MaxValue; int bq = -1;
                for (int q = 0; q < probeCount; q++) { float ex = o.X - px[q], ez = o.Z - pz[q]; float d2 = ex * ex + ez * ez; if (d2 < best) { best = d2; bq = q; } }
                float lat, lon;
                if (bq < 0 || best > 12f * 12f || bq == 0)
                {
                    float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hl < 1e-4f) hl = 1f;
                    float hx = car.Hx / hl, hz = car.Hz / hl;
                    lon = dx * hx + dz * hz; lat = dx * hz - dz * hx + car.Off;
                }
                else
                {
                    float off = OffsetAt(car, bq * ProbeStep, Mathf.Max(car.V, 4f));
                    lat = (o.X - px[bq]) * phz[bq] - (o.Z - pz[bq]) * phx[bq] + off; lon = bq * ProbeStep;
                }
                float cosA = ohx * car.Hx + ohz * car.Hz; float hlm = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hlm > 1e-4f) cosA /= hlm;
                bool same = cosA > .3f;
                float sep = Mathf.Abs(lat - target);
                if (same)
                {
                    if (lon > -14f && lon < gapRider + 40f && sep < halfW + o.Width * .5f + .45f) return false;
                }
                else
                {
                    if (lon > -6f && lon < 140f && sep < halfW + o.Width * .5f + 1.0f) return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ Kreuzungs-Reservierung
        private int ChooseNext(int laneId)
        {
            var l = g.Lanes[laneId];
            if (l.Next.Length == 0) return -1;
            if (l.Next.Length == 1) return l.Next[0];
            float sum = 0f;
            for (int i = 0; i < l.Next.Length; i++) sum += TurnWeight(g.Lanes[l.Next[i]]);
            float t = (float)rng.NextDouble() * sum, acc = 0f;
            for (int i = 0; i < l.Next.Length; i++) { acc += TurnWeight(g.Lanes[l.Next[i]]); if (t < acc) return l.Next[i]; }
            return l.Next[l.Next.Length - 1];
        }

        // bei Kettenreservierung steht die nächste Wahl schon fest
        private int NextPlanned(Car car, int laneId)
        {
            var l = g.Lanes[laneId];
            for (int k = 0; k < car.ResN; k++)
                if (car.ResState[k] == 0) for (int q = 0; q < l.Next.Length; q++) if (l.Next[q] == car.Res[k]) return car.Res[k];
            return ChooseNext(laneId);
        }

        private float TurnWeight(TrafficGraph.Lane conn)
        {
            float w = conn.Turn == TrafficGraph.TurnStraight ? 3f : conn.Turn == TrafficGraph.TurnU ? .4f : 1.4f;
            var nl = g.Lanes[conn.Next[0]];
            if ((nl.Flags & TrafficGraph.FlagRoute) != 0) w *= 2f;
            return w;
        }

        public const float ShortLane = 22f;                     // Exit-Spuren kürzer als das: nächste Kreuzung gleich mitreservieren

        private static int FirstOpen(Car car)
        {
            for (int k = 0; k < car.ResN; k++) if (car.ResState[k] == 0) return k;
            return car.ResN;
        }

        private static int ResIndex(Car car, int conn)
        {
            for (int k = 0; k < car.ResN; k++) if (car.Res[k] == conn && car.ResState[k] == 0) return k;
            return -1;
        }

        private static void DropFrom(Car car, int idx)
        {
            // offene Einträge ab idx verwerfen (Einträge davor sind schon befahren)
            int n = idx; while (n > 0 && car.ResState[n - 1] == 0) n--;
            for (int k = idx; k < car.ResN; k++) if (car.ResState[k] == 0) { car.ResN = k; break; }
        }

        private static void ResAdd(Car car, int conn, bool granted)
        {
            if (car.ResN >= Car.MaxRes) return;
            int k = car.ResN++; car.Res[k] = conn; car.ResState[k] = 0; car.ResGranted[k] = granted; car.ResExitOdo[k] = 0f;
        }

        private void MakeRequest(Car car, float distEnd)
        {
            var conn = g.Lanes[car.NextConn];
            car.PhysBlocked = false; car.ReqTime = time;
            float eta = distEnd / Mathf.Max(car.V, 3f);
            car.ReqKey = time + eta - 4f * conn.Prio;
            ResAdd(car, car.NextConn, false);
            // kurze Exit-Spur: die nächste Kreuzung gleich mitreservieren (sonst kann das Auto dort nicht mehr anhalten)
            int c = car.NextConn; int chain = 0;
            while (chain < 2 && car.ResN < Car.MaxRes)
            {
                var e = g.Lanes[g.Lanes[c].Next[0]];
                if (e.Length >= ShortLane || e.Next.Length == 0) break;
                int c2 = ChooseNext(e.Id);
                if (c2 < 0) break;
                ResAdd(car, c2, false); c = c2; chain++;
            }
        }

        // Darf das Auto die Einträge ab 'idx' befahren? Alles oder nichts.
        private void TryGrant(int me, Car car, int idx)
        {
            float key = car.ReqKey; if (time - car.ReqTime > 20f) key -= 1000f;
            if (RiderSet && RiderBehindClose(car)) key -= 1000f;      // Radfahrer kommt von hinten auf die Haltelinie zu: zuerst weg
            car.PhysBlocked = false;
            for (int k = idx; k < car.ResN; k++)
            {
                if (car.ResState[k] != 0) continue;
                if (!CanEnter(me, car, g.Lanes[car.Res[k]], key)) { if (car.PhysBlocked) { for (int q = idx; q < car.ResN; q++) car.ResGranted[q] = false; } return; }
            }
            for (int k = idx; k < car.ResN; k++) if (car.ResState[k] == 0) car.ResGranted[k] = true;
            car.GrantTime = time;
        }

        private bool CanEnter(int me, Car car, TrafficGraph.Lane conn, float key)
        {
            var hub = g.Hubs[conn.Hub]; int n = hub.Conns.Length;
            // 1) physisch: Platz hinter der Kreuzung, Radfahrer, fremde Karosserien auf dem Pfad
            if (!PhysFree(me, car, conn)) { car.PhysBlocked = true; return false; }
            // 2) Konflikte mit Reservierungen anderer
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                var o = cars[j];
                for (int k = 0; k < o.ResN; k++)
                {
                    int oc = o.Res[k];
                    if (oc == conn.Id) continue;
                    var ocl = g.Lanes[oc];
                    if (ocl.Hub != conn.Hub) continue;
                    if (!hub.Conflict[conn.Slot * n + ocl.Slot]) continue;
                    if (o.ResGranted[k]) return false;
                    if (o.ResState[k] != 0 || !o.Eligible) continue;                    // nur wartende, nahe Autos gehen vor
                    if (k > FirstOpen(o) && !o.ResGranted[FirstOpen(o)]) continue;      // Kettenglied weit hinter der ersten, noch nicht freigegebenen Kreuzung: nicht gleich dran
                    float ok = o.ReqKey; if (time - o.ReqTime > 20f) ok -= 1000f;
                    if (!(ok < key || (ok == key && j < me))) continue;
                    if (!OrderFree(j, o, ocl)) continue;                                 // der Vordermann in der Reihe kann dauerhaft nicht fahren (Stau auf der Exit-Spur): nicht blockieren
                    return false;
                }
            }
            return true;
        }

        // Diagnose (nur Tests): warum darf das wartende Auto nicht einfahren?
        public string Explain(int me)
        {
            var car = cars[me]; var sb = new System.Text.StringBuilder();
            for (int k = 0; k < car.ResN; k++)
            {
                if (car.ResState[k] != 0) continue;
                var conn = g.Lanes[car.Res[k]]; var exit = g.Lanes[conn.Next[0]]; var hub = g.Hubs[conn.Hub]; int n = hub.Conns.Length;
                sb.Append($"      entry {k} conn {conn.Id} hub {conn.Hub} exit {exit.Id} (len {exit.Length:0.0}) granted={car.ResGranted[k]} room={RoomFrom(exit.Id, me, 3, 4f, 6f):0.0}/{car.Length + 3f:0.0} riderNear={(RiderSet && RiderNearConn(conn))} riderAppr={(RiderSet && RiderApproachingExit(conn, exit))} pathOcc={PathOccupied(me, car, conn)}\n");
                for (int j = 0; j < cars.Length; j++)
                {
                    if (j == me || !cars[j].Active) continue;
                    var o = cars[j];
                    for (int q = 0; q < o.ResN; q++)
                    {
                        var ocl = g.Lanes[o.Res[q]];
                        if (o.Res[q] == conn.Id || ocl.Hub != conn.Hub || !hub.Conflict[conn.Slot * n + ocl.Slot]) continue;
                        sb.Append($"         vs car {j} conn {ocl.Id} state={o.ResState[q]} granted={o.ResGranted[q]} elig={o.Eligible} key={o.ReqKey:0.0} wait={time - o.ReqTime:0.0} orderFree={OrderFree(j, o, ocl)} lane={o.Lane} S={o.S:0.0} v={o.V:0.0}\n");
                    }
                }
                sb.Append($"         myKey={car.ReqKey:0.0} wait={time - car.ReqTime:0.0} elig={car.Eligible}\n");
            }
            return sb.ToString();
        }

        // Für die Reihenfolge: steht dem Wartenden etwas Dauerhaftes im Weg? Durchfahrende (auch langsame) Autos und Reservierungen anderer zählen nicht,
        // sonst könnte ein geschlossener Pulk auf der Nachbarspur den Wartenden endlos vorbeilassen (Kreisverkehr: Zufahrt gegen Ringverkehr)
        private bool OrderFree(int me, Car car, TrafficGraph.Lane conn)
        {
            var exit = g.Lanes[conn.Next[0]];
            if (RoomFrom(exit.Id, me, 3, 1.5f, -999f) < car.Length + 3f) return false;
            return !(RiderSet && (RiderNearConn(conn) || RiderApproachingExit(conn, exit)));
        }

        // Kann 'car' die Verbindung jetzt physisch befahren? (Kastenregel: Platz ab Anfang der Exit-Spur, Radfahrer, fremde Karosserien)
        private bool PhysFree(int me, Car car, TrafficGraph.Lane conn)
        {
            var exit = g.Lanes[conn.Next[0]];
            float need = car.Length + 3f;
            if (RoomFrom(exit.Id, me, 3, 4f, 6f) < need) return false;
            if (RiderSet && (RiderNearConn(conn) || RiderApproachingExit(conn, exit))) return false;
            return !PathOccupied(me, car, conn);
        }

        // Steht ein fremdes Auto (Karosserie) gerade auf dem Pfad der Verbindung? Dann nicht einfahren (sonst Verklemmung: der Wartende steht im Weg)
        private bool PathOccupied(int me, Car car, TrafficGraph.Lane conn)
        {
            float rMe = car.Width * .5f, off = car.Length * .5f - rMe; int h = 0;
            for (float s = 0f; s <= conn.Length + .01f; s += 1.5f)
            {
                conn.Eval(Mathf.Min(s, conn.Length), ref h, out float x, out _, out float z, out float hx, out float hz);
                for (int j = 0; j < cars.Length; j++)
                {
                    if (j == me || !cars[j].Active) continue;
                    var o = cars[j];
                    if (o.Lane == car.Lane || o.Lane == conn.Id) continue;          // Vordermann auf eigener Spur/Verbindung regelt das Folgen
                    float R = rMe + o.Width * .5f + .2f, R2 = R * R;
                    for (int k = 0; k < 3; k++)
                    {
                        float mx = x + hx * off * (k - 1), mz = z + hz * off * (k - 1);      // meine drei Kreise an dieser Stelle des Pfads
                        for (int q = 0; q < 3; q++) if (Dist2(mx, mz, cxs[j * 3 + q], czs[j * 3 + q]) < R2) return true;
                    }
                }
            }
            return false;
        }

        // Freie Länge ab dem Anfang einer Spur bis zum ersten Heck davor; ist die Spur leer und kürzer als nötig, zählen die Folgespuren mit
        private float RoomFrom(int laneId, int skipCar, int depth, float vMoving, float rearFree)
        {
            var l = g.Lanes[laneId]; float rearMin = float.MaxValue;
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == skipCar || !cars[j].Active || cars[j].Lane != laneId) continue;
                float rear = cars[j].S - cars[j].Length + .9f;
                if (cars[j].V > vMoving && rear > rearFree) continue;                      // fährt schon weg
                if (rear < rearMin) rearMin = rear;
            }
            if (rearMin < float.MaxValue) return Mathf.Max(0f, rearMin);
            if (depth <= 0 || l.Next.Length == 0) return l.Length + 60f;
            float best = float.MaxValue;
            for (int q = 0; q < l.Next.Length; q++) best = Mathf.Min(best, RoomFrom(l.Next[q], skipCar, depth - 1, vMoving, rearFree));
            return l.Length + best;
        }

        // Radfahrer kommt auf die Exit-Spur zu (gleiche Richtung, in ~4 s dort): das Auto soll sich nicht davor setzen
        private bool RiderApproachingExit(TrafficGraph.Lane conn, TrafficGraph.Lane exit)
        {
            float rs2 = RiderVX * RiderVX + RiderVZ * RiderVZ; if (rs2 < .25f) return false;
            float rs = Mathf.Sqrt(rs2);
            int h = 0; exit.Eval(0f, ref h, out float x, out _, out float z, out float hx, out float hz);
            float dx = x - RiderX, dz = z - RiderZ; float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d > 14f + 4f * rs || d < 1e-3f) return false;
            float toward = (RiderVX * dx + RiderVZ * dz) / (rs * d);          // > 0: Radfahrer fährt auf den Punkt zu
            float same = (RiderVX * hx + RiderVZ * hz) / rs;                   // > 0: gleiche Richtung wie die Exit-Spur
            return toward > .3f && same > .6f;
        }

        // Radfahrer hinter dem Auto (bis 35 m) in dessen Fahrtrichtung: das Auto sollte die Kreuzung bald räumen
        private bool RiderBehindClose(Car car)
        {
            float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hl < 1e-4f) return false;
            float hx = car.Hx / hl, hz = car.Hz / hl;
            float dx = RiderX - car.X, dz = RiderZ - car.Z;
            float lon = dx * hx + dz * hz, lat = dx * hz - dz * hx;
            float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ);
            return lon < -2f && lon > -35f && Mathf.Abs(lat) < 3.5f && rs > .5f && (RiderVX * hx + RiderVZ * hz) / rs > .6f;
        }

        private bool RiderNearConn(TrafficGraph.Lane conn)
        {
            int h = 0;
            for (float s = 0f; s <= conn.Length + .01f; s += 2f)
            {
                conn.Eval(Mathf.Min(s, conn.Length), ref h, out float x, out _, out float z, out _, out _);
                if (Dist2(x, z, RiderX, RiderZ) < 3.8f * 3.8f) return true;
                float rs2 = RiderVX * RiderVX + RiderVZ * RiderVZ;
                if (rs2 > .36f && Dist2(x, z, RiderX + RiderVX * 1.5f, RiderZ + RiderVZ * 1.5f) < 3f * 3f) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Bewegung
        private void Integrate(Car car, float a, float dt)
        {
            float v = car.V;
            float vNew = Mathf.Max(0f, v + a * dt);
            float ds = (v + vNew) * .5f * dt;
            car.V = vNew; car.S += ds; car.Odo += ds;
            // seitlicher Versatz
            float rate = LateralRate * Mathf.Clamp(car.V / 4f, .3f, 1f) * dt;
            float d = car.OffTarget - car.Off;
            if (g.Lanes[car.Lane].Type != TrafficGraph.Road) { car.OffTarget = 0f; rate = Mathf.Max(rate, 1.5f * dt); d = -car.Off; }
            car.Off += Mathf.Clamp(d, -rate, rate);
            car.StoppedFor = car.V < .3f ? car.StoppedFor + dt : 0f;

            // Spurwechsel (Vorderachse)
            int guard = 0;
            while (guard++ < 4)
            {
                var lane = g.Lanes[car.Lane];
                if (car.S <= lane.Length) break;
                float carry = car.S - lane.Length;
                if (lane.Type == TrafficGraph.Road)
                {
                    if (car.NextConn < 0)
                    {
                        // Senke: am Ende verschwinden (nur weit vom Radfahrer), sonst stehen bleiben
                        if (!RiderSet || Dist2(car.X, car.Z, RiderX, RiderZ) > 80f * 80f) { SinkDespawns++; Deactivate(car, true); return; }
                        car.S = lane.Length; car.V = 0f; break;
                    }
                    int ri = ResIndex(car, car.NextConn);
                    if (!(ri >= 0 && car.ResGranted[ri]))
                    {
                        // Notbremse: ohne Freigabe nicht in die Kreuzung (passiert nur, wenn die Spur zu kurz zum Anhalten war)
                        ForcedStopTicks++; if (car.V > 1f) { ForcedStops++; LastForcedLane = lane.Id; LastForcedSpeed = car.V; }
                        car.S = lane.Length - .05f; car.V = 0f; break;
                    }
                    car.ResState[ri] = 1;
                    EnterLane(car, car.NextConn, carry);
                    var cn = g.Lanes[car.Lane];
                    car.NextConn = NextPlanned(car, cn.Next[0]);
                }
                else
                {
                    int nl = lane.Next[0];
                    for (int k = 0; k < car.ResN; k++) if (car.Res[k] == lane.Id && car.ResState[k] == 1) { car.ResState[k] = 2; car.ResExitOdo[k] = car.Odo - carry; }
                    EnterLane(car, nl, carry);
                    if (car.NextConn < 0) car.NextConn = NextPlanned(car, nl);
                }
            }
            // Reservierung freigeben, wenn das Auto den Verbinder vollständig verlassen hat (Heck über das Ende hinaus)
            while (car.ResN > 0 && car.ResState[0] == 2 && car.Odo - car.ResExitOdo[0] >= car.Length - .9f)
            {
                for (int k = 1; k < car.ResN; k++) { car.Res[k - 1] = car.Res[k]; car.ResState[k - 1] = car.ResState[k]; car.ResGranted[k - 1] = car.ResGranted[k]; car.ResExitOdo[k - 1] = car.ResExitOdo[k]; }
                car.ResN--;
            }
            UpdatePose(car);

            // Entfernung
            if (RiderSet)
            {
                float d2 = Dist2(car.X, car.Z, RiderX, RiderZ);
                if (d2 > Cfg.Despawn * Cfg.Despawn) { Despawned++; Deactivate(car, true); return; }
                if (car.StoppedFor > 25f && d2 > 60f * 60f) { StuckCulls++; if (Log != null) Log($"stuck cull: Lane {car.Lane} ({g.Lanes[car.Lane].Type}) S={car.S:0.0}/{g.Lanes[car.Lane].Length:0.0} res={car.ResN} blocked={car.PhysBlocked} next={car.NextConn} pos=({car.X:0},{car.Z:0})"); Deactivate(car, true); return; }
                if (d2 > 60f * 60f && car.StoppedFor > MaxStoppedFar) MaxStoppedFar = car.StoppedFor;
            }
        }

        private void EnterLane(Car car, int laneId, float s)
        {
            car.Prev2 = car.Prev1; car.Prev1 = car.Lane; car.Lane = laneId; car.S = s; car.LaneHint = 0;
        }

        private void Deactivate(Car car, bool counted)
        {
            car.Active = false; car.ResN = 0; car.Passing = false; car.NextConn = -1; car.V = 0f;
        }

        // Pose aus Vorder- und Hinterachse auf der Spur
        private void UpdatePose(Car car)
        {
            var lane = g.Lanes[car.Lane];
            lane.Eval(car.S, ref car.LaneHint, out float fx, out float fy, out float fz, out float fhx, out float fhz);
            float off = car.Off;
            fx += fhz * off; fz -= fhx * off;
            float sr = car.S - car.Wheelbase;
            float rx, ry, rz, rhx, rhz;
            if (sr >= 0f) { int h = car.PrevHint; lane.Eval(sr, ref h, out rx, out ry, out rz, out rhx, out rhz); car.PrevHint = h; }
            else
            {
                int pl = car.Prev1; float s2 = sr;
                if (pl >= 0) { var p = g.Lanes[pl]; s2 = p.Length + sr; if (s2 < 0f && car.Prev2 >= 0) { var p2 = g.Lanes[car.Prev2]; pl = car.Prev2; s2 += p2.Length; p = p2; } if (s2 < 0f) s2 = 0f; int h = 0; p.Eval(s2, ref h, out rx, out ry, out rz, out rhx, out rhz); }
                else { int h = 0; lane.Eval(0f, ref h, out rx, out ry, out rz, out rhx, out rhz); rx -= rhx * -sr; rz -= rhz * -sr; }
            }
            rx += rhz * off; rz -= rhx * off;
            float dx = fx - rx, dz = fz - rz, dy = fy - ry; float l2 = Mathf.Sqrt(dx * dx + dz * dz);
            car.X = (fx + rx) * .5f; car.Z = (fz + rz) * .5f; car.Y = (fy + ry) * .5f;
            if (l2 > .2f)
            {
                float l3 = Mathf.Sqrt(l2 * l2 + dy * dy);
                car.Hx = dx / l3; car.Hz = dz / l3; car.Hy = dy / l3;
            }
        }

        // ------------------------------------------------------------------ Spawn / Despawn
        private void ManageSpawns(float dt)
        {
            spawnTimer -= dt;
            if (spawnTimer > 0f) return;
            spawnTimer = .35f;
            int active = 0; for (int i = 0; i < cars.Length; i++) if (cars[i].Active) active++;
            if (active >= cars.Length) return;
            RebuildCandidates();
            if (candCount == 0) return;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float t = (float)rng.NextDouble() * candCum[candCount - 1];
                int lo = 0, hi = candCount - 1;
                while (lo < hi) { int mid = (lo + hi) >> 1; if (candCum[mid] < t) lo = mid + 1; else hi = mid; }
                var lane = g.Lanes[cand[lo]];
                if (lane.Length < 26f) continue;
                float s = 8f + (float)rng.NextDouble() * (lane.Length - 22f);
                int h = 0;
                lane.Eval(s, ref h, out float x, out float y, out float z, out float hx, out float hz);
                float d2 = Dist2(x, z, RiderX, RiderZ);
                if (d2 < Cfg.SpawnMin * Cfg.SpawnMin || d2 > Cfg.SpawnMax * Cfg.SpawnMax) continue;
                bool free = true;
                for (int j = 0; j < cars.Length && free; j++)
                    if (cars[j].Active && Dist2(x, z, cars[j].X, cars[j].Z) < 16f * 16f) free = false;
                if (!free) continue;
                for (int p = 0; p < g.Parked.Count && free; p++)
                    if (Dist2(x, z, g.Parked[p].x, g.Parked[p].y) < 3.2f * 3.2f) free = false;
                if (!free) continue;
                int slot = -1; for (int i = 0; i < cars.Length; i++) if (!cars[i].Active) { slot = i; break; }
                if (slot < 0) return;
                var car = cars[slot];
                car.Active = true; car.Serial++; car.Lane = lane.Id; car.Prev1 = car.Prev2 = -1; car.LaneHint = car.PrevHint = 0;
                car.S = s; car.Off = car.OffTarget = 0f; car.ResN = 0; car.PhysBlocked = false; car.Passing = false; car.Odo = 0f; car.StoppedFor = 0f; car.Age = 0f;
                car.V0Factor = .88f + (float)rng.NextDouble() * .2f; car.AccelFactor = .8f + (float)rng.NextDouble() * .4f; car.TimeGap = 1.1f + (float)rng.NextDouble() * .5f;
                car.V = lane.Speed * car.V0Factor * .8f;
                car.NextConn = ChooseNext(lane.Id);
                UpdatePose(car);
                Spawned++;
                return;
            }
        }

        private void RebuildCandidates()
        {
            if (!float.IsNaN(candX) && Dist2(candX, candZ, RiderX, RiderZ) < 40f * 40f) return;
            candX = RiderX; candZ = RiderZ; candCount = 0; float acc = 0f;
            float lo = Cfg.SpawnMin - 50f, hi = Cfg.SpawnMax + 50f;
            for (int i = 0; i < g.Lanes.Count; i++)
            {
                var l = g.Lanes[i];
                if (l.Type != TrafficGraph.Road || l.Weight <= 0f || l.Sink && l.Length < 30f) continue;
                float d = Mathf.Sqrt(Dist2(laneCx[i], laneCz[i], RiderX, RiderZ));
                if (d + laneR[i] < lo || d - laneR[i] > hi) continue;
                acc += l.Weight; cand[candCount] = i; candCum[candCount] = acc; candCount++;
            }
        }

        private static float Dist2(float ax, float az, float bx, float bz) { float dx = ax - bx, dz = az - bz; return dx * dx + dz * dz; }
    }
}
