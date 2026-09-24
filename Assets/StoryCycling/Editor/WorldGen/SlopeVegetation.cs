using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryCycling.WorldGen.Editor
{
    // Hangvegetation: bedeckt die Berge entlang der Route bis ~700 m mit Fynbos-Büschen, Bäumen in
    // Schluchten und Felsen an Steilhängen — per GPU-Instancing (nur Positionsdaten, kaum Speicher).
    // Arten wechseln fleckweise, Dichte folgt Biom, Neigung und Gelände (wie auf dem Hout-Bay-Foto).
    public static class SlopeVegetation
    {
        private const float Cell = 9f, Bucket = 250f, StartDistance = 120f;

        public struct Part { public Mesh mesh; public int sub; public Material mat; public Matrix4x4 local; }
        private sealed class Inst { public readonly List<Vector4> pos = new List<Vector4>(); public readonly List<float> scale = new List<float>(); }

        public static int Place(WorldTerrain terrain, WorldAssets a, Occupancy occupied, Transform parent, float maxDistance)
        {
            var bushes = new List<GameObject>(); bushes.AddRange(a.Bushes); bushes.AddRange(a.Ferns);
            var trees = new List<GameObject>(); trees.AddRange(a.CoastalTrees); trees.AddRange(a.Pines); trees.AddRange(a.BroadTrees);
            var rocks = new List<GameObject>(); rocks.AddRange(a.Rocks); rocks.AddRange(a.Boulders);
            if (bushes.Count == 0) { Debug.LogWarning("Hangvegetation: keine Büsche im Katalog."); return 0; }

            var parts = new Dictionary<GameObject, List<Part>>();
            var dem = terrain.Dem;
            int w = Mathf.CeilToInt((dem.MaxX - dem.MinX) / Cell), h = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / Cell);
            var dist2 = new float[w * h];
            for (int k = 0; k < dist2.Length; k++) dist2[k] = float.MaxValue;
            int rad = Mathf.CeilToInt(maxDistance / Cell);
            var s = terrain.Road.Samples;
            for (int si = 0; si < s.Count; si += 10)
            {
                Vector3 p = s[si].pos;
                int ci = Mathf.FloorToInt((p.x - dem.MinX) / Cell), cj = Mathf.FloorToInt((p.z - dem.MinZ) / Cell);
                for (int dj = -rad; dj <= rad; dj++)
                {
                    int j = cj + dj; if (j < 0 || j >= h) continue;
                    float dz = dem.MinZ + (j + .5f) * Cell - p.z;
                    for (int di = -rad; di <= rad; di++)
                    {
                        int i = ci + di; if (i < 0 || i >= w) continue;
                        float dx = dem.MinX + (i + .5f) * Cell - p.x, d2 = dx * dx + dz * dz;
                        if (d2 < dist2[j * w + i]) dist2[j * w + i] = d2;
                    }
                }
            }

            // Instanzen pro (Zelle, Prefab)
            var batches = new Dictionary<(long, GameObject), Inst>();
            var rng = new System.Random(8080);
            int count = 0;
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float d2 = dist2[j * w + i];
                if (d2 < StartDistance * StartDistance || d2 > maxDistance * maxDistance) continue;
                float x = dem.MinX + (i + .5f + (float)(rng.NextDouble() - .5) * .8f) * Cell;
                float z = dem.MinZ + (j + .5f + (float)(rng.NextDouble() - .5) * .8f) * Cell;
                var biome = terrain.BiomeAt(x, z);
                if (biome == WorldTerrain.Biome.Urban || biome == WorldTerrain.Biome.Beach || biome == WorldTerrain.Biome.Water) continue;
                float above = terrain.DemY(x, z) - terrain.SeaY;
                if (above < 3f) continue;
                float slope = terrain.SlopeDeg(x, z);
                float n = Mathf.PerlinNoise(x * .012f + 11f, z * .012f + 5f);
                float clump = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.25f, .7f, n));
                float ravine = Ravine(terrain, x, z);
                float roll = (float)rng.NextDouble();

                List<GameObject> pool; float scale;
                float pRock = slope > 38f ? .25f : slope > 28f ? .08f : .01f;
                float pTree = biome == WorldTerrain.Biome.Forest ? .45f : (.02f + .35f * ravine) * (slope < 35f ? 1f : 0f);
                float pBush = (biome == WorldTerrain.Biome.Forest ? .15f : biome == WorldTerrain.Biome.Field ? .12f : .5f) * clump * (slope < 45f ? 1f : .3f);
                if (roll < pRock) { pool = rocks; scale = .8f + (float)rng.NextDouble() * 1.6f; }
                else if (roll < pRock + pTree) { pool = trees; scale = .7f + (float)rng.NextDouble() * .5f; }
                else if (roll < pRock + pTree + pBush) { pool = bushes; scale = .8f + (float)rng.NextDouble() * .7f; }
                else continue;
                if (pool.Count == 0 || !occupied.IsFree(x, z, 1.5f) || VegetationPlacer.OnStreet(terrain, x, z, 2f)) continue;

                // Art pro 40-m-Fleck
                int seed = Mathf.FloorToInt(x / 40f) * 73856093 ^ Mathf.FloorToInt(z / 40f) * 19349663 ^ pool.GetHashCode();
                var prefab = pool[new System.Random(seed).Next(pool.Count)];
                if (a.ForestTrees.Contains(prefab)) scale *= .6f;
                float y = terrain.HeightAt(x, z) - .15f;
                long cell = ((long)Mathf.FloorToInt(x / Bucket) << 32) | (uint)Mathf.FloorToInt(z / Bucket);
                var key = (cell, prefab);
                if (!batches.TryGetValue(key, out var list)) { list = new Inst(); batches[key] = list; }
                list.pos.Add(new Vector4(x, y, z, (float)rng.NextDouble() * 360f));
                list.scale.Add(scale);
                count++;
            }

            var field = new GameObject("SlopeVegetation").AddComponent<InstancedMeshField>();
            field.transform.SetParent(parent, false);
            field.drawDistance = maxDistance + 200f;
            field.shadows = ShadowCastingMode.Off;
            var mats = new HashSet<Material>();
            foreach (var kv in batches)
            {
                if (!parts.TryGetValue(kv.Key.Item2, out var pl)) { pl = InstanceParts(kv.Key.Item2); parts[kv.Key.Item2] = pl; }
                var pos = kv.Value.pos;
                var bounds = new Bounds(new Vector3(pos[0].x, pos[0].y, pos[0].z), Vector3.one * 20f);
                foreach (var q in pos) bounds.Encapsulate(new Bounds(new Vector3(q.x, q.y, q.z), Vector3.one * 20f));
                foreach (var part in pl)
                {
                    // Modell-Teilmatrix (Kind-Transform im Prefab) muss in jede Instanz: nur Position/Drehung/Skalierung
                    // der Wurzel werden gepackt, Teile mit Versatz bekommen eigene volle Matrizen.
                    var batch = new InstancedMeshField.Batch { mesh = part.mesh, submesh = part.sub, material = part.mat, bounds = bounds };
                    if (IsIdentity(part.local)) { batch.packed = pos.ToArray(); batch.scales = kv.Value.scale.ToArray(); }
                    else
                    {
                        batch.matrices = new Matrix4x4[pos.Count];
                        for (int k = 0; k < pos.Count; k++)
                            batch.matrices[k] = Matrix4x4.TRS(new Vector3(pos[k].x, pos[k].y, pos[k].z), Quaternion.Euler(0f, pos[k].w, 0f),
                                                              Vector3.one * kv.Value.scale[k]) * part.local;
                    }
                    field.batches.Add(batch);
                    mats.Add(part.mat);
                }
            }
            foreach (var m in mats)
                if (m != null && !m.enableInstancing) { m.enableInstancing = true; UnityEditor.EditorUtility.SetDirty(m); }
            Debug.Log($"Hangvegetation: {count} Instanzen in {field.batches.Count} Batches (bis {maxDistance:0} m).");
            return count;
        }

        // Meshes eines Prefabs; bei LODGroup nur die gröbste Stufe (Fernsicht, spart Vertices).
        public static List<Part> InstanceParts(GameObject prefab)
        {
            var result = new List<Part>();
            var root = prefab.transform.worldToLocalMatrix;
            IEnumerable<Renderer> renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            var lod = prefab.GetComponent<LODGroup>();
            if (lod != null)
            {
                var lods = lod.GetLODs();
                if (lods != null && lods.Length > 0 && lods[lods.Length - 1].renderers != null && lods[lods.Length - 1].renderers.Length > 0)
                    renderers = lods[lods.Length - 1].renderers;
            }
            foreach (var r in renderers)
            {
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                Matrix4x4 local = root * r.transform.localToWorldMatrix;
                for (int sIdx = 0; sIdx < mf.sharedMesh.subMeshCount; sIdx++)
                    result.Add(new Part { mesh = mf.sharedMesh, sub = sIdx, mat = mats[Mathf.Min(sIdx, mats.Length - 1)], local = local });
            }
            return result;
        }

        private static bool IsIdentity(Matrix4x4 m)
        {
            var id = Matrix4x4.identity;
            for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - id[i]) > 1e-4f) return false;
            return true;
        }

        private static float Ravine(WorldTerrain t, float x, float z)
        {
            const float e = 25f;
            float lap = (t.DemY(x + e, z) + t.DemY(x - e, z) + t.DemY(x, z + e) + t.DemY(x, z - e) - 4f * t.DemY(x, z)) / (e * e);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .004f, lap));
        }
    }
}
