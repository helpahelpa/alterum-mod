using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace AlterumModelSwap;

// Stable half of the mod: owns every il2cpp-injected type and the reloadable assembly,
// so the game never has to restart to pick up a code change.
[BepInPlugin(Guid, "Alterum Model Swap (host)", "2.0.0")]
public class HostPlugin : BasePlugin
{
    public const string Guid = "com.user0.alterum.modelswap";
    public static HostPlugin Instance;
    internal static ManualLogSource L;

    public static ConfigEntry<float> MirrorWidth;
    public static ConfigEntry<float> MirrorHeight;
    public static ConfigEntry<float> MirrorOffset;
    public static ConfigEntry<float> MirrorCenterHeight;
    public static ConfigEntry<float> MirrorTilt;
    public static ConfigEntry<bool> MirrorFrame;
    public static ConfigEntry<bool> MirrorBottomOnFloor;
    public static ConfigEntry<bool> MirrorSnapToWall;
    public static ConfigEntry<float> MirrorEyeHeight;
    public static ConfigEntry<bool> MirrorLobbyOnly;
    // [Mirror] ReflectLocalPlayer. The ConfigSnapshot field keeps the old MirrorShowPlayer name
    // so snapshot/1 diagnostics and the hot module stay unchanged.
    public static ConfigEntry<bool> MirrorReflectLocalPlayer;
    public static ConfigEntry<bool> MirrorOverlay;
    public static ConfigEntry<bool> AutoMirror;
    public static ConfigEntry<float> AutoMirrorDelay;

    public static ConfigEntry<string> RenderSlots;
    public static ConfigEntry<float> RenderDelay;
    public static ConfigEntry<bool> TakeScreenshots;
    public static ConfigEntry<float> ScreenshotInterval;

    public static ConfigEntry<bool> ForceWindowed;
    public static ConfigEntry<int> WindowX;
    public static ConfigEntry<int> WindowY;
    public static ConfigEntry<bool> Quiet;
    public static ConfigEntry<int> QuietWidth;
    public static ConfigEntry<int> QuietHeight;

    public static ConfigEntry<string> ClickSequence;
    public static ConfigEntry<float> ClickFirstDelay;
    public static ConfigEntry<float> ClickStepDelay;

    internal static string InstanceName = "";
    // The config actually bound into Snapshot(), not BasePlugin.Config on a named instance.
    internal static string ActiveConfigPath => Instance?._cfgFile?.ConfigFilePath;
    private ConfigFile _cfgFile;
    internal static string ScreensDir;
    private DateTime _cfgMtime = DateTime.MinValue;

    // Lets several copies of the game run side by side, each with its own hot dll,
    // screenshots, config and log:  Alterum.exe -alterumInstance=featureB
    internal static string ParseInstance()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i] ?? "";
            const string flag = "-alteruminstance=";
            if (a.ToLowerInvariant().StartsWith(flag)) return a.Substring(flag.Length).Trim();
            if (a.Equals("-alterumInstance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return (args[i + 1] ?? "").Trim();
        }
        return "";
    }

    public override void Load()
    {
        Instance = this;
        L = Log;

        // Parsed once (DiagnosticsMode) and gates the whole mod, host and hot alike. Logged before
        // anything else so a capture explains absent `[net]`/`[hot]` lines by itself.
        Log.LogInfo($"[host] mode={(DiagnosticsMode.Enabled ? "diagnostics-only" : "normal")} ({DiagnosticsMode.Flag}={DiagnosticsMode.Enabled})");

        // Permanent startup gate: it runs before any per-instance config/log/hot work and before
        // the pump exists, so a missing or failing hot module can never skip it. Any failure is
        // fatal inside the guard (exit code 78); it deliberately does not go through the catch
        // block around the pump below, which is allowed to log-and-continue.
        InstanceLabelGuard.Info = m => Log.LogInfo(m);
        InstanceLabelGuard.Warn = m => Log.LogWarning(m);
        InstanceLabelGuard.Error = m => Log.LogError(m);
        InstanceLabelGuard.HotDllPathProvider = () => HotLoader.ActiveAssemblyPath;
        InstanceLabelGuard.Initialize(Paths.GameRootPath);
        HookInstanceLabelEvents();

        InstanceName = ParseInstance();
        ScreensDir = string.IsNullOrEmpty(InstanceName)
            ? Path.Combine(Paths.GameRootPath, "BepInEx", "screens")
            : Path.Combine(Paths.GameRootPath, "BepInEx", "screens", InstanceName);
        _cfgFile = string.IsNullOrEmpty(InstanceName) ? Config : BuildPerInstanceConfig();
        if (!string.IsNullOrEmpty(InstanceName))
        {
            StartInstanceLog();
            Log.LogInfo($"[host] instance '{InstanceName}' - isolated config/hot/screens/log");
        }

        MirrorWidth = _cfgFile.Bind("Mirror", "Width", 0.9f, "Mirror width in metres.");
        MirrorHeight = _cfgFile.Bind("Mirror", "Height", 1.95f, "Mirror height in metres.");
        MirrorOffset = _cfgFile.Bind("Mirror", "OffsetFromPlayer", 2.5f, "Distance in front of the player.");
        MirrorCenterHeight = _cfgFile.Bind("Mirror", "HeightOffsetFromEye", -0.10f, "Vertical offset from eye height.");
        MirrorTilt = _cfgFile.Bind("Mirror", "TiltDegrees", 0f, "Lean the top of the glass away from you.");
        MirrorFrame = _cfgFile.Bind("Mirror", "Frame", true, "Draw a dark border around the glass.");
        MirrorBottomOnFloor = _cfgFile.Bind("Mirror", "StandOnFloor", true, "Stand the mirror on the floor.");
        MirrorSnapToWall = _cfgFile.Bind("Mirror", "SnapToWall", true, "Slide it back to the wall in front of you.");
        MirrorEyeHeight = _cfgFile.Bind("Mirror", "EyeHeight", 1.75f, "Fallback floor offset.");
        MirrorLobbyOnly = _cfgFile.Bind("Mirror", "LobbyOnly", true, "Only in the Lobby scene.");
        MirrorReflectLocalPlayer = MirrorConfig.BindReflectLocalPlayer(_cfgFile, m => Log.LogInfo(m));
        MirrorOverlay = _cfgFile.Bind("Mirror", "ShowOverlayButtons", false, "On-screen placement buttons.");
        AutoMirror = _cfgFile.Bind("Mirror", "AutoPlace", true, "Place it automatically in the lobby.");
        AutoMirrorDelay = _cfgFile.Bind("Mirror", "AutoPlaceDelaySeconds", 1.5f, "Delay before AutoPlace first tries.");

        RenderSlots = _cfgFile.Bind("Render", "RenderWardrobeSlots", "", "Wardrobe slot indexes to render to PNG.");
        RenderDelay = _cfgFile.Bind("Render", "RenderDelaySeconds", 40f, "Delay before rendering slots.");
        TakeScreenshots = _cfgFile.Bind("Debug", "Screenshots", false, "Write screenshots into BepInEx/screens.");
        ScreenshotInterval = _cfgFile.Bind("Debug", "ScreenshotIntervalSeconds", 6f, "Seconds between screenshots.");

        ForceWindowed = _cfgFile.Bind("Window", "ForceWindowed", true,
            "Keep the game windowed when launched with -alterumDevMode.");
        WindowX = _cfgFile.Bind("Window", "WindowX", 0, "Screen X position for the game window.");
        WindowY = _cfgFile.Bind("Window", "WindowY", 0, "Screen Y position for the game window.");
        Quiet = _cfgFile.Bind("Window", "Quiet", false, "Also park the window off-screen and give focus back.");
        QuietWidth = _cfgFile.Bind("Window", "QuietWidth", 1280, "Windowed width used when Quiet is on.");
        QuietHeight = _cfgFile.Bind("Window", "QuietHeight", 720, "Windowed height used when Quiet is on.");

        ClickSequence = _cfgFile.Bind("Automation", "ClickSequence", "", "Pipe-separated UI button name substrings to click.");
        ClickFirstDelay = _cfgFile.Bind("Automation", "ClickFirstDelaySeconds", 7f, "Delay before the first click.");
        ClickStepDelay = _cfgFile.Bind("Automation", "ClickStepDelaySeconds", 3f, "Delay between clicks.");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<PumpBehaviour>();
            var go = new GameObject("AlterumHotPump");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var pump = go.AddComponent(Il2CppType.Of<PumpBehaviour>()).TryCast<PumpBehaviour>();
            if (pump == null) { Log.LogError("[host] could not create pump"); return; }
            HotLoader.Init(pump);
            Log.LogInfo("[host] ready - hot assembly is reloadable, no restart needed");
        }
        catch (Exception e) { Log.LogError($"[host] startup failed: {e}"); }
    }

    private static UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode> _sceneLoadedHook;
    private static UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene> _activeSceneChangedHook;

    // Scene changes are the moments Unity is most likely to rewrite the window title. The frame
    // checks in PumpBehaviour keep working without these hooks; they just shorten the window.
    private void HookInstanceLabelEvents()
    {
        try
        {
            _sceneLoadedHook = DelegateSupport.ConvertDelegate<
                UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>>(
                new Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>(OnSceneLoaded));
            var loaded = Il2CppSystem.Delegate.Combine(
                UnityEngine.SceneManagement.SceneManager.sceneLoaded, _sceneLoadedHook);
            var loadedCast = loaded?.TryCast<
                UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>>();
            if (loadedCast != null) UnityEngine.SceneManagement.SceneManager.sceneLoaded = loadedCast;
            else Log.LogWarning("[host] instance-label sceneLoaded hook not installed");

            _activeSceneChangedHook = DelegateSupport.ConvertDelegate<
                UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>(
                new Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>(OnActiveSceneChanged));
            var changed = Il2CppSystem.Delegate.Combine(
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged, _activeSceneChangedHook);
            var changedCast = changed?.TryCast<
                UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>();
            if (changedCast != null) UnityEngine.SceneManagement.SceneManager.activeSceneChanged = changedCast;
            else Log.LogWarning("[host] instance-label activeSceneChanged hook not installed");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[host] instance-label scene hooks failed: {e.Message}");
        }
    }

    private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        InstanceLabelGuard.Reassert("sceneLoaded:" + scene.name);
    }

    private static void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene from, UnityEngine.SceneManagement.Scene to)
    {
        InstanceLabelGuard.Reassert("activeSceneChanged:" + to.name);
    }

    // BepInEx writes one shared LogOutput.log for the whole install, which the second
    // instance truncates. Give named instances their own log file.
    private void StartInstanceLog()
    {
        try
        {
            var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, InstanceName + ".log");
            var listener = new FileLogListener(path);
            BepInEx.Logging.Logger.Listeners.Add(listener);
        }
        catch (Exception e) { Log.LogWarning($"[host] instance log failed: {e.Message}"); }
    }

    private sealed class FileLogListener : ILogListener
    {
        private readonly System.IO.StreamWriter _w;
        private readonly object _gate = new object();
        public FileLogListener(string path)
        {
            _w = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
        }
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs e)
        {
            lock (_gate)
            {
                try { _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{e.Level,-7}] {e.Data}"); } catch { }
            }
        }
        public void Dispose() { lock (_gate) { try { _w.Dispose(); } catch { } } }
    }

    // BepInEx gives every plugin one config path; with an instance name we keep our own file.
    private ConfigFile BuildPerInstanceConfig()
    {
        try
        {
            var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "config");
            var path = Path.Combine(dir, $"com.user0.alterum.modelswap.{InstanceName}.cfg");
            var basePath = Config.ConfigFilePath;
            if (!File.Exists(path) && File.Exists(basePath)) File.Copy(basePath, path, false);
            return new ConfigFile(path, saveOnInit: true);
        }
        catch (Exception e)
        {
            Log.LogWarning($"[host] per-instance config failed, using shared: {e.Message}");
            return Config;
        }
    }

    internal static ConfigSnapshot Snapshot() => new ConfigSnapshot
    {
        MirrorWidth = MirrorWidth.Value,
        MirrorHeight = MirrorHeight.Value,
        MirrorOffset = MirrorOffset.Value,
        MirrorCenterHeight = MirrorCenterHeight.Value,
        MirrorTilt = MirrorTilt.Value,
        MirrorFrame = MirrorFrame.Value,
        MirrorBottomOnFloor = MirrorBottomOnFloor.Value,
        MirrorSnapToWall = MirrorSnapToWall.Value,
        MirrorEyeHeight = MirrorEyeHeight.Value,
        MirrorLobbyOnly = MirrorLobbyOnly.Value,
        MirrorShowPlayer = MirrorReflectLocalPlayer.Value,
        MirrorOverlay = MirrorOverlay.Value,
        AutoMirror = AutoMirror.Value,
        AutoMirrorDelay = AutoMirrorDelay.Value,
        RenderSlots = RenderSlots.Value,
        RenderDelay = RenderDelay.Value,
        TakeScreenshots = TakeScreenshots.Value,
        ScreenshotInterval = ScreenshotInterval.Value,
        ForceWindowed = ForceWindowed.Value,
        WindowX = WindowX.Value,
        WindowY = WindowY.Value,
        Quiet = Quiet.Value,
        QuietWidth = QuietWidth.Value,
        QuietHeight = QuietHeight.Value,
        ClickSequence = ClickSequence.Value,
        ClickFirstDelay = ClickFirstDelay.Value,
        ClickStepDelay = ClickStepDelay.Value,
    };

    // Editing the cfg on disk is picked up live - no rebuild, no restart.
    internal void WatchConfig()
    {
        try
        {
            var path = _cfgFile.ConfigFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var mt = File.GetLastWriteTimeUtc(path);
            if (_cfgMtime == DateTime.MinValue) { _cfgMtime = mt; return; }
            if (mt == _cfgMtime) return;
            _cfgFile.Reload();          // throws while the file is mid-write -> retried next tick
            _cfgMtime = mt;
            L.LogInfo("[config] reloaded from disk");
        }
        catch (Exception e) { L.LogWarning($"[config] reload deferred: {e.Message}"); }
    }
}
