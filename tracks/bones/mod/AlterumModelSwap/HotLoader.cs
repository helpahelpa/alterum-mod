using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using AlterumModelSwap.Contracts;
using BepInEx;
using UnityEngine;

namespace AlterumModelSwap;

// Loads /home/.../BepInEx/hot/AlterumModelSwap.Hot.dll into a collectible context from a
// shadow copy (so the file stays writable while the game runs) and hot-swaps it when the
// file changes.
internal static class HotLoader
{
    private const string HotFile = "AlterumModelSwap.Hot.dll";
    private static string HotDir => string.IsNullOrEmpty(HostPlugin.InstanceName)
        ? Path.Combine(Paths.GameRootPath, "BepInEx", "hot")
        : Path.Combine(Paths.GameRootPath, "BepInEx", "hot", HostPlugin.InstanceName);
    private static readonly string CacheDir = Path.Combine(Paths.GameRootPath, "BepInEx", "cache", "hot");

    private static string _source;
    private static DateTime _sourceStamp = DateTime.MinValue;
    private static AssemblyLoadContext _alc;
    private static IHotModule _module;
    private static int _generation;
    private static int _faults;
    private static bool _firstActivation = true;
    private static DateTime _nextRetryUtc = DateTime.MinValue;
    private static int _candidate;

    internal static void Init(PumpBehaviour pump)
    {
        Directory.CreateDirectory(CacheDir);
        // Shadow copies from previous runs are unlocked now; clear them.
        try { foreach (var d in Directory.GetDirectories(CacheDir)) Directory.Delete(d, true); } catch { }
        _source = Path.Combine(HotDir, HotFile);
        HostPlugin.L.LogInfo($"[hot] watching {_source}");
        if (!File.Exists(_source))
        {
            HostPlugin.L.LogWarning("[hot] hot assembly not found - mod inactive");
            return;
        }
        if (TryLoadAndSwap())
        {
            // seed the stamp so the first poll does not re-load the same file
            try { _sourceStamp = File.GetLastWriteTimeUtc(_source); } catch { }
        }
    }

    internal static void Tick(float dt)
    {
        HostPlugin.Instance?.WatchConfig();
        Poll();

        var m = _module;
        if (m == null) return;
        try { m.Update(dt); }
        catch (Exception e) { Fault("Update", e); }
    }

    internal static void Late()
    {
        var m = _module;
        if (m == null) return;
        try { m.LateUpdate(); }
        catch (Exception e) { Fault("LateUpdate", e); }
    }

    internal static void Gui()
    {
        var m = _module;
        if (m == null) return;
        try { m.OnGUI(); }
        catch (Exception e) { Fault("OnGUI", e); }
    }

    private static void Poll()
    {
        try
        {
            if (_source == null) return;
            if (!File.Exists(_source)) return;

            var mt = File.GetLastWriteTimeUtc(_source);
            // No module yet (started without the dll, or the last load failed) -> keep trying,
            // even when the timestamp has not changed.
            if (_module != null && mt == _sourceStamp) return;
            if (DateTime.UtcNow < _nextRetryUtc) return;

            if (_module != null) HostPlugin.L.LogInfo("[hot] change detected, reloading");

            // Only commit the timestamp once the new generation is actually running, otherwise a
            // transient sharing violation would consume the reload for good.
            if (TryLoadAndSwap())
            {
                _sourceStamp = mt;
                _nextRetryUtc = DateTime.MinValue;
                _faults = 0;
            }
            else
            {
                _nextRetryUtc = DateTime.UtcNow.AddSeconds(2);
            }
        }
        catch (Exception e) { HostPlugin.L.LogWarning($"[hot:copy-retry] {e.Message}"); }
    }

    private static bool TryLoadAndSwap()
    {
        IHotModule candidate = null;
        AssemblyLoadContext alc = null;
        try
        {
            var gen = ++_candidate;
            var shadowDir = Path.Combine(CacheDir, $"{Environment.ProcessId}-{gen}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(shadowDir);
            var shadow = Path.Combine(shadowDir, HotFile);
            File.Copy(_source, shadow, true);

            alc = new HotAlc($"AlterumHot#{gen}");
            var asm = alc.LoadFromAssemblyPath(shadow);
            var type = asm.GetType("AlterumModelSwap.Hot.HotModule", throwOnError: false);
            if (type == null) throw new Exception("AlterumModelSwap.Hot.HotModule not found");
            var obj = Activator.CreateInstance(type);
            candidate = obj as IHotModule;
            if (candidate == null) throw new Exception("HotModule does not implement IHotModule");
            candidate.Initialize(new HostContext(), _firstActivation);
        }
        catch (Exception e)
        {
            HostPlugin.L.LogError($"[hot:init-failed] {e.GetType().Name}: {e.Message}");
            try { candidate?.Dispose(); } catch { }
            try { alc?.Unload(); } catch { }
            return false;
        }

        // Candidate is live; retire the old generation.
        var old = _module;
        var oldAlc = _alc;
        _module = candidate;
        _alc = alc;
        _firstActivation = false;

        if (old != null)
        {
            try { old.Dispose(); }
            catch (Exception e) { HostPlugin.L.LogWarning($"[hot:dispose-failed] {e.Message}"); }
        }
        if (oldAlc != null)
        {
            try { oldAlc.Unload(); }
            catch (Exception e) { HostPlugin.L.LogWarning($"[hot:unload-pending] {e.Message}"); }
        }

        _generation = _candidate;
        HostPlugin.L.LogInfo($"[hot] generation {_generation} active");
        return true;
    }

    private static void Fault(string phase, Exception e)
    {
        if (_faults++ >= 5) return;
        HostPlugin.L.LogError($"[hot:dispatch-fault] phase={phase} gen={_generation}: {e}");
    }

    private sealed class HotAlc : AssemblyLoadContext
    {
        public HotAlc(string name) : base(name, isCollectible: true) { }

        // Resolve everything through the default context so il2cpp type identity is shared
        // with the host (BepInEx, Il2CppInterop, interop assemblies, Contracts).
        protected override Assembly Load(AssemblyName name) => null;
    }

    private sealed class HostContext : IHostContext
    {
        public ConfigSnapshot Config => HostPlugin.Snapshot();
        public string ConfigPath => HostPlugin.Instance?.Config?.ConfigFilePath;
        public string ScreensDir => HostPlugin.ScreensDir;
        public string InstanceName => HostPlugin.InstanceName;
        public bool ReloadConfig() { try { HostPlugin.Instance.Config.Reload(); return true; } catch { return false; } }
        public void LogInfo(string m) => HostPlugin.L.LogInfo(m);
        public void LogWarning(string m) => HostPlugin.L.LogWarning(m);
        public void LogError(string m) => HostPlugin.L.LogError(m);
    }
}
