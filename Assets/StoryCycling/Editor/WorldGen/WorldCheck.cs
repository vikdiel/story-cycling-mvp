using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Prüfbericht nach jedem Bau: feste Garantien der Straßenwelt, gemessen an der tatsächlich gebauten Fläche.
    // Verstöße erscheinen als Warnung in der Console (mit Ort), der Rest als eine Zusammenfassung.
    public static class WorldCheck
    {
        public sealed class Report
        {
            public int DrivePoints, DriveOff; public float LongestGapM, LongestGapKm;
            public int RoadProbes, RoadOff, BandTris, BandOnAsphalt;
            public int Roundabouts, IslandsFilled;
            public float KmOneLanePerDir, KmTwoPlusPerDir, KmOneway;
            public readonly List<string> Warnings = new List<string>();
            public bool Ok => Warnings.Count == 0;
        }

        public static Report Run(RoadNet net, List<Vector3> drive, RoadSurface.Result surf, OsmContext osm)
        {
            var r = new Report();
            if (net == null || surf == null) return r;
            var asp = surf.Asphalt;

            // 1) Fahrlinie liegt vollständig auf Asphalt
            if (drive != null && drive.Count > 1)
            {
                float acc = 0f, runStart = -1f;
                for (int i = 0; i < drive.Count; i++)
                {
                    if (i > 0) acc += Vector3.Distance(drive[i - 1], drive[i]);
                    r.DrivePoints++;
                    bool off = !asp.Inside(drive[i].x, drive[i].z);
                    if (off) { r.DriveOff++; if (runStart < 0f) runStart = acc; }
                    if ((!off || i == drive.Count - 1) && runStart >= 0f)
                    {
                        float len = acc - runStart + 1f;
                        if (len > r.LongestGapM) { r.LongestGapM = len; r.LongestGapKm = runStart / 1000f; }
                        runStart = -1f;
                    }
                }
                if (r.LongestGapM > 3f) r.Warnings.Add($"Fahrlinie ohne Asphalt: {r.DriveOff} Punkte, längstes Stück {r.LongestGapM:0} m bei km {r.LongestGapKm:0.00}.");
            }

            // 2) Fahrbahnproben (Mitte, ±80 % der halben Breite) liegen auf Asphalt; Spurstatistik
            foreach (var sg in net.Segs)
                for (int k = 0; k < sg.S.Count; k += 3)
                {
                    var s = sg.S[k];
                    foreach (float f in new[] { 0f, -.8f, .8f })
                    {
                        var q = s.pos + s.side * (f * s.half);
                        r.RoadProbes++; if (!asp.Inside(q.x, q.z)) r.RoadOff++;
                    }
                    if (sg.Fallback || k >= sg.Lanes.Count) continue;
                    var li = sg.Lanes[k]; float km = RoadNet.SampleStep * 3f / 1000f;
                    if (li.Dir != 0) r.KmOneway += km;
                    int perDir = li.Dir != 0 ? li.N : Mathf.Max(li.L, li.R);
                    if (perDir >= 2) r.KmTwoPlusPerDir += km; else r.KmOneLanePerDir += km;
                }
            if (r.RoadProbes > 0 && r.RoadOff > r.RoadProbes * .005f)
                r.Warnings.Add($"Fahrbahn ohne Asphalt: {r.RoadOff} von {r.RoadProbes} Proben ({(float)r.RoadOff / r.RoadProbes:P1}).");

            // 3) Gehweg/Randstreifen liegt nie auf der Fahrbahn
            r.BandTris = surf.Band.Count; r.BandOnAsphalt = surf.Band.CentroidsInside(asp);
            if (r.BandOnAsphalt > r.BandTris * .001f)
                r.Warnings.Add($"Gehweg/Randstreifen auf Asphalt: {r.BandOnAsphalt} von {r.BandTris} Dreiecken.");

            // 4) Kreisverkehrsinseln bleiben frei
            if (osm != null)
            {
                var rings = osm.Streets.Where(st => st.roundabout).ToList(); var used = new bool[rings.Count];
                for (int i = 0; i < rings.Count; i++)
                {
                    if (used[i]) continue; used[i] = true;
                    var grp = new List<Vector2>(rings[i].pts);
                    for (bool grow = true; grow;)
                    {
                        grow = false;
                        for (int j = 0; j < rings.Count; j++)
                            if (!used[j] && rings[j].pts.Any(p => grp.Any(g => (g - p).sqrMagnitude < 1f))) { grp.AddRange(rings[j].pts); used[j] = true; grow = true; }
                    }
                    var c = new Vector2(grp.Average(p => p.x), grp.Average(p => p.y));
                    float rad = grp.Average(p => (p - c).magnitude), ri = rad - 4.5f;
                    if (rad < 6f || ri < 1f || !net.Segs.Any(sg => sg.Roundabout && sg.S.Count > 0 &&
                        (new Vector2(sg.S[0].pos.x, sg.S[0].pos.z) - c).magnitude < rad + 10f)) continue;
                    r.Roundabouts++;
                    int tot = 0, on = 0;
                    for (float x = -ri; x <= ri; x += .5f)
                        for (float z = -ri; z <= ri; z += .5f)
                        {
                            if (x * x + z * z > ri * ri) continue;
                            tot++; if (asp.Inside(c.x + x, c.y + z)) on++;
                        }
                    if (tot > 0 && on > tot * .15f)
                    {
                        r.IslandsFilled++;
                        r.Warnings.Add($"Kreisverkehrsinsel bei ({c.x:0}, {c.y:0}) zu {100f * on / tot:0} % asphaltiert.");
                    }
                }
            }

            // Zusammenfassung
            Debug.Log($"Prüfbericht Straßenwelt: Fahrlinie {(r.DrivePoints > 0 ? 1f - (float)r.DriveOff / r.DrivePoints : 1f):P1} auf Asphalt; " +
                      $"Fahrbahnproben {(r.RoadProbes > 0 ? 1f - (float)r.RoadOff / r.RoadProbes : 1f):P1}; Gehweg auf Asphalt {r.BandOnAsphalt}/{r.BandTris}; " +
                      $"Kreisverkehre {r.Roundabouts - r.IslandsFilled}/{r.Roundabouts} mit freier Insel; " +
                      $"Spuren: {r.KmOneLanePerDir:0.0} km 1 Spur/Richtung, {r.KmTwoPlusPerDir:0.0} km ≥2 Spuren/Richtung ({r.KmOneway:0.0} km Einbahn); " +
                      $"Markierungen: {surf.CenterLineM / 1000f:0.0} km Mittellinie, {surf.LaneLineM / 1000f:0.0} km Spurtrenner, " +
                      $"{surf.EdgeLineM / 1000f:0.0} km Randlinie, {surf.StopLines} Halte-, {surf.YieldLines} Wartelinien" +
                      (r.Ok ? " — alle Garantien erfüllt." : $" — {r.Warnings.Count} Verstöße:"));
            foreach (var w in r.Warnings) Debug.LogWarning("Prüfbericht: " + w);
            return r;
        }
    }
}
