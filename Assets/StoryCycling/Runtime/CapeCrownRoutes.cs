using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryCycling
{
    // Available ride sections. Scene names must match the built .unity scenes.
    public static class CapeCrownRoutes
    {
        public struct Entry { public string label; public string sceneName; }
        public static readonly Entry[] All = {
            new Entry { label = "Cape Crown Promenade", sceneName = "CampsBayTrainerRide" },
            new Entry { label = "Clifton Cove", sceneName = "CliftonCove" },
            new Entry { label = "The Apostles Climb", sceneName = "ApostlesClimb" },
            new Entry { label = "Hout Bay Harbour", sceneName = "HoutBayHarbour" },
            new Entry { label = "Constantia Vines", sceneName = "ConstantiaVines" },
            new Entry { label = "Bo-Kaap Steps", sceneName = "BoKaapSteps" },
            new Entry { label = "Kirstenbosch Loop", sceneName = "KirstenboschLoop" },
            new Entry { label = "False Bay Sands", sceneName = "FalseBaySands" },
            new Entry { label = "Cape Point Headland", sceneName = "CapePointHeadland" },
            new Entry { label = "Table Foothills", sceneName = "TableFoothills" }
        };
        public static void Load(string sceneName) => SceneManager.LoadScene(sceneName);
    }
}
