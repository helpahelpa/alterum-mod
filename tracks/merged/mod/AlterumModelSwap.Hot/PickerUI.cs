using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AlterumModelSwap.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Both cosmetic and emote pickers. They share one 640x360 bitmap surface (PickerSurface) that is
// blitted by OnGUI and composited over the game camera for captures, so the screenshot is the same
// pixels the player sees.
//
// Input model:
//   * while closed, the only keys read are the two open keys (U and F12). No IMGUI event is
//     consumed, no cursor state is written, no rect is hit-tested, and Esc is left entirely to
//     the GAME (its pause action);
//   * while open, the overlay is mouse-driven and reads exactly one hardcoded key: Esc closes it.
//     Picking an emote goes through EmoteRuntime.StartEmote (the same PosePlayer lease path the
//     request file uses). Equipping a cosmetic goes through CosmeticsRuntime's equipped set (the
//     same set cosmetic.request writes), so Reconcile applies it on the next update.
//
//   U          open/close the emote picker   (registry row `mod.picker.emote`)
//   F12        open/close the cosmetic picker (registry row `mod.picker.cosmetic`)
//   click      pick/toggle the cell under the pointer
//   wheel      page both pickers (cursor must be over the panel)
//   Esc        close the picker (hardcoded; the pause action is gated while it is open)
//   STOP EMOTE / UNEQUIP COSMETICS buttons: the bottom-right action row. Stop releases the same
//   lease a stop request does; Unequip empties the same equipped set `NONE` empties.
//
// Nothing else is a key any more: the picker's navigation rows (PgUp/PgDn, Enter/Space, arrows,
// digits, F6 close, F9/Shift+F9 paging) were pruned with the controls table. Page stepping, cell
// selection and hovering now come from the mouse, and the `picker.request` file remains the
// unattended path for every one of those operations.
//
// Cursor: the game runs with the cursor hidden and locked, which leaves nothing to aim with. An
// open picker captures both values, sets visible/None, and restores the captured pair on close and
// in Dispose (a hot reload retires this object through Dispose, so a reload while open cannot
// leave the cursor visible). The marker is drawn into the surface itself, because the OS cursor is
// never part of a RenderTexture and the desktop session is locked.
internal sealed class PickerUI : IDisposable
{
    private enum Kind { None, Emote, Cosmetic }

    private const string RequestFileName = "picker.request";
    private const float RequestPollSeconds = 0.2f;

    // Design-space layout (see PickerSurface.Width/Height). The live blit stretches this to the
    // real screen; the window is run at 640x360, so it is 1:1 in the demo.
    private const int PanelX = 8, PanelY = 8, PanelW = 624, PanelH = 344;
    private const int GridX = 20, GridY = 68;
    private const int Cols = 4, Rows = 3, Gap = 5;
    private const int CellW = 147, CellH = 64;                 // emote grid: 4*147+3*5 = 603
    private const int CosCellW = 90, CosCellH = 64;            // cosmetic grid: 4*90+3*4 = 372
    private const int CosGap = 4;
    private const int PagesPerGrid = Cols * Rows;
    private const int PreviewPanelX = 404, PreviewPanelY = 68, PreviewPanelW = 216, PreviewPanelH = 200;
    private const int PreviewSize = 160;

    // Palette. Straight alpha: the panel is translucent over the live game.
    private static readonly Color32 CDim = new(5, 7, 11, 205);
    private static readonly Color32 CPanel = new(16, 20, 28, 238);
    private static readonly Color32 CPanelEdge = new(64, 78, 100, 255);
    private static readonly Color32 CHeader = new(24, 30, 41, 255);
    private static readonly Color32 CTitle = new(240, 245, 252, 255);
    private static readonly Color32 CText = new(224, 230, 239, 255);
    private static readonly Color32 CDimText = new(140, 152, 170, 255);
    private static readonly Color32 CFaint = new(76, 88, 106, 255);
    private static readonly Color32 CAccent = new(255, 196, 84, 255);
    private static readonly Color32 CAccentFill = new(255, 196, 84, 42);
    private static readonly Color32 CGreen = new(112, 232, 176, 255);
    private static readonly Color32 CGreenFill = new(112, 232, 176, 32);
    private static readonly Color32 CCell = new(26, 32, 43, 255);
    private static readonly Color32 CCellHi = new(40, 49, 64, 255);
    private static readonly Color32 CCellEdge = new(52, 62, 78, 255);
    private static readonly Color32 CBadgeLc = new(240, 170, 80, 255);
    private static readonly Color32 CBadgeBody = new(120, 190, 255, 255);
    private static readonly Color32 CBadgeArm = new(192, 152, 255, 255);
    private static readonly Color32 CBadgeHat = new(240, 170, 80, 255);
    private static readonly Color32 CBadgeChest = new(120, 190, 255, 255);
    private static readonly Color32 CBadgeHip = new(160, 220, 130, 255);
    private static readonly Color32 CBadgeWrist = new(192, 152, 255, 255);

    private readonly IHostContext _h;
    private readonly EmoteRuntime _emotes;
    private readonly CosmeticsRuntime _cosmetics;
    private readonly PickerSurface _surface = new();

    private Kind _open = Kind.None;
    private int _emotePage;
    private int _emoteHighlight;
    private int _cosPage;
    private int _cosHighlight;
    private bool _dirty = true;
    private string _status = "";

    private List<EmoteRuntime.PickerEntry> _entries = new();
    private readonly List<Page> _pages = new();
    private readonly List<string> _cosIds = new();
    private int _catalogSignature = -1;
    private int _cosSignature = -1;

    private float _nextRequestPoll;
    private float _openedAt;

    private bool _cursorSaved;
    private CursorLockMode _savedLock;
    private bool _mouseArmed;
    private Vector2 _openMouse;
    private bool _savedVisible;
    private int _cursorFights;

    // Screen -> surface mapping, recomputed once per Tick. `_mouseMapped` is false only when there
    // is no mouse device; `_mouseOverride` is the automation path (locked desktop => the real
    // pointer cannot be moved from outside), fed by `mouse=x,y` in picker.request.
    private bool _mouseMapped;
    private int _mouseX = -1, _mouseY = -1;
    private float _mouseScreenX, _mouseScreenY;
    private string _mouseSource = "none";
    private bool _mouseOverride;
    private float _overrideX, _overrideY;
    private int _drawnMouseX = int.MinValue, _drawnMouseY = int.MinValue;

    // The bottom-right action row (Stop Emote / Unequip Cosmetics) and the surface state that has
    // to mark the bitmap dirty because a blit only shows what was last built.
    private Rect _actionRect = new(0f, 0f, 0f, 0f);
    private bool _actionHover;
    private string _stateSignature = "";

    private Rect _pageRect;

    // One page holds the whole emote catalogue: two clips, one row, no category groups. `Start` and
    // `Count` are still used by the grid/hit-test code so a future catalogue longer than one row
    // would page instead of overflowing.
    private sealed class Page
    {
        internal int Start;
        internal int Count;
    }

    internal PickerUI(IHostContext host, EmoteRuntime emotes, CosmeticsRuntime cosmetics)
    {
        _h = host;
        _emotes = emotes;
        _cosmetics = cosmetics;
        _h.LogInfo("[picker] ready: U emote picker | F12 cosmetic picker | click pick | wheel page | " +
                   "Esc close while open (hardcoded) | overlay 640x360 IMGUI blit, camera-composited captures");
        _live = this;
        InstallEscGuard(host);
    }

    internal bool IsOpen => _open != Kind.None;
    internal bool EmoteOpen => _open == Kind.Emote;
    internal bool CosmeticOpen => _open == Kind.Cosmetic;

    // ------------------------------------------------------------------ update

    internal void Tick(float dt)
    {
        try
        {
            RebuildCatalogs();
            UpdateMouse();
            Keys();
            PollRequestFile();
            UpdateDirty();
            if (_dirty) BuildSurface();
            // The game can re-hide or re-lock the cursor while a menu is open. Report the fight
            // once per open instead of fighting it every frame (the camera keeps working).
            if (_open != Kind.None && _cursorSaved && _openedAt > 0f && Time.unscaledTime - _openedAt > 0.5f && _cursorFights == 0)
            {
                _cursorFights = 1;
                _h.LogInfo($"[picker:cursor] 0.5s after open: visible={B(Cursor.visible)} locked={LockName(Cursor.lockState)} " +
                           $"(saved {B(_savedVisible)}/{LockName(_savedLock)}); the picker does not re-assert it");
            }
        }
        catch (Exception e) { _h.LogError($"[picker] tick threw: {e}"); }
    }

    private static string B(bool v) => v ? "true" : "false";

    private static string LockName(CursorLockMode mode) =>
        mode == CursorLockMode.None ? "None" : mode == CursorLockMode.Locked ? "Locked" : "Confined";

    // Screen -> surface. The overlay is one stretched blit (PickerSurface.Draw), so the mouse has to
    // be scaled by Screen.width/height rather than assumed 1:1: the window is normally 640x360, but
    // a track instance, a manual resize or a launch override makes it something else, and the
    // marker has to land under the OS cursor there too. Unity reports Mouse.position in window
    // client pixels with the origin at the bottom-left; the surface is top-down.
    private void UpdateMouse()
    {
        _mouseMapped = false;
        int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
        Vector2 p;
        if (_mouseOverride)
        {
            p = new Vector2(_overrideX, _overrideY);
            _mouseSource = "request";
        }
        else
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            try { p = mouse.position.ReadValue(); } catch { return; }
            _mouseSource = "mouse";
        }
        _mouseScreenX = p.x;
        _mouseScreenY = p.y;
        _mouseX = Mathf.RoundToInt(p.x * PickerSurface.Width / sw);
        _mouseY = Mathf.RoundToInt((sh - p.y) * PickerSurface.Height / sh);
        _mouseMapped = true;
    }

    // The surface is a bitmap, so anything that changes without a key/pick has to mark it dirty or
    // the overlay shows a stale frame: the marker after a mouse move, the action button's hover/
    // enabled state, and the play/equipped state the button renders.
    //
    // The equipped part is the monotonic EquippedVersion, NOT EquippedCount + EquippedSummary():
    // the summary is a display string that truncates after 64 chars, so once the sorted set fills
    // that prefix a mutation that swaps a later id while keeping the count (unequip one id, equip
    // another that sorts after the cut) left the signature byte-identical, _dirty false, and the
    // equipped badges + highlighted cell stale until some other input redrew the surface. The
    // version changes on every mutation of the set, whatever the ids and the count do.
    private void UpdateDirty()
    {
        if (_open == Kind.None) return;
        string sig;
        if (_open == Kind.Emote)
            sig = "emote:play=" + (_emotes.PlayingId ?? "-") + ":lease=" + (_emotes.LeaseHeld ? "1" : "0");
        else
            sig = "cosmetic:v=" + _cosmetics.EquippedVersion;
        if (sig != _stateSignature) { _stateSignature = sig; _dirty = true; }
        if (_mouseMapped && (_mouseX != _drawnMouseX || _mouseY != _drawnMouseY)) _dirty = true;
        bool hover = ActionHovered();
        if (hover != _actionHover) { _actionHover = hover; _dirty = true; }
    }

    private bool ActionHovered() =>
        _open != Kind.None && _actionRect.width > 0f && _mouseMapped &&
        _actionRect.Contains(new Vector2(_mouseX, _mouseY));

    private void RebuildCatalogs()
    {
        int emotes = _emotes.PickerEntryCount;
        int cos = _cosmetics.CosmeticCount;
        int sig = emotes * 131 + cos;
        int cosSig = cos;
        if (sig != _catalogSignature)
        {
            _catalogSignature = sig;
            _entries = _emotes.PickerEntries();
            BuildPages();
            if (_open == Kind.Emote) SetDefaultEmoteSelection();
            _dirty = true;      // the native catalogue arrives after the pack: redraw when it does
        }
        if (cosSig != _cosSignature)
        {
            _cosSignature = cosSig;
            BuildCosIds();
            if (_open == Kind.Cosmetic) SetDefaultCosSelection();
            _dirty = true;
        }
    }

    // The emote catalogue is one flat list in EmoteCatalog order (no category groups, no "BODY" or
    // "ITEMS" sections), so paging only exists for the (unreachable today) case of a catalogue that
    // does not fit one grid. Two entries build exactly one page.
    private void BuildPages()
    {
        _pages.Clear();
        for (int s = 0; s < _entries.Count; s += PagesPerGrid)
            _pages.Add(new Page { Start = s, Count = Mathf.Min(PagesPerGrid, _entries.Count - s) });
        if (_emotePage >= _pages.Count) _emotePage = Mathf.Max(0, _pages.Count - 1);
    }

    private void BuildCosIds()
    {
        _cosIds.Clear();
        _cosIds.Add("");                       // the NONE cell
        foreach (var def in _cosmetics.SortedCosmetics()) _cosIds.Add(def.Id);
        if (_cosPage >= CosPageCount) _cosPage = Mathf.Max(0, CosPageCount - 1);
    }

    private int CosPageCount => Mathf.Max(1, (_cosIds.Count + PagesPerGrid - 1) / PagesPerGrid);

    private void SetDefaultEmoteSelection()
    {
        int selected = _emotes.SelectedIndex;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Index != selected) continue;
            _emoteHighlight = i;
            _emotePage = PageOf(i);
            return;
        }
        _emotePage = 0;
        _emoteHighlight = 0;
    }

    private void SetDefaultCosSelection()
    {
        for (int i = 1; i < _cosIds.Count; i++)
        {
            if (!_cosmetics.IsEquipped(_cosIds[i])) continue;
            _cosHighlight = i;
            _cosPage = i / PagesPerGrid;
            return;
        }
        _cosPage = 0;
        _cosHighlight = 0;
    }

    private int PageOf(int entryPosition)
    {
        for (int p = 0; p < _pages.Count; p++)
            if (entryPosition >= _pages[p].Start && entryPosition < _pages[p].Start + _pages[p].Count) return p;
        return 0;
    }

    private Page CurrentPage => _pages.Count == 0 ? null : _pages[Mathf.Clamp(_emotePage, 0, _pages.Count - 1)];

    // ------------------------------------------------------------------ keys

    // The only registry reads this overlay makes are the two open keys, both World-context rows, so
    // they are read with the world gate: a paused menu or a focused chat field must not open a
    // picker. `trigger` names the row and its current key for the [picker] log lines.
    private static bool Pressed(ModContext ctx, string id) => ModBindings.PressedIn(ctx, id, out _);

    private static bool Pressed(ModContext ctx, string id, out string trigger)
        => ModBindings.PressedIn(ctx, id, out trigger);

    private void Keys()
    {
        // Open/close keys. World-context rows (they open the overlay), so the world gate applies.
        if (Pressed(ModContext.World, "mod.picker.emote", out string emoteTrigger))
        {
            if (_open == Kind.Emote) Close(emoteTrigger, false);
            else Open(Kind.Emote, emoteTrigger);
        }
        if (Pressed(ModContext.World, "mod.picker.cosmetic", out string cosTrigger))
        {
            if (_open == Kind.Cosmetic) Close(cosTrigger, false);
            else Open(Kind.Cosmetic, cosTrigger);
        }
        if (_open == Kind.None) return;

        // Closing: hardcoded Esc, and ONLY while the overlay is open. It is not a registry row any
        // more, so a closed picker leaves Esc to the game's pause action untouched. The same
        // physical press is kept off that action by the guard installed in InstallEscGuard: an
        // Input System callback is not an IMGUI event, so Event.Use() cannot consume it.
        if (EscPressed()) { Close("esc", false); return; }

        MouseInput();
    }

    // True only while an overlay is open and the world gate is clear (a game console owning the
    // keyboard keeps its own Esc). Reads the hardcoded key, never the registry.
    private bool EscPressed()
    {
        if (_open == Kind.None) return false;
        if (!EscThisFrame()) return false;
        try { _escConsumedFrame = Time.frameCount; } catch { }
        return true;
    }

    // The raw hardcoded Esc, gated the same way for both readers of it: a world-blocked keyboard (the
    // game console, a focused chat field) keeps its own Esc. Never the registry.
    private static bool EscThisFrame()
    {
        if (ModBindings.Blocked(out _)) return false;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            var key = kb[Key.Escape];
            return key != null && key.wasPressedThisFrame;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ esc / pause boundary

    // The overlay is the topmost thing on screen, so THE ONE PRESS this overlay consumed closes it and
    // must not also open the pause menu. The game toggles pause on its own InputAction, whose callback
    // runs independently of IMGUI; the one place both paths meet is Pause.Pauser.Toggle/Pause.
    //
    // What the guard declines is therefore one exact press, not a state: the frame whose Esc this
    // overlay consumed (`_escConsumedFrame`). Every other pause - gamepad, a programmatic call, or one
    // pressed while the overlay is merely open - passes through untouched; it also closes the overlay
    // (see PauseTogglePrefix), so the picker never floats over a paused game. `Esc` with no overlay
    // open stays the game's key: nothing is consumed then, so nothing is declined.
    //
    // Installation is all-or-nothing: both entry points are patched or none is. A partial patch would
    // be worse than none - a caller reaching the unguarded entry point would pause on the Esc frame.
    private static PickerUI _live;
    private static HarmonyLib.Harmony _escGuard;
    // One line per outcome per session, so both the declined and the allowed path are visible once.
    private static bool _escGuardDeclinedLogged, _escGuardAllowedLogged;
    // The frame this overlay consumed Esc in. Declining pause only while `_open` is set is not
    // enough: this tick may close the overlay BEFORE the game's pause callback runs in the same
    // frame, and that callback would then see a closed picker and pause. The frame stamp makes the
    // decision independent of callback order.
    private static int _escConsumedFrame = int.MinValue;

    private static bool SameFrame(int frame)
    {
        try { return frame != int.MinValue && Time.frameCount == frame; }
        catch { return false; }
    }

    private static void InstallEscGuard(IHostContext log)
    {
        if (_escGuard != null) return;
        HarmonyLib.Harmony h = null;
        try
        {
            // `Toggle` is the game's pause action entry point; `Pause` is patched as well because a
            // handler that calls it directly would otherwise slip past the guard. Both prefixes are
            // the same method: they decline ONLY the exact Esc frame this overlay consumed.
            h = new HarmonyLib.Harmony("com.user0.alterum.pickerguard." + Guid.NewGuid().ToString("N"));
            var prefix = new HarmonyLib.HarmonyMethod(typeof(PickerUI), nameof(PauseTogglePrefix));
            int hooked = 0;
            var missing = new System.Text.StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                string name = i == 0 ? "Toggle" : "Pause";
                var m = HarmonyLib.AccessTools.DeclaredMethod(typeof(Pause.Pauser), name);
                if (m == null)
                {
                    if (missing.Length > 0) missing.Append(',');
                    missing.Append(name);
                    continue;
                }
                h.Patch(m, prefix: prefix);
                hooked++;
            }
            // All or nothing: a guard on one of two `Pauser` entry points is a half-guard - the other
            // entrance would still pause on the frame the overlay consumed Esc in.
            if (hooked < 2)
            {
                try { h.UnpatchSelf(); } catch { }
                log?.LogWarning($"[picker:esc] pause guard install failed: {hooked}/2 Pause.Pauser entry point(s) " +
                                $"({(missing.Length == 0 ? "-" : missing.ToString())} not found) - NOT installed (nothing patched); " +
                                "Esc closes the overlay and the pause menu may open on the same press");
                return;
            }
            _escGuard = h;
            log?.LogInfo($"[picker:esc] pause declined only on the frame this overlay consumed Esc " +
                         $"(prefix on {hooked} Pause.Pauser entry point(s)); a pause while the overlay is open " +
                         "closes it, and Esc with no overlay open is unchanged");
        }
        catch (Exception e)
        {
            // No half-guard survives a failure either: whatever was patched is removed again.
            try { h?.UnpatchSelf(); } catch { }
            log?.LogWarning($"[picker:esc] pause guard install failed ({e.GetType().Name}: {e.Message}) - " +
                            "NOT installed; Esc still closes the overlay and the pause menu may open on the same press");
        }
    }

    // false = skip the game's pause toggle. The ONLY condition that declines a pause is "this frame's
    // Esc press belongs to this overlay": either the picker's own tick already consumed it
    // (`_escConsumedFrame`), or the game's pause callback got here first and the press is still
    // unconsumed - in which case the picker consumes it here (stamp + close) so one press does one
    // thing whichever order the two paths run in. Every other pause - gamepad, programmatic, or one
    // while the overlay is merely open - is the game's: it goes through untouched and closes the
    // overlay, so the picker never floats over a paused game.
    private static bool PauseTogglePrefix()
    {
        try
        {
            var live = _live;
            bool consumed = SameFrame(_escConsumedFrame);
            if (!consumed && live != null && live._open != Kind.None && EscThisFrame())
            {
                try { _escConsumedFrame = Time.frameCount; } catch { }
                try { live.Close("esc", false); }
                catch (Exception e) { live._h.LogWarning($"[picker:esc] close from the pause callback failed: {e.Message}"); }
                consumed = true;
            }
            if (consumed)
            {
                if (!_escGuardDeclinedLogged && live != null)
                {
                    _escGuardDeclinedLogged = true;
                    live._h.LogInfo("[picker:esc] pause declined (overlayOpen=" +
                                    (live._open != Kind.None) + " escThisFrame=True): " +
                                    "Esc closes the overlay, not the menu");
                }
                return false;
            }
            if (live != null && live._open != Kind.None)
            {
                // Esc was not pressed this frame, so this pause is the game's own (gamepad /
                // programmatic). It is allowed and the overlay closes with it.
                string was = live._open == Kind.Emote ? "emote" : "cosmetic";
                try { live.Close("pause", false); }
                catch (Exception e) { live._h.LogWarning($"[picker:esc] close on pause failed: {e.Message}"); }
                if (!_escGuardAllowedLogged)
                {
                    _escGuardAllowedLogged = true;
                    live._h.LogInfo($"[picker:esc] pause allowed (escThisFrame=False): the {was} overlay closes " +
                                    "with it and the game's pause proceeds");
                }
            }
            return true;
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ mouse

    // Everything the pruned key rows used to do, from the pointer: click-select (grid and the
    // bottom-right action row), wheel paging and hover. Hit-testing is in surface pixels; the
    // marker is drawn into the surface so it lands under the OS cursor after the stretch.
    private void MouseInput()
    {
        var mouse = Mouse.current;
        if (!_mouseMapped) return;
        int mx = _mouseX, my = _mouseY;

        // The action row is hit-tested before the grid, so a click on Stop Emote / Unequip
        // Cosmetics can never also land on a cell behind it.
        if (_actionRect.width > 0f && _actionRect.Contains(new Vector2(mx, my)))
        {
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) PressAction("mouse-click");
            return;
        }

        bool inside = mx >= PanelX && mx < PanelX + PanelW && my >= PanelY && my < PanelY + PanelH;

        // The OS cursor sits wherever it was when the picker opened (usually the screen centre
        // while the game has it locked). Do not let that position steal the initial highlight:
        // hovering only starts once the user actually moves the mouse. A click is always honoured,
        // and a request-supplied position counts as a move.
        if (_mouseOverride) _mouseArmed = true;
        else if (!_mouseArmed && mouse != null &&
                 (mouse.position.ReadValue() - _openMouse).sqrMagnitude > 4f) _mouseArmed = true;
        if (inside && mouse != null && Mathf.Abs(mouse.scroll.ReadValue().y) > 0.1f)
            StepPage(mouse.scroll.ReadValue().y > 0f ? -1 : +1, "mouse-wheel");

        int cell = HitCell(mx, my);
        if (_mouseArmed && cell >= 0 && cell != HighlightCell())
        {
            SetHighlightCell(cell);
            _h.LogInfo($"[picker:{(EmoteOpen ? "emote" : "cosmetic")}] hover cell={cell + 1} " +
                       $"id='{(EmoteOpen ? EntryAt(cell)?.Id : IdAtCos(cell))}' trigger=mouse");
        }
        if (inside && cell >= 0 && mouse != null && mouse.leftButton.wasPressedThisFrame)
            PickCell(cell, "mouse-click");
    }

    private int HighlightCell()
    {
        if (_open == Kind.Emote)
        {
            var page = CurrentPage;
            return page == null ? 0 : Mathf.Clamp(_emoteHighlight - page.Start, 0, Mathf.Max(0, page.Count - 1));
        }
        return Mathf.Clamp(_cosHighlight - _cosPage * PagesPerGrid, 0, Mathf.Max(0, PageCosCount(_cosPage) - 1));
    }

    private void SetHighlightCell(int cell)
    {
        if (_open == Kind.Emote)
        {
            var page = CurrentPage;
            if (page == null) return;
            _emoteHighlight = Mathf.Clamp(page.Start + cell, 0, Mathf.Max(0, _entries.Count - 1));
        }
        else _cosHighlight = Mathf.Clamp(_cosPage * PagesPerGrid + cell, 0, Mathf.Max(0, _cosIds.Count - 1));
        _dirty = true;
    }

    private int PageCosCount(int page)
    {
        int start = page * PagesPerGrid;
        return Mathf.Clamp(_cosIds.Count - start, 0, PagesPerGrid);
    }

    private int HitCell(int mx, int my)
    {
        int start = EmoteOpen ? (CurrentPage?.Start ?? 0) : _cosPage * PagesPerGrid;
        int count = EmoteOpen ? (CurrentPage?.Count ?? 0) : PageCosCount(_cosPage);
        if (count <= 0) return -1;
        int cw = EmoteOpen ? CellW : CosCellW;
        int ch = EmoteOpen ? CellH : CosCellH;
        int gap = EmoteOpen ? Gap : CosGap;
        if (mx < GridX || my < GridY) return -1;
        int col = (mx - GridX) / (cw + gap);
        int row = (my - GridY) / (ch + gap);
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return -1;
        int cx = GridX + col * (cw + gap), cy = GridY + row * (ch + gap);
        if (mx > cx + cw || my > cy + ch) return -1;            // in the gap
        int cell = row * Cols + col;
        return cell < count ? cell : -1;
    }

    private void StepPage(int delta, string trigger)
    {
        if (_open == Kind.Emote)
        {
            if (_pages.Count == 0) return;
            int next = (_emotePage + delta + _pages.Count) % _pages.Count;
            if (next == _emotePage) return;
            _emotePage = next;
            _emoteHighlight = CurrentPage.Start;
            _h.LogInfo($"[picker:emote] page {_emotePage + 1}/{_pages.Count} trigger={trigger}");
        }
        else
        {
            int count = CosPageCount;
            int next = (_cosPage + delta + count) % count;
            if (next == _cosPage) return;
            _cosPage = next;
            _cosHighlight = _cosPage * PagesPerGrid;
            _h.LogInfo($"[picker:cosmetic] page {_cosPage + 1}/{count} trigger={trigger}");
        }
        _dirty = true;
    }

    private void PickCell(int cell, string trigger)
    {
        if (_open == Kind.Emote)
        {
            int pos = (CurrentPage?.Start ?? 0) + cell;
            if (pos < 0 || pos >= _entries.Count) return;
            PickEmote(_entries[pos], trigger);
        }
        else
        {
            int index = _cosPage * PagesPerGrid + cell;
            if (index < 0 || index >= _cosIds.Count) return;
            PickCosmetic(_cosIds[index], trigger);
        }
    }

    private EmoteRuntime.PickerEntry EntryAt(int cell)
    {
        int pos = (CurrentPage?.Start ?? 0) + cell;
        return pos >= 0 && pos < _entries.Count ? _entries[pos] : null;
    }

    private string IdAtCos(int cell)
    {
        int index = _cosPage * PagesPerGrid + cell;
        return index >= 0 && index < _cosIds.Count ? _cosIds[index] : null;
    }

    // ------------------------------------------------------------------ open / close / pick

    private void Open(Kind kind, string trigger)
    {
        if (kind == Kind.None) { Close(trigger, false); return; }
        if (_open == kind) return;
        if (_open != Kind.None) Close(trigger + ":switch", true);
        _open = kind;
        _status = "";
        _cursorFights = 0;
        _actionHover = false;
        _stateSignature = "";
        _openedAt = Time.unscaledTime;
        _mouseArmed = false;
        try { if (Mouse.current != null) _openMouse = Mouse.current.position.ReadValue(); } catch { }
        if (kind == Kind.Emote)
        {
            if (_pages.Count == 0) RebuildCatalogs();
            SetDefaultEmoteSelection();
            // `playing`/`lease` on both the open and the close line are the emote-continuity
            // evidence: opening mid-emote must leave the same clip playing, and closing without a
            // pick must not change it.
            _h.LogInfo($"[picker:emote] open trigger={trigger} page={_emotePage + 1}/{Mathf.Max(1, _pages.Count)} " +
                       $"clips={_entries.Count} selected='{_emotes.SelectedId}' highlight='{(_emoteHighlight < _entries.Count ? _entries[_emoteHighlight].Id : "-")}' " +
                       $"playing='{(_emotes.PlayingId ?? "-")}' lease={(_emotes.LeaseHeld ? "held" : "free")}");
        }
        else
        {
            if (_cosIds.Count == 0) RebuildCatalogs();
            SetDefaultCosSelection();
            _h.LogInfo($"[picker:cosmetic] open trigger={trigger} page={_cosPage + 1}/{CosPageCount} " +
                       $"cosmetics={Mathf.Max(0, _cosIds.Count - 1)} equipped={_cosmetics.EquippedCount} " +
                       $"highlight='{(_cosHighlight < _cosIds.Count ? _cosIds[_cosHighlight] : "-")}'");
        }
        SaveCursorAndShow(trigger);
        _emotes.SetPickerOpen(true);
        _dirty = true;
        BuildSurface();       // builds now so the first OnGUI frame is already correct
    }

    private void Close(string trigger, bool switching)
    {
        if (_open == Kind.None) return;
        string was = _open == Kind.Emote ? $"emote page={_emotePage + 1}/{Mathf.Max(1, _pages.Count)}" : $"cosmetic page={_cosPage + 1}/{CosPageCount}";
        string emoteState = _open == Kind.Emote
            ? $" playing='{(_emotes.PlayingId ?? "-")}' lease={(_emotes.LeaseHeld ? "held" : "free")}"
            : "";
        _open = Kind.None;
        _status = "";
        _dirty = true;
        _actionRect = new Rect(0f, 0f, 0f, 0f);
        _actionHover = false;
        _stateSignature = "";
        _emotes.SetPickerOpen(false);
        if (!switching) RestoreCursor(trigger);
        _h.LogInfo($"[picker] closed ({was}) trigger={trigger}{emoteState}");
    }

    private void SaveCursorAndShow(string trigger)
    {
        if (_cursorSaved) return;
        _savedLock = Cursor.lockState;
        _savedVisible = Cursor.visible;
        _cursorSaved = true;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        _h.LogInfo($"[picker:cursor] visible=true locked=None (was {B(_savedVisible)}/{LockName(_savedLock)}) trigger={trigger}");
        _h.LogInfo($"[picker:cursor] map screen={Screen.width}x{Screen.height} -> surface={PickerSurface.Width}x{PickerSurface.Height} " +
                   $"scaleX={PickerSurface.Width / (float)Mathf.Max(1, Screen.width):0.000} " +
                   $"scaleY={PickerSurface.Height / (float)Mathf.Max(1, Screen.height):0.000} " +
                   $"mouse={MouseLabel()}");
    }

    private string MouseLabel() => _mouseMapped
        ? $"({_mouseX},{_mouseY}) from screen ({_mouseScreenX:0.#},{_mouseScreenY:0.#}) src={_mouseSource}"
        : "none";

    private void RestoreCursor(string trigger)
    {
        if (!_cursorSaved) return;
        _cursorSaved = false;
        try
        {
            Cursor.visible = _savedVisible;
            Cursor.lockState = _savedLock;
            // The readback is what proves the pair went back; the saved values alone would only
            // prove what this class intended.
            _h.LogInfo($"[picker:cursor] restored visible={B(_savedVisible)} locked={LockName(_savedLock)} " +
                       $"readback={B(Cursor.visible)}/{LockName(Cursor.lockState)} trigger={trigger}");
        }
        catch (Exception e) { _h.LogWarning($"[picker:cursor] restore failed: {e.Message}"); }
    }

    private void PickEmote(EmoteRuntime.PickerEntry entry, string trigger)
    {
        if (entry == null) return;
        _emoteHighlight = Mathf.Clamp(IndexOfEntry(entry.Index), 0, Mathf.Max(0, _entries.Count - 1));
        bool ok = _emotes.StartEmote(entry.Index, "picker:" + trigger);
        _status = ok ? $"PLAYING {entry.Id}" : $"FAILED {entry.Id}";
        _h.LogInfo($"[picker:emote] pick id='{entry.Id}' index={entry.Index} trigger={trigger} started={ok} " +
                   $"page={_emotePage + 1}/{Mathf.Max(1, _pages.Count)}");
        if (ok) Close(trigger + ":picked", false);
        _dirty = true;
    }

    private int IndexOfEntry(int emoteIndex)
    {
        for (int i = 0; i < _entries.Count; i++) if (_entries[i].Index == emoteIndex) return i;
        return 0;
    }

    private void PickCosmetic(string id, string trigger)
    {
        if (id == null) return;
        if (id.Length == 0)
        {
            int before = _cosmetics.EquippedCount;
            _cosmetics.ClearEquipped("picker:" + trigger);
            _status = before == 0 ? "ALREADY EMPTY" : "UNEQUIPPED ALL";
            _h.LogInfo($"[picker:cosmetic] pick NONE trigger={trigger} was={before} equipped=0");
        }
        else
        {
            bool on = _cosmetics.ToggleEquipped(id, "picker:" + trigger);
            _status = (on ? "EQUIPPED " : "UNEQUIPPED ") + id;
            _h.LogInfo($"[picker:cosmetic] pick id='{id}' trigger={trigger} equipped={on} " +
                       $"count={_cosmetics.EquippedCount}");
        }
        _dirty = true;
    }

    // ------------------------------------------------------------------ draw

    private void BuildSurface()
    {
        _dirty = false;
        var s = _surface;
        s.Clear();
        s.Dim(CDim);
        s.Fill(PanelX, PanelY, PanelW, PanelH, CPanel);
        s.Frame(PanelX, PanelY, PanelW, PanelH, CPanelEdge, 2);
        s.Fill(PanelX + 2, PanelY + 2, PanelW - 4, 30, CHeader);
        if (_open == Kind.Emote) DrawEmote();
        else if (_open == Kind.Cosmetic) DrawCosmetic();
        else { _actionRect = new Rect(0f, 0f, 0f, 0f); return; }
        DrawActionRow();
        if (_mouseMapped)
        {
            DrawCursorMarker(_mouseX, _mouseY);
            _drawnMouseX = _mouseX;
            _drawnMouseY = _mouseY;
        }
        else { _drawnMouseX = int.MinValue; _drawnMouseY = int.MinValue; }
    }

    // The bottom-right action row: "Stop Emote" in the emote picker, "Unequip Cosmetics" in the
    // cosmetic picker. Both are dimmed when there is nothing to act on, and both call the existing
    // path (EmoteRuntime.StopEmote / CosmeticsRuntime.ClearEquipped) rather than a second one.
    private void DrawActionRow()
    {
        var s = _surface;
        bool emote = _open == Kind.Emote;
        string label = emote ? "STOP EMOTE" : "UNEQUIP COSMETICS";
        bool enabled;
        string hint;
        if (emote)
        {
            string playing = _emotes.PlayingId;
            enabled = _emotes.Playing;
            hint = enabled
                ? $"STOP '{playing}' | RELEASES THE POSE LEASE (JIGGLE RESUMES)"
                : "NOTHING PLAYING | PICK A CLIP FIRST";
        }
        else
        {
            int n = _cosmetics.EquippedCount;
            enabled = n > 0;
            hint = enabled
                ? $"CLEAR ALL {n} EQUIPPED | SAME SET + RECONCILE AS NONE"
                : "NOTHING EQUIPPED";
        }

        const int h = 20, y = 322;
        int w = PickerSurface.Measure(label, 1) + 26;
        int x = PanelX + PanelW - 14 - w;
        _actionRect = new Rect(x, y, w, h);
        // Read the hover straight from the mapped mouse instead of the Tick-cached flag: a capture
        // requested from the same picker.request also builds the surface before UpdateDirty runs.
        bool hover = enabled && ActionHovered();
        s.Fill(x, y, w, h, enabled ? (hover ? CGreenFill : CAccentFill) : CCell);
        s.Frame(x, y, w, h, enabled ? (hover ? CGreen : CAccent) : CFaint, hover ? 2 : 1);
        s.TextCenter(x + w / 2, y + (h - PickerFont.GlyphHeight) / 2, label, 1, enabled ? CTitle : CFaint);
        s.Text(GridX, y + 7, PickerSurface.EllipsizeLine(hint, Mathf.Max(40, x - GridX - 10), 1), 1,
               enabled ? CDimText : CFaint);
    }

    // Drawn into the surface, not by OnGUI, so the same marker is in the live blit and in a
    // camera-composited capture. A RenderTexture never contains the OS cursor, and the desktop
    // session is locked, so this is the only pointer that can be shown in an image at all.
    private void DrawCursorMarker(int mx, int my)
    {
        int x = Mathf.Clamp(mx, 9, PickerSurface.Width - 10);
        int y = Mathf.Clamp(my, 9, PickerSurface.Height - 10);
        var s = _surface;
        s.Frame(x - 5, y - 5, 11, 11, new Color32(0, 0, 0, 210), 1);
        s.Frame(x - 4, y - 4, 9, 9, CAccent, 1);
        s.Fill(x - 1, y - 1, 3, 3, new Color32(0, 0, 0, 220));
        s.Set(x, y, CTitle);
        s.Fill(x - 9, y, 3, 1, CAccent);
        s.Fill(x + 7, y, 3, 1, CAccent);
        s.Fill(x, y - 9, 1, 3, CAccent);
        s.Fill(x, y + 7, 1, 3, CAccent);
    }

    private void PressAction(string trigger)
    {
        if (_open == Kind.Emote)
        {
            string was = _emotes.PlayingId;
            bool ok = _emotes.StopEmote("picker:" + trigger);
            _status = ok ? "STOPPED" : "NOTHING PLAYING";
            _h.LogInfo($"[picker:emote] action='stop emote' trigger={trigger} was='{was ?? "-"}' accepted={ok} " +
                       $"playing='{_emotes.PlayingId ?? "-"}' lease={(_emotes.LeaseHeld ? "held" : "free")}");
        }
        else if (_open == Kind.Cosmetic)
        {
            int before = _cosmetics.EquippedCount;
            _cosmetics.ClearEquipped("picker:" + trigger, unequipAll: true);
            _status = before == 0 ? "ALREADY EMPTY" : "UNEQUIPPED ALL";
            _h.LogInfo($"[picker:cosmetic] action='unequip cosmetics' trigger={trigger} removed={before} " +
                       $"equipped={_cosmetics.EquippedCount}");
        }
        _dirty = true;
    }

    private void DrawHeader(string title, string right)
    {
        _surface.Text(PanelX + 12, PanelY + 10, title, 2, CTitle);
        _surface.TextRight(PanelX + PanelW - 14, PanelY + 14, right, 1, CDimText);
    }

    // One compact row, one card per allowlisted clip, no category tabs and no placeholder cells:
    // the grid is drawn from PickerEntries() alone, so a removed clip cannot leave an empty slot.
    private void DrawEmote()
    {
        DrawHeader("EMOTE PICKER", $"{_entries.Count} CLIPS  ALLOWLIST: {EmoteCatalog.UpperList()}");

        // Page text only when there is more than one page: a single row is not "PAGE 1/1".
        if (_pages.Count > 1)
        {
            const int ty = 44, th = 16;
            _pageRect = new Rect(PanelX + PanelW - 170, ty, 156, th);
            _surface.TextRight(PanelX + PanelW - 14, ty + 4, $"PAGE {_emotePage + 1}/{_pages.Count}", 1, CText);
        }

        DrawEmoteGrid();
        DrawFooter();
    }

    private void DrawEmoteGrid()
    {
        var page = CurrentPage;
        if (page == null) { _surface.Text(GridX, GridY + 40, "CATALOGUE NOT READY YET", 1, CDimText); return; }
        int playing = PlayingEmoteIndex();
        for (int cell = 0; cell < page.Count; cell++)
        {
            var entry = _entries[page.Start + cell];
            int col = cell % Cols, row = cell / Cols;
            int x = GridX + col * (CellW + Gap), y = GridY + row * (CellH + Gap);
            bool hover = (page.Start + cell) == _emoteHighlight;
            bool selected = entry.Index == _emotes.SelectedIndex;
            bool isPlaying = entry.Index == playing;
            _surface.Fill(x, y, CellW, CellH, hover ? CCellHi : CCell);
            _surface.Frame(x, y, CellW, CellH, hover ? CAccent : selected ? CGreen : CCellEdge, hover ? 2 : 1);
            if (hover) _surface.Fill(x + 1, y + 1, 3, CellH - 2, CAccent);
            else if (selected) _surface.Fill(x + 1, y + 1, 3, CellH - 2, CGreen);
            if (isPlaying) _surface.Fill(x + 4, y + CellH - 4, CellW - 8, 2, CGreen);

            _surface.Text(x + 7, y + 5, (entry.Index + 1).ToString("00", CultureInfo.InvariantCulture), 1, CFaint);
            Color32 badge = entry.Badge == "LC" ? CBadgeLc : entry.Badge == "BODY" ? CBadgeBody : CBadgeArm;
            _surface.TextRight(x + CellW - 7, y + 5, entry.Badge, 1, badge);
            if (isPlaying) _surface.Text(x + 26, y + 5, "PLAYING", 1, CGreen);
            else if (selected) _surface.Text(x + 26, y + 5, "SELECTED", 1, CGreen);

            _surface.Wrap(x + 7, y + 19, entry.Name, CellW - 14, 1, 2, CText);
            string meta = entry.Length > 0f
                ? $"{entry.Length.ToString("0.00", CultureInfo.InvariantCulture)}S  {(entry.Loop ? "LOOP" : "ONCE")}"
                : "";
            _surface.Text(x + 7, y + CellH - 13, meta, 1, CDimText);
            if (cell < 10)
                _surface.TextRight(x + CellW - 7, y + CellH - 13,
                    cell < 9 ? (cell + 1).ToString(CultureInfo.InvariantCulture) : "0", 1, CFaint);
        }
    }

    private int PlayingEmoteIndex()
    {
        string playing = _emotes.PlayingId;
        if (string.IsNullOrEmpty(playing)) return -1;
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Id, playing, StringComparison.OrdinalIgnoreCase)) return _entries[i].Index;
        return -1;
    }

    private void DrawCosmetic()
    {
        DrawHeader("COSMETIC PICKER", $"{Mathf.Max(0, _cosIds.Count - 1)} FROM COSMETICS.APACK   EQUIPPED {_cosmetics.EquippedCount}");

        // One row of status: the equip set is a set, not a single slot, and the picker shows it.
        _surface.Text(GridX, 46, "CLICK A CELL TO EQUIP/UNEQUIP | NONE CLEARS ALL | MULTI-EQUIP IS PRESERVED", 1, CDimText);
        _pageRect = new Rect(PanelX + PanelW - 170, 44, 156, 16);
        _surface.TextRight(PanelX + PanelW - 14, 46, $"PAGE {_cosPage + 1}/{CosPageCount}", 1, CText);

        DrawCosGrid();
        DrawPreviewPanel();
        DrawFooter();
    }

    private void DrawCosGrid()
    {
        int count = PageCosCount(_cosPage);
        for (int cell = 0; cell < count; cell++)
        {
            int index = _cosPage * PagesPerGrid + cell;
            string id = _cosIds[index];
            bool none = id.Length == 0;
            bool equipped = none ? _cosmetics.EquippedCount == 0 : _cosmetics.IsEquipped(id);
            int col = cell % Cols, row = cell / Cols;
            int x = GridX + col * (CosCellW + CosGap), y = GridY + row * (CosCellH + CosGap);
            bool hover = index == _cosHighlight;
            _surface.Fill(x, y, CosCellW, CosCellH, hover ? CCellHi : equipped ? CGreenFill : CCell);
            _surface.Frame(x, y, CosCellW, CosCellH, hover ? CAccent : equipped ? CGreen : CCellEdge, hover ? 2 : 1);
            if (hover) _surface.Fill(x + 1, y + 1, 3, CosCellH - 2, CAccent);
            else if (equipped) _surface.Fill(x + 1, y + 1, 3, CosCellH - 2, CGreen);

            string name = none ? "NONE" : ShortCosmeticName(id);
            _surface.Text(x + 7, y + 5, (index).ToString("00", CultureInfo.InvariantCulture), 1, CFaint);
            if (equipped) _surface.Text(x + 24, y + 5, "X", 1, CGreen);
            string slot = none ? "CLEAR" : SlotName(id);
            _surface.TextRight(x + CosCellW - 7, y + 5, slot, 1, SlotColor(id));
            _surface.Wrap(x + 7, y + 19, name, CosCellW - 14, 1, 2, equipped ? CGreen : CText);
            _surface.Text(x + 7, y + CosCellH - 13,
                none ? "REMOVE ALL" : equipped ? "EQUIPPED" : "CLICK TO EQUIP", 1,
                equipped ? CGreen : CDimText);
        }
    }

    private static string ShortCosmeticName(string id)
    {
        if (id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return id.Substring(8);
        return id;
    }

    private Color32 SlotColor(string id)
    {
        int type = _cosmetics.CosmeticType(id);
        switch (type)
        {
            case 0: return CBadgeHat;
            case 2: return CBadgeChest;
            case 3: return CBadgeWrist;
            case 4: return CBadgeHip;
            default: return CDimText;
        }
    }

    private string SlotName(string id)
    {
        switch (_cosmetics.CosmeticType(id))
        {
            case 0: return "HAT";
            case 2: return "CHEST";
            case 3: return "WRIST";
            case 4: return "HIP";
            default: return "SLOT";
        }
    }

    private void DrawPreviewPanel()
    {
        var s = _surface;
        s.Fill(PreviewPanelX, PreviewPanelY, PreviewPanelW, PreviewPanelH, CCell);
        s.Frame(PreviewPanelX, PreviewPanelY, PreviewPanelW, PreviewPanelH, CCellEdge, 1);
        int px = PreviewPanelX + (PreviewPanelW - PreviewSize) / 2, py = PreviewPanelY + 16;
        s.Fill(px - 1, py - 1, PreviewSize + 2, PreviewSize + 2, CPanelEdge);

        string id = _cosHighlight >= 0 && _cosHighlight < _cosIds.Count ? _cosIds[_cosHighlight] : "";
        s.Text(PreviewPanelX + 10, PreviewPanelY + 4, "LIVE 3D PREVIEW", 1, CDimText);
        if (id.Length == 0)
        {
            s.Fill(px, py, PreviewSize, PreviewSize, new Color32(20, 25, 33, 255));
            s.TextCenter(px + PreviewSize / 2, py + PreviewSize / 2 - 10, "NO COSMETIC", 2, CFaint);
            s.TextCenter(px + PreviewSize / 2, py + PreviewSize / 2 + 8, "UNEQUIP ALL", 1, CFaint);
            s.Text(PreviewPanelX + 10, PreviewPanelY + PreviewPanelH - 22, "NONE | CLEARS THE SET", 1, CText);
            return;
        }

        var preview = _cosmetics.GetPreview(id, PreviewSize);
        if (preview != null && preview.Rgba != null)
        {
            s.Blit(preview.Rgba, preview.Width, preview.Height, px + (PreviewSize - preview.Width) / 2,
                   py + (PreviewSize - preview.Height) / 2);
        }
        else
        {
            s.Fill(px, py, PreviewSize, PreviewSize, new Color32(20, 25, 33, 255));
            s.TextCenter(px + PreviewSize / 2, py + PreviewSize / 2 - 6, "PREVIEW N/A", 1, CFaint);
            if (preview != null && !string.IsNullOrEmpty(preview.Note))
                s.Wrap(PreviewPanelX + 10, py + PreviewSize / 2 + 8, preview.Note, PreviewPanelW - 20, 1, 3, CDimText);
        }
        s.Text(PreviewPanelX + 10, PreviewPanelY + PreviewPanelH - 22,
               PickerSurface.EllipsizeLine(id, PreviewPanelW - 20, 1), 1, CText);
        bool equipped = _cosmetics.IsEquipped(id);
        s.Text(PreviewPanelX + 10, PreviewPanelY + PreviewPanelH - 12,
               $"{SlotName(id)} | {(equipped ? "EQUIPPED" : "NOT EQUIPPED")}", 1, equipped ? CGreen : CDimText);
    }

    private void DrawFooter()
    {
        var s = _surface;
        int y = 274;
        s.HLine(GridX, y, PanelW - 24, CFaint);
        if (_open == Kind.Emote)
        {
            var page = CurrentPage;
            var entry = _emoteHighlight >= 0 && _emoteHighlight < _entries.Count ? _entries[_emoteHighlight] : null;
            s.Text(GridX, y + 5, "HIGHLIGHT", 1, CDimText);
            s.Text(GridX + 62, y + 5, entry != null ? PickerSurface.EllipsizeLine(entry.Id, 360, 1) : "-", 1, CAccent);
            string playing = _emotes.PlayingId;
            s.TextRight(PanelX + PanelW - 14, y + 5, string.IsNullOrEmpty(playing) ? "NO EMOTE" : "PLAYING " + playing, 1,
                        string.IsNullOrEmpty(playing) ? CDimText : CGreen);
            s.Text(GridX, y + 17, _status.Length > 0 ? _status
                      : $"CLICK A CELL TO PLAY | WHEEL PAGE | ALLOWLIST {EmoteCatalog.UpperList()}",
                   1, _status.Length > 0 ? CAccent : CText);
            s.Text(GridX, y + 29, "U OR ESC CLOSE | PICKING USES THE SAME POSE LEASE AS A REQUEST", 1, CDimText);
        }
        else
        {
            string id = _cosHighlight >= 0 && _cosHighlight < _cosIds.Count ? _cosIds[_cosHighlight] : "-";
            s.Text(GridX, y + 5, "HIGHLIGHT", 1, CDimText);
            s.Text(GridX + 62, y + 5, id.Length == 0 ? "NONE" : PickerSurface.EllipsizeLine(id, 300, 1), 1, CAccent);
            string set = _cosmetics.EquippedSummary();
            s.TextRight(PanelX + PanelW - 14, y + 5, "SET: " + (set.Length == 0 ? "EMPTY" : set), 1, CGreen);
            s.Text(GridX, y + 17, _status.Length > 0 ? _status : "CLICK TOGGLE | WHEEL PAGE",
                   1, _status.Length > 0 ? CAccent : CText);
            s.Text(GridX, y + 29, "F12 OR ESC CLOSE | NONE UNEQUIPS ALL | EQUIP PATH UNCHANGED (LAYER 6/23, MATERIAL CLONE)", 1, CDimText);
        }
    }

    // ------------------------------------------------------------------ request file

    private void PollRequestFile()
    {
        if (Time.unscaledTime < _nextRequestPoll) return;
        _nextRequestPoll = Time.unscaledTime + RequestPollSeconds;
        string path;
        try { path = Path.Combine(_h.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = null;
        try { text = File.ReadAllText(path); }
        catch (Exception e)
        {
            // A writer may still hold the file for the few milliseconds after creating it. Retry
            // on the next poll instead of deleting an unread request.
            _h.LogWarning($"[picker:request] read failed, will retry: {e.Message}");
            return;
        }
        try { File.Delete(path); } catch { }

        var actions = new List<(string key, string val)>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { _h.LogWarning($"[picker:request] ignored line '{line}' (not key=value)"); continue; }
            actions.Add((line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim()));
        }

        int applied = 0;
        foreach (var action in actions)
        {
            try { if (ApplyAction(action.key, action.val)) applied++; }
            catch (Exception e) { _h.LogError($"[picker:request] {action.key}='{action.val}' threw: {e}"); }
        }
        _h.LogInfo($"[picker:request] actions={actions.Count} applied={applied} open={(IsOpen ? _open.ToString().ToLowerInvariant() : "none")} " +
                   $"emotePage={_emotePage + 1}/{Mathf.Max(1, _pages.Count)} cosPage={_cosPage + 1}/{CosPageCount}");
    }

    private bool ApplyAction(string key, string val)
    {
        switch (key)
        {
            case "open":
            {
                string v = val.ToLowerInvariant();
                if (v == "emote" || v == "1") Open(Kind.Emote, "request:open");
                else if (v == "cosmetic" || v == "cosmetics") Open(Kind.Cosmetic, "request:open");
                else if (v == "none" || v == "0" || v == "close") Close("request:open", false);
                else return false;
                return true;
            }
            case "emote":
                if (val.Length > 0 && val != "0") Open(Kind.Emote, "request:emote");
                else Close("request:emote", false);
                return true;
            case "cosmetic":
                if (val.Length > 0 && val != "0") Open(Kind.Cosmetic, "request:cosmetic");
                else Close("request:cosmetic", false);
                return true;
            case "close":
                Close("request:close", false);
                return true;
            case "page":
            {
                if (val == "next" || val == "+1") { StepPage(+1, "request:page"); return true; }
                if (val == "prev" || val == "-1") { StepPage(-1, "request:page"); return true; }
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return false;
                if (_open == Kind.Emote && _pages.Count > 0)
                {
                    _emotePage = Mathf.Clamp(n - 1, 0, _pages.Count - 1);
                    _emoteHighlight = CurrentPage.Start;
                    _h.LogInfo($"[picker:emote] page {_emotePage + 1}/{_pages.Count} (request)");
                    _dirty = true;
                }
                else if (_open == Kind.Cosmetic)
                {
                    _cosPage = Mathf.Clamp(n - 1, 0, CosPageCount - 1);
                    _cosHighlight = _cosPage * PagesPerGrid;
                    _h.LogInfo($"[picker:cosmetic] page {_cosPage + 1}/{CosPageCount} (request)");
                    _dirty = true;
                }
                return true;
            }
            case "tab":
            {
                // Kept for older automation: the catalogue has no category tabs any more.
                if (!EmoteOpen) return false;
                _h.LogInfo($"[picker:emote] tab='{val}' ignored: the catalogue is one row " +
                           $"({EmoteCatalog.List()}), there are no category tabs");
                return true;
            }
            case "hover":
            {
                if (_open == Kind.Emote) return HoverEmote(val);
                if (_open == Kind.Cosmetic) return HoverCosmetic(val);
                return false;
            }
            case "pick":
            {
                if (_open == Kind.Emote) return PickEmoteById(val, "request:pick");
                if (_open == Kind.Cosmetic) return PickCosmeticById(val, "request:pick");
                return false;
            }
            case "capture":
                Capture(val.Length == 0 ? "state" : val);
                return true;
            case "stop":
                if (!EmoteOpen)
                {
                    _h.LogWarning("[picker:request] stop=1 needs the emote picker open");
                    return false;
                }
                if (val.Length > 0 && val != "0") PressAction("request:stop");
                return true;
            case "unequip":
                if (!CosmeticOpen)
                {
                    _h.LogWarning("[picker:request] unequip=1 needs the cosmetic picker open");
                    return false;
                }
                if (val.Length > 0 && val != "0") PressAction("request:unequip");
                return true;
            case "mouse":
                return SetMouseOverride(val);
            case "state":
                _h.LogInfo($"[picker:state] open={_open} emotePage={_emotePage + 1}/{Mathf.Max(1, _pages.Count)} " +
                           $"emoteHighlight='{(_emoteHighlight >= 0 && _emoteHighlight < _entries.Count ? _entries[_emoteHighlight].Id : "-")}' " +
                           $"selected='{_emotes.SelectedId}' cosPage={_cosPage + 1}/{CosPageCount} " +
                           $"cosHighlight='{(_cosHighlight >= 0 && _cosHighlight < _cosIds.Count ? _cosIds[_cosHighlight] : "-")}' " +
                           $"equipped=[{_cosmetics.EquippedSummary()}] playing='{_emotes.PlayingId ?? "-"}' " +
                           $"lease={(_emotes.LeaseHeld ? "held" : "free")} action={(ActionHovered() ? "hover" : "-")} " +
                           $"cursor=visible:{B(Cursor.visible)}/locked:{LockName(Cursor.lockState)} " +
                           $"saved={(_cursorSaved ? B(_savedVisible) + "/" + LockName(_savedLock) : "none")} " +
                           $"mouse={MouseLabel()}");
                return true;
            default:
                _h.LogWarning($"[picker:request] unknown key '{key}' ignored");
                return false;
        }
    }

    // `mouse=x,y` (screen client pixels, Unity's bottom-left origin) feeds the pointer from the
    // request file. The desktop session is locked, so no real pointer can be moved from outside;
    // this is the automation equivalent of moving the mouse, and it goes through the same
    // screen->surface mapping the real device does (logged so the scale can be checked).
    private bool SetMouseOverride(string val)
    {
        if (string.Equals(val, "clear", StringComparison.OrdinalIgnoreCase))
        {
            _mouseOverride = false;
            UpdateMouse();
            _dirty = true;
            _h.LogInfo($"[picker:cursor] mouse override cleared; now {MouseLabel()}");
            return true;
        }
        string[] parts = val.Split(',');
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy))
        {
            _h.LogWarning($"[picker:cursor] mouse='{val}' is not x,y");
            return false;
        }
        _mouseOverride = true;
        _overrideX = sx;
        _overrideY = sy;
        _mouseArmed = true;
        UpdateMouse();
        _dirty = true;
        _h.LogInfo($"[picker:cursor] mouse screen=({sx:0.#},{sy:0.#}) of {Screen.width}x{Screen.height} -> " +
                   $"surface=({_mouseX},{_mouseY}) [0..{PickerSurface.Width - 1}x0..{PickerSurface.Height - 1}] src=request");
        return true;
    }

    private bool HoverEmote(string val)
    {
        if (_entries.Count == 0) return false;
        int pos = -1;
        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flat))
        {
            for (int i = 0; i < _entries.Count; i++) if (_entries[i].Index == flat) { pos = i; break; }
        }
        else
        {
            // Accept "Axe_Idle", "Axe Idle" and the full id: automation and the log use ids, the
            // grid shows display names with spaces.
            string want = val.Replace('_', ' ').Trim();
            for (int i = 0; i < _entries.Count; i++)
                if (string.Equals(_entries[i].Id, val, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_entries[i].Name, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_entries[i].Name, val, StringComparison.OrdinalIgnoreCase)) { pos = i; break; }
        }
        if (pos < 0) { _h.LogWarning($"[picker:emote] hover '{val}' is not in the catalogue"); return false; }
        _emoteHighlight = pos;
        _emotePage = PageOf(pos);
        _h.LogInfo($"[picker:emote] hover id='{_entries[pos].Id}' cell={pos - CurrentPage.Start + 1} page={_emotePage + 1}/{_pages.Count} trigger=request");
        _dirty = true;
        return true;
    }

    private bool HoverCosmetic(string val)
    {
        if (_cosIds.Count == 0) return false;
        int pos = -1;
        if (val.Length == 0 || string.Equals(val, "none", StringComparison.OrdinalIgnoreCase)) pos = 0;
        else
        {
            for (int i = 0; i < _cosIds.Count; i++)
                if (string.Equals(_cosIds[i], val, StringComparison.OrdinalIgnoreCase)) { pos = i; break; }
        }
        if (pos < 0) { _h.LogWarning($"[picker:cosmetic] hover '{val}' is not a cosmetic id"); return false; }
        _cosHighlight = pos;
        _cosPage = pos / PagesPerGrid;
        _h.LogInfo($"[picker:cosmetic] hover id='{_cosIds[pos]}' page={_cosPage + 1}/{CosPageCount} trigger=request");
        _dirty = true;
        return true;
    }

    private bool PickEmoteById(string val, string trigger)
    {
        if (_entries.Count == 0) { _h.LogWarning("[picker:emote] pick: catalogue is empty"); return false; }
        int pos = -1;
        if (string.Equals(val, "highlight", StringComparison.OrdinalIgnoreCase)) pos = _emoteHighlight;
        else if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flat))
        {
            for (int i = 0; i < _entries.Count; i++) if (_entries[i].Index == flat) { pos = i; break; }
        }
        else
        {
            // Accept "Axe_Idle", "Axe Idle" and the full id: automation and the log use ids, the
            // grid shows display names with spaces.
            string want = val.Replace('_', ' ').Trim();
            for (int i = 0; i < _entries.Count; i++)
                if (string.Equals(_entries[i].Id, val, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_entries[i].Name, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_entries[i].Name, val, StringComparison.OrdinalIgnoreCase)) { pos = i; break; }
        }
        if (pos < 0 || pos >= _entries.Count) { _h.LogWarning($"[picker:emote] pick '{val}' is not in the catalogue"); return false; }
        _emoteHighlight = pos;
        _emotePage = PageOf(pos);
        PickEmote(_entries[pos], trigger);
        return true;
    }

    private bool PickCosmeticById(string val, string trigger)
    {
        if (_cosIds.Count == 0) return false;
        if (val.Length == 0 || string.Equals(val, "highlight", StringComparison.OrdinalIgnoreCase))
        {
            PickCell(HighlightCell(), trigger);
            return true;
        }
        if (string.Equals(val, "none", StringComparison.OrdinalIgnoreCase)) { PickCosmetic("", trigger); return true; }
        for (int i = 0; i < _cosIds.Count; i++)
            if (string.Equals(_cosIds[i], val, StringComparison.OrdinalIgnoreCase))
            {
                _cosHighlight = i;
                _cosPage = i / PagesPerGrid;
                PickCosmetic(_cosIds[i], trigger);
                return true;
            }
        _h.LogWarning($"[picker:cosmetic] pick '{val}' is not a cosmetic id");
        return false;
    }

    // ------------------------------------------------------------------ captures

    // Renders the live game camera into a 640x360 RenderTexture and alpha-composites the picker
    // surface over it. camera.Render() is the same mechanism ScreenShot uses; the desktop is never
    // read, so this works while the session is locked.
    private void Capture(string tag)
    {
        if (_dirty) BuildSurface();
        const int W = PickerSurface.Width, H = PickerSurface.Height;
        var log = _h;
        RenderTexture rt = null;
        Texture2D read = null, outTex = null;
        Camera cam = null;
        var prevActive = RenderTexture.active;
        var prevTarget = (RenderTexture)null;
        bool swapped = false;
        try
        {
            cam = ScreenShot.FindGameCamera(log);
            Color32[] screen;
            if (cam == null)
            {
                log.LogWarning("[picker:shot] no game camera; capturing the overlay over a solid background");
                screen = new Color32[W * H];
                for (int i = 0; i < screen.Length; i++) screen[i] = new Color32(10, 12, 16, 255);
            }
            else
            {
                rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32)
                {
                    name = "AlterumPickerShotRT", antiAliasing = 1, useMipMap = false, autoGenerateMips = false,
                };
                if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
                prevTarget = cam.targetTexture;
                cam.targetTexture = rt;
                swapped = true;
                cam.Render();
                cam.targetTexture = prevTarget;
                swapped = false;
                RenderTexture.active = rt;
                read = new Texture2D(W, H, TextureFormat.RGB24, false);
                read.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                read.Apply();
                RenderTexture.active = prevActive;
                screen = read.GetPixels32(0);
            }

            byte[] outPx = IsOpen ? _surface.CompositeOver(screen, W, H) : OpaqueFromScreen(screen, W, H);
            outTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            outTex.LoadRawTextureData(outPx);
            outTex.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(outTex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string safe = Sanitize(tag);
            string path = Path.Combine(log.ScreensDir, $"picker_{safe}.png");
            File.WriteAllBytes(path, bytes);
            string where = _open == Kind.Emote
                ? $"page={_emotePage + 1}/{Mathf.Max(1, _pages.Count)} highlight='{(_emoteHighlight < _entries.Count ? _entries[_emoteHighlight].Id : "-")}'"
                : _open == Kind.Cosmetic
                    ? $"page={_cosPage + 1}/{CosPageCount} highlight='{(_cosHighlight < _cosIds.Count ? _cosIds[_cosHighlight] : "-")}' equipped=[{_cosmetics.EquippedSummary()}]"
                    : "closed";
            log.LogInfo($"[picker:shot] tag='{safe}' {W}x{H} camera='{(cam != null ? cam.name : "none")}' " +
                        $"cameraPixels={(cam != null ? cam.pixelWidth + "x" + cam.pixelHeight : "0x0")} open={_open} {where} " +
                        $"composited={IsOpen} bytes={bytes.Length} -> {path}");
        }
        catch (Exception e) { log.LogError($"[picker:shot] tag='{tag}' failed: {e}"); }
        finally
        {
            try { if (swapped && cam != null) cam.targetTexture = prevTarget; } catch { }
            try { RenderTexture.active = prevActive; } catch { }
            try { if (read != null) UnityEngine.Object.Destroy(read); } catch { }
            try { if (outTex != null) UnityEngine.Object.Destroy(outTex); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
        }
    }

    private static byte[] OpaqueFromScreen(Color32[] screen, int w, int h)
    {
        var outPx = new byte[w * h * 4];
        for (int i = 0; i < screen.Length; i++)
        {
            outPx[i * 4] = screen[i].r; outPx[i * 4 + 1] = screen[i].g;
            outPx[i * 4 + 2] = screen[i].b; outPx[i * 4 + 3] = 255;
        }
        return outPx;
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "state";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
            sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' ? s[i] : '_');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ imgui

    internal void OnGUI()
    {
        if (_open == Kind.None || _surface == null) return;
        try { _surface.Draw(); }
        catch (Exception e) { _h.LogWarning($"[picker:overlay] {e.Message}"); }
    }

    public void Dispose()
    {
        try { Close("dispose", false); } catch { }
        try { RestoreCursor("dispose"); } catch { }
        try
        {
            // Only the generation that installed the pause guard may remove it: a reload's Dispose
            // can run after the next generation already installed its own, and unpatching then
            // would silently restore the double-Esc behaviour.
            if (ReferenceEquals(_live, this))
            {
                _live = null;
                _escGuard?.UnpatchSelf();
                _escGuard = null;
            }
        }
        catch { }
        _surface.Dispose();
    }
}
