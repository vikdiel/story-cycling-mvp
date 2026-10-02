using System;
using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    // Radcomputer im Stil eines Lenker-Computers (eigene Gestaltung, kein Hersteller-Logo): LED-Leiste nach Leistungszone, Leistung (3-s-Mittel),
    // Tempo, Puls und das Höhenprofil des nächsten Kilometers. Liest die Werte aus CapeCrownDevices (Trainer/Brustgurt/Demo) und die Strecke aus der Fahrt.
    public sealed class BikeComputerView : MonoBehaviour
    {
        [Tooltip("FTP für die LED-Zonen (W)")] public float ftp = 250f;
        public const float ProfileMetres = 1000f;
        private const int Segments = 64;

        private Func<IRideSession> session;
        private CapeCrownDevices devices;
        private Text power, speed, heart, grade, climb, clock, trainerTag, heartTag;
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
            // Gehäuse unten rechts (Referenzauflösung 1440 x 900), hochkant wie ein Lenker-Computer
            var body = Box("Bike computer", parent, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-28, 28), new Vector2(300, 476), BodyCol, 26f);
            var v = body.gameObject.AddComponent<BikeComputerView>();
            v.session = session; v.devices = devices;
            v.leds = new Image[7];
            for (int i = 0; i < 7; i++) v.leds[i] = Box("LED " + i, body, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2((i - 3) * 34f, -12f), new Vector2(26, 7), LedOff, 3.5f).GetComponent<Image>();
            for (int i = 0; i < 3; i++) Box("Taste " + i, body, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2((i - 1) * 74f, 12f), new Vector2(46, 9), new Color(.20f, .21f, .22f), 4.5f);

            var scr = Box("Display", body, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -30), new Vector2(272, 404), ScreenCol, 9f);
            // Statuszeile
            v.clock = Label(scr, "", 15, FontStyle.Bold, new Vector2(12, -8), new Vector2(80, 20), Ink, TextAnchor.MiddleLeft);
            v.trainerTag = Label(scr, "TRAINER", 12, FontStyle.Bold, new Vector2(128, -8), new Vector2(70, 20), Muted, TextAnchor.MiddleRight);
            v.heartTag = Label(scr, "HR", 12, FontStyle.Bold, new Vector2(204, -8), new Vector2(56, 20), Muted, TextAnchor.MiddleRight);
            Rule(scr, -32);
            // Leistung
            Label(scr, "LEISTUNG 3S  W", 12, FontStyle.Bold, new Vector2(12, -38), new Vector2(248, 18), Muted, TextAnchor.UpperLeft);
            v.power = Label(scr, "—", 74, FontStyle.Bold, new Vector2(8, -52), new Vector2(256, 86), Ink, TextAnchor.MiddleCenter);
            Rule(scr, -142);
            // Tempo | Puls
            Label(scr, "TEMPO  KM/H", 12, FontStyle.Bold, new Vector2(12, -148), new Vector2(120, 18), Muted, TextAnchor.UpperLeft);
            v.speed = Label(scr, "—", 44, FontStyle.Bold, new Vector2(6, -164), new Vector2(124, 60), Ink, TextAnchor.MiddleCenter);
            Label(scr, "PULS  BPM", 12, FontStyle.Bold, new Vector2(148, -148), new Vector2(116, 18), Muted, TextAnchor.UpperLeft);
            v.heart = Label(scr, "—", 44, FontStyle.Bold, new Vector2(142, -164), new Vector2(124, 60), Ink, TextAnchor.MiddleCenter);
            Box("Trenner", scr, new Vector2(0, 1), new Vector2(0, 1), new Vector2(135.5f, -148), new Vector2(1, 82), RuleCol, 0f);
            Rule(scr, -236);
            // Höhenprofil nächster Kilometer
            Label(scr, "NÄCHSTER KM", 12, FontStyle.Bold, new Vector2(12, -243), new Vector2(120, 18), Muted, TextAnchor.UpperLeft);
            v.grade = Label(scr, "", 20, FontStyle.Bold, new Vector2(132, -240), new Vector2(128, 24), Ink, TextAnchor.UpperRight);
            var g = new GameObject("Hoehenprofil", typeof(RectTransform)).GetComponent<RectTransform>();
            g.SetParent(scr, false); g.anchorMin = g.anchorMax = g.pivot = new Vector2(0, 1); g.anchoredPosition = new Vector2(12, -270); g.sizeDelta = new Vector2(248, 98);
            v.profile = g.gameObject.AddComponent<ElevationProfileGraphic>(); v.profile.raycastTarget = false;
            Label(scr, "0", 11, FontStyle.Normal, new Vector2(12, -372), new Vector2(40, 16), Muted, TextAnchor.UpperLeft);
            Label(scr, "1 km", 11, FontStyle.Normal, new Vector2(200, -372), new Vector2(60, 16), Muted, TextAnchor.UpperRight);
            v.climb = Label(scr, "", 14, FontStyle.Bold, new Vector2(60, -370), new Vector2(150, 20), Ink, TextAnchor.UpperCenter);
            return v;
        }

        private void Update()
        {
            var s = session != null ? session() : null;
            float now = Time.unscaledTime;
            clock.text = DateTime.Now.ToString("HH:mm");
            bool hasDev = devices != null;
            trainerTag.color = hasDev && devices.TrainerConnected ? Ok : Muted;
            heartTag.color = hasDev && devices.HeartConnected ? Ok : Muted;

            // Leistung: 3-s-Mittel (10 Hz), wie am Lenker-Computer üblich — ruhiger als der Rohwert
            bool fp = hasDev && devices.FreshPower;
            if (now - pwT >= .1f) { pwT = now; if (fp) { pw[pwI] = devices.Watts; pwI = (pwI + 1) % pw.Length; pwN = Mathf.Min(pwN + 1, pw.Length); } else pwN = 0; }
            float avg = 0f; for (int i = 0; i < pwN; i++) avg += pw[i]; if (pwN > 0) avg /= pwN;
            power.text = fp && pwN > 0 ? Mathf.RoundToInt(avg).ToString() : "—";
            speed.text = hasDev && devices.FreshSpeed ? devices.Speed.ToString("0.0") : "—";
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
        private static void Rule(RectTransform parent, float y) => Box("Linie", parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, y), new Vector2(252, 1), RuleCol, 0f);
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
