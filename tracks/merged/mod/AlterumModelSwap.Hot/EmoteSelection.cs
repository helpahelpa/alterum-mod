using System;
using System.IO;
using AlterumModelSwap.Contracts;
using BepInEx.Configuration;

namespace AlterumModelSwap.Hot;

// The selected emote, persisted BY ID.
//
// The catalogue is a closed allowlist whose index space changed when it was closed, so an index is
// never written down: the id is resolved through EmoteCatalog on the way back in, and a miss (a
// missing file, a hand-edited value, an id this build no longer offers) resets the selection to the
// first catalogue entry - Twerk - without starting anything. A selection is not a start.
//
// Storage is the mod's own per-instance client config, exactly like EnhancementSettings and
// LobbyStartSettings: `com.user0.alterum.emotes[.<instance>].cfg`, one BepInEx ConfigFile under
// [Emotes] SelectedId. Deleting the file restores the default; two instances never share one.
internal static class EmoteSelection
{
    internal const string FileName = "com.user0.alterum.emotes.cfg";
    private const string Section = "Emotes";
    private const string Key = "SelectedId";

    private static ConfigFile _file;
    private static ConfigEntry<string> _entry;
    private static string _id = "";
    private static string _path = "(not loaded)";
    private static int _saves;

    internal static string ConfigPathUsed => _path;
    internal static int Saves => _saves;

    private static void EnsureLoaded(IHostContext log)
    {
        if (_file != null) return;
        try
        {
            string name = string.IsNullOrEmpty(log?.InstanceName)
                ? FileName
                : FileName.Replace(".cfg", "." + log.InstanceName + ".cfg");
            _path = Path.Combine(ConfigDir(log), name);
            _file = new ConfigFile(_path, true);
            _entry = _file.Bind(Section, Key, "",
                "The emote selected in the picker, by id. Restored through the build's allowlist; an id this " +
                "build does not offer resets the selection to the first entry and starts nothing.");
            _id = _entry.Value ?? "";
            log?.LogInfo($"[emote:cfg] selection '{(string.IsNullOrEmpty(_id) ? "<none>" : _id)}' from '{_path}'" +
                         (File.Exists(_path) ? "" : " (created with the default: none)"));
        }
        catch (Exception e)
        {
            log?.LogWarning($"[emote:cfg] selection config unavailable ({e.Message}); the selection is kept in memory only");
            _file = null;
            _entry = null;
        }
    }

    private static string ConfigDir(IHostContext log)
    {
        try
        {
            var p = log?.ConfigPath;
            if (!string.IsNullOrEmpty(p))
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), "alterum");
    }

    // The id to select: the persisted one when the allowlist still offers it, else the first
    // catalogue entry. `raw` is what was stored and `reset` says the fallback was used, so the caller
    // can log exactly what happened. Nothing is started here.
    internal static string Restore(IHostContext log, out string raw, out bool reset)
    {
        EnsureLoaded(log);
        raw = _id ?? "";
        string canonical = EmoteCatalog.Canonical(raw);
        reset = canonical == null;
        return canonical ?? EmoteCatalog.DefaultSelectionId;
    }

    // Persist a selection the catalogue accepts. Called on every selection change (the F9 cycle, a
    // picker pick, a request-file clip), so the chosen id survives a hot reload or a restart.
    internal static void Save(IHostContext log, string id)
    {
        string canonical = EmoteCatalog.Canonical(id);
        if (canonical == null) return;
        EnsureLoaded(log);
        bool changed = !string.Equals(_id, canonical, StringComparison.Ordinal);
        _id = canonical;
        if (!changed) return;
        try
        {
            if (_entry != null)
            {
                if (!string.Equals(_entry.Value, canonical, StringComparison.Ordinal))
                {
                    _entry.Value = canonical;
                    _file.Save();
                    _saves++;
                }
            }
            log?.LogInfo($"[emote:cfg] selection saved id='{canonical}' path='{_path}' saves={_saves}");
        }
        catch (Exception e) { log?.LogWarning($"[emote:cfg] saving selection '{canonical}' failed: {e.Message}"); }
    }
}
