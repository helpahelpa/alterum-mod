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
//   F6          toggle the selected emote (LC port or a game clip, same lease)
//   F7 / F9     cycle the emote list (12 Lethal Company clips + the game's own playable clips)
//   F8          toggle the selected game clip (native) emote explicitly
//
// The LC port never builds AnimationClips and never touches runtimeAnimatorController: the motion
// comes from tools/extract_emotes.py and is applied as bone transforms while the game's own
// animation is suspended by an ownership lease. Game clips take the *same* lease and are written
// to the same skeleton from a scratch sample rig (NativeSampler), so there is exactly one path.
internal sealed class EmoteRuntime
{
    // Diagnostic sweep: DEFAULT OFF and armed per run only. It plays all 12 clips, acquires the
    // gameplay lease and nudges the player 0.6 m, so allowing it to start on its own makes every
    // hot reload hijack a live session. Arm it with a request file line `selftest=1` or with the
    // environment variable ALTERUM_EMOTE_SELFTEST=1; nothing else can turn it on.
    private bool _selfTest;
    private bool _selfTestConfigured;
    private const float SelfTestFirstAt = 6f;
    private const string SelfTestEnvVar = "ALTERUM_EMOTE_SELFTEST";

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
    private int _selfStep;
    private float _nextSelfAt = SelfTestFirstAt;
    private float _nudgeAt = -1f;
    private int _runs;
    private bool _dump;
    private StreamWriter _dumpWriter;
    private int _dumpFrame;

    private int _shotFor = -1;
    private bool _shotAfterStop;
    private float _afterShotAt = -1f;

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

            Keys();
            PollRequestFile();
            if (_manualUntil > 0f && _player.Active && _runTime >= _manualUntil)
            {
                _manualUntil = -1f;
                _log.LogInfo($"[emote:request] manual stop clip='{_player.CurrentClip}' after {_runTime:F3}s");
                _player.Stop("request-window");
            }
            SelfTestDriver();
            NativeSweepDriver();
            NativeFilmDriver();

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
        if (_dump)
        {
            if (_player != null && _player.Active) WriteJointDump();
            else CloseJointDump("inactive");
        }
        if (_player != null && _player.Active && _stripShot < StripAt.Length)
        {
            float frac = _player.Elapsed / Mathf.Max(_player.Duration, 0.05f);
            if (frac >= StripAt[_stripShot])
            {
                int n = _stripShot++;
                CaptureVerify($"{SafeName(_player.CurrentClip)}_r{_runs}_f{n}_{frac:F2}");
                _log.LogInfo($"[emote:sample] run={_runs} clip='{_player.CurrentClip}' strip={n} " +
                             $"t={_player.Elapsed:F3}s dur={_player.Duration:F3}s frac={frac:F3} metrics[{Metrics()}]");
            }
        }
        CaptureDriver();
        TrackTravel();
        if (_shotAfterStop)
        {
            _shotAfterStop = false;
            CaptureVerify($"after{_runs}");
        }
        if (_afterShotAt > 0f && _t >= _afterShotAt)
        {
            _afterShotAt = -1f;
            CaptureVerify($"restored{_runs}");
        }
    }

    private void LoadPack()
    {
        _pack = EmotePack.TryLoad(_log);
        if (_pack == null) { _log.LogWarning("[emote] no emote pack, feature idle"); return; }
        _player = new PosePlayer(_log, _pack);
        _clipIndex = IndexOfClip("Salute");     // Salute: rotation-driven; the IK clips are in Steps
        ConfigureSelfTest();
    }

    // The sweep is never on by default. The environment is read once per runtime so a cold start
    // can arm it; the request file can also arm it at any point of a running session.
    private void ConfigureSelfTest()
    {
        if (_selfTestConfigured) return;
        _selfTestConfigured = true;
        string env = null;
        try { env = Environment.GetEnvironmentVariable(SelfTestEnvVar); } catch { }
        if (env != null && (env.Trim() == "1" || string.Equals(env.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
        {
            _selfTest = true;
            _log.LogInfo($"[emote:selftest] ARMED by {SelfTestEnvVar}={env}; the sweep will take ownership at t={SelfTestFirstAt:F1}s");
            return;
        }
        _log.LogInfo($"[emote:selftest] off (default). The sweep takes ownership and moves the player, so it is " +
                     $"armed per run: 'selftest=1' in {RequestFileName}, or {SelfTestEnvVar}=1 at launch");
    }

    // ------------------------------------------------------------------ request-file control
    // The demo desktop is locked, so no key can be pressed: a recording run starts and stops an
    // emote through "<ScreensDir>/emote.request", deleted on pickup. Line-oriented:
    //   clip=Clap      pack clip name (case-insensitive); absent with stop=1 means "just stop"
    //   repeats=2      how many clip loops to hold before stopping (default 1, 0 = hold forever)
    //   stop=1         release whatever is playing before looking at clip
    // The first request puts this runtime in manual mode permanently (until the next hot reload),
    // which freezes the acceptance sweep so a recording run drives exactly the clip it asked for.
    private const string RequestFileName = "emote.request";
    private const float RequestPollSeconds = 0.25f;
    private float _nextRequestPoll;
    private bool _manual;
    private float _manualUntil = -1f;

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

        string clip = null, badRepeats = null;
        int repeats = 1;
        bool stop = false;
        bool selfTestSeen = false, selfTestOn = false;
        bool nativeSeen = false, nativeOn = false;
        bool baselineSeen = false, baselineOn = false;
        bool dumpSeen = false, dumpOn = false;
        bool p3Seen = false, p3On = true, tipSeen = false, tipOn = true;
        bool legSeen = false, legOn = true, clavSeen = false, clavOn = false;
        bool legTipSeen = false, legTipOn = false;
        bool censusSeen = false, censusOn = false;
        bool sweepSeen = false, sweepOn = false;
        bool filmSeen = false, filmOn = false;
        string filmNames = null;
        string probeName = null;
        int cycleCount = 0;
        // tri-state: -1 keeps each clip's manifest decision, 0/1 force it for the A/B
        int armConformOn = -1; bool armConformSeen = false;
        bool legHintSeen = false, legHintOn = true;
        string capTag = null, phasesRaw = null, yawRaw = null, distRaw = null, aimRaw = null, fovRaw = null;
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
                case "clip": clip = val; break;
                case "repeats":
                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeats))
                    { repeats = 1; badRepeats = val; }
                    break;
                case "stop": stop = val.Length > 0 && val != "0"; break;
                case "selftest": selfTestSeen = true; selfTestOn = val.Length > 0 && val != "0"; break;
                case "census": censusSeen = true; censusOn = val.Length > 0 && val != "0"; break;
                case "nativesweep": sweepSeen = true; sweepOn = val.Length > 0 && val != "0"; break;
                case "nativefilm": filmSeen = true; filmOn = val.Length > 0 && val != "0"; break;
                case "filmnames": filmNames = val; break;
                case "probe": probeName = val; break;
                case "cycle": int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out cycleCount); break;
                case "native": nativeSeen = true; nativeOn = val.Length > 0 && val != "0"; break;
                case "baseline": baselineSeen = true; baselineOn = val.Length > 0 && val != "0"; break;
                case "dump": dumpSeen = true; dumpOn = val.Length > 0 && val != "0"; break;
                case "pelvis3": p3Seen = true; p3On = val.Length > 0 && val != "0"; break;
                case "tiprot": tipSeen = true; tipOn = val.Length > 0 && val != "0"; break;
                case "legik": legSeen = true; legOn = val.Length > 0 && val != "0"; break;
                case "legtiprot": legTipSeen = true; legTipOn = val.Length > 0 && val != "0"; break;
                case "claviconform": clavSeen = true; clavOn = val.Length > 0 && val != "0"; break;
                case "armconform": armConformSeen = true; armConformOn = val.Length > 0 && val != "0" ? 1 : 0; break;
                case "leghint": legHintSeen = true; legHintOn = val.Length > 0 && val != "0"; break;
                case "cap": capTag = val.Length == 0 || val == "1" ? "lc" : val; break;
                case "phases": phasesRaw = val; break;
                case "camyaw": yawRaw = val; break;
                case "camdist": distRaw = val; break;
                case "camaim": aimRaw = val; break;
                case "camfov": fovRaw = val; break;
                default: _log.LogWarning($"[emote:request] unknown key '{key}' ignored"); break;
            }
        }
        if (badRepeats != null) _log.LogWarning($"[emote:request] repeats='{badRepeats}' is not an integer; using 1");

        if (dumpSeen) { _dump = dumpOn; _log.LogInfo($"[emote:request] joint dump {(_dump ? "ON" : "off")}"); }

        // Full census of the player model's own animation source (layers, states, clips). Never
        // starts playback and never freezes the sweep; purely read-only enumeration.
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

        // Verification / filming are armed per run and stop any earlier run first.
        if (sweepSeen)
        {
            if (sweepOn)
            {
                if (_nativeEmotes.Count == 0) _log.LogWarning("[emote:nativesweep] no game clips in the catalogue; nothing to verify");
                else
                {
                    _nativeSweep = true; _sweepIndex = 0; _sweepPhase = 0; _sweepAt = _t;
                    _log.LogInfo($"[emote:nativesweep] ARMED clips={_nativeEmotes.Count}");
                }
            }
            else { _nativeSweep = false; _log.LogInfo("[emote:nativesweep] disarmed"); }
        }
        if (filmSeen)
        {
            if (filmOn)
            {
                if (_nativeEmotes.Count == 0) _log.LogWarning("[emote:nativefilm] no game clips in the catalogue; nothing to film");
                else
                {
                    _filmNames = filmNames;
                    _nativeFilm = true; _filmIndex = 0; _filmPhase = 0; _filmAt = _t;
                    _log.LogInfo($"[emote:nativefilm] ARMED clips={_nativeEmotes.Count} " +
                                 $"filter={(string.IsNullOrEmpty(_filmNames) ? "all" : _filmNames)}");
                }
            }
            else { _nativeFilm = false; _log.LogInfo("[emote:nativefilm] disarmed"); }
        }
        if (_player != null && (p3Seen || tipSeen || legSeen || clavSeen || legTipSeen || armConformSeen || legHintSeen))
        {
            if (p3Seen) _player.FixPelvis3 = p3On;
            if (tipSeen) _player.FixTipRot = tipOn;
            if (legSeen) _player.FixLegIk = legOn;
            if (legTipSeen) _player.FixLegTipRot = legTipOn;
            if (clavSeen) _player.FixClavicle = clavOn;
            if (legHintSeen) _player.FixLegHint = legHintOn;
            if (armConformSeen) _player.ArmConformOverride = armConformOn;
            _log.LogInfo($"[emote:request] fixes {_player.FixSummary}");
        }

        // Explicit per-run arming of the diagnostic sweep. It is the only way to turn it on at
        // runtime; nothing arms it implicitly, and a reload always starts with it off.
        if (selfTestSeen)
        {
            _selfTest = selfTestOn;
            _selfTestConfigured = true;
            if (selfTestOn)
            {
                // Arming is explicit, so it must beat the "manual clip request froze the sweep"
                // state from an earlier request in the same generation. Without this reset a
                // recording run followed by `selftest=1` would silently do nothing.
                _selfStep = 0;
                _manual = false;
                _stepEndsAt = -1f;
                _nudgeAt = -1f;
                _restoreCheckAt = -1f;
                _restoreCheckAt2 = -1f;
                _nextSelfAt = _t + 2f;
            }
            _log.LogInfo($"[emote:request] self-test {(selfTestOn ? "ARMED for this run" : "disarmed")}");
        }

        // Exercises the same cycle path the F9 key uses (the desktop is locked, so a key cannot be
        // pressed): `cycle=2` advances twice and logs the selected id each time.
        for (int i = 0; i < cycleCount; i++) CycleNext("request-cycle");

        // Read-only drive-mask probe for one clip, by name, from the loaded AnimationClip set.
        if (!string.IsNullOrEmpty(probeName))
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AnimationClip>());
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

        // Manual clip control (the recording path) freezes the acceptance sweep. A bare diagnostic
        // request (selftest/native/baseline) must NOT, or `selftest=1` could never run its sweep.
        bool manualControl = stop || !string.IsNullOrEmpty(clip);
        if (manualControl)
        {
            _manual = true;
            _selfStep = Steps.Length;            // the acceptance sweep stops stepping
            _nextSelfAt = float.MaxValue;
            _stepEndsAt = -1f;
            _manualUntil = -1f;
            _nudgeAt = -1f;
        }

        if (nativeSeen)
        {
            if (nativeOn) StartAt(PackCount + _nativeSel, "request-native");
            else if (_player.Active) _player.Stop("request-native");
        }

        if (baselineSeen && _player != null)
        {
            _player.ForceBaselineFadeTarget = baselineOn;
            _log.LogInfo($"[emote:request] baseline fade target {(baselineOn ? "forced" : "cleared")} for the next stop (P1.5 diagnostic: return to the pre-emote pelvis instead of the animator's value)");
        }

        if (stop && _player.Active) { _player.Unfreeze(); _capOn = false; _player.Stop("request-stop"); }
        if (string.IsNullOrEmpty(clip))
        {
            // A bare `selftest=1` / `native=1` request is the normal case for a diagnostic run: it
            // must not be mistaken for a manual clip request (that would freeze the sweep).
            if (selfTestSeen || nativeSeen || stop)
                _log.LogInfo($"[emote:request] diagnostic request only; no clip requested (active={_player.Active})");
            else
                _log.LogInfo($"[emote:request] manual mode; no clip requested (active={_player.Active})");
            return;
        }

        int index;
        if (!TryIndexOfEmote(clip, out index))
        {
            _log.LogWarning($"[emote:request] clip '{clip}' is not in the emote list (LC names, or native:<Name>); nothing started");
            return;
        }
        if (_player.Active) _player.Stop("request-restart");
        _runs++;
        _shotFor = -1;
        _stripShot = 0;
        StartAt(index, "request");
        float dur = _player.Duration;
        _manualUntil = repeats > 0 ? dur * repeats : -1f;

        // Matched-phase capture: a phase list turns the request into "freeze and shoot" instead of
        // normal playback. Parsed here so a malformed list only disables the capture.
        _capOn = false;
        if (!string.IsNullOrEmpty(phasesRaw))
        {
            var list = new List<float>();
            foreach (var p in phasesRaw.Split(','))
            {
                float v;
                if (!float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                list.Add(Mathf.Repeat(v, 1f));
            }
            if (capTag == null) capTag = "lc";
            if (list.Count > 0)
            {
                _capPhases = list.ToArray();
                _capTag = SafeName(capTag);
                _capIndex = 0; _capWarm = 3; _captures = 0; _capOn = true;
                float.TryParse(yawRaw ?? "25", NumberStyles.Float, CultureInfo.InvariantCulture, out _capYaw);
                float.TryParse(distRaw ?? "4.2", NumberStyles.Float, CultureInfo.InvariantCulture, out _capDist);
                float.TryParse(aimRaw ?? "0.85", NumberStyles.Float, CultureInfo.InvariantCulture, out _capAim);
                float.TryParse(fovRaw ?? "42", NumberStyles.Float, CultureInfo.InvariantCulture, out _capFov);
                _player.FreezeAt(_capPhases[0]);
                _log.LogInfo($"[emote:cap] start clip='{_pack.Clips[_clipIndex].Name}' tag='{_capTag}' phases={list.Count} " +
                             $"yaw={_capYaw:F1} dist={_capDist:F2} aim={_capAim:F2} fov={_capFov:F1} frozen=1");
            }
            else _log.LogWarning($"[emote:request] phases='{phasesRaw}' has no numbers; capture disabled");
        }
        else if (_player != null) _player.Unfreeze();
        _log.LogInfo($"[emote:request] manual start clip='{_pack.Clips[_clipIndex].Name}' repeats={repeats} " +
                     $"dur={dur:F3}s hold={(_manualUntil < 0f ? "until-stop" : _manualUntil.ToString("F3", CultureInfo.InvariantCulture) + "s")} " +
                     $"active={_player.Active}");
    }

    private void Keys()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f6Key != null && kb.f6Key.wasPressedThisFrame) Toggle("F6");
            if ((kb.f7Key != null && kb.f7Key.wasPressedThisFrame) ||
                (kb.f9Key != null && kb.f9Key.wasPressedThisFrame)) CycleNext("F9");
            if (kb.f8Key != null && kb.f8Key.wasPressedThisFrame)
            {
                if (_nativeEmotes.Count == 0) _log.LogWarning("[emote] F8: no game clips in the catalogue");
                else if (_player.Active) _player.Stop("F8");
                else StartAt(PackCount + _nativeSel, "F8");
            }
        }
        catch (Exception e) { _log.LogWarning($"[emote] key read failed: {e.Message}"); }
    }

    private void CycleNext(string trigger)
    {
        int total = TotalEmotes;
        if (total == 0) return;
        _clipIndex = (_clipIndex + 1) % total;
        if (_clipIndex >= PackCount) _nativeSel = _clipIndex - PackCount;
        _log.LogInfo($"[emote] {trigger}: clip index {_clipIndex}/{total} -> '{IdAt(_clipIndex)}'");
    }

    private void Toggle(string trigger)
    {
        if (_player == null) return;
        if (_player.Active) { _player.Stop(trigger); return; }
        if (TotalEmotes == 0) return;
        _runs++;
        _shotFor = -1;
        _shotAfterStop = true;
        StartAt(_clipIndex, trigger);
    }

    private void StartAt(int index, string trigger)
    {
        if (_player == null) return;
        int total = TotalEmotes;
        if (total == 0) return;
        _clipIndex = ((index % total) + total) % total;
        if (_player.Active || _player.LeaseHeld) _player.Stop("restart");
        string id = IdAt(_clipIndex);
        _log.LogInfo($"[emote] requested '{id}' by {trigger} ({(_clipIndex < PackCount ? "LC port" : "game clip")})");
        bool ok;
        if (_clipIndex >= PackCount)
        {
            var ne = _nativeEmotes[_clipIndex - PackCount];
            _nativeSel = _clipIndex - PackCount;
            ok = _player.StartNative(ne.Clip, ne.Id, ne.Mask);
            if (_dump) CloseJointDump("native");   // joint CSV is an LC-pack measurement
        }
        else
        {
            _player.Start(_pack.Clips[_clipIndex].Name);
            ok = _player.Active;
            if (_dump && ok) OpenJointDump(_pack.Clips[_clipIndex].Name);
        }
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

    // Deterministic acceptance sweep: every ported clip is played for the same clip-relative
    // window, so "before" and "after" runs of a measurement change can be diffed directly.
    // Four IK-target clips (Clap/Twerk/Prisyadka/Idle1Fix) + Middle_Finger + the 5 clips the
    // fourth pass audited and enabled. Clips whose authored ChainIKConstraint weight is 0
    // (Prisyadka, Sign, D_Sign) stay rotation-driven by design.
    private static readonly (string clip, float hold, bool nudge)[] Steps =
    {
        ("Salute", 1.6f, false),
        ("Middle_Finger", 1.2f, false),
        ("Sign", 1.6f, false),
        ("D_Sign", 1.6f, false),
        ("The_Griddy", 1.6f, false),
        ("Clap", 1.6f, false),
        ("Twerk", 1.2f, false),
        ("Prisyadka", 2.0f, false),
        ("Idle1Fix", 1.6f, true),
        ("Shy", 1.6f, false),
        ("D_Clap", 1.0f, false),
        ("D_Middle_Finger", 0.9f, false),
    };
    private float _stepEndsAt = -1f;
    private float _restoreCheckAt = -1f;
    private float _restoreCheckAt2 = -1f;
    private int _stripShot;
    private static readonly float[] StripAt = { 0.06f, 0.25f, 0.45f, 0.65f, 0.85f };

    // ---------------------------------------------------------------- matched-phase capture
    // Request-driven (`cap=<tag> phases=... camyaw=...`): holds the clip at each normalised phase
    // and writes one PNG from an LC-shaped camera. Off unless a request asks for it, so normal
    // playback (and every earlier gate) is untouched.
    private bool _capOn;
    private string _capTag = "cap";
    private float[] _capPhases = new float[0];
    private int _capIndex;
    private int _capWarm = 2;
    private float _capYaw = 25f;
    private float _capDist = 4.2f;
    private float _capAim = 0.85f;
    private float _capFov = 42f;
    private int _captures;

    private void CaptureDriver()
    {
        if (!_capOn || _player == null || !_player.Active) return;
        if (_capIndex >= _capPhases.Length) return;
        _player.FreezeAt(_capPhases[_capIndex]);
        if (_capWarm > 0) { _capWarm--; return; }
        float ph = _capPhases[_capIndex];
        CaptureMatch($"{SafeName(_player.CurrentClip)}_{_capTag}_{_capIndex:000}", ph);
        _captures++;
        _capIndex++;
        _capWarm = 2;
        if (_capIndex >= _capPhases.Length)
            _log.LogInfo($"[emote:cap] done clip='{_player.CurrentClip}' shots={_captures} tag='{_capTag}'");
    }

    private void SelfTestDriver()
    {
        if (!_selfTest || _manual) return;

        if (_nudgeAt > 0f && _t >= _nudgeAt)
        {
            _nudgeAt = -1f;
            try
            {
                var tr = _player_owner.transform;
                Vector3 before = tr.position;
                tr.position = before + new Vector3(0.6f, 0f, 0f);
                _log.LogInfo($"[emote:selftest] nudged owner {before} -> {tr.position} to exercise movement cancellation");
            }
            catch (Exception e) { _log.LogWarning($"[emote:selftest] nudge failed: {e.Message}"); }
        }

        bool active = _player.Active;
        if (active)
        {
            if (_t >= _nextHeartbeat) { _nextHeartbeat = _t + 1.0f; Heartbeat(); }
            if (_stepEndsAt > 0f && _t >= _stepEndsAt)
            {
                _stepEndsAt = -1f;
                _player.Stop("selftest-window");
                _afterShotAt = _t + 1.2f;
            }
        }
        else if (_wasActive)
        {
            // One heartbeat of live-game animation between clips, then the next step.
            if (_selfStep >= Steps.Length) _restoreCheckAt2 = _t + 1.5f;
            else _nextSelfAt = _t + 1.0f;
        }
        _wasActive = active;
        if (active) return;

        if (_restoreCheckAt2 > 0f && _t >= _restoreCheckAt2)
        {
            _restoreCheckAt2 = -1f;
            _restorePose = SampleProbes();
            _restoreCheckAt = _t + 0.4f;
            return;
        }
        if (_restoreCheckAt > 0f && _t >= _restoreCheckAt)
        {
            _restoreCheckAt = -1f;
            var b = SampleProbes();
            var sb = new StringBuilder();
            float max = 0f;
            for (int i = 0; i < b.Length && i < _restorePose.Length; i++)
            {
                float d = Quaternion.Angle(_restorePose[i], b[i]);
                max = Mathf.Max(max, d);
                if (d > 0.2f) sb.Append(_probes[i]).Append('=').Append(d.ToString("F2")).Append("deg ");
            }
            _log.LogInfo($"[emote:selftest] RESTORE CHECK game animation running again: max bone change over 0.4s = {max:F2}deg; " +
                         $"bones that moved: {(sb.Length == 0 ? "(none - game animation looks frozen!)" : sb.ToString())}");
            _log.LogInfo("[emote:selftest] sequence complete");
            return;
        }

        if (_t < _nextSelfAt || _selfStep >= Steps.Length) return;
        var step = Steps[_selfStep];
        int stepIndex = _selfStep;
        _selfStep++;
        _runs++;
        _shotFor = -1;
        _stripShot = 0;
        StartAt(IndexOfClip(step.clip), $"selftest#{stepIndex + 1}");
        _stepEndsAt = step.hold > 0f ? _t + step.hold : -1f;
        if (step.nudge) _nudgeAt = _t + 1.0f;
        _nextSelfAt = float.MaxValue;
    }

    private bool _wasActive;

    private Quaternion[] _restorePose;

    private int IndexOfClip(string name)
    {
        for (int i = 0; i < _pack.Clips.Count; i++)
            if (string.Equals(_pack.Clips[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
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
        _nativeCatalog = false; _nativeEmotes.Clear(); _graphClips.Clear(); _nativeSel = 0;
        _nativeSweep = false; _nativeFilm = false;
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
                var provs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CurrentPlayerProvider>());
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
            log.LogInfo($"[emote] pose player ready ({_pack.Clips.Count} LC clips + {_nativeEmotes.Count} game clips); " +
                        "F6 toggles, F7/F9 cycles, F8 toggles the selected game clip");
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
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
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
    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "clip";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    private void OpenJointDump(string clip)
    {
        CloseJointDump("restart");
        try
        {
            var path = Path.Combine(_log.ScreensDir, $"emote_joints_{SafeName(clip)}.csv");
            _dumpWriter = new StreamWriter(path, false) { AutoFlush = true };
            _dumpWriter.WriteLine(PosePlayer.JointCsvHeader());
            _dumpFrame = 0;
            _log.LogInfo($"[emote:dump] OPEN clip='{clip}' path='{path}' fixes[{_player?.FixSummary}]");
        }
        catch (Exception e) { _log.LogWarning($"[emote:dump] open failed: {e.Message}"); _dumpWriter = null; }
    }

    private void WriteJointDump()
    {
        if (_dumpWriter == null) return;
        try
        {
            var row = _player.JointCsvRow(_dumpFrame++);
            if (row != null) _dumpWriter.WriteLine(row);
        }
        catch (Exception e) { _log.LogWarning($"[emote:dump] write failed: {e.Message}"); CloseJointDump("error"); }
    }

    private void CloseJointDump(string reason)
    {
        if (_dumpWriter == null) return;
        try { _dumpWriter.Flush(); _dumpWriter.Dispose(); } catch { }
        _log.LogInfo($"[emote:dump] CLOSE reason={reason} frames={_dumpFrame}");
        _dumpWriter = null;
    }

    private void CaptureVerify(string tag)
    {
        var log = _log;
        try
        {
            var root = _animator != null ? _animator.transform
                     : (_player_owner != null ? _player_owner.transform : null);
            if (root == null) { log.LogWarning($"[emote:shot] {tag}: no model root"); return; }
            EnsureVerifyCam(768, 768, 45f);
            if (_verifyCam == null) return;

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

    // (Re)create the private capture camera at a given shape. Only one shape is live at a time;
    // switching shape (verify 768x768 <-> matcher 960x540) tears the old RT down first.
    private void EnsureVerifyCam(int w, int h, float fov)
    {
        if (_verifyCam != null && _verifyRt != null && _verifyRt.IsCreated()
            && _verifyRt.width == w && _verifyRt.height == h
            && Mathf.Abs(_verifyCam.fieldOfView - fov) < 0.01f) return;
        DestroyVerifyResources();
        try
        {
            var go = new GameObject("AlterumEmoteVerifyCam");
            _verifyCamGo = go;
            go.layer = 0;
            _verifyCam = go.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (_verifyCam == null) { _log.LogWarning("[emote:shot] camera creation failed"); DestroyVerifyResources(); return; }
            _verifyCam.enabled = false;
            int mask = (1 << 14) | (1 << 0) | (1 << 23) | (1 << 20);
            mask &= ~(1 << 24); mask &= ~(1 << 5); mask &= ~(1 << 15);
            _verifyCam.cullingMask = mask;
            _verifyCam.clearFlags = CameraClearFlags.SolidColor;
            _verifyCam.backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
            _verifyCam.nearClipPlane = 0.05f;
            _verifyCam.farClipPlane = 100f;
            _verifyCam.fieldOfView = fov;
            _verifyCam.allowHDR = false;
            _verifyCam.allowMSAA = false;
            _verifyCam.useOcclusionCulling = false;
            _verifyCam.depth = -100;
            _verifyRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "AlterumEmoteVerifyRT", useMipMap = false, autoGenerateMips = false };
            if (!_verifyRt.Create()) { _log.LogWarning("[emote:shot] verify RT create failed"); DestroyVerifyResources(); return; }
            _verifyCam.targetTexture = _verifyRt;
            _log.LogInfo($"[emote:shot] verify camera mask=0x{mask:X8} rt={w}x{h} fov={fov:F1}");
        }
        catch (Exception e) { _log.LogWarning($"[emote:shot] camera setup failed: {e.Message}"); DestroyVerifyResources(); }
    }

    // One frame from the LC-shaped camera at a pinned normalised clip phase: same geometry the LC
    // reference capture used (4.2 m, +0.2 m, 25 deg off forward, 42 deg FOV, 16:9), on our model.
    private void CaptureMatch(string tag, float phase)
    {
        var log = _log;
        try
        {
            var root = _animator != null ? _animator.transform
                     : (_player_owner != null ? _player_owner.transform : null);
            if (root == null) { log.LogWarning($"[emote:cap] {tag}: no model root"); return; }
            EnsureVerifyCam(960, 540, _capFov);
            if (_verifyCam == null) return;
            // LC's capture hid every other renderer (`onlybody on`); the body sits on the mod's
            // Hitbox layer, so a single-layer mask gives the same clean silhouette.
            _verifyCam.cullingMask = 1 << 14;

            Vector3 focus = root.position + Vector3.up * _capAim;
            Vector3 fwd = root.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 dir = Quaternion.Euler(0f, _capYaw, 0f) * fwd;
            Vector3 eye = focus + dir * _capDist + Vector3.up * 0.2f;
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
                string path = Path.Combine(log.ScreensDir, $"emote_cap_{tag}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                log.LogInfo($"[emote:cap] {tag} phase={phase:F4} t={_player.Elapsed:F3}s dur={_player.Duration:F3}s " +
                            $"eye={eye} frozen={(_player.FreezePhase ? 1 : 0)} metrics[{Metrics()}]");
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) { try { UnityEngine.Object.Destroy(tex); } catch { } }
            }
        }
        catch (Exception e) { log.LogWarning($"[emote:cap] {tag} failed: {e.Message}"); }
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
        internal string Name;
        internal string Klass;
        internal string Reason;
        internal string Layer;       // "body" | "upper"
        internal AnimationClip Clip;
        internal float Len;
        internal bool Loop;
        internal AvatarMask Mask;    // layer mask for upper-body clips (null = full body)
        internal string Id => "native:" + Name;
    }

    internal const string NativePrefix = "native:";

    private readonly List<NativeEmote> _nativeEmotes = new();
    private readonly List<AnimationClip> _graphClips = new();
    private bool _nativeCatalog;
    private int _nativeSel;

    private int TotalEmotes => _pack.Clips.Count + _nativeEmotes.Count;

    private string IdAt(int index)
    {
        if (index < 0 || index >= TotalEmotes) return "";
        return index < _pack.Clips.Count ? _pack.Clips[index].Name : _nativeEmotes[index - _pack.Clips.Count].Id;
    }

    private int PackCount => _pack != null ? _pack.Clips.Count : 0;

    // Curated full-body entries from the model's own animancer graph. Only the two idle loops are
    // self-contained poses; the graph's other 39 clips are locomotion blend-tree leaves or
    // first-person hands clips (classified and logged below).
    private static readonly (string name, string klass, string reason)[] NativeBodyManifest =
    {
        ("Common_ThirdView_Movement_Idle", "emote",
            "self-contained standing idle loop (6.13 s); deliberate resting pose, no locomotion input"),
        ("CommonAdventure_ThirdView_Movement_Idle", "emote",
            "self-contained standing idle loop (6.13 s) for the adventure body set"),
    };

    // Upper-body content: the player-character animation set the game has loaded (FemaleAdventure
    // body family), played on the hands layer mask so only the arms move. One representative body
    // family is offered; the Female/Male variants of the same animations are logged as duplicates.
    private const string UpperPrefix = "FemaleAdventure_Hands_TP_";
    private AvatarMask _handsMask;

    private List<AnimationClip> _loadedClips = new();

    // Build the native catalogue from the model's own graph plus the loaded player-character
    // content, then classify everything. One build per model generation: the graph dies with it.
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

            foreach (var m in NativeBodyManifest)
            {
                if (m.klass != "emote") continue;
                AnimationClip found = null;
                for (int i = 0; i < _graphClips.Count; i++)
                    if (string.Equals(_graphClips[i].name, m.name, StringComparison.Ordinal)) { found = _graphClips[i]; break; }
                if (found == null)
                {
                    log.LogWarning($"[emote:native] manifest clip '{m.name}' is not in the player graph; not offered");
                    continue;
                }
                _nativeEmotes.Add(new NativeEmote
                {
                    Name = m.name, Klass = "emote", Reason = m.reason, Layer = "body",
                    Clip = found, Len = found.length, Loop = found.isLooping, Mask = null,
                });
            }

            log.LogInfo($"[emote:native] graph source: clips={_graphClips.Count}");

            // ---- source 2: loaded player-character content (upper body) -------------------
            // The game's hands layers carry no states in the lobby, so their clips are loaded but
            // not instantiated. They are enumerated from the loaded AnimationClip set by the
            // player-character name families, then sampled before being offered.
            _handsMask = null;
            try
            {
                var l1 = _person.Layers.Count > 1 ? _person.Layers[1] : null;
                if (l1 != null) _handsMask = l1.Mask;
            }
            catch { }
            log.LogInfo($"[emote:native] hands layer mask='{(_handsMask == null ? "none" : _handsMask.name)}' " +
                        $"({(_handsMask != null ? "arms-only layering" : "no mask: full body")})");

            _loadedClips.Clear();
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AnimationClip>());
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

            for (int i = 0; i < _loadedClips.Count; i++)
            {
                var c = _loadedClips[i];
                var (klass, reason) = ClassifyPlayerClip(c.name);
                if (klass == "emote")
                {
                    _nativeEmotes.Add(new NativeEmote
                    {
                        Name = c.name, Klass = klass, Reason = reason, Layer = "upper",
                        Clip = c, Len = c.length, Loop = c.isLooping, Mask = _handsMask,
                    });
                }
            }
            if (_nativeSel >= _nativeEmotes.Count) _nativeSel = 0;

            log.LogInfo($"[emote:native] catalogue: offered={_nativeEmotes.Count} " +
                        $"(body={_nativeEmotes.FindAll(e => e.Layer == "body").Count} " +
                        $"upper={_nativeEmotes.FindAll(e => e.Layer == "upper").Count}) " +
                        $"ids=[{string.Join(", ", _nativeEmotes.ConvertAll(e => e.Id).ToArray())}]");

            // ---- classification of everything that was considered ------------------------
            for (int i = 0; i < _graphClips.Count; i++)
            {
                var c = _graphClips[i];
                string klass, reason;
                var off = FindOffered(c.name);
                if (off != null) { klass = "emote"; reason = off.Reason; }
                else (klass, reason) = ClassifyNative(c.name);
                log.LogInfo($"[emote:native] classify source=graph '{c.name}' len={c.length:F3} " +
                            $"loop={(c.isLooping ? 1 : 0)} class={klass} reason=\"{reason}\"");
            }
            for (int i = 0; i < _loadedClips.Count; i++)
            {
                var c = _loadedClips[i];
                var off = FindOffered(c.name);
                string klass, reason;
                if (off != null) { klass = "emote"; reason = off.Reason; }
                else (klass, reason) = ClassifyPlayerClip(c.name);
                if (klass == "skip") continue;
                log.LogInfo($"[emote:native] classify source=loaded '{c.name}' len={c.length:F3} " +
                            $"loop={(c.isLooping ? 1 : 0)} class={klass} reason=\"{reason}\"");
            }
        }
        catch (Exception e) { log.LogError($"[emote:native] catalogue build threw: {e}"); }
    }

    private NativeEmote FindOffered(string name)
    {
        for (int i = 0; i < _nativeEmotes.Count; i++)
            if (string.Equals(_nativeEmotes[i].Name, name, StringComparison.Ordinal)) return _nativeEmotes[i];
        return null;
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

    private bool TryIndexOfEmote(string key, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(key)) return false;
        if (key.StartsWith(NativePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string want = key.Substring(NativePrefix.Length);
            for (int i = 0; i < _nativeEmotes.Count; i++)
                if (string.Equals(_nativeEmotes[i].Name, want, StringComparison.OrdinalIgnoreCase))
                { index = PackCount + i; return true; }
            return false;
        }
        for (int i = 0; i < _pack.Clips.Count; i++)
            if (string.Equals(_pack.Clips[i].Name, key, StringComparison.OrdinalIgnoreCase)) { index = i; return true; }
        // `clip=Common_ThirdView_Movement_Idle` is accepted too: a native id needs the prefix only
        // to disambiguate, and the catalog names are unique against the LC names.
        for (int i = 0; i < _nativeEmotes.Count; i++)
            if (string.Equals(_nativeEmotes[i].Name, key, StringComparison.OrdinalIgnoreCase)) { index = PackCount + i; return true; }
        return false;
    }

    private float ProbePelvisY()
    {
        var p = Find("pelvis");
        return p != null ? p.localPosition.y : 0f;
    }

    // ------------------------------------------------------------------ native verification sweep

    // One request (`nativesweep=1`) plays every offered native emote through the normal path and
    // prints one VERIFY line per clip: start, hold, stop, lease released, animancer re-enabled,
    // restore check (bone motion after release) and pose-vs-entry after release.
    private bool _nativeSweep;
    private int _sweepIndex;
    private int _sweepPhase;
    private float _sweepAt;
    private float _sweepStartT;
    private Quaternion[] _sweepPre, _sweepSettled, _sweepMotion;
    private float _sweepPelvisPre, _sweepPelvisPost;
    private float _sweepHold;
    private bool _sweepStarted;
    private bool _sweepReleased, _sweepAnimEnabled, _sweepOwned;
    private string _sweepClip = "";

    private void NativeSweepDriver()
    {
        if (!_nativeSweep) return;
        if (_t < _sweepAt) return;
        try
        {
            switch (_sweepPhase)
            {
                case 0:
                {
                    if (_sweepIndex >= _nativeEmotes.Count)
                    {
                        _nativeSweep = false;
                        _log.LogInfo($"[emote:nativesweep] COMPLETE clips={_nativeEmotes.Count}");
                        return;
                    }
                    var e = _nativeEmotes[_sweepIndex];
                    if (_player.Active || _player.LeaseHeld)
                    {
                        // Never measure (or start) on top of another run: stop, let the 0.2 s
                        // hand-back finish, then look at the entry pose.
                        _player.Stop("nativesweep-prepare");
                        _sweepAt = _t + 0.7f;
                        return;
                    }
                    _sweepClip = e.Name;
                    _sweepPre = SampleProbes();
                    _sweepPelvisPre = ProbePelvisY();
                    _sweepSettled = null;
                    StartAt(PackCount + _sweepIndex, "nativesweep");
                    _sweepStartT = _t;
                    _sweepHold = 1.6f;
                    bool active = _player.Active;
                    _sweepStarted = active;
                    _log.LogInfo($"[emote:nativesweep] START {_sweepIndex + 1}/{_nativeEmotes.Count} " +
                                 $"clip='{e.Id}' len={e.Len:F3} loop={(e.Loop ? 1 : 0)} active={active} " +
                                 $"lease={_player.LeaseHeld} skeletonOwned={SkeletonOwned}");
                    _sweepPhase = 1;
                    _sweepAt = _t + (active ? 0.8f : 0.1f);
                    break;
                }
                case 1:
                    if (!_sweepStarted)
                    {
                        // Fail closed: a clip that did not start must never print a passing line.
                        _log.LogWarning($"[emote:nativesweep] VERIFY clip='native:{_sweepClip}' start=0 " +
                                        "stop=0 released=True animEnabled=True restoreMotionMaxDeg=0 " +
                                        "movedBones=0/0 poseVsEntryMaxDeg=0 worst='-' pelvisLocalYentry=0 afterRelease=0 delta=0");
                        _sweepIndex++;
                        _sweepPhase = 0;
                        _sweepAt = _t + 0.5f;
                        break;
                    }
                    CaptureVerify($"native_{SafeName(_sweepClip)}");
                    _sweepPhase = 2;
                    _sweepAt = _sweepStartT + 1.6f;
                    break;
                case 2:
                {
                    bool wasActive = _player.Active;
                    _player.Stop("nativesweep");
                    _sweepReleased = !_player.LeaseHeld;
                    _sweepAnimEnabled = _controller != null && _controller.IsAnimancerAndAnimatorEnabled;
                    _sweepOwned = SkeletonOwned;
                    _log.LogInfo($"[emote:nativesweep] STOP clip='native:{_sweepClip}' wasActive={wasActive} " +
                                 $"released={_sweepReleased} animEnabled={_sweepAnimEnabled} " +
                                 $"skeletonOwned={_sweepOwned} hold={(_t - _sweepStartT):F2}s");
                    _sweepPhase = 3;
                    _sweepAt = _t + 1.0f;
                    break;
                }
                case 3:
                    _sweepSettled = SampleProbes();
                    _sweepPelvisPost = ProbePelvisY();
                    _sweepPhase = 4;
                    _sweepAt = _t + 0.4f;
                    break;
                case 4:
                {
                    _sweepMotion = SampleProbes();
                    float motion = 0f, poseDelta = 0f; string worst = "-";
                    int moved = 0;
                    if (_sweepSettled != null && _sweepMotion != null && _sweepPre != null)
                    {
                        for (int i = 0; i < _sweepSettled.Length && i < _sweepMotion.Length; i++)
                        {
                            float d = Quaternion.Angle(_sweepSettled[i], _sweepMotion[i]);
                            motion = Mathf.Max(motion, d);
                            if (d > 0.2f) moved++;
                        }
                        for (int i = 0; i < _sweepPre.Length && i < _sweepSettled.Length; i++)
                        {
                            float d = Quaternion.Angle(_sweepPre[i], _sweepSettled[i]);
                            if (d > poseDelta) { poseDelta = d; worst = i < _probes.Count ? _probes[i] : i.ToString(); }
                        }
                    }
                    _log.LogInfo($"[emote:nativesweep] VERIFY clip='native:{_sweepClip}' start={(_sweepStarted ? 1 : 0)} " +
                                 $"hold={_sweepHold:F2}s stop=1 released={_sweepReleased} " +
                                 $"animEnabled={_sweepAnimEnabled} restoreMotionMaxDeg={motion:F2} " +
                                 $"movedBones={moved}/{(_sweepSettled == null ? 0 : _sweepSettled.Length)} " +
                                 $"poseVsEntryMaxDeg={poseDelta:F2} worst='{worst}' " +
                                 $"pelvisLocalYentry={_sweepPelvisPre:F4} afterRelease={_sweepPelvisPost:F4} " +
                                 $"delta={Mathf.Abs(_sweepPelvisPost - _sweepPelvisPre):F4}");
                    _sweepIndex++;
                    _sweepPhase = 0;
                    _sweepAt = _t + 0.5f;
                    break;
                }
            }
        }
        catch (Exception e)
        {
            _log.LogError($"[emote:nativesweep] threw at phase {_sweepPhase}: {e}");
            _nativeSweep = false;
            try { if (_player.Active) _player.Stop("nativesweep-failed"); } catch { }
        }
    }

    // ------------------------------------------------------------------ native showcase filming

    // `nativefilm=1`: records every offered native emote with ClipRecorder (showcase camera,
    // 2 fps, 4 s) so the poses can be judged from PNG filmstrips without a desktop.
    private bool _nativeFilm;
    private int _filmIndex;
    private int _filmPhase;
    private float _filmAt;
    private string _filmNames;

    private void NativeFilmDriver()
    {
        if (!_nativeFilm) return;
        if (_t < _filmAt) return;
        try
        {
            switch (_filmPhase)
            {
                case 0:
                {
                    if (_filmIndex >= _nativeEmotes.Count)
                    {
                        _nativeFilm = false;
                        _log.LogInfo($"[emote:nativefilm] COMPLETE clips={_nativeEmotes.Count}");
                        return;
                    }
                    var e = _nativeEmotes[_filmIndex];
                    if (!string.IsNullOrEmpty(_filmNames) && !("," + _filmNames + ",").Contains("," + e.Name + ","))
                    {
                        _filmIndex++;
                        _filmAt = _t;
                        return;
                    }
                    var spec = ClipRecorder.ClipSpec.Default($"native_{_filmIndex:00}_{SafeName(e.Name)}");
                    spec.View = "showcase";
                    spec.Seconds = 3f;
                    spec.Fps = 2f;
                    spec.Width = 640;
                    spec.Height = 360;
                    spec.YawAuto = true;
                    spec.Dist = 3.0f;
                    spec.Fov = 40f;
                    spec.Lift = 0.06f;
                    bool rec = ClipRecorder.Record(_log, spec);
                    _log.LogInfo($"[emote:nativefilm] clip='{e.Id}' recording={rec} -> '{spec.Tag}'");
                    StartAt(PackCount + _filmIndex, "nativefilm");
                    _filmPhase = 1;
                    _filmAt = _t + 3.8f;
                    break;
                }
                case 1:
                    if (_player.Active) _player.Stop("nativefilm");
                    _filmPhase = 2;
                    _filmAt = _t + 0.3f;
                    break;
                case 2:
                    if (ClipRecorder.IsRecording) { _filmAt = _t + 0.3f; return; }
                    _filmIndex++;
                    _filmPhase = 0;
                    _filmAt = _t + 0.3f;
                    break;
            }
        }
        catch (Exception e)
        {
            _log.LogError($"[emote:nativefilm] threw at phase {_filmPhase}: {e}");
            _nativeFilm = false;
            try { if (_player.Active) _player.Stop("nativefilm-failed"); } catch { }
        }
    }

    // Kept as the single "is anything writing the skeleton right now" signal. In the merged tree
    // this is what suspends the jiggle solver; here it is the same lease-backed condition.
    internal bool SkeletonOwned
    {
        get { try { return _player != null && _player.Writing; } catch { return false; } }
    }

    internal bool LeaseHeld
    {
        get { try { return _player != null && _player.LeaseHeld; } catch { return false; } }
    }

    // ------------------------------------------------------------------ teardown

    // Every generation retires what it owns: the lease and pelvis pose (PosePlayer.Dispose), the
    // diagnostic Animancer layer, and the verification camera + RenderTexture. The summary line is
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

        try { DestroyVerifyResources(); } catch (Exception e) { _log.LogWarning($"[emote:shot] verify resource teardown failed: {e.Message}"); }

        int layersAfter = -1;
        try { layersAfter = _person != null ? _person.Layers.Count : -1; } catch { }
        _log.LogInfo($"[emote:teardown] id={_id} writes={writeFrames} leaseHeld={leaseHeldBefore}->{leaseHeldAfter} " +
                     $"skeletonOwned={ownedBefore} selftest={(_selfTest ? "on" : "off")} layers={layerCount}->{layersAfter} " +
                     $"verifyCams={CountNamed<Camera>("AlterumEmoteVerifyCam")} " +
                     $"verifyRts={CountNamed<RenderTexture>("AlterumEmoteVerifyRT")} " +
                     $"nativeOffered={_nativeEmotes.Count}");

        _nativeSweep = false; _nativeFilm = false;
        _nativeCatalog = false; _nativeEmotes.Clear(); _graphClips.Clear(); _nativeSel = 0;
        _person = null; _controller = null; _refs = null; _player_owner = null;
        _player = null;
    }
}
