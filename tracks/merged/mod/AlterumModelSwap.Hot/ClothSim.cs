using System;
using System.Collections.Generic;
using System.IO;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime.InteropTypes;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// STATUS (bones track, jiggle pass): this intervention is DEFAULT OFF and buys nothing. The
// "registration starts the simulation" claim in the block below was retracted - see
// notes/cloth-lifecycle.md: SimulationStepTeamUpdate is invoked 0 times/s and SimulationStepCount
// stays 0 across 11 hot reloads and a scene change, and with the animator frozen an impulse is
// 0.0000 deg over 25 and 50 frames. With no simulation the per-frame bookkeeping is pure overhead
// (0.2-0.5 ms/frame, no CPU delta). The jiggle feature is now built on our own solver
// (JiggleSolver.cs). This type is kept because it is harmless and reversible at the enum level,
// and because re-testing a future build costs one config line:
//   clothsim.request: enabled=1
//
// ---- original note (kept for the evidence trail) ---------------------------------------------
// MagicaCloth2 never simulates in this build until its custom PlayerLoop registration is redone.
//
// Evidence (tracks/bones/notes/cloth-simulation.md):
//   * every manager phase (ClothUpdate / AlwaysTeamUpdate / TimeManager.FrameUpdate / the culling
//     passes) is invoked every frame, but TeamManager.SimulationStepTeamUpdate is invoked zero
//     times and SimulationManager.SimulationStepCount stays 0;
//   * the shipped breast cloths, the local player's own hair cloth and a fresh BoneCloth this mod
//     creates itself all write the rest pose back and never move - with camera culling Off, no
//     renderer, no owner, no animator;
//   * one call to MagicaManager.SetUpdateLocation() starts the simulation, and it keeps running
//     afterwards even when the enum is put back.
//
// So the trigger is the re-registration, not the location value. Two consequences drive this
// type:
//
//   1. Registration is GLOBAL and irreversible from managed code. MagicaCloth2 exposes
//      SetUpdateLocation / InitCustomGameLoop, whose loop building (SetCustomGameLoop /
//      AddPlayerLoop / CheckRegist) is private, and it has no RemovePlayerLoop counterpart. Once
//      the loop holds valid delegates the teams step every frame. Restoring the UpdateLocation
//      enum afterwards does NOT undo that (measured; see notes/cloth-simulation.md). We therefore
//      do not pretend a rollback is possible: Rollback() restores the enum and says in the log
//      what it does and does not do. The reversible control is the runtime opt-out below.
//
//   2. The registration must be manager-scoped. A scene transition re-creates the MagicaCloth
//      managers and the loop is left with stale delegates: the simulation silently stops, and a
//      hot-loaded generation that only tried six sweeps at startup never recovers (observed: the
//      first cold start after the six-attempt version logged attempts 1..6 in the Menu scene,
//      then entered the Lobby scene and reported SimulationStepCount=0 for the rest of the run).
//      So: wait until the manager is READY, register at most once per manager generation, and
//      treat a re-created manager as a new generation that gets exactly one fresh attempt.
//
// Lifecycle rules, in order:
//   * readiness gate  - Application.isPlaying, MagicaManager.isPlaying, time/team/simulation/cloth
//                       valid and at least one team registered. Nothing is consumed before this.
//   * already-running - if SimulationStepCount is observed >0 on a fresh generation, the loop is
//                       already doing the job: leave it alone (this is what makes repeated hot
//                       reloads safe; see notes/cloth-simulation.md for the callback counts).
//   * one attempt     - SetUpdateLocation(BeforeLateUpdate) exactly once per generation key
//                       (manager object identity + a re-init epoch for same-instance rebuilds).
//   * confirmation    - log the observed step callbacks after 1.5 s, success or failure. A failed
//                       attempt is not repeated for the same generation.
//
// Runtime opt-out: a persistent request file "clothsim.request" in ScreensDir with
//   enabled=0            disable the intervention for the rest of the process (default 1)
//   enabled=1            re-enable it (a new registration attempt follows)
//   monitor=1            install the per-second step-callback counters (ClothMetrics)
//   guard=1              enable the gross-displacement guard (default on)
// The file is not deleted (unlike jiggle.request): it is the runtime switch, not a one-shot.
// Its state is logged on every change and at the first observation.
internal static class ClothSim
{
    internal const string RequestName = "clothsim.request";
    internal static readonly string ModuleId = Guid.NewGuid().ToString("N").Substring(0, 6);

    // The phase that demonstrably steps the teams. The value is not itself the trigger; the
    // re-registration is. Kept at BeforeLateUpdate because that is the phase that was measured.
    private static readonly TimeManager.UpdateLocation Target = TimeManager.UpdateLocation.BeforeLateUpdate;
    internal static TimeManager.UpdateLocation SimTarget => Target;

    // --- runtime switch -----------------------------------------------------------------
    // DEFAULT OFF: the intervention was measured to buy no simulation (see the header), so the
    // shipped default is "do not touch the manager"; a request file can turn it back on.
    internal const bool DefaultEnabled = false;
    internal static bool Enabled = DefaultEnabled;   // intervention allowed (clothsim.request enabled)
    internal static bool GuardEnabled = true;     // gross-displacement guard
    private static bool _defaultLogged;
    private static DateTime _requestMtime = DateTime.MinValue;
    private static float _nextPoll;
    private static bool _requestLogged;

    // --- generation / registration state ------------------------------------------------
    internal static long GenId;                   // monotonic id for logs, one per manager generation
    private static long _generationKey = -1;      // identity of the generation we have seen
    private static long _handledKey = -1;         // generation we have already decided on
    private static bool _registered;              // we called SetUpdateLocation for _handledKey
    private static bool _confirmed;               // stepping observed after our registration
    private static float _registeredAt;
    private static float _nextConfirmAt;
    private static int _stepsMax;
    private static int _stepsNonZeroFrames;

    // manager identity + re-init epoch (same instances, managers rebuilt: cloth set emptied)
    private static long _teamPtr, _timePtr, _simPtr, _clothPtr;
    private static int _epoch;
    private static int _prevRunning = -1;
    private static float _zeroRunningSince = -1f;
    private static bool _prevIsPlaying;

    // readiness pacing
    private static float _nextWaitLog;
    private static float _nextStateLog;

    // rollback bookkeeping
    private static bool _priorCaptured;
    private static int _priorLocation = -1;
    private static int _currentLocation = -1;

    // per-second observation (cheap; also feeds ClothMetrics)
    internal static int LastRunning, LastTeams, LastSteps;
    internal static bool LastLoopReg, LastReady;
    internal static string LastState = "";

    // --- guard ---------------------------------------------------------------------------
    private sealed class Sample
    {
        public Vector3 World;
        public bool HasWorld;
        public readonly Dictionary<int, Vector3> Local = new();
        public string Path = "";
        public float LastLog;
    }
    private static readonly Dictionary<int, Sample> _samples = new();
    private static readonly HashSet<Transform> _used = new();
    private static float _nextGuard;
    private static readonly List<int> _stale = new();

    // =====================================================================================
    // per-frame entry point (ModelSwapRuntime.Update)
    // =====================================================================================
    internal static void Tick(IHostContext log)
    {
        try
        {
            TickCore(log);
        }
        catch (Exception e)
        {
            try { log.LogWarning($"[bounds:cloth-sim] tick failed: {e.GetType().Name}: {e.Message}"); } catch { }
        }
        try { Guard(log); } catch (Exception e) { log.LogWarning($"[bounds:cloth-sim] guard failed: {e.Message}"); }
    }

    private static void TickCore(IHostContext log)
    {
            float now = Time.unscaledTime;
            if (!_defaultLogged)
            {
                _defaultLogged = true;
                log.LogInfo($"[bounds:cloth-sim] DEFAULT OFF (module={ModuleId}): the shipped cloth never steps a " +
                            $"team in this build, so registering its PlayerLoop bought 0 simulation at 0.2-0.5 ms/frame " +
                            $"of overhead (notes/cloth-lifecycle.md). Jiggle is served by JiggleSolver instead. " +
                            $"{RequestName} enabled=1 re-enables this path for a re-test.");
            }
            if (now >= _nextPoll)
            {
                _nextPoll = now + 1f;
                PollRequest(log);
            }

            int running = CountRunning(out int teams);
            LastRunning = running;
            LastTeams = teams;
            if (!Ready(log, running, out string why))
            {
                LastReady = false;
                if (now >= _nextWaitLog)
                {
                    _nextWaitLog = now + 5f;
                    log.LogInfo($"[bounds:cloth-sim] waiting for the MagicaCloth manager: {why} " +
                                $"(module={ModuleId} attemptsConsumed=0)");
                }
                return;
            }
            LastReady = true;
            Observe(now, running);
            long key = MakeKey();
            if (key != _generationKey)
            {
                StartGeneration(log, key, running, teams);
            }

            int steps = ReadSteps();
            LastSteps = steps;
            if (steps > 0) { _stepsNonZeroFrames++; if (steps > _stepsMax) _stepsMax = steps; }

            if (!Enabled)
            {
                // The switch owns the state: no registration, no confirmation churn.
                if (now >= _nextStateLog)
                {
                    _nextStateLog = now + 30f;
                    log.LogInfo($"[bounds:cloth-sim] state enabled=0 gen={GenId} module={ModuleId} " +
                                $"mgr={Hex(_teamPtr)} running={running} teams={teams} steps={steps} " +
                                $"loopReg={LastLoopReg} loc={LocName(_currentLocation)} registered={_registered}");
                }
                return;
            }

            if (_handledKey == key)
            {
                if (!_confirmed && _registered && _stepsMax > 0)
                {
                    _confirmed = true;
                    log.LogInfo($"[bounds:cloth-sim] gen={GenId} registration CONFIRMED: " +
                                $"stepCount={_stepsMax} observed, running={running} teams={teams} " +
                                $"loopReg={LastLoopReg} loc={LocName(_currentLocation)}");
                }
                else if (!_confirmed && _registered && now >= _nextConfirmAt)
                {
                    _confirmed = true;   // decided; a failed attempt is not repeated for this gen
                    log.LogWarning($"[bounds:cloth-sim] gen={GenId} registration NOT confirmed after " +
                                   $"{now - _registeredAt:0.0}s: stepCount still 0, running={running} teams={teams} " +
                                   $"loopReg={LastLoopReg} loc={LocName(_currentLocation)}. One attempt per " +
                                   $"manager generation; the next attempt needs a re-created manager (scene change). " +
                                   $"See [bounds:steps] for the callback counts.");
                }
                return;
            }

            // A fresh generation. Respect a simulation that is already stepping: the loop holds
            // working delegates (most likely a previous module generation registered it), so
            // touching SetUpdateLocation again would only risk a second callback registration.
            // Require two observations so a stale count from the frame before a scene change does
            // not count as "already running".
            if (_stepsNonZeroFrames >= 2)
            {
                _handledKey = key;
                _confirmed = true;
                log.LogInfo($"[bounds:cloth-sim] gen={GenId} simulation already stepping " +
                            $"(steps={steps}, observed {_stepsNonZeroFrames} frame(s), running={running}, " +
                            $"teams={teams}, loopReg={LastLoopReg}, loc={LocName(_currentLocation)}); " +
                            $"no re-registration");
                return;
            }

            Register(log, key, running, teams, steps);
    }

    // =====================================================================================
    // readiness
    // =====================================================================================
    private static bool Ready(IHostContext log, int running, out string why)
    {
        why = "";
        try
        {
            if (!Application.isPlaying) { why = "Application.isPlaying=false"; return false; }
            if (!MagicaManager.isPlaying) { why = "MagicaManager.isPlaying=false"; return false; }
            var time = MagicaManager.Time;
            if (time == null) { why = "MagicaManager.Time=null"; return false; }
            if (!time.isValid) { why = "TimeManager not valid"; return false; }
            var team = MagicaManager.Team;
            if (team == null) { why = "MagicaManager.Team=null"; return false; }
            if (!team.IsValid()) { why = "TeamManager not valid"; return false; }
            if (team.TeamCount <= 0) { why = $"no teams registered (TeamCount={team.TeamCount})"; return false; }
            if (MagicaManager.Simulation == null) { why = "SimulationManager=null"; return false; }
            if (MagicaManager.Cloth == null) { why = "ClothManager=null"; return false; }
            if (MagicaManager.Time.SimulationDeltaTime <= 0f)
            {
                why = $"SimulationDeltaTime={MagicaManager.Time.SimulationDeltaTime} (manager still initialising)";
                return false;
            }
            if (running <= 0) { why = "no running cloth yet"; return false; }
        }
        catch (Exception e) { why = $"{e.GetType().Name}: {e.Message}"; return false; }
        return true;
    }

    private static int CountRunning(out int teams)
    {
        teams = 0;
        int running = 0;
        try
        {
            var set = MagicaManager.Cloth?.boneClothSet;
            if (set != null)
                foreach (var cp in set)
                {
                    try
                    {
                        teams++;
                        if (cp != null && cp.IsRunning()) running++;
                    }
                    catch { }
                }
        }
        catch { }
        return running;
    }

    private static int ReadSteps()
    {
        try { return MagicaManager.Simulation.SimulationStepCount; } catch { return 0; }
    }

    private static long MakeKey()
    {
        // Manager object identity. The three managers are created/destroyed together; hashing the
        // trio makes an accidental pointer reuse by a single manager less likely to look identical.
        return (_teamPtr * 397) ^ (_timePtr * 31) ^ _simPtr ^ (_clothPtr << 7) ^ (_epoch * 0x9E3779B1L);
    }

    private static void Observe(float now, int running)
    {
        _teamPtr = Ptr(MagicaManager.Team);
        _timePtr = Ptr(MagicaManager.Time);
        _simPtr = Ptr(MagicaManager.Simulation);
        _clothPtr = Ptr(MagicaManager.Cloth);

        bool playing = false;
        try { playing = MagicaManager.isPlaying; } catch { }

        // Same manager instances but the cloth set emptied and rebuilt: MagicaCloth2 re-created its
        // teams in place (scene load / wardrobe teardown). That is a new manager generation for our
        // purposes - the loop delegates are as stale as after an instance swap.
        if (running == 0)
        {
            if (_prevRunning > 0) _zeroRunningSince = now;
        }
        else if (_prevRunning == 0 && _zeroRunningSince > 0f)
        {
            // The cloth set emptied and was rebuilt: MagicaCloth2 re-initialised its teams in
            // place without swapping the manager objects. The loop delegates captured before
            // that rebuild are as stale as after an instance swap, so this is a new generation.
            _epoch++;
        }
        else if (_prevRunning < 0 && running == 0)
        {
            _zeroRunningSince = now;   // start of the process: no cloth built yet
        }

        if (_prevRunning >= 0 && playing && !_prevIsPlaying) _epoch++;
        _prevRunning = running;
        _prevIsPlaying = playing;
    }

    private static void StartGeneration(IHostContext log, long key, int running, int teams)
    {
        _generationKey = key;
        GenId++;
        _handledKey = -1;
        _registered = false;
        _confirmed = false;
        _stepsNonZeroFrames = 0;
        _stepsMax = 0;

        try { LastLoopReg = CheckLoopRegistered(); } catch { LastLoopReg = false; }
        try { _currentLocation = (int)MagicaManager.GetUpdateLocation(); } catch { _currentLocation = -1; }

        log.LogInfo($"[bounds:cloth-sim] manager generation gen={GenId} module={ModuleId} epoch={_epoch} " +
                    $"mgr={Hex(_teamPtr)}/{Hex(_timePtr)}/{Hex(_simPtr)}/{Hex(_clothPtr)} " +
                    $"ready=1 teams={teams} running={running} loopReg={LastLoopReg} " +
                    $"loc={LocName(_currentLocation)} enabled={(Enabled ? 1 : 0)}");
    }

    private static bool CheckLoopRegistered()
    {
        try
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            return MagicaManager.CheckRegist(ref loop);
        }
        catch { return false; }
    }

    // =====================================================================================
    // registration
    // =====================================================================================
    private static void Register(IHostContext log, long key, int running, int teams, int steps)
    {
        _handledKey = key;
        _registered = true;
        _registeredAt = Time.unscaledTime;
        _nextConfirmAt = _registeredAt + 1.5f;
        try
        {
            int prior = (int)MagicaManager.GetUpdateLocation();
            if (!_priorCaptured) { _priorLocation = prior; _priorCaptured = true; }
            MagicaManager.SetUpdateLocation(Target);
            _currentLocation = (int)MagicaManager.GetUpdateLocation();
            LastLoopReg = CheckLoopRegistered();
        }
        catch (Exception e)
        {
            _currentLocation = -1;
            log.LogWarning($"[bounds:cloth-sim] gen={GenId} SetUpdateLocation threw: {e.GetType().Name}: " +
                           $"{e.Message}; attempt consumed for this manager generation");
            return;
        }

        log.LogInfo($"[bounds:cloth-sim] gen={GenId} REGISTERED custom game loop: " +
                    $"updateLocation {LocName(_priorLocation)} -> {LocName(_currentLocation)} " +
                    $"(priorCaptured={( _priorCaptured ? 1 : 0 )}, target={LocName((int)Target)}) " +
                    $"mgr={Hex(_teamPtr)} running={running} teams={teams} stepsBefore={steps} " +
                    $"loopRegBefore={LastLoopReg} (callback rates: [bounds:steps])");
    }

    // Restores the enum only. Honest about what it cannot undo.
    internal static void Rollback(IHostContext log, string why)
    {
        int prior = _priorCaptured ? _priorLocation : -1;
        try
        {
            int before = (int)MagicaManager.GetUpdateLocation();
            if (prior >= 0 && before != prior)
            {
                MagicaManager.SetUpdateLocation((TimeManager.UpdateLocation)prior);
                _currentLocation = (int)MagicaManager.GetUpdateLocation();
                log.LogWarning($"[bounds:cloth-sim] rollback ({why}): updateLocation {LocName(before)} -> " +
                               $"{LocName(_currentLocation)}. The PlayerLoop registration itself is global and " +
                               $"has no managed removal API, so teams that already step keep stepping; " +
                               $"only a restart restores the original (non-simulating) build behaviour.");
            }
            else
            {
                log.LogWarning($"[bounds:cloth-sim] rollback ({why}): updateLocation already " +
                               $"{LocName(before)}; registration (module={ModuleId}, gen={GenId}) remains global.");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"[bounds:cloth-sim] rollback ({why}) failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // Module teardown. The registration is intentionally left in place: un-registering is not
    // possible from managed code, and every extra SetUpdateLocation is a re-registration. The
    // value is put back so the next generation starts from the game's own value; the next
    // generation detects the still-running simulation and does not register again.
    internal static void OnDispose(IHostContext log)
    {
        try
        {
            if (_priorCaptured && _currentLocation != _priorLocation)
            {
                try
                {
                    MagicaManager.SetUpdateLocation((TimeManager.UpdateLocation)_priorLocation);
                    _currentLocation = (int)MagicaManager.GetUpdateLocation();
                    log.LogInfo($"[bounds:cloth-sim] dispose: updateLocation restored to " +
                                $"{LocName(_currentLocation)}; the global registration " +
                                $"(module={ModuleId}, gen={GenId}, registered={_registered}) stays in the " +
                                $"PlayerLoop and stepping is expected to continue - this is documented, not a bug");
                }
                catch (Exception e)
                {
                    log.LogWarning($"[bounds:cloth-sim] dispose: could not restore updateLocation: {e.Message}");
                }
            }
            else
            {
                log.LogInfo($"[bounds:cloth-sim] dispose: module={ModuleId} gen={GenId} registered={_registered} " +
                            $"loc={LocName(_currentLocation)} steps={LastSteps} (registration stays global)");
            }
        }
        catch { }
    }

    // =====================================================================================
    // runtime switch
    // =====================================================================================
    private static void PollRequest(IHostContext log)
    {
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestName); }
        catch { return; }
        try
        {
            if (!File.Exists(path))
            {
                if (_requestLogged && Enabled != DefaultEnabled)
                {
                    Enabled = DefaultEnabled;
                    log.LogInfo($"[bounds:cloth-sim] opt-out file removed: enabled={(Enabled ? 1 : 0)} (default)");
                }
                _requestLogged = false;
                return;
            }
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt == _requestMtime) return;
            _requestMtime = mt;

            bool? enabled = null;
            bool? guard = null;
            foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                bool on = v == "1" || v == "true" || v == "on" || v == "yes";
                switch (k)
                {
                    case "enabled": case "sim": enabled = on; break;
                    case "guard": guard = on; break;
                    default: log.LogWarning($"[bounds:cloth-sim] request key '{k}' unknown; ignored"); break;
                }
            }

            if (guard.HasValue && guard.Value != GuardEnabled)
            {
                GuardEnabled = guard.Value;
                if (!GuardEnabled) { _samples.Clear(); _used.Clear(); }
                log.LogInfo($"[bounds:cloth-sim] guard={(GuardEnabled ? 1 : 0)} (clothsim.request)");
            }
            if (enabled.HasValue && enabled.Value != Enabled)
            {
                Enabled = enabled.Value;
                if (!Enabled)
                {
                    Rollback(log, "runtime opt-out enabled=0");
                    _handledKey = -1;   // re-enabling may register again for the current generation
                    _confirmed = false;
                    log.LogInfo($"[bounds:cloth-sim] opt-out: enabled=0 (clothsim.request) - no registration " +
                                $"while disabled; running cloths={LastRunning} stay at their last behaviour");
                }
                else
                {
                    _handledKey = -1;
                    _confirmed = false;
                    log.LogInfo($"[bounds:cloth-sim] opt-out: enabled=1 (clothsim.request) - a registration " +
                                $"attempt follows if the manager is ready");
                }
            }
            if (!_requestLogged)
            {
                _requestLogged = true;
                log.LogInfo($"[bounds:cloth-sim] request state: enabled={(Enabled ? 1 : 0)} " +
                            $"guard={(GuardEnabled ? 1 : 0)} (clothsim.request)");
            }
        }
        catch (Exception e)
        {
            try { log.LogWarning($"[bounds:cloth-sim] request read failed: {e.Message}"); } catch { }
        }
    }

    // =====================================================================================
    // gross-displacement guard (P1.2.3)
    // =====================================================================================
    // Once a second, for every RUNNING bone cloth: the cloth root's world position and the local
    // positions of the transforms it simulates. A teleport/visibility transition moves the root
    // metres in one sample; a particle blow-up moves a simulated bone far away from where it was
    // a second earlier. Either way it becomes one log line with the path and the magnitude, so an
    // artefact is visible without a full census. Cheap by construction: 1 Hz, <=64 cloths,
    // <=128 transforms each, one shared HashSet.
    private static void Guard(IHostContext log)
    {
        if (!GuardEnabled) return;
        float now = Time.unscaledTime;
        if (now < _nextGuard) return;
        _nextGuard = now + 1f;

        var set = MagicaManager.Cloth?.boneClothSet;
        if (set == null) return;
        int cloths = 0, checkedBones = 0;
        _stale.Clear();
        foreach (var cp in set)
        {
            if (cp == null) continue;
            try { if (!cp.IsRunning()) continue; } catch { continue; }
            if (cloths++ >= 64) break;
            var mc = cp.cloth;
            if (mc == null) continue;
            int id;
            try { id = mc.GetInstanceID(); } catch { continue; }
            if (!_samples.TryGetValue(id, out var s)) { s = new Sample(); _samples[id] = s; }
            s.Path = TPath.Of(mc.transform);
            Vector3 world = mc.transform.position;
            float worldJump = s.HasWorld ? (world - s.World).magnitude : 0f;
            s.World = world;
            s.HasWorld = true;
            if (worldJump > 3f && now - s.LastLog > 2f)
            {
                s.LastLog = now;
                log.LogWarning($"[bounds:cloth-guard] cloth root jumped {worldJump:F2}m in 1s '{s.Path}' " +
                               $"(teleport/culling transition; toggle: clothsim.request guard=0)");
            }

            try
            {
                _used.Clear();
                var used = mc.GetUsedTransform();
                if (used != null)
                    foreach (var t in used) if (t != null) _used.Add(t);
            }
            catch { }

            float maxLocal = 0f; string farBone = "";
            foreach (var t in _used)
            {
                if (t == null) continue;
                int key;
                Vector3 p;
                try { key = t.GetInstanceID(); p = t.localPosition; } catch { continue; }
                checkedBones++;
                if (s.Local.TryGetValue(key, out var prev))
                {
                    float d = (p - prev).magnitude;
                    if (d > maxLocal) { maxLocal = d; farBone = t.name ?? ""; }
                }
                s.Local[key] = p;
            }

            if (maxLocal > 0.5f && now - s.LastLog > 2f)
            {
                s.LastLog = now;
                log.LogWarning($"[bounds:cloth-guard] large simulated-bone displacement " +
                               $"{maxLocal:F2}m on '{farBone}' in 1s cloth='{s.Path}' (sim instability? " +
                               $"toggle: clothsim.request guard=0)");
            }
        }

        // Drop samples for cloths that are gone.
        if (_samples.Count > 0)
        {
            foreach (var kv in _samples)
                if (!MagicaClothLookup.Contains(kv.Key)) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) _samples.Remove(_stale[i]);
        }

        if (now >= _nextGuardLog)
        {
            _nextGuardLog = now + 60f;
            log.LogInfo($"[bounds:cloth-guard] sampling cloths={cloths} transforms={checkedBones} " +
                        $"tracked={_samples.Count} threshold=0.50m/s guard=1");
        }
    }

    private static float _nextGuardLog;

    // Cheap membership test used when pruning guard samples.
    private static class MagicaClothLookup
    {
        internal static bool Contains(int instanceId)
        {
            try
            {
                var set = MagicaManager.Cloth?.boneClothSet;
                if (set == null) return false;
                foreach (var cp in set)
                {
                    try { if (cp?.cloth != null && cp.cloth.GetInstanceID() == instanceId) return true; }
                    catch { }
                }
            }
            catch { }
            return false;
        }
    }

    // =====================================================================================
    // helpers
    // =====================================================================================
    private static long Ptr(Il2CppObjectBase o)
    {
        try { return o == null ? 0 : o.Pointer.ToInt64(); } catch { return 0; }
    }

    internal static string Hex(long v) => v == 0 ? "0" : v.ToString("X");

    internal static string LocName(int v) => v switch
    {
        0 => "AfterLateUpdate",
        1 => "BeforeLateUpdate",
        -1 => "unknown",
        _ => v.ToString(),
    };

    // Small path helper shared with the guard (ClothInternals has one too; this keeps the guard
    // independent of diagnostics being installed).
    private static class TPath
    {
        internal static string Of(Transform t)
        {
            if (t == null) return "null";
            var sb = new System.Text.StringBuilder(t.name);
            var p = t.parent;
            for (int i = 0; p != null && i < 12; i++) { sb.Insert(0, p.name + "/"); p = p.parent; }
            string s = sb.ToString();
            return s.Length <= 120 ? s : "..." + s.Substring(s.Length - 117);
        }
    }
}
