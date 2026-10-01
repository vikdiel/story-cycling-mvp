using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Macht aus den Pflanzstellen (VegetationScatter) Szenenobjekte: löst jede Art auf Prefabs der vorhandenen Synty-Pakete auf,
    // erzeugt getönte Materialkopien (Kap-Farben) und legt GPU-Instancing-Felder je Entfernungsband an:
    //   Nah (bis nearEnd):   volle Modelle (LOD0; bei sehr schweren Modellen LOD1), Bäume werfen Schatten
    //   Mittel (bis midEnd): gröbste LOD
    //   Fern:                prozedurale Baumsilhouetten (Kegel/Kuppel, 30-60 Dreiecke)
    // Es entstehen nur Instanzdaten (Position/Drehung/Maßstab), keine kopierten Meshes.
    public static class VegetationBuilder
    {
        public struct Part { public Mesh mesh; public int sub; public Material mat; public Matrix4x4 local; }

        public sealed class Variant
        {
            public GameObject prefab;             // null bei Silhouetten
            public int silhouette = -1;           // 0 = Nadelbaum, 1 = Laubbaum
            public float weight;
            public bool tintable;                 // Atlas x _BaseColor (nur Generic-Modelle)
            public float height = 1f, minY;       // Maße bei Maßstab 1 (aus den Meshes)
            public List<Part> near, coarse;
        }

        public sealed class SpeciesTable
        {
            public VegSpecies spec; public Variant[] variants = new Variant[0]; public int[] map = new int[256];
        }

        public sealed class BuildStats { public int instances, batches, batchRecords, materials; public int[] perTier = new int[3]; public string text; }

        private const int HeavyVerts = 5000;       // darüber nimmt das Nahband LOD1 statt LOD0

        // ---------------------------------------------------------------- Prefab-Auflösung (ohne AssetDatabase, auch im Test-Harness)
        public static Dictionary<Sp, SpeciesTable> Resolve(WorldAssets a)
        {
            var result = new Dictionary<Sp, SpeciesTable>();
            for (int i = 0; i < (int)Sp.Count; i++)
            {
                var spec = VegSpecies.All[i];
                var table = new SpeciesTable { spec = spec };
                var list = new List<Variant>();
                if (Array.IndexOf(spec.Roles, "Silhouette") >= 0)
                {
                    int kind = spec.Id == Sp.FarConifer ? 0 : 1;
                    list.Add(new Variant { silhouette = kind, weight = 1f, tintable = true, height = kind == 0 ? 20f : 12.5f, minY = 0f });
                }
                else
                {
                    var chosen = new List<(List<GameObject> pool, float w)>();
                    for (int r = 0; r < spec.Roles.Length; r++)
                    {
                        if (a.Roles == null || !a.Roles.TryGetValue(spec.Roles[r], out var pool) || pool == null || pool.Count == 0) continue;
                        float w = spec.Weights != null ? (r < spec.Weights.Length ? spec.Weights[r] : 0f) : (chosen.Count == 0 ? 1f : 0f);
                        chosen.Add((pool, w));
                    }
                    float sum = 0f; foreach (var c in chosen) sum += c.w;
                    if (chosen.Count > 0 && sum <= 0f) { chosen[0] = (chosen[0].pool, 1f); sum = 1f; }
                    int slots = Mathf.Max(1, spec.MaxVariants);
                    foreach (var c in chosen)
                    {
                        if (c.w <= 0f) continue;
                        float share = c.w / sum;
                        int cnt = Mathf.Clamp(Mathf.RoundToInt(slots * share), 1, Mathf.Min(slots, c.pool.Count));
                        var sorted = new List<GameObject>(c.pool);
                        sorted.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
                        for (int j = 0; j < cnt; j++)
                        {
                            var prefab = sorted[Mathf.Min(sorted.Count - 1, (int)((j + .5f) * sorted.Count / cnt))];
                            var v = MakeVariant(prefab);
                            if (v == null) continue;
                            v.weight = share / cnt;
                            list.Add(v);
                        }
                    }
                }
                float tw = 0f; foreach (var v in list) tw += v.weight;
                table.variants = list.ToArray();
                if (tw > 0f)
                {
                    float acc = 0f; int vi = 0;
                    for (int b = 0; b < 256; b++)
                    {
                        float u = (b + .5f) / 256f * tw;
                        while (vi < list.Count - 1 && u > acc + list[vi].weight) { acc += list[vi].weight; vi++; }
                        table.map[b] = vi;
                    }
                }
                result[spec.Id] = table;
            }
            return result;
        }

        private static Variant MakeVariant(GameObject prefab)
        {
            var near = PartsOf(prefab, 0); var coarse = PartsOf(prefab, -1);
            if (coarse.Count == 0) return null;
            bool any = false; var bnd = new Bounds();
            foreach (var p in coarse)
            {
                if (p.mesh == null) continue;
                var mb = p.mesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = mb.center + new Vector3((c & 1) != 0 ? mb.extents.x : -mb.extents.x, (c & 2) != 0 ? mb.extents.y : -mb.extents.y, (c & 4) != 0 ? mb.extents.z : -mb.extents.z);
                    var w = p.local.MultiplyPoint3x4(corner);
                    if (!any) { bnd = new Bounds(w, Vector3.zero); any = true; } else bnd.Encapsulate(w);
                }
            }
            if (!any) return null;
            bool tintable = prefab.name.StartsWith("SM_Gen_");
            foreach (var p in coarse) if (p.mat == null || !p.mat.HasProperty("_BaseColor")) tintable = false;
            return new Variant { prefab = prefab, near = near, coarse = coarse, tintable = tintable, height = Mathf.Max(.05f, bnd.size.y), minY = bnd.min.y };
        }

        // Meshes eines Prefabs. LODGroup: lodIndex 0 = feinste Stufe (bei > HeavyVerts die nächste), -1 = gröbste.
        public static List<Part> PartsOf(GameObject prefab, int lodIndex)
        {
            var result = new List<Part>();
            IEnumerable<Renderer> renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            var lod = prefab.GetComponent<LODGroup>();
            if (lod != null)
            {
                var lods = lod.GetLODs();
                if (lods != null && lods.Length > 0)
                {
                    int idx = lodIndex < 0 ? lods.Length - 1 : Mathf.Min(lodIndex, lods.Length - 1);
                    if (lodIndex == 0 && lods.Length > 1 && Verts(lods[0].renderers) > HeavyVerts) idx = 1;
                    if (lods[idx].renderers != null && lods[idx].renderers.Length > 0) renderers = lods[idx].renderers;
                }
            }
            foreach (var r in renderers)
            {
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;
                Matrix4x4 local = r.transform.localToWorldMatrix;          // wie Instanziieren an der Welt-Null: Wurzel-Maßstab inklusive
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                    result.Add(new Part { mesh = mf.sharedMesh, sub = s, mat = mats[Mathf.Min(s, mats.Length - 1)], local = local });
            }
            return result;
        }

        private static int Verts(Renderer[] rs)
        {
            int n = 0;
            if (rs == null) return 0;
            foreach (var r in rs) { var mf = r != null ? r.GetComponent<MeshFilter>() : null; if (mf != null && mf.sharedMesh != null) n += mf.sharedMesh.vertexCount; }
            return n;
        }

        private static bool IsIdentity(Matrix4x4 m)
        {
            var id = Matrix4x4.identity;
            for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - id[i]) > 1e-4f) return false;
            return true;
        }

        // ---------------------------------------------------------------- Aufbau
        public static BuildStats Build(ScatterResult scatter, WorldAssets assets, Transform parent, ScatterSettings cfg,
                                       Func<Mesh, Mesh> saveMesh, Func<Material, Material> saveMat)
        {
            var tables = Resolve(assets);
            var stats = new BuildStats();
            var items = scatter.Items;
            var batches = VegetationBatching.Plan(items,
                (sp, b) => tables[sp].variants.Length == 0 ? -1 : tables[sp].map[b],
                (sp, v) => v >= 0 && v < tables[sp].variants.Length && tables[sp].variants[v].tintable);
            batches.RemoveAll(b => b.variant < 0);

            var fields = new InstancedMeshField[3];
            string[] names = { "VegetationNear", "VegetationMid", "VegetationFar" };
            float[] dist = { cfg.nearEnd + 250f, cfg.midEnd + 120f, cfg.farEnd + 100f };
            for (int t = 0; t < 3; t++)
            {
                var go = new GameObject(names[t]);
                go.transform.SetParent(parent, false);
                fields[t] = go.AddComponent<InstancedMeshField>();
                fields[t].drawDistance = dist[t];
                fields[t].shadows = t == 0 ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
            var matCache = new Dictionary<(Material, int, int), Material>();
            var silhouetteMeshes = new Mesh[2];
            var silhouetteMats = new Dictionary<(int, int), Material>();
            var used = new HashSet<Material>();

            foreach (var b in batches)
            {
                var table = tables[b.sp]; var spec = table.spec; var v = table.variants[b.variant];
                var field = fields[(int)b.tier];
                List<Part> parts;
                if (v.silhouette >= 0)
                {
                    if (silhouetteMeshes[v.silhouette] == null) silhouetteMeshes[v.silhouette] = saveMesh(MakeSilhouette(v.silhouette));
                    int ti = b.tint % spec.TintCount;
                    if (!silhouetteMats.TryGetValue((v.silhouette, ti), out var sm))
                    {
                        var shader = Shader.Find("Universal Render Pipeline/Lit");
                        sm = new Material(shader) { name = "VegSilhouette_" + spec.Name + "_" + ti };
                        Color col = spec.Tints[ti]; sm.SetColor("_BaseColor", new Color(col.r, col.g, col.b, 1f)); sm.SetFloat("_Smoothness", 0f); sm.SetFloat("_Metallic", 0f);
                        sm.enableInstancing = true;
                        sm = saveMat(sm); silhouetteMats[(v.silhouette, ti)] = sm;
                    }
                    parts = new List<Part> { new Part { mesh = silhouetteMeshes[v.silhouette], sub = 0, mat = sm, local = Matrix4x4.identity } };
                }
                else parts = b.tier == VegTier.Near ? v.near : v.coarse;

                int n = b.items.Count;
                var packed = new Vector4[n]; var scales = new float[n]; Vector2[] lean = null;
                for (int k = 0; k < n; k++)
                {
                    var p = items[b.items[k]];
                    float sc = Mathf.Clamp(p.scale * spec.Height / v.height, .15f, 6f);
                    float y = p.y - spec.Sink * spec.Height * p.scale - v.minY * sc;
                    packed[k] = new Vector4(p.x, y, p.z, p.yaw); scales[k] = sc;
                    if (p.leanX != 0f || p.leanZ != 0f) { if (lean == null) lean = new Vector2[n]; lean[k] = new Vector2(p.leanX, p.leanZ); }
                }
                for (int pi = 0; pi < parts.Count; pi++)
                {
                    var part = parts[pi];
                    Material mat = v.silhouette >= 0 ? part.mat : MaterialFor(part.mat, spec, b.tint, v.tintable, matCache, saveMat);
                    var batch = new InstancedMeshField.Batch
                    {
                        mesh = part.mesh, submesh = part.sub, material = mat, bounds = b.bounds,
                        maxDistance = Mathf.Min(spec.MaxDraw, b.tier == VegTier.Near ? cfg.nearEnd + 250f : 1e6f),
                        noShadows = spec.Group != VegGroup.Tree || b.tier != VegTier.Near
                    };
                    if (IsIdentity(part.local)) { batch.packed = packed; batch.scales = scales; batch.lean = lean; }
                    else
                    {
                        // Teil mit Versatz im Prefab: volle Matrizen (Wurzel-Transform * Teil-Transform)
                        var root = new InstancedMeshField.Batch { packed = packed, scales = scales, lean = lean };
                        var rm = root.Instances;
                        batch.matrices = new Matrix4x4[n];
                        for (int k = 0; k < n; k++) batch.matrices[k] = rm[k] * part.local;
                    }
                    field.batches.Add(batch);
                    used.Add(mat);
                    stats.batchRecords++;
                }
                stats.batches++; stats.instances += n; stats.perTier[(int)b.tier] += n;
            }
            stats.materials = used.Count;
            stats.text = $"Vegetation: {stats.instances} Instanzen in {stats.batches} Batches ({stats.batchRecords} Draw-Records, {used.Count} Materialien; nah {stats.perTier[0]}, mittel {stats.perTier[1]}, fern {stats.perTier[2]}).";
            Debug.Log(stats.text);
            return stats;
        }

        // Materialkopie mit Instancing; bei tönbaren Modellen mit Kap-Farbton: _BaseColor = Zielfarbe / mittlere Atlasfarbe.
        private static Material MaterialFor(Material src, VegSpecies spec, int tint, bool tintable, Dictionary<(Material, int, int), Material> cache, Func<Material, Material> saveMat)
        {
            bool doTint = tintable && spec.Tints != null && spec.Tints.Length > 0;
            if (!doTint && src.enableInstancing) return src;
            int ti = doTint ? tint % spec.Tints.Length : -1;
            var key = (src, (int)spec.Id * (doTint ? 1 : 0), ti);
            if (cache.TryGetValue(key, out var m)) return m;
            m = new Material(src) { name = src.name + (doTint ? "_" + spec.Name + "_t" + ti : "_inst") };
            if (doTint)
            {
                Color t = spec.Tints[ti], b = spec.Base;
                m.SetColor("_BaseColor", new Color(Mathf.Clamp(t.r / Mathf.Max(.02f, b.r), 0f, 2.5f), Mathf.Clamp(t.g / Mathf.Max(.02f, b.g), 0f, 2.5f), Mathf.Clamp(t.b / Mathf.Max(.02f, b.b), 0f, 2.5f), 1f));
            }
            m.enableInstancing = true;
            m = saveMat(m);
            cache[key] = m;
            return m;
        }

        // ---------------------------------------------------------------- Silhouetten (Ferne)
        // Drehkörper aus Profil (Höhe, Radius): 8 Seiten, am Ende eine Spitze. Pivot am Boden.
        private static Mesh MakeSilhouette(int kind)
        {
            float[,] prof = kind == 0
                ? new float[,] { { 0f, .55f }, { 3.5f, 3.4f }, { 11f, 2.1f }, { 20f, 0f } }                      // Nadelbaum: Kegel mit breitem Fuß
                : new float[,] { { 0f, .45f }, { 2.4f, .5f }, { 4f, 3.4f }, { 7f, 6f }, { 10f, 4.6f }, { 12.5f, 0f } };   // Laubbaum: Stamm + Kuppel
            const int segs = 8;
            int rings = prof.GetLength(0);
            var verts = new List<Vector3>(); var tris = new List<int>();
            for (int r = 0; r < rings; r++)
            {
                float y = prof[r, 0], rad = prof[r, 1];
                if (rad <= 0f) { verts.Add(new Vector3(0f, y, 0f)); continue; }
                for (int s = 0; s < segs; s++)
                {
                    float a = s * Mathf.PI * 2f / segs + (r % 2) * .2f;
                    verts.Add(new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad));
                }
            }
            int idx = 0; var ringStart = new int[rings];
            for (int r = 0; r < rings; r++) { ringStart[r] = idx; idx += prof[r, 1] <= 0f ? 1 : segs; }
            for (int r = 0; r < rings - 1; r++)
            {
                bool topApex = prof[r + 1, 1] <= 0f;
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int a0 = ringStart[r] + s, a1 = ringStart[r] + s1;
                    if (topApex) { tris.Add(a0); tris.Add(ringStart[r + 1]); tris.Add(a1); }
                    else
                    {
                        int b0 = ringStart[r + 1] + s, b1 = ringStart[r + 1] + s1;
                        tris.Add(a0); tris.Add(b0); tris.Add(a1);
                        tris.Add(a1); tris.Add(b0); tris.Add(b1);
                    }
                }
            }
            // Boden schließen (Unterseite sieht man vom Hang aus nie, aber der Schatten braucht geschlossene Körper nicht) — weggelassen
            var mesh = new Mesh { name = kind == 0 ? "VegSilhouetteConifer" : "VegSilhouetteBroad" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
