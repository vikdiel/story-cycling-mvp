using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Authors landmark PREFABS (editor-time, not runtime generation) modelled on the real
    // landmarks of the Nordhoek / Chapman's Peak / Hout Bay route. Each landmark is built
    // from primitives and saved as a .prefab under Assets/WorldAssets/Landmarks/, so the
    // world generator can place them like any other catalog asset (placer, not inventor).
    public static class LandmarkFactory
    {
        private const string OutDir = "Assets/WorldAssets/Landmarks";
        private static Shader lit;
        private static readonly Dictionary<Color, Material> mats = new Dictionary<Color, Material>();

        [MenuItem("Story Cycling/WorldGen/Build Landmarks")]
        public static void BuildAll()
        {
            lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new System.Exception("URP Lit shader missing");
            Directory.CreateDirectory(OutDir);
            AssetDatabase.Refresh();

            SlangkopLighthouse();
            HoutBayHarbour();
            KakapoShipwreck();
            EastFort();
            ChapmansLookout();
            ConstantiaManor();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Landmarks built in {OutDir}");
        }

        // --- helpers ---------------------------------------------------------
        static Material Mat(Color c)
        {
            if (mats.TryGetValue(c, out var m)) return m;
            m = new Material(lit) { color = c };
            m.SetFloat("_Smoothness", 0.12f);
            AssetDatabase.CreateAsset(m, $"{OutDir}/_mat_{mats.Count:D2}.mat");
            mats[c] = m;
            return m;
        }

        static GameObject Prim(Transform parent, PrimitiveType t, string name, Vector3 pos, Vector3 scale, Color c)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            return go;
        }

        static GameObject Cube(Transform parent, string name, Vector3 pos, Vector3 scale, Color c)
            => Prim(parent, PrimitiveType.Cube, name, pos, scale, c);
        static GameObject Cyl(Transform parent, string name, Vector3 pos, Vector3 scale, Color c)
            => Prim(parent, PrimitiveType.Cylinder, name, pos, scale, c);
        static GameObject Sph(Transform parent, string name, Vector3 pos, Vector3 scale, Color c)
            => Prim(parent, PrimitiveType.Sphere, name, pos, scale, c);

        static void Save(string fileName, GameObject root)
        {
            string path = $"{OutDir}/{fileName}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        // --- landmarks -------------------------------------------------------
        static void SlangkopLighthouse()
        {
            var root = new GameObject("Landmark_SlangkopLighthouse");
            Color white = new Color(.94f, .93f, .88f);
            Color red = new Color(.72f, .22f, .18f);
            Color dark = new Color(.13f, .16f, .18f);
            Cube(root.transform, "Base platform", new Vector3(0, .25f, 0), new Vector3(9, .5f, 9), new Color(.62f, .62f, .58f));
            Cyl(root.transform, "Tower lower", new Vector3(0, 4f, 0), new Vector3(3.4f, 4f, 3.4f), white);
            Cyl(root.transform, "Tower red band", new Vector3(0, 7.4f, 0), new Vector3(3.2f, 2.8f, 3.2f), red);
            Cyl(root.transform, "Tower upper", new Vector3(0, 10.2f, 0), new Vector3(3f, 2.8f, 3f), white);
            Cyl(root.transform, "Gallery", new Vector3(0, 11.9f, 0), new Vector3(3.8f, .6f, 3.8f), dark);
            Cyl(root.transform, "Lantern room", new Vector3(0, 13.1f, 0), new Vector3(2.2f, 1.8f, 2.2f), new Color(.55f, .75f, .85f));
            Cyl(root.transform, "Roof", new Vector3(0, 14.4f, 0), new Vector3(2.6f, .8f, 2.6f), dark);
            Sph(root.transform, "Lamp", new Vector3(0, 13.1f, 0), new Vector3(.8f, .8f, .8f), new Color(1f, .92f, .6f));
            Save("Landmark_SlangkopLighthouse", root);
        }

        static void HoutBayHarbour()
        {
            var root = new GameObject("Landmark_HoutBayHarbour");
            Color quay = new Color(.58f, .56f, .5f);
            Color wood = new Color(.4f, .32f, .22f);
            Color hull = new Color(.15f, .34f, .45f);
            Color white = new Color(.9f, .88f, .82f);
            Cube(root.transform, "Quay", new Vector3(0, -.2f, 0), new Vector3(30, .6f, 14), quay);
            // Fishing boat: hull, cabin, mast.
            Cube(root.transform, "Boat hull", new Vector3(0, .4f, 0), new Vector3(3f, 1.2f, 8f), hull);
            Cube(root.transform, "Cabin", new Vector3(-1, 1.6f, 1), new Vector3(1.6f, 1.4f, 2.4f), white);
            Cyl(root.transform, "Mast", new Vector3(1.2f, 3f, 0), new Vector3(.15f, 3f, .15f), wood);
            Cube(root.transform, "Boom", new Vector3(1.2f, 2.2f, 2.5f), new Vector3(.15f, .15f, 5f), wood);
            // Fish market shed.
            Cube(root.transform, "Fish market", new Vector3(-8, 1.6f, 5), new Vector3(7, 3.2f, 5), new Color(.85f, .72f, .48f));
            Cyl(root.transform, "Bollard a", new Vector3(-3, .5f, -4), new Vector3(.3f, 1f, .3f), dark());
            Cyl(root.transform, "Bollard b", new Vector3(-4, .5f, -4), new Vector3(.3f, 1f, .3f), dark());
            Save("Landmark_HoutBayHarbour", root);
        }

        static void KakapoShipwreck()
        {
            var root = new GameObject("Landmark_KakapoShipwreck");
            Color rust = new Color(.45f, .26f, .18f);
            Color sand = new Color(.75f, .68f, .5f);
            Color dark = new Color(.12f, .13f, .14f);
            Cube(root.transform, "Sand mound", new Vector3(0, -.3f, 0), new Vector3(20, .8f, 12), sand);
            // Hull angled half-buried.
            var hull = Cube(root.transform, "Wrecked hull", new Vector3(0, .5f, 0), new Vector3(8f, 2f, 14f), rust);
            hull.transform.localRotation = Quaternion.Euler(12f, 0f, 8f);
            Cyl(root.transform, "Funnel", new Vector3(-2, 2.6f, 4), new Vector3(1.2f, 3f, 1.2f), dark);
            Cyl(root.transform, "Broken mast", new Vector3(2, 3f, -3), new Vector3(.3f, 4f, .3f), rust);
            Sph(root.transform, "Rust pile a", new Vector3(3, .6f, 5), new Vector3(1.5f, 1f, 1.5f), rust);
            Sph(root.transform, "Rust pile b", new Vector3(-4, .4f, -4), new Vector3(1.2f, .8f, 1.2f), rust);
            Save("Landmark_KakapoShipwreck", root);
        }

        static void EastFort()
        {
            var root = new GameObject("Landmark_EastFort");
            Color stone = new Color(.55f, .53f, .47f);
            Color dark = new Color(.1f, .12f, .14f);
            // Wall enclosure.
            Cube(root.transform, "Fort wall north", new Vector3(0, 1.5f, -6), new Vector3(16, 3f, 1.2f), stone);
            Cube(root.transform, "Fort wall south", new Vector3(0, 1.5f, 6), new Vector3(16, 3f, 1.2f), stone);
            Cube(root.transform, "Fort wall east", new Vector3(8, 1.5f, 0), new Vector3(1.2f, 3f, 13.2f), stone);
            Cube(root.transform, "Fort wall west", new Vector3(-8, 1.5f, 0), new Vector3(1.2f, 3f, 13.2f), stone);
            Cube(root.transform, "Parapet ledge", new Vector3(0, 3.1f, -6), new Vector3(16, .5f, 1.6f), stone);
            // Two cannons facing seaward.
            for (int i = -1; i <= 1; i += 2)
            {
                Cyl(root.transform, "Cannon barrel " + i, new Vector3(i * 3, 1.2f, -5.4f), new Vector3(.5f, .5f, 2.4f), dark);
                Cyl(root.transform, "Cannon wheel " + i, new Vector3(i * 3, .6f, -4.2f), new Vector3(.9f, .9f, .2f), dark);
            }
            // Flag.
            Cyl(root.transform, "Flag pole", new Vector3(0, 4.5f, 0), new Vector3(.15f, 5f, .15f), dark);
            Cube(root.transform, "Flag", new Vector3(1.3f, 6.4f, 0), new Vector3(2.6f, 1.4f, .1f), new Color(.85f, .2f, .15f));
            Save("Landmark_EastFort", root);
        }

        static void ChapmansLookout()
        {
            var root = new GameObject("Landmark_ChapmansLookout");
            Color rock = new Color(.5f, .48f, .42f);
            Color conc = new Color(.68f, .66f, .6f);
            Color dark = new Color(.12f, .14f, .16f);
            Cube(root.transform, "Viewpoint platform", new Vector3(0, -.3f, 0), new Vector3(14, .6f, 10), rock);
            // Rockfall shelter canopy on posts.
            Cube(root.transform, "Canopy", new Vector3(0, 4.6f, 0), new Vector3(10, .5f, 7), conc);
            for (int i = -1; i <= 1; i += 2)
            {
                Cyl(root.transform, "Post " + i + "a", new Vector3(i * 4, 2.4f, -2.8f), new Vector3(.4f, 4.4f, .4f), dark);
                Cyl(root.transform, "Post " + i + "b", new Vector3(i * 4, 2.4f, 2.8f), new Vector3(.4f, 4.4f, .4f), dark);
            }
            // View railing + a bench.
            for (int i = 0; i < 6; i++)
                Cyl(root.transform, "Rail post " + i, new Vector3(-6 + i * 2.4f, .5f, 4.5f), new Vector3(.15f, 1f, .15f), dark);
            Cube(root.transform, "Rail", new Vector3(0, 1f, 4.5f), new Vector3(12.5f, .12f, .12f), dark);
            Cube(root.transform, "Bench seat", new Vector3(-3, .7f, 3.5f), new Vector3(2.6f, .15f, .7f), conc);
            Cube(root.transform, "Bench back", new Vector3(-3, 1.2f, 4f), new Vector3(2.6f, .9f, .15f), conc);
            Save("Landmark_ChapmansLookout", root);
        }

        static void ConstantiaManor()
        {
            var root = new GameObject("Landmark_ConstantiaManor");
            Color white = new Color(.94f, .93f, .88f);
            Color dark = new Color(.15f, .17f, .19f);
            Color thatch = new Color(.42f, .34f, .24f);
            Cube(root.transform, "Manor body", new Vector3(0, 2.5f, 0), new Vector3(16, 5f, 9f), white);
            // Cape-Dutch gable roof (prism via cubes).
            Cube(root.transform, "Roof", new Vector3(0, 5.4f, 0), new Vector3(17f, .6f, 10f), thatch);
            Cube(root.transform, "Central gable", new Vector3(0, 6.6f, 0), new Vector3(.6f, 2.4f, 4f), white);
            Cube(root.transform, "Gable top", new Vector3(0, 8f, 0), new Vector3(4f, .6f, 4f), white);
            // Doors and windows.
            Cube(root.transform, "Door", new Vector3(0, 1.4f, 4.55f), new Vector3(1.6f, 2.8f, .2f), dark);
            for (int i = -2; i <= 2; i++)
                if (i != 0)
                    Cube(root.transform, "Window " + i, new Vector3(i * 3, 2.4f, 4.55f), new Vector3(1.4f, 1.8f, .15f), dark);
            Cube(root.transform, "Porch", new Vector3(0, -.1f, 5.5f), new Vector3(8f, .3f, 2.5f), white);
            Save("Landmark_ConstantiaManor", root);
        }

        static Color dark() => new Color(.12f, .14f, .16f);
    }
}
