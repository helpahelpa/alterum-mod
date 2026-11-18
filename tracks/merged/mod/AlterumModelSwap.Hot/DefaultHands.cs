using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Cameras.SwitchActionProcessors;
using Gameplay.PlayerSystem;
using HandheldSystem.Core;
using HandheldSystem.Implementation.Properties;
using Il2CppInterop.Runtime;
using InventorySystem.Core.Item;
using InventorySystem.Realization;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Default hands: while one of the imported bodies is worn, the game's own hands stay in the scene
// and keep gripping whatever the player holds.
//
// Why this is needed at all: a held item is parented by the game to a hand bone of the worn body
// (ModelInHandsParentingActionProcessor -> GetHandheldModelParent(hands)), and the two views use
// different bodies. The imported bodies have no hand geometry, so
//   * third person: the item follows the (hidden) native skeleton and floats;
//   * first person: whatever the game does with the first-view hands decides it.
// The probe below exists to settle WHICH of those is broken before anything is written:
//   hands.request  probe=1      full census of both views, the item, and the grip distance
//                  lease=on    hold the lease (see below)
//                  lease=off   release it and restore every snapshot
//                  mode=hands|forearm|arms   which native arm renderers the lease covers
//
// The lease is the fix, and it never adds geometry: it takes the native arm/hand renderers it needs
// away from the body-hide pass (SahurModel/SahurRig skip a leased renderer), forces them visible
// for as long as the lease holds, and writes back the exact snapshot it recorded - model
// generation, renderer enabled/forceRenderingOff, held-item identity and POV all participate, and
// anything that changes releases it.
internal static class DefaultHands
{
    private const string RequestName = "hands.request";
    private const float PollSeconds = 0.2f;
    private const float ReassertSeconds = 0.25f;

    // Native renderer names that carry the arms. The body meshes are authored as three segments
    // per arm pair (upper arm / forearm / hand) and every rig uses the same names; the female rigs
    // use the same convention with a different prefix. Measured from the scene, not assumed - the
    // census prints every renderer name it sees so a new rig shows up as a missing match.
    private static readonly string[] HandTokens = { "_arm_3" };
    private static readonly string[] ForearmTokens = { "_arm_2" };
    private static readonly string[] ArmTokens = { "_arm_" };

    internal enum Mode { Hands, Forearm, Arms }

    private static bool _announced;
    private static float _nextPoll, _nextReassert;

    private sealed class Lease
    {
        public Renderer R;
        public bool HadEnabled, HadForceOff;
        public int Layer;
        public string Name;
        public int Frames;
    }

    private static readonly List<Lease> _leases = new();
    private static bool _leaseOn;
    private static Mode _mode = Mode.Forearm;
    private static string _why = "";
    private static bool _force;
    private static int _releases, _takes;
    private static string _lastKey = "";

    internal static bool Active => _leaseOn && _leases.Count > 0;
    internal static int LeasedCount => _leases.Count;
    internal static bool IsLeased(Renderer r)
    {
        if (r == null || _leases.Count == 0) return false;
        for (int i = 0; i < _leases.Count; i++)
        {
            var l = _leases[i];
            if (l.R == null) continue;
            if (l.R.Pointer == r.Pointer) return true;
        }
        return false;
    }

    // ================================================================== tick

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        if (!_announced)
        {
            _announced = true;
            try { log.LogInfo($"[hands] ready request={Path.Combine(log.ScreensDir ?? "", RequestName)}"); } catch { }
        }
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        try { PollRequest(log); } catch (Exception e) { log.LogWarning($"[hands] request poll failed: {e.Message}"); }

        if (now < _nextReassert) return;
        _nextReassert = now + ReassertSeconds;
        try { Reconcile(log); } catch (Exception e) { log.LogWarning($"[hands] reconcile failed: {e.Message}"); }
    }

    // ================================================================== request file

    private static void PollRequest(IHostContext log)
    {
        if (string.IsNullOrEmpty(log.ScreensDir)) return;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        string path = Path.Combine(log.ScreensDir, RequestName);
        string text;
        try
        {
            if (!File.Exists(path)) return;
            text = File.ReadAllText(path);
            File.Delete(path);
        }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        string lease = ParseValue(text, "lease");
        if (lease != null)
        {
            bool on = lease.IndexOf("off", StringComparison.OrdinalIgnoreCase) < 0 &&
                      (lease.IndexOf("on", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       lease.IndexOf("1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       lease.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       lease.IndexOf("take", StringComparison.OrdinalIgnoreCase) >= 0);
            _force = lease.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0;
            if (on) TakeLease(log, _force ? "request:force" : "request"); else Release(log, "request");
        }
        string mode = ParseValue(text, "mode");
        if (mode != null)
        {
            if (mode.StartsWith("hand", StringComparison.OrdinalIgnoreCase)) _mode = Mode.Hands;
            else if (mode.StartsWith("fore", StringComparison.OrdinalIgnoreCase)) _mode = Mode.Forearm;
            else if (mode.StartsWith("arm", StringComparison.OrdinalIgnoreCase)) _mode = Mode.Arms;
            log.LogInfo($"[hands] mode={_mode}");
            Release(log, "mode-change");
        }
        if (Has(text, "probe") || Has(text, "dump")) Census(log, "request");
        if (Has(text, "lease")) DumpLease(log);
        string grant = ParseValue(text, "grant");
        if (!string.IsNullOrEmpty(grant)) GrantItem(log, grant);
        string drop = ParseValue(text, "drop");
        if (drop != null) DropItem(log, lease ?? drop);
        // Equip by name through the game's own active-slot activation. The reel's `hold=` request
        // reaches the same call but only while a booth exists, and a hands check must work without one.
        string hold = ParseValue(text, "hold");
        if (!string.IsNullOrEmpty(hold))
        {
            bool ok = ReelLogic.DebugHold(log, hold);
            log?.LogInfo($"[hands:hold] id='{hold}' ok={ok}");
            Reconcile(log);
        }
    }

    // Grant an item through the game's own inventory path - the same call ReelLogic.Grant uses to
    // pay a reel win out. Unattended pickup is not reachable: in a map spawn the nearest item
    // interactable sits outside the looker's 4 m sphere and nothing here can walk the player, so the
    // hands check needs the item handed over rather than picked up. Reports the held state
    // immediately afterwards, because "granted into the inventory" and "actually in the hand" are
    // different things and only the second one exercises the hand-parenting path.
    private static void GrantItem(IHostContext log, string match)
    {
        try
        {
            var player = ReelLogic.FindLocalPlayerEntity();
            if (player == null) { log?.LogWarning("[hands:grant] no local player"); return; }
            var arr = SceneCache.Scan(Il2CppType.Of<InventoryItemData>());
            var names = new List<string>();
            InventoryItemData chosen = null;
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<InventoryItemData>();
                if (d == null) continue;
                string n = "";
                try { n = d.name; } catch { }
                if (names.Count < 80 && !names.Contains(n)) names.Add(n);
                if (chosen == null && !string.IsNullOrEmpty(n) &&
                    n.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) chosen = d;
            }
            if (chosen == null)
            {
                log?.LogWarning($"[hands:grant] no InventoryItemData matching '{match}' ({arr.Length} loaded): " +
                                $"{string.Join(",", names.ToArray())}");
                return;
            }
            bool threw = false;
            string err = "";
            try { PlayerInventoryUtilities.AddItemOrDropIfNoSpace(player, chosen, 1); }
            catch (Exception e) { threw = true; err = e.GetType().Name + ": " + e.Message; }
            var hands = HandHelper.FindHands(player);
            var held = hands != null ? hands.CurrentHandheld : null;
            log?.LogInfo($"[hands:grant] item='{chosen.name}' threw={threw} {err} hands={(hands != null)} " +
                         $"held='{(held != null && held.gameObject != null ? held.gameObject.name : "-")}' item={held != null}");
        }
        catch (Exception e) { log?.LogWarning($"[hands:grant] failed: {e.Message}"); }
    }

    // The empty-handed control of the capture matrix: put the current handheld back on the ground
    // through the game's own drop, so the lease releases for the same reason a real player's would
    // (no item in hand), and report what the hands did.
    private static void DropItem(IHostContext log, string why)
    {
        try
        {
            var player = ReelLogic.FindLocalPlayerEntity();
            var hands = player != null ? HandHelper.FindHands(player) : null;
            var held = hands != null ? hands.CurrentHandheld : null;
            if (held == null) { log?.LogInfo($"[hands:drop] why={why} nothing held"); return; }
            bool ok = false;
            string err = "";
            try { ok = hands.TryDropCurrentHandheldOnGround(); }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            var after = hands != null ? hands.CurrentHandheld : null;
            log?.LogInfo($"[hands:drop] why={why} item='{(held.gameObject != null ? held.gameObject.name : "-")}' ok={ok} {err} " +
                         $"heldAfter={(after != null && after.gameObject != null ? after.gameObject.name : "-")} leased={_leases.Count}");
            Reconcile(log);
        }
        catch (Exception e) { log?.LogWarning($"[hands:drop] failed: {e.Message}"); }
    }

    private static bool Has(string text, string word) =>
        text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ParseValue(string text, string key)
    {
        int i = text.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int s = i + key.Length + 1, e = s;
        while (e < text.Length && text[e] != '\n' && text[e] != '\r' && text[e] != ' ' && text[e] != '\t') e++;
        return text.Substring(s, e - s).Trim();
    }

    // ================================================================== the lease

    // Every input the lease depends on, in one string: a change of any of them releases it so the
    // next reconcile takes a fresh snapshot. A held item that changes drops the lease too, because
    // the grip pose (one hand vs two) is per item.
    private static string KeyOf(Player player, Hands hands, Handheld held, POV pov)
    {
        var sb = new StringBuilder();
        sb.Append(pov == POV.ThirdPerson ? "3" : "1").Append('|');
        sb.Append(SahurModel.Active ? "c" : "-");
        sb.Append(SahurRig.Active ? "r" : "-");
        sb.Append(CustomEnhancements.CurrentSlug ?? "-").Append('|');
        sb.Append(held != null && held.gameObject != null ? held.gameObject.name : "-").Append('|');
        sb.Append(hands != null && hands.gameObject != null ? hands.gameObject.name : "-").Append('|');
        var model = SahurModel.LiveModel ?? SahurRig.LiveModel;
        sb.Append(model != null ? model.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "-");
        return sb.ToString();
    }

    private static void Reconcile(IHostContext log)
    {
        var player = ReelLogic.FindLocalPlayerEntity();
        var hands = player != null ? HandHelper.FindHands(player) : null;
        var held = hands != null ? hands.CurrentHandheld : null;
        var pov = HandHelper.Pov(player);
        bool custom = SahurModel.Active || SahurRig.Active;

        string key = KeyOf(player, hands, held, pov);
        if (key != _lastKey)
        {
            _lastKey = key;
            if (_leases.Count > 0) Release(log, "state-change");
        }

        // The lease only exists while a worn imported body has something in its hands. `lease=force`
        // is the measurement path: it takes the same renderers with empty hands so the A/B capture
        // ("hands retained" vs "hands hidden") does not depend on being able to hold an item.
        bool want = _leaseOn && custom && (held != null || _force);
        if (!want)
        {
            if (_leases.Count > 0) Release(log, custom ? "hands-empty" : "native-body");
            return;
        }
        if (_leases.Count > 0)
        {
            Reassert(log);
            return;
        }
        Take(log, why: "auto");
    }

    private static void TakeLease(IHostContext log, string why)
    {
        _leaseOn = true;
        _why = why;
        Release(log, "re-take");
        _lastKey = "";
        Take(log, why);
    }

    private static void Take(IHostContext log, string why)
    {
        var player = ReelLogic.FindLocalPlayerEntity();
        if (player == null) { log.LogInfo("[hands:lease] no local player"); return; }
        var hands = HandHelper.FindHands(player);
        var held = hands != null ? hands.CurrentHandheld : null;
        var pov = HandHelper.Pov(player);
        var item = held != null ? held.gameObject : null;

        var candidates = new List<Renderer>();
        string source;
        if (pov == POV.ThirdPerson)
        {
            var model = SahurModel.LiveModel ?? SahurRig.LiveModel ?? HandHelper.ThirdModel(player);
            source = "third:" + HandHelper.PathOf(model != null ? model.transform : null);
            HandHelper.CollectArms(model != null ? model.transform : null, _mode, candidates);
        }
        else
        {
            var model = HandHelper.FirstModel(player);
            source = "first:" + HandHelper.PathOf(model != null ? model.transform : null);
            HandHelper.CollectArms(model != null ? model.transform : null, _mode, candidates);
        }

        int taken = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];
            if (r == null) continue;
            bool on = true, off = false;
            int layer = 0;
            try { on = r.enabled; off = r.forceRenderingOff; layer = r.gameObject.layer; } catch { continue; }
            _leases.Add(new Lease { R = r, HadEnabled = on, HadForceOff = off, Layer = layer, Name = r.gameObject.name });
            try { r.enabled = true; r.forceRenderingOff = false; } catch { }
            taken++;
        }
        _takes++;
        // Record the key this lease was taken for, or the very next reconcile would see "the state
        // changed" (empty key) and release it again - the release/re-take churn seen in [hands:lease].
        _lastKey = KeyOf(ReelLogic.FindLocalPlayerEntity(), hands, held, pov);
        log.LogInfo($"[hands:lease] took {taken} renderer(s) from {source} mode={_mode} pov={pov} why={why} " +
                    $"held='{(item != null ? item.name : "-")}' itemUnder='{HandHelper.ParentChain(item != null ? item.transform : null, 4)}'");
        foreach (var l in _leases)
            log.LogInfo($"[hands:lease]   '{l.Name}' layer={l.Layer} was(enabled={l.HadEnabled},forceOff={l.HadForceOff})");
    }

    private static void Reassert(IHostContext log)
    {
        int changed = 0;
        for (int i = 0; i < _leases.Count; i++)
        {
            var l = _leases[i];
            if (l.R == null) continue;
            try
            {
                if (!l.R.enabled) { l.R.enabled = true; changed++; }
                if (l.R.forceRenderingOff) { l.R.forceRenderingOff = false; changed++; }
                l.Frames++;
            }
            catch { }
        }
        if (changed > 0) log.LogInfo($"[hands:lease] re-asserted {changed} write(s) on {_leases.Count} renderer(s)");
    }

    private static void Release(IHostContext log, string why)
    {
        if (_leases.Count == 0) return;
        int n = 0;
        for (int i = 0; i < _leases.Count; i++)
        {
            var l = _leases[i];
            if (l.R == null) continue;
            try
            {
                int layer = l.R.gameObject.layer;
                if (layer != l.Layer) { try { l.R.gameObject.layer = l.Layer; } catch { } }
                l.R.enabled = l.HadEnabled;
                l.R.forceRenderingOff = l.HadForceOff;
                n++;
            }
            catch { }
        }
        log.LogInfo($"[hands:lease] released {n} renderer(s) (why={why}); enabled/forceOff restored to the snapshot");
        _leases.Clear();
        _releases++;
    }

    internal static void Dispose(IHostContext log)
    {
        try { Release(log, "dispose"); } catch { }
        _leaseOn = false;
    }

    private static void DumpLease(IHostContext log)
    {
        log.LogInfo($"[hands:lease] dump on={_leaseOn} active={Active} count={_leases.Count} mode={_mode} " +
                    $"takes={_takes} releases={_releases} key='{_lastKey}'");
        for (int i = 0; i < _leases.Count; i++)
        {
            var l = _leases[i];
            if (l.R == null) { log.LogInfo($"[hands:lease]   [{i}] <destroyed>"); continue; }
            bool on = false, off = false;
            try { on = l.R.enabled; off = l.R.forceRenderingOff; } catch { }
            log.LogInfo($"[hands:lease]   [{i}] '{l.Name}' layer={l.R.gameObject.layer} enabled={on} forceOff={off} " +
                        $"snapshot(enabled={l.HadEnabled},forceOff={l.HadForceOff},layer={l.Layer}) frames={l.Frames}");
        }
    }

    // ================================================================== census / probe

    // The discovery record. One block per view: every renderer under that view's model with the
    // three flags that decide whether it can be seen, plus the held item's parent chain and where
    // its bounds sit relative to the native hand meshes. `grip=` is the measured answer to "are
    // there hands around the item": a visible native arm renderer whose bounds contain the item.
    internal static void Census(IHostContext log, string why)
    {
        var player = ReelLogic.FindLocalPlayerEntity();
        if (player == null) { log.LogInfo($"[hands:census] why={why} no local player"); return; }
        var hands = HandHelper.FindHands(player);
        var held = hands != null ? hands.CurrentHandheld : null;
        var item = held != null ? held.gameObject : null;
        var pov = HandHelper.Pov(player);

        var third = HandHelper.ThirdModel(player);
        var first = HandHelper.FirstModel(player);

        log.LogInfo($"[hands:census] why={why} pov={pov} custom=(classic={SahurModel.Active},rigged={SahurRig.Active}," +
                    $"slug={CustomEnhancements.CurrentSlug ?? "-"}) held='{(item != null ? item.name : "-")}' " +
                    $"handsObject='{(hands != null && hands.gameObject != null ? hands.gameObject.name : "-")}' " +
                    $"third='{HandHelper.PathOf(third != null ? third.transform : null)}' " +
                    $"first='{HandHelper.PathOf(first != null ? first.transform : null)}'");

        // The held item's visible model, through the game's own property component (findable only via
        // the Handheld), is what every later test measures.
        Transform model = HandHelper.ModelTransformOf(held);
        if (item != null || model != null)
        {
            log.LogInfo($"[hands:census] item path='{HandHelper.PathOf(item != null ? item.transform : null)}' " +
                        $"chain='{HandHelper.ParentChain(item != null ? item.transform : null, 6)}' " +
                        $"model='{HandHelper.PathOf(model)}' bounds={HandHelper.BoundsText(model != null ? model.gameObject : item)}");
            HandHelper.ReportItemRenderers(log, item, model);
        }
        HandHelper.ReportModelSearch(log, player);
        if (held != null)
            HandHelper.ReportItemState(log, held, third != null ? third.transform : null,
                                               first != null ? first.transform : null);

        HandHelper.ReportView(log, "third", third != null ? third.transform : null, item, player);
        HandHelper.ReportView(log, "first", first != null ? first.transform : null, item, player);
        HandHelper.ReportGrip(log, item, model, pov, _mode);
    }
}

// Shared scene reading for the hands work. Every helper here is read-only: it never writes a
// renderer, a layer or a mask, so a census can be taken at any point of any pass.
internal static class HandHelper
{
    internal static Hands FindHands(Player player)
    {
        try
        {
            var arr = player.GetComponentsInChildren<Hands>(true);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].gameObject != null && arr[i].gameObject.activeInHierarchy) return arr[i];
            if (arr.Length > 0) return arr[0];
        }
        catch { }
        return null;
    }

    internal static POV Pov(Player player)
    {
        try
        {
            var c = player.GetComponentsInChildren<PlayerPOVController>(true);
            for (int i = 0; i < c.Length; i++)
                if (c[i] != null) return c[i].CurrentPOV;
        }
        catch { }
        return POV.None;
    }

    internal static ThirdViewPlayerModel ThirdModel(Player player)
    {
        // A map's player hierarchy is not the lobby's: the same view model hangs under a deeper,
        // differently named root, so a path test against the lobby's shape is the wrong instrument.
        // The local player's own children are authoritative in both scenes - ask them first and keep
        // the scene scan as the fallback for a census taken without a resolved player.
        try
        {
            if (player != null)
            {
                var own = player.GetComponentsInChildren<ThirdViewPlayerModel>(true);
                ThirdViewPlayerModel idle = null;
                for (int i = 0; i < own.Length; i++)
                {
                    var m = own[i];
                    if (m == null || m.transform == null) continue;
                    if (!InScene(m.transform)) continue;
                    if (m.gameObject.activeInHierarchy) return m;
                    if (idle == null) idle = m;
                }
                if (idle != null) return idle;
            }
        }
        catch { }
        // Scan fallback, used only when the player is unknown. `HasNativeBody` used to be a hard
        // filter here, which is why a map returned none: that body is one `Body_Optimized` mesh with
        // no `_arm_` segments, so the arm test rejected the real player body. It is a preference now;
        // the `Owner: 0` test already keeps the wardrobe's mannequins out.
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            ThirdViewPlayerModel idle = null;
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.transform == null) continue;
                if (!HandHelper.InScene(m.transform)) continue;
                if (HandHelper.PathOf(m.transform).IndexOf("Owner: 0", StringComparison.Ordinal) < 0) continue;
                if (HandHelper.HasNativeBody(m.transform)) return m;
                if (idle == null) idle = m;
            }
            if (idle != null) return idle;
        }
        catch { }
        return null;
    }

    internal static FirstViewPlayerModel FirstModel(Player player)
    {
        try
        {
            if (player != null)
            {
                var own = player.GetComponentsInChildren<FirstViewPlayerModel>(true);
                FirstViewPlayerModel idle = null;
                for (int i = 0; i < own.Length; i++)
                {
                    var m = own[i];
                    if (m == null || m.transform == null) continue;
                    if (!InScene(m.transform)) continue;
                    if (m.gameObject.activeInHierarchy) return m;
                    if (idle == null) idle = m;
                }
                if (idle != null) return idle;
            }
        }
        catch { }
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<FirstViewPlayerModel>());
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i].TryCast<FirstViewPlayerModel>();
                if (m == null || m.transform == null) continue;
                if (!HandHelper.InScene(m.transform)) continue;
                if (HandHelper.PathOf(m.transform).IndexOf("Owner: 0", StringComparison.Ordinal) < 0) continue;
                return m;
            }
        }
        catch { }
        return null;
    }

    internal static bool InScene(Transform t)
    {
        var s = t.gameObject != null ? t.gameObject.scene.name : null;
        return !string.IsNullOrEmpty(s);
    }

    // The game's own answer to "where is the model this handheld puts in the player's hand".
    internal static Transform ModelTransformOf(Handheld held)
    {
        if (held == null) return null;
        try
        {
            var mp = held.GetProperties<HandheldModelProperties>(false);
            if (mp == null) return null;
            return mp.ModelTransform;
        }
        catch { return null; }
    }

    internal static bool HasNativeBody(Transform t)
    {
        try
        {
            var cs = t.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < cs.Length; i++)
            {
                var r = cs[i]?.TryCast<Renderer>();
                if (r == null || r.gameObject == null) continue;
                string n = r.gameObject.name ?? "";
                if (IsArmName(n)) return true;
            }
        }
        catch { }
        return false;
    }

    internal static bool IsArmName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (n.IndexOf("arm", StringComparison.Ordinal) < 0) return false;
        return n.IndexOf("alarm", StringComparison.Ordinal) < 0;
    }

    internal static bool Matches(string name, DefaultHands.Mode mode)
    {
        string n = (name ?? "").ToLowerInvariant();
        // First-view hands are their OWN mesh (`Hands_01_Mesh`, layer 24) - not an `_Arm_` segment -
        // so every mode has to treat a hand mesh as in scope, or the lease would take nothing in
        // first person.
        if (IsHandMesh(n)) return true;
        if (n.IndexOf("arm", StringComparison.Ordinal) < 0) return false;
        switch (mode)
        {
            case DefaultHands.Mode.Hands: return n.EndsWith("_arm_3");
            case DefaultHands.Mode.Forearm: return n.EndsWith("_arm_2") || n.EndsWith("_arm_3");
            default: return n.IndexOf("_arm_", StringComparison.Ordinal) >= 0;
        }
    }

    internal static bool IsHandMesh(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        return n.IndexOf("hand", StringComparison.Ordinal) >= 0 && n.IndexOf("handheld", StringComparison.Ordinal) < 0;
    }

    // The arm meshes under one view's model, in hierarchy order. Never includes our own geometry:
    // nothing the mod builds is named with an arm token, and the roster check keeps it that way.
    internal static void CollectArms(Transform root, DefaultHands.Mode mode, List<Renderer> into)
    {
        if (root == null || into == null) return;
        try
        {
            var cs = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < cs.Length; i++)
            {
                var r = cs[i]?.TryCast<Renderer>();
                if (r == null || r.gameObject == null) continue;
                if (CosmeticMarkers.IsCosmetic(r)) continue;
                if (ModelRoster.IsOurObjectName(r.gameObject.name ?? "")) continue;
                if (!Matches(r.gameObject.name, mode)) continue;
                if (!into.Contains(r)) into.Add(r);
            }
        }
        catch { }
    }

    internal static string PathOf(Transform t)
    {
        if (t == null) return "none";
        var sb = new StringBuilder(t.name);
        for (int i = 0; t.parent != null && i < 12; i++)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
    }

    internal static string ParentChain(Transform t, int depth)
    {
        if (t == null) return "none";
        var sb = new StringBuilder();
        for (int i = 0; t != null && i < depth; i++, t = t.parent)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append(t.name);
        }
        return sb.ToString();
    }

    internal static string BoundsText(GameObject go)
    {
        try
        {
            var rs = go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            if (rs == null || rs.Length == 0) return "none";
            var b = rs[0].TryCast<Renderer>().bounds;
            for (int i = 1; i < rs.Length; i++) { var r = rs[i].TryCast<Renderer>(); if (r != null) b.Encapsulate(r.bounds); }
            return $"c=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}) e=({b.extents.x:F3},{b.extents.y:F3},{b.extents.z:F3})";
        }
        catch (Exception e) { return "<" + e.Message + ">"; }
    }

    // The inventory-side `Handheld` object never carries the visible model: the game parents the
    // item's model under the hand bone of the active view's skeleton (measured: `.../hand_r/First
    // Person Item Parent/Model`) and hands that model over through HandheldModelProperties. Counting
    // renderers on the logic holder is what reported 0 forever, so the count comes from the model and
    // the holder's own count is printed beside it instead of replacing it.
    internal static void ReportItemRenderers(IHostContext log, GameObject item, Transform model)
    {
        try
        {
            int holder = -1;
            try { holder = item.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true).Length; } catch { }
            int on = 0;
            var sb = new StringBuilder();
            var src = model != null ? model : item.transform;
            var rs = src.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; rs != null && i < rs.Length && i < 8; i++)
            {
                var r = rs[i].TryCast<Renderer>();
                if (r == null || r.gameObject == null) continue;
                bool en = false, off = false;
                try { en = r.enabled; off = r.forceRenderingOff; } catch { }
                if (en && !off) on++;
                sb.Append($"'{r.gameObject.name}'(layer={r.gameObject.layer},enabled={en},forceOff={off}) ");
            }
            log.LogInfo($"[hands:census] item renderers={(rs != null ? rs.Length : 0)} visible={on} " +
                        $"source='{(model != null ? "model" : "holder")}' holderRenderers={holder} " +
                        $"path='{PathOf(src)}' [{sb.ToString().Trim()}]");
        }
        catch (Exception e) { log.LogWarning($"[hands:census] item renderers failed: {e.Message}"); }
    }

    // The inventory-side `Handheld` object is the item's logic holder; the MODEL the player sees is
    // parented by the game (ModelInHandsParentingActionProcessor -> GetHandheldModelParent) and is
    // described by the handheld's own HandheldModelProperties. Reading renderers off the logic
    // holder reported 0 forever, so the census reads the model through the game's property component
    // and, when that is empty too, prints the node tree and every in-hands node in both views - that
    // is what says whether the item is merely in the inventory or actually taken out.
    internal static void ReportItemState(IHostContext log, Handheld held, Transform third, Transform first)
    {
        try
        {
            string state = "-", major = "-", holding = "-";
            bool ready = false;
            try { state = held.CurrentState.ToString(); } catch { }
            try { ready = held.IsReadyToUse; } catch { }
            try { major = PathOf(held.MajorTransform); } catch { }
            try
            {
                var h = held.HoldingHands;
                holding = h != null ? "IReadOnlyHands" : "-";
            }
            catch { }
            log.LogInfo($"[hands:census] item state={state} readyToUse={ready} holdingHands='{holding}' major='{major}'");

            HandheldModelProperties mp = null;
            try { mp = held.GetProperties<HandheldModelProperties>(false); }
            catch (Exception e) { log.LogInfo($"[hands:census] item properties threw {e.GetType().Name}: {e.Message}"); }
            if (mp == null)
            {
                log.LogInfo("[hands:census] item properties=none (no HandheldModelProperties)");
            }
            else
            {
                Transform mt = null;
                try { mt = mp.ModelTransform; } catch { }
                int n = 0;
                string names = "";
                try
                {
                    if (mt != null)
                    {
                        var rs = mt.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                        n = rs != null ? rs.Length : 0;
                        var sb = new StringBuilder();
                        for (int i = 0; rs != null && i < rs.Length && i < 6; i++)
                        {
                            var r = rs[i].TryCast<Renderer>();
                            if (r == null || r.gameObject == null) continue;
                            bool on = false; try { on = r.enabled; } catch { }
                            sb.Append($"'{r.gameObject.name}'(enabled={on}) ");
                        }
                        names = sb.ToString().Trim();
                    }
                }
                catch { }
                log.LogInfo($"[hands:census] item modelTransform='{PathOf(mt)}' modelRenderers={n} [{names}]");
            }

            var item = held.gameObject;
            if (item != null)
            {
                var sb = new StringBuilder();
                int shown = 0;
                DumpNodes(item.transform, 0, 3, ref shown, sb);
                log.LogInfo($"[hands:census] item nodes({shown}) {sb}");
            }
        }
        catch (Exception e) { log.LogWarning($"[hands:census] item state failed: {e.Message}"); }

        // The in-hands node: whatever the game parented the model to, it is a descendant of a view's
        // model root. Name it and measure it, so "no renderers" cannot be confused with "no model".
        try
        {
            log.LogInfo($"[hands:census] inhands third: {InHandsNodes(third)}");
            log.LogInfo($"[hands:census] inhands first: {InHandsNodes(first)}");
        }
        catch (Exception e) { log.LogWarning($"[hands:census] inhands scan failed: {e.Message}"); }
    }

    private static string InHandsNodes(Transform root)
    {
        if (root == null) return "root=none";
        var sb = new StringBuilder();
        int shown = 0;
        try
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; all != null && i < all.Length && shown < 8; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = (t.name ?? "").ToLowerInvariant();
                if (n.IndexOf("handheld", StringComparison.Ordinal) < 0) continue;
                int rn = 0;
                bool anyOn = false;
                try
                {
                    var rs = t.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                    rn = rs != null ? rs.Length : 0;
                    for (int k = 0; rs != null && k < rs.Length; k++)
                    {
                        var r = rs[k].TryCast<Renderer>();
                        if (r != null && r.enabled) { anyOn = true; break; }
                    }
                }
                catch { }
                sb.Append($"'{PathOf(t)}' renderers={rn} anyEnabled={anyOn} active={t.gameObject.activeInHierarchy} | ");
                shown++;
            }
        }
        catch (Exception e) { return "<" + e.Message + ">"; }
        return shown == 0 ? "none" : sb.ToString().Trim();
    }

    private static void DumpNodes(Transform t, int depth, int maxDepth, ref int shown, StringBuilder sb)
    {
        if (t == null || depth > maxDepth || shown >= 16) return;
        try
        {
            bool hasRenderer = false;
            try { hasRenderer = t.gameObject.GetComponent<Renderer>() != null; } catch { }
            sb.Append(new string(' ', depth * 2)).Append(t.name);
            if (hasRenderer) sb.Append('*');
            if (!t.gameObject.activeSelf) sb.Append("(off)");
            sb.Append(" | ");
            shown++;
            int kids = 0;
            try { kids = t.childCount; } catch { }
            for (int i = 0; i < kids && shown < 16; i++) DumpNodes(t.GetChild(i), depth + 1, maxDepth, ref shown, sb);
        }
        catch { }
    }

    // Which of the two resolution routes found the views. A map resolves through the player's own
    // children; the scene scan is the fallback, and its per-candidate filter results are printed so a
    // failure says which test rejected the body instead of just 'none'.
    internal static void ReportModelSearch(IHostContext log, Player player)
    {
        int ownThird = -1, ownFirst = -1;
        try { if (player != null) ownThird = player.GetComponentsInChildren<ThirdViewPlayerModel>(true).Length; } catch { }
        try { if (player != null) ownFirst = player.GetComponentsInChildren<FirstViewPlayerModel>(true).Length; } catch { }
        log.LogInfo($"[hands:census] search playerChildren third={ownThird} first={ownFirst}");

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            int shown = 0;
            for (int i = 0; arr != null && i < arr.Length && shown < 6; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.transform == null) continue;
                string path = PathOf(m.transform);
                log.LogInfo($"[hands:census] scan3 inScene={InScene(m.transform)} owner0={path.IndexOf("Owner: 0", StringComparison.Ordinal) >= 0} " +
                            $"native={HasNativeBody(m.transform)} active={m.gameObject.activeInHierarchy} path='{path}'");
                shown++;
            }
            if (shown == 0) log.LogInfo("[hands:census] scan3 none");
        }
        catch (Exception e) { log.LogWarning($"[hands:census] scan3 failed: {e.Message}"); }

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<FirstViewPlayerModel>());
            int shown = 0;
            for (int i = 0; arr != null && i < arr.Length && shown < 6; i++)
            {
                var m = arr[i].TryCast<FirstViewPlayerModel>();
                if (m == null || m.transform == null) continue;
                string path = PathOf(m.transform);
                log.LogInfo($"[hands:census] scan1 inScene={InScene(m.transform)} owner0={path.IndexOf("Owner: 0", StringComparison.Ordinal) >= 0} " +
                            $"active={m.gameObject.activeInHierarchy} path='{path}'");
                shown++;
            }
            if (shown == 0) log.LogInfo("[hands:census] scan1 none");
        }
        catch (Exception e) { log.LogWarning($"[hands:census] scan1 failed: {e.Message}"); }
    }

    internal static void ReportView(IHostContext log, string tag, Transform root, GameObject item, Player player)
    {
        if (root == null) { log.LogInfo($"[hands:census] {tag} model=none"); return; }
        Renderer[] rs = null;
        try { rs = root.GetComponentsInChildren<Renderer>(true); } catch { }
        log.LogInfo($"[hands:census] {tag} model renderers={(rs != null ? rs.Length : 0)} layerCensus=see-below");
        int shown = 0;
        for (int i = 0; rs != null && i < rs.Length && shown < 40; i++)
        {
            var r = rs[i];
            if (r == null || r.gameObject == null) continue;
            bool on = false, off = false;
            string mesh = "-";
            try
            {
                on = r.enabled; off = r.forceRenderingOff;
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null) mesh = smr.sharedMesh.name ?? "-";
            }
            catch { }
            var b = r.bounds;
            log.LogInfo($"[hands:census] {tag}[{i}] '{r.gameObject.name}' layer={r.gameObject.layer} enabled={on} " +
                        $"forceOff={off} arm={IsArmName(r.gameObject.name)} mesh='{mesh}' " +
                        $"c=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}) e=({b.extents.x:F3},{b.extents.y:F3},{b.extents.z:F3})");
            shown++;
        }
        // The hand bones the item is parented to, in this view's skeleton.
        foreach (var bone in new[] { "hand_r", "hand_l", "lowerarm_r" })
        {
            var t = FindBone(root, bone);
            if (t == null) { log.LogInfo($"[hands:census] {tag} bone '{bone}'=none"); continue; }
            log.LogInfo($"[hands:census] {tag} bone '{bone}' world=({t.position.x:F3},{t.position.y:F3},{t.position.z:F3}) " +
                        $"local=({t.localPosition.x:F3},{t.localPosition.y:F3},{t.localPosition.z:F3})");
        }
    }

    internal static Transform FindBone(Transform root, string name)
    {
        if (root == null) return null;
        try
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && string.Equals(all[i].name, name, StringComparison.OrdinalIgnoreCase)) return all[i];
        }
        catch { }
        return null;
    }

    // The measured answer: for each arm renderer the lease would take, is it visible, and does its
    // bounds contain the held item's centre (within 5 cm)? "grip=1" means the player sees native
    // hands around the item right now.
    internal static void ReportGrip(IHostContext log, GameObject item, Transform model, POV pov, DefaultHands.Mode mode)
    {
        Transform root = null;
        if (pov == POV.ThirdPerson) { try { var p = ReelLogic.FindLocalPlayerEntity(); var m = ThirdModel(p); root = m != null ? m.transform : null; } catch { } }
        if (root == null) { try { var p = ReelLogic.FindLocalPlayerEntity(); var m = FirstModel(p); root = m != null ? m.transform : null; } catch { } }
        if (root == null || (item == null && model == null)) { log.LogInfo($"[hands:census] grip=n/a root={(root != null)} item={(item != null)} model={(model != null)}"); return; }

        Bounds ib;
        var bsrc = model != null ? model : item.transform;
        try
        {
            var rs = bsrc.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            if (rs == null || rs.Length == 0) { log.LogInfo($"[hands:census] grip=n/a no renderer on '{PathOf(bsrc)}'"); return; }
            ib = rs[0].TryCast<Renderer>().bounds;
            for (int i = 1; i < rs.Length; i++) { var r = rs[i].TryCast<Renderer>(); if (r != null) ib.Encapsulate(r.bounds); }
        }
        catch (Exception e) { log.LogInfo($"[hands:census] grip=n/a bounds <{e.Message}>"); return; }

        var cand = new List<Renderer>();
        CollectArms(root, mode, cand);
        int visible = 0, contact = 0;
        var sb = new StringBuilder();
        foreach (var r in cand)
        {
            bool on = false, off = false;
            Bounds rb = default;
            try { on = r.enabled; off = r.forceRenderingOff; rb = r.bounds; } catch { }
            rb.Expand(0.05f);
            bool hit = rb.Contains(ib.center) || rb.Intersects(ib);
            if (on && !off) visible++;
            if (on && !off && hit) contact++;
            sb.Append($"'{r.gameObject.name}'[vis={(on && !off)} hit={hit} d={(r.bounds.center - ib.center).magnitude:F3}] ");
        }
        log.LogInfo($"[hands:census] grip={(contact > 0 ? 1 : 0)} arms={cand.Count} visible={visible} contact={contact} " +
                    $"pov={pov} mode={mode} itemFrom='{(model != null ? "model" : "holder")}' itemBounds=c=({ib.center.x:F3},{ib.center.y:F3},{ib.center.z:F3}) " +
                    $"[{sb.ToString().Trim()}]");
    }
}
