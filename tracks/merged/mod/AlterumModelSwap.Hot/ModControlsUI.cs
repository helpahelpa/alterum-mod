using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using HWG.Settings.Controls;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Puts every registry action into the game's own Settings -> Controls list.
//
// Rows are built the way the game builds them: a cloned native HWG.Settings.Controls.RebindActionUI
// row, given a real InputActionReference (not a fake) whose action lives in an InputActionAsset
// built here from ModBindings' table, plus the GUID of the binding the row should show/rebind.
// The row is then appended to the scene Services.RebindActionsCollection, because that is what the
// game's own resolution (GetRebindActionUI), its reset path and its conflicts walk. Cloning alone
// would render a row that nothing resolves - it looks right and does nothing.
//
// The asset is separate from the game's PlayerInput assets on purpose: adding to
// PlayerInput.InputActionAssets would enable our actions in the game's input runtime and let the
// game's own rebind save own our bindings. Instead the row's InputAction is the display/rebind
// surface and ModBindings stays the dispatch + persistence source of truth; each tick the row's
// effective binding path is mirrored into the registry by id, so a native rebind, a reset in the
// game's UI and a hand-edited store all converge on the same value.
internal static class ModControlsUI
{
    private const string MapName = ModBindings.MapName;
    private const string RowPrefix = "AlterumRow_";
    private const float ScanSeconds = 0.5f;      // sweep cadence while the settings rows are on screen
    private const float IdleScanSeconds = 2f;    // ... and while the pause menu is closed
    private const float BadRetrySeconds = 5f;    // a row that cannot resolve is retried this slowly

    private static InputActionAsset _asset;
    private static readonly Dictionary<string, InputActionReference> Refs =
        new Dictionary<string, InputActionReference>(StringComparer.Ordinal);
    private static bool _probed;
    private static readonly Dictionary<string, RebindActionUI> Rows =
        new Dictionary<string, RebindActionUI>(StringComparer.Ordinal);

    private static float _nextScan;
    private static Transform _container;
    private static string _containerPath;

    // `_built` describes a *generation*, it is not a latch: "the rows in `Rows` are live scene
    // objects of the scene `_scene` and the menu generation `_menuGen` that built them". It is
    // cleared by Invalidate() the moment one of those objects is gone (donor container, a cloned
    // row, the host asset/PlayerInput, a scene change, a menu edge that finds them destroyed), so
    // Tick's `if (!_built)` gate opens again and the next lifecycle event rebuilds. It used to be
    // set once and never cleared: a scene transition destroyed the container and the clones, Tick
    // kept calling Mirror (which skips dead rows), and the Controls page stayed empty or dead until
    // a hot reload or a restart.
    private static bool _built;
    private static int _menuGen = -1;                               // MenuScope.Generation this build belongs to
    private static int _scene = int.MinValue;                       // active scene handle it was built in
    private static int _invalidations;
    private static string _lastInvalidation = "none";
    private static string _waitNote;                                // last "waiting for ..." line: logged once
    private static bool _assetFailed;
    private static bool _readOnly;                                  // detached asset: listing only
    private static PInput.PlayerInput _playerInput;                 // the row's resolution surface
    private static int _builds;
    private static string _lastSummary = "not built";

    // Per-row mirror cache, rebuilt with the rows (Build) and therefore scoped to the generation.
    // `ResolveActionAndBinding` is the expensive call in the sweep - it walks the row's asset and
    // player input - and in this game it returns false for every row it cannot seat, which made the
    // 0.5 s sweep 35 native calls that bought nothing (measured: 1.2-3.9 ms every 500 ms, and that
    // class of frame is exactly what sat in the Menu p99 band). So a row is resolved once, its
    // (action, binding index) pair is reused, the last mirrored path skips the parse when nothing
    // changed, and a row that failed is retried on a slow round-robin instead of every sweep.
    private sealed class RowState
    {
        internal InputAction Action;
        internal int Index = -1;
        internal bool Bad;
        internal float RetryAt;
        internal string Path;
    }
    private static readonly Dictionary<string, RowState> _state = new Dictionary<string, RowState>(StringComparer.Ordinal);

    internal static string Summary()
        => $"built={_built} rows={Rows.Count} builds={_builds} gen={_menuGen} scene={_scene} " +
           $"invalidations={_invalidations} invalidatedBy='{_lastInvalidation}' " +
           $"container='{(string.IsNullOrEmpty(_containerPath) ? "-" : _containerPath)}' " +
           $"asset={(_asset != null ? _asset.name : (_assetFailed ? "FAILED" : "pending"))} {_lastSummary}";

    internal static void Tick(IHostContext log)
    {
        float now;
        try { now = Time.unscaledTime; } catch { return; }

        // Menu lifecycle edge. MenuScope.Generation is a managed int that moves on every pause-menu
        // open/close (MenuScope bumps it in SetState), so noticing it costs one compare per frame -
        // no native call, no scan, no timer.
        bool edge = false;
        try
        {
            int gen = MenuScope.Generation;
            if (gen != _menuGen) { _menuGen = gen; edge = true; }
        }
        catch { }

        if (edge)
        {
            // The rows are children of the panel that generation owned, so an edge is exactly where
            // they can have died. This is a liveness probe, not a blind rebuild: a menu that closes
            // and reopens over rows the game never destroyed keeps them (and their resolved
            // handles).
            CheckObjects(log, "menu lifecycle", true);
            _nextScan = 0;              // pick the new generation up on this frame, not on a timer
        }

        if (now < _nextScan) return;

        bool menu = false;
        try { menu = MenuScope.IsOpen; } catch { }

        // Sweep cadence. None of this is a steady-state object-table poll:
        //  * menu open: the mirror rate. A native rebind is the only thing the mirror exists for and
        //    it cannot happen while the pause menu is closed;
        //  * menu closed: idle. The rows cannot be touched, and a panel the game destroyed is caught
        //    by the liveness probe on the next menu edge, not by a timer;
        //  * rows not built: the same two rates, and the build attempt itself is gated below on the
        //    menu actually being open. The donor row and its container are children of the
        //    Settings -> Controls page, which the game instantiates when the user activates that tab
        //    and destroys with the panel; there is no mod-visible event for it (MenuScope only
        //    reports open/close, not page changes), so this one lookup cannot be event-driven. It is
        //    bounded by "the rows are missing": the moment Build() succeeds the sweep drops to the
        //    mirror rate, and a build that found no donor is re-armed by the next menu edge or scene
        //    change instead of being retried faster.
        _nextScan = now + (menu ? ScanSeconds : IdleScanSeconds);

        try
        {
            CheckScene(log);
            if (!_built)
            {
                // Nothing in a closed pause menu can host the rows, so an attempt made now could
                // only fail; the next open edge arms it again.
                if (!menu) return;
                Build(log);
            }
            if (!_built) return;
            CheckObjects(log, "sweep", false);
            if (!_built) return;
            Mirror(log, now);
        }
        catch (Exception e) { log?.LogWarning($"[bind:rows] tick failed: {e.Message}"); }
    }

    // Drops the whole build generation. Clearing `_built` is what makes the rows recoverable; the
    // row dictionary and the per-row resolve cache go with it, so neither the mirror nor anything
    // that asks for a row (RowFor/ApplyOverride) can address an object the game has already
    // destroyed. `dropHost` also drops the scene-scoped input side: the PlayerInput, the asset our
    // map was hosted in, and every InputActionReference made from that asset - all of them belong to
    // the scene that created them and are re-resolved by BuildAsset on the rebuild.
    private static void Invalidate(IHostContext log, string reason, bool dropHost)
    {
        bool wasBuilt = _built;
        int rows = Rows.Count;
        _built = false;
        Rows.Clear();
        _state.Clear();
        _container = null;
        _containerPath = null;
        _waitNote = null;
        _invalidations++;
        _lastInvalidation = reason;
        _lastSummary = "invalidated: " + reason;
        if (dropHost)
        {
            Refs.Clear();
            _asset = null;
            _playerInput = null;
            _readOnly = false;
            _assetFailed = false;       // a failure in the previous scene is not a verdict on this one
            _probed = false;
        }
        if (wasBuilt || dropHost)
            log?.LogInfo($"[bind:rows] build state invalidated ({reason}): wasBuilt={wasBuilt} rows={rows} " +
                         $"hostDropped={dropHost} -> rebuild armed for the next lifecycle event");
    }

    // Scene-change signal. The active scene handle is the one SceneCache and MenuScope already watch,
    // and it is read here only under the sweep gate (one call per sweep, never per frame).
    private static void CheckScene(IHostContext log)
    {
        int h;
        try { h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { return; }
        if (h == _scene) return;
        int prev = _scene;
        _scene = h;
        if (prev == int.MinValue && !_built && _asset == null) return;   // first observation, nothing cached yet
        Invalidate(log, $"scene {prev}->{h}", true);
    }

    // Cheap liveness probes on the objects the built generation actually holds: the container is one
    // reference and the rows are the same handles the mirror loop already checks, so this adds no
    // scan and no measurable cost. `hostDead` is computed before the early returns so a rebuilt
    // generation never inherits references made from a destroyed asset. `deep` (menu edges only)
    // also asks whether the game swapped the asset our map was hosted in.
    private static void CheckObjects(IHostContext log, string why, bool deep)
    {
        if (!_built) return;
        bool hostDead = !_readOnly && (_asset == null || _playerInput == null);
        if (_container == null)
        {
            Invalidate(log, $"donor container destroyed ({why})", hostDead);
            return;
        }
        string dead = null;
        foreach (var kv in Rows)
        {
            if (kv.Value == null) { dead = kv.Key; break; }
        }
        if (dead != null)
        {
            Invalidate(log, $"cloned row '{dead}' destroyed ({why})", hostDead);
            return;
        }
        if (hostDead) { Invalidate(log, $"host asset/PlayerInput destroyed ({why})", true); return; }
        if (deep && HostReplaced()) Invalidate(log, $"host asset replaced ({why})", true);
    }

    // The asset the PlayerInput currently backs our rows with. A different *live* asset means the
    // game swapped the host under us: the rows still carry the map we added to the old one, so the
    // generation has to be rebuilt against the new asset. Asked only on a menu edge (an event), and
    // a probe that cannot answer is read as "unchanged", so it can never cause a rebuild loop.
    private static bool HostReplaced()
    {
        if (_readOnly || _asset == null || _playerInput == null) return false;
        try
        {
            var wrapper = _playerInput.PlayerActionsControls;
            if (wrapper == null) return false;
            var live = wrapper._asset_k__BackingField;
            return live != null && live != _asset;
        }
        catch { return false; }
    }

    // "Waiting for the tab" is a normal state, not an error, but it must not print once per sweep.
    private static void NoteWaiting(IHostContext log, string what)
    {
        if (string.Equals(_waitNote, what, StringComparison.Ordinal)) return;
        _waitNote = what;
        _lastSummary = what;
        log?.LogInfo($"[bind:rows] waiting: {what} - re-armed on the next menu edge / scene change");
    }

    // ---- asset ---------------------------------------------------------------------------

    // The asset has to be one of the game's own. RebindActionUI resolves a row through
    // PInput.PlayerInput.GetInputAction(reference), and that returns null for an action whose asset
    // is not in PlayerInput.InputActionAssets; worse, a row whose cached PlayerInput is null throws
    // NullReferenceException *inside* GetInputAction, which is the exception that surfaced as
    // "ResolveActionAndBinding throws for a runtime-built asset" (measured field by field: the
    // reference was fine - runtime asset, correct binding id, action resolvable - while
    // row._playerInput was null and every native row had it set).
    //
    // So the map is added to a live game asset (PlayerActionsControls by preference) and every row
    // is handed that PlayerInput. ModBindings stays the dispatch + persistence source of truth;
    // the row's InputAction is only the display/rebind surface, and each tick its effective path is
    // mirrored into the registry by id.
    //
    // If no PlayerInput/asset exists, the rows fall back to a detached asset and are marked
    // read-only rather than shipped looking interactive: ResolveActionAndBinding cannot succeed
    // for a detached asset, so there is nothing to pretend.
    private static InputActionAsset BuildAsset(IHostContext log)
    {
        if (_asset != null) return _asset;
        if (_assetFailed) return null;
        try
        {
            var playerInput = FindPlayerInput(log);
            var host = HostAsset(playerInput, log);
            if (host == null)
            {
                _readOnly = true;
                _asset = DetachedAsset(log);
                log?.LogWarning("[bind:rows] no PlayerInput asset to host the bindings in: Controls rows are " +
                                "a read-only listing (the game can only resolve a rebind row through its own asset)");
                return _asset;
            }

            _playerInput = playerInput;
            _asset = host;

            // The Input System refuses to change an asset's maps while any of its actions is enabled
            // ("Cannot add, remove, or change elements of InputActionAsset ..."). That is the whole
            // reason the rows failed to build in the Menu scene and after a reload taken mid-frame:
            // measured one [Error] per generation and zero rows. Record which actions were enabled,
            // disable the asset for the edit, and restore exactly those afterwards - in a finally, so
            // a throw cannot leave the player's input disabled.
            var reenable = new List<InputAction>();
            try
            {
                for (int m = 0; m < host.actionMaps.Count; m++)
                {
                    var am = host.actionMaps[m];
                    if (am == null) continue;
                    for (int b = 0; b < am.actions.Count; b++)
                    {
                        var act = am.actions[b];
                        if (act != null && act.enabled) reenable.Add(act);
                    }
                }
                if (reenable.Count > 0)
                {
                    host.Disable();
                    log?.LogInfo($"[bind:rows] disabled '{host.name}' for the edit ({reenable.Count} action(s) were enabled; restored after)");
                }
            }
            catch (Exception e) { log?.LogWarning($"[bind:rows] asset disable failed: {e.Message}"); reenable.Clear(); }
            try
            {

            // A previous generation's map can still be attached to the live asset.
            try
            {
                var stale = host.FindActionMap(MapName, false);
                if (stale != null) { host.RemoveActionMap(stale); log?.LogInfo($"[bind:rows] removed stale map '{MapName}' from '{host.name}'"); }
            }
            catch (Exception e) { log?.LogWarning($"[bind:rows] stale map removal failed: {e.Message}"); }

            var map = host.AddActionMap(MapName);
            if (map == null) throw new InvalidOperationException($"AddActionMap('{MapName}') returned null on '{host.name}'");

            var actions = ModBindings.Actions;
            int reapplied = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                InputAction added;
                if (!a.Default.Valid)
                {
                    added = map.AddAction(a.Id, InputActionType.Button);
                }
                else if (a.Default.Plain)
                {
                    added = map.AddAction(a.Id, InputActionType.Button, a.Default.Path);
                }
                else
                {
                    added = map.AddAction(a.Id, InputActionType.Button);
                    added.AddCompositeBinding("ButtonWithOneModifier")
                        .With("modifier", "<Keyboard>/leftShift")
                        .With("button", ModBindings.PathOf(a.Default.Key, false, false, false) ?? "<Keyboard>/f5");
                }
                if (added == null) throw new InvalidOperationException($"AddAction('{a.Id}') returned null");

                // Re-apply the registry's effective binding onto the fresh action. Without this a
                // reload rebuilds the map at the *default* path while the store still holds the
                // user's override, and the mirror below would read the default back and silently
                // revert the user's rebind (measured: 'G' -> 'F5' on the reload after a rebind).
                try
                {
                    if (a.Current.Valid && !a.Current.SameAs(a.Default) && added.bindings.Count > 0)
                    {
                        if (a.Default.Plain)
                        {
                            added.ApplyBindingOverride(0, a.Current.Path);
                        }
                        else
                        {
                            // Composite layout above: 0 = ButtonWithOneModifier, 1 = modifier,
                            // 2 = button. An override has to name a control, not a chord:
                            // "shift+<Keyboard>/f10" is not a registered composite, and applying it
                            // to the composite index throws InvalidOperationException (measured:
                            // two [Error] lines per generation with a Shift+ override in the store).
                            int button = added.bindings.Count - 1;
                            added.ApplyBindingOverride(button, ModBindings.PathOf(a.Current.Key, false, false, false));
                        }
                        reapplied++;
                    }
                }
                catch (Exception e) { log?.LogWarning($"[bind:rows] {a.Id} override re-apply failed: {e.Message}"); }
            }

            log?.LogInfo($"[bind:rows] hosted map '{MapName}' in game asset '{host.name}' " +
                         $"mapActions={map.actions.Count} mapBindings={map.bindings.Count} " +
                         $"assetMaps={host.actionMaps.Count} playerInput='{SafeName(playerInput)}' " +
                         $"overridesReapplied={reapplied}");
            return _asset;
            }
            finally
            {
                for (int i = 0; i < reenable.Count; i++)
                {
                    try { if (reenable[i] != null && !reenable[i].enabled) reenable[i].Enable(); } catch { }
                }
            }
        }
        catch (Exception e)
        {
            _assetFailed = true;
            log?.LogError($"[bind:rows] action asset build failed, Controls rows disabled: {e}");
            return null;
        }
    }

    private static PInput.PlayerInput FindPlayerInput(IHostContext log)
    {
        var arr = SceneCache.ScanPolled(Il2CppType.Of<PInput.PlayerInput>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i].TryCast<PInput.PlayerInput>();
            if (p == null) continue;
            var go = p.gameObject;
            if (go == null) continue;
            string scene = "";
            try { scene = go.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;
            log?.LogInfo($"[bind:rows] PlayerInput candidate '{ClothInternals.PathOf(p.transform)}' scene='{scene}'");
            return p;
        }
        return null;
    }

    private static InputActionAsset HostAsset(PInput.PlayerInput playerInput, IHostContext log)
    {
        if (playerInput == null) return null;
        // PInput.PlayerActionsControls is the Input System's generated wrapper for the asset (same
        // shape as the rows' "asset='PlayerActionsControls'"); its backing field is the asset.
        try
        {
            var wrapper = playerInput.PlayerActionsControls;
            if (wrapper != null)
            {
                var asset = wrapper._asset_k__BackingField;
                if (asset != null) return asset;
            }
        }
        catch (Exception e) { log?.LogWarning($"[bind:rows] PlayerActionsControls threw: {e.Message}"); }
        try
        {
            var list = playerInput._inputActionAssets;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a != null) return a;
                }
        }
        catch (Exception e) { log?.LogWarning($"[bind:rows] _inputActionAssets threw: {e.Message}"); }
        return null;
    }

    private static string SafeName(UnityEngine.Object o)
    {
        try { return o == null ? "<null>" : (o.name ?? "<unnamed>"); } catch { return "<threw>"; }
    }

    // Fallback only: a detached asset cannot be resolved by the game, so the rows built from it are
    // presented as a read-only listing.
    private static InputActionAsset DetachedAsset(IHostContext log)
    {
        var json = new StringBuilder();
        json.Append("{\"name\":\"AlterumModBindings\",\"maps\":[{\"name\":\"").Append(MapName)
            .Append("\",\"id\":\"").Append(GuidFor("map:" + MapName)).Append("\",\"actions\":[");
        var actions = ModBindings.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"name\":\"").Append(actions[i].Id).Append("\",\"type\":\"Button\",\"id\":\"")
                .Append(GuidFor("action:" + actions[i].Id)).Append('"');
            json.Append(",\"expectedControlType\":\"Button\",\"processors\":\"\",\"interactions\":\"\",\"initialStateCheck\":false}");
        }
        json.Append("],\"bindings\":[");
        bool first = true;
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (!a.Default.Valid) continue;
            if (!first) json.Append(',');
            first = false;
            string actionName = a.Id;
            if (a.Default.Plain)
            {
                json.Append("{\"name\":\"\",\"id\":\"").Append(BindingId(a.Id)).Append("\",\"path\":\"")
                    .Append(a.Default.Path).Append("\",\"action\":\"").Append(actionName)
                    .Append("\",\"isComposite\":false,\"isPartOfComposite\":false}");
            }
            else
            {
                json.Append("{\"name\":\"Shift+").Append(ModBindings.KeyName(a.Default.Key))
                    .Append("\",\"id\":\"").Append(BindingId(a.Id)).Append("\",\"path\":\"ButtonWithOneModifier\"")
                    .Append(",\"action\":\"").Append(actionName)
                    .Append("\",\"isComposite\":true,\"isPartOfComposite\":false},");
                json.Append("{\"name\":\"modifier\",\"id\":\"").Append(GuidFor("part:modifier:" + a.Id))
                    .Append("\",\"path\":\"<Keyboard>/leftShift\",\"action\":\"").Append(actionName)
                    .Append("\",\"isComposite\":false,\"isPartOfComposite\":true},");
                json.Append("{\"name\":\"button\",\"id\":\"").Append(GuidFor("part:button:" + a.Id))
                    .Append("\",\"path\":\"<Keyboard>/").Append(ControlOf(a.Default.Key)).Append("\",\"action\":\"").Append(actionName)
                    .Append("\",\"isComposite\":false,\"isPartOfComposite\":true}");
            }
        }
        json.Append("]}],\"controlSchemes\":[]}");
        var asset = InputActionAsset.FromJson(json.ToString());
        if (asset == null) throw new InvalidOperationException("FromJson returned null");
        return asset;
    }

    private static string ControlOf(Key key)
    {
        string p = ModBindings.PathOf(key, false, false, false);
        if (p == null) return "f5";
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p.Substring(slash + 1) : p;
    }

    private static string BindingId(string actionId) => GuidFor("binding:" + actionId);

    // The binding id the hosted asset actually produced for an action's primary binding.
    private static bool HasCompositeBinding(InputAction action)
    {
        try
        {
            if (action == null) return false;
            var bs = action.bindings;
            for (int i = 0; i < bs.Count; i++) if (bs[i].isComposite) return true;
        }
        catch { }
        return false;
    }

    private static string BindingIdOf(InputAction action)
    {
        try
        {
            if (action == null || action.bindings.Count == 0) return null;
            var b = action.bindings[0];
            return b.id.ToString();
        }
        catch { return null; }
    }

    // The path a row's binding actually uses. A composite (chord) row has no usable path of its
    // own - its head reads "ButtonWithOneModifier" - so the parts are composed into the same
    // "Shift+<Keyboard>/x" form ModBindings.Parse takes. Without this a native rebind of a chord row
    // mirrors the composite name and the store never follows.
    private static string EffectivePathOf(InputAction action, int index)
    {
        try
        {
            if (action == null || index < 0) return null;
            // One native read of the binding list per row per sweep, not one per field access: each
            // `action.bindings` marshal copies the whole list into managed memory.
            var bindings = action.bindings;
            int count = bindings.Count;
            if (index >= count) return null;
            var b = bindings[index];
            if (!b.isComposite)
                return string.IsNullOrEmpty(b.effectivePath) ? b.path : b.effectivePath;

            string mod = null, button = null;
            for (int i = index + 1; i < count; i++)
            {
                var part = bindings[i];
                if (!part.isPartOfComposite) break;
                string p = string.IsNullOrEmpty(part.effectivePath) ? part.path : part.effectivePath;
                if (string.Equals(part.name, "modifier", StringComparison.Ordinal)) mod = p;
                else if (string.Equals(part.name, "button", StringComparison.Ordinal)) button = p;
            }
            if (string.IsNullOrEmpty(button)) return null;
            if (string.IsNullOrEmpty(mod)) return button;
            string prefix = "Shift+";
            int slash = mod.LastIndexOf('/');
            string modName = slash >= 0 ? mod.Substring(slash + 1) : mod;
            if (string.Equals(modName, "leftCtrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modName, "rightCtrl", StringComparison.OrdinalIgnoreCase)) prefix = "Ctrl+";
            else if (string.Equals(modName, "leftAlt", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(modName, "rightAlt", StringComparison.OrdinalIgnoreCase)) prefix = "Alt+";
            return prefix + button;
        }
        catch { return null; }
    }

    // Honest fallback presentation: a row the game cannot resolve must not look pressable.
    private static void MakeReadOnly(GameObject go, RebindActionUI row, ModAction action)
    {
        try
        {
            var buttons = go.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            for (int i = 0; i < buttons.Length; i++) if (buttons[i] != null) buttons[i].interactable = false;
        }
        catch { }
        try { row.enabled = false; } catch { }
        try { if (row.m_BindingText != null) row.m_BindingText.text = action.DefaultText + "  (fixed)"; } catch { }
    }

    // Stable GUID text from a name. FNV-1a twice with different offsets; only used where a GUID is
    // required (Input System binding ids, row m_BindingId), never for anything security-shaped.
    private static string GuidFor(string name)
    {
        ulong h1 = 14695981039346656037UL, h2 = 1099511628211UL;
        for (int i = 0; i < name.Length; i++)
        {
            h1 = (h1 ^ name[i]) * 1099511628211UL;
            h2 = (h2 + name[i] + 1UL) * 16777619UL;
        }
        var b = new byte[16];
        for (int i = 0; i < 8; i++) { b[i] = (byte)(h1 >> (i * 8)); b[8 + i] = (byte)(h2 >> (i * 8)); }
        return new Guid(b).ToString();
    }

    private static InputActionReference RefFor(IHostContext log, InputAction action)
    {
        if (action == null) return null;
        if (Refs.TryGetValue(action.name, out var have) && have != null) return have;
        try
        {
            var reference = InputActionReference.Create(action);
            // Asset-backed, like every reference the game's own rows carry: a reference with only
            // m_Action set breaks any consumer that resolves through reference.asset.
            reference.Set(_asset, MapName, action.name);
            reference.name = $"{MapName}/{action.name}";
            if (!_probed)
            {
                _probed = true;
                string map = "?", assetName = "?";
                try { map = action.actionMap != null ? action.actionMap.name : "<null>"; } catch (Exception e) { map = "<threw " + e.GetType().Name + ">"; }
                try { assetName = action.actionMap != null && action.actionMap.asset != null ? action.actionMap.asset.name : "<null>"; }
                catch (Exception e) { assetName = "<threw " + e.GetType().Name + ">"; }
                log?.LogInfo($"[bind:rows] reference probe: action='{action.name}' map='{map}' asset='{assetName}' " +
                             $"ref.action={(reference.action != null ? reference.action.name : "<null>")}");
            }
            Refs[action.name] = reference;
            return reference;
        }
        catch (Exception e)
        {
            log?.LogError($"[bind:rows] InputActionReference for '{action.name}' failed: {e.Message}");
            return null;
        }
    }

    // ---- rows ----------------------------------------------------------------------------

    // Drop every action of the hosted mod map whose id the registry no longer declares. Removal is
    // by NAME, never by index: MapName is ours alone, so anything else in it is a previous
    // generation's row (an F-key body/garment/emote action, a picker navigation row, ...). Names are
    // snapshotted first because RemoveAction mutates the map's own list while it is enumerated.
    private static int PruneObsoleteActions(IHostContext log, InputActionMap map)
    {
        if (map == null) return 0;
        var alive = new HashSet<string>(StringComparer.Ordinal);
        var actions = ModBindings.Actions;
        for (int i = 0; i < actions.Count; i++) alive.Add(actions[i].Id);

        var dead = new List<InputAction>();
        var live = map.actions;
        for (int i = 0; live != null && i < live.Count; i++)
        {
            var a = live[i];
            if (a == null) continue;
            string name = null;
            try { name = a.name; } catch { }
            if (!string.IsNullOrEmpty(name) && alive.Contains(name)) continue;
            dead.Add(a);
        }
        if (dead.Count == 0) return 0;

        var names = new StringBuilder();
        for (int i = 0; i < dead.Count; i++)
        {
            string name = "?";
            try { name = dead[i].name ?? "?"; } catch { }
            if (names.Length > 0) names.Append(',');
            names.Append(name);
            try { UnityEngine.InputSystem.InputActionSetupExtensions.RemoveAction(dead[i]); }
            catch (Exception e) { log?.LogWarning($"[bind:rows] removing obsolete action '{name}' failed: {e.Message}"); }
        }
        log?.LogInfo($"[bind:rows] pruned {dead.Count} obsolete action(s) from map '{MapName}': {names}");
        return dead.Count;
    }

    private static void Build(IHostContext log)
    {
        var asset = BuildAsset(log);
        if (asset == null) return;

        // The hosted map is rebuilt from the registry on every asset build, so an action that no
        // longer exists cannot be added - but a map a previous generation put on this very asset
        // (or one this build found already present) is pruned explicitly, by id, before any row is
        // cloned. A removed row's InputAction therefore cannot linger as a live, resolvable entry
        // that `RowFor` never addresses.
        try { PruneObsoleteActions(log, asset.FindActionMap(MapName, false)); }
        catch (Exception e) { log?.LogWarning($"[bind:rows] obsolete-action prune failed: {e.Message}"); }

        // Rows from a retired generation are still scene objects (the assembly reload does not
        // destroy them), so without this every reload appended another 34 rows to the game's list
        // and they accumulate in the UI as dead, unresolvable duplicates.
        int swept = DestroyStaleRows(log);
        if (swept > 0) log?.LogInfo($"[bind:rows] destroyed {swept} row(s) left by a previous generation");

        // A build attempt that finds nothing to clone from is not an error and must not spin: it
        // logs the reason once, leaves `_built` false, and the next menu edge / scene change arms it
        // again (Tick's `_nextScan = 0` on the edge, and the discovery sweep while the menu is open).
        var donors = SceneRows();
        if (donors.Count == 0) { NoteWaiting(log, "no donor row in this scene yet (Controls tab not instantiated)"); return; }
        if (donors.Count < 2) { NoteWaiting(log, $"donors={donors.Count} (the controls list is not populated yet)"); return; }

        RebindActionUI donor = null;
        for (int i = 0; i < donors.Count; i++)
        {
            var d = donors[i];
            if (d.transform.parent == null) continue;
            donor = d;
            break;
        }
        if (donor == null) { NoteWaiting(log, "no donor row with a parent"); return; }

        _container = donor.transform.parent;
        _containerPath = ClothInternals.PathOf(_container);

        int made = 0, failed = 0;
        try
        {
            var actions = ModBindings.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                if (RowFor(actions[i].Id) != null) continue;
                var row = Clone(log, donor, _container, actions[i]);
                if (row == null) { failed++; continue; }
                Rows[actions[i].Id] = row;
                made++;
            }
        }
        catch (Exception e) { log?.LogError($"[bind:rows] clone pass failed: {e}"); }

        if (made > 0)
        {
            Register(log);
            _state.Clear();                         // new rows, new handles: re-resolve once
            int resolved = 0;
            foreach (var kv in Rows) if (ResolveRow(log, kv.Key, kv.Value, false)) resolved++;
            _builds++;
            _built = true;
            _waitNote = null;
            _lastSummary = $"made={made} failed={failed} resolved={resolved}/{Rows.Count}";
            log?.LogInfo($"[bind:rows] built {made} rows in '{_containerPath}' (donor='{ClothInternals.PathOf(donor.transform)}') " +
                         $"resolved={resolved}/{Rows.Count} failed={failed}");
        }
        else if (failed > 0)
        {
            _waitNote = null;
            _lastSummary = $"made=0 failed={failed}";
        }
    }

    private static int DestroyStaleRows(IHostContext log)
    {
        int gone = 0;
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var r = arr[i].TryCast<RebindActionUI>();
            if (r == null) continue;
            var go = r.gameObject;
            if (go == null) continue;
            string scene = "";
            try { scene = go.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;                  // prefab asset, not a clone
            if (!go.name.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
            try { UnityEngine.Object.Destroy(go); gone++; } catch { }
        }
        return gone;
    }

    private static List<RebindActionUI> SceneRows()
    {
        var list = new List<RebindActionUI>();
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var r = arr[i].TryCast<RebindActionUI>();
            if (r == null) continue;
            var go = r.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            if (go.name.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
            list.Add(r);
        }
        return list;
    }

    private static RebindActionUI RowFor(string id)
    {
        if (Rows.TryGetValue(id, out var row) && row != null) return row;
        Rows.Remove(id);
        return null;
    }

    private static RebindActionUI Clone(IHostContext log, RebindActionUI donor, Transform parent, ModAction action)
    {
        try
        {
            // RebindActionUI.OnEnable runs UpdateBinding -> GetInputAction the instant Instantiate
            // makes the clone active, i.e. before _playerInput can be seated here: that is one NRE
            // per row, per generation, straight out of the game's code. Instantiate the donor with
            // its RebindActionUI disabled so the clone inherits it disabled (OnEnable is deferred),
            // seat the fields below, then enable the row once: OnEnable then runs with a valid
            // PlayerInput and a valid binding id, which is exactly the state a native row is in.
            bool donorWasEnabled = true;
            try { donorWasEnabled = donor.enabled; donor.enabled = false; } catch { }
            GameObject go;
            try { go = UnityEngine.Object.Instantiate(donor.gameObject, parent, false); }
            finally { try { donor.enabled = donorWasEnabled; } catch { } }
            if (go == null) { log?.LogWarning($"[bind:rows] instantiate returned null for {action.Id}"); return null; }
            go.name = RowPrefix + action.Id;

            var row = go.GetComponent<RebindActionUI>();
            if (row == null) { log?.LogWarning($"[bind:rows] clone has no RebindActionUI ({action.Id})"); UnityEngine.Object.Destroy(go); return null; }

            var reference = RefFor(log, _asset.FindAction(action.Id, false));
            if (reference == null) { UnityEngine.Object.Destroy(go); return null; }

            // Donor handlers first: the clone must not drive the game's settings or the donor's
            // controller when the user presses it, and a donor label localizer must not overwrite
            // our caption on the next update.
            int stripped = StripDonorHandlers(go);

            // The row's `actionReference` *property* runs the game's UpdateBinding(), which resolves
            // the action through RebindActionUI.GetInputAction and throws NullReferenceException for
            // a row whose cached PlayerInput is null. Write the serialized fields the game's own rows
            // carry instead - m_Action/m_BindingId are plain field stores with no game logic (see the
            // interop wrapper) - seat the PlayerInput the resolution goes through, and then let the
            // game's own refreshers run. `actionReference`/`bindingId` read the same fields back, so
            // ResolveActionAndBinding and the rebind path see an ordinary row.
            try { row.m_Action = reference; }
            catch (Exception e)
            {
                log?.LogError($"[bind:rows] {action.Id} m_Action assign failed: {e.Message}");
                UnityEngine.Object.Destroy(go);
                return null;
            }

            // RebindActionUI.GetInputAction(actRef) delegates to PlayerInput.GetInputAction, which
            // throws if that field is null and returns null if the action's asset is not one of the
            // PlayerInput's own. The clone does not inherit it from the donor (not serialized), so
            // it is seated here from the live PlayerInput (falling back to the donor's copy).
            bool seated = false;
            try { if (_playerInput != null) { row._playerInput = _playerInput; seated = true; } } catch (Exception e) { log?.LogWarning($"[bind:rows] {action.Id} _playerInput seat failed: {e.Message}"); }
            if (!seated) { try { row._playerInput = donor._playerInput; seated = row._playerInput != null; } catch { } }
            try { row._rebindOverlay = donor._rebindOverlay; } catch { }

            // The binding id must be one the hosted asset actually produced (AddAction generates its
            // own), not the id a detached JSON asset would have carried.
            string bindingId = BindingIdOf(reference.action);
            try { row.m_BindingId = bindingId; }
            catch (Exception e) { log?.LogWarning($"[bind:rows] {action.Id} m_BindingId failed: {e.Message}"); }
            try { row.OnBindingChanged = null; } catch { }

            // Deferred OnEnable (see the disabled Instantiate above). The game's own OnEnable runs
            // UpdateBinding, which dereferences the row's display string; that is null for a chord
            // (composite head) and for the digit/arrow rows, and update-in-binding throws. Instead of
            // guessing which shapes those are, ask the game's own refresher - it calls the identical
            // UpdateBinding path - and only enable the rows it survives. Every row keeps the
            // registry's label and key text either way (asserted unconditionally below).
            bool chord = HasCompositeBinding(reference.action);
            bool hasBinding = !string.IsNullOrEmpty(bindingId);
            bool displayOk = true;
            string displayWhy = "ok";
            try { row.UpdateBindingDisplay(); displayOk = true; }
            catch (Exception e) { displayOk = false; displayWhy = "UpdateBinding throws: " + e.Message; }
            if (seated && !chord && hasBinding && displayOk)
            {
                try { row.enabled = true; }
                catch (Exception e) { log?.LogWarning($"[bind:rows] {action.Id} enable failed: {e.Message}"); }
            }
            else log?.LogInfo($"[bind:rows] {action.Id} row left disabled (seated={seated} chord={chord} " +
                              $"binding={hasBinding} display={displayWhy})");

            if (_readOnly) MakeReadOnly(go, row, action);

            if (row.m_ActionLabel != null) row.m_ActionLabel.text = (_readOnly ? "Alterum (read-only): " : "Alterum: ") + action.Label;

            // The game's own refreshers run first (they are what a real row does); whatever they
            // write is the donor's text, so the two labels are re-asserted afterwards and
            // unconditionally: the row must show the registry's label and key, never the donor's.
            try { row.UpdateActionLabel(); } catch { }
            if (displayOk) try { row.UpdateBindingDisplay(); } catch { }
            string label = (_readOnly ? "Alterum (read-only): " : "Alterum: ") + action.Label;
            try { if (row.m_ActionLabel != null) row.m_ActionLabel.text = label; }
            catch (Exception e) { log?.LogWarning($"[bind:rows] {action.Id} action label failed: {e.Message}"); }
            try
            {
                if (row.m_BindingText != null)
                    row.m_BindingText.text = _readOnly
                        ? action.DefaultText
                        : (string.IsNullOrEmpty(row.DisplayString) ? action.CurrentText : row.DisplayString);
            }
            catch (Exception e) { log?.LogWarning($"[bind:rows] {action.Id} binding text failed: {e.Message}"); }

            bool resolved = !_readOnly && ResolveRow(log, action.Id, row, true);
            log?.LogInfo($"[bind:rows] row id={action.Id} label='{action.Label}' default={action.DefaultText} " +
                         $"resolved={resolved} readOnly={_readOnly} seatedPlayerInput={seated} strippedHandlers={stripped} " +
                         $"bindingId={bindingId}");
            return row;
        }
        catch (Exception e)
        {
            log?.LogError($"[bind:rows] clone for {action.Id} failed: {e}");
            return null;
        }
    }

    // Everything on the row that belonged to the game's own settings wiring: label localizers,
    // setting appliers, tooltips, hover handlers that call donor logic. Toggle/Button and the
    // TMP labels are kept, the RebindActionUI itself is kept (it *is* the row widget).
    private static int StripDonorHandlers(GameObject go)
    {
        int n = 0;
        try
        {
            var mbs = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < mbs.Length; i++)
            {
                var mb = mbs[i];
                if (mb == null) continue;
                string tn;
                try { tn = mb.GetIl2CppType().Name; } catch { continue; }
                if (tn == "RebindActionUI" || tn == "Button" || tn == "Toggle" || tn == "TextMeshProUGUI" ||
                    tn == "TMP_Text" || tn == "RectTransform" || tn == "CanvasRenderer") continue;
                if (tn.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) < 0 &&
                    tn.IndexOf("Applier", StringComparison.OrdinalIgnoreCase) < 0 &&
                    tn.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) < 0 &&
                    tn.IndexOf("RebindActionUIBindingSource", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try { if (mb.enabled) { mb.enabled = false; n++; } } catch { }
            }
            try
            {
                var row = go.GetComponent<RebindActionUI>();
                if (row != null)
                {
                    row.m_UpdateBindingUIEvent?.RemoveAllListeners();
                    row.m_BinbingChanged?.RemoveAllListeners();
                    row.m_RebindStartEvent?.RemoveAllListeners();
                    row.m_RebindStopEvent?.RemoveAllListeners();
                }
            }
            catch { }
        }
        catch { }
        return n;
    }

    private static bool ResolveRow(IHostContext log, string id, RebindActionUI row, bool verbose)
    {
        try
        {
            InputAction action = null;
            int index = -1;
            bool ok = row.ResolveActionAndBinding(out action, out index);
            if (!ok || action == null || index < 0)
            {
                if (verbose) log?.LogWarning($"[bind:rows] {id} did NOT resolve (ok={ok} action={(action != null)} index={index}) " +
                                             $"— the row is visible but has no resolved action");
                return false;
            }
            var b = action.bindings[index];
            string effective = EffectivePathOf(action, index);
            if (verbose)
                log?.LogInfo($"[bind:rows] resolved id={id} action='{action.name}' index={index} " +
                             $"path='{b.path}' effective='{effective}' display='{row.DisplayString}'");
            ModBindings.SetFromNative(id, effective);
            return true;
        }
        catch (Exception e)
        {
            if (verbose) log?.LogWarning($"[bind:rows] {id} resolve threw: {e.Message}");
            return false;
        }
    }

    // The game's own collection is what GetRebindActionUI / reset / conflicts walk; a row outside
    // it is decoration. Append rather than replace, so the game's rows stay untouched. Destroyed
    // entries are not copied forward: a rebuild within one scene (a panel the game tore down and
    // rebuilt) would otherwise grow the array by 34 dead references each time.
    private static void Register(IHostContext log)
    {
        try
        {
            var collection = FindCollection();
            if (collection == null) { log?.LogWarning("[bind:rows] no scene RebindActionsCollection: rows are not resolvable by the game"); return; }
            var old = collection._rebindActions;
            int oldCount = old == null ? 0 : old.Length;

            // The current generation's rows, by instance id. A reference to an `AlterumRow_` object
            // that is not one of them is OBSOLETE (a removed action's row from an earlier
            // generation, or a clone the game already destroyed) and is not copied forward: the
            // game's own GetRebindActionUI/reset/conflicts walk must never resolve a row this
            // generation cannot address. The game's own rows are always kept.
            var current = new HashSet<int>();
            foreach (var kv in Rows)
            {
                if (kv.Value == null) continue;
                try { current.Add(kv.Value.GetInstanceID()); } catch { }
            }
            var kept = new List<RebindActionUI>(oldCount);
            int dropped = 0;
            for (int i = 0; i < oldCount; i++)
            {
                var row = old[i];
                if (row == null) { dropped++; continue; }
                int id;
                try { id = row.GetInstanceID(); }
                catch { dropped++; continue; }                   // destroyed native object
                string name;
                try { name = row.gameObject == null ? "" : (row.gameObject.name ?? ""); }
                catch { dropped++; continue; }                   // destroyed: no readable name
                if (name.StartsWith(RowPrefix, StringComparison.Ordinal) && !current.Contains(id))
                {
                    dropped++;
                    continue;                                    // obsolete mod row
                }
                kept.Add(row);
            }

            var presentIds = new HashSet<int>(kept.Count);
            for (int i = 0; i < kept.Count; i++) { try { presentIds.Add(kept[i].GetInstanceID()); } catch { } }
            int add = 0;
            foreach (var kv in Rows)
            {
                if (kv.Value == null) continue;
                bool present = false;
                try { present = presentIds.Contains(kv.Value.GetInstanceID()); } catch { }
                if (!present) add++;
            }
            int keep = kept.Count;
            if (add == 0 && keep == oldCount) return;

            var arr = new Il2CppReferenceArray<RebindActionUI>(keep + add);
            int at = 0;
            for (int i = 0; i < kept.Count; i++) arr[at++] = kept[i];
            foreach (var kv in Rows)
            {
                if (kv.Value == null) continue;
                bool present = false;
                try { present = presentIds.Contains(kv.Value.GetInstanceID()); } catch { }
                if (!present) arr[at++] = kv.Value;
            }
            collection._rebindActions = arr;
            log?.LogInfo($"[bind:rows] registered in RebindActionsCollection '{ClothInternals.PathOf(collection.transform)}' " +
                         $"rows {oldCount} -> {at} (kept={keep} added={add} obsolete={dropped})");

            // PreInitialize is the collection's own (re)grab; if it replaces the array from its
            // ComponentsGrabber it will drop our rows, which is exactly what must be visible in the
            // log rather than silently tolerated.
            try
            {
                collection.PreInitialize();
                var after = collection._rebindActions;
                int afterCount = after == null ? 0 : after.Length;
                log?.LogInfo($"[bind:rows] PreInitialize -> rows={afterCount} (expected>={at})");
                if (afterCount < at)
                {
                    collection._rebindActions = arr;
                    log?.LogWarning("[bind:rows] PreInitialize dropped the added rows; array re-applied after it");
                }
            }
            catch (Exception e) { log?.LogWarning($"[bind:rows] PreInitialize threw (array kept): {e.Message}"); }
        }
        catch (Exception e) { log?.LogError($"[bind:rows] registration failed: {e}"); }
    }

    private static Services.RebindActionsCollection FindCollection()
    {
        var arr = SceneCache.ScanPolled(Il2CppType.Of<Services.RebindActionsCollection>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i].TryCast<Services.RebindActionsCollection>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            return c;
        }
        return null;
    }

    // ---- live mirror ---------------------------------------------------------------------

    // The native row owns the InputAction once built. Whatever it resolves to (user rebind, the
    // game's reset-to-default, a conflict rewrite) is copied into the registry by id, so dispatch
    // and the legend follow the row in the same second. Nothing here writes the action.
    private static void Mirror(IHostContext log, float now)
    {
        foreach (var kv in Rows)
        {
            var row = kv.Value;
            if (row == null) continue;
            if (!_state.TryGetValue(kv.Key, out var st)) { st = new RowState(); _state[kv.Key] = st; }
            if (st.Bad && now < st.RetryAt) continue;
            try
            {
                var action = st.Action;
                int index = st.Index;
                if (action == null || index < 0)
                {
                    action = null;
                    index = -1;
                    if (!row.ResolveActionAndBinding(out action, out index) || action == null || index < 0)
                    {
                        st.Bad = true;
                        st.RetryAt = now + BadRetrySeconds;
                        continue;
                    }
                    st.Action = action;
                    st.Index = index;
                }
                string effective = EffectivePathOf(action, index);
                if (string.IsNullOrEmpty(effective)) continue;
                st.Bad = false;
                if (string.Equals(effective, st.Path, StringComparison.Ordinal)) continue;   // unchanged
                st.Path = effective;
                ModBindings.SetFromNative(kv.Key, effective);
            }
            catch
            {
                // A handle that stops working (row rebuilt, asset re-hosted) is dropped, not retried
                // every sweep: the next Build recreates both the rows and this cache.
                st.Action = null;
                st.Index = -1;
                st.Bad = true;
                st.RetryAt = now + BadRetrySeconds;
            }
        }
    }

    internal static RebindActionUI Row(string id) => RowFor(id);

    // ---- diagnosis -----------------------------------------------------------------------

    // Step-by-step probe of the exact property that throws, run over one of our rows and over a
    // native row as the control. Every step is its own try/catch so the first throw does not hide
    // what the later fields hold.
    internal static void Diag(IHostContext log, string id)
    {
        log.LogInfo($"[bind:diag] asset={(_asset == null ? "<null>" : _asset.name)} " +
                    $"assetType={(_asset == null ? "-" : _asset.GetType().FullName)} rows={Rows.Count} built={_built}");

        foreach (var kv in Rows)
        {
            if (id.Length > 0 && kv.Key != id && kv.Key != "mod." + id) continue;
            Probe(log, "ours:" + kv.Key, kv.Value);
            break;
        }

        // control: a scene row the game built from a real asset
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var row = arr[i].TryCast<RebindActionUI>();
            if (row == null || row.gameObject == null) continue;
            string scene = ""; try { scene = row.gameObject.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;
            if (row.gameObject.name.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
            Probe(log, "native:" + ClothInternals.PathOf(row.transform), row);
            break;
        }
    }

    private static void Probe(IHostContext log, string tag, RebindActionUI row)
    {
        log.LogInfo($"[bind:diag] --- {tag}");
        log.LogInfo($"[bind:diag]   m_Action={Step(() => row.m_Action)}");
        log.LogInfo($"[bind:diag]   actionReference={Step(() => row.actionReference)}");
        log.LogInfo($"[bind:diag]   ref.action={Step(() => { var r = row.actionReference; return r == null ? null : r.action; })}");
        log.LogInfo($"[bind:diag]   ref.asset={Step(() => { var r = row.actionReference; return r == null ? null : r.asset; })}");
        log.LogInfo($"[bind:diag]   ref.asset.name={Step(() => { var r = row.actionReference; var a = r == null ? null : r.asset; return a == null ? null : a.name; })}");
        log.LogInfo($"[bind:diag]   ref.name={Step(() => { var r = row.actionReference; return r == null ? null : r.name; })}");
        log.LogInfo($"[bind:diag]   action.name={Step(() => { var a = Act(row); return a == null ? null : a.name; })}");
        log.LogInfo($"[bind:diag]   action.actionMap={Step(() => { var a = Act(row); var m = a == null ? null : a.actionMap; return m == null ? null : m.name; })}");
        log.LogInfo($"[bind:diag]   action.enabled={Step(() => { var a = Act(row); return a == null ? null : (object)a.enabled; })}");
        log.LogInfo($"[bind:diag]   action.bindings.Count={Step(() => { var a = Act(row); return a == null ? null : (object)a.bindings.Count; })}");
        log.LogInfo($"[bind:diag]   action.bindings[0].id={Step(() => { var a = Act(row); return a == null || a.bindings.Count == 0 ? null : a.bindings[0].id.ToString(); })}");
        log.LogInfo($"[bind:diag]   action.bindings[0].path={Step(() => { var a = Act(row); return a == null || a.bindings.Count == 0 ? null : a.bindings[0].path; })}");
        log.LogInfo($"[bind:diag]   action.bindings[0].effectivePath={Step(() => { var a = Act(row); return a == null || a.bindings.Count == 0 ? null : a.bindings[0].effectivePath; })}");
        log.LogInfo($"[bind:diag]   action.bindings[0].isComposite={Step(() => { var a = Act(row); return a == null || a.bindings.Count == 0 ? null : (object)a.bindings[0].isComposite; })}");
        log.LogInfo($"[bind:diag]   action.bindings.Count(all ids)={Step(() => Ids(Act(row)))}");
        log.LogInfo($"[bind:diag]   m_BindingId={Step(() => row.m_BindingId)}");
        log.LogInfo($"[bind:diag]   bindingId={Step(() => row.bindingId)}");
        log.LogInfo($"[bind:diag]   indexOf(m_BindingId)={Step(() => (object)IndexFor(row))}");
        log.LogInfo($"[bind:diag]   DisplayString={Step(() => { var d = row.DisplayString; return d; })}");
        log.LogInfo($"[bind:diag]   m_ActionLabel={(row.m_ActionLabel == null ? "<null>" : "'" + row.m_ActionLabel.text + "'")}");
        log.LogInfo($"[bind:diag]   m_BindingText={(row.m_BindingText == null ? "<null>" : "'" + row.m_BindingText.text + "'")}");
        log.LogInfo($"[bind:diag]   ResolveActionAndBinding={Full(() => { InputAction a; int b; return (object)row.ResolveActionAndBinding(out a, out b); })}");
        log.LogInfo($"[bind:diag]   UpdateBindingDisplay={Full(() => { row.UpdateBindingDisplay(); return "ok"; })}");
        log.LogInfo($"[bind:diag]   UpdateActionLabel={Full(() => { row.UpdateActionLabel(); return "ok"; })}");
    }

    // Bisects the InputActionReference shape: RebindActionUI.GetInputAction is public, so each
    // candidate reference is handed to it directly and the failure (or success) is attributable.
    internal static void RefProbe(IHostContext log, string id)
    {
        if (_asset == null) { log.LogWarning("[bind:ref] no asset"); return; }
        var row = RowFor(id);
        if (row == null) { log.LogWarning($"[bind:ref] no row '{id}'"); return; }
        var action = _asset.FindAction(id, false);
        if (action == null) { log.LogWarning($"[bind:ref] no action '{id}'"); return; }

        TryRef(log, row, "V0 row.m_Action (current)", () => row.m_Action);
        TryRef(log, row, "V1 Create+Set(asset,map,name)+name", () =>
        {
            var r = InputActionReference.Create(action);
            r.Set(_asset, MapName, action.name);
            r.name = $"{MapName}/{action.name}";
            return r;
        });
        TryRef(log, row, "V2 Create(action) only", () => InputActionReference.Create(action));
        TryRef(log, row, "V3 CreateInstance+Set(action)", () =>
        {
            var r = ScriptableObject.CreateInstance<InputActionReference>();
            r.Set(action);
            return r;
        });
        TryRef(log, row, "V4 Create(action)+name=donor name", () =>
        {
            var r = InputActionReference.Create(action);
            r.name = "Light Source/Toggle";
            return r;
        });
        TryRef(log, row, "V5 Create+Set(asset)+name=asset/map/action", () =>
        {
            var r = InputActionReference.Create(action);
            r.name = $"{_asset.name}/{MapName}/{action.name}";
            return r;
        });
        TryRef(log, row, "V6 Create+Set(asset,map,null)", () =>
        {
            var r = InputActionReference.Create(action);
            r.Set(_asset, MapName, null);
            return r;
        });

        // control: the game's own row and its own reference
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var nat = arr[i].TryCast<RebindActionUI>();
            if (nat == null || nat.gameObject == null) continue;
            string scene = ""; try { scene = nat.gameObject.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;
            if (nat.gameObject.name.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
            TryRef(log, row, "V7 NATIVE row + NATIVE ref (control)", () => nat.m_Action);
            TryRef(log, nat, "V8 native row + OUR ref", () => row.m_Action);
            break;
        }
    }

    private static void TryRef(IHostContext log, RebindActionUI row, string tag, Func<InputActionReference> make)
    {
        InputActionReference reference = null;
        try { reference = make(); }
        catch (Exception e) { log.LogInfo($"[bind:ref] {tag}: make THREW {e.GetType().Name} '{OneLine(e.Message)}'"); return; }
        if (reference == null) { log.LogInfo($"[bind:ref] {tag}: reference=<null>"); return; }

        string name;
        try { name = reference.name; } catch (Exception e) { name = "<threw " + e.GetType().Name + ">"; }
        string asset;
        try { var a = reference.asset; asset = a == null ? "<null>" : a.name; }
        catch (Exception e) { asset = "<threw " + e.GetType().Name + ">"; }
        string act;
        try { var a = reference.action; act = a == null ? "<null>" : a.name; }
        catch (Exception e) { act = "<threw " + e.GetType().Name + ">"; }

        try
        {
            var got = row.GetInputAction(reference);
            log.LogInfo($"[bind:ref] {tag}: OK -> action={(got == null ? "<null>" : got.name)} " +
                        $"(ref name='{name}' asset='{asset}' ref.action='{act}')");
        }
        catch (Exception e)
        {
            log.LogInfo($"[bind:ref] {tag}: GetInputAction THREW {e.GetType().Name} " +
                        $"(ref name='{name}' asset='{asset}' ref.action='{act}')");
        }
    }

    // Tests the one field that differs between a working native row and a broken clone: the
    // row's cached PInput.PlayerInput. Copies it from a live native row and retries resolution.
    internal static void PlayerInputProbe(IHostContext log, string id)
    {
        var row = RowFor(id);
        if (row == null) { log.LogWarning($"[bind:pi] no row '{id}'"); return; }

        log.LogInfo($"[bind:pi] ours _playerInput={Step(() => row._playerInput)} _rebindOverlay={Step(() => row._rebindOverlay)}");

        RebindActionUI nat = null;
        var arr = SceneCache.ScanPolled(Il2CppType.Of<RebindActionUI>(), 0.5);
        for (int i = 0; i < arr.Length; i++)
        {
            var r = arr[i].TryCast<RebindActionUI>();
            if (r == null || r.gameObject == null) continue;
            string scene = ""; try { scene = r.gameObject.scene.name; } catch { }
            if (string.IsNullOrEmpty(scene)) continue;
            if (r.gameObject.name.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
            nat = r; break;
        }
        if (nat == null) { log.LogWarning("[bind:pi] no native row"); return; }
        log.LogInfo($"[bind:pi] native _playerInput={Step(() => nat._playerInput)}");

        log.LogInfo($"[bind:pi] before: {ResolveText(row)}");
        try { row._playerInput = nat._playerInput; } catch (Exception e) { log.LogWarning($"[bind:pi] assign failed: {e.Message}"); }
        log.LogInfo($"[bind:pi] ours _playerInput now={Step(() => row._playerInput)}");
        log.LogInfo($"[bind:pi] after: {ResolveText(row)}");
        log.LogInfo($"[bind:pi] after GetInputAction: {Step2(() => row.GetInputAction(row.m_Action))}");
        log.LogInfo($"[bind:pi] after UpdateBindingDisplay: {Full(() => { row.UpdateBindingDisplay(); return "ok"; })}");
        log.LogInfo($"[bind:pi] ours DisplayString={Step(() => row.DisplayString)} shown='{(row.m_BindingText == null ? "<null>" : row.m_BindingText.text)}'");
    }

    private static string ResolveText(RebindActionUI row)
    {
        try
        {
            InputAction a; int i;
            bool ok = row.ResolveActionAndBinding(out a, out i);
            return $"ok={ok} action={(a == null ? "<null>" : a.name)} index={i}";
        }
        catch (Exception e) { return $"THREW {e.GetType().Name}"; }
    }

    private static string Step2(Func<InputAction> f)
    {
        try { var a = f(); return a == null ? "<null>" : a.name + " bindings=" + a.bindings.Count; }
        catch (Exception e) { return "THREW " + e.GetType().Name; }
    }

    private static InputAction Act(RebindActionUI row)    {
        var r = row.actionReference;
        return r == null ? null : r.action;
    }

    private static int IndexFor(RebindActionUI row)
    {
        string bid = row.m_BindingId;
        if (string.IsNullOrEmpty(bid)) return -2;
        var g = new Guid(bid);
        var a = Act(row);
        if (a == null) return -3;
        var bs = a.bindings;
        for (int i = 0; i < bs.Count; i++) if (string.Equals(bs[i].id.ToString(), g.ToString(), StringComparison.Ordinal)) return i;
        return -1;
    }

    private static string Ids(InputAction a)
    {
        if (a == null) return "<no action>";
        var bs = a.bindings;
        var sb = new StringBuilder();
        sb.Append(bs.Count).Append(":");
        for (int i = 0; i < bs.Count && i < 6; i++)
            sb.Append(' ').Append(i).Append('=').Append(Short(bs[i].id))
              .Append('/').Append(bs[i].isComposite ? "c" : (bs[i].path ?? "-"));
        return sb.ToString();
    }

    private static string Short(Il2CppSystem.Guid g)
    {
        try { string s = g.ToString(); return string.IsNullOrEmpty(s) ? "" : (s.Length > 8 ? s.Substring(0, 8) : s); }
        catch { return "?"; }
    }

    private static string Step<T>(Func<T> f)
    {
        try
        {
            T v = f();
            if (v == null) return "<null>";
            if (v is UnityEngine.Object uo) return uo == null ? "<null>" : (string.IsNullOrEmpty(uo.name) ? "<unnamed>" : uo.name);
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) { return "<threw " + e.GetType().Name + ">"; }
    }

    private static string Full(Func<object> f)
    {
        try { var v = f(); return v == null ? "<null>" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) { return "THREW " + e.GetType().FullName + " msg='" + OneLine(e.Message) + "' stack=" + OneLine(e.StackTrace); }
    }

    private static string OneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " | ");
        return s.Length > 480 ? s.Substring(0, 480) + "…" : s;
    }

    // Verification hook: apply a binding the way a completed native rebind would (override on the
    // row's action), so the store, the registry and the row display all move through the real path.
    internal static bool ApplyOverride(IHostContext log, string id, string path)
    {
        bool ok = ModBindings.SetOverride(id, path, "controls");
        var row = RowFor(id);
        if (row != null)
        {
            try
            {
                InputAction action = null; int index = -1;
                if (row.ResolveActionAndBinding(out action, out index) && action != null && index >= 0)
                {
                    action.ApplyBindingOverride(index, ModBindings.Parse(path).Path);
                    try { row.UpdateBindingDisplay(); } catch { }
                    log?.LogInfo($"[bind:rows] {id} action override applied index={index} path='{ModBindings.Parse(path).Path}' " +
                                 $"shown='{(row.m_BindingText != null ? row.m_BindingText.text : "?")}'");
                }
            }
            catch (Exception e) { log?.LogWarning($"[bind:rows] {id} action override failed: {e.Message}"); }
        }
        return ok;
    }
}
