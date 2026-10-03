using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryCycling.WorldGen.Editor
{
    // Editor: Verkehr wie auf dem Gerät (TrafficWorld) + Spurgraph als Datei <route>.traffic.txt neben der GPX, die das TrafficSystem beim Start liest.
    public static class TrafficSceneBuilder
    {
        public static void Build(RouteWorldConfig cfg, RoadNet net, RoadSurface.Result surf, AssetCatalog catalog, CarPaintSet carPaint, Transform rider)
        {
            string path = cfg.BakedTrafficPath;
            var ts = TrafficWorld.Build(cfg, net, surf, catalog, carPaint, rider, out var graph);
            if (graph == null) { if (File.Exists(path)) AssetDatabase.DeleteAsset(path); return; }     // keine veraltete Datei zur Laufzeit
            string text = graph.ToText();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"Verkehr: Spurgraph -> {path} ({text.Length / 1024} KB).");
            if (ts != null) ts.trafficRelPath = path.Replace("\\", "/").Replace("Assets/StreamingAssets/", "");
        }
    }
}
