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
    private Quaternion _hatRotation = Quaternion.identity;   // world rotation of the cosmetic root
    private Vector3 _fitShift;                               // world-space shift in hat-local axes
    private Vector3 _headLossy = Vector3.one;
    private string _fitSource = "none";
    private float _nextBindPoll;
    private float _nextFitLog;
    // CHEST socket, resolved once per body binding: a spine bone present on every rig (the typed
    // Bones surface has no chest slot, and chest_01_* are female-only breast bones).
    private Transform _chestBone;
    private bool _chestResolved, _socketScanLogged;

    // ---------------------------------------------------------------- state
    private readonly HashSet<string> _equipped = new(StringComparer.Ordinal);
    private readonly List<Instance> _instances = new();
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

    private bool _overlayOpen;
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

    private sealed class Instance
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
        _overlayOpen = Environment.GetEnvironmentVariable("ALTERUM_COSMETIC_OVERLAY") == "1";
    }

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
        if (changed > 0) _stateDirty = true;
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

    internal void OnGUI()
    {
        if (_disposed || _pack == null) return;
        try
        {
            if (PanelKeyPressed()) _overlayOpen = !_overlayOpen;
            if (!_overlayOpen) return;
            var rect = new Rect(300f, 18f, 330f, Mathf.Min(600f, 90f + _pack.Cosmetics.Count * 26f));
            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 20f),
                $"Cosmetics ({_equipped.Count})  F12 to close");
            float y = rect.y + 26f;
            foreach (var def in SortedCosmetics())
            {
                bool on = _equipped.Contains(def.Id);
                if (GUI.Button(new Rect(rect.x + 8f, y, rect.width - 16f, 22f), (on ? "[x] " : "[ ] ") + def.Id))
                {
                    if (on) _equipped.Remove(def.Id); else _equipped.Add(def.Id);
                    _stateDirty = true;
                }
                y += 24f;
            }
            if (GUI.Button(new Rect(rect.x + 8f, y + 4f, rect.width - 16f, 22f), "Clear all"))
            {
                _equipped.Clear();
                _stateDirty = true;
            }
            GUI.Label(new Rect(18f, 300f, 640f, 60f), _overlayInfo);
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:overlay] {e.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        int destroyed = 0;
        foreach (var instance in _instances)
        {
            if (instance.Root != null) destroyed++;
            DestroyInstance(instance);
        }
        _instances.Clear();
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

    private List<CosmeticPack.CosmeticDef> SortedCosmetics()
    {
        var list = new List<CosmeticPack.CosmeticDef>(_pack.Cosmetics.Values);
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    private Mesh GetMesh(int id)
    {
        if (_meshes.TryGetValue(id, out var cached)) return cached;
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
        if (_textures.TryGetValue(id, out var cached)) return cached;
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
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
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
        if (_materials.TryGetValue(id, out var cached)) return cached;
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
            var models = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
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
            bool same = _fitReady && _bodyPtr == body.Pointer && _headPtr == head.Pointer &&
                        _bodyId == bodyId && _headId == headId;
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
            _chestResolved = false; _chestBone = null; _socketScanLogged = false;   // new rig: re-resolve the spine socket
            // Roots that died with the previous scene/model can never be re-parented; drop them
            // (and their markers) before the refit, then let Reconcile recreate them.
            RetireDeadInstances("rebind");
            ComputeFit(body, head);
        }
        catch (Exception e) { _h.LogWarning($"[cosmetic:bind] poll failed: {e.Message}"); }
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
            var providers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CurrentPlayerProvider>());
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
            var players = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Player>());
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
            _hatRotation = head.rotation * (Quaternion.Inverse(rTarget) * rSrc);   // world rotation of the cosmetic root
            var headToHat = Quaternion.Inverse(rSrc) * rTarget;                    // head-local -> hat-local

            float width, top, centerX, centerZ;
            string source;
            if (MeasureHeadMesh(head, headToHat, out width, out top, out centerX, out centerZ, out source))
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

            if (width <= 1e-5f || _pack.SourceHead.Width <= 1e-5f)
            {
                _h.LogError($"[cosmetic:fit-invalid] id=* width={width:F5} sourceWidth={_pack.SourceHead.Width:F5}");
                return;
            }

            var src = _pack.SourceHead;
            _fitScale = width / src.Width;
            _fitShift = new Vector3(centerX - src.CenterX * _fitScale,
                                    top - src.Top * _fitScale,
                                    centerZ - src.CenterZ * _fitScale);
            _fitSource = source;
            _fitReady = true;

            // P2.9: show what the unreadable-mesh fallback would produce here too, so the
            // unit-invariant ratios are measurable even when the head mesh is readable.
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
                       $"modelRoot='{_modelRoot?.name}' headBone='{head.name}'");
            foreach (var instance in _instances) Attach(instance);
        }
        catch (Exception e)
        {
            _h.LogError($"[cosmetic:fit-failed] {e}");
        }
    }

    // Head-weighted vertex bounds of the target head, expressed in the cosmetic's local frame.
    private bool MeasureHeadMesh(Transform head, Quaternion headToHat,
        out float width, out float top, out float centerX, out float centerZ, out string source)
    {
        width = top = centerX = centerZ = 0f;
        source = "mesh";
        var renderers = _body.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
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
                var controllers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerPOVController>());
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
            _h.LogInfo($"[cosmetic:pov] {previous} -> {_pov} (owner='{_owner?.name}')");
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
        bool screen = _fitReady && bodyActive && !hideAll && _pov == POV.ThirdPerson;
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
            var renderers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());
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
        bool want = _pov == POV.ThirdPerson && _fitReady && !_foreignHide;
        bool rescan = Time.unscaledTime >= _nextCameraScan;
        if (want == _screenLayerApplied && !rescan) return;
        _nextCameraScan = Time.unscaledTime + CameraScanSeconds;

        int bit = 1 << _localLayer;
        int screens = 0, changed = 0;
        var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
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
                       $"want={want} screenCameras={screens} masksChanged={changed} hideAll={_hideAll}");
        }
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
        _screenMasks.Add(new CameraMaskState { Camera = camera, OriginalMask = camera.cullingMask, InstanceId = id });
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
            var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
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
            DestroyInstance(instance);
            _instances.RemoveAt(i);
            _h.LogInfo($"[cosmetic:invalid] id={instance.Id} reason=dead-or-reparented instances={_instances.Count} markers={CosmeticMarkers.RendererCount}");
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
    private bool InstanceLive(Instance instance)
    {
        if (instance.Root == null) return false;
        if (instance.Renderers.Count == 0) return false;
        for (int i = 0; i < instance.Renderers.Count; i++)
            if (instance.Renderers[i] == null) return false;
        if (!_fitReady || _head == null || _body == null) return true; // socket check is deferred until a fit exists; root/renderer liveness was checked above
        var socket = ResolveSocket(instance.Def);
        if (socket == null) return false;
        try
        {
            var parent = instance.Root.transform.parent;
            return parent != null && parent.Pointer == socket.Pointer;
        }
        catch { return false; }
    }

    private Instance CreateInstance(CosmeticPack.CosmeticDef def)
    {
        var instance = new Instance { Id = def.Id, Def = def };
        try
        {
            int layer = _localLayer >= 0 ? _localLayer : TvPlayerModelLayer;
            var root = new GameObject("Cosmetic_" + def.Id);
            root.layer = layer;
            instance.Root = root;
            instance.RootPointer = root.transform.Pointer;
            // Registered before the tree is built, so a throw anywhere below still reaches
            // DestroyInstance's unregister path.
            CosmeticMarkers.AddRoot(root.transform);
            foreach (var node in def.Children) BuildNode(node, root.transform, instance);
            if (!Attach(instance))
            {
                _h.LogWarning($"[cosmetic:load-failed] id={def.Id} could not attach to its socket; retrying in 10s");
                DestroyInstance(instance);
                return null;
            }
            SetInstanceVisible(instance, !_hideAll);
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

    private void BuildNode(CosmeticPack.NodeDef node, Transform parent, Instance instance)
    {
        var go = new GameObject(string.IsNullOrEmpty(node.Name) ? "node" : node.Name);
        go.layer = _localLayer >= 0 ? _localLayer : TvPlayerModelLayer;
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
        foreach (var child in node.Children) BuildNode(child, t, instance);
    }

    // Returns true when the root is parented to its socket, so creation can fail loudly instead of
    // registering an instance that reconciliation would immediately reject.
    private bool Attach(Instance instance)
    {
        if (instance?.Root == null || _head == null || !_fitReady) return false;
        try
        {
            var t = instance.Root.transform;
            var socket = ResolveSocket(instance.Def);
            if (socket == null) return false;
            // The baked prefab offset (the child TRS chain) is preserved for every slot: only the
            // socket bone changes, the root keeps the calibrated source-rig orientation and the
            // measured head-fit shift. Applied to a chest item this puts the sign on the torso at
            // the same world rotation it had when it hung (wrongly) at the pelvis.
            t.SetParent(socket, false);
            var socketRot = socket.rotation;
            var lossy = socket.lossyScale;
            Vector3 worldPos = socket.position + _hatRotation * _fitShift;
            Vector3 offset = worldPos - socket.position;
            Vector3 localOffset = Quaternion.Inverse(socketRot) * offset;
            localOffset = new Vector3(localOffset.x / Mathf.Max(1e-6f, lossy.x),
                                      localOffset.y / Mathf.Max(1e-6f, lossy.y),
                                      localOffset.z / Mathf.Max(1e-6f, lossy.z));
            t.localPosition = localOffset;
            t.localRotation = Quaternion.Inverse(socketRot) * _hatRotation;
            // P2.8: the prefab root's authored localScale belongs to the cosmetic (MoreCompany
            // multiplies it into the runtime scale, it never replaces it). Fold it into the fitted
            // root scale exactly once; the fit offset stays in fitted units, because the root's
            // placement relative to the head is unchanged by the prefab's own scale.
            var rootScale = instance.Def?.RootScale ?? Vector3.one;
            float fitX = _fitScale * rootScale.x, fitY = _fitScale * rootScale.y, fitZ = _fitScale * rootScale.z;
            t.localScale = new Vector3(fitX / Mathf.Max(1e-6f, lossy.x),
                                       fitY / Mathf.Max(1e-6f, lossy.y),
                                       fitZ / Mathf.Max(1e-6f, lossy.z));
            t.gameObject.SetActive(true);

            if (instance.Def != null && instance.Def.Type == 2) ApplyChestClearance(instance, socket);

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
                           $"localScale={t.localScale} worldScale={_fitScale:F5} rootScale=({rootScale.x:F4},{rootScale.y:F4},{rootScale.z:F4}) " +
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
    private Transform ResolveSocket(CosmeticPack.CosmeticDef def)
    {
        var bones = _body?.Bones;
        switch (def.Type)
        {
            case 0: // HAT
                return _head;
            case 4: // HIP
                if (bones != null && bones.Pelvis != null) { LogSlotApprox(def, "Pelvis", "HIP"); return bones.Pelvis; }
                break;
            case 3: // R_LOWER_ARM
                if (bones != null && bones.RightWrist != null) { LogSlotApprox(def, "RightWrist", "R_LOWER_ARM (wrist instead of mid-forearm)"); return bones.RightWrist; }
                break;
            case 2: // CHEST
            {
                var chest = ResolveChestBone();
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
        return _head;
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

    // F12: F6/F8/F9 are the emote controls and F7 is the POV toggle in the merged build.
    private static bool PanelKeyPressed()
    {
        try
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.f12Key != null && keyboard.f12Key.wasPressedThisFrame;
        }
        catch { return false; }
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
                _h.LogInfo($"[cosmetic:state] no '{_statePath}'; starting with '{DefaultEquipped}'");
                return;
            }
            foreach (var line in File.ReadAllLines(_statePath))
            {
                var id = line.Trim();
                if (id.Length == 0 || id.StartsWith("#", StringComparison.Ordinal)) continue;
                _equipped.Add(id);
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
        var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
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
            var controllers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerPOVController>());
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
