using System;
using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    // Radcomputer im Stil eines Lenker-Computers (eigene Gestaltung, kein Hersteller-Logo): LED-Leiste nach Leistungszone, Strecke/Runde/Status,
    // Leistung (3-s-Mittel), Tempo, Puls und das Höhenprofil des nächsten Kilometers. Liest die Werte aus CapeCrownDevices (Trainer/Brustgurt/Demo)
    // und die Strecke aus der Fahrt. Gebaut in Entwurfsmaßen, angezeigt mit 'size' skaliert (im Inspector auch während der Fahrt einstellbar).
    public sealed class BikeComputerView : MonoBehaviour
    {
        [Tooltip("FTP für die LED-Zonen (W)")] public float ftp = 250f;
        [Tooltip("Anzeigegröße (1 = Entwurfsgröße 300 x 530)")] [Range(.35f, 1f)] public float size = .5f;
        public const float ProfileMetres = 1000f;
        private const int Segments = 64;

        private Func<IRideSession> session;
        private CapeCrownDevices devices;
        private Text power, speed, heart, grade, climb, clock, trainerTag, heartTag, trip, status;
        private Image[] leds;
        private ElevationProfileGraphic profile;
        private readonly float[] heights = new float[Segments + 1];
        private readonly float[] pw = new float[30]; private int pwN, pwI; private float pwT;      // 3-s-Mittel aus 10-Hz-Proben
        private float profileT;

        private static readonly Color BodyCol = new Color(.075f, .078f, .085f), ScreenCol = new Color(.93f, .935f, .915f), Ink = new Color(.07f, .07f, .08f),
            Muted = new Color(.38f, .40f, .40f), LedOff = new Color(.17f, .18f, .19f), RuleCol = new Color(.72f, .73f, .71f), Ok = new Color(.20f, .62f, .32f);
        private static Sprite rounded;

        public static BikeComputerView Create(Transform parent, Func<IRideSession> session, CapeCrownDevices devices)
        {
            // Gehäuse unten rechts (Referenzauflösung 1440 x 900), hochkant wie ein Lenker-Computer; Pivot unten rechts -> skaliert in die Ecke
            var body = Box("Bike computer", parent, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 24), new Vector2(300, 530), BodyCol, 26f);
            var v = body.gameObject.AddComponent<BikeComputerView>();
            v.session = session; v.devices = devices;
            body.localScale = Vector3.one * v.size;
            v.leds = new Image[7];
            for (int i = 0; i < 7; i++) v.leds[i] = Box("LED " + i, body, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2((i - 3) * 34f, -12f), new Vector2(26, 7), LedOff, 3.5f).GetComponent<Image>();
            for (int i = 0; i < 3; i++) Box("Taste " + i, body, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2((i - 1) * 74f, 12f), new Vector2(46, 9), new Color(.20f, .21f, .22f), 4.5f);

            var scr = Box("Display", body, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -30), new Vector2(272, 458), ScreenCol, 9f);
            // Statuszeile
            v.clock = Label(scr, "", 18, FontStyle.Bold, new Vector2(12, -7), new Vector2(80, 22), Ink, TextAnchor.MiddleLeft);
            v.trainerTag = Label(scr, "TRAINER", 15, FontStyle.Bold, new Vector2(112, -7), new Vector2(86, 22), Muted, TextAnchor.MiddleRight);
            v.heartTag = Label(scr, "HR", 15, FontStyle.Bold, new Vector2(204, -7), new Vector2(56, 22), Muted, TextAnchor.MiddleRight);
            Rule(scr, -34);
            // Strecke / Runde und Datenstatus (vorher Panel oben links)
            v.trip = Label(scr, "", 22, FontStyle.Bold, new Vector2(12, -38), new Vector2(248, 28), Ink, TextAnchor.MiddleLeft);
            v.status = Label(scr, "", 15, FontStyle.Bold, new Vector2(12, -66), new Vector2(248, 20), Muted, TextAnchor.MiddleLeft);
            Rule(scr, -90);
            // Leistung
            Label(scr, "LEISTUNG 3S  W", 15, FontStyle.Bold, new Vector2(12, -96), new Vector2(248, 20), Muted, TextAnchor.UpperLeft);
            v.power = Label(scr, "—", 80, FontStyle.Bold, new Vector2(8, -112), new Vector2(256, 90), Ink, TextAnchor.MiddleCenter);
            Rule(scr, -206);
            // Tempo | Puls
            Label(scr, "TEMPO  KM/H", 15, FontStyle.Bold, new Vector2(12, -212), new Vector2(120, 20), Muted, TextAnchor.UpperLeft);
            v.speed = Label(scr, "—", 48, FontStyle.Bold, new Vector2(6, -230), new Vector2(124, 62), Ink, TextAnchor.MiddleCenter);
            Label(scr, "PULS  BPM", 15, FontStyle.Bold, new Vector2(148, -212), new Vector2(116, 20), Muted, TextAnchor.UpperLeft);
            v.heart = Label(scr, "—", 48, FontStyle.Bold, new Vector2(142, -230), new Vector2(124, 62), Ink, TextAnchor.MiddleCenter);
            Box("Trenner", scr, new Vector2(0, 1), new Vector2(0, 1), new Vector2(135.5f, -212), new Vector2(1, 84), RuleCol, 0f);
            Rule(scr, -300);
            // Höhenprofil nächster Kilometer
            Label(scr, "NÄCHSTER KM", 15, FontStyle.Bold, new Vector2(12, -306), new Vector2(140, 20), Muted, TextAnchor.UpperLeft);
            v.grade = Label(scr, "", 24, FontStyle.Bold, new Vector2(132, -303), new Vector2(128, 28), Ink, TextAnchor.UpperRight);
            var g = new GameObject("Hoehenprofil", typeof(RectTransform)).GetComponent<RectTransform>();
            g.SetParent(scr, false); g.anchorMin = g.anchorMax = g.pivot = new Vector2(0, 1); g.anchoredPosition = new Vector2(14, -336); g.sizeDelta = new Vector2(246, 92);
            v.profile = g.gameObject.AddComponent<ElevationProfileGraphic>(); v.profile.Build(Segments);
            Label(scr, "0", 14, FontStyle.Bold, new Vector2(12, -434), new Vector2(40, 18), Muted, TextAnchor.UpperLeft);
            Label(scr, "1 km", 14, FontStyle.Bold, new Vector2(196, -434), new Vector2(64, 18), Muted, TextAnchor.UpperRight);
            v.climb = Label(scr, "", 16, FontStyle.Bold, new Vector2(60, -433), new Vector2(150, 20), Ink, TextAnchor.UpperCenter);
            return v;
        }

        private void Update()
        {
            if (!Mathf.Approximately(transform.localScale.x, size)) transform.localScale = Vector3.one * size;
            var s = session != null ? session() : null;
            float now = Time.unscaledTime;
            clock.text = DateTime.Now.ToString("HH:mm");
            bool hasDev = devices != null;
            trainerTag.color = hasDev && devices.TrainerConnected ? Ok : Muted;
            heartTag.color = hasDev && devices.HeartConnected ? Ok : Muted;
            status.text = !hasDev ? "" : devices.IsTestFeed ? "DEMO · KEIN FORTSCHRITT" : !devices.FreshSpeed ? "Warte auf Trainerdaten"
                        : !devices.FreshHeart ? "Brustgurt optional · kein Puls" : "TRAINER + PULS · LIVE";

            // Leistung: 3-s-Mittel (10 Hz), wie am Lenker-Computer üblich — ruhiger als der Rohwert
            bool fp = hasDev && devices.FreshPower;
            if (now - pwT >= .1f) { pwT = now; if (fp) { pw[pwI] = devices.Watts; pwI = (pwI + 1) % pw.Length; pwN = Mathf.Min(pwN + 1, pw.Length); } else pwN = 0; }
            float avg = 0f; for (int i = 0; i < pwN; i++) avg += pw[i]; if (pwN > 0) avg /= pwN;
            power.text = fp && pwN > 0 ? Mathf.RoundToInt(avg).ToString() : "—";
            speed.text = s != null && s.Started ? s.SpeedKph.ToString("0.0") : hasDev && devices.FreshSpeed ? devices.Speed.ToString("0.0") : "—";   // Spieltempo (Fahrphysik)
            heart.text = hasDev && devices.FreshHeart ? Mathf.RoundToInt(devices.Heart).ToString() : "—";

            // LED-Leiste: Leistungszone (Coggan, bezogen auf FTP)
            int zone = 0; Color zc = LedOff;
            if (fp && pwN > 0 && ftp > 0f)
            {
                float r = avg / ftp;
                zone = r < .55f ? 1 : r < .75f ? 2 : r < .90f ? 3 : r < 1.05f ? 4 : r < 1.20f ? 5 : r < 1.50f ? 6 : 7;
                zc = zone <= 1 ? new Color(.55f, .60f, .66f) : zone == 2 ? new Color(.20f, .55f, .95f) : zone == 3 ? new Color(.25f, .80f, .35f)
                   : zone == 4 ? new Color(.98f, .82f, .20f) : zone == 5 ? new Color(.98f, .55f, .15f) : new Color(.92f, .20f, .18f);
            }
            for (int i = 0; i < leds.Length; i++) leds[i].color = i < zone ? zc : LedOff;

            if (s == null) return;
            trip.text = $"{s.TotalMetres / 1000f:0.00} km  ·  Runde {s.CompletedLaps + 1}";
            float gr = s.CurrentGrade;
            grade.text = (gr * 100f).ToString("+0.0;-0.0;0.0") + " %";
            grade.color = gr >= .03f ? ElevationProfileGraphic.GradeColor(gr) * .85f + Ink * .15f : Ink;
            if (now - profileT >= .25f)
            {
                profileT = now;
                float step = ProfileMetres / Segments, up = 0f;
                for (int i = 0; i <= Segments; i++) { heights[i] = s.HeightAhead(i * step); if (i > 0 && heights[i] > heights[i - 1]) up += heights[i] - heights[i - 1]; }
                profile.SetProfile(heights, step);
                climb.text = "+" + Mathf.RoundToInt(up) + " Hm";
            }
        }

        // ---------------------------------------------------------------- UI-Bausteine
        private static RectTransform Box(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color color, float radius)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = anchor; r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
            var img = r.gameObject.AddComponent<Image>(); img.color = color; img.raycastTarget = false;
            if (radius > 0f) { img.sprite = Rounded(); img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = RoundedRadius / radius; }
            return r;
        }
        private static void Rule(RectTransform parent, float y) => Box("Linie", parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, y), new Vector2(252, 1.5f), RuleCol, 0f);
        private static Text Label(RectTransform parent, string value, int size, FontStyle style, Vector2 pos, Vector2 area, Color color, TextAnchor align)
        {
            var r = new GameObject(value.Length > 0 ? value : "Wert", typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = pos; r.sizeDelta = area;
            var t = r.gameObject.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); t.fontSize = size; t.fontStyle = style; t.text = value; t.color = color;
            t.alignment = align; t.raycastTarget = false; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        // Abgerundetes Rechteck als 9-Slice-Sprite (zur Laufzeit erzeugt, keine Asset-Abhängigkeit). Radius über pixelsPerUnitMultiplier.
        private const int RoundedSize = 64; private const float RoundedRadius = 24f;
        private static Sprite Rounded()
        {
            if (rounded != null) return rounded;
            var tex = new Texture2D(RoundedSize, RoundedSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "RoundedRect" };
            var px = new Color32[RoundedSize * RoundedSize];
            for (int y = 0; y < RoundedSize; y++)
            for (int x = 0; x < RoundedSize; x++)
            {
                float cx = Mathf.Clamp(x + .5f, RoundedRadius, RoundedSize - RoundedRadius), cy = Mathf.Clamp(y + .5f, RoundedRadius, RoundedSize - RoundedRadius);
                float d = Mathf.Sqrt((x + .5f - cx) * (x + .5f - cx) + (y + .5f - cy) * (y + .5f - cy));
                px[y * RoundedSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(RoundedRadius - d + .5f) * 255f));
            }
            tex.SetPixels32(px); tex.Apply(false, true);
            rounded = Sprite.Create(tex, new Rect(0, 0, RoundedSize, RoundedSize), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(RoundedRadius, RoundedRadius, RoundedRadius, RoundedRadius));
            return rounded;
        }
    }
}
