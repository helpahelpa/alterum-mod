using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Cameras.SwitchActionProcessors;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace AlterumModelSwap.Hot;

// Rebuilds MoreCompany's built-in cosmetics from extracted numeric data (CosmeticPack) against
// UnityEngine + URP, rigidly parents them to the local third-person head bone, and owns their
// screen/mirror visibility.
//
// Design notes (see tracks/cosmetics/notes.md and plans/emotes-cosmetics.md):
//  * no MoreCompany asset is loaded: only vertex/index/UV/normal/tangent arrays and PNG pixels;
//  * the fit is measured, not copied: source head box (authoring units) vs Alterum's own head box
//    measured from the head-weighted vertices through the skinned mesh bindposes;
//  * MoreCompany's runtime 0.38 multiplier is deliberately not used;
//  * local only: nothing is networked, no native customization registry is touched.
internal sealed class CosmeticsRuntime : IDisposable
{
    // LayerMask.LayerToName(23) == "TV_PlayerModelMesh": the layer the game's third-person body
    // uses, present in both the screen camera and the mirror camera. It is shared with the game's
    // own third-person meshes and with other players' cosmetics, so it is never removed from the
    // screen camera.
    private const int TvPlayerModelLayer = 23;
    // Per-generation local-cosmetic layer. Only the local player's cosmetics are moved here; the
    // screen cameras carry the bit only in third person and the mirror camera always does.
    private const int FallbackLocalCosmeticLayer = 6;
    private const int PreferredLocalCosmeticLayer = 6;
    private const int MirrorBodyLayer = 14;                 // MirrorPlayerVisibility's isolated body
    private const string DefaultEquipped = "builtin.partyhat";
    private const float BindPollSeconds = 0.5f;
    private const float PovPollSeconds = 0.25f;
    private const float CameraScanSeconds = 2f;

    private readonly IHostContext _h;
    private bool _disposed;

    private CosmeticPack _pack;
    private string _packFailure;

    // ---------------------------------------------------------------- shared caches
    private readonly Dictionary<int, Mesh> _meshes = new();
    private readonly Dictionary<int, Texture2D> _textures = new();
    private readonly Dictionary<int, Material> _materials = new();
    private Shader _litShader;
    private Material _template;
    private string _templateName = "none";
    private bool _unlitFallback;

    // ---------------------------------------------------------------- binding / fit
    private Transform _owner;
    private ThirdViewPlayerModel _body;
    private Transform _head;
    private Transform _modelRoot;
    private IntPtr _bodyPtr, _headPtr;
    // Instance ids, checked together with the pointers: an address can be reused by a replacement
    // head within one frame, and pointer equality alone then keeps the stale binding.
    private int _bodyId, _headId;
    private bool _fitReady;
    private float _fitScale = 1f;
    private Quaternion _hatRotation = Quaternion.identity;   // world rotation the last refit produced (head.rotation * _localRot at that moment)
    private Vector3 _fitShift;                               // world-space shift in hat-local axes
    // Per-slot placement in each SOCKET's own local frame, captured by the refit that produced
    // `_localRot`/`_fitShift` (slot key = `CosmeticDef.Type`: 0 hat, 2 chest, 3 wrist, 4 hip).
    // `_localRot`/`_localPos` are calibrated in the HEAD bone's frame, so a chest/hip/wrist item
    // cannot be attached from them directly: Attach needs the placement in the item's own socket
    // frame, and deriving it from the LIVE head pose (`inverse(socket.rotation) * head.rotation *
    // _localRot`) bakes whatever the head was doing at equip time into the item, permanently. These
    // two hold the conversion evaluated at the fit - when head and socket poses are both the fit's -
    // and Attach applies them verbatim: no later head/socket pose can reach a non-head item.
    private readonly Dictionary<int, Quaternion> _slotRot = new();
    private readonly Dictionary<int, Vector3> _slotPos = new();
    // The placement in the game head bone's LOCAL frame: localPos = head x (offset, rotation, scale).
    // Filled either from the per-model HatAnchor (a mod body) or from the native head measurement.
    // This pair, not _hatRotation, is what a fresh attach derives from: the world rotation is
    // _head.rotation * _localRot, evaluated at attach time (see LocalTarget).
    private Vector3 _localPos;
    private Quaternion _localRot = Quaternion.identity;
    private Vector3 _headLossy = Vector3.one;
    private string _fitSource = "none";
    private string _wornSlug = "";
    // World box of the worn model's own head geometry as it was last measured, and the rule the
    // last refit used. The seat log reads both; nothing in the placement reads them.
    private Bounds _wornHeadWorld;
    private bool _wornHeadWorldValid;
    // Harness only: `hatrule.request` with `anchor` forces the stored per-model table so the seat
    // numbers of the old rule can be measured with the same code that measures the new one.
    private bool _forceAnchorRule;
    private float _nextRulePoll;
    private string _seatRule = "none";
    private float _nextBindPoll;
    private float _nextFitLog;
    // CHEST socket, resolved once per body binding: a spine bone present on every rig (the typed
    // Bones surface has no chest slot, and chest_01_* are female-only breast bones).
    private Transform _chestBone;
    private bool _chestResolved, _socketScanLogged;

    // ---------------------------------------------------------------- state
    private readonly HashSet<string> _equipped = new(StringComparer.Ordinal);
    private int _equippedVersion;
    private readonly List<Instance> _instances = new();
    // Stage 3A: the equipped sets attached to REMOTE players' heads. Kept out of `_instances` so
    // every local-only sweep (POV layer, screen/mirror state, native-hide, reconcile) never touches
    // them: a remote hat lives on the game's third-person mesh layer for the whole frame.
    private readonly List<RemoteHats> _remoteHats = new();
    private readonly Dictionary<string, float> _retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _slotLogs = new(StringComparer.Ordinal);
    private string _statePath;
    private bool _stateDirty;
    private float _nextStateFlush;
    private float _nextRequestPoll;
    private const string RequestFileName = "cosmetic.request";

    private POV _pov = POV.None;
    private float _nextPovPoll;
    private bool _foreignHide;
    // Measured native hide state (see PollNativeHide): what the mirror's bound body controllers
    // report right now, not a constant.
    private bool _foreignHideBound;
    private int _foreignHideKeys, _foreignRenderingOff;
    private bool _screenVisible, _mirrorVisible;
    private string _lastView = "";
    private int _assetsRolledBack;

    // ---------------------------------------------------------------- per-camera local layer
    private int _localLayer = -1;
    private bool _localLayerResolved;
    private bool _layerProbeDeferred;
    private string _layerScene = "";
    private float _nextCameraScan;
    private bool _screenLayerApplied;
    private bool _hideAll;                                  // native body hide (death/cutscene)
    private readonly List<CameraMaskState> _screenMasks = new();

    private string _overlayInfo = "";
    private float _nextProof = -1f;
    private int _proofDumps;
    private int _proofStage;
    private float _proofAt;
    private POV _proofRestorePov = POV.None;
    private static readonly bool ProofEnabled = Environment.GetEnvironmentVariable("ALTERUM_COSMETIC_PROOF") == "1";
    private Camera _mirrorCamera;
    private Camera _lastScreenCamera;
    private int _lastScreenMask;

    internal sealed class Instance
    {
        public string Id;
        public CosmeticPack.CosmeticDef Def;
        public GameObject Root;
        public readonly List<Renderer> Renderers = new();
        // Marker addresses recorded at registration, so unregistering does not depend on the
        // wrappers still being alive when the tree is torn down.
        public readonly List<IntPtr> RendererPointers = new();
        public IntPtr RootPointer;
        public int MeshCount, Verts, Tris;
        public bool Visible;
    }

    // Pristine culling mask of a screen camera, captured before the first local-layer write so a
    // hot reload puts back exactly what the game had.
    private sealed class CameraMaskState
    {
        public Camera Camera;
        public int OriginalMask;
        public int InstanceId;
    }

    internal CosmeticsRuntime(IHostContext host)
    {
        _h = host;
        CosmeticMarkers.Clear();
        LoadEquippedState();
        Current = this;
    }

    // The live generation's instance, so the stage-3A remote-body path can build the wearer's
    // equipped set from the same pack/mesh/material caches instead of a second copy of them.
    internal static CosmeticsRuntime Current { get; private set; }

    // Bumped by every mutation of the equipped set (request file, picker toggle, clear, and the
    // startup state load). Remote wearers compare it instead of re-reading the set every frame, and
    // the picker uses it as its redraw signature - a count or a truncated summary cannot stand in
    // for it, because a same-size swap can leave both unchanged.
    internal int EquippedVersion => _equippedVersion;

    // ================================================================ public surface

    // Re-poll the local camera mode now: called right after PovToggle changed it, so the
    // [cosmetic:view] line for that transition lands in the same frame.
    internal void ForcePovRefresh() => _nextPovPoll = 0f;

    // A cosmetic that exists and still has at least one live renderer. The mirror admits the shared
    // TV_PlayerModelMesh layer only while this is true (P2.3): with an empty equipped set the
    // merged mirror must not render anything on that layer the source camera would not.
    internal bool CosmeticsInUse
    {
        get
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                var instance = _instances[i];
                if (instance == null || instance.Root == null) continue;
                for (int r = 0; r < instance.Renderers.Count; r++)
                    if (instance.Renderers[r] != null) return true;
            }
            return false;
        }
    }

    internal void Update()
    {
        if (_disposed) return;
        if (_pack == null && _packFailure == null) LoadPack();
        if (_pack == null) return;

        EnsureLocalCosmeticLayer();
        PollRequestFile();
        PollBind();
        PollAnchorProbe();
        PollPov();
        PollNativeHide();
        Reconcile();
        ApplyScreenState("update");
        RunProof();
        FlushState();

    }

    // The demo desktop is locked, so the F12 panel cannot be clicked during an unattended run.
    // This is the same line-oriented automation path the other features expose:
    //   equip=builtin.partyhat   add an id to the equipped set
    //   unequip=builtin.partyhat remove it (live: the instance, its markers and its root go away)
    //   clear=1                  empty the equipped set
    // Applied before Reconcile() so an unequip takes effect in the same frame.
    private void PollRequestFile()
    {
        if (Time.unscaledTime < _nextRequestPoll) return;
        _nextRequestPoll = Time.unscaledTime + 0.25f;
        string path;
        try { path = Path.Combine(_h.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = null;
        try { text = File.ReadAllText(path); }
        catch (Exception e) { _h.LogWarning($"[cosmetic:request] read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }
        if (text == null) return;
        int changed = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { _h.LogWarning($"[cosmetic:request] ignored line '{line}' (not key=value)"); continue; }
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "equip":
                    if (val.Length > 0 && _equipped.Add(val)) changed++;
                    break;
                case "unequip":
                    if (_equipped.Remove(val)) changed++;
                    break;
                case "clear":
                    if (val.Length > 0 && val != "0") { changed += _equipped.Count; _equipped.Clear(); }
                    break;
                default:
                    _h.LogWarning($"[cosmetic:request] unknown key '{key}' ignored");
                    break;
            }
        }
        if (changed > 0) { _stateDirty = true; _equippedVersion++; }
        _h.LogInfo($"[cosmetic:request] equipped={_equipped.Count} changed={changed} " +
                   $"instances={_instances.Count} markers={CosmeticMarkers.RendererCount}");
    }

    // Per-camera visibility, no renderer toggling for POV: the local player's cosmetics sit on a
    // dedicated layer. The screen cameras add that bit in third person and clear it in first
    // person; the mirror camera keeps it, so the same hat object is hidden from the screen and
    // visible in the mirror in the same frame. Layer 23 (the game's TV meshes and other players'
    // cosmetics) is never removed, so foreign hats keep rendering in first person.
    internal IDisposable BeginMirrorView(Camera camera)
    {
        if (_disposed || _pack == null) return null;
        _mirrorCamera = camera;
        EnsureMirrorLocalLayer();
        return null;   // nothing to restore: the mirror is a mask, not a renderer-state scope
    }

    internal void SetMirrorCamera(Camera camera)
    {
        bool changed = camera != _mirrorCamera;
        _mirrorCamera = camera;
        if (camera != null && changed)
            _h.LogInfo($"[cosmetic:mirror-mask] camera='{CameraPath(camera?.transform)}' mask=0x{camera.cullingMask:X8} " +
                       $"layer{TvPlayerModelLayer}={(camera.cullingMask & (1 << TvPlayerModelLayer)) != 0} " +
                       $"name='{LayerMask.LayerToName(TvPlayerModelLayer)}' localLayer={_localLayer}");
        EnsureMirrorLocalLayer();
    }

    // ================================================================ picker state
    // The F12 picker (PickerUI) reads and writes the same equipped set the request file and the
    // old one-at-a-time panel used. Reconcile() stays the only place that creates/destroys
    // instances, so equipping still runs through CreateInstance/Attach and keeps layer 6, layer 23
    // and the material clone untouched.
    internal int CosmeticCount => _pack != null ? _pack.Cosmetics.Count : 0;
    internal int EquippedCount => _equipped.Count;
    internal bool IsEquipped(string id) => id != null && _equipped.Contains(id);

    internal int CosmeticType(string id)
    {
        try { return _pack != null && _pack.Cosmetics.TryGetValue(id, out var def) ? def.Type : -1; }
        catch { return -1; }
    }

    internal bool ToggleEquipped(string id, string trigger)
    {
        if (id == null || _pack == null || !_pack.Cosmetics.ContainsKey(id)) return false;
        bool on = !_equipped.Contains(id);
        if (on) _equipped.Add(id); else _equipped.Remove(id);
        _stateDirty = true;
        _equippedVersion++;
        _h.LogInfo($"[cosmetic:picker] {(on ? "equip" : "unequip")} id='{id}' trigger={trigger} " +
                   $"set={_equipped.Count} [{EquippedSummary()}] (reconcile runs in the same frame)");
        return on;
    }

    internal void ClearEquipped(string trigger, bool unequipAll = false)
    {
        int before = _equipped.Count;
        _equipped.Clear();
        _stateDirty = true;
        _equippedVersion++;
        // Two entry points, one mutation: `NONE` keeps its line, the picker's Unequip Cosmetics
        // button writes the line the brief asks for. Both only empty this set; Reconcile below is
        // still the only place that destroys the instances (layer 6 / layer 23 / material clone).
        _h.LogInfo(unequipAll
            ? $"[cosmetic:picker] unequip-all -> set=[NONE] removed={before} trigger={trigger} (reconcile runs in the same frame)"
            : $"[cosmetic:picker] clear trigger={trigger} removed={before} set=0");
    }

    internal string EquippedSummary()
    {
        if (_equipped.Count == 0) return "";
        var ids = EquippedIds();
        string s = string.Join(",", ids.ToArray());
        if (s.Length > 64) s = s.Substring(0, 61) + "...";
        return s;
    }

    // The set, sorted, as the wire/presentation paths see it (never truncated: EquippedSummary is
    // the log-friendly form).
    internal List<string> EquippedIds()
    {
        var ids = new List<string>(_equipped);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    // ================================================================ 3D preview
    // The .apack carries material textures only: CosmeticDef.Icon is a MoreCompany store icon id
    // (53..94) and those icon textures are not part of the pack. So there is nothing to use as a
    // thumbnail, and the picker renders each highlighted cosmetic from its real meshes/materials
    // through a private camera. The objects live on their own layer, 4 km below the lobby, are
    // never registered as markers, and are destroyed after the readback; the material cache is
    // only read, so the fitted instances keep their clones.
    internal sealed class PreviewBitmap
    {
        internal int Width;
        internal int Height;
        internal byte[] Rgba;       // top-down RGBA
        internal string Note;
    }

    private const int PreviewLayer = 6;   // the layer the fitted local cosmetics already render on
    private readonly Dictionary<string, PreviewBitmap> _previews = new(StringComparer.Ordinal);
    private bool _previewLayerLogged;

    internal PreviewBitmap GetPreview(string id, int size)
    {
        if (_disposed || _pack == null || string.IsNullOrEmpty(id)) return null;
        if (_previews.TryGetValue(id, out var cached)) return cached;
        PreviewBitmap bitmap = null;
        try
        {
            bitmap = BuildPreview(id, Mathf.Clamp(size, 32, 256));
        }
        catch (Exception e)
        {
            _h.LogWarning($"[cosmetic:preview-failed] id='{id}': {e}");
            bitmap = new PreviewBitmap { Width = 0, Height = 0, Rgba = null, Note = "preview failed" };
        }
        _previews[id] = bitmap;
        return bitmap;
    }

    private PreviewBitmap BuildPreview(string id, int size)
    {
        if (!_pack.Cosmetics.TryGetValue(id, out var def)) return null;
        if (!_previewLayerLogged)
        {
            _previewLayerLogged = true;
            _h.LogInfo($"[cosmetic:preview] layer={PreviewLayer} name='{LayerMask.LayerToName(PreviewLayer)}' " +
                       "(private camera renders this layer only; no marker registration)");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        GameObject root = null, camGo = null, lightGo = null;
        RenderTexture rt = null;
        Texture2D read = null;
        Camera cam = null;
        var previousActive = RenderTexture.active;
        try
        {
            root = new GameObject("CosmeticPreview_" + id);
            root.layer = PreviewLayer;
            root.transform.position = new Vector3(0f, -4000f, 0f);
            root.transform.localScale = def.RootScale;
            var renderers = new List<Renderer>();
            foreach (var node in def.Children) BuildPreviewNode(node, root.transform, renderers);
            if (renderers.Count == 0)
                return new PreviewBitmap { Width = 0, Height = 0, Rgba = null, Note = "no meshes" };

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++) bounds.Encapsulate(renderers[i].bounds);
            float radius = Mathf.Max(0.02f, bounds.extents.magnitude);
            Vector3 center = bounds.center;
            Vector3 dir = Quaternion.Euler(14f, 208f, 0f) * Vector3.forward;
            Vector3 eye = center + dir * (radius * 2.35f);

            camGo = new GameObject("CosmeticPreviewCam");
            camGo.layer = PreviewLayer;
            cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) return new PreviewBitmap { Width = 0, Height = 0, Rgba = null, Note = "no camera" };
            cam.transform.position = eye;
            cam.transform.LookAt(center);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.075f, 0.09f, 0.12f, 1f);
            cam.cullingMask = 1 << PreviewLayer;
            cam.fieldOfView = 34f;
            cam.nearClipPlane = Mathf.Max(0.005f, radius * 0.02f);
            cam.farClipPlane = radius * 12f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.enabled = false;

            lightGo = new GameObject("CosmeticPreviewLight");
            lightGo.layer = PreviewLayer;
            var light = lightGo.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
            if (light != null)
            {
                light.type = LightType.Directional;
                light.intensity = 1.25f;
                light.color = new Color(1f, 0.97f, 0.92f);
                light.cullingMask = 1 << PreviewLayer;
                light.transform.rotation = Quaternion.Euler(32f, 140f, 0f);
            }

            rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32)
            {
                name = "AlterumCosmeticPreviewRT", useMipMap = false, autoGenerateMips = false,
            };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("preview RT create failed");
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            read = new Texture2D(size, size, TextureFormat.RGB24, false);
            read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            read.Apply();
            RenderTexture.active = previousActive;
            var pixels = read.GetPixels32(0);
            int nonBg = 0;
            int bgR = pixels[0].r, bgG = pixels[0].g, bgB = pixels[0].b;
            for (int i = 0; i < pixels.Length; i++)
                if (Mathf.Abs(pixels[i].r - bgR) > 6 || Mathf.Abs(pixels[i].g - bgG) > 6 || Mathf.Abs(pixels[i].b - bgB) > 6) nonBg++;
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                int src = (size - 1 - y) * size;    // GetPixels32 is bottom-up; the panel is top-down
                int dst = y * size * 4;
                for (int x = 0; x < size; x++, src++, dst += 4)
                {
                    rgba[dst] = pixels[src].r; rgba[dst + 1] = pixels[src].g;
                    rgba[dst + 2] = pixels[src].b; rgba[dst + 3] = 255;
                }
            }
            sw.Stop();
            var bmp = new PreviewBitmap
            {
                Width = size, Height = size, Rgba = rgba,
                Note = $"3D {nonBg * 100 / (size * size)}%",
            };
            _h.LogInfo($"[cosmetic:preview] id='{id}' size={size}x{size} renderers={renderers.Count} " +
                       $"bounds={bounds.size} radius={radius:F3} nonBackgroundPixels={nonBg} coverage={(100f * nonBg / (size * size)):F1}% " +
                       $"ms={sw.ElapsedMilliseconds}");
            return bmp;
        }
        finally
        {
            RenderTexture.active = previousActive;
            try { if (cam != null) cam.targetTexture = null; } catch { }
            try { if (read != null) UnityEngine.Object.Destroy(read); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
            try { if (lightGo != null) UnityEngine.Object.Destroy(lightGo); } catch { }
            try { if (root != null) UnityEngine.Object.Destroy(root); } catch { }
        }
    }

    private void BuildPreviewNode(CosmeticPack.NodeDef node, Transform parent, List<Renderer> renderers)
    {
        var go = new GameObject(string.IsNullOrEmpty(node.Name) ? "node" : node.Name);
        go.layer = PreviewLayer;
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = node.Pos;
        t.localRotation = node.Rot;
        t.localScale = node.Scale;
        go.SetActive(node.Active);
        if (node.Mesh >= 0 && _pack.Meshes.TryGetValue(node.Mesh, out var meshDef))
        {
            var mesh = GetMesh(node.Mesh);
            if (mesh != null)
            {
                var filter = go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
                var renderer = go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
                if (filter != null && renderer != null)
                {
                    filter.sharedMesh = mesh;
                    var materials = new Material[node.Materials.Length];
                    for (int i = 0; i < node.Materials.Length; i++) materials[i] = GetMaterial(node.Materials[i]);
                    if (materials.Length == 1) renderer.sharedMaterial = materials[0];
                    else if (materials.Length > 1) renderer.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.enabled = true;
                    renderers.Add(renderer);
                }
            }
        }
        foreach (var child in node.Children) BuildPreviewNode(child, t, renderers);
    }

    internal void OnGUI()
    {
        // The F12 panel was replaced by PickerUI: the old overlay drew one button per cosmetic and
        // polled F12 itself, which would now double-handle the key. Nothing is drawn here.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop the body-on-screen coupling before the masks are restored: a retired generation must
        // not be called back into, and the restore below is the last word on the screen masks.
        try { MirrorPlayerVisibility.Current?.SetBodyOnScreenSink(null); } catch { }
        int destroyed = 0;
        foreach (var instance in _instances)
        {
            if (instance.Root != null) destroyed++;
            DestroyInstance(instance);
        }
        _instances.Clear();
        // Remote wearers go with the generation too: their roots are markers like any other, so a
        // retired generation that left them behind would hand the mirror sweep live cosmetics it no
        // longer owns.
        foreach (var set in new List<RemoteHats>(_remoteHats))
        {
            foreach (var instance in set.Instances) DestroyInstance(instance);
            set.Instances.Clear();
            set.Ids.Clear();
        }
        _remoteHats.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
        int markersLeft = CosmeticMarkers.RendererCount, rootsLeft = CosmeticMarkers.RootCount;
        CosmeticMarkers.Clear();
        RestoreScreenCameraMasks();
        _localLayer = -1;
        int meshCount = _meshes.Count, materialCount = _materials.Count, textureCount = _textures.Count;
        int destroyedMeshes = 0, destroyedMaterials = 0, destroyedTextures = 0;
        foreach (var material in _materials.Values) { try { if (material != null) { UnityEngine.Object.Destroy(material); destroyedMaterials++; } } catch { } }
        _materials.Clear();
        foreach (var texture in _textures.Values) { try { if (texture != null) { UnityEngine.Object.Destroy(texture); destroyedTextures++; } } catch { } }
        _textures.Clear();
        foreach (var mesh in _meshes.Values) { try { if (mesh != null) { UnityEngine.Object.Destroy(mesh); destroyedMeshes++; } } catch { } }
        _meshes.Clear();
        _previews.Clear();
        _pack = null;
        _mirrorCamera = null;
        _lastScreenCamera = null;
        _h.LogInfo($"[cosmetic:disposed] roots={destroyed} markersLeftAfterUnregister={markersLeft} rootsLeftAfterUnregister={rootsLeft} " +
                   $"markersLeftAfterClear={CosmeticMarkers.RendererCount} " +
                   $"meshesDestroyed={destroyedMeshes}/{meshCount} materialsDestroyed={destroyedMaterials}/{materialCount} " +
                   $"texturesDestroyed={destroyedTextures}/{textureCount} assetsRolledBack={_assetsRolledBack}");
    }

    // ================================================================ pack / caches

    private void LoadPack()
    {
        try
        {
            var asm = typeof(CosmeticsRuntime).Assembly;
            string resource = null;
            foreach (var name in asm.GetManifestResourceNames())
                if (name.EndsWith("cosmetics.apack", StringComparison.OrdinalIgnoreCase)) { resource = name; break; }
            if (resource == null)
            {
                _packFailure = "no cosmetics.apack resource";
                _h.LogError($"[cosmetic:pack-missing] {_packFailure} in {asm.GetName().Name}");
                return;
            }
            using (var stream = asm.GetManifestResourceStream(resource))
            {
                if (stream == null) { _packFailure = "resource stream null"; return; }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                _pack = CosmeticPack.Load(bytes);
            }
            try { if (!_unlitFallback) GetTemplate(); } catch { }
            _h.LogInfo($"[cosmetic:pack] resource='{resource}' bytes={_pack.Bytes.Length} generator='{_pack.Generator}' " +
                       $"cosmetics={_pack.Cosmetics.Count} meshes={_pack.Meshes.Count} materials={_pack.Materials.Count} textures={_pack.Textures.Count} " +
                       $"sourceHeadWidth={_pack.SourceHead.Width:F4} sourceHeadTop={_pack.SourceHead.Top:F4} " +
                       $"authoringUnitScale={_pack.SourceHead.AuthoringUnitScale:F4}");
        }
        catch (Exception e)
        {
            _packFailure = e.Message;
            _h.LogError($"[cosmetic:pack-invalid] {e}");
        }
    }

    internal List<CosmeticPack.CosmeticDef> SortedCosmetics()
    {
        // The picker ticks before this runtime's first Update on a cold start: an unloaded pack is
        // an empty list, never a throw.
        var list = new List<CosmeticPack.CosmeticDef>();
        if (_pack != null) list.AddRange(_pack.Cosmetics.Values);
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    private Mesh GetMesh(int id)
    {
        if (_meshes.TryGetValue(id, out var cached))
        {
            // Unity's != is false for a destroyed wrapper too. A scene load runs an asset sweep that
            // destroys anything the scene no longer references; at that instant an equipped cosmetic's
            // meshes are held only by this cache (its root died with the old scene), so the sweep takes
            // them. Returning the dead wrapper here made BuildNode silently build no renderer at all -
            // an empty instance that Reconcile destroyed and rebuilt every frame. Rebuild instead.
            if (cached != null) return cached;
            _meshes.Remove(id);
            _h.LogInfo($"[cosmetic:mesh-stale] id={id} cached mesh was destroyed by an asset unload; rebuilding");
        }
        if (!_pack.Meshes.TryGetValue(id, out var def)) return null;
        var bytes = _pack.Bytes;
        var vertices = new Vector3[def.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            int o = def.Verts.Off + i * 12;
            vertices[i] = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
        }
        // Transactional: the native Mesh is registered before the first upload. A throw from
        // SetTriangles or a property setter then destroys it, instead of leaving an allocation
        // that Dispose() cannot discover (failed equips retry every 10 s).
        var mesh = new Mesh();
        _meshes[id] = mesh;
        try
        {
            // Assets referenced only from a hot-assembly cache are invisible to the scene's asset
            // sweep, so the pool destroys them on the next scene load. Pin them: Dispose() is the
            // owner and the only thing that destroys them.
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            mesh.name = "Cosmetic_" + def.Name;
            if (def.IndexFormat > 16) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
            if (def.HasNormals && def.Norms != null)
            {
                var normals = new Vector3[def.VertexCount];
                for (int i = 0; i < normals.Length; i++)
                {
                    int o = def.Norms.Off + i * 12;
                    normals[i] = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
                }
                mesh.normals = new Il2CppStructArray<Vector3>(normals);
            }
            else mesh.RecalculateNormals();
            if (def.HasUv && def.Uvs != null)
            {
                var uvs = new Vector2[def.VertexCount];
                for (int i = 0; i < uvs.Length; i++)
                {
                    int o = def.Uvs.Off + i * 8;
                    uvs[i] = new Vector2(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4));
                }
                mesh.uv = new Il2CppStructArray<Vector2>(uvs);
            }
            if (def.HasTangents && def.Tangs != null)
            {
                var tangents = new Vector4[def.VertexCount];
                for (int i = 0; i < tangents.Length; i++)
                {
                    int o = def.Tangs.Off + i * 16;
                    tangents[i] = new Vector4(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4),
                                              BitConverter.ToSingle(bytes, o + 8), BitConverter.ToSingle(bytes, o + 12));
                }
                mesh.tangents = new Il2CppStructArray<Vector4>(tangents);
            }
            mesh.subMeshCount = def.Subs.Length;
            for (int s = 0; s < def.Subs.Length; s++)
            {
                var sub = def.Subs[s];
                var indices = new int[sub.IndexCount];
                for (int i = 0; i < indices.Length; i++) indices[i] = BitConverter.ToInt32(bytes, def.Indices.Off + (sub.IndexStart + i) * 4);
                mesh.SetTriangles(new Il2CppStructArray<int>(indices), s, false);
            }
            mesh.RecalculateBounds();
            int tris = 0;
            foreach (var sub in def.Subs) tris += sub.IndexCount / 3;
            _h.LogInfo($"[cosmetic:mesh] id={id} name='{def.Name}' verts={def.VertexCount} tris={tris} submeshes={def.Subs.Length} " +
                       $"uv={def.HasUv} normals={def.HasNormals} tangents={def.HasTangents} bounds={mesh.bounds.size}");
            return mesh;
        }
        catch (Exception e)
        {
            RollbackMesh(id, mesh);
            _h.LogWarning($"[cosmetic:mesh-failed] id={id} name='{def.Name}' native mesh destroyed: {e.Message}");
            throw;
        }
    }

    private void RollbackMesh(int id, Mesh mesh)
    {
        _meshes.Remove(id);
        _assetsRolledBack++;
        try { if (mesh != null) UnityEngine.Object.Destroy(mesh); } catch { }
    }

    private Texture2D GetTexture(int id)
    {
        if (id < 0) return null;
        if (_textures.TryGetValue(id, out var cached))
        {
            if (cached != null) return cached;
            _textures.Remove(id);
            _h.LogInfo($"[cosmetic:texture-stale] id={id} cached texture was destroyed by an asset unload; rebuilding");
        }
        if (!_pack.Textures.TryGetValue(id, out var def)) return null;
        var png = new byte[def.Data.Len];
        Buffer.BlockCopy(_pack.Bytes, def.Data.Off, png, 0, def.Data.Len);
        int width, height;
        byte[] rgba;
        try { rgba = PngDecoder.DecodeRgbaBottomUp(png, out width, out height); }
        catch (Exception e)
        {
            _h.LogWarning($"[cosmetic:texture-failed] id={id} name='{def.Name}' png={def.Data.Len} bytes: {e.Message}");
            return null;
        }
        int levels;
        var chain = PngDecoder.BuildMipChain(rgba, width, height, out levels);
        // Transactional: register before the upload; a throw from LoadRawTextureData/Apply destroys
        // the native Texture2D instead of orphaning it.
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
        _textures[id] = texture;
        try
        {
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.name = "Cosmetic_" + def.Name;
            texture.LoadRawTextureData(new Il2CppStructArray<byte>(chain));
            texture.Apply(true, true);
            texture.wrapModeU = (TextureWrapMode)def.WrapU;
            texture.wrapModeV = (TextureWrapMode)def.WrapV;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            _h.LogInfo($"[cosmetic:texture] id={id} name='{def.Name}' size={texture.width}x{texture.height} png={def.Data.Len} " +
                       $"rawChain={chain.Length} mips={texture.mipmapCount}/{levels} sourceFormat={def.SourceFormat}");
            return texture;
        }
        catch (Exception e)
        {
            RollbackTexture(id, texture);
            _h.LogWarning($"[cosmetic:texture-failed] id={id} name='{def.Name}' native texture destroyed: {e.Message}");
            throw;
        }
    }

    private void RollbackTexture(int id, Texture2D texture)
    {
        _textures.Remove(id);
        _assetsRolledBack++;
        try { if (texture != null) UnityEngine.Object.Destroy(texture); } catch { }
    }

    private bool EnsureShader()
    {
        if (_litShader != null) return true;
        try
        {
            _litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (_litShader != null && _litShader.isSupported)
            {
                _h.LogInfo($"[cosmetic:shader] using '{_litShader.name}' (Shader.Find)");
                return true;
            }
            _h.LogWarning($"[cosmetic:shader-unavailable] Universal Render Pipeline/Lit found={_litShader != null} " +
                          $"supported={_litShader != null && _litShader.isSupported}");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:shader] lookup failed: {e.Message}"); }
        _litShader = Shader.Find("Universal Render Pipeline/Unlit");
        _unlitFallback = _litShader != null;
        _h.LogWarning($"[cosmetic:shader-fallback] unlit={_unlitFallback} shader='{_litShader?.name}' (diagnostic only)");
        return _litShader != null;
    }

    // Cloning a material the game already renders keeps the working URP/Lit variant; Shader.Find
    // alone can return a shader whose Lit variants were stripped.
    private Material GetTemplate()
    {
        if (_template != null) return _template;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Material>());
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i].TryCast<Material>();
                if (m == null) continue;
                var shader = m.shader;
                if (shader == null) continue;
                if (shader.name != "Universal Render Pipeline/Lit") continue;
                if (!m.HasProperty("_BaseMap")) continue;
                if (m.name != null && m.name.StartsWith("Cosmetic_", StringComparison.Ordinal)) continue;  // ours
                if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) continue;  // prefer opaque
                _template = m;
                _templateName = m.name;
                _h.LogInfo($"[cosmetic:shader-template] cloning game material '{m.name}' shader='{shader.name}'");
                return _template;
            }
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:shader-template] scan failed: {e.Message}"); }
        _h.LogWarning("[cosmetic:shader-template] no game URP/Lit material found; using Shader.Find material");
        return null;
    }

    private Material GetMaterial(int id)
    {
        if (id < 0) return null;
        if (_materials.TryGetValue(id, out var cached))
        {
            if (cached != null) return cached;
            _materials.Remove(id);
            _h.LogInfo($"[cosmetic:material-stale] id={id} cached material was destroyed by an asset unload; rebuilding");
        }
        if (!_pack.Materials.TryGetValue(id, out var def)) return null;
        if (!EnsureShader()) return null;
        Material material = null;
        var template = _unlitFallback ? null : GetTemplate();
        try
        {
            material = template != null ? new Material(template) : new Material(_litShader);
        }
        catch (Exception e)
        {
            _h.LogWarning($"[cosmetic:material] '{def.Name}' creation failed: {e.Message}");
            return null;
        }
        // Transactional: register before the first property setter; a throw below destroys the
        // native material (its texture is either cached or already rolled back).
        _materials[id] = material;
        try
        {
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            material.name = "Cosmetic_" + def.Name;
            var texture = GetTexture(def.BaseMap);
            if (material.HasProperty("_BaseMap"))
            {
                if (texture != null) material.SetTexture("_BaseMap", texture);
                material.SetColor("_BaseColor", def.BaseColor);
            }
            else if (material.HasProperty("_MainTex"))
            {
                if (texture != null) material.SetTexture("_MainTex", texture);
                material.SetColor("_Color", def.BaseColor);
            }
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", def.Metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", def.Smoothness);
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.black);
                try { material.DisableKeyword("_EMISSION"); } catch { }
            }
            _h.LogInfo($"[cosmetic:material] id={id} name='{def.Name}' shader='{material.shader?.name}' template='{_templateName}' " +
                       $"base=({def.BaseColor.r:F3},{def.BaseColor.g:F3},{def.BaseColor.b:F3},{def.BaseColor.a:F3}) " +
                       $"metallic={def.Metallic:F3} smoothness={def.Smoothness:F3} baseMap={(texture == null ? "none" : texture.name)} " +
                       $"transparent={def.Transparent} (normal/mask maps omitted: COSMETIC_MATERIAL_APPROX)");
            return material;
        }
        catch (Exception e)
        {
            RollbackMaterial(id, material);
            _h.LogWarning($"[cosmetic:material-failed] id={id} name='{def.Name}' native material destroyed: {e.Message}");
            throw;
        }
    }

    private void RollbackMaterial(int id, Material material)
    {
        _materials.Remove(id);
        _assetsRolledBack++;
        try { if (material != null) UnityEngine.Object.Destroy(material); } catch { }
    }

    // ================================================================ local player / fit

    private void PollBind()
    {
        if (Time.unscaledTime < _nextBindPoll) return;
        _nextBindPoll = Time.unscaledTime + BindPollSeconds;
        try
        {
            var owner = FindLocalOwner();
            if (owner == null) { _owner = null; _body = null; _head = null; _fitReady = false; return; }
            _owner = owner;
            ThirdViewPlayerModel body = null;
            var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < models.Length; i++)
            {
                var m = models[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || !MirrorPlayerVisibility.Under(m.transform, owner)) continue;
                if (m.gameObject.activeInHierarchy) { body = m; break; }
            }
            if (body == null) { _body = null; _head = null; _fitReady = false; return; }
            var bones = body.Bones;
            var head = bones == null ? null : bones.Head;
            if (head == null) { _body = body; _head = null; _fitReady = false; return; }

            int bodyId = SafeInstanceId(body), headId = SafeInstanceId(head);
            // The worn mod body can change without the native head transform changing (the wardrobe
            // swaps our body object under the same skeleton), and each one needs its own anchor.
            var wornNow = ModelRoster.WornSpec(body.transform);
            string wornSlug = wornNow == null ? "native" : wornNow.Slug;
            // The head socket's lossy scale is not constant: a map character is scaled in after it
            // spawns (measured 1.0 -> 1.54 within the first seconds on both sides), and a local-space
            // placement made before that settle is then multiplied by the new scale - the hat drifts
            // up and away from the head. A scale change is therefore a refit reason, exactly like a
            // new head: the seat is re-measured from the geometry at the scale it is drawn at.
            bool scaleChanged = _fitReady && !NearLossy(head.lossyScale, _headLossy);
            bool same = _fitReady && _bodyPtr == body.Pointer && _headPtr == head.Pointer &&
                        _bodyId == bodyId && _headId == headId && !scaleChanged &&
                        string.Equals(_wornSlug, wornSlug, StringComparison.Ordinal);
            if (scaleChanged)
                _h.LogInfo($"[cosmetic:refit] head '{head.name}' lossy {_headLossy} -> {head.lossyScale}: re-measuring the seat");
            // Publish the complete new binding BEFORE anything attaches: ComputeFit re-parents every
            // existing instance through ResolveSocket, which must see the replacement head, not the
            // previous (possibly destroyed) one. Pointers alone are not identity here - a recycled
            // address would compare equal and skip the refit - so instance ids are part of the check.
            _body = body;
            _head = head;
            _modelRoot = body.transform;
            if (same) return;
            _bodyPtr = body.Pointer; _headPtr = head.Pointer;
            _bodyId = bodyId; _headId = headId;
            if (!string.Equals(_wornSlug, wornSlug, StringComparison.Ordinal))
                _h.LogInfo($"[cosmetic:worn] slug='{wornSlug}' spec={(wornNow == null ? "null" : wornNow.BodyName)} anchor={(ModelRoster.AnchorFor(wornNow) != null)}");
            _wornSlug = wornSlug;
            _chestResolved = false; _chestBone = null; _socketScanLogged = false;   // new rig: re-resolve the spine socket
            // Roots that died with the previous scene/model can never be re-parented; drop them
            // (and their markers) before the refit, then let Reconcile recreate them.
            RetireDeadInstances("rebind");
            ComputeFit(body, head);
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:bind] poll failed: {e.Message}"); }
    }

    private static bool NearLossy(Vector3 a, Vector3 b)
    {
        float scale = Mathf.Max(1e-3f, Mathf.Max(Mathf.Abs(a.x), Mathf.Max(Mathf.Abs(a.y), Mathf.Abs(a.z))));
        return Mathf.Abs(a.x - b.x) <= 0.01f * scale &&
               Mathf.Abs(a.y - b.y) <= 0.01f * scale &&
               Mathf.Abs(a.z - b.z) <= 0.01f * scale;
    }

    private static int SafeInstanceId(UnityEngine.Object obj)
    {
        try { return obj == null ? 0 : obj.GetInstanceID(); }
        catch { return 0; }
    }

    private void RetireDeadInstances(string why)
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var instance = _instances[i];
            if (instance.Root != null) continue;
            _h.LogInfo($"[cosmetic:invalid] id={instance.Id} reason=root-destroyed why={why} instances={_instances.Count - 1}");
            DestroyInstance(instance);
            _instances.RemoveAt(i);
        }
    }

    private Transform FindLocalOwner()
    {
        try
        {
            var providers = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
            for (int i = 0; i < providers.Length; i++)
            {
                var provider = providers[i].TryCast<CurrentPlayerProvider>();
                if (provider == null) continue;
                var player = provider.CurrentPlayer;
                if (player != null && player.transform != null) return player.transform;
            }
        }
        catch { }
        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i].TryCast<Player>();
                if (player == null || !player.IsOwner) continue;
                if (player.transform == null || string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                return player.transform;
            }
        }
        catch { }
        return null;
    }

    private void ComputeFit(ThirdViewPlayerModel body, Transform head)
    {
        _fitReady = false;
        try
        {
            _headLossy = head.lossyScale;
            float sx = Mathf.Abs(_headLossy.x), sy = Mathf.Abs(_headLossy.y), sz = Mathf.Abs(_headLossy.z);
            float scale = Mathf.Pow(sx * sy * sz, 1f / 3f);
            bool uniform = Mathf.Abs(sx - sy) < 1e-3f * scale && Mathf.Abs(sy - sz) < 1e-3f * scale;
            if (!uniform) _h.LogWarning($"[cosmetic:fit-nonuniform] headLossyScale={_headLossy} using geometric mean {scale:F5}");

            var modelRoot = _modelRoot != null ? _modelRoot : body.transform;
            var rTarget = Quaternion.Inverse(modelRoot.rotation) * head.rotation;   // head-local -> model-local
            var rSrc = _pack.SourceHead.RotRelModel;                               // source head-local -> source model-local
            var headToHat = Quaternion.Inverse(rSrc) * rTarget;                    // head-local -> hat-local
            _hatRotation = head.rotation * (Quaternion.Inverse(rTarget) * rSrc);   // world rotation of the cosmetic root

            // A mod body is NOT the game skeleton: its head geometry sits wherever the bake put it,
            // and the native head-weighted box measured below belongs to the hidden game head. So the
            // seat is measured from the geometry that is actually drawn: the worn model's own head
            // vertices, skinned to where they are now, in the same hat axes the native path uses.
            // That removes the whole class of "anchor measured against another skeleton" bugs (the
            // lobby donor vs the map's body, a map client's different native body, any head lossy
            // scale), because nothing about the fit is carried over from a different context.
            // The stored per-model table stays as the fallback when that mesh is unreadable.
            var worn = ModelRoster.WornSpec(modelRoot);
            var hat = ModelRoster.AnchorFor(worn);
            _wornHeadWorldValid = false;
            float width = 0f, top = 0f, centerX = 0f, centerZ = 0f;
            string source = null;
            bool measuredWorn = false;
            if (worn != null)
            {
                // Always measured, even when the harness forces the stored table: the seat log then
                // reports the old rule's error with the same geometry the new rule uses.
                measuredWorn = MeasureWornHead(modelRoot, head, headToHat,
                                               out width, out top, out centerX, out centerZ, out source);
            }

            if (measuredWorn && !_forceAnchorRule)
            {
                _seatRule = "measured-worn";
                if (!ApplyMeasuredBox(width * scale, top * scale, centerX * scale, centerZ * scale, headToHat, source)) return;
                _h.LogInfo($"[cosmetic:fit] id=* source={_fitSource} rule={_seatRule} headLossyScale={_headLossy} " +
                           $"fitScale={_fitScale:F5} shift={_fitShift} sourceWidth={_pack.SourceHead.Width:F4} " +
                           $"modelRoot='{modelRoot.name}' headBone='{head.name}' worn='{worn.Slug}'");
                foreach (var instance in _instances) Attach(instance);
                LogSeat("fit", body, head, modelRoot, worn);
                return;
            }

            if (worn != null && hat != null)
            {
                _fitScale = hat.Scale;
                _localRot = hat.Rotation;
                _localPos = hat.Offset;
                // The anchor is authored against the GAME head bone, but a rigged custom body draws
                // its own head: on a skeleton that is not the donor's (a map client keeps its native
                // one) the two head positions differ and the hat floats. Shift the anchor by that
                // difference, in head-local space, so the hat lands on the head that is on screen.
                try
                {
                    if (SahurRig.TryGetWornHead(modelRoot, out var rigHead) && rigHead != null)
                    {
                        var delta = Quaternion.Inverse(head.rotation) * (rigHead.position - head.position);
                        if (delta.sqrMagnitude > 0.000001f)
                        {
                            _localPos += delta;
                            _h.LogInfo($"[cosmetic:anchor-shift] rig head is {delta.magnitude:F3} m from the native " +
                                       $"head bone in head-local space; the hat follows the visible head (delta={delta})");
                        }
                    }
                }
                catch { }
                _hatRotation = head.rotation * _localRot;
                _fitShift = Quaternion.Inverse(_localRot) * _localPos;   // hat-local shift, as the native path produces
                _fitSource = $"anchor('{worn.Slug}')";
                _seatRule = _forceAnchorRule ? "stored-anchor(forced)" : "stored-anchor";
                _fitReady = true;
                CaptureSlotPlacements();
                _nextFitLog = 0f;   // log the placement this refit produced, once
                _h.LogInfo($"[cosmetic:fit] id=* source={_fitSource} rule={_seatRule} headLossyScale={_headLossy} fitScale={_fitScale:F5} " +
                           $"anchorOffset={_localPos} anchorRot={_localRot.eulerAngles} " +
                           $"sourceWidth={_pack.SourceHead.Width:F4} modelRoot='{modelRoot.name}' headBone='{head.name}' " +
                           $"headLocalWorldScale={(head.lossyScale)} worn='{worn.Slug}' measuredWorn={measuredWorn}");
                foreach (var instance in _instances) Attach(instance);
                LogSeat("fit", body, head, modelRoot, worn);
                return;
            }

            if (worn != null && hat == null)
                _h.LogWarning($"[cosmetic:seat] worn '{worn.Slug}' has no stored anchor and its head mesh could not be " +
                              "measured; falling back to the game neck/head ratio (the hat will not sit on this body)");

            if (MeasureHeadMesh(_body != null ? _body.transform : null, head, headToHat,
                                out width, out top, out centerX, out centerZ, out source))
            {
                width *= scale; top *= scale; centerX *= scale; centerZ *= scale;
            }
            else
            {
                // Fallback: the head renderer's mesh is not CPU readable. Use the source rig's
                // measured head-width/head-top per neck->head bone distance, applied to Alterum's
                // own measured neck->head distance.
                var bones = body.Bones;
                var neck = bones == null ? null : bones.Neck;
                float distance = neck != null ? Vector3.Distance(head.position, neck.position) : 0f;
                if (distance <= 1e-5f)
                {
                    _h.LogWarning("[cosmetic:fit-unavailable] no readable head mesh and no neck bone; cosmetics stay hidden");
                    return;
                }
                width = _pack.SourceHead.WidthPerNeckToHead * distance;
                top = _pack.SourceHead.TopPerNeckToHead * distance;
                centerX = 0f;
                centerZ = 0f;
                source = $"neck-ratio(d={distance:F4})";
                _h.LogWarning("[cosmetic:fit-fallback] head mesh not CPU readable; using source neck/head ratio " +
                              $"(widthPerNeck={_pack.SourceHead.WidthPerNeckToHead:F4} topPerNeck={_pack.SourceHead.TopPerNeckToHead:F4} " +
                              $"distance={distance:F4} -> width={width:F4} top={top:F4})");
            }

            _seatRule = "measured-native";
            if (!ApplyMeasuredBox(width, top, centerX, centerZ, headToHat, source)) return;

            // P2.9: show what the unreadable-mesh fallback would produce here too, so the
            // unit-invariant ratios are measurable even when the head mesh is readable.
            var src = _pack.SourceHead;
            string fallbackProbe = "n/a";
            try
            {
                var neckBone = body.Bones == null ? null : body.Bones.Neck;
                if (neckBone != null)
                {
                    float d = Vector3.Distance(head.position, neckBone.position);
                    fallbackProbe = $"d={d:F4} width={src.WidthPerNeckToHead * d:F4} top={src.TopPerNeckToHead * d:F4}";
                }
            }
            catch { }

            _h.LogInfo($"[cosmetic:fit] id=* source={source} headLossyScale={_headLossy} fitScale={_fitScale:F5} " +
                       $"sourceBox(width={src.Width:F4} top={src.Top:F4} cx={src.CenterX:F4} cz={src.CenterZ:F4} authoringUnitScale={src.AuthoringUnitScale:F4}) " +
                       $"targetBox(width={width:F4} top={top:F4} cx={centerX:F4} cz={centerZ:F4}) shift={_fitShift} " +
                       $"fallbackPerNeck=(w={src.WidthPerNeckToHead:F4},top={src.TopPerNeckToHead:F4}) fallbackWouldBe=({fallbackProbe}) " +
                       $"rule={_seatRule} modelRoot='{_modelRoot?.name}' headBone='{head.name}'");
            foreach (var instance in _instances) Attach(instance);
            LogSeat("fit", body, head, modelRoot, worn);
        }
        catch (Exception e)
        {
            _h.LogError($"[cosmetic:fit-failed] {e}");
        }
    }

    // One placement formula for every body: the hat's source-head box is seated on the measured head
    // box. `width/top/cx/cz` are in hat axes and already multiplied by the head's own lossy scale,
    // exactly what MeasureHeadMesh produces for a native head, so Attach's world-space division by
    // the socket's lossy scale lands the hat on the geometry that is drawn.
    private bool ApplyMeasuredBox(float width, float top, float centerX, float centerZ, Quaternion headToHat, string source)
    {
        var src = _pack.SourceHead;
        if (width <= 1e-5f || src.Width <= 1e-5f)
        {
            _h.LogError($"[cosmetic:fit-invalid] id=* width={width:F5} sourceWidth={src.Width:F5}");
            return false;
        }
        _fitScale = width / src.Width;
        _fitShift = new Vector3(centerX - src.CenterX * _fitScale,
                                top - src.Top * _fitScale,
                                centerZ - src.CenterZ * _fitScale);
        _localRot = Quaternion.Inverse(headToHat);
        _localPos = _localRot * _fitShift;
        _fitSource = source;
        _fitReady = true;
        CaptureSlotPlacements();
        _nextFitLog = 0f;
        return true;
    }

    // Convert the head-frame calibration into every non-head socket's own local frame, once per fit.
    // This is the only place the conversion is ever evaluated: it reads the head and the sockets at
    // the same instant, so the result is the calibrated placement expressed in that socket's axes.
    // Attach then writes the stored pair unchanged, which is what makes a chest/hip/wrist item
    // invariant to the head pose at equip time and to every pose change since the fit. The hat keeps
    // the live head path (because socket == head it is exactly `_localRot` for every pose).
    private void CaptureSlotPlacements()
    {
        _slotRot.Clear();
        _slotPos.Clear();
        var head = _head;
        if (head == null) return;
        Quaternion hatWorldRot;
        Vector3 hatWorldShift;
        try
        {
            hatWorldRot = head.rotation * _localRot;    // the fit's cosmetic-root world rotation
            hatWorldShift = hatWorldRot * _fitShift;    // world offset the head path would apply
        }
        catch { return; }
        // The description of the world rotation the last refit produced, for the head-gone fallback.
        _hatRotation = hatWorldRot;
        var sb = new StringBuilder();
        foreach (int type in new[] { 2, 3, 4 })
        {
            Transform socket;
            try { socket = SocketForType(type); } catch { continue; }
            if (socket == null) continue;
            try
            {
                var socketRot = socket.rotation;
                var lossy = socket.lossyScale;
                var local = Quaternion.Inverse(socketRot) * hatWorldShift;
                _slotRot[type] = Quaternion.Inverse(socketRot) * hatWorldRot;
                _slotPos[type] = new Vector3(local.x / Mathf.Max(1e-6f, lossy.x),
                                             local.y / Mathf.Max(1e-6f, lossy.y),
                                             local.z / Mathf.Max(1e-6f, lossy.z));
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"type{type}='{socket.name}'");
            }
            catch { }
        }
        if (sb.Length > 0)
            _h.LogInfo($"[cosmetic:slot-frame] captured the calibrated placement in each socket's own " +
                       $"local frame: {sb} (head='{head.name}'; these are constants until the next fit)");
    }

    // The socket a slot resolves to for the LOCAL body. Must mirror ResolveSocket's local choices:
    // the capture and the attach have to agree, or the stored frame belongs to a different bone.
    private Transform SocketForType(int type)
    {
        var bones = _body == null ? null : _body.Bones;
        switch (type)
        {
            case 4: return bones != null && bones.Pelvis != null ? bones.Pelvis : _head;
            case 3: return bones != null && bones.RightWrist != null ? bones.RightWrist : _head;
            case 2:
            {
                var chest = ResolveChestBone();
                if (chest != null && !string.Equals(chest.name, "pelvis", StringComparison.OrdinalIgnoreCase)) return chest;
                return bones != null && bones.Pelvis != null ? bones.Pelvis : _head;
            }
            default: return _head;
        }
    }

    // The seat as two world numbers: the top of the head geometry that is drawn and the bottom of the
    // cosmetics that are drawn. `gap` > 0 means the hat floats above the head; < 0 means it is sunk.
    private void LogSeat(string when, ThirdViewPlayerModel body, Transform head, Transform modelRoot, ModelSpec worn)
    {
        try
        {
            // A request-driven probe re-measures right now: the body may have walked since the refit,
            // and the hat is a child of the head bone, so only a live head box makes the gap honest.
            if (when == "probe" && head != null && modelRoot != null)
            {
                var rT = Quaternion.Inverse(modelRoot.rotation) * head.rotation;
                var rS = _pack.SourceHead.RotRelModel;
                float w, t, cx, cz; string src;
                MeasureWornHead(modelRoot, head, Quaternion.Inverse(rS) * rT, out w, out t, out cx, out cz, out src);
            }
            var sb = new StringBuilder();
            sb.Append($"[hatseat] when={when} slug='{(worn == null ? "native" : worn.Slug)}' rule='{_seatRule}' ");
            sb.Append($"gameHead='{head.name}' pos={V3(head.position)} rot={V3(head.rotation.eulerAngles)} lossy={V3(head.lossyScale)} ");
            Transform modelHead = null;
            if (worn != null) TryWornHeadBone(modelRoot, out modelHead);
            sb.Append($"modelHead='{(modelHead == null ? "none" : modelHead.name)}' " +
                      $"pos={(modelHead == null ? "-" : V3(modelHead.position))} ");
            // The measured head box in world space is what the seat must touch; it is refreshed by
            // MeasureWornHead on every refit and stays valid for the seat log.
            string headTop = _wornHeadWorldValid ? $"{_wornHeadWorld.max.y:F4}" : "n/a";
            sb.Append($"wornHeadTop(world.y)={headTop} wornHeadWorld={(_wornHeadWorldValid ? $"min={V3(_wornHeadWorld.min)} max={V3(_wornHeadWorld.max)}" : "-")} ");
            int logged = 0;
            foreach (var instance in _instances)
            {
                if (instance?.Root == null || instance.Renderers.Count == 0) continue;
                var b = new Bounds(instance.Root.transform.position, Vector3.zero);
                bool first = true;
                foreach (var r in instance.Renderers)
                {
                    if (r == null) continue;
                    if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
                }
                if (first) continue;
                float gap = b.min.y - (_wornHeadWorldValid ? _wornHeadWorld.max.y : head.position.y);
                sb.Append($"hatRoot={V3(instance.Root.transform.position)} hatBottom(y)={b.min.y:F4} hatWorld={{" +
                          $"min={V3(b.min)} max={V3(b.max)}}} gap={(gap >= 0f ? "+" : "")}{gap:F3}m ");
                logged++;
            }
            if (logged == 0) sb.Append("hat=none ");
            _h.LogInfo(sb.ToString().TrimEnd());
        }
        catch (Exception e) { _h.LogWarning($"[hatseat] log failed: {e.Message}"); }
    }

    private static string V3(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

    // The same seat numbers for a remote wearer as LogSeat prints for the local one, so the two paths
    // can be compared from the same log without reading two different formats.
    private void LogRemoteSeat(string label, Transform head, Transform modelRoot, RemoteHats set)
    {
        try
        {
            Transform modelHead = null;
            TryWornHeadBone(modelRoot, out modelHead);
            var sb = new StringBuilder();
            sb.Append($"[hatseat] when=remote {label} head='{head.name}' pos={V3(head.position)} rot={V3(head.rotation.eulerAngles)} lossy={V3(head.lossyScale)} ");
            sb.Append($"modelHead='{(modelHead == null ? "none" : modelHead.name)}' pos={(modelHead == null ? "-" : V3(modelHead.position))} ");
            // `_wornHeadWorld` is the box of the last body of OURS that was measured. A native remote
            // head has no such box (its seat came from the game's own head mesh) and the local body's
            // box belongs to a different person, so the gap is only reported for a worn-body seat.
            bool ours = set.Rule != null && set.Rule.StartsWith("measured-worn", StringComparison.Ordinal);
            sb.Append($"wornHeadTop(world.y)={(ours && _wornHeadWorldValid ? $"{_wornHeadWorld.max.y:F4}" : (ours ? "n/a" : "n/a(native)"))} ");
            int logged = 0;
            foreach (var instance in set.Instances)
            {
                if (instance?.Root == null || instance.Renderers.Count == 0) continue;
                var b = new Bounds(instance.Root.transform.position, Vector3.zero);
                bool first = true;
                foreach (var r in instance.Renderers)
                {
                    if (r == null) continue;
                    if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
                }
                if (first) continue;
                sb.Append($"hatRoot={V3(instance.Root.transform.position)} hatBottom(y)={b.min.y:F4} ");
                if (!ours) sb.Append("gap=n/a(native) ");
                else
                {
                    float gap = b.min.y - (_wornHeadWorldValid ? _wornHeadWorld.max.y : head.position.y);
                    sb.Append($"gap={(gap >= 0f ? "+" : "")}{gap:F3}m ");
                }
                logged++;
            }
            if (logged == 0) sb.Append("hat=none ");
            _h.LogInfo(sb.ToString().TrimEnd());
        }
        catch (Exception e) { _h.LogWarning($"[hatseat] remote log failed: {e.Message}"); }
    }

    // The worn model's own head bone (the rig node the head geometry is weighted to), searched under
    // the body's meshes. Null for a body with no head-weighted renderer.
    private static bool TryWornHeadBone(Transform modelRoot, out Transform head)
    {
        head = null;
        if (modelRoot == null) return false;
        try
        {
            var smrs = modelRoot.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            for (int r = 0; r < smrs.Length; r++)
            {
                var smr = smrs[r].TryCast<SkinnedMeshRenderer>();
                if (smr == null || !ModelRoster.IsOurObjectName(smr.gameObject.name ?? "")) continue;
                var bones = smr.bones;
                for (int b = 0; bones != null && b < bones.Length; b++)
                {
                    if (bones[b] == null || !IsHeadBoneName(bones[b].name)) continue;
                    head = bones[b];
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    // Head-weighted vertices of the WORN body, skinned to where they are right now: the union box in
    // world space (for the seat log) and the same box in hat axes (for the placement). Measured from
    // our own meshes only - the native body under them is hidden and is not what the player sees.
    private bool MeasureWornHead(Transform modelRoot, Transform head, Quaternion headToHat,
        out float width, out float top, out float centerX, out float centerZ, out string source)
    {
        width = top = centerX = centerZ = 0f;
        source = null;
        if (modelRoot == null || head == null) return false;
        var smrs = modelRoot.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        var headWorldInv = head.worldToLocalMatrix;
        float hminX = float.MaxValue, hmaxX = float.MinValue, hminY = float.MaxValue, hmaxY = float.MinValue;
        float hminZ = float.MaxValue, hmaxZ = float.MinValue;
        var wmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var wmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int counted = 0;
        var used = new StringBuilder();
        for (int r = 0; r < smrs.Length; r++)
        {
            var smr = smrs[r].TryCast<SkinnedMeshRenderer>();
            if (smr == null) continue;
            if (!ModelRoster.IsOurObjectName(smr.gameObject.name ?? "")) continue;
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            if (mesh == null || bones == null) continue;
            int headIndex = -1;
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null) continue;
                if (IsHeadBoneName(bones[b].name)) { headIndex = b; break; }
            }
            if (headIndex < 0)
            {
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b] != null && bones[b].Pointer == head.Pointer) { headIndex = b; break; }
            }
            if (headIndex < 0) continue;
            Il2CppStructArray<Vector3> vertices;
            Il2CppStructArray<BoneWeight> weights;
            Il2CppStructArray<Matrix4x4> bindposes;
            try
            {
                if (!mesh.isReadable) continue;
                vertices = mesh.vertices;
                weights = mesh.boneWeights;
                bindposes = mesh.bindposes;
            }
            catch { continue; }
            if (vertices == null || weights == null || bindposes == null) continue;
            if (vertices.Length == 0 || weights.Length != vertices.Length || bindposes.Length <= headIndex) continue;
            int local = 0;
            for (int v = 0; v < vertices.Length; v++)
            {
                var w = weights[v];
                float wh = 0f;
                if (w.m_BoneIndex0 == headIndex) wh += w.m_Weight0;
                if (w.m_BoneIndex1 == headIndex) wh += w.m_Weight1;
                if (w.m_BoneIndex2 == headIndex) wh += w.m_Weight2;
                if (w.m_BoneIndex3 == headIndex) wh += w.m_Weight3;
                if (wh < 0.5f) continue;
                var p = vertices[v];
                var world = Vector3.zero;
                Accumulate(ref world, bones, bindposes, w.m_BoneIndex0, w.m_Weight0, p);
                Accumulate(ref world, bones, bindposes, w.m_BoneIndex1, w.m_Weight1, p);
                Accumulate(ref world, bones, bindposes, w.m_BoneIndex2, w.m_Weight2, p);
                Accumulate(ref world, bones, bindposes, w.m_BoneIndex3, w.m_Weight3, p);
                var q = headToHat * headWorldInv.MultiplyPoint3x4(world);
                if (q.x < hminX) hminX = q.x; if (q.x > hmaxX) hmaxX = q.x;
                if (q.y < hminY) hminY = q.y; if (q.y > hmaxY) hmaxY = q.y;
                if (q.z < hminZ) hminZ = q.z; if (q.z > hmaxZ) hmaxZ = q.z;
                if (world.x < wmin.x) wmin.x = world.x; if (world.x > wmax.x) wmax.x = world.x;
                if (world.y < wmin.y) wmin.y = world.y; if (world.y > wmax.y) wmax.y = world.y;
                if (world.z < wmin.z) wmin.z = world.z; if (world.z > wmax.z) wmax.z = world.z;
                counted++; local++;
            }
            if (local > 0) used.Append(used.Length == 0 ? "" : ",").Append($"'{smr.name}'/{mesh.name}:{local}");
        }
        if (counted < 16) return false;
        width = hmaxX - hminX;
        top = hmaxY;
        centerX = (hminX + hmaxX) * 0.5f;
        centerZ = (hminZ + hmaxZ) * 0.5f;
        _wornHeadWorld = new Bounds((wmin + wmax) * 0.5f, wmax - wmin);
        _wornHeadWorldValid = true;
        source = $"worn-head({used} verts={counted})";
        _h.LogInfo($"[cosmetic:worn-head] measured {source} hatBox(w={width:F4} top={top:F4} cx={centerX:F4} cz={centerZ:F4}) " +
                   $"worldTop={wmax.y:F4} head='{head.name}' headLossy={head.lossyScale} modelRoot='{modelRoot.name}'");
        return true;
    }

    // Head-weighted vertex bounds of the target head, expressed in the cosmetic's local frame.
    // `root` is the body the vertices are measured under: the local player's own model for the
    // local native seat, or the REMOTE player's native model when the host dresses a remote head
    // (rule=measured-native on somebody else's game body). Null falls back to the local body.
    private bool MeasureHeadMesh(Transform root, Transform head, Quaternion headToHat,
        out float width, out float top, out float centerX, out float centerZ, out string source)
    {
        width = top = centerX = centerZ = 0f;
        source = "mesh";
        var under = root != null ? root : (_body != null ? _body.transform : null);
        if (under == null) return false;
        var renderers = under.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        for (int i = 0; i < renderers.Length; i++)
        {
            var skinned = renderers[i].TryCast<SkinnedMeshRenderer>();
            if (skinned == null) continue;
            var mesh = skinned.sharedMesh;
            if (mesh == null) continue;
            var bones = skinned.bones;
            if (bones == null) continue;
            int headIndex = -1;
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] != null && bones[b].Pointer == head.Pointer) { headIndex = b; break; }
            }
            if (headIndex < 0) continue;

            Il2CppStructArray<Vector3> vertices;
            Il2CppStructArray<BoneWeight> weights;
            Il2CppStructArray<Matrix4x4> bindposes;
            try
            {
                if (!mesh.isReadable)
                    throw new InvalidOperationException("mesh is not readable");
                vertices = mesh.vertices;
                weights = mesh.boneWeights;
                bindposes = mesh.bindposes;
            }
            catch (Exception e)
            {
                _h.LogInfo($"[cosmetic:head-mesh] skinned='{skinned.name}' mesh='{mesh.name}' unusable ({e.Message})");
                continue;
            }
            if (vertices == null || weights == null || bindposes == null) continue;
            if (vertices.Length == 0 || weights.Length != vertices.Length || bindposes.Length <= headIndex) continue;

            var bp = bindposes[headIndex];
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            int counted = 0;
            for (int v = 0; v < vertices.Length; v++)
            {
                var w = weights[v];
                float weight = 0f;
                if (w.m_BoneIndex0 == headIndex) weight += w.m_Weight0;
                if (w.m_BoneIndex1 == headIndex) weight += w.m_Weight1;
                if (w.m_BoneIndex2 == headIndex) weight += w.m_Weight2;
                if (w.m_BoneIndex3 == headIndex) weight += w.m_Weight3;
                if (weight < 0.5f) continue;
                var p = vertices[v];
                var local = new Vector3(
                    bp.m00 * p.x + bp.m01 * p.y + bp.m02 * p.z + bp.m03,
                    bp.m10 * p.x + bp.m11 * p.y + bp.m12 * p.z + bp.m13,
                    bp.m20 * p.x + bp.m21 * p.y + bp.m22 * p.z + bp.m23);
                var q = headToHat * local;
                if (q.x < minX) minX = q.x; if (q.x > maxX) maxX = q.x;
                if (q.y < minY) minY = q.y; if (q.y > maxY) maxY = q.y;
                if (q.z < minZ) minZ = q.z; if (q.z > maxZ) maxZ = q.z;
                counted++;
            }
            if (counted < 16) continue;
            width = maxX - minX;
            top = maxY;
            centerX = (minX + maxX) * 0.5f;
            centerZ = (minZ + maxZ) * 0.5f;
            source = $"mesh('{skinned.name}'/{mesh.name} verts={counted})";
            _h.LogInfo($"[cosmetic:head-mesh] measured from '{skinned.name}'/'{mesh.name}' headWeightedVerts={counted} " +
                       $"boxMin=({minX:F4},{minY:F4},{minZ:F4}) boxMax=({maxX:F4},{maxY:F4},{maxZ:F4})");
            return true;
        }
        return false;
    }

    // ================================================================ POV / visibility

    private void PollPov()
    {
        if (Time.unscaledTime < _nextPovPoll) return;
        _nextPovPoll = Time.unscaledTime + PovPollSeconds;
        var previous = _pov;
        _pov = POV.None;
        try
        {
            if (_owner != null)
            {
                var controllers = SceneCache.Scan(Il2CppType.Of<PlayerPOVController>());
                for (int i = 0; i < controllers.Length; i++)
                {
                    var controller = controllers[i].TryCast<PlayerPOVController>();
                    if (controller == null || !MirrorPlayerVisibility.Under(controller.transform, _owner)) continue;
                    _pov = controller.CurrentPOV;
                    break;
                }
            }
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:pov] {e.Message}"); }
        if (previous != _pov)
        {
            // `?.name` is a plain reference check, so it does NOT catch a destroyed IL2CPP object:
            // on a scene change the owner is dead while _pov drops to None, and reading .name threw
            // a NullReferenceException straight into Update. Unity's overloaded != is the real test.
            string owner = "-";
            try { if (_owner != null) owner = _owner.name; } catch { owner = "destroyed"; }
            _h.LogInfo($"[cosmetic:pov] {previous} -> {_pov} (owner='{owner}')");
        }
    }

    // P1.4: cosmetics follow the body they sit on. The gate is measured from the mirror's live
    // bindings, not assumed: the mirror removes only the owner-hide keys from the body controllers
    // and passes every foreign key through, so a disable key still present on a bound controller - or
    // a controller that reports rendering disabled - is the game itself hiding the body (death,
    // cutscene, equipment). Hats then hide with it instead of floating over an invisible body.
    // No independent death API is used: none is wired for the local player in this build.
    private void PollNativeHide()
    {
        bool bound = false;
        int keys = 0, renderingOff = 0;
        try
        {
            var visibility = MirrorPlayerVisibility.Current;
            bound = visibility != null && visibility.TryMeasureNativeHide(out keys, out renderingOff);
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:foreign-hide] measure failed: {e.Message}"); }
        bool hidden = bound && (keys > 0 || renderingOff > 0);
        if (hidden != _foreignHide || bound != _foreignHideBound || keys != _foreignHideKeys || renderingOff != _foreignRenderingOff)
        {
            _foreignHide = hidden;
            _foreignHideBound = bound;
            _foreignHideKeys = keys;
            _foreignRenderingOff = renderingOff;
            _h.LogInfo($"[cosmetic:foreign-hide] measured={hidden} mirrorBound={bound} foreignKeys={keys} renderingOff={renderingOff}");
        }
    }

    private void ApplyScreenState(string why)
    {
        if (_pack == null) return;
        bool bodyActive = _body != null && _head != null && _body.gameObject.activeInHierarchy;
        // Native body hides (death, cutscene, equipment) still withdraw the hat from every camera;
        // first person is NOT one of them, because the mirror has to keep showing the hat. The
        // mirror's own body controllers are what distinguishes the two (see PollNativeHide).
        bool hideAll = _foreignHide && _pov != POV.FirstPerson;
        // The screen bit is the body's answer, not a POV rule: MirrorPlayerVisibility owns "is the
        // local body on screen" (third person + no native hide + a camera pose proven out of the
        // body) and this bit follows it, so a hat is never drawn over a withdrawn body.
        bool screen = _fitReady && bodyActive && BodyOnScreen;
        bool mirror = _fitReady && bodyActive && !hideAll;
        bool hideChanged = hideAll != _hideAll;
        bool changed = screen != _screenVisible || mirror != _mirrorVisible || hideChanged;
        _screenVisible = screen;
        _mirrorVisible = mirror;
        _hideAll = hideAll;
        if (hideChanged)
            foreach (var instance in _instances) SetInstanceVisible(instance, !hideAll);

        ApplyScreenCameraMasks(why);

        string view = $"pov={_pov} bodyActive={bodyActive} foreignHide={_foreignHide} " +
                      $"(measured mirrorBound={_foreignHideBound} foreignKeys={_foreignHideKeys} renderingOff={_foreignRenderingOff}) " +
                      $"fit={_fitReady} screen={screen} mirror={mirror} hideAll={hideAll} localLayer={_localLayer}";
        if (changed || view != _lastView)
        {
            _lastView = view;
            _h.LogInfo($"[cosmetic:view] why={why} {view} instances={_instances.Count} renderers={RendererCount()}");
        }
        if (changed)
        {
            LogCameraMasks();
            _overlayInfo = view;
        }
    }

    // Choose the layer only the local player's cosmetics use. Requirements, all measured at
    // runtime instead of inferred from the tag names:
    //  * the URP renderer asset actually renders it (the mirror shares the same probe), otherwise
    //    the mirror camera could not show the hat either;
    //  * no live renderer sits on it, so widening/removing the screen mask bit changes nothing but
    //    our hats;
    //  * it is not the mirror's isolated body layer, the game's TV/FV model layers, UI, handheld,
    //    default, or the shared TV player-mesh layer (23).
    // Preference order: unused and outside every screen camera (then the bit is purely ours to
    // add/remove), unused anywhere, then the least-used outside/inside with a warning.
    private void EnsureLocalCosmeticLayer()
    {
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
        // The first probe runs in the main menu, where no lobby camera exists yet. Re-probe on the
        // scene transition so the "no live renderer on this layer" evidence is measured in the
        // scene the cosmetics actually render in. The old bit is withdrawn before the new one.
        if (_localLayerResolved && scene == _layerScene) return;
        // One Update of slack on the first probe of a generation: a hot reload replaces the module
        // inside the same frame the retiring generation's roots are scheduled for destruction.
        if (!_layerProbeDeferred) { _layerProbeDeferred = true; return; }
        if (_localLayer >= 0)
        {
            RestoreScreenCameraMasks();
            if (_mirrorCamera != null)
                try { _mirrorCamera.cullingMask &= ~(1 << _localLayer); } catch { }
            _screenLayerApplied = false;
        }
        _localLayerResolved = true;
        _layerScene = scene;
        try
        {
            var renderable = MirrorFactory.LayerRenderability(_h);
            var counts = new int[32];
            int cosmeticRenderers = 0;
            var renderers = SceneCache.Scan(Il2CppType.Of<Renderer>());
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i].TryCast<Renderer>();
                if (r == null) continue;
                GameObject go;
                try { go = r.gameObject; } catch { continue; }
                if (go == null || !go.activeInHierarchy || string.IsNullOrEmpty(go.scene.name)) continue;
                // A hot reload can probe before the retiring generation's roots are destroyed and
                // their markers are cleared; the Cosmetic_ ancestor name keeps those stale
                // renderers from making the layer we just used look occupied.
                if (IsModCosmetic(r)) { cosmeticRenderers++; continue; }
                counts[go.layer]++;
            }

            int screenMask = ScreenMask();
            int best = -1, bestPriority = int.MaxValue, bestCount = int.MaxValue;
            bool preferredOk = renderable[PreferredLocalCosmeticLayer] && counts[PreferredLocalCosmeticLayer] == 0 &&
                               (screenMask & (1 << PreferredLocalCosmeticLayer)) == 0;
            if (preferredOk)
            {
                best = PreferredLocalCosmeticLayer;
                bestPriority = 0;
                bestCount = 0;
            }
            for (int layer = 0; !preferredOk && layer < 32; layer++)
            {
                if (!renderable[layer]) continue;
                if (layer == 0 || layer == 2 || layer == 5 || layer == 15 || layer == 23 || layer == 24 || layer == 27) continue;
                if (layer == MirrorBodyLayer) continue;
                bool outside = (screenMask & (1 << layer)) == 0;
                int count = counts[layer];
                int priority = count == 0 ? (outside ? 0 : 1) : (outside ? 2 : 3);
                if (priority > bestPriority || (priority == bestPriority && count >= bestCount)) continue;
                best = layer; bestPriority = priority; bestCount = count;
            }

            if (best < 0)
            {
                best = FallbackLocalCosmeticLayer;
                _h.LogWarning($"[cosmetic:layer] no renderable unused layer found; falling back to {best} " +
                              $"('{LayerMask.LayerToName(best)}'); renderable={RenderableList(renderable)} screenMask=0x{screenMask:X8}");
            }
            _localLayer = best;
            int bit = 1 << best;
            string[] why = { "unused/outside-screen", "unused/inside-screen", "least-used/outside-screen", "least-used/inside-screen" };
            _h.LogInfo($"[cosmetic:layer] selected={best} name='{LayerMask.LayerToName(best)}' bit=0x{bit:X8} " +
                       $"reason={why[Math.Min(bestPriority, 3)]} otherLiveRenderers={bestCount} cosmeticRenderersExcluded={cosmeticRenderers} " +
                       $"renderable={renderable[best]} inScreenMask={(screenMask & bit) != 0}");
            _h.LogInfo($"[cosmetic:layer] probe renderable={RenderableList(renderable)} screenMask=0x{screenMask:X8} census={CensusList(counts)}");

            ApplyLocalLayerToInstances();
            ApplyScreenCameraMasks("layer-selected");
        }
        catch (Exception e)
        {
            _localLayer = FallbackLocalCosmeticLayer;
            _h.LogWarning($"[cosmetic:layer] probe failed ({e.Message}); using fallback layer {_localLayer} ('{LayerMask.LayerToName(_localLayer)}')");
        }
    }

    private void ApplyLocalLayerToInstances()
    {
        if (_localLayer < 0) return;
        foreach (var instance in _instances)
        {
            if (instance.Root != null) instance.Root.layer = _localLayer;
            foreach (var r in instance.Renderers)
            {
                if (r == null) continue;
                try { r.gameObject.layer = _localLayer; } catch { }
            }
        }
    }

    private static bool IsModCosmetic(Renderer renderer)
    {
        if (CosmeticMarkers.IsCosmetic(renderer)) return true;
        try
        {
            var t = renderer.transform;
            for (int i = 0; t != null && i < 8; i++, t = t.parent)
                if (!string.IsNullOrEmpty(t.name) && t.name.StartsWith("Cosmetic_", StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    private static string RenderableList(bool[] renderable)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < renderable.Length; i++)
            if (renderable[i]) sb.Append(i).Append(',');
        if (sb.Length > 1) sb.Length--;
        return sb.Append(']').ToString();
    }

    private static string CensusList(int[] counts)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] != 0) sb.Append(i).Append('=').Append(counts[i]).Append(' ');
        return sb.Length == 0 ? "(none)" : sb.ToString().TrimEnd();
    }

    // Per-camera, per-POV screen visibility. Only the local-cosmetic bit is written; the camera's
    // pristine mask is recorded once and restored on Dispose. Layer 23 is never touched.
    private void ApplyScreenCameraMasks(string why)
    {
        if (_localLayer < 0) return;
        bool want = _fitReady && BodyOnScreen;
        bool rescan = Time.unscaledTime >= _nextCameraScan;
        if (want == _screenLayerApplied && !rescan) return;
        _nextCameraScan = Time.unscaledTime + CameraScanSeconds;

        int bit = 1 << _localLayer;
        int screens = 0, changed = 0;
        var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i].TryCast<Camera>();
            if (!MirrorFactory.ScreenCamera(c)) continue;
            screens++;
            TrackScreenCamera(c);
            int mask = c.cullingMask;
            int target = want ? (mask | bit) : (mask & ~bit);
            if (target == mask) continue;
            try { c.cullingMask = target; changed++; }
            catch (Exception e) { _h.LogWarning($"[cosmetic:layer] screen mask write failed: {e.Message}"); }
        }

        EnsureMirrorLocalLayer();

        if (want != _screenLayerApplied || changed > 0)
        {
            _screenLayerApplied = want;
            _h.LogInfo($"[cosmetic:layer] screen policy why={why} pov={_pov} localLayer={_localLayer} bit=0x{bit:X8} " +
                       $"want={want} screenCameras={screens} masksChanged={changed} hideAll={_hideAll} bodyOnScreen={BodyOnScreen}");
        }
    }

    // ---------------------------------------------------------------- body-on-screen coupling
    // The one owner is MirrorPlayerVisibility.LocalBodyOnScreen: armed, third person, no native hide,
    // and a camera pose the rig has proven out of the body. The body bit there and this cosmetic bit
    // are two halves of that single answer - so first person, the no-room fallback and a native hide
    // all take the hat away with the body, and the mirror keeps both on its own mask regardless.
    private bool BodyOnScreen
    {
        get
        {
            try
            {
                var visibility = MirrorPlayerVisibility.Current;
                return visibility != null && visibility.LocalBodyOnScreen;
            }
            catch { return false; }
        }
    }

    // Called by MirrorPlayerVisibility the moment its answer changes. The answer can change in
    // LateUpdate (the camera rig's veto), after this frame's cosmetic policy has already run, so the
    // bit is written here too; then the two boxes change in the same frame, not one apart.
    internal void OnLocalBodyOnScreenChanged(bool onScreen)
    {
        if (_disposed) return;
        ApplyScreenCameraMasks(onScreen ? "body-on-screen" : "body-off-screen");
    }

    private void TrackScreenCamera(Camera camera)
    {
        int id;
        try { id = camera.GetInstanceID(); } catch { return; }
        for (int i = 0; i < _screenMasks.Count; i++)
        {
            var known = _screenMasks[i];
            if (known.InstanceId == id && known.Camera != null) return;
        }
        _screenMasks.Add(new CameraMaskState
        {
            Camera = camera,
            // The isolated body layer is mod-owned (MirrorPlayerVisibility admits it in third person
            // and withdraws it otherwise). Masking it out of the pristine record keeps this restore
            // from reinstating a body bit that a later teardown already withdrew, in either dispose
            // order.
            OriginalMask = camera.cullingMask & ~(1 << MirrorBodyLayer),
            InstanceId = id
        });
        _h.LogInfo($"[cosmetic:layer] screen camera '{CameraPath(camera.transform)}' pristineMask=0x{camera.cullingMask:X8} tracked={_screenMasks.Count}");
    }

    private void RestoreScreenCameraMasks()
    {
        int restored = 0;
        for (int i = 0; i < _screenMasks.Count; i++)
        {
            var known = _screenMasks[i];
            if (known.Camera == null) continue;
            try { known.Camera.cullingMask = known.OriginalMask; restored++; }
            catch (Exception e) { _h.LogWarning($"[cosmetic:layer] screen mask restore failed: {e.Message}"); }
        }
        if (_screenMasks.Count > 0)
            _h.LogInfo($"[cosmetic:layer] restored {restored}/{_screenMasks.Count} screen camera mask(s)");
        _screenMasks.Clear();
    }

    // The mirror camera is mod-owned: it always includes the local layer, independent of the
    // screen's POV. Never remove layer 23 here (the merged build gates that bit separately).
    private void EnsureMirrorLocalLayer()
    {
        if (_localLayer < 0 || _mirrorCamera == null) return;
        int bit = 1 << _localLayer;
        try
        {
            int mask = _mirrorCamera.cullingMask;
            if ((mask & bit) != 0) return;
            _mirrorCamera.cullingMask = mask | bit;
            _h.LogInfo($"[cosmetic:layer] mirror mask 0x{mask:X8} -> 0x{_mirrorCamera.cullingMask:X8} added localLayer={_localLayer} " +
                       $"('{LayerMask.LayerToName(_localLayer)}') layer23={(mask & (1 << TvPlayerModelLayer)) != 0}");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:layer] mirror mask write failed: {e.Message}"); }
    }

    private void SetInstanceVisible(Instance instance, bool visible)
    {
        instance.Visible = visible;
        for (int i = 0; i < instance.Renderers.Count; i++)
        {
            var r = instance.Renderers[i];
            if (r == null) continue;
            try { r.enabled = visible; } catch { }
        }
    }

    private int RendererCount()
    {
        int count = 0;
        foreach (var instance in _instances) count += instance.Renderers.Count;
        return count;
    }

    private void LogCameraMasks()
    {
        try
        {
            int bit = 1 << TvPlayerModelLayer;
            int localBit = _localLayer < 0 ? 0 : 1 << _localLayer;
            var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
            int screenCams = 0, screenWithLayer = 0, screenWithLocal = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (!MirrorFactory.ScreenCamera(c)) continue;
                screenCams++;
                bool has = (c.cullingMask & bit) != 0;
                bool hasLocal = localBit != 0 && (c.cullingMask & localBit) != 0;
                if (has) { screenWithLayer++; if (_lastScreenCamera == null) { _lastScreenCamera = c; _lastScreenMask = c.cullingMask; } }
                if (hasLocal) screenWithLocal++;
                sb.Append($"'{CameraPath(c.transform)}'=0x{c.cullingMask:X8}{(has ? "+" : "-")}{(hasLocal ? "L" : ".")}; ");
            }
            bool mirrorHas = _mirrorCamera != null && (_mirrorCamera.cullingMask & bit) != 0;
            bool mirrorLocal = _mirrorCamera != null && localBit != 0 && (_mirrorCamera.cullingMask & localBit) != 0;
            _h.LogInfo($"[cosmetic:mask] layer={TvPlayerModelLayer} ({LayerMask.LayerToName(TvPlayerModelLayer)}) bit=0x{bit:X8} " +
                       $"screenCameras={screenCams} screenCamerasIncludingLayer={screenWithLayer} " +
                       $"mirrorCamera={( _mirrorCamera == null ? "none" : CameraPath(_mirrorCamera.transform))} mirrorIncludesLayer={mirrorHas} " +
                       $"renderers={RendererCount()} marked={CosmeticMarkers.RendererCount}");
            _h.LogInfo($"[cosmetic:mask] localLayer={_localLayer} ({( _localLayer < 0 ? "none" : LayerMask.LayerToName(_localLayer))}) " +
                       $"localBit=0x{localBit:X8} screenCamerasIncludingLocal={screenWithLocal} mirrorIncludesLocal={mirrorLocal} " +
                       $"pov={_pov} hideAll={_hideAll}");
            _h.LogInfo($"[cosmetic:mask] screen masks: {sb}");
            if (screenCams > 0 && screenWithLayer == 0)
                _h.LogWarning($"[cosmetic:view-mismatch] camera=screen layer={TvPlayerModelLayer} mask=none-includes-layer pov={_pov} " +
                              $"foreignHide={_foreignHide} -> foreign hats are also hidden");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:mask] {e.Message}"); }
    }

    // ================================================================ instances

    private void Reconcile()
    {
        if (_pack == null) return;
        // Drop instances that are no longer equipped (or unknown), and instances that no longer
        // belong to the live head/socket or whose renderers died with the previous scene. ID alone
        // is not presence: a dead root must be recreated, not mistaken for the equipped item.
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var instance = _instances[i];
            if (!_equipped.Contains(instance.Id) || !_pack.Cosmetics.ContainsKey(instance.Id))
            {
                DestroyInstance(instance);
                _instances.RemoveAt(i);
                _h.LogInfo($"[cosmetic:unequip] id={instance.Id} instances={_instances.Count} markers={CosmeticMarkers.RendererCount}");
                continue;
            }
            if (InstanceLive(instance)) continue;
            string why = InstanceHealth(instance);
            DestroyInstance(instance);
            _instances.RemoveAt(i);
            _h.LogInfo($"[cosmetic:invalid] id={instance.Id} reason={why} instances={_instances.Count} markers={CosmeticMarkers.RendererCount}");
        }
        if (!_fitReady || _head == null) return;
        foreach (var id in _equipped)
        {
            bool exists = false;
            foreach (var instance in _instances) if (instance.Id == id) { exists = true; break; }
            if (exists) continue;
            if (!_pack.Cosmetics.TryGetValue(id, out var def)) { _h.LogWarning($"[cosmetic:unknown] id={id}"); continue; }
            if (_retryAfter.TryGetValue(id, out var retry) && Time.unscaledTime < retry) continue;
            var created = CreateInstance(def);
            if (created != null) { _instances.Add(created); _retryAfter.Remove(id); }
            else _retryAfter[id] = Time.unscaledTime + 10f;
        }
    }

    // A present instance must still be alive AND still sit on the socket the current binding
    // resolves the slot to. While no head is bound the socket check is deferred (the root can be
    // re-parented on the next refit); root/renderer liveness is always checked.
    //
    // Re-parenting is NOT a failure here: the anchor path re-parents the root onto the head bone on
    // every refit (ComputeFit -> Attach), and the check below is "is the parent the resolved socket",
    // so an anchor-driven placement satisfies it. Only a parent that is something else counts.
    private bool InstanceLive(Instance instance) => InstanceHealth(instance) == null;

    // null = live; otherwise the reason it must be rebuilt, for the one diagnostic line.
    private string InstanceHealth(Instance instance)
    {
        if (instance.Root == null) return "dead-root";
        if (instance.Renderers.Count == 0) return "no-geometry";
        for (int i = 0; i < instance.Renderers.Count; i++)
            if (instance.Renderers[i] == null) return "dead-renderer";
        if (!_fitReady || _head == null || _body == null) return null; // socket check is deferred until a fit exists; root/renderer liveness was checked above
        var socket = ResolveSocket(instance.Def);
        if (socket == null) return "no-socket";
        try
        {
            var parent = instance.Root.transform.parent;
            if (parent == null) return "orphaned-root";
            return parent.Pointer == socket.Pointer ? (string)null : "reparented";
        }
        catch { return "parent-unreadable"; }
    }

    // The local player's own attachment target: the fields ComputeFit left behind. A remote wearer
    // passes its own target explicitly (its head bone, its roster anchor, layer 23), so the same
    // construction/attach code serves both and there is no second placement rule.
    private sealed class AttachTarget
    {
        public ThirdViewPlayerModel Body;
        public Transform Head;
        public float Scale;
        public Quaternion HatRotation;
        public Vector3 FitShift;
        public int Layer;
        public bool Remote;
        public string Rule;
    }

    // One placement rule for every wearer, local or remote: the seat is measured from the head
    // geometry that is drawn on THIS body, now. The per-model table is the fallback when that mesh
    // is unreadable. Sharing the rule is what keeps the two paths from disagreeing again.
    private AttachTarget BuildTarget(ThirdViewPlayerModel body, Transform head, ModelSpec spec, bool remote, string owner)
    {
        if (body == null || head == null) return null;
        var modelRoot = body.transform;
        var rTarget = Quaternion.Inverse(modelRoot.rotation) * head.rotation;
        var rSrc = _pack.SourceHead.RotRelModel;
        var headToHat = Quaternion.Inverse(rSrc) * rTarget;
        var lossy = head.lossyScale;
        float scale = Mathf.Pow(Mathf.Abs(lossy.x * lossy.y * lossy.z), 1f / 3f);
        var target = new AttachTarget
        {
            Body = body,
            Head = head,
            HatRotation = head.rotation * (Quaternion.Inverse(rTarget) * rSrc),
            Layer = remote ? TvPlayerModelLayer : (_localLayer >= 0 ? _localLayer : TvPlayerModelLayer),
            Remote = remote,
        };
        float width, top, centerX, centerZ;
        string source = null;
        if (MeasureWornHead(modelRoot, head, headToHat, out width, out top, out centerX, out centerZ, out source))
        {
            var src = _pack.SourceHead;
            if (width * scale <= 1e-5f || src.Width <= 1e-5f) return null;
            target.Scale = width * scale / src.Width;
            target.FitShift = new Vector3(centerX * scale - src.CenterX * target.Scale,
                                          top * scale - src.Top * target.Scale,
                                          centerZ * scale - src.CenterZ * target.Scale);
            target.Rule = $"measured-worn({source})";
            return target;
        }
        // Native body: no mesh of ours is drawn, so the seat is measured on the GAME's own head mesh -
        // the same MeasureHeadMesh the local owner's native seat uses (rule=measured-native). This is
        // what lets a remote player who wears no model of ours still wear a hat on his own head.
        if (spec == null || ModelRoster.AnchorFor(spec) == null)
        {
            if (MeasureHeadMesh(modelRoot, head, headToHat, out width, out top, out centerX, out centerZ, out source))
            {
                var srcN = _pack.SourceHead;
                float w = width * scale;
                if (w > 1e-5f && srcN.Width > 1e-5f)
                {
                    target.Scale = w / srcN.Width;
                    target.FitShift = new Vector3(centerX * scale - srcN.CenterX * target.Scale,
                                                  top * scale - srcN.Top * target.Scale,
                                                  centerZ * scale - srcN.CenterZ * target.Scale);
                    target.Rule = $"measured-native({source})";
                    return target;
                }
            }
        }
        var anchor = ModelRoster.AnchorFor(spec);
        if (anchor == null)
        {
            _h.LogWarning($"[cosmetic:seat] {owner}: no readable head mesh and no stored anchor for " +
                          $"'{(spec == null ? "?" : spec.Slug)}'; cosmetics cannot be placed");
            return null;
        }
        target.Scale = anchor.Scale;
        target.HatRotation = head.rotation * anchor.Rotation;
        target.FitShift = Quaternion.Inverse(anchor.Rotation) * anchor.Offset;
        target.Rule = $"stored-anchor('{(spec == null ? "?" : spec.Slug)}')";
        return target;
    }

    // The seat is stored head-RELATIVE (_localRot/_localPos are in the head bone's local frame), so
    // the target's world rotation is that calibration re-based on the head's CURRENT rotation - not
    // the _hatRotation snapshot from the last refit. PollBind skips refitting while body/head id,
    // lossy scale and worn model are unchanged (cost), so after the player turns or animates the
    // snapshot lags arbitrarily far behind; Attach would then fold the stale world rotation into the
    // current socket's local frame (localRot = inverse(socket.rotation) * HatRotation) and a newly
    // equipped cosmetic would come out turned by the head's rotation-since-fit, with its seat offset
    // carried along in the same wrong axes. Reconstructing it here makes every Attach use the same
    // head-relative placement the last refit calibrated, whatever the head has done since.
    private AttachTarget LocalTarget() => new AttachTarget
    {
        Body = _body,
        Head = _head,
        Scale = _fitScale,
        // _hatRotation only stands in when the head is already gone (then no live rotation exists).
        HatRotation = _head != null ? _head.rotation * _localRot : _hatRotation,
        FitShift = _fitShift,
        Layer = _localLayer >= 0 ? _localLayer : TvPlayerModelLayer,
        Remote = false,
    };

    private Instance CreateInstance(CosmeticPack.CosmeticDef def) => CreateInstance(def, null);

    private Instance CreateInstance(CosmeticPack.CosmeticDef def, AttachTarget given)
    {
        var instance = new Instance { Id = def.Id, Def = def };
        var target = given ?? LocalTarget();
        try
        {
            int layer = target.Layer;
            var root = new GameObject("Cosmetic_" + def.Id);
            root.layer = layer;
            instance.Root = root;
            instance.RootPointer = root.transform.Pointer;
            // Registered before the tree is built, so a throw anywhere below still reaches
            // DestroyInstance's unregister path.
            CosmeticMarkers.AddRoot(root.transform);
            foreach (var node in def.Children) BuildNode(node, root.transform, instance, layer);
            // An instance with no geometry is not a cosmetic: InstanceLive rejects it, so registering
            // it makes Reconcile destroy and rebuild it every single frame (the invalid/load log loop).
            // Report it once and let the caller's retry backoff hold it off instead.
            if (instance.Renderers.Count == 0)
            {
                _h.LogWarning($"[cosmetic:load-empty] id={def.Id} built no renderer from {def.Children.Length} prefab child(ren) " +
                             $"(pack meshes={_pack.Meshes.Count} cache={_meshes.Count}); not registering, retrying in 10s");
                DestroyInstance(instance);
                return null;
            }
            if (!Attach(instance, target))
            {
                _h.LogWarning($"[cosmetic:load-failed] id={def.Id} could not attach to its socket; retrying in 10s");
                DestroyInstance(instance);
                return null;
            }
            SetInstanceVisible(instance, target.Remote || !_hideAll);
            _h.LogInfo($"[cosmetic:load] id={def.Id} type={def.Type} prefabChildren={def.Children.Length} meshes={instance.MeshCount} " +
                       $"verts={instance.Verts} tris={instance.Tris} renderers={instance.Renderers.Count} layer={root.layer} " +
                       $"({LayerMask.LayerToName(root.layer)}) rootScale={def.RootScale.x:F4},{def.RootScale.y:F4},{def.RootScale.z:F4}");
            return instance;
        }
        catch (Exception e)
        {
            _h.LogError($"[cosmetic:load-failed] id={def.Id}: {e}");
            DestroyInstance(instance);
            return null;
        }
    }

    private void BuildNode(CosmeticPack.NodeDef node, Transform parent, Instance instance, int layer)
    {
        var go = new GameObject(string.IsNullOrEmpty(node.Name) ? "node" : node.Name);
        go.layer = layer;
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = node.Pos;
        t.localRotation = node.Rot;
        t.localScale = node.Scale;
        go.SetActive(node.Active);

        if (node.Mesh >= 0 && _pack.Meshes.TryGetValue(node.Mesh, out var meshDef))
        {
            var mesh = GetMesh(node.Mesh);
            if (mesh != null)
            {
                var filter = go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
                var renderer = go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
                if (filter != null && renderer != null)
                {
                    filter.sharedMesh = mesh;
                    var materials = new Material[node.Materials.Length];
                    for (int i = 0; i < node.Materials.Length; i++) materials[i] = GetMaterial(node.Materials[i]);
                    if (materials.Length == 1)
                    {
                        renderer.sharedMaterial = materials[0];
                    }
                    else if (materials.Length > 1)
                    {
                        renderer.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
                    }
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.enabled = false;
                    CosmeticMarkers.Add(renderer);
                    instance.Renderers.Add(renderer);
                    instance.RendererPointers.Add(renderer.Pointer);
                    instance.MeshCount++;
                    instance.Verts += meshDef.VertexCount;
                    foreach (var sub in meshDef.Subs) instance.Tris += sub.IndexCount / 3;
                }
            }
        }
        foreach (var child in node.Children) BuildNode(child, t, instance, layer);
    }

    // Returns true when the root is parented to its socket, so creation can fail loudly instead of
    // registering an instance that reconciliation would immediately reject.
    private bool Attach(Instance instance) => Attach(instance, LocalTarget());

    private bool Attach(Instance instance, AttachTarget target)
    {
        if (target == null) return false;
        if (instance?.Root == null || target.Head == null) return false;
        if (!target.Remote && !_fitReady) return false;
        try
        {
            var t = instance.Root.transform;
            var socket = ResolveSocket(instance.Def, target);
            if (socket == null) return false;
            // The baked prefab offset (the child TRS chain) is preserved for every slot: only the
            // socket bone changes, the root keeps the calibrated source-rig orientation and the
            // measured head-fit shift. Applied to a chest item this puts the sign on the torso at
            // the same world rotation it had when it hung (wrongly) at the pelvis.
            t.SetParent(socket, false);
            var socketRot = socket.rotation;
            var lossy = socket.lossyScale;
            Quaternion localRot;
            Vector3 localOffset;
            // Non-head slot: use the fit-time conversion into this socket's frame. It is a constant
            // between fits, so the item's local rotation/position equal the calibrated values
            // whatever the head did at equip time and whatever has moved since the last fit. The
            // hat (type 0) and every remote body keep the head-derived path below, which for
            // socket == head reduces exactly to `_localRot`.
            if (!target.Remote && instance.Def != null && instance.Def.Type != 0 &&
                _slotRot.TryGetValue(instance.Def.Type, out var slotRot) &&
                _slotPos.TryGetValue(instance.Def.Type, out var slotPos))
            {
                localRot = slotRot;
                localOffset = slotPos;
            }
            else
            {
                Vector3 worldPos = socket.position + target.HatRotation * target.FitShift;
                Vector3 offset = worldPos - socket.position;
                localOffset = Quaternion.Inverse(socketRot) * offset;
                localOffset = new Vector3(localOffset.x / Mathf.Max(1e-6f, lossy.x),
                                          localOffset.y / Mathf.Max(1e-6f, lossy.y),
                                          localOffset.z / Mathf.Max(1e-6f, lossy.z));
                localRot = Quaternion.Inverse(socketRot) * target.HatRotation;
            }
            t.localPosition = localOffset;
            t.localRotation = localRot;
            // P2.8: the prefab root's authored localScale belongs to the cosmetic (MoreCompany
            // multiplies it into the runtime scale, it never replaces it). Fold it into the fitted
            // root scale exactly once; the fit offset stays in fitted units, because the root's
            // placement relative to the head is unchanged by the prefab's own scale.
            var rootScale = instance.Def?.RootScale ?? Vector3.one;
            float fitX = target.Scale * rootScale.x, fitY = target.Scale * rootScale.y, fitZ = target.Scale * rootScale.z;
            t.localScale = new Vector3(fitX / Mathf.Max(1e-6f, lossy.x),
                                       fitY / Mathf.Max(1e-6f, lossy.y),
                                       fitZ / Mathf.Max(1e-6f, lossy.z));
            t.gameObject.SetActive(true);

            if (!target.Remote && instance.Def != null && instance.Def.Type == 2) ApplyChestClearance(instance, socket);

            if (Time.unscaledTime >= _nextFitLog || _nextFitLog <= 0f)
            {
                _nextFitLog = Time.unscaledTime + 30f;
                var b = new Bounds(t.position, Vector3.zero);
                bool first = true;
                foreach (var r in instance.Renderers)
                {
                    if (r == null) continue;
                    if (first) { b = r.bounds; first = false; }
                    else b.Encapsulate(r.bounds);
                }
                _h.LogInfo($"[cosmetic:bind] id={instance.Id} bone='{socket.name}' boneWorld={socket.position} " +
                           $"hatWorld={t.position} relBone={t.position - socket.position} localPos={t.localPosition} " +
                           $"localScale={t.localScale} worldScale={target.Scale:F5} rootScale=({rootScale.x:F4},{rootScale.y:F4},{rootScale.z:F4}) " +
                           $"headLossy={lossy} worldBounds(center={b.center} size={b.size})");
                _nextProof = Time.unscaledTime + 2.0f;
            }
            return true;
        }
        catch (Exception e)
        {
            _h.LogError($"[cosmetic:attach-failed] id={instance.Id}: {e}");
            return false;
        }
    }

    // The typed Bones surface only exposes Root/Pelvis/Wrists/Neck/Head. CHEST items need a torso
    // socket, so pick a spine bone from the rig itself. MoreCompany's own CHEST socket is the third
    // spine bone (`chest = spine.spine.001.spine.002.spine.003`), which is spine_03 here; spine_02 is
    // the fallback for rig generations without it (Emma/Freya in the asset set have no spine_03).
    // chest_01_* are female-only breast bones and are never used as a torso socket.
    private Transform ResolveChestBone()
    {
        if (_chestResolved) return _chestBone;
        _chestResolved = true;
        _chestBone = null;
        var candidates = new List<Transform>();
        try
        {
            var bones = _body == null ? null : _body.Bones;
            var all = bones == null ? null : bones.AllBones;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    string n = (t.name ?? "").ToLowerInvariant();
                    if (n.StartsWith("spine") || n.StartsWith("chest")) candidates.Add(t);
                }
            }
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:socket] spine scan failed: {e.Message}"); }

        if (!_socketScanLogged)
        {
            _socketScanLogged = true;
            var sb = new StringBuilder();
            foreach (var t in candidates)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{t.name}@({t.position.x:F3},{t.position.y:F3},{t.position.z:F3})");
            }
            var pelvis = _body == null || _body.Bones == null ? null : _body.Bones.Pelvis;
            var neck = _body == null || _body.Bones == null ? null : _body.Bones.Neck;
            _h.LogInfo($"[cosmetic:socket-scan] body='{_body?.name}' bones=[{sb}] " +
                       $"pelvis={(pelvis == null ? "none" : $"({pelvis.position.x:F3},{pelvis.position.y:F3},{pelvis.position.z:F3})")} " +
                       $"neck={(neck == null ? "none" : $"({neck.position.x:F3},{neck.position.y:F3},{neck.position.z:F3})")}");
        }

        // Preference: spine_03 (MoreCompany's own chest bone), then spine_02, spine_01, spine_04.
        // Breast bones (chest_01_*/chest_02_*) are excluded: they exist on female rigs only and are
        // not a torso attachment point.
        string[] preference = { "spine_03", "spine_02", "spine_01", "spine_04" };
        foreach (var want in preference)
        {
            foreach (var t in candidates)
            {
                if (t == null) continue;
                if (string.Equals(t.name, want, StringComparison.OrdinalIgnoreCase)) { _chestBone = t; return _chestBone; }
            }
        }
        var bonesB = _body == null ? null : _body.Bones;
        if (bonesB != null && bonesB.Neck != null) { _chestBone = bonesB.Neck; return _chestBone; }
        if (bonesB != null && bonesB.Pelvis != null) { _chestBone = bonesB.Pelvis; return _chestBone; }
        return null;
    }

    // Torso socket for a REMOTE body: same preference, read off that body's own bone list. Uncached
    // because each remote wearer has its own rig and the attachment is rebuilt whole when it changes.
    private Transform ResolveChestBone(ThirdViewPlayerModel body)
    {
        var candidates = new List<Transform>();
        try
        {
            var all = body == null || body.Bones == null ? null : body.Bones.AllBones;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = (t.name ?? "").ToLowerInvariant();
                if (n.StartsWith("spine") || n.StartsWith("chest")) candidates.Add(t);
            }
        }
        catch { }
        string[] preference = { "spine_03", "spine_02", "spine_01", "spine_04" };
        foreach (var want in preference)
            foreach (var t in candidates)
                if (t != null && string.Equals(t.name, want, StringComparison.OrdinalIgnoreCase)) return t;
        var bones = body == null ? null : body.Bones;
        if (bones != null && bones.Neck != null) return bones.Neck;
        if (bones != null && bones.Pelvis != null) return bones.Pelvis;
        return null;
    }

    // ================================================================ remote wearers (stage 3A)

    // One remote player's attached equipped set. Owned by the body lease: built when the body is
    // built, rebuilt when the set or the body changes, destroyed with the lease.
    internal sealed class RemoteHats
    {
        internal string Label;
        internal ulong ClientId;
        internal readonly List<string> Ids = new List<string>();
        internal readonly List<Instance> Instances = new();
        internal Transform Head;
        internal IntPtr HeadPointer;
        internal int SetVersion;
        internal int Attaches, Disposes;
        internal string Rule = "";          // the seat rule this set was placed with (measured-*)
        internal string Note = "";
        internal string Signature => Ids.Count == 0 ? "" : string.Join(",", Ids.ToArray());
        internal int Renderers
        {
            get { int n = 0; foreach (var i in Instances) n += i.Renderers.Count; return n; }
        }
    }

    // Attach `ids` (the wearer's equipped set, already validated against OUR pack) to `head`. The
    // placement comes from BuildTarget - the same measured seat the local path uses, measured on the
    // remote body that is actually on screen. Nothing about the transform ever comes from the wire.
    internal RemoteHats AttachRemote(ulong clientId, string label, ThirdViewPlayerModel body, Transform head,
                                     ModelSpec spec, List<string> ids)
    {
        var set = new RemoteHats
        {
            Label = label, ClientId = clientId, Head = head, SetVersion = _equippedVersion,
        };
        try
        {
            if (_pack == null) { set.Note = "no-pack"; _h.LogWarning($"[cosmetic:remote] {label}: pack not loaded"); return set; }
            if (head == null || body == null) { set.Note = "no-head"; _h.LogWarning($"[cosmetic:remote] {label}: no head bone/body"); return set; }
            IntPtr headPtr = IntPtr.Zero;
            try { headPtr = head.Pointer; } catch { }
            set.HeadPointer = headPtr;
            var target = BuildTarget(body, head, spec, true, $"remote {label}");
            if (target == null)
            {
                set.Note = "no-anchor";
                _h.LogWarning($"[cosmetic:remote] {label}: roster entry '{(spec == null ? "?" : spec.Slug)}' has no " +
                              "measurable head and no stored hat anchor, so cosmetics cannot be placed on this body");
                return set;
            }
            int unknown = 0, failed = 0;
            foreach (var id in ids ?? new List<string>())
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!_pack.Cosmetics.TryGetValue(id, out var def)) { unknown++; continue; }
                var instance = CreateInstance(def, target);
                if (instance == null) { failed++; continue; }
                set.Instances.Add(instance);
                set.Ids.Add(id);
            }
            set.Ids.Sort(StringComparer.Ordinal);
            set.Attaches++;
            set.Rule = target.Rule ?? "";
            _remoteHats.Add(set);
            _h.LogInfo($"[cosmetic:remote] attach {label} id={(ids == null ? 0 : ids.Count)} attached={set.Instances.Count} " +
                       $"renderers={set.Renderers} set=[{set.Signature}] layer={TvPlayerModelLayer} " +
                       $"rule={target.Rule} scale={target.Scale:F4} shift={target.FitShift} " +
                       $"head='{head.name}' headWorld={V3(head.position)} setVersion={set.SetVersion} unknown={unknown} failed={failed}");
            LogRemoteSeat(label, head, body.transform, set);
        }
        catch (Exception e)
        {
            set.Note = "threw";
            _h.LogWarning($"[cosmetic:remote] attach {label} failed: {e}");
            DisposeRemote(set);
        }
        return set;
    }

    internal void DisposeRemote(RemoteHats set)
    {        if (set == null) return;
        int n = set.Instances.Count;
        foreach (var instance in set.Instances) DestroyInstance(instance);
        set.Instances.Clear();
        set.Ids.Clear();
        if (_remoteHats.Remove(set)) set.Disposes++;
        _h.LogInfo($"[cosmetic:remote] detach {set.Label} instances={n} attaches={set.Attaches} disposes={set.Disposes} " +
                   $"markersLeft={CosmeticMarkers.RendererCount}" + (set.Note.Length > 0 ? $" note={set.Note}" : ""));
    }

    internal int RemoteSetCount => _remoteHats.Count;
    internal int RemoteRendererCount
    {
        get { int n = 0; foreach (var s in _remoteHats) n += s.Renderers; return n; }
    }

    // A chest accessory hangs at the spine socket, but its own baked offset can still leave it inside
    // the body: on this body the sheriff vest's front surface sits ~0.28 m ahead of spine_03 while the
    // outoforder placard is only ~0.12 m ahead of its root, so the placard is buried in the vest. Push
    // the socket forward until the item's own bounds clear the measured torso surface. The prefab
    // chain (the baked offset) is untouched; only the socket position moves, once, and the
    // measurement is logged.
    private void ApplyChestClearance(Instance instance, Transform socket)
    {
        if (instance?.Root == null || _body == null) return;
        var fwd = _modelRoot != null ? _modelRoot.forward : socket.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd = fwd.normalized;
        var side = Vector3.Cross(Vector3.up, fwd);

        Bounds item = default;
        bool haveItem = false;
        foreach (var r in instance.Renderers)
        {
            if (r == null) continue;
            var b = r.bounds;
            if (b.size.sqrMagnitude <= 1e-8f) continue;
            if (!haveItem) { item = b; haveItem = true; } else item.Encapsulate(b);
        }
        if (!haveItem) return;

        float torsoFront = float.NegativeInfinity;
        string torsoName = "";
        var candidates = new StringBuilder();
        try
        {
            var arr = _body.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < arr.Length; i++)
            {
                // The body's garments are skinned meshes; mod cosmetics are plain MeshRenderers under
                // their own root. Restricting the sweep to skinned meshes keeps the measurement to the
                // body even while a hot-reload's previous generation is still alive (its markers are
                // gone, but its renderers are not skinned).
                var skinned = arr[i].TryCast<SkinnedMeshRenderer>();
                var r = skinned == null ? null : skinned.TryCast<Renderer>();
                if (r == null || !r.enabled || CosmeticMarkers.IsCosmetic(r)) continue;
                var b = r.bounds;
                if (b.max.y < item.min.y || b.min.y > item.max.y) continue;   // not at the item's height
                // Torso garments only: a skinned mesh AABB that spans the arms (the shirt spans ~1.1 m)
                // reports the sleeve front, not the chest, and would push the item absurdly far out.
                float span = 2f * (b.extents.x * Mathf.Abs(side.x) + b.extents.y * Mathf.Abs(side.y) + b.extents.z * Mathf.Abs(side.z));
                if (span > 0.8f) { candidates.Append($"{r.name}:span{span:F2} "); continue; }
                float p = MaxAlong(b, fwd);
                if (p > torsoFront) { torsoFront = p; torsoName = r.name; }
                candidates.Append($"{r.name}:{p:F2} ");
            }
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:socket-offset] torso measure failed: {e.Message}"); return; }
        if (float.IsNegativeInfinity(torsoFront)) return;

        // The accessory's *centre* goes to the torso surface, not its most recessed point: a worn item
        // may legitimately wrap the body (the outoforder rope loop goes round the neck), so aligning
        // the deepest point would push a dangling plate metres away. 2 cm of clearance keeps the flat
        // plate in front of the vest.
        float itemCenter = Vector3.Dot(item.center, fwd);
        float shift = Mathf.Clamp(torsoFront + 0.02f - itemCenter, 0f, 0.30f);

        string key = "offset:" + instance.Id;
        bool logged = _slotLogs.ContainsKey(key);
        if (shift <= 0.002f)
        {
            if (!logged) { _slotLogs[key] = true; _h.LogInfo($"[cosmetic:socket-offset] id={instance.Id} bone='{socket.name}' torso='{torsoName}' shift=0 (item centre already at/outside the body surface)"); }
            return;
        }
        var t = instance.Root.transform;
        t.position += fwd * shift;

        if (logged) return;
        _slotLogs[key] = true;
        _h.LogInfo($"[cosmetic:socket-offset] id={instance.Id} bone='{socket.name}' torso='{torsoName}' " +
                   $"torsoFront={torsoFront:F3} itemCenter={itemCenter:F3} shift={shift:F3}m forward=({fwd.x:F2},{fwd.y:F2},{fwd.z:F2}) " +
                   $"candidates=[{candidates}] (prefab offset untouched; the body surface is measured, not assumed)");
    }

    private static float MaxAlong(Bounds b, Vector3 axis) =>
        Vector3.Dot(b.center, axis) + b.extents.x * Mathf.Abs(axis.x) + b.extents.y * Mathf.Abs(axis.y) + b.extents.z * Mathf.Abs(axis.z);

    private static float MinAlong(Bounds b, Vector3 axis) =>
        Vector3.Dot(b.center, axis) - (b.extents.x * Mathf.Abs(axis.x) + b.extents.y * Mathf.Abs(axis.y) + b.extents.z * Mathf.Abs(axis.z));

    // HAT is the calibrated path (Head). The three other MoreCompany slots have no typed Alterum
    // socket; they use the nearest available bone and are logged as approximations.
    private Transform ResolveSocket(CosmeticPack.CosmeticDef def) => ResolveSocket(def, LocalTarget());

    private Transform ResolveSocket(CosmeticPack.CosmeticDef def, AttachTarget target)
    {
        var bones = target?.Body?.Bones;
        var head = target?.Head ?? _head;
        switch (def.Type)
        {
            case 0: // HAT
                return head;
            case 4: // HIP
                if (bones != null && bones.Pelvis != null) { LogSlotApprox(def, "Pelvis", "HIP"); return bones.Pelvis; }
                break;
            case 3: // R_LOWER_ARM
                if (bones != null && bones.RightWrist != null) { LogSlotApprox(def, "RightWrist", "R_LOWER_ARM (wrist instead of mid-forearm)"); return bones.RightWrist; }
                break;
            case 2: // CHEST
            {
                var chest = target != null && target.Remote ? ResolveChestBone(target.Body) : ResolveChestBone();
                if (chest != null && !string.Equals(chest.name, "pelvis", StringComparison.OrdinalIgnoreCase))
                {
                    LogSlot(def, chest.name, "CHEST = MoreCompany's spine.003 socket (no chest_01_* on male rigs)");
                    return chest;
                }
                if (bones != null && bones.Pelvis != null) { LogSlotApprox(def, "Pelvis", "CHEST (no spine bone found)"); return bones.Pelvis; }
                break;
            }
        }
        LogSlotApprox(def, "Head", "fallback");
        return head;
    }

    private void LogSlot(CosmeticPack.CosmeticDef def, string bone, string why)
    {
        if (_slotLogs.TryGetValue(def.Id, out _)) return;
        _slotLogs[def.Id] = true;
        _h.LogInfo($"[cosmetic:socket] id={def.Id} type={def.Type} bone='{bone}' reason={why}");
    }

    private void LogSlotApprox(CosmeticPack.CosmeticDef def, string bone, string why)
    {
        if (_slotLogs.TryGetValue(def.Id, out _)) return;
        _slotLogs[def.Id] = true;
        _h.LogWarning($"[cosmetic:slot-approx] id={def.Id} type={def.Type} bone={bone} reason={why}");
    }

    private void DestroyInstance(Instance instance)
    {
        if (instance == null) return;
        // P1.3: unregister on every removal path (unequip, failed construction, model destruction),
        // using the addresses captured at registration so a destroyed wrapper cannot hide a marker.
        for (int i = 0; i < instance.RendererPointers.Count; i++) CosmeticMarkers.Remove(instance.RendererPointers[i]);
        instance.RendererPointers.Clear();
        if (instance.RootPointer != IntPtr.Zero) CosmeticMarkers.RemoveRoot(instance.RootPointer);
        instance.RootPointer = IntPtr.Zero;
        try { if (instance.Root != null) UnityEngine.Object.Destroy(instance.Root); } catch { }
        instance.Renderers.Clear();
        instance.Root = null;
    }

    // ================================================================ HEAD ANCHOR PROBE (measurement only)
    // Request file "hatanchor.request" in ScreensDir: logs, for the model currently worn, the head
    // geometry of OUR body in the game head bone's local frame (live skin -> world -> head local),
    // plus the head bone's reference transform in model-root space. This is the instrument the
    // offline bake used to validate the per-spec HatAnchor; it changes no placement.
    private const string AnchorRequestFileName = "hatanchor.request";
    private float _nextAnchorProbe;

    private void PollAnchorProbe()
    {
        PollRuleOverride();
        if (Time.unscaledTime < _nextAnchorProbe) return;
        _nextAnchorProbe = Time.unscaledTime + 0.5f;
        string path;
        try { path = Path.Combine(_h.ScreensDir, AnchorRequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        try { File.Delete(path); } catch { }
        try { ProbeHatAnchor(); }
        catch (Exception e) { _h.LogWarning($"[cosmetic:hatanchor] probe failed: {e.Message}"); }
    }

    // Harness only: `hatrule.request` (`anchor` / `measured`) selects the placement rule without a
    // rebuild, so the old table and the measured seat can be compared on the same frame and body.
    private void PollRuleOverride()
    {
        if (Time.unscaledTime < _nextRulePoll) return;
        _nextRulePoll = Time.unscaledTime + 1.0f;
        string path;
        try { path = Path.Combine(_h.ScreensDir, "hatrule.request"); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path).Trim().ToLowerInvariant(); } catch { }
        try { File.Delete(path); } catch { }
        bool force = text.StartsWith("anchor");
        if (force == _forceAnchorRule) return;
        _forceAnchorRule = force;
        _h.LogInfo($"[cosmetic:seat] rule override -> {(_forceAnchorRule ? "stored-anchor" : "measured-worn")} " +
                   "(refit on the next bind tick)");
        // Force the refit itself: PollBind skips a body/head it has already fitted.
        _fitReady = false;
        _nextBindPoll = 0f;
    }

    private static bool IsHeadBoneName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.ToLowerInvariant();
        if (n.StartsWith("sb_")) n = n.Substring(3);         // SahurRig prefixes its bone objects
        if (n.IndexOf(':') >= 0) n = n.Substring(n.LastIndexOf(':') + 1);   // mixamorig:Head
        if (n.IndexOf('|') >= 0) n = n.Substring(n.LastIndexOf('|') + 1);   // Armature|Head
        return n == "head" || n == "bip_head" || n == "def-spine.006" || n == "head_end";
    }

    private void ProbeHatAnchor()
    {
        if (_head == null || _modelRoot == null)
        {
            _h.LogWarning("[cosmetic:hatanchor] no bound head (no local third-person model)");
            return;
        }
        var modelRoot = _modelRoot;
        var head = _head;
        var headInRoot = modelRoot.InverseTransformPoint(head.position);
        var headRotInRoot = Quaternion.Inverse(modelRoot.rotation) * head.rotation;
        _h.LogInfo($"[cosmetic:hatanchor] begin modelRoot='{modelRoot.name}' body='{_body?.name}' " +
                   $"headBone='{head.name}' headInRoot={headInRoot} headRotInRoot={headRotInRoot.eulerAngles} " +
                   $"headLossy={head.lossyScale}");

        var smrs = modelRoot.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        // Reference frame: the native body's HEAD BIND transform in model-root space, i.e. the
        // game skeleton's own rest head pose. This is the frame per-model HatAnchor offsets are
        // expressed in ("head bone local").
        try
        {
            var natives = modelRoot.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            for (int r = 0; r < natives.Length; r++)
            {
                var nsmr = natives[r].TryCast<SkinnedMeshRenderer>();
                if (nsmr == null || ModelRoster.IsOurObjectName(nsmr.gameObject.name ?? "")) continue;
                var nmesh = nsmr.sharedMesh;
                var nb = nsmr.bones;
                if (nmesh == null || nb == null) continue;
                var nbp = nmesh.bindposes;
                if (nbp == null || nbp.Length != nb.Length) continue;
                for (int b = 0; b < nb.Length; b++)
                {
                    if (nb[b] == null || !string.Equals(nb[b].name, "head", StringComparison.OrdinalIgnoreCase)) continue;
                    var bindWorld = nbp[b].inverse;
                    var pWorld = nsmr.transform.TransformPoint(bindWorld.GetColumn(3));
                    var pRoot = modelRoot.InverseTransformPoint(pWorld);
                    var qRoot = Quaternion.Inverse(modelRoot.rotation) * nsmr.transform.rotation * bindWorld.rotation;
                    _h.LogInfo($"[cosmetic:hatanchor] refHead smr='{nsmr.name}' pRoot=({pRoot.x:F5},{pRoot.y:F5},{pRoot.z:F5}) " +
                               $"qRoot=({qRoot.x:F5},{qRoot.y:F5},{qRoot.z:F5},{qRoot.w:F5})");
                    r = natives.Length; break;
                }
            }
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:hatanchor] refHead failed: {e.Message}"); }
        var sh = _pack.SourceHead;
        _h.LogInfo($"[cosmetic:hatanchor] sourceHead width={sh.Width:F5} top={sh.Top:F5} cx={sh.CenterX:F5} cz={sh.CenterZ:F5} " +
                   $"rotRelModel=({sh.RotRelModel.x:F5},{sh.RotRelModel.y:F5},{sh.RotRelModel.z:F5},{sh.RotRelModel.w:F5}) " +
                   $"authoringUnitScale={sh.AuthoringUnitScale:F5} wPerNeck={sh.WidthPerNeckToHead:F5} topPerNeck={sh.TopPerNeckToHead:F5}");
        int found = 0;
        for (int r = 0; r < smrs.Length; r++)
        {
            var smr = smrs[r].TryCast<SkinnedMeshRenderer>();
            if (smr == null) continue;
            if (!ModelRoster.IsOurObjectName(smr.gameObject.name ?? "")) continue;
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            if (mesh == null || bones == null) continue;
            int headIndex = -1;
            int modelHeadIndex = -1;
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null) continue;
                if (IsHeadBoneName(bones[b].name) && modelHeadIndex < 0) modelHeadIndex = b;
                if (bones[b].Pointer == head.Pointer) headIndex = b;
            }
            if (modelHeadIndex < 0) modelHeadIndex = headIndex;
            if (modelHeadIndex < 0)
            {
                var nb = new StringBuilder();
                for (int b = 0; b < bones.Length; b++) nb.Append(b == 0 ? "" : ",").Append(bones[b] == null ? "null" : $"'{bones[b].name}'");
                _h.LogWarning($"[cosmetic:hatanchor] smr='{smr.name}' has no head bone in {bones.Length} bones [{nb}]");
                continue;
            }
            Il2CppStructArray<Vector3> vertices;
            Il2CppStructArray<BoneWeight> weights;
            Il2CppStructArray<Matrix4x4> bindposes;
            try
            {
                if (!mesh.isReadable) throw new InvalidOperationException("mesh is not readable");
                vertices = mesh.vertices;
                weights = mesh.boneWeights;
                bindposes = mesh.bindposes;
            }
            catch (Exception e)
            {
                _h.LogWarning($"[cosmetic:hatanchor] smr='{smr.name}' unusable ({e.Message})");
                continue;
            }
            if (vertices == null || weights == null || bindposes == null) continue;
            if (vertices.Length == 0 || weights.Length != vertices.Length || bindposes.Length <= modelHeadIndex) continue;

            var headWorldInv = head.worldToLocalMatrix;
            var rootWorldInv = modelRoot.worldToLocalMatrix;
            float hminX = float.MaxValue, hmaxX = float.MinValue, hminY = float.MaxValue, hmaxY = float.MinValue;
            float hminZ = float.MaxValue, hmaxZ = float.MinValue;
            float rminX = float.MaxValue, rmaxX = float.MinValue, rminY = float.MaxValue, rmaxY = float.MinValue;
            float rminZ = float.MaxValue, rmaxZ = float.MinValue;
            int counted = 0;
            for (int v = 0; v < vertices.Length; v++)
            {
                var w = weights[v];
                float wh = 0f;
                if (w.m_BoneIndex0 == modelHeadIndex) wh += w.m_Weight0;
                if (w.m_BoneIndex1 == modelHeadIndex) wh += w.m_Weight1;
                if (w.m_BoneIndex2 == modelHeadIndex) wh += w.m_Weight2;
                if (w.m_BoneIndex3 == modelHeadIndex) wh += w.m_Weight3;
                if (wh < 0.5f) continue;
                var p = vertices[v];
                var skinned = Vector3.zero;
                Accumulate(ref skinned, bones, bindposes, w.m_BoneIndex0, w.m_Weight0, p);
                Accumulate(ref skinned, bones, bindposes, w.m_BoneIndex1, w.m_Weight1, p);
                Accumulate(ref skinned, bones, bindposes, w.m_BoneIndex2, w.m_Weight2, p);
                Accumulate(ref skinned, bones, bindposes, w.m_BoneIndex3, w.m_Weight3, p);
                var world = skinned;   // bone.localToWorld * bindpose already lands in world space here
                var hl = headWorldInv.MultiplyPoint3x4(world);
                var rp = rootWorldInv.MultiplyPoint3x4(world);
                if (hl.x < hminX) hminX = hl.x; if (hl.x > hmaxX) hmaxX = hl.x;
                if (hl.y < hminY) hminY = hl.y; if (hl.y > hmaxY) hmaxY = hl.y;
                if (hl.z < hminZ) hminZ = hl.z; if (hl.z > hmaxZ) hmaxZ = hl.z;
                if (rp.x < rminX) rminX = rp.x; if (rp.x > rmaxX) rmaxX = rp.x;
                if (rp.y < rminY) rminY = rp.y; if (rp.y > rmaxY) rmaxY = rp.y;
                if (rp.z < rminZ) rminZ = rp.z; if (rp.z > rmaxZ) rmaxZ = rp.z;
                counted++;
            }
            if (counted < 16)
            {
                _h.LogWarning($"[cosmetic:hatanchor] smr='{smr.name}' counted={counted} (headIndex={modelHeadIndex} bones={bones.Length} verts={vertices.Length})");
                continue;
            }
            found++;
            var modelHead = bones[modelHeadIndex];
            _h.LogInfo($"[cosmetic:hatanchor] smr='{smr.name}' mesh='{mesh.name}' verts={counted} " +
                       $"modelHeadBone='{modelHead.name}' modelHeadVsGame={Quaternion.Inverse(head.rotation) * modelHead.rotation}" +
                       $" euler={(Quaternion.Inverse(head.rotation) * modelHead.rotation).eulerAngles} " +
                       $"headLocal(min=({hminX:F4},{hminY:F4},{hminZ:F4}) max=({hmaxX:F4},{hmaxY:F4},{hmaxZ:F4}) " +
                       $"w={hmaxX - hminX:F4} top={hmaxY:F4} cx={(hminX + hmaxX) * 0.5f:F4} cz={(hminZ + hmaxZ) * 0.5f:F4} " +
                       $"h={hmaxY - hminY:F4}) rootBox(min=({rminX:F4},{rminY:F4},{rminZ:F4}) max=({rmaxX:F4},{rmaxY:F4},{rmaxZ:F4}))");
        }
        // Where the equipped cosmetics ACTUALLY are, in the same root space as the head box above:
        // hatBottom vs headTop is the seating error (negative = sunk into the head).
        foreach (var instance in _instances)
        {
            if (instance?.Root == null) continue;
            var b = new Bounds(instance.Root.transform.position, Vector3.zero);
            bool first = true;
            foreach (var r in instance.Renderers)
            {
                if (r == null) continue;
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            if (first) continue;
            var lo = modelRoot.InverseTransformPoint(b.min);
            var hi = modelRoot.InverseTransformPoint(b.max);
            _h.LogInfo($"[cosmetic:hatanchor] hat id='{instance.Id}' rootLocal={instance.Root.transform.localPosition} " +
                       $"scale={instance.Root.transform.lossyScale.x:F4} rootBox(min=({lo.x:F4},{lo.y:F4},{lo.z:F4}) max=({hi.x:F4},{hi.y:F4},{hi.z:F4}))");
        }
        _h.LogInfo($"[cosmetic:hatanchor] end modelRoot='{modelRoot.name}' bodies={found}");
        // The same numbers the live placement produces, in world metres, so a capture can be tied to
        // a measured seat instead of an opinion about a screenshot.
        LogSeat("probe", _body, _head, modelRoot, ModelRoster.WornSpec(modelRoot));
    }

    private static void Accumulate(ref Vector3 sum, Il2CppReferenceArray<Transform> bones,
        Il2CppStructArray<Matrix4x4> bindposes, int index, float weight, Vector3 v)
    {
        if (weight <= 0f || index < 0 || index >= bones.Length || index >= bindposes.Length) return;
        var b = bones[index];
        if (b == null) return;
        var m = b.localToWorldMatrix * bindposes[index];
        sum += m.MultiplyPoint3x4(v) * weight;
    }

    // ================================================================ state file

    private void LoadEquippedState()
    {
        try
        {
            var configDir = _h.ConfigPath == null ? null : Path.GetDirectoryName(_h.ConfigPath);
            if (string.IsNullOrEmpty(configDir)) return;
            _statePath = Path.Combine(configDir, "cosmetics.txt");
            if (!File.Exists(_statePath))
            {
                _equipped.Add(DefaultEquipped);
                _stateDirty = true;
                _equippedVersion++;      // the startup default is an equipped-set mutation too
                _h.LogInfo($"[cosmetic:state] no '{_statePath}'; starting with '{DefaultEquipped}'");
                return;
            }
            foreach (var line in File.ReadAllLines(_statePath))
            {
                var id = line.Trim();
                if (id.Length == 0 || id.StartsWith("#", StringComparison.Ordinal)) continue;
                if (_equipped.Add(id)) _equippedVersion++;   // the same bump every other mutation does
            }
            _h.LogInfo($"[cosmetic:state] loaded {_equipped.Count} id(s) from '{_statePath}'");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:state] load failed: {e.Message}"); }
    }

    private void FlushState()
    {
        if (!_stateDirty || _statePath == null || Time.unscaledTime < _nextStateFlush) return;
        _nextStateFlush = Time.unscaledTime + 1f;
        _stateDirty = false;
        try
        {
            var ids = new List<string>(_equipped);
            ids.Sort(StringComparer.Ordinal);
            File.WriteAllLines(_statePath, ids);
            _h.LogInfo($"[cosmetic:state] wrote {ids.Count} id(s) to '{_statePath}': {string.Join(",", ids)}");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:state] write failed: {e.Message}"); }
    }

    // ================================================================ proof render

    // Bounded pixel proof, independent of the locked desktop: the same viewpoint is rendered
    // through the real screen-camera mask in third person (hat expected), through the real mirror
    // scope+mask (hat expected) and through the screen mask in first person (hat expected hidden).
    private void RunProof()
    {
        // Pixel/RT proof is opt-in: it briefly forces the local POV so the same viewpoint can be
        // rendered through the screen mask (third person), the mirror scope+mask, and the screen
        // mask again in first person. Enable with ALTERUM_COSMETIC_PROOF=1.
        if (!ProofEnabled) return;
        if (_proofStage >= 5) return;
        if (_instances.Count == 0 || !_fitReady) return;
        if (Time.unscaledTime < _proofAt) return;
        var instance = _instances[0];
        try
        {
            switch (_proofStage)
            {
                case 0:
                    if (_nextProof < 0f || Time.unscaledTime < _nextProof) return;
                    _proofRestorePov = _pov;
                    if (_pov != POV.ThirdPerson)
                    {
                        ForcePov(true);
                        _h.LogInfo($"[cosmetic:proof-pov] forced=ThirdPerson was={_pov} (local camera mode, restored after proof)");
                    }
                    _proofStage = 1;
                    _proofAt = Time.unscaledTime + 2.0f;
                    break;
                case 1:
                    DumpProof(instance, ScreenMask(), "screen_thirdperson", false);
                    if (_mirrorCamera != null) DumpProof(instance, _mirrorCamera.cullingMask, "mirror", true);
                    else _h.LogWarning("[cosmetic:proof] no mirror camera yet; mirror dump skipped");
                    _proofStage = 2;
                    _proofAt = Time.unscaledTime + 0.5f;
                    break;
                case 2:
                    ForcePov(false);
                    _h.LogInfo("[cosmetic:proof-pov] forced=FirstPerson (negative control for the screen policy)");
                    _proofStage = 3;
                    _proofAt = Time.unscaledTime + 2.0f;
                    break;
                case 3:
                    DumpProof(instance, ScreenMask(), "screen_firstperson", false);
                    _proofStage = 4;
                    _proofAt = Time.unscaledTime + 0.5f;
                    break;
                case 4:
                    if (_proofRestorePov == POV.ThirdPerson) { ForcePov(true); _h.LogInfo("[cosmetic:proof-pov] restored=ThirdPerson"); }
                    else _h.LogInfo($"[cosmetic:proof-pov] restored={_proofRestorePov} (game default kept)");
                    _proofStage = 5;
                    _proofDumps++;
                    break;
            }
        }
        catch (Exception e) { _h.LogError($"[cosmetic:proof-failed] {e}"); _proofStage = 5; }
    }

    private int ScreenMask()
    {
        int mask = 0;
        var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i].TryCast<Camera>();
            if (MirrorFactory.ScreenCamera(c)) mask |= c.cullingMask;
        }
        return mask == 0 ? _lastScreenMask : mask;
    }

    private void ForcePov(bool thirdPerson)
    {
        try
        {
            if (_owner == null) return;
            var controllers = SceneCache.Scan(Il2CppType.Of<PlayerPOVController>());
            for (int i = 0; i < controllers.Length; i++)
            {
                var controller = controllers[i].TryCast<PlayerPOVController>();
                if (controller == null || !MirrorPlayerVisibility.Under(controller.transform, _owner)) continue;
                if (thirdPerson) controller.EnableThirdPersonViewMode();
                else controller.EnableFirstPersonViewMode();
                _nextPovPoll = 0f;
                return;
            }
            _h.LogWarning("[cosmetic:proof-pov] local PlayerPOVController not found");
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:proof-pov] {e.Message}"); }
    }

    private void DumpProof(Instance instance, int mask, string label, bool viaMirrorScope)
    {
        GameObject camGo = null, lightGo = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        var previous = RenderTexture.active;
        try
        {
            const int W = 640, H = 640;
            var headPos = _head.position;
            var up = _hatRotation * Vector3.up;
            var fwd = _hatRotation * Vector3.forward;
            var center = headPos + up * 0.12f;
            var camPos = center + fwd * 1.1f + up * 0.12f;

            camGo = new GameObject("CosmeticProofCam");
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) return;
            cam.transform.position = camPos;
            cam.transform.LookAt(center);
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
            cam.cullingMask = mask;
            cam.enabled = false;

            lightGo = new GameObject("CosmeticProofLight");
            lightGo.transform.position = camPos + up * 0.3f;
            var light = lightGo.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
            if (light != null)
            {
                light.type = LightType.Point;
                light.range = 6f;
                light.intensity = 2.5f;
                light.color = Color.white;
            }

            int enabledBefore = 0;
            foreach (var r in instance.Renderers) if (r != null && r.enabled) enabledBefore++;

            rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            if (viaMirrorScope && _mirrorCamera != null)
            {
                using (BeginMirrorView(_mirrorCamera)) cam.Render();
            }
            else cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            var pixels = tex.GetPixels32(0);
            // The lobby fills the frame, so use the corner pixel as the "no hat here" reference.
            int bgR = pixels[0].r, bgG = pixels[0].g, bgB = pixels[0].b;
            int lit = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int r = pixels[i].r, g = pixels[i].g, b = pixels[i].b;
                if (Mathf.Abs(r - bgR) > 14 || Mathf.Abs(g - bgG) > 14 || Mathf.Abs(b - bgB) > 14) lit++;
            }
            float coverage = 100f * lit / pixels.Length;

            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(_h.ScreensDir);
            string path = Path.Combine(_h.ScreensDir, $"cosmetic_proof_{label}_{instance.Id}.png");
            File.WriteAllBytes(path, bytes);
            _h.LogInfo($"[cosmetic:proof] label={label} id={instance.Id} mask=0x{mask:X8} " +
                       $"layer{TvPlayerModelLayer}InMask={(mask & (1 << TvPlayerModelLayer)) != 0} pov={_pov} " +
                       $"renderersEnabled={enabledBefore}/{instance.Renderers.Count} viaMirrorScope={viaMirrorScope} cam={camPos} " +
                       $"nonBackgroundPixels={lit} coverage={coverage:F2}% -> {path} ({bytes.Length} bytes)");
        }
        catch (Exception e) { _h.LogError($"[cosmetic:proof-failed] label={label}: {e}"); }
        finally
        {
            RenderTexture.active = previous;
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
            try { if (lightGo != null) UnityEngine.Object.Destroy(lightGo); } catch { }
        }
    }

    private static string CameraPath(Transform t)
    {
        if (t == null) return "null";
        var path = t.name;
        for (int i = 0; i < 5 && t.parent != null; i++) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

}
