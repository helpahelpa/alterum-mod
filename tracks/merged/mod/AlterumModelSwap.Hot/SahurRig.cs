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

// The rigged Sahur wardrobe variant: "Tung Sahur Game 3D model (Roblox)" by alex.cgwarrior,
// CC BY 4.0 (https://sketchfab.com/3d-models/tung-sahur-game-3d-model-roblox-4cb52341b3bb4a59bd951e4e6b0ae0e6).
// Modified: retextured (4K PBR downscaled, roughness/AO folded into constants), mesh mirrored in X
// to match the game's handedness, skin weights reduced to 4 influences.
//
// Unlike SahurModel (the classic, rigid 6-piece bind onto the game's own bones), this one keeps the
// model's OWN 71-bone Mixamo skeleton as real Transform objects and drives it from the game's rig
// every frame. `sahur_roblox.rset` is baked offline by tools/bake_sahur_roblox.py: geometry +
// 4-influence skin + the bone hierarchy (local transforms = the bind pose) + the authored walk clip.
//
// Retarget, per mapped pair (game bone -> Mixamo bone):
//     delta   = (rootInv * gameBone.rotation) * inverse(gameBindRotation)
//     ourWorld = delta * ourBindWorld
//     local   = inverse(ourWorldParent) * ourWorld
// The game rotations are read in the PLAYER MODEL ROOT's space, so the whole computation is
// independent of where the player stands. `gameBindRotation` is captured on the first frame after
// the model binds, which makes "delta = identity" mean "the pose the game happened to be in", and
// emotes are then relative deviations from it. Unmapped model bones inherit their parent's delta,
// which is what makes the fingers, the toes, and the bat socket (`BIT` under `RightHand`) follow
// their parent for free.
//
// Handedness: this model's `LeftHand` sits at +X while the game's `hand_l` sits at -X for a
// +Z-facing body, so the bake mirrors X; the yaw below is still decided at runtime from the two
// left-hand positions (same idea as SahurModel.DecideFlip), never hardcoded.
//
// Unattended control: "<ScreensDir>/sahurrig.request", deleted on pickup, line-oriented:
//     on|off|toggle      this body (bare word works too)
//     variant=classic|roblox   hand the body over to the other Sahur module
//     walk=on|off|toggle play/stop the baked Mixamo walk clip (debug action, proves the clip survived)
//     human=on|off|toggle spawn/despawn a real game character beside the model for scale
//     view=front|back|side|left|compare[:orthoSize]   private 640x360 capture on the cosmetic layer
//     mats=1             dump the renderer/material/bone state
//     dump=1             full state, bone map, unmapped game bones
// Hotkey F1 toggles it (F2/F3 stay with the classic module, F4 is the reel).
internal static class SahurRig
{
    public const Key ToggleKey = Key.F1;

    // Every transform of our own skeleton is named with this prefix, so the bake's own bone name is
    // recoverable from the object and a profile name can never match a game bone by accident.
    internal const string BonePrefix = "SB_";

    private const string RequestFileName = "sahurrig.request";
    private const float PollSeconds = 0.1f;
    private const float ResolveSeconds = 1.0f;
    private const float AssertSeconds = 0.5f;

    private const int ModelLayer = 6;
    private static int _layer = ModelLayer;   // the rig's layer: 6 for the player, 2 for a wardrobe preview

    // The active bone table: { model bone, game bone } pairs from the spec (ModelRoster). Anatomy,
    // not names - the game side is the shipped rig's UE-style names, the model side is whatever the
    // bake emitted. Assigned per build, snapshotted per rig lease, so two rigged entries can hold
    // different tables at the same time.
    private static string[][] Map = ModelRoster.All[0].BoneMap;

    private static bool _announced;
    private static bool _on;
    private static float _nextPoll, _nextResolve, _nextAssert;
    // Build health, for the identity line and the bounded retry. A module that is merely ON is not a
    // body: a skipped (missing bones) or failed build leaves the native body visible, and reporting
    // the wanted spec as the body is the false pass the acceptance forbids. `Streak` backs the retry
    // off instead of rebuilding a failing body every resolve tick. The block is a value type so the
    // external/preview lease swap (Snapshot/RestoreSnapshot) copies and restores ALL of it.
    private static ModelWantedPolicy.BuildHealth _health = ModelWantedPolicy.BuildHealth.Fresh();
    private const float FailBackoffBase = 1.0f;
    private const float FailBackoffMax = 15.0f;

    // Read-only state for the hands lease (DefaultHands). Same fields the module writes, nothing new.
    internal static bool Active => _on;
    internal static ThirdViewPlayerModel LiveModel => _model;
    internal static ModelSpec LiveSpec => _spec;
    internal static Renderer LiveRenderer => _smr;
    // The episode's spec, for the wardrobe's "is this module already drawing the wanted entry"
    // question (the map row forcing must not run while it is).
    internal static ModelSpec Spec => _spec;

    // The EFFECTIVE body, not the wanted one: a body object with a mesh is what is on screen.
    internal static bool BodyLive
    {
        get
        {
            try { return _on && _bodyGo != null && _smr != null && _smr.sharedMesh != null; }
            catch { return false; }
        }
    }
    internal static string VisibleSpecSlug => BodyLive && _spec != null ? _spec.Slug : "";
    internal static string BodyStateName => !_on ? "native" : (BodyLive ? "live" : (_health.Failed ? "failed" : "pending"));
    internal static string BuildFailWhy => _health.Failed ? _health.Why : "";

    private const int CacheLimit = 2;
    private static readonly ModelAssetCache<Asset> _assets = new(CacheLimit, DestroyAsset);
    private static ModelAssetCache<Asset>.Lease _assetUse; // owned by this body; included in rig snapshots
    private static ModelSpec _spec;                // the model the live rig is built from
    private static Asset _asset;                   // == _assets[_spec.Slug] once built
    private static ThirdViewPlayerModel _model;
    private static Transform _root;                 // player model root
    private static GameObject _bodyGo;              // renderer host, "SahurRigged_Body"
    private static Mesh _bodyMesh;                  // private bindposes/deformation for THIS body
    private static Transform _rigRoot;              // "SahurRigged_Skeleton", carries the yaw
    private static SkinnedMeshRenderer _smr;
    private static Transform[] _bones;              // 71 of ours, index matches the blob

    // The imported bone arrays of the rigs that are live right now, keyed by the body renderer that
    // owns them (the local body, and one entry per remote/preview rig lease). This is the ONLY door
    // the enhancement profiles use to reach a skeleton: the value is the very array Build() filled
    // with `new GameObject("SB_" + name)` transforms, so it cannot contain - and nothing reachable
    // from it can be - a game bone. See ImportedBones().
    private sealed class ImportedEntry
    {
        internal Renderer R;
        internal Transform[] Bones;
    }
    private static readonly Dictionary<long, ImportedEntry> _importedBones = new();
    private static Transform[] _game;               // per pair
    private static Quaternion[] _gameBind;          // per pair, player-model-root space
    private static Vector3 _gameBindPos;            // pelvis, player-model-root space
    private static Quaternion[] _written;           // last local rotation written per bone
    private static Vector3 _writtenHips;
    private static Quaternion _yaw = Quaternion.identity;
    private static bool _captured, _batLogged;
    private static readonly HashSet<string> _dumpedMaps = new();
    private static bool _walk;
    private static float _walkT;
    private static int _hides, _restores, _builds, _frames;

    private static List<BodyRenderer> _body = new();
    private static GameObject _human;

    internal struct BodyRenderer
    {
        public Renderer R;
        public bool HadEnabled;
        public bool Hidden;
    }

    private sealed class Asset
    {
        public Mesh Mesh;
        public Texture2D Albedo, Normal;
        public Material Body;
        public int Verts, Tris, Submeshes, BoneCount;
        public string[] BoneName;
        public int[] Parent;
        public Vector3[] BindPos;
        public Quaternion[] BindRot;
        public Quaternion[] BindWorld;       // accumulated with the yaw
        public Matrix4x4[] Bindposes;        // with the yaw
        public int[] MapBone;                // per pair -> our bone index
        public Material[] Materials;         // one per material index in the bake (Head/Body...)
        public int[] SubmeshMat;             // per submesh: which Materials slot it draws with
        public int[][] SubmeshTri;           // per submesh triangle count + kind/bone for the log
        public int[] SubmeshKind;
        public Vector3[] Pos;
        public Vector2[] Uv;
        public Vector3[] Nrm;
        public BoneWeight[] Weights;
        public int BatBone, BatTris;
        public int Hips;
        public int ClipFrames;
        public float ClipDuration;
        public Quaternion[] ClipRot;         // frames * bones, yaw-conjugated
        public Vector3[] ClipPos;            // frames, source space
        public float MinY, MaxY;
        public int Bytes, TexBytes;
        public readonly List<Texture2D> Textures = new();   // every decoded map, incl. normal/material N
        public long DecodedBytes;                            // sum of those mip chains

        public int IndexOf(string boneName)
        {
            if (BoneName == null) return -1;
            for (int i = 0; i < BoneName.Length; i++)
                if (string.Equals(BoneName[i], boneName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    // ================================================================== entry points

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null) return;
        _log2 = log;
        if (!_announced)
        {
            _announced = true;
            try
            {
                log.LogInfo($"[sahurrig] ready trigger=none (request file only) " +
                            $"request={RequestPath(log)} values=on/off/toggle, walk=on|off, human=on|off, " +
                            "view=front|back|side|compare, mats=1, dump=1");
                log.LogInfo("[sahurrig] model: 'Tung Sahur Game 3D model (Roblox)' by alex.cgwarrior, " +
                            "CC BY 4.0 - modified (4K maps downscaled, roughness/AO folded into constants, " +
                            "mesh mirrored in X, weights reduced to 4 influences)");
            }
            catch { }
        }

        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        try { PollRequest(log); } catch (Exception e) { log.LogWarning($"[sahurrig] request poll failed: {e.Message}"); }

        if (!_on)
        {
            if (_body.Count > 0) Restore(log, "off");
            return;
        }

        try { Apply(log, now); }
        catch (Exception e) { log.LogError($"[sahurrig] apply threw: {e}"); }
    }

    internal static bool On => _on;
    internal static int BuildCount => _builds;

    // [mapbody] census: whether the module wants the body, what it is bound to, and the lease.
    internal static string MapBodyState()
    {
        string model = "none", owner = "none";
        try { if (_model != null && _model.gameObject != null) model = _model.gameObject.name; } catch { }
        try { if (_root != null) owner = _root.parent == null ? _root.name : _root.parent.name; } catch { }
        return $"rig[on={(_on ? 1 : 0)} spec='{(_spec == null ? "none" : _spec.Slug)}' bind='{model}' under='{owner}' " +
               $"bodyGo={(_bodyGo != null ? 1 : 0)} smr={(_smr != null ? 1 : 0)} hidden={_body.Count} " +
               $"builds={_builds} hides={_hides} restores={_restores} frames={_frames}]";
    }

    internal static void SetFromWardrobe(IHostContext log, bool on) => SetFromWardrobe(log, ModelRoster.All[0], on);

    internal static void SetFromWardrobe(IHostContext log, ModelSpec spec, bool on)
    {
        if (on && spec != null && !ReferenceEquals(spec, _spec))
        {
            // Hard switch: give the host body back and drop the old rig; the new one is built on the
            // next Apply. Parsed assets stay cached, one per spec.
            if (_body.Count > 0) { try { Restore(log, "spec-switch"); } catch { } }
            try { DestroyBody(log); } catch { }
            log?.LogInfo($"[sahurrig] spec '{( _spec == null ? "none" : _spec.Slug )}' -> '{spec.Slug}' " +
                         $"(rigged delta-retarget, label '{spec.Label}', {spec.BoneMap?.Length ?? 0} mapped pairs)");
            _spec = spec;
            Map = spec.BoneMap;
            _asset = null;
            _bodyGo = null;
            _smr = null;
            _captured = false;
            _health = ModelWantedPolicy.BuildHealth.Fresh();
        }
        if (_on == on) return;
        _on = on;
        log?.LogInfo($"[sahurrig] {(on ? "ON" : "OFF")} (from wardrobe selection, '{(_spec == null ? "?" : _spec.Label)}')");
        if (!on)
        {
            try { Restore(log, "wardrobe"); } catch { }
            try { DestroyBody(log); } catch { }
        }
    }

    // The retarget runs in LateUpdate, after every skeleton writer (game animation, emote pose,
    // proportion cage) and before the mirror renders, so the mirror shows this frame's pose.
    internal static void LateTick(IHostContext log)
    {
        if (!_on || _asset == null || _smr == null || _model == null || _root == null) return;
        try
        {
            if (_walk) { PoseFromClip(log); return; }
            PoseFromGame(log);
        }
        catch (Exception e) { log.LogWarning($"[sahurrig] pose failed: {e.Message}"); }
    }

    internal static void Dispose(IHostContext log)
    {
        try { DefaultHands.Dispose(log); } catch { }
        try { Restore(log, "dispose"); } catch { }
        try { DestroyBody(log); } catch { }
        try { DespawnHuman(log); } catch { }
        _assets.Dispose(); // outstanding preview leases release after runtime teardown
        _asset = null;
        _model = null;
        _root = null;
        _on = false;
        _captured = false;
        _frames = 0;
        _health = ModelWantedPolicy.BuildHealth.Fresh();
        _announced = false;
        if (log != null)
            try { log.LogInfo($"[sahurrig] disposed builds={_builds} hides={_hides} restores={_restores} frames={_frames}"); } catch { }
    }

    // ================================================================== second instance
    //
    // The retarget above is written once against module state. The wardrobe preview needs the
    // *same* path a second time, on a different ThirdViewPlayerModel (a spawned preview body),
    // while the player body keeps its own bind/pose. Rather than a second renderer path, the state
    // is snapshotted and swapped around each call: Attach builds a rig for an arbitrary model,
    // Pose writes that rig's bones, Detach restores the host renderers and destroys the objects.
    // The host rig's state is saved and restored around every swap, so the player body is never
    // touched by the preview (and vice versa). Parsed assets are shared under explicit leases;
    // each body owns its own mutable mesh. The asset-use token moves with the snapshot.
    internal sealed class RigLease
    {
        internal RigSnapshot S;
        internal string Label;
        internal int Builds, Poses, Disposes;
    }

    internal struct RigSnapshot
    {
        public ModelSpec Spec;
        public object Asset;      // SahurRig.Asset (private type; the struct itself is internal)
        public IDisposable AssetUse; // same ownership token moves with the body, never released by a snapshot swap
        public string[][] Map;
        public ThirdViewPlayerModel Model;
        public Transform Root;
        public GameObject BodyGo;
        public Mesh BodyMesh;
        public Transform RigRoot;
        public SkinnedMeshRenderer Smr;
        public Transform[] Bones, Game;
        public Quaternion[] GameBind;
        public Vector3 GameBindPos;
        public Quaternion[] Written;
        public Vector3 WrittenHips;
        public Quaternion Yaw;
        public bool Captured;
        public int Layer;
        public List<BodyRenderer> Body;
        public int Builds, Hides, Restores, Frames;
        // Failure state and the one-shot fault are part of the lease, not process-wide health. A
        // preview/remote success or failure must not clear or consume the local actor's episode.
        public ModelWantedPolicy.BuildHealth Health;
    }

    private static RigSnapshot Snapshot()
    {
        return new RigSnapshot
        {
            Spec = _spec, Asset = _asset, AssetUse = _assetUse, Map = Map,
            Model = _model, Root = _root, BodyGo = _bodyGo, BodyMesh = _bodyMesh, RigRoot = _rigRoot, Smr = _smr,
            Bones = _bones, Game = _game, GameBind = _gameBind, GameBindPos = _gameBindPos,
            Written = _written, WrittenHips = _writtenHips, Yaw = _yaw, Captured = _captured,
            Layer = _layer, Body = _body, Builds = _builds, Hides = _hides, Restores = _restores, Frames = _frames,
            Health = ModelWantedPolicy.BuildHealthLease.Copy(_health),
        };
    }

    private static void RestoreSnapshot(RigSnapshot s)
    {
        _spec = s.Spec; _asset = (Asset)s.Asset; _assetUse = (ModelAssetCache<Asset>.Lease)s.AssetUse;
        Map = s.Map ?? ModelRoster.All[0].BoneMap;
        _model = s.Model; _root = s.Root; _bodyGo = s.BodyGo; _bodyMesh = s.BodyMesh; _rigRoot = s.RigRoot; _smr = s.Smr;
        _bones = s.Bones; _game = s.Game; _gameBind = s.GameBind; _gameBindPos = s.GameBindPos;
        _written = s.Written; _writtenHips = s.WrittenHips; _yaw = s.Yaw; _captured = s.Captured;
        _layer = s.Layer <= 0 ? ModelLayer : s.Layer;
        _body = s.Body ?? new List<BodyRenderer>();
        _builds = s.Builds; _hides = s.Hides; _restores = s.Restores; _frames = s.Frames;
        ModelWantedPolicy.BuildHealthLease.Restore(ref _health, s.Health);
    }

    // Build the retarget on `model` (a preview body). Returns null when the model's bones do not
    // match, so the caller can report and retry. The host rig's state is untouched on return.
    internal static RigLease AttachExternal(IHostContext log, ModelSpec spec, ThirdViewPlayerModel model, Transform root, string label, int layer)
    {
        var saved = Snapshot();
        var lease = new RigLease { Label = label, S = new RigSnapshot() };
        try
        {
            RestoreSnapshot(new RigSnapshot());
            ModelWantedPolicy.BuildHealthLease.EnterExternal(ref _health);
            _spec = spec;
            Map = spec != null ? spec.BoneMap : ModelRoster.All[0].BoneMap;
            _model = model;
            _root = root;
            _layer = layer <= 0 ? ModelLayer : layer;
            Build(log);
            lease.S = Snapshot();
            lease.Builds = 1;
            if (lease.S.BodyGo == null) return null;
            log.LogInfo($"[sahurrig:preview] attached label='{label}' model='{PathOf(root)}' " +
                        $"bones={(lease.S.Bones == null ? 0 : lease.S.Bones.Length)} hiddenHost={lease.S.Body.Count} " +
                        $"builds={lease.Builds}");
            return lease;
        }
        catch (Exception e)
        {
            // A failed preview build owns its own partial objects: destroy them before the host
            // lease is restored, or they stay in the scene with no lease left to name them.
            try { if (_body.Count > 0) Restore(log, "attach-failed"); } catch { }
            try { DestroyBody(log); } catch { }
            log.LogWarning($"[sahurrig:preview] attach failed label='{label}': {e}");
            return null;
        }
        finally { RestoreSnapshot(saved); }
    }

    internal static void PoseExternal(IHostContext log, RigLease lease)
    {
        if (lease == null) return;
        var saved = Snapshot();
        try
        {
            RestoreSnapshot(lease.S);
            if (_bodyGo == null || _smr == null || _model == null || _root == null) return;
            PoseFromGame(log);
            lease.Poses++;
            lease.S = Snapshot();
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:preview] pose failed label='{lease.Label}': {e.Message}"); }
        finally { RestoreSnapshot(saved); }
    }

    internal static void DetachExternal(IHostContext log, RigLease lease, string why)
    {
        if (lease == null) return;
        var saved = Snapshot();
        try
        {
            RestoreSnapshot(lease.S);
            Restore(log, why);
            DestroyBody(log);
            lease.Disposes++;
            lease.S = new RigSnapshot();
            log.LogInfo($"[sahurrig:preview] detached label='{lease.Label}' why={why} " +
                        $"builds={lease.Builds} poses={lease.Poses} disposes={lease.Disposes}");
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:preview] detach failed label='{lease.Label}': {e.Message}"); }
        finally { RestoreSnapshot(saved); }
    }

    // ================================================================== input (request file only)

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
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[sahurrig] request read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            string key = eq > 0 ? line.Substring(0, eq).Trim().ToLowerInvariant() : "";
            string val = eq > 0 ? line.Substring(eq + 1).Trim().ToLowerInvariant() : line.ToLowerInvariant();
            switch (key)
            {
                case "walk":
                    switch (val)
                    {
                        case "on": case "1": SetWalk(log, true); break;
                        case "off": case "0": SetWalk(log, false); break;
                        case "toggle": SetWalk(log, !_walk); break;
                        default: log.LogWarning($"[sahurrig] walk={val} is not on/off/toggle"); break;
                    }
                    break;
                case "human":
                    if (val == "off" || val == "0") DespawnHuman(log);
                    else SpawnHuman(log);
                    break;
                case "view":
                    Capture(log, val.Length == 0 ? "front" : val);
                    break;
                case "mats":
                    Compare(log);
                    break;
                case "hudshot":
                    HudShot(log, val.Length == 0 ? "sahurrig_hud" : val);
                    break;
                case "dump":
                    Dump(log);
                    break;
                case "variant":
                    // hand the body to the other Sahur module (the wardrobe does this through the
                    // model selection; this is the unattended equivalent)
                    try { SahurWardrobe.Command(log, "select=" + val); } catch (Exception e) { log.LogWarning($"[sahurrig] variant failed: {e.Message}"); }
                    break;
                case "selftest":
                    // TEST SEAM, inert without the request file. `failbuild` arms exactly one injected
                    // exception inside Build, so the shipping teardown/native-restore/FAILED/backoff
                    // path is driven in a live process instead of a fabricated log line.
                    switch (val)
                    {
                        case "failbuild":
                        case "failbuild-skeleton":
                        case "failbuild-body":
                        case "failbuild-renderer":
                            _health.TestFault = val == "failbuild" ? "pre" : val.Substring("failbuild-".Length);
                            _health.NextAttempt = 0f;
                            try { DestroyBody(log); } catch { }
                            log.LogInfo($"[sahurrig:selftest] armed {val} (phase={_health.TestFault}): the next Build throws once");
                            break;
                        case "state":
                            log.LogInfo($"[sahurrig:selftest] on={(_on ? 1 : 0)} spec='{(_spec == null ? "none" : _spec.Slug)}' " +
                                        $"bodyLive={(BodyLive ? 1 : 0)} state={BodyStateName} failStreak={_health.Streak} " +
                                        $"nextAttemptIn={Math.Max(0f, _health.NextAttempt - Now()):F1}s failWhy='{_health.Why}'");
                            break;
                        default:
                            log.LogWarning($"[sahurrig] selftest='{val}' is not failbuild[-skeleton|-body|-renderer]/state");
                            break;
                    }
                    break;
                case "":
                case "body":
                case "sahurrig":
                    switch (val)
                    {
                        case "on": case "1": case "yes": SetOn(log, true, "request"); break;
                        case "off": case "0": case "no": SetOn(log, false, "request"); break;
                        case "toggle": SetOn(log, !_on, "request"); break;
                        case "dump": Dump(log); break;
                        default: log.LogWarning($"[sahurrig] request '{val}' is not on/off/toggle/dump"); break;
                    }
                    break;
                default:
                    log.LogWarning($"[sahurrig] request key '{key}' is not body/walk/human/view/mats/dump/variant/selftest");
                    break;
            }
        }
    }

    private static void SetOn(IHostContext log, bool on, string why)
    {
        if (_on == on) { if (why == "request") log.LogInfo($"[sahurrig] already {(on ? "on" : "off")}"); return; }
        _on = on;
        log.LogInfo($"[sahurrig] {(on ? "ON" : "OFF")} (from {why})");
        if (on)
        {
            // Same rule as the classic module: a body switched on by request is a deliberate choice,
            // so the wardrobe adopts it as the wanted entry instead of forcing another row.
            try { SahurWardrobe.NoteBodyRequest(log, _spec ?? ModelRoster.All[0], true); }
            catch (Exception e) { log.LogWarning($"[sahurrig] wardrobe note failed: {e.Message}"); }
        }
        if (!on) { Restore(log, why); DestroyBody(log); _walk = false; }
    }

    private static void SetWalk(IHostContext log, bool on)
    {
        _walk = on;
        _walkT = 0f;
        if (_asset != null)
            log.LogInfo($"[sahurrig:walk] {(on ? "playing" : "stopped")} baked Mixamo clip " +
                        $"{_asset.ClipFrames} frames / {_asset.ClipDuration:F3}s " +
                        $"(overrides the retarget while on)");
    }

    // ================================================================== apply / bind

    private static void Apply(IHostContext log, float now)
    {
        if (!BindReady(log, now)) return;
        if (_bodyGo == null || _smr == null)
        {
            // One attempt per backoff window, never one per frame: a failing asset read or build is
            // re-parsed and re-logged only when the gate opens (`[sahurrig:build] FAILED` carries
            // the retry delay it will observe).
            if (!ModelWantedPolicy.BuildAllowed(now, _health.NextAttempt)) return;
            try { Build(log); }
            catch (Exception e) { FailBuild(log, _spec, e, "exception"); }
        }
        if (_bodyGo == null) return;
        try { _bodyGo.SetActive(true); } catch { }
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

        if (_body.Count > 0) Restore(log, "model-gone");
        DestroyBody(log); // releases even when Unity has already destroyed the body wrapper
        _captured = false;
        if (now < _nextResolve) return false;
        _nextResolve = now + ResolveSeconds;

        var (model, owner) = FindLocalBody(_spec);
        if (model == null || model.gameObject == null)
        {
            // This used to be silent, so a map body the resolution could not find looked exactly
            // like a body that was never wanted. Rate-limited to the resolve interval.
            log.LogWarning($"[sahurrig:nobody] rigged body wanted but no local ThirdViewPlayerModel resolved " +
                           $"(owner='{(owner == null ? "null" : owner.name)}' scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' " +
                           $"on={_on}) - retrying in {ResolveSeconds:F1}s");
            return false;
        }
        _model = model;
        _root = model.transform;
        log.LogInfo($"[sahurrig:bind] model='{model.gameObject.name}' owner='{(owner == null ? "null" : owner.name)}'");
        return true;
    }

    // Same local-body resolution as SahurModel: the current player's own third-view model, so the
    // retarget reads exactly the skeleton the emotes and the jiggle solver write.
    private static (ThirdViewPlayerModel, Transform) FindLocalBody(ModelSpec spec)
    {
        // One owner resolution for everybody: CurrentPlayerProvider first, then the Netcode
        // IsOwner fallback (ReelLogic.FindLocalPlayerEntity). The bare provider pass missed the map
        // body on the first frames and on a joining client, and the old fallback accepted ANY
        // active ThirdViewPlayerModel when no owner resolved - on a client that hid another
        // player's body and left the local one native. Never bind a body that is not provably
        // under the local player.
        Transform owner = null;
        try { var player = ReelLogic.FindLocalPlayerEntity(); if (player != null && player.transform != null) owner = player.transform; } catch { }
        if (owner == null) return (null, null);

        // More than one body can live under the player while the game swaps prefabs (SetupModel on
        // a client leaves the old one active for a moment). Pick the one that can actually carry
        // this model: the mapped game bones must all exist, otherwise the build would skip and the
        // native body would show. Order is therefore not trusted.
        var map = spec != null && spec.BoneMap != null ? spec.BoneMap : Map;
        ThirdViewPlayerModel best = null;
        string why = null;
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
                if (map == null || map.Length == 0) return (m, owner);
                int missing = 0;
                var names = BoneMap(m);
                for (int k = 0; k < map.Length; k++)
                    if (ResolveGameBone(names, map[k][1]) == null) missing++;
                if (missing == 0) return (m, owner);
                if (best == null) { best = m; why = $"{m.gameObject.name}: {missing}/{map.Length} bones missing"; }
            }
        }
        catch { }
        if (why != null) _lastNoBones = why;
        // No candidate can carry the model: dump the scene's player models so "the body the game
        // built is not under the player" and "the body was never rebuilt" are distinguishable.
        float now = 0f; try { now = Time.unscaledTime; } catch { }
        if (now >= _nextNoBodyDump)
        {
            _nextNoBodyDump = now + 5f;
            try
            {
                var sb = new StringBuilder();
                var all = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
                int shown = 0;
                for (int i = 0; all != null && i < all.Length && shown < 8; i++)
                {
                    var m = all[i].TryCast<ThirdViewPlayerModel>();
                    if (m == null || m.gameObject == null) continue;
                    string sc = ""; try { sc = m.gameObject.scene.name ?? ""; } catch { }
                    if (string.IsNullOrEmpty(sc)) continue;
                    shown++;
                    int b = -1; try { b = m.Bones?._allBones?.Count ?? -1; } catch { }
                    sb.Append($"\n  '{m.gameObject.name}' active={(m.gameObject.activeInHierarchy ? 1 : 0)} " +
                              $"bones={b} underOwner={(MirrorPlayerVisibility.Under(m.transform, owner) ? 1 : 0)} " +
                              $"path='{PathOf(m.transform)}'");
                }
                _log2?.LogInfo($"[sahurrig:census] no body can carry '{(_spec == null ? "?" : _spec.Slug)}' " +
                               $"(why={why ?? "none"}) owner='{owner.name}', {shown} player model(s) in scene:{sb}");
            }
            catch { }
        }
        return (best ?? (null), owner);
    }

    private static string _lastNoBones;
    private static IHostContext _log2;
    private static float _nextNoBodyDump;
    internal static string NoBonesWhy => _lastNoBones;

    // ================================================================== build

    // The rig's own head bone, for the cosmetic socket. The game's `head` bone sits where the
    // NATIVE skeleton's head is; our model has its own proportions, so on a body whose skeleton is
    // not the donor's (a map client keeps its native one) a hat anchored on the game bone floats
    // above the visible head. When our rigged body is up, the hat belongs on SB_head instead.
    internal static bool TryGetWornHead(Transform modelRoot, out Transform head)
    {
        head = null;
        try
        {
            if (modelRoot == null || _root == null || _bones == null || _asset == null || _smr == null) return false;
            if (!MirrorPlayerVisibility.Under(_root, modelRoot)) return false;
            int i = _asset.IndexOf("head");
            if (i < 0 || i >= _bones.Length || _bones[i] == null) return false;
            head = _bones[i];
            return true;
        }
        catch { return false; }
    }

    // A native body does not always carry every bone the retarget maps: Male_2_Thomas has no
    // spine_04/spine_05, so binding to it used to skip the build and leave the native body on
    // screen (a map client whose stale row is that model showed nothing). Alias a missing chain
    // segment to the next lower one with the same stem: spine_05 -> spine_04 -> spine_03.
    private static Transform ResolveGameBone(Dictionary<string, Transform> bones, string name)
    {
        if (bones == null || string.IsNullOrEmpty(name)) return null;
        if (bones.TryGetValue(name, out var exact) && exact != null) return exact;
        int cut = name.Length;
        while (cut > 0 && char.IsDigit(name[cut - 1])) cut--;
        if (cut == name.Length || cut == 0) return null;
        string stem = name.Substring(0, cut);
        int digits = name.Length - cut;
        if (!int.TryParse(name.Substring(cut, digits), out int n)) return null;
        for (int k = n - 1; k >= 1; k--)
        {
            string alt = stem + k.ToString(new string('0', digits));
            if (bones.TryGetValue(alt, out var t) && t != null)
            {
                string msg = $"[sahurrig:alias] game bone '{name}' is missing; using '{alt}'";
                if (_aliases.Add(name)) _log2?.LogInfo(msg);
                return t;
            }
        }
        return null;
    }

    private static readonly HashSet<string> _aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    private static void Build(IHostContext log)
    {
        var model = _model;
        var root = _root;
        if (model == null || root == null) return;

        var spec = _spec ?? (_spec = ModelRoster.All[0]);
        Map = spec.BoneMap;
        if (_health.TakeFault("pre"))
        {
            // Selftest seam (request file `selftest=failbuild`): the ONE deterministic way to drive
            // the real failure path in a live process. It injects at a point where a real failure
            // occurs (a throw inside Build), so the teardown, native restore, FAILED line and
            // bounded retry below are the shipping ones - no second implementation. The phase
            // variants fail after the skeleton, body or renderer allocation so cleanup of a
            // partially built rig is exercised too, not only the pre-allocation throw.
            throw new InvalidOperationException("selftest: injected build failure at pre (request selftest=failbuild)");
        }
        // The temporary borrow pins resources throughout construction. Only a successful body
        // retains a second token; failures and early returns release automatically.
        _assetUse?.Dispose(); _assetUse = null;
        using var assetUse = AcquireAsset(log, spec);
        var asset = _asset = assetUse?.Value;
        if (asset == null)
        {
            // Was a silent `return`: the module stayed "on" with no body, retried every frame and
            // the identity line could not say why. The cause is carried into the FAILED line.
            throw new InvalidOperationException("asset=null for '" + spec.Slug + "'" +
                (string.IsNullOrEmpty(_health.AssetError) ? " (no loader error recorded)" : " (" + _health.AssetError + ")"));
        }
        if (asset.Hips < 0)
            for (int k = 0; k < Map.Length; k++)
                if (string.Equals(Map[k][1], "pelvis", StringComparison.OrdinalIgnoreCase)) { asset.Hips = asset.IndexOf(Map[k][0]); break; }

        var bones = BoneMap(model);
        int missing = 0;
        var miss = new StringBuilder();
        _game = new Transform[Map.Length];
        _gameBind = new Quaternion[Map.Length];
        for (int k = 0; k < Map.Length; k++)
        {
            var t = ResolveGameBone(bones, Map[k][1]);
            if (t != null) _game[k] = t;
            else { missing++; if (miss.Length < 120) miss.Append(miss.Length == 0 ? "" : ",").Append(Map[k][1]); }
            _asset.MapBone[k] = asset.IndexOf(Map[k][0]);
        }
        if (missing > 0)
        {
            // Clear the binding, not just the bone table: a body the game is about to replace (the
            // wardrobe index just changed, or the map swapped the prefab) would otherwise stay
            // bound, and every frame would retry THIS skeleton and skip forever.
            log.LogWarning($"[sahurrig:skip] '{model.gameObject.name}' is missing {missing} game bone(s) '{miss}'; " +
                           "re-resolving (the bound body may be the one being replaced)");
            _game = null;
            _model = null;
            return;
        }

        // Everything allocated from here on is owned by THIS attempt and tracked in locals as well
        // as in the static fields: the cleanup below must not depend on _bodyGo having been
        // published. Declared before the try so the catch can destroy them.
        GameObject rig = null;
        GameObject body = null;
        SkinnedMeshRenderer smr = null;
        Mesh bodyMesh = null;
        try
        {
        DecideYaw(log, asset, bones, root);
        BuildMesh(log, asset);

        // skeleton: our own Transform hierarchy, one GameObject per baked bone
        SahurModel.SweepOrphans(log, root, null);
        HideBodyBeforeBuild(log);
        rig = new GameObject(spec.Tag + "_Skeleton");
        rig.transform.SetParent(root, false);
        rig.transform.localPosition = Vector3.zero;
        rig.transform.localRotation = _yaw;
        rig.transform.localScale = Vector3.one;
        _rigRoot = rig.transform;

        _bones = new Transform[asset.BoneCount];
        for (int i = 0; i < asset.BoneCount; i++)
        {
            var go = new GameObject(BonePrefix + asset.BoneName[i]);
            var tr = go.transform;
            int p = asset.Parent[i];
            tr.SetParent(p >= 0 && p < i ? _bones[p] : rig.transform, false);
            tr.localPosition = asset.BindPos[i];
            tr.localRotation = asset.BindRot[i];
            tr.localScale = Vector3.one;
            go.layer = _layer;
            _bones[i] = tr;
        }
        if (_health.TakeFault("skeleton"))
            throw new InvalidOperationException("selftest: injected build failure after skeleton allocation (request selftest=failbuild-skeleton)");

        body = new GameObject(spec.BodyName);
        body.transform.SetParent(root, false);
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.layer = _layer;
        if (_health.TakeFault("body"))
            throw new InvalidOperationException("selftest: injected build failure after body allocation (request selftest=failbuild-body)");

        smr = body.AddComponent(Il2CppType.Of<SkinnedMeshRenderer>()).TryCast<SkinnedMeshRenderer>();
        if (smr == null)
        {
            // No explicit Destroy here: the catch below destroys BOTH locals, including the
            // skeleton this attempt already created (the old branch left it in the scene).
            log.LogError("[sahurrig] SkinnedMeshRenderer creation failed");
            throw new InvalidOperationException("SkinnedMeshRenderer creation failed for '" + spec.Slug + "'");
        }

        // Cached geometry stays immutable. Local, remote and preview bodies each own a clone;
        // neither bindposes nor enhancement deformation may leak from one wearer to another.
        bodyMesh = UnityEngine.Object.Instantiate((UnityEngine.Object)asset.Mesh)?.TryCast<Mesh>();
        if (bodyMesh == null) throw new InvalidOperationException("body mesh clone failed for '" + spec.Slug + "'");
        bodyMesh.name = spec.MeshName;
        bodyMesh.bindposes = new Il2CppStructArray<Matrix4x4>(asset.Bindposes);
        smr.sharedMesh = bodyMesh;
        smr.bones = new Il2CppReferenceArray<Transform>(_bones);
        smr.rootBone = _bones[asset.Hips >= 0 ? asset.Hips : 0];
        // one material per submesh, always: 8 submeshes with 1 material draws only the first one.
        // Which material is the bake's own slot decision (Capuchino: Head=0, Body=1).
        var mats = new Material[Math.Max(1, asset.Submeshes)];
        for (int m = 0; m < mats.Length; m++)
        {
            int slot = asset.SubmeshMat != null && m < asset.SubmeshMat.Length ? asset.SubmeshMat[m] : 0;
            mats[m] = asset.Materials != null && asset.Materials.Length > 0
                ? asset.Materials[Math.Min(Math.Max(slot, 0), asset.Materials.Length - 1)]
                : asset.Body;
        }
        smr.sharedMaterials = new Il2CppReferenceArray<Material>(mats);
        smr.quality = SkinQuality.Bone4;
        smr.updateWhenOffscreen = true;
        smr.allowOcclusionWhenDynamic = false;
        smr.skinnedMotionVectors = false;
        smr.shadowCastingMode = ShadowCastingMode.On;
        smr.receiveShadows = true;
        float h = asset.MaxY - asset.MinY;
        try
        {
            smr.localBounds = new Bounds(new Vector3(0f, h * 0.5f, 0f), new Vector3(0.9f, h, 1.2f));
        }
        catch { }
        if (_health.TakeFault("renderer"))
            throw new InvalidOperationException("selftest: injected build failure after renderer setup (request selftest=failbuild-renderer)");

        _bodyGo = body;
        _bodyMesh = bodyMesh;
        _smr = smr;
        RegisterImportedBones(smr, _bones);
        _builds++;
        _captured = false;
        _written = new Quaternion[asset.BoneCount];
        for (int i = 0; i < _written.Length; i++) _written[i] = asset.BindRot[i];
        _writtenHips = asset.Hips >= 0 ? asset.BindPos[asset.Hips] : Vector3.zero;
        CosmeticMarkers.AddRoot(body.transform);
        CosmeticMarkers.Add(smr);
        CosmeticMarkers.AddRoot(rig.transform);

        log.LogInfo($"[sahurrig:build] '{spec.Label}' root={PathOf(root)} layer={_layer} ('{LayerMask.LayerToName(_layer)}') " +
                    $"verts={asset.Verts} tris={asset.Tris} submeshes={asset.Submeshes} materials={mats.Length} " +
                    $"bones={asset.BoneCount} yaw={(Mathf.Abs(_yaw.eulerAngles.y) < 1f ? 0 : 180)} " +
                    $"mats={mats.Length} " +
                    $"height={h:F3}m (source baked 1.807m) bounds y[{asset.MinY:F3},{asset.MaxY:F3}] " +
                    $"blob={asset.Bytes} tex={asset.TexBytes} builds={_builds}");
        LogMap(log, bones);
        if (_batLogged == false)
        {
            _batLogged = true;
            var b = asset.BatBone;
            log.LogInfo($"[sahurrig:bat] island tris={asset.BatTris} bone='{(b >= 0 ? asset.BoneName[b] : "?")}' " +
                        $"parent='{(b >= 0 && asset.Parent[b] >= 0 ? asset.BoneName[asset.Parent[b]] : "?")}' " +
                        $"(unmapped skeleton bone: it inherits RightHand's delta, so the hammer follows the hand)");
        }
        HideBody(log);
        _builds++;

        // Success resets the failure episode: the next failure starts its backoff from the base and
        // the identity line reports a body again.
        _health.RecordSuccess();

        // Intended vs BUILT, read back off the live renderer: the mesh the game is drawing, its
        // vertex/submesh counts, the bone count actually bound and whether the renderer is enabled.
        // This is what makes a "built" claim falsifiable from a log instead of from a portrait.
        LogBuilt(log, spec, asset, smr);
        _assetUse = assetUse.Retain();
        }
        catch
        {
            CleanupPartialBuild(log, rig, body, smr);
            try { if (bodyMesh != null) UnityEngine.Object.Destroy(bodyMesh); } catch { }
            _bodyMesh = null;
            throw;
        }
    }

    // Destroy every object THIS build attempt allocated, from the local references, whether or not
    // it reached _bodyGo/_rigRoot publication. Only objects created by this attempt are touched:
    // an external/preview build runs in its own snapshot, so it can never destroy a resource another
    // actor's lease still names.
    private static void CleanupPartialBuild(IHostContext log, GameObject rig, GameObject body, SkinnedMeshRenderer smr)
    {
        int bodyN = 0, rigN = 0, smrN = 0;
        try { if (smr != null) { CosmeticMarkers.Remove(smr.Pointer); smrN = 1; } } catch { }
        try { if (body != null) CosmeticMarkers.RemoveRoot(body.transform.Pointer); } catch { }
        try { if (rig != null) CosmeticMarkers.RemoveRoot(rig.transform.Pointer); } catch { }
        try { if (body != null) { UnityEngine.Object.Destroy(body); bodyN = 1; } } catch (Exception e) { log?.LogWarning($"[sahurrig:cleanup] body destroy failed: {e.Message}"); }
        try { if (rig != null) { UnityEngine.Object.Destroy(rig); rigN = 1; } } catch (Exception e) { log?.LogWarning($"[sahurrig:cleanup] skeleton destroy failed: {e.Message}"); }

        // Unpublish only what this attempt published; state belonging to a restored lease is never
        // in these fields while this build runs (AttachExternal swaps it out first).
        if (ReferenceEquals(_bodyGo, body)) _bodyGo = null;
        if (ReferenceEquals(_smr, smr)) _smr = null;
        if (rig != null && _rigRoot != null && ReferenceEquals(_rigRoot, rig.transform)) _rigRoot = null;
        UnregisterImportedBones(_bones);
        _bones = null;
        _captured = false;
        log?.LogInfo($"[sahurrig:cleanup] partial build: destroyed body={bodyN} skeleton={rigN} renderer={smrN} " +
                     $"(allocation tracked in locals, not in the publication fields)");
    }

    // The readback pair: intended spec from the roster, built identity from the renderer itself.
    private static void LogBuilt(IHostContext log, ModelSpec spec, Asset asset, SkinnedMeshRenderer smr)
    {
        if (log == null || spec == null) return;
        string mesh = "-";
        int verts = -1, subs = -1, bones = -1, enabled = 0;
        try { mesh = asset != null && asset.Mesh != null ? (asset.Mesh.name ?? "-") : "-"; } catch { }
        try { verts = asset != null ? asset.Verts : -1; } catch { }
        try { subs = smr != null && smr.sharedMesh != null ? smr.sharedMesh.subMeshCount : -1; } catch { }
        try { bones = smr != null && smr.bones != null ? smr.bones.Length : -1; } catch { }
        try { enabled = smr != null && smr.enabled ? 1 : 0; } catch { }
        log.LogInfo($"[sahurrig:render] intended='{spec.Slug}' label='{spec.Label}' built='{mesh}' " +
                    $"verts={verts} submeshes={subs} bones={bones} rendererEnabled={enabled} " +
                    $"hiddenNative={_body.Count} builds={_builds} state={BodyStateName} mesh=private-clone");
    }

    // The ONE failure exit: partial objects destroyed, the game's own body given back, first cause
    // recorded, and the next attempt pushed out by the bounded backoff. Called from Apply's catch
    // (assets, parse, renderer creation, interop) and from the selftest seam.
    private static void FailBuild(IHostContext log, ModelSpec spec, Exception e, string kind)
    {
        string label = spec != null ? spec.Label : "?";
        string cause = e == null ? "unknown" : (e.GetType().Name + ": " + e.Message);
        Exception inner = e;
        while (inner != null && inner.InnerException != null) inner = inner.InnerException;
        if (inner != null && !ReferenceEquals(inner, e)) cause += " <- " + inner.GetType().Name + ": " + inner.Message;

        _health.Failed = true;
        _health.Why = cause;
        _health.Streak++;
        float now = Now();
        _health.NextAttempt = ModelWantedPolicy.NextBuildAttempt(now, _health.Streak, FailBackoffBase, FailBackoffMax);
        float delay = ModelWantedPolicy.RetryDelay(_health.Streak, FailBackoffBase, FailBackoffMax);

        // Restore first, destroy second: the snapshot HideBodyBeforeBuild took is the game's own
        // body, and a failed build must never leave it hidden with no replacement.
        try { if (_body.Count > 0) Restore(log, "build-failed"); } catch { }
        // Unconditional: DestroyBody is null-safe and destroys the skeleton independently of the
        // body field, so a throw between the skeleton allocation and _bodyGo publication cannot
        // leave it (or a local body object) in the scene. Build's own catch already destroyed the
        // objects of THIS attempt; this is the belt for anything that was published before it threw.
        bool cleaned = false;
        try { cleaned = DestroyBody(log); } catch (Exception de) { cause += " <- cleanup: " + de.Message; }

        log?.LogError($"[sahurrig:build] FAILED '{label}' slug='{(spec == null ? "?" : spec.Slug)}' " +
                     $"kind={kind} cause={cause} - partial rig {(cleaned ? "destroyed" : "cleanup FAILED")}, " +
                     $"native body restored (hiddenNative={_body.Count}), retry in {delay:F0}s " +
                     $"(streak={_health.Streak}, max={FailBackoffMax:F0}s), bodyState=failed");
    }

    private static float Now()
    {
        try { return Time.unscaledTime; } catch { return 0f; }
    }

    // Yaw: the model's own left hand sits at +X before the yaw (baked and mirrored: -X), the game's
    // hand_l sits where it sits in the player root. Same sign -> no yaw, opposite -> 180 degrees.
    private static void DecideYaw(IHostContext log, Asset asset, Dictionary<string, Transform> bones, Transform root)
    {
        // bone LOCAL x is meaningless (Mixamo bone axes run along the bone), so accumulate the bind
        // chain to get the left hand's position in the model's own space.
        float ours = 0f;
        int lh = -1;
        var pairs = (_spec != null ? _spec.BoneMap : null) ?? Map;
        for (int k = 0; pairs != null && k < pairs.Length; k++)
            if (string.Equals(pairs[k][1], "hand_l", StringComparison.OrdinalIgnoreCase))
            { lh = asset.IndexOf(pairs[k][0]); break; }
        if (lh < 0) lh = asset.IndexOf("LeftHand");   // rigs without a BoneMap entry
        if (lh >= 0)
        {
            // accumulate the bind hierarchy properly: local TRS per bone, parents first, so the
            // result is the bone's bind position in the model's own space (the old leaf->root
            // accumulation multiplied each position by the wrong rotation and read too far out).
            int n = asset.BoneCount;
            var world = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                int pa = asset.Parent[i];
                var local = Matrix4x4.TRS(asset.BindPos[i], asset.BindRot[i], Vector3.one);
                world[i] = pa >= 0 && pa < i ? world[pa] * local : local;
            }
            ours = world[lh].GetColumn(3).x;
        }
        float theirs = 0f;
        string src = "bind";
        if (bones.TryGetValue("hand_l", out var handL) && handL != null)
        {
            theirs = root.InverseTransformPoint(handL.position).x;
            src = "hand_l";
        }
        _yaw = (ours * theirs < 0f) ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        log.LogInfo($"[sahurrig:orient] our hand_l bone '{(lh >= 0 ? asset.BoneName[lh] : "none")}' bind " +
                    $"x={ours:F3} m, game {src} x={theirs:F3} m in '{PathOf(root)}' => " +
                    $"yaw={(_yaw == Quaternion.identity ? 0 : 180)} deg " +
                    "(baked mesh is mirrored to the game's handedness, so this should be 0)");
        PrepareBind(asset);
    }

    // bindposes / bind world rotations with the yaw folded in; the clip deltas conjugated by it.
    private static void PrepareBind(Asset asset)
    {
        if (asset.Bindposes != null) return;          // already prepared (rebuild after a scene change)
        int n = asset.BoneCount;
        asset.BindWorld = new Quaternion[n];
        asset.Bindposes = new Matrix4x4[n];
        var chain = new Matrix4x4[n];
        var rootM = Matrix4x4.TRS(Vector3.zero, _yaw, Vector3.one);
        for (int i = 0; i < n; i++)
        {
            var p = asset.Parent[i];
            var local = Matrix4x4.TRS(asset.BindPos[i], asset.BindRot[i], Vector3.one);
            chain[i] = p >= 0 ? chain[p] * local : rootM * local;
            asset.BindWorld[i] = p >= 0 ? asset.BindWorld[p] * asset.BindRot[i] : _yaw * asset.BindRot[i];
            asset.Bindposes[i] = chain[i].inverse;
        }
        // the clip was baked in the source frame; a non-zero yaw has to be conjugated through it
        if (_yaw == Quaternion.identity || asset.ClipRot == null) return;
        var inv = Quaternion.Inverse(_yaw);
        for (int f = 0; f < asset.ClipFrames; f++)
            for (int i = 0; i < n; i++)
            {
                int o = f * n + i;
                asset.ClipRot[o] = _yaw * asset.ClipRot[o] * inv;
                asset.ClipPos[f] = inv * asset.ClipPos[f];
            }
    }

    private static void BuildMesh(IHostContext log, Asset asset)
    {
        if (asset.Mesh != null) return;
        try
        {
            var mesh = new Mesh { name = "Sahur_RiggedBody" };
            mesh.vertices = new Il2CppStructArray<Vector3>(asset.Pos);
            // un-mirror handled at bake; the mesh is already in the game's space
            mesh.uv = new Il2CppStructArray<Vector2>(asset.Uv);
            mesh.normals = new Il2CppStructArray<Vector3>(asset.Nrm);
            mesh.boneWeights = new Il2CppStructArray<BoneWeight>(asset.Weights);
            mesh.subMeshCount = asset.Submeshes;
            for (int s = 0; s < asset.Submeshes; s++)
            {
                var tri = new int[asset.SubmeshTri[s].Length];
                Array.Copy(asset.SubmeshTri[s], tri, tri.Length);
                mesh.SetTriangles(new Il2CppStructArray<int>(tri), s, false);
            }
            mesh.RecalculateBounds();
            try { mesh.RecalculateTangents(); } catch (Exception e) { log.LogWarning($"[sahurrig] RecalculateTangents failed: {e.Message}"); }
            asset.Mesh = mesh;
            var bw = mesh.boneWeights;
            log.LogInfo($"[sahurrig:mesh] submeshes={mesh.subMeshCount} materials={asset.Submeshes} " +
                        $"weights={bw.Length} [0]=b{bw[0].m_BoneIndex0}/{bw[0].m_Weight0:F2} " +
                        $"tangents={mesh.tangents.Length} bounds={mesh.bounds.size.ToString("F3")}");
        }
        catch (Exception e) { log.LogError($"[sahurrig:mesh] build failed: {e}"); }
    }

    private static void LogMap(IHostContext log, Dictionary<string, Transform> bones)
    {
        string key = (_spec == null ? "?" : _spec.Slug) + "/" + Map.Length;
        if (!_dumpedMaps.Add(key)) return;   // once per spec: the preview rig shares the same map
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[sahurrig:map] game bone -> model bone (anatomy; the model's names are Mixamo's):");
            for (int k = 0; k < Map.Length; k++)
                sb.AppendLine($"    {Map[k][1],-12} -> {Map[k][0],-14} our#{_asset.MapBone[k]}");
            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < Map.Length; k++) mapped.Add(Map[k][1]);
            var rest = new List<string>();
            var all = _model != null ? _model.Bones?._allBones : null;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name ?? "";
                if (n.Length == 0 || mapped.Contains(n)) continue;
                if (n.IndexOf("chest_0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("spine_0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.StartsWith("neck_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("clavicle", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("upperarm", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("lowerarm", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("hand_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("thigh", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("calf", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("foot", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("ball", StringComparison.OrdinalIgnoreCase) ||
                    n == "pelvis" || n == "head")
                    rest.Add(n);
            }
            sb.AppendLine($"  unmapped game bones ({rest.Count}): {string.Join(", ", rest.ToArray())} " +
                          "(no counterpart in a 3-spine/3-finger-tip Mixamo rig; their motion is not applied)");
            log.LogInfo(sb.ToString());
            // the model side
            var ours = new StringBuilder();
            for (int k = 0; k < Map.Length; k++)
                ours.Append(k == 0 ? "" : ",").Append(Map[k][0]);
            log.LogInfo($"[sahurrig:map] {Map.Length}/{_asset.BoneCount} model bones driven; the other " +
                        $"{_asset.BoneCount - Map.Length} inherit their parent's delta " +
                        $"(fingers from their hand, toes+foot-chain from the foot, BIT from RightHand, eyes/mouth from Head) [{ours}]");
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:map] failed: {e.Message}"); }
    }

    // ================================================================== retarget

    private static void PoseFromGame(IHostContext log)
    {
        var asset = _asset;
        int n = asset.BoneCount;
        Quaternion rootInv = Quaternion.Inverse(_root.rotation);
        var delta = new Quaternion[n];
        for (int i = 0; i < n; i++) delta[i] = Quaternion.identity;   // never leave (0,0,0,0)
        bool first = !_captured;

        if (first) CaptureBind(log);
        var live = new Quaternion[Map.Length];
        var livePos = new Vector3[Map.Length];

        for (int k = 0; k < Map.Length; k++)
        {
            int b = asset.MapBone[k];
            if (b < 0) continue;
            var g = _game[k];
            if (g == null) { delta[b] = Quaternion.identity; continue; }
            var local = rootInv * g.rotation;             // player-model-root space
            live[k] = local;
            livePos[k] = rootInv * (g.position - _root.position);   // root space, origin at the root
            delta[b] = local * Quaternion.Inverse(_gameBind[k]);
        }
        // root follows position: pelvis translation delta, root space
        Vector3 posDelta = Vector3.zero;
        for (int k = 0; k < Map.Length; k++)
        {
            if (!string.Equals(Map[k][1], "pelvis", StringComparison.OrdinalIgnoreCase)) continue;
            if (_game[k] == null) break;
            posDelta = livePos[k] - _gameBindPos;
            break;
        }
        if (first)
        {
            _captured = true;
            // One-shot diagnostic: how far each mapped game bone is from its bind at the moment the
            // model binds, and where the model's own bind puts it. A large delta here means the two
            // skeletons disagree about the rest pose, which shows up as a bent/flipped model.
            var sb = new StringBuilder();
            for (int k = 0; k < Map.Length; k++)
            {
                float ang = Quaternion.Angle(delta[asset.MapBone[k] >= 0 ? asset.MapBone[k] : 0],
                                             Quaternion.identity);
                if (ang < 5f) continue;
                sb.Append($"{Map[k][1]}<->{Map[k][0]}:{ang:F0}deg ");
            }
            log.LogInfo($"[sahurrig:pose] '{(_spec == null ? "?" : _spec.Slug)}' bind deltas >5deg: {(sb.Length == 0 ? "none" : sb.ToString())}");
        }

        // unmapped bones inherit their parent's delta (fingers, toes, BIT, eyes, mouth, head end)
        for (int i = 0; i < n; i++)
        {
            if (asset.Parent[i] < 0) continue;
            if (IsMapped(asset, i)) continue;
            delta[i] = delta[asset.Parent[i]];
        }

        var inv = Quaternion.Inverse(_yaw);
        ApplyDeltas(asset, delta, inv * posDelta);
    }

    // The game's bind pose, not "whatever pose it happened to be in on the first frame": after a
    // model respawn the prefab sits in its own rest pose for a few frames while the animator is
    // still warming up, and capturing that gave the model a permanent ~90 degree arm offset. Every
    // body SkinnedMeshRenderer under the model root publishes its skeleton's bind pose as
    // mesh.bindposes, and inverse(bindpose) is the bone's bind world matrix in that renderer's
    // frame, so the union of those is the true rest pose. A bone that no renderer publishes keeps
    // the live first-frame rotation (logged).
    private static void CaptureBind(IHostContext log)
    {
        var found = new bool[Map.Length];
        var rootInv = Quaternion.Inverse(_root.rotation);
        int rends = 0, bones = 0;
        try
        {
            var all = _model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            for (int r = 0; all != null && r < all.Length; r++)
            {
                var smr = all[r].TryCast<SkinnedMeshRenderer>();
                if (smr == null || IsOurs(smr)) continue;
                var mesh = smr.sharedMesh;
                var bs = smr.bones;
                if (mesh == null || bs == null) continue;
                var bp = mesh.bindposes;
                if (bp == null || bp.Length != bs.Length || bp.Length == 0) continue;
                rends++;
                var rq = rootInv * smr.transform.rotation;      // renderer's frame in the root's space
                for (int i = 0; i < bs.Length; i++)
                {
                    if (bs[i] == null) continue;
                    string nm = bs[i].name ?? "";
                    int k = -1;
                    for (int m = 0; m < Map.Length; m++)
                        if (string.Equals(Map[m][1], nm, StringComparison.OrdinalIgnoreCase)) { k = m; break; }
                    if (k < 0 || found[k]) continue;
                    var bindWorld = bp[i].inverse;               // bone bind pose in the renderer frame
                    _gameBind[k] = rq * bindWorld.rotation;
                    if (string.Equals(Map[k][1], "pelvis", StringComparison.OrdinalIgnoreCase))
                    {
                        // bindWorld is in the renderer's local frame; the renderer itself sits a few
                        // nodes under the model root, so go through the world, then into root space.
                        var p = smr.transform.TransformPoint(bindWorld.GetColumn(3));
                        _gameBindPos = rootInv * (p - _root.position);
                    }
                    found[k] = true;
                    bones++;
                }
            }
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:capture] bindpose read failed: {e.Message}"); }

        int live = 0;
        for (int k = 0; k < Map.Length; k++)
        {
            if (found[k] || _game[k] == null) continue;
            _gameBind[k] = rootInv * _game[k].rotation;
            live++;
        }
        log.LogInfo($"[sahurrig:capture] bind pose from {rends} body renderer(s): {bones}/{Map.Length} bones " +
                    $"from mesh.bindposes, {live} from the live first-frame rotation; " +
                    $"pelvis bind = {_gameBindPos.ToString("F3")} (root space)");
    }

    private static bool IsMapped(Asset asset, int bone)
    {
        for (int k = 0; k < asset.MapBone.Length; k++) if (asset.MapBone[k] == bone) return true;
        return false;
    }

    private static void PoseFromClip(IHostContext log)
    {
        var asset = _asset;
        int n = asset.BoneCount;
        int f0 = asset.ClipFrames;
        if (f0 <= 0) return;
        _walkT += Time.deltaTime;
        if (_walkT >= asset.ClipDuration) _walkT -= asset.ClipDuration;
        float ft = Mathf.Clamp(_walkT / Mathf.Max(asset.ClipDuration, 0.001f) * (f0 - 1), 0f, f0 - 1.0001f);
        int i0 = (int)ft, i1 = Mathf.Min(i0 + 1, f0 - 1);
        float a = ft - i0;
        var delta = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            delta[i] = Quaternion.Slerp(asset.ClipRot[i0 * n + i], asset.ClipRot[i1 * n + i], a);
            if (!(delta[i].w * delta[i].w + delta[i].x * delta[i].x +
                  delta[i].y * delta[i].y + delta[i].z * delta[i].z > 1e-8f))
                delta[i] = Quaternion.identity;
        }
        var pos = Vector3.Lerp(asset.ClipPos[i0], asset.ClipPos[i1], a);
        ApplyDeltas(asset, delta, pos);
        if ((_frames % 60) == 0)
            log.LogInfo($"[sahurrig:walk] t={_walkT:F2}s frame={i0}+{a:F2} of {f0} " +
                        $"pelvisDelta={pos.ToString("F3")} (in place)");
    }

    private static void ApplyDeltas(Asset asset, Quaternion[] delta, Vector3 hipsPosDelta)
    {
        int n = asset.BoneCount;
        var world = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            int p = asset.Parent[i];
            var bw = asset.BindWorld[i];
            world[i] = delta[i] * bw;
            Quaternion parentWorld = p >= 0 ? world[p] : _yaw;
            var local = Quaternion.Inverse(parentWorld) * world[i];
            if (Quaternion.Dot(local, _written[i]) < 0.9999995f)
            {
                _written[i] = local;
                _bones[i].localRotation = local;
            }
        }
        var hips = asset.Hips;
        if (hips >= 0 && _bones[hips] != null)
        {
            var lp = asset.BindPos[hips] + hipsPosDelta;
            if ((lp - _writtenHips).sqrMagnitude > 1e-10f)
            {
                _writtenHips = lp;
                _bones[hips].localPosition = lp;
            }
        }
        _frames++;
    }

    // ================================================================== original body hide/restore

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
                if (CosmeticMarkers.IsCosmetic(r)) continue;
                if (DefaultHands.IsLeased(r)) continue;   // the hands lease owns this renderer
                // Another lease (a remote player's body) owns this renderer: the local rig must never
                // snapshot it, or its release would fight the remote lease's own restore.
                if (RemoteModels.IsLeased(r)) continue;
                var go = r.gameObject;
                if (go == null) continue;
                string nm = go.name ?? "";
                if (nm.StartsWith("Sahur", StringComparison.Ordinal)) continue;
                bool had = false;
                try { had = r.enabled; } catch { continue; }
                _body.Add(new BodyRenderer { R = r, HadEnabled = had });
            }
            log.LogInfo($"[sahurrig:body] snapshot {_body.Count} renderer(s) under '{PathOf(model.transform)}'");
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:body] snapshot failed: {e.Message}"); }
    }

    private static void HideBody(IHostContext log)
    {
        if (_body.Count == 0) return;
        int changed = 0, tracked = 0;
        for (int i = 0; i < _body.Count; i++)
        {
            var e = _body[i];
            if (e.R == null) continue;
            if (DefaultHands.IsLeased(e.R)) continue;   // written by the hands lease, not by this pass
            bool on;
            try { on = e.R.enabled; } catch { continue; }
            if (on)
            {
                try { e.R.enabled = false; changed++; _hides++; } catch { continue; }
            }
            // Track EVERY renderer this lease has disabled, not only the ones this pass changed.
            // The old rule (`Hidden` set only when enabled -> disabled) meant a lease that snapshotted
            // an already-disabled renderer never owned it, so Restore counted 0 and the renderer was
            // left disabled forever - the "restored 0" asymmetry, and an invisible remote body once
            // the lease that had really hidden it was gone. HadEnabled still records the state we
            // found, so restoring a renderer we did not disable is a no-op, never a wrong enable.
            if (!e.Hidden) { e.Hidden = true; tracked++; }
            _body[i] = e;
        }
        if (changed > 0 || tracked > 0)
            log.LogInfo($"[sahurrig:body] hid {changed} body renderer(s), tracked {tracked} for restore (incl. ones already disabled)");
    }

    // Restore is unconditional: every snapshot entry goes back to the enabled state it was found in,
    // whether or not this lease was the writer that disabled it. A dead wrapper cannot be restored
    // (the owner object was destroyed with it) - that is counted and logged instead of silently
    // reporting a smaller number, so "restored 0" always says which of the two it was.
    private static void Restore(IHostContext log, string why)
    {
        int restored = 0, already = 0, lost = 0, skipped = 0;
        for (int i = 0; i < _body.Count; i++)
        {
            var e = _body[i];
            if (!e.Hidden) { skipped++; continue; }
            bool dead = false;
            try
            {
                if (e.R == null) dead = true;              // destroyed with its owner: nothing to restore
                else
                {
                    bool now = e.R.enabled;
                    if (now == e.HadEnabled) already++;
                    else { e.R.enabled = e.HadEnabled; restored++; _restores++; }
                }
            }
            catch { dead = true; }
            if (dead) lost++;
            e.Hidden = false;
            _body[i] = e;
        }
        _body.Clear();
        if (restored > 0 || why != "off")
            log?.LogInfo($"[sahurrig:body] restored {restored} body renderer(s) (why={why}) " +
                         $"already={already} lost={lost} untracked={skipped}" +
                         (lost > 0 ? " - the owner object was destroyed before the release, so those renderers no longer exist in this scene" : ""));
        try { if (_bodyGo != null) _bodyGo.SetActive(false); } catch { }
    }

    // Returns false when a destroy call threw, so the FAILED line can say whether the partial rig
    // was really torn down instead of always claiming it was. The skeleton is destroyed on its own
    // field, never through _bodyGo: the two are siblings and a build can fail between them.
    private static bool DestroyBody(IHostContext log)
    {
        bool ok = true;
        try
        {
            if (_smr != null) { _smr.enabled = false; CosmeticMarkers.Remove(_smr.Pointer); }
            if (_bodyGo != null) { _bodyGo.SetActive(false); CosmeticMarkers.RemoveRoot(_bodyGo.transform.Pointer); }
            if (_rigRoot != null) CosmeticMarkers.RemoveRoot(_rigRoot.Pointer);
            if (_bodyGo != null) UnityEngine.Object.Destroy(_bodyGo);
            if (_rigRoot != null) UnityEngine.Object.Destroy(_rigRoot.gameObject);
        }
        catch (Exception e) { ok = false; log?.LogWarning($"[sahurrig] destroy failed: {e.Message}"); }
        UnregisterImportedBones(_bones);
        try { if (_bodyMesh != null) UnityEngine.Object.Destroy(_bodyMesh); } catch { ok = false; }
        _bodyMesh = null;
        _bodyGo = null;
        _smr = null;
        _rigRoot = null;
        _bones = null;
        _captured = false;
        _assetUse?.Dispose(); _assetUse = null;
        return ok;
    }

    // ============================================================== imported bone lookup
    //
    // The enhancement profiles name the MODEL's chest / hip bones (Sahur: Spine2 / Hips, Nordbo:
    // spine_03 / pelvis) and must resolve them against this model's own skeleton. They do that
    // through the registry above and through nothing else - no hierarchy search, no search root.
    // The array is built one-to-one from the bake's bone table, so:
    //   * every element is an imported bone, and only an imported bone;
    //   * the game's skeleton is not in the array and is not reachable from it (the array holds
    //     transforms, not a subtree we walk - there is no traversal at all);
    //   * a profile name that is not in the array is a MISS, never a fallback into native bones.
    // That is the whole fix for the UE-named bakes: `spine_03` / `pelvis` exist in the game's own
    // skeleton too, so a subtree search from the player-model root resolved them there and scaled
    // bones under the hidden native body while the imported bones stayed at 1.0.

    private static void RegisterImportedBones(Renderer body, Transform[] bones)
    {
        if (body == null || bones == null || bones.Length == 0) return;
        long key;
        try { key = body.Pointer.ToInt64(); } catch { return; }
        _importedBones[key] = new ImportedEntry { R = body, Bones = bones };
    }

    // Identified by the ARRAY (a fresh Transform[] per Build), so a renderer pointer that the engine
    // hands to a later body cannot leave a stale array behind.
    private static void UnregisterImportedBones(Transform[] bones)
    {
        if (bones == null || _importedBones.Count == 0) return;
        List<long> dead = null;
        foreach (var kv in _importedBones)
            if (kv.Value != null && ReferenceEquals(kv.Value.Bones, bones)) (dead ??= new List<long>()).Add(kv.Key);
        if (dead == null) return;
        for (int i = 0; i < dead.Count; i++) _importedBones.Remove(dead[i]);
    }

    // The imported bone array of the rig that draws `body`, or null when this renderer is not one of
    // ours. Null means "no imported skeleton": the caller must treat every profile name as a miss.
    internal static Transform[] ImportedBones(Renderer body)
    {
        if (body == null) return null;
        long key;
        try { key = body.Pointer.ToInt64(); } catch { return null; }
        if (!_importedBones.TryGetValue(key, out var e) || e == null) return null;
        var owner = e.R;
        if (owner == null) { _importedBones.Remove(key); return null; }
        try { if (owner.Pointer != body.Pointer) return null; } catch { return null; }
        return e.Bones;
    }

    // ================================================================== asset load

    private static byte[] Resource(string suffix, out string found)
    {
        found = null;
        var asm = typeof(SahurRig).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { found = name; break; }
        if (found == null) return null;
        using (var s = asm.GetManifestResourceStream(found))
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
    }

    // All consumers pin the same entry through construction and renderer lifetime. Snapshotting
    // a rig cannot hide its ownership from this cache; remote and preview roles are immaterial.
    private static ModelAssetCache<Asset>.Lease AcquireAsset(IHostContext log, ModelSpec spec)
    {
        spec ??= ModelRoster.All[0];
        return _assets.Acquire(spec.Slug, () => Load(log, spec));
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

    private static Asset Load(IHostContext log, ModelSpec spec)
    {
        string blobName, albedoName, normalName = null;
        var blob = Resource(spec.BlobSuffix, out blobName);
        var albedo = Resource(spec.AlbedoSuffix, out albedoName);
        string nrm0 = spec.NormalSuffixes != null && spec.NormalSuffixes.Length > 0 ? spec.NormalSuffixes[0] : null;
        var normal = nrm0 == null ? null : Resource(nrm0, out normalName);
        if (normal == null) normalName = null;
        if (blob == null || albedo == null)
        {
            _health.AssetError = $"missing resource(s) blob='{spec.BlobSuffix}' ({(blobName ?? "not embedded")}) albedo='{spec.AlbedoSuffix}' ({(albedoName ?? "not embedded")})";
            log.LogError($"[sahurrig:asset] missing resource(s) for '{spec.Slug}' " +
                         $"blob='{spec.BlobSuffix}' ({blobName ?? "not embedded"}) " +
                         $"albedo='{spec.AlbedoSuffix}' ({albedoName ?? "not embedded"})");
            return null;
        }
        var asset = new Asset();
        try
        {
            Parse(log, blob, asset, spec);
        }
        catch (Exception e)
        {
            _health.AssetError = "parse failed: " + e.GetType().Name + ": " + e.Message;
            log.LogError($"[sahurrig:asset] parse failed: {e}");
            DestroyAsset(asset);
            return null;
        }
        try
        {
            // One material per texture set the spec declares, in the bake's material order. The
            // submesh->material slot from the blob then picks them, so a two-material model (the
            // cup-headed ballerina: Head, Body) draws both shells with their own maps.
            int n = Math.Max(1, spec.Materials);
            asset.Materials = new Material[n];
            for (int m = 0; m < n; m++)
            {
                string albSuffix = spec.AlbedoSuffixes[Math.Min(m, spec.AlbedoSuffixes.Length - 1)];
                string nrmSuffix = spec.NormalSuffixes != null && m < spec.NormalSuffixes.Length ? spec.NormalSuffixes[m] : null;
                var albB = m == 0 ? albedo : Resource(albSuffix, out var foundA);
                var nrmB = nrmSuffix == null ? null : (m == 0 && normal != null ? normal : Resource(nrmSuffix, out _));
                var albT = Texture(log, albB, "Cosmetic_" + spec.Tag + "Albedo" + m, out _, out long albBytes);
                Texture2D nrmT = null;
                long nrmBytes = 0;
                if (nrmB != null) nrmT = Texture(log, nrmB, "Cosmetic_" + spec.Tag + "Normal" + m, out _, out nrmBytes);
                asset.DecodedBytes += albBytes + nrmBytes;
                if (albT != null) asset.Textures.Add(albT);
                if (nrmT != null) asset.Textures.Add(nrmT);
                asset.Materials[m] = LitMaterial(log, "Cosmetic_" + spec.Tag + "Mat" + m, albT, nrmT);
                asset.TexBytes += (albB == null ? 0 : albB.Length) + (nrmB == null ? 0 : nrmB.Length);
                if (m == 0) { asset.Albedo = albT; asset.Normal = nrmT; }
            }
            asset.Body = asset.Materials[0];
        }
        catch (Exception e)
        {
            _health.AssetError = "texture/material failed: " + e.GetType().Name + ": " + e.Message;
            log.LogError($"[sahurrig:asset] texture/material failed: {e}");
            DestroyAsset(asset);
            return null;
        }
        log.LogInfo($"[sahurrig:asset] '{spec.Label}' blob='{blobName}' {blob.Length} B albedo='{albedoName}' {albedo.Length} B " +
                    $"normal='{normalName ?? "none"}' {(normal == null ? 0 : normal.Length)} B " +
                    $"verts={asset.Verts} tris={asset.Tris} bones={asset.BoneCount} submeshes={asset.Submeshes} " +
                    $"materials={asset.Materials.Length} " +
                    $"clip={asset.ClipFrames}f/{asset.ClipDuration:F3}s payload={blob.Length + asset.TexBytes} B");
        return asset;
    }

    private static Texture2D Texture(IHostContext log, byte[] png, string name, out int levels, out long decoded)
    {
        int w, h;
        var rgba = PngDecoder.DecodeRgbaBottomUp(png, out w, out h);
        var chain = PngDecoder.BuildMipChain(rgba, w, h, out levels);
        decoded = chain.Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
        tex.name = name;
        tex.LoadRawTextureData(new Il2CppStructArray<byte>(chain));
        tex.Apply(true, true);
        log.LogInfo($"[sahurrig:tex] '{name}' {w}x{h} mips={levels} ({png.Length} B png -> {w * h * 4} B + mips)");
        return tex;
    }

    private static Material LitMaterial(IHostContext log, string name, Texture2D albedo, Texture2D normal)
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
            log.LogWarning($"[sahurrig:shader] no game URP/Lit template; Shader.Find found={sh != null}");
            mat = new Material(sh);
        }
        mat.name = name;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", albedo != null ? (Texture)albedo : Texture2D.whiteTexture);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        if (mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", normal != null ? (Texture)normal : null);
            if (normal != null)
            {
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                try { mat.EnableKeyword("_NORMALMAP"); mat.EnableKeyword("_NORMALMAP_TANGENT_SPACE"); } catch { }
            }
            else try { mat.DisableKeyword("_NORMALMAP"); } catch { }
        }
        if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", null);
        // the roughness/AO maps are not baked: a constant smoothness from the mean roughness (0.525)
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.475f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        log.LogInfo($"[sahurrig:mat] '{name}' template={(template != null ? template.name : "Shader.Find")} " +
                    $"normal={(normal != null)} smoothness=0.475 metallic=0");
        return mat;
    }

    private static void DestroyAsset(Asset a)
    {
        try { if (a.Mesh != null) UnityEngine.Object.Destroy(a.Mesh); } catch { }
        // Every material, not just slot 0: a two-material bake (Capuchino, the TC2 set) holds a
        // second material and its normal map. Albedo/Normal are members of Textures.
        try
        {
            if (a.Materials != null)
                for (int i = 0; i < a.Materials.Length; i++)
                    if (a.Materials[i] != null) UnityEngine.Object.Destroy(a.Materials[i]);
        }
        catch { }
        try { if (a.Body != null && (a.Materials == null || a.Materials.Length == 0)) UnityEngine.Object.Destroy(a.Body); } catch { }
        try
        {
            for (int i = 0; i < a.Textures.Count; i++)
                if (a.Textures[i] != null) UnityEngine.Object.Destroy(a.Textures[i]);
            a.Textures.Clear();
        }
        catch { }
    }

    // ================================================================== blob parse

    private static int I32(byte[] b, int o) { return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); }
    private static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
    private static short S16(byte[] b, int o) { return (short)(b[o] | (b[o + 1] << 8)); }
    private static float F32(byte[] b, int o) { return BitConverter.ToSingle(BitConverter.GetBytes(I32(b, o)), 0); }

    private static void Parse(IHostContext log, byte[] b, Asset a, ModelSpec spec)
    {
        if (b.Length < 48 || b[0] != (byte)'S' || b[1] != (byte)'A' || b[2] != (byte)'H' || b[3] != (byte)'R')
            throw new FormatException("bad magic (expected SAHRRG1)");
        int o = 8;
        int version = I32(b, o); o += 4;
        int flags = I32(b, o); o += 4;
        if (version != 1) throw new FormatException($"unsupported version {version}");
        int nv = I32(b, o); o += 4;
        int nt = I32(b, o); o += 4;
        int nb = I32(b, o); o += 4;
        int ns = I32(b, o); o += 4;
        int nm = I32(b, o); o += 4;
        var lo = new Vector3(F32(b, o), F32(b, o + 4), F32(b, o + 8)); o += 12;
        var hi = new Vector3(F32(b, o), F32(b, o + 4), F32(b, o + 8)); o += 12;
        var uvlo = new Vector2(F32(b, o), F32(b, o + 4)); o += 8;
        var uvhi = new Vector2(F32(b, o), F32(b, o + 4)); o += 8;
        int nameLen = I32(b, o); o += 4;
        var names = Encoding.UTF8.GetString(b, o, nameLen).Split('\n');
        o += nameLen;

        a.BoneCount = nb;
        a.Parent = new int[nb];
        a.BindPos = new Vector3[nb];
        a.BindRot = new Quaternion[nb];
        a.BoneName = new string[nb];
        for (int i = 0; i < nb; i++)
        {
            sbyte parent = (sbyte)b[o]; o += 1;
            o += 1;
            a.BindPos[i] = new Vector3(F32(b, o), F32(b, o + 4), F32(b, o + 8)); o += 12;
            a.BindRot[i] = new Quaternion(F32(b, o), F32(b, o + 4), F32(b, o + 8), F32(b, o + 12)); o += 16;
            // The blob is canonical metres with unit-scale bones; a decode must not rescale them.
            // Reject a quaternion that is not a rotation instead of letting TRS build garbage.
            if (!(a.BindRot[i].w * a.BindRot[i].w + a.BindRot[i].x * a.BindRot[i].x +
                  a.BindRot[i].y * a.BindRot[i].y + a.BindRot[i].z * a.BindRot[i].z > 1e-8f))
            {
                log.LogWarning($"[sahurrig:parse] bone {i} has a zero quaternion; using identity");
                a.BindRot[i] = Quaternion.identity;
            }
            a.Parent[i] = parent;
            a.BoneName[i] = i < names.Length ? names[i] : "?";
        }
        a.Submeshes = ns;
        a.SubmeshTri = new int[ns][];
        a.SubmeshKind = new int[ns];
        a.SubmeshMat = new int[ns];
        int triBase = 0;
        for (int s = 0; s < ns; s++)
        {
            int tn = I32(b, o); o += 4;
            int mat = b[o]; o += 1;                   // material slot: the one piece of the bake that
            int kind = b[o]; o += 1;                  // differs per model (Capuchino has 2 materials)
            o += 2;
            a.SubmeshTri[s] = new int[tn * 3];
            a.SubmeshKind[s] = kind;
            a.SubmeshMat[s] = mat;
            triBase += tn;
        }
        if (triBase != nt) throw new FormatException($"submesh tris {triBase} != {nt}");

        var span = hi - lo;
        a.Pos = new Vector3[nv];
        for (int i = 0; i < nv; i++)
        {
            int q = o + i * 6;
            a.Pos[i] = new Vector3(
                lo.x + (S16(b, q) + 32768f) / 65535f * span.x,
                lo.y + (S16(b, q + 2) + 32768f) / 65535f * span.y,
                lo.z + (S16(b, q + 4) + 32768f) / 65535f * span.z);
        }
        o += nv * 6;
        a.Uv = new Vector2[nv];
        var uvSpan = uvhi - uvlo;
        for (int i = 0; i < nv; i++)
        {
            int q = o + i * 4;
            a.Uv[i] = new Vector2(uvlo.x + U16(b, q) / 65535f * uvSpan.x,
                                  uvlo.y + U16(b, q + 2) / 65535f * uvSpan.y);
        }
        o += nv * 4;
        a.Nrm = new Vector3[nv];
        for (int i = 0; i < nv; i++)
        {
            int q = o + i * 3;
            a.Nrm[i] = new Vector3((sbyte)b[q] / 127f, (sbyte)b[q + 1] / 127f, (sbyte)b[q + 2] / 127f);
        }
        o += nv * 3;
        int bones = o;
        var bi = new int[nv * 4];
        for (int i = 0; i < bi.Length; i++) bi[i] = b[bones + i];
        o += nv * 4;
        a.Weights = new BoneWeight[nv];
        for (int i = 0; i < nv; i++)
        {
            a.Weights[i] = new BoneWeight
            {
                m_BoneIndex0 = Mathf.Clamp(bi[i * 4], 0, nb - 1),
                m_BoneIndex1 = Mathf.Clamp(bi[i * 4 + 1], 0, nb - 1),
                m_BoneIndex2 = Mathf.Clamp(bi[i * 4 + 2], 0, nb - 1),
                m_BoneIndex3 = Mathf.Clamp(bi[i * 4 + 3], 0, nb - 1),
                m_Weight0 = b[o + i * 4] / 255f,
                m_Weight1 = b[o + i * 4 + 1] / 255f,
                m_Weight2 = b[o + i * 4 + 2] / 255f,
                m_Weight3 = b[o + i * 4 + 3] / 255f,
            };
        }
        o += nv * 4;
        var idx = new int[nt * 3];
        for (int i = 0; i < idx.Length; i++) idx[i] = U16(b, o + i * 2);
        o += nt * 6;
        int t = 0;
        for (int s = 0; s < ns; s++)
        {
            int c = a.SubmeshTri[s].Length;
            Array.Copy(idx, t, a.SubmeshTri[s], 0, c);
            t += c;
        }

        a.ClipFrames = U16(b, o); o += 2;
        a.ClipDuration = F32(b, o); o += 4;
        a.ClipRot = new Quaternion[a.ClipFrames * nb];
        for (int f = 0; f < a.ClipFrames; f++)
            for (int i = 0; i < nb; i++)
            {
                int q = o + (f * nb + i) * 8;
                a.ClipRot[f * nb + i] = new Quaternion(S16(b, q) / 32767f, S16(b, q + 2) / 32767f,
                                                      S16(b, q + 4) / 32767f, S16(b, q + 6) / 32767f);
            }
        o += a.ClipFrames * nb * 8;
        a.ClipPos = new Vector3[a.ClipFrames];
        for (int f = 0; f < a.ClipFrames; f++)
            a.ClipPos[f] = new Vector3(S16(b, o + f * 6) / 1000f, S16(b, o + f * 6 + 2) / 1000f,
                                       S16(b, o + f * 6 + 4) / 1000f);
        o += a.ClipFrames * 6;
        if (o != b.Length) throw new FormatException($"{b.Length - o} trailing bytes");

        a.Verts = nv;
        a.Tris = nt;
        a.Bytes = b.Length;
        a.MinY = lo.y;
        a.MaxY = hi.y;
        a.Hips = a.IndexOf("Hips");
        // Only the table of THIS model may size MapBone: a rigged entry's pair count is its own.
        a.MapBone = new int[spec != null && spec.BoneMap != null ? spec.BoneMap.Length : Map.Length];
        // the bat island: the submesh the bake marked as kind 1
        a.BatBone = -1;
        a.BatTris = 0;
        for (int s = 0; s < ns; s++)
            if (a.SubmeshKind[s] == 1) { a.BatTris += a.SubmeshTri[s].Length / 3; }
        // find the BIT bone and which submesh uses it (weights of the first bat triangle)
        for (int i = 0; i < nb; i++) if (a.BoneName[i] == "BIT") a.BatBone = i;
        log.LogInfo($"[sahurrig:parse] flags={flags} (1 = mesh mirrored in X at bake) " +
                    $"verts={nv} tris={nt} bones={nb} submeshes={ns} materials={nm} " +
                    $"clipFrames={a.ClipFrames} clip={a.ClipDuration:F3}s");
    }

    private static float HeightM(Asset a) { return a.MaxY - a.MinY; }

    // ================================================================== report

    private static void Dump(IHostContext log)
    {
        try
        {
            var a = _asset;
            var sb = new StringBuilder();
            sb.Append($"[sahurrig:dump] on={_on} walk={_walk} builds={_builds} hides={_hides} restores={_restores} ");
            sb.Append($"frames={_frames} root={(_root == null ? "none" : PathOf(_root))} ");
            sb.Append($"body={(_bodyGo == null ? "none" : PathOf(_bodyGo.transform))} ");
            if (a != null)
                sb.Append($"asset verts={a.Verts} tris={a.Tris} bones={a.BoneCount} submeshes={a.Submeshes} " +
                          $"height={HeightM(a):F3}m clip={a.ClipFrames}f/{a.ClipDuration:F3}s payload={a.Bytes + a.TexBytes}B ");
            if (_smr != null)
                sb.Append($"smr layer={_smr.gameObject.layer} bones={_smr.bones.Length} quality={_smr.quality} " +
                          $"materials={_smr.sharedMaterials.Length} submeshes={_smr.sharedMesh.subMeshCount} " +
                          $"boundsWorld={_smr.bounds.size.ToString("F2")} visible={_smr.enabled} ");
            sb.Append($"hidden={CountHidden()} human={(_human != null)}");
            log.LogInfo(sb.ToString());
            if (_smr != null && _smr.sharedMesh != null)
            {
                var mesh = _smr.sharedMesh;
                var verts = mesh.vertices;
                Vector3 lo = verts[0], hi = verts[0];
                for (int i = 1; i < verts.Length; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
                log.LogInfo($"[sahurrig:dump] mesh local y {lo.y:F3}..{hi.y:F3} ({(hi.y - lo.y):F3} m) " +
                            $"x {lo.x:F3}..{hi.x:F3} z {lo.z:F3}..{hi.z:F3} bindposes={mesh.bindposes.Length}");
                MeasureWorld(log);
                var b = _asset != null ? _asset.BatBone : -1;
                if (b >= 0 && _bones != null && _bones[b] != null)
                {
                    var hand = _asset.MapBone.Length > 0 ? _bones[_asset.MapBone[13]] : null;
                    log.LogInfo($"[sahurrig:dump] BIT world={_bones[b].position.ToString("F3")} " +
                                $"RightHand world={(hand == null ? "n/a" : hand.position.ToString("F3"))} " +
                                $"distance={(hand == null ? 0f : Vector3.Distance(hand.position, _bones[b].position)):F3} m");
                }
            }
            log.LogInfo("[sahurrig:dump] model skeleton names: " + (_asset == null ? "none" : string.Join(",", _asset.BoneName)));
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:dump] failed: {e.Message}"); }
    }

    private static void Compare(IHostContext log)
    {
        try
        {
            if (_smr == null) { log.LogWarning("[sahurrig:mats] nothing built"); return; }
            var mesh = _smr.sharedMesh;
            log.LogInfo($"[sahurrig:mats] submeshes={mesh.subMeshCount} materials={_smr.sharedMaterials.Length} " +
                        $"bones={_smr.bones.Length} rootBone='{(_smr.rootBone == null ? "null" : _smr.rootBone.name)}' " +
                        $"quality={_smr.quality} localBounds={_smr.localBounds.size.ToString("F3")} " +
                        $"meshBounds={mesh.bounds.size.ToString("F3")}@{mesh.bounds.center.ToString("F3")} " +
                        $"weights={mesh.boneWeights.Length} bindposes={mesh.bindposes.Length}");
            for (int p = 0; p < mesh.subMeshCount; p++)
            {
                var tri = mesh.GetTriangles(p);
                log.LogInfo($"[sahurrig:mats]   submesh {p}: tris={tri.Length / 3} material='{_smr.sharedMaterials[p].name}' " +
                            $"kind={(_asset != null && p < _asset.SubmeshKind.Length ? _asset.SubmeshKind[p] : -1)}");
            }
        }
        catch (Exception e) { log.LogError($"[sahurrig:mats] failed: {e}"); }
    }

    // CPU skin of one vertex per bone-weighted sample: proves the retarget deforms the MESH (not just
    // the transforms) and reports the world height of the skinned result.
    private static void MeasureWorld(IHostContext log)
    {
        try
        {
            var mesh = _smr.sharedMesh;
            var verts = mesh.vertices;
            var weights = mesh.boneWeights;
            var bind = mesh.bindposes;
            var bones = _smr.bones;
            if (weights.Length != verts.Length || bind.Length != bones.Length) return;
            var skin = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                skin[b] = bones[b] != null ? bones[b].localToWorldMatrix * bind[b] : Matrix4x4.identity;
            float minY = float.MaxValue, maxY = float.MinValue;
            var acc = Vector3.zero;
            for (int v = 0; v < verts.Length; v++)
            {
                var w = weights[v];
                var p = Vector3.zero;
                float sum = 0f;
                if (w.m_Weight0 > 0f && w.m_BoneIndex0 >= 0 && w.m_BoneIndex0 < skin.Length)
                { p += skin[w.m_BoneIndex0].MultiplyPoint3x4(verts[v]) * w.m_Weight0; sum += w.m_Weight0; }
                if (w.m_Weight1 > 0f && w.m_BoneIndex1 >= 0 && w.m_BoneIndex1 < skin.Length)
                { p += skin[w.m_BoneIndex1].MultiplyPoint3x4(verts[v]) * w.m_Weight1; sum += w.m_Weight1; }
                if (w.m_Weight2 > 0f && w.m_BoneIndex2 >= 0 && w.m_BoneIndex2 < skin.Length)
                { p += skin[w.m_BoneIndex2].MultiplyPoint3x4(verts[v]) * w.m_Weight2; sum += w.m_Weight2; }
                if (w.m_Weight3 > 0f && w.m_BoneIndex3 >= 0 && w.m_BoneIndex3 < skin.Length)
                { p += skin[w.m_BoneIndex3].MultiplyPoint3x4(verts[v]) * w.m_Weight3; sum += w.m_Weight3; }
                if (sum > 1e-6f) p /= sum;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
                acc += p;
            }
            var head = _model != null && _model.gameObject != null ? _model.Bones?.Head : null;
            log.LogInfo($"[sahurrig:measure] skinned world y {minY:F3}..{maxY:F3} => height={(maxY - minY):F3} m " +
                        $"(feetY={minY:F3} centroid={ (acc / Mathf.Max(1, verts.Length)).ToString("F2") }) " +
                        $"gameHeadBoneY={(head == null ? float.NaN : head.position.y):F3}");
        }
        catch (Exception e) { log.LogWarning($"[sahurrig:measure] failed: {e.Message}"); }
    }

    private static int CountHidden()
    {
        int n = 0;
        for (int i = 0; i < _body.Count; i++) if (_body[i].Hidden) n++;
        return n;
    }

    // ================================================================== scale human (same trick as the classic module)

    private static void DespawnHuman(IHostContext log)
    {
        try { if (_human != null) UnityEngine.Object.Destroy(_human); } catch { }
        _human = null;
        log?.LogInfo("[sahurrig:human] removed the scale reference");
    }

    private static void SpawnHuman(IHostContext log)
    {
        try
        {
            if (_root == null) { log.LogWarning("[sahurrig:human] no player model bound"); return; }
            PlayerModelData pick = null;
            var arr = SceneCache.Scan(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null || d.ThirdViewPlayerModel == null) continue;
                if ((d.name ?? "").IndexOf("Freya", StringComparison.OrdinalIgnoreCase) >= 0) { pick = d; break; }
                if (pick == null) pick = d;
            }
            if (pick == null) { log.LogWarning("[sahurrig:human] no PlayerModelData with a third-view prefab"); return; }
            DespawnHuman(null);
            var obj = UnityEngine.Object.Instantiate(pick.ThirdViewPlayerModel.gameObject);
            var go = obj?.TryCast<GameObject>();
            if (go == null) { log.LogWarning("[sahurrig:human] instantiate failed"); return; }
            go.name = "SahurRigged_ScaleHuman";
            go.transform.position = _root.position - _root.right * 1.15f;
            go.transform.rotation = _root.rotation;
            var trs = go.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            for (int i = 0; trs != null && i < trs.Length; i++)
            {
                var t = trs[i].TryCast<Transform>();
                if (t != null) t.gameObject.layer = ModelLayer;
            }
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
            log.LogInfo($"[sahurrig:human] '{pick.name}' at {go.transform.position.ToString("F2")} " +
                        $"feetY={_root.position.y:F3} headY={(head == null ? float.NaN : head.position.y):F3} " +
                        $"-> gameHeight={(head == null ? float.NaN : head.position.y - _root.position.y):F3}m");
        }
        catch (Exception e) { log.LogError($"[sahurrig:human] failed: {e}"); }
    }

    // ================================================================== capture

    private static void Capture(IHostContext log, string view)
    {
        float orthoSize = 1.02f;
        int colon = view.IndexOf(':');
        if (colon > 0)
        {
            if (float.TryParse(view.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var sz) && sz > 0.05f) orthoSize = sz;
            view = view.Substring(0, colon);
        }
        if (_root == null || _smr == null) { log.LogWarning("[sahurrig:view] nothing built"); return; }
        GameObject camGo = null, lightGo = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        var previous = RenderTexture.active;
        try
        {
            Vector3 origin = _root.position;
            Vector3 fwd = _root.forward, right = _root.right;
            Vector3 dir;
            float dist = 3.4f;
            switch (view.ToLowerInvariant())
            {
                case "back": dir = -fwd; break;
                case "side": dir = right; dist = 3.0f; break;
                case "left": dir = -right; dist = 3.0f; break;
                case "compare": dir = fwd; dist = 4.2f; orthoSize = Mathf.Max(orthoSize, 1.35f); break;
                default: dir = fwd; break;
            }
            Vector3 eye = origin + Vector3.up * 1.02f + dir.normalized * dist;
            Vector3 look = origin + Vector3.up * 0.92f;

            camGo = new GameObject("SahurRigged_CapCam");
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) throw new InvalidOperationException("camera creation failed");
            cam.enabled = false;
            cam.cullingMask = 1 << ModelLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f, 1f);
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
            lightGo = new GameObject("SahurRigged_CapLight");
            var light = lightGo.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
            if (light != null)
            {
                light.type = LightType.Directional;
                light.intensity = 1.35f;
                light.color = new Color(1f, 0.97f, 0.9f);
                lightGo.transform.rotation = Quaternion.LookRotation(-(eye - look).normalized + new Vector3(0.35f, -0.55f, 0f), Vector3.up);
                light.cullingMask = 1 << ModelLayer;
            }

            Material captureMat = null;
            var savedMode = RenderSettings.ambientMode;
            var savedAmbient = RenderSettings.ambientLight;
            var hidden = new List<Renderer>();
            try
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null || !sh.isSupported) sh = Shader.Find("Universal Render Pipeline/Lit");
                captureMat = new Material(sh) { name = "Cosmetic_SahurRobloxCapture" };
                if (captureMat.HasProperty("_BaseMap")) captureMat.SetTexture("_BaseMap", _asset != null ? (Texture)_asset.Albedo : Texture2D.whiteTexture);
                if (captureMat.HasProperty("_BaseColor")) captureMat.SetColor("_BaseColor", Color.white);
                if (captureMat.HasProperty("_Color")) captureMat.SetColor("_Color", Color.white);
                _smr.sharedMaterial = captureMat;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.74f, 0.74f, 0.78f);

                var all = SceneCache.Scan(Il2CppType.Of<Renderer>());
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var g = r.gameObject;
                    if (g == null || g.layer != ModelLayer || !r.enabled) continue;
                    if (IsOurs(r) || IsHuman(r)) continue;      // keep ours and the scale reference
                    r.enabled = false;
                    hidden.Add(r);
                }
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
            }
            finally
            {
                for (int i = 0; i < hidden.Count; i++)
                    try { if (hidden[i] != null) hidden[i].enabled = true; } catch { }
                try { RenderSettings.ambientMode = savedMode; } catch { }
                try { RenderSettings.ambientLight = savedAmbient; } catch { }
                try { if (_smr != null && _asset != null) _smr.sharedMaterial = _asset.Body; } catch { }
                try { if (captureMat != null) UnityEngine.Object.Destroy(captureMat); } catch { }
            }
            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, $"sahurrig_view_{view}.png");
            File.WriteAllBytes(path, bytes);
            var wb = _smr.bounds;
            log.LogInfo($"[sahurrig:view] view={view} {rt.width}x{rt.height} ortho={cam.orthographicSize:F2} " +
                        $"eye={eye.ToString("F2")} root={origin.ToString("F2")} fwd={fwd.ToString("F2")} " +
                        $"smrWorldBounds={wb.size.ToString("F2")}@y{wb.center.y:F2} human={(_human != null)} -> {path} ({bytes.Length} B)");
        }
        catch (Exception e) { log.LogError($"[sahurrig:view] capture failed: {e}"); }
        finally
        {
            RenderTexture.active = previous;
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
            try { if (lightGo != null) UnityEngine.Object.Destroy(lightGo); } catch { }
        }
    }

    private static bool IsOurs(Renderer r)
    {
        try
        {
            for (var t = r.transform; t != null; t = t.parent)
            {
                string n = t.name ?? "";
                if (n.StartsWith("SahurRigged", StringComparison.Ordinal)) return true;
                if (n.StartsWith("Sahur", StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsHuman(Renderer r)
    {
        if (_human == null) return false;
        try
        {
            for (var t = r.transform; t != null; t = t.parent)
                if (t.gameObject == _human) return true;
        }
        catch { }
        return false;
    }

    private static void HudShot(IHostContext log, string name)
    {
        try
        {
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            log.LogInfo($"[sahurrig] full-screen capture -> {path}");
        }
        catch (Exception e) { log.LogWarning($"[sahurrig] capture failed: {e.Message}"); }
    }

    // ================================================================== helpers

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
