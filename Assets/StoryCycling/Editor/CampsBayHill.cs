using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.Editor
{
    public static partial class CapeCrownSceneBuilder
    {
        private const string HillRock = "Assets/Synty/PolygonGeneric/Prefabs/Environment/SM_Gen_Env_Rock_05.prefab";
        private const string HillBush = "Assets/Synty/PolygonGeneric/Prefabs/Environment/SM_Gen_Env_Bush_02.prefab";
        private static void BuildCoastalHill()
        {
            Material earth = Mat("Hill warm sandstone", new Color(.52f,.48f,.36f));
            Material scrub = Mat("Hill fynbos", new Color(.36f,.43f,.28f));
            Material pale = Mat("Lookout stone", new Color(.77f,.73f,.62f));
            Material rail = Mat("Coastal railing", new Color(.14f,.26f,.27f));
            // Sloping embankments physically support the elevated road. No floating asphalt.
            foreach (int side in new[] { -1, 1 })
            {
                const int samples = 440;
                var vertices = new Vector3[(samples + 1) * 3];
                var triangles = new List<int>();
                for (int i = 0; i <= samples; i++)
                {
                    float d = Mathf.Lerp(CapeCrownRoute.HillStart, CapeCrownRoute.HillEnd, i / (float)samples);
                    for (int j = 0; j < 3; j++)
                    {
                        float offset = side * (8 + j * 9);
                        Vector3 p = CapeCrownRoute.Position(d,offset,0,relief);
                        p.y = Mathf.Lerp(p.y - .035f, -.15f, j / 2f);
                        vertices[i * 3 + j] = p;
                    }
                    if (i == samples) continue;
                    for (int j = 0; j < 2; j++)
                    {
                        int a = i * 3 + j;
                        if (side == 1) triangles.AddRange(new[] { a,a+3,a+1,a+1,a+3,a+4 });
                        else triangles.AddRange(new[] { a,a+1,a+3,a+1,a+4,a+3 });
                    }
                }
                MeshObject(side == 1 ? "Ocean hill embankment" : "Inland hill embankment",null,vertices,triangles.ToArray(),scrub);
            }
            const float summit = (CapeCrownRoute.HillStart + CapeCrownRoute.HillEnd) / 2;
            Vector3 lookout = CapeCrownRoute.Position(summit, 11, 0, relief);
            Box("Summit lookout terrace",lookout + Vector3.down * .25f,new Vector3(7,.45f,12),pale);
            for (int i = 0; i <= 6; i++)
            {
                Vector3 post = lookout + new Vector3(-3,.55f,-5.5f + i * 1.8f);
                Box("Lookout rail post",post,new Vector3(.09f,1.2f,.09f),rail);
            }
            Box("Lookout handrail",lookout + new Vector3(-3,1.08f,0),new Vector3(.09f,.09f,11),rail);
            AddCoastalProp(Bench,lookout + new Vector3(-1,0,-2),90,2.2f,"Hilltop ocean bench");
            // Terrace has a solid base down to ground, avoiding a hanging platform.
            Box("Lookout sandstone foundation",new Vector3(lookout.x,(lookout.y-.15f)/2,lookout.z),
                new Vector3(7,lookout.y+.15f,12),earth);
            Wayfinding("OCEAN VIEW",summit-10,11,rail);
            Wayfinding("COASTAL CLIMB\n6% MAX",215,-7,rail);
            Wayfinding("CAMPS BAY\nBEACH LOOP",36,7,rail);
            // Low white bollards delineate the bends without turning the road into a motorway.
            Material white = Mat("Coastal bollards",new Color(.89f,.86f,.74f));
            for (float d = 198; d < 618; d += 18)
            {
                Vector3 p = CapeCrownRoute.Position(d,7.45f,.34f,relief);
                Box("Hill edge bollard",p,new Vector3(.15f,.7f,.15f),white);
                Box("Bollard reflector",p+Vector3.up*.18f,new Vector3(.16f,.08f,.16f),rail);
            }
            // Fynbos groups follow the embankment's actual elevation and stay clear of the promenade.
            for (int i=0;i<22;i++)
            {
                float d = 215+i*16;
                if (Mathf.Abs(d-summit)<30) continue; // keep the viewpoint clear
                foreach (int side in new[] { -1,1 })
                {
                    float offset = side*(13+(i%3)*3);
                    Vector3 p = CapeCrownRoute.Position(d,offset,0,relief);
                    p.y = Mathf.Lerp(p.y-.035f,-.15f,(Mathf.Abs(offset)-8)/18);
                    AddCoastalProp(i%4==0 ? HillRock : HillBush,p,i*47, i%4==0 ? 3.5f : 2.1f,"Hillside fynbos group");
                }
            }
            for (int i=0;i<8;i++)
            {
                float z=-120+i*32;
                AddCoastalProp(HillRock,new Vector3(91+(i%3)*3,-.65f,z),i*31,3.4f,"Beach granite");
            }
            // Restrained colour and a continuous forecourt tie the beach shops together.
            Color[] colors = { new Color(.24f,.51f,.56f),new Color(.75f,.42f,.29f),new Color(.79f,.69f,.42f) };
            for (int i = 0; i < 9; i++)
            {
                float z = -72 + 18 * i;
                Material canvas = Mat("Cafe canopy " + i,colors[i%colors.Length]);
                Box("Beach cafe awning",new Vector3(38.6f,3.1f,z),new Vector3(2.4f,.12f,7.5f),canvas);
                for(int end=-1;end<=1;end+=2)
                    Box("Awning support",new Vector3(39.5f,1.55f,z+end*3.6f),new Vector3(.065f,3.1f,.065f),rail);
            }
        }

        private static void Wayfinding(string title,float distance,float offset,Material material)
        {
            Vector3 ground = CapeCrownRoute.Position(distance,offset,0,relief);
            CapeCrownRoute.Sample(distance,out _,out Vector3 forward);
            Transform sign = new GameObject(title.Replace('\n',' ')).transform;
            sign.position = ground; sign.rotation = Quaternion.LookRotation(forward);
            Part(sign,PrimitiveType.Cube,new Vector3(0,.9f,0),new Vector3(.09f,1.8f,.09f),material);
            Part(sign,PrimitiveType.Cube,new Vector3(0,1.8f,0),new Vector3(2.5f,.8f,.08f),material);
            var go = new GameObject("Sign text",typeof(TextMesh)); go.transform.SetParent(sign,false);
            go.transform.localPosition = new Vector3(0,1.8f,-.05f);
            go.transform.localRotation = Quaternion.identity;
            var text = go.GetComponent<TextMesh>();
            text.text = title; text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
            text.characterSize = .075f; text.fontSize = 48; text.color = new Color(.97f,.92f,.78f);
        }
    }
}
