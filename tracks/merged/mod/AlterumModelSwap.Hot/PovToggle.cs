using System;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Cameras.SwitchActionProcessors;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// User-facing third-person toggle. Switches the game's own local camera mode through
// PlayerPOVController.EnableThirdPersonViewMode()/EnableFirstPersonViewMode() - the same mode the
// game drives for animation, IK, hitboxes and the mirror - instead of introducing a camera of our
// own. Nothing else is touched: no renderer, layer, mask or scope.
//
// Why it exists: the cosmetics screen policy is "visible in the local third person POV", which is
// correct (a first-person camera must not see the owner's own body), so without this the owner can
// never see their own cosmetic. It is also the only way a player can see the body's proportions.
//
// Triggers:
//   * F7 while the game runs (toggles between third and first person),
//   * a request file "<ScreensDir>/pov.request", deleted on pickup, one word on its first
//     non-comment line:
//       on|third|thirdperson|1   force third person
//       off|first|firstperson|0  force first person
//       toggle                    same as the key
//
// The requested mode is sticky: it is re-asserted when the game returns the controller to the other
// mode (scene change, respawn, the game's own transitions). Every actual transition is logged as
// "[pov] third-person ON/OFF"; the cosmetics runtime then logs its matching "[cosmetic:view]" line
// in the same frame.
internal static class PovToggle
{
    // Registry row `mod.pov.toggle` (default F7); see ModBindings.cs.
    public static string Hotkey => ModBindings.Get("mod.pov.toggle")?.CurrentText ?? "F7";
    private const string ActionId = "mod.pov.toggle";

    private const string RequestFileName = "pov.request";
    private const float PollSeconds = 0.1f;
    // Re-assert at most this often, so a mode that keeps flipping on its own cannot make this spam.
    private const float ApplyRetrySeconds = 0.5f;
    private const float ReassertLogSeconds = 2f;

    private enum Want { None, Third, First }

    private static Want _want = Want.None;
    private static POV _observed = POV.None;
    private static bool _observedThird, _reportedFirst, _reportedThird;
    private static bool _unavailableLogged, _inputLogged, _announced, _inputFailed;
    private static float _nextPoll, _nextApply, _lastReassertLog;
    private static int _applies;

    // Returns true when the camera mode changed (or was first observed) this tick, so the caller can
    // refresh dependent state - the cosmetics screen policy - in the same frame.
    internal static bool Tick(IHostContext log, float now)
    {
        if (log == null) return false;
        if (!_announced)
        {
            _announced = true;
            try
            {
                log.LogInfo($"[pov] ready hotkey={Hotkey} request={RequestPath(log)} values=on/off/toggle " +
                            "(game default: first person; third person is the game's own view mode)");
            }
            catch { }
        }

        try { PollHotkey(log); }
        catch (Exception e)
        {
            _inputFailed = true;
            if (!_inputLogged) { _inputLogged = true; log.LogWarning($"[pov] input unavailable, request file only: {e.Message}"); }
        }
        try { PollRequestFile(log); }
        catch (Exception e) { log.LogWarning($"[pov] request poll failed: {e.Message}"); }

        if (now < _nextPoll) return false;
        _nextPoll = now + PollSeconds;

        try { return PollAndApply(log, now); }
        catch (Exception e) { log.LogWarning($"[pov] tick failed (camera mode left alone): {e.Message}"); return false; }
    }

    private static bool PollAndApply(IHostContext log, float now)
    {
        var controller = FindController(out var owner);
        if (controller == null)
        {
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                log.LogWarning("[pov] unavailable: no PlayerPOVController under the local player" +
                               (owner == null ? " (no local player in this scene)" : $" ('{owner.name}')") +
                               "; camera mode left alone" +
                               (_want == Want.None ? "" : $"; {WantText(_want)} kept pending"));
            }
            return false;
        }
        if (_unavailableLogged)
        {
            _unavailableLogged = false;
            log.LogInfo("[pov] PlayerPOVController available again");
        }

        POV pov;
        try { pov = controller.CurrentPOV; }
        catch (Exception e) { log.LogWarning($"[pov] CurrentPOV failed: {e.Message}"); return false; }

        bool changed = ReportObserved(log, pov, controller, owner);
        if (changed) { try { SceneCache.InvalidateCamera(); } catch { } }
        if (_want == Want.None) return changed;

        var target = _want == Want.Third ? POV.ThirdPerson : POV.FirstPerson;
        if (pov == target) return changed;
        if (now < _nextApply) return changed;
        _nextApply = now + ApplyRetrySeconds;

        // The mode may not exist in every scene: a switch that throws is a logged no-op, never an
        // exception into the game loop.
        try
        {
            if (_want == Want.Third) controller.EnableThirdPersonViewMode();
            else controller.EnableFirstPersonViewMode();
            _applies++;
            if (_applies == 1)
                log.LogInfo($"[pov] applied {WantText(_want)} (controller='{PathOf(controller.transform)}')");
            else if (now - _lastReassertLog >= ReassertLogSeconds)
            {
                _lastReassertLog = now;
                log.LogInfo($"[pov] re-asserted {WantText(_want)} #{_applies - 1} (game had {pov})");
            }
            // The switch is normally synchronous: report the resulting mode in this same tick, so the
            // ON/OFF line and the [cosmetic:view] line for the new mode are adjacent.
            changed |= ReportObserved(log, controller.CurrentPOV, controller, owner);
            try { SceneCache.InvalidateCamera(); } catch { }
            return changed;
        }
        catch (Exception e)
        {
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                log.LogWarning($"[pov] {WantText(_want)} unavailable here: {e.Message}; camera mode left alone");
            }
        }
        return changed;
    }

    // Exactly one ON/OFF line per actual mode change, plus one line for the mode first observed.
    private static bool ReportObserved(IHostContext log, POV pov, PlayerPOVController controller, Transform owner)
    {
        var previous = _observed;
        _observed = pov;
        string prev = previous == POV.None ? "startup" : previous.ToString();
        if (pov == POV.ThirdPerson)
        {
            bool announce = !_reportedThird || previous != POV.ThirdPerson;
            _reportedThird = true;
            _reportedFirst = false;
            _observedThird = true;
            if (announce)
                log.LogInfo($"[pov] third-person ON (was {prev}) controller='{PathOf(controller.transform)}' owner='{owner.name}'");
            return announce;
        }
        if (pov == POV.FirstPerson)
        {
            bool announce = !_reportedFirst || previous != POV.FirstPerson;
            _reportedFirst = true;
            _reportedThird = false;
            _observedThird = false;
            if (announce)
                log.LogInfo($"[pov] third-person OFF (was {prev}) controller='{PathOf(controller.transform)}' owner='{owner.name}'");
            return announce;
        }
        return false;
    }

    private static void PollHotkey(IHostContext log)
    {
        if (_inputFailed) return;
        if (!ModBindings.Pressed(ActionId, out string trigger)) return;
        SetWant(_observedThird ? Want.First : Want.Third);
        log.LogInfo($"[pov] {trigger} pressed -> {WantText(_want)}");
    }

    private static void PollRequestFile(IHostContext log)
    {
        string request = null;
        try
        {
            request = RequestPath(log);
            if (request == null || !File.Exists(request)) return;
            string text = "";
            try { text = File.ReadAllText(request); } catch (Exception e) { log.LogWarning($"[pov] request read failed: {e.Message}"); }
            try { File.Delete(request); } catch { }

            string value = FirstWord(text);
            switch (value)
            {
                case "on": case "third": case "thirdperson": case "third-person": case "1":
                    SetWant(Want.Third);
                    log.LogInfo("[pov] request third-person (from pov.request)");
                    break;
                case "off": case "first": case "firstperson": case "first-person": case "0":
                    SetWant(Want.First);
                    log.LogInfo("[pov] request first-person (from pov.request)");
                    break;
                case "toggle":
                    SetWant(_observedThird || _want == Want.Third ? Want.First : Want.Third);
                    log.LogInfo($"[pov] request toggle -> {WantText(_want)} (from pov.request)");
                    break;
                case "":
                    log.LogWarning("[pov] request file was empty; camera mode left alone");
                    break;
                default:
                    log.LogWarning($"[pov] request '{value}' is not on/off/toggle; camera mode left alone");
                    break;
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"[pov] request file '{request}' failed: {e.Message}");
            try { if (request != null && File.Exists(request)) File.Delete(request); } catch { }
        }
    }

    private static void SetWant(Want want)
    {
        _want = want;
        _nextApply = 0f;   // apply on the next poll, not after the retry window
        _nextPoll = 0f;
        _applies = 0;
        _lastReassertLog = 0f;
    }

    private static string WantText(Want w) => w == Want.Third ? "third-person" : w == Want.First ? "first-person" : "no change";

    private static string FirstWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int space = line.IndexOfAny(new[] { ' ', '\t', '=' });
            return (space > 0 ? line.Substring(0, space) : line).Trim().ToLowerInvariant();
        }
        return "";
    }

    // The scene's own controller for the local player: CurrentPlayerProvider first, then Player.IsOwner.
    private static PlayerPOVController FindController(out Transform owner)
    {
        owner = LocalOwner();
        if (owner == null) return null;
        var controllers = SceneCache.Scan(Il2CppType.Of<PlayerPOVController>());
        for (int i = 0; i < controllers.Length; i++)
        {
            var controller = controllers[i].TryCast<PlayerPOVController>();
            if (controller == null) continue;
            var t = controller.transform;
            if (t == null || !MirrorPlayerVisibility.Under(t, owner)) continue;
            return controller;
        }
        return null;
    }

    private static Transform LocalOwner()
    {
        try
        {
            var providers = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
            for (int i = 0; i < providers.Length; i++)
            {
                var provider = providers[i].TryCast<CurrentPlayerProvider>();
                if (provider == null) continue;
                var player = provider.CurrentPlayer;
                if (player != null && player.transform != null) return player.transform;
            }
        }
        catch { }
        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i].TryCast<Player>();
                if (player == null || !player.IsOwner) continue;
                if (player.transform == null || string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                return player.transform;
            }
        }
        catch { }
        return null;
    }

    private static string RequestPath(IHostContext log)
    {
        try { return log.ScreensDir == null ? null : Path.Combine(log.ScreensDir, RequestFileName); }
        catch { return null; }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }
}
