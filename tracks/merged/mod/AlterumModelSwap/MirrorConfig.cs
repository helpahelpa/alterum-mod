using System;
using System.IO;
using BepInEx.Configuration;

namespace AlterumModelSwap;

// The mirror's self-reflection setting. The pre-fix [Mirror] ShowLocalPlayer defaulted to false
// and carried a "Does not work" description; both were wrong once the mirror could bind the
// native body. A pre-fix cfg cannot tell its generated false from a deliberate opt-out, so the
// old value is not migrated: the new key starts at true on fresh and upgraded configs alike.
// The old key is removed so it cannot look like a live setting. Setting
// [Mirror] ReflectLocalPlayer=false is the opt-out.
internal static class MirrorConfig
{
    internal const string Section = "Mirror";
    internal const string ObsoleteKey = "ShowLocalPlayer";
    internal const string ReflectKey = "ReflectLocalPlayer";
    private const string ReflectDescription =
        "Show your own body in the mirror. Set false to opt out of self-reflection.";

    internal static ConfigEntry<bool> BindReflectLocalPlayer(ConfigFile file, Action<string> log)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        var obsolete = new ConfigDefinition(Section, ObsoleteKey);
        bool retired = false;
        if (HasSetting(file.ConfigFilePath, Section, ObsoleteKey))
        {
            try
            {
                // ConfigFile only knows keys it has bound; an unbound ShowLocalPlayer would be
                // kept as an orphan and could never be removed. Bind it, remove it, then save so
                // the obsolete entry is gone from disk as well as from memory.
                file.Bind(Section, ObsoleteKey, false, "Obsolete: superseded by ReflectLocalPlayer.");
                if (file.Remove(obsolete))
                {
                    file.Save();
                    retired = true;
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"[config] could not retire obsolete [Mirror] {ObsoleteKey}: {e.Message}");
            }
        }

        var entry = file.Bind(Section, ReflectKey, true, ReflectDescription);
        if (retired)
            log?.Invoke($"[config] retired obsolete [Mirror] {ObsoleteKey}; " +
                        $"[Mirror] {ReflectKey} now defaults to true (set it to false to opt out)");
        return entry;
    }

    // ConfigFile keeps unknown keys as orphans and does not expose them, so check the raw file
    // for the obsolete [Mirror] entry before binding it. This keeps fresh configs free of a
    // transient ShowLocalPlayer write and avoids a false "retired" log line.
    private static bool HasSetting(string path, string section, string key)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string current = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }
}
