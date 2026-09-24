using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace StoryCycling.WorldGen
{
    // Textformat der gebackenen Route (eine Zeile pro Punkt, invariante Kultur):
    //   x y z lane half inset
    // lane = seitlicher Versatz des Fahrers, half = halbe Fahrbahnbreite, inset = Seitenstreifenbreite.
    public static class BakedRoute
    {
        private const string Header = "# StoryCycling baked route v1: x y z lane half inset";

        public static string Write(IList<Vector3> pts, IList<float> lane, IList<float> half, IList<float> inset)
        {
            var sb = new StringBuilder(Header).Append('\n');
            var c = CultureInfo.InvariantCulture;
            for (int i = 0; i < pts.Count; i++)
                sb.Append(pts[i].x.ToString("0.###", c)).Append(' ').Append(pts[i].y.ToString("0.###", c)).Append(' ')
                  .Append(pts[i].z.ToString("0.###", c)).Append(' ').Append(lane[i].ToString("0.###", c)).Append(' ')
                  .Append(half[i].ToString("0.###", c)).Append(' ').Append(inset[i].ToString("0.###", c)).Append('\n');
            return sb.ToString();
        }

        public static bool TryRead(string text, out List<Vector3> pts, out List<float> lane)
        {
            return TryRead(text, out pts, out lane, out _, out _);
        }

        public static bool TryRead(string text, out List<Vector3> pts, out List<float> lane, out List<float> half, out List<float> inset)
        {
            pts = new List<Vector3>(); lane = new List<float>(); half = new List<float>(); inset = new List<float>();
            var c = CultureInfo.InvariantCulture;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(' ');
                if (f.Length < 6) return false;
                pts.Add(new Vector3(float.Parse(f[0], c), float.Parse(f[1], c), float.Parse(f[2], c)));
                lane.Add(float.Parse(f[3], c)); half.Add(float.Parse(f[4], c)); inset.Add(float.Parse(f[5], c));
            }
            return pts.Count >= 2;
        }
    }
}
