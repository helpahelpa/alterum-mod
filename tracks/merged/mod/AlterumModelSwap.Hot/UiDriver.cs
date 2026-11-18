using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AlterumModelSwap.Hot;

// Unattended UI driver: "<ScreensDir>/ui.request", line-oriented, polled ~3x a second and deleted
// on pickup. It exposes the *same* click machinery the startup ClickSequence uses
// (ModelSwapRuntime.ActivateOrClick: Button.Click + ClickedSafe + ClickedSafeReferenced +
// ClickedUnsafe, and Tab.Activate), so a menu can be walked by path without a keyboard and
// without a visible desktop. Nothing here invents a second way to press a button.
//
// Line commands, executed one per poll so the UI gets a frame between actions:
//
//   dump                  every active Button/Tab/label under every real (scene-owned) canvas
//   dumpall               the same, including inactive objects (hidden panels)
//   text                  TMP labels only, path + string (to identify a button by its caption)
//   scene                 active + loaded scenes, IsMapRun, local player, held item
//   inv                   the local player's inventories, slot by slot (read-only)
//   wardrobe              open the native wardrobe UI directly (diagnostic setup; no selection;
//                         the same native entry point as GameConsole's gated test.open-wardrobe)
//   click=<substr>        click every Button/Tab whose hierarchy path contains <substr>
//   seq=<a|b|c>           hand a whole sequence to the auto-click list (same syntax as the cfg)
//
// The dump lines are the paths ActivateOrClick matches on, so a probe round is:
//   dump  ->  read [ui:tree]  ->  click=<path from the dump>.
internal static class UiDriver
{
    internal const string RequestFile = "ui.request";

    private const float PollSeconds = 0.35f;
    private const int MaxLines = 500;

    internal static bool Pending => Queue.Count != 0;
    private static float _nextPoll;
    private static readonly List<string> Queue = new List<string>();
    private static float _nextAction;
    private static bool _announced;

    internal static void Tick(IHostContext log, ModelSwapRuntime runtime)
    {
        if (log == null) return;
        if (!_announced)
        {
            _announced = true;
            try { log.LogInfo($"[ui] driver ready request={Path.Combine(log.ScreensDir ?? "", RequestFile)}"); }
            catch { }
        }
        try { Poll(log); }
        catch (Exception e) { log.LogWarning($"[ui] request poll failed: {e.Message}"); }

        if (Queue.Count == 0) return;
        if (Time.unscaledTime < _nextAction) return;
        _nextAction = Time.unscaledTime + PollSeconds;

        string action = Queue[0];
        Queue.RemoveAt(0);
        try { Run(log, runtime, action); }
        catch (Exception e) { log.LogWarning($"[ui] '{action}' threw: {e}"); }
    }

    private static void Poll(IHostContext log)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;

        string dir = log.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        string path = Path.Combine(dir, RequestFile);
        if (!File.Exists(path)) return;

        string text = null;
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[ui] read failed: {e.Message}"); return; }
        try { File.Delete(path); } catch { }
        if (text == null) return;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            Queue.Add(line);
        }
    }

    private static void Run(IHostContext log, ModelSwapRuntime runtime, string action)
    {
        int eq = action.IndexOf('=');
        string key = (eq > 0 ? action.Substring(0, eq) : action).Trim().ToLowerInvariant();
        string val = eq > 0 ? action.Substring(eq + 1).Trim() : "";

        switch (key)
        {
            case "dump": Dump(log, false, false); return;
            case "dumpall": Dump(log, true, false); return;
            case "text": Dump(log, true, true); return;
            case "tree": Tree(log, val); return;
            case "screen": ScreenInfo(log); return;
            case "pause": PauseMenu(log, true); return;
            case "unpause": PauseMenu(log, false); return;
            case "cap": Capture(log, val); return;
            case "find": Find(log, val); return;
            case "kids": Kids(log, val); return;
            case "scene": SceneInfo(log); return;
            case "inv": Inventory(log); return;
            case "wardrobe": OpenWardrobe(log); return;
            case "lobby": Lobby(log); return;
            case "standat": ReelInteraction.StandPlayerAt(log, val); return;
            case "maps": Maps(log); return;
            case "items": Items(log); return;
            case "near": Near(log, val); return;
            case "req": RequiredPlayers(log, val); return;
            case "startmap":
                // Bench-only map entry: the one-player start switch is a mod feature, so a
                // features-off control arm has no other way into a map. The game's own
                // GameSceneStarter path does the transition; this verb only forces readiness and
                // selects the first map, exactly like ReelLogic.StartMap's request hook.
                try
                {
                    log.LogInfo("[ui] startmap requested");
                    ReelLogic.StartMap(log);
                }
                catch (Exception e) { log.LogWarning($"[ui] startmap failed: {e.Message}"); }
                return;
            case "click":
            case "tab":
                if (val.Length == 0) { log.LogWarning("[ui] click needs a path substring"); return; }
                if (runtime == null) { log.LogWarning("[ui] no runtime for click"); return; }
                {
                    int hits = runtime.ActivateOrClick(val);
                    log.LogInfo($"[ui] request click '{val}' hits={hits}");
                    if (hits == 0) Dump(log, true, false);
                }
                return;
            case "seq":
                if (runtime == null) { log.LogWarning("[ui] no runtime for seq"); return; }
                runtime.StartSequence(val);
                return;
            default:
                log.LogWarning($"[ui] unknown request line '{action}'");
                return;
        }
    }

    // Diagnostic setup only: use the real optional UI layer so its input, cursor and close lifecycle
    // are correct. Never teleport/walk to open this panel, and never select a model on this path.
    private static void OpenWardrobe(IHostContext log)
    {
        if (TryOpenNativeWardrobe(out string detail))
            log.LogInfo($"[ui:wardrobe] native wardrobe {detail}; selection untouched");
        else
            log.LogWarning($"[ui:wardrobe] {detail}; nothing was opened or selected");
    }

    // The one handler selection rule for both wardrobe lifecycle verbs below: a scene-owned, ACTIVE
    // InputSystem.UI.WardrobeInputHandler. A prefab asset (empty scene) or an inactive handler is not
    // the scene's panel and is skipped. Kept in one place so open and close cannot target different
    // handlers. `detail` is the skip/absence reason; true means `handler` is the panel's handler.
    private static bool TryFindSceneWardrobeHandler(
        out InputSystem.UI.WardrobeInputHandler handler, out string detail)
    {
        handler = null;
        detail = "";
        try
        {
            var handlers = SceneCache.Scan(Il2CppType.Of<InputSystem.UI.WardrobeInputHandler>());
            int found = handlers == null ? 0 : handlers.Length;
            int skipped = 0;
            for (int i = 0; i < found; i++)
            {
                var candidate = handlers[i].TryCast<InputSystem.UI.WardrobeInputHandler>();
                if (candidate == null || candidate.gameObject == null) { skipped++; continue; }
                var go = candidate.gameObject;
                if (!go.scene.IsValid() || !go.activeInHierarchy) { skipped++; continue; }
                handler = candidate;
                return true;
            }
            detail = found == 0
                ? "no scene-owned wardrobe input handler available"
                : $"no ACTIVE scene-owned wardrobe input handler ({found} found, {skipped} skipped)";
            return false;
        }
        catch (Exception e)
        {
            detail = $"wardrobe handler scan threw {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    // The one way this mod opens the native wardrobe panel, shared by the `wardrobe` line above and
    // GameConsole's gated `test.open-wardrobe` row. The scene's own
    // InputSystem.UI.WardrobeInputHandler decides, through its TryOpenWardrobe() lifecycle entry
    // point, so the native camera/scene, optional UI layer, HUD and close callbacks behave exactly
    // as when the wardrobe button is pressed. Showing the view alone would leave the game camera
    // active, which is why no view object, transform or UI element is ever fabricated here.
    //
    // `detail` names the handler's own answer, or why none answered; `false` means the native open
    // did not report success.
    internal static bool TryOpenNativeWardrobe(out string detail)
    {
        detail = "";
        if (!TryFindSceneWardrobeHandler(out var handler, out detail)) return false;
        try
        {
            bool opened = handler.TryOpenWardrobe();
            detail = $"open={opened} at '{ClothInternals.PathOf(handler.transform)}'";
            return opened;
        }
        catch (Exception e)
        {
            detail = $"native open threw {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    // The one way this mod closes the native wardrobe panel, used by GameConsole's gated
    // `alterum.test.close-wardrobe` row. It calls the same handler's own
    // InputSystem.UI.WardrobeInputHandler.TryCloseWardrobe(): the pinned interop declares it
    // `public bool TryCloseWardrobe()` with no parameters - the same method SahurWardrobe's own close
    // diagnostic calls - and it is the public entry point behind the wardrobe's native close action
    // (`OnCloseWardrobePerformed`). That method owns the native close lifecycle (the optional view's
    // hide and the lobby UI/camera restore); no view Hide(), field write,
    // GameObject/camera/cursor/EventSystem toggle or other fallback exists here, so a native method
    // that is unavailable or reports false is reported as such instead of a fabricated close.
    //
    // Handler selection is the same rule as TryOpenNativeWardrobe, so the two verbs cannot disagree
    // about which handler is the panel's. `detail` names the method's own answer or why none did.
    internal static bool TryCloseNativeWardrobe(out string detail)
    {
        detail = "";
        if (!TryFindSceneWardrobeHandler(out var handler, out detail)) return false;
        try
        {
            bool closed = handler.TryCloseWardrobe();
            detail = $"close={closed} at '{ClothInternals.PathOf(handler.transform)}'";
            return closed;
        }
        catch (Exception e)
        {
            detail = $"native close threw {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    // ---- reports ------------------------------------------------------------------------

    // Every scene-owned Canvas, walked depth-first. A node is printed when it carries one of the
    // components this mod can press (Button/Tab/ButtonedTab) or a label (TMP_Text/dropdown), so
    // the log reads as the clickable surface rather than as geometry.
    private static void Dump(IHostContext log, bool includeInactive, bool textOnly)
    {
        var sb = new StringBuilder();
        int canvases = 0, nodes = 0, lines = 0;
        bool cut = false;
        var arr = SceneCache.Scan(Il2CppType.Of<Canvas>());
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null) continue;
            string scene = "";
            try { scene = go.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;          // prefab/asset canvas, not on screen
            canvases++;
            sb.AppendLine($"  canvas '{ClothInternals.PathOf(go.transform)}' scene='{scene}' mode={c.renderMode} " +
                          $"active={go.activeInHierarchy} sort={c.sortingOrder}");
            Walker(go.transform, 1, includeInactive, textOnly, sb, ref nodes, ref lines, ref cut);
        }
        log.LogInfo($"[ui:tree] includeInactive={includeInactive} textOnly={textOnly} canvases={canvases} " +
                    $"nodes={nodes} lines={lines}{(cut ? " TRUNCATED" : "")}\n{sb}");
    }

    private static void Walker(Transform t, int depth, bool includeInactive, bool textOnly,
                               StringBuilder sb, ref int nodes, ref int lines, ref bool cut)
    {
        if (t == null || depth > 14 || lines >= MaxLines) { if (lines >= MaxLines) cut = true; return; }
        var go = t.gameObject;
        if (go == null) return;
        nodes++;
        bool active = go.activeInHierarchy;
        if (!includeInactive && !active) return;

        string tag = Inactive(go, active, textOnly);
        if (tag != null)
        {
            sb.AppendLine($"    {tag}");
            lines++;
        }

        int n = t.childCount;
        for (int i = 0; i < n; i++)
            Walker(t.GetChild(i), depth + 1, includeInactive, textOnly, sb, ref nodes, ref lines, ref cut);
    }

    private static string Inactive(GameObject go, bool active, bool textOnly) => Describe(go, active, textOnly);

    // One line per interesting component on this object, or null when there is nothing to press
    // and nothing to read.
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
                if (tn != "Button" && tn != "Tab" && tn != "ButtonedTab" && tn != "Dropdown" &&
                    tn != "EnumDropdown" && tn != "Toggle") continue;
                if (textOnly) continue;

                string path = ClothInternals.PathOf(go.transform);
                if (tn == "Button")
                {
                    var b = mb.TryCast<UI.Elements.Button>();
                    bool interactable = false, canClick = false;
                    string safe = "?", safeRef = "?";
                    try { interactable = b.IsInteractable; } catch { }
                    try { canClick = b.CanClick(); } catch { }
                    try { safe = b.ClickedSafe == null ? "null" : "set"; } catch { }
                    try { safeRef = b.ClickedSafeReferenced == null ? "null" : "set"; } catch { }
                    (sb ??= new StringBuilder()).Append(
                        $"[Button] '{(path)}' active={active} interactable={interactable} canClick={canClick} " +
                        $"ClickedSafe={safe} ClickedSafeRef={safeRef}");
                }
                else if (tn == "Tab" || tn == "ButtonedTab")
                {
                    var tab = mb.TryCast<UI.Elements.Tab>();
                    (sb ??= new StringBuilder()).Append($"[{tn}] '{path}' active={active} hasTab={(tab != null)}");
                }
                else
                {
                    (sb ??= new StringBuilder()).Append($"[{tn}] '{path}' active={active}");
                }
                sb.AppendLine();
            }
        }
        catch { }

        try
        {
            var tmp = go.GetComponent<TMPro.TMP_Text>();
            if (tmp != null)
            {
                string s = "";
                try { s = tmp.text ?? ""; } catch { }
                s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > 80) s = s.Substring(0, 80) + "…";
                string path = ClothInternals.PathOf(go.transform);
                (sb ??= new StringBuilder()).Append($"[{(tmp.GetIl2CppType().Name)}] '{path}' active={active} text='{s}'").AppendLine();
            }
        }
        catch { }

        if (sb == null) return null;
        string outStr = sb.ToString().TrimEnd('\n', '\r');
        return outStr.Length == 0 ? null : outStr;
    }

    // One subtree, with geometry: every node from the first transform whose hierarchy path contains
    // <substr>, printing the RectTransform numbers (anchors, pivot, size, offsets, resolved rect),
    // the layout components and every MonoBehaviour type on the node. This is the dump a row has to
    // be cloned from: paths alone say nothing about who positions a row, and a row positioned by a
    // layout group looks identical to a row positioned by an anchoredPosition until the numbers are
    // next to each other.
    private static void Tree(IHostContext log, string val)
    {
        if (string.IsNullOrEmpty(val)) { log.LogWarning("[ui:tree] needs a path substring"); return; }
        var root = FindByPath(val);
        if (root == null) { log.LogWarning($"[ui:tree] no transform matched '{val}'"); return; }

        var sb = new StringBuilder();
        int nodes = 0;
        Walk(root, 0, sb, ref nodes, 400);
        log.LogInfo($"[ui:tree] root='{ClothInternals.PathOf(root)}' nodes={nodes}\n{sb}");
    }

    private static Transform FindByPath(string substr)
    {
        var arr = SceneCache.Scan(Il2CppType.Of<Canvas>());
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            var hit = DeepFind(go.transform, substr);
            if (hit != null) return hit;
        }
        return null;
    }

    // Walks every node (active or not): the pause menu's rows are under a panel that is disabled
    // while the game runs, and a search that skips those finds nothing.
    private static Transform DeepFind(Transform t, string substr)
    {
        if (t == null) return null;
        var go = t.gameObject;
        if (go != null && ClothInternals.PathOf(t).IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0) return t;
        int n = t.childCount;
        for (int i = 0; i < n; i++)
        {
            var r = DeepFind(t.GetChild(i), substr);
            if (r != null) return r;
        }
        return null;
    }

    private static void Walk(Transform t, int depth, StringBuilder sb, ref int nodes, int cap)
    {
        if (t == null || nodes >= cap) return;
        var go = t.gameObject;
        if (go == null) return;
        nodes++;
        string pad = new string(' ', 2 + depth * 2);
        sb.Append(pad).Append(go.name).Append(go.activeSelf ? "" : " [inactive]");

        var rt = t.TryCast<RectTransform>();
        if (rt != null)
        {
            var r = rt.rect;
            sb.Append($" rt aMin=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2}) aMax=({rt.anchorMax.x:F2},{rt.anchorMax.y:F2}) " +
                      $"piv=({rt.pivot.x:F2},{rt.pivot.y:F2}) pos=({rt.anchoredPosition.x:F1},{rt.anchoredPosition.y:F1}) " +
                      $"size=({rt.sizeDelta.x:F1},{rt.sizeDelta.y:F1}) rect=({r.width:F1}x{r.height:F1}) " +
                      $"scale={rt.localScale.x:F2}");
        }
        else if (t.GetType() != null) sb.Append(" [no RectTransform]");

        // Layout components decide who positions the children; print their settings verbatim.
        var vlg = go.GetComponent<VerticalLayoutGroup>();
        if (vlg != null)
            sb.Append($" [VLG spacing={vlg.spacing:F1} pad={vlg.padding.left}/{vlg.padding.right}/{vlg.padding.top}/{vlg.padding.bottom} " +
                      $"h={vlg.childControlHeight} w={vlg.childControlWidth} fh={vlg.childForceExpandHeight} fw={vlg.childForceExpandWidth} " +
                      $"align={vlg.childAlignment}]");
        var hlg = go.GetComponent<HorizontalLayoutGroup>();
        if (hlg != null) sb.Append($" [HLG spacing={hlg.spacing:F1}]");
        var le = go.GetComponent<LayoutElement>();
        if (le != null) sb.Append($" [LE min={le.minWidth:F1}x{le.minHeight:F1} pref={le.preferredWidth:F1}x{le.preferredHeight:F1} " +
                                  $"flex={le.flexibleWidth:F1}x{le.flexibleHeight:F1} ignore={le.ignoreLayout}]");
        var fit = go.GetComponent<ContentSizeFitter>();
        if (fit != null) sb.Append($" [FIT h={fit.horizontalFit} v={fit.verticalFit}]");

        try
        {
            var mbs = go.GetComponents<MonoBehaviour>();
            int shown = 0;
            for (int i = 0; i < mbs.Length && shown < 6; i++)
            {
                var mb = mbs[i];
                if (mb == null) continue;
                string tn;
                try { tn = mb.GetIl2CppType().Name; } catch { continue; }
                if (tn == "RectTransform" || tn == "CanvasRenderer") continue;
                sb.Append(' ').Append(tn).Append(mb.enabled ? "" : "(off)");
                shown++;
            }
        }
        catch { }

        var tmp = go.GetComponent<TMPro.TMP_Text>();
        if (tmp != null)
        {
            string s = "";
            try { s = (tmp.text ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); } catch { }
            if (s.Length > 40) s = s.Substring(0, 40) + "…";
            sb.Append($" text='{s}'");
        }
        sb.AppendLine();

        int n = t.childCount;
        for (int i = 0; i < n; i++) Walk(t.GetChild(i), depth + 1, sb, ref nodes, cap);
    }

    // Screen size, DPI and every live canvas with its scale factor and resolved pixel rect. The
    // rows were positioned against an assumed 640x360 while the real client was ~512x274, so this
    // is the measurement every UI placement has to be read against.
    private static void ScreenInfo(IHostContext log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  Screen={Screen.width}x{Screen.height} dpi={Screen.dpi} safeArea={Screen.safeArea.width}x{Screen.safeArea.height} " +
                      $"guiMatrixScale={GUI.matrix.m00:F3}x{GUI.matrix.m11:F3} fullScreen={Screen.fullScreen}");
        var arr = SceneCache.Scan(Il2CppType.Of<Canvas>());
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            var rt = go.transform.TryCast<RectTransform>();
            var r = rt != null ? rt.rect : new Rect();
            sb.AppendLine($"  canvas '{ClothInternals.PathOf(go.transform)}' mode={c.renderMode} scale={c.scaleFactor:F3} " +
                          $"rect={r.width:F1}x{r.height:F1} active={go.activeInHierarchy} root={(c.rootCanvas != null ? c.rootCanvas.gameObject.name : "-")} scene='{go.scene.name}'");
        }
        log.LogInfo($"[ui:screen]\n{sb}");
    }

    // Whole-screen capture through the engine's own path (ScreenCapture), which is the only one
    // that includes the screen-space UI canvases. The RT-based capture in ScreenShot.cs renders a
    // camera, so it shows the world and none of the menu: every menu proof before this was taken
    // with the engine capture for that reason.
    // Direct children of the first matching transform, each with its rect and components. The
    // pause menu is a page stack, so this is how its pages are enumerated without a 400-node dump.
    private static void Kids(IHostContext log, string val)
    {
        if (string.IsNullOrEmpty(val)) { log.LogWarning("[ui:kids] needs a path substring"); return; }
        var root = FindByPath(val);
        if (root == null) { log.LogWarning($"[ui:kids] no transform matched '{val}'"); return; }
        var sb = new StringBuilder();
        var prt = root.TryCast<RectTransform>();
        if (prt != null) sb.AppendLine($"  parent rect={prt.rect.width:F1}x{prt.rect.height:F1} children={root.childCount}");
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c == null) continue;
            var rt = c.TryCast<RectTransform>();
            string geo = rt != null ? $"rect={rt.rect.width:F1}x{rt.rect.height:F1} pos=({rt.position.x:F0},{rt.position.y:F0})" : "no-rt";
            var types = new StringBuilder();
            var mbs = c.gameObject.GetComponents<MonoBehaviour>();
            for (int m = 0; m < mbs.Length && m < 8; m++)
            {
                if (mbs[m] == null) continue;
                string tn; try { tn = mbs[m].GetIl2CppType().Name; } catch { continue; }
                if (tn == "RectTransform" || tn == "CanvasRenderer") continue;
                types.Append(tn).Append(' ');
            }
            sb.AppendLine($"  [{i}] '{c.name}' active={c.gameObject.activeSelf} {geo} types=[{types.ToString().Trim()}]");
        }
        log.LogInfo($"[ui:kids] root='{ClothInternals.PathOf(root)}'\n{sb}");
    }

    private static void Capture(IHostContext log, string tag)
    {
        if (string.IsNullOrEmpty(tag)) tag = "cap";
        foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) tag = tag.Replace(bad, '_');
        try
        {
            string path = System.IO.Path.Combine(log.ScreensDir, tag + ".png");
            System.IO.Directory.CreateDirectory(log.ScreensDir);
            ScreenCapture.CaptureScreenshot(path);
            log.LogInfo($"[ui:cap] {path} screen={Screen.width}x{Screen.height}");
        }
        catch (Exception e) { log.LogWarning($"[ui:cap] failed: {e.Message}"); }
    }

    // Every transform (active or not, under any scene canvas) whose path contains <substr>. Rects
    // are in canvas units and in screen pixels: the difference between the two is what made a row
    // land outside a window that was assumed to be 640x360.
    private static void Find(IHostContext log, string val)
    {
        if (string.IsNullOrEmpty(val)) { log.LogWarning("[ui:find] needs a path substring"); return; }
        var sb = new StringBuilder();
        int hits = 0;
        var arr = SceneCache.Scan(Il2CppType.Of<Canvas>());
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Canvas>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            CollectHits(go.transform, val, c, sb, ref hits, 40);
        }
        log.LogInfo($"[ui:find] '{val}' hits={hits}\n{sb}");
    }

    private static void CollectHits(Transform t, string substr, Canvas owner, StringBuilder sb, ref int hits, int cap)
    {
        if (t == null || hits >= cap) return;
        var go = t.gameObject;
        if (go == null) return;
        string path = ClothInternals.PathOf(t);
        if (path.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var rt = t.TryCast<RectTransform>();
            string geo = "-";
            if (rt != null)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var r = rt.rect;
                geo = $"rect={r.width:F1}x{r.height:F1} canvasXY=({rt.position.x:F0},{rt.position.y:F0}) " +
                      $"bl=({corners[0].x:F0},{corners[0].y:F0}) tr=({corners[2].x:F0},{corners[2].y:F0}) sceneXY=({rt.position.x * owner.scaleFactor:F0},{rt.position.y * owner.scaleFactor:F0})";
            }
            sb.AppendLine($"  '{path}' canvas='{ClothInternals.PathOf(owner.transform)}' scale={owner.scaleFactor:F3} " +
                          $"activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} {geo}");
            hits++;
        }
        int n = t.childCount;
        for (int i = 0; i < n; i++) CollectHits(t.GetChild(i), substr, owner, sb, ref hits, cap);
    }

    private static void PauseMenu(IHostContext log, bool open)
    {
        var arr = SceneCache.Scan(Il2CppType.Of<Pause.Pauser>());
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i].TryCast<Pause.Pauser>();
            if (p == null || p.gameObject == null || string.IsNullOrEmpty(p.gameObject.scene.name)) continue;
            try
            {
                bool was = p.IsPaused;
                if (open) p.Pause(); else p.Unpause();
                log.LogInfo($"[ui:pause] Pauser '{ClothInternals.PathOf(p.gameObject.transform)}' open={open} was={was} now={p.IsPaused}");
                return;
            }
            catch (Exception e) { log.LogWarning($"[ui:pause] {e.Message}"); }
        }
        log.LogWarning($"[ui:pause] no scene Pauser (open={open})");
    }

    // Read-only lobby state: what the game thinks is selected, what it requires to start, and
    // where the readiness trigger (the walk-in pad) actually is. Answers "why can't this lobby
    // start" without guessing at the HUD text.
    private static void Lobby(IHostContext log)
    {
        var sb = new StringBuilder();
        try
        {
            var starters = SceneCache.Scan(Il2CppType.Of<GameLobby.GameSceneStarter>());
            for (int i = 0; i < starters.Length; i++)
            {
                var st = starters[i].TryCast<GameLobby.GameSceneStarter>();
                if (st == null) continue;
                var go = st.gameObject;
                if (go == null) continue;
                var r = st._readinessController;
                string map = "-";
                try { var m = st._mapSelector != null ? st._mapSelector.SelectedGameMap : null; map = m != null ? m.name : "null"; } catch (Exception e) { map = $"<{e.Message}>"; }
                sb.AppendLine($"  starter '{ClothInternals.PathOf(go.transform)}' scene='{go.scene.name}' active={go.activeInHierarchy} map='{map}'");
                if (r != null)
                {
                    bool can = false; try { can = r.CanStartExpedition != null && r.CanStartExpedition.Value; } catch { }
                    string req = "-"; try { req = r.RequiredPlayerNumberToStartGame?.Value.ToString() ?? "null"; } catch { }
                    sb.AppendLine($"    readiness canStart={can} required={req} ready={r.NumberOfReadyPlayers} min={r.MinRequiredPlayers} " +
                                  $"max={r.MaxRequiredPlayers} minRec={r.MinRecommendedPlayers} maxRec={r.MaxRecommendedPlayers} " +
                                  $"tooMany={r.IsTooManyPlayers}");
                }
                // The countdown the game itself runs before StartTransitionToGameScene: TimeLeft and
                // the normalised value both come from the starter's own tick timer, so a value that
                // stops changing means the timer never started (which is exactly what "canStart=true
                // but nothing happens" looked like).
                string timer = "none";
                try
                {
                    var t = st.StartTimer;
                    if (t != null)
                    {
                        timer = $"normalised={t.NormalizedValue:F2}";
                        var tt = t.TryCast<Timers.TickTimer>();
                        if (tt != null) timer += $" timeLeft={tt.TimeLeft:F2} timerValue={tt.TimerValue:F2}";
                    }
                }
                catch (Exception e) { timer = $"<{e.Message}>"; }
                sb.AppendLine($"    startTimer {timer}");
                try
                {
                    var book = st._mapSelector != null ? st._mapSelector.TryCast<Gameplay.Books.MapBookNetworkMapSelector>() : null;
                    if (book != null) sb.AppendLine($"    mapBook selected={book.SelectedMapIndex.Value} effective={book.EffectiveMapIndex}");
                }
                catch (Exception e) { sb.AppendLine($"    mapBook <{e.Message}>"); }
            }
        }
        catch (Exception e) { sb.AppendLine($"  starters <{e.Message}>"); }

        try
        {
            var triggers = SceneCache.Scan(Il2CppType.Of<GameLobby.PlayersReadinessTrigger>());
            sb.AppendLine($"  readinessTriggers={triggers.Length}");
            for (int i = 0; i < triggers.Length; i++)
            {
                var t = triggers[i].TryCast<GameLobby.PlayersReadinessTrigger>();
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null) continue;
                sb.AppendLine($"    trigger '{ClothInternals.PathOf(go.transform)}' scene='{go.scene.name}' active={go.activeInHierarchy} pos={V3(go.transform.position)}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  triggers <{e.Message}>"); }

        log.LogInfo($"[ui:lobby]\n{sb}");
    }

    // Every GameMapData asset the game ships: name and its player requirements. The lobby refuses
    // a start below MinRequiredPlayers, so the solo-capable map has to be visible here.
    private static void Maps(IHostContext log)
    {
        var sb = new StringBuilder();
        int n = 0;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<GameLobby.GameMapData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<GameLobby.GameMapData>();
                if (m == null) continue;
                n++;
                int eff = -1; try { eff = m.EffectiveMinRequiredPlayers; } catch { }
                sb.AppendLine($"  [{i}] '{m.name}' min={m.MinRequiredPlayers} max={m.MaxRequiredPlayers} " +
                              $"effmin={eff} (rec {m.MinRecommendedPlayers}-{m.MaxRecommendedPlayers})");
            }
        }
        catch (Exception e) { sb.AppendLine($"  <{e.Message}>"); }
        log.LogInfo($"[ui:maps] count={n}\n{sb}");
    }

    // Lobby's own "how many players are needed" value, when the map data demands more than are
    // present (both shipped maps demand 4).
    //
    // `req=1` is a REQUEST FOR THE MOD'S ONE-PLAYER START SWITCH, not a raw write any more: the
    // switch (LobbyStartSettings) is the one place that value is applied, it re-runs the game's own
    // readiness RPCs and it keeps the value asserted when the controller recomputes it. The old raw
    // write was undone by the switch's own "off" sync 100 ms later - required went back to 4 and
    // canStart back to false, which is why the countdown never started. `req=0` turns it off again.
    //
    // On a joining client this routes into the switch and stops there: LobbyStartSettings.Set
    // persists the value and LobbyStartSettings.Apply refuses the server-owned write (see NetRole),
    // which is the fix for the `_requiredPlayerNumberToStartGame` Write-permission error a client
    // `req=1` used to produce.
    private static void RequiredPlayers(IHostContext log, string val)
    {
        int n;
        if (!int.TryParse(val, out n)) { log.LogWarning($"[ui:req] '{val}' is not a number"); return; }
        // `req=1` on, `req=0` off. It used to be `n <= 1`, which made `req=0` mean ON - the opposite
        // of what this comment and the automation that drives it say.
        bool on = n == 1;
        log.LogInfo($"[ui:req] routing req={n} through the one-player start switch (on={on}, role={NetRole.Word()})");
        LobbyStartSettings.Set(log, on);
        log.LogInfo($"[ui:req] switch now {LobbyStartSettings.Summary()}");
    }

    private static string SafeByte(Unity.Netcode.NetworkVariable<byte> v)
    {
        try { return v != null ? v.Value.ToString() : "null"; } catch (Exception e) { return $"<{e.Message}>"; }
    }

    private static string SafeBool(Unity.Netcode.NetworkVariable<bool> v)
    {
        try { return v != null ? v.Value.ToString() : "null"; } catch (Exception e) { return $"<{e.Message}>"; }
    }

    // Where the world's item pickups are, nearest first. The reel's pull needs one held item and
    // a map spawn has empty writable inventories, so this is what tells the player where to go.
    private static void Items(IHostContext log)
    {
        var player = ReelLogic.FindLocalPlayerEntity();
        Vector3 at = Vector3.zero; string pose = "none";
        Vector3 fwd = Vector3.forward;
        bool have = ReelLogic.TryGetPlayerPose(out at, out fwd, out pose);

        var found = new List<(float d, string label)>();
        try
        {
            var spawners = SceneCache.Scan(Il2CppType.Of<Network.Spawners.NetworkInventoryItemSpawner>());
            for (int i = 0; i < spawners.Length; i++)
            {
                var sp = spawners[i].TryCast<Network.Spawners.NetworkInventoryItemSpawner>();
                if (sp == null) continue;
                var go = sp.gameObject;
                if (go == null) continue;
                float d = have ? Vector3.Distance(at, go.transform.position) : -1f;
                found.Add((d, $"spawner '{ClothInternals.PathOf(go.transform)}' scene='{go.scene.name}' active={go.activeInHierarchy} pos={V3(go.transform.position)}"));
            }
        }
        catch (Exception e) { log.LogWarning($"[ui:items] spawners failed: {e.Message}"); }

        found.Sort((a, b) => a.d.CompareTo(b.d));
        var sb = new StringBuilder();
        int n = 0;
        foreach (var f in found)
        {
            sb.AppendLine($"  {f.d:F2}m {f.label}");
            if (++n >= 12) break;
        }
        log.LogInfo($"[ui:items] playerPose={pose} at={V3(at)} spawners={found.Count}\n{sb}");
    }

    // Everything the player could press E on within <metres> (default 20): the map's item pickups
    // among them. Interactable is the base of every lookable/carriable in the game, so this is the
    // honest answer to "is there anything to pick up near spawn".
    private static void Near(IHostContext log, string val)
    {
        float radius = 20f;
        float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
        if (radius <= 0f) radius = 20f;

        Vector3 at = Vector3.zero; Vector3 fwd = Vector3.forward; string pose = "none";
        bool have = ReelLogic.TryGetPlayerPose(out at, out fwd, out pose);
        var found = new List<(float d, string label)>();
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<InteractionSystem.Core.Interactable>());
            for (int i = 0; i < arr.Length; i++)
            {
                var it = arr[i].TryCast<InteractionSystem.Core.Interactable>();
                if (it == null) continue;
                var go = it.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(go.scene.name)) continue;
                float d = have ? Vector3.Distance(at, go.transform.position) : -1f;
                if (d < 0f || d > radius) continue;
                found.Add((d, $"{it.GetIl2CppType().Name} '{ClothInternals.PathOf(go.transform)}' pos={V3(go.transform.position)}"));
            }
        }
        catch (Exception e) { log.LogWarning($"[ui:near] failed: {e.Message}"); }

        found.Sort((a, b) => a.d.CompareTo(b.d));
        var sb = new StringBuilder();
        int n = 0;
        foreach (var f in found)
        {
            sb.AppendLine($"  {f.d:F2}m {f.label}");
            if (++n >= 20) break;
        }
        log.LogInfo($"[ui:near] radius={radius} playerPose={pose} at={V3(at)} interactables={found.Count}\n{sb}");
    }

    private static string V3(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    private static void SceneInfo(IHostContext log)
    {
        var loaded = new StringBuilder();
        string active = "";
        int count = 0;
        try
        {
            count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                loaded.Append($"'{s.name}'{(s.isLoaded ? "" : "(unloaded)")} ");
            }
            active = SceneManager.GetActiveScene().name ?? "";
        }
        catch (Exception e) { loaded.Append($"<{e.Message}>"); }

        bool map = ReelLogic.IsMapRun(out string scene);
        var player = ReelLogic.FindLocalPlayerEntity();
        string pose = "none"; Vector3 p = Vector3.zero; Vector3 f = Vector3.forward;
        if (ReelLogic.TryGetPlayerPose(out p, out f, out pose)) { }
        log.LogInfo($"[ui:scene] active='{active}' scenes={count} loaded=[{loaded.ToString().Trim()}] mapRun={map} " +
                    $"sceneForMapRun='{scene}'");
        log.LogInfo($"[ui:scene] player={(player != null)} pose={pose} pos=({p.x:F2},{p.y:F2},{p.z:F2}) " +
                    $"fwd=({f.x:F2},{f.y:F2},{f.z:F2})");
        if (player != null && player.gameObject != null)
            log.LogInfo($"[ui:scene] playerObject='{ClothInternals.PathOf(player.gameObject.transform)}' " +
                        $"scene='{player.gameObject.scene.name}' owner={player.IsOwner} connected={player.IsConnected}");
    }

    // Read-only inventory report: the pull needs one held Handheld, so a spawn with empty pockets
    // has to be visible rather than guessed at.
    private static void Inventory(IHostContext log)
    {
        var player = ReelLogic.FindLocalPlayerEntity();
        if (player == null) { log.LogInfo("[ui:inv] no local player"); return; }

        var sb = new StringBuilder();
        int comps = 0, items = 0;
        try
        {
            var arr = player.GetComponentsInChildren<InventorySystem.Core.InventoryComponent>(true);
            for (int c = 0; c < arr.Length; c++)
            {
                var comp = arr[c];
                if (comp == null) continue;
                comps++;
                var inv = comp.Inventory;
                if (inv == null) { sb.AppendLine($"  [{c}] '{comp.gameObject.name}' inventory=null writable={comp.IsWritableInventory}"); continue; }
                int slots = 0;
                try { slots = inv._slots != null ? inv._slots.Count : 0; } catch { }
                sb.AppendLine($"  [{c}] '{comp.gameObject.name}' writable={comp.IsWritableInventory} slots={slots}");
                for (int s = 0; s < slots; s++)
                {
                    InventorySystem.Core.InventorySlot slot = null;
                    try { slot = inv._slots[s]; } catch { }
                    if (slot == null) continue;
                    int n = 0;
                    try { n = slot.Count; } catch { }
                    if (n == 0) continue;
                    var ids = new StringBuilder();
                    for (int k = 0; k < n; k++)
                    {
                        string id = "?";
                        try
                        {
                            var it = slot.Items[k];
                            var data = it != null ? it._data : null;
                            var g = data != null ? data.GameItemData : null;
                            if (g != null) id = g.ItemDataID;
                        }
                        catch { }
                        ids.Append(id).Append(' ');
                        items++;
                    }
                    sb.AppendLine($"      slot {s} count={n} items=[{ids.ToString().Trim()}]");
                }
            }
        }
        catch (Exception e) { sb.AppendLine($"  <{e.Message}>"); }

        bool held = false;
        string heldName = "?";
        try
        {
            var hands = player.GetComponentsInChildren<HandheldSystem.Core.Hands>(true);
            for (int i = 0; i < hands.Length; i++)
            {
                if (hands[i] == null || hands[i].CurrentHandheld == null) continue;
                held = true;
                heldName = hands[i].CurrentHandheld.gameObject != null ? hands[i].CurrentHandheld.gameObject.name : "?";
                break;
            }
        }
        catch { }

        log.LogInfo($"[ui:inv] components={comps} items={items} held={held} heldObject='{heldName}'\n{sb}");
    }
}
