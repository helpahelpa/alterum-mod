using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AlterumModelSwap.Hot;

// "Alterum Mod" block in the real ESC/pause menu.
//
// What went wrong before, and what this file does instead
// -------------------------------------------------------
// The section used to be a GameObject parents to `Pause Menu/Viewport` with absolute anchors and
// its own VerticalLayoutGroup. That viewport is a plain full-screen Image with no layout group, so
// the section was placed by coordinates we chose - against an assumed 640x360 surface while the
// real UI canvas is 1920x1080 at scaleFactor ~0.333 - and it landed at the bottom edge of the
// window, outside the pause panel, as raw text. Worse, every hot reload reset the static handles
// but not the GameObjects, so each generation added another section and the pause menu accumulated
// six of them (measured: `kids=Pause Menu/Viewport` -> 6x AlterumModSection).
//
// Now the block is a clone of a native settings block (Title + vertical `Settings` group) and its
// rows are clones of native toggle rows, parented into the Settings page's own block container.
// Nothing is positioned by us: the game's VerticalLayoutGroup/ContentSizeFitter place the block and
// its rows exactly as they place `Camera`/`Highlighting`, at the native row size. Build() destroys
// any earlier section first, so a hot reload replaces the block instead of stacking another one.
//
// The rows carry no reachable game setting: the donor's Set* components (SettingView and its
// selectors, appliers, localizers, tooltips) are destroyed, not disabled, and the on/off graphic
// is the row's own Checkmark driven by our state. The value lives in EnhancementSettings (chest,
// hips) and LobbyStartSettings (one player) and is read/written by polling, so a click runs the
// owner's setter and an external change moves the widget with SetValueWithoutNotify.
internal static class ModPauseSettings
{
    private const string BlockName = "AlterumModBlock";
    private const string LegacySectionName = "AlterumModSection";
    private const string RowPrefix = "AlterumRow_";
    private const string BlockTitle = "Alterum Mod";

    private const float SyncSeconds = 0.25f;

    // The value the game's own readiness controller accepts as "not modded". Off restores it.
    private const string StateType = "AlterumModelSwap.Hot.EnhancementSettings";

    private static float _nextSync;
    private static int _gen = -1;
    private static GameObject _block;
    private static string _blockPath;
    private static string _containerPath;
    private static bool _buildFailed;
    private static bool _loggedMissing;
    private static string _lastSummary = "not built";
    private static int _builds;
    private static int _containerWarned;

    private sealed class Row
    {
        internal string Id;
        internal UI.Elements.Toggle Toggle;
        internal GameObject Checkmark;
        internal Func<bool?> Get;
        internal Action<bool> Set;
        // Last widget value this table has seen. The widget is only treated as a USER CLICK when it
        // changes between scans; a widget that merely differs from the owner (fresh clone, built
        // after the owner was flipped by automation) is painted FROM the owner instead. Without
        // this, the one-player row pushed its stale `false` back into LobbyStartSettings on every
        // scan and undid the automation's own `on` - the countdown never started.
        internal bool Last;
        internal bool Seen;
    }

    private static readonly List<Row> Rows = new();

    internal static string Summary()
        => $"built={(_block != null)} rows={Rows.Count} builds={_builds} onePlayer={(LobbyStartSettings.OnePlayer ? "on" : "off")} " +
           $"container='{(_containerPath ?? "-")}' {_lastSummary}";

    internal static void Tick(IHostContext log)
    {
        // Event-driven. The block lives in the pause menu's Settings page, which only exists while
        // that menu is open, so nothing is looked up (and nothing is scanned) otherwise. Within one
        // open the objects are stable: the build runs once per MenuScope generation, plus a bounded
        // retry inside the settling window for a page that finishes a frame or two after the edge.
        if (!MenuScope.IsOpen) return;

        try
        {
            if (_block == null || _gen != MenuScope.Generation)
            {
                bool firstThisGeneration = _gen != MenuScope.Generation;
                if (!firstThisGeneration && !MenuScope.Settling) return;   // no retry storm on a timer
                _gen = MenuScope.Generation;
                Build(log);
            }
            if (_block == null) return;

            float now;
            try { now = Time.unscaledTime; } catch { return; }
            if (now < _nextSync) return;
            _nextSync = now + SyncSeconds;
            Sync(log);
        }
        catch (Exception e) { log?.LogWarning($"[pause:mod] tick failed: {e.Message}"); }
    }

    // ---- stream A state, by reflection ---------------------------------------------------

    private static Type _stateType;
    private static bool _stateProbed;

    private static Type StateTypeOrNull()
    {
        if (_stateProbed) return _stateType;
        _stateProbed = true;
        try { _stateType = typeof(ModPauseSettings).Assembly.GetType(StateType, false, false); }
        catch { _stateType = null; }
        return _stateType;
    }

    private static bool EnhancementResolved() => StateTypeOrNull() != null;

    private static bool? ReadState(string name)
    {
        var t = StateTypeOrNull();
        if (t == null) return null;
        try
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (p != null && p.CanRead && p.PropertyType == typeof(bool)) return (bool)p.GetValue(null, null);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(null);
        }
        catch { }
        return null;
    }

    private static void WriteState(string setter, bool value)
    {
        var t = StateTypeOrNull();
        if (t == null) return;
        try
        {
            var m = t.GetMethod(setter, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic,
                                null, new[] { typeof(bool) }, null);
            if (m == null) { _lastSummary = $"EnhancementSettings.{setter}(bool) missing"; return; }
            m.Invoke(null, new object[] { value });
        }
        catch (Exception e) { _lastSummary = $"{setter} threw: {e.InnerException?.Message ?? e.Message}"; }
    }

    // ---- build ---------------------------------------------------------------------------

    private static void Build(IHostContext log)
    {
        if (_buildFailed) return;

        var view = FindFirst<UI.Pause.PauseMenuView>();
        if (view == null) return;                                     // no pause menu in this scene yet

        // The pause menu's Settings page is a page stack: Pause Menu/Viewport/Content/Settings.
        Transform settings = null;
        try
        {
            settings = Dig(view.transform, "Viewport", "Content", "Settings");
            if (settings == null) settings = DeepFindName(view.transform, "Settings", 6);
        }
        catch { }
        if (settings == null) { _lastSummary = "no Settings page under the pause menu"; return; }

        // Its block container is the scroll content every native block (General/Camera/…) sits in.
        // Found structurally rather than by an absolute path: the Settings page keeps several
        // sub-pages (`Gameplay Settings`, `Session Settings`, …), each with its own ScrollView, and
        // a hardcoded path picked the wrong one the moment the page set changed.
        Transform container = FindBlockContainer(settings, log);
        if (container == null)
        {
            _lastSummary = "no settings block container";
            if (_containerWarned < 3)
            {
                _containerWarned++;
                log?.LogWarning("[pause:mod] pause menu Settings page found but its block container did not resolve");
            }
            return;
        }

        var donorBlock = FindDonorBlock(container);
        var donorRow = FindDonorRow(container);
        if (donorBlock == null) { _lastSummary = "no donor block"; return; }
        if (donorRow == null) { _lastSummary = "no donor toggle row"; return; }

        try
        {
            // One section, always: an earlier generation's block is destroyed rather than joined.
            int removed = DestroyExistingSections();

            _block = UnityEngine.Object.Instantiate(donorBlock, container, false);
            if (_block == null) { _buildFailed = true; _lastSummary = "block clone returned null"; return; }
            _block.name = BlockName;

            var title = _block.GetComponentInChildren<TMP_Text>(true);
            if (title != null)
            {
                StripRowHandlers(title.gameObject, "title");
                title.text = BlockTitle;
                title.richText = true;
            }

            // The donor block's rows are dropped; ours are built below. The block's own `Settings`
            // group (a VerticalLayoutGroup with the native spacing and padding) is kept, so the rows
            // are placed by the game's layout code.
            Transform rowGroup = Dig(_block.transform, "Settings");
            if (rowGroup == null) rowGroup = FirstChildWithVerticalLayout(_block.transform);
            if (rowGroup == null) { _buildFailed = true; _lastSummary = "donor block has no Settings group"; return; }
            ClearChildren(rowGroup);

            Rows.Clear();
            Rows.Add(MakeRow(log, donorRow, rowGroup, "chest", "Alterum: Chest enhancements",
                () => EnhancementResolved() ? ReadState("ChestEnabled") : null,
                v => WriteState("SetChest", v)));
            Rows.Add(MakeRow(log, donorRow, rowGroup, "hips", "Alterum: Hip enhancements",
                () => EnhancementResolved() ? ReadState("HipsEnabled") : null,
                v => WriteState("SetHips", v)));
            Rows.Add(MakeRow(log, donorRow, rowGroup, "oneplayer", "Alterum: Start with one player",
                () => LobbyStartSettings.OnePlayer,
                // The row is the same switch automation's `ui.request req=1` drives; the write to the
                // lobby's server-owned NetworkVariable happens only in LobbyStartSettings.Apply, which
                // refuses it on a client (NetRole) and persists the switch value instead.
                v => LobbyStartSettings.Set(log, v)));

            if (Rows.Count < 3 || Rows[2].Toggle == null)
            {
                _lastSummary = "a row clone failed";
                _buildFailed = true;
                return;
            }

            _blockPath = ClothInternals.PathOf(_block.transform);
            _containerPath = ClothInternals.PathOf(container);
            _builds++;

            int w = 0, h = 0; float scale = 0f;
            try { w = Screen.width; h = Screen.height; } catch { }
            try { var cv = _block.GetComponentInParent<Canvas>(); scale = cv != null ? cv.scaleFactor : 0f; } catch { }
            var blockRt = _block.transform.TryCast<RectTransform>();
            log?.LogInfo($"[pause:mod] block built '{_blockPath}' container='{_containerPath}' " +
                         $"screen={w}x{h} canvasScale={scale:F3} blockRect={(blockRt != null ? $"{blockRt.rect.width:F1}x{blockRt.rect.height:F1}" : "-")} " +
                         $"rows={Rows.Count} destroyed={removed} donorRow='{ClothInternals.PathOf(donorRow.transform)}'");
            _lastSummary = $"rows={Rows.Count} destroyed={removed}";

            if (!EnhancementResolved() && !_loggedMissing)
            {
                _loggedMissing = true;
                log?.LogWarning($"[pause:mod] '{StateType}' not present in this build: chest/hips widgets mirror nothing");
            }

            Sync(log);
        }
        catch (Exception e)
        {
            _buildFailed = true;
            log?.LogError($"[pause:mod] block build failed: {e}");
        }
    }

    private static Row MakeRow(IHostContext log, GameObject donorRow, Transform parent, string id, string caption,
                              Func<bool?> get, Action<bool> set)
    {
        var row = new Row { Id = id, Get = get, Set = set };
        try
        {
            var go = UnityEngine.Object.Instantiate(donorRow, parent, false);
            if (go == null) return row;
            go.name = RowPrefix + id;

            int stripped = StripRowHandlers(go, id);

            var labels = go.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] == null) continue;
                labels[i].text = caption;
                labels[i].richText = true;
            }

            var toggle = go.GetComponentInChildren<UI.Elements.Toggle>(true);
            if (toggle == null) { UnityEngine.Object.Destroy(go); return row; }
            row.Toggle = toggle;
            try { toggle.Toggled = null; } catch { }
            try { toggle.ValueChanged = null; } catch { }
            try { toggle.IsBecomeActiveUnsafe?.RemoveAllListeners(); } catch { }
            try { toggle.IsBecomeNotActiveUnsafe?.RemoveAllListeners(); } catch { }
            try { toggle.ClickedSafe = null; } catch { }
            try { toggle.ClickedSafeReferenced = null; } catch { }
            try { toggle.ClickedUnsafe?.RemoveAllListeners(); } catch { }

            row.Checkmark = FindCheckmark(go);

            // Adopt the owner's current value BEFORE any sync runs: a fresh Toggle starts off, and
            // a sync that read that as a user click would write `off` over a default-on setting
            // (measured: chest on -> off on the first build).
            bool? initial = null;
            try { initial = get?.Invoke(); } catch { }
            if (initial.HasValue)
            {
                try { toggle.SetValueWithoutNotify(initial.Value); } catch { }
                PaintCheckmark(row, initial.Value);
            }

            log?.LogInfo($"[pause:mod] row '{id}' clone donors destroyed={stripped} left=[{MenuProof.DonorAppliers(go)}] " +
                         $"checkmark={(row.Checkmark != null)}");
            return row;
        }
        catch (Exception e)
        {
            log?.LogWarning($"[pause:mod] row '{id}' clone failed: {e.Message}");
            return row;
        }
    }

    private static GameObject FindCheckmark(GameObject row)
    {
        try
        {
            var trs = row.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < trs.Length; i++)
                if (trs[i] != null && trs[i].name == "Checkmark") return trs[i].gameObject;
        }
        catch { }
        return null;
    }

    // Components a cloned row must not keep: everything that can write the donor's setting or
    // rewrite the caption. Destroyed rather than disabled - a disabled donor applier can still be
    // the object another (kept) component dereferences, and it can be re-enabled by the game.
    private static int StripRowHandlers(GameObject go, string what)
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
                if (tn == "Toggle" || tn == "Button" || tn == "TextMeshProUGUI" || tn == "TMP_Text" ||
                    tn == "Image" || tn == "LayoutElement" || tn == "Mask" || tn == "RectMask2D" ||
                    tn == "ColorTransitionController" || tn == "ColorTransition" || tn == "UIElement") continue;

                bool donor = tn.IndexOf("Selector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             tn.IndexOf("SettingDisplayer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             tn.IndexOf("Applier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             tn.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             tn.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             tn.IndexOf("SettingView", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!donor) continue;
                try { UnityEngine.Object.Destroy(mb); n++; } catch { }
            }
        }
        catch { }
        return n;
    }

    // ---- dedupe --------------------------------------------------------------------------

    // Every block/section a previous generation built, under any scene canvas. Their GameObjects
    // outlive a hot reload (the statics do not), and they are what made the rows appear two, three
    // and finally six times.
    private static int DestroyExistingSections()
    {
        int n = 0;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Canvas>());
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i].TryCast<Canvas>();
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                var found = new List<GameObject>();
                CollectByName(go.transform, found);
                for (int k = 0; k < found.Count; k++)
                {
                    if (found[k] == null) continue;
                    try { UnityEngine.Object.Destroy(found[k]); n++; } catch { }
                }
            }
        }
        catch { }
        if (n > 0) _ = n;
        return n;
    }

    private static void CollectByName(Transform t, List<GameObject> into)
    {
        if (t == null) return;
        var go = t.gameObject;
        if (go == null) return;
        string nm = go.name;
        if (nm == BlockName || nm == LegacySectionName) { into.Add(go); return; }
        int c = t.childCount;
        for (int i = 0; i < c; i++) CollectByName(t.GetChild(i), into);
    }

    // ---- donors --------------------------------------------------------------------------

    // A native block: a direct child of the block container that has its own `Settings` group.
    private static GameObject FindDonorBlock(Transform container)
    {
        GameObject best = null;
        for (int i = 0; i < container.childCount; i++)
        {
            var c = container.GetChild(i);
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || go.name.StartsWith("Alterum", StringComparison.Ordinal)) continue;
            if (Dig(c, "Settings") == null) continue;
            if (go.GetComponentInChildren<UI.Elements.Toggle>(true) == null) continue;   // needs a toggle row
            var le = go.GetComponent<LayoutElement>();
            if (best == null) best = go;
            else if (le != null) best = go;                                            // prefer one with explicit sizing
        }
        return best;
    }

    // A native toggle row: sits under a `Settings` group and holds exactly one toggle and one label.
    private static GameObject FindDonorRow(Transform container)
    {
        for (int i = 0; i < container.childCount; i++)
        {
            var block = container.GetChild(i);
            if (block == null) continue;
            if (block.gameObject.name.StartsWith("Alterum", StringComparison.Ordinal)) continue;
            var group = Dig(block, "Settings");
            if (group == null) continue;
            for (int r = 0; r < group.childCount; r++)
            {
                var row = group.GetChild(r);
                if (row == null) continue;
                var toggles = row.GetComponentsInChildren<UI.Elements.Toggle>(true);
                if (toggles == null || toggles.Length != 1) continue;
                var labels = row.GetComponentsInChildren<TMP_Text>(true);
                if (labels == null || labels.Length < 1) continue;
                return row.gameObject;
            }
        }
        return null;
    }

    // ---- live sync -----------------------------------------------------------------------

    private static void Sync(IHostContext log)
    {
        try
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if (row?.Toggle == null) continue;
                bool? owner = null;
                try { owner = row.Get?.Invoke(); } catch { }
                bool widget = false;
                try { widget = row.Toggle.IsActive; } catch { }
                bool click = row.Seen && widget != row.Last;
                row.Last = widget;
                row.Seen = true;
                if (click)
                {
                    // The widget changed since the last scan: that is the user operating it.
                    row.Set?.Invoke(widget);
                    log?.LogInfo($"[pause:mod] {row.Id} {owner?.ToString() ?? "-"} -> {widget} (widget click)");
                    try { owner = row.Get?.Invoke(); } catch { }
                }
                else if (owner.HasValue && widget != owner.Value)
                {
                    // Owner changed underneath a widget the user did not touch: paint the widget.
                    try { row.Toggle.SetValueWithoutNotify(owner.Value); } catch { }
                    try { widget = row.Toggle.IsActive; } catch { }
                    row.Last = widget;
                }
                try { row.Toggle.SetValueWithoutNotify(owner ?? widget); } catch { }
                PaintCheckmark(row, owner ?? widget);
            }
            LobbyStartSettings.Sync(log);
        }
        catch (Exception e) { log?.LogWarning($"[pause:mod] sync failed: {e.Message}"); }
    }

    // The checkmark is a plain Image child in the native row; the donor component that used to move
    // it is gone, so the state is painted here. Only written when it differs.
    private static void PaintCheckmark(Row row, bool on)
    {
        var cm = row.Checkmark;
        if (cm == null) return;
        try { if (cm.activeSelf != on) cm.SetActive(on); } catch { }
    }

    // Verification hook: set a widget's value the way a click would.
    internal static bool SetWidget(IHostContext log, string which, bool on)
    {
        Row row = null;
        for (int i = 0; i < Rows.Count; i++)
            if (string.Equals(Rows[i].Id, which, StringComparison.OrdinalIgnoreCase)) row = Rows[i];
        if (row?.Toggle == null) { log?.LogWarning($"[pause:mod] widget '{which}' not built"); return false; }
        try
        {
            row.Toggle.SetValueWithoutNotify(on);
            log?.LogInfo($"[pause:mod] widget '{which}' set {on} (sync applies it to its owner)");
            Sync(log);
            return true;
        }
        catch (Exception e) { log?.LogWarning($"[pause:mod] widget '{which}' failed: {e.Message}"); return false; }
    }

    internal static UI.Elements.Toggle Widget(string which)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (string.Equals(Rows[i].Id, which, StringComparison.OrdinalIgnoreCase)) return Rows[i].Toggle;
        return null;
    }

    // ---- transform helpers ---------------------------------------------------------------

    // The block container: the `Content` under a `Scroll View/Viewport` inside the Settings page
    // that holds the most native blocks (a block has its own `Settings` group and a toggle row).
    private static Transform FindBlockContainer(Transform settings, IHostContext log)
    {
        Transform best = null;
        int bestBlocks = -1;
        var found = new List<Transform>();
        CollectScrollContents(settings, 0, found);

        // The page the ESC menu opens on is the first sub-page (`Gameplay Settings`); the mod block
        // belongs there, next to the rows a player is already looking at. Only if that page has no
        // toggle row at all does the walk fall back to the sub-page with the most blocks.
        for (int i = 0; i < found.Count; i++)
        {
            var c = found[i];
            if (c == null || !c.gameObject.activeSelf && !c.gameObject.activeInHierarchy) { }
            if (c == null) continue;
            if (ClothInternals.PathOf(c).IndexOf("Gameplay Settings", StringComparison.Ordinal) < 0) continue;
            if (DigForToggleRow(c)) { best = c; bestBlocks = CountBlocks(c); break; }
        }

        if (best == null)
        for (int i = 0; i < found.Count; i++)
        {
            var c = found[i];
            if (c == null) continue;
            int blocks = CountBlocks(c);
            if (blocks > 0 && DigForToggleRow(c) && blocks > bestBlocks) { bestBlocks = blocks; best = c; }
        }
        if (best != null && bestBlocks > 0)
        {
            var all = new StringBuilder();
            for (int i = 0; i < found.Count; i++)
            {
                var c = found[i];
                if (c == null) continue;
                int n = 0;
                for (int k = 0; k < c.childCount; k++) if (c.GetChild(k) != null && Dig(c.GetChild(k), "Settings") != null) n++;
                all.Append($"\n    blocks={n} active={c.gameObject.activeInHierarchy} '{ClothInternals.PathOf(c)}'");
            }
            log?.LogInfo($"[pause:mod] block container '{ClothInternals.PathOf(best)}' blocks={bestBlocks} candidates={found.Count}{all}");
        }
        return bestBlocks > 0 ? best : null;
    }

    // Blocks in a container, ignoring the block this file builds (a previous generation's block
    // is still standing while the next one is being placed).
    private static int CountBlocks(Transform c)
    {
        int n = 0;
        for (int k = 0; k < c.childCount; k++)
        {
            var b = c.GetChild(k);
            if (b == null || b.gameObject == null) continue;
            if (b.name.StartsWith("Alterum", StringComparison.Ordinal)) continue;
            if (Dig(b, "Settings") != null) n++;
        }
        return n;
    }

    private static bool DigForToggleRow(Transform c) => FindDonorRow(c) != null;

    private static void CollectScrollContents(Transform t, int depth, List<Transform> into)
    {
        if (t == null || depth > 12) return;
        if (t.name == "Content" && t.parent != null && t.parent.name == "Viewport" &&
            t.parent.parent != null && t.parent.parent.name == "Scroll View" &&
            t.GetComponent<VerticalLayoutGroup>() != null)
            into.Add(t);
        for (int i = 0; i < t.childCount; i++) CollectScrollContents(t.GetChild(i), depth + 1, into);
    }

    private static Transform Dig(Transform root, params string[] path)
    {
        var t = root;
        for (int i = 0; i < path.Length && t != null; i++)
        {
            Transform next = null;
            for (int c = 0; c < t.childCount; c++)
            {
                var ch = t.GetChild(c);
                if (ch != null && ch.name == path[i]) { next = ch; break; }
            }
            t = next;
        }
        return t;
    }

    private static Transform DeepFindName(Transform t, string name, int depth)
    {
        if (t == null || depth < 0) return null;
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = DeepFindName(t.GetChild(i), name, depth - 1);
            if (r != null) return r;
        }
        return null;
    }

    private static Transform FirstChildWithVerticalLayout(Transform t)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c == null) continue;
            if (c.GetComponent<VerticalLayoutGroup>() != null) return c;
        }
        return null;
    }

    private static void ClearChildren(Transform t)
    {
        var doomed = new List<GameObject>();
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c != null && c.gameObject != null) doomed.Add(c.gameObject);
        }
        for (int i = 0; i < doomed.Count; i++)
        {
            try { UnityEngine.Object.Destroy(doomed[i]); } catch { }
        }
    }

    private static T FindFirst<T>(Func<T, bool> filter = null) where T : Component
    {
        var arr = SceneCache.Scan(Il2CppType.Of<T>());
        for (int i = 0; i < arr.Length; i++)
        {
            var v = arr[i].TryCast<T>();
            if (v == null) continue;
            GameObject go = null;
            try { go = v.gameObject; } catch { }
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            if (go.name.StartsWith(RowPrefix, StringComparison.Ordinal) || go.name.StartsWith("Alterum", StringComparison.Ordinal)) continue;
            if (filter == null || filter(v)) return v;
        }
        return null;
    }

    internal static string RowsReport()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var rt = r.Toggle != null ? r.Toggle.transform.TryCast<RectTransform>() : null;
            sb.AppendLine($"  {r.Id} toggle={(r.Toggle != null)} active={(r.Toggle != null && r.Toggle.IsActive)} " +
                          $"checkmark={(r.Checkmark != null && r.Checkmark.activeSelf)} " +
                          $"rect={(rt != null ? $"{rt.rect.width:F1}x{rt.rect.height:F1}" : "-")}");
        }
        return sb.ToString();
    }
}
