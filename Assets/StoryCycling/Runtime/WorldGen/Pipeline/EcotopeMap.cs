using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Ökotope der Kap-Halbinsel. Statt Zufallswürfen je Zelle entscheidet hier ein Regelwerk aus
    //   Landbedeckung (ESA WorldCover, wenn vorhanden) + OSM (landuse/natural, Gebäude) + Gelände (Neigung, Höhe, Hangrichtung,
    //   Geländeform, Küstenabstand),
    // welche Lebensgemeinschaft wo wächst. Gelände-Farbe (TerrainPaint) und Vegetation (VegetationScatter) lesen dieselbe Karte,
    // deshalb passen Farbe und Bewuchs zusammen.
    public enum Eco : byte
    {
        Water, Beach, DuneScrub, CoastRock, Urban, Garden, Field, Vlei, Fynbos, Restio, Sandstone, RavineForest, Plantation, Woodland, Count
    }

    public sealed class EcotopeMap
    {
        public const float Cell = WorldTerrain.RasterCell;      // gleiches Raster wie Farbtextur/OSM-Biome (10 m)
        public readonly int W, H;
        public readonly float MinX, MinZ, SeaY;
        public readonly float[] Height;       // Gelände (ohne Straßeneinschnitt), m relativ zum GPX-Start
        public readonly float[] Slope;        // Grad
        public readonly float[] Aspect;       // Grad, Richtung, in die der Hang blickt (0 = Nord, 90 = Ost)
        public readonly float[] Ravine;       // 0..1: konkave Stellen (Schluchten/Rinnen)
        public readonly float[] Wet;          // 0..1: Entwässerung (D8-Fließakkumulation, 2 Zellen aufgeweitet): Bachläufe, Kloofs, Talsohlen
        public readonly float[] Contour;      // Grad: geglättete Höhenlinienrichtung (0 = Nord, 90 = Ost), für Muster entlang der Hänge
        public readonly float[] CutFill;      // m: Straßenböschung minus natürliches Gelände (nur < 70 m von der Route/Querstraße, sonst 0)
        public readonly float[] WaterDist;    // m bis zum Meer (gekappt bei 1500 m)
        public readonly float[] RoadDist;     // m bis zur Route (Chamfer, ±3 %), gekappt bei 6000 m
        public readonly byte[] Kind;          // Eco
        public readonly byte[] Lc;            // LandCoverClass (0 = unbekannt)
        public readonly bool[] Building;      // OSM-Gebäude (+10 m)
        public bool HasLandCover;

        private EcotopeMap(WorldTerrain t)
        {
            var dem = t.Dem;
            MinX = dem.MinX; MinZ = dem.MinZ; SeaY = t.SeaY;
            W = Mathf.CeilToInt((dem.MaxX - dem.MinX) / Cell) + 1;
            H = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / Cell) + 1;
            int n = W * H;
            Height = new float[n]; Slope = new float[n]; Aspect = new float[n]; Ravine = new float[n];
            Wet = new float[n]; Contour = new float[n]; CutFill = new float[n];
            WaterDist = new float[n]; RoadDist = new float[n]; Kind = new byte[n]; Lc = new byte[n]; Building = new bool[n];
        }

        // ---------------------------------------------------------------- Abfragen
        public int Index(float x, float z)
        {
            int i = Mathf.Clamp(Mathf.FloorToInt((x - MinX) / Cell), 0, W - 1), j = Mathf.Clamp(Mathf.FloorToInt((z - MinZ) / Cell), 0, H - 1);
            return j * W + i;
        }
        public bool Inside(float x, float z) => x >= MinX && z >= MinZ && x < MinX + W * Cell && z < MinZ + H * Cell;
        public Eco At(float x, float z) => (Eco)Kind[Index(x, z)];
        public float SlopeAt(float x, float z) => Slope[Index(x, z)];
        public float RoadDistAt(float x, float z) => RoadDist[Index(x, z)];

        // Ökotop mit verrauschter Suchposition: Grenzen verlaufen organisch statt im 10-m-Raster.
        public Eco AtWarped(float x, float z)
        {
            float wx = (EcoNoise.Fbm(x, z, 70f, 11, 2) - .5f) * 36f, wz = (EcoNoise.Fbm(x, z, 70f, 12, 2) - .5f) * 36f;
            return At(x + wx, z + wz);
        }

        // ---------------------------------------------------------------- Aufbau
        public static EcotopeMap Build(WorldTerrain t, OsmContext osm, LandCoverGrid lc)
        {
            var m = new EcotopeMap(t);
            m.HasLandCover = lc != null;
            int W = m.W, H = m.H;

            Parallel.For(0, H, j =>
            {
                float z = m.MinZ + (j + .5f) * Cell;
                for (int i = 0; i < W; i++)
                {
                    float x = m.MinX + (i + .5f) * Cell;
                    m.Height[j * W + i] = t.DemY(x, z);
                    m.Lc[j * W + i] = lc != null ? lc.Sample(x, z) : (byte)0;
                }
            });
            m.ComputeTerrainAttributes();
            m.ComputeFlow();
            m.ComputeWaterDist();
            m.ComputeRoadDist(t.Road);
            m.ComputeCutFill(t);
            if (osm != null) m.RasterizeBuildings(osm);

            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    int k = j * W + i;
                    m.Kind[k] = (byte)m.Classify(t, i, j, k);
                }
            });
            m.DropOffshoreSpecks(2000, 6f, 100f);
            m.ModeFilter();
            m.MergeSmallPlantations(300);
            return m;
        }

        private void ComputeTerrainAttributes()
        {
            var gx = new float[W * H]; var gz = new float[W * H];
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    int k = j * W + i;
                    float hE = Height[j * W + Mathf.Min(W - 1, i + 1)], hW = Height[j * W + Mathf.Max(0, i - 1)];
                    float hN = Height[Mathf.Min(H - 1, j + 1) * W + i], hS = Height[Mathf.Max(0, j - 1) * W + i];
                    float dx = (hE - hW) / (2f * Cell), dz = (hN - hS) / (2f * Cell);
                    Slope[k] = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
                    // Hang blickt bergab: Richtung = -Gradient
                    float a = Mathf.Atan2(-dx, -dz) * Mathf.Rad2Deg; if (a < 0f) a += 360f;
                    Aspect[k] = a;
                    // Konkavität: Mittel der 4 Nachbarn in 30 m Abstand minus Mitte
                    int s = 3;
                    float hx1 = Height[j * W + Mathf.Min(W - 1, i + s)], hx0 = Height[j * W + Mathf.Max(0, i - s)];
                    float hz1 = Height[Mathf.Min(H - 1, j + s) * W + i], hz0 = Height[Mathf.Max(0, j - s) * W + i];
                    float lap = (hx1 + hx0 + hz1 + hz0 - 4f * Height[k]) / ((s * Cell) * (s * Cell));
                    Ravine[k] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .0045f, lap));
                    gx[k] = dx; gz[k] = dz;
                }
            });
            // Höhenlinienrichtung aus dem über ~90 m geglätteten Gefälle (senkrecht zum Gradienten)
            var bx = Blur(gx, 4); var bz = Blur(gz, 4);
            for (int k = 0; k < W * H; k++)
            {
                float a = Mathf.Atan2(-bz[k], bx[k]) * Mathf.Rad2Deg;       // Richtung (-gz, gx) = senkrecht zum Gefälle
                Contour[k] = a < 0f ? a + 360f : a;
            }
        }

        // Kastenmittel (getrennt in x/z), Rand wird abgeschnitten.
        private float[] Blur(float[] src, int r)
        {
            var tmp = new float[src.Length]; var dst = new float[src.Length];
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    float sum = 0f; int c = 0;
                    for (int d = -r; d <= r; d++) { int ii = i + d; if (ii < 0 || ii >= W) continue; sum += src[j * W + ii]; c++; }
                    tmp[j * W + i] = sum / c;
                }
            });
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    float sum = 0f; int c = 0;
                    for (int d = -r; d <= r; d++) { int jj = j + d; if (jj < 0 || jj >= H) continue; sum += tmp[jj * W + i]; c++; }
                    dst[j * W + i] = sum / c;
                }
            });
            return dst;
        }

        // D8-Fließakkumulation auf dem leicht geglätteten Gelände: von hoch nach tief gibt jede Zelle ihre Einzugsfläche an den steilsten
        // tieferen Nachbarn weiter. Daraus entstehen die Bachläufe/Kloofs (dunkler Wald, Farne, Dickicht) als LINIEN im Gelände statt als
        // Zufallsflecken. Zellen unter dem Meeresspiegel beenden den Lauf. Senken (Pits) halten ihr Wasser: für Vegetation unschädlich.
        private void ComputeFlow()
        {
            int n = W * H;
            var hs = new float[n];
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    float sum = 0f; int c = 0;
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        int jj = j + dj; if (jj < 0 || jj >= H) continue;
                        for (int di = -1; di <= 1; di++) { int ii = i + di; if (ii < 0 || ii >= W) continue; sum += Height[jj * W + ii]; c++; }
                    }
                    hs[j * W + i] = sum / c;
                }
            });
            var keys = new float[n]; var order = new int[n];
            for (int k = 0; k < n; k++) { keys[k] = -hs[k]; order[k] = k; }
            System.Array.Sort(keys, order);                       // aufsteigend nach -h = absteigend nach h
            keys = null;
            var acc = new float[n];
            for (int k = 0; k < n; k++) acc[k] = 1f;
            for (int q = 0; q < n; q++)
            {
                int k = order[q];
                if (Height[k] < SeaY) continue;
                int i = k % W, j = k / W, bk = -1; float best = 0f;
                for (int dj = -1; dj <= 1; dj++)
                {
                    int jj = j + dj; if (jj < 0 || jj >= H) continue;
                    for (int di = -1; di <= 1; di++)
                    {
                        int ii = i + di; if ((di == 0 && dj == 0) || ii < 0 || ii >= W) continue;
                        int nk = jj * W + ii;
                        float drop = (hs[k] - hs[nk]) * ((di == 0 || dj == 0) ? 1f : .7071f);
                        if (drop > best) { best = drop; bk = nk; }
                    }
                }
                if (bk >= 0) acc[bk] += acc[k];
            }
            // 1,8 ha Einzugsgebiet = 0, 40 ha = 1; danach 2 Zellen (20 m) aufweiten, damit auch breite Rinnen erfasst werden
            var w0 = new float[n];
            for (int k = 0; k < n; k++) w0[k] = Mathf.Clamp01((Mathf.Log(acc[k], 2f) - 7.5f) / 4.5f);
            Parallel.For(0, H, j =>
            {
                for (int i = 0; i < W; i++)
                {
                    float m = 0f;
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        int jj = j + dj; if (jj < 0 || jj >= H) continue;
                        for (int di = -2; di <= 2; di++)
                        {
                            int ii = i + di; if (ii < 0 || ii >= W) continue;
                            float f = 1f - .22f * Mathf.Sqrt(di * di + dj * dj);
                            if (f <= 0f) continue;
                            m = Mathf.Max(m, w0[jj * W + ii] * f);
                        }
                    }
                    Wet[j * W + i] = m;
                }
            });
        }

        // Straßenböschung: wie weit weicht die tatsächliche Geländehöhe (Einschnitt/Damm) vom DEM ab? Nur nahe der Straße.
        private void ComputeCutFill(WorldTerrain t)
        {
            Parallel.For(0, H, j =>
            {
                float z = MinZ + (j + .5f) * Cell;
                for (int i = 0; i < W; i++)
                {
                    int k = j * W + i;
                    if (RoadDist[k] > 70f) continue;
                    CutFill[k] = t.HeightAt(MinX + (i + .5f) * Cell, z) - Height[k];
                }
            });
        }

        // DEM-Rauschen vor der Küste: kleine, flache Landflecken, die vom Festland durch Meer (Gelände < Meeresspiegel + 0,3 m)
        // getrennt sind, wären sonst grüne Inseln mit Pflanzen (mit WorldCover "Wasser" sogar Schilf-Vlei). Sie werden Wasser
        // (= nasser Sand, keine Vegetation). Echte Inseln sind höher/größer, alles in Straßennähe oder mit Gebäuden bleibt.
        private void DropOffshoreSpecks(int maxCells, float maxAbove, float minRoadDist)
        {
            float landY = SeaY + .3f;
            var seen = new bool[W * H]; var stack = new Stack<int>(); var comp = new List<int>();
            for (int k0 = 0; k0 < Height.Length; k0++)
            {
                if (seen[k0] || Height[k0] < landY) continue;
                comp.Clear(); stack.Push(k0); seen[k0] = true;
                bool keep = false;
                while (stack.Count > 0)
                {
                    int k = stack.Pop(); comp.Add(k);
                    if (comp.Count > maxCells || Height[k] - SeaY >= maxAbove || RoadDist[k] < minRoadDist || Building[k]) keep = true;
                    int i = k % W, j = k / W;
                    if (i > 0) VisitLand(k - 1, landY, seen, stack); if (i < W - 1) VisitLand(k + 1, landY, seen, stack);
                    if (j > 0) VisitLand(k - W, landY, seen, stack); if (j < H - 1) VisitLand(k + W, landY, seen, stack);
                }
                if (!keep) foreach (int k in comp) Kind[k] = (byte)Eco.Water;
            }
        }
        private void VisitLand(int k, float landY, bool[] seen, Stack<int> stack)
        {
            if (seen[k] || Height[k] < landY) return;
            seen[k] = true; stack.Push(k);
        }

        // Pflanzungen kleiner als minCells Zellen (z. B. Gartenbäume, Windschutz) sind keine Forstabteilung: Gehölz statt Reihenforst.
        private void MergeSmallPlantations(int minCells)
        {
            var seen = new bool[W * H]; var stack = new Stack<int>(); var comp = new List<int>();
            for (int k0 = 0; k0 < Kind.Length; k0++)
            {
                if (seen[k0] || Kind[k0] != (byte)Eco.Plantation) continue;
                comp.Clear(); stack.Push(k0); seen[k0] = true;
                while (stack.Count > 0)
                {
                    int k = stack.Pop(); comp.Add(k);
                    int i = k % W, j = k / W;
                    if (i > 0) Visit(k - 1, seen, stack); if (i < W - 1) Visit(k + 1, seen, stack);
                    if (j > 0) Visit(k - W, seen, stack); if (j < H - 1) Visit(k + W, seen, stack);
                }
                if (comp.Count < minCells) foreach (int k in comp) Kind[k] = (byte)Eco.Woodland;
            }
        }
        private void Visit(int k, bool[] seen, Stack<int> stack)
        {
            if (seen[k] || Kind[k] != (byte)Eco.Plantation) return;
            seen[k] = true; stack.Push(k);
        }

        // Zweipass-Chamfer-Abstandstransformation (5-7-11-Maske, Fehler ~2 %).
        private static void Chamfer(float[] d, int W, int H, float cap)
        {
            const float a = 1f, b = 1.4f, c = 2.2f;       // Zellen: 1, sqrt2, sqrt5
            for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                float v = d[j * W + i];
                if (i > 0) v = Mathf.Min(v, d[j * W + i - 1] + a);
                if (j > 0)
                {
                    v = Mathf.Min(v, d[(j - 1) * W + i] + a);
                    if (i > 0) v = Mathf.Min(v, d[(j - 1) * W + i - 1] + b);
                    if (i < W - 1) v = Mathf.Min(v, d[(j - 1) * W + i + 1] + b);
                    if (i > 1) v = Mathf.Min(v, d[(j - 1) * W + i - 2] + c);
                    if (i < W - 2) v = Mathf.Min(v, d[(j - 1) * W + i + 2] + c);
                }
                if (j > 1)
                {
                    if (i > 0) v = Mathf.Min(v, d[(j - 2) * W + i - 1] + c);
                    if (i < W - 1) v = Mathf.Min(v, d[(j - 2) * W + i + 1] + c);
                }
                d[j * W + i] = v;
            }
            for (int j = H - 1; j >= 0; j--)
            for (int i = W - 1; i >= 0; i--)
            {
                float v = d[j * W + i];
                if (i < W - 1) v = Mathf.Min(v, d[j * W + i + 1] + a);
                if (j < H - 1)
                {
                    v = Mathf.Min(v, d[(j + 1) * W + i] + a);
                    if (i < W - 1) v = Mathf.Min(v, d[(j + 1) * W + i + 1] + b);
                    if (i > 0) v = Mathf.Min(v, d[(j + 1) * W + i - 1] + b);
                    if (i < W - 2) v = Mathf.Min(v, d[(j + 1) * W + i + 2] + c);
                    if (i > 1) v = Mathf.Min(v, d[(j + 1) * W + i - 2] + c);
                }
                if (j < H - 2)
                {
                    if (i < W - 1) v = Mathf.Min(v, d[(j + 2) * W + i + 1] + c);
                    if (i > 0) v = Mathf.Min(v, d[(j + 2) * W + i - 1] + c);
                }
                d[j * W + i] = Mathf.Min(v, cap / Cell);
            }
            for (int k = 0; k < d.Length; k++) d[k] *= Cell;
        }

        private void ComputeWaterDist()
        {
            for (int k = 0; k < WaterDist.Length; k++) WaterDist[k] = Height[k] < SeaY + .3f ? 0f : 1e9f;
            Chamfer(WaterDist, W, H, 1500f);
        }

        private void ComputeRoadDist(RoadField road)
        {
            for (int k = 0; k < RoadDist.Length; k++) RoadDist[k] = 1e9f;
            foreach (var s in road.Samples)
            {
                int i = Mathf.FloorToInt((s.pos.x - MinX) / Cell), j = Mathf.FloorToInt((s.pos.z - MinZ) / Cell);
                if (i >= 0 && j >= 0 && i < W && j < H) RoadDist[j * W + i] = 0f;
            }
            Chamfer(RoadDist, W, H, 6000f);
        }

        private void RasterizeBuildings(OsmContext osm)
        {
            var xs = new List<float>();
            foreach (var b in osm.Buildings)
            {
                var ring = b.ring;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in ring) { minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
                int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - MinZ) / Cell) - 1), j1 = Mathf.Min(H - 1, Mathf.CeilToInt((maxZ - MinZ) / Cell) + 1);
                float minX = float.MaxValue, maxX = float.MinValue;
                foreach (var p in ring) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); }
                int i0 = Mathf.Max(0, Mathf.FloorToInt((minX - MinX) / Cell) - 1), i1 = Mathf.Min(W - 1, Mathf.CeilToInt((maxX - MinX) / Cell) + 1);
                // Zellen, deren Mittelpunkt im Umriss liegt oder weniger als 6 m daneben (Hof, Terrasse)
                for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var c = new Vector2(MinX + (i + .5f) * Cell, MinZ + (j + .5f) * Cell);
                    if (OsmContext.PointInPolygon(c, ring) || NearRing(c, ring, 6f)) Building[j * W + i] = true;
                }
            }
            xs.Clear();
        }

        private static bool NearRing(Vector2 p, List<Vector2> ring, float r)
        {
            float r2 = r * r;
            for (int a = 0, b = ring.Count - 1; a < ring.Count; b = a++)
            {
                Vector2 s = ring[b], e = ring[a], d = e - s; float l2 = d.sqrMagnitude;
                float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - s, d) / l2) : 0f;
                if ((s + d * t - p).sqrMagnitude < r2) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- Regeln
        // Reihenfolge = Priorität. Jede Landbedeckungsklasse (ESA WorldCover) bekommt auf JEDEM Gelände eine sinnvolle Entscheidung;
        // ohne Landbedeckung (hasLc = false) tragen OSM und die Geländeform allein.
        private Eco Classify(WorldTerrain t, int i, int j, int k)
        {
            float x = MinX + (i + .5f) * Cell, z = MinZ + (j + .5f) * Cell;
            float h = Height[k], above = h - SeaY, slope = Slope[k], ravine = Ravine[k], wet = Wet[k], wd = WaterDist[k];
            var osm = t.BiomeAt(x, z);
            byte lc = Lc[k];
            bool hasLc = HasLandCover && lc != LandCoverClass.Unknown;

            if (above < .3f) return Eco.Water;
            if (osm == WorldTerrain.Biome.Water && above < 1.5f) return Eco.Water;

            // Feuchtgebiete: Noordhoek-Vlei, Teiche, Stauseen
            if ((lc == LandCoverClass.Wetland || (lc == LandCoverClass.Water && above >= 1.5f)) && slope < 10f) return Eco.Vlei;
            if (osm == WorldTerrain.Biome.Water && above >= 1.5f && slope < 8f) return Eco.Vlei;

            // Siedlung: Häuser/Straßen = Urban, bebaute Fläche dazwischen = Gärten
            bool built = lc == LandCoverClass.Built || osm == WorldTerrain.Biome.Urban;
            if (built && slope < 34f) return Building[k] ? Eco.Urban : Eco.Garden;
            if (Building[k] && slope < 34f) return Eco.Urban;

            // Felswände: steile Hänge, in horizontalen Sandsteinbändern (Tafelberg-Sandstein bildet Stufen)
            float strata = Mathf.Sin((h / 26f + EcoNoise.Fbm(x, z, 500f, 3, 2) * 2.2f) * 6.2831853f);
            float slopeEff = slope + (slope > 22f ? strata * 4.5f : 0f);
            bool steepRock = slopeEff >= 37f || (osm == WorldTerrain.Biome.Rock && slope >= 15f) || (lc == LandCoverClass.Bare && slope >= 18f);
            if (steepRock) return wd < 120f && above < 80f ? Eco.CoastRock : Eco.Sandstone;

            // Strand: flacher Küstenstreifen bzw. Landbedeckung "karg/Sand" an der Küste
            bool sandLc = lc == LandCoverClass.Bare && above < 14f && slope < 14f && wd < 400f;
            bool flatCoast = !hasLc && above < 4.5f && slope < 9f && wd < 35f;
            if (osm == WorldTerrain.Biome.Beach || sandLc || flatCoast) return Eco.Beach;

            // Felsküste (Chapman's Peak: Granit-Findlinge am Ufer)
            if (wd < 60f && slope >= 13f && above < 90f) return Eco.CoastRock;

            // Bäume: Schluchtwald (Hang/Rinne), Küstengehölz (Milkwood/Port Jackson), Forst (flach, Reihen), sonst Gehölz
            bool treeLc = lc == LandCoverClass.Tree || lc == LandCoverClass.Mangrove;
            bool osmForest = osm == WorldTerrain.Biome.Forest && (!hasLc || lc == LandCoverClass.Tree || (lc == LandCoverClass.Shrub && slope < 22f));
            if (treeLc || osmForest)
            {
                if (ravine >= .25f || wet >= .45f || slope >= 22f) return Eco.RavineForest;
                if (above < 45f && wd < 700f) return Eco.Woodland;
                if (slope < 24f && above > 6f) return Eco.Plantation;      // kleine Flecken werden später zu Gehölz (MergeSmallPlantations)
                return Eco.Woodland;
            }

            // Ohne Landbedeckung: Schluchtwald aus Geländeform (Rinnen mit Einzugsgebiet, mittlere Höhe, eher schattig)
            if (!hasLc && (ravine > .55f || wet > .6f) && slope > 10f && slope < 34f && above > 40f)
            {
                float south = Mathf.Cos(Aspect[k] * Mathf.Deg2Rad);                                       // -1 = Südhang
                if (EcoNoise.Fbm(x, z, 140f, 8, 2) + (1f - south) * .12f + wet * .25f > .6f) return Eco.RavineForest;
            }

            // Grasland / Acker / Weide: Tiefland = Weide, am Berg (Brandflächen, Restioveld) = Restio-Fynbos
            if (lc == LandCoverClass.Grass || lc == LandCoverClass.Crop || osm == WorldTerrain.Biome.Field)
            {
                bool lowland = above < 130f && slope < 14f;
                return lowland ? Eco.Field : Eco.Restio;
            }

            // Karge Flächen (Plateaus, Brand, Steinbruch) ohne Steilhang: lückiges Restioveld
            if (lc == LandCoverClass.Bare || lc == LandCoverClass.Moss) return Eco.Restio;

            // Küstendünen: niedrige, flache Küstenvegetation hinter dem Strand (Noordhoek, Kommetjie)
            if (above < 45f && wd < 700f && slope < 13f && osm != WorldTerrain.Biome.Urban &&
                (lc == LandCoverClass.Shrub || lc == LandCoverClass.Unknown)) return Eco.DuneScrub;

            return Eco.Fynbos;
        }

        // Mehrheitsfilter 3x3: entfernt Einzelpixel (Landbedeckung rauscht), Wasser bleibt unberührt.
        private void ModeFilter()
        {
            var src = (byte[])Kind.Clone();
            Parallel.For(1, H - 1, j =>
            {
                var cnt = new int[(int)Eco.Count];
                for (int i = 1; i < W - 1; i++)
                {
                    int k = j * W + i; var e = src[k];
                    if (e == (byte)Eco.Water || e == (byte)Eco.Urban) continue;
                    for (int q = 0; q < cnt.Length; q++) cnt[q] = 0;
                    for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++) cnt[src[(j + dj) * W + i + di]]++;
                    if (cnt[e] > 2) continue;
                    int best = e, bc = cnt[e];
                    for (int q = 0; q < cnt.Length; q++) if (q != (int)Eco.Water && q != (int)Eco.Urban && cnt[q] > bc) { bc = cnt[q]; best = q; }
                    Kind[k] = (byte)best;
                }
            });
        }

        public string CoverageText(float maxDist)
        {
            var c = Coverage(maxDist, out float area);
            var sb = new System.Text.StringBuilder($"{area:0.0} km² Land: ");
            for (int q = 0; q < c.Length - 1; q++) if (q != (int)Eco.Water && c[q] >= .002f) sb.Append($"{(Eco)q} {c[q]:P0}, ");
            return sb.ToString().TrimEnd(' ', ',');
        }

        // Flächenanteile je Ökotop im Streifen bis maxDist von der Route (nur Land).
        public float[] Coverage(float maxDist, out float landArea)
        {
            var c = new long[(int)Eco.Count]; long total = 0;
            for (int k = 0; k < Kind.Length; k++)
            {
                if (RoadDist[k] > maxDist || Kind[k] == (byte)Eco.Water) continue;
                c[Kind[k]]++; total++;
            }
            var r = new float[c.Length];
            for (int q = 0; q < c.Length; q++) r[q] = total > 0 ? c[q] / (float)total : 0f;
            landArea = total * Cell * Cell / 1e6f;
            return r;
        }
    }
}
