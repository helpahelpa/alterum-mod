using System;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.Notifications;
using Il2CppInterop.Runtime;
using TMPro;
using UI.HUD;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Everything the player is told.
//
//   prompt          the game's own world-space prompt, owned by the reel's cloned interactable
//                   (ReelInteraction). ReelInteraction verifies the prompt's localised action name
//                   and, if it does not resolve, copies one from another live prompt of the same
//                   native family - it never invents a translation key.
//   drawn prompt    ReelHudWorld's camera quads, used ONLY when that native prompt cannot display,
//                   and never at the same time (LobbyPropRuntime.UpdateInteraction picks one).
//   result / error  Gameplay.Notifications.GameNotifications.ShowNotification(...) - the game's
//                   toast stack - or the drawn camera quad when the toast draws nothing.
//   IMGUI           last resort, in exactly the rectangle ReelHudWorld computed for the quads.
//
// What this file deliberately does NOT do any more: insert a row into the shared
// ActionPromptsController/KeyActionPrompt HUD list. The old code called
// ShowOrChangePrompt(action, "Interactable", "Primary Interact") after BORROWING an action out of
// KeyActionPrompt._tableKeys (a map that carries the nine non-interact actions, microphone among
// them). The row it produced carried the borrowed action's key and binding (M / Alt observed) while
// its action label was rewritten to the reel's text and its localisation pair stayed the default
// Interactable / Primary Interact - which is not a table entry, hence the third row and the garbage
// row reading NO TRANSLATION FOUND FOR 'PRIMARY INTERACT' IN 'INTERACTABLE'. Nothing here adopts a
// game action, edits _tableKeys, or hides the game's own microphone row.
internal static class ReelHud
{
    // Our prompt text. Unique to this feature, and the one thing that identifies a reel-owned row
    // left behind by a previous generation.
    private const string PromptMarker = "Reel of Fortune";

    private static bool _init;
    private static bool _swept;
    private static IHostContext _log;

    private static GameNotifications _notes;
    private static bool _notesLooked;
    private static bool _toastWarned;

    private static string _fallbackPrompt;
    private static string _fallbackLine;
    private static bool _fallbackLineError;
    private static float _fallbackLineUntil;
    private static float _now;
    private static string _path = "not probed";

    internal static string Path => _path;

    private static void Ensure(IHostContext log)
    {
        if (log != null) _log = log;
        if (_init) return;
        _init = true;
        try
        {
            SweepLegacy(log);
            _path = "native: interactable world prompt (E); drawn quad only if that cannot display";
            log?.LogInfo($"[reel:ui] prompt path={_path}");
        }
        catch (Exception e)
        {
            _path = "native: interactable world prompt; sweep failed: " + e.Message;
            log?.LogWarning($"[reel:ui] {_path}");
        }
    }

    // ---- legacy row cleanup --------------------------------------------------------------

    // Removes a row a PREVIOUS generation left in the shared HUD prompt list, and prints the
    // evidence for it: the bound action id, its effective binding, the prompt object path and the
    // localisation pair. Identification is deliberately narrow: the row has to carry OUR prompt text
    // ("Reel of Fortune"), because that text is the one thing a reel-created row cannot share with a
    // stock one. Being bound to an action of the game's shared table means the row is one of the
    // game's own - the microphone among them - and is never enough on its own to hide it. No
    // action-keyed HidePrompt is issued at all.
    private static void SweepLegacy(IHostContext log)
    {
        if (_swept) return;
        _swept = true;
        try
        {
            CensusTable(log);
            var arr = SceneCache.Scan(Il2CppType.Of<KeyActionPrompt>());
            int rows = 0, removed = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i].TryCast<KeyActionPrompt>();
                if (p == null) continue;
                var go = p.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                rows++;
                string actionText = TextOf(p, "_actionTMP");
                string warnText = TextOf(p, "_warningTMP");
                string nameText = TextOf(p, "_nameTMP");
                var action = Field(p, "_action") ?? Field(p, "_inputAction") ?? Field(p, "_keyAction");
                string actionId = "-", binding = "-", tableKey = "-";
                if (action != null)
                {
                    var ia = action as UnityEngine.InputSystem.InputAction;
                    actionId = ia != null && ia.name != null ? ia.name : "-";
                    binding = Bindings(action);
                    tableKey = TableKeyOf(action);
                }
                string loc = Localized(Field(p, "_actionName"));
                // Only our own marker text identifies a reel-owned row. A localisation-error string
                // plus membership in the shared action table is what a STOCK row looks like before
                // it resolves - the microphone row included - so table membership never decides a
                // hide on its own.
                bool ours = Contains(actionText, PromptMarker) || Contains(nameText, PromptMarker)
                            || Contains(warnText, PromptMarker);
                if (sb.Length < 1400)
                    sb.Append($"\n  row path='{PathOf(p.transform)}' active={go.activeInHierarchy} " +
                              $"action='{actionId}' binding='{binding}' table='{tableKey}' " +
                              $"actionTMP='{Short(actionText)}' nameTMP='{Short(nameText)}' " +
                              $"warnTMP='{Short(warnText)}' loc={loc} reelOwned={ours}");
                if (!ours) continue;
                try { go.SetActive(false); removed++; }
                catch (Exception e) { log?.LogWarning($"[reel:ui] legacy row '{PathOf(p.transform)}' not hidden: {e.Message}"); }
                log?.LogInfo($"[reel:ui] legacy HUD prompt row removed path='{PathOf(p.transform)}' " +
                             $"action='{actionId}' binding='{binding}' table='{tableKey}' actionTMP='{Short(actionText)}' loc={loc}");
            }
            log?.LogInfo($"[reel:ui] HUD prompt row census rows={rows} removed={removed}{sb}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:ui] legacy prompt sweep failed: {e.Message}"); }
    }

    // Read-only census of the shared action -> localisation-key map: the evidence for which action
    // the old code could have borrowed, and with which binding and pair. Nothing is adopted here -
    // `legacyPick` only names what the deleted scorer would have chosen.
    private static void CensusTable(IHostContext log)
    {
        try
        {
            var controllers = SceneCache.Scan(Il2CppType.Of<ActionPromptsController>());
            int live = 0;
            var paths = new StringBuilder();
            for (int i = 0; i < controllers.Length && paths.Length < 400; i++)
            {
                var c = controllers[i].TryCast<ActionPromptsController>();
                if (c == null || c.gameObject == null || !c.gameObject.activeInHierarchy) continue;
                live++;
                paths.Append($"'{PathOf(c.transform)}' ");
            }
            log?.LogInfo($"[reel:ui] ActionPromptsController live={live} paths=[{paths}] (the reel borrows nothing from it)");
            var keys = KeyActionPrompt._tableKeys;
            if (keys == null) { log?.LogInfo("[reel:ui] table census: _tableKeys null"); return; }
            var sb = new StringBuilder();
            object best = null;
            string bestKey = null;
            int bestScore = -1;
            int n = 0;
            var e = keys.GetEnumerator();
            while (e.MoveNext())
            {
                var kv = e.Current;
                var a = kv.Key;
                string k = kv.Value;
                if (a == null) continue;
                n++;
                string bindings = Bindings(a);
                int score = 0;
                if (bindings.IndexOf("/e", StringComparison.OrdinalIgnoreCase) >= 0) score += 3;
                if (!string.IsNullOrEmpty(k) && k.IndexOf("interact", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
                if (score > bestScore) { bestScore = score; best = a; bestKey = k; }
                if (sb.Length < 1200)
                    sb.Append($"\n  action='{ActionName(a)}' binding='{bindings}' table='{k}'");
            }
            string pick = "-";
            if (best != null && bestScore >= 3)
                pick = $"'{ActionName(best)}' binding='{Bindings(best)}' table='{bestKey}'";
            log?.LogInfo($"[reel:ui] shared prompt table census actions={n} (read-only, nothing adopted) " +
                         $"legacyBorrowCandidate={(pick == "-" ? "none" : pick)}{sb}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:ui] table census failed: {e.Message}"); }
    }

    private static string ActionName(object action)
    {
        try
        {
            var a = action as UnityEngine.InputSystem.InputAction;
            return a != null && a.name != null ? a.name : "-";
        }
        catch { return "-"; }
    }

    private static string Bindings(object action)
    {
        try
        {
            var a = action as UnityEngine.InputSystem.InputAction;
            if (a == null) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < a.bindings.Count; i++)
            {
                string p = a.bindings[i].effectivePath;
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(p);
            }
            return sb.Length > 0 ? sb.ToString() : "-";
        }
        catch { return "-"; }
    }

    private static string TableKeyOf(object action)
    {
        try
        {
            var a = action as UnityEngine.InputSystem.InputAction;
            if (a == null) return "-";
            var keys = KeyActionPrompt._tableKeys;
            string k;
            return keys != null && keys.TryGetValue(a, out k) && !string.IsNullOrEmpty(k) ? k : "-";
        }
        catch { return "-"; }
    }

    private static string TextOf(object o, string field)
    {
        try
        {
            var t = Field(o, field) as TMPro.TMP_Text;
            return t != null && t.text != null ? t.text : "";
        }
        catch { return ""; }
    }

    private static object Field(object o, string name)
    {
        if (o == null) return null;
        try
        {
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var pi = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (pi != null && pi.GetIndexParameters().Length == 0) return pi.GetValue(o);
                var fi = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
        }
        catch { }
        return null;
    }

    // LocalisedString dump (table + entry + the resolved text), the same shape ReelInteraction logs.
    private static string Localized(object o)
    {
        if (o == null) return "null";
        try
        {
            string text = null;
            try { text = o.GetType().GetMethod("GetLocalizedString", Type.EmptyTypes)?.Invoke(o, null) as string; } catch { }
            var sb = new StringBuilder();
            foreach (var field in new[] { "m_TableReference", "m_TableEntryReference" })
            {
                var f = o.GetType().GetField(field, System.Reflection.BindingFlags.Public |
                                                    System.Reflection.BindingFlags.NonPublic |
                                                    System.Reflection.BindingFlags.Instance);
                if (f == null) { sb.Append(field).Append("=? "); continue; }
                var v = f.GetValue(o);
                sb.Append(field).Append('{');
                if (v != null)
                    foreach (var inner in v.GetType().GetFields(System.Reflection.BindingFlags.Public |
                                                                System.Reflection.BindingFlags.NonPublic |
                                                                System.Reflection.BindingFlags.Instance))
                        try { sb.Append(inner.Name).Append('=').Append(inner.GetValue(v)).Append(' '); } catch { }
                sb.Append("} ");
            }
            if (!string.IsNullOrEmpty(text)) sb.Append("resolved='").Append(Short(text)).Append('\'');
            return sb.ToString();
        }
        catch (Exception e) { return "err:" + e.Message; }
    }

    private static bool Contains(string s, string token) =>
        !string.IsNullOrEmpty(s) && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Short(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 70 ? s : s.Substring(0, 70) + "...");

    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "?";
            string s = t.name;
            int guard = 0;
            for (var p = t.parent; p != null && guard++ < 6; p = p.parent) s = p.name + "/" + s;
            return s;
        }
        catch { return "?"; }
    }

    // ---- interaction prompt ------------------------------------------------------------

    // The drawn prompt (camera quad, with the IMGUI plate as its own last resort). True only when
    // the quad actually drew; the caller uses that to decide whether it still needs a prompt at
    // all, and it never runs this while the native world prompt is live.
    internal static bool Prompt(IHostContext log, string text)
    {
        Ensure(log);
        _fallbackPrompt = text;
        bool drawn = ReelHudWorld.Prompt(log, text);
        if (!drawn)
            log?.LogWarning($"[reel:ui] prompt not drawn: {ReelHudWorld.Status} text='{text}'");
        return drawn;
    }

    internal static void HidePrompt(IHostContext log)
    {
        _fallbackPrompt = null;
        ReelHudWorld.HidePrompt();
    }

    // ---- messages ---------------------------------------------------------------------

    private static void Notify(IHostContext log, string text, float seconds, bool error)
    {
        _fallbackLine = text;
        _fallbackLineError = error;
        // Unscaled, not the booth's clock: the IMGUI line must expire even when no booth is ticking.
        _fallbackLineUntil = Time.unscaledTime + seconds;

        bool toasted = false;
        try
        {
            EnsureNotes(log);
            toasted = TryGameToast(log, text, seconds);
        }
        catch (Exception e) { log?.LogWarning($"[reel:ui] notify tier=toast failed: {e.Message}"); }

        // Tier 2 is the normal drawn path. It reports whether the pixels actually landed on the
        // camera that is rendering; a "shown" over a quad that died with the previous scene is the
        // regression this replaces, so the answer is checked instead of assumed.
        if (!toasted && ReelHudWorld.Message(log, text, seconds, error)) return;

        // Tier 3: last resort. OnGUI draws _fallbackLine because ReelHudWorld.Available is false.
        log?.LogWarning($"[reel:ui] notify tier=imgui message='{text}' reason='{ReelHudWorld.Status}'");
    }

    // The game's own toast stack, used only when it genuinely renders. A toast that silently draws
    // nothing is indistinguishable from a working one by its return value, so the stack is checked
    // for a NEW object after the call. Observed here (both entry points) is an NRE inside the game:
    // Gameplay.Notifications.GameNotifications.ShowNotification with a live instance, which is the
    // reason the drawn quad below is the normal path.
    private static bool TryGameToast(IHostContext log, string text, float seconds)
    {
        var data = NewData(seconds);
        int before = Descendants(_notes);
        try
        {
            GameNotifications.ShowNotificationStatic(text, data);
            int after = Descendants(_notes);
            if (after > before) { log?.LogInfo($"[reel:ui] notify tier=toast static ok=True objects={before}->{after}"); return true; }
            log?.LogInfo($"[reel:ui] notify tier=toast static ok=False no new object ({before}->{after})");
        }
        catch (Exception e) { log?.LogInfo($"[reel:ui] notify tier=toast static threw: {e.GetType().Name}: {e.Message}"); }

        try
        {
            _notes.ShowNotification(text, NewData(seconds));
            int after = Descendants(_notes);
            if (after > before) { log?.LogInfo($"[reel:ui] notify tier=toast instance ok=True objects={before}->{after}"); return true; }
            log?.LogInfo($"[reel:ui] notify tier=toast instance ok=False no new object ({before}->{after})");
        }
        catch (Exception e)
        {
            if (!_toastWarned)
            {
                _toastWarned = true;
                log?.LogWarning($"[reel:ui] notify tier=toast instance threw (first time): {e.GetType().Name}: {e.Message}");
            }
            else log?.LogInfo($"[reel:ui] notify tier=toast instance threw: {e.GetType().Name}: {e.Message}");
        }
        return false;
    }

    private static GameNotificationData NewData(float seconds) => new GameNotificationData
    {
        Duration = seconds,
        Sprite = null,
        SpriteColor = Color.white,
        SoundEventReference = default(FMODUnity.EventReference),
    };

    private static int Descendants(GameNotifications n)
    {
        try { return n == null ? 0 : n.GetComponentsInChildren<Transform>(true).Length; } catch { return 0; }
    }

    private static void EnsureNotes(IHostContext log)
    {
        if (_notesLooked && _notes != null) return;
        _notesLooked = true;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<GameNotifications>());
            for (int i = 0; i < arr.Length; i++)
            {
                var n = arr[i].TryCast<GameNotifications>();
                if (n == null) continue;
                if (n.gameObject != null && n.gameObject.activeInHierarchy) { _notes = n; break; }
                if (_notes == null) _notes = n;
            }
        }
        catch (Exception e) { log?.LogInfo($"[reel:ui] notification scan failed: {e.Message}"); return; }
        if (_notes == null) return;
        string tail = "notifications=GameNotifications";
        if (_path.IndexOf(tail, StringComparison.Ordinal) < 0) _path += "; " + tail;
        log?.LogInfo($"[reel:ui] notifications path=GameNotifications '{PathOf(_notes.transform)}'");
    }

    internal static void Result(IHostContext log, string text)
    {
        log.LogInfo($"[reel] result text='{text}'");
        Notify(log, text, 3.5f, false);
    }

    internal static void Error(IHostContext log, string text)
    {
        log.LogInfo($"[reel] message text='{text}' kind=error");
        Notify(log, text, 3.0f, true);
    }

    // ---- drawn fallback ----------------------------------------------------------------

    internal static void Tick(float dt)
    {
        _now += dt;
        ReelHudWorld.Tick(dt);
    }

    // The sweep must also run for a generation that never opens a booth: OnGUI is the one hook the
    // host calls every frame, so a leftover row from the previous generation is removed even in a
    // lobby where no reel is ticking.
    internal static void SweepOnce() { if (!_swept && _log != null) Ensure(_log); }

    internal static void OnGUI()
    {
        // Last resort only: the camera quads are the normal drawn path (they land in captures and
        // need no window composition on a locked desktop). IMGUI is used only if those failed.
        try
        {
            SweepOnce();
            if (ReelHudWorld.Available) return;
            if (!string.IsNullOrEmpty(_fallbackLine) && Time.unscaledTime < _fallbackLineUntil)
                DrawPanel(_fallbackLine, _fallbackLineError);
            if (!string.IsNullOrEmpty(_fallbackPrompt))
                DrawPrompt(_fallbackPrompt);
        }
        catch { }
    }

    // Same rectangle the camera quad occupies, from ReelHudWorld's layout: a prompt above the
    // native hotbar (or above the reserved bottom 96 reference pixels), messages 8 ref px above it.
    private static void DrawPrompt(string text)
    {
        var r = ReelHudWorld.ScreenRect(true, text);
        Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0.02f, 0.02f, 0.03f, 0.75f));
        Fill(r, new Color(0.06f, 0.05f, 0.07f, 0.92f));
        Fill(new Rect(r.x, r.y, r.width, 1f), new Color(0.77f, 0.66f, 0.36f, 1f));
        Fill(new Rect(r.x, r.y + r.height - 1f, r.width, 1f), new Color(0.77f, 0.66f, 0.36f, 1f));
        var key = new Rect(r.x + 10f, r.y + (r.height - 20f) * 0.5f, 24f, 20f);
        Fill(key, new Color(0.95f, 0.90f, 0.75f, 0.95f));
        GUI.Label(key, "E", Style(14, TextAnchor.MiddleCenter, new Color(0.1f, 0.06f, 0.05f)));
        GUI.Label(new Rect(r.x + 40f, r.y, r.width - 48f, r.height), "  " + text,
                  Style(15, TextAnchor.MiddleLeft, new Color(0.96f, 0.93f, 0.82f)));
    }

    private static void DrawPanel(string text, bool error)
    {
        var r = ReelHudWorld.ScreenRect(false, text);
        Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0.02f, 0.02f, 0.03f, 0.7f));
        Fill(r, new Color(0.06f, 0.05f, 0.07f, 0.9f));
        // Same border colours as the camera quad: gold for a result, red for a refusal/error.
        Fill(new Rect(r.x, r.y, r.width, 1f), error ? new Color(0.91f, 0.36f, 0.28f, 1f) : new Color(0.77f, 0.66f, 0.36f, 1f));
        Fill(new Rect(r.x, r.y + r.height - 1f, r.width, 1f), error ? new Color(0.91f, 0.36f, 0.28f, 1f) : new Color(0.77f, 0.66f, 0.36f, 1f));
        GUI.Label(new Rect(r.x + 12f, r.y, r.width - 20f, r.height), text,
                  Style(14, TextAnchor.MiddleCenter, new Color(0.96f, 0.93f, 0.82f)));
    }

    private static GUIStyle Style(int size, TextAnchor anchor, Color color)
    {
        // Cheap per-call style clone so size/colour changes do not leak into other mod labels.
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            alignment = anchor,
            wordWrap = false,
        };
        s.normal.textColor = color;
        return s;
    }

    private static void Fill(Rect r, Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }
}
