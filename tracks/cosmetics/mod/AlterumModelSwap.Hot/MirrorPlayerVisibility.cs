using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace AlterumModelSwap.Hot;

// Managed, hot-owned state only. No injected component or camera callbacks.
//
// Evidence (runtime, generation 3 logs): the local third-person body's 14 renderers sit on
// TV_PlayerModelMesh and are disabled by two live Gameplay.PlayerSystem.SimpleRenderingController
// components (Meshes/Other Meshes: 11 cached, Meshes/Head Extras Meshes: 3 cached). Each has one
// entry in RenderingController._disableKeys, so the native hide runs
// RenderingController.DisableRendering(key) -> RenderersRenderingController.DisableRenderers().
// This module removes those exact (controller, key) pairs, suppresses only their re-application,
// leaves every other key/reason on the controller alone, isolates every body renderer (active or
// not) on one mirror-only layer and restores every snapshot on Dispose.
internal sealed class MirrorPlayerVisibility : IDisposable
{
    private readonly IHostContext _log;
    private readonly string _id = "com.user0.alterum.mirror." + Guid.NewGuid().ToString("N");
    private Harmony _harmony;
    private static MirrorPlayerVisibility _current;
    private Transform _owner;
    private ThirdViewPlayerModel _body;
    private int _layer;
    private bool _armed, _dirty, _disposed, _removingInitialKeys;
    private readonly List<RendererState> _renderers = new();
    private readonly List<(LODGroup group, bool enabled)> _lods = new();
    private readonly List<ControllerState> _controllers = new();
    private readonly List<Binding> _bindings = new();
    private int _skippedKeys, _skippedDisableRenderers, _passedDisableRenderers, _passedKeys, _foreignKeys, _skippedCosmetics;

    private sealed class ControllerState
    {
        public RenderersRenderingController Controller;
        public Il2CppReferenceArray<Renderer> Cached;
    }

    private sealed class Binding
    {
        public ControllerState Controller;
        public Il2CppSystem.Object Key;
        // Desired native key membership (Restore: the hide existed when we bound, so teardown must
        // put it back) tracked separately from the current one (Present: in _disableKeys now).
        // A suppressed Disable changes neither; a genuine Enable cancels Restore.
        public bool Restore, Present, Suppressed;
    }

    private sealed class RendererState
    {
        public Renderer Renderer;
        public int Layer;
        public bool Enabled, ForceOff, Offscreen;
        public ShadowCastingMode Shadows;
        // Isolated: moved to the mirror-only layer. Drive: we own its enabled state. Selected:
        // the visible goal inside Drive (LOD0 winners only).
        public bool Isolated, Drive, Selected;
        public RendererState(Renderer r)
        {
            Renderer = r; Layer = r.gameObject.layer; Enabled = r.enabled; ForceOff = r.forceRenderingOff;
            Shadows = r.shadowCastingMode;
            var sk = r.TryCast<SkinnedMeshRenderer>();
            Offscreen = sk != null && sk.updateWhenOffscreen;
        }
    }

    internal MirrorPlayerVisibility(IHostContext log) => _log = log;
    private static IntPtr Ptr(Il2CppObjectBase obj) => obj == null ? IntPtr.Zero : obj.Pointer;

    // The live generation, for the cosmetic runtime's measured hide gate (same assembly).
    internal static MirrorPlayerVisibility Current => _current;

    internal static bool Under(Transform child, Transform parent)
    {
        if (parent == null) return false;
        for (int i = 0; child != null && i < 64; i++, child = child.parent)
            if (Ptr(child) == Ptr(parent)) return true;
        return false;
    }

    internal static string Hierarchy(Transform t)
    {
        if (t == null) return "null";
        var s = t.name;
        for (int i = 0; i < 8 && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    internal void Attach(Transform localPlayer, int bodyLayer)
    {
        _owner = localPlayer;
        _layer = bodyLayer;
        Rebind();
    }

    internal void Update()
    {
        // Called in the runtime's Update, after SetupModel's native callbacks finish.
        if (_dirty && !_disposed) { _dirty = false; Rebind(); }
    }

    internal void Rebind()
    {
        if (_disposed) return;
        Disarm();
        RestoreBinding();
        try
        {
            var models = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            ThirdViewPlayerModel inactive = null;
            for (int i = 0; i < models.Length; i++)
            {
                var model = models[i].TryCast<ThirdViewPlayerModel>();
                if (model == null || !Under(model.transform, _owner)) continue;
                if (model.gameObject.activeInHierarchy) { _body = model; break; }
                if (inactive == null) inactive = model;
            }
            if (_body == null)
            {
                if (inactive != null) _log.LogWarning($"[mirror:body-inactive] owner='{_owner.name}' ThirdViewPlayerModel '{inactive.name}' exists but its GameObject is inactive; not force-activating game state");
                else _log.LogWarning($"[mirror:body-unavailable] no ThirdViewPlayerModel under local owner '{_owner.name}'; no mutation");
                return;
            }
            var bodyRenderers = _body.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            var bodyPointers = new HashSet<IntPtr>();
            int cosmeticRenderers = 0;
            var cosmeticNames = new StringBuilder();
            for (int i = 0; i < bodyRenderers.Length; i++)
            {
                var r = bodyRenderers[i].TryCast<Renderer>();
                if (r == null) continue;
                // Mod cosmetics carry their own marker and are never part of the body sweep: the
                // mirror must not relayer/drive them (they render in both masks).
                if (CosmeticMarkers.IsCosmetic(r))
                {
                    cosmeticRenderers++;
                    if (cosmeticRenderers <= 16) cosmeticNames.Append($"'{r.name}'@L{r.gameObject.layer}; ");
                    continue;
                }
                bodyPointers.Add(Ptr(r));
            }
            if (cosmeticRenderers > 0)
                _log.LogInfo($"[mirror:body-cosmetic-skipped] generation={_id} renderers={cosmeticRenderers} of {bodyRenderers.Length} carry the cosmetic marker; " +
                             $"bodyRenderers={bodyRenderers.Length - cosmeticRenderers} excluded={cosmeticNames}");

            // Discover the live body controllers from the model's own aggregate plus a scene scan.
            var seen = new HashSet<IntPtr>();
            void Accept(RenderingController rc, string origin)
            {
                var rrc = rc == null ? null : rc.TryCast<RenderersRenderingController>();
                if (rrc == null || !seen.Add(Ptr(rrc))) return;
                Il2CppReferenceArray<Renderer> cached = null;
                try { rrc.TryGetRenderers(out cached); } catch (Exception e) { _log.LogWarning($"[mirror:body-controller] TryGetRenderers failed: {e.Message}"); }
                int cachedBody = 0;
                if (cached != null)
                    for (int j = 0; j < cached.Length; j++)
                        if (cached[j] != null && bodyPointers.Contains(Ptr(cached[j]))) cachedBody++;
                int queueBody = 0;
                var queue = rrc._cachedActiveModelRenderers;
                if (queue != null)
                    foreach (var qr in queue)
                        if (qr != null && bodyPointers.Contains(Ptr(qr))) queueBody++;
                bool underBody = Under(rrc.transform, _body.transform) || Under(rrc.transform, _owner);
                var state = new ControllerState { Controller = rrc, Cached = cached };
                _log.LogInfo($"[mirror:body-controller-scan] origin={origin} native={rrc.GetIl2CppType().FullName} path='{Hierarchy(rrc.transform)}' underBody={underBody} keys={rrc._disableKeys?.Count} renderingEnabled={rrc.IsRenderingEnabled} cached={(cached == null ? -1 : cached.Length)} cachedBody={cachedBody} cachedActive={(queue == null ? -1 : queue.Count)} cachedActiveBody={queueBody}");
                if (!(underBody || cachedBody > 0 || queueBody > 0)) return;
                _controllers.Add(state);
            }
            void ScanMulti(MultiRenderingController m, string origin)
            {
                if (m == null) { _log.LogInfo($"[mirror:body-multi] origin={origin} null"); return; }
                var subs = m._subRenderingControllers;
                var refs = m._subRenderingControllersRefs;
                _log.LogInfo($"[mirror:body-multi] origin={origin} object='{Hierarchy(m.transform)}' subs={(subs == null ? -1 : subs.Count)} refs={(refs == null ? -1 : refs.Count)}");
                if (subs != null)
                    foreach (var kv in subs) Accept(kv.Value, origin + ":" + kv.Key);
                if (refs != null)
                    for (int i = 0; i < refs.Count; i++) Accept(refs[i].RenderingController, origin + ":ref" + i);
            }
            try { ScanMulti(_body.MultiRenderingController, "body"); }
            catch (Exception e) { _log.LogWarning($"[mirror:body-multi] body getter failed: {e.Message}"); }
            try
            {
                var modelRefs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelReferences>());
                for (int i = 0; i < modelRefs.Length; i++)
                {
                    var reference = modelRefs[i].TryCast<PlayerModelReferences>();
                    if (reference == null || !Under(reference.transform, _owner)) continue;
                    _log.LogInfo($"[mirror:body-refs] path='{Hierarchy(reference.transform)}' first=0x{Ptr(reference._firstPersonModelRenderingController):X} third=0x{Ptr(reference._thirdPersonModelRenderingController):X}");
                    ScanMulti(reference._thirdPersonModelRenderingController, "refs.third");
                }
            }
            catch (Exception e) { _log.LogWarning($"[mirror:body-refs] scan failed: {e.Message}"); }
            if (_controllers.Count == 0)
            {
                // Last resort: any renderer controller already caching one of this body's renderers.
                var rrcs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<RenderersRenderingController>());
                for (int i = 0; i < rrcs.Length; i++)
                {
                    var rrc = rrcs[i].TryCast<RenderersRenderingController>();
                    if (rrc == null) continue;
                    var queue = rrc._cachedActiveModelRenderers;
                    if (queue == null) continue;
                    bool owns = false;
                    foreach (var qr in queue) if (qr != null && bodyPointers.Contains(Ptr(qr))) owns = true;
                    if (owns) Accept(rrc, "scene");
                }
            }
            if (_controllers.Count == 0)
            {
                _log.LogWarning($"[mirror:body-controller-unavailable] owner='{_owner.name}' body='{_body.name}' renderers={bodyRenderers.Length}; no body renderer controller found, body unchanged");
                return;
            }

            // Bind only the keys identified as this owner's own hide. Every other key stays in the
            // controller, so death/cutscene/equipment hides keep working and keep hiding the body.
            foreach (var controller in _controllers)
            {
                var keys = controller.Controller._disableKeys;
                if (keys == null || keys.Count == 0)
                {
                    _log.LogInfo($"[mirror:body-key] controller=0x{Ptr(controller.Controller):X} no disable keys; renderers already visible");
                    continue;
                }
                var live = new List<Il2CppSystem.Object>();
                foreach (var key in keys) live.Add(key);
                // Evidence-backed fallback: a lone key on a body controller is the owner's own hide.
                bool lone = live.Count == 1;
                foreach (var key in live)
                {
                    var proxy = key == null ? null : key.TryCast<ProxyRenderingController>();
                    bool forwards = proxy != null && Ptr(proxy._renderingController) == Ptr(controller.Controller);
                    bool ownerHide = forwards || lone;
                    _log.LogInfo($"[mirror:body-key] controller=0x{Ptr(controller.Controller):X} key=0x{Ptr(key):X} type={key?.GetIl2CppType().FullName} proxyTarget=0x{Ptr(proxy?._renderingController):X} ownerHide={ownerHide} forwards={forwards} lone={lone}");
                    if (!ownerHide) { _foreignKeys++; continue; }
                    _bindings.Add(new Binding { Controller = controller, Key = key, Present = true });
                }
            }
            if (!InstallPatches()) { _controllers.Clear(); _bindings.Clear(); return; }

            // Snapshot BEFORE clearing keys can restore the natively disabled renderers, and cover
            // every renderer these controllers can mutate: inactive body parts too, so nothing the
            // controllers touch can change without a restoration record.
            var snapshotted = new HashSet<IntPtr>();
            void Snapshot(Renderer renderer)
            {
                if (renderer == null) return;
                // A cosmetic renderer is never snapshotted, re-layered, selected, enabled or
                // restored here. Covers the body scan and the cached-controller paths alike.
                if (CosmeticMarkers.IsCosmetic(renderer))
                {
                    if (_skippedCosmetics++ < 2)
                        _log.LogInfo($"[mirror:body-cosmetic-skipped] generation={_id} snapshot skipped '{renderer.name}' layer={renderer.gameObject.layer}");
                    return;
                }
                if (!snapshotted.Add(Ptr(renderer))) return;
                var captured = new RendererState(renderer);
                captured.Isolated = renderer.gameObject.layer != 24 && renderer.gameObject.layer != 15;
                captured.Drive = captured.Isolated && renderer.gameObject.activeInHierarchy;
                captured.Selected = captured.Drive;
                _renderers.Add(captured);
            }
            for (int i = 0; i < bodyRenderers.Length; i++) Snapshot(bodyRenderers[i]?.TryCast<Renderer>());
            // The controllers can also mutate renderers they cached directly; only this body's own
            // renderers are covered, so a shared controller can never pull in a foreign object.
            foreach (var controller in _controllers)
            {
                void Cached(Renderer candidate)
                {
                    if (candidate == null) return;
                    try { if (bodyPointers.Contains(Ptr(candidate))) Snapshot(candidate); } catch { }
                }
                if (controller.Cached != null)
                    for (int i = 0; i < controller.Cached.Length; i++) Cached(controller.Cached[i]);
                var mutated = controller.Controller._cachedActiveModelRenderers;
                if (mutated != null) foreach (var entry in mutated) Cached(entry);
            }
            var groups = _body.GetComponentsInChildren(Il2CppType.Of<LODGroup>(), true);
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i].TryCast<LODGroup>();
                if (group == null || !group.gameObject.activeInHierarchy) continue;
                var lods = group.GetLODs();
                if (lods == null || lods.Length == 0) { _log.LogWarning($"[mirror:body-lod-skipped] '{group.name}' has no LOD0"); continue; }
                _lods.Add((group, group.enabled));
                var lod0 = new HashSet<IntPtr>();
                foreach (var r in lods[0].renderers) if (r != null) lod0.Add(Ptr(r));
                var lower = new HashSet<IntPtr>();
                for (int j = 1; j < lods.Length; j++) foreach (var r in lods[j].renderers) if (r != null) lower.Add(Ptr(r));
                foreach (var state in _renderers)
                    if (lower.Contains(Ptr(state.Renderer)) && !lod0.Contains(Ptr(state.Renderer))) state.Selected = false;
            }
            // Isolate every body renderer (active or not) before undoing the native hide keys.
            foreach (var state in _renderers) if (state.Isolated) state.Renderer.gameObject.layer = _layer;
            foreach (var lod in _lods) lod.group.enabled = false;
            _armed = true;
            _current = this;
            _removingInitialKeys = true;
            try
            {
                foreach (var b in _bindings)
                    try { b.Controller.Controller.EnableRendering(b.Key); b.Present = false; b.Restore = true; }
                    catch (Exception e) { _log.LogWarning($"[mirror:body-enable-failed] controller=0x{Ptr(b.Controller.Controller):X}: {e.Message}"); }
            }
            finally { _removingInitialKeys = false; }

            int selected = 0;
            foreach (var state in _renderers)
            {
                var r = state.Renderer;
                bool controlled = false, allowed = true;
                foreach (var controller in _controllers)
                {
                    if (!Owns(controller, r)) continue;
                    controlled = true;
                    if (!controller.Controller.IsRenderingEnabled) allowed = false;
                }
                // Respect unrelated disabling; renderers this body never owned keep their state.
                r.enabled = state.Isolated && state.Drive && controlled ? state.Selected && allowed : state.Enabled;
                if (!controlled) continue;
                if (state.Selected && allowed)
                {
                    selected++;
                    r.forceRenderingOff = false;
                    r.shadowCastingMode = ShadowCastingMode.On;
                    var sk = r.TryCast<SkinnedMeshRenderer>();
                    if (sk != null) sk.updateWhenOffscreen = true;
                }
            }
            int colliders = 0;
            foreach (var state in _renderers)
                try { colliders += state.Renderer.GetComponents(Il2CppType.Of<Collider>()).Length; } catch { }
            _log.LogInfo($"[mirror:body-bound] generation={_id} owner='{_owner.name}' ownerPtr=0x{Ptr(_owner):X} body='{_body.name}' selected={selected}/{_renderers.Count} LODGroups={_lods.Count} controllers={_controllers.Count} keys={_bindings.Count} layer={_layer} ({LayerMask.LayerToName(_layer)}) collidersOnBodyRenderers={colliders}");
            foreach (var state in _renderers) LogRenderer(state, null);
        }
        catch (Exception e)
        {
            _log.LogError($"[mirror:body-attach-failed] {e}");
            Disarm();
            RemovePatches();
            RestoreBinding();
        }
    }

    private static bool Owns(ControllerState controller, Renderer r)
    {
        if (controller == null || controller.Controller == null) return false;
        if (Under(r.transform, controller.Controller.transform)) return true;
        if (controller.Cached != null)
            for (int i = 0; i < controller.Cached.Length; i++)
                if (controller.Cached[i] != null && Ptr(controller.Cached[i]) == Ptr(r)) return true;
        var queue = controller.Controller._cachedActiveModelRenderers;
        if (queue != null)
            foreach (var qr in queue)
                if (qr != null && Ptr(qr) == Ptr(r)) return true;
        return false;
    }

    private bool InstallPatches()
    {
        if (_harmony != null) return true;
        try
        {
            _harmony = new Harmony(_id);
            // Exact (controller, key) pair, identified from the local body's own disable keys.
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(RenderingController), "DisableRendering", new[] { typeof(Il2CppSystem.Object) }),
                prefix: new HarmonyMethod(typeof(MirrorPlayerVisibility), nameof(DisableRenderingPrefix)));
            // Strongest narrow leaf named by the recon: prevent the actual renderer stop.
            // Kept, but scoped to the keyless owner-hide case only (see DisableRenderersPrefix).
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(RenderersRenderingController), "DisableRenderers", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(MirrorPlayerVisibility), nameof(DisableRenderersPrefix)));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(RenderingController), "EnableRendering", new[] { typeof(Il2CppSystem.Object) }),
                postfix: new HarmonyMethod(typeof(MirrorPlayerVisibility), nameof(EnableRenderingPostfix)));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(PlayerModelSetuper), "SetupModel", new[] { typeof(PlayerModelData) }),
                postfix: new HarmonyMethod(typeof(MirrorPlayerVisibility), nameof(SetupModelPostfix)));
            _log.LogInfo($"[mirror:patch-installed] generation={_id} scoped DisableRendering(key), watched DisableRenderers(), SetupModel dirty hook");
            return true;
        }
        catch (Exception e)
        {
            _log.LogError($"[mirror:patch-failed] generation={_id}: {e}");
            RemovePatches();
            return false;
        }
    }

    private bool OwnsController(RenderingController controller)
    {
        if (controller == null) return false;
        foreach (var state in _controllers)
            if (state.Controller != null && Ptr(state.Controller) == Ptr(controller)) return true;
        return false;
    }

    private static bool DisableRenderingPrefix(RenderingController __instance, Il2CppSystem.Object __0)
    {
        var self = _current;
        if (self == null || !self._armed || __instance == null) return true;
        foreach (var b in self._bindings)
        {
            if (b.Controller.Controller == null || Ptr(__instance) != Ptr(b.Controller.Controller) || Ptr(__0) != Ptr(b.Key)) continue;
            // The native key is still absent, so teardown restoration stays required.
            b.Suppressed = true;
            if (++self._skippedKeys <= 3)
                self._log.LogInfo($"[mirror:body-hide-suppressed] generation={self._id} controller=0x{Ptr(__instance):X} key=0x{Ptr(__0):X}");
            return false;
        }
        // Unknown key on a bound controller: never swallow it, or death/cutscene/equipment hides
        // would leave the body visible.
        if (self.OwnsController(__instance) && ++self._passedKeys <= 3)
            self._log.LogInfo($"[mirror:body-hide-passed] generation={self._id} controller=0x{Ptr(__instance):X} key=0x{Ptr(__0):X} type={__0?.GetIl2CppType().FullName} (not our owner-hide; allowed)");
        return true;
    }

    // Only the owner-hide leaf is swallowed, and it is identifiable: our suppressed request never
    // inserted its key, so _disableKeys is empty and a binding exists for that controller. Every
    // reason-bearing hide inserts its key first, and an unbound controller was never touched by us,
    // so both pass through untouched.
    private static bool DisableRenderersPrefix(RenderersRenderingController __instance)
    {
        var self = _current;
        if (self == null || !self._armed || !self.OwnsController(__instance)) return true;
        var ptr = Ptr(__instance);
        bool bound = false;
        foreach (var b in self._bindings)
            if (b.Controller.Controller != null && Ptr(b.Controller.Controller) == ptr) { bound = true; break; }
        if (!bound)
        {
            if (++self._passedDisableRenderers <= 3)
                self._log.LogInfo($"[mirror:body-renderers-passed] generation={self._id} controller=0x{ptr:X} noOwnerHideBound (allowed)");
            return true;
        }
        bool empty;
        try { var keys = __instance._disableKeys; empty = keys == null || keys.Count == 0; }
        catch { return true; }
        if (!empty)
        {
            if (++self._passedDisableRenderers <= 3)
                self._log.LogInfo($"[mirror:body-renderers-passed] generation={self._id} controller=0x{ptr:X} keyedReason (allowed)");
            return true;
        }
        if (++self._skippedDisableRenderers <= 3)
            self._log.LogInfo($"[mirror:body-renderers-suppressed] generation={self._id} controller=0x{ptr:X} keyless owner-hide");
        return false;
    }

    private static void EnableRenderingPostfix(RenderingController __instance, Il2CppSystem.Object __0)
    {
        var self = _current;
        if (self == null || !self._armed || self._removingInitialKeys) return;
        foreach (var b in self._bindings)
            if (b.Controller.Controller != null && Ptr(__instance) == Ptr(b.Controller.Controller) && Ptr(__0) == Ptr(b.Key))
            {
                // A genuine Enable cancels the teardown requirement; the key is absent again.
                b.Restore = false; b.Present = false; b.Suppressed = false;
            }
    }

    private static void SetupModelPostfix(PlayerModelSetuper __instance)
    {
        var self = _current;
        if (self != null && self._armed && __instance != null && Under(__instance.transform, self._owner)) self._dirty = true;
    }

    // Measured native hide state of the bound body. The mirror removes only the owner-hide keys and
    // lets every foreign key through by design, so a key still present on a bound controller - or a
    // controller that reports rendering disabled - means the game is hiding the body (death,
    // cutscene, equipment). Returns false when there is no live binding to measure.
    internal bool TryMeasureNativeHide(out int foreignKeys, out int renderingOff)
    {
        foreignKeys = 0;
        renderingOff = 0;
        if (_disposed || !_armed) return false;
        foreach (var controller in _controllers)
        {
            var rc = controller == null ? null : controller.Controller;
            if (rc == null) continue;
            try { var keys = rc._disableKeys; if (keys != null) foreignKeys += keys.Count; } catch { }
            try { if (!rc.IsRenderingEnabled) renderingOff++; } catch { }
        }
        return true;
    }

    internal void Diagnose(Camera camera)
    {
        if (_body == null) return;
        int isolatedCount = 0, drivenCount = 0;
        foreach (var state in _renderers) { if (state.Isolated) isolatedCount++; if (state.Drive) drivenCount++; }
        _log.LogInfo($"[mirror:body-check] owner='{_owner?.name}' bound={_armed} renderers={_renderers.Count} controllers={_controllers.Count} keys={_bindings.Count} isolated={isolatedCount} driven={drivenCount} suppressedKeys={_skippedKeys} passedKeys={_passedKeys} foreignKeysLeftInPlace={_foreignKeys} suppressedDisableRenderers={_skippedDisableRenderers} passedDisableRenderers={_passedDisableRenderers}; enabled/in-frustum is NOT proof of pixels");
        foreach (var state in _renderers) LogRenderer(state, camera);
    }

    private void LogRenderer(RendererState state, Camera camera)
    {
        var r = state.Renderer;
        if (r == null) return;
        var sk = r.TryCast<SkinnedMeshRenderer>();
        string mesh = sk != null ? sk.sharedMesh?.name : "non-skinned";
        string viewport = camera != null ? camera.WorldToViewportPoint(r.bounds.center).ToString() : "not-measured";
        _log.LogInfo($"[mirror:body-renderer] '{r.name}' selected={state.Selected} active={r.gameObject.activeInHierarchy} enabled={r.enabled} forceOff={r.forceRenderingOff} bounds={r.bounds} mesh='{mesh}' material='{r.sharedMaterial?.name}' layer={r.gameObject.layer} viewportCenter={viewport}");
    }

    internal void Disarm()
    {
        _armed = false;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private void RemovePatches()
    {
        if (_harmony == null) return;
        try { _harmony.UnpatchSelf(); _log.LogInfo($"[mirror:patch-removed] generation={_id}"); _harmony = null; }
        catch (Exception e) { _log.LogError($"[mirror:restore-failed] unpatch generation={_id}, patch kept for retry: {e}"); }
    }

    private void RestoreBinding()
    {
        int keys = 0, restored = 0;
        // Restore native key state first, then exact surviving renderer/LOD snapshots.
        foreach (var b in _bindings)
        {
            if (!b.Restore || b.Key == null || b.Controller?.Controller == null) continue;
            if (b.Present) _log.LogWarning($"[mirror:body-key-state] generation={_id} key=0x{Ptr(b.Key):X} restore-needed but already present");
            try { b.Controller.Controller.DisableRendering(b.Key); b.Restore = false; b.Present = true; keys++; }
            catch (Exception e) { _log.LogWarning($"[mirror:restore-failed] hide key: {e}"); }
        }
        foreach (var state in _renderers)
        {
            try
            {
                var r = state.Renderer;
                if (r == null) continue;
                r.gameObject.layer = state.Layer; r.enabled = state.Enabled;
                r.forceRenderingOff = state.ForceOff; r.shadowCastingMode = state.Shadows;
                var sk = r.TryCast<SkinnedMeshRenderer>();
                if (sk != null) sk.updateWhenOffscreen = state.Offscreen;
                restored++;
            }
            catch (Exception e) { _log.LogWarning($"[mirror:restore-failed] body renderer: {e}"); }
        }
        foreach (var lod in _lods)
            try { if (lod.group != null) lod.group.enabled = lod.enabled; }
            catch (Exception e) { _log.LogWarning($"[mirror:restore-failed] LOD: {e}"); }
        if (_bindings.Count > 0 || _renderers.Count > 0) _log.LogInfo($"[mirror:body-restored] generation={_id} renderers={restored} LODGroups={_lods.Count} keys={keys}");
        _bindings.Clear(); _renderers.Clear(); _lods.Clear(); _controllers.Clear(); _body = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disarm();
        try { RemovePatches(); }
        finally { RestoreBinding(); _owner = null; _log.LogInfo($"[mirror:disposed] visibility generation={_id} patchesRemoved={_harmony == null}"); }
    }
}
