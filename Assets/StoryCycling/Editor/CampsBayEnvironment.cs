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
            float half = StadiumHalf;
            Box("Beach promenade extension", new Vector3(58, -.035f, 0), new Vector3(4, .08f, 2*half+8), stone);
            Box("Cafe terrace ribbon", new Vector3(38.4f, -.04f, 0), new Vector3(5.2f, .1f, 2*half+6), stone);
            Color[] facades = {
                new Color(.96f,.72f,.35f),
                new Color(.88f,.42f,.32f),
                new Color(.35f,.68f,.74f),
                new Color(.80f,.62f,.82f),
                new Color(.58f,.78f,.62f),
                new Color(.91f,.55f,.48f),
                new Color(.48f,.64f,.82f)
            };
            Material glass = Mat("Coastal blue glazing",new Color(.13f,.31f,.36f));
            Material trim = Mat("Warm white joinery",new Color(.94f,.91f,.82f));
            Material rail = Mat("Balcony bronze",new Color(.22f,.27f,.25f));
            int cafeCount = Mathf.Max(3, Mathf.FloorToInt((2f * half - 36f) / 18f) + 1);
            float cafeStart = -(half - 18f);
            for (int i = 0; i < cafeCount; i++)
            {
                float z = cafeStart + 18 * i;
                if (Mathf.Abs(z + 65) < 19 || Mathf.Abs(z - 70) < 19) continue; // Leave side streets open.
                GameObject shop = GroundPrefab(CampsBayShops[i % CampsBayShops.Length], Vector3.zero, 90, 18f, "Cafe ground floor " + i);
                Bounds b = BoundsOf(shop);
                // Set human-scale height explicitly. Old code only shrank already-small 3 m shops.
                shop.transform.localScale *= Mathf.Min(4.3f / b.size.y,16f / b.size.z);
                b = BoundsOf(shop);
                shop.transform.position += new Vector3(37.2f - b.max.x, -.14f - b.min.y, z - b.center.z);
                b=BoundsOf(shop);
                Transform building = new GameObject("Promenade villa " + i).transform;
                shop.transform.SetParent(building,true);
                Material plaster=Mat("Villa plaster "+i,facades[i%facades.Length]);
                float[] heights = {4.4f, 2.8f, 5.8f, 3.6f};
                float upperHeight=heights[i%heights.Length];
                float roof=b.max.y+upperHeight;
                Part(building,PrimitiveType.Cube,new Vector3(b.center.x,b.max.y+upperHeight/2,z),new Vector3(b.size.x,upperHeight,b.size.z),plaster);
                Part(building,PrimitiveType.Cube,new Vector3(b.center.x,roof+.18f,z),new Vector3(b.size.x+.3f,.36f,b.size.z+.3f),trim);
                int bays=Mathf.Max(2,Mathf.FloorToInt(b.size.z/3));
                for(int floor=0;floor<(upperHeight>4?2:1);floor++)
                for(int bay=0;bay<bays;bay++)
                {
                    float wz=z+(bay-(bays-1)*.5f)*(b.size.z/bays);
                    float wy=b.max.y+1.65f+floor*2.6f;
                    Part(building,PrimitiveType.Cube,new Vector3(37.26f,wy,wz),new Vector3(.14f,2.05f,2.05f),trim);
                    Part(building,PrimitiveType.Cube,new Vector3(37.35f,wy,wz),new Vector3(.07f,1.75f,1.7f),glass);
                    Part(building,PrimitiveType.Cube,new Vector3(37.41f,wy,wz),new Vector3(.07f,1.8f,.075f),trim);
                }
                Part(building,PrimitiveType.Cube,new Vector3(37.7f,b.max.y+.05f,z),new Vector3(1.25f,.16f,b.size.z),trim);
                Part(building,PrimitiveType.Cube,new Vector3(38.22f,b.max.y+1.05f,z),new Vector3(.06f,.08f,b.size.z),rail);
                for(float bz=z-b.size.z/2;bz<=z+b.size.z/2;bz+=1.25f)
                    Part(building,PrimitiveType.Cube,new Vector3(38.22f,b.max.y+.55f,bz),new Vector3(.055f,1,.055f),rail);
                // End facade gets windows as well: no blank warehouse wall facing the arriving rider.
                for(int w=0;w<2;w++) {
                    float x=b.center.x+(w==0?-1.5f:1.5f);
                    Part(building,PrimitiveType.Cube,new Vector3(x,b.max.y+1.65f,b.min.z-.06f),new Vector3(1.7f,2.05f,.12f),trim);
                    Part(building,PrimitiveType.Cube,new Vector3(x,b.max.y+1.65f,b.min.z-.14f),new Vector3(1.4f,1.75f,.06f),glass);
                }
                CoastalShops.Add(building.gameObject);CoastalProps.Add(building.gameObject);
                AddCoastalProp(CafeTable,new Vector3(39.8f,.01f,z-2.6f),90,2.5f,"Cafe terrace table "+i);
                Box("Terrace planter",new Vector3(40.5f,.30f,z+6.8f),new Vector3(.65f,.65f,1.8f),accent);
                Box("Planter greenery",new Vector3(40.5f,.7f,z+6.8f),new Vector3(.6f,.3f,1.65f),leaf);
            }

            Transform palm = CreatePalm(timber, leaf);
            int palmCount = Mathf.Max(3, Mathf.FloorToInt((2f * half - 32f) / 16f) + 1);
            float palmStart = -(half - 16f);
            for (int i = 0; i < palmCount; i++)
            {
                Transform instance = i == 0 ? palm : UnityEngine.Object.Instantiate(palm);
                instance.name = "Promenade palm " + i;
                instance.position = new Vector3(62f, .005f, palmStart + i * 16);
                instance.rotation = Quaternion.Euler(0, i * 53, 0);
                instance.localScale = Vector3.one * (.92f + (i % 3) * .035f);
                CoastalProps.Add(instance.gameObject);
                if (i % 2 == 0)
                    AddCoastalProp(Bench, new Vector3(60.2f, .01f, palmStart + 7 + i * 16), 90, 2.2f, "Ocean-facing bench " + i);
            }

            // Quiet inland return: continuous planting along the longer straight.
            for (int g = 0; g < 6; g++)
            {
                float cz = -(half - 18) + g * (2 * (half - 18) / 5f);
                for (int j = 0; j < 3; j++)
                {
                    Vector3 p = new Vector3(-22 + (g % 2) * 8, -.14f, cz) + new Vector3((j % 2) * 6, 0, j * 8);
                    AddCoastalProp(Tree, p, g * 37 + j * 63, 5.5f, "Inland grove");
                }
            }
            for (int i = 0; i < 6; i++)
                {
                    float d = 520 + i * 58;
                    Vector3 p = CapeCrownRoute.Position(d, 10.8f, -0.12f, relief);
                    AddCoastalProp(Bench, p, -90, 2.2f, "Return road lookout");
                }

            // Curbs are continuous and remain outside the full 10 m ride corridor.
            Strip("Inner limestone curb", -4.24f, -4.03f, .09f, 0, CapeCrownRoute.Length, stone);
            Strip("Outer limestone curb", 4.03f, 4.24f, .09f, 0, CapeCrownRoute.Length, stone);
        }

        private static float ShoreX(float z) => 107 + 7 * Mathf.Cos(z * Mathf.PI / 340);

        private static void BuildCampsBayStreetLife()
        {
            // Beach promenade runs along the flat straight (route distance ~0–350 m),
            // ocean on the +offset side, cafe terrace on the -offset side. relief=0 here,
            // so GroundPrefab/ArtFrame place everything on the promenade surface.
            const string P = Root;
            string cone = P + "Props/SM_Prop_Cone_01.prefab";
            string cone2 = P + "Props/SM_Prop_Cone_02.prefab";
            string barrier = P + "Props/SM_Prop_Barrier_01.prefab";
            string skip = P + "Props/SM_Prop_Skip_01.prefab";
            string pallet = P + "Props/SM_Prop_Pallet_01.prefab";
            string policeCar = P + "Vehicles/SM_Veh_Car_Police_01.prefab";
            string ambulance = P + "Vehicles/SM_Veh_Car_Ambo_01.prefab";
            string policeOfficer = P + "Characters/Character_Male_Police.prefab";
            string sedan = P + "Vehicles/SM_Veh_Car_Sedan_01.prefab";
            string smallCar = P + "Vehicles/SM_Veh_Car_Small_01.prefab";
            string giveWay = P + "Props/SM_Prop_Sign_GiveWay_01.prefab";
            string stopSign = P + "Props/SM_Prop_Sign_Stop_01.prefab";
            string warning = P + "Props/SM_Prop_Sign_Warning_01.prefab";
            string billboard = P + "Props/SM_Prop_Billboard_01.prefab";
            string flower = P + "Environments/SM_Env_Flower_01.prefab";
            string planter = P + "Props/SM_Prop_Planter_01.prefab";
            string potPlant = P + "Props/SM_Prop_PotPlant_01.prefab";
            string hydrant = P + "Props/SM_Prop_Hydrant_01.prefab";
            string parkingMeter = P + "Props/SM_Prop_ParkingMeter_01.prefab";
            string trashCan = P + "Props/SM_Prop_TrashCan_01.prefab";

            // 1) Street lamps: one at each intersection corner + spaced along the promenade.
            ArtLamp(110, -7f); ArtLamp(245, -7f);
            for (float d = 40; d < 340; d += 46) if (!AtJunction(d)) ArtLamp(d, 7f);

            // 2) Road signs at each side-street mouth — face the road (perpendicular to travel).
            ArtProp(giveWay, 96, -7.5f, 2f, "Give way sign", 90);
            ArtProp(stopSign, 124, 7.5f, 2f, "Stop sign", 270);
            ArtProp(giveWay, 231, -7.5f, 2f, "Give way sign", 90);
            ArtProp(stopSign, 259, 7.5f, 2f, "Stop sign", 270);

            // 3) Construction site (Baustelle) on the road shoulder.
            ArtProp(warning, 302, 7f, 2f, "Roadworks warning", 0);
            ArtProp(barrier, 312, 6.5f, 4f, "Roadworks barrier", 90);
            ArtProp(cone, 306, 5.5f, 1.2f, "Roadworks cone", 0);
            ArtProp(cone2, 318, 5.5f, 1.2f, "Roadworks cone", 0);
            ArtProp(skip, 322, 6.5f, 3.5f, "Roadworks skip", 0);
            ArtProp(pallet, 314, 8f, 2.5f, "Roadworks pallet", 45);

            // 4) Police scene: patrol car, officer, cone.
            ArtProp(policeCar, 56, 7f, 4.6f, "Police patrol car", 0);
            ArtProp(policeOfficer, 50, 9f, 2f, "Police officer", 180);
            ArtProp(cone, 44, 7f, 1.2f, "Police cone", 0);

            // 5) Accident scene: two cars, cones, police, ambulance.
            ArtProp(sedan, 178, 6.5f, 4.6f, "Crashed sedan", 25);
            ArtProp(smallCar, 186, 8f, 4.6f, "Second car", -30);
            ArtProp(cone, 170, 6f, 1.2f, "Accident cone", 0);
            ArtProp(cone, 194, 6f, 1.2f, "Accident cone", 0);
            ArtProp(policeCar, 168, 9f, 4.6f, "Incident police", 0);
            ArtProp(ambulance, 197, 9f, 4.6f, "Ambulance", 0);

            // 6) Billboards facing the road.
            ArtProp(billboard, 132, 11.5f, 5f, "Promenade billboard", 90);
            ArtProp(billboard, 268, 11.5f, 5f, "Promenade billboard", 90);

            // 7) Street clutter + greenery along the promenade for life.
            for (float d = 30; d < 340; d += 22)
            {
                if (AtJunction(d, 12)) continue;
                if (d > 160 && d < 200) continue;   // keep accident scene readable
                if (d > 300) continue;              // keep construction readable
                float off = 8.5f + ((int)d % 3) * 1.2f;
                switch ((int)(d / 22) % 6)
                {
                    case 0: ArtProp(flower, d, off, 1.5f, "Wayside flowers", 0); break;
                    case 1: ArtProp(planter, d, off, 2f, "Promenade planter", 0); break;
                    case 2: ArtProp(potPlant, d, off, 1.5f, "Potted plant", 0); break;
                    case 3: ArtProp(hydrant, d, off, 1.2f, "Fire hydrant", 0); break;
                    case 4: ArtProp(parkingMeter, d, off, 1.2f, "Parking meter", 0); break;
                    default: ArtProp(trashCan, d, off, 1.2f, "Trash can", 0); break;
                }
            }
        }

        private static void BuildCampsBayVegetation()
        {
            // Dense inland fynbos behind the cafe frontage on the flat promenade straight,
            // so the eye never hits empty ground between the buildings and the mountain.
            // Seeded, so the exact placement is reproducible on every rebuild.
            ScatterVegetation(20260921, 0f, 340f, -28f, -52f, 2.2f, 420, -.15f);
            ScatterVegetation(20260922, 0f, 340f, 12f, 24f, 4.5f, 90, -.12f);
            BuildCampsBayVillas();
            ScatterClouds();
        }

        private static void BuildCampsBayVillas()
        {
            // Two rows of larger villas behind the beach-front cafes (inland side, flat straight),
            // so the eye never hits empty ground between the front and the mountain.
            // The hill back-straight stays natural: villas there would float on the sloping
            // embankment, so it gets fynbos + the viewpoint instead.
            for (float d = 16; d < 338; d += 18)
            {
                if (AtJunction(d, 28)) continue;
                ArtVilla(d, -1, (int)(d / 18), 15f, 22f);          // first villa row
                ArtVilla(d + 9, -1, 6 + (int)(d / 18), 18f, 40f);  // second, larger villa row
            }
        }

        private static void ScatterClouds()
        {
            string[] clouds = {
                Root + "Environments/SM_Env_Cloud_01.prefab",
                Root + "Environments/SM_Env_Cloud_02.prefab",
                Root + "Environments/SM_Env_Cloud_03.prefab"
            };
            var rng = new System.Random(7312026);
            for (int i = 0; i < 16; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(clouds[i % clouds.Length]));
                go.name = "Drifting cloud " + i;
                go.transform.position = new Vector3(-220f + rng.Next(440), 110f + rng.Next(50), -380f + rng.Next(760));
                go.transform.rotation = Quaternion.Euler(0f, rng.Next(360), 0f);
                go.transform.localScale = Vector3.one * (16f + rng.Next(18));
                foreach (Collider c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            }
        }

        private static void CoastalSky()
        {
            Shader shader = Shader.Find("CapeCrown/CoastalSky");
            if(shader==null)throw new InvalidOperationException("Coastal sky shader missing");
            var sky=new Material(shader){name="Camps Bay golden evening"};
            sky.SetColor("_Zenith",new Color(.28f,.61f,.80f));
            sky.SetColor("_Horizon",new Color(.98f,.85f,.73f));
            sky.SetVector("_SunDirection",new Vector4(.82f,.27f,.51f,0));
            RenderSettings.skybox=Save(sky);
            RenderSettings.fogColor=new Color(.88f,.83f,.71f);
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
            float[] xs = relief > 0
                ? new[] { -88f, -118f, -155f, -177f, -192f, -212f, -240f, -290f, -365f, -460f }
                : new[] { -88f, -130f, -190f, -228f, -300f, -430f };
            float[] profile = relief > 0
                ? new[] { -.15f, 3f, 16f, 28f, 46f, 49f, 42f, 26f, 12f, 1f }
                : new[] { -.15f, 9f, 70f, 73f, 31f, 1f };
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
            if (col > 0) height *= relief > 0
                ? .68f + .34f * Mathf.Abs(Mathf.Sin(row * .36f)) + .11f * Mathf.Sin(row * 1.17f + col*.6f)
                : .83f + .30f * Mathf.Abs(Mathf.Sin(row * 1.13f)) + .12f * Mathf.Sin(row * .32f);
            return new Vector3(xs[col] + (relief > 0 && col > 0 ? Mathf.Sin(row*.8f+col)*7 : 0), height, -440 + row * 22);
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
                if (a.max.x > 38.4f || a.size.y > 10.8f || a.size.y < 6f || Mathf.Abs(a.min.y + .14f) > .01f)
                    throw new InvalidOperationException("Invalid frontage alignment: " + CoastalShops[i].name);
                for (int j = i + 1; j < CoastalShops.Count; j++)
                    if (a.Intersects(BoundsOf(CoastalShops[j])))
                        throw new InvalidOperationException("Overlapping cafe buildings.");
            }
            Debug.Log("Camps Bay placement PASS: grounded low-rise frontage, no building overlaps, full-lap road clearance.");
        }
    }
}
