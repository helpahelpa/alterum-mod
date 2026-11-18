using System;
using System.Reflection;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// "Is a game menu up right now?" - answered from a held reference and an event, never from a scan.
//
// Why this exists: every settings/controls lookup used to be a TTL poll (`SceneCache.ScanPolled`,
// 0.5-1 s) because panels create and destroy the objects the mod needs. That is only paid while
// the cursor is visible, and the Menu scene is the one scene where the cursor is visible for its
// whole life without any panel being open: the polls then walk the object table every second for
// nothing (measured: 54x PauseMenuView + 40x each of Pauser/RebindActionUI/TMP_InputField in 40 s,
// menu p99 22.4 ms vs 11.9 ms control).
//
// The objects a menu shows are stable while it is open, so the cache entry only has to be dropped
// when the menu opens, when its page changes and when it closes. This class is that lifecycle:
//
//   * the Pauser instance is captured once, from a postfix on `Pause.Pauser.Awake`; if that hook
//     does not attach, one generation-scoped scan per scene is the fallback (no timer, no retry);
//   * the open/closed edge comes from `Pauser.PauseStateChanged` (the game's own event) with a
//     cheap `IsPaused` read at 4 Hz as the safety net - a property on a held reference, no scan;
//   * on each edge (and on a settings page change the panels report) `SceneCache.InvalidateMenu()`
//     runs, so the next lookup after the event rescans and every lookup after that is a cache hit.
//
// `Settling` is the only retry window left: for a short time after an edge, a panel that is a frame
// or two late may still be resolved. Nothing retries on a timer in steady state.
internal static class MenuScope
{
    internal const float SettleSeconds = 2.0f;
    private const float StatePollSeconds = 0.25f;

    private static IHostContext _log;
    private static Harmony _harmony;
    private static Pause.Pauser _pauser;
    private static Il2CppSystem.Action<bool> _onChanged;
    private static bool _subscribed;
    private static bool _hookStateChecked;
    private static bool _isOpen;
    private static int _generation;
    private static int _edges;
    private static int _lookups;
    private static float _settleUntil;
    private static float _nextState;
    private static int _sceneHandle = int.MinValue;
    private static string _via = "none";

    internal static bool IsOpen => _isOpen;

    // Bumped on every open/close edge and on every page change. Consumers that hold references to
    // panel objects compare it and rebuild only when it moves.
    internal static int Generation => _generation;

    internal static Pause.Pauser Pauser => _pauser;

    // The bounded retry window: true only for a couple of seconds after an edge.
    internal static bool Settling
    {
        get { try { return Time.unscaledTime < _settleUntil; } catch { return false; } }
    }

    internal static string Summary =>
        $"open={(_isOpen ? 1 : 0)} gen={_generation} edges={_edges} pauser={(_pauser != null ? 1 : 0)} " +
        $"via={_via} lookups={_lookups} settling={(Settling ? 1 : 0)}";

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_pauser != null) return;
        if (_harmony != null) return;
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(Pause.Pauser), "Awake");
            if (m == null)
            {
                _via = "no-awake";
                log?.LogWarning("[menu:scope] Pause.Pauser.Awake not found; pauser resolves by one scan per scene");
                return;
            }
            _harmony = new Harmony("com.user0.alterum.menuscope." + Guid.NewGuid().ToString("N"));
            _harmony.Patch(m, postfix: new HarmonyMethod(typeof(MenuScope), nameof(PostAwake)));
            log?.LogInfo("[menu:scope] installed (Pauser.Awake postfix + PauseStateChanged)");
        }
        catch (Exception e)
        {
            _harmony = null;
            _via = "patch-failed";
            log?.LogWarning($"[menu:scope] install failed ({e.Message}); pauser resolves by one scan per scene");
        }
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        try
        {
            if (_subscribed && _pauser != null && _onChanged != null)
                _pauser.remove_PauseStateChanged(_onChanged);
        }
        catch { }
        _subscribed = false;
        _onChanged = null;
        _pauser = null;
        _isOpen = false;
        _log = null;
        _via = "disposed";
    }

    // Harmony postfix: the instance exists the moment the game creates it, so no scan is needed.
    private static void PostAwake(Pause.Pauser __instance)
    {
        try { Attach(__instance, "awake"); }
        catch (Exception e) { _log?.LogWarning($"[menu:scope] attach failed: {e.Message}"); }
    }

    private static void Attach(Pause.Pauser p, string via)
    {
        if (p == null) return;
        if (_pauser != null && !_pauser.Equals(null) && _pauser.Pointer == p.Pointer) return;
        try { if (_subscribed && _pauser != null && _onChanged != null) _pauser.remove_PauseStateChanged(_onChanged); } catch { }
        _subscribed = false;
        _pauser = p;
        _via = via;
        try
        {
            _onChanged = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(new Action<bool>(OnPauseChanged));
            p.add_PauseStateChanged(_onChanged);
            _subscribed = true;
        }
        catch (Exception e) { _log?.LogWarning($"[menu:scope] PauseStateChanged subscribe failed: {e.Message}"); }

        bool open = false;
        try { open = p.IsPaused; } catch { }
        SetState(open, "attach");
        _log?.LogInfo($"[menu:scope] pauser attached via {via} isPaused={open}");
    }

    private static void OnPauseChanged(bool open)
    {
        try { SetState(open, "event"); }
        catch (Exception e) { _log?.LogWarning($"[menu:scope] event threw: {e.Message}"); }
    }

    // Per frame, from HotModule.Update before the feature ticks. Cost when nothing changes: one
    // scene-handle read and (4 Hz) one IsPaused property read on a held reference.
    internal static void Tick()
    {
        int h;
        try { h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { return; }

        if (_pauser == null || _pauser.Equals(null))
        {
            // No hook and no instance yet: one scan per scene generation, never a timer.
            if (h != _sceneHandle)
            {
                _sceneHandle = h;
                LookupOnce();
            }
            return;
        }
        _sceneHandle = h;

        float now;
        try { now = Time.unscaledTime; } catch { return; }
        if (now < _nextState) return;
        _nextState = now + StatePollSeconds;

        try { SetState(_pauser.IsPaused, "poll"); } catch { }
    }

    private static void LookupOnce()
    {
        if (!_hookStateChecked)
        {
            _hookStateChecked = true;
            if (_via == "none") _via = "scan";
        }
        if (_via == "awake") _via = "scan-fallback";
        _lookups++;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Pause.Pauser>());
            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i].TryCast<Pause.Pauser>();
                if (p == null) continue;
                var go = p.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                Attach(p, "scan");
                return;
            }
        }
        catch (Exception e) { _log?.LogWarning($"[menu:scope] lookup failed: {e.Message}"); }
    }

    private static void SetState(bool open, string why)
    {
        if (open == _isOpen) return;
        _isOpen = open;
        _generation++;
        _edges++;
        try { _settleUntil = Time.unscaledTime + SettleSeconds; } catch { _settleUntil = 0; }
        try { SceneCache.InvalidateMenu(); } catch { }
        _log?.LogInfo($"[menu:scope] menu {(open ? "open" : "closed")} gen={_generation} via={why}");
    }
}
