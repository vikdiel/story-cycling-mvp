using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace StoryCycling.WorldGen.Editor
{
    // Bindeglied zu Tools/osm2streets/convert.mjs (siehe README dort). Dieser Schritt ist ein optionales
    // Zusatzwerkzeug: fehlt "node" oder wurde "npm install" nicht ausgeführt, baut die Pipeline unverändert
    // mit unserer eigenen Flächenvereinigung (RoadSurface) weiter — nur mit den robusteren Kreuzungen, wenn
    // die Datei verfügbar ist.
    public static class Osm2StreetsGeometry
    {
        private const string ToolRelPath = "Assets/StoryCycling/Tools/osm2streets";

        public static string OutputPathFor(string osmPath) =>
            osmPath.EndsWith(".osm.xml") ? osmPath.Substring(0, osmPath.Length - ".osm.xml".Length) + ".o2s.json" : osmPath + ".o2s.json";

        public static bool NeedsRefresh(string gpxPath, string osmPath, string outPath)
        {
            if (!File.Exists(outPath)) return true;
            var outTime = File.GetLastWriteTimeUtc(outPath);
            return (File.Exists(osmPath) && File.GetLastWriteTimeUtc(osmPath) > outTime) ||
                   (File.Exists(gpxPath) && File.GetLastWriteTimeUtc(gpxPath) > outTime);
        }

        // Versucht, das Node-Skript auszuführen; liefert false (mit Begründung in message), wenn node fehlt,
        // node_modules nicht installiert ist, oder die Konvertierung fehlschlägt. Wirft nie.
        public static bool TryRun(string gpxPath, string osmPath, string outPath, out string message)
        {
            string toolDir = Path.GetFullPath(ToolRelPath);
            string script = Path.Combine(toolDir, "convert.mjs");
            if (!File.Exists(script)) { message = $"osm2streets: Werkzeug fehlt unter {ToolRelPath}."; return false; }
            if (!Directory.Exists(Path.Combine(toolDir, "node_modules")))
            {
                message = $"osm2streets: nicht eingerichtet — einmalig \"npm install\" in {ToolRelPath} ausführen (siehe README dort). " +
                           "Baue ohne osm2streets-Geometrie weiter.";
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{script}\" \"{Path.GetFullPath(gpxPath)}\" \"{Path.GetFullPath(osmPath)}\" \"{Path.GetFullPath(outPath)}\"",
                    WorkingDirectory = toolDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = new Process { StartInfo = psi })
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    p.OutputDataReceived += (sender, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    p.ErrorDataReceived += (sender, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); p.WaitForExit(); } catch { }
                        message = "osm2streets: Zeitüberschreitung (>120 s) — baue ohne osm2streets-Geometrie weiter.";
                        return false;
                    }
                    p.WaitForExit(); // die asynchronen Ausgabe-Handler vollständig leeren
                    if (p.ExitCode != 0 || !File.Exists(outPath))
                    { message = $"osm2streets fehlgeschlagen (Code {p.ExitCode}): {LastLine(stderr.ToString(), stdout.ToString())} — baue ohne osm2streets-Geometrie weiter."; return false; }
                    message = $"osm2streets: {LastLine(stdout.ToString(), stderr.ToString())}";
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                message = "osm2streets: \"node\" wurde nicht gefunden (Node.js installiert?). Baue ohne osm2streets-Geometrie weiter.";
                return false;
            }
            catch (System.Exception ex)
            {
                message = $"osm2streets: unerwarteter Fehler ({ex.GetType().Name}: {ex.Message}). Baue ohne osm2streets-Geometrie weiter.";
                return false;
            }
        }

        private static string LastLine(string primary, string fallback)
        {
            string s = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
            var lines = s.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--) if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i].Trim();
            return "(keine Ausgabe)";
        }

        // Lädt die geschriebene JSON-Datei (Schema: {"polygons":[[[x,z],[x,z],...],...]}), bereits in denselben
        // lokalen Metern wie der Rest der Pipeline. Gibt null zurück (statt zu werfen), wenn Datei fehlt/kaputt ist.
        public static List<Vector2[]> TryLoad(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var json = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                var polysNode = json["polygons"];
                if (polysNode == null) return null;
                var res = new List<Vector2[]>(polysNode.Count);
                foreach (var ring in polysNode.Items)
                {
                    var pts = new Vector2[ring.Count];
                    for (int i = 0; i < ring.Count; i++) pts[i] = new Vector2((float)ring[i][0].Number, (float)ring[i][1].Number);
                    if (pts.Length >= 3) res.Add(pts);
                }
                return res;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"osm2streets: {path} konnte nicht gelesen werden ({ex.GetType().Name}: {ex.Message}) — baue ohne osm2streets-Geometrie weiter.");
                return null;
            }
        }
    }

    // Minimaler JSON-Reader (nur was hier gebraucht wird: Objekte, Arrays, Zahlen) — kein Paket-Abhängigkeit nötig.
    internal sealed class Json
    {
        public double Number; public List<Json> Items; private Dictionary<string, Json> obj;
        public int Count => Items?.Count ?? 0;
        public Json this[int i] => Items[i];
        public Json this[string key] => obj != null && obj.TryGetValue(key, out var v) ? v : null;

        public static Json Parse(string s) { int i = 0; return ParseValue(s, ref i); }

        private static Json ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') { SkipString(s, ref i); return new Json(); }
            if (c == 't' || c == 'f') { i += c == 't' ? 4 : 5; return new Json(); }
            if (c == 'n') { i += 4; return new Json(); }
            return ParseNumber(s, ref i);
        }

        private static Json ParseObject(string s, ref int i)
        {
            var j = new Json { obj = new Dictionary<string, Json>() };
            i++; SkipWs(s, ref i);
            if (s[i] == '}') { i++; return j; }
            while (true)
            {
                SkipWs(s, ref i);
                int start = ++i; while (s[i] != '"') { if (s[i] == '\\') i++; i++; }
                string key = s.Substring(start, i - start); i++;
                SkipWs(s, ref i); i++; // ':'
                j.obj[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++; break; // '}'
            }
            return j;
        }

        private static Json ParseArray(string s, ref int i)
        {
            var j = new Json { Items = new List<Json>() };
            i++; SkipWs(s, ref i);
            if (s[i] == ']') { i++; return j; }
            while (true)
            {
                j.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++; break; // ']'
            }
            return j;
        }

        private static void SkipString(string s, ref int i) { i++; while (s[i] != '"') { if (s[i] == '\\') i++; i++; } i++; }

        private static Json ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            return new Json { Number = double.Parse(s.Substring(start, i - start), System.Globalization.CultureInfo.InvariantCulture) };
        }

        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    }
}
