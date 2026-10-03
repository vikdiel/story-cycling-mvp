using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Menüpunkte zur Route-Config (die Config selbst liegt im Laufzeit-Code, weil der Weltbau auf dem Gerät sie braucht).
    public static class RouteWorldConfigMenu
    {
        [MenuItem("Story Cycling/WorldGen/Fetch DEM for Selected Route")]
        private static void FetchDem() { var c = RouteWorldConfig.Selected(); DemFetcher.Fetch(c.gpxPath, c.demPath); }

        [MenuItem("Story Cycling/WorldGen/Fetch Land Cover for Selected Route")]
        private static void FetchLandCover() { var c = RouteWorldConfig.Selected(); LandCoverFetcher.Fetch(c.gpxPath, c.LandCoverFile); }

        [MenuItem("Story Cycling/WorldGen/Fetch OSM for Selected Route")]
        private static void FetchOsm() { var c = RouteWorldConfig.Selected(); OsmFetcher.Fetch(c.gpxPath, c.osmPath); }

        [MenuItem("Story Cycling/WorldGen/Build OSM2Streets Geometry for Selected Route")]
        private static void FetchOsm2Streets()
        {
            var c = RouteWorldConfig.Selected();
            if (Osm2StreetsGeometry.TryRun(c.gpxPath, c.osmPath, c.Osm2StreetsPath, out string msg)) Debug.Log(msg);
            else Debug.LogWarning(msg);
        }

        [MenuItem("Story Cycling/WorldGen/Build Selected Route World")]
        private static void Build() => GpxSceneBuilder.Build(RouteWorldConfig.Selected());
    }
}
