using System.Collections.Generic;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Höhenmodell an die bekannte Straßenhöhe anpassen.
    // Das Höhenmodell (~16 m Raster) verschmiert Steilwände: an Klippenstraßen (Chapman's Peak) liegt es bis
    // 140 m ÜBER der Straße, anderswo einige Meter darunter. Statt dort einen Canyon zu graben, wird der Fehler
    // an der Straße gemessen (entlang der Route geglättet) und mit dem Abstand weich ausgeblendet abgezogen:
    // der Hang behält seine Form, die Straße liegt auf ihm. Fehler bis 3 m bleiben den Böschungen überlassen.
    public sealed class DemCorrection
    {
        private const float Cell = 20f, Tolerance = 3f;
        private readonly float[] corr; private readonly int cw, ch; private readonly float minX, minZ;
        public readonly float MaxCorrection; public readonly float CorrectedKm;

        public DemCorrection(DemGrid dem, float ele0, IList<Vector3> road)
        {
            minX = dem.MinX; minZ = dem.MinZ;
            cw = Mathf.CeilToInt((dem.MaxX - dem.MinX) / Cell) + 1;
            ch = Mathf.CeilToInt((dem.MaxZ - dem.MinZ) / Cell) + 1;
            corr = new float[cw * ch];
            int n = road.Count;
            if (n == 0) return;
            var err = new float[n];
            for (int i = 0; i < n; i++) err[i] = dem.Sample(road[i].x, road[i].z) - ele0 - road[i].y;
            float step = 0f; for (int i = 1; i < n; i++) step += Vector3.Distance(road[i - 1], road[i]); step /= Mathf.Max(1, n - 1);
            int win = Mathf.Max(3, Mathf.RoundToInt(50f / Mathf.Max(.5f, step)));      // ±50 m glätten
            var pre = new double[n + 1]; for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + err[i];
            float corrLen = 0f, maxC = 0f;
            int stride = Mathf.Max(1, Mathf.RoundToInt(6f / Mathf.Max(.5f, step)));
            for (int i = 0; i < n; i += stride)
            {
                int a = Mathf.Max(0, i - win), b = Mathf.Min(n - 1, i + win);
                float e = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
                e = Mathf.Sign(e) * Mathf.Max(0f, Mathf.Abs(e) - Tolerance);
                if (e == 0f) continue;
                corrLen += step * stride; maxC = Mathf.Max(maxC, Mathf.Abs(e));
                float D = Mathf.Clamp(30f + Mathf.Abs(e), 40f, 160f);                  // größere Fehler weiter ausblenden
                var p = road[i];
                int i0 = Mathf.Max(0, Mathf.FloorToInt((p.x - D - minX) / Cell)), i1 = Mathf.Min(cw - 1, Mathf.CeilToInt((p.x + D - minX) / Cell));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((p.z - D - minZ) / Cell)), j1 = Mathf.Min(ch - 1, Mathf.CeilToInt((p.z + D - minZ) / Cell));
                for (int jj = j0; jj <= j1; jj++)
                for (int ii = i0; ii <= i1; ii++)
                {
                    float dx = minX + ii * Cell - p.x, dz = minZ + jj * Cell - p.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > D) continue;
                    float t = 1f - d / D; t = t * t * (3f - 2f * t);
                    float v = e * t;
                    int k = jj * cw + ii;
                    if (Mathf.Abs(v) > Mathf.Abs(corr[k])) corr[k] = v;          // stärkste Korrektur gewinnt
                }
            }
            MaxCorrection = maxC; CorrectedKm = corrLen / 1000f;
            if (corrLen > 0f) Debug.Log($"Höhenmodell an Straße angepasst: {CorrectedKm:0.0} km mit Abweichung > {Tolerance} m, max. {maxC:0} m.");
        }

        public float At(float x, float z)
        {
            float fx = (x - minX) / Cell, fz = (z - minZ) / Cell;
            int i = Mathf.Clamp(Mathf.FloorToInt(fx), 0, cw - 2), j = Mathf.Clamp(Mathf.FloorToInt(fz), 0, ch - 2);
            float tx = Mathf.Clamp01(fx - i), tz = Mathf.Clamp01(fz - j);
            float a = Mathf.Lerp(corr[j * cw + i], corr[j * cw + i + 1], tx), b = Mathf.Lerp(corr[(j + 1) * cw + i], corr[(j + 1) * cw + i + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }
    }
}
