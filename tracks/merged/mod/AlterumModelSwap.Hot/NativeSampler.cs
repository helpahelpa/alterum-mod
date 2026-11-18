using System;
using System.Globalization;
using System.Text;
using AlterumModelSwap.Contracts;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace AlterumModelSwap.Hot;

// Samples one of the game's own AnimationClips onto a scratch copy of the player rig so the pose
// can be written through PosePlayer's ownership lease.
//
// Why a copy instead of the real rig: while the lease is held the game's Animator and Animancer are
// disabled, so nothing else evaluates the clip. Sampling on the real rig would be a second writer
// with no fade control; sampling on a private hierarchy keeps clip evaluation isolated and the
// faded pose is then written to the real skeleton by PosePlayer, exactly like a ported LC clip.
//
// The hierarchy, bone names and avatar come from the live model, so Unity's own humanoid muscle
// retargeting (and generic path binding) does the conversion - this file never interprets a clip's
// curve data.
internal sealed class NativeSampler
{
    private const float PoseEpsilonDeg = 0.05f;
    private const float PosEpsilon = 0.0005f;

    private IHostContext _log;
    private GameObject _root;
    private Animator _animator;
    private Transform[] _bones = Array.Empty<Transform>();
    private Quaternion[] _restRot = Array.Empty<Quaternion>();
    private Vector3[] _restPos = Array.Empty<Vector3>();
    private PlayableGraph _graph;
    private AnimationClipPlayable _playable;
    private AnimationClip _clip;
    private bool _graphMode;
    private bool _ready;

    // Per-bone sampled values, indexed like PosePlayer's walk order.
    internal Quaternion[] Rot = Array.Empty<Quaternion>();
    internal Vector3[] Pos = Array.Empty<Vector3>();
    internal bool[] RotDriven = Array.Empty<bool>();
    internal bool[] PosDriven = Array.Empty<bool>();

    internal bool Ready => _ready;
    internal string Mode => _graphMode ? "playablegraph" : "sampleanimation";
    internal int DrivenRotCount { get; private set; }
    internal int DrivenPosCount { get; private set; }
    internal int Bones => Rot.Length;

    // Build the scratch rig. `entryRot`/`entryPos` are the live model's pose at Start; they seed
    // every bone so a clip that does not animate a bone leaves it at the pose the player had.
    internal bool Bind(IHostContext log, Animator source, Transform[] order, int[] parent,
                       Quaternion[] entryRot, Vector3[] entryPos, AnimationClip clip)
    {
        _log = log;
        _clip = clip;
        try
        {
            if (source == null || order == null || order.Length == 0 || clip == null)
            {
                log.LogWarning("[emote:native] sampler: no source animator / rig / clip");
                return false;
            }
            var avatar = source.avatar;
            if (avatar == null || !avatar.isHuman)
            {
                log.LogWarning($"[emote:native] sampler: source avatar is not humanoid " +
                               $"('{(avatar == null ? "null" : avatar.name)}'); humanoid clips cannot be retargeted");
                return false;
            }

            int n = order.Length;
            Rot = new Quaternion[n];
            Pos = new Vector3[n];
            RotDriven = new bool[n];
            PosDriven = new bool[n];
            _restRot = new Quaternion[n];
            _restPos = new Vector3[n];

            _root = new GameObject("AlterumEmoteSampleRig");
            _root.hideFlags = HideFlags.HideAndDontSave;
            _root.SetActive(true);
            _bones = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                Transform t;
                if (i == 0)
                {
                    t = _root.transform;
                }
                else
                {
                    var child = new GameObject(order[i].name);
                    child.hideFlags = HideFlags.HideAndDontSave;
                    t = child.transform;
                    int pi = parent[i];
                    t.SetParent(pi >= 0 && pi < i ? _bones[pi] : _root.transform, false);
                }
                // Keep the scratch root's own name: renaming it to the model root's name would put
                // a second GameObject with that name in the scene and confuse name-based lookups.
                if (i > 0) t.name = order[i].name;
                t.localPosition = entryPos[i];
                t.localRotation = entryRot[i];
                t.localScale = order[i].localScale;
                _bones[i] = t;
            }
            for (int i = 0; i < n; i++)
            {
                _restRot[i] = _bones[i].localRotation;
                _restPos[i] = _bones[i].localPosition;
            }

            _animator = _root.AddComponent<Animator>();
            _animator.avatar = avatar;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.enabled = true;

            bool graphOk = false;
            try
            {
                _graph = PlayableGraph.Create("AlterumEmoteSample");
                _playable = AnimationClipPlayable.Create(_graph, clip);
                if (_playable.IsValid())
                {
                    _playable.SetApplyFootIK(false);
                    _playable.SetApplyPlayableIK(false);
                    var output = AnimationPlayableOutput.Create(_graph, "AlterumEmoteSampleOut", _animator);
                    PlayableOutputExtensions.SetSourcePlayable(output, _playable);
                    _graph.Play();
                    graphOk = true;
                }
            }
            catch (Exception e) { log.LogWarning($"[emote:native] sampler: playable graph setup failed: {e.Message}"); }

            // Probe both modes on a real sample: zero driven bones for a non-empty clip means the
            // evaluation path did not write the clone, so fall back to SampleAnimation.
            _graphMode = graphOk;
            float probeT = clip.length > 0.02f ? clip.length * 0.5f : 0f;
            int graphBones = ProbeDriven(probeT);
            if (graphBones == 0 && clip.length > 0.001f)
            {
                _graphMode = false;
                int altBones = ProbeDriven(probeT);
                log.LogInfo($"[emote:native] sampler: playablegraph probe drove {graphBones} bone(s), " +
                            $"SampleAnimation probe drove {altBones}; using {Mode}");
                if (altBones == 0)
                {
                    log.LogWarning("[emote:native] sampler: neither evaluation path moved the scratch rig; clip is unusable");
                    Dispose();
                    return false;
                }
            }

            Measure(clip.length);
            _ready = true;
            log.LogInfo($"[emote:native] sampler ready clip='{clip.name}' len={Fmt(clip.length)} " +
                        $"loop={(clip.isLooping ? 1 : 0)} humanMotion={(clip.humanMotion ? 1 : 0)} mode={Mode} " +
                        $"bones={Bones} rotDriven={DrivenRotCount} posDriven={DrivenPosCount} " +
                        $"driven=[{DrivenNames(order)}]");
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[emote:native] sampler bind threw: {e}");
            Dispose();
            return false;
        }
    }

    private int ProbeDriven(float time)
    {
        try
        {
            SampleAt(time);
            int moved = 0;
            for (int i = 0; i < Rot.Length; i++)
            {
                if (Quaternion.Angle(_restRot[i], Rot[i]) > PoseEpsilonDeg) moved++;
                else if ((_restPos[i] - Pos[i]).sqrMagnitude > PosEpsilon * PosEpsilon) moved++;
            }
            return moved;
        }
        catch (Exception e)
        {
            _log?.LogWarning($"[emote:native] sampler probe at {time:F3}s threw: {e.Message}");
            return 0;
        }
    }

    // Sample across the clip and record which bones it drives. A bone counts as driven when it is
    // posed away from the seeded rest, or when it changes between samples (baseline-independent).
    private void Measure(float duration)
    {
        int n = Rot.Length;
        Array.Clear(RotDriven, 0, n);
        Array.Clear(PosDriven, 0, n);

        const int samples = 16;
        var rotRef = new Quaternion[n];
        var posRef = new Vector3[n];
        for (int s = 0; s < samples; s++)
        {
            float t = duration > 0.001f ? duration * s / (samples - 1) : 0f;
            SampleAt(t);
            for (int i = 0; i < n; i++)
            {
                if (Quaternion.Angle(_restRot[i], Rot[i]) > PoseEpsilonDeg) RotDriven[i] = true;
                if ((_restPos[i] - Pos[i]).sqrMagnitude > PosEpsilon * PosEpsilon) PosDriven[i] = true;
                if (s > 0)
                {
                    if (Quaternion.Angle(rotRef[i], Rot[i]) > PoseEpsilonDeg) RotDriven[i] = true;
                    if ((posRef[i] - Pos[i]).sqrMagnitude > PosEpsilon * PosEpsilon) PosDriven[i] = true;
                }
                rotRef[i] = Rot[i];
                posRef[i] = Pos[i];
            }
        }
        DrivenRotCount = 0; DrivenPosCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (RotDriven[i]) DrivenRotCount++;
            if (PosDriven[i]) DrivenPosCount++;
        }
    }

    internal void SampleAt(float time)
    {
        if (_bones.Length == 0) return;
        try
        {
            if (_clip != null && _clip.length > 0.0001f)
                time = _clip.isLooping ? Mathf.Repeat(time, _clip.length) : Mathf.Clamp(time, 0f, _clip.length);
            if (_graphMode)
            {
                _playable.SetTime(time);
                _graph.Evaluate(0f);
            }
            else
            {
                _clip.SampleAnimation(_root, time);
            }
            for (int i = 0; i < _bones.Length; i++)
            {
                Rot[i] = _bones[i].localRotation;
                Pos[i] = _bones[i].localPosition;
            }
        }
        catch (Exception e)
        {
            _log?.LogWarning($"[emote:native] sampler sample at {time:F3}s threw: {e.Message}");
        }
    }

    // Names of the bones the clip drives, for the census line ('*' = translation is driven too).
    internal string DrivenNames(Transform[] order, int max = 40)
    {
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < RotDriven.Length && i < order.Length; i++)
        {
            if (!RotDriven[i] && !PosDriven[i]) continue;
            if (shown++ >= max) { sb.Append("..."); break; }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(order[i].name);
            if (PosDriven[i]) sb.Append('*');
        }
        return sb.ToString();
    }

    internal void Dispose()
    {
        _ready = false;
        try { if (_graph.IsValid()) _graph.Destroy(); } catch (Exception e) { _log?.LogWarning($"[emote:native] sampler graph destroy failed: {e.Message}"); }
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch (Exception e) { _log?.LogWarning($"[emote:native] sampler rig destroy failed: {e.Message}"); }
        _root = null; _animator = null; _bones = Array.Empty<Transform>(); _playable = default;
        Rot = Array.Empty<Quaternion>(); Pos = Array.Empty<Vector3>();
        RotDriven = Array.Empty<bool>(); PosDriven = Array.Empty<bool>();
        _restRot = Array.Empty<Quaternion>(); _restPos = Array.Empty<Vector3>();
    }

    private static string Fmt(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
}
