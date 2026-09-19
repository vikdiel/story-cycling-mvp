using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCycling
{
    // Available ride sections. Scene names must match the built .unity scenes.
    public static class CapeCrownRoutes
    {
        public struct Entry { public string label; public string sceneName; public string scenery; }
        public static readonly Entry[] All = {
            new Entry { label = "Cape Crown Promenade", sceneName = "CampsBayTrainerRide", scenery = "Palmenpromenade · Cafés · Twelve Apostles" },
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
        public static Entry Current => System.Array.Find(All, e => e.sceneName == SceneManager.GetActiveScene().name);
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
