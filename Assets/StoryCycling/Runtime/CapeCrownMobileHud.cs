using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace StoryCycling
{
    public sealed class CapeCrownMobileHud : MonoBehaviour
    {
        [SerializeField] private CapeCrownRideController ride;
        private CapeCrownDevices devices;
        private CapeCrownMusic music;
        private RectTransform safe;
        private GameObject home, hud, settings, pause, routes;
        private Transform list;
        private Text speed,power,heart,distance,connection,startLabel,trainerLabel,heartLabel,pauseLabel,muteLabel,statusLine,demoLabel,titleLabel,routeDescription,routeError;
        private Button start,resume;
        private int revision=-1;
        private bool settingsOpen, routesOpen;
        private Color ink=new Color(.035f,.095f,.125f,.94f),teal=new Color(.15f,.66f,.62f),paper=new Color(.96f,.94f,.86f);
        public void Configure(CapeCrownRideController controller)=>ride=controller;
        private void Start()
        {
            devices=FindAnyObjectByType<CapeCrownDevices>();music=FindAnyObjectByType<CapeCrownMusic>();
            var root=new GameObject("Cape Crown Interface",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var scaler=root.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1440,900);scaler.matchWidthOrHeight=.5f;
            safe=Rect("Safe Area",root.transform,Vector2.zero,Vector2.zero,Vector2.zero,Vector2.zero);safe.anchorMin=Vector2.zero;safe.anchorMax=Vector2.one;safe.offsetMin=safe.offsetMax=Vector2.zero;
            if(FindAnyObjectByType<EventSystem>()==null)new GameObject("UI Events",typeof(EventSystem)).AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
            BuildHome();BuildRide();BuildSettings();BuildPause();BuildRoutes();
        }
        private void BuildHome()
        {
            home=Panel("Start menu",safe,new Vector2(0,.5f),new Vector2(38,0),new Vector2(510,768),ink);
            Label(home.transform,"CAPE CROWN  /  SOUTH AFRICA",24,new Vector2(30,-32),new Vector2(455,34),teal);
            titleLabel=Label(home.transform,"",58,new Vector2(28,-84),new Vector2(460,172),paper);
            titleLabel.resizeTextForBestFit=true;titleLabel.resizeTextMinSize=30;titleLabel.resizeTextMaxSize=58;
            Label(home.transform,"Dein Winter. Deine Küste.",26,new Vector2(30,-265),new Vector2(450,40),paper);
            routeDescription=Label(home.transform,"",23,new Vector2(30,-327),new Vector2(450,68),new Color(.72f,.82f,.82f));
            connection=Label(home.transform,"KICKR verbinden, dann geht es los",21,new Vector2(30,-411),new Vector2(450,56),paper);
            start=Button(home.transform,"Runde starten",new Vector2(30,-480),new Vector2(450,64),teal,()=>{if(ride.CanStart){ride.TryStart();settingsOpen=false;}else ShowSettings();});
            startLabel=start.GetComponentInChildren<Text>();
            Button(home.transform,"Geräte & Einstellungen",new Vector2(30,-558),new Vector2(450,54),new Color(.16f,.25f,.28f),()=>ShowSettings());
            var demoButton=Button(home.transform,"Demo-Fahrt (ohne KICKR)",new Vector2(30,-622),new Vector2(450,50),new Color(.40f,.30f,.20f),()=>{ if(ride.IsDemo)ride.StopDemo(); else { ride.StartDemo(); ride.TryStart(); } });
            demoLabel=demoButton.GetComponentInChildren<Text>();
            Button(home.transform,"Routen",new Vector2(30,-674),new Vector2(450,48),new Color(.16f,.25f,.28f),()=>ShowRoutes());
        }
        private void BuildRide()
        {
            hud=new GameObject("Ride metrics",typeof(RectTransform));hud.transform.SetParent(safe,false);
            var r=hud.GetComponent<RectTransform>();r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=r.offsetMax=Vector2.zero;
            speed=Metric("km/h",28);power=Metric("WATT",208);heart=Metric("PULS · bpm",388);
            var info=Panel("Ride status",hud.transform,new Vector2(0,1),new Vector2(28,-160),new Vector2(526,82),ink);
            distance=Label(info.transform,"",22,new Vector2(17,-10),new Vector2(495,30),paper);
            statusLine=Label(info.transform,"",17,new Vector2(17,-45),new Vector2(495,30),paper);
            var actions=Rect("Ride actions",hud.transform,new Vector2(1,1),new Vector2(-28,-28),new Vector2(262,130),new Vector2(1,1));
            Button(actions,"Geräte",Vector2.zero,new Vector2(262,54),ink,()=>ShowSettings());
            Button(actions,"Pause",new Vector2(0,-66),new Vector2(262,54),ink,()=>ride.Pause());
        }
        private Text Metric(string unit,float x)
        {
            var panel=Panel(unit,hud.transform,new Vector2(0,1),new Vector2(x,-28),new Vector2(166,122),ink);
            Label(panel.transform,unit,18,new Vector2(18,-14),new Vector2(140,28),teal);
            return Label(panel.transform,"—",52,new Vector2(16,-42),new Vector2(145,70),paper);
        }
        private void BuildSettings()
        {
            settings=Panel("Settings backdrop",safe,new Vector2(.5f,.5f),Vector2.zero,new Vector2(950,680),ink);
            Label(settings.transform,"GERÄTE & ATMOSPHÄRE",30,new Vector2(28,-22),new Vector2(690,45),paper);
            Button(settings.transform,"Schließen",new Vector2(750,-22),new Vector2(170,46),new Color(.16f,.25f,.28f),()=>settingsOpen=false);
            trainerLabel=Label(settings.transform,"",23,new Vector2(28,-95),new Vector2(570,63),paper);
            Button(settings.transform,"KICKR suchen",new Vector2(620,-94),new Vector2(176,52),teal,()=>devices.Scan("trainer"));
            Button(settings.transform,"Trennen",new Vector2(807,-94),new Vector2(115,52),new Color(.16f,.25f,.28f),()=>devices.Disconnect("trainer"));
            heartLabel=Label(settings.transform,"",23,new Vector2(28,-179),new Vector2(570,63),paper);
            Button(settings.transform,"Puls suchen",new Vector2(620,-178),new Vector2(176,52),teal,()=>devices.Scan("heart"));
            Button(settings.transform,"Trennen",new Vector2(807,-178),new Vector2(115,52),new Color(.16f,.25f,.28f),()=>devices.Disconnect("heart"));
            list=Rect("Gefundene Geräte",settings.transform,new Vector2(0,1),new Vector2(28,-262),new Vector2(894,155),new Vector2(0,1));
            Label(settings.transform,"Zwift Click · Anbindung noch in Arbeit",21,new Vector2(28,-425),new Vector2(850,34),new Color(.70f,.76f,.76f));
            Label(settings.transform,"ABEND AN DER KÜSTE",20,new Vector2(28,-478),new Vector2(600,30),teal);
            var mute=Button(settings.transform,"",new Vector2(28,-520),new Vector2(218,52),new Color(.16f,.25f,.28f),()=>music.ToggleMute());muteLabel=mute.GetComponentInChildren<Text>();
            var slider=CreateSlider(settings.transform,new Vector2(277,-530),new Vector2(642,36));slider.value=music.Volume;slider.onValueChanged.AddListener(music.SetVolume);
            Label(settings.transform,"Fahren: KICKR Core  ·  Touch: Menüs und Pause",20,new Vector2(28,-614),new Vector2(880,34),new Color(.70f,.76f,.76f));
            Label(settings.transform,"Fahr-Musik: Kevin MacLeod (incompetech.com) · CC BY 4.0",16,new Vector2(28,-650),new Vector2(880,26),new Color(.52f,.60f,.60f));
        }
        private void BuildPause()
        {
            pause=Panel("Pause menu",safe,new Vector2(.5f,.5f),Vector2.zero,new Vector2(610,352),ink);
            Label(pause.transform,"KURZE PAUSE",32,new Vector2(30,-25),new Vector2(550,46),paper);
            pauseLabel=Label(pause.transform,"",22,new Vector2(30,-95),new Vector2(550,80),paper);
            resume=Button(pause.transform,"Weiterfahren",new Vector2(30,-192),new Vector2(262,60),teal,()=>ride.Resume());
            Button(pause.transform,"Geräte",new Vector2(310,-192),new Vector2(270,60),new Color(.16f,.25f,.28f),()=>ShowSettings());
            Button(pause.transform,"Runde beenden",new Vector2(30,-273),new Vector2(550,52),new Color(.16f,.25f,.28f),()=>ride.EndRide());
        }
        public void ShowSettings() { settingsOpen=true;if(ride.Started)ride.Pause("Geräteeinstellungen geöffnet"); }
        public void ShowRoutes() { routesOpen=true;if(ride.Started)ride.Pause("Route wechseln"); }
        private static string TitleFromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            int idx = label.IndexOf('•');
            return (idx < 0 ? label : label.Substring(0, idx)).Trim();
        }
        private void BuildRoutes()
        {
            routes=Panel("Routes backdrop",safe,new Vector2(.5f,.5f),Vector2.zero,new Vector2(860,570),ink);
            Label(routes.transform,"ROUTE WÄHLEN",30,new Vector2(28,-22),new Vector2(700,45),paper);
            Button(routes.transform,"Schließen",new Vector2(680,-22),new Vector2(150,46),new Color(.16f,.25f,.28f),()=>routesOpen=false);
            for (int i = 0; i < CapeCrownRoutes.All.Length; i++)
            {
                var entry = CapeCrownRoutes.All[i];
                int col = i % 2, row = i / 2;
                Button(routes.transform, entry.label, new Vector2(28 + col * 406, -86 - row * 74), new Vector2(390, 62), new Color(.16f,.25f,.28f), () => CapeCrownRoutes.Load(entry.sceneName));
            }
            routeError=Label(routes.transform,"",18,new Vector2(28,-510),new Vector2(800,38),teal);
            Label(routes.transform,"Runde endet beim Wechsel · Demo zählt keinen Fortschritt",18,new Vector2(28,-470),new Vector2(700,30),new Color(.70f,.76f,.76f));
        }
        private void Update()
        {
            if(safe==null)return;
            Rect a=Screen.safeArea;safe.anchorMin=new Vector2(a.xMin/Screen.width,a.yMin/Screen.height);safe.anchorMax=new Vector2(a.xMax/Screen.width,a.yMax/Screen.height);
            home.SetActive(!ride.Started&&!settingsOpen&&!routesOpen);hud.SetActive(ride.Started&&!settingsOpen&&!routesOpen);pause.SetActive(ride.Started&&ride.IsPaused&&!settingsOpen&&!routesOpen);settings.SetActive(settingsOpen&&!routesOpen);routes.SetActive(routesOpen);
            start.interactable=true;startLabel.text=ride.CanStart?"Runde starten":"KICKR verbinden";resume.interactable=ride.CanStart;
            connection.text=ride.IsDemo?"DEMO-FAHRT · simuliert · kein Fortschritt":ride.CanStart?"KICKR bereit · Steig aufs Rad":devices.TrainerConnected?"Verbunden · kurz treten für Live-Daten":devices.TrainerState+" · Geräte öffnen";
            speed.text=devices.FreshSpeed?devices.Speed.ToString("0.0"):"—";power.text=devices.FreshPower?devices.Watts.ToString("0"):"—";heart.text=devices.FreshHeart?devices.Heart.ToString("0"):"—";
            distance.text=$"{ride.TotalMetres/1000:0.00} km    /    Runde {ride.CompletedLaps+1}    /    {CapeCrownRoute.Grade(ride.RouteDistance,ride.HillHeight)*100:+0.0;-0.0;0.0}%";
            statusLine.text=devices.IsTestFeed?"DEMO · KEIN FORTSCHRITT":!devices.FreshSpeed?"Warte auf Trainerdaten":!devices.FreshHeart?"Brustgurt optional · noch kein Puls":"KICKR + PULS  ·  LIVE";
            pauseLabel.text=ride.PauseReason+$"\n{ride.TotalMetres/1000:0.00} km in dieser Fahrt";
            trainerLabel.text=devices.TrainerName+"\n"+devices.TrainerState;heartLabel.text=devices.HeartName+"\n"+devices.HeartState;
            muteLabel.text=music.Muted?"Musik einschalten":"Musik stummschalten";
            if(demoLabel!=null)demoLabel.text=ride.IsDemo?"Demo beenden":"Demo-Fahrt (ohne KICKR)";
            if(titleLabel!=null)titleLabel.text=TitleFromLabel(ride.RouteLabel);
            if(routeDescription!=null)routeDescription.text=CapeCrownRoutes.Current.scenery+$"\n{CapeCrownRoute.Length/1000:0.00} km · Cape Town Collection";
            if(routeError!=null)routeError.text=CapeCrownRoutes.IsLoading?"Route wird geladen …":CapeCrownRoutes.LastError??"";
            if(revision!=devices.Revision)RebuildCandidates();
        }
        private void RebuildCandidates()
        {
            revision=devices.Revision;foreach(Transform child in list)Destroy(child.gameObject);
            if(devices.Candidates.Count==0)Label(list,"Gerät einschalten, Suche starten und hier auswählen.\nZwift / Wahoo während des Verbindens schließen.",21,new Vector2(0,-10),new Vector2(890,90),new Color(.72f,.82f,.82f));
            // Bounded two-column discovery list; no overflow into audio controls.
            for(int i=0;i<Mathf.Min(devices.Candidates.Count,6);i++)
            {
                var candidate=devices.Candidates[i];Button(list,candidate.name,new Vector2(i%2*452,-i/2*50),new Vector2(437,44),new Color(.16f,.25f,.28f),()=>devices.Connect(candidate));
            }
        }
        private RectTransform Rect(string name,Transform parent,Vector2 anchor,Vector2 pos,Vector2 size,Vector2 pivot)
        {
            var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=anchor;r.pivot=pivot;r.anchoredPosition=pos;r.sizeDelta=size;return r;
        }
        private GameObject Panel(string name,Transform parent,Vector2 anchor,Vector2 pos,Vector2 size,Color color)
        {
            var r=Rect(name,parent,anchor,pos,size,anchor);var image=r.gameObject.AddComponent<Image>();image.color=color;return r.gameObject;
        }
        private Text Label(Transform parent,string value,int size,Vector2 pos,Vector2 area,Color color)
        {
            var r=Rect(value,parent,new Vector2(0,1),pos,area,new Vector2(0,1));var text=r.gameObject.AddComponent<Text>();
            text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.fontSize=size;text.text=value;text.color=color;text.raycastTarget=false;text.horizontalOverflow=HorizontalWrapMode.Wrap;return text;
        }
        private Button Button(Transform parent,string title,Vector2 pos,Vector2 size,Color color,UnityEngine.Events.UnityAction click)
        {
            var go=Panel(title,parent,new Vector2(0,1),pos,size,color);var button=go.AddComponent<Button>();button.targetGraphic=go.GetComponent<Image>();button.onClick.AddListener(click);
            var text=Label(go.transform,title,21,new Vector2(8,0),size-new Vector2(16,0),paper);text.gameObject.name="Label";text.alignment=TextAnchor.MiddleCenter;
            var colors=button.colors;colors.disabledColor=new Color(.45f,.5f,.5f,.8f);button.colors=colors;return button;
        }
        private Slider CreateSlider(Transform parent,Vector2 pos,Vector2 size)
        {
            var r=Rect("Musiklautstärke",parent,new Vector2(0,1),pos,size,new Vector2(0,1));
            var bg=Panel("Track",r,new Vector2(.5f,.5f),Vector2.zero,new Vector2(size.x,8),new Color(.25f,.39f,.40f));
            var handle=Panel("Handle",r,new Vector2(0,.5f),Vector2.zero,new Vector2(32,36),teal);
            var slider=r.gameObject.AddComponent<Slider>();slider.targetGraphic=handle.GetComponent<Image>();slider.handleRect=handle.GetComponent<RectTransform>();slider.direction=Slider.Direction.LeftToRight;return slider;
        }
    }
}
