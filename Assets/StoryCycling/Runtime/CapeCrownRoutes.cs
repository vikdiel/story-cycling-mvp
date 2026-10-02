using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCycling
{
    // Available ride sections. Scene names must match the built .unity scenes.
    public static class CapeCrownRoutes
    {
        public struct Entry { public string label; public string sceneName; public string scenery; }
        // Strecken im Menü "Routen" (Szene muss im Build sein: File > Build Profiles > Scene List; der GPX-Weltbau trägt seine Szene selbst ein)
        public static readonly Entry[] All = {
            new Entry { label = "Nordhoek • Chapman's Peak", sceneName = "NordhoekGpxTest", scenery = "Noordhoek · Chapman's Peak · Hout Bay" }
        };
        // Frühere Kapstadt-Abschnitte (Cape Crown Collection) — bei Bedarf wieder in All aufnehmen und die Szenen in den Build legen
        public static readonly Entry[] Archived = {
            new Entry { label = "Cape Crown Promenade", sceneName = "CampsBayTrainerRide", scenery = "Palmenpromenade · Cafés · Twelve Apostles" },
            new Entry { label = "Cape Town City Center", sceneName = "CapeTownCityCenter", scenery = "Downtown · Bürotürme · Kreuzungen" },
            new Entry { label = "Clifton Cove", sceneName = "CliftonCove", scenery = "Granitbuchten · Strandtreppen · Küstenvillen" },
            new Entry { label = "The Apostles Climb", sceneName = "ApostlesClimb", scenery = "Felsgalerie · Aussichtspunkte · Atlantik" },
            new Entry { label = "Hout Bay Harbour", sceneName = "HoutBayHarbour", scenery = "Fischerhafen · Boote · Markt" },
            new Entry { label = "Constantia Vines", sceneName = "ConstantiaVines", scenery = "Weinreben · Cape-Dutch-Gut · Alleen" },
            new Entry { label = "Bo-Kaap Steps", sceneName = "BoKaapSteps", scenery = "Bunte Fassaden · Gassen · Moschee" },
            new Entry { label = "Kirstenbosch Loop", sceneName = "KirstenboschLoop", scenery = "Baumkronenweg · Garten · Bergwald" },
            new Entry { label = "False Bay Sands", sceneName = "FalseBaySands", scenery = "Bunte Strandhütten · Surfer’s Corner" },
            new Entry { label = "Cape Point Headland", sceneName = "CapePointHeadland", scenery = "Leuchtturm · Fynbos · Steilküste" },
            new Entry { label = "Table Foothills", sceneName = "TableFoothills", scenery = "Tafelberg · Seilbahn · Vorstadt" }
        };
        public static string LastError { get; private set; }
        public static bool IsLoading { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { LastError = null; IsLoading = false; }
        public static Entry Current
        {
            get
            {
                string scene = SceneManager.GetActiveScene().name;
                var e = System.Array.Find(All, x => x.sceneName == scene);
                return e.sceneName != null ? e : System.Array.Find(Archived, x => x.sceneName == scene);
            }
        }
        public static void Load(string sceneName)
        {
            if (IsLoading) return;
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            { LastError = "Route fehlt im Build: " + sceneName; Debug.LogError(LastError); return; }
            LastError = null; IsLoading = true;
            var operation = SceneManager.LoadSceneAsync(sceneName);
            if (operation == null) { IsLoading = false; LastError = "Route konnte nicht geladen werden"; return; }
            operation.completed += _ => IsLoading = false;
        }
    }
}
