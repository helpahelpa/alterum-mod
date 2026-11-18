using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// User bindings for the mod shortcuts, stored by stable action id.
//
//   <install>/BepInEx/config/alterum-bindings.txt
//   # alterum mod bindings v1 - id=<path>, path is a canonical Input System chord ("<Keyboard>/f5",
//   # "shift+<Keyboard>/f5"). Written by this mod only; edited by hand at your own risk.
//   mod.clothes.toggle=<Keyboard>/f5
//
// Deliberately id-keyed and label-free: renaming a label or reordering the table keeps the user's
// binding, and an id that no longer exists is dropped with a count in the log instead of being
// re-applied to whatever action inherited its position. The file is written whole and replaced by
// rename, so a crash mid-save cannot leave a half-written map that drops every override.
internal static class ModBindingsStore
{
    private const string FileName = "alterum-bindings.txt";
    private const string Header = "# alterum mod bindings v1 - id=<path> (canonical Input System chord; <Keyboard>/f5, shift+<Keyboard>/f5)";

    private static string _path;
    private static string _dir;

    internal static string PathFor(IHostContext log)
    {
        if (_path != null) return _path;
        string baseDir = null;
        try { baseDir = log?.ScreensDir; } catch { }
        if (string.IsNullOrEmpty(baseDir)) { try { baseDir = log?.ConfigPath; } catch { } }
        try
        {
            var dir = string.IsNullOrEmpty(baseDir) ? null : new DirectoryInfo(baseDir);
            while (dir != null && !string.Equals(dir.Name, "BepInEx", StringComparison.OrdinalIgnoreCase)) dir = dir.Parent;
            if (dir != null) _dir = Path.Combine(dir.FullName, "config");
            if (_dir == null) _dir = string.IsNullOrEmpty(baseDir) ? Environment.CurrentDirectory : baseDir;
        }
        catch { _dir = string.IsNullOrEmpty(baseDir) ? Environment.CurrentDirectory : baseDir; }
        _path = Path.Combine(_dir, FileName);
        return _path;
    }

    // Missing file is not an error: the shipped defaults are the first-run state.
    internal static int Load(IHostContext log, out Dictionary<string, string> overrides)
    {
        overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = PathFor(log);
        bool exists = false;
        try { exists = File.Exists(path); } catch { }
        if (!exists) return 0;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) { log?.LogWarning($"[bind] store read failed '{path}': {e.Message}"); return 0; }

        int read = 0;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { log?.LogWarning($"[bind] store line without '=' ignored: '{line}'"); continue; }
            string id = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (id.Length == 0 || value.Length == 0) continue;
            overrides[id] = value;
            read++;
        }
        return read;
    }

    // Writes only the actions that differ from their default. No-op when nothing is overridden and
    // the file does not exist yet (a fresh install keeps its config folder clean).
    internal static void Save(IHostContext log, IReadOnlyList<ModAction> actions)
    {
        string path = PathFor(log);
        try
        {
            var body = new StringBuilder();
            body.AppendLine(Header);
            int n = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (!a.Overridden) continue;
                body.Append(a.Id).Append('=').Append(a.Current.Path).AppendLine();
                n++;
            }
            if (n == 0 && !File.Exists(path)) return;

            Directory.CreateDirectory(_dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, body.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            log?.LogInfo($"[bind] store saved '{path}' overrides={n}");
        }
        catch (Exception e) { log?.LogWarning($"[bind] store save failed '{path}': {e.Message}"); }
    }
}
