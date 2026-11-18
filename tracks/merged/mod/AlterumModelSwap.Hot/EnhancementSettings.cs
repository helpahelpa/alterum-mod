using System;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// Chest / hip enhancement state: the single source the ESC menu (stream C) calls into.
//
// Defaults are the shipped state, not a guess: chest enhancement ON (the enlarged-bust rig is what
// the whole proportion pass was built for) and hip enhancement OFF (the widened pelvis is the
// bigger, more situational change, so a fresh config does not silently reshape every body).
//
// Persistence is a dedicated client config file next to the host's own, named per instance exactly
// like the host's (`com.user0.alterum.enhancements[.<instance>].cfg`), so two instances never share
// a toggle and deleting one file restores the defaults.
//
// SetChest/SetHips are the interface: write the entry, Save(), notify (log), apply live. Applying
// never restarts anything - it re-derives every write from the recorded originals through
// Proportions (native) and CustomEnhancements (imported bodies).
internal static class EnhancementSettings
{
    internal const string FileName = "com.user0.alterum.enhancements.cfg";

    // Defaults. Chest on, hips off - the shipped state (see the header).
    internal const bool ChestDefault = true;
    internal const bool HipsDefault = false;

    private static bool _chest = ChestDefault;
    private static bool _hips = HipsDefault;
    private static bool _loaded;
    private static ConfigFile _file;
    private static ConfigEntry<bool> _chestEntry, _hipsEntry;
    private static IHostContext _host;
    private static int _saves, _applies;

    internal static bool ChestEnabled => _chest;
    internal static bool HipsEnabled => _hips;
    internal static string ConfigPathUsed { get; private set; } = "(not loaded)";
    internal static int Saves => _saves;
    internal static int Applies => _applies;

    // Loaded once per hot generation from the host's config directory. Called from the first
    // DefaultHands/CustomEnhancements tick; safe to call again (no-op).
    internal static void EnsureLoaded(IHostContext log)
    {
        if (_loaded) return;
        _loaded = true;
        _host = log;
        try
        {
            string dir = ConfigDir(log);
            string name = string.IsNullOrEmpty(log.InstanceName) ? FileName : FileName.Replace(".cfg", "." + log.InstanceName + ".cfg");
            ConfigPathUsed = Path.Combine(dir, name);
            _file = new ConfigFile(ConfigPathUsed, true);
            _chestEntry = _file.Bind("Enhancements", "ChestEnabled", ChestDefault,
                "Enlarge the chest. Off restores the authored (shipped) proportions, garment weights included.");
            _hipsEntry = _file.Bind("Enhancements", "HipsEnabled", HipsDefault,
                "Widen the hips. Off restores the authored (shipped) pelvis width.");
            _chest = _chestEntry.Value;
            _hips = _hipsEntry.Value;
            log.LogInfo($"[enh] loaded chest={OnOff(_chest)} hips={OnOff(_hips)} from '{ConfigPathUsed}'" +
                        (File.Exists(ConfigPathUsed) ? "" : " (created with the defaults: chest on, hips off)"));
        }
        catch (Exception e)
        {
            log.LogWarning($"[enh] config load failed ({e.Message}); using the defaults in memory only");
            _chest = ChestDefault;
            _hips = HipsDefault;
        }
        Apply(log, "load");
    }

    private static string ConfigDir(IHostContext log)
    {
        try
        {
            var p = log.ConfigPath;
            if (!string.IsNullOrEmpty(p))
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), "alterum");
    }

    // ------------------------------------------------------------------ the stream-C interface

    internal static void SetChest(bool on) => SetChest(on, "menu");

    internal static void SetChest(bool on, string why)
    {
        bool changed = on != _chest;
        _chest = on;
        if (_chestEntry != null && _chestEntry.Value != on) { _chestEntry.Value = on; Save("chest"); }
        else if (_chestEntry == null) Save("chest");
        _host?.LogInfo($"[enh] chest={(on ? "on" : "off")} (why={why}{(changed ? "" : ", unchanged")}) saved={_saves}");
        if (changed) Apply(_host, "chest");
    }

    internal static void SetHips(bool on) => SetHips(on, "menu");

    internal static void SetHips(bool on, string why)
    {
        bool changed = on != _hips;
        _hips = on;
        if (_hipsEntry != null && _hipsEntry.Value != on) { _hipsEntry.Value = on; Save("hips"); }
        else if (_hipsEntry == null) Save("hips");
        _host?.LogInfo($"[enh] hips={(on ? "on" : "off")} (why={why}{(changed ? "" : ", unchanged")}) saved={_saves}");
        if (changed) Apply(_host, "hips");
    }

    internal static void ToggleChest(string why) => SetChest(!_chest, why);
    internal static void ToggleHips(string why) => SetHips(!_hips, why);

    private static void Save(string why)
    {
        try
        {
            if (_file == null) return;
            _file.Save();
            _saves++;
        }
        catch (Exception e) { _host?.LogWarning($"[enh] save failed ({why}): {e.Message}"); }
    }

    // Applies the current state everywhere. Never "undo by scaling back": both writers re-derive
    // from the immutable originals they recorded, so an apply/release cycle cannot accumulate.
    internal static void Apply(IHostContext log, string why)
    {
        _applies++;
        bool chest = _chest, hips = _hips;
        log?.LogInfo($"[enh:apply] chest={OnOff(chest)} hips={OnOff(hips)} why={why}");

        // Native bodies: map "off" to the authored values. Proportions.SetScales RESTORES first and
        // re-applies from its recorded originals, so 1 is exactly the authored rig.
        try
        {
            var props = Proportions.Current;
            if (props != null)
            {
                float breast = chest ? Proportions.BreastLeafScaleDefault : 1f;
                float cage = chest ? Proportions.ChestCageScaleDefault : 1f;
                float hip = hips ? Proportions.HipScaleDefault : 1f;
                props.SetScales(breast, hip, cage, "enhancement:" + why);
                // The garment re-weight is part of "chest on": with the chest off it would deform the
                // authored bust. Applying it back re-derives from the authored sharedMesh.
                props.SetChestFix(chest, "enhancement:" + why);
            }
            else
            {
                // Before the first rig exists the constants are what the next sweep applies.
                Proportions.SetEnhancementDefaults(chest, hips);
            }
        }
        catch (Exception e) { log?.LogWarning($"[enh:apply] native failed: {e.Message}"); }

        // Jiggle writes on top of the chest leaves: with the chest off it must not add motion to the
        // authored bust, and it must forget the offsets it already wrote (never subtract a stale one).
        try { JiggleSolver.SetChestEnabled(log, chest, "enhancement:" + why); }
        catch (Exception e) { log?.LogWarning($"[enh:apply] jiggle gate failed: {e.Message}"); }

        // Imported bodies: their own mapped profile / bounded rest-space deformation.
        try { CustomEnhancements.ApplyAll(log, chest, hips, "enhancement:" + why); }
        catch (Exception e) { log?.LogWarning($"[enh:apply] custom failed: {e.Message}"); }
    }

    internal static string OnOff(bool v) => v ? "on" : "off";

    internal static string Dump()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "chest={0} hips={1} path='{2}' saves={3} applies={4}",
            OnOff(_chest), OnOff(_hips), ConfigPathUsed, _saves, _applies);
    }

    // Hot unload: the config object is recreated next generation; nothing is written here.
    internal static void Dispose()
    {
        _loaded = false;
        _file = null;
        _chestEntry = null;
        _hipsEntry = null;
    }
}
