using UnityEngine;
using UnityEngine.InputSystem;

namespace StoryCycling
{
    public enum TrainerMode { Simulation, VirtualGears, Erg }

    // Trainer-Widerstand über FTMS (CapeCrownDevices.SendSimulation / SendTargetPower) und Fahrphysik für das Tempo im Spiel.
    //  Simulation:      Steigung der Strecke (× Steigungsgefühl) geht an den Trainer, geschaltet wird an der echten Kassette.
    //  Virtuelle Gänge: zusätzlich ein Gang-Versatz auf die Steigung (24 Gänge, Gang 12 = neutral) — schalten per Bildschirm/Tastatur.
    //  ERG:             feste Zielleistung, der Trainer regelt den Widerstand selbst.
    // In allen Modi kommt das Tempo aus der gemessenen Leistung (Fahrphysik), nicht aus der Rollen-Geschwindigkeit — wie bei Zwift.
    public sealed class TrainerControl : MonoBehaviour
    {
        public const int Gears = 24, NeutralGear = 12;
        [Header("Fahrer & Rad (Fahrphysik)")]
        public float riderKg = 75f, bikeKg = 9f;
        [Tooltip("Luftwiderstandsfläche m² (Oberlenker ~0,32, Unterlenker ~0,28)")] public float cda = .32f;
        [Tooltip("Rollwiderstand (Straße ~0,004)")] public float crr = .004f;
        [Header("Virtuelle Gänge")]
        [Tooltip("Steigungs-Versatz je Gang in %")] public float gearStep = .8f;

        public TrainerMode Mode { get; private set; }
        public int Gear { get; private set; } = NeutralGear;
        public int ErgWatts { get; private set; } = 180;
        public float Difficulty { get; private set; } = 1f;       // Steigungsgefühl: 0,5 / 0,75 / 1
        public float TrainerGrade { get; private set; }             // zuletzt an den Trainer gesendete Steigung in %
        public bool Active { get; private set; }

        private CapeCrownDevices devices;
        private IRideSession session;
        private float sentGrade = float.NaN, sendT, keepT; private int sentErg = -1; private TrainerMode sentMode; private bool sentActive;
        private const float AirDensity = 1.225f, G = 9.81f;

        public float Mass => riderKg + bikeKg;
        public float Cw => .5f * AirDensity * cda;

        private void Awake()
        {
            Mode = (TrainerMode)Mathf.Clamp(PlayerPrefs.GetInt("trainer.mode", 0), 0, 2);
            ErgWatts = Mathf.Clamp(PlayerPrefs.GetInt("trainer.erg", 180), 50, 1000);
            Difficulty = Mathf.Clamp(PlayerPrefs.GetFloat("trainer.difficulty", 1f), .25f, 1f);
            riderKg = PlayerPrefs.GetFloat("rider.kg", riderKg);
        }
        private void Start()
        {
            devices = FindAnyObjectByType<CapeCrownDevices>(); session = GetComponent<IRideSession>();
            if (devices != null) devices.Shift += OnClick;
        }
        private void OnDestroy() { if (devices != null) devices.Shift -= OnClick; }

        // Zwift Click: ERG ±10 W, sonst schalten (im SIM-Modus wechselt der erste Klick auf virtuelle Gänge)
        private void OnClick(bool up)
        {
            if (Mode == TrainerMode.Erg) { AddErg(up ? 10 : -10); return; }
            if (Mode == TrainerMode.Simulation) SetMode(TrainerMode.VirtualGears);
            if (up) ShiftUp(); else ShiftDown();
        }

        public void SetMode(TrainerMode m) { Mode = m; PlayerPrefs.SetInt("trainer.mode", (int)m); sendT = 0f; }
        public void ShiftUp() { Gear = Mathf.Min(Gears, Gear + 1); sendT = 0f; }
        public void ShiftDown() { Gear = Mathf.Max(1, Gear - 1); sendT = 0f; }
        public void AddErg(int delta) { ErgWatts = Mathf.Clamp(ErgWatts + delta, 50, 1000); PlayerPrefs.SetInt("trainer.erg", ErgWatts); }
        public void CycleDifficulty() { Difficulty = Difficulty >= .99f ? .5f : Difficulty < .6f ? .75f : 1f; PlayerPrefs.SetFloat("trainer.difficulty", Difficulty); sendT = 0f; }

        // Steigung, die der Trainer im aktuellen Modus bekommt (in %), aus der Streckensteigung (Anteil, 0.05 = 5 %)
        public float GradeFor(float routeGrade)
        {
            float g = routeGrade * 100f * Difficulty;
            if (Mode == TrainerMode.VirtualGears) g += (Gear - NeutralGear) * gearStep;
            return Mathf.Clamp(g, -10f, 20f);                                   // Bereich, den gängige Rollentrainer umsetzen
        }

        private void Update()
        {
            HandleKeys();
            if (devices == null || session == null) return;
            Active = session.Started && !session.IsPaused;
            if (!devices.TrainerConnected || devices.IsTestFeed) { TrainerGrade = Active ? GradeFor(session.CurrentGrade) : 0f; sentGrade = float.NaN; sentErg = -1; return; }
            sendT -= Time.unscaledDeltaTime; keepT -= Time.unscaledDeltaTime;
            bool changed = Mode != sentMode || Active != sentActive;
            if (Active && Mode == TrainerMode.Erg)
            {
                // ERG: bei Änderung sofort, sonst alle 5 s auffrischen (falls der Trainer einen Befehl verloren hat)
                if (changed || ErgWatts != sentErg || keepT <= 0f) { devices.SendTargetPower(ErgWatts); sentErg = ErgWatts; sentGrade = float.NaN; keepT = 5f; }
                TrainerGrade = 0f;
            }
            else
            {
                // Simulation / Gänge: Steigung höchstens 2× pro Sekunde, nur bei Änderung >= 0,1 %, sonst alle 5 s auffrischen. Pause/Menü: flach.
                float g = Active ? GradeFor(session.CurrentGrade) : 0f;
                g = Mathf.Round(g * 10f) / 10f;
                if (changed || (sendT <= 0f && (float.IsNaN(sentGrade) || Mathf.Abs(g - sentGrade) >= .1f)) || keepT <= 0f)
                {
                    devices.SendSimulation(g, crr, Cw); sentGrade = g; sentErg = -1; sendT = .5f; keepT = 5f;
                }
                TrainerGrade = g;
            }
            sentMode = Mode; sentActive = Active;
        }

        // Tastatur (Editor / iPad mit Tastatur): Pfeil hoch/runter oder +/- = schalten bzw. ERG ±10 W, M = Modus wechseln
        private void HandleKeys()
        {
            var k = Keyboard.current; if (k == null) return;
            bool up = k.upArrowKey.wasPressedThisFrame || k.equalsKey.wasPressedThisFrame || k.numpadPlusKey.wasPressedThisFrame;
            bool down = k.downArrowKey.wasPressedThisFrame || k.minusKey.wasPressedThisFrame || k.numpadMinusKey.wasPressedThisFrame;
            if (Mode == TrainerMode.Erg) { if (up) AddErg(10); if (down) AddErg(-10); }
            else if (Mode == TrainerMode.VirtualGears) { if (up) ShiftUp(); if (down) ShiftDown(); }
            if (k.mKey.wasPressedThisFrame) SetMode((TrainerMode)(((int)Mode + 1) % 3));
        }

        // Fahrphysik: neue Geschwindigkeit (m/s) aus Leistung (W) und Steigung (Anteil) — Antrieb, Hangabtrieb, Rollwiderstand, Luftwiderstand.
        // Bergab ohne Treten rollt das Rad weiter (Schwerkraft), rückwärts nie.
        public float StepSpeed(float v, float watts, float grade, float dt)
        {
            dt = Mathf.Min(dt, .1f);
            float m = Mass + 1.2f;                                               // + Trägheit der Laufräder
            float th = Mathf.Atan(grade), drive = Mathf.Max(0f, watts) / Mathf.Max(v, 1.5f);
            float resist = Mass * G * (Mathf.Sin(th) + crr * Mathf.Cos(th)) + .5f * AirDensity * cda * v * v;
            v += (drive - resist) / m * dt;
            return Mathf.Clamp(v, 0f, 30f);
        }
    }
}
