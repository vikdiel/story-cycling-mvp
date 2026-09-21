using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace StoryCycling
{
    // Minimal HUD for the GPX test ride: speed + distance readout and a speed slider (0–100 km/h).
    public sealed class GpxTestHud : MonoBehaviour
    {
        [SerializeField] private GpxRideController ride;
        private Text speedText, distText;

        private void Start()
        {
            if (ride == null) ride = FindAnyObjectByType<GpxRideController>();
            var root = new GameObject("GPX HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1440, 900);
            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("UI Events", typeof(EventSystem)).AddComponent<InputSystemUIInputModule>().AssignDefaultActions();

            var panel = Panel(root.transform, new Vector2(0, 0), new Vector2(28, 28), new Vector2(470, 150), new Color(.035f, .095f, .125f, .94f));
            Label(panel.transform, "NORDHOEK · GPX TEST", 20, new Vector2(18, -14), new Vector2(420, 30), new Color(.15f, .66f, .62f));
            speedText = Label(panel.transform, "0 km/h", 30, new Vector2(18, -50), new Vector2(200, 40), new Color(.96f, .94f, .86f));
            distText = Label(panel.transform, "0.00 km", 20, new Vector2(18, -94), new Vector2(300, 30), new Color(.72f, .82f, .82f));

            var slider = CreateSlider(panel.transform, new Vector2(230, -95), new Vector2(210, 30));
            slider.minValue = 0; slider.maxValue = 100; slider.value = 30;
            slider.onValueChanged.AddListener(v => ride.SetSpeed(v));
        }

        private void Update()
        {
            if (speedText != null) speedText.text = ride.SpeedKph.ToString("0") + " km/h";
            if (distText != null) distText.text = (ride.TotalMetres / 1000f).ToString("0.00") + " km von " + (ride.Length / 1000f).ToString("0.0") + " km";
        }

        private RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = anchor; r.pivot = anchor; r.anchoredPosition = pos; r.sizeDelta = size; return r;
        }
        private GameObject Panel(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            var r = Rect("Panel", parent, anchor, pos, size); var img = r.gameObject.AddComponent<Image>(); img.color = color; return r.gameObject;
        }
        private Text Label(Transform parent, string value, int size, Vector2 pos, Vector2 area, Color color)
        {
            var r = Rect(value, parent, new Vector2(0, 1), pos, area); var t = r.gameObject.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); t.fontSize = size; t.text = value; t.color = color; t.raycastTarget = false; t.horizontalOverflow = HorizontalWrapMode.Wrap; return t;
        }
        private Slider CreateSlider(Transform parent, Vector2 pos, Vector2 size)
        {
            var r = Rect("Speed", parent, new Vector2(0, 1), pos, size);
            Panel(r, new Vector2(.5f, .5f), Vector2.zero, new Vector2(size.x, 8), new Color(.25f, .39f, .40f));
            var handle = Panel(r, new Vector2(0, .5f), Vector2.zero, new Vector2(32, 36), new Color(.15f, .66f, .62f));
            var s = r.gameObject.AddComponent<Slider>(); s.targetGraphic = handle.GetComponent<Image>(); s.handleRect = handle.GetComponent<RectTransform>(); s.direction = Slider.Direction.LeftToRight; return s;
        }
    }
}
