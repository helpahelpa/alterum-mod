using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace AlterumModelSwap.Hot;

// Playable rigid body: a baked mesh with one rigid piece per game bone region, bound to the
// game's own third-person skeleton and built at runtime from an embedded binary. No Blender, no
// asset bundle, no FBX at runtime: the roster spec names the `.mset` blob and its albedo, which
// the offline bake writes (tracks/merged/tools/bake_*.py).
//
// This module began as the original rigid Sahur body. That entry is retired, and the module is now
// the shared rigid path for every `ModelKind.Rigid` roster entry (Golubiro today), so the asset,
// region table and portraits all come from that entry's `ModelSpec`. `SahurRig` remains the
// separate rigged path (Sahur/Roblox and Capuchino).
//
// Why a Mesh built here rather than a game asset: the source has no bones, no skin and one welded
// surface. The bake splits it at measured joints so every emitted vertex belongs to exactly one
// region, and each region is bound 1.0 to one of the game's bones. A wooden statue does not bend:
// rigid pieces with hard transitions are the intended result, and the boundary split means no
// triangle is smeared across two bones.
//
// What it drives and what it must not break:
//   * bones are `ThirdViewPlayerModel.Bones._allBones` of the LOCAL player only
//     (CurrentPlayerProvider -> MirrorPlayerVisibility.Under), the same skeleton the emote pack
//     and the jiggle solver write, so walking and every emote drive this mesh for free;
//   * the original body renderers are disabled, never destroyed, and restored on off/dispose;
//   * layer 6 is the mod's local-cosmetic layer, whose existing per-camera policy already gives
//     "third person + mirror, never first person". The renderers are also registered with
//     CosmeticMarkers so the mirror's body sweep and the cosmetic census treat them as ours -
//     that is what keeps this from fighting the layer-6 / layer-14 rules;
//
// Hotkey F3 toggles the body (F4 is the reel placement shortcut). Unattended control:
//   "<ScreensDir>/sahur.request", deleted on pickup, line-oriented:
//     on|off|toggle     the body (bare word on the first line works too)
//     dump=1            log the full state (pieces, bones, height, what is hidden)
internal static class SahurModel
{
    public const Key BodyKey = Key.F3;

    private const string RequestFileName = "sahur.request";
    private const float PollSeconds = 0.1f;
    private const float ResolveSeconds = 1.0f;
    private const float AssertSeconds = 0.5f;

    // The mod's local-cosmetic layer. CosmeticsRuntime owns the per-camera policy for it (third
    // person screen camera yes, first person no, mirror yes), which is exactly the body's rule.
    private const int ModelLayer = 6;

    private const float Scale = 0.01f;             // source is centimetres, 179.62 cm tall

    // bone names on the game's rig (UE-style, same names EmoteRuntime probes for), per spec:
    // RequiredBones = every region bone of the spec (all must exist).
    private static string[] RequiredBones;          // rebuilt from _spec before each build

    private static bool _announced;
    private static bool _on;
    private static float _nextPoll, _nextResolve, _nextAssert;

    // Read-only state for the hands lease (DefaultHands): which body module owns the skeleton right
    // now, and the model it is bound to. Both are reads of the fields above, never a second source.
    internal static bool Active => _on;
    internal static ThirdViewPlayerModel LiveModel => _model;
    internal static ModelSpec LiveSpec => _spec;
    internal static Renderer LiveRenderer => _smr;

    // The EFFECTIVE body, the same contract SahurRig exposes (the identity line reports one shape for
    // both kinds). A module that is merely ON is not a body: the rigid body object with a mesh is.
    private static bool _buildFailed;
    private static string _buildFailWhy = "";
    internal static bool BodyLive
    {
        get
        {
            try { return _on && _bodyRoot != null && _smr != null && _smr.sharedMesh != null && _smr.enabled; }
            catch { return false; }
        }
    }
    internal static string VisibleSpecSlug => BodyLive && _spec != null ? _spec.Slug : "";
    internal static string BodyStateName => !_on ? "native" : (BodyLive ? "live" : (_buildFailed ? "failed" : "pending"));
    internal static string BuildFailWhy => _buildFailed ? _buildFailWhy : "";

    // Shared resources are pinned by every body, not just the selected local row. The limit is
    // soft while consumers are alive; idle entries return to the two-entry LRU automatically.
    private const int CacheLimit = 2;
    private static readonly ModelAssetCache<Asset> _assets = new(CacheLimit, DestroyAsset);
    private static ModelAssetCache<Asset>.Lease _assetUse;
    private static readonly Dictionary<long, ModelAssetCache<Asset>.Lease> _previewAssetUses = new();
    private static ModelSpec _spec;                  // the model the live body is (or will be) built from
    private static Asset _asset;                     // == _assets[_spec.Slug] once built
    private static ThirdViewPlayerModel _model;
    private static Transform _owner;
    private static GameObject _bodyRoot;            // spec.BodyName under the model root
    private static Mesh _bodyMesh;                  // this body's PRIVATE clone (bindposes live on it)
    private static SkinnedMeshRenderer _smr;
    private static readonly List<BodyRenderer> _body = new();
    private static readonly List<Transform> _bones = new();
    private static int _hides, _restores, _binds, _builds;

    private struct Piece
    {
        public int Region, VertStart, VertCount, TriStart, TriCount;
    }

    private sealed class Asset
    {
        public Mesh Mesh;
        public Texture2D Albedo;
        public Material Body;
        public int VertexCount, TriangleCount, Pieces;
        public float HeightCm, MinY, MaxY;
        public bool Flip;                           // 180 deg yaw about Y (source faces -Z)
        public string[] RegionBoneName;             // region -> game bone name
        public long DecodedBytes;                   // albedo mip chain held by this asset
    }

    private struct BodyRenderer
    {
        public Renderer R;
        public bool HadEnabled;
        public bool Hidden;
    }

    // ================================================================== entry points

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        // Hands lease first: while an imported body is worn the native hands are the only geometry
        // around a held item, and the hide pass below has to know which renderers are leased.
        try { DefaultHands.Tick(log, dt); } catch (Exception e) { log.LogWarning($"[hands] tick threw: {e.Message}"); }
        // Chest/hip enhancement: the state lives in EnhancementSettings, the writes here.
        try { CustomEnhancements.Tick(log, dt); } catch (Exception e) { log.LogWarning($"[enh] tick threw: {e.Message}"); }
        if (!_announced)
        {
            _announced = true;
            try
            {
                log.LogInfo($"[sahur] ready trigger=none (request file only) request={RequestPath(log)} " +
                            "values=on/off/toggle, dump=1");
            }
            catch { }
        }

        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        try { PollRequest(log); } catch (Exception e) { log.LogWarning($"[sahur] request poll failed: {e.Message}"); }

        if (!_on)
        {
            if (_body.Count > 0) Restore(log, "off");
            return;
        }

        try { Apply(log, now); }
        catch (Exception e) { log.LogError($"[sahur] apply threw: {e}"); }
    }

    internal static bool On => _on;
    internal static int BuildCount => _builds;
    internal static ModelSpec Spec => _spec;

    // [mapbody] census: whether the module wants the body, what it is bound to, and the lease.
    internal static string MapBodyState()
    {
        string model = "none", owner = "none";
        try { if (_model != null && _model.gameObject != null) model = _model.gameObject.name; } catch { }
        try { if (_owner != null) owner = _owner.name; } catch { }
        return $"rigid[on={(_on ? 1 : 0)} spec='{(_spec == null ? "none" : _spec.Slug)}' bind='{model}' owner='{owner}' " +
               $"bodyGo={(_bodyRoot != null ? 1 : 0)} smr={(_smr != null ? 1 : 0)} hidden={_body.Count} " +
               $"builds={_builds} hides={_hides} restores={_restores}]";
    }

    // The wardrobe selects a model through the game's own model index; the visible body is still this
    // module. Only called on a selection edge, so a manual F3 press is never fought.
    internal static void SetFromWardrobe(IHostContext log, ModelSpec spec, bool on)
    {
        if (on && spec != null && !ReferenceEquals(spec, _spec))
        {
            // Hard switch: hand the host body back, drop the old mesh/body, build the new one on the
            // next Apply. The parsed assets stay cached until the LRU drops them.
            if (_body.Count > 0) { try { Restore(log, "spec-switch"); } catch { } }
            try { DestroyBody(log); } catch { }
            log?.LogInfo($"[sahur] spec '{( _spec == null ? "none" : _spec.Slug )}' -> '{spec.Slug}' " +
                         $"(rigid path, label '{spec.Label}', yaw={(spec.Yaw180 ? 180 : 0)})");
            _spec = spec;
            _bodyRoot = null; _smr = null;
        }
        if (_on == on) return;
        _on = on;
        log?.LogInfo($"[sahur] {(on ? "ON" : "OFF")} (from wardrobe selection, '{(_spec == null ? "?" : _spec.Label)}')");
        if (!on)
        {
            try { Restore(log, "wardrobe"); } catch { }
            try { DestroyBody(log); } catch { }
        }
    }

    internal static void Dispose(IHostContext log)
    {
        try { DefaultHands.Dispose(log); } catch { }
        try { CustomEnhancements.Dispose(log); } catch { }
        try { Restore(log, "dispose"); } catch { }
        try { DestroyBody(log); } catch { }
        try { DespawnHuman(log); } catch { }
        _assets.Dispose(); // outstanding preview leases release after runtime teardown
        _asset = null;
        _model = null;
        _owner = null;
        _on = false;
        _announced = false;
        if (log != null)
            try { log.LogInfo($"[sahur] disposed builds={_builds} binds={_binds} hides={_hides} restores={_restores}"); } catch { }
    }

    // ================================================================== input

    private static void PollRequest(IHostContext log)
    {
        float now = Time.unscaledTime;
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        string path = RequestPath(log);
        if (path == null) return;
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[sahur] request read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }

        bool sawBody = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            string key = eq > 0 ? line.Substring(0, eq).Trim().ToLowerInvariant() : "";
            string val = eq > 0 ? line.Substring(eq + 1).Trim().ToLowerInvariant() : line.ToLowerInvariant();
            switch (key)
            {
                case "dump":
                    Dump(log, val != "0");
                    break;
                case "ref":
                    // reference frame: the same mesh, unskinned, through a MeshRenderer at the same
                    // world transform. Anything that differs between this and the skinned body is
                    // the skinning, not the mesh or the capture camera.
                    {
                        GameObject refGo = null;
                        try
                        {
                            var wbRef = _smr.bounds;
                            refGo = new GameObject("Sahur_RefBody");
                            refGo.layer = ModelLayer;
                            refGo.transform.position = _bodyRoot.transform.position;
                            refGo.transform.rotation = _bodyRoot.transform.rotation;
                            refGo.transform.localScale = Vector3.one;
                            var mf = refGo.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
                            var mr = refGo.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
                            if (mf != null) mf.sharedMesh = _asset.Mesh;
                            if (mr != null)
                            {
                                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                                var m2 = new Material(sh) { name = "Cosmetic_SahurRef" };
                                if (m2.HasProperty("_BaseMap")) m2.SetTexture("_BaseMap", (Texture)_asset.Albedo);
                                if (m2.HasProperty("_BaseColor")) m2.SetColor("_BaseColor", Color.white);
                                // same submesh/material rule as the skinned body: one per piece
                                var refMats = new Material[Math.Max(1, _asset.Pieces)];
                                for (int m = 0; m < refMats.Length; m++) refMats[m] = m2;
                                mr.sharedMaterials = new Il2CppReferenceArray<Material>(refMats);
                                log.LogInfo($"[sahur:ref] ref bounds world={mr.bounds.size.ToString("F2")} center={mr.bounds.center.ToString("F2")} " +
                                            $"smr bounds world={wbRef.size.ToString("F2")} center={wbRef.center.ToString("F2")}");
                            }
                            if (_smr != null) _smr.enabled = false;
                            Capture(log, "ref");
                            if (_smr != null) _smr.enabled = true;
                        }
                        finally { try { if (refGo != null) UnityEngine.Object.Destroy(refGo); } catch { } }
                    }
                    break;
                case "nobody":
                    // negative control: same frame with every renderer of ours switched off
                    if (_smr != null) _smr.enabled = false;
                    Capture(log, "nobody");
                    if (_smr != null) _smr.enabled = true;
                    break;
                case "probe":
                    Probe(log);
                    break;
                case "mats":
                    CompareGame(log, val);
                    break;
                case "wardrobe":
                    SahurWardrobe.Command(log, val);
                    break;
                case "human":
                    // scale reference: a real game body next to Sahur, on our layer so the capture
                    // rig renders both in one orthographic frame
                    if (val == "0" || val == "off") DespawnHuman(log);
                    else SpawnHuman(log);
                    break;
                case "bindtest":
                    // diagnostic: everything rigid to one bone. The mesh must then render exactly in
                    // its bind pose at the player root; if it does not, the fault is not the bind.
                    BindTest(log, val.Length == 0 ? "pelvis" : val);
                    break;
                case "view":
                    Capture(log, val.Length == 0 ? "front" : val);
                    break;
                case "mirror":
                    // proof-of-pixels for the mirror: read the mirror camera's own RT (frames 90/180
                    // of the mirror generation are the only other dumps, in Mirror.cs which is shared)
                    DumpMirror(log, val.Length == 0 ? "request" : val);
                    break;
                case "":                                     // bare word: the body verb
                case "body":
                case "sahur":
                    sawBody = true;
                    switch (val)
                    {
                        case "on": case "1": case "yes": SetOn(log, true, "request"); break;
                        case "off": case "0": case "no": SetOn(log, false, "request"); break;
                        case "toggle": SetOn(log, !_on, "request"); break;
                        case "dump": Dump(log, true); break;
                        default: log.LogWarning($"[sahur] request '{val}' is not on/off/toggle/dump"); break;
                    }
                    break;
                default:
                    log.LogWarning($"[sahur] request key '{key}' is not body/dump");
                    break;
            }
        }
        if (!sawBody && text.Trim().Length == 0) log.LogWarning("[sahur] request file was empty");
    }

    private static void SetOn(IHostContext log, bool on, string why)
    {
        if (_on == on)
        {
            if (why == "request") log.LogInfo($"[sahur] already {(_on ? "on" : "off")} (from {why})");
            return;
        }
        _on = on;
        log.LogInfo($"[sahur] {(on ? "ON" : "OFF")} (from {why})");
        if (on)
        {
            // Deliberate body choice outside the wardrobe: tell the wardrobe so the map path records
            // it as the wanted entry and stops forcing the game's row onto the same skeleton.
            try { SahurWardrobe.NoteBodyRequest(log, _spec ?? ModelRoster.DefaultRigid, true); }
            catch (Exception e) { log.LogWarning($"[sahur] wardrobe note failed: {e.Message}"); }
        }
        if (!on) { Restore(log, why); DestroyBody(log); }
    }

    // ================================================================== apply

    private static void Apply(IHostContext log, float now)
    {
        if (!BindReady(log, now)) return;
        if (_bodyRoot == null || _smr == null) Build(log);
        if (_bodyRoot == null) return;
        try { _bodyRoot.SetActive(true); } catch { }
        if (now >= _nextAssert)
        {
            _nextAssert = now + AssertSeconds;
            HideBody(log);
        }
    }

    private static bool BindReady(IHostContext log, float now)
    {
        bool alive = false;
        try { alive = _model != null && _model.gameObject != null && _model.gameObject.activeInHierarchy; }
        catch { alive = false; }
        if (alive) return true;

        // The bound model went away (scene change, respawn, mirror rebuild): give its renderers
        // back first so a dead list cannot leak into the new one.
        if (_body.Count > 0) Restore(log, "model-gone");
        DestroyBody(log); // includes the asset lease even if Unity already destroyed its objects
        if (now < _nextResolve) return false;
        _nextResolve = now + ResolveSeconds;

        var (model, owner) = FindLocalBody();
        if (model == null || model.gameObject == null)
        {
            log.LogWarning($"[sahur:nobody] rigid body wanted but no local ThirdViewPlayerModel resolved " +
                           $"(owner='{(owner == null ? "null" : owner.name)}' scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' " +
                           $"on={_on}) - retrying in {ResolveSeconds:F1}s");
            return false;
        }
        _model = model;
        _owner = owner;
        log.LogInfo($"[sahur:bind] model='{model.gameObject.name}' owner='{(owner == null ? "null" : owner.name)}'");
        try { _bones.Clear(); } catch { }
        return true;
    }

    private static (ThirdViewPlayerModel, Transform) FindLocalBody()
    {
        // Same resolution as the rigged module and the mirror: provider first, Netcode IsOwner
        // second. A body with no resolved owner is NOT ours; the old version accepted any active
        // ThirdViewPlayerModel in that case, which on a client bound to the host's body.
        Transform owner = null;
        try { var player = ReelLogic.FindLocalPlayerEntity(); if (player != null && player.transform != null) owner = player.transform; } catch { }
        if (owner == null) return (null, null);
        try
        {
            var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < models.Length; i++)
            {
                var m = models[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;   // prefab asset
                if (!MirrorPlayerVisibility.Under(m.transform, owner)) continue;
                if (!go.activeInHierarchy) continue;
                var bones = m.Bones;
                if (bones == null || bones._allBones == null || bones._allBones.Count == 0) continue;
                return (m, owner);
            }
        }
        catch { }
        return (null, owner);
    }

    // ================================================================== build

    private static void Build(IHostContext log)
    {
        var model = _model;
        if (model == null) return;
        var root = model.transform;
        if (root == null) return;
        var spec = _spec ?? (_spec = ModelRoster.DefaultRigid);
        if (spec == null) { log.LogWarning("[sahur:build] no rigid roster entry to build"); return; }
        RequiredBones = RequiredFor(spec);

        var bones = BoneMap(model);
        string missing = null;
        foreach (var want in RequiredBones)
            if (!bones.ContainsKey(want)) missing = (missing == null ? want : missing + "," + want);
        if (missing != null)
        {
            log.LogWarning($"[sahur:skip] model '{model.gameObject.name}' is missing bones '{missing}' " +
                           $"(the '{spec.Label}' rigid bind needs {(spec.IsRigged ? "?" : string.Join(",", spec.RegionBones))}); " +
                           "retrying on the next resolve");
            _model = null;
            return;
        }

        _assetUse?.Dispose(); _assetUse = null;
        using var assetUse = AcquireAsset(log, spec, bones, root);
        var asset = _asset = assetUse?.Value;
        if (asset == null) { _model = null; return; }

        // Bind pose: the mesh sits in the renderer's local space, the renderer at identity under
        // the player model root, so a bindpose is a bone's inverse space at bind times that root.
        //
        // SOURCE OF THE BIND. The published contract for a rigid body is the NATIVE bind - the same
        // construction BuildPreviewBody uses for remote and preview bodies:
        //     P' = P_native * renderer.worldToLocal * root.localToWorld
        // The live-bone form (b.worldToLocalMatrix * l2w) is only the fallback for a bone no native
        // renderer publishes. A live pose is not a bind: a body built while the skeleton is already
        // idling bakes that frame into the bindpose and the pieces sit offset - the "does not render
        // properly" symptom reported for a rigid body. `[sahur:bind]` records BOTH sources and their
        // measured disagreement per build, so which one was used is evidence, not a claim.
        //
        // The bindposes go onto a PRIVATE CLONE of the mesh. The cached asset mesh is shared with
        // the wardrobe preview and with any later build; writing it would move the other body (and
        // did: one build's live pose became the next preview's starting point).
        _bones.Clear();
        for (int i = 0; i < asset.RegionBoneName.Length; i++)
            _bones.Add(bones[asset.RegionBoneName[i]]);
        var l2w = root.localToWorldMatrix;
        var bind = new Matrix4x4[_bones.Count];
        var native = BindSources(model);
        int fromBind = 0, fromLive = 0;
        float worstDeg = -1f, worstMm = -1f;
        string worstBone = "-";
        for (int i = 0; i < _bones.Count; i++)
        {
            var b = _bones[i];
            Matrix4x4 live = b != null ? b.worldToLocalMatrix * l2w : Matrix4x4.identity;
            if (b != null && native.TryGetValue(b.name, out var src) && src.Found)
            {
                bind[i] = src.Bindpose * src.Renderer.worldToLocalMatrix * l2w;
                fromBind++;
                // Measured disagreement between the two sources, bone by bone. Not used to choose -
                // the native bind is the contract - only to make the choice visible in the log.
                float deg = Quaternion.Angle(live.rotation, bind[i].rotation);
                float mm = (live.GetColumn(3) - bind[i].GetColumn(3)).magnitude * 1000f;
                if (deg > worstDeg) { worstDeg = deg; worstBone = b.name; }
                if (mm > worstMm) worstMm = mm;
            }
            else
            {
                bind[i] = live;
                fromLive++;
            }
        }

        SweepOrphans(log, root, null);
        HideBodyBeforeBuild(log);

        GameObject go = null;
        Mesh clone = null;
        SkinnedMeshRenderer smr = null;
        int matCount = 1;
        try
        {
            // Non-generic Instantiate: the argument is cast to UnityEngine.Object so overload
            // resolution cannot select Instantiate<T>(T). That generic binds an IL2CPP generic
            // interop call which traps natively here - a managed catch cannot survive an access
            // violation, so the cast is what keeps the failure catchable at all (same form as
            // RemoteModels' per-body mesh clone, and this clone is destroyed in the catch below).
            clone = UnityEngine.Object.Instantiate((UnityEngine.Object)asset.Mesh)?.TryCast<Mesh>();
            if (clone == null) throw new InvalidOperationException("mesh clone failed");
            clone.name = spec.MeshName;
            clone.bindposes = new Il2CppStructArray<Matrix4x4>(bind);

            go = new GameObject(spec.BodyName);
            go.transform.SetParent(root, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.layer = ModelLayer;
            smr = go.AddComponent(Il2CppType.Of<SkinnedMeshRenderer>()).TryCast<SkinnedMeshRenderer>();
            if (smr == null) throw new InvalidOperationException("SkinnedMeshRenderer creation failed");

            smr.sharedMesh = clone;
            smr.bones = new Il2CppReferenceArray<Transform>(_bones.ToArray());
            smr.rootBone = bones.ContainsKey("pelvis") ? bones["pelvis"] : _bones[0];
            // One material PER SUBMESH. The body mesh has one submesh per rigid piece (8); a renderer
            // with fewer materials than submeshes draws only the ones that have one, so a single
            // material drew the torso piece alone. That is the whole "1/3 scale" bug: nothing was
            // scaled, only 332 of 3939 vertices (the 27.9 x 69.6 cm torso) were ever submitted.
            matCount = Math.Max(1, asset.Pieces);
            var mats = new Material[matCount];
            for (int m = 0; m < matCount; m++) mats[m] = asset.Body;
            smr.sharedMaterials = new Il2CppReferenceArray<Material>(mats);
            smr.updateWhenOffscreen = true;
            smr.allowOcclusionWhenDynamic = false;
            smr.skinnedMotionVectors = false;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = true;
            try
            {
                float h = asset.HeightCm * Scale;
                smr.localBounds = new Bounds(new Vector3(0f, h * 0.5f + spec.YOffset, 0f), new Vector3(0.6f, h, 0.9f));
            }
            catch { }

            _bodyRoot = go;
            _bodyMesh = clone;
            _smr = smr;
            _builds++;
            _buildFailed = false;
            _buildFailWhy = "";
            CosmeticMarkers.AddRoot(go.transform);
            CosmeticMarkers.Add(smr);
            _assetUse = assetUse.Retain();
        }
        catch (Exception e)
        {
            // A failed build must never leave the native body hidden with no replacement: the
            // snapshot HideBodyBeforeBuild took is restored here, and the partial objects are
            // destroyed. (`_model = null` makes the next resolve retry on a fresh body.)
            log.LogError($"[sahur:build] FAILED '{spec.Label}': {e.Message} - tearing the partial body " +
                         "down and restoring the native body");
            _buildFailed = true;
            _buildFailWhy = e.GetType().Name + ": " + e.Message;
            try { if (smr != null) CosmeticMarkers.Remove(smr.Pointer); } catch { }
            try { if (go != null) { CosmeticMarkers.RemoveRoot(go.transform.Pointer); UnityEngine.Object.Destroy(go); } } catch { }
            try { if (clone != null) UnityEngine.Object.Destroy(clone); } catch { }
            try { Restore(log, "build-failed"); } catch { }
            _bodyRoot = null;
            _bodyMesh = null;
            _smr = null;
            _bones.Clear();
            _model = null;
            return;
        }

        log.LogInfo($"[sahur:bind] '{spec.Label}' native={fromBind}/{_bones.Count} live={fromLive} " +
                    $"worstLiveVsNative={(worstDeg < 0f ? "n/a" : worstDeg.ToString("F2", CultureInfo.InvariantCulture) + "deg/" +
                                                           worstMm.ToString("F1", CultureInfo.InvariantCulture) + "mm")} " +
                    $"worstBone='{worstBone}' mesh=private-clone");
        log.LogInfo($"[sahur:build] '{spec.Label}' root={PathOf(root)} layer={ModelLayer} ('{LayerMask.LayerToName(ModelLayer)}') " +
                    $"verts={asset.VertexCount} tris={asset.TriangleCount} submeshes={asset.Pieces} materials={matCount} " +
                    $"bones={string.Join(",", _bones.ConvertAll(b => b == null ? "null" : b.name).ToArray())} " +
                    $"height={(asset.HeightCm * Scale):F3}m scale={Scale} yaw={(asset.Flip ? 180 : 0)} " +
                    $"yRange=[{asset.MinY:F2},{asset.MaxY:F2}]cm yOffset={spec.YOffset:F3}m " +
                    $"builds={_builds}");
        _binds++;
        HideBody(log);
    }

    // A previous hot generation's body is a plain scene object this generation cannot address;
    // without the sweep it stays visible (or half-hidden) forever and the "one body, one rig"
    // invariant is unreachable. `keep` is the object this build is about to create (always null at
    // the call sites: the sweep runs before the build).
    internal static void SweepOrphans(IHostContext log, Transform root, GameObject keep)
    {
        if (root == null) return;
        try
        {
            var all = root.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            var doomed = new List<Transform>();
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null || t.gameObject == null) continue;
                if (!ModelRoster.IsOurObjectName(t.name ?? "")) continue;
                if (keep != null && (t.gameObject == keep || t.IsChildOf(keep.transform))) continue;
                // only the root of a subtree: destroying it takes the children with it
                var p = t.parent;
                bool rootOfOurs = p == null || !ModelRoster.IsOurObjectName(p.name ?? "");
                if (rootOfOurs) doomed.Add(t);
            }
            foreach (var t in doomed)
            {
                log?.LogInfo($"[sahur:orphan] destroying leftover '{PathOf(t)}' from an earlier generation");
                try { UnityEngine.Object.Destroy(t.gameObject); } catch { }
            }
        }
        catch (Exception e) { log?.LogWarning($"[sahur:orphan] sweep failed: {e.Message}"); }
    }

    private static Dictionary<string, Transform> BoneMap(ThirdViewPlayerModel model)
    {
        var map = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = model.Bones._allBones;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name ?? "";
                if (n.Length == 0) continue;
                if (!map.ContainsKey(n)) map[n] = t;      // first wins: duplicates are garment bones
            }
        }
        catch { }
        return map;
    }

    // ---------------------------------------------------------------- game human for scale
    private static GameObject _human;

    private static void DespawnHuman(IHostContext log)
    {
        try { if (_human != null) UnityEngine.Object.Destroy(_human); } catch { }
        _human = null;
        log?.LogInfo("[sahur:human] removed the scale reference");
    }

    private static void SpawnHuman(IHostContext log)
    {
        try
        {
            if (_model == null) { log.LogWarning("[sahur:human] no player model bound"); return; }
            PlayerModelData pick = null;
            var arr = SceneCache.Scan(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null || d.ThirdViewPlayerModel == null) continue;
                if ((d.name ?? "").IndexOf("Freya", StringComparison.OrdinalIgnoreCase) >= 0) { pick = d; break; }
                if (pick == null) pick = d;
            }
            if (pick == null) { log.LogWarning("[sahur:human] no PlayerModelData with a third-view prefab"); return; }
            DespawnHuman(null);

            var obj = UnityEngine.Object.Instantiate(pick.ThirdViewPlayerModel.gameObject);
            var go = obj?.TryCast<GameObject>();
            if (go == null) { log.LogWarning("[sahur:human] instantiate failed"); return; }
            var root = _model.transform;
            go.name = TagOf(_spec) + "_ScaleHuman";
            go.transform.position = root.position - root.right * 1.15f;
            go.transform.rotation = root.rotation;
            // every child on the cosmetic layer, so the private capture camera sees it
            var trs = go.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i].TryCast<Transform>();
                if (t != null) t.gameObject.layer = ModelLayer;
            }
            // the prefab is a live ragdoll: make it hold still
            var rbs = go.GetComponentsInChildren(Il2CppType.Of<Rigidbody>(), true);
            for (int i = 0; rbs != null && i < rbs.Length; i++)
            {
                var rb = rbs[i].TryCast<Rigidbody>();
                if (rb == null) continue;
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            _human = go;
            var tvm = go.GetComponent(Il2CppType.Of<ThirdViewPlayerModel>())?.TryCast<ThirdViewPlayerModel>();
            var head = tvm?.Bones?.Head;
            var feet = root.position;
            log.LogInfo($"[sahur:human] '{pick.name}' at {go.transform.position.ToString("F2")} " +
                        $"renderers={(go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true)?.Length ?? 0)} " +
                        $"bones={tvm?.Bones?._allBones?.Count ?? 0} headY={(head == null ? float.NaN : head.position.y):F3} " +
                        $"playerFeetY={feet.y:F3} -> " +
                        $"gameHeight={(head == null ? float.NaN : head.position.y - feet.y):F3}m (capture with view=compare)");
        }
        catch (Exception e) { log.LogError($"[sahur:human] failed: {e}"); }
    }

    private static void DestroyBody(IHostContext log)
    {
        try
        {
            if (_smr != null) _smr.enabled = false;
            if (_bodyRoot != null)
            {
                _bodyRoot.SetActive(false);
                CosmeticMarkers.Remove(_smr != null ? _smr.Pointer : IntPtr.Zero);
                CosmeticMarkers.RemoveRoot(_bodyRoot.transform.Pointer);
                UnityEngine.Object.Destroy(_bodyRoot);
                log?.LogInfo("[sahur] body object destroyed");
            }
        }
        catch (Exception e) { log?.LogWarning($"[sahur] body destroy failed: {e.Message}"); }
        // The mesh is this body's own clone, never the cached asset mesh: destroy it with the body.
        try { if (_bodyMesh != null) UnityEngine.Object.Destroy(_bodyMesh); }
        catch (Exception e) { log?.LogWarning($"[sahur] body mesh destroy failed: {e.Message}"); }
        _bodyRoot = null;
        _bodyMesh = null;
        _smr = null;
        _bones.Clear();
        _assetUse?.Dispose(); _assetUse = null;
    }

    // ================================================================== preview build
    //
    // The rigid mesh on another model's skeleton (the wardrobe preview). Same asset, same rigid
    // 1.0 weights, same per-submesh material rule: the only thing that differs is where the bind
    // pose comes from. The player build reads it from the live bones, which is safe because it
    // happens on a freshly spawned body; a preview has already been idling for a while, so the
    // live pose would be mistaken for the bind and the pieces would sit offset. A native body
    // renderer publishes the true bind in mesh.bindposes, so use that:
    //     P' = P_native * renderer.worldToLocal * previewRoot.localToWorld
    // (the same result `b.worldToLocalMatrix * l2w` gives at the true bind). The mesh is cloned:
    // bindposes live on the Mesh, so sharing it with the player body would move that one too.
    internal static GameObject BuildPreviewBody(IHostContext log, ModelSpec spec, ThirdViewPlayerModel model, int layer, out Mesh mesh, out string note)
    {
        mesh = null;
        note = null;
        if (model == null) return null;
        var root = model.transform;
        if (root == null) return null;
        if (spec == null) spec = _spec ?? ModelRoster.DefaultRigid;
        if (spec == null) { note = "no rigid roster entry"; return null; }
        var bones = BoneMap(model);
        string missing = null;
        foreach (var want in RequiredFor(spec))
            if (!bones.ContainsKey(want)) missing = (missing == null ? want : missing + "," + want);
        if (missing != null) { note = "missing bones " + missing; return null; }

        using var assetUse = AcquireAsset(log, spec, bones, root);
        var asset = assetUse?.Value;
        if (asset == null) { note = "asset load failed"; return null; }

        GameObject go = null;
        Mesh clone = null;
        try
        {
        clone = UnityEngine.Object.Instantiate((UnityEngine.Object)asset.Mesh)?.TryCast<Mesh>();
        if (clone == null) { note = "mesh clone failed"; return null; }
        clone.name = spec.PreviewBodyName;
        mesh = clone;

        var bindBones = new List<Transform>();
        for (int i = 0; i < asset.RegionBoneName.Length; i++) bindBones.Add(bones[asset.RegionBoneName[i]]);
        var bind = new Matrix4x4[bindBones.Count];
        var l2w = root.localToWorldMatrix;
        int fromBindposes = 0, fromLive = 0;
        var native = BindSources(model);
        for (int i = 0; i < bindBones.Count; i++)
        {
            var b = bindBones[i];
            if (b != null && native.TryGetValue(b.name, out var src) && src.Found)
            {
                bind[i] = src.Bindpose * src.Renderer.worldToLocalMatrix * l2w;
                fromBindposes++;
            }
            else
            {
                bind[i] = b != null ? b.worldToLocalMatrix * l2w : Matrix4x4.identity;
                fromLive++;
            }
        }

        go = new GameObject(spec.PreviewBodyName);
        go.transform.SetParent(root, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        go.layer = layer <= 0 ? ModelLayer : layer;
        var smr = go.AddComponent(Il2CppType.Of<SkinnedMeshRenderer>()).TryCast<SkinnedMeshRenderer>();
        if (smr == null)
        {
            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(clone);
            mesh = null;
            note = "SkinnedMeshRenderer creation failed";
            return null;
        }
        clone.bindposes = new Il2CppStructArray<Matrix4x4>(bind);
        smr.sharedMesh = clone;
        smr.bones = new Il2CppReferenceArray<Transform>(bindBones.ToArray());
        smr.rootBone = bones.ContainsKey("pelvis") ? bones["pelvis"] : bindBones[0];
        int matCount = Math.Max(1, asset.Pieces);
        var mats = new Material[matCount];
        for (int m = 0; m < matCount; m++) mats[m] = asset.Body;
        smr.sharedMaterials = new Il2CppReferenceArray<Material>(mats);
        smr.updateWhenOffscreen = true;
        smr.allowOcclusionWhenDynamic = false;
        smr.skinnedMotionVectors = false;
        smr.shadowCastingMode = ShadowCastingMode.On;
        smr.receiveShadows = true;
        try
        {
            float h = asset.HeightCm * Scale;
            smr.localBounds = new Bounds(new Vector3(0f, h * 0.5f + spec.YOffset, 0f), new Vector3(1.4f, h, 1.4f));
        }
        catch { }
        CosmeticMarkers.AddRoot(go.transform);
        CosmeticMarkers.Add(smr);
        note = $"bindposes={fromBindposes}/{bindBones.Count} live={fromLive}";
        log.LogInfo($"[sahur:preview] rigid body under '{PathOf(root)}' layer={go.layer} " +
                    $"verts={asset.VertexCount} pieces={asset.Pieces} materials={matCount} " +
                    $"bones={string.Join(",", bindBones.ConvertAll(x => x == null ? "null" : x.name).ToArray())} " +
                    $"{note}");
        _previewAssetUses.Add(clone.Pointer.ToInt64(), assetUse.Retain());
        return go;
        }
        catch (Exception e)
        {
            DestroyPreviewBody(log, go, clone);
            mesh = null;
            note = "build failed: " + e.Message;
            return null;
        }
    }

    internal static void DestroyPreviewBody(IHostContext log, GameObject go, Mesh mesh)
    {
        long key = 0;
        try { if (!ReferenceEquals(mesh, null)) key = mesh.Pointer.ToInt64(); } catch { }
        try
        {
            if (go != null)
            {
                var smr = go.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>())?.TryCast<SkinnedMeshRenderer>();
                if (smr != null) { smr.enabled = false; CosmeticMarkers.Remove(smr.Pointer); }
                go.SetActive(false);
                CosmeticMarkers.RemoveRoot(go.transform.Pointer);
                UnityEngine.Object.Destroy(go);
            }
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            log?.LogInfo("[sahur:preview] rigid body destroyed");
        }
        catch (Exception e) { log?.LogWarning($"[sahur:preview] rigid destroy failed: {e.Message}"); }
        finally
        {
            if (_previewAssetUses.TryGetValue(key, out var use))
            {
                _previewAssetUses.Remove(key);
                use.Dispose();
            }
        }
    }

    private struct BindSource
    {
        public SkinnedMeshRenderer Renderer;
        public Matrix4x4 Bindpose;
        public bool Found;
    }

    // For every bone name the preview skeleton shares with a native body renderer, the renderer and
    // that bone's bindpose (bone local -> renderer local). First renderer wins; the concrete body
    // renderers all publish the same skeleton.
    private static Dictionary<string, BindSource> BindSources(ThirdViewPlayerModel model)
    {
        var map = new Dictionary<string, BindSource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            for (int r = 0; all != null && r < all.Length; r++)
            {
                var smr = all[r].TryCast<SkinnedMeshRenderer>();
                if (smr == null || IsOurs(smr)) continue;
                var mesh = smr.sharedMesh;
                var bs = smr.bones;
                if (mesh == null || bs == null) continue;
                var bp = mesh.bindposes;
                if (bp == null || bp.Length != bs.Length || bp.Length == 0) continue;
                for (int i = 0; i < bs.Length; i++)
                {
                    if (bs[i] == null) continue;
                    string n = bs[i].name ?? "";
                    if (n.Length == 0 || map.ContainsKey(n)) continue;
                    map[n] = new BindSource { Renderer = smr, Bindpose = bp[i], Found = true };
                }
            }
        }
        catch { }
        return map;
    }

    // ================================================================== original body hide/restore

    // Snapshot taken BEFORE the Sahur objects exist, so our own renderers can never enter the list.
    private static void HideBodyBeforeBuild(IHostContext log)
    {
        if (_body.Count > 0) return;
        var model = _model;
        if (model == null) return;
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < comps.Length; i++)
            {
                var r = comps[i]?.TryCast<Renderer>();
                if (r == null) continue;
                if (CosmeticMarkers.IsCosmetic(r)) continue;         // never hide a mod cosmetic
                if (DefaultHands.IsLeased(r)) continue;              // the hands lease owns this renderer
                var go = r.gameObject;
                if (go == null || ModelRoster.IsOurObjectName(go.name)) continue;
                bool had = false;
                try { had = r.enabled; } catch { continue; }
                _body.Add(new BodyRenderer { R = r, HadEnabled = had });
            }
            log.LogInfo($"[sahur:body] snapshot {_body.Count} renderer(s) under '{PathOf(model.transform)}' " +
                        "(disabled, not destroyed, while the Sahur body is active)");
        }
        catch (Exception e) { log.LogWarning($"[sahur:body] snapshot failed: {e.Message}"); }
    }

    // Disable, never destroy. Re-asserted every AssertSeconds because the mirror's body sweep and
    // the wardrobe restore write Renderer state from their own snapshots.
    private static void HideBody(IHostContext log)
    {
        if (_body.Count == 0) HideBodyBeforeBuild(log);
        int changed = 0;
        for (int i = 0; i < _body.Count; i++)
        {
            var e = _body[i];
            if (e.R == null) continue;
            // A leased hand/arm renderer is written by DefaultHands, not by this pass: re-hiding it
            // here would take the hands off the item every AssertSeconds.
            if (DefaultHands.IsLeased(e.R)) continue;
            bool on;
            try { on = e.R.enabled; } catch { continue; }
            if (!on) { continue; }
            try { e.R.enabled = false; e.Hidden = true; _body[i] = e; changed++; _hides++; }
            catch { }
        }
        if (changed > 0) log.LogInfo($"[sahur:body] hid {changed} body renderer(s), {CountHidden()} hidden total");
    }

    private static int CountHidden()
    {
        int n = 0;
        for (int i = 0; i < _body.Count; i++) if (_body[i].Hidden) n++;
        return n;
    }

    private static void Restore(IHostContext log, string why)
    {
        int restored = 0;
        for (int i = 0; i < _body.Count; i++)
        {
            var e = _body[i];
            if (!e.Hidden) continue;
            try { if (e.R != null) { e.R.enabled = e.HadEnabled; restored++; _restores++; } }
            catch { }
            e.Hidden = false;
            _body[i] = e;
        }
        _body.Clear();
        if (restored > 0 || why != "off")
            log?.LogInfo($"[sahur:body] restored {restored} body renderer(s) (why={why})");
        try { if (_bodyRoot != null) _bodyRoot.SetActive(false); } catch { }
    }

    // ================================================================== asset load

    private static ModelAssetCache<Asset>.Lease AcquireAsset(IHostContext log, ModelSpec spec, Dictionary<string, Transform> bones, Transform root)
    {
        spec ??= ModelRoster.DefaultRigid;
        if (spec == null) return null;
        return _assets.Acquire(spec.Slug, () => LoadAsset(log, spec, bones, root));
    }

    internal static int CacheCount => _assets.Count;

    internal static long CacheBytes
    {
        get
        {
            long n = 0;
            foreach (var a in _assets.Values) n += a == null ? 0 : a.DecodedBytes;
            return n;
        }
    }

    // Every region bone of the spec (all must exist on the game rig).
    private static string[] RequiredFor(ModelSpec spec)
    {
        return new List<string>(spec.RegionBones ?? new string[0]).ToArray();
    }

    private static Asset LoadAsset(IHostContext log, ModelSpec spec, Dictionary<string, Transform> bones, Transform root)
    {
        var asm = typeof(SahurModel).Assembly;
        byte[] mset = null, png = null;
        string msetName = null, pngName = null;
        try
        {
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (msetName == null && name.EndsWith(spec.BlobSuffix, StringComparison.OrdinalIgnoreCase)) msetName = name;
                if (pngName == null && name.EndsWith(spec.AlbedoSuffix, StringComparison.OrdinalIgnoreCase)) pngName = name;
            }
            if (msetName != null) using (var s = asm.GetManifestResourceStream(msetName)) mset = ReadAll(s);
            if (pngName != null) using (var s = asm.GetManifestResourceStream(pngName)) png = ReadAll(s);
        }
        catch (Exception e)
        {
            log.LogError($"[sahur:asset] resource read failed for '{spec.Slug}': {e.Message}");
            return null;
        }
        if (mset == null || png == null)
        {
            log.LogError($"[sahur:asset] missing resource(s) for '{spec.Slug}' " +
                         $"blob='{spec.BlobSuffix}' ({msetName ?? "not embedded"}) albedo='{spec.AlbedoSuffix}' ({pngName ?? "not embedded"})");
            return null;
        }

        var asset = new Asset();
        try
        {
            // Per-spec orientation: the variants2 bakes are mirrored to the game's handedness at bake
            // time (manifest mirror_x) and need no yaw; a bake that is not mirrored needs 180.
            bool flip = spec.Yaw180;
            if (flip) flip = DecideFlip(log, bones, root);
            else log.LogInfo($"[sahur:orient] '{spec.Slug}' baked mirrored to the game's handedness => yaw=0 deg, scale={Scale}");
            var src = ParseBlob(mset, asset, flip);
            BuildMesh(log, asset, src, spec);
        }
        catch (Exception e)
        {
            log.LogError($"[sahur:asset] parse/build failed for '{spec.Slug}': {e}");
            DestroyAsset(asset);
            return null;
        }

        // texture + materials (cloning a game URP/Lit keeps a working variant; Shader.Find alone
        // can return a stripped shader - same trick as LobbyPropFactory.CreateLitMaterial)
        try
        {
            int w, h;
            var rgba = PngDecoder.DecodeRgbaBottomUp(png, out w, out h);
            int levels;
            var chain = PngDecoder.BuildMipChain(rgba, w, h, out levels);
            asset.DecodedBytes = chain.Length;
            var tex = ModelAssetLifetime.Own(new Texture2D(w, h, TextureFormat.RGBA32, true, false));
            tex.name = "Cosmetic_" + spec.Tag + "Albedo";
            tex.LoadRawTextureData(new Il2CppStructArray<byte>(chain));
            tex.Apply(true, true);
            asset.Albedo = tex;
            asset.Body = LitMaterial(log, "Cosmetic_" + spec.Tag + "Albedo", tex, new Color(1f, 1f, 1f, 1f));
        }
        catch (Exception e)
        {
            log.LogError($"[sahur:asset] texture/material failed: {e}");
            DestroyAsset(asset);
            return null;
        }

        log.LogInfo($"[sahur:asset] '{spec.Label}' mset='{msetName}' {mset.Length} B albedo='{pngName}' {png.Length} B " +
                    $"verts={asset.VertexCount} tris={asset.TriangleCount} pieces={asset.Pieces} " +
                    $"height={asset.HeightCm:F2}cm yaw={(asset.Flip ? 180 : 0)} bones={string.Join(",", asset.RegionBoneName)}");
        return asset;
    }

    // The source faces -Z; the game's rig decides the yaw. Our left geometry sits at +X before a
    // 180 degree rotation, and the character's left hand is wherever hand_l is in the model root's
    // space, so the rotation is required exactly when hand_l.x is negative. Deciding it from the
    // bones (not a constant) is what keeps the arms from ending up crossed on a mirrored rig.
    private static bool DecideFlip(IHostContext log, Dictionary<string, Transform> bones, Transform root)
    {
        try
        {
            if (root == null || !bones.TryGetValue("hand_l", out var handL) || handL == null) return true;
            var local = root.InverseTransformPoint(handL.position);
            bool flip = local.x < 0f;
            log.LogInfo($"[sahur:orient] hand_l local=({local.x:F3},{local.y:F3},{local.z:F3}) in '{PathOf(root)}' " +
                        $"=> yaw={(flip ? 180 : 0)} deg, scale={Scale} (source faces -Z, left geometry at +X)");
            return flip;
        }
        catch (Exception e)
        {
            log.LogWarning($"[sahur:orient] hand_l read failed ({e.Message}); assuming 180 deg yaw");
            return true;
        }
    }

    private static string TagOf(ModelSpec s) => (s ?? ModelRoster.DefaultRigid)?.Tag ?? "Sahur";

    private static byte[] ReadAll(Stream s)
    {
        if (s == null) return null;
        var bytes = new byte[s.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = s.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < bytes.Length) Array.Resize(ref bytes, read);
        return bytes;
    }

    private struct Blob
    {
        public Piece[] Pieces;
        public int[] Region;            // per emitted vertex
        public Vector3[] Pos;           // mesh-local, already scaled and yawed
        public Vector2[] Uv;
        public Vector3[] Nrm;
        public int[] Indices;
    }

    private static Blob ParseBlob(byte[] b, Asset asset, bool flip)
    {
        if (b.Length < 48 || b[0] != (byte)'S' || b[1] != (byte)'A' || b[2] != (byte)'H' || b[3] != (byte)'U' ||
            b[4] != (byte)'R' || b[5] != (byte)'M' || b[6] != (byte)'1')
            throw new FormatException("bad magic (expected SAHURM1)");
        int o = 8;
        int version = BitConverter.ToInt32(b, o); o += 4;
        if (version != 1) throw new FormatException($"unsupported version {version}");
        int pieces = BitConverter.ToInt32(b, o); o += 4;
        float lox = BitConverter.ToSingle(b, o), loy = BitConverter.ToSingle(b, o + 4), loz = BitConverter.ToSingle(b, o + 8); o += 12;
        float hix = BitConverter.ToSingle(b, o), hiy = BitConverter.ToSingle(b, o + 4), hiz = BitConverter.ToSingle(b, o + 8); o += 12;
        int nvert = BitConverter.ToInt32(b, o); o += 4;
        int ntri = BitConverter.ToInt32(b, o); o += 4;
        var lo = new Vector3(lox, loy, loz);
        var span = new Vector3(hix - lox, hiy - loy, hiz - loz);

        var meta = new Piece[pieces];
        for (int i = 0; i < pieces; i++)
        {
            meta[i].VertStart = BitConverter.ToInt32(b, o); o += 4;
            meta[i].Region = b[o]; o += 4;                  // u8 region + 3 pad
            meta[i].VertCount = BitConverter.ToInt32(b, o); o += 4;
            meta[i].TriStart = BitConverter.ToInt32(b, o); o += 4;
            meta[i].TriCount = BitConverter.ToInt32(b, o); o += 4;
        }

        var region = new int[nvert];
        for (int i = 0; i < pieces; i++)
            for (int v = 0; v < meta[i].VertCount; v++)
                region[meta[i].VertStart + v] = meta[i].Region;

        // The source faces -Z. `flip` (a 180 degree yaw about Y) is decided by the caller from
        // the game's own bones, so the character's left hand ends up on the arm_l geometry.
        var pos = new Vector3[nvert];
        var nrm = new Vector3[nvert];
        for (int i = 0; i < nvert; i++)
        {
            int oq = o + i * 6;
            float px = lo.x + (BitConverter.ToInt16(b, oq) + 32768f) / 65535f * span.x;
            float py = lo.y + (BitConverter.ToInt16(b, oq + 2) + 32768f) / 65535f * span.y;
            float pz = lo.z + (BitConverter.ToInt16(b, oq + 4) + 32768f) / 65535f * span.z;
            if (flip) { px = -px; pz = -pz; }
            pos[i] = new Vector3(px * Scale, py * Scale, pz * Scale);
        }
        o += nvert * 6;
        var uv = new Vector2[nvert];
        for (int i = 0; i < nvert; i++)
        {
            int oq = o + i * 4;
            uv[i] = new Vector2(BitConverter.ToUInt16(b, oq) / 65535f, BitConverter.ToUInt16(b, oq + 2) / 65535f);
        }
        o += nvert * 4;
        for (int i = 0; i < nvert; i++)
        {
            int oq = o + i * 3;
            float nx = (sbyte)b[oq] / 127f, ny = (sbyte)b[oq + 1] / 127f, nz = (sbyte)b[oq + 2] / 127f;
            if (flip) { nx = -nx; nz = -nz; }
            nrm[i] = new Vector3(nx, ny, nz);
        }
        o += nvert * 3;
        var idx = new int[ntri * 3];
        for (int i = 0; i < idx.Length; i++) idx[i] = BitConverter.ToUInt16(b, o + i * 2);
        o += idx.Length * 2;
        if (o != b.Length) throw new FormatException($"{b.Length - o} trailing bytes");

        asset.Flip = flip;
        asset.Pieces = pieces;
        asset.VertexCount = nvert;
        asset.TriangleCount = ntri;
        asset.MinY = loy;
        asset.MaxY = hiy;
        asset.HeightCm = hiy - loy;
        return new Blob { Pieces = meta, Region = region, Pos = pos, Uv = uv, Nrm = nrm, Indices = idx };
    }

    private static void BuildMesh(IHostContext log, Asset asset, Blob src, ModelSpec spec)
    {
        if (spec.YOffset != 0f)
        {
            // The bake's origin is the source's, not the character's feet (min y can sit well above
            // the soles). A SkinnedMeshRenderer ignores its own transform - bindposes cancel it
            // - so the lift has to be in the geometry, which also carries it into the preview clone.
            for (int i = 0; i < src.Pos.Length; i++) src.Pos[i].y += spec.YOffset;
            log.LogInfo($"[sahur:mesh] '{spec.Slug}' lifted {spec.YOffset:F3} m so its feet sit on the model root");
        }
        var mesh = ModelAssetLifetime.Own(new Mesh { name = spec.MeshName });
        mesh.vertices = new Il2CppStructArray<Vector3>(src.Pos);
        mesh.uv = new Il2CppStructArray<Vector2>(src.Uv);
        mesh.normals = new Il2CppStructArray<Vector3>(src.Nrm);

        // Rigid weights: every vertex 1.0 to the single bone of its region. Bone array order =
        // the distinct region bones (region id == bone index), so region -> bone is a lookup. The
        // table lives in the spec (ModelRoster), not here: every variants2 rigid bake numbers its
        // regions the same way, and a bake that does not only needs its own array.
        asset.RegionBoneName = spec.RegionBones;
        var bw = new BoneWeight[src.Pos.Length];
        for (int i = 0; i < bw.Length; i++)
        {
            int r = src.Region[i];
            if (r < 0 || r >= asset.RegionBoneName.Length) r = 0;
            bw[i] = new BoneWeight
            {
                m_BoneIndex0 = r, m_Weight0 = 1f,
                m_BoneIndex1 = 0, m_Weight1 = 0f,
                m_BoneIndex2 = 0, m_Weight2 = 0f,
                m_BoneIndex3 = 0, m_Weight3 = 0f,
            };
        }
        mesh.boneWeights = new Il2CppStructArray<BoneWeight>(bw);

        mesh.subMeshCount = src.Pieces.Length;
        for (int p = 0; p < src.Pieces.Length; p++)
        {
            var piece = src.Pieces[p];
            var tri = new int[piece.TriCount * 3];
            Array.Copy(src.Indices, piece.TriStart * 3, tri, 0, tri.Length);
            mesh.SetTriangles(new Il2CppStructArray<int>(tri), p, false);
        }
        mesh.RecalculateBounds();
        asset.Mesh = mesh;
    }

    private static Material LitMaterial(IHostContext log, string name, Texture2D tex, Color tint)
    {
        Material template = null;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Material>());
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i].TryCast<Material>();
                if (m == null) continue;
                var sh = m.shader;
                if (sh == null || sh.name != "Universal Render Pipeline/Lit") continue;
                if (!m.HasProperty("_BaseMap")) continue;
                if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) continue;
                template = m;
                break;
            }
        }
        catch { }
        Material mat;
        if (template != null) mat = new Material(template);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            log.LogWarning($"[sahur:shader] no game URP/Lit template; Shader.Find found={sh != null}");
            mat = new Material(sh);
        }
        ModelAssetLifetime.Own(mat);
        mat.name = name;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex != null ? (Texture)tex : Texture2D.whiteTexture);
        if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
        if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", null);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        return mat;
    }

    private static void DestroyAsset(Asset a)
    {
        if (a == null) return;
        try { if (a.Mesh != null) UnityEngine.Object.Destroy(a.Mesh); } catch { }
        try { if (a.Albedo != null) UnityEngine.Object.Destroy(a.Albedo); } catch { }
        try { if (a.Body != null) UnityEngine.Object.Destroy(a.Body); } catch { }
    }

    // ================================================================== dump
    // (bumped once to force a hot reload: the live generation was left with the body hidden)

    private static void Dump(IHostContext log, bool verbose)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append($"[sahur:dump] on={_on} builds={_builds} binds={_binds} hides={_hides} restores={_restores} ");
            sb.Append($"bodyRoot={(_bodyRoot == null ? "none" : PathOf(_bodyRoot.transform))} ");
            sb.Append($"bodyRenderersSnapshot={_body.Count} hidden={CountHidden()} ");
            sb.Append($"model={(_model == null || _model.gameObject == null ? "none" : PathOf(_model.transform))} ");
            if (_asset != null)
                sb.Append($"asset verts={_asset.VertexCount} tris={_asset.TriangleCount} pieces={_asset.Pieces} " +
                          $"height={(Mathf.Abs(_asset.MaxY - _asset.MinY) * Scale):F3}m bounds={(Mathf.Abs(_asset.MaxY - _asset.MinY)):F2}cm ");
            if (_smr != null)
                sb.Append($"smr layer={_smr.gameObject.layer} ('{LayerMask.LayerToName(_smr.gameObject.layer)}') " +
                          $"bones={_smr.bones.Length} visible={_smr.enabled} submeshes={_smr.sharedMesh.subMeshCount} ");
            sb.Append($"spec='{(_spec == null ? "none" : _spec.Slug)}'");
            log.LogInfo(sb.ToString());
            if (verbose && _smr != null && _smr.sharedMesh != null)
            {
                var mesh = _smr.sharedMesh;
                var verts = mesh.vertices;
                if (verts.Length > 0)
                {
                    // in-renderer space: the mesh is drawn through the bones, so the world height of
                    // the skinned result is measured on the bones, not on these vertices
                    Vector3 lo = verts[0], hi = verts[0];
                    for (int i = 1; i < verts.Length; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
                    log.LogInfo($"[sahur:dump] mesh local y {lo.y:F3}..{hi.y:F3} m (height {(hi.y - lo.y):F3} m) " +
                                $"x {lo.x:F3}..{hi.x:F3} z {lo.z:F3}..{hi.z:F3}");
                }
                log.LogInfo($"[sahur:dump] bones: {BoneList()}");
                MeasureWorld(log);
                if (_model != null && _model.gameObject != null)
                {
                    var head = _model.Bones?.Head;
                    if (head != null)
                        log.LogInfo($"[sahur:dump] head bone world y={head.position.y:F3} m " +
                                    $"(the game's own head bone; the Sahur head bone is the same transform)");
                }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:dump] failed: {e.Message}"); }
    }

    // ================================================================== small helpers

    // The mirror camera is created by MirrorFactory with the local-cosmetic bit in its mask, so a
    // capture of its own RT is the honest check for "visible in the mirror". Read-only: this never
    // touches the mask, the mirror's renderer states or the RT's lifetime.
    private static void DumpMirror(IHostContext log, string tag)
    {
        Texture2D tex = null;
        var previous = RenderTexture.active;
        try
        {
            var cam = FindMirrorCamera(log);
            if (cam == null) { log.LogWarning("[sahur:mirror] no mirror camera in the scene (is the mirror placed?)"); return; }
            var rt = cam.targetTexture;
            if (rt == null) { log.LogWarning("[sahur:mirror] mirror camera has no RenderTexture"); return; }
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, $"sahur_mirror_{tag}.png");
            File.WriteAllBytes(path, bytes);
            log.LogInfo($"[sahur:mirror] cam='{cam.name}' {rt.width}x{rt.height} mask=0x{cam.cullingMask:X8} " +
                        $"layer{ModelLayer}InMask={((cam.cullingMask & (1 << ModelLayer)) != 0)} " +
                        $"bodyRoot={(_bodyRoot != null && _bodyRoot.activeSelf)} hidden={CountHidden()} -> {path} ({bytes.Length} bytes)");
        }
        catch (Exception e) { log.LogError($"[sahur:mirror] dump failed: {e}"); }
        finally
        {
            RenderTexture.active = previous;
            if (tex != null) try { UnityEngine.Object.Destroy(tex); } catch { }
        }
    }

    private static Camera _mirrorCam;

    private static bool IsOurs(Renderer r)
    {
        try
        {
            var t = r.transform;
            for (int i = 0; t != null && i < 10; i++, t = t.parent)
            {
                string n = t.name ?? "";
                if (ModelRoster.IsOurObjectName(n)) return true;
            }
        }
        catch { }
        return false;
    }

    private static void BindTest(IHostContext log, string boneName)
    {
        try
        {
            if (_smr == null || _model == null) { log.LogWarning("[sahur:bindtest] nothing built"); return; }
            var bones = BoneMap(_model);
            if (!bones.ContainsKey(boneName)) { log.LogWarning($"[sahur:bindtest] no bone '{boneName}'"); return; }
            var b = bones[boneName];
            var l2w = _model.transform.localToWorldMatrix;
            var mesh = _smr.sharedMesh;
            var one = new Il2CppStructArray<Matrix4x4>(new[] { b.worldToLocalMatrix * l2w });
            mesh.bindposes = one;
            var bw = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < bw.Length; i++) bw[i] = new BoneWeight { m_BoneIndex0 = 0, m_Weight0 = 1f };
            mesh.boneWeights = new Il2CppStructArray<BoneWeight>(bw);
            _smr.bones = new Il2CppReferenceArray<Transform>(new[] { b });
            log.LogInfo($"[sahur:bindtest] all {bw.Length} vertices -> '{boneName}' (1 bone); capture with view=front to see it");
        }
        catch (Exception e) { log.LogError($"[sahur:bindtest] failed: {e}"); }
    }

    // ---------------------------------------------------------------- compare vs the game body
    // The decisive experiment for the 1/3-scale report: read the game's OWN body renderer, then copy
    // exactly the values the diagnosis listed (bindposes convention, rootBone, quality, localBounds,
    // updateWhenOffscreen, forceMatrixRecalculationPerRender) onto ours one at a time and capture a
    // frame after each. `mats` reports the piece/material counts and reproduces the old
    // single-material body (one submesh drawn) next to the fixed one, which is what actually differed.
    private static SkinnedMeshRenderer GameBody()
    {
        try
        {
            if (_model == null) return null;
            var all = _model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var r = all[i].TryCast<SkinnedMeshRenderer>();
                if (r == null || r == _smr) continue;
                if (IsOurs(r)) continue;
                return r;
            }
        }
        catch (Exception e) { }
        return null;
    }

    private static void CompareGame(IHostContext log, string step)
    {
        try
        {
            if (_smr == null || _smr.sharedMesh == null)
            {
                log.LogWarning("[sahur:mats] nothing built (turn the body on first)");
                return;
            }
            var mesh = _smr.sharedMesh;
            int subs = mesh.subMeshCount;
            var mats = _smr.sharedMaterials;
            log.LogInfo($"[sahur:mats] ours: submeshes={subs} materials={(mats == null ? -1 : mats.Length)} " +
                        $"bones={(_smr.bones == null ? -1 : _smr.bones.Length)} rootBone='{(_smr.rootBone == null ? "null" : _smr.rootBone.name)}' " +
                        $"quality={_smr.quality} localBounds={_smr.localBounds.size.ToString("F3")}@{_smr.localBounds.center.ToString("F3")} " +
                        $"updateWhenOffscreen={_smr.updateWhenOffscreen} matrixPerRender={_smr.forceMatrixRecalculationPerRender} " +
                        $"meshBounds={mesh.bounds.size.ToString("F3")}@{mesh.bounds.center.ToString("F3")}");
            var sb = new StringBuilder();
            int total = 0;
            for (int p = 0; p < subs; p++)
            {
                int n = 0;
                try { n = mesh.GetTriangles(p).Length; } catch (Exception e) { n = -1; }
                total += Math.Max(0, n) / 3;
                // per-piece extent in mesh space: proves which pieces exist and how big they are
                var tri = n > 0 ? mesh.GetTriangles(p) : null;
                var verts = mesh.vertices;
                Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int k = 0; tri != null && k < tri.Length; k++)
                {
                    var v = verts[tri[k]];
                    lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
                }
                string ext = tri != null && tri.Length > 0 ? $" size={(hi - lo).ToString("F3")} y[{lo.y:F3},{hi.y:F3}]" : "";
                sb.AppendLine($"    submesh {p}: tris={(n < 0 ? n : n / 3)}{ext}");
            }
            log.LogInfo($"[sahur:mats] ours per-submesh (readback):\n{sb}  total tris={total} (mesh has {mesh.triangles.Length / 3})");

            var game = GameBody();
            if (game == null) log.LogWarning("[sahur:mats] no game body SkinnedMeshRenderer under the model root");
            else
            {
                var gm = game.sharedMesh;
                log.LogInfo($"[sahur:mats] game body '{PathOf(game.transform)}': submeshes={(gm == null ? -1 : gm.subMeshCount)} " +
                            $"materials={game.sharedMaterials.Length} bones={(game.bones == null ? -1 : game.bones.Length)} " +
                            $"rootBone='{(game.rootBone == null ? "null" : game.rootBone.name)}' quality={game.quality} " +
                            $"localBounds={game.localBounds.size.ToString("F3")}@{game.localBounds.center.ToString("F3")} " +
                            $"updateWhenOffscreen={game.updateWhenOffscreen} matrixPerRender={game.forceMatrixRecalculationPerRender} " +
                            $"skinnedMotionVectors={game.skinnedMotionVectors} lossyScale={game.transform.lossyScale.ToString("F3")}");
                // does the game's own bindpose follow bone.worldToLocal * renderer.localToWorld? If it
                // does, our formula is the engine's convention and the bind is not the fault.
                if (gm != null && game.bones != null && gm.bindposes.Length == game.bones.Length && game.bones.Length > 0)
                {
                    var l2w = game.transform.localToWorldMatrix;
                    var bp = gm.bindposes;
                    float worst = 0f;
                    for (int i = 0; i < game.bones.Length; i++)
                    {
                        if (game.bones[i] == null) continue;
                        var formula = game.bones[i].worldToLocalMatrix * l2w;
                        var got = bp[i];
                        for (int c = 0; c < 4; c++)
                            for (int r2 = 0; r2 < 4; r2++)
                                worst = Math.Max(worst, Math.Abs(formula[r2, c] - got[r2, c]));
                    }
                    log.LogInfo($"[sahur:mats] game bindposes vs bone.worldToLocal*renderer.localToWorld: worst |delta|={worst:F6} " +
                                $"=> convention {(worst < 1e-3f ? "MATCHES ours" : "differs")}");
                }
            }

            string s = (step ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return;
            if (s == "1" || s == "one")
            {
                // reproduce the old build (a single material) and the fix, in the same rig
                var save = _smr.sharedMaterials;
                _smr.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { _asset.Body });
                log.LogInfo("[sahur:mats] step=mat1: 1 material for 8 submeshes");
                Capture(log, "mat1");
                _smr.sharedMaterials = save;
                log.LogInfo($"[sahur:mats] step=mat8: {save.Length} materials");
                Capture(log, "mat8");
                return;
            }
            if (s == "all") s = "root,quality,bounds,offscreen,matrix";
            foreach (var raw in s.Split(','))
            {
                string one = raw.Trim();
                if (one.Length == 0) continue;
                if (game == null) break;
                switch (one)
                {
                    case "root":
                    {
                        var save = _smr.rootBone;
                        _smr.rootBone = game.rootBone;
                        log.LogInfo($"[sahur:mats] step=root: rootBone '{save?.name}' -> '{(game.rootBone == null ? "null" : game.rootBone.name)}'");
                        Capture(log, "step_root");
                        _smr.rootBone = save;
                        break;
                    }
                    case "quality":
                    {
                        var save = _smr.quality;
                        _smr.quality = game.quality;
                        log.LogInfo($"[sahur:mats] step=quality: {save} -> {game.quality}");
                        Capture(log, "step_quality");
                        _smr.quality = save;
                        break;
                    }
                    case "bounds":
                    {
                        var save = _smr.localBounds;
                        _smr.localBounds = game.localBounds;
                        log.LogInfo($"[sahur:mats] step=bounds: localBounds {save.size.ToString("F3")} -> {game.localBounds.size.ToString("F3")}");
                        Capture(log, "step_bounds");
                        _smr.localBounds = save;
                        break;
                    }
                    case "offscreen":
                    {
                        var save = _smr.updateWhenOffscreen;
                        _smr.updateWhenOffscreen = game.updateWhenOffscreen;
                        log.LogInfo($"[sahur:mats] step=offscreen: updateWhenOffscreen {save} -> {game.updateWhenOffscreen}");
                        Capture(log, "step_offscreen");
                        _smr.updateWhenOffscreen = save;
                        break;
                    }
                    case "matrix":
                    {
                        var save = _smr.forceMatrixRecalculationPerRender;
                        _smr.forceMatrixRecalculationPerRender = game.forceMatrixRecalculationPerRender;
                        log.LogInfo($"[sahur:mats] step=matrix: forceMatrixRecalculationPerRender {save} -> {game.forceMatrixRecalculationPerRender}");
                        Capture(log, "step_matrix");
                        _smr.forceMatrixRecalculationPerRender = save;
                        break;
                    }
                    case "bind":
                    {
                        // rebind through the game renderer's own bone order/bindposes where the bone
                        // names allow it: same convention, different mesh -> must not change the size
                        var save = mesh.bindposes;
                        var bp = new Matrix4x4[mesh.bindposes.Length];
                        var l2w = _model != null ? _model.transform.localToWorldMatrix : Matrix4x4.identity;
                        for (int i = 0; i < bp.Length; i++)
                        {
                            var b = _smr.bones != null && i < _smr.bones.Length ? _smr.bones[i] : null;
                            bp[i] = b != null ? b.worldToLocalMatrix * l2w : Matrix4x4.identity;
                        }
                        mesh.bindposes = new Il2CppStructArray<Matrix4x4>(bp);
                        log.LogInfo($"[sahur:mats] step=bind: rebuilt {bp.Length} bindposes through the model root (same formula)");
                        Capture(log, "step_bind");
                        mesh.bindposes = save;
                        break;
                    }
                    default:
                        log.LogWarning($"[sahur:mats] unknown step '{one}'");
                        break;
                }
            }
        }
        catch (Exception e) { log.LogError($"[sahur:mats] failed: {e}"); }
    }

    // ---------------------------------------------------------------- probe
    private static void Probe(IHostContext log)
    {
        try
        {
            if (_smr != null)
            {
                var wb = _smr.bounds;
                log.LogInfo($"[sahur:probe] smr bounds world center={wb.center.ToString("F2")} size={wb.size.ToString("F2")} " +
                            $"enabled={_smr.enabled} go.activeSelf={_smr.gameObject.activeSelf} layer={_smr.gameObject.layer} " +
                            $"updateWhenOffscreen={_smr.updateWhenOffscreen}");
                var mesh = _smr.sharedMesh;
                if (mesh != null)
                {
                    var mb = mesh.bounds;
                    var bind = mesh.bindposes;
                    log.LogInfo($"[sahur:probe] mesh bounds center={mb.center.ToString("F2")} size={mb.size.ToString("F2")} " +
                                $"bindposes={bind.Length} bind[0].pos=({bind[0].m03:F3},{bind[0].m13:F3},{bind[0].m23:F3}) " +
                                $"bind[0].scale=({bind[0].GetColumn(0).magnitude:F4},{bind[0].GetColumn(1).magnitude:F4},{bind[0].GetColumn(2).magnitude:F4})");
                }
            }
            // read the weights back (a silent setter failure is invisible otherwise) and bake the
            // current skinned pose: BakeMesh is the GPU's own answer, not our assumption.
            if (_smr != null && _smr.sharedMesh != null)
            {
                var bw = _smr.sharedMesh.boneWeights;
                if (bw != null && bw.Length > 0)
                    log.LogInfo($"[sahur:probe] weights readback: n={bw.Length} [0]=b{bw[0].m_BoneIndex0}/w{bw[0].m_Weight0:F2} " +
                                $"[500]=b{bw[500].m_BoneIndex0}/w{bw[500].m_Weight0:F2} [3900]=b{bw[3900].m_BoneIndex0}/w{bw[3900].m_Weight0:F2}");
                var tmp = new Mesh();
                try
                {
                    _smr.BakeMesh(tmp);
                    var bb = tmp.bounds;
                    var tv = tmp.vertices;
                    Vector3 lo = tv.Length > 0 ? tv[0] : Vector3.zero, hi = lo;
                    for (int i = 1; i < tv.Length; i++) { lo = Vector3.Min(lo, tv[i]); hi = Vector3.Max(hi, tv[i]); }
                    log.LogInfo($"[sahur:probe] BakeMesh verts={tv.Length} bounds center={bb.center.ToString("F2")} " +
                                $"size={bb.size.ToString("F2")} actual y {lo.y:F3}..{hi.y:F3} ({(hi.y - lo.y):F3} m)");
                }
                finally { UnityEngine.Object.Destroy(tmp); }
            }
            var cams = SceneCache.Scan(Il2CppType.Of<Renderer>());
            int onLayer = 0;
            foreach (var nm in new[] { "Sphere", "Cone" })
            {
                // the wardrobe party hat lives on our layer; log its world extent so a capture frame
                // can be attributed to it or to our mesh without guessing
            }
            var names = new StringBuilder();
            for (int i = 0; i < cams.Length; i++)
            {
                var r = cams[i].TryCast<Renderer>();
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || go.layer != ModelLayer) continue;
                if (!r.enabled || !go.activeInHierarchy) continue;
                onLayer++;
                if (onLayer <= 12)
                {
                    string bnd = "";
                    try { var bb = r.bounds; bnd = $" wb={bb.size.ToString("F2")}@y{bb.center.y:F2}"; } catch { }
                    names.Append(PathOf(r.transform)).Append(bnd).Append("; ");
                }
            }
            if (_bodyRoot != null)
            {
                var t = _bodyRoot.transform;
                log.LogInfo($"[sahur:probe] bodyRoot lossyScale={t.lossyScale.ToString("F3")} localScale={t.localScale.ToString("F3")} " +
                            $"parentLossy={(t.parent != null ? t.parent.lossyScale.ToString("F3") : "n/a")} " +
                            $"root='{(t.parent != null ? t.parent.name : "")}'");
                var bm = _smr != null ? _smr.sharedMesh : null;
                if (bm != null && _smr.bones != null)
                {
                    var bp = bm.bindposes;
                    int k = 0;
                    log.LogInfo($"[sahur:probe] bind[{k}] from {_smr.bones[k].name}: " +
                                $"l2w.pos={_smr.bones[k].localToWorldMatrix.GetColumn(3).ToString("F3")} " +
                                $"l2w.scale={_smr.bones[k].lossyScale.ToString("F3")}");
                }
            }
            log.LogInfo($"[sahur:probe] live renderers on layer {ModelLayer}: {onLayer} [{names}]");
        }
        catch (Exception e) { log.LogWarning($"[sahur:probe] failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- capture rig
    // A private camera on our own layer only, plus a directional light: the shipped scene is dark
    // (this install is on a map, not the lobby) and the mirror booth is a lobby object, so a lit
    // isolated render is the only honest way to look at the mesh itself. Culling mask = layer 6
    // only, so nothing of the world can hide a missing face. The pose is read from the game's own
    // player model root, so the frame is the real in-game transform (0.01 scale, yaw 180).
    private static void Capture(IHostContext log, string view)
    {
        float orthoSize = 1.02f;
        int colon = view.IndexOf(':');
        if (colon > 0)
        {
            if (float.TryParse(view.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var sz) && sz > 0.05f) orthoSize = sz;
            view = view.Substring(0, colon);
        }
        var model = _model;
        if (model == null || _smr == null) { log.LogWarning("[sahur:view] nothing built"); return; }
        var root = model.transform;
        GameObject camGo = null, lightGo = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        var previous = RenderTexture.active;
        try
        {
            Vector3 origin = root.position;
            Vector3 fwd = root.forward;
            Vector3 right = root.right;
            Vector3 dir;
            float dist = 3.4f;
            switch (view.ToLowerInvariant())
            {
                case "back": dir = -fwd; break;
                case "side": dir = right; dist = 3.0f; break;
                case "left": dir = -right; dist = 3.0f; break;
                default: dir = fwd; break;
            }
            var wb0 = _smr.bounds;
            Vector3 eye = origin + Vector3.up * 1.05f + dir.normalized * dist;
            Vector3 look = origin + Vector3.up * 0.95f;

            camGo = new GameObject("Sahur_CapCam");
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) throw new InvalidOperationException("camera creation failed");
            cam.enabled = false;
            cam.cullingMask = 1 << ModelLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f, 1f);
            // fixed ortho box: the same framing every view, so the silhouette can be measured
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation((look - eye).normalized, Vector3.up);
            rt = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            lightGo = new GameObject("Sahur_CapLight");
            var light = lightGo.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
            if (light != null)
            {
                light.type = LightType.Directional;
                light.intensity = 1.35f;
                light.color = new Color(1f, 0.97f, 0.9f);
                lightGo.transform.rotation = Quaternion.LookRotation(-(eye - look).normalized + new Vector3(0.35f, -0.55f, 0f), Vector3.up);
                light.cullingMask = 1 << ModelLayer;
            }

            // The scene here is a map, i.e. very dark: the shipped ambient is nearly black and a
            // temporary light is not picked up by URP's per-camera culling. An unlit copy of the
            // albedo material is the honest way to see the silhouette (a hole or an inside-out
            // face still shows), and the ambient bump keeps the shaded parts readable. Both are
            // restored immediately, and nothing else in the scene is touched.
            Material captureMat = null;
            var savedMode = RenderSettings.ambientMode;
            var savedAmbient = RenderSettings.ambientLight;
            var hiddenForCapture = new List<Renderer>();
            try
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null || !sh.isSupported) sh = Shader.Find("Universal Render Pipeline/Lit");
                captureMat = new Material(sh) { name = "Cosmetic_SahurCapture" };
                if (captureMat.HasProperty("_BaseMap")) captureMat.SetTexture("_BaseMap", _asset != null ? (Texture)_asset.Albedo : Texture2D.whiteTexture);
                if (captureMat.HasProperty("_BaseColor")) captureMat.SetColor("_BaseColor", Color.white);
                if (captureMat.HasProperty("_Color")) captureMat.SetColor("_Color", Color.white);
                _smr.sharedMaterial = captureMat;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.72f, 0.72f, 0.76f);

                // anything else that happens to live on our layer (a wardrobe hat is parented to the
                // head bone and would sit over the mask) is out of this frame only
                var all = SceneCache.Scan(Il2CppType.Of<Renderer>());
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var g = r.gameObject;
                    if (g == null || g.layer != ModelLayer || !r.enabled) continue;
                    if (IsOurs(r)) continue;
                    r.enabled = false;
                    hiddenForCapture.Add(r);
                }

                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
            }
            finally
            {
                for (int i = 0; i < hiddenForCapture.Count; i++)
                    try { if (hiddenForCapture[i] != null) hiddenForCapture[i].enabled = true; } catch { }
                try { RenderSettings.ambientMode = savedMode; } catch { }
                try { RenderSettings.ambientLight = savedAmbient; } catch { }
                try { if (_smr != null && _asset != null) _smr.sharedMaterial = _asset.Body; } catch { }
                try { if (captureMat != null) UnityEngine.Object.Destroy(captureMat); } catch { }
            }
            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, $"sahur_view_{view}.png");
            File.WriteAllBytes(path, bytes);
            var c0 = cam.WorldToScreenPoint(wb0.center);
            var c1 = cam.WorldToScreenPoint(wb0.center + Vector3.up * (wb0.size.y * 0.5f));
            log.LogInfo($"[sahur:view] framing ortho={cam.orthographic} size={cam.orthographicSize:F3} aspect={cam.aspect:F3} " +
                        $"smrCenterOnScreen=({c0.x:F0},{c0.y:F0}) topOnScreenY={c1.y:F0} boundsWorld={wb0.size.ToString("F2")}");
            log.LogInfo($"[sahur:view] view={view} {rt.width}x{rt.height} eye={eye.ToString("F2")} " +
                        $"root={origin.ToString("F2")} fwd={fwd.ToString("F2")} layerMask=0x{(1 << ModelLayer):X8} -> {path} ({bytes.Length} bytes)");
        }
        catch (Exception e) { log.LogError($"[sahur:view] capture failed: {e}"); }
        finally
        {
            RenderTexture.active = previous;
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
            try { if (lightGo != null) UnityEngine.Object.Destroy(lightGo); } catch { }
        }
    }

    private static Camera FindMirrorCamera(IHostContext log)
    {
        try
        {
            var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c == null) continue;
                string n = c.name ?? "";
                if (n.IndexOf("MirrorCam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.targetTexture == null) continue;
                _mirrorCam = c;
                return c;
            }
        }
        catch (Exception e) { log.LogWarning($"[sahur:mirror] camera scan failed: {e.Message}"); }
        return null;
    }

    // The in-game height: skin every vertex on the CPU with the live bone transforms (skin matrix =
    // bone.localToWorld * bindpose, exactly what the GPU does) and take the world-space y extent.
    private static void MeasureWorld(IHostContext log)
    {
        try
        {
            var mesh = _smr != null ? _smr.sharedMesh : null;
            if (mesh == null) return;
            var verts = mesh.vertices;
            var weights = mesh.boneWeights;
            var bind = mesh.bindposes;
            var bones = _smr.bones;
            if (verts == null || weights == null || bind == null || bones == null) return;
            if (weights.Length != verts.Length || bind.Length != bones.Length) return;
            float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
            var skin = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                skin[b] = bones[b] != null ? bones[b].localToWorldMatrix * bind[b] : Matrix4x4.identity;
            for (int v = 0; v < verts.Length; v++)
            {
                var w = weights[v];
                int bi = w.m_BoneIndex0;
                if (bi < 0 || bi >= skin.Length) continue;
                var p = skin[bi].MultiplyPoint3x4(verts[v]);
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
            }
            var head = _model != null && _model.gameObject != null ? _model.Bones?.Head : null;
            string headY = head != null ? $"{head.position.y:F3}" : "n/a";
            log.LogInfo($"[sahur:measure] skinned world bounds: y {minY:F3}..{maxY:F3} => height={(maxY - minY):F3} m " +
                        $"(x {minX:F2}..{maxX:F2}) feetY={minY:F3} gameHeadBoneY={headY} verts={verts.Length}");
        }
        catch (Exception e) { log.LogWarning($"[sahur:measure] failed: {e.Message}"); }
    }

    private static string BoneList()
    {
        try
        {
            var b = _smr != null ? _smr.bones : null;
            if (b == null) return "none";
            var names = new string[b.Length];
            for (int i = 0; i < b.Length; i++) names[i] = b[i] == null ? "null" : b[i].name;
            return string.Join(",", names);
        }
        catch (Exception e) { return "unreadable(" + e.Message + ")"; }
    }

    private static string RequestPath(IHostContext log)
    {
        try { return log.ScreensDir == null ? null : Path.Combine(log.ScreensDir, RequestFileName); }
        catch { return null; }
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var p = t.name ?? "";
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; p = (t.name ?? "") + "/" + p; }
        return p;
    }
}
