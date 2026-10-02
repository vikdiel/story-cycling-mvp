using System;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Simulationskern des Hintergrundverkehrs: reines C# (keine Unity-Objekte, keine Physik), damit er ohne Editor testbar ist.
    //   - Autos fahren auf den Spuren des TrafficGraph (Linksverkehr), Längsdynamik IDM (Folgeabstand, Haltelinie, Kurven-/Abbiegetempo)
    //   - Kreuzungen: Reservierung je Verbinder (FIFO nach Ankunftszeit + Vorrang), kreuzende/einmündende Verbinder schließen sich aus,
    //     Kastenregel (nur einfahren, wenn dahinter Platz ist), Zeitlimit gegen Verklemmen
    //   - Radfahrer (kennt die Sim nur als Position + Geschwindigkeit, er ist nicht anhaltbar): Lage im Pfad des Autos (Station/Querlage), dann
    //       * Auto hinter dem langsameren Rad: folgt mit >= 1,5 s / 5 m Abstand (IDM), überholt erst bei Platz, freier Gegenspur, Sichtweite
    //         (Sehnenabstand der Spurgeometrie voraus) und genug Strecke bis zur nächsten Kreuzung; hält dann >= 1,5 m Abstand (Karosseriekante -> Radmitte),
    //         bleibt nach außen, bis das Heck >= 5 m vor dem Rad ist, und kehrt weich zurück (Querbeschleunigung begrenzt)
    //       * schnelleres Rad kommt von hinten / entgegen (Gefälle, Schlange, Ampel): das Auto weicht zur Seite aus (>= 1 m), bis das Rad vorbei ist
    //       * Rad quer zum Pfad: Zeit-Raum-Test, das Auto hält vor dem Konfliktpunkt; Kreuzungsfreigabe nur, wenn das Rad nicht im Zeitfenster des Autos ankommt
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
            public int Mode, Side;                                   // Radfahrer-Interaktion: 0 frei, 1 überholt (Passing), 2 weicht aus; Seite beim Ausweichen (+1 rechts, -1 links)
            public float OffV, PassCool, EvalT, WaitT;                      // seitliche Geschwindigkeit; Sperrzeit nach Überholen/Abbruch; Wartezeit hinter einem langsamen/stehenden Rad
            public float Accel;                                      // zuletzt berechnete Beschleunigung (Diagnose)
        }

        public struct Tuning
        {
            public int Cars; public float SpawnMin, SpawnMax, Despawn;
            public static Tuning Default => new Tuning { Cars = 24, SpawnMin = 120f, SpawnMax = 450f, Despawn = 500f };
        }

        // Fahrer/Fahrzeug-Konstanten
        // Abstände = Karosseriekante -> Radmitte (seitlich)
        public const float PassClear = 1.8f, PassMin = 1.5f;      // Überholen: Ziel / angenommenes Minimum (harte Grenze 1,0 m)
        public const float HoldClear = 1.5f;                      // darunter gilt das Rad als im Weg (Hindernis für die Längsregelung)
        public const float YieldClear = 1.0f, YieldPref = .5f;    // Ausweichen vor schnellerem/entgegenkommendem Rad (Rad nicht anhaltbar): Mindest- und bevorzugter Zusatzabstand (Karosserie -> Radmitte)
        public const float ReturnGap = 5f, ReturnTime = 1.3f;     // Heck mindestens so weit vor dem Rad, bevor das Auto zurückkehrt; Rückkehrdauer
        public const float RiderStopGap = 5f, RiderTimeGap = 1.5f; // Folgen: Mindestabstand Stoßstange -> Radmitte im Stand, Zeitlücke
        public const float BumpTime = 2.5f;                           // Weltgeometrie-Verfeinerung des Ausweichens: so viele Sekunden vor dem Eintreffen
        public const float PatienceMin = .5f;                         // kleinster Faktor auf die geforderte Sichtweite nach langem Warten (ab 8 s, voll nach 38 s)
        public const float OncomingGap = .6f, OncomingLeft = .6f;   // Gegenverkehr: seitliche Luft zwischen zwei Karosserien / größter Versatz zum linken Fahrbahnrand
        public const float SafeTime = .6f, SafeMargin = .1f;                  // letzte Sicherung: Auto verschwindet, wenn es dem (nicht anhaltbaren) Radfahrer in SafeTime s näher als Radius + SafeMargin käme
        public const float PassBoost = 1.6f;                                   // beim Überholen stärker beschleunigen (Kickdown)
        public const float JunctionBuf = 20f, SightSag = 3.5f, LeftRoom = 1.2f, AbortGap = 8f, KerbExtra = 1f;   // KerbExtra: Ausweichen darf notfalls so weit über den Fahrbahnrand hinaus (Bordstein/Bankett)
        public const float StopGap = 2.0f, Brake = 2.0f, BrakeLimit = 2.3f, MaxDecel = 9f, LateralRate = 1.2f, LateralAcc = 1.2f;
        public const float ProbeStep = 1.5f, RequestDist = 100f, StopLine = 2.4f;

        private readonly TrafficGraph g;
        private readonly Car[] cars;
        private readonly System.Random rng;
        public Tuning Cfg;
        private float time, spawnTimer;
        public float Time => time;

        // Radfahrer
        public float RiderX, RiderZ, RiderVX, RiderVZ; public bool RiderSet;
        private float riderYaw, prevVX, prevVZ;                // geschätzte Gierrate des Radfahrers (rad/s) für die Bahnvorhersage in Kurven/Kreisverkehren (wenn keine Fahrlinie bekannt ist)
        // Fahrlinie des Radfahrers voraus (optional, vom TrafficSystem aus Route + Fahrspur-Versatz): Punkte im Abstand TrackStep, Punkt 0 = jetzige Radposition.
        // Damit ist die Bahnvorhersage in Kurven, Kreisverkehren und bei Sprüngen des Spur-Versatzes exakt (RiderAt), sonst Kreisbogen aus der Gierrate.
        public const int TrackMax = 48;
        public readonly float[] TrackX = new float[TrackMax], TrackZ = new float[TrackMax];
        public int TrackN, TrackMismatch; public float TrackStep = 3f, TrackSpeed; private float trackDx, trackDz;

        // Spawnkandidaten
        private readonly float[] laneCx, laneCz, laneR;
        private readonly int[] cand; private readonly float[] candCum; private int candCount; private float candX = float.NaN, candZ;

        // Zwischenpuffer
        private const int MaxProbe = 96, NB = 36, NP = NB + MaxProbe; private const float BackStep = 3f;   // hinter dem Auto: 36 Stützpunkte im Abstand 3 m (108 m), voraus 96 im Abstand 1,5 m
        private readonly float[] px = new float[MaxProbe], pz = new float[MaxProbe], phx = new float[MaxProbe], phz = new float[MaxProbe];   // Pfad der Karosserie (mit Querversatz)
        private readonly float[] cx = new float[NP], cz = new float[NP], chx = new float[NP], chz = new float[NP], csta = new float[NP];    // Mittelpfad; Index NB + q voraus (q * ProbeStep), Index < NB hinter dem Auto
        private int probeCount, probe0, pathN; private bool behindOk;
        private bool rNear, rSame, rOpp, rMov; private float rSta, rLat, rAl, rLatV, riderGap, riderLv;      // Radfahrer relativ zum Pfad des gerade betrachteten Autos
        private readonly float[] sgx = new float[64], sgz = new float[64], sgs = new float[64], sgc = new float[64];                                           // Sichtweite: Stützpunkte voraus
        private readonly float[] cxs, czs;                     // Kreise je Auto: 3 an der jetzigen Pose, 3 am Zielversatz (Ausweichen/Überholen ist angekündigt -> Gegenverkehr wartet rechtzeitig)
        private readonly float[] accel;
        private readonly int[] near = new int[64];
        private readonly int[] pathLane = new int[5];

        // Zähler für Prüfung/Diagnose
        public int ForcedStopTicks, LastForcedLane; public float LastForcedSpeed;
        public int Spawned, Despawned, StuckCulls, ForcedStops, SinkDespawns, PassStarted, PassAborted, PassDone, GrantTimeouts, EmergencyBrakes, SafetyRemovals;
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
            cxs = new float[cars.Length * 6]; czs = new float[cars.Length * 6]; accel = new float[cars.Length];
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

        // Fahrlinie voraus setzen (TrackX/TrackZ vorher füllen): n Punkte im Abstand step, Tempo entlang der Linie. Weicht Punkt 0 mehr als 2,5 m von der
        // gemeldeten Radposition ab, stimmt die Linie nicht zum Radfahrer (falsch verdrahtet) und wird nicht benutzt.
        public void SetRiderTrack(int n, float step, float speed)
        {
            TrackN = 0;
            if (n < 2 || !RiderSet) return;
            float dx = RiderX - TrackX[0], dz = RiderZ - TrackZ[0];
            if (dx * dx + dz * dz > 2.5f * 2.5f) { TrackMismatch++; return; }
            trackDx = dx; trackDz = dz; TrackN = n; TrackStep = step; TrackSpeed = speed;
        }

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
            UpdateRiderYaw(dt);
            if (RiderSet) ManageSpawns(dt);
            int n = cars.Length;
            for (int i = 0; i < n; i++)
            {
                var c = cars[i]; if (!c.Active) continue;
                float hx = c.Hx, hz = c.Hz; float hl = Mathf.Sqrt(hx * hx + hz * hz); if (hl > 1e-4f) { hx /= hl; hz /= hl; }
                float off = c.Length * .5f - c.Width * .5f;
                float dl = c.OffTarget - c.Off; if (dl > -.05f && dl < .05f) dl = 0f;
                for (int k = 0; k < 3; k++) { float ax = c.X + hx * off * (k - 1), az = c.Z + hz * off * (k - 1); cxs[i * 6 + k] = ax; czs[i * 6 + k] = az; cxs[i * 6 + 3 + k] = ax + hz * dl; czs[i * 6 + 3 + k] = az - hx * dl; }
            }
            for (int i = 0; i < n; i++) if (cars[i].Active) accel[i] = Decide(i, dt);
            for (int i = 0; i < n; i++) if (cars[i].Active) Integrate(cars[i], accel[i], dt);
        }

        private void UpdateRiderYaw(float dt)
        {
            float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ), ps = Mathf.Sqrt(prevVX * prevVX + prevVZ * prevVZ);
            if (RiderSet && rs > 1.5f && ps > 1.5f)
            {
                float w = Mathf.Atan2(prevVX * RiderVZ - prevVZ * RiderVX, prevVX * RiderVX + prevVZ * RiderVZ) / dt;
                float wMax = 4f / Mathf.Max(rs, 3f);                                  // Querbeschleunigung <= 4 m/s²: sonst ist es Rauschen der Geschwindigkeitsschätzung
                riderYaw = Mathf.Lerp(riderYaw, Mathf.Clamp(w, -wMax, wMax), .12f);
            }
            else riderYaw *= .8f;
            prevVX = RiderVX; prevVZ = RiderVZ;
        }

        // Radfahrer in t Sekunden: Kreisbogen mit der geschätzten Gierrate (gerade bei kleiner Gierrate)
        private void RiderAt(float t, out float x, out float z)
        {
            if (TrackN >= 2)
            {
                float k = TrackSpeed * t / TrackStep; int i = (int)k;
                if (i >= TrackN - 1)
                {
                    int l = TrackN - 1; float ex = TrackX[l] - TrackX[l - 1], ez = TrackZ[l] - TrackZ[l - 1], el = Mathf.Sqrt(ex * ex + ez * ez); if (el < 1e-4f) el = 1f;
                    float extra = (k - l) * TrackStep;
                    x = TrackX[l] + ex / el * extra + trackDx; z = TrackZ[l] + ez / el * extra + trackDz;
                }
                else { float f = k - i; x = TrackX[i] + (TrackX[i + 1] - TrackX[i]) * f + trackDx; z = TrackZ[i] + (TrackZ[i + 1] - TrackZ[i]) * f + trackDz; }
                return;
            }
            float w = riderYaw, a = w * t;
            if (Mathf.Abs(a) < .02f) { x = RiderX + RiderVX * t; z = RiderZ + RiderVZ * t; return; }
            float sn = Mathf.Sin(a), cs = Mathf.Cos(a);
            x = RiderX + (RiderVX * sn + RiderVZ * (cs - 1f)) / w; z = RiderZ + (RiderVX * (1f - cs) + RiderVZ * sn) / w;
        }

        // Letzte Sicherung: kommt die Karosserie dem Radfahrer innerhalb von SafeTime Sekunden näher als Radius + SafeMargin, ist das Problem für das Auto nicht mehr
        // lösbar -> das Auto wird entfernt, statt durch das Rad zu fahren. Liegt der Radfahrer im Pfad des Autos (gleiche/Gegenrichtung), wird im Pfadsystem gerechnet
        // (Station/Querlage laufen linear weiter; Kurven fallen heraus), sonst mit drei Kreisen auf dem Pfad voraus gegen die vorhergesagte Radfahrerbahn.
        private bool RiderWillHit(Car car)
        {
            if (!RiderSet || probeCount == 0) return false;
            float halfW = car.Width * .5f, halfL = car.Length * .5f, rad = .35f + SafeMargin;
            for (float t = 0f; t <= SafeTime + .001f; t += .1f)
            {
                RiderAt(t, out float rx, out float rz);
                BodyAt(car, t, false, out float mx, out float mz, out float hx, out float hz);
                float dist = RectDist(rx - mx, rz - mz, hx, hz, halfL, halfW, out _);
                if (dist < rad) { if (Log != null) Log($"  hit t={t:0.0} dist={dist:0.00} rider=({rx - RiderX:0.0},{rz - RiderZ:0.0}) body=({mx - RiderX:0.0},{mz - RiderZ:0.0}) rv=({RiderVX:0.0},{RiderVZ:0.0})"); return true; }
            }
            return false;
        }

        // Karosserie zur Zeit t auf der geplanten Bahn: Mitte (zwischen den Achsen) und Richtung; target = mit dem Zielversatz statt dem erwarteten Verlauf.
        // Erster Pfadpunkt (stehendes/langsames Auto): echte Pose (Sehne zwischen den Achsen) — die Spurtangente an der Vorderachse weicht in Kurven und
        // Kreuzungen um einige Grad ab, das verschiebt die Ecken um Dezimeter (genau dort streifte das Rad sonst stehende Autos an der Haltelinie).
        private void BodyAt(Car car, float t, bool target, out float mx, out float mz, out float hx, out float hz)
        {
            float d = car.V * t; int q = Mathf.Min(probeCount - 1, (int)(d / ProbeStep + .5f)), k = NB + q;
            float off = q < probe0 ? (target ? car.OffTarget : OffsetAt(car, q * ProbeStep)) : 0f;
            if (q == 0)
            {
                float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz);
                hx = hl > 1e-4f ? car.Hx / hl : chx[k]; hz = hl > 1e-4f ? car.Hz / hl : chz[k];
                float dl = off - car.Off; mx = car.X + hx * d + hz * dl; mz = car.Z + hz * d - hx * dl; return;
            }
            hx = chx[k]; hz = chz[k]; float wb2 = car.Wheelbase * .5f;
            mx = cx[k] + hz * off - hx * wb2; mz = cz[k] - hx * off - hz * wb2;
        }

        // Abstand Punkt -> Rechteck (Halbmaße halfL längs, halfW quer; negativ = innen); la = Querlage des Punkts (rechts +)
        private static float RectDist(float ex, float ez, float hx, float hz, float halfL, float halfW, out float la)
        {
            float lo = ex * hx + ez * hz; la = ex * hz - ez * hx;
            float ox = Mathf.Abs(lo) - halfL, oy = Mathf.Abs(la) - halfW;
            return ox > 0f || oy > 0f ? Mathf.Sqrt(Mathf.Max(0f, ox) * Mathf.Max(0f, ox) + Mathf.Max(0f, oy) * Mathf.Max(0f, oy)) : Mathf.Max(ox, oy);
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
                    else if (idx >= 0 && car.ResGranted[idx] && car.ResState[idx] == 0 && RiderSet && distEnd > v * v / 8f + 3f && Dist2(car.X, car.Z, RiderX, RiderZ) < 200f * 200f)
                    {
                        // Freigabe zurücknehmen, solange das Auto noch sicher anhalten kann und der Radfahrer inzwischen auf die Kreuzung zukommt
                        var cn = g.Lanes[car.NextConn];
                        if (RiderConflictsConn(car, cn, g.Lanes[cn.Next[0]]) || RiderApproachingExit(cn, g.Lanes[cn.Next[0]]))
                            for (int q = idx; q < car.ResN; q++) if (car.ResState[q] == 0) car.ResGranted[q] = false;
                    }
                }
            }

            // --- Pfad-Stützpunkte voraus (und hinter dem Auto, wenn der Radfahrer in der Nähe ist)
            float vEff = Mathf.Max(v, 4f);
            float dLook = Mathf.Min(MaxProbe * ProbeStep - 3f, 14f + v * 3f + v * v / (2f * Brake));
            bool riderClose = RiderSet && Dist2(car.X, car.Z, RiderX, RiderZ) < 150f * 150f;
            if (riderClose) dLook = MaxProbe * ProbeStep - 3f;
            BuildPath(car, dLook, riderClose);

            // --- Sollgeschwindigkeit: Spurlimit, Kurvenlimits voraus, Abbiegetempo
            float vlim = SpeedLimit(car, lane);

            // --- Radfahrer: Lage im eigenen Pfad, Überholen / Ausweichen (seitlicher Versatz), Radfahrer als Hindernis
            if (riderClose) RiderRel(car); else rNear = false;
            RiderDecide(i, car, lane, distEnd, vlim, vEff, dt);
            if (car.Mode == 0 && lane.Type == TrafficGraph.Road && !lane.Ring) OncomingYield(i, car);
            if (car.Mode != 1 && car.Off > .05f ? car.OffTarget < car.Off - .01f : car.Off < -.05f && car.OffTarget > car.Off + .01f)
                if (!BandFree(i, car, car.OffTarget, false)) car.OffTarget = car.Off;                  // zurück in die Spur nur, wenn dort Platz ist (Auto dicht hinter/neben mir)
            ApplyOffsets(car, vEff);
            if (i == DebugCar && Log != null && riderClose) Log($"  dbg t={time:0.00} car {i} lane {car.Lane} S={car.S:0.0} v={car.V:0.0} off={car.Off:0.00}->{car.OffTarget:0.00} mode={car.Mode} near={rNear} rSta={rSta:0.0} rLat={rLat:0.00} gap={riderGap:0.0} rd={Mathf.Sqrt(Dist2(car.X, car.Z, RiderX, RiderZ)):0.0}");
            if (riderClose && RiderWillHit(car)) { SafetyRemovals++; if (Log != null) Log($"safety removal car {i} lane {car.Lane} S={car.S:0.0} v={car.V:0.0} off={car.Off:0.00}->{car.OffTarget:0.00} mode={car.Mode} near={rNear} rSta={rSta:0.0} rLat={rLat:0.00} W={car.Width:0.0} room={lane.Room:0.0} rider d={Mathf.Sqrt(Dist2(car.X, car.Z, RiderX, RiderZ)):0.0}"); if (!NoRemove) { Deactivate(car, true); return 0f; } }

            // --- nächstes Hindernis voraus (Autos)
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
            if (riderGap < 1e6f) { float ar = Idm(car, v, vlim, riderGap, v - riderLv, RiderStopGap, Mathf.Max(car.TimeGap, RiderTimeGap)); if (ar < a) a = ar; }
            if (aStop < a) a = aStop;
            if (i == DebugCar && Log != null && riderClose) Log($"  acc t={time:0.00} car {i} v={v:0.0} a={a:0.00} vlim={vlim:0.0} carGap={gap:0.0} riderGap={riderGap:0.0} aStop={aStop:0.00} dist={distEnd:0.0}");
            car.Accel = a;
            return Mathf.Clamp(a, -MaxDecel, 3f);
        }

        private float Idm(Car car, float v, float v0, float gap, float dv) { return Idm(car, v, v0, gap, dv, StopGap, car.TimeGap); }

        private float Idm(Car car, float v, float v0, float gap, float dv, float s0, float timeGap)
        {
            float aMax = 1.6f * car.AccelFactor * (car.Mode == 1 ? PassBoost : 1f);
            v0 = Mathf.Max(v0, .5f);
            float free = 1f - Mathf.Pow(v / v0, 4f);
            if (gap >= 1e6f) return aMax * free;
            float sStar = s0 + Mathf.Max(0f, v * timeGap + v * dv / (2f * Mathf.Sqrt(aMax * Brake)));
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

        // ------------------------------------------------------------------ Pfad (Stützpunkte voraus und hinter dem Auto)
        // cx/cz/chx/chz = Mittelpfad der Spur ohne Querversatz (Index NB + q, q = Stützpunkt ab Vorderachse, negativ = hinter dem Auto);
        // px/pz = Pfad der Karosserie voraus mit dem erwarteten Querversatz (ApplyOffsets)
        private void BuildPath(Car car, float dLook, bool behind)
        {
            var lane = g.Lanes[car.Lane];
            int n = 0;
            pathLane[n++] = car.Lane;
            if (lane.Type == TrafficGraph.Road) { if (car.NextConn >= 0) { pathLane[n++] = car.NextConn; pathLane[n++] = g.Lanes[car.NextConn].Next[0]; } }
            else { int nl = lane.Next[0]; pathLane[n++] = nl; if (car.NextConn >= 0) { pathLane[n++] = car.NextConn; pathLane[n++] = g.Lanes[car.NextConn].Next[0]; } }
            pathN = n;
            probeCount = 0; probe0 = 0;
            int stage = 0; float s = car.S; int hint = car.LaneHint; float traveled = 0f;
            while (probeCount < MaxProbe && traveled <= dLook && stage < n)
            {
                var l = g.Lanes[pathLane[stage]];
                if (s > l.Length) { s -= l.Length; stage++; hint = 0; if (stage >= n) break; continue; }
                l.Eval(s, ref hint, out float x, out _, out float z, out float hx, out float hz);
                int k = NB + probeCount;
                cx[k] = x; cz[k] = z; chx[k] = hx; chz[k] = hz; csta[k] = probeCount * ProbeStep;
                if (stage == 0) probe0 = probeCount + 1;
                probeCount++;
                s += ProbeStep; traveled += ProbeStep;
            }
            behindOk = behind;
            if (behind) for (int k = 1; k <= NB; k++) EvalBack(car, k * BackStep, NB - k);
        }

        private void EvalBack(Car car, float back, int idx)
        {
            float s = car.S - back, x, z, hx, hz;
            var lane = g.Lanes[car.Lane];
            if (s >= 0f) { int h = car.LaneHint; lane.Eval(s, ref h, out x, out _, out z, out hx, out hz); }
            else
            {
                bool ok = false; x = z = hx = hz = 0f;
                if (car.Prev1 >= 0)
                {
                    var p = g.Lanes[car.Prev1]; float s2 = p.Length + s;
                    if (s2 < 0f && car.Prev2 >= 0) { p = g.Lanes[car.Prev2]; s2 += p.Length; }
                    if (s2 >= 0f) { int h = 0; p.Eval(s2, ref h, out x, out _, out z, out hx, out hz); ok = true; }
                }
                if (!ok) { int h = 0; lane.Eval(0f, ref h, out float x0, out _, out float z0, out hx, out hz); x = x0 + hx * s; z = z0 + hz * s; }   // keine Vorspur: gerade verlängern
            }
            cx[idx] = x; cz[idx] = z; chx[idx] = hx; chz[idx] = hz; csta[idx] = -back;
        }

        private void ApplyOffsets(Car car, float vEff)
        {
            for (int q = 0; q < probeCount; q++)
            {
                float off = q < probe0 ? OffsetAt(car, q * ProbeStep) : 0f;
                int k = NB + q;
                px[q] = cx[k] + chz[k] * off; pz[q] = cz[k] - chx[k] * off; phx[q] = chx[k]; phz[q] = chz[k];
            }
        }

        // Querrate: langsame/stehende Autos lenken weniger schnell
        private static float LatRate(float v) { return LateralRate * Mathf.Clamp(v / 4f, .5f, 1f); }

        // erwarteter seitlicher Versatz nach 'dist' Metern (Bewegung mit der Querrate zum Zielversatz; ~0,4 s Anlaufzeit der Querbeschleunigung)
        private static float OffsetAt(Car car, float dist)
        {
            float t = Mathf.Max(0f, dist / Mathf.Max(car.V, 4f) - .4f), step = LatRate(car.V) * t;
            float d = car.OffTarget - car.Off;
            if (d > step) return car.Off + step;
            if (d < -step) return car.Off - step;
            return car.OffTarget;
        }

        // Lage eines Punkts im eigenen Pfad: Station (m ab Vorderachse, negativ = dahinter), Querlage (rechts +), Pfadrichtung; false = nicht im Pfadbereich
        private bool ProjectOnPath(float x, float z, out float sta, out float lat, out float hx, out float hz)
        {
            float best = float.MaxValue; int bi = -1, i0 = behindOk ? 0 : NB, i1 = NB + probeCount - 1;
            for (int i = i0; i <= i1; i++) { float dx = x - cx[i], dz = z - cz[i]; float d2 = dx * dx + dz * dz; if (d2 < best) { best = d2; bi = i; } }
            sta = lat = hx = hz = 0f;
            if (bi < 0 || best > 14f * 14f) return false;
            float ex = x - cx[bi], ez = z - cz[bi]; float along = ex * chx[bi] + ez * chz[bi];
            if ((bi == i0 || bi == i1) && Mathf.Abs(along) > (bi < NB ? BackStep : ProbeStep) + .3f) return false;     // jenseits des Pfadendes
            sta = csta[bi] + along; lat = ex * chz[bi] - ez * chx[bi]; hx = chx[bi]; hz = chz[bi];
            return true;
        }

        // ------------------------------------------------------------------ Hindernisse (andere Autos)
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
                    for (int c = 0; c < 6 && !hit; c++)
                    {
                        float dx = px[q] - cxs[j * 6 + c], dz = pz[q] - czs[j * 6 + c];
                        if (dx * dx + dz * dz < R2) hit = true;
                    }
                    if (!hit) continue;
                    float ohl = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (ohl < 1e-4f) ohl = 1f;
                    float cosA = (o.Hx * phx[q] + o.Hz * phz[q]) / ohl;
                    gap = d; lv = cosA > .3f ? o.V * cosA : 0f;
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ Radfahrer im Pfad des Autos
        // Lage des Radfahrers: Station ab Vorderachse, Querlage (rechts +), Geschwindigkeit entlang des Pfads
        private void RiderRel(Car car)
        {
            rNear = false;
            if (!RiderSet || !ProjectOnPath(RiderX, RiderZ, out rSta, out rLat, out float hx, out float hz) || Mathf.Abs(rLat) > 9f) return;
            float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ);
            rMov = rs > .6f;
            rAl = rMov ? RiderVX * hx + RiderVZ * hz : 0f; rLatV = rMov ? RiderVX * hz - RiderVZ * hx : 0f;
            float cosA = rMov ? rAl / rs : 1f;
            rSame = cosA > .5f; rOpp = cosA < -.5f;
            if (!rMov) rSame = true;
            rNear = true;
        }

        // Vorhergesagter kleinster Abstand (Karosserie-Rechteck -> Radmitte, negativ = Radmitte im Rechteck) auf der geplanten Bahn des Autos (mit dem aktuellen
        // Zielversatz als konstantem Versatz) und der vorhergesagten Radbahn in den nächsten 'horizon' s. Rechnet in Weltkoordinaten, ist also unabhängig von der Pfad-Projektion des Rades
        // (die in Kreuzungs-Knoten und bei springender Spurgeometrie auf Distanz ungenau ist). tMin: Zeitpunkt, lat: Lage des Rades im Bezugssystem des Autos (rechts +).
        private float PredClear(Car car, float horizon, out float tMin, out float lat)
        {
            tMin = 0f; lat = 0f;
            if (probeCount == 0) return 99f;
            float halfW = car.Width * .5f, halfL = car.Length * .5f, best = 99f;
            for (float t = 0f; t <= horizon + .001f; t += .15f)
            {
                BodyAt(car, t, true, out float mx, out float mz, out float hx, out float hz);       // Zielversatz sofort (die Querbewegung selbst dauert, das berücksichtigt der Auslösezeitpunkt)
                RiderAt(t, out float rx, out float rz);
                float dist = RectDist(rx - mx, rz - mz, hx, hz, halfL, halfW, out float la);
                if (dist < best) { best = dist; tMin = t; lat = la; }
            }
            return best;
        }

        // Ausweichseite und Zielversatz (Pfad-Koordinaten, taugt auch auf Distanz): rechts (+1) = rechts vom Rad + halbe Breite + Abstand, links (-1) = links davon.
        // Eine Seite ist nur möglich, wenn dort Platz ist (Fahrbahnrand + KerbExtra bzw. LeftRoom) und kein anderes Auto im Streifen steht; keep != 0 behält die
        // Seite, solange das Rad nicht auf ihr liegt.
        private int DodgeSide(int me, Car car, float halfW, float right, float keep, out float tgt, out float need)
        {
            float offR = rLat + halfW + YieldClear, offL = rLat - halfW - YieldClear;
            right += KerbExtra;
            int side;
            if (keep != 0f && (keep > 0f ? rLat <= car.Off : rLat >= car.Off)) side = (int)keep;
            else
            {
                float shiftR = Mathf.Max(0f, offR - car.Off), shiftL = Mathf.Max(0f, car.Off - offL);
                bool feasR = offR <= right + .01f && BandFree(me, car, Mathf.Min(offR + YieldPref, right - KerbExtra), true), feasL = offL >= -LeftRoom - .01f && BandFree(me, car, Mathf.Max(offL - YieldPref, -LeftRoom), true);
                if (feasR && (!feasL || shiftR <= shiftL + .3f)) side = 1;
                else if (feasL) side = -1;
                else side = (offR - right) <= (-LeftRoom - offL) ? 1 : -1;
            }
            right -= KerbExtra;
            if (side > 0) { tgt = Mathf.Max(0f, Mathf.Max(Mathf.Min(offR, right + KerbExtra), Mathf.Min(offR + YieldPref, right))); need = Mathf.Max(0f, offR - car.Off); }
            else { tgt = Mathf.Min(0f, Mathf.Min(Mathf.Max(offL, -LeftRoom), Mathf.Max(offL - YieldPref, -LeftRoom))); need = Mathf.Max(0f, car.Off - offL); }
            return side;
        }

        // Ausweichen mit bekannter Fahrlinie des Radfahrers (TrackN >= 2): alles in Weltgeometrie (PredClear), also auch mit springender Spurgeometrie, Knoten und
        // Kurven exakt. Zielversatz so weit zur radabgewandten Seite schieben (nie zurück), dass der vorhergesagte Abstand YieldClear (+ YieldPref) erreicht;
        // keep != 0 behält die Seite, solange das Rad nicht auf ihr liegt. Rückgabe: false = Abstand reicht (Ziel unverändert), tMin: Zeit bis zum engsten Punkt.
        private bool DodgeWorld(int me, Car car, float right, float horizon, int keep, out int side, out float tgt, out float tMin, out float need)
        {
            float clr = PredClear(car, horizon, out tMin, out float latR), halfW = car.Width * .5f, t0 = car.OffTarget;
            need = YieldClear - clr; side = keep; tgt = t0;
            if (need <= .02f) return false;
            if (keep == 0 || (keep > 0 ? latR > halfW * .3f : latR < -halfW * .3f))
            {
                if (latR < -halfW * .3f) side = 1;                                                // Rad links vom Auto -> nach rechts
                else if (latR > halfW * .3f) side = -1;
                else
                {
                    bool feasR = t0 + need <= right + KerbExtra + .01f && BandFree(me, car, Mathf.Min(t0 + need + YieldPref, right), true), feasL = t0 - need >= -LeftRoom - .01f && BandFree(me, car, Mathf.Max(t0 - need - YieldPref, -LeftRoom), true);
                    side = feasR || !feasL ? 1 : -1;                                              // Rad in meiner Spur: dorthin, wo Platz ist
                }
            }
            float t1;
            if (side > 0) { t1 = t0 + need; tgt = Mathf.Max(t0, Mathf.Max(Mathf.Min(t1 + YieldPref, right), Mathf.Min(t1, right + KerbExtra))); }
            else { t1 = t0 - need; tgt = Mathf.Min(t0, Mathf.Min(Mathf.Max(t1 - YieldPref, -LeftRoom), Mathf.Max(t1, -LeftRoom))); }
            return Mathf.Abs(tgt - t0) > .02f;
        }

        // Verfeinerung in Weltgeometrie für die nahe Zukunft (die Pfad-Koordinaten des Rades stimmen nicht, wenn die Spurgeometrie seitlich springt, z. B. bei
        // Änderung der Spurzahl, und auf Distanz nicht in Knoten): reicht der vorhergesagte Abstand auf der Bahn mit dem jetzigen Zielversatz nicht, wird das Ziel
        // zur Ausweichseite weitergeschoben (nie zurück). Rückgabe: neuer Zielversatz, bumped: true, wenn geändert.
        private float Bump(Car car, float right, int side, float tArr, out bool bumped)
        {
            bumped = false; float t0 = car.OffTarget;
            if (tArr > BumpTime) return t0;
            float clr = PredClear(car, Mathf.Min(BumpTime + 1f, tArr + 1f), out float tMin, out float latR);
            if (clr >= YieldClear || tMin > BumpTime + .5f) return t0;
            if (side > 0 ? latR > car.Width * .5f : latR < -car.Width * .5f) return t0;                  // Rad liegt auf der Ausweichseite: keine Verschlimmerung
            float need = YieldClear - clr, t1;
            if (side > 0) { t1 = t0 + need; t1 = Mathf.Max(Mathf.Min(t1 + YieldPref, right), Mathf.Min(t1, right + KerbExtra)); }
            else { t1 = t0 - need; t1 = Mathf.Min(Mathf.Max(t1 - YieldPref, -LeftRoom), Mathf.Max(t1, -LeftRoom)); }
            bumped = side > 0 ? t1 > t0 + .02f : t1 < t0 - .02f;
            return bumped ? t1 : t0;
        }

        // Gegenverkehr ragt (mit seinem Zielversatz) in meinen Streifen, z. B. weil es einem Rad ausweicht: ich rücke zum Fahrbahnrand (links), damit beide
        // aneinander vorbeipassen (gegenseitiges Ausweichen auf schmalen Straßen). Nur nach links, höchstens LeftRoom, und nie ins Rad hinein.
        private void OncomingYield(int me, Car car)
        {
            float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hl < 1e-4f) return;
            float hx = car.Hx / hl, hz = car.Hz / hl, halfW = car.Width * .5f, tgt = car.OffTarget; bool any = false;
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                var o = cars[j]; float ex = o.X - car.X, ez = o.Z - car.Z;
                if (ex * ex + ez * ez > 70f * 70f) continue;
                float ol = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (ol < 1e-4f) continue;
                float ohx = o.Hx / ol, ohz = o.Hz / ol, cs = ohx * hx + ohz * hz;
                if (cs > -.5f) continue;                                                                    // nur Gegenverkehr
                float along = ex * hx + ez * hz;
                if (along < -(car.Length + o.Length) * .5f - 1f || along / (car.V + o.V + 1f) > 5f) continue;     // schon vorbei / noch weit weg
                float lat = ex * hz - ez * hx + (o.OffTarget - o.Off) * cs, ext = o.Width * .5f * -cs + o.Length * .5f * Mathf.Abs(ohx * hz - ohz * hx);
                float need = lat - ext - OncomingGap - halfW;                                               // größter Versatz, bei dem noch OncomingGap Luft bleibt
                if (need < tgt) { tgt = need; any = true; }
            }
            if (!any) return;
            tgt = Mathf.Max(tgt, -OncomingLeft);
            if (rNear && Mathf.Abs(rLat - tgt) < halfW + 1.2f) return;                                       // nicht in das Rad hinein
            car.OffTarget = tgt;
        }

        // Ist der Querstreifen zwischen jetzigem und Zielversatz (sweep) bzw. um den Zielversatz (mit Karosseriebreite) neben/vor/hinter dem Auto frei von anderen Autos? Entgegenkommende
        // brauchen eine Sekunde Vorlauf. Rechnet in Weltkoordinaten im Bezugssystem des Autos.
        private bool BandFree(int me, Car car, float tgt, bool sweep)
        {
            float hl = Mathf.Sqrt(car.Hx * car.Hx + car.Hz * car.Hz); if (hl < 1e-4f) return true;
            float hx = car.Hx / hl, hz = car.Hz / hl, halfW = car.Width * .5f;
            float lo = (sweep ? Mathf.Min(car.Off, tgt) : tgt) - halfW - .25f, hi = (sweep ? Mathf.Max(car.Off, tgt) : tgt) + halfW + .25f;
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                var o = cars[j]; float ex = o.X - car.X, ez = o.Z - car.Z;
                if (ex * ex + ez * ez > 45f * 45f) continue;
                float ol = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (ol < 1e-4f) continue;
                float ohx = o.Hx / ol, ohz = o.Hz / ol, cs = Mathf.Abs(ohx * hx + ohz * hz), sn = Mathf.Abs(ohx * hz - ohz * hx);
                float along = ex * hx + ez * hz, lat = ex * hz - ez * hx + car.Off, ext = o.Width * .5f * cs + o.Length * .5f * sn;
                if (lat + ext < lo || lat - ext > hi) continue;
                float vrel = ohx * hx + ohz * hz < -.3f ? o.V + car.V : 0f;
                if (Mathf.Abs(along) < (car.Length + o.Length) * .5f + 1.5f + vrel * 1.2f) return false;
            }
            return true;
        }

        private void AbortPass(Car car) { car.Mode = 0; car.Passing = false; car.OffTarget = 0f; car.PassCool = 3f; PassAborted++; }

        // Radfahrer-Entscheidung des Autos: Querversatz (Überholen / Ausweichen) und Längsregelung (riderGap/riderLv als Hindernis)
        private void RiderDecide(int me, Car car, TrafficGraph.Lane lane, float distEnd, float vlim, float vEff, float dt)
        {
            riderGap = float.MaxValue; riderLv = 0f;
            car.PassCool = Mathf.Max(0f, car.PassCool - dt);
            if (!rNear)
            {
                if (car.Mode == 1) PassDone++;
                car.Mode = 0; car.Passing = false; car.OffTarget = 0f; car.WaitT = 0f; return;
            }
            float v = car.V, halfW = car.Width * .5f, fa = car.Length * .5f - car.Wheelbase * .5f, back = car.Length * .5f + car.Wheelbase * .5f;
            float right = Mathf.Max(0f, lane.Room - halfW - .1f);
            float gapF = rSta - fa;                         // Stoßstange -> Radmitte (> 0: Rad vor dem Auto)
            float gapB = -back - rSta;                      // Heck -> Radmitte (> 0: Rad hinter dem Auto)
            float rAl0 = rMov ? rAl : 0f;
            bool latOk = lane.Type == TrafficGraph.Road, canPass = latOk && !lane.Ring;      // im Kreisverkehr nur ausweichen, nicht überholen
            if (!latOk)
            {
                if (car.Mode == 1) AbortPass(car);
                car.Mode = 0; car.OffTarget = 0f;
            }
            else if (car.Mode == 1)
            {
                // überholen: Abstand halten, erst zurück, wenn das Heck >= ReturnGap vor dem Rad ist
                if (!rSame) AbortPass(car);
                else if (gapB >= ReturnGap) { car.Mode = 0; car.OffTarget = 0f; PassDone++; car.PassCool = 1f; }
                else
                {
                    float tgt = Mathf.Clamp(rLat + halfW + PassClear, 0f, right);
                    car.OffTarget = tgt;
                    if (gapF > AbortGap && !PassFeasible(me, car, lane, distEnd, tgt, gapF, vlim, rAl0, 1f)) AbortPass(car);
                }
            }
            else if (car.Mode == 2)
            {
                // ausweichen: bis das Rad vorbei ist
                bool done = rOpp ? gapB > 2f : rSame ? (gapF > 3f || (gapB > 12f && rAl0 - v < .1f)) : true;
                if (done) { car.Mode = 0; car.OffTarget = 0f; }
                else
                {
                    float tA = rOpp ? Mathf.Max(0f, gapF) / (v + Mathf.Abs(rAl0) + .5f) : gapB > 0f ? gapB / Mathf.Max(.5f, rAl0 - v) : 0f;
                    if (TrackN >= 2) { if (DodgeWorld(me, car, right, Mathf.Min(8f, tA + 2.5f), car.Side, out int sd, out float tg, out _, out _)) { car.Side = sd; car.OffTarget = tg; } }
                    else
                    {
                        car.Side = DodgeSide(me, car, halfW, right, car.Side, out float tg, out _);
                        car.OffTarget = tg;
                        car.OffTarget = Bump(car, right, car.Side, tA, out _);
                    }
                }
            }
            else
            {
                car.OffTarget = (gapF <= 0f && gapB <= 0f) ? car.Off : 0f;          // Rad auf gleicher Höhe: Querlage halten
                if (rSame && gapF > -.5f)
                {
                    // Rad vor dem Auto: überholen, wenn nötig (Querversatz) und sicher
                    float tgt = rLat + halfW + PassClear;
                    if (canPass && tgt > .15f && car.PassCool <= 0f && vlim > rAl0 + 1.2f)
                    {
                        float vrel = Mathf.Max(0f, v - rAl0);
                        float gs = Mathf.Max(1.5f * v + 14f, vrel * (Mathf.Max(0f, tgt - car.Off) / (.8f * LatRate(v)) + 3f) + 10f);
                        float t2 = Mathf.Min(tgt, right);
                        if (gapF < gs && t2 - halfW - rLat >= PassMin - .01f)
                        {
                            car.EvalT -= dt;
                            if (car.EvalT <= 0f)
                            {
                                car.EvalT = .2f;                                                // Prüfung höchstens 5 x pro Sekunde
                                if (PassFeasible(me, car, lane, distEnd, t2, gapF, vlim, rAl0, 2f)) { car.Mode = 1; car.OffTarget = t2; PassStarted++; }
                            }
                        }
                    }
                }
                else if (rSame && rMov && gapB > 0f)
                {
                    // schnelleres Rad kommt von hinten: ausweichen, rechtzeitig vor dem Eintreffen
                    float closing = rAl0 - v;
                    if (closing > .4f)
                    {
                        float tArr = gapB / closing;
                        if (TrackN >= 2)
                        {
                            if (tArr < 9f && DodgeWorld(me, car, right, tArr + 2.5f, 0, out int sd, out float tg, out float tMin, out float nd) && tMin < nd / (.8f * LatRate(v)) + 4.2f) { car.Mode = 2; car.Side = sd; car.OffTarget = tg; }
                        }
                        else
                        {
                            int side = DodgeSide(me, car, halfW, right, 0, out float tgt, out float need);
                            float tl = need / (.8f * LatRate(v)) + 1.2f;
                            if (need > .05f && tArr < tl + 3f) { car.Mode = 2; car.Side = side; car.OffTarget = tgt; }
                            else { float nt = Bump(car, right, side, tArr, out bool bumped); if (bumped) { car.Mode = 2; car.Side = side; car.OffTarget = nt; } }       // Weltgeometrie, kurz vor dem Eintreffen
                        }
                    }
                }
                else if (rOpp && gapF > -back)
                {
                    // Rad kommt entgegen (Gegenrichtung auf meiner Spur): ausweichen
                    float ttc = Mathf.Max(0f, gapF) / (v + Mathf.Abs(rAl0) + .5f);
                    if (TrackN >= 2)
                    {
                        if (ttc < 9f && DodgeWorld(me, car, right, ttc + 2.5f, 0, out int sd, out float tg, out float tMin, out float nd) && tMin < nd / (.8f * LatRate(v)) + 3.7f) { car.Mode = 2; car.Side = sd; car.OffTarget = tg; }
                    }
                    else
                    {
                        int side = DodgeSide(me, car, halfW, right, 0, out float tgt, out float need);
                        float tl = need / (.8f * LatRate(v)) + 1.2f;
                        if (need > .05f && ttc < tl + 2.5f) { car.Mode = 2; car.Side = side; car.OffTarget = tgt; }
                        else { float nt = Bump(car, right, side, ttc, out bool bumped); if (bumped) { car.Mode = 2; car.Side = side; car.OffTarget = nt; } }
                    }
                }
            }
            car.Passing = car.Mode == 1;
            car.WaitT = rSame && gapF > -.3f && gapF < 40f && v < 1.5f && car.Mode == 0 ? car.WaitT + dt : Mathf.Max(0f, car.WaitT - 2f * dt);      // wartet hinter dem Rad

            // ---- Längsregelung: Radfahrer als Hindernis (mit dem Querversatz, den das Auto am Radfahrer haben wird)
            if (rSame)
            {
                if (gapF > -.3f)
                {
                    float offAt = OffsetAt(car, Mathf.Max(0f, rSta));
                    if (Mathf.Abs(rLat - offAt) - halfW < HoldClear) { riderGap = Mathf.Max(.05f, gapF); riderLv = Mathf.Max(0f, rAl0); }
                }
            }
            else if (rOpp)
            {
                if (gapF > -.3f)
                {
                    float offAt = OffsetAt(car, Mathf.Max(0f, rSta));
                    if (Mathf.Abs(rLat - offAt) - halfW < 1f) { riderGap = Mathf.Max(.05f, gapF - 1.5f * Mathf.Abs(rAl0)); riderLv = 0f; }
                }
            }
            else
            {
                // quer laufend: Zeit-Raum-Test (Radfahrer geradeaus mit seiner Geschwindigkeit)
                float rr = halfW + fa + 1.6f, rr2 = rr * rr;
                for (int q = 0; q < probeCount; q += 2)
                {
                    float d = q * ProbeStep, t = d / Mathf.Max(v, 2.5f); if (t > 6f) break;
                    float off = q < probe0 ? OffsetAt(car, d) : 0f; int k = NB + q;
                    RiderAt(t, out float rx, out float rz);
                    float ex = cx[k] + chz[k] * off - rx, ez = cz[k] - chx[k] * off - rz;
                    if (ex * ex + ez * ez < rr2) { riderGap = Mathf.Max(.05f, d - fa - .5f); riderLv = 0f; break; }
                }
            }
        }

        // Kann das Auto den Radfahrer sicher überholen? Zeit/Weg bis das Heck ReturnGap vor dem Rad ist (Auto beschleunigt auf Wunschtempo), dann
        // genug Strecke bis zur Kreuzung, genug Sicht in die Gegenrichtung und keine Autos im überstrichenen Streifen. tM = Zeitreserve zum Gegenverkehr.
        private float pfT, pfD; private int pfWhy; public int DebugCar = -1; public static bool NoRemove;      // Diagnose (Tests): Grund der letzten Ablehnung 1 Tempo, 2 Zeit, 3 Kreuzung, 4 Sicht, 5 Korridor
        private bool PassFeasible(int me, Car car, TrafficGraph.Lane lane, float distEnd, float tgt, float gapF, float vdes, float vr, float tM)
        {
            float aM = 1.6f * car.AccelFactor * PassBoost, L = car.Length, fa = L * .5f - car.Wheelbase * .5f;
            pfWhy = 0;
            if (vdes < vr + 1.2f) { pfWhy = 1; return false; }
            float delta = gapF + L + ReturnGap;
            float vc = car.V, t = 0f, dCar = 0f, rel = 0f;
            while (rel < delta)
            {
                if (t > 18f) { pfWhy = 2; return false; }
                vc = Mathf.Min(vdes, vc + aM * .25f); dCar += vc * .25f; rel += (vc - vr) * .25f; t += .25f;
            }
            float tTot = t + ReturnTime, dTot = dCar + vc * ReturnTime;
            if (dTot + fa + JunctionBuf > distEnd) { pfWhy = 3; return false; }                  // nicht in/vor eine Kreuzung hinein überholen
            float vAvg = dTot / tTot;
            float sight = SightDist(car, out float capMin);
            float vOnc = Mathf.Clamp(capMin, 8f, Mathf.Max(8f, lane.Speed));                       // unsichtbarer Gegenverkehr fährt höchstens das Kurventempo der einsehbaren Strecke
            if (Log != null && me == DebugCar) Log($"DBGP t={time:0.0} car {me} sight {sight:0} need {(vAvg + vOnc) * tTot + 15f:0} vOnc {vOnc:0.0} tTot {tTot:0.0} dTot {dTot:0}");
            float patience = Mathf.Lerp(1f, PatienceMin, Mathf.Clamp01((car.WaitT - 8f) / 30f));                    // wer lange hinter dem Rad wartet, nimmt weniger Sichtreserve (der Gegenverkehr bremst ebenfalls)
            if (sight < ((vAvg + vOnc) * tTot + 15f) * patience) { pfWhy = 4; return false; }                                   // Kurve/Kuppe: Gegenverkehr nicht rechtzeitig sichtbar
            bool free = CorridorFree(me, car, tgt, vAvg, tTot, t, dCar, tM);
            if (!free) pfWhy = 5;
            return free;
        }

        // Sichtweite voraus: Bogenlänge, bis der Pfad mehr als SightSag von der Sehne Auto -> Punkt abweicht (Kurve verdeckt die Gegenspur);
        // capMin = kleinstes Kurventempo auf der einsehbaren Strecke
        private float SightDist(Car car, out float capMin)
        {
            int n = 1; sgx[0] = cx[NB]; sgz[0] = cz[NB]; sgs[0] = 0f; sgc[0] = 60f; float baseDist = 0f;
            for (int st = 0; st < pathN && n < sgx.Length; st++)
            {
                var l = g.Lanes[pathLane[st]]; float from = st == 0 ? car.S : 0f;
                for (int i = 0; i < l.N && n < sgx.Length; i++)
                {
                    if (l.S[i] <= from + .01f) continue;
                    float d = baseDist + l.S[i] - from; if (d > 320f) break;
                    sgx[n] = l.X[i]; sgz[n] = l.Z[i]; sgs[n] = d; sgc[n] = l.Cap[i]; n++;
                }
                baseDist += l.Length - from;
            }
            int end = n - 1;
            for (int k = 2; k < n && end == n - 1; k++)
            {
                float dx = sgx[k] - sgx[0], dz = sgz[k] - sgz[0], len = Mathf.Sqrt(dx * dx + dz * dz); if (len < 1f) continue;
                for (int i = 1; i < k; i++)
                    if (Mathf.Abs((sgx[i] - sgx[0]) * dz - (sgz[i] - sgz[0]) * dx) / len > SightSag) { end = k - 1; break; }
            }
            capMin = 60f; for (int i = 1; i <= end; i++) if (sgc[i] < capMin) capMin = sgc[i];
            return sgs[end];
        }

        // Ist der beim Überholen überstrichene Streifen frei? Gegenverkehr (Zeit bis zur Begegnung), Autos vor dem Rad (Platz zum Einscheren), Autos daneben
        private bool CorridorFree(int me, Car car, float tgt, float vAvg, float tTot, float tPass, float dPass, float tM)
        {
            float halfW = car.Width * .5f, fa = car.Length * .5f - car.Wheelbase * .5f, back = car.Length * .5f + car.Wheelbase * .5f;
            float bandL = Mathf.Min(0f, tgt) - halfW - .4f, bandR = Mathf.Max(0f, tgt) + halfW + .4f;
            for (int j = 0; j < cars.Length; j++)
            {
                if (j == me || !cars[j].Active) continue;
                var o = cars[j];
                if (Dist2(car.X, car.Z, o.X, o.Z) > 300f * 300f) continue;
                if (!ProjectOnPath(o.X, o.Z, out float sj, out float latj, out float hx, out float hz)) continue;
                float oh = Mathf.Sqrt(o.Hx * o.Hx + o.Hz * o.Hz); if (oh < 1e-4f) continue;
                float cosA = (o.Hx * hx + o.Hz * hz) / oh;
                float oHalf = o.Width * .5f, oL = o.Length;
                if (latj + oHalf < bandL || latj - oHalf > bandR) continue;
                if (cosA < -.3f)
                {
                    float dMeet = sj - oL * .5f - fa;                              // Bug -> Bug
                    if (dMeet < -(oL + back)) continue;                            // schon vorbei
                    if (dMeet < 3f) return false;
                    if (dMeet / (vAvg + o.V + .5f) < tTot + tM) return false;
                }
                else if (cosA > .3f)
                {
                    float ov = o.V * cosA;
                    bool inLane = Mathf.Abs(latj - car.Off) < halfW + oHalf + .3f;
                    if (sj > rSta + .3f)
                    {
                        if (sj - oL * .5f + ov * tPass - (fa + dPass) < 8f) return false;            // nach dem Überholen kein Platz hinter dem Vordermann
                    }
                    else if (inLane) { if (sj > fa + 1f) return false; }                            // Auto zwischen mir und dem Rad
                    else if (sj - oL * .5f < fa + 3f && sj + oL * .5f > -back - 25f) return false;   // Auto daneben / dicht dahinter im Streifen
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
            if (Log != null) Log($"DBG t={time:0.00} grant car {me} lane {car.Lane} conn {car.Res[idx]} v={car.V:0.0} rider d={Mathf.Sqrt(Dist2(car.X, car.Z, RiderX, RiderZ)):0.0} yaw={riderYaw:0.00} rs={Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ):0.0}");
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
                sb.Append($"      entry {k} conn {conn.Id} hub {conn.Hub} exit {exit.Id} (len {exit.Length:0.0}) granted={car.ResGranted[k]} room={RoomFrom(exit.Id, me, 3, 4f, 6f):0.0}/{car.Length + 3f:0.0} riderNear={(RiderSet && RiderConflictsConn(car, conn, exit))} riderAppr={(RiderSet && RiderApproachingExit(conn, exit))} pathOcc={PathOccupied(me, car, conn)}\n");
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
            return !(RiderSet && (RiderConflictsConn(car, conn, exit) || RiderApproachingExit(conn, exit)));
        }

        // Kann 'car' die Verbindung jetzt physisch befahren? (Kastenregel: Platz ab Anfang der Exit-Spur, Radfahrer, fremde Karosserien)
        private bool PhysFree(int me, Car car, TrafficGraph.Lane conn)
        {
            var exit = g.Lanes[conn.Next[0]];
            float need = car.Length + 3f;
            if (RoomFrom(exit.Id, me, 3, 4f, 6f) < need) return false;
            if (RiderSet && (RiderConflictsConn(car, conn, exit) || RiderApproachingExit(conn, exit))) return false;
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
                        for (int q = 0; q < 3; q++) if (Dist2(mx, mz, cxs[j * 6 + q], czs[j * 6 + q]) < R2) return true;
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

        // Radfahrer kommt auf die Exit-Spur zu (vorhergesagte Bahn in den nächsten ~4 s nahe dem Anfang der Exit-Spur, gleiche Richtung): das Auto soll sich nicht davor setzen
        private bool RiderApproachingExit(TrafficGraph.Lane conn, TrafficGraph.Lane exit)
        {
            float rs2 = RiderVX * RiderVX + RiderVZ * RiderVZ; if (rs2 < .25f) return false;
            float rs = Mathf.Sqrt(rs2);
            int h = 0; exit.Eval(0f, ref h, out float x, out _, out float z, out float hx, out float hz);
            if (Dist2(x, z, RiderX, RiderZ) > (14f + 5f * rs) * (14f + 5f * rs)) return false;
            for (float t = 0f; t <= 4f; t += .5f)
            {
                RiderAt(t, out float rx, out float rz);
                if (Dist2(x, z, rx, rz) < 4f * 4f) { float same = (RiderVX * hx + RiderVZ * hz) / rs; if (same > .5f) return true; }
            }
            return false;
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

        // Kreuzungsfreigabe gegen den Radfahrer (nicht anhaltbar): Er wird entlang seiner vorhergesagten Bahn (Kreisbogen mit seiner Gierrate) mit allen Punkten
        // des Verbinders (und der ersten Meter der Ausfahrt) verglichen, solange das Auto die Kreuzung noch belegt (konservativ: 1,5 x Anfahrzeit + 1 s).
        private bool RiderConflictsConn(Car car, TrafficGraph.Lane conn, TrafficGraph.Lane exit)
        {
            float v = car.V, d0 = Mathf.Max(0f, g.Lanes[car.Lane].Length - car.S);
            for (int k = 0; k < car.ResN && car.Res[k] != conn.Id; k++)                       // Kettenreservierung: Weg bis zu diesem Verbinder
                if (car.ResState[k] == 0) { var ck = g.Lanes[car.Res[k]]; d0 += ck.Length + g.Lanes[ck.Next[0]].Length; }
            float rho = car.Width * .5f + 1.6f, total = conn.Length + Mathf.Min(12f, exit.Length);
            float tClear = 1.5f * TimeTo(v, d0 + total + car.Length + .5f) + 1f;
            float rs = Mathf.Sqrt(RiderVX * RiderVX + RiderVZ * RiderVZ);
            if (rs <= .6f) tClear = 0f;
            for (float t = 0f; t <= tClear + .001f; t += .25f)
            {
                RiderAt(t, out float rx, out float rz);
                int h = 0, h2 = 0;
                for (float s = 0f; s <= total + .01f; s += 2f)
                {
                    float x, z;
                    if (s <= conn.Length) conn.Eval(s, ref h, out x, out _, out z, out _, out _); else exit.Eval(s - conn.Length, ref h2, out x, out _, out z, out _, out _);
                    float dx = x - rx, dz = z - rz;
                    if (dx * dx + dz * dz < (t == 0f ? 3.8f * 3.8f : rho * rho)) return true;
                }
            }
            return false;
        }

        // Zeit, um aus der Geschwindigkeit v die Strecke d zurückzulegen (anfahren mit 1,4 m/s²)
        private static float TimeTo(float v, float d) { return (-v + Mathf.Sqrt(v * v + 2f * 1.4f * Mathf.Max(0f, d))) / 1.4f; }

        // ------------------------------------------------------------------ Bewegung
        private void Integrate(Car car, float a, float dt)
        {
            float v = car.V;
            float vNew = Mathf.Max(0f, v + a * dt);
            float ds = (v + vNew) * .5f * dt;
            car.V = vNew; car.S += ds; car.Odo += ds;
            // seitlicher Versatz: Querbeschleunigung begrenzt (weicher Anfang/Ende), auf Verbindern wie bisher zügig zurück zur Spurmitte
            float d = car.OffTarget - car.Off;
            if (g.Lanes[car.Lane].Type != TrafficGraph.Road)
            {
                car.OffTarget = 0f; car.OffV = 0f; car.Mode = 0; car.Passing = false;
                float rate = Mathf.Max(LatRate(car.V) * dt, 1.5f * dt);
                car.Off += Mathf.Clamp(-car.Off, -rate, rate);
            }
            else
            {
                float vd = Mathf.Sign(d) * Mathf.Min(LatRate(car.V), Mathf.Sqrt(2f * LateralAcc * Mathf.Abs(d)));
                float mx = LateralAcc * dt;
                car.OffV += Mathf.Clamp(vd - car.OffV, -mx, mx);
                float step = car.OffV * dt;
                if (Mathf.Abs(d) < Mathf.Abs(step) + .002f && Mathf.Abs(car.OffV) < .15f) { car.Off = car.OffTarget; car.OffV = 0f; } else car.Off += step;
            }
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
            car.Active = false; car.ResN = 0; car.Passing = false; car.Mode = 0; car.OffV = 0f; car.PassCool = 0f; car.WaitT = 0f; car.NextConn = -1; car.V = 0f;
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
                car.S = s; car.Off = car.OffTarget = car.OffV = 0f; car.Mode = 0; car.PassCool = 0f; car.WaitT = 0f; car.ResN = 0; car.PhysBlocked = false; car.Passing = false; car.Odo = 0f; car.StoppedFor = 0f; car.Age = 0f;
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
