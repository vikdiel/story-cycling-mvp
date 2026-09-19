using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StoryCycling.Editor
{
    // Dense "Cape Town City Center" loop: a complete downtown built road-first from
    // Synty building prefabs — street-wall shops, apartment mid-rings and an office-tower
    // skyline — with sidewalks, visible cross streets, traffic lights and street lamps.
    public static partial class CapeCrownSceneBuilder
    {
        private static readonly string[] CityShops = {
            Root + "Buildings/SM_Bld_Shop_01.prefab", Root + "Buildings/SM_Bld_Shop_02.prefab",
            Root + "Buildings/SM_Bld_Shop_03.prefab", Root + "Buildings/SM_Bld_Shop_04.prefab",
            Root + "Buildings/SM_Bld_Shop_05.prefab", Root + "Buildings/SM_Bld_Shop_06.prefab",
            Root + "Buildings/SM_Bld_Shop_Corner_01.prefab", Root + "Buildings/SM_Bld_Shop_Corner_02.prefab"
        };
        private static readonly string[] CityApartments = {
            Root + "Buildings/SM_Bld_Apartment_01.prefab", Root + "Buildings/SM_Bld_Apartment_02.prefab",
            Root + "Buildings/SM_Bld_Apartment_03.prefab", Root + "Buildings/SM_Bld_Apartment_Corner_01.prefab",
            Root + "Buildings/SM_Bld_Apartment_Corner_02.prefab", Root + "Buildings/SM_Bld_Apartment_Corner_03.prefab",
            Root + "Buildings/SM_Bld_Apartment_Door_01.prefab", Root + "Buildings/SM_Bld_Apartment_Door_02.prefab"
        };
        private static readonly string[] CityTowers = {
            Root + "Buildings/SM_Bld_OfficeSquare_01.prefab", Root + "Buildings/SM_Bld_OfficeSquare_02.prefab",
            Root + "Buildings/SM_Bld_OfficeSquare_03.prefab", Root + "Buildings/SM_Bld_OfficeSquare_04.prefab",
            Root + "Buildings/SM_Bld_OfficeRound_01.prefab", Root + "Buildings/SM_Bld_OfficeRound_02.prefab",
            Root + "Buildings/SM_Bld_OfficeRound_03.prefab", Root + "Buildings/SM_Bld_OfficeRound_04.prefab",
            Root + "Buildings/SM_Bld_OfficeOctagon_01.prefab", Root + "Buildings/SM_Bld_OfficeOld_Large_01.prefab",
            Root + "Buildings/SM_Bld_OfficeOld_Large_02.prefab"
        };

        private const float IntersectionSpacing = 110f;
        private const float IntersectionGap = 12f;

        [MenuItem("Story Cycling/Build Cape Town City Center")]
        public static void BuildCityCenter()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            relief = 0f;
            Generated = "Assets/StoryCycling/GeneratedCityCenter";
            string scenePath = "Assets/StoryCycling/Scenes/CapeTownCityCenter.unity";
            Directory.CreateDirectory(Generated);
            Directory.CreateDirectory("Assets/StoryCycling/Scenes");
            AssetDatabase.Refresh();
            assetId = 0;
            CapeCrownRoute.Define(Stadium(155f, 66f));
            SetHillsFrac();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildCityBlocks();
            FinishRouteScene(scene, scenePath, "CAPE TOWN • CITY CENTER", true);
        }

        private static bool NearIntersection(float d)
        {
            float m = Mathf.Repeat(d, IntersectionSpacing);
            return m < IntersectionGap || m > IntersectionSpacing - IntersectionGap;
        }

        private static void BuildCityBlocks()
        {
            Material ground = Mat("City ground", new Color(.15f, .17f, .19f));
            Box("City ground", new Vector3(0, -0.06f, 0), new Vector3(900, 0.12f, 900), ground);

            Material sidewalk = Mat("Sidewalk", new Color(.50f, .51f, .53f));
            Strip("Sidewalk outer", 4.3f, 11.0f, 0.045f, 0, CapeCrownRoute.Length, sidewalk);
            Strip("Sidewalk inner", -11.0f, -4.3f, 0.045f, 0, CapeCrownRoute.Length, sidewalk);

            // Five staggered rings: dense street wall out to a tower skyline.
            BuildFacade(10.5f, 8.5f, CityShops, false, 0f);
            BuildFacade(20f, 11f, CityApartments, false, 4f);
            BuildFacade(31f, 13f, CityApartments, true, 2f);
            BuildFacade(47f, 18f, CityTowers, true, 7f);
            BuildFacade(72f, 22f, CityTowers, true, 12f);

            AddIntersections();
            AddStreetLamps();
            AddParkedCars(6f, 20f);
        }

        private static void BuildFacade(float offset, float spacing, string[] prefabs, bool deepRing, float phase)
        {
            for (float d = phase; d < CapeCrownRoute.Length; d += spacing)
            {
                if (NearIntersection(d)) continue; // leave room for cross streets
                int idx = Mathf.RoundToInt(d / spacing);
                if (deepRing)
                {
                    CapeCrownRoute.Sample(d, out _, out Vector3 fwd);
                    CapeCrownRoute.Sample(d + 6, out _, out Vector3 ahead);
                    Vector3 hf = new Vector3(fwd.x, 0, fwd.z);
                    Vector3 ah = new Vector3(ahead.x, 0, ahead.z);
                    if (hf.sqrMagnitude < 1e-6f) hf = Vector3.forward;
                    if (ah.sqrMagnitude < 1e-6f) ah = Vector3.forward;
                    if (Mathf.Abs(Vector3.SignedAngle(hf.normalized, ah.normalized, Vector3.up)) > 9f) continue;
                }
                for (int side = -1; side <= 1; side += 2)
                    PlaceRoadside(prefabs[idx % prefabs.Length], d, side * offset, spacing * 1.15f, idx);
            }
        }

        private static GameObject PlaceRoadside(string path, float d, float lateral, float footprint, int idx)
        {
            CapeCrownRoute.Sample(d, out Vector3 p, out Vector3 fwd);
            Vector3 hf = new Vector3(fwd.x, 0, fwd.z);
            if (hf.sqrMagnitude < 1e-6f) hf = Vector3.forward;
            hf.Normalize();
            float yaw = Mathf.Atan2(hf.x, hf.z) * Mathf.Rad2Deg;
            Vector3 pos = p + Vector3.Cross(Vector3.up, hf) * lateral;
            return GroundPrefab(path, pos, yaw + 90f, footprint, "City " + idx);
        }

        private static void AddIntersections()
        {
            Material asphalt = Mat("Cross street", new Color(.11f, .13f, .16f));
            for (float d = IntersectionSpacing * 0.5f; d < CapeCrownRoute.Length; d += IntersectionSpacing)
            {
                CapeCrownRoute.Sample(d, out Vector3 p, out Vector3 fwd);
                Vector3 hf = new Vector3(fwd.x, 0, fwd.z).normalized;
                var street = Part(null, PrimitiveType.Cube, p + Vector3.up * 0.055f, new Vector3(52f, .06f, 8f), asphalt);
                street.transform.rotation = Quaternion.LookRotation(hf, Vector3.up);
                street.name = "Cross street";
                PlaceRoadside(TrafficLightPrefab, d, 5.7f, 2.4f, 0);
                PlaceRoadside(TrafficLightPrefab, d, -5.7f, 2.4f, 1);
                PlaceRoadside(GiveWaySign, d, 4.5f, 1.8f, 2);
            }
        }

        private static void AddStreetLamps()
        {
            for (float d = 12f; d < CapeCrownRoute.Length; d += 24f)
            {
                int side = (Mathf.RoundToInt(d / 24f) % 2 == 0) ? -1 : 1;
                var lamp = PlaceRoadside(LampPole, d, side * 7.4f, 2.4f, 3);
                if (lamp != null) lamp.transform.localScale *= 1.6f; // more prominent street lamps
            }
        }
    }
}
