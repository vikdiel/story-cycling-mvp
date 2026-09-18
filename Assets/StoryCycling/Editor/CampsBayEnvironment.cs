using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.Editor
{
    public static partial class CapeCrownSceneBuilder
    {
        // Compact, art-directed interpretation; NOT a geographic reconstruction.
        private static readonly string[] CampsBayShops = {
            Root + "Buildings/SM_Bld_Shop_01.prefab",
            Root + "Buildings/SM_Bld_Shop_03.prefab",
            Root + "Buildings/SM_Bld_Shop_05.prefab",
            Root + "Buildings/SM_Bld_Shop_06.prefab"
        };
        private const string Bench = Root + "Props/SM_Prop_ParkBench_01.prefab";
        private const string Umbrella = Root + "Props/SM_Prop_Umbrella_01.prefab";
        private const string CafeTable = Root + "Props/SM_Prop_PicnicTable_01.prefab";
        private static string[] CampsBayAssets => new[] {
            CampsBayShops[0], CampsBayShops[1], CampsBayShops[2], CampsBayShops[3], Bench, Umbrella, CafeTable, Tree
        };
        private static readonly List<GameObject> CoastalProps = new List<GameObject>();
        private static readonly List<GameObject> CoastalShops = new List<GameObject>();

        private static void BuildCampsBayEnvironment()
        {
            CoastalProps.Clear(); CoastalShops.Clear();
            Material grass = Mat("Fynbos sage", new Color(.39f, .46f, .32f));
            Material sand = Mat("Warm ivory beach", new Color(.87f, .83f, .69f));
            Material stone = Mat("Promenade limestone", new Color(.72f, .70f, .62f));
            Material wetSand = Mat("Wet sand", new Color(.64f, .65f, .52f));
            Material water = Mat("Atlantic blue", new Color(.055f, .32f, .46f));
            Material shallow = Mat("Turquoise shallows", new Color(.14f, .52f, .59f));
            Material foam = Mat("Surf", new Color(.80f, .91f, .88f));
            Material rock = Mat("Mountain sandstone", new Color(.40f, .44f, .40f));
            Material timber = Mat("Palm trunk", new Color(.39f, .31f, .22f));
            Material leaf = Mat("Palm fronds", new Color(.22f, .40f, .23f));
            Material accent = Mat("Terrace teal", new Color(.16f, .38f, .41f));

            // Land continues inland. Ocean only occupies the seaward side, not an island moat.
            Box("Coastal land", new Vector3(-326, -.65f, 0), new Vector3(772, 1, 1400), grass);
            Box("Atlantic horizon", new Vector3(1080, -1.15f, 0), new Vector3(1970, .2f, 2600), water);
            CoastRibbon("Wide crescent beach", z => 60, z => ShoreX(z), -.15f, -1.12f, sand);
            CoastRibbon("Wet shoreline", z => ShoreX(z) - 5, z => ShoreX(z) + 1, -.91f, -1.1f, wetSand);
            CoastRibbon("Shallow water", z => ShoreX(z) - .5f, z => ShoreX(z) + 29, -1.015f, -1.015f, shallow);
            for (int i = 0; i < 3; i++)
            {
                float offset = 2 + i * 9;
                CoastRibbon("Breaking surf " + i,
                    z => ShoreX(z) + offset + Mathf.Sin(z * .09f + offset) * 1.1f,
                    z => ShoreX(z) + offset + Mathf.Sin(z * .09f + offset) * 1.1f + .5f,
                    -.995f, -.995f, foam);
            }
            MountainBackdrop(rock);

            // Promenade broadens on the beach straight, retaining the proven asphalt loop.
            Box("Beach promenade extension", new Vector3(58, -.035f, 0), new Vector3(4, .08f, 178), stone);
            Box("Cafe terrace ribbon", new Vector3(37, -.04f, 0), new Vector3(6, .1f, 166), stone);
            for (int i = 0; i < 9; i++)
            {
                float z = -72 + 18 * i;
                GameObject shop = GroundPrefab(CampsBayShops[i % CampsBayShops.Length], Vector3.zero, 90, 15.5f, "Cafe frontage " + i);
                Bounds b = BoundsOf(shop);
                shop.transform.localScale *= Mathf.Min(1, 8f / b.size.y);
                b = BoundsOf(shop);
                // Shop windows face local +Z in these Synty models; +90 yaw faces the beach (+X).
                shop.transform.position += new Vector3(33.5f - b.max.x, -.14f - b.min.y, z - b.center.z);
                CoastalShops.Add(shop); CoastalProps.Add(shop);
                AddCoastalProp(CafeTable, new Vector3(36.5f, .01f, z - 2.8f), 90, 2.7f, "Cafe terrace table " + i);
                AddCoastalProp(Umbrella, new Vector3(36.5f, .01f, z + 2.8f), 0, 2.8f, "Cafe parasol " + i);
                Box("Terrace planter", new Vector3(39, .30f, z + 6.8f), new Vector3(.7f, .65f, 1.8f), accent);
                Box("Planter greenery", new Vector3(39, .7f, z + 6.8f), new Vector3(.65f, .3f, 1.65f), leaf);
            }

            Transform palm = CreatePalm(timber, leaf);
            for (int i = 0; i < 11; i++)
            {
                Transform instance = i == 0 ? palm : UnityEngine.Object.Instantiate(palm);
                instance.name = "Promenade palm " + i;
                instance.position = new Vector3(58.5f, .005f, -80 + i * 16);
                instance.rotation = Quaternion.Euler(0, i * 53, 0);
                instance.localScale = Vector3.one * (.92f + (i % 3) * .035f);
                CoastalProps.Add(instance.gameObject);
                if (i % 2 == 0)
                    AddCoastalProp(Bench, new Vector3(56.7f, .01f, -73 + i * 16), 90, 2.2f, "Ocean-facing bench " + i);
            }

            // Quiet inland return, grouped planting instead of repetitive towers on a lawn.
            foreach (Vector3 centre in new[] { new Vector3(-24, 0, -56), new Vector3(-16, 0, 6), new Vector3(-23, 0, 63) })
                for (int j = 0; j < 3; j++)
                    AddCoastalProp(Tree, centre + new Vector3((j % 2) * 6, -.14f, j * 8), j * 63, 5.5f, "Inland grove");
            for (int i = 0; i < 4; i++)
                AddCoastalProp(Bench, new Vector3(-58.8f, -.14f, -57 + 38 * i), -90, 2.2f, "Return road lookout");

            // Curbs are continuous and remain outside the full 10 m ride corridor.
            Strip("Inner limestone curb", -5.24f, -5.03f, .09f, 0, CapeCrownRoute.Length, stone);
            Strip("Outer limestone curb", 5.03f, 5.24f, .09f, 0, CapeCrownRoute.Length, stone);
        }

        private static float ShoreX(float z) => 107 + 7 * Mathf.Cos(z * Mathf.PI / 340);

        private static void CoastalSky()
        {
            Shader shader = Shader.Find("Skybox/Procedural");
            if (shader == null) throw new InvalidOperationException("Procedural sky shader unavailable.");
            var sky = new Material(shader) { name = "Camps Bay afternoon sky" };
            sky.SetFloat("_AtmosphereThickness", .8f);
            sky.SetFloat("_Exposure", 1.05f);
            sky.SetFloat("_SunSize", .025f);
            sky.SetFloat("_SunDisk", 1);
            sky.EnableKeyword("_SUNDISK_SIMPLE");
            sky.SetColor("_SkyTint", new Color(.5f, .5f, .5f));
            sky.SetColor("_GroundColor", new Color(.47f, .53f, .57f));
            RenderSettings.skybox = Save(sky);
            RenderSettings.fogColor = new Color(.58f, .72f, .80f);
        }

        private static void AddCoastalProp(string path, Vector3 position, float yaw, float size, string name)
        { CoastalProps.Add(GroundPrefab(path, position, yaw, size, name)); }

        private static void CoastRibbon(string name, Func<float, float> left, Func<float, float> right,
            float leftHeight, float rightHeight, Material material)
        {
            const int count = 180;
            var vertices = new Vector3[(count + 1) * 2];
            var triangles = new int[count * 6];
            for (int i = 0; i <= count; i++)
            {
                float z = Mathf.Lerp(-600, 600, i / (float)count);
                vertices[2 * i] = new Vector3(left(z), leftHeight, z);
                vertices[2 * i + 1] = new Vector3(right(z), rightHeight, z);
                if (i == count) continue;
                int a = 2 * i, t = 6 * i;
                triangles[t] = a; triangles[t + 1] = a + 2; triangles[t + 2] = a + 1;
                triangles[t + 3] = a + 1; triangles[t + 4] = a + 2; triangles[t + 5] = a + 3;
            }
            MeshObject(name, null, vertices, triangles, material);
        }

        private static void MountainBackdrop(Material material)
        {
            // Faceted sandstone ridge to suggest the Twelve Apostles, well beyond the ride corridor.
            float[] xs = { -88, -130, -190, -228, -300, -430 };
            float[] profile = { -.15f, 9, 70, 73, 31, 1 };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int row = 0; row < 40; row++)
                for (int col = 0; col < xs.Length - 1; col++)
                {
                    Vector3 a = RidgePoint(row, col, xs, profile), b = RidgePoint(row + 1, col, xs, profile);
                    Vector3 c = RidgePoint(row, col + 1, xs, profile), d = RidgePoint(row + 1, col + 1, xs, profile);
                    // Duplicated vertices give the broad flat facets their own normals.
                    foreach (Vector3 v in new[] { a, c, b, b, c, d }) { triangles.Add(vertices.Count); vertices.Add(v); }
                }
            MeshObject("Twelve Apostles inspired ridge", null, vertices.ToArray(), triangles.ToArray(), material);
        }

        private static Vector3 RidgePoint(int row, int col, float[] xs, float[] profile)
        {
            float height = profile[col];
            if (col > 0) height *= .83f + .30f * Mathf.Abs(Mathf.Sin(row * 1.13f)) + .12f * Mathf.Sin(row * .32f);
            return new Vector3(xs[col], height, -440 + row * 22);
        }

        private static Transform CreatePalm(Material trunk, Material leaves)
        {
            Transform root = new GameObject("Coastal palm").transform;
            for (int i = 0; i < 7; i++)
                Tube(root, new Vector3(.012f * i * i, i, 0), new Vector3(.012f * (i + 1) * (i + 1), i + 1, 0), .14f - i * .008f, trunk);
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            Vector3 crown = new Vector3(.588f, 7, 0);
            for (int leaf = 0; leaf < 9; leaf++)
            {
                float angle = leaf * Mathf.PI * 2 / 9;
                Vector3 outwards = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 across = Vector3.Cross(Vector3.up, outwards);
                for (int segment = 0; segment < 5; segment++)
                {
                    float t = segment / 5f, n = (segment + 1) / 5f;
                    Vector3 a = crown + outwards * (t * 2.7f) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * .45f - t * t * 1.3f);
                    Vector3 b = crown + outwards * (n * 2.7f) + Vector3.up * (Mathf.Sin(n * Mathf.PI) * .45f - n * n * 1.3f);
                    float w = .42f * Mathf.Sin((t * .85f + .12f) * Mathf.PI), nw = n == 1 ? 0 : .42f * Mathf.Sin((n * .85f + .12f) * Mathf.PI);
                    int v = vertices.Count;
                    Vector3[] face = { a - across * w, a + across * w, b - across * nw, b + across * nw };
                    vertices.AddRange(face); vertices.AddRange(face);
                    // Separate back-face vertices prevent cancelling lighting normals.
                    indices.AddRange(new[] { v, v + 2, v + 1, v + 1, v + 2, v + 3,
                        v + 5, v + 6, v + 4, v + 7, v + 6, v + 5 });
                }
            }
            MeshObject("Palm fronds", root, vertices.ToArray(), indices.ToArray(), leaves);
            BatchParts(root, null);
            return root;
        }

        private static void MeshObject(string name, Transform parent, Vector3[] vertices, int[] triangles, Material material)
        {
            Mesh mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh = Save(mesh);
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void ValidateCampsBayPlacement()
        {
            foreach (GameObject go in CoastalProps)
            {
                Bounds b = BoundsOf(go);
                // Test conservative world-space footprints along the entire route, including bends.
                for (float d = 0; d < CapeCrownRoute.Length; d += .5f)
                {
                    CapeCrownRoute.Sample(d, out Vector3 point, out _);
                    float dx = Mathf.Max(b.min.x - point.x, 0, point.x - b.max.x);
                    float dz = Mathf.Max(b.min.z - point.z, 0, point.z - b.max.z);
                    if (dx * dx + dz * dz < 5.3f * 5.3f)
                        throw new InvalidOperationException(go.name + " intrudes into the road corridor.");
                }
            }
            for (int i = 0; i < CoastalShops.Count; i++)
            {
                Bounds a = BoundsOf(CoastalShops[i]);
                if (a.max.x > 33.51f || a.size.y > 8.01f || Mathf.Abs(a.min.y + .14f) > .01f)
                    throw new InvalidOperationException("Invalid frontage alignment: " + CoastalShops[i].name);
                for (int j = i + 1; j < CoastalShops.Count; j++)
                    if (a.Intersects(BoundsOf(CoastalShops[j])))
                        throw new InvalidOperationException("Overlapping cafe buildings.");
            }
            Debug.Log("Camps Bay placement PASS: grounded low-rise frontage, no building overlaps, full-lap road clearance.");
        }
    }
}
