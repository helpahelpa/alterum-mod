using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AlterumModelSwap.Contracts;
using HWG.Settings.Controls;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Which input context an action belongs to. A shortcut is only dispatched in its own context.
// The overlays no longer need a context of their own: every key they read that has to be
// rebindable is a World row (U / F12 open them), and everything else they do - Esc close, mouse
// select, wheel paging, the action buttons - is hardcoded inside PickerUI. The enum keeps the
// Picker member so a future rebindable overlay row still has a slot instead of a second table.
public enum ModContext
{
    World = 0,      // normal play; the ESC menu / chat / a rebind dialog gate the whole context
    Picker = 1,     // one of the two overlays is open
    Menu = 2,       // reserved: keys that act while the pause menu is up (none today)
}

// A binding is "key + held modifiers", which is what the mod actually polls. `Path` is the
// canonical Input System path stored on disk and handed to the native row's InputAction, so the
// store, the row and the legend all speak the same string.
internal readonly struct ModBinding
{
    internal readonly Key Key;
    internal readonly bool Shift;
    internal readonly bool Ctrl;
    internal readonly bool Alt;
    internal readonly string Path;       // null = unresolvable (kept so the row can show why)

    internal ModBinding(Key key, bool shift, bool ctrl, bool alt, string path)
    {
        Key = key; Shift = shift; Ctrl = ctrl; Alt = alt; Path = path;
    }

    internal bool Valid => Path != null;
    internal bool Plain => !Shift && !Ctrl && !Alt;
    internal bool SameAs(ModBinding other) => Path == other.Path;
}

internal sealed class ModAction
{
    internal string Id;
    internal string Label;
    internal ModContext Context;

    // Empty only for menu-only placeholder rows; every real shortcut has a default.
    internal ModBinding Default;
    internal ModBinding Current;

    internal bool Overridden => Current.Valid && !Current.SameAs(Default);
    internal string DefaultText => ModBindings.KeyText(Default);
    internal string CurrentText => ModBindings.KeyText(Current);
    internal string Trigger => $"{Id}({CurrentText})";
}

// The single registry of mod shortcuts.
//
// Every *rebindable* shortcut the mod owns is declared once here - action id, label, context,
// default binding - and every consumer reads it from here: the key poll in each feature, the
// native Settings -> Controls rows (ModControlsUI), the ESC menu section (ModPauseSettings), the
// on-screen legend (Legend()) and the startup [keys] log line. There is no second table anywhere,
// so a legend line cannot drift from the key that is actually polled.
//
// Exactly four rows survive the controls prune: the two picker open keys, the POV toggle and the
// game console. Every other key the mod used to own is gone - the bodies, garments, the emote
// toggle/cycle, the screenshot/clip triggers and the pickers' own navigation rows. Those paths are
// request files (`sahur.request`, `sahurrig.request`, `clothes.request`, `emote.request stop`,
// `shot.request`, `clip.request`) and the mouse-driven overlay. A removed id cannot be re-bound,
// and a stale override for one is dropped on load with a log line (see Attach) instead of being
// re-applied to whatever now sits at that position.
//
// Overrides are persisted by id (ModBindingsStore), never by label or by index, so a renamed or
// reordered action keeps its user binding and a stale override for a removed action is dropped
// with a log line instead of being applied to whatever now sits at that index.
//
// Binding text ("Shift+F5") is derived from the same Binding struct that is polled, so what the
// Controls row shows, what the legend prints and what the check below reads are one value.
internal static class ModBindings
{
    // Every id, in the order the Controls list and the legend present them. Exactly four rows:
    // the two overlay open keys, the POV toggle and the game console. Anything else the mod does
    // on a key is either the game's own binding (Interact) or a request file.
    //
    // The controls prune deleted F1/F2/F3 (Sahur bodies), F5 / Shift+F5 (garments), F6/F9 (emote
    // toggle/cycle), F10 (screenshot), F11 (clip) and every picker navigation/close row (Esc,
    // PgUp/PgDn, Enter/Space, arrows, digits). Their functions are not lost:
    //   bodies      -> sahur.request / sahurrig.request
    //   garments    -> clothes.request
    //   emotes      -> the emote picker (U) + emote.request stop
    //   screenshot  -> shot.request
    //   clip        -> clip.request
    //   picker nav  -> mouse click-select, wheel paging, Esc (hardcoded in PickerUI)
    private static readonly ModAction[] Table =
    {
        Make("mod.pov.toggle",         "Third-person view",       ModContext.World,  Key.F7),
        Make("mod.picker.emote",       "Emote picker",            ModContext.World,  Key.U),
        Make("mod.picker.cosmetic",    "Cosmetic picker",         ModContext.World,  Key.F12),
        // Tilde belongs to the GAME's console (QFSW/Quantum Console), which the mod enables and
        // toggles through its own Activate()/Deactivate() (GameConsole). Backslash and shift+?
        // are free again now that the mod-side IMGUI console and error overlay are gone.
        Make("mod.console.toggle",     "Game console (game commands)", ModContext.World, Key.Backquote),
    };

    private static readonly Dictionary<string, ModAction> ById = new Dictionary<string, ModAction>(StringComparer.Ordinal);

    internal const string MapName = "AlterumMod";

    private static IHostContext _log;
    private static bool _ready;
    private static bool _nativeProbed;
    private static string _nativeInteract = "";

    // Gate state, refreshed at most 4x a second (the checks walk a few scene components).
    private static float _nextGate;
    private static bool _blocked;
    private static string _blockWhy = "";

    internal static bool Ready => _ready;

    internal static IReadOnlyList<ModAction> Actions => Table;

    private static ModAction Make(string id, string label, ModContext ctx, Key key, bool shift = false,
                                   bool ctrl = false, bool alt = false)
        => new ModAction
        {
            Id = id,
            Label = label,
            Context = ctx,
            Default = new ModBinding(key, shift, ctrl, alt, PathOf(key, shift, ctrl, alt)),
        };

    // ---- lifecycle ----------------------------------------------------------------------

    // Idempotent. Called from the first tick of whatever owned feature runs first; the host is
    // kept only for logging/paths (no Unity object is touched here).
    internal static void Attach(IHostContext log)
    {
        if (_ready) { if (_log == null) _log = log; return; }
        _ready = true;
        _log = log;
        ById.Clear();
        for (int i = 0; i < Table.Length; i++)
        {
            ById[Table[i].Id] = Table[i];
            Table[i].Current = Table[i].Default;
        }

        int applied = ModBindingsStore.Load(log, out var overrides);
        int used = 0, stale = 0;
        var dropped = new List<string>();
        foreach (var kv in overrides)
        {
            if (!ById.TryGetValue(kv.Key, out var action))
            {
                // A stale override is dropped BY ID and never re-assigned to a sibling row by
                // position: the store is id-keyed on purpose. The id is named so the evidence line
                // shows exactly which removed action the file still carries; the file itself is
                // rewritten (and the entry gone) on the next normal save.
                stale++;
                dropped.Add(kv.Key);
                continue;
            }
            var parsed = Parse(kv.Value);
            if (!parsed.Valid) { stale++; dropped.Add(kv.Key + "(bad-path)"); continue; }
            action.Current = parsed;
            used++;
        }
        log?.LogInfo($"[bind] {Table.Length} actions registered (world={CountOf(ModContext.World)} " +
                     $"picker={CountOf(ModContext.Picker)}); overrides loaded={used} applied={applied} " +
                     $"dropped={stale} store='{ModBindingsStore.PathFor(log)}'");
        if (stale > 0)
            log?.LogInfo($"[bind] stale overrides ignored (removed/unknown ids, never re-applied by position): " +
                         $"{string.Join(",", dropped)} - the next normal store save rewrites the file without them");
        log?.LogInfo($"[keys] registry: {Legend()}");
    }

    internal static void Tick(IHostContext log, float dt)
    {
        Attach(log);
        ModControlsUI.Tick(log);
        ModPauseSettings.Tick(log);
        MenuProof.Tick(log);
    }

    private static int CountOf(ModContext ctx)
    {
        int n = 0;
        for (int i = 0; i < Table.Length; i++) if (Table[i].Context == ctx) n++;
        return n;
    }

    internal static ModAction Get(string id)
    {
        if (id == null) return null;
        // Before Attach runs (first frame of a generation, before any owned Tick), the table is
        // still the fixed declaration order, so lookups resolve to the *defaults* instead of null.
        // A press in that one frame therefore behaves exactly as if the store were empty.
        if (ById.Count == 0 && id.Length > 0)
        {
            for (int i = 0; i < Table.Length; i++)
                if (Table[i].Id == id) return Table[i];
            return null;
        }
        return ById.TryGetValue(id, out var a) ? a : null;
    }

    // ---- dispatch -----------------------------------------------------------------------

    // The one read every feature uses. False while a menu/chat/rebind is up: the gate is part of
    // the registry so no feature can forget it and no shortcut can fire "through" a dialog.
    internal static bool Pressed(string id) => Pressed(id, out _);

    internal static bool Pressed(string id, out string trigger)
    {
        trigger = null;
        var a = Get(id);
        if (a == null || !a.Current.Valid) return false;
        if (Blocked(out _)) return false;
        if (!Down(a.Current)) return false;
        trigger = a.Trigger;
        return true;
    }

    internal static bool Held(string id)
    {
        var a = Get(id);
        if (a == null || !a.Current.Valid) return false;
        if (Blocked(out _)) return false;
        return State(a.Current);
    }

    // The console's own toggle must still work while the console is capturing input (it is what
    // closes it), so this read skips the gate. Only the console may use it.
    internal static bool PressedUngated(string id, out string trigger)
    {
        trigger = null;
        var a = Get(id);
        if (a == null || !a.Current.Valid) return false;
        if (!Down(a.Current)) return false;
        trigger = a.Trigger;
        return true;
    }

    // Pickers consume their own keys even when the world gate would block (the picker *is* the
    // menu), so they ask for the context they are in.
    internal static bool PressedIn(ModContext ctx, string id) => PressedIn(ctx, id, out _);

    internal static bool PressedIn(ModContext ctx, string id, out string trigger)
    {
        trigger = null;
        var a = Get(id);
        if (a == null || a.Context != ctx || !a.Current.Valid) return false;
        if (Blocked(out _)) return false;
        if (!Down(a.Current)) return false;
        trigger = a.Trigger;
        return true;
    }

    internal static bool HeldIn(ModContext ctx, string id)
    {
        var a = Get(id);
        if (a == null || a.Context != ctx || !a.Current.Valid) return false;
        if (Blocked(out _)) return false;
        return State(a.Current);
    }

    private static Keyboard Kb => Keyboard.current;

    private static bool Mod(ModBinding b, bool shift, bool ctrl, bool alt) => b.Shift == shift && b.Ctrl == ctrl && b.Alt == alt;

    private static bool Down(ModBinding b)
    {
        var kb = Kb;
        if (kb == null) return false;
        try
        {
            var control = kb[b.Key];
            if (control == null || !control.wasPressedThisFrame) return false;
            return ModifiersMatch(b, kb);
        }
        catch { return false; }
    }

    private static bool State(ModBinding b)
    {
        var kb = Kb;
        if (kb == null) return false;
        try
        {
            var control = kb[b.Key];
            if (control == null || !control.isPressed) return false;
            return ModifiersMatch(b, kb);
        }
        catch { return false; }
    }

    // Held modifiers are a keyboard-wide property: every polled action reads the same six controls
    // (left/right x shift/ctrl/alt). Cached per engine frame, so 35 shortcut polls cost 35 key reads
    // instead of 35 x 7 - nothing can change inside one frame, and the cache is keyed on the frame
    // number, so it can never outlive the frame it was taken in.
    private static int _modFrame = int.MinValue;
    private static bool _modShift, _modCtrl, _modAlt;

    private static bool ModifiersMatch(ModBinding b, Keyboard kb)
    {
        int frame = int.MinValue;
        try { frame = Time.frameCount; } catch { }
        if (frame == int.MinValue || frame != _modFrame)
        {
            _modFrame = frame;
            _modShift = Held(kb, Key.LeftShift) || Held(kb, Key.RightShift);
            _modCtrl = Held(kb, Key.LeftCtrl) || Held(kb, Key.RightCtrl);
            _modAlt = Held(kb, Key.LeftAlt) || Held(kb, Key.RightAlt);
        }
        return Mod(b, _modShift, _modCtrl, _modAlt);
    }

    private static bool Held(Keyboard kb, Key key)
    {
        try { var c = kb[key]; return c != null && c.isPressed; }
        catch { return false; }
    }

    // ---- gate ---------------------------------------------------------------------------

    // Menus, chat and an in-flight rebind must swallow the shortcuts. Cached for 0.25 s: each
    // check is a scene component lookup, not a per-key cost.
    internal static bool Blocked(out string why)
    {
        if (Time.unscaledTime < _nextGate) { why = _blockWhy; return _blocked; }
        _nextGate = Time.unscaledTime + 0.25f;
        _blocked = false;
        _blockWhy = "";

        try { if (RebindInFlight()) { _blocked = true; _blockWhy = "rebind"; } } catch { }
        if (!_blocked) { try { if (Paused()) { _blocked = true; _blockWhy = "menu"; } } catch { } }
        if (!_blocked) { try { if (TextInputFocused()) { _blocked = true; _blockWhy = "chat"; } } catch { } }
        // The game console is a UI that owns the gate itself, so it reports here: while it is
        // open no world or picker shortcut may fire (Tilde is read ungated so it can always close
        // what it opened). Nothing here touches gameplay input.
        if (!_blocked && GameConsole.CapturesInput) { _blocked = true; _blockWhy = "gameconsole"; }

        why = _blockWhy;
        return _blocked;
    }

    private static bool RebindInFlight()
    {
        // Generation-scoped, not polled: a row that is mid-rebind lives in the pause menu's Controls
        // page, whose objects are stable while it is open (MenuScope drops the group on each edge).
        var arr = SceneCache.Scan(Il2CppType.Of<RebindActionUI>());
        for (int i = 0; i < arr.Length; i++)
        {
            var r = arr[i].TryCast<RebindActionUI>();
            if (r == null) continue;
            var go = r.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            try { if (r.ongoingRebind != null) return true; }
            catch { }
        }
        return false;
    }

    private static bool Paused()
    {
        // The held Pauser and its own state event, not a scene scan (see MenuScope).
        try { return MenuScope.IsOpen; }
        catch { return false; }
    }

    // Any focused input field (text chat, a rename box, a rebind filter) owns the keyboard.
    private static bool TextInputFocused()
    {
        // Generation-scoped: the fields are part of the scene's HUD and are not recreated while it
        // runs. A panel that builds one late is covered by MenuScope's edge invalidation.
        var arr = SceneCache.Scan(Il2CppType.Of<TMPro.TMP_InputField>());
        for (int i = 0; i < arr.Length; i++)
        {
            var f = arr[i].TryCast<TMPro.TMP_InputField>();
            if (f == null) continue;
            var go = f.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            try { if (f.isFocused) return true; }
            catch { }
        }
        return false;
    }

    // ---- persistence / overrides --------------------------------------------------------

    internal static bool SetOverride(string id, string path, string source)
    {
        var a = Get(id);
        if (a == null) return false;
        var b = Parse(path);
        if (!b.Valid) { _log?.LogWarning($"[bind] {source}: id={id} path='{path}' is not a keyboard chord"); return false; }
        if (b.SameAs(a.Current)) return true;
        var old = a.Current;
        a.Current = b;
        ModBindingsStore.Save(_log, Table);
        _log?.LogInfo($"[bind] override id={id} '{KeyText(old)}' -> '{KeyText(b)}' source={source}");
        return true;
    }

    internal static bool ResetOverride(string id, string source)
    {
        var a = Get(id);
        if (a == null) return false;
        if (!a.Overridden) { ModBindingsStore.Save(_log, Table); return true; }
        var old = a.Current;
        a.Current = a.Default;
        ModBindingsStore.Save(_log, Table);
        _log?.LogInfo($"[bind] reset id={id} '{KeyText(old)}' -> '{KeyText(a.Current)}' source={source}");
        return true;
    }

    internal static void ResetAll(string source)
    {
        int n = 0;
        for (int i = 0; i < Table.Length; i++)
        {
            if (!Table[i].Overridden) continue;
            Table[i].Current = Table[i].Default;
            n++;
        }
        ModBindingsStore.Save(_log, Table);
        _log?.LogInfo($"[bind] reset all ({n} overridden) source={source}");
    }

    // A native row changed its InputAction, or reported one at build time. Path is the effective
    // binding path the input system will use, so the poll and the row cannot disagree.
    internal static void SetFromNative(string id, string path)
    {
        var a = Get(id);
        if (a == null) return;
        var b = Parse(path);
        if (!b.Valid) { _log?.LogInfo($"[bind] native id={id} path='{path}' is not pollable as a key (stored only)"); return; }
        if (b.SameAs(a.Current)) return;
        var old = a.Current;
        a.Current = b;
        ModBindingsStore.Save(_log, Table);
        _log?.LogInfo($"[bind] native rebind id={id} '{KeyText(old)}' -> '{KeyText(b)}' path='{path}'");
    }

    // ---- text ---------------------------------------------------------------------------

    // "F5", "Shift+F5", "PgUp", "1". Derived from the binding, never from a label.
    internal static string KeyText(ModBinding b)
    {
        if (!b.Valid) return "<none>";
        var sb = new StringBuilder();
        if (b.Ctrl) sb.Append("Ctrl+");
        if (b.Alt) sb.Append("Alt+");
        if (b.Shift) sb.Append("Shift+");
        sb.Append(KeyName(b.Key));
        return sb.ToString();
    }

    internal static string KeyName(Key key)
    {
        switch (key)
        {
            case Key.PageUp: return "PgUp";
            case Key.PageDown: return "PgDn";
            case Key.Escape: return "Esc";
            case Key.Enter: return "Enter";
            case Key.Space: return "Space";
            case Key.LeftArrow: return "Left";
            case Key.RightArrow: return "Right";
            case Key.UpArrow: return "Up";
            case Key.DownArrow: return "Down";
            case Key.LeftShift: return "Shift";
            case Key.Digit0: return "0";
        }
        string n = key.ToString();
        if (n.StartsWith("Digit", StringComparison.Ordinal) && n.Length == 6) return n.Substring(5);
        return n;
    }

    // The on-screen legend and the [keys] log line, generated from the table so it cannot drift.
    // A context with no rows is omitted rather than printed as an empty section: after the controls
    // prune there is no Picker row left (Esc and the mouse are hardcoded in PickerUI).
    //
    // The reel interaction is *not* an action here: E belongs to the game's Interact binding and
    // is shown from the game's own action asset (NativeInteractText) instead of a second E action.
    internal static string Legend()
    {
        var sb = new StringBuilder();
        sb.Append("Alterum mod");
        for (int i = 0; i < Table.Length; i++)
        {
            var a = Table[i];
            if (a.Context != ModContext.World) continue;
            sb.Append(" | ").Append(a.CurrentText).Append(' ').Append(ShortLabel(a));
        }
        sb.Append(" | ").Append(NativeInteractText()).Append(" reel interact");
        sb.Append("\nPicker");
        int picker = 0;
        for (int i = 0; i < Table.Length; i++)
        {
            var a = Table[i];
            if (a.Context != ModContext.Picker) continue;
            sb.Append(" | ").Append(a.CurrentText).Append(' ').Append(ShortLabel(a));
            picker++;
        }
        sb.Append(picker == 0 ? " | Esc close, click select, wheel page (hardcoded)" : "");
        return sb.ToString();
    }

    private static string ShortLabel(ModAction a)
    {
        string s = a.Label;
        if (s.StartsWith("Picker: ", StringComparison.Ordinal)) s = s.Substring(8);
        return s;
    }

    // One line per action: id, context, label, default, effective, override flag, stored path.
    internal static string Inventory()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Table.Length; i++)
        {
            var a = Table[i];
            sb.AppendLine($"  id={a.Id,-24} ctx={a.Context,-6} default={a.DefaultText,-9} " +
                          $"effective={a.CurrentText,-9} override={(a.Overridden ? "yes" : "no"),-3} " +
                          $"path='{a.Current.Path ?? "-"}' label='{a.Label}'");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Summary()
        => $"actions={Table.Length} overrides={CountOverrides()} blocked={(_blocked ? _blockWhy : "no")} " +
           $"nativeInteract='{NativeInteractText()}'";

    private static int CountOverrides()
    {
        int n = 0;
        for (int i = 0; i < Table.Length; i++) if (Table[i].Overridden) n++;
        return n;
    }

    // ---- native bindings -----------------------------------------------------------------

    // The game's own Interact key, read from its loaded InputActionAsset. Used for the reel legend
    // so the mod never claims a second E action (stream B: the reel consumes this native action).
    internal static string NativeInteractText()
    {
        if (_nativeProbed) return _nativeInteract;
        _nativeProbed = true;
        _nativeInteract = "E";
        try
        {
            var arr = SceneCache.ScanPolled(Il2CppType.Of<InputActionAsset>(), 1.0);
            for (int i = 0; i < arr.Length; i++)
            {
                var asset = arr[i].TryCast<InputActionAsset>();
                if (asset == null) continue;
                var action = asset.FindAction("Interact", false) ?? asset.FindAction("Interactable", false);
                if (action == null)
                {
                    var map = asset.FindActionMap("InteractionControls", false);
                    if (map != null)
                    {
                        var m = map.actions;
                        for (int k = 0; m != null && k < m.Count; k++)
                        {
                            var an = m[k].name;
                            if (an != null && an.IndexOf("nteract", StringComparison.Ordinal) >= 0) { action = m[k]; break; }
                        }
                    }
                }
                if (action == null) continue;
                string text = BindingLabel(action, 0);
                if (text.Length == 0) continue;
                _nativeInteract = text;
                _log?.LogInfo($"[bind] native Interact resolved asset='{asset.name}' action='{action.name}' key='{text}'");
                break;
            }
        }
        catch (Exception e) { _log?.LogWarning($"[bind] native Interact lookup failed: {e.Message}"); }
        return _nativeInteract;
    }

    internal static string BindingLabel(InputAction action, int index)
    {
        try
        {
            if (action == null || index < 0 || index >= action.bindings.Count) return "";
            string s = action.GetBindingDisplayString(index);
            if (string.IsNullOrEmpty(s)) s = action.bindings[index].effectivePath;
            return string.IsNullOrEmpty(s) ? "" : KeyText(Parse(s));
        }
        catch { return ""; }
    }

    // ---- path <-> binding ----------------------------------------------------------------

    // Canonical: "shift+<Keyboard>/f5". Accepted on load: with or without the "Keyboard/" prefix,
    // "F5"/"f5", and the game's own display strings ("Shift+F5") that a native row reports.
    internal static ModBinding Parse(string path)
    {
        if (string.IsNullOrEmpty(path)) return default;
        string s = path.Trim();
        bool shift = false, ctrl = false, alt = false;
        while (true)
        {
            if (s.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase)) { shift = true; s = s.Substring(6); }
            else if (s.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase)) { ctrl = true; s = s.Substring(5); }
            else if (s.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase)) { alt = true; s = s.Substring(4); }
            else break;
        }
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        if (s.Length == 0) return default;
        if (!s.StartsWith("<", StringComparison.Ordinal)) { /* "<Keyboard>/f5" already stripped */ }
        string name = s.Length == 1 ? s.ToUpperInvariant() : char.ToUpperInvariant(s[0]) + s.Substring(1);
        Key key;
        try
        {
            if (!Enum.TryParse(name, true, out key)) return default;
        }
        catch { return default; }
        return new ModBinding(key, shift, ctrl, alt, PathOf(key, shift, ctrl, alt));
    }

    internal static string PathOf(Key key, bool shift, bool ctrl, bool alt)
    {
        string control = ControlName(key);
        if (control == null) return null;
        var sb = new StringBuilder();
        if (ctrl) sb.Append("ctrl+");
        if (alt) sb.Append("alt+");
        if (shift) sb.Append("shift+");
        sb.Append("<Keyboard>/").Append(control);
        return sb.ToString();
    }

    private static string ControlName(Key key)
    {
        string n = key.ToString();
        switch (key)
        {
            case Key.None: return null;
            case Key.Digit0: case Key.Digit1: case Key.Digit2: case Key.Digit3: case Key.Digit4:
            case Key.Digit5: case Key.Digit6: case Key.Digit7: case Key.Digit8: case Key.Digit9:
                return "digit" + n.Substring(5);
            case Key.LeftShift: return "leftShift";
            case Key.LeftCtrl: return "leftCtrl";
            case Key.LeftAlt: return "leftAlt";
        }
        if (n.Length == 1) return n.ToLowerInvariant();
        return char.ToLowerInvariant(n[0]) + n.Substring(1);
    }

    // The mod's own ids that collide with a key the game already binds, for the [bind] startup
    // census. Native collisions are reported, not silently "fixed": only the user's own remap does.
    internal static string Conflicts(string id)
    {
        var a = Get(id);
        if (a == null || !a.Current.Valid) return "";
        var hits = new List<string>();
        try
        {
            var arr = SceneCache.ScanPolled(Il2CppType.Of<InputActionAsset>(), 1.0);
            for (int i = 0; i < arr.Length; i++)
            {
                var asset = arr[i].TryCast<InputActionAsset>();
                if (asset == null) continue;
                var maps = asset.actionMaps;
                for (int m = 0; maps != null && m < maps.Count; m++)
                {
                    var actions = maps[m].actions;
                    for (int k = 0; actions != null && k < actions.Count; k++)
                    {
                        var action = actions[k];
                        var bs = action.bindings;
                        for (int b = 0; bs != null && b < bs.Count; b++)
                        {
                            var path = bs[b].effectivePath;
                            if (string.IsNullOrEmpty(path)) continue;
                            var nb = Parse(path);
                            if (!nb.Valid || !nb.SameAs(a.Current)) continue;
                            hits.Add($"{asset.name}/{maps[m].name}/{action.name}");
                        }
                    }
                }
            }
        }
        catch { }
        return string.Join(",", hits);
    }

    internal static string ContextList()
    {
        var sb = new StringBuilder();
        foreach (ModContext ctx in Enum.GetValues(typeof(ModContext)))
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(ctx).Append('=').Append(CountOf(ctx));
        }
        return sb.ToString();
    }

    internal static string UtcStamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
