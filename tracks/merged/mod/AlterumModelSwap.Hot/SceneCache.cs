using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AlterumModelSwap.Hot;

// One snapshot per object type per scene generation, for the frame/poll-driven lookups.
//
// Why this exists: `Resources.FindObjectsOfTypeAll` enumerates the whole object table and wraps
// every match. In the lobby that is a few ms; in a map scene it is ~40 ms per call, whatever type
// is asked for. A 1 s TTL was not enough: six subsystems polling at different rates still made
// ~27% of a wall second in a map (6-7 types x 40 ms at 1 Hz), and the timers clustered.
//
// This cache is generation-scoped, not time-scoped. A type is scanned once per scene generation
// and then held; explicit events (body spawn, session change, wardrobe/menu open, POV change)
// invalidate the groups whose object set can actually change. A scan that came back empty is
// retried with exponential backoff (1 s .. 30 s) because "not spawned yet" is the one case where
// no event can fire; once it finds something it is held for the generation.
//
// Menu/UI types are the exception: their objects are created and destroyed by opening and closing
// panels. MenuScope owns that lifecycle: it holds the Pauser, listens to PauseStateChanged, and
// calls InvalidateMenu() on every open/close edge, so the menu lookups below use the same
// generation cache as everything else - one scan per open, none while the menu is up and idle.
// ScanPolled() remains for the panels the mod does not own (wardrobe, pickers), where the opening
// event is the cursor becoming visible.
//
// Objects destroyed after a snapshot read as null through the usual Unity fake-null check, which
// every caller already performs. The managed wrapper array itself stays valid for the generation.
internal static class SceneCache
{
    private const double EmptyRetryMin = 1.0;
    private const double EmptyRetryMax = 30.0;
    // A type that is genuinely absent in this scene must stop costing anything. Five attempts
    // (t0, +1, +2, +4, +8 s) cover "spawned a moment later"; after that the entry is held empty
    // until an event invalidates the group. The 30 s cap used to mean one object-table walk every
    // half minute forever - visible as a scan row per 30 s in the menu arm.
    private const int EmptyRetryAttempts = 5;

    private sealed class Entry
    {
        public Il2CppReferenceArray<UnityEngine.Object> Arr;
        public double At;
        public double NextRetry;
        public double RetryDelay;
        public int Scans;
    }

    private static readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private static int _sceneHandle = int.MinValue;
    private static int _sceneChanges, _scans;
    private static double _scanMs;
    private static bool _cursorVisible = true;

    // Simple-name groups. Il2CppSystem.Type.Name is the unqualified name for every type the mod
    // uses (including nested game types), so these are stable keys for the cache.
    private static readonly string[] PlayerGroup =
    {
        "CurrentPlayerProvider", "Player", "PlayerModelSetuper",
        "ThirdViewPlayerModel", "FirstViewPlayerModel", "PlayerPOVController",
        "PlayerModelReferences", "RenderersRenderingController", "PlayersAppearanceManager",
        "PlayersModelSetuper", "MagicaCloth",
    };
    private static readonly string[] BodyGroup =
    {
        "ThirdViewPlayerModel", "FirstViewPlayerModel", "PlayerModelSetuper",
        "PlayerModelReferences", "RenderersRenderingController", "MagicaCloth",
    };
    private static readonly string[] MenuGroup =
    {
        "RebindActionUI", "Pauser", "TMP_InputField", "PauseMenuView",
        "SettingsPageController", "Canvas", "Toggle", "Tab", "PlayerInput",
        "RebindActionsCollection", "InputActionAsset", "MonoBehaviour",
    };
    private static readonly string[] WardrobeGroup =
    {
        "WardrobeModelDisplayer", "WardrobeOptionalViewController", "PlayerModelData",
    };
    private static readonly string[] CameraGroup = { "Camera" };
    private static readonly string[] RendererGroup = { "Renderer" };

    internal static string State =>
        $"scene={_sceneHandle} changes={_sceneChanges} types={_map.Count} scans={_scans} " +
        $"scan_ms={_scanMs:F1} cursor={(_cursorVisible ? 1 : 0)}";

    private static double Now => Stopwatch.GetTimestamp() * (1.0 / Stopwatch.Frequency);

    // A new scene cannot reuse proxies from the previous one. This is the only unconditional reset.
    private static void CheckScene()
    {
        int h;
        try { h = SceneManager.GetActiveScene().handle; } catch { return; }
        if (h == _sceneHandle) return;
        _sceneHandle = h;
        _sceneChanges++;
        _map.Clear();
        try { _cursorVisible = Cursor.visible; } catch { _cursorVisible = true; }
    }

    // Cheap per-frame signal only: cursor visibility is what separates gameplay from a panel.
    // It does not scan anything and is safe in the features-off arm.
    internal static void Tick()
    {
        bool v;
        try { v = Cursor.visible; } catch { return; }
        if (v == _cursorVisible) return;
        _cursorVisible = v;
        if (v) InvalidateMenu();
    }

    internal static void InvalidateAll() => _map.Clear();

    internal static void Invalidate(params string[] names)
    {
        if (names == null) return;
        for (int i = 0; i < names.Length; i++) _map.Remove(names[i]);
    }

    internal static void InvalidatePlayer() => Invalidate(PlayerGroup);
    internal static void InvalidateBody() => Invalidate(BodyGroup);
    internal static void InvalidateMenu() => Invalidate(MenuGroup);
    internal static void InvalidateWardrobe() => Invalidate(WardrobeGroup);
    internal static void InvalidateCamera() => Invalidate(CameraGroup);
    internal static void InvalidateRenderer() => Invalidate(RendererGroup);

    // Generation cache: scan once, hold until a group above is invalidated (or the scene changes).
    internal static Il2CppReferenceArray<UnityEngine.Object> Scan(Il2CppSystem.Type t) => Get(t, 0, false);

    // Polled cache: used only for menu/UI objects that panels create and destroy. The TTL is only
    // consulted while the cursor is visible; gameplay never pays for a menu rescan.
    internal static Il2CppReferenceArray<UnityEngine.Object> ScanPolled(Il2CppSystem.Type t, double seconds) => Get(t, seconds, true);

    // Warm a type during a scene transition rather than on the first gameplay frame that needs it.
    internal static void Prewarm(Il2CppSystem.Type t) { try { Get(t, 0, false); } catch { } }

    private static Il2CppReferenceArray<UnityEngine.Object> Get(Il2CppSystem.Type t, double ttl, bool polled)
    {
        if (t == null) return null;
        CheckScene();
        string key = t.Name ?? "?";
        double now = Now;

        if (_map.TryGetValue(key, out var e) && e.Arr != null)
        {
            if (polled)
            {
                bool due = _cursorVisible && ttl > 0 && now - e.At >= ttl;
                if (!due) { Perf.Hit("scanhit"); return e.Arr; }
            }
            else
            {
                // Non-empty snapshots are held for the generation. Empty ones are retried with
                // backoff, so "not spawned yet" converges without polling forever.
                if (e.Arr.Length > 0 || now < e.NextRetry) { Perf.Hit("scanhit"); return e.Arr; }
            }
        }

        long s = Stopwatch.GetTimestamp();
        var arr = Resources.FindObjectsOfTypeAll(t);
        double ms = (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
        Perf.Add("scan:" + key, s);
        _scans++;
        _scanMs += ms;

        if (e == null) { e = new Entry(); _map[key] = e; }
        e.Arr = arr;
        e.At = now;
        e.Scans++;
        if (arr == null || arr.Length == 0)
        {
            if (e.Scans >= EmptyRetryAttempts) { e.NextRetry = double.PositiveInfinity; }
            else
            {
                e.RetryDelay = e.RetryDelay <= 0 ? EmptyRetryMin : Math.Min(e.RetryDelay * 2.0, EmptyRetryMax);
                e.NextRetry = now + e.RetryDelay;
            }
        }
        else
        {
            e.RetryDelay = 0;
            e.NextRetry = 0;
        }
        return arr;
    }
}
