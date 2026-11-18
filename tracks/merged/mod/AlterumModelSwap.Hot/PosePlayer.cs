using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Animations.ExtendedAnimancer.PersonAnimancer;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// CPU pose player for ported Lethal Company emote motion.
//
// Alterum's rig is driven by Animancer every frame, so a pose write only sticks if the game's own
// animation is suspended. This takes an exclusive lease on the model (DoingSomethingHandler +
// ModelPersonAnimancerController.DisableAnimancerAndAnimator with one native key object), captures
// the model generation, and writes target local rotations from the post-animation callback.
//
// Retargeting: the pack stores, per source bone and per 60 Hz frame, the *global* rotation delta of
// the LC metarig relative to either its rest pose (Anchor=Rest) or the clip's first frame
// (Anchor=Frame0). Either anchor is converted into the model-root-local space of the Alterum rig,
// rotated by the calibrated source->target basis C, and composed with the target anchor pose.
// Only rotations are transferred; target bone lengths and scales are never modified.
internal sealed class PosePlayer
{
    internal bool Active { get; private set; }
    internal string CurrentClip { get; private set; } = "";
    internal float Elapsed => _nativeClip != null ? _nativeTime : _stateTime;
    internal float Duration => _nativeClip != null ? _nativeClip.length : (_clip != null ? _clip.Duration : 0f);

    // True while this player writes local transforms: an active clip (ported LC or one of the
    // game's own clips), or the short fade-out that blends back to the live pose after Stop. The
    // jiggle solve has to stay suspended for both, so the skeleton has exactly one post-animation
    // writer at any moment. Both clip kinds set Active, so there is one ownership signal, not two.
    internal bool Writing => Active || _stopping;

    // The game's own clip being played through the lease (null for a ported LC clip). Same lease,
    // same write phase, same fade and same RestorePoseNow hand-back as the LC path.
    internal AnimationClip NativeClip => _nativeClip;
    internal bool NativeMode => _nativeClip != null;

    // Upper-body guard evidence: bones the clip's layer mask does NOT allow (pelvis, spine, neck,
    // thighs, calves, feet...) are never assigned by the native path. This counts the distinct
    // such channels found changed against their entry value, so the sweep can assert zero.
    internal int NativeBodyChannelsChanged { get; private set; }
    internal float NativeBodyMaxDeviationDeg { get; private set; }
    internal int NativeAllowedBones { get; private set; }
    internal string NativeMaskSummary { get; private set; } = "-";

    private readonly IHostContext _log;
    private EmotePack _pack;

    private PersonAnimancerComponent _person;
    private ModelPersonAnimancerController _controller;
    private PlayerModelReferences _refs;
    private DoingSomethingHandler _doingSomething;
    private Animator _animator;
    private int _generation;

    // Lease bookkeeping. Each acquisition step is recorded separately so a partial acquisition can
    // be unwound exactly, and each release obligation is retained (with the handler that owes it)
    // until it has actually completed. A release that throws keeps the key, so the next
    // Stop/Dispose retries it instead of leaking native gameplay state.
    private Il2CppSystem.Object _lease;
    private DoingSomethingHandler _leaseDoingSomething;
    private ModelPersonAnimancerController _leaseController;
    private bool _obligationDoingSomething;
    private bool _obligationAnimancer;
    private bool _releasedDoingSomething;
    private bool _releasedAnimancer;

    // Live generations share one native player rig, and the static jiggle probe outlives a single
    // PosePlayer. This counter is the probe's signal that the emote owns Animator.enabled right
    // now: a freeze session must not write back a captured state it does not own (P2.2).
    internal static int LeasesHeld;

    private Transform[] _order;
    private int[] _parent;
    private Quaternion[] _entryLocal;
    private Vector3[] _entryLocalPos;
    private Quaternion[] _anchorRot;
    private Vector3[] _anchorPos;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private Quaternion _rootRotInv;
    private int _pelvisIndex = -1;
    private int _footIndex = -1;
    private float _legScale = 1f;

    private EmoteClip _clip;
    private int[] _map;                 // order index -> clip bone index (-1)
    private Quaternion[] _written;
    private float _stateTime;
    private float _fadeIn;
    private float _fadeOut;
    private Quaternion[] _stopFrom;
    private bool _stopping;
    private int _lateWriterReports;
    private Quaternion _basis = Quaternion.identity;
    private Quaternion _basisInv = Quaternion.identity;

    // Position bookkeeping. Only the pelvis is ever moved, and only vertically, but the offset has
    // to fade in and out like the rotations and has to be handed back on teardown: a controller
    // that does not write pelvis translation would otherwise keep the last emote's offset and the
    // next Start would capture it as its own baseline.
    private Vector3[] _writtenPos;      // last localPosition we wrote per bone
    private Vector3[] _stopFromPos;     // fade-out start positions
    private bool[] _posDirty;           // bones whose position we changed and must restore
    private int _writeFrames;           // evidence: frames on which this run wrote the skeleton
    private bool _seekPending;          // this frame is the first after a Seek (evidence only)
    private int _posTraceLines;
    private bool _entryTraceDone;

    // Native (game clip) playback state. The clip is sampled on a scratch copy of the rig and the
    // sampled pose is written here with the same fade/restore/lease bookkeeping as a ported clip.
    private AnimationClip _nativeClip;
    private NativeSampler _sampler;
    private float _nativeTime;
    private int _nativeWriteFrames;
    private bool[] _nativeAllow;        // order index -> this clip's layer mask allows writing the bone
    private bool[] _nativeTouched;      // order index -> already counted in the body-channel guard

    // Two-bone arm IK (upperarm -> lowerarm -> hand) to the ported LC arm targets. Built from the
    // model's own bind-pose bone lengths; the target offset itself is in source model space and is
    // mapped with the same basis C the rotations use, scaled by the arm reach ratio.
    private sealed class IkChain
    {
        internal int Upper = -1, Lower = -1, Hand = -1, Parent = -1;
        internal float L1, L2, Reach, Scale = 1f;
        internal int Clamps;
        internal float LastWeight, LastDist, LastErr;
        internal Vector3 LastTargetLocal, LastUpperLocal, LastHandLocal;
        internal bool Valid => Upper >= 0 && Lower >= 0 && Hand >= 0 && Parent >= 0;
    }
    private readonly IkChain _ikL = new IkChain();
    private readonly IkChain _ikR = new IkChain();

    internal Quaternion Basis => _basis;
    internal string RigSummary { get; private set; } = "";

    private const float FadeInSeconds = 0.20f;
    private const float FadeOutSeconds = 0.20f;

    // Verification switch: false reproduces the pre-IK build exactly (the pack's IK data is
    // ignored), so before/after measurements can be taken with identical measurement code.
    private const bool IkEnabled = true;

    internal PosePlayer(IHostContext log, EmotePack pack)
    {
        _log = log;
        _pack = pack;
    }

    internal bool Bound => _order != null && _person != null;
    internal bool LeaseHeld => _lease != null;
    internal int WriteFrames => _writeFrames;

    // Diagnostic harness for the P1.5 acceptance case "the returning animator does not write pelvis
    // translation". When set, the next fade-out takes the branch it would take in that situation:
    // blend the pelvis back to the captured baseline instead of to the animator's value. The game's
    // animation system re-drives the pelvis every frame, so this branch cannot be triggered from
    // outside; the switch exercises exactly the same code path. One-shot, cleared when the fade ends.
    internal bool ForceBaselineFadeTarget;

    // The bound skeleton is destroyed when the game re-instantiates the player model. A captured
    // transform can also die on its own (a cosmetic root under a socket used to be captured here),
    // so the whole order is checked, not just the pelvis: a single dead entry makes every write
    // loop throw, and the retry would keep the stale order forever.
    internal bool HasDeadBone()
    {
        var order = _order;
        if (order == null || order.Length == 0) return true;
        int i = _pelvisIndex >= 0 && _pelvisIndex < order.Length ? _pelvisIndex : 0;
        if (!Alive(order[i])) return true;
        for (i = 0; i < order.Length; i++) if (!Alive(order[i])) return true;
        return false;
    }

    // Unity destroys the native object, not the managed wrapper, so a plain reference comparison is
    // not enough; touching the component forces the native liveness check.
    private static bool Alive(Transform t)
    {
        if (t == null) return false;
        try { return t.gameObject != null; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ bind

    internal bool Bind(PlayerModelReferences refs, PlayerModelPersonAnimancerController controller,
                       PersonAnimancerComponent person)
    {
        try
        {
            Unbind();
            if (person == null || controller == null || refs == null) return false;
            _refs = refs;
            _controller = controller;
            _person = person;
            _animator = person.Animator;
            _doingSomething = refs.DoingSomethingHandler;
            if (_animator == null) { _log.LogWarning("[emote] bind: animancer has no Animator"); return false; }
            _generation++;

            var root = _animator.transform;
            _rootRotInv = Quaternion.Inverse(root.rotation);

            var all = new List<Transform>();
            var parents = new List<int>();
            _byName.Clear();
            int cosmeticSkipped = 0;
            Walk(root, root, -1, all, parents, 0, ref cosmeticSkipped);
            _order = all.ToArray();
            _parent = parents.ToArray();
            int n = _order.Length;

            _entryLocal = new Quaternion[n];
            _entryLocalPos = new Vector3[n];
            _anchorRot = new Quaternion[n];
            _anchorPos = new Vector3[n];
            _written = new Quaternion[n];
            _globals = new Quaternion[n];
            _writtenPos = new Vector3[n];
            _posDirty = new bool[n];
            for (int i = 0; i < n; i++)
            {
                _entryLocal[i] = _order[i].localRotation;
                _entryLocalPos[i] = _order[i].localPosition;
                _anchorRot[i] = _entryLocal[i];
                _anchorPos[i] = _entryLocalPos[i];
                _writtenPos[i] = _entryLocalPos[i];
            }

            // Rest anchors from the skinned meshes' own bind poses: exact, per-model, and not an
            // arbitrary mid-animation pose.
            string restSource = "entry-pose";
            var restRot = new Quaternion[n];
            var restPos = new Vector3[n];
            var restFound = new bool[n];
            if (TryBindPoseAnchors(restRot, restPos, restFound))
            {
                int got = 0;
                for (int i = 0; i < n; i++) if (restFound[i]) got++;
                if (got >= 20)
                {
                    restSource = $"bindposes({got}/{n})";
                    for (int i = 0; i < n; i++)
                        if (restFound[i]) { _anchorRot[i] = restRot[i]; _anchorPos[i] = restPos[i]; }
                }
            }

            _pelvisIndex = IndexOfTarget("pelvis");
            _footIndex = IndexOfTarget("foot_l");
            _legScale = ComputeLegScale();
            ComputeBasis();
            ConformArmAnchors();
            SetupIk(_ikL, "L");
            SetupIk(_ikR, "R");

            RigSummary = $"bones={n} cosmeticSkipped={cosmeticSkipped} rest={restSource} legScale={_legScale:F3} basis={Euler(_basis)} " +
                         $"pelvis={_pelvisIndex} footL={_footIndex}";
            _log.LogInfo($"[emote] bound model generation {_generation}: {RigSummary}");
            _log.LogInfo($"[emote:rig] two-bone IK L={IkSummary(_ikL)} R={IkSummary(_ikR)}");
            LogRestFrameArms();
            return true;
        }
        catch (Exception e)
        {
            _log.LogError($"[emote] bind threw: {e}");
            Unbind();
            return false;
        }
    }

    internal void Unbind()
    {
        try { Stop("unbind"); } catch (Exception e) { _log.LogWarning($"[emote] stop on unbind failed: {e.Message}"); }
        // The sampler owns a private scene hierarchy and a PlayableGraph; both are native objects
        // that must be destroyed even if the Stop above threw (its dispose is in a finally too).
        DisposeSampler("unbind");
        _nativeClip = null;
        // Teardown is immediate: the fade-out that Stop started may never tick (the transforms die
        // with the model), so hand the pelvis translation back before the skeleton references go.
        RestorePoseNow("unbind");
        // A release that failed keeps its key and its handler, so the next Stop/Dispose still owns
        // the obligation; only the bind-time references are cleared here.
        ReleaseLease("unbind");
        _order = null; _parent = null; _entryLocal = null; _entryLocalPos = null;
        _anchorRot = null; _anchorPos = null; _written = null; _writtenPos = null;
        _stopFrom = null; _stopFromPos = null; _posDirty = null; _byName.Clear();
        _person = null; _controller = null; _refs = null; _animator = null; _doingSomething = null;
        _clip = null; _map = null; _stopping = false; Active = false;
        _sampler = null; _nativeAllow = null; _nativeTouched = null; _nativeTime = 0f;
    }

    private void Walk(Transform root, Transform t, int parentIndex, List<Transform> all, List<int> parents, int depth, ref int cosmeticSkipped)
    {
        if (t == null || depth > 32) return;
        // A mod-owned cosmetic root (and therefore its whole subtree) is not part of the game's
        // skeleton. It hangs off a socket, is destroyed on unequip, and the emote never writes it,
        // so it must not enter the captured order: a dead transform in _order would make the entry
        // capture and the per-frame write loop throw, and no retry could rebuild the order (P2.1).
        if (CosmeticMarkers.IsCosmeticRoot(t))
        {
            cosmeticSkipped += CountSubtree(t, 0);
            return;
        }
        int me = all.Count;
        all.Add(t);
        parents.Add(parentIndex);
        if (!_byName.ContainsKey(t.name)) _byName[t.name] = me;
        int n = t.childCount;
        for (int i = 0; i < n; i++) Walk(root, t.GetChild(i), me, all, parents, depth + 1, ref cosmeticSkipped);
    }

    private static int CountSubtree(Transform t, int depth)
    {
        if (t == null || depth > 32) return 0;
        int n = 1;
        try { for (int i = 0; i < t.childCount; i++) n += CountSubtree(t.GetChild(i), depth + 1); }
        catch { return 1; }
        return n;
    }

    private int IndexOfTarget(string bone)
    {
        return _byName.TryGetValue(bone, out var i) ? i : -1;
    }

    private bool TryBindPoseAnchors(Quaternion[] rot, Vector3[] pos, bool[] found)
    {
        try
        {
            var renderers = _animator.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            if (renderers == null || renderers.Length == 0) { _log.LogWarning("[emote] no SkinnedMeshRenderer under the model"); return false; }
            int used = 0;
            for (int r = 0; r < renderers.Length; r++)
            {
                var smr = renderers[r].TryCast<SkinnedMeshRenderer>();
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                if (mesh == null || bones == null) continue;
                var bindposes = mesh.bindposes;
                if (bindposes == null || bindposes.Length != bones.Length) continue;
                Matrix4x4 l2w = smr.transform.localToWorldMatrix;
                for (int i = 0; i < bones.Length; i++)
                {
                    var b = bones[i];
                    if (b == null) continue;
                    int idx;
                    if (!_byName.TryGetValue(b.name, out idx)) continue;
                    if (_order[idx] != b) continue;
                    if (found[idx]) continue;
                    Matrix4x4 inv = Invert(bindposes[i]);
                    Matrix4x4 world = Multiply(l2w, inv);
                    Quaternion q = _rootRotInv * RotationOf(world);
                    Vector3 p = _rootRotInv * new Vector3(world.m03, world.m13, world.m23);
                    rot[idx] = q; pos[idx] = p; found[idx] = true;
                    used++;
                }
            }
            return used > 0;
        }
        catch (Exception e) { _log.LogWarning($"[emote] bind pose read failed: {e.Message}"); return false; }
    }

    private static Matrix4x4 Invert(Matrix4x4 m)
    {
        // affine inverse (rotation+translation only; rig bind poses have no scale on the bone)
        float a = m.m00, b = m.m01, c = m.m02;
        float d = m.m10, e = m.m11, f = m.m12;
        float g = m.m20, h = m.m21, i = m.m22;
        float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Mathf.Abs(det) < 1e-9f) return Matrix4x4.identity;
        float inv = 1f / det;
        var r = Matrix4x4.identity;
        r.m00 = (e * i - f * h) * inv; r.m01 = (c * h - b * i) * inv; r.m02 = (b * f - c * e) * inv;
        r.m10 = (f * g - d * i) * inv; r.m11 = (a * i - c * g) * inv; r.m12 = (c * d - a * f) * inv;
        r.m20 = (d * h - e * g) * inv; r.m21 = (b * g - a * h) * inv; r.m22 = (a * e - b * d) * inv;
        float tx = m.m03, ty = m.m13, tz = m.m23;
        r.m03 = -(r.m00 * tx + r.m01 * ty + r.m02 * tz);
        r.m13 = -(r.m10 * tx + r.m11 * ty + r.m12 * tz);
        r.m23 = -(r.m20 * tx + r.m21 * ty + r.m22 * tz);
        return r;
    }

    private static Matrix4x4 Multiply(Matrix4x4 a, Matrix4x4 b)
    {
        var r = Matrix4x4.identity;
        r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20;
        r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21;
        r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22;
        r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03;
        r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20;
        r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21;
        r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22;
        r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13;
        r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20;
        r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21;
        r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22;
        r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23;
        return r;
    }

    private static Quaternion RotationOf(Matrix4x4 m)
    {
        Vector3 c0 = new Vector3(m.m00, m.m10, m.m20).normalized;
        Vector3 c1 = new Vector3(m.m01, m.m11, m.m21).normalized;
        Vector3 c2 = Vector3.Cross(c0, c1);
        float trace = c0.x + c1.y + c2.z;
        if (trace > 0f)
        {
            float s = Mathf.Sqrt(trace + 1f) * 2f;
            return new Quaternion((c1.z - c2.y) / s, (c2.x - c0.z) / s, (c0.y - c1.x) / s, 0.25f * s);
        }
        if (c0.x > c1.y && c0.x > c2.z)
        {
            float s = Mathf.Sqrt(1f + c0.x - c1.y - c2.z) * 2f;
            return new Quaternion(0.25f * s, (c1.x + c0.y) / s, (c2.x + c0.z) / s, (c1.z - c2.y) / s);
        }
        if (c1.y > c2.z)
        {
            float s = Mathf.Sqrt(1f + c1.y - c0.x - c2.z) * 2f;
            return new Quaternion((c1.x + c0.y) / s, 0.25f * s, (c2.y + c1.z) / s, (c2.x - c0.z) / s);
        }
        float s2 = Mathf.Sqrt(1f + c2.z - c0.x - c1.y) * 2f;
        return new Quaternion((c2.x + c0.z) / s2, (c2.y + c1.z) / s2, 0.25f * s2, (c0.y - c1.x) / s2);
    }

    // Bind-time arm chain: bone indices, target-parent index, world-space bind lengths and the
    // source/target reach ratio used to scale the ported target offsets.
    private void SetupIk(IkChain c, string side)
    {
        try
        {
            string s = side.ToLowerInvariant();
            c.Upper = IndexOfTarget("upperarm_" + s);
            c.Lower = IndexOfTarget("lowerarm_" + s);
            c.Hand = IndexOfTarget("hand_" + s);
            c.Parent = IndexOfTarget("spine_03");
            if (!c.Valid) return;
            c.L1 = Vector3.Distance(_order[c.Upper].position, _order[c.Lower].position);
            c.L2 = Vector3.Distance(_order[c.Lower].position, _order[c.Hand].position);
            c.Reach = c.L1 + c.L2;
            string srcUpper = null, srcLower = null, srcHand = null;
            foreach (var kv in _pack.Map)
            {
                if (kv.Value == "upperarm_" + s) srcUpper = kv.Key;
                if (kv.Value == "lowerarm_" + s) srcLower = kv.Key;
                if (kv.Value == "hand_" + s) srcHand = kv.Key;
            }
            float srcReach = 0f;
            if (srcUpper != null && srcLower != null && srcHand != null &&
                _pack.SrcRestPos.TryGetValue(srcUpper, out var pu) &&
                _pack.SrcRestPos.TryGetValue(srcLower, out var pl) &&
                _pack.SrcRestPos.TryGetValue(srcHand, out var ph))
                srcReach = Vector3.Distance(pu, pl) + Vector3.Distance(pl, ph);
            if (srcReach > 0.05f) c.Scale = Mathf.Clamp(c.Reach / srcReach, 0.25f, 4f);
        }
        catch (Exception e) { _log.LogWarning($"[emote] ik setup {side} failed: {e.Message}"); }
    }

    private static void ResetIk(IkChain c)
    {
        c.Clamps = 0; c.LastWeight = 0f; c.LastDist = 0f; c.LastErr = 0f;
        c.LastTargetLocal = Vector3.zero; c.LastUpperLocal = Vector3.zero; c.LastHandLocal = Vector3.zero;
    }

    // Rest-frame arm diagnostics (measurement only, nothing is written to a bone).
    //
    // The rotation transfer is `global = C * dR * C^-1 * anchor` with `anchor` = this model's own
    // bind rotation, so a *global* source delta lands on the target's rest arm. When the two rest
    // arms point in different directions the absolute pose (and the swing arc, hence hand travel)
    // differs even though every delta is transferred exactly. LC's rest arms hang at the sides;
    // the target binds wider. Logging both rest chains in the frame they are compared in (the
    // model's own, source mapped by C) makes the bias measurable offline instead of guessed.
    private void LogRestFrameArms()
    {
        try
        {
            string[] chain = { "pelvis", "spine_01", "spine_02", "spine_03",
                               "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
                               "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r" };
            var posSb = new StringBuilder();
            var rotSb = new StringBuilder();
            foreach (var bn in chain)
            {
                int i = IndexOfTarget(bn);
                if (i < 0) continue;
                var p = _anchorPos[i]; var q = _anchorRot[i];
                posSb.Append($" {bn}=({p.x:F4},{p.y:F4},{p.z:F4})");
                rotSb.Append($" {bn}=({q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4})");
            }
            _log.LogInfo($"[emote:rig] rest chain pos:{posSb} rot:{rotSb}");
            _log.LogInfo($"[emote:rig] basis quat=({_basis.x:F5},{_basis.y:F5},{_basis.z:F5},{_basis.w:F5})");

            foreach (var s in new[] { "l", "r" })
            {
                string[] names = { "clavicle_" + s, "upperarm_" + s, "lowerarm_" + s, "hand_" + s };
                var idx = new int[4];
                bool ok = true;
                for (int k = 0; k < 4; k++) { idx[k] = IndexOfTarget(names[k]); if (idx[k] < 0) ok = false; }
                if (!ok) continue;
                var srcPos = new Vector3[4];
                bool sok = true;
                for (int k = 0; k < 4; k++)
                {
                    string sp = null;
                    foreach (var kv in _pack.Map) if (kv.Value == names[k]) { sp = kv.Key; break; }
                    Vector3 v;
                    if (sp == null || !_pack.SrcRestPos.TryGetValue(sp, out v)) { sok = false; break; }
                    srcPos[k] = _basis * v;                       // the frame the deltas use
                }
                var sb = new StringBuilder();
                for (int k = 0; k < 3; k++)
                {
                    var dt = (_anchorPos[idx[k + 1]] - _anchorPos[idx[k]]).normalized;
                    if (!sok)
                    {
                        sb.Append($" {names[k]}->{names[k + 1]} target=({dt.x:F2},{dt.y:F2},{dt.z:F2}) (no source rest)");
                        continue;
                    }
                    var ds = (srcPos[k + 1] - srcPos[k]).normalized;
                    sb.Append($" {names[k]}->{names[k + 1]} angle={Vector3.Angle(ds, dt):F1}deg" +
                              $" target=({dt.x:F2},{dt.y:F2},{dt.z:F2}) source=({ds.x:F2},{ds.y:F2},{ds.z:F2})");
                }
                _log.LogInfo($"[emote:rig] arm rest {s.ToUpperInvariant()}:{sb}");
            }
        }
        catch (Exception e) { _log.LogWarning($"[emote] rest-frame arm log failed: {e.Message}"); }
    }

    // Rest-frame correction for the arm chain: the actual fix for the rotation-retarget bias.
    //
    // LC's clips carry *global* rotation deltas, so a delta lands on this model's own rest arm:
    //   dir_i(t) = C * dR_i(t) * C^-1 * (bindPos_child - bindPos_i)_dir
    // LC's own arm segment direction is dR_i(t) * (srcRest_child - srcRest_i)_dir. The two agree at
    // every frame only when the two *rest* directions agree after C, and they do not: LC's rest
    // arms hang at the sides while this model binds wider (20-24 deg at the shoulder, 25-39 deg at
    // the elbow, 49-72 deg at the wrist - measured, see the `arm rest` log line). Pre-multiplying
    // each arm bone's anchor by `Q_i = FromTo(bind segment dir, C * source rest segment dir)` makes
    // the segment direction `C * dR_i(t) * (source rest dir)` = exactly LC's, at every frame, for
    // free: it is one constant quaternion per bone computed from the two rest poses, it does not
    // touch the pack, and zero-weight clips are unaffected in kind.
    //
    // The hand has no child segment in the chain, so it cannot be fixed by a swing; it takes LC's
    // rest orientation outright instead (no clip animates the hand, so that is LC's hand pose in
    // every clip, and it is what puts the fingers in LC's plane).
    //
    // The clavicle is deliberately NOT conformed: `spine_03 + C*(target offset)` already places the
    // arm root, and this model's clavicle->upperarm link is shorter than LC's (~0.17 m, the same
    // 0.81 arm-length ratio), so conforming its direction pulled the shoulder toward the IK targets
    // and made the IK-driven clips' reach utilisation 0.10 worse on Clap/Twerk while changing the
    // rotation-driven handLR by <1 cm. Measured, not assumed - see the numbers in emotes-port.md.
    private void ConformArmAnchors()
    {
        try
        {
            var summary = new StringBuilder();
            foreach (var s in new[] { "l", "r" })
            {
                string[] names = { "clavicle_" + s, "upperarm_" + s, "lowerarm_" + s, "hand_" + s };
                var idx = new int[4];
                var srcPos = new Vector3[4];
                var srcRot = new Quaternion[4];
                var havePos = new bool[4];
                var haveRot = new bool[4];
                for (int k = 0; k < 4; k++)
                {
                    idx[k] = IndexOfTarget(names[k]);
                    if (idx[k] < 0) continue;
                    string sp = SourcePathOf(names[k]);
                    if (sp == null) continue;
                    if (_pack.SrcRestPos.TryGetValue(sp, out var p)) { srcPos[k] = _basis * p; havePos[k] = true; }
                    if (_pack.SrcRestRot.TryGetValue(sp, out var q)) { srcRot[k] = _basis * q; haveRot[k] = true; }
                }
                for (int k = 1; k < 3; k++)              // upperarm, lowerarm (clavicle excluded, see above)
                {
                    if (idx[k] < 0 || idx[k + 1] < 0 || !havePos[k] || !havePos[k + 1]) continue;
                    var dt = _anchorPos[idx[k + 1]] - _anchorPos[idx[k]];
                    var ds = srcPos[k + 1] - srcPos[k];
                    if (dt.sqrMagnitude < 1e-8f || ds.sqrMagnitude < 1e-8f) continue;
                    var q = Quaternion.FromToRotation(dt.normalized, ds.normalized);
                    _anchorRot[idx[k]] = q * _anchorRot[idx[k]];
                    summary.Append($" {names[k]}={Quaternion.Angle(Quaternion.identity, q):F1}deg");
                }
                if (idx[3] >= 0 && haveRot[3])
                {
                    float a = Quaternion.Angle(_anchorRot[idx[3]], srcRot[3]);
                    _anchorRot[idx[3]] = srcRot[3];
                    summary.Append($" {names[3]}=rest({a:F1}deg)");
                }
            }
            _log.LogInfo($"[emote:rig] arm rest-frame conform (bind):{summary}");
        }
        catch (Exception e) { _log.LogWarning($"[emote] arm conform failed: {e.Message}"); }
    }

    // target bone -> source bone path (from the pack's map); null when the bone is not mapped
    private string SourcePathOf(string targetBone)
    {
        foreach (var kv in _pack.Map)
            if (string.Equals(kv.Value, targetBone, StringComparison.Ordinal)) return kv.Key;
        return null;
    }

    private string IkSummary(IkChain c)
    {
        if (!c.Valid) return "unavailable";
        return $"upper='{_order[c.Upper].name}' lower='{_order[c.Lower].name}' hand='{_order[c.Hand].name}' " +
               $"parent='{_order[c.Parent].name}' L1={c.L1:F3} L2={c.L2:F3} reach={c.Reach:F3} scale={c.Scale:F3}";
    }

    // IK target diagnostics for the metrics line; empty when the clip drives no targets.
    internal string IkReport()
    {
        if (_clip == null || !_clip.HasIk) return "";
        return $" ik[L:dist={_ikL.LastDist:F3} reach={_ikL.Reach:F3} err={_ikL.LastErr:F3} w={_ikL.LastWeight:F2} " +
               $"clamps={_ikL.Clamps} ul={_ikL.LastUpperLocal} tl={_ikL.LastTargetLocal} hl={_ikL.LastHandLocal} " +
               $"R:dist={_ikR.LastDist:F3} reach={_ikR.Reach:F3} err={_ikR.LastErr:F3} " +
               $"w={_ikR.LastWeight:F2} clamps={_ikR.Clamps} ur={_ikR.LastUpperLocal} tr={_ikR.LastTargetLocal} " +
               $"hr={_ikR.LastHandLocal}]";
    }

    private float ComputeLegScale()
    {
        try
        {
            if (_pelvisIndex < 0 || _footIndex < 0) return 1f;
            string srcPelvis = null, srcFoot = null;
            foreach (var kv in _pack.Map)
            {
                if (kv.Value == "pelvis") srcPelvis = kv.Key;
                if (kv.Value == "foot_l") srcFoot = kv.Key;
            }
            if (srcPelvis == null || srcFoot == null) return 1f;
            if (!_pack.SrcRestPos.TryGetValue(srcPelvis, out var sp)) return 1f;
            if (!_pack.SrcRestPos.TryGetValue(srcFoot, out var sf)) return 1f;
            float src = Vector3.Distance(sp, sf);
            float tgt = Vector3.Distance(_anchorPos[_pelvisIndex], _anchorPos[_footIndex]);
            if (src < 0.05f || tgt < 0.05f) return 1f;
            return Mathf.Clamp(tgt / src, 0.25f, 4f);
        }
        catch { return 1f; }
    }

    // Source-model -> target-model basis from the reference skeletons (up / left-right / forward),
    // never from an animated frame.
    private void ComputeBasis()
    {
        _basis = Quaternion.identity;
        try
        {
            string sPelvis = null, sChest = null, sThighL = null, sThighR = null;
            foreach (var kv in _pack.Map)
            {
                if (kv.Value == "pelvis") sPelvis = kv.Key;
                if (kv.Value == "neck_01") sChest = kv.Key;
                if (kv.Value == "thigh_l") sThighL = kv.Key;
                if (kv.Value == "thigh_r") sThighR = kv.Key;
            }
            if (sPelvis == null || sChest == null || sThighL == null || sThighR == null) return;
            var srcUp = (_pack.SrcRestPos[sChest] - _pack.SrcRestPos[sPelvis]).normalized;
            var srcRight = (_pack.SrcRestPos[sThighR] - _pack.SrcRestPos[sThighL]).normalized;

            int tPelvis = IndexOfTarget("pelvis"), tChest = IndexOfTarget("neck_01");
            int tThighL = IndexOfTarget("thigh_l"), tThighR = IndexOfTarget("thigh_r");
            if (tPelvis < 0 || tChest < 0 || tThighL < 0 || tThighR < 0) return;
            var tgtUp = (_anchorPos[tChest] - _anchorPos[tPelvis]).normalized;
            var tgtRight = (_anchorPos[tThighR] - _anchorPos[tThighL]).normalized;
            if (srcUp.sqrMagnitude < 0.5f || tgtUp.sqrMagnitude < 0.5f) return;

            var srcFwd = Vector3.Cross(srcRight, srcUp);
            var tgtFwd = Vector3.Cross(tgtRight, tgtUp);
            _basis = FrameRotation(srcRight, srcUp, srcFwd, tgtRight, tgtUp, tgtFwd);
            _basisInv = Quaternion.Inverse(_basis);
            _log.LogInfo($"[emote:rig] basis source(up={srcUp} right={srcRight}) target(up={tgtUp} right={tgtRight}) " +
                         $"-> C euler={Euler(_basis)} angle={Quaternion.Angle(Quaternion.identity, _basis):F1}deg");
        }
        catch (Exception e) { _log.LogWarning($"[emote] basis failed: {e.Message}"); }
    }

    private static Quaternion FrameRotation(Vector3 sr, Vector3 su, Vector3 sf, Vector3 tr, Vector3 tu, Vector3 tf)
    {
        // M * s_axis = t_axis for the three basis vectors; C = Mt * Ms^T
        var ms = Matrix4x4.identity;
        ms.m00 = sr.x; ms.m10 = sr.y; ms.m20 = sr.z;
        ms.m01 = su.x; ms.m11 = su.y; ms.m21 = su.z;
        ms.m02 = sf.x; ms.m12 = sf.y; ms.m22 = sf.z;
        var mt = Matrix4x4.identity;
        mt.m00 = tr.x; mt.m10 = tr.y; mt.m20 = tr.z;
        mt.m01 = tu.x; mt.m11 = tu.y; mt.m21 = tu.z;
        mt.m02 = tf.x; mt.m12 = tf.y; mt.m22 = tf.z;
        var mst = Matrix4x4.identity;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                float s = 0f;
                for (int k = 0; k < 3; k++) s += mt[r, k] * ms[c, k];  // Mt * Ms^T
                mst[r, c] = s;
            }
        return RotationOf(mst);
    }

    // ------------------------------------------------------------------ play

    internal bool Start(string clipName)
    {
        var log = _log;
        try
        {
            if (_order == null) { log.LogWarning("[emote] start: not bound"); return false; }
            // Fail closed on the allowlist, before the pack is even asked: a removed clip keeps its
            // bytes in the blob, but there is no playback path to it.
            if (!EmoteCatalog.Contains(clipName))
            {
                log.LogWarning($"[emote] start: clip '{clipName}' is not in the emote allowlist " +
                               $"({EmoteCatalog.List()}); nothing started");
                return false;
            }
            var clip = _pack.ByName(clipName);
            if (clip == null) { log.LogWarning($"[emote] start: clip '{clipName}' not in pack"); return false; }
            if (!_controller.IsAnimancerAndAnimatorEnabled)
            {
                log.LogWarning("[emote] start: animancer/animator already disabled by the game - not fighting for the bones");
                return false;
            }

            // Hand any previous run's pelvis offset back first: the entry pose must be the pose the
            // game actually had, never a leftover emote offset captured as the new baseline.
            RestorePoseNow("pre-start");

            // capture the pose we hand over from
            for (int i = 0; i < _order.Length; i++)
            {
                _entryLocal[i] = _order[i].localRotation;
                _entryLocalPos[i] = _order[i].localPosition;
                _written[i] = _entryLocal[i];
                _writtenPos[i] = _entryLocalPos[i];
                _posDirty[i] = false;
            }
            _writeFrames = 0;
            _posTraceLines = 0;
            _entryTraceDone = false;
            // A native run must not survive a switch back to a ported clip.
            DisposeSampler("start-lc");
            _nativeClip = null;

            _map = new int[_order.Length];
            for (int i = 0; i < _map.Length; i++) _map[i] = -1;
            int mapped = 0, missing = 0;
            foreach (var kv in _pack.Map)
            {
                int ci = clip.IndexOf(kv.Key);
                if (ci < 0) continue;                        // bone not animated by this clip
                int ti = IndexOfTarget(kv.Value);
                if (ti < 0) { missing++; continue; }
                _map[ti] = ci;
                mapped++;
            }
            _clip = clip;
            CurrentClip = clip.Name;
            ResetIk(_ikL); ResetIk(_ikR);
            _stateTime = 0f;
            _fadeIn = FadeInSeconds;
            _stopping = false;
            _lateWriterReports = 0;

            // Fail closed: nothing is played and no bone is written unless the whole lease is in
            // place. A failed acquisition unwinds exactly the steps that succeeded.
            if (!AcquireLease())
            {
                _clip = null; CurrentClip = ""; Active = false;
                return false;
            }

            Active = true;
            log.LogInfo($"[emote] START clip='{clip.Name}' (Lethal Company port) gen={_generation} " +
                        $"mapped={mapped}/{_pack.Map.Count} targetMissing={missing} dur={clip.Duration:F3}s " +
                        $"loop={clip.Loop} frames={clip.Frames} suspended=True lease=DoingSomething+DisableAnimancerAndAnimator");
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[emote] start threw: {e}");
            Active = false; _clip = null; CurrentClip = "";
            ReleaseLease("start-threw");
            RestorePoseNow("start-threw");
            return false;
        }
    }

    internal void Stop(string reason)
    {
        var log = _log;
        try
        {
            if (Active)
            {
                if (_order != null && _order.Length > 0 && _written != null)
                {
                    _stopFrom = new Quaternion[_order.Length];
                    for (int i = 0; i < _order.Length; i++) _stopFrom[i] = _written[i];
                    if (_writtenPos != null && _posDirty != null)
                    {
                        _stopFromPos = new Vector3[_order.Length];
                        for (int i = 0; i < _order.Length; i++) _stopFromPos[i] = _writtenPos[i];
                    }
                }
                _fadeOut = FadeOutSeconds;
                _stopping = true;
                Active = false;
                log.LogInfo($"[emote] STOP reason={reason} clip='{CurrentClip}' t={(_nativeClip != null ? _nativeTime : _stateTime):F2}s released=" +
                            $"{(_lease != null)}" + $" fadeOut={FadeOutSeconds:F2}s" +
                            (_nativeClip != null ? " native=1" : "") +
                            (_nativeClip != null && _nativeAllow != null
                                ? $" maskedAllowed={NativeAllowedBones}/{_order.Length} bodyChannelsChanged={NativeBodyChannelsChanged} bodyMaxDev={NativeBodyMaxDeviationDeg:F3}deg"
                                : ""));
            }
        }
        catch (Exception e) { log.LogError($"[emote] stop threw: {e}"); }
        finally
        {
            // The sampled pose is not needed once the hand-back starts: the fade-out blends
            // _stopFrom toward the live game pose, which needs no clip evaluation.
            DisposeSampler("stop");
            _nativeClip = null;
            // Hand the bones back immediately so the game's own animation resumes (the fade-out
            // writes on top of the freshly evaluated pose for a moment). The release is in a
            // finally so no bookkeeping failure can leave the lease installed.
            ReleaseLease(reason);
        }
    }

    // ------------------------------------------------------------------ game (native) clips

    // Start one of the game's own clips. CLOSED AT THE ENTRY POINT: this build offers zero native ids
    // (EmoteCatalog.NativeCount) and the native boundary is enforced here, not only at the callers -
    // a native start samples a game clip and takes the pose lease, which is exactly what must not be
    // reachable. Every call is refused with the attempt named; nothing is started, no lease is
    // acquired. The sampler/lease implementation is kept (StartNativeSampled) for the record but has
    // no caller.
    internal bool StartNative(AnimationClip clip, string id, AvatarMask layerMask)
    {
        _log?.LogWarning($"[emote] native start REFUSED at the entry point: id='{id}' clip='{(clip != null ? clip.name : "null")}' " +
                         $"mask={(layerMask != null ? layerMask.name : "null")} - native clip playback is closed in " +
                         $"this build (EmoteCatalog.NativeCount=0, allowlist {EmoteCatalog.List()}); nothing started, " +
                         "no pose lease taken");
        return false;
    }

    // The retained native sampler. Not reachable from any entry point (see StartNative above); kept so
    // the measured native behaviour and the mask maths stay readable in the hot source.
    private bool StartNativeSampled(AnimationClip clip, string id, AvatarMask layerMask)
    {
        var log = _log;
        try
        {
            if (!EmoteCatalog.Contains(id))
            {
                log.LogWarning($"[emote] native start '{id}' refused: native emote ids are not in the " +
                               $"allowlist ({EmoteCatalog.List()}); nothing started");
                return false;
            }
            if (_order == null) { log.LogWarning("[emote] native start: not bound"); return false; }
            if (clip == null) { log.LogWarning($"[emote] native start '{id}': clip is null"); return false; }
            if (Active || _lease != null) Stop("pre-start-native");
            if (!_controller.IsAnimancerAndAnimatorEnabled)
            {
                log.LogWarning("[emote] native start: animancer/animator already disabled by the game - not fighting for the bones");
                return false;
            }

            RestorePoseNow("pre-start");
            for (int i = 0; i < _order.Length; i++)
            {
                _entryLocal[i] = _order[i].localRotation;
                _entryLocalPos[i] = _order[i].localPosition;
                _written[i] = _entryLocal[i];
                _writtenPos[i] = _entryLocalPos[i];
                _posDirty[i] = false;
            }
            _writeFrames = 0;
            _posTraceLines = 0;
            _entryTraceDone = false;
            _map = null;
            _clip = null;

            var sampler = new NativeSampler();
            if (!sampler.Bind(_log, _animator, _order, _parent, _entryLocal, _entryLocalPos, clip))
            {
                sampler.Dispose();
                CurrentClip = "";
                log.LogWarning($"[emote] native start '{id}': sampler could not bind; nothing started");
                return false;
            }

            _nativeAllow = ComputeBodyPartMask(layerMask, out string maskSummary);
            _nativeTouched = new bool[_order.Length];
            NativeBodyChannelsChanged = 0;
            NativeBodyMaxDeviationDeg = 0f;
            NativeAllowedBones = 0;
            for (int i = 0; i < _nativeAllow.Length; i++) if (_nativeAllow[i]) NativeAllowedBones++;
            NativeMaskSummary = maskSummary;
            CurrentClip = id;
            _nativeClip = clip;
            _sampler = sampler;
            _nativeTime = 0f;
            _nativeWriteFrames = 0;
            _stateTime = 0f;
            _fadeIn = FadeInSeconds;
            _stopping = false;
            _lateWriterReports = 0;

            if (!AcquireLease())
            {
                _nativeClip = null; CurrentClip = ""; Active = false;
                DisposeSampler("acquire-failed");
                return false;
            }

            Active = true;
            log.LogInfo($"[emote] START clip='{id}' (game clip) gen={_generation} len={clip.length:F3}s " +
                        $"loop={(clip.isLooping ? 1 : 0)} bones={sampler.Bones} rotDriven={sampler.DrivenRotCount} " +
                        $"posDriven={sampler.DrivenPosCount} sampler={sampler.Mode} mask=[{maskSummary}] " +
                        $"maskedAllowed={NativeAllowedBones}/{_order.Length} suspended=True " +
                        "lease=DoingSomething+DisableAnimancerAndAnimator");
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[emote] native start threw: {e}");
            Active = false; _clip = null; _nativeClip = null; CurrentClip = "";
            DisposeSampler("start-threw");
            ReleaseLease("start-threw");
            RestorePoseNow("start-threw");
            return false;
        }
    }

    // Start an already-running clip at the position it would have reached by now. Used when a
    // remote emote start reaches the observer late (relay/lease latency) or when the observer joins
    // an emote that is already in progress: playing from frame zero would be visibly out of phase
    // with the wearer. Looping clips wrap, one-shots clamp just short of the end (a late arrival
    // must not instantly trip the end-of-clip Stop). No fade: the pose is written as the clip has
    // it, blending in from the entry pose is exactly the frame-zero artefact being avoided.
    internal bool Seek(float seconds)
    {
        try
        {
            if (!Active) return false;
            float dur = Duration;
            if (dur <= 0.02f) return false;
            bool loop = _nativeClip != null ? _nativeClip.isLooping : (_clip != null && _clip.Loop);
            float t;
            if (loop) t = Mathf.Repeat(seconds, dur);
            else t = Mathf.Clamp(seconds, 0f, dur - Mathf.Min(0.02f, dur * 0.01f));
            if (t <= 0.02f) return false;
            if (_nativeClip != null) _nativeTime = t; else _stateTime = t;
            _fadeIn = 0f;
            _lateWriterReports = 3;      // a seek is a jump; the late-writer guard must not report it
            _seekPending = true;
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning($"[emote] seek failed: {e.Message}");
            return false;
        }
    }

    private void DisposeSampler(string reason)
    {
        var s = _sampler;
        _sampler = null;
        if (s == null) return;
        try { s.Dispose(); } catch (Exception e) { _log.LogWarning($"[emote:native] sampler dispose ({reason}) failed: {e.Message}"); }
        _log.LogInfo($"[emote:native] sampler disposed reason={reason} writes={_nativeWriteFrames}");
    }

    // Map the avatar's human bones to AvatarMask body parts, then read the layer's mask. An
    // upper-body (hands-layer) clip is written only on the parts the game's own mask enables, so
    // the torso/legs keep the pose the player had: layered, not replaced.
    private bool[] ComputeBodyPartMask(AvatarMask mask, out string summary)
    {
        var allow = new bool[_order.Length];
        if (mask == null)
        {
            for (int i = 0; i < allow.Length; i++) allow[i] = true;
            summary = "no layer mask (full body)";
            return allow;
        }
        var partByBone = new Dictionary<string, AvatarMaskBodyPart>(StringComparer.Ordinal);
        try
        {
            var hd = _animator.avatar.humanDescription;
            var human = hd.human;
            for (int i = 0; i < human.Length; i++)
            {
                var hb = human[i];
                var part = BodyPartOf(hb.humanName);
                if (part >= 0) partByBone[hb.boneName] = part;
            }
        }
        catch (Exception e) { _log.LogWarning($"[emote:native] human description unreadable: {e.Message}"); }

        if (partByBone.Count == 0)
        {
            for (int i = 0; i < allow.Length; i++) allow[i] = true;
            summary = $"mask='{mask.name}' UNMAPPED (avatar human description gave no bones); full body";
            _log.LogWarning("[emote:native] body-part mask could not be mapped; falling back to full body");
            return allow;
        }
        int allowed = 0, mapped = 0;
        var parts = new HashSet<AvatarMaskBodyPart>();
        for (int i = 0; i < _order.Length; i++)
        {
            if (!partByBone.TryGetValue(_order[i].name, out var part)) continue;
            mapped++;
            bool on = false;
            try { on = mask.GetHumanoidBodyPartActive(part); } catch { }
            allow[i] = on;
            if (on) { allowed++; parts.Add(part); }
        }
        var sb = new StringBuilder();
        foreach (var p in parts) { if (sb.Length > 0) sb.Append(','); sb.Append(p.ToString()); }
        summary = $"mask='{mask.name}' parts={sb} bones={allowed}/{mapped} mapped";
        return allow;
    }

    private static AvatarMaskBodyPart BodyPartOf(string humanName)
    {
        switch (humanName)
        {
            case "LeftShoulder": case "LeftUpperArm": case "LeftLowerArm": case "LeftHand": return AvatarMaskBodyPart.LeftArm;
            case "RightShoulder": case "RightUpperArm": case "RightLowerArm": case "RightHand": return AvatarMaskBodyPart.RightArm;
            case "Hips": case "Spine": case "Chest": case "UpperChest": return AvatarMaskBodyPart.Body;
            case "Neck": case "Head": case "Jaw": case "LeftEye": case "RightEye": return AvatarMaskBodyPart.Head;
            case "LeftUpperLeg": case "LeftLowerLeg": case "LeftFoot": case "LeftToes": return AvatarMaskBodyPart.LeftLeg;
            case "RightUpperLeg": case "RightLowerLeg": case "RightFoot": case "RightToes": return AvatarMaskBodyPart.RightLeg;
            case "Root": return AvatarMaskBodyPart.Root;
        }
        if (humanName != null && humanName.StartsWith("Left", StringComparison.Ordinal)) return AvatarMaskBodyPart.LeftFingers;
        if (humanName != null && humanName.StartsWith("Right", StringComparison.Ordinal)) return AvatarMaskBodyPart.RightFingers;
        return (AvatarMaskBodyPart)(-1);
    }

    // Read-only probe used by the census: how many of this rig's bones does the clip actually
    // drive, and which evaluation path worked. Never takes the lease and never writes the rig.
    internal string ProbeNativeClip(AnimationClip clip)
    {
        if (_order == null) return "unbound";
        var s = new NativeSampler();
        try
        {
            if (!s.Bind(_log, _animator, _order, _parent, _entryLocal, _entryLocalPos, clip)) return "bind-failed";
            return $"rotDriven={s.DrivenRotCount}/{s.Bones} posDriven={s.DrivenPosCount} mode={s.Mode} " +
                   $"driven=[{s.DrivenNames(_order)}]";
        }
        catch (Exception e) { return "probe-failed:" + e.Message; }
        finally { s.Dispose(); }
    }

    // One frame of a game clip: sample it on the scratch rig, then write the faded pose here.
    //
    // The write rule for a masked (upper-body) clip is stricter than the ported-LC path: a bone the
    // game's own mask does not allow is *not assigned at all*. That is what makes "upper-body clips
    // write zero pelvis/spine/leg channels" a property of the code rather than of the numbers, and
    // the guard below measures it anyway (merged build: other post-animation writers exist, so it
    // is re-checked per run instead of assumed from the mask).
    private void NativeApply(float dt)
    {
        var clip = _nativeClip;
        var sampler = _sampler;
        if (clip == null || sampler == null || !sampler.Ready)
        {
            AbortPlayback("native-sampler-lost", new InvalidOperationException("sampler not ready"));
            return;
        }
        float dur = Mathf.Max(clip.length, 0.02f);
        _nativeTime += dt;
        if (_nativeTime >= dur)
        {
            if (clip.isLooping) _nativeTime -= dur;
            else { Stop($"end({CurrentClip})"); return; }
        }

        float fade = _fadeIn > 0f ? 1f - Mathf.Clamp01(_fadeIn / FadeInSeconds) : 1f;
        if (_fadeIn > 0f) _fadeIn = Mathf.Max(0f, _fadeIn - dt);

        CheckLateWriter();
        sampler.SampleAt(_nativeTime);

        int n = _order.Length;
        int wrote = 0, positions = 0, allowedCount = 0;
        for (int i = 0; i < n; i++)
        {
            // The model root itself is never driven: a clip's root curve would move the visual
            // body away from the character capsule. Pelvis translation is driven normally.
            bool allowed = _nativeAllow != null && i < _nativeAllow.Length && _nativeAllow[i];
            if (!allowed)
            {
                // Not our channel in this clip: leave the transform exactly as the game left it and
                // only record the guard observation (a value change here is a regression).
                WatchBodyChannel(i);
                continue;
            }
            allowedCount++;
            bool driven = i > 0 && sampler.RotDriven[i];
            Quaternion local = driven ? Quaternion.Slerp(_entryLocal[i], sampler.Rot[i], fade) : _entryLocal[i];
            _order[i].localRotation = local;
            _written[i] = local;
            if (driven) wrote++;

            if (i > 0 && sampler.PosDriven[i])
            {
                var np = Vector3.Lerp(_entryLocalPos[i], sampler.Pos[i], fade);
                _order[i].localPosition = np;
                _writtenPos[i] = np;
                _posDirty[i] = true;
                positions++;
            }
        }
        _nativeWriteFrames++;
        _writeFrames++;   // teardown evidence counts native runs too
        if ((_nativeWriteFrames % 30) == 0)
            _log.LogInfo($"[emote:native] t={_nativeTime:F3}/{dur:F3} fade={fade:F2} wrote={wrote} posBones={positions} " +
                         $"allowed={allowedCount}/{n} bodyChannelsChanged={NativeBodyChannelsChanged} " +
                         $"bodyMaxDev={NativeBodyMaxDeviationDeg:F3}deg frame={Time.frameCount}");
    }

    // Guard for the masked path: a bone the layer mask disables must keep its entry value. Counted
    // once per bone per run, with the worst deviation, so "zero body channels written" is reported
    // from the same run that produced the pose rather than inferred from the mask.
    private void WatchBodyChannel(int i)
    {
        if (_nativeTouched == null || i < 0 || i >= _nativeTouched.Length) return;
        float deg = 0f, pos = 0f;
        try
        {
            deg = Quaternion.Angle(_entryLocal[i], _order[i].localRotation);
            pos = (_entryLocalPos[i] - _order[i].localPosition).magnitude;
        }
        catch { return; }
        if (deg <= 0.01f && pos <= 1e-5f) return;
        if (!_nativeTouched[i]) { _nativeTouched[i] = true; NativeBodyChannelsChanged++; }
        if (deg > NativeBodyMaxDeviationDeg) NativeBodyMaxDeviationDeg = deg;
    }

    // Transactional acquisition: every step is recorded independently and any failure unwinds
    // exactly what was acquired. Returning false means: no ownership, no playback, no bone writes.
    private bool AcquireLease()
    {
        bool hadLease = _lease != null;
        _leaseDoingSomething = _doingSomething;
        _leaseController = _controller;
        _lease = new Il2CppSystem.Object();
        _obligationDoingSomething = false;
        _obligationAnimancer = false;
        _releasedDoingSomething = false;
        _releasedAnimancer = false;

        if (_leaseDoingSomething == null)
        {
            _log.LogError("[emote] EMOTE_OWNERSHIP_FAILED the model has no DoingSomethingHandler; not taking the bones");
            _lease = null;
            return false;
        }

        // The release obligation is recorded before the call: if it throws midway the native state
        // may already hold our key, so the key is kept for a later retry.
        _obligationDoingSomething = true;
        try { _leaseDoingSomething.StartDoingSomething(_lease); }
        catch (Exception e)
        {
            _log.LogError($"[emote] EMOTE_OWNERSHIP_FAILED StartDoingSomething threw: {e.Message}; not taking the bones");
            ReleaseLease("acquire-doing-something");
            return false;
        }

        _obligationAnimancer = true;
        try { _controller.DisableAnimancerAndAnimator(_lease); }
        catch (Exception e)
        {
            _log.LogError($"[emote] EMOTE_OWNERSHIP_FAILED DisableAnimancerAndAnimator threw: {e.Message}; releasing");
            ReleaseLease("acquire-disable");
            return false;
        }

        if (_controller.IsAnimancerAndAnimatorEnabled)
        {
            _log.LogError("[emote] EMOTE_OWNERSHIP_FAILED DisableAnimancerAndAnimator did not take effect; releasing");
            ReleaseLease("acquire-not-suspended");
            return false;
        }
        if (!hadLease) LeasesHeld++;
        return true;
    }

    // Each release obligation stays recorded until its call has actually completed. A throw keeps
    // the key and the handler, so a later Stop/Dispose retries instead of leaking the keyed state.
    private void ReleaseLease(string reason)
    {
        if (_lease == null) return;

        bool doingDone = !_obligationDoingSomething || _releasedDoingSomething;
        if (!doingDone)
        {
            try
            {
                _leaseDoingSomething?.StopDoingSomething(_lease);
                _releasedDoingSomething = true;
                doingDone = true;
            }
            catch (Exception e)
            {
                _log.LogError($"[emote] EMOTE_RELEASE_FAILED StopDoingSomething ({reason}): {e.Message}; key retained for a retry");
            }
        }

        bool animDone = !_obligationAnimancer || _releasedAnimancer;
        if (!animDone)
        {
            try
            {
                _leaseController?.EnabledAnimancerAndAnimator(_lease);
                _releasedAnimancer = true;
                animDone = true;
            }
            catch (Exception e)
            {
                _log.LogError($"[emote] EMOTE_RELEASE_FAILED EnabledAnimancerAndAnimator ({reason}): {e.Message}; key retained for a retry");
            }
        }

        if (!doingDone || !animDone)
        {
            _log.LogWarning($"[emote] lease release incomplete ({reason}); the key stays held and is retried on the next Stop/Dispose");
            return;
        }

        _lease = null;
        _leaseDoingSomething = null;
        _leaseController = null;
        _obligationDoingSomething = false;
        _obligationAnimancer = false;
        _releasedDoingSomething = false;
        _releasedAnimancer = false;
        LeasesHeld = Math.Max(0, LeasesHeld - 1);
    }

    // Fail-closed abort used when a write throws: no fade (writes are failing), no more playback,
    // pose handed back and the lease released. A looping clip must never keep writing on errors.
    private void AbortPlayback(string stage, Exception e)
    {
        _log.LogError($"[emote] EMOTE_WRITE_FAILED {stage} clip='{CurrentClip}' t={(_nativeClip != null ? _nativeTime : _stateTime):F3}s: {e}");
        Active = false;
        _stopping = false;
        _fadeOut = 0f;
        _stopFrom = null;
        _stopFromPos = null;
        _clip = null;
        _nativeClip = null;
        DisposeSampler($"write-failed:{stage}");
        RestorePoseNow($"write-failed:{stage}");
        ReleaseLease($"write-failed:{stage}");
    }

    // ------------------------------------------------------------------ per frame

    // Called from the post-animation callback: the pose written here must survive to rendering.
    internal void PostAnimationUpdate(float dt)
    {
        if (_stopping)
        {
            try { TickFadeOut(dt); }
            catch (Exception e) { AbortPlayback("fade-out", e); }
            return;
        }
        if (!Active || _order == null) return;
        if (_seekPending)
        {
            _seekPending = false;
            _log.LogInfo($"[emote] seek applied clip='{CurrentClip}' t={Elapsed:F3}s dur={Duration:F3}s " +
                         "native=" + (_nativeClip != null ? "1" : "0") + " (late arrival: no frame-zero restart)");
        }
        // A game clip takes exactly this path too: same phase, same fade, same lease. It only
        // differs in where the target pose comes from (a sampled scratch rig instead of the pack).
        if (_nativeClip != null)
        {
            try { NativeApply(dt); }
            catch (Exception e) { AbortPlayback("native-apply", e); }
            return;
        }
        if (_clip == null) return;

        _stateTime += dt;
        float dur = Mathf.Max(_clip.Duration, 0.05f);
        if (_stateTime >= dur)
        {
            if (_clip.Loop) _stateTime -= dur;
            else { Stop($"end({_clip.Name})"); return; }
        }

        CheckLateWriter();
        try { Apply(_stateTime, dt); }
        catch (Exception e) { AbortPlayback("apply", e); }
    }

    private void CheckLateWriter()
    {
        if (_lateWriterReports >= 3) return;
        bool native = _nativeClip != null && _sampler != null;
        for (int i = 0; i < _order.Length; i++)
        {
            bool watched = native
                ? (_nativeAllow != null && i < _nativeAllow.Length && _nativeAllow[i] &&
                   _sampler.RotDriven != null && i < _sampler.RotDriven.Length && _sampler.RotDriven[i])
                : (_map != null && _map[i] >= 0);
            if (!watched) continue;
            float d = Quaternion.Angle(_written[i], _order[i].localRotation);
            if (d > 1.0f)
            {
                _lateWriterReports++;
                _log.LogWarning($"[emote] EMOTE_LATE_WRITER bone='{_order[i].name}' delta={d:F2}deg " +
                                $"at t={(_nativeClip != null ? _nativeTime : _stateTime):F2} (something wrote the bone after our last write)");
                return;
            }
        }
    }

    private void Apply(float time, float dt)
    {
        var clip = _clip;
        int nb = clip.Bones.Length;
        float fp = time * clip.Fps;
        int f0 = Mathf.FloorToInt(fp);
        float frac = fp - f0;
        if (clip.Loop)
        {
            int last = Mathf.Max(clip.Frames - 1, 1);
            if (f0 >= last) { f0 %= last; frac = 0f; }
        }
        else if (f0 >= clip.Frames - 1) { f0 = clip.Frames - 1; frac = 0f; }
        int f1 = clip.Loop ? (f0 + 1) % Mathf.Max(clip.Frames - 1, 1) : Mathf.Min(f0 + 1, clip.Frames - 1);
        if (clip.Loop && f0 == clip.Frames - 1) f0 = 0;

        float fade = _fadeIn > 0f ? 1f - Mathf.Clamp01(_fadeIn / FadeInSeconds) : 1f;
        if (_fadeIn > 0f) _fadeIn = Mathf.Max(0f, _fadeIn - dt);

        int n = _order.Length;
        Quaternion parentGlobal = Quaternion.identity;
        for (int i = 0; i < n; i++)
        {
            int pi = _parent[i];
            parentGlobal = pi < 0 ? Quaternion.identity : _globals[pi];

            int ci = _map[i];
            Quaternion local;
            Quaternion global;
            if (ci >= 0)
            {
                var d0 = clip.Delta(f0, ci);
                var d1 = clip.Delta(f1, ci);
                var d = Quaternion.Slerp(d0, d1, frac);
                var dw = _basis * d * _basisInv;
                global = dw * _anchorRot[i];
                local = Quaternion.Inverse(parentGlobal) * global;

                if (fade < 1f)
                {
                    local = Quaternion.Slerp(_entryLocal[i], local, fade);
                    global = parentGlobal * local;
                }
            }
            else
            {
                local = _entryLocal[i];
                global = parentGlobal * local;
            }

            _globals[i] = global;
            if (i == _pelvisIndex && _legScale > 0f)
            {
                float y0 = clip.PelvisY(f0), y1 = clip.PelvisY(f1);
                // The pelvis offset fades with the rotations. Withholding it until fade==1 and then
                // applying it whole was a 0.4 m pop at entry for the crouch clips (Prisyadka).
                float y = Mathf.Lerp(y0, y1, frac) * _legScale * fade;
                var p = _entryLocalPos[i];
                var np = new Vector3(p.x, p.y + y, p.z);
                _order[i].localPosition = np;
                _writtenPos[i] = np;
                _posDirty[i] = Mathf.Abs(y) > 1e-5f;
                // Trace the whole fade, then exactly one steady-state frame: the trace budget is
                // reserved for the entry and exit windows, not the bulk of a looping clip.
                if (fade < 1f) TracePelvis(clip, "in", fade, y, np, p);
                else if (!_entryTraceDone) { _entryTraceDone = true; TracePelvis(clip, "in", fade, y, np, p); }
            }
            _order[i].localRotation = local;
            _written[i] = local;
        }

        _writeFrames++;
        if (IkEnabled && clip.HasIk) ApplyIk(clip, fade, f0, f1, frac);
    }

    // Per-frame evidence for the pelvis offset. Bounded so a long requested run cannot flood the
    // log; the fade windows themselves are only 12 frames at 60 Hz.
    private void TracePelvis(EmoteClip clip, string phase, float fade, float y, Vector3 now, Vector3 baseline)
    {
        if (_posTraceLines >= 80) return;
        _posTraceLines++;
        _log.LogInfo($"[emote:pos] phase={phase} clip='{clip.Name}' f={Time.frameCount} t={_stateTime:F3}s " +
                     $"fade={fade:F3} offset={y:F4} pelvisY={now.y:F4} base={baseline.y:F4} dirty={_posDirty[_pelvisIndex]}");
    }

    // Immediate teardown restore: put every position this player changed back to the captured
    // baseline. Rotations are re-evaluated by the game every frame; translation is not, so a
    // controller that only drives rotations would otherwise keep the emote offset indefinitely.
    private void RestorePoseNow(string reason)
    {
        if (_order == null || _entryLocalPos == null || _posDirty == null) return;
        int restored = 0;
        for (int i = 0; i < _order.Length && i < _posDirty.Length; i++)
        {
            if (!_posDirty[i]) continue;
            var t = _order[i];
            try
            {
                if (t != null) { t.localPosition = _entryLocalPos[i]; restored++; }
            }
            catch (Exception e) { _log.LogWarning($"[emote:pos] restore failed bone='{(t == null ? "null" : t.name)}': {e.Message}"); }
            _posDirty[i] = false;
            if (_writtenPos != null) _writtenPos[i] = _entryLocalPos[i];
        }
        if (restored > 0 || (reason != "pre-start" && reason != "unbind"))
            _log.LogInfo($"[emote:pos] restored {restored} position(s) to the pre-emote baseline reason={reason}");
    }

    // Two-bone IK from the ported target. Called after the rotation pass has written the whole
    // skeleton, so the chain's actual world positions are the rotation-driven pose; the solve
    // keeps that pose's elbow plane and only rotates upperarm/lowerarm (the hand keeps its
    // authored local rotation, exactly like a CCD chain whose tip is the hand).
    private void ApplyIk(EmoteClip clip, float fade, int f0, int f1, float frac)
    {
        var root = _order[0];
        var rootRotInv = Quaternion.Inverse(root.rotation);
        ApplyIkSide(_ikL, "L", clip, fade, f0, f1, frac, root, rootRotInv);
        ApplyIkSide(_ikR, "R", clip, fade, f0, f1, frac, root, rootRotInv);
    }

    private void ApplyIkSide(IkChain c, string side, EmoteClip clip, float fade,
                             int f0, int f1, float frac, Transform root, Quaternion rootRotInv)
    {
        var curve = clip.Ik(side);
        if (curve == null) { ResetIk(c); return; }
        if (!c.Valid || c.L1 <= 0.001f || c.L2 <= 0.001f) { ResetIk(c); return; }

        float w = Mathf.Lerp(curve.Weight(f0), curve.Weight(f1), frac) * fade;
        c.LastWeight = w;
        if (w <= 1e-4f)
        {
            // clip does not drive this side: clear the diagnostics too, or the next clip's sample
            // would report this side's stale clamp count
            c.LastDist = 0f; c.LastErr = 0f; c.Clamps = 0;
            return;
        }

        // target position: parent bone's animated world position + the ported offset, mapped by the
        // same source->target basis the rotations use and scaled by the arm reach ratio
        var offSrc = Vector3.Lerp(curve.Pos(f0), curve.Pos(f1), frac) * c.Scale;
        Vector3 offTgt = _basis * offSrc;
        Vector3 parentPos = _order[c.Parent].position;
        Vector3 target = parentPos + root.TransformVector(offTgt);

        Vector3 a = _order[c.Upper].position;
        Vector3 b = _order[c.Lower].position;
        Vector3 handPos = _order[c.Hand].position;
        Vector3 d = target - a;
        float dlen = d.magnitude;
        c.LastDist = dlen;

        float dmax = (c.L1 + c.L2) * 0.999f;
        float dmin = Mathf.Abs(c.L1 - c.L2) + 0.001f;
        float dc = Mathf.Clamp(dlen, dmin, dmax);
        Vector3 v = dlen > 1e-5f ? d / dlen : (handPos - a).normalized;
        if (v.sqrMagnitude < 1e-6f) v = Vector3.forward;

        // keep the current elbow plane: project the current upper-arm direction onto the plane
        // perpendicular to the target direction, which is where a CCD solve from this pose lands
        Vector3 u = b - a;
        float ul = u.magnitude;
        u = ul > 1e-5f ? u / ul : v;
        Vector3 p = u - v * Vector3.Dot(u, v);
        if (p.sqrMagnitude < 1e-6f)
        {
            Vector3 axis = Mathf.Abs(v.x) < 0.9f ? Vector3.right : Vector3.up;
            p = Vector3.Cross(v, axis);
        }
        p.Normalize();

        float cosA = Mathf.Clamp((c.L1 * c.L1 + dc * dc - c.L2 * c.L2) / (2f * c.L1 * dc), -1f, 1f);
        float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));
        Vector3 bNew = a + (v * cosA + p * sinA) * c.L1;
        Vector3 cNew = a + v * dc;

        Quaternion qA = Quaternion.FromToRotation(b - a, bNew - a);
        Vector3 c1 = a + qA * (handPos - a);
        Quaternion qB = Quaternion.FromToRotation(c1 - bNew, cNew - bNew);

        // world-space deltas -> model-root-local deltas (_globals is root-local)
        Quaternion qAw = rootRotInv * qA * root.rotation;
        Quaternion qBw = rootRotInv * qB * root.rotation;
        // the upperarm's own parent (clavicle) carries its local frame; `Parent` is spine.03 and
        // is only the attachment for the target offset
        int pi = _parent[c.Upper];
        Quaternion gParent = pi < 0 ? Quaternion.identity : _globals[pi];
        Quaternion gU = _globals[c.Upper];
        Quaternion gL = _globals[c.Lower];
        Quaternion gH = _globals[c.Hand];

        Quaternion nU = Quaternion.Slerp(gU, qAw * gU, w);
        Quaternion nL = Quaternion.Slerp(gL, qBw * qAw * gL, w);
        Quaternion nH = Quaternion.Slerp(gH, qBw * qAw * gH, w);

        Quaternion lU = Quaternion.Inverse(gParent) * nU;
        _order[c.Upper].localRotation = lU;
        _written[c.Upper] = lU;
        _globals[c.Upper] = nU;

        Quaternion lL = Quaternion.Inverse(nU) * nL;
        _order[c.Lower].localRotation = lL;
        _written[c.Lower] = lL;
        _globals[c.Lower] = nL;

        Quaternion lH = Quaternion.Inverse(nL) * nH;
        _order[c.Hand].localRotation = lH;
        _written[c.Hand] = lH;
        _globals[c.Hand] = nH;

        // diagnostics: how far the wrist is from the (possibly unreachable) target. Measure the
        // actual transform, not the analytical point, so a bad rotation write shows up here.
        c.LastErr = Vector3.Distance(_order[c.Hand].position, target);
        c.LastTargetLocal = root.InverseTransformPoint(target);
        c.LastUpperLocal = root.InverseTransformPoint(a);
        c.LastHandLocal = root.InverseTransformPoint(_order[c.Hand].position);
        if (dlen > dmax) c.Clamps++;
    }

    private Quaternion[] _globals = new Quaternion[0];


    private void TickFadeOut(float dt)
    {
        if (_order == null) { _stopping = false; _stopFrom = null; _stopFromPos = null; return; }
        float a = 1f - Mathf.Clamp01(_fadeOut / FadeOutSeconds);
        // Finish the blend exactly on the last tick: at 15 fps the fade is only three ticks and a
        // partial final blend followed by a hard restore was itself a visible jump.
        if (_fadeOut - dt <= 0f) a = 1f;
        _fadeOut -= dt;
        for (int i = 0; i < _order.Length; i++)
        {
            var live = _order[i].localRotation;
            _order[i].localRotation = Quaternion.Slerp(_stopFrom[i], live, a);

            if (_posDirty != null && _posDirty[i] && _stopFromPos != null && _writtenPos != null)
            {
                var livePos = _order[i].localPosition;
                // If the returning animator rewrites the pelvis position, blend to its value. If it
                // does not (culling, rotation-only layer), `livePos` is still our own last write,
                // so blend back to the captured baseline instead of holding the offset forever.
                bool untouched = ForceBaselineFadeTarget || (livePos - _writtenPos[i]).sqrMagnitude < 1e-10f;
                var target = untouched ? _entryLocalPos[i] : livePos;
                var np = Vector3.Lerp(_stopFromPos[i], target, a);
                _order[i].localPosition = np;
                _writtenPos[i] = np;
                _posTraceLines++;
                if (_posTraceLines <= 80)
                    _log.LogInfo($"[emote:pos] phase=out clip='{CurrentClip}' f={Time.frameCount} fade={a:F3} " +
                                 $"pelvisY={np.y:F4} base={_entryLocalPos[i].y:F4} live={livePos.y:F4} target={(untouched ? "baseline" : "animator")}");
                // At a==1 the write is exactly the blend target (the animator's own value when it
                // writes translation, the baseline when it does not), so ownership ends here. A
                // restore after that must not fight the animator for a bone it already re-drives.
                if (a >= 1f) _posDirty[i] = false;
            }
        }
        if (_fadeOut <= 0f)
        {
            _stopping = false;
            _stopFrom = null;
            _stopFromPos = null;
            // Safety net: nothing may keep a residual offset after the hand-back, whatever the
            // animator did or did not write during the fade.
            RestorePoseNow("fadeout-complete");
            ForceBaselineFadeTarget = false;
            _log.LogInfo($"[emote] STOP fadeout complete clip='{CurrentClip}' handed back to the game's abstraction");
        }
    }

    private static string Euler(Quaternion q) { var e = q.eulerAngles; return $"({e.x:F0},{e.y:F0},{e.z:F0})"; }

    internal void Dispose() => Unbind();
}
