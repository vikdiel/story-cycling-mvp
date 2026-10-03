using System;
using System.Collections;
using System.IO;
using System.Text;
using StoryCycling.WorldGen;
using StoryCycling.WorldGen.Editor;
using UnityEngine;
using UnityEngine.UI;

namespace StoryCycling
{
    // Weltbau auf dem Gerät (Prototyp): baut beim Start aus den Rohdaten in StreamingAssets/WorldGen/<Route> (route.gpx, osm.xml, dem.bytes, landcover.bytes)
    // Gelände, Straßen, Gebäude, Details, Vegetation und Verkehr — mit demselben Code wie der Editor (RouteWorldBuild). Zeigt den Fortschritt,
    // misst jeden Schritt und meldet Zeit und Speicher (Konsole/Xcode und am Ende eingeblendet). Die Szene (Licht, Fahrer, Menü) baut der Editor.
    public sealed class RuntimeWorldBuilder : MonoBehaviour
    {
        public RouteWorldConfig config;
        public AssetCatalog catalog;
        [Tooltip("URP-Lit-Vorlage (sorgt dafür, dass Shader und Detail-Variante im Build sind)")] public Material litTemplate;
        public Material oceanSource;
        public string[] landmarkNames = new string[0];
        public GameObject[] landmarks = new GameObject[0];
        public Transform rider;

        public static bool Building { get; private set; }
        public string DataDir => Path.Combine(Application.streamingAssetsPath, "WorldGen", config != null ? config.routeName : "");

        private GameObject overlay;
        private Text title, stepText, detail;
        private RectTransform bar;

        private void Start()
        {
            if (config == null) { Debug.LogError("Weltbau: keine RouteWorldConfig gesetzt."); return; }
            StartCoroutine(Run());
        }

        private void OnDestroy() { Building = false; }

        private IEnumerator Run()
        {
            Building = true;
            BuildOverlay();
            SetProgress("Rohdaten lesen", 0f);
            yield return null;
            string dir = DataDir;
            var input = new RouteWorldInputs
            {
                Gpx = Path.Combine(dir, "route.gpx"), Osm = Path.Combine(dir, "osm.xml"),
                Dem = Path.Combine(dir, "dem.bytes"), LandCover = Path.Combine(dir, "landcover.bytes"),
            };
            if (!File.Exists(input.Gpx) || !File.Exists(input.Dem))
            {
                Fail($"Rohdaten fehlen in {dir} (route.gpx, dem.bytes) — im Editor die Welt mit 'Weltbau auf dem Gerät' neu bauen.");
                yield break;
            }
            long mem0 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            var result = new RouteWorldResult();
            var it = RouteWorldBuild.Run(config, input, catalog, new Sink(this), result, checks: false);
            while (true)
            {
                bool more; Exception error = null;
                try { more = it.MoveNext(); }
                catch (Exception e) { error = e; more = false; }
                if (error != null) { Debug.LogException(error); Fail("Weltbau abgebrochen: " + error.Message); yield break; }
                if (!more) break;
                yield return null;
            }

            SetProgress("Fahrlinie & Verkehr", .95f);
            yield return null;
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            if (result.BakedRoute != null && GpxRide.LoadBaked(result.BakedRoute))
            {
                var ride = FindAnyObjectByType<GpxRideController>();
                if (ride != null) ride.OnRouteChanged();
            }
            var traffic = TrafficWorld.Build(config, result.Net, result.Surface, catalog, result.CarPaint, rider, out var graph);
            if (traffic != null) traffic.preloaded = graph;
            result.Timings.Add(new System.Collections.Generic.KeyValuePair<string, double>("Fahrlinie & Verkehr", t0.Elapsed.TotalSeconds));
            double total = result.TotalSeconds + t0.Elapsed.TotalSeconds;
            GC.Collect();
            long mem1 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

            var sb = new StringBuilder();
            sb.Append($"Welt gebaut in {total:0.0} s · Speicher {mem1 / 1048576} MB (+{(mem1 - mem0) / 1048576} MB) · Gerät {SystemInfo.deviceModel}, {SystemInfo.systemMemorySize} MB RAM\n");
            foreach (var kv in result.Timings) sb.Append($"  {kv.Key}: {kv.Value:0.0} s\n");
            Debug.Log("Weltbau auf dem Gerät — " + sb);
            Building = false;
            title.text = "Welt fertig";
            stepText.text = $"{total:0} s";
            detail.text = sb.ToString();
            bar.anchorMax = new Vector2(1f, 1f);
            yield return new WaitForSecondsRealtime(6f);
            Destroy(overlay);
        }

        private void Fail(string msg)
        {
            Debug.LogError("Weltbau: " + msg);
            Building = false;
            if (title != null) { title.text = "Weltbau fehlgeschlagen"; stepText.text = ""; detail.text = msg; }
        }

        private void SetProgress(string what, float t)
        {
            if (stepText == null) return;
            stepText.text = what;
            bar.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
        }

        // ---------------------------------------------------------------- Ladeanzeige
        private void BuildOverlay()
        {
            overlay = new GameObject("Weltbau", typeof(Canvas), typeof(CanvasScaler));
            var canvas = overlay.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 500;
            var scaler = overlay.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1440, 900); scaler.matchWidthOrHeight = .5f;
            var bg = Box(overlay.transform, Vector2.zero, Vector2.one, new Color(.035f, .095f, .125f, .96f));
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            title = Label(bg, font, "Deine Welt wird gebaut", 44, new Vector2(.1f, .62f), new Vector2(.9f, .72f), new Color(.96f, .94f, .86f));
            stepText = Label(bg, font, "", 26, new Vector2(.1f, .54f), new Vector2(.9f, .61f), new Color(.15f, .66f, .62f));
            var track = Box(bg, new Vector2(.1f, .50f), new Vector2(.9f, .515f), new Color(.16f, .25f, .28f));
            bar = Box(track, Vector2.zero, new Vector2(0f, 1f), new Color(.15f, .66f, .62f));
            detail = Label(bg, font, "Gelände, Straßen, Gebäude und Vegetation entstehen aus den Kartendaten der Strecke.", 18, new Vector2(.1f, .12f), new Vector2(.9f, .47f), new Color(.72f, .82f, .82f));
            detail.alignment = TextAnchor.UpperLeft;
        }
        private static RectTransform Box(Transform parent, Vector2 min, Vector2 max, Color c)
        {
            var r = new GameObject("Box", typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = min; r.anchorMax = max; r.offsetMin = r.offsetMax = Vector2.zero;
            r.gameObject.AddComponent<Image>().color = c;
            return r;
        }
        private static Text Label(Transform parent, Font font, string value, int size, Vector2 min, Vector2 max, Color c)
        {
            var r = new GameObject("Text", typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = min; r.anchorMax = max; r.offsetMin = r.offsetMax = Vector2.zero;
            var t = r.gameObject.AddComponent<Text>(); t.font = font; t.fontSize = size; t.color = c; t.text = value; t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow; t.raycastTarget = false;
            return t;
        }

        // ---------------------------------------------------------------- Ausgabe in den Speicher
        private sealed class Sink : IWorldSink
        {
            private readonly RuntimeWorldBuilder owner;
            public Sink(RuntimeWorldBuilder o) { owner = o; }
            public Mesh Mesh(Mesh mesh) => mesh;
            public Texture2D Texture(Texture2D tex, string name, bool repeat, int maxSize)
            {
                tex.name = name; tex.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear; tex.anisoLevel = repeat ? 4 : 1;
                tex.Apply(tex.mipmapCount > 1, true);                                   // Mipmaps, CPU-Kopie freigeben
                return tex;
            }
            public T Asset<T>(T asset) where T : UnityEngine.Object => asset;
            public Material NewLit()
            {
                Material m;
                if (owner.litTemplate != null) m = new Material(owner.litTemplate);
                else
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) throw new InvalidOperationException("URP-Lit-Shader fehlt im Build (litTemplate setzen).");
                    m = new Material(shader);
                }
                m.DisableKeyword("_DETAIL_MULX2"); m.SetTexture("_DetailAlbedoMap", null);
                m.SetTexture("_BaseMap", null); m.mainTexture = null;
                return m;
            }
            public Material OceanSource() => owner.oceanSource;
            public GameObject Landmark(string name)
            {
                for (int i = 0; i < owner.landmarkNames.Length && i < owner.landmarks.Length; i++) if (owner.landmarkNames[i] == name) return owner.landmarks[i];
                return null;
            }
            public void Progress(string what, float t) => owner.SetProgress(what, t);
        }
    }
}
