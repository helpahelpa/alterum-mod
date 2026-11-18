using System;

namespace AlterumModelSwap.Contracts;

/// <summary>
/// Snapshot of the plugin configuration. Plain primitives only, so it can cross the
/// AssemblyLoadContext boundary without dragging BepInEx types into the hot assembly.
/// </summary>
public struct ConfigSnapshot
{
    // Mirror
    public float MirrorWidth;
    public float MirrorHeight;
    public float MirrorOffset;
    public float MirrorCenterHeight;
    public float MirrorTilt;
    public bool MirrorFrame;
    public bool MirrorBottomOnFloor;
    public bool MirrorSnapToWall;
    public float MirrorEyeHeight;
    public bool MirrorLobbyOnly;
    public bool MirrorShowPlayer;
    public bool MirrorOverlay;
    public bool AutoMirror;
    public float AutoMirrorDelay;

    // Render / debug
    public string RenderSlots;
    public float RenderDelay;
    public bool TakeScreenshots;
    public float ScreenshotInterval;

    // Window
    public bool ForceWindowed;
    public int WindowX;
    public int WindowY;
    public bool Quiet;
    public int QuietWidth;
    public int QuietHeight;

    // Automation
    public string ClickSequence;
    public float ClickFirstDelay;
    public float ClickStepDelay;
}

public interface IHostContext
{
    ConfigSnapshot Config { get; }
    string ConfigPath { get; }
    string ScreensDir { get; }
    string InstanceName { get; }
    bool ReloadConfig();

    // Permanent named-message bridge (see Net.cs). Owned by the host assembly, so the
    // registration never depends on a hot-module generation.
    INetBridge Net { get; }

    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message);
}

/// <summary>
/// Implemented by the reloadable assembly. Everything is called on Unity's main thread.
/// The implementation must expose a parameterless public constructor and do nothing
/// Unity-related in it.
/// </summary>
public interface IHotModule : IDisposable
{
    void Initialize(IHostContext host, bool firstActivation);
    void Update(float unscaledDeltaTime);
    void LateUpdate();
    void OnGUI();
}
