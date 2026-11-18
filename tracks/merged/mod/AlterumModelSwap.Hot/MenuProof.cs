using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using HWG.Settings.Controls;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Unattended proof rig for the game's own menus: "<ScreensDir>/menu.request", one command per
// poll, deleted on pickup. It exists because ScreenShot.Capture renders a *camera* and a
// screen-space overlay canvas is not in that camera at all - a pause menu can be perfectly
// "verified" by a screenshot that never contained it. So a capture here switches the target
// canvases to ScreenSpaceCamera for exactly one camera render into an RT (restored in finally),
// and every capture is accompanied by a hierarchy dump that says what the canvas actually held.
//
// Commands (line oriented, "key=value" or bare key):
//   pause | unpause | resume        Pause.Pauser.Pause/Unpause - no keyboard, no overlay blind spot
//   settings                        activate the pause menu's Settings tab
//   controls|gameplay|interface|graphics|audio
//                                   activate that tab inside SettingsPageController
//   back                            deactivate the pause menu's open tab (settings -> menu)
//   dump | dumpall                  scene-owned canvases, walk nodes that carry Toggle/Button/Tab/
//                                   TabGroup/TMP label/RebindActionUI, with their live state
//   state                           pause/settings/controls resolution + row/toggle counts
//   rows                            every RebindActionUI: action, binding id/index, resolved
//                                   effective path, displayed string (the row-resolution evidence)
//   actions                         the mod binding registry: id, context, default, effective
//   text                            TMP labels only (find a row by its caption)
//   shoot[=tag]                     capture every canvas in the selected root into an RT + PNG
//   canvas=<substr>                 choose the capture root (hierarchy path substring)
//   rebind=<id>                     press the native row whose action carries that mod id
//   diag[=<id>]                     step-by-step field/exception probe of one mod row + a native
//                                   control row (what ResolveActionAndBinding actually touches)
//   refprobe=<id>                   hand RebindActionUI.GetInputAction a matrix of candidate
//                                   InputActionReference shapes and report which one resolves
//   verify                          one-shot: dump + rows + actions + state + shoot
//
// Everything is read-only except the navigation calls and the RT-mode swap itself, both restored.
internal static class MenuProof
{
    internal const string RequestFile = "menu.request";

    private const float PollSeconds = 0.35f;
    private const int MaxLines = 400;
    private const int MaxShotPixels = 1920;

    private static float _nextPoll;
    private static string _requestDir;
    private static string _requestPath;
    private static readonly List<string> Queue = new List<string>();
    private static float _nextAction;
    private static bool _announced;
    private static string _canvasFilter;
    private static int _shotCount;

    internal static void Tick(IHostContext log)
    {
        if (log == null) return;
        if (!_announced)
        {
            _announced = true;
            try { log.LogInfo($"[menu] proof rig ready request={Path.Combine(log.ScreensDir ?? "", RequestFile)}"); }
            catch { }
        }

        try { Poll(log); }
        catch (Exception e) { log.LogWarning($"[menu] request poll failed: {e.Message}"); }

        if (Queue.Count == 0) return;
        if (Time.unscaledTime < _nextAction) return;
        _nextAction = Time.unscaledTime + PollSeconds;

        string action = Queue[0];
        Queue.RemoveAt(0);
        try { Run(log, action); }
        catch (Exception e) { log.LogWarning($"[menu] '{action}' threw: {e}"); }
    }

    // Exposed so the pause-menu section can hand the same command language to a UI button / the
    // verification driver without a second implementation.
    internal static void Enqueue(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        Queue.Add(command);
    }

    private static void Poll(IHostContext log)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;

        string dir = log.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        // Cached: Path.Combine allocates, and this poll runs every 0.35 s for the life of the process.
        if (!string.Equals(dir, _requestDir, StringComparison.Ordinal))
        {
            _requestDir = dir;
            try { _requestPath = Path.Combine(dir, RequestFile); } catch { _requestPath = null; }
        }
        string path = _requestPath;
        if (string.IsNullOrEmpty(path)) return;
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;

        string text = null;
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[menu] read failed: {e.Message}"); return; }
        try { File.Delete(path); } catch { }
        if (text == null) return;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            Queue.Add(line);
        }
    }

    private static void Run(IHostContext log, string action)
    {
        int eq = action.IndexOf('=');
        string key = (eq > 0 ? action.Substring(0, eq) : action).Trim().ToLowerInvariant();
        string val = eq > 0 ? action.Substring(eq + 1).Trim() : "";

        switch (key)
        {
            case "pause": SetPaused(log, true); return;
            case "unpause":
            case "resume": SetPaused(log, false); return;
            case "settings": ShowPauseTab(log, "settings"); return;
            case "players": ShowPauseTab(log, "players"); return;
            case "controls": ShowSettingsTab(log, "controls"); return;
            case "gameplay": ShowSettingsTab(log, "gameplay"); return;
            case "interface": ShowSettingsTab(log, "interface"); return;
            case "graphics": ShowSettingsTab(log, "graphics"); return;
            case "audio": ShowSettingsTab(log, "audio"); return;
            case "back": HidePauseTab(log); return;
            case "dump": Dump(log, false, false); return;
            case "dumpall": Dump(log, true, false); return;
            case "text": Dump(log, true, true); return;
            case "state": State(log); return;
            case "rows": Rows(log); return;
            case "diag": ModControlsUI.Diag(log, val); return;
            case "refprobe": ModControlsUI.RefProbe(log, val); return;
            case "piprobe": ModControlsUI.PlayerInputProbe(log, val); return;
            case "actions": Actions(log); return;
            case "canvas":
                _canvasFilter = val;
                log.LogInfo($"[menu] capture root filter='{_canvasFilter}'");
                return;
            case "shoot": Shoot(log, val.Length == 0 ? $"menu_{++_shotCount:00}" : val); return;
            case "rebind": RowPress(log, val); return;
            case "set": Set(log, val); return;
            case "reset": Reset(log, val); return;
            case "resetall": ModBindings.ResetAll("menu.request"); log.LogInfo("[menu] reset all overrides"); return;
            case "toggle": ToggleWidget(log, val); return;
            case "verify":
                Dump(log, true, false);
                Rows(log);
                Actions(log);
                State(log);
                Shoot(log, val.Length == 0 ? "menu_verify" : val);
                return;
            default:
                log.LogWarning($"[menu] unknown request line '{action}'");
                return;
        }
    }

    // ---- navigation ---------------------------------------------------------------------

    private static Pause.Pauser FindPauser()
    {
        var arr = SceneCache.ScanPolled(Il2CppType.Of<Pause.Pauser>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i].TryCast<Pause.Pauser>();
            if (p == null) continue;
            var go = p.gameObject;
            if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
            return p;
        }
        return null;
    }

    private static UI.Pause.PauseMenuView FindPauseView()
    {
        var arr = SceneCache.ScanPolled(Il2CppType.Of<UI.Pause.PauseMenuView>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var v = arr[i].TryCast<UI.Pause.PauseMenuView>();
            if (v == null) continue;
            var go = v.gameObject;
            if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
            return v;
        }
        return null;
    }

    private static Pause.UI.Controllers.SettingsPageController FindSettingsController()
    {
        var arr = SceneCache.ScanPolled(Il2CppType.Of<Pause.UI.Controllers.SettingsPageController>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Pause.UI.Controllers.SettingsPageController>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
            return c;
        }
        return null;
    }

    private static void SetPaused(IHostContext log, bool paused)
    {
        var pauser = FindPauser();
        if (pauser == null) { log.LogWarning("[menu] no scene Pauser found"); return; }
        try
        {
            if (paused) { if (!pauser.IsPaused) pauser.Pause(); }
            else { if (pauser.IsPaused) pauser.Unpause(); }
            log.LogInfo($"[menu] pause={(paused ? "on" : "off")} isPaused={pauser.IsPaused} " +
                        $"path='{ClothInternals.PathOf(pauser.transform)}'");
        }
        catch (Exception e) { log.LogWarning($"[menu] pause toggle failed: {e.Message}"); }
    }

    private static void ShowPauseTab(IHostContext log, string which)
    {
        var view = FindPauseView();
        if (view == null) { log.LogWarning("[menu] no scene PauseMenuView found"); return; }
        try
        {
            var tab = which == "players" ? view._playersTab : view._settingsTab;
            if (tab == null) { log.LogWarning($"[menu] pause tab '{which}' is null"); return; }
            tab.Activate();
            log.LogInfo($"[menu] pause tab '{which}' activated={tab.IsActivated} " +
                        $"group='{(view._menuTabs != null ? ClothInternals.PathOf(view._menuTabs.transform) : "null")}' " +
                        $"viewport='{(view._viewport != null ? ClothInternals.PathOf(view._viewport) : "null")}'");
        }
        catch (Exception e) { log.LogWarning($"[menu] pause tab '{which}' failed: {e.Message}"); }
    }

    private static void HidePauseTab(IHostContext log)
    {
        var view = FindPauseView();
        if (view == null) { log.LogWarning("[menu] no scene PauseMenuView found"); return; }
        try
        {
            var tab = view._settingsTab;
            // Tab has no "deactivate" of its own: the group activates one tab at a time, so the
            // reliable drive is the other tab (players) or the view's own continue button.
            if (tab != null && tab.IsActivated) view._playersTab?.Activate();
            log.LogInfo($"[menu] pause tab switched back: settings={view._settingsTab?.IsActivated} players={view._playersTab?.IsActivated}");
        }
        catch (Exception e) { log.LogWarning($"[menu] back failed: {e.Message}"); }
    }

    private static void ShowSettingsTab(IHostContext log, string which)
    {
        var c = FindSettingsController();
        if (c == null) { log.LogWarning("[menu] no scene SettingsPageController found"); return; }
        try
        {
            switch (which)
            {
                case "controls": c.OnControlsTabActivated(); break;
                case "gameplay": c.OnGameplayTabActivated(); break;
                case "interface": c.OnInterfaceTabActivated(); break;
                case "graphics": c.OnGraphicsTabActivated(); break;
                case "audio": c.OnAudioTabActivated(); break;
            }
            log.LogInfo($"[menu] settings tab '{which}' activated controller='{ClothInternals.PathOf(c.transform)}'");
        }
        catch (Exception e) { log.LogWarning($"[menu] settings tab '{which}' failed: {e.Message}"); }
    }

    // ---- reports ------------------------------------------------------------------------

    private static void State(IHostContext log)
    {
        var sb = new StringBuilder();
        var pauser = FindPauser();
        sb.AppendLine($"  pauser={(pauser != null ? $"isPaused={pauser.IsPaused} '{ClothInternals.PathOf(pauser.transform)}'" : "null")}");
        var view = FindPauseView();
        if (view != null)
        {
            sb.AppendLine($"  pauseView='{ClothInternals.PathOf(view.transform)}' tabs='{(view._menuTabs != null ? ClothInternals.PathOf(view._menuTabs.transform) : "null")}' " +
                          $"settingsTab={(view._settingsTab != null && view._settingsTab.IsActivated)} playersTab={(view._playersTab != null && view._playersTab.IsActivated)}");
        }
        else sb.AppendLine("  pauseView=null");

        var c = FindSettingsController();
        sb.AppendLine($"  settingsController={(c != null ? $"'{ClothInternals.PathOf(c.transform)}'" : "null")}");
        if (c != null)
        {
            sb.AppendLine($"    controlsTab={(c._controlsTab != null && c._controlsTab.IsActivated)} interfaceTab={(c._interfaceTab != null && c._interfaceTab.IsActivated)} " +
                          $"controlsView='{(c._controlsViewController != null ? ClothInternals.PathOf(c._controlsViewController.transform) : "null")}'");
        }

        int rows = 0, modRows = 0, toggles = 0, activeToggles = 0, tabs = 0;
        var rowArr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 1.0);
        for (int i = 0; i < rowArr.Length; i++)
        {
            var r = rowArr[i].TryCast<RebindActionUI>();
            if (r == null) continue;
            var go = r.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            rows++;
            if (IsModRow(r)) modRows++;
        }
        var togArr = SceneCache.ScanPolled(Il2CppType.Of<UI.Elements.Toggle>(), 1.0);
        for (int i = 0; i < togArr.Length; i++)
        {
            var t = togArr[i].TryCast<UI.Elements.Toggle>();
            if (t == null) continue;
            var go = t.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            toggles++;
            bool active = false;
            try { active = t.IsActive; } catch { }
            if (active) activeToggles++;
        }
        var tabArr = SceneCache.ScanPolled(Il2CppType.Of<UI.Elements.Tab>(), 1.0);
        for (int i = 0; i < tabArr.Length; i++)
        {
            var t = tabArr[i].TryCast<UI.Elements.Tab>();
            if (t == null) continue;
            var go = t.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            tabs++;
        }
        sb.AppendLine($"  rebindRows={rows} modRows={modRows} toggles={toggles} togglesOn={activeToggles} tabs={tabs}");
        sb.AppendLine($"  registry={ModBindings.Summary()}");
        sb.AppendLine($"  controlsUI={ModControlsUI.Summary()} pauseSection={ModPauseSettings.Summary()}");
        log.LogInfo($"[menu:state]\n{sb}");
    }

    private static bool IsModRow(RebindActionUI row)
    {
        try
        {
            var r = row.actionReference;
            if (r == null) return false;
            var a = r.action;
            return a != null && a.name != null && a.name.StartsWith("mod.", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // One line per RebindActionUI: the resolved action and binding index (the task's "dump every
    // mod row with resolved action and binding"), what the widget shows, and what the input
    // system will actually use (effectivePath).
    private static void Rows(IHostContext log)
    {
        var sb = new StringBuilder();
        int seen = 0, mod = 0, resolved = 0;
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var row = arr[i].TryCast<RebindActionUI>();
            if (row == null) continue;
            var go = row.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            seen++;
            bool isMod = IsModRow(row);
            if (isMod) mod++;
            LogRow(sb, row, isMod, ref resolved);
        }
        // A row can be inactive while its view is cached: those are the "added but not visible"
        // case a capture would hide, so they are listed too but flagged.
        log.LogInfo($"[menu:rows] total={seen} mod={mod} resolved={resolved}\n{sb}");
    }

    private static void LogRow(StringBuilder sb, RebindActionUI row, bool isMod, ref int resolved)
    {
        var go = row.gameObject;
        string path = ClothInternals.PathOf(go.transform);
        string action = "?", asset = "?", bindingId = "?", display = "?", effective = "?", label = "?", shown = "?";
        int index = -1;
        bool res = false;
        try
        {
            var r = row.actionReference;
            var a = r != null ? r.action : null;
            action = a != null ? a.name : "null";
            var ast = a != null ? a.actionMap?.asset : null;
            asset = ast != null ? ast.name : (r != null && r.asset != null ? r.asset.name : "null");
            bindingId = row.bindingId;
            display = row.DisplayString;
            try
            {
                InputAction ra = null; int bi = -1;
                res = row.ResolveActionAndBinding(out ra, out bi);
                if (res)
                {
                    resolved++;
                    index = bi;
                    var b = ra.bindings[bi];
                    effective = string.IsNullOrEmpty(b.effectivePath) ? b.path : b.effectivePath;
                }
            }
            catch (Exception e) { effective = $"<{e.Message}>"; }
            label = row.m_ActionLabel != null ? (row.m_ActionLabel.text ?? "") : "null";
            shown = row.m_BindingText != null ? (row.m_BindingText.text ?? "") : "null";
        }
        catch (Exception e) { action = $"<{e.Message}>"; }

        sb.AppendLine($"  {(isMod ? "MOD" : "   ")} row '{path}' active={go.activeInHierarchy} action='{action}' asset='{asset}' " +
                      $"bindingId={(bindingId == null ? "null" : (bindingId.Length > 8 ? bindingId.Substring(0, 8) : bindingId))} " +
                      $"resolved={res} index={index} effective='{effective}' display='{display}' " +
                      $"label='{Trunc(label)}' shown='{Trunc(shown)}'");
    }

    private static void RowPress(IHostContext log, string id)
    {
        if (id.Length == 0) { log.LogWarning("[menu] rebind needs an action id"); return; }
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var row = arr[i].TryCast<RebindActionUI>();
            if (row == null) continue;
            var go = row.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string name = "?";
            try { var r = row.actionReference; name = r != null && r.action != null ? r.action.name : "?"; }
            catch { }
            if (name != id && name != "mod." + id) continue;
            try
            {
                row.StartInteractiveRebind();
                log.LogInfo($"[menu] row '{id}' StartInteractiveRebind() ongoing={(row.ongoingRebind != null)}");
            }
            catch (Exception e) { log.LogWarning($"[menu] row '{id}' rebind start failed: {e.Message}"); }
            return;
        }
        log.LogWarning($"[menu] no row for action '{id}'");
    }

    private static void Actions(IHostContext log) => log.LogInfo($"[menu:actions]\n{ModBindings.Inventory()}");

    // "set=mod.clothes.toggle:/g" or "<Keyboard>/g" - the same path a completed native rebind or a
    // hand-edited store produces, so the verification drives the real override path.
    private static void Set(IHostContext log, string val)
    {
        int colon = val.IndexOf(':');
        if (colon <= 0) { log.LogWarning("[menu] set needs id:path"); return; }
        string id = val.Substring(0, colon).Trim();
        string path = val.Substring(colon + 1).Trim();
        bool ok = ModControlsUI.ApplyOverride(log, id, path);
        log.LogInfo($"[menu] set id={id} path='{path}' ok={ok} effective='{ModBindings.KeyText(ModBindings.Get(id)?.Current ?? default)}'");
    }

    private static void Reset(IHostContext log, string id)
    {
        if (id.Length == 0) { ModBindings.ResetAll("menu.request"); log.LogInfo("[menu] reset all"); return; }
        // The row's own InputAction carries the binding override, and the mirror copies it back into
        // the registry every tick - so clearing only the store would be undone half a second later
        // (measured: reset -> F5, then G again). Clear the action through the game's own reset first.
        var row = ModControlsUI.Row(id);
        bool nativeReset = false;
        if (row != null)
        {
            try { row.ResetToDefault(); nativeReset = true; }
            catch (Exception e) { log.LogWarning($"[menu] reset id={id} row ResetToDefault failed: {e.Message}"); }
            try { row.UpdateBindingDisplay(); } catch { }
        }
        bool ok = ModBindings.ResetOverride(id, "menu.request");
        log.LogInfo($"[menu] reset id={id} ok={ok} nativeRowReset={nativeReset} " +
                    $"effective='{ModBindings.KeyText(ModBindings.Get(id)?.Current ?? default)}'");
    }

    // "toggle=chest:on" - drives the ESC-section widget exactly as a click would (the poll then
    // calls stream A's setter on the next tick).
    private static void ToggleWidget(IHostContext log, string val)
    {
        int colon = val.IndexOf(':');
        string which = colon > 0 ? val.Substring(0, colon).Trim() : val.Trim();
        bool on = colon > 0 ? val.Substring(colon + 1).Trim().ToLowerInvariant() is "on" or "1" or "true" : true;
        bool ok = ModPauseSettings.SetWidget(log, which, on);
        log.LogInfo($"[menu] toggle '{which}'={(on ? "on" : "off")} ok={ok}");
    }

    private static void Dump(IHostContext log, bool includeInactive, bool textOnly)
    {
        var sb = new StringBuilder();
        int canvases = 0, nodes = 0, lines = 0;
        bool cut = false;
        var arr = SceneCache.ScanPolled(Il2CppType.Of<Canvas>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null) continue;
            string scene = "";
            try { scene = go.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;
            canvases++;
            sb.AppendLine($"  canvas '{ClothInternals.PathOf(go.transform)}' scene='{scene}' mode={c.renderMode} " +
                          $"active={go.activeInHierarchy} sort={c.sortingOrder}");
            Walker(go.transform, 1, includeInactive, textOnly, sb, ref nodes, ref lines, ref cut);
        }
        log.LogInfo($"[menu:tree] includeInactive={includeInactive} textOnly={textOnly} canvases={canvases} " +
                    $"nodes={nodes} lines={lines}{(cut ? " TRUNCATED" : "")}\n{sb}");
    }

    private static void Walker(Transform t, int depth, bool includeInactive, bool textOnly,
                               StringBuilder sb, ref int nodes, ref int lines, ref bool cut)
    {
        if (t == null || depth > 16 || lines >= MaxLines) { if (lines >= MaxLines) cut = true; return; }
        var go = t.gameObject;
        if (go == null) return;
        nodes++;
        bool active = go.activeInHierarchy;
        if (!includeInactive && !active) return;

        string d = Describe(go, active, textOnly);
        if (d != null) { sb.AppendLine($"    {d}"); lines++; }

        int n = t.childCount;
        for (int i = 0; i < n; i++)
            Walker(t.GetChild(i), depth + 1, includeInactive, textOnly, sb, ref nodes, ref lines, ref cut);
    }

    private static string Describe(GameObject go, bool active, bool textOnly)
    {
        StringBuilder sb = null;
        try
        {
            var mbs = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < mbs.Length; i++)
            {
                var mb = mbs[i];
                if (mb == null) continue;
                string tn;
                try { tn = mb.GetIl2CppType().Name; } catch { continue; }
                bool interesting = tn == "Button" || tn == "Tab" || tn == "ButtonedTab" || tn == "Toggle" ||
                                   tn == "TabGroup" || tn == "RebindActionUI" || tn == "InputField" ||
                                   tn == "TMP_InputField" || tn == "Dropdown" || tn == "EnumDropdown";
                if (!interesting || textOnly) continue;

                string path = ClothInternals.PathOf(go.transform);
                string extra = "";
                try
                {
                    switch (tn)
                    {
                        case "Toggle":
                            var tog = mb.TryCast<UI.Elements.Toggle>();
                            if (tog != null)
                            {
                                var donor = DonorAppliers(go);
                                extra = $" value={(tog.IsActive ? "on" : "off")} interactable={tog.IsInteractable} donors=[{donor}]";
                            }
                            break;
                        case "Button":
                            var b = mb.TryCast<UI.Elements.Button>();
                            if (b != null) extra = $" interactable={b.IsInteractable} canClick={b.CanClick()}";
                            break;
                        case "Tab":
                        case "ButtonedTab":
                            var tab = mb.TryCast<UI.Elements.Tab>();
                            if (tab != null) extra = $" activated={tab.IsActivated}";
                            break;
                        case "RebindActionUI":
                            var r = mb.TryCast<RebindActionUI>();
                            string an = "?";
                            try { an = r != null && r.actionReference != null && r.actionReference.action != null ? r.actionReference.action.name : "null"; } catch { }
                            extra = $" action='{an}'";
                            break;
                    }
                }
                catch (Exception e) { extra = $" <{e.Message}>"; }
                (sb ??= new StringBuilder()).Append($"[{tn}] '{path}' active={active}{extra}").AppendLine();
            }
        }
        catch { }

        try
        {
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp != null)
            {
                string s = "";
                try { s = tmp.text ?? ""; } catch { }
                s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > 90) s = s.Substring(0, 90) + "…";
                string path = ClothInternals.PathOf(go.transform);
                (sb ??= new StringBuilder()).Append($"[{tmp.GetIl2CppType().Name}] '{path}' active={active} text='{s}'").AppendLine();
            }
        }
        catch { }

        if (sb == null) return null;
        string outStr = sb.ToString().TrimEnd('\n', '\r');
        return outStr.Length == 0 ? null : outStr;
    }

    // Component type names on a toggle row that belong to the game's own settings plumbing. Used
    // by the dump (evidence for what a donor row would drag in) and by ModPauseSettings when it
    // strips a cloned row.
    internal static string DonorAppliers(GameObject go)
    {
        var names = new List<string>();
        try
        {
            var mbs = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < mbs.Length; i++)
            {
                var mb = mbs[i];
                if (mb == null) continue;
                string tn;
                try { tn = mb.GetIl2CppType().Name; } catch { continue; }
                if (tn == "Toggle" || tn == "Button" || tn == "TextMeshProUGUI" || tn == "TMP_Text") continue;
                names.Add(tn);
            }
        }
        catch { }
        return string.Join(",", names);
    }

    // ---- capture ------------------------------------------------------------------------

    // Render the selected canvas root into an RT and write a PNG. Screen-space overlay canvases
    // are not in any camera, so they are switched to ScreenSpaceCamera for the single render and
    // restored afterwards; if that swap fails the capture is reported as failed, never as an
    // invisible "proof".
    private static void Shoot(IHostContext log, string tag)
    {
        var canvases = new List<Canvas>();
        var arr = SceneCache.ScanPolled(Il2CppType.Of<Canvas>(), 1.0);
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string path = ClothInternals.PathOf(go.transform);
            if (_canvasFilter != null && path.IndexOf(_canvasFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            canvases.Add(c);
        }
        if (canvases.Count == 0) { log.LogWarning($"[menu:shot] tag='{tag}' no canvas matched filter='{_canvasFilter}'"); return; }

        int w = Mathf.Clamp(Screen.width, 64, MaxShotPixels);
        int h = Mathf.Clamp(Screen.height, 64, MaxShotPixels);

        var olds = new List<(Canvas c, RenderMode mode, Camera cam, float plane, float scale, bool overrideSort)>();

        GameObject camGo = null; Camera cam = null; RenderTexture rt = null; Texture2D tex = null;
        RenderTexture prevActive = RenderTexture.active;
        try
        {
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            { name = "AlterumMenuProofRT", antiAliasing = 1, useMipMap = false, autoGenerateMips = false };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");

            camGo = new GameObject("AlterumMenuProofCam");
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.targetTexture = rt;
            cam.transform.position = new Vector3(0f, 0f, -10f);

            int mask = 0;
            foreach (var c in canvases)
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (!go.activeInHierarchy) continue;
                olds.Add((c, c.renderMode, c.worldCamera, c.planeDistance, c.scaleFactor,
                          c.overrideSorting));
                mask |= 1 << go.layer;
            }
            if (olds.Count == 0) { log.LogWarning($"[menu:shot] tag='{tag}' canvases={canvases.Count} none active"); return; }
            cam.cullingMask = mask;

            foreach (var o in olds)
            {
                o.c.renderMode = RenderMode.ScreenSpaceCamera;
                o.c.worldCamera = cam;
                o.c.planeDistance = 1f;
                o.c.overrideSorting = o.overrideSort;
            }

            cam.Render();

            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];

            Directory.CreateDirectory(log.ScreensDir);
            string file = Path.Combine(log.ScreensDir, $"menu_{tag.Replace(' ', '_')}.png");
            File.WriteAllBytes(file, bytes);

            int nonBlack = CountNonBlack(tex);
            var names = new StringBuilder();
            foreach (var o in olds) names.Append(ClothInternals.PathOf(o.c.transform)).Append(';');
            log.LogInfo($"[menu:shot] tag='{tag}' {w}x{h} canvases={olds.Count} nonBlackPixels={nonBlack} " +
                        $"pct={(100f * nonBlack / (w * h)):F1} file='{file}' roots='{names}'");
        }
        catch (Exception e)
        {
            log.LogError($"[menu:shot] tag='{tag}' failed: {e}");
        }
        finally
        {
            foreach (var o in olds)
            {
                try
                {
                    if (o.c == null) continue;
                    o.c.renderMode = o.mode;
                    o.c.worldCamera = o.cam;
                    o.c.planeDistance = o.plane;
                    o.c.overrideSorting = o.overrideSort;
                }
                catch { }
            }
            try { RenderTexture.active = prevActive; } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
        }
    }

    private static int CountNonBlack(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            int n = 0;
            for (int i = 0; i < px.Length; i++)
                if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8) n++;
            return n;
        }
        catch { return -1; }
    }

    private static string Trunc(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    }
}
