using System;
using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.Editor
{
    // Reference-led, original low-poly landmark silhouettes. No downloaded photos ship in the game.
    public static partial class CapeCrownSceneBuilder
    {
        private static readonly Dictionary<string, Material> ArtMaterials = new Dictionary<string, Material>();
        private static readonly List<float> JunctionDistances = new List<float>();
        private static Material Art(string name, Color color)
        {
            if (!ArtMaterials.TryGetValue(name, out var material)) ArtMaterials[name] = material = Mat(name, color);
            return material;
        }
        private static Material Ivory => Art("Limestone ivory", new Color(.90f,.86f,.74f));
        private static Material Ink => Art("Coastal iron", new Color(.09f,.15f,.17f));
        private static Material Leaf => Art("Cape evergreen", new Color(.24f,.39f,.23f));
        private static Material Terra => Art("Weathered sandstone", new Color(.57f,.43f,.31f));
        private static Material Sea => Art("Deep Atlantic", new Color(.06f,.30f,.42f));
        private static Material Glazing => Art("Window blue",new Color(.19f,.37f,.42f));
        private static Material RoadSurface => Art("Junction asphalt",new Color(.12f,.15f,.18f));
        private static void BeginArt() { ArtMaterials.Clear(); JunctionDistances.Clear(); }
        private static Transform ArtFrame(string name,float distance,float offset)
        {
            CapeCrownRoute.Sample(distance,out var p,out var forward,relief);
            var t=new GameObject(name).transform;
            var flat=new Vector3(forward.x,0,forward.z).normalized;
            t.SetPositionAndRotation(p+Vector3.Cross(Vector3.up,flat)*offset,Quaternion.LookRotation(flat));
            return t;
        }
        private static void ABox(Transform t,string name,Vector3 p,Vector3 size,Material mat)
        { Part(t,PrimitiveType.Cube,p,size,mat).name=name; }
        private static void FinishArt(Transform t) { BatchParts(t,null); }
        private static bool AtJunction(float d,float margin=17)
        {
            foreach(float junction in JunctionDistances)
                if(Mathf.Abs(Mathf.DeltaAngle(d/CapeCrownRoute.Length*360,junction/CapeCrownRoute.Length*360))*CapeCrownRoute.Length/360<margin)return true;
            return false;
        }
        // Raised road and roadside share the route elevation. Land slopes away, never hanging asphalt.
        private static void SupportedVerge(Material ground,Material walk)
        {
            Strip("Left walkable verge",-8,-4,.005f,0,CapeCrownRoute.Length,walk);
            Strip("Right walkable verge",4,8,.005f,0,CapeCrownRoute.Length,walk);
            foreach(int side in new[]{-1,1})
            {
                int count=Mathf.CeilToInt(CapeCrownRoute.Length/4);
                var verts=new Vector3[(count+1)*3];var tris=new List<int>();
                for(int i=0;i<=count;i++)
                {
                    float d=CapeCrownRoute.Length*i/count;
                    for(int j=0;j<3;j++)
                    {
                        var p=CapeCrownRoute.Position(d,side*(8+j*18),-.05f,relief);
                        // The 26 m shoulder stays at route height for grounded buildings/landmarks.
                        if(j==2)p.y=-.16f;
                        verts[i*3+j]=p;
                    }
                    if(i==count)continue;
                    for(int j=0;j<2;j++) { int a=i*3+j;
                        if(side>0)tris.AddRange(new[]{a,a+3,a+1,a+1,a+3,a+4});
                        else tris.AddRange(new[]{a,a+1,a+3,a+1,a+4,a+3});
                    }
                }
                MeshObject("Route-supported landscape "+side,null,verts,tris.ToArray(),ground);
            }
        }
        // Actual connected lateral roads, not little decals hidden underneath a continuous sidewalk.
        private static void ArtJunction(float distance,int side,bool signals,string name)
        {
            JunctionDistances.Add(distance);
            var t=ArtFrame(name,distance,0);
            ABox(t,"Connected side street",new Vector3(side*25,.06f,0),new Vector3(42,.07f,8),RoadSurface);
            ABox(t,"Road foundation",new Vector3(side*25,-.55f,0),new Vector3(44,1,8),Terra);
            for(int edge=-1;edge<=1;edge+=2)
                ABox(t,"Side street pavement",new Vector3(side*27,.1f,edge*5.1f),new Vector3(40,.14f,2.2f),Ivory);
            // Crossing across the side street mouth, away from the cycling corridor.
            for(int z=-3;z<=3;z++)ABox(t,"Zebra crossing",new Vector3(side*11,.108f,z),new Vector3(3,.015f,.45f),Ivory);
            for(int x=17;x<45;x+=6)ABox(t,"Lane dash",new Vector3(side*x,.103f,0),new Vector3(2.8f,.015f,.1f),Ivory);
            ABox(t,"Side road stop line",new Vector3(side*15,.11f,2),new Vector3(.22f,.018f,3.5f),Ivory);
            if(signals)
            {
                foreach(int edge in new[]{-1,1})
                {
                    float x=side*8.9f,z=edge*6.2f;
                    ABox(t,"Traffic signal pole",new Vector3(x,1.65f,z),new Vector3(.13f,3.3f,.13f),Ink);
                    ABox(t,"Signal housing",new Vector3(x,3.1f,z),new Vector3(.45f,1.15f,.30f),Ink);
                    for(int lamp=0;lamp<3;lamp++)
                    {
                        var color=lamp==2?new Color(.18f,.85f,.45f):new Color(.25f,.15f,.12f);
                        var sphere=Part(t,PrimitiveType.Sphere,new Vector3(x,3.45f-lamp*.34f,z-.17f),new Vector3(.23f,.23f,.06f),Art("Signal lens "+lamp,color));
                        sphere.name=lamp==2?"Green route signal":"Unlit signal lens";
                    }
                }
            }
            StreetName(t,name,new Vector3(side*9,2.3f,-7));
            FinishArt(t);
        }
        private static void StreetName(Transform parent,string title,Vector3 p)
        {
            ABox(parent,"Sign post",p/1f+Vector3.down*.9f,new Vector3(.075f,2.6f,.075f),Ink);
            ABox(parent,"Street name plate",p,new Vector3(3.4f,.48f,.10f),Art("Street sign green",new Color(.08f,.31f,.29f)));
            var go=new GameObject(title,typeof(TextMesh));go.transform.SetParent(parent,false);go.transform.localPosition=p+Vector3.back*.065f;
            var tx=go.GetComponent<TextMesh>();tx.text=title;tx.fontSize=48;tx.characterSize=.055f;tx.anchor=TextAnchor.MiddleCenter;tx.color=Color.white;
            go.transform.localRotation=Quaternion.identity;
        }
        private static void ArtLamp(float d,float offset)
        {
            var t=ArtFrame("Promenade lamp",d,offset);
            ABox(t,"Lamp post",new Vector3(0,2.8f,0),new Vector3(.12f,5.6f,.12f),Ink);
            ABox(t,"Lamp arm",new Vector3(-Mathf.Sign(offset)*.55f,5.6f,0),new Vector3(1.2f,.12f,.12f),Ink);
            ABox(t,"Warm lamp",new Vector3(-Mathf.Sign(offset)*1.1f,5.53f,0),new Vector3(.48f,.13f,.34f),Ivory);FinishArt(t);
        }
        private static void ShadeTree(float d,float offset,float height)
        {
            CapeCrownRoute.Sample(d,out var p,out var forward,relief);
            p+=Vector3.Cross(Vector3.up,new Vector3(forward.x,0,forward.z).normalized)*offset;
            var go=GroundPrefab(Tree,p,d,12,"Cape shade tree");
            var bounds=BoundsOf(go);
            go.transform.localScale*=height/Mathf.Max(.1f,bounds.size.y);
            bounds=BoundsOf(go);go.transform.position+=p-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
        }
        private static void RoadsideGarden(float d,int side,bool flowers)
        {
            if(AtJunction(d,15))return;
            var t=ArtFrame(flowers?"Flower border":"Low roadside planting",d,side*10);
            ABox(t,"Planting bed",new Vector3(0,.18f,0),new Vector3(1.8f,.36f,6),Terra);
            for(int i=0;i<4;i++)
            {
                var bush=Part(t,PrimitiveType.Sphere,new Vector3(0,.65f,(i-1.5f)*1.4f),new Vector3(1.3f,1,1.5f),Leaf);
                if(flowers)Part(t,PrimitiveType.Sphere,new Vector3(.25f,1.08f,(i-1.5f)*1.4f),new Vector3(.45f,.32f,.5f),Art("Protea pink",new Color(.74f,.37f,.45f)));
            }
            FinishArt(t);
        }
        private static void ArtProp(string path,float d,float offset,float footprint,string name,float yaw=0)
        {
            CapeCrownRoute.Sample(d,out var p,out var forward,relief);
            var flat=new Vector3(forward.x,0,forward.z).normalized;
            p+=Vector3.Cross(Vector3.up,flat)*offset;
            GroundPrefab(path,p,Mathf.Atan2(flat.x,flat.z)*Mathf.Rad2Deg+yaw,footprint,name);
        }

        // Reusable vegetation pools drawn from the installed Synty packs.
        private const string GenEnv = "Assets/Synty/PolygonGeneric/Prefabs/Environment/";
        private static readonly string[] ScatterShrubs = {
            GenEnv + "SM_Gen_Env_Bush_01.prefab", GenEnv + "SM_Gen_Env_Bush_02.prefab",
            GenEnv + "SM_Gen_Env_Bush_03.prefab", GenEnv + "SM_Gen_Env_Bush_04.prefab",
            GenEnv + "SM_Gen_Env_Fern_01.prefab", GenEnv + "SM_Gen_Env_Fern_02.prefab",
            GenEnv + "SM_Gen_Env_Fern_03.prefab", GenEnv + "SM_Gen_Env_Shrub_01.prefab",
            GenEnv + "SM_Gen_Env_Shrub_02.prefab", GenEnv + "SM_Gen_Env_Shrub_03.prefab",
            GenEnv + "SM_Gen_Env_Grass_Tall_01.prefab", GenEnv + "SM_Gen_Env_Grass_Tall_02.prefab",
            GenEnv + "SM_Gen_Env_Grass_Tall_03.prefab", GenEnv + "SM_Gen_Env_Grass_Tall_04.prefab"
        };
        private static readonly string[] ScatterBloom = {
            GenEnv + "SM_Gen_Env_Flowers_01.prefab", GenEnv + "SM_Gen_Env_Flowers_02.prefab",
            GenEnv + "SM_Gen_Env_Flowers_03.prefab", GenEnv + "SM_Gen_Env_Flowers_04.prefab",
            GenEnv + "SM_Gen_Env_Flowers_05.prefab", GenEnv + "SM_Gen_Env_Flowers_06.prefab",
            GenEnv + "SM_Gen_Env_Flowers_07.prefab", GenEnv + "SM_Gen_Env_Flowers_08.prefab"
        };
        private static readonly string[] ScatterCanopy = {
            GenEnv + "SM_Gen_Env_Tree_01.prefab", GenEnv + "SM_Gen_Env_Tree_02.prefab",
            GenEnv + "SM_Gen_Env_Tree_03.prefab", GenEnv + "SM_Gen_Env_Bush_Large_01.prefab",
            GenEnv + "SM_Gen_Env_Bush_Large_02.prefab", GenEnv + "SM_Gen_Env_Bush_Large_03.prefab",
            GenEnv + "SM_Gen_Env_Bush_Large_04.prefab"
        };

        // Larger coastal villas/apartment blocks for the second row behind the street front.
        private static readonly string[] VillaPool = {
            Root + "Buildings/SM_Bld_Apartment_01.prefab",
            Root + "Buildings/SM_Bld_Apartment_02.prefab",
            Root + "Buildings/SM_Bld_Apartment_03.prefab",
            Root + "Buildings/SM_Bld_Apartment_Stack_01.prefab",
            Root + "Buildings/SM_Bld_Apartment_Stack_02.prefab",
            Root + "Buildings/SM_Bld_Apartment_Stack_03.prefab",
            Root + "Buildings/SM_Bld_OfficeOld_Large_01.prefab",
            Root + "Buildings/SM_Bld_OfficeOld_Large_02.prefab"
        };

        // Seeded Poisson-disk scatter along a route-distance band. Reusable across all
        // routes: pass a band, density and ground offset; the same seed reproduces the
        // exact same placement, so a route is deterministic and cheap to iterate.
        private static void ScatterVegetation(int seed, float dFrom, float dTo, float offMin,
            float offMax, float minDist, int count, float yOffset)
        {
            float span = dTo - dFrom;
            var rng = new System.Random(seed);
            var placed = new List<Vector2>();
            int tries = count * 30;
            for (int a = 0; a < tries && placed.Count < count; a++)
            {
                float d = dFrom + (float)rng.NextDouble() * span;
                float o = offMin + (float)rng.NextDouble() * (offMax - offMin);
                if (AtJunction(d, 24)) continue;   // keep side-street mouths clear
                bool ok = true;
                foreach (var q in placed)
                {
                    float dd = Mathf.Abs(d - q.x);
                    float dz = o - q.y;
                    if (dd * dd + dz * dz < minDist * minDist) { ok = false; break; }
                }
                if (!ok) continue;
                placed.Add(new Vector2(d, o));
                float roll = (float)rng.NextDouble();
                string path; float fp;
                if (roll < .5f) { path = ScatterShrubs[rng.Next(ScatterShrubs.Length)]; fp = 1.4f + (float)rng.NextDouble(); }
                else if (roll < .74f) { path = ScatterBloom[rng.Next(ScatterBloom.Length)]; fp = 1f + .6f * (float)rng.NextDouble(); }
                else { path = ScatterCanopy[rng.Next(ScatterCanopy.Length)]; fp = 4.2f + 2.6f * (float)rng.NextDouble(); }
                Vector3 pos = CapeCrownRoute.Position(d, o, 0, relief);
                pos.y += yOffset;
                GroundPrefab(path, pos, (float)rng.NextDouble() * 360f, fp, "Scattered vegetation");
            }
        }

        // A larger villa/apartment block as a second row behind the street front, facing the road.
        private static void ArtVilla(float d, int side, int variant, float footprint, float offsetMul = 20f)
        {
            if (AtJunction(d, 26)) return;
            ArtProp(VillaPool[variant % VillaPool.Length], d, side * offsetMul, footprint, "Coastal villa " + variant, side > 0 ? 270 : 90);
            var t = ArtFrame("Villa forecourt", d, side * offsetMul);
            ABox(t, "Villa foundation", new Vector3(0, -.25f, 0), new Vector3(footprint + 2, .5f, footprint + 2), Ivory); FinishArt(t);
        }
        private static void ArtBuilding(float d,int side,int variant,float footprint=13)
        {
            if(AtJunction(d,23))return;
            // Forecourt provides a real foundation at road height, and keeps the walking corridor open.
            float offset=side*21;
            ArtProp(GenericBuildings[variant%6],d,offset,footprint,"Neighbourhood shop "+variant,side>0?270:90);
            var t=ArtFrame("Shop forecourt",d,offset);
            ABox(t,"Shop foundation",new Vector3(0,-.25f,0),new Vector3(footprint+2,.5f,footprint+2),Ivory);FinishArt(t);
        }
        private static void ArtParkedCar(float d,int side,int variant)
        {
            if(AtJunction(d,18))return;
            ArtProp(LifeCars[variant%LifeCars.Length],d,side*10.5f,4.5f,"Parked car",0);
            var t=ArtFrame("Parking bay",d,side*10.5f);
            ABox(t,"Parking surface",new Vector3(0,-.015f,0),new Vector3(4,.08f,6),RoadSurface);
            foreach(float z in new[]{-2.7f,2.7f})ABox(t,"Bay edge",new Vector3(0,.035f,z),new Vector3(3,.015f,.10f),Ivory);FinishArt(t);
        }
        private static void ArtOcean(float east,float half,string name)
        {
            Box(name,new Vector3(east+600,-1.4f,0),new Vector3(1200,.2f,half*2+1800),Sea);
            Box("Broad warm sand",new Vector3(east+12,-.33f,0),new Vector3(30,.35f,half*2+350),Art("Beach sand",new Color(.83f,.78f,.62f)));
            for(int i=0;i<3;i++)Box("Longshore surf",new Vector3(east+29+i*8,-1.22f,0),new Vector3(.6f,.025f,half*2+500),Ivory);
        }
        private static void ArtRidge(string name,Vector3 center,float width,float height,float depth,bool flat)
        {
            // Broad faceted geological silhouette, not stretched stock rocks or cuboid towers.
            var vertices=new List<Vector3>();var triangles=new List<int>();const int count=18;
            for(int i=0;i<count;i++)
            {
                float a=(i/(float)count-.5f)*width,b=((i+1)/(float)count-.5f)*width;
                float ha=height*(flat?.93f+.035f*Mathf.Sin(i*1.7f):.62f+.28f*Mathf.Abs(Mathf.Sin(i*.7f)));
                float hb=height*(flat?.93f+.035f*Mathf.Sin((i+1)*1.7f):.62f+.28f*Mathf.Abs(Mathf.Sin((i+1)*.7f)));
                Vector3[] quad={new Vector3(-depth*.5f,0,a),new Vector3(0,ha,a),new Vector3(-depth*.5f,0,b),new Vector3(0,hb,b),new Vector3(depth*.5f,0,a),new Vector3(depth*.5f,0,b)};
                foreach(int n in new[]{0,2,1,2,3,1,1,3,4,3,5,4}){triangles.Add(vertices.Count);vertices.Add(center+quad[n]);}
                if(i==0)foreach(int n in new[]{0,1,4}){triangles.Add(vertices.Count);vertices.Add(center+quad[n]);}
                if(i==count-1)foreach(int n in new[]{2,5,3}){triangles.Add(vertices.Count);vertices.Add(center+quad[n]);}
            }
            MeshObject(name,null,vertices.ToArray(),triangles.ToArray(),Terra);
        }
        private static void CoastalBoulders(float start,float end,float offset,float size)
        {
            int n=0;for(float d=start;d<end;d+=22){ArtProp(HillRock,d,offset+(n%3)*3,size+(n%3),"Weathered granite",n*43);n++;}
        }
        private static void BuildReferenceEnvironment(string sceneName,int theme)
        {
            BeginArt();float half=0,east=0;
            foreach(var p in CapeCrownRoute.Waypoints){half=Mathf.Max(half,Mathf.Abs(p.y));east=Mathf.Max(east,p.x);}
            bool coastal=sceneName=="CliftonCove"||sceneName=="ApostlesClimb"||sceneName=="HoutBayHarbour"||sceneName=="FalseBaySands"||sceneName=="CapePointHeadland";
            var ground=Art("Local ground",theme==3||theme==4?new Color(.38f,.49f,.29f):new Color(.52f,.54f,.37f));
            if(coastal)Box("Coastal mainland",new Vector3(-500,-.65f,0),new Vector3(1000+east+10,1,half*2+500),ground);
            else Box("Inland landscape",new Vector3(0,-.65f,0),new Vector3(1400,1,half*2+500),ground);
            SupportedVerge(ground,Art("Local pavement",new Color(.64f,.62f,.52f)));
            if(coastal)ArtOcean(east+27,half,sceneName=="HoutBayHarbour"?"Hout Bay harbour water":"Atlantic sea");
            float L=CapeCrownRoute.Length;
            // Low-density road junctions in towns; rural sections use access lanes, not urban signal forests.
            bool urban=sceneName=="HoutBayHarbour"||sceneName=="FalseBaySands"||sceneName=="TableFoothills";
            ArtJunction(65,-1,urban,urban?"MAIN ROAD":"SCENIC ACCESS");
            ArtJunction(L*.58f,1,urban,urban?"MARKET STREET":"LOOKOUT LANE");
            for(float d=30;d<L;d+=85)if(!AtJunction(d))
            {
                if(urban||sceneName=="CliftonCove")ArtLamp(d,-8.7f);
                if(urban&&d<L*.32f)ArtParkedCar(d+22,-1,(int)d);
                if(theme==3||theme==4||theme==7)ArtProp(Tree,d,-17-(int)d%3*5,theme==4?11:8,"Roadside shade tree",d);
                else ArtProp(HillBush,d,-17,2.8f,"Cape fynbos",d);
            }
            // Composed roadside bands continue beyond the single landmark, rather than
            // leaving kilometres of empty plane between isolated props.
            for(float d=18;d<L;d+=urban?32:44)
            {
                if(AtJunction(d,22))continue;
                if(urban){ArtBuilding(d,-1,(int)(d/32),15);RoadsideGarden(d,1,false);}
                if(sceneName=="CliftonCove"&&d<L*.43f)ArtBuilding(d,-1,(int)d,16);
                if(sceneName=="ConstantiaVines"||sceneName=="KirstenboschLoop"||sceneName=="TableFoothills")
                {
                    if(!urban)ShadeTree(d,-14,9+(int)d%4);ShadeTree(d+11,17,8+(int)d%3);
                    RoadsideGarden(d,1,sceneName=="KirstenboschLoop");
                }
                else if(sceneName=="CapePointHeadland"||sceneName=="ApostlesClimb")
                {
                    ArtProp(HillRock,d,-14,4,"Roadside sandstone",d);
                    RoadsideGarden(d,-1,false);
                }
            }
            switch(sceneName)
            {
                case "CliftonCove":
                    CoastalBoulders(20,L*.33f,23,7);ArtRidge("Clifton rocky headland",new Vector3(-east-110,0,half*.6f),210,70,120,false);
                    for(int i=0;i<5;i++)BeachStair(90+i*66,22);
                    break;
                case "ApostlesClimb":
                    ArtRidge("Chapmans Peak sandstone wall",new Vector3(-east-95,0,0),half*2+220,115,160,false);
                    CoastalBoulders(100,L*.45f,-24,8);StoneParapet(15,L*.42f,8.6f);ViewTerrace(L*.21f);break;
                case "HoutBayHarbour":
                    Harbour(120,east);ArtRidge("Hout Bay Sentinel",new Vector3(-east-155,0,half*.7f),240,155,170,false);break;
                case "ConstantiaVines":
                    for(float d=90;d<L;d+=170)if(!AtJunction(d,55)&&Mathf.Abs(d-210)>70)Vineyard(d,-24);
                    Vineyard(L*.55f,24);CapeDutchManor(210,-25);ArtRidge("Constantia green foothills",new Vector3(-east-200,0,0),900,85,190,false);break;
                case "KirstenboschLoop":
                    CanopyWalk(150,-27);for(float d=10;d<L;d+=38){ArtProp(Tree,d,17+(int)d%3*5,10,"Botanical canopy",d);ArtProp(HillBush,d,-12,3,"Garden border",d);}
                    ArtRidge("Kirstenbosch eastern slopes",new Vector3(-east-230,0,0),1000,190,260,false);break;
                case "FalseBaySands":
                    BeachHuts(100,21);BeachHuts(260,21);CoastalBoulders(L*.6f,L*.7f,23,3);break;
                case "CapePointHeadland":
                    Lighthouse(190,27);ArtRidge("Cape Point rocky promontory",new Vector3(east+72,0,half*.25f),145,35,85,false);StoneParapet(80,L*.36f,8.5f);
                    for(float d=25;d<L;d+=24)ArtProp(HillBush,d,16,2.2f,"Windswept fynbos",d);break;
                case "TableFoothills":
                    ArtRidge("Table Mountain flat summit",new Vector3(-east-235,0,0),900,230,260,true);Cableway(L*.14f);for(float d=L*.53f;d<L*.72f;d+=45)ArtBuilding(d,1,(int)d,12);break;
            }
        }
        private static void Cableway(float d)
        {
            var t=ArtFrame("Table Mountain cableway silhouette",d,-48);
            ABox(t,"Lower cable station",new Vector3(0,3,0),new Vector3(15,6,12),Ivory);
            ABox(t,"Station glazing",new Vector3(7.55f,3.3f,0),new Vector3(.12f,2.3f,10),Glazing);
            for(int side=-1;side<=1;side+=2)
            {
                Vector3 a=new Vector3(0,6,side*3),b=new Vector3(-120,170,side*3);
                Tube(t,a,b,.06f,Ink);
                Vector3 mid=Vector3.Lerp(a,b,side<0?.35f:.65f);
                ABox(t,"Cable car hanger",mid+Vector3.down,new Vector3(.12f,2,.12f),Ink);
                ABox(t,"Cable car cabin",mid+Vector3.down*3,new Vector3(3.8f,3,3.3f),Ivory);
                ABox(t,"Cable car window",mid+new Vector3(1.95f,-2.7f,0),new Vector3(.1f,1.3f,2.8f),Glazing);
            }
            FinishArt(t);
        }
        private static void StoneParapet(float start,float end,float offset)
        {for(float d=start;d<end;d+=8){var t=ArtFrame("Coastal stone parapet",d,offset);ABox(t,"Sandstone wall",new Vector3(0,.43f,0),new Vector3(.55f,.85f,7.8f),Terra);ABox(t,"Pale coping",new Vector3(0,.9f,0),new Vector3(.65f,.12f,7.9f),Ivory);FinishArt(t);}}
        private static void ViewTerrace(float d)
        {var t=ArtFrame("Chapmans Peak viewpoint",d,17);ABox(t,"Lookout terrace",new Vector3(0,-.15f,0),new Vector3(15,.3f,24),Ivory);for(int i=0;i<8;i++)ABox(t,"Lookout bollard",new Vector3(7,.6f,-10+i*3),new Vector3(.2f,1.2f,.2f),Ink);FinishArt(t);ArtProp(Bench,d,19,2.5f,"Ocean viewpoint bench",90);}
        private static void BeachStair(float d,float offset)
        {var t=ArtFrame("Clifton beach stair access",d,offset);for(int i=0;i<9;i++)ABox(t,"Beach stair",new Vector3(i*.55f,-i*.10f,0),new Vector3(.6f,.2f,2.4f),Ivory);FinishArt(t);}
        private static void Roof(Transform t,Vector3 center,float width,float length,float rise,Material mat)
        {
            var v=new[]{center+new Vector3(-width/2,0,-length/2),center+new Vector3(width/2,0,-length/2),center+new Vector3(0,rise,-length/2),center+new Vector3(-width/2,0,length/2),center+new Vector3(width/2,0,length/2),center+new Vector3(0,rise,length/2)};
            MeshObject("Pitched roof",t,v,new[]{0,2,1,3,4,5,0,3,2,2,3,5,1,2,4,4,2,5},mat);
        }
        private static void BeachHuts(float distance,float offset)
        {
            var t=ArtFrame("Muizenberg bathing boxes",distance,offset);
            Color[] colors={new Color(.84f,.15f,.12f),new Color(.96f,.74f,.13f),new Color(.10f,.46f,.69f),new Color(.16f,.55f,.30f)};
            for(int i=0;i<8;i++)
            {
                float z=(i-3.5f)*4.1f;var mat=Art("Beach hut "+i%4,colors[i%4]);
                ABox(t,"Timber changing hut",new Vector3(0,1.6f,z),new Vector3(3.2f,2.7f,3.4f),mat);
                Roof(t,new Vector3(0,2.95f,z),3.6f,3.7f,1.2f,Ivory);
                ABox(t,"White door surround",new Vector3(-1.63f,1.45f,z),new Vector3(.10f,2.3f,1.15f),Ivory);
                ABox(t,"Hut door",new Vector3(-1.70f,1.4f,z),new Vector3(.06f,2.1f,.90f),mat);
                ABox(t,"Hut stilts",new Vector3(0,.13f,z),new Vector3(3.3f,.26f,3.5f),Ink);
            }FinishArt(t);
        }
        private static void Lighthouse(float d,float offset)
        {
            var t=ArtFrame("Cape Point lighthouse landmark",d,offset);
            Part(t,PrimitiveType.Cylinder,new Vector3(0,5.5f,0),new Vector3(4,5.5f,4),Ivory);
            Part(t,PrimitiveType.Cylinder,new Vector3(0,10,0),new Vector3(4.8f,.20f,4.8f),Ink);
            Part(t,PrimitiveType.Cylinder,new Vector3(0,11.4f,0),new Vector3(3.2f,1.1f,3.2f),Glazing);
            Part(t,PrimitiveType.Cylinder,new Vector3(0,12.6f,0),new Vector3(4,.25f,4),Art("Lighthouse red",new Color(.67f,.20f,.15f)));
            ABox(t,"Lighthouse cottage",new Vector3(5,1.5f,1),new Vector3(6,3,7),Ivory);Roof(t,new Vector3(5,3,1),6.5f,7.5f,1.8f,Ink);
            ABox(t,"Headland platform",new Vector3(0,-.3f,0),new Vector3(19,.6f,18),Terra);FinishArt(t);
        }
        private static void Vineyard(float d,float offset)
        {
            var t=ArtFrame("Constantia vineyard terraces",d,offset);int side=offset<0?-1:1;
            var vine=Art("Vine leaf",new Color(.35f,.49f,.20f));
            for(int row=0;row<8;row++)for(int n=0;n<13;n++)
            {
                float x=side*(row*3.7f),z=(n-6)*5;
                ABox(t,"Vine canopy",new Vector3(x,1.25f,z),new Vector3(1.65f,1.2f,4.3f),vine);
                ABox(t,"Vine stake",new Vector3(x,.65f,z),new Vector3(.09f,1.8f,.09f),Terra);
            }FinishArt(t);
        }
        private static void CapeDutchManor(float d,float offset)
        {
            var t=ArtFrame("Groot Constantia inspired manor",d,offset);
            ABox(t,"Whitewashed manor",new Vector3(-6,2.5f,0),new Vector3(13,5,24),Ivory);Roof(t,new Vector3(-6,5,0),14,25,3.4f,Ink);
            // Distinct raised central Cape-Dutch gable on the road-facing elevation.
            ABox(t,"Central gable",new Vector3(.6f,5.7f,0),new Vector3(.5f,3.6f,5.5f),Ivory);
            Part(t,PrimitiveType.Sphere,new Vector3(.6f,7.35f,0),new Vector3(.55f,2.1f,4.4f),Ivory);
            ABox(t,"Entry door",new Vector3(.91f,1.6f,0),new Vector3(.06f,3.2f,1.65f),Ink);
            for(int i=-3;i<=3;i++)if(i!=0)ABox(t,"Manor shutter",new Vector3(.55f,2.4f,i*3.2f),new Vector3(.14f,2.2f,1.4f),Art("Shutter green",new Color(.12f,.29f,.23f)));
            ABox(t,"Manor terrace",new Vector3(-4,-.15f,0),new Vector3(20,.3f,29),Ivory);FinishArt(t);
        }
        private static void CanopyWalk(float d,float offset)
        {
            var t=ArtFrame("Kirstenbosch Boomslang inspired canopy walkway",d,offset);
            for(int i=0;i<18;i++)
            {
                float z=(i-9)*3.4f,x=Mathf.Sin(i*.27f)*7,y=5+Mathf.Sin(i*.17f)*2;
                ABox(t,"Timber walkway deck",new Vector3(x,y,z),new Vector3(2.2f,.22f,3.8f),Terra);
                for(int side=-1;side<=1;side+=2)ABox(t,"Walkway balustrade",new Vector3(x+side*1.15f,y+.7f,z),new Vector3(.10f,1.4f,3.8f),Ivory);
                if(i%3==0)ABox(t,"Canopy walkway stilt",new Vector3(x,y/2,z),new Vector3(.3f,y,.3f),Ink);
            }FinishArt(t);
        }
        private static void Harbour(float d,float east)
        {
            var t=ArtFrame("Hout Bay working fishing harbour",d,30);
            ABox(t,"Harbour quay",new Vector3(4,-.1f,0),new Vector3(13,.5f,112),Ivory);
            for(int pier=0;pier<3;pier++)
            {
                float z=(pier-1)*34;
                ABox(t,"Timber pier",new Vector3(26,-.2f,z),new Vector3(35,.4f,5),Terra);
                for(int n=0;n<4;n++)
                {
                    ABox(t,"Mooring bollard",new Vector3(15+n*7,.35f,z+2.2f),new Vector3(.35f,1.1f,.35f),Ink);
                    ABox(t,"Pier piling",new Vector3(15+n*7,-1.4f,z),new Vector3(.4f,2.8f,.4f),Ink);
                }
                // Original low-poly fishing boat silhouette: hull, cabin, mast, boom, no stock blocks.
                var boat=new GameObject("Moored fishing boat").transform;boat.SetParent(t,false);boat.localPosition=new Vector3(25,-.25f,z+8);
                var verts=new[]{new Vector3(-8,0,-2.1f),new Vector3(-8,0,2.1f),new Vector3(6,0,-2.1f),new Vector3(6,0,2.1f),new Vector3(10,0,0),new Vector3(-6,-1.4f,0),new Vector3(6,-1.4f,0)};
                MeshObject("Painted fishing hull",boat,verts,new[]{0,1,2,2,1,3,2,3,4,0,5,1,0,2,5,2,6,5,2,4,6,3,6,4,1,5,3,3,5,6},Art("Boat hull "+pier,pier%2==0?new Color(.15f,.38f,.55f):new Color(.75f,.27f,.16f)));
                ABox(boat,"Wheelhouse",new Vector3(-2,1.3f,0),new Vector3(4,2.6f,3.5f),Ivory);
                ABox(boat,"Cabin window",new Vector3(-2,1.6f,-1.78f),new Vector3(2.6f,.9f,.06f),Glazing);
                ABox(boat,"Fishing mast",new Vector3(2,4,0),new Vector3(.16f,8,.16f),Ink);
                ABox(boat,"Fishing boom",new Vector3(2,6,0),new Vector3(.12f,.12f,7),Ink);
            }
            ABox(t,"Fish market",new Vector3(-9,2.5f,32),new Vector3(12,5,20),Art("Harbour ochre",new Color(.69f,.53f,.30f)));
            Roof(t,new Vector3(-9,5,32),13,21,2,Ink);FinishArt(t);
        }
        private static void BuildReferenceBoKaap()
        {
            BeginArt();var ground=Art("Bo Kaap sandstone",new Color(.53f,.46f,.39f));
            Box("Bo Kaap land",new Vector3(0,-.65f,0),new Vector3(1100,1,1100),ground);SupportedVerge(ground,Ivory);
            foreach(float d in new[]{65f,350f,730f,1070f})ArtJunction(d,-1,true,d<100?"WALE STREET":d<500?"CHIAPPINI STREET":d<900?"ROSE STREET":"SHORTMARKET STREET");
            Color[] colors={new Color(.85f,.23f,.38f),new Color(.91f,.65f,.19f),new Color(.24f,.64f,.65f),new Color(.47f,.36f,.69f),new Color(.29f,.57f,.38f),new Color(.85f,.40f,.22f)};
            for(float d=12;d<CapeCrownRoute.Length;d+=22)
            {
                if(AtJunction(d,18))continue;
                for(int side=-1;side<=1;side+=2)
                {
                    int idx=(int)(d/22)+(side>0?3:0);var t=ArtFrame("Bo Kaap stoep house",d,side*15);
                    float h=idx%4==0?6:3.8f;
                    var wall=Art("Bo Kaap plaster "+idx%6,colors[idx%6]);
                    ABox(t,"Plaster facade",new Vector3(0,h/2,0),new Vector3(8,h,16),wall);
                    ABox(t,"Cornice",new Vector3(0,h+.1f,0),new Vector3(8.4f,.25f,16.4f),Ivory);
                    for(int w=-2;w<=2;w++)
                    {
                        float x=-side*4.08f,z=w*2.6f;
                        ABox(t,"White window surround",new Vector3(x,2.1f,z),new Vector3(.16f,1.75f,1.3f),Ivory);
                        ABox(t,"Sash window",new Vector3(x-side*.10f,2.1f,z),new Vector3(.07f,1.5f,1.05f),Glazing);
                    }
                    ABox(t,"Front door",new Vector3(-side*4.22f,1.28f,5.8f),new Vector3(.08f,2.5f,1.3f),Ink);
                    for(int step=0;step<3;step++)ABox(t,"Stoep step",new Vector3(-side*(4.4f+step*.35f),.35f-step*.11f,5.8f),new Vector3(.5f,.22f,2.4f),Ivory);
                    ABox(t,"Raised house foundation",new Vector3(0,-.25f,0),new Vector3(8.4f,.5f,16.4f),Terra);FinishArt(t);
                }
                if((int)(d/22)%3==0)ArtLamp(d,8.7f);
            }
            var mosque=ArtFrame("Bo Kaap neighbourhood mosque silhouette",160,-29);
            var mint=Art("Mosque mint",new Color(.55f,.77f,.61f));
            ABox(mosque,"Prayer hall",new Vector3(0,3,0),new Vector3(12,6,18),mint);
            ABox(mosque,"Minaret",new Vector3(5,7,7),new Vector3(3,14,3),Ivory);
            Part(mosque,PrimitiveType.Sphere,new Vector3(5,14.2f,7),new Vector3(3.5f,2.5f,3.5f),mint);
            ABox(mosque,"Arched entry",new Vector3(6.1f,2,0),new Vector3(.15f,4,2),Ink);FinishArt(mosque);
        }
    }
}
