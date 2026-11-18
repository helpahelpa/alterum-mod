using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Animancer;
using Animations.ExtendedAnimancer.PersonAnimancer;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Emote feature entry point.
//
//   U           open the emote picker (PickerUI); a click there is the only way to START an emote
//
// There is no emote key any more. F6/F9 were pruned with the controls table, and so were the request
// file's clip playback (`clip=`/`repeats=`), its `cycle=` stepping and the self-test/sweep/filming
// drivers: every one of them could start or drive an emote autonomously, which is exactly what "no
// keyboard path to an emote" rules out. What remains of the request file is the read-only census
// and probe diagnostics plus stop/cleanup (`stop=1`), so an unattended run can still enumerate and
// release, but never start.
//
// The catalogue is CLOSED: EmoteCatalog is the only catalogue, and every id that can be named,
// enumerated, started, published or accepted is resolved through it. No clip of the game's own
// animancer graph is offered (zero native ids): the graph is still sampled for the read-only
// census, but nothing from it can enter the catalogue or be started.
//
// The LC port never builds AnimationClips and never touches runtimeAnimatorController: the motion
// comes from tools/extract_emotes.py and is applied as bone transforms while the game's own
// animation is suspended by an ownership lease. That is the only playback path left: one lease,
// one post-animation write phase, one SkeletonOwned signal for the jiggle solver.
internal sealed class EmoteRuntime
{
    private static int _nextId;
    private readonly int _id = ++_nextId;
    private readonly IHostContext _log;
    private readonly Func<RenderTexture> _mirrorTexture;

    private float _t;
    private bool _packTried;
    private EmotePack _pack;
    private PosePlayer _player;
    private float _nextResolve;
    private float _nextGenerationCheck;

    private Player _player_owner;
    private PlayerModelReferences _refs;
    private PlayerModelPersonAnimancerController _controller;
    private PersonAnimancerComponent _person;
    private Animator _animator;

    private int _clipIndex;
    private int _runs;
    // Diagnostic pose/strip sampling of an ACTIVE emote (whichever path started it). Read-only: it
    // captures the verify camera at fixed clip-relative fractions.
    private int _stripShot;
    private static readonly float[] StripAt = { 0.06f, 0.25f, 0.45f, 0.65f, 0.85f };

    // stage-1 diagnostic state (native Alterum clip on an extra Animancer layer) is gone: game
    // clips now go through the same lease as the ported clips (see the native-emote section).
    private readonly Dictionary<string, Transform> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transform> _byLeaf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dupLeaf = new(StringComparer.Ordinal);
    private readonly List<string> _probes = new();
    private int[] _probeIdx = new int[0];
    private Quaternion[] _entryRot;
    private Vector3 _lastPos;
    private float _posSampleAt;
    private float _nextHeartbeat;

    internal EmoteRuntime(IHostContext log, Func<RenderTexture> mirrorTexture)
    {
        _log = log;
        _mirrorTexture = mirrorTexture;
    }

    // True while an emote (or its fade-out) owns the skeleton and writes local transforms in
    // PostAnimationUpdate. ModelSwapRuntime.LateUpdate uses this to suspend the jiggle solve, so
    // chest_02_l/r have one writer instead of two.
    internal bool SkeletonOwned
    {
        get { try { return _player != null && _player.Writing; } catch { return false; } }
    }

    // Teardown evidence: the host logs these around Dispose so the retire path can be asserted
    // (lease released, Animator.enabled actually restored) instead of inferred from write stops.
    internal bool LeaseHeld
    {
        get { try { return _player != null && _player.LeaseHeld; } catch { return false; } }
    }

    internal Animator AnimatorRef
    {
        get { try { return _animator; } catch { return null; } }
    }

    // ------------------------------------------------------------------ picker surface
    // The picker lives in PickerUI, and a pick uses the single entry point: StartAt selects the
    // clip, takes the PosePlayer lease and starts playback. Nothing here opens a second playback
    // path, and the movement cancellation is untouched.
    internal sealed class PickerEntry
    {
        internal int Index;
        internal string Id;
        internal string Name;
        internal string Badge;       // LC
        internal float Length;
        internal bool Loop;
    }

    private bool _pickerOpen;

    // The picker tells this runtime when it owns the screen. Kept as the one flag a future key path
    // would have to respect; nothing polls a mod key for an emote any more.
    internal void SetPickerOpen(bool open) => _pickerOpen = open;
    internal bool PickerOpen => _pickerOpen;

    internal int PickerEntryCount => TotalEmotes;
    internal int SelectedIndex => _clipIndex;
    internal string SelectedId => IdAt(_clipIndex);

    internal string PlayingId
    {
        get
        {
            try { return _player != null && _player.Active ? _player.CurrentClip : null; }
            catch { return null; }
        }
    }

    // The picker's "Stop Emote" button uses this to decide between the enabled and the dimmed
    // state. LeaseHeld is in the OR because the fade-out window (Active already false, lease still
    // installed) is still something a stop has to release.
    internal bool Playing => PlayingId != null || LeaseHeld;

    // Called by the picker; identical to the F6 path once the index is resolved.
    internal bool StartEmote(int index, string trigger)
    {
        if (_player == null || TotalEmotes == 0) return false;
        StartAt(index, trigger);
        try { return _player.Active; } catch { return false; }
    }

    // The picker's "Stop Emote" button. This is the active branch of Toggle() below, nothing else:
    // PosePlayer.Stop releases the DoingSomethingHandler lease, hands the bones back to the game
    // animator and starts the fade-out, which is what lets JiggleSolver resume (ModelSwapRuntime
    // LateUpdate drives SetSuppressed from SkeletonOwned). Returns false when there is nothing to
    // stop, so the button can report "not playing" instead of pretending it did something.
    internal bool StopEmote(string trigger)
    {
        if (_player == null) return false;
        try
        {
            if (!_player.Active && !_player.LeaseHeld) return false;
            _player.Stop(trigger);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning($"[emote] stop by {trigger} failed: {e.Message}");
            return false;
        }
    }

    // The catalogue, in EmoteCatalog order. Two entries, one row: PickerUI draws exactly what this
    // returns, so a removed clip cannot leave a cell (or a category tab) behind.
    internal List<PickerEntry> PickerEntries()
    {
        var list = new List<PickerEntry>();
        if (_pack == null) return list;
        for (int i = 0; i < _pack.Clips.Count; i++)
        {
            var c = _pack.Clips[i];
            list.Add(new PickerEntry
            {
                Index = i, Id = c.Name, Name = c.Name.Replace('_', ' '), Badge = "LC",
                Length = c.Duration, Loop = c.Loop,
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ update

    internal void Update(float dt)
    {
        try
        {
            _t += dt;
            if (_player != null && _player.Active) _runTime += dt;
            if (!_packTried && _t > 3f) { _packTried = true; LoadPack(); }
            if (_pack == null) return;

            if (_player_owner == null && _t >= _nextResolve) { _nextResolve = _t + 1f; Resolve(); }
            // The generation check runs even when the owner is gone: losing the owner IS the event,
            // and the lease/pose must be released before the early return below can skip it.
            if (_t >= _nextGenerationCheck) { _nextGenerationCheck = _t + 1f; CheckGeneration(); }
            if (_player_owner == null) return;

            PollRequestFile();

            if (_player.Active) MovementCheck();
        }
        catch (Exception e) { _log.LogError($"[emote] update threw: {e}"); }
    }

    // Runs after LateUpdate of every other behaviour: this is where the pose has to land.
    internal void PostAnimationUpdate(float dt)
    {
        try
        {
            _player?.PostAnimationUpdate(dt);
        }
        catch (Exception e) { _log.LogError($"[emote] pose write threw: {e}"); }
    }
    internal void LateUpdate()
    {
        if (_player != null && _player.Active && _stripShot < StripAt.Length)
        {
            float frac = _player.Elapsed / Mathf.Max(_player.Duration, 0.05f);
            if (frac >= StripAt[_stripShot])
            {
                int n = _stripShot++;
                CaptureVerify($"run{_runs}_f{n}_{frac:F2}");
                _log.LogInfo($"[emote:sample] run={_runs} clip='{_player.CurrentClip}' strip={n} " +
                             $"t={_player.Elapsed:F3}s dur={_player.Duration:F3}s frac={frac:F3} metrics[{Metrics()}]");
            }
        }
        TrackTravel();
    }

    private void LoadPack()
    {
        _pack = EmotePack.TryLoad(_log);
        if (_pack == null) { _log.LogWarning("[emote] no emote pack, feature idle"); return; }
        _player = new PosePlayer(_log, _pack);
        RestoreSelection();
    }

    // The selection is an ID, and it is restored through the allowlist: the index space changed
    // when the catalogue was closed, so a stale numeric index can never select a different clip.
    // A saved id that is no longer offered resets to the first catalogue entry (Twerk) and is NOT
    // started - restoring a selection never takes the pose lease.
    private void RestoreSelection()
    {
        string raw;
        bool reset;
        // The persisted id, resolved through the allowlist (EmoteSelection.Restore); the pack lookup
        // is the second half of the same rule, so an id this build does not offer can never select a
        // different clip by landing on an index.
        string saved = EmoteSelection.Restore(_log, out raw, out reset);
        int index = EmoteCatalog.ResolveSelection(_pack, saved, out bool missing);
        _clipIndex = index < 0 ? 0 : index;
        string id = IdAt(_clipIndex);
        string from = string.IsNullOrEmpty(raw) ? "<none saved>" : raw;
        if (reset || missing)
            _log.LogInfo($"[emote:catalog] saved selection '{from}' is not in this build's " +
                         $"allowlist ({EmoteCatalog.List()}); selection reset to '{id}' - nothing started (no auto-play)");
        else
            _log.LogInfo($"[emote:catalog] selection restored by id '{id}' index={_clipIndex}/{TotalEmotes} - nothing started");
    }

    // ------------------------------------------------------------------ request-file control
    // The demo desktop is locked, so no key can be pressed. "<ScreensDir>/emote.request" is now a
    // READ-ONLY + STOP surface, deleted on pickup. Line-oriented:
    //   stop=1         release the active emote / lease (cleanup; always honoured)
    //   census=1       dump the player model's own animation source (nothing starts)
    //   probe=<clip>   drive-mask probe for one loaded AnimationClip (nothing starts)
    //   native=1       reports that no native emote is offered (nothing starts)
    //   baseline=1     force the pre-emote pelvis fade target for the next stop (diagnostic)
    // The keys that could start or advance playback (`clip=`, `repeats=`, `cycle=`, `selftest=`,
    // `nativesweep=`, `nativefilm=`, `filmnames=`) are gone with the controls prune: they all
    // started an emote with no user input, which is the behaviour the prune removes.
    private const string RequestFileName = "emote.request";
    private const float RequestPollSeconds = 0.25f;
    private float _nextRequestPoll;

    private void PollRequestFile()
    {
        if (_t < _nextRequestPoll) return;
        _nextRequestPoll = _t + RequestPollSeconds;
        string path;
        try { path = Path.Combine(_log.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;

        string text = null;
        try { text = File.ReadAllText(path); }
        catch (Exception e) { _log.LogWarning($"[emote:request] read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }
        if (text == null) return;

        bool stop = false;
        bool nativeSeen = false, nativeOn = false;
        bool baselineSeen = false, baselineOn = false;
        bool censusSeen = false, censusOn = false;
        string probeName = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { _log.LogWarning($"[emote:request] ignored line '{line}' (not key=value)"); continue; }
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "stop": stop = val.Length > 0 && val != "0"; break;
                case "native": nativeSeen = true; nativeOn = val.Length > 0 && val != "0"; break;
                case "baseline": baselineSeen = true; baselineOn = val.Length > 0 && val != "0"; break;
                case "census": censusSeen = true; censusOn = val.Length > 0 && val != "0"; break;
                case "probe": probeName = val; break;
                // A pruned key is reported by name instead of being silently accepted: an old
                // recording script must see that its playback line no longer does anything.
                case "clip": case "repeats": case "cycle": case "selftest":
                case "nativesweep": case "nativefilm": case "filmnames":
                    _log.LogWarning($"[emote:request] '{key}' was removed with the controls prune: " +
                                    "no emote key or emote.request line starts one any more (the U picker is the path)");
                    break;
                default: _log.LogWarning($"[emote:request] unknown key '{key}' ignored"); break;
            }
        }

        // Full census of the player model's own animation source (layers, states, clips). Never
        // starts playback; purely read-only enumeration.
        if (censusSeen)
        {
            if (censusOn)
            {
                NativeCensus.Dump(_log, _person, _animator);
                if (!_nativeCatalog) BuildNativeCatalog();
                if (_player != null && _player.Bound)
                {
                    for (int i = 0; i < _graphClips.Count; i++)
                    {
                        var c = _graphClips[i];
                        _log.LogInfo($"[emote:census] BONES '{c.name}' {_player.ProbeNativeClip(c)}");
                    }
                }
                else _log.LogWarning("[emote:census] pose player not bound; bone-drive counts skipped");
            }
            else _log.LogInfo("[emote:request] census=0 (nothing to turn off)");
        }

        // Read-only drive-mask probe for one clip, by name, from the loaded AnimationClip set.
        if (!string.IsNullOrEmpty(probeName))
        {
            var all = SceneCache.Scan(Il2CppType.Of<AnimationClip>());
            AnimationClip found = null;
            for (int i = 0; i < all.Length; i++)
            {
                AnimationClip c = null;
                try { c = all[i].TryCast<AnimationClip>(); } catch { }
                if (c != null && string.Equals(c.name, probeName, StringComparison.Ordinal)) { found = c; break; }
            }
            if (found == null) _log.LogWarning($"[emote:probe] '{probeName}' is not a loaded AnimationClip");
            else if (_player == null || !_player.Bound) _log.LogWarning("[emote:probe] pose player not bound");
            else _log.LogInfo($"[emote:probe] '{found.name}' len={found.length:F3} loop={(found.isLooping ? 1 : 0)} " +
                              $"humanMotion={(found.humanMotion ? 1 : 0)} {_player.ProbeNativeClip(found)}");
        }

        if (nativeSeen)
        {
            // Retained request key: it resolves to nothing now, so it can only report that.
            if (!nativeOn) { if (_player.Active) _player.Stop("request-native"); }
            else _log.LogWarning($"[emote:native] no native emote is offered in this build " +
                                 $"({EmoteCatalog.Describe(_pack)}); nothing started");
        }

        if (baselineSeen && _player != null)
        {
            _player.ForceBaselineFadeTarget = baselineOn;
            _log.LogInfo($"[emote:request] baseline fade target {(baselineOn ? "forced" : "cleared")} for the next stop (P1.5 diagnostic: return to the pre-emote pelvis instead of the animator's value)");
        }

        if (stop && _player.Active) _player.Stop("request-stop");
        _log.LogInfo($"[emote:request] diagnostic request handled; active={_player.Active} " +
                     "(no clip is ever started from this file)");
    }

    // One entry point for playback. The index is mapped into the allowlisted catalogue and the id
    // is checked against the allowlist again before anything is started, so no caller can reach a
    // clip that is not offered.
    // Every start is a choice now (a picker click), so the selection is always persisted.
    private void StartAt(int index, string trigger)
    {
        if (_player == null) return;
        int total = TotalEmotes;
        if (total == 0) return;
        _clipIndex = ((index % total) + total) % total;
        string id = IdAt(_clipIndex);
        if (!EmoteCatalog.Contains(id))
        {
            _log.LogWarning($"[emote] requested index {_clipIndex} does not resolve to an allowlisted id " +
                            $"({EmoteCatalog.List()}); nothing started");
            return;
        }
        EmoteSelection.Save(_log, id);
        if (_player.Active || _player.LeaseHeld) _player.Stop("restart");
        _runs++;
        _stripShot = 0;
        _log.LogInfo($"[emote] requested '{id}' by {trigger} (allowlisted LC port)");
        _player.Start(id);
        bool ok = _player.Active;
        if (!ok)
        {
            _log.LogWarning($"[emote] '{id}' did not start (see the warning above); playback unchanged");
            return;
        }
        _runTime = 0f; _travelActive = false;
        _entryRot = SampleProbes();
        _posSampleAt = _t;
        try { _lastPos = _player_owner.transform.position; } catch { }
        _nextHeartbeat = _t;
    }

    // The model is re-instantiated on respawn/wardrobe changes: the bound transforms die with it,
    // so the lease must be released and everything rebound against the new rig.
    //
    // A live replacement can leave the old skeleton readable, so presence alone is not enough:
    // the controller/person are compared by identity against what was committed at bind time, and
    // any loss path stops playback and releases the lease before scheduling a new resolve.
    private void CheckGeneration()
    {
        try
        {
            if (_player == null) return;
            if (_refs == null)
            {
                // No refs to check against. Never keep a lease or a pose alive on their strength.
                if (_player.Active || _player.LeaseHeld || _controller != null)
                    Rebind("refs-lost");
                else _nextResolve = 0f;
                return;
            }

            bool dead;
            try { dead = _player.HasDeadBone(); } catch { dead = false; }

            ModelPersonAnimancerController current = null;
            PersonAnimancerComponent person = null;
            try
            {
                current = _refs.ThirdPlayerModelPersonAnimancerController;
                person = current != null ? current.CurrentPersonAnimancerComponent : null;
            }
            catch (Exception e) { _log.LogWarning($"[emote] generation read failed: {e.Message}"); }

            bool sameController = SamePointer(current, _controller);
            bool samePerson = SamePointer(person, _person);
            bool swapped = current == null || person == null || !sameController || !samePerson;
            if (!dead && !swapped)
            {
                if (_player.Active && _controller != null && _controller.IsAnimancerAndAnimatorEnabled)
                {
                    _log.LogError("[emote] EMOTE_OWNERSHIP_FAILED the game re-enabled animancer while we held the lease; stopping");
                    _player.Stop("ownership-lost");
                }
                return;
            }
            _log.LogInfo($"[emote] model changed (deadBone={dead} swapped={swapped} sameController={sameController} " +
                         $"samePerson={samePerson} currentNull={(current == null)} personNull={(person == null)}) - releasing and rebinding");
            Rebind("model-changed");
        }
        catch (Exception e) { _log.LogWarning($"[emote] generation check failed: {e.Message}"); }
    }

    // Single loss path: never leave playback, a lease or stale bind state behind before resolving
    // against whatever the game has now.
    private void Rebind(string reason)
    {
        try { if (_player != null && (_player.Active || _player.LeaseHeld || _player.Bound)) _player.Stop(reason); }
        catch (Exception e) { _log.LogWarning($"[emote] stop on {reason} failed: {e.Message}"); }
        try { _player?.Unbind(); } catch (Exception e) { _log.LogWarning($"[emote] unbind on {reason} failed: {e.Message}"); }
        _player_owner = null; _refs = null; _controller = null; _person = null; _animator = null;
        _nativeCatalog = false; _nativeEmotes.Clear(); _graphClips.Clear();
        _nextResolve = 0f;
        _nextGenerationCheck = 0f;
    }

    // Identity, not presence: Il2CppInterop wrappers for the same native object can be different
    // managed instances, so compare the native pointer. Null stays null.
    private static bool SamePointer(Il2CppObjectBase a, Il2CppObjectBase b)
    {
        if (a == null || b == null) return a == null && b == null;
        try { return a.Pointer == b.Pointer; } catch { return false; }
    }

    private void MovementCheck()
    {
        if (_t - _posSampleAt < 0.5f) return;
        _posSampleAt = _t;
        Vector3 p;
        try { p = _player_owner.transform.position; } catch { return; }
        float d = (p - _lastPos).magnitude;
        _lastPos = p;
        if (d > 0.08f) _player.Stop($"movement({d:F3}m)");
        else _log.LogInfo($"[emote:move-check] delta={d:F4}m over 0.5s (threshold 0.08) - no movement");
    }

    private void Heartbeat()
    {
        if (_player == null || !_player.Active) return;
        var sb = new StringBuilder();
        try
        {
            var now = SampleProbes();
            for (int i = 0; i < now.Length && i < _entryRot.Length; i++)
                sb.Append(_probes[i]).Append('=').Append(Quaternion.Angle(_entryRot[i], now[i]).ToString("F1")).Append(' ');
        }
        catch { }
        _log.LogInfo($"[emote:active] id={_id} f={Time.frameCount} clip='{_player.CurrentClip}' t={_player.Elapsed:F3}s " +
                     $"animEnabled={_controller.IsAnimancerAndAnimatorEnabled} metrics[{Metrics()}] deltas-vs-entry: {sb}");
    }

    // Model-space joint distances: directly comparable with the same numbers computed offline
    // from the LC source pose, so retarget fidelity is measurable rather than eyeballed.
    private string Metrics()
    {
        try
        {
            var model = _animator != null ? _animator.transform : null;
            if (model == null) return "no-model";
            Vector3 P(string bone)
            {
                var t = Find(bone);
                return t != null ? model.InverseTransformPoint(t.position) : Vector3.zero;
            }
            var pelvis = P("pelvis"); var head = P("head");
            var hl = P("hand_l"); var hr = P("hand_r"); var fl = P("foot_l");
            float reachL = ReachL(), reachR = ReachR();
            float utilL = reachL > 0.01f ? Vector3.Distance(P("upperarm_l"), hl) / reachL : 0f;
            float utilR = reachR > 0.01f ? Vector3.Distance(P("upperarm_r"), hr) / reachR : 0f;
            return $"pelvisY={pelvis.y:F3} pelvisHead={Vector3.Distance(pelvis, head):F3} " +
                   $"pelvisHandL={Vector3.Distance(pelvis, hl):F3} pelvisHandR={Vector3.Distance(pelvis, hr):F3} " +
                   $"handLR={Vector3.Distance(hl, hr):F3} headHandR={Vector3.Distance(head, hr):F3} " +
                   $"legL={Vector3.Distance(pelvis, fl):F3} reachUtils[L={utilL:F3} R={utilR:F3}] " +
                   $"arms[L={reachL:F3} R={reachR:F3}]" + (_player != null ? _player.IkReport() : "");
        }
        catch (Exception e) { return "metrics-failed:" + e.Message; }
    }

    private Vector3 HandModelLocal(string side)
    {
        var model = _animator != null ? _animator.transform : null;
        if (model == null) return Vector3.zero;
        var t = Find("hand_" + side);
        return t != null ? model.InverseTransformPoint(t.position) : Vector3.zero;
    }

    private float Reach(string side)
    {
        var model = _animator != null ? _animator.transform : null;
        if (model == null) return 0f;
        var u = Find("upperarm_" + side); var l = Find("lowerarm_" + side); var h = Find("hand_" + side);
        if (u == null || l == null || h == null) return 0f;
        var up = model.InverseTransformPoint(u.position);
        var lo = model.InverseTransformPoint(l.position);
        var ha = model.InverseTransformPoint(h.position);
        return Vector3.Distance(up, lo) + Vector3.Distance(lo, ha);
    }

    private float ReachL() => Reach("l");
    private float ReachR() => Reach("r");

    // Per-run hand travel (axis-aligned bounds of the hand positions after the fade-in), reported
    // in metres and in arm reaches. Phase independent, so it works for looping clips too.
    private Vector3 _handMinL, _handMaxL, _handMinR, _handMaxR;
    private float _lrMin, _lrMax;
    private float _runTime;
    private bool _travelActive;
    private string _travelClip = "";

    private void TrackTravel()
    {
        if (_player == null) return;
        if (_player.Active)
        {
            if (!_travelActive && _runTime >= 0.35f)
            {
                _travelActive = true; _travelClip = _player.CurrentClip;
                var hl = HandModelLocal("l"); var hr = HandModelLocal("r");
                _handMinL = _handMaxL = hl; _handMinR = _handMaxR = hr;
                _lrMin = _lrMax = Vector3.Distance(hl, hr);
            }
            if (_travelActive)
            {
                var hl = HandModelLocal("l"); var hr = HandModelLocal("r");
                _handMinL = Vector3.Min(_handMinL, hl); _handMaxL = Vector3.Max(_handMaxL, hl);
                _handMinR = Vector3.Min(_handMinR, hr); _handMaxR = Vector3.Max(_handMaxR, hr);
                float lr = Vector3.Distance(hl, hr);
                _lrMin = Mathf.Min(_lrMin, lr); _lrMax = Mathf.Max(_lrMax, lr);
            }
        }
        else if (_travelActive)
        {
            _travelActive = false;
            float reach = Mathf.Max(ReachL(), 0.01f);
            var dL = _handMaxL - _handMinL; var dR = _handMaxR - _handMinR;
            _log.LogInfo($"[emote:travel] clip='{_travelClip}' handL={dL.magnitude:F3}m ({dL.magnitude / reach:F2}xReach) " +
                         $"handR={dR.magnitude:F3}m ({dR.magnitude / reach:F2}xReach) reach={reach:F3}m " +
                         $"bboxL={dL.x:F3}/{dL.y:F3}/{dL.z:F3} handLR={_lrMin:F3}..{_lrMax:F3} " +
                         $"(hand bounds from 0.35 s after start, past the 0.2 s fade-in)");
        }
    }

    // ------------------------------------------------------------------ resolve

    private void Resolve()
    {
        var log = _log;
        try
        {
            Player player = null;
            try
            {
                var provs = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
                for (int i = 0; i < provs.Length; i++)
                {
                    var prov = provs[i].TryCast<CurrentPlayerProvider>();
                    if (prov == null) continue;
                    var p = prov.CurrentPlayer;
                    if (p != null) { player = p; break; }
                }
            }
            catch { }
            if (player == null) return;

            var refs = player.GetRefs<PlayerModelReferences>();
            if (refs == null) return;
            var controller = refs.ThirdPlayerModelPersonAnimancerController;
            if (controller == null) return;
            var person = controller.CurrentPersonAnimancerComponent;
            if (person == null) return;
            var animator = person.Animator;
            if (animator == null) { log.LogWarning("[emote] resolved model has no Animator yet; retrying"); return; }

            // A binding is committed only after it succeeds: a failed bind must not suppress a
            // later resolve by leaving `_player_owner` set against a model we are not driving.
            DumpRig(animator);
            if (!_player.Bind(refs, controller, person))
            {
                log.LogError("[emote] bind failed; will retry resolution");
                _byPath.Clear(); _byLeaf.Clear(); _dupLeaf.Clear(); _probes.Clear();
                _nextResolve = _t + 2f;
                return;
            }

            _player_owner = player;
            _refs = refs; _controller = controller; _person = person; _animator = animator;
            _t = 0f;
            _nextGenerationCheck = 0f;   // a resolve resets the clock; keep generation checks immediate
            log.LogInfo($"[emote] resolved model: player='{player.name}' animEnabled={controller.IsAnimancerAndAnimatorEnabled} " +
                        $"animator={animator.name} layers={person.Layers.Count}");
            log.LogInfo($"[emote] rig mapping check: {MappingReport()}");
            BuildNativeCatalog();
            log.LogInfo($"[emote] pose player ready ({_pack.Clips.Count} allowlisted LC clips + 0 game clips); " +
                        $"U opens the emote picker (a click plays) - no emote key remains; " +
                        $"list={TotalEmotes} ({EmoteCatalog.List()})");
        }
        catch (Exception e) { _log.LogError($"[emote] resolve threw: {e}"); }
    }

    private string MappingReport()
    {
        try
        {
            var sb = new StringBuilder();
            int found = 0, missing = 0;
            var miss = new List<string>();
            foreach (var kv in _pack.Map)
            {
                if (_byLeaf.ContainsKey(kv.Value) && !_dupLeaf.Contains(kv.Value)) found++;
                else { missing++; if (miss.Count < 12) miss.Add(kv.Value); }
            }
            sb.Append($"{found}/{_pack.Map.Count} mapped bones resolved on the target rig");
            if (missing > 0) sb.Append($", MISSING: {string.Join(",", miss.ToArray())}");
            return sb.ToString();
        }
        catch (Exception e) { return "map report failed: " + e.Message; }
    }

    private void DumpRig(Animator animator)
    {
        try
        {
            var a = animator;
            if (a == null) return;
            string avatar = "null";
            try { var av = a.avatar; if (av != null) avatar = $"'{av.name}' isHuman={av.isHuman} isValid={av.isValid}"; }
            catch (Exception e) { avatar = "unreadable(" + e.Message + ")"; }
            var root = a.transform;
            _byPath.Clear(); _byLeaf.Clear(); _dupLeaf.Clear();
            Collect(root, root, 0);
            _log.LogInfo($"[emote:rig] root='{root.name}' avatar={avatar} transforms={_byPath.Count}");

            _probes.Clear();
            foreach (var want in new[] { "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head",
                                         "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
                                         "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
                                         "thigh_l", "calf_l", "foot_l", "thigh_r", "calf_r", "foot_r" })
                if (Find(want) != null) _probes.Add(want);
            _log.LogInfo($"[emote:rig] probes: {string.Join(", ", _probes.ToArray())}");
        }
        catch (Exception e) { _log.LogWarning($"[emote] rig dump failed: {e.Message}"); }
    }

    private void Collect(Transform root, Transform t, int depth)
    {
        if (depth > 32 || t == null) return;
        string path = RelPath(root, t);
        if (!_byPath.ContainsKey(path)) _byPath[path] = t;
        string leaf = t.name;
        if (_byLeaf.ContainsKey(leaf)) _dupLeaf.Add(leaf); else _byLeaf[leaf] = t;
        int n = t.childCount;
        for (int i = 0; i < n; i++) Collect(root, t.GetChild(i), depth + 1);
    }

    private Transform Find(string bone)
    {
        Transform t;
        if (_byPath.TryGetValue(bone, out t)) return t;
        if (!_dupLeaf.Contains(bone) && _byLeaf.TryGetValue(bone, out t)) return t;
        return null;
    }

    private static string RelPath(Transform root, Transform t)
    {
        if (t == root) return "";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && p != root && guard++ < 24) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ evidence

    private Quaternion[] SampleProbes()
    {
        var arr = new Quaternion[_probes.Count];
        for (int i = 0; i < _probes.Count; i++)
        {
            var t = Find(_probes[i]);
            arr[i] = t != null ? t.localRotation : Quaternion.identity;
        }
        return arr;
    }

    private Camera _verifyCam;
    private RenderTexture _verifyRt;
    private GameObject _verifyCamGo;

    // The verification camera and its RenderTexture are native objects that outlive a managed
    // generation, so every path that makes them must be able to destroy them again.
    private void DestroyVerifyResources()
    {
        var rt = _verifyRt; _verifyRt = null;
        var cam = _verifyCam; _verifyCam = null;
        var go = _verifyCamGo; _verifyCamGo = null;
        if (cam != null) { try { cam.targetTexture = null; } catch { } }
        if (rt != null)
        {
            try { rt.Release(); } catch (Exception e) { _log.LogWarning($"[emote:shot] verify RT release failed: {e.Message}"); }
            try { UnityEngine.Object.Destroy(rt); } catch (Exception e) { _log.LogWarning($"[emote:shot] verify RT destroy failed: {e.Message}"); }
        }
        if (go != null) { try { UnityEngine.Object.Destroy(go); } catch (Exception e) { _log.LogWarning($"[emote:shot] verify camera destroy failed: {e.Message}"); } }
        else if (cam != null) { try { UnityEngine.Object.Destroy(cam); } catch { } }
    }

    // Native-object census used as teardown evidence: counts only, no game state is changed.
    private static int CountNamed<T>(string name) where T : UnityEngine.Object
    {
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<T>());
            if (all == null) return -1;
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var o = all[i].TryCast<T>();
                if (o != null && string.Equals(o.name, name, StringComparison.Ordinal)) n++;
            }
            return n;
        }
        catch { return -1; }
    }

    // A dedicated camera in front of the model: the desktop is locked and the baked mirror pose
    // does not frame a freshly spawned player, so this is the only reliable way to see the pose.
    private void CaptureVerify(string tag)
    {
        var log = _log;
        try
        {
            var root = _animator != null ? _animator.transform
                     : (_player_owner != null ? _player_owner.transform : null);
            if (root == null) { log.LogWarning($"[emote:shot] {tag}: no model root"); return; }
            if (_verifyCam == null)
            {
                var go = new GameObject("AlterumEmoteVerifyCam");
                _verifyCamGo = go;
                go.layer = 0;
                _verifyCam = go.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
                if (_verifyCam == null) { log.LogWarning("[emote:shot] camera creation failed"); DestroyVerifyResources(); return; }
                _verifyCam.enabled = false;
                int mask = (1 << 14) | (1 << 0) | (1 << 23) | (1 << 20);
                mask &= ~(1 << 24); mask &= ~(1 << 5); mask &= ~(1 << 15);
                _verifyCam.cullingMask = mask;
                _verifyCam.clearFlags = CameraClearFlags.SolidColor;
                _verifyCam.backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                _verifyCam.nearClipPlane = 0.05f;
                _verifyCam.farClipPlane = 100f;
                _verifyCam.fieldOfView = 45f;
                _verifyCam.allowHDR = false;
                _verifyCam.allowMSAA = false;
                _verifyCam.useOcclusionCulling = false;
                _verifyCam.depth = -100;
                _verifyRt = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32) { name = "AlterumEmoteVerifyRT", useMipMap = false, autoGenerateMips = false };
                if (!_verifyRt.Create()) { log.LogWarning("[emote:shot] verify RT create failed"); DestroyVerifyResources(); return; }
                _verifyCam.targetTexture = _verifyRt;
                log.LogInfo($"[emote:shot] verify camera mask=0x{mask:X8} rt={_verifyRt.width}x{_verifyRt.height}");
            }

            Vector3 focus = root.position + Vector3.up * 1.10f;
            Vector3 fwd = root.forward;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 eye = focus + fwd * 2.7f - right * 1.5f + Vector3.up * 0.30f;
            _verifyCam.transform.position = eye;
            _verifyCam.transform.rotation = Quaternion.LookRotation((focus - eye).normalized, Vector3.up);
            _verifyCam.Render();

            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = _verifyRt;
                tex = new Texture2D(_verifyRt.width, _verifyRt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, _verifyRt.width, _verifyRt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                var png = ImageConversion.EncodeToPNG(tex);
                string path = Path.Combine(log.ScreensDir, $"emote_{tag}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                log.LogInfo($"[emote:shot] {tag} -> {path} focus={focus} eye={eye} modelFwd={fwd}");
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) { try { UnityEngine.Object.Destroy(tex); } catch { } }
            }
        }
        catch (Exception e) { log.LogWarning($"[emote:shot] {tag} failed: {e.Message}"); }
    }

    internal void CaptureMirror(string tag)
    {
        var log = _log;
        RenderTexture rt = null;
        try { rt = _mirrorTexture?.Invoke(); } catch { }
        if (rt == null) { log.LogWarning($"[emote:shot] {tag}: no mirror RenderTexture"); return; }
        var prev = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var png = ImageConversion.EncodeToPNG(tex);
            string path = Path.Combine(log.ScreensDir, $"emote_{tag}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
            log.LogInfo($"[emote:shot] {tag} -> {path} ({rt.width}x{rt.height})");
        }
        catch (Exception e) { log.LogWarning($"[emote:shot] {tag} failed: {e.Message}"); }
        finally
        {
            RenderTexture.active = prev;
            if (tex != null) { try { UnityEngine.Object.Destroy(tex); } catch { } }
        }
    }

    // ------------------------------------------------------------------ game-built-in (native) emotes

    // One clip of the game's own animation set offered by the emote cycle. The manifest is
    // explicit: only a clip listed with class "emote" is offered, everything else in the player
    // graph is reported with the reason it was rejected (see ClassifyNative).
    internal sealed class NativeEmote
    {
        internal string Name = "";
        internal string Klass = "";
        internal string Reason = "";
        internal string Layer = "";      // "body" | "upper"
        internal AnimationClip Clip = null;
        internal float Len = 0f;
        internal bool Loop = false;
        internal AvatarMask Mask = null; // layer mask for upper-body clips (null = full body)
        internal string Id => "native:" + Name;
    }

    internal const string NativePrefix = "native:";

    // Always empty in this build: nothing can add to it (see BuildNativeCatalog). The field and the
    // NativeEmote shape stay so the retained native diagnostics compile; they read zero entries.
    private readonly List<NativeEmote> _nativeEmotes = new();
    private readonly List<AnimationClip> _graphClips = new();
    private bool _nativeCatalog;

    private int TotalEmotes => PackCount;

    private string IdAt(int index)
    {
        if (index < 0 || index >= TotalEmotes) return "";
        return _pack.Clips[index].Name;
    }

    private int PackCount => _pack != null ? _pack.Clips.Count : 0;

    // Zero native ids are offered (EmoteCatalog.NativeCount): the catalogue is the two pack clips
    // and nothing else. The list and the classification below are kept because the census and the
    // sweep/film drivers read them, but no path can put an entry into this list any more.
    private const string UpperPrefix = "FemaleAdventure_Hands_TP_";

    private List<AnimationClip> _loadedClips = new();

    // Gather and classify the model's own graph plus the loaded player-character content. One build
    // per model generation (the graph dies with it). Nothing is offered: this is read-only census
    // input for [emote:census] and for the classify lines.
    private void BuildNativeCatalog()
    {
        var log = _log;
        _nativeCatalog = true;
        try
        {
            _graphClips.Clear();
            _nativeEmotes.Clear();
            if (_person == null) return;

            // ---- source 1: the model's own Animancer graph --------------------------------
            var list = new Il2CppSystem.Collections.Generic.List<AnimationClip>();
            try
            {
                var coll = list.TryCast<Il2CppSystem.Collections.Generic.ICollection<AnimationClip>>();
                _person.GatherAnimationClips(coll);
            }
            catch (Exception e) { log.LogWarning($"[emote:native] gather failed: {e.Message}"); }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
            {
                AnimationClip c = null;
                try { c = list[i]; } catch { }
                if (c == null || string.IsNullOrEmpty(c.name) || !names.Add(c.name)) continue;
                _graphClips.Add(c);
            }
            _graphClips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            log.LogInfo($"[emote:native] graph source: clips={_graphClips.Count}");

            // ---- source 2: loaded player-character content -------------------------------
            // Still enumerated (read-only) so the census and the classify lines keep their input.
            AvatarMask handsMask = null;
            try
            {
                var l1 = _person.Layers.Count > 1 ? _person.Layers[1] : null;
                if (l1 != null) handsMask = l1.Mask;
            }
            catch { }
            log.LogInfo($"[emote:native] hands layer mask='{(handsMask == null ? "none" : handsMask.name)}' " +
                        $"({(handsMask != null ? "arms-only layering" : "no mask: full body")}); not used: no native emote is offered");

            _loadedClips.Clear();
            var all = SceneCache.Scan(Il2CppType.Of<AnimationClip>());
            var loadedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.Length; i++)
            {
                AnimationClip c = null;
                try { c = all[i].TryCast<AnimationClip>(); } catch { }
                if (c == null || string.IsNullOrEmpty(c.name) || !loadedNames.Add(c.name)) continue;
                _loadedClips.Add(c);
            }
            _loadedClips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            log.LogInfo($"[emote:native] loaded clip source: clips={_loadedClips.Count}");

            // The catalogue is closed: no entry is added, in either direction. Everything the two
            // sources carry is classified and logged as considered-but-not-offered.
            log.LogInfo($"[emote:native] catalogue: offered={_nativeEmotes.Count} nativeIds=[] " +
                        $"(closed by the emote allowlist; {EmoteCatalog.Describe(_pack)}); " +
                        $"considered graph={_graphClips.Count} loaded={_loadedClips.Count}");

            // ---- classification of everything that was considered ------------------------
            for (int i = 0; i < _graphClips.Count; i++)
            {
                var c = _graphClips[i];
                var (klass, reason) = ClassifyNative(c.name);
                log.LogInfo($"[emote:native] classify source=graph '{c.name}' len={c.length:F3} " +
                            $"loop={(c.isLooping ? 1 : 0)} class={klass} offered=0 reason=\"{reason}\"");
            }
            for (int i = 0; i < _loadedClips.Count; i++)
            {
                var c = _loadedClips[i];
                var (klass, reason) = ClassifyPlayerClip(c.name);
                if (klass == "skip") continue;
                log.LogInfo($"[emote:native] classify source=loaded '{c.name}' len={c.length:F3} " +
                            $"loop={(c.isLooping ? 1 : 0)} class={klass} offered=0 reason=\"{reason}\"");
            }
        }
        catch (Exception e) { log.LogError($"[emote:native] catalogue build threw: {e}"); }
    }

    // Rejection reasons for the model's own graph. Mechanically derived from the clip role.
    private static (string klass, string reason) ClassifyNative(string name)
    {
        if (name.StartsWith("Hands_FPS", StringComparison.Ordinal))
            return ("unusable", "first-person hands clip; the third-person player model has no matching rig for it");
        if (name.StartsWith("CommonAdventure_ThirdView_Movement_", StringComparison.Ordinal) ||
            name.StartsWith("Common_ThirdView_Movement_", StringComparison.Ordinal))
            return ("unusable", "locomotion blend-tree state authored for the movement mixer; not a self-contained pose");
        if (name.EndsWith(" Loop", StringComparison.Ordinal))
            return ("unusable", "locomotion blend-tree state authored for the movement mixer; not a self-contained pose");
        return ("unusable", "not in the curated emote manifest");
    }

    // Classification for clips in the loaded player-character families. "skip" means the clip is
    // not player-character content at all and is only listed in the LOADEDCLIP census.
    private static (string klass, string reason) ClassifyPlayerClip(string name)
    {
        if (name.StartsWith(UpperPrefix, StringComparison.Ordinal))
        {
            string rest = name.Substring(UpperPrefix.Length);
            if (rest.EndsWith("_TakingOut", StringComparison.Ordinal))
                return ("unusable", "transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn");
            if (rest.EndsWith("_Attack_1", StringComparison.Ordinal) || rest.EndsWith("_Attack_2", StringComparison.Ordinal) ||
                rest.EndsWith("_Attack_Throw", StringComparison.Ordinal))
                return ("emote", "one-shot weapon action; arms-only clip, layered over the body pose");
            if (rest.EndsWith("_Using", StringComparison.Ordinal) || rest.EndsWith("_Using_Quick", StringComparison.Ordinal) ||
                rest.EndsWith("_Purification", StringComparison.Ordinal) || rest.EndsWith("_Burning", StringComparison.Ordinal))
                return ("emote", "one-shot item-use action; arms-only clip, layered over the body pose");
            if (rest.EndsWith("_Idle", StringComparison.Ordinal) || rest.EndsWith("_Idle_ThrowPrepare", StringComparison.Ordinal) ||
                rest.EndsWith("_IdleLowered", StringComparison.Ordinal))
                return ("emote", "held-item pose loop; arms-only clip, layered over the body pose");
            if (rest.StartsWith("DoingSomething", StringComparison.Ordinal))
                return ("emote", "deliberate scripted action for the upper body; arms-only clip, layered over the body pose");
            return ("unusable", "not in the curated upper-body manifest");
        }
        if (name.StartsWith("FemaleAdventure_Movement_TP_", StringComparison.Ordinal))
            return ("unusable", "locomotion/crawl state for the movement mixer; not a self-contained pose");
        if (name.StartsWith("FemaleAdventure_Body_TP_", StringComparison.Ordinal) ||
            name.StartsWith("FemaleAdventure_TP_", StringComparison.Ordinal) ||
            name.StartsWith("FemaleAdventure_Exile_TP_", StringComparison.Ordinal))
            return ("unusable", "scripted sequence (kill/victim/exile); it assumes another character's position and state");
        if (name.StartsWith("Female_Hands_TP_", StringComparison.Ordinal) ||
            name.StartsWith("Male_Hands_TP_", StringComparison.Ordinal) ||
            name.StartsWith("MaleAdventure_Hands_TP_", StringComparison.Ordinal))
            return ("duplicate", "same animation as the FemaleAdventure set, authored for another body variant; offered once");
        if (name.StartsWith("Female_", StringComparison.Ordinal) || name.StartsWith("Male_", StringComparison.Ordinal) ||
            name.StartsWith("MaleAdventure_", StringComparison.Ordinal) || name.StartsWith("Hands_", StringComparison.Ordinal) ||
            name.StartsWith("Common_", StringComparison.Ordinal) || name.StartsWith("CommonAdventure_", StringComparison.Ordinal) ||
            name.StartsWith("Crouch ", StringComparison.Ordinal) || name.StartsWith("Idle ", StringComparison.Ordinal) ||
            name.StartsWith("Walk ", StringComparison.Ordinal) || name.StartsWith("Jog ", StringComparison.Ordinal) ||
            name.StartsWith("Run ", StringComparison.Ordinal))
            return ("unusable", "player-character clip outside the curated manifest (locomotion, first-person hands, or scripted)");
        return ("skip", "not player-character animation");
    }

    // Request ids resolve through the allowlist only. A `native:<Name>` id (or any other stale id)
    // has no index: it is rejected here instead of being mapped onto a survivor.
    private bool TryIndexOfEmote(string key, out int index)
    {
        index = -1;
        if (_pack == null || string.IsNullOrEmpty(key)) return false;
        if (!EmoteCatalog.Contains(key)) return false;
        index = _pack.IndexOf(key);
        return index >= 0;
    }

    private float ProbePelvisY()
    {
        var p = Find("pelvis");
        return p != null ? p.localPosition.y : 0f;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "clip";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ teardown

    // Every generation retires what it owns: the lease and pelvis pose (PosePlayer.Dispose), the
    // native sampler/hierarchy, and the verification camera + RenderTexture. The summary line is
    // the evidence that a teardown left nothing behind.
    internal void Dispose()
    {
        int layerCount = -1;
        try { layerCount = _person != null ? _person.Layers.Count : -1; } catch { }
        int writeFrames = _player != null ? _player.WriteFrames : 0;
        bool leaseHeldBefore = _player != null && _player.LeaseHeld;
        bool ownedBefore = SkeletonOwned;

        try { _player?.Dispose(); }
        catch (Exception e) { _log.LogWarning($"[emote] dispose of the pose player failed: {e.Message}"); }
        bool leaseHeldAfter = _player != null && _player.LeaseHeld;
        string animatorAfter = "none";
        try { if (_animator != null) animatorAfter = _animator.enabled ? "true" : "false"; } catch { animatorAfter = "dead"; }

        try { DestroyVerifyResources(); } catch (Exception e) { _log.LogWarning($"[emote:shot] verify resource teardown failed: {e.Message}"); }

        int layersAfter = -1;
        try { layersAfter = _person != null ? _person.Layers.Count : -1; } catch { }
        _log.LogInfo($"[emote:teardown] id={_id} writes={writeFrames} leaseHeld={leaseHeldBefore}->{leaseHeldAfter} " +
                     $"skeletonOwned={ownedBefore} animatorEnabled={animatorAfter} " +
                     $"layers={layerCount}->{layersAfter} " +
                     $"verifyCams={CountNamed<Camera>("AlterumEmoteVerifyCam")} " +
                     $"verifyRts={CountNamed<RenderTexture>("AlterumEmoteVerifyRT")} " +
                     $"samplerRigs={CountNamed<GameObject>("AlterumEmoteSampleRig")} nativeOffered={_nativeEmotes.Count}");

        _nativeCatalog = false; _nativeEmotes.Clear(); _graphClips.Clear();
        _person = null; _controller = null; _refs = null; _player_owner = null;
        _player = null;
    }
}
