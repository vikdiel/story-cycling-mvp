using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Ein Ampelmast aus den Synty-Baukastenteilen (PolygonCity "LightPole").
    //
    // Ursache des früheren "dritten Kopfes" unter dem Ausleger (Befund aus den FBX-Meshes):
    //  - SM_Prop_LightPole_Arm_01 enthält bereits ZWEI dreilinsige Signalköpfe (Höhe 4,11–5,01 m, bei z ≈ 4,3 und 6,0 m).
    //  - Der Generator hängte zusätzlich ein SM_Prop_TrafficLight_0x (Standalone-Requisit, Höhe -0,90..0 ab Ursprung) auf
    //    y = 4,12 m an den Ausleger -> Kopf hing frei direkt unter dem Ausleger (y 3,22–4,12) und war mit nichts verbunden.
    //  - Synty selbst baut den Mast in Demo.unity/Overview.unity ausschließlich aus Base_01 + Arm_01 + Box_01 + CrossLights_01
    //    + CrossButton_01 + Lights_01 (+ Lights_02) — alle Teile am GEMEINSAMEN Ursprung, ohne Versatz (20 von 20 Masten).
    //    TrafficLight_0x kommt in keinem dieser Masten vor; als Mast diente außerdem Base_01 (nicht Base_02, die zwei
    //    Laternen trägt und mit dem Ausleger kollidiert).
    //
    // Lokales Mastsystem (wie in OsmDetailPlacer): +z = zur Fahrbahn (Ausleger), +x = gegen die Fahrtrichtung = Leuchtseite.
    public static class SignalAssembly
    {
        // Achsparallele Box im Mastsystem.
        public struct Box
        {
            public Vector3 min, max;
            public Box(Vector3 min, Vector3 max) { this.min = min; this.max = max; }
            public Vector3 Center => (min + max) * .5f;
            public Vector3 Size => max - min;
            public Box Translated(Vector3 d) => new Box(min + d, max + d);

            public void Encapsulate(Vector3 p)
            {
                min = new Vector3(Mathf.Min(min.x, p.x), Mathf.Min(min.y, p.y), Mathf.Min(min.z, p.z));
                max = new Vector3(Mathf.Max(max.x, p.x), Mathf.Max(max.y, p.y), Mathf.Max(max.z, p.z));
            }

            // Überlappung der Boxen: kleinste Überlappungslänge über x/y/z (negativ = Lücke).
            public static float Penetration(Box a, Box b)
            {
                float ox = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
                float oy = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
                float oz = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z);
                return Mathf.Min(ox, Mathf.Min(oy, oz));
            }
        }

        public sealed class Kit
        {
            public GameObject pole, arm, box, head, ped, button;
            public bool Usable => pole != null && arm != null;
            internal readonly Dictionary<GameObject, Box> boxes = new Dictionary<GameObject, Box>();
        }

        // Teil im Mast: Name, Box im Mastsystem, ob es eine Leuchtseite hat (Signalkopf).
        public struct PartInfo
        {
            public string name; public Box box; public bool hasLens;
            public PartInfo(string name, Box box, bool hasLens) { this.name = name; this.box = box; this.hasLens = hasLens; }
        }

        public const float MinContact = .02f;   // so tief muss ein Teil in den Mast/Ausleger hineinragen (m)

        public static Kit Load(System.Func<string, GameObject> one)
        {
            return new Kit
            {
                pole = one(@"^SM_Prop_LightPole_Base_01$"),
                arm = one(@"^SM_Prop_LightPole_Arm_01$"),
                box = one(@"^SM_Prop_LightPole_Box_01$"),
                head = one(@"^SM_Prop_LightPole_Lights_01$"),
                ped = one(@"^SM_Prop_LightPole_CrossLights_01$"),
                button = one(@"^SM_Prop_LightPole_CrossButton_01$")
            };
        }

        private static int builtCount, problemCount;
        private static readonly HashSet<string> reported = new HashSet<string>();

        public static void ResetStats() { builtCount = 0; problemCount = 0; reported.Clear(); }
        public static void LogSummary() =>
            Debug.Log($"Ampelmasten: {builtCount} aufgebaut, {problemCount} Warnungen zur Teile-Anbindung.");

        // Baut den Mast an 'foot' mit Drehung 'rot' (+z zur Fahrbahn, +x gegen die Fahrtrichtung).
        public static Transform Build(Kit kit, Transform parent, Vector3 foot, Quaternion rot)
        {
            var mast = new GameObject("TrafficSignal").transform;
            mast.SetParent(parent, false);
            mast.SetPositionAndRotation(foot, rot);
            var parts = new List<PartInfo>();
            AddPart(kit, kit.pole, "Mast (Base_01)", mast, false, parts);
            AddPart(kit, kit.arm, "Ausleger (Arm_01, mit zwei Köpfen)", mast, true, parts);
            AddPart(kit, kit.box, "Steuerkasten (Box_01)", mast, false, parts);
            AddPart(kit, kit.head, "Mastkopf (Lights_01)", mast, true, parts);
            AddPart(kit, kit.ped, "Fußgängerkopf (CrossLights_01)", mast, false, parts);
            AddPart(kit, kit.button, "Fußgängertaster (CrossButton_01)", mast, false, parts);
            builtCount++;
            foreach (string problem in Check(parts))
            {
                problemCount++;
                if (reported.Add(problem)) Debug.LogWarning("Ampelmast: " + problem);
            }
            return mast;
        }

        private static void AddPart(Kit kit, GameObject prefab, string name, Transform mast, bool lens, List<PartInfo> parts)
        {
            if (prefab == null) return;
            var go = WorldSpawn.Spawn(prefab, mast);
            go.transform.localPosition = Vector3.zero;            // Synty: alle Teile teilen den Ursprung
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            if (PrefabBox(kit, prefab, out Box b)) parts.Add(new PartInfo(name, b, lens));
        }

        // Box des Prefabs im Wurzelsystem (aus den Mesh-Bounds, die Meshes sind nicht lesbar).
        private static bool PrefabBox(Kit kit, GameObject prefab, out Box box)
        {
            if (kit.boxes.TryGetValue(prefab, out box)) return true;
            bool any = false;
            box = default(Box);
            var root = prefab.transform;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds bb = mf.sharedMesh.bounds;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = bb.center + Vector3.Scale(bb.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { box = new Box(p, p); any = true; } else box.Encapsulate(p);
                }
            }
            if (any) kit.boxes[prefab] = box;
            return any;
        }

        // Stütze des Mastes: die quadratische Grundfläche des Mast-Meshes (Kantenlänge = Breite in x), ab dessen
        // Rand, an dem der Laternenausleger NICHT sitzt (Ausleger liegt bei Synty immer in +z). Nur die
        // Mesh-Bounds sind bekannt, deshalb wird der Schaft aus der Grundfläche abgeleitet.
        public static Box Shaft(Box pole)
        {
            float side = pole.max.x - pole.min.x;
            return new Box(new Vector3(pole.min.x, pole.min.y, pole.min.z),
                           new Vector3(pole.max.x, pole.max.y, pole.min.z + side));
        }

        // Prüft, dass jedes Teil den Mast berührt (Mast = parts[0]), der Ausleger im Mast endet, nichts
        // freischwebt und Signalköpfe (Leuchtseite = +x) zum ankommenden Verkehr zeigen.
        public static List<string> Check(IList<PartInfo> parts)
        {
            var problems = new List<string>();
            if (parts.Count == 0) { problems.Add("keine Teile"); return problems; }
            Box pole = parts[0].box, shaft = Shaft(pole);
            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                float pen = Box.Penetration(p.box, shaft);
                if (pen < MinContact)
                    problems.Add($"'{p.name}' berührt den Mast nicht (Überlappung {pen:0.000} m, nötig {MinContact:0.00} m) - würde frei schweben.");
                if (p.hasLens && p.box.Center.x - shaft.Center.x <= 0f)
                    problems.Add($"'{p.name}': Leuchtseite (+x im Mastsystem) liegt nicht auf der Seite zum ankommenden Verkehr.");
                if (p.box.max.y > pole.max.y + .01f)
                    problems.Add($"'{p.name}' ragt über die Mastspitze hinaus.");
            }
            return problems;
        }
    }
}
