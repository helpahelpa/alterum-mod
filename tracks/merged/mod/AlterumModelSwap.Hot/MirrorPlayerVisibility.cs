using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Cameras.SwitchActionProcessors;
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
    // The body's own renderers (by pointer), so "is this renderer mine" is answerable without a scan.
    private HashSet<IntPtr> _bodyPointers = new();

    // ---------------------------------------------------------------- third-person screen view
    // Layer 14 (Hitbox) is the body's isolated layer: it was chosen because no screen camera renders
    // it, and BodyLayerCensus shows the layer holds exactly this body's own renderers and nothing
    // else that could draw. Third person is the one POV whose screen camera must see the body (first
    // person must stay bodyless - the game's own first-view proxy is layer 24 and is untouched), so
    // the bit is admitted per screen camera while the game reports ThirdPerson and withdrawn
    // otherwise. The census runs again before every admission, and a foreign renderer appearing on
    // the layer withdraws the bit, so the mask can never carry more than the local body.
    // The live-foreign guard walks the renderer table. Ten seconds is the safety interval, not a
    // polling rate: a foreign renderer appearing on the isolated body layer is a mod/regression
    // event, and the admission flips (which get a full census) still happen immediately.
    private const float ScreenScanSeconds = 20f, PovResolveSeconds = 0.5f;
    private sealed class ScreenBit
    {
        public Camera Camera;
        public int Id;
        public bool HadBitAtFirstSight, Ours;
    }
    private readonly List<ScreenBit> _screenBits = new();
    private PlayerPOVController _pov;
    private float _nextPovResolve, _nextScreenScan;
    private bool _screenScanned, _screenBitApplied, _povThird, _povNativeHide;
    // The cosmetic bit follows the body bit: see LocalBodyOnScreen / SetBodyOnScreenSink.
    private Action<bool> _bodyOnScreenSink;
    private bool _bodyOnScreenNotified;
    private POV _povValue = POV.None;
    private int _screenCameras, _screenChanged, _screenBlocked, _screenRestored;
    // The third-person camera rig (ThirdPersonCamera.cs) and the body-bit veto it can raise: the body
    // is only drawn when the lens is demonstrably out of it.
    private ThirdPersonCameraRig _camera;
    private bool _veto;
    private string _vetoWhy;

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

    // The layer the live binding isolated the body on (-1 when nothing is bound), for the mirror's
    // own layer selection.
    internal static int CurrentBodyLayer => _current != null ? _current._layer : -1;

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

    // The scene root's own child for a hierarchy, for attributing a collider census entry to the
    // object that owns it rather than to a bone three levels down ("Lobby Global" is one umbrella).
    internal static Transform BranchRoot(Transform t)
    {
        if (t == null) return null;
        var branch = t;
        for (int i = 0; i < 64 && t.parent != null; i++) { branch = t; t = t.parent; }
        return branch;
    }

    internal void Attach(Transform localPlayer, int bodyLayer)
    {
        _owner = localPlayer;
        _layer = bodyLayer;
        Rebind();
        if (_disposed) return;
        // The real third-person camera (see ThirdPersonCamera.cs). It owns the same condition the
        // body admission does, so the mask can never carry the body from a lens that was not moved.
        _camera = new ThirdPersonCameraRig(_log, this);
    }

    internal void Update()
    {
        // Called in the runtime's Update, after SetupModel's native callbacks finish.
        if (_dirty && !_disposed) { _dirty = false; Rebind(); }
        // Read-only layer census: available to automation at any time through bodylayer.request, and
        // dumped once per generation anyway, so the set a wider culling mask would add is on record
        // before any mask is ever widened.
        if (_disposed) return;
        // POV state first, then the camera rig, then the admission: the rig's veto ("is there room for
        // a third-person lens at all", "has the lens moved yet") is part of the same decision as the
        // mask, so all three read one frame's state and the mask can never be admitted for a lens the
        // rig is about to refuse.
        RefreshPovState();
        _camera?.Tick();
        BodyLayerCensus.Tick(_log, _layer, _owner, IsLocalBodyRenderer);
        SyncScreenView();
        BodyScreenProof.Tick(_log, _layer, FirstScreenCamera);
    }

    // The game POV and the game's own hide state, read once per frame before the camera rig and the
    // mask admission both use them.
    private void RefreshPovState()
    {
        var pov = _armed ? ReadPov() : POV.None;
        _povValue = pov;
        bool third = pov == POV.ThirdPerson;
        bool nativeHide = false;
        if (third)
        {
            // The game hides the body itself on death/cutscene/equipment (a key it owns stays in the
            // controller). Widening a mask in that state would be pointless at best, so it waits.
            try { nativeHide = TryMeasureNativeHide(out var keys, out var off) && (keys > 0 || off > 0); } catch { }
        }
        _povThird = third;
        _povNativeHide = nativeHide;
    }

    // LateUpdate half of the camera: the Cinemachine event is the normal hook, this is the fallback
    // used only when that event is not delivering (see ThirdPersonCamera.cs).
    internal void LateTick()
    {
        if (_disposed) return;
        _camera?.LateTick();
    }

    // Part of the local player's own third person body, as identified by this module's body sweep.
    private bool IsLocalBodyRenderer(Renderer r) => r != null && _bodyPointers.Contains(Ptr(r));

    // ---------------------------------------------------------------- third-person screen view
    // The game camera is at the owner's eyes in both POVs (PovToggle switches the game's own view
    // mode, it does not move that camera), so the body is geometry around/behind it: in third person
    // the screen camera must be allowed to draw it, in first person it must not.
    //
    // This is the ONE owner of "is the local body on screen this frame". Everything else follows it:
    // the body bit below, and the local cosmetic bit in CosmeticsRuntime (via SetBodyOnScreenSink)
    // and its own policy read. A hat can therefore never be drawn over a body that a POV, a native
    // hide, or a refused camera pose has withdrawn - and none of those rules has to be repeated.
    internal bool LocalBodyOnScreen => !_disposed && _armed && _povThird && !_povNativeHide && !_veto;

    internal void SyncScreenView()
    {
        if (_disposed || _layer < 0) return;
        var pov = _povValue;
        // The camera rig's veto is part of the same decision: a lens that is not (yet) proven to be
        // out of the body must not draw it, or the frame is the inside of the owner's face.
        bool want = LocalBodyOnScreen;
        bool rescan = Time.unscaledTime >= _nextScreenScan;
        if (_screenScanned && want == _screenBitApplied && !rescan) return;
        ApplyScreenView(want, pov);
    }

    private void ApplyScreenView(bool want, POV pov)
    {
        _nextScreenScan = Time.unscaledTime + ScreenScanSeconds;
        int bit = 1 << _layer;
        bool flip = !_screenScanned || want != _screenBitApplied;
        if (want)
        {
            // Before this layer joins a screen camera's mask, prove it carries nothing but this
            // body: a full census (renderers + hitbox colliders) on the first admission and on every
            // flip, a cheap live-renderer guard on the periodic rescans.
            int foreign;
            string foreignNames = null;
            if (flip) foreign = BodyLayerCensus.Dump(_log, _layer, _owner, IsLocalBodyRenderer, "screen-admit").LiveForeign;
            else foreign = BodyLayerCensus.LiveForeign(_log, _layer, IsLocalBodyRenderer, out foreignNames);
            if (foreign > 0)
            {
                want = false;
                if (_screenBlocked++ < 3)
                    _log.LogWarning($"[bodyview:blocked] {foreign} foreign live renderer(s) on layer {_layer} ('{LayerMask.LayerToName(_layer)}'): " +
                                    $"the screen bit is not admitted, the mask stays body-only {(foreignNames == null ? "" : "[" + foreignNames + "]")}");
            }
        }

        int cameras = 0, changed = 0;
        var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
        for (int i = 0; i < cams.Length; i++)
        {
            var camera = cams[i].TryCast<Camera>();
            if (!MirrorFactory.ScreenCamera(camera)) continue;
            if (IsProofCamera(camera)) continue;
            cameras++;
            var tracked = TrackScreenCamera(camera);
            if (tracked == null) continue;
            int mask;
            try { mask = camera.cullingMask; } catch { continue; }
            bool has = (mask & bit) != 0;
            if (want && !has) { if (WriteMask(camera, mask | bit)) { tracked.Ours = true; changed++; } }
            // Withdrawn unconditionally, not only when this generation set it: the layer is mod-owned
            // by contract (it was chosen because no screen camera renders it), so a bit found here in
            // first person is a leftover from a retired generation or a mod-owned write either way.
            else if (!want && has) { if (WriteMask(camera, mask & ~bit)) { tracked.Ours = false; changed++; } }
            else if (want && has) tracked.Ours = true;
        }

        // The answer first, the cosmetic bit second, the log last: then the line below prints the masks
        // as they will render, hat coupling included, and not the state halfway through the frame.
        bool wasApplied = _screenBitApplied;
        _screenBitApplied = want;
        if (wasApplied != want) NotifyBodyOnScreen(want);
        if (flip || changed > 0 || cameras != _screenCameras)
        {
            _screenCameras = cameras;
            _screenChanged += changed;
            _log.LogInfo($"[bodyview:screen] pov={pov} want={want} layer={_layer} ('{LayerMask.LayerToName(_layer)}') " +
                        $"bit=0x{bit:X8} screenCameras={cameras} masksChanged={changed} totalChanges={_screenChanged} " +
                        $"nativeHide={_povNativeHide} blocked={_screenBlocked} veto={(_veto ? _vetoWhy ?? "yes" : "no")} " +
                        $"{EyeInsideBody()} masks={MaskList(bit)}");
        }
        _screenScanned = true;
    }

    // The cosmetic bit's input: called with the answer that the body bit actually has, not with a
    // prediction of it, so the two boxes in the same frame always agree. It fires from the mask
    // write and is a no-op when the value did not change.
    private void NotifyBodyOnScreen(bool onScreen)
    {
        if (onScreen == _bodyOnScreenNotified) return;
        _bodyOnScreenNotified = onScreen;
        var sink = _bodyOnScreenSink;
        if (sink == null) return;
        try { sink(onScreen); }
        catch (Exception e) { _log.LogWarning($"[bodyview:cosmetic-gate] cosmetic mask update failed: {e.Message}"); }
    }

    // Wired once by the runtime (PlaceMirror) to CosmeticsRuntime.OnLocalBodyOnScreenChanged. Cleared
    // before either side is torn down, so a retired generation can never be called back into.
    internal void SetBodyOnScreenSink(Action<bool> sink)
    {
        _bodyOnScreenSink = sink;
        _bodyOnScreenNotified = _screenBitApplied;
    }

    // Measured, not assumed: with the game camera parked at the owner's eyes in both POVs, a
    // third-person body drawn through the screen mask is geometry around the lens. This says
    // whether the eye is inside one of the body's own renderer bounds, which is what turns the
    // added bit into a head-interior view rather than a body silhouette.
    private string EyeInsideBody()
    {
        var cam = FirstScreenCamera();
        if (cam == null || _renderers.Count == 0) return "eyeInsideBody=unknown";
        Vector3 eye;
        try { eye = cam.transform.position; } catch { return "eyeInsideBody=unknown"; }
        var sb = new StringBuilder();
        int hits = 0;
        foreach (var state in _renderers)
        {
            var r = state.Renderer;
            if (r == null) continue;
            try
            {
                if (!r.bounds.Contains(eye)) continue;
                hits++;
                if (hits <= 4) sb.Append(hits == 1 ? "" : ",").Append('\'').Append(r.name).Append('\'');
            }
            catch { }
        }
        return $"eyeInsideBody={(hits > 0 ? "True" : "False")} eyeIn=[{(sb.Length == 0 ? "none" : sb.ToString())}]";
    }

    internal const string ProofCameraName = "AlterumScreenProofCam";
    private static bool IsProofCamera(Camera c)
    {
        try { return c != null && c.name == ProofCameraName; } catch { return false; }
    }

    private Camera FirstScreenCamera()
    {
        for (int i = 0; i < _screenBits.Count; i++)
        {
            var c = _screenBits[i].Camera;
            if (c == null) continue;
            try { if (MirrorFactory.ScreenCamera(c) && !IsProofCamera(c)) return c; } catch { }
        }
        return null;
    }

    // A bit this generation added to a screen camera on purpose. The mirror's own conflict check
    // consults this, so an intended third-person admission is not mistaken for a foreign camera.
    internal bool OwnsScreenBit(Camera camera)
    {
        if (camera == null) return false;
        for (int i = 0; i < _screenBits.Count; i++)
        {
            var s = _screenBits[i];
            if (!s.Ours || s.Camera == null) continue;
            try { if (s.Camera.GetInstanceID() == camera.GetInstanceID()) return true; } catch { }
        }
        return false;
    }

    private ScreenBit TrackScreenCamera(Camera camera)
    {
        int id;
        try { id = camera.GetInstanceID(); } catch { return null; }
        for (int i = _screenBits.Count - 1; i >= 0; i--)
            if (_screenBits[i].Camera == null) _screenBits.RemoveAt(i);
        for (int i = 0; i < _screenBits.Count; i++)
            if (_screenBits[i].Id == id) return _screenBits[i];
        var state = new ScreenBit { Camera = camera, Id = id };
        try { state.HadBitAtFirstSight = (camera.cullingMask & (1 << _layer)) != 0; } catch { }
        _screenBits.Add(state);
        _log.LogInfo($"[bodyview:screen-camera] '{CameraPath(camera)}" +
                    $"' pristineMask=0x{(camera == null ? 0 : camera.cullingMask):X8} " +
                    $"hadBit{_layer}={state.HadBitAtFirstSight} tracked={_screenBits.Count}");
        if (state.HadBitAtFirstSight)
            _log.LogWarning($"[bodyview:preexisting] screen camera '{CameraPath(camera)}' already had layer {_layer} " +
                            $"('{LayerMask.LayerToName(_layer)}') before this generation wrote it; it is treated as mod-owned " +
                            $"and is withdrawn outside third person");
        return state;
    }

    private bool WriteMask(Camera camera, int mask)
    {
        try { camera.cullingMask = mask; return true; }
        catch (Exception e) { _log.LogWarning($"[bodyview:mask] write failed: {e.Message}"); return false; }
    }

    private string MaskList(int bit)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _screenBits.Count; i++)
        {
            var s = _screenBits[i];
            if (s.Camera == null) continue;
            try
            {
                int mask = s.Camera.cullingMask;
                sb.Append('\'').Append(CameraPath(s.Camera)).Append("'=0x").Append(mask.ToString("X8"))
                  .Append((mask & bit) != 0 ? "+" : "-").Append(s.Ours ? "o" : ".").Append("; ");
            }
            catch { }
        }
        return sb.Length == 0 ? "(none tracked)" : sb.ToString().TrimEnd();
    }

    private static string CameraPath(Camera c)
    {
        try { return Hierarchy(c.transform); } catch { return c == null ? "null" : "unreadable"; }
    }

    // Game POV from the local player's own controller (the same object PovToggle drives), cached;
    // re-resolved at most twice a second, read every call so a switch is honoured in its own frame.
    private POV ReadPov()
    {
        try
        {
            bool alive = true;
            if (_pov == null) alive = false;
            else { var go = _pov.gameObject; alive = go != null && go.activeInHierarchy; }
            if (!alive)
            {
                if (Time.unscaledTime < _nextPovResolve) return POV.None;
                _nextPovResolve = Time.unscaledTime + PovResolveSeconds;
                _pov = null;
                var controllers = SceneCache.Scan(Il2CppType.Of<PlayerPOVController>());
                for (int i = 0; i < controllers.Length; i++)
                {
                    var c = controllers[i].TryCast<PlayerPOVController>();
                    if (c == null || !Under(c.transform, _owner)) continue;
                    _pov = c;
                    break;
                }
            }
            return _pov == null ? POV.None : _pov.CurrentPOV;
        }
        catch { return POV.None; }
    }

    // Put every tracked screen camera back the way it was found (body bit withdrawn).
    internal void RestoreScreenView()
    {
        int restored = 0;
        if (_layer >= 0)
        {
            int bit = 1 << _layer;
            for (int i = 0; i < _screenBits.Count; i++)
            {
                var s = _screenBits[i];
                if (s.Camera == null) continue;
                try
                {
                    int mask = s.Camera.cullingMask;
                    if ((mask & bit) == 0) { s.Ours = false; continue; }
                    if (WriteMask(s.Camera, mask & ~bit)) { s.Ours = false; restored++; }
                }
                catch (Exception e) { _log.LogWarning($"[bodyview:mask] restore failed: {e.Message}"); }
            }
        }
        if (restored > 0)
            _log.LogInfo($"[bodyview:screen] restored {restored} screen camera mask(s) (body layer {_layer} withdrawn)");
        _screenRestored += restored;
        _screenBitApplied = false;
        _screenScanned = false;
        // The body is off the screen now, so the cosmetic bit must be too (the hat follows the body).
        NotifyBodyOnScreen(false);
    }

    internal void Rebind()
    {
        if (_disposed) return;
        Disarm();
        RestoreBinding();
        try
        {
            var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
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
            var bodyPointers = _bodyPointers = new HashSet<IntPtr>();
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
                var modelRefs = SceneCache.Scan(Il2CppType.Of<PlayerModelReferences>());
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
                var rrcs = SceneCache.Scan(Il2CppType.Of<RenderersRenderingController>());
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
            BodyLayerCensus.Dump(_log, _layer, _owner, IsLocalBodyRenderer, "body-bound");
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

    // ---------------------------------------------------------------- third-person camera seam
    // Used by ThirdPersonCameraRig. The rig owns the pose; this module owns which renderers are the
    // local body, so the bounds/inside-body test stays with the renderer set it was measured from.
    internal bool IsArmed => _armed && !_disposed;

    // Exactly the condition that admits the body bit, minus the thing the rig itself supplies (a
    // lens proven out of the body). The camera moves only when the body would be drawn, and the body
    // is drawn only when the camera moved.
    internal bool WantsThirdPersonCamera => !_disposed && _armed && _povThird && !_povNativeHide && _layer >= 0;

    internal Transform Owner => _owner;

    // The camera whose mask this module writes in third person (the game's own view camera).
    internal Camera ScreenCamera() => FirstScreenCamera();

    internal bool IsScreenCamera(Camera c)
    {
        if (c == null) return false;
        try { return MirrorFactory.ScreenCamera(c) && !IsProofCamera(c); } catch { return false; }
    }

    internal bool IsOwnTransform(Transform t) => t != null && Under(t, _owner);

    // World-space union of the local body's own renderer bounds (the nine isolated renderers).
    internal bool TryGetBodyBounds(out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (var state in _renderers)
        {
            var r = state.Renderer;
            if (r == null || !state.Isolated) continue;
            try
            {
                var b = r.bounds;
                if (!any) { bounds = b; any = true; }
                else bounds.Encapsulate(b);
            }
            catch { }
        }
        return any;
    }

    // The head, for the "how far is the camera from the head" measurement only. The body rig carries
    // a bone named head (and a `head [Hitbox]` child); a model without one falls back to the highest
    // isolated renderer's bounds centre (hair/head is the top of the body either way).
    internal bool TryGetHeadCenter(out Vector3 centre)
    {
        centre = default;
        try
        {
            if (_body != null)
            {
                var all = _body.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i].TryCast<Transform>();
                    if (t == null) continue;
                    string n;
                    try { n = t.name; } catch { continue; }
                    if (n == null || !n.StartsWith("head", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.IndexOf('[') >= 0) continue;   // the hitbox child, not the bone
                    centre = t.position;
                    return true;
                }
            }
        }
        catch { }
        bool any = false;
        float bestTop = float.MinValue;
        foreach (var state in _renderers)
        {
            var r = state.Renderer;
            if (r == null) continue;
            try
            {
                var b = r.bounds;
                if (b.max.y <= bestTop) continue;
                bestTop = b.max.y;
                centre = b.center;
                any = true;
            }
            catch { }
        }
        return any;
    }

    // Is this point inside one of the body's own renderer bounds? A margin of 0 is the exact bounds
    // test the eye-inside-body line uses.
    internal bool BodyContains(Vector3 point, float margin, out string names)
    {
        var sb = new StringBuilder();
        int hits = 0;
        foreach (var state in _renderers)
        {
            var r = state.Renderer;
            if (r == null) continue;
            try
            {
                var b = r.bounds;
                if (margin != 0f) b.Expand(margin);
                if (!b.Contains(point)) continue;
                hits++;
                if (hits <= 4) sb.Append(hits == 1 ? "" : ",").Append('\'').Append(r.name).Append('\'');
            }
            catch { }
        }
        names = sb.Length == 0 ? "none" : sb.ToString();
        return hits > 0;
    }

    // Called every frame by the rig. Raising it withdraws the bit in the same frame (this runs in
    // LateUpdate, before the camera renders), so a refused pose can never show the face interior.
    internal void SetBodyVeto(bool veto, string why)
    {
        if (_disposed) return;
        if (veto == _veto) return;
        _veto = veto;
        _vetoWhy = why;
        if (_veto)
        {
            _log.LogWarning($"[bodyview:veto] {why} -> body bit withdrawn (a third-person lens that is not proven out of the body must not draw it)");
            if (_screenBitApplied) ApplyScreenView(false, _povThird ? POV.ThirdPerson : POV.FirstPerson);
        }
        else _log.LogInfo("[bodyview:veto] cleared -> third-person body admission allowed again");
    }

    internal void Disarm()
    {
        _armed = false;
        // Teardown, Rebind and a failed rebind all end here: the screen cameras must never keep a
        // body bit that no armed binding justifies.
        try { RestoreScreenView(); } catch (Exception e) { _log.LogWarning($"[bodyview:mask] disarm restore failed: {e.Message}"); }
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
        _bodyPointers = new();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disarm();
        _bodyOnScreenSink = null;
        // The camera rig is torn down before the binding: its dispose puts the game's own transform
        // back and unsubscribes the Cinemachine listener, so a retired generation cannot keep moving
        // the lens or keep the body bit withdrawn.
        try { _camera?.Dispose(); } catch (Exception e) { _log.LogWarning($"[tp-cam:dispose] failed: {e.Message}"); }
        _camera = null;
        try { RemovePatches(); }
        finally { RestoreBinding(); _owner = null; _log.LogInfo($"[mirror:disposed] visibility generation={_id} patchesRemoved={_harmony == null}"); }
    }
}
