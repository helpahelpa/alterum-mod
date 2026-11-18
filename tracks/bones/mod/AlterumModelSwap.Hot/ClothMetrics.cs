using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Diagnostics for the global cloth intervention (P1.1 callback counts, P1.2 cost envelope).
//
// Driven by a PERSISTENT request file "clothmetrics.request" in ScreensDir, re-read on mtime
// change so the same file survives hot reloads and the counter re-arms on every new module:
//
//   count=1                 install the simulation-callback counters and log one [bounds:steps]
//                           line per second: TeamManager.SimulationStepTeamUpdate calls,
//                           SimulationManager.set_SimulationStepCount calls, AlwaysTeamUpdate and
//                           TimeManager.FrameUpdate calls, plus the fixed-step reconciliation
//                           (sum of Time.deltaTime / SimulationDeltaTime, capped by
//                           maxSimulationCountPerFrame) and the running-cloth count.
//   count=0                 uninstall them.
//   recount=N               re-call MagicaManager.SetUpdateLocation() once a second, N times,
//                           while the counters run. This is the direct test for whether repeated
//                           registration adds a second simulation callback: compare the per-second
//                           stepTeam rate before and after.
//   cost=1                  start a frame-time / CPU measurement window (see keys below).
//   tag=NAME                csv + log tag for the window (default "cost").
//   seconds=S               window length, 3..120 (default 10).
//   cloff=0|1               disable every running MagicaCloth component for the window (the
//                           "simulation off" side of the comparison), restored at the end.
//   kick=0|1                AddForce to every running cloth every 0.5 s (a loaded, not static,
//                           cloth set).
//   fps=0                   leave Application.targetFrameRate alone.
//   fps=-1                  uncapped for the window (removes the mod's 15 fps cap as a confound).
//   fps=N                   set targetFrameRate = N for the window.
//
// What is NOT reachable: Unity.Profiling.ProfilerRecorder is not generated into the game's
// interop assemblies (only UnityEngine.Profiling.Profiler and ProfilerUnsafeUtility are), so
// MagicaCloth2's own ProfilerMarkers (startClothUpdate*Profiler) cannot be sampled. Cost is
// therefore reported as wall frame time + Time.deltaTime + process CPU time (total processor
// time covers MagicaCloth's worker jobs, not just the main thread) and is labelled as such.
internal static class ClothMetrics
{
    private const string RequestName = "clothmetrics.request";

    // ---- counters -----------------------------------------------------------------------
    private static Harmony _harmony;
    private static bool _counting;
    private static long _stepTeam, _postTeam, _setSteps, _always, _frameUpdate;
    private static long _frames;
    private static float _sumDt;
    private static float _sumUdt;
    private static float _nextReport;
    private static DateTime _mtime = DateTime.MinValue;
    private static float _nextPoll;
    private static bool _pollLogged;

    // ---- cost window --------------------------------------------------------------------
    private sealed class ClothState { public MagicaCloth Mc; public bool Enabled; }
    private sealed class Cost
    {
        public string Tag = "cost";
        public float Seconds = 10f;
        public bool ClothOff, Kick;
        public int Fps;
        public double Start, LastWall;
        public long Frames;
        public double CpuStart, CpuLast;
        public readonly List<double> WallMs = new();
        public readonly List<float> DtMs = new();
        public readonly List<long> Steps = new();
        public readonly List<ClothState> Disabled = new();
        public int PriorTarget = -999, PriorVsync = -999;
        public double NextSecond, NextKick;
        public string Scene = "";
    }
    private static Cost _cost;

    internal static bool CostActive => _cost != null;

    // ---- one-shot -----------------------------------------------------------------------
    private static int _recountLeft, _recountTotal;
    private static float _nextRecount;

    // ---- watch (where SimulationStepCount is written / reset) ----------------------------
    private static bool _watching;
    private static float _nextWatch;
    private static long _wf, _wb, _wa, _wset;
    private static int _stepsBeforeFrame = -1, _stepsAfterFrame = -1, _stepsAfterBefore = -1, _stepsAfterAfter = -1;
    private static int _stepsAfterEarly = -1, _stepsAfterFirstPre = -1, _stepsAfterCloth = -1, _stepsAfterAlways = -1;
    private static int _stepsMaxSeen;

    // =====================================================================================
    // per-frame entry point (ModelSwapRuntime.Update, after ClothSim.Tick)
    // =====================================================================================
    internal static void Tick(IHostContext log, float dt)
    {
        try
        {
            float now = Time.unscaledTime;
            if (now >= _nextPoll)
            {
                _nextPoll = now + 0.5f;
                Poll(log);
            }
            if (_counting) CountTick(log, now, dt);
            if (_watching) WatchTick(log, now);
            if (_cost != null) CostTick(log, now);
            RecountTick(log, now);
        }
        catch (Exception e)
        {
            try { log.LogWarning($"[bounds:metrics] tick failed: {e.GetType().Name}: {e.Message}"); } catch { }
        }
    }

    internal static void Shutdown(IHostContext log)
    {
        try
        {
            if (_cost != null) Finish(log, "module teardown");
        }
        catch { }
        try { Uninstall(log); } catch { }
        try { UninstallWatch(log); } catch { }
    }

    // =====================================================================================
    // request
    // =====================================================================================
    private static void Poll(IHostContext log)
    {
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestName); } catch { return; }
        try
        {
            if (!File.Exists(path))
            {
                if (_counting) Uninstall(log);
                return;
            }
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt == _mtime) return;
            _mtime = mt;

            bool? count = null;
            bool? cost = null;
            bool? cloff = null;
            bool? kick = null;
            bool? watch = null;
            bool? loopdump = null;
            int recount = 0, fps = 0;
            string tag = "cost";
            float seconds = 10f;

            foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                bool on = v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                          v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                          v.Equals("on", StringComparison.OrdinalIgnoreCase);
                switch (k)
                {
                    case "count": case "monitor": count = on; break;
                    case "watch": watch = on; break;
                    case "loopdump": loopdump = on; break;
                    case "cost": cost = on; break;
                    case "cloff": cloff = on; break;
                    case "kick": kick = on; break;
                    case "tag": tag = Sanitize(v); break;
                    case "seconds": float.TryParse(v, out seconds); break;
                    case "recount": int.TryParse(v, out recount); break;
                    case "fps": int.TryParse(v, out fps); break;
                    default: log.LogWarning($"[bounds:metrics] request key '{k}' unknown; ignored"); break;
                }
            }

            if (count.HasValue)
            {
                if (count.Value && !_counting) Install(log);
                else if (!count.Value && _counting) Uninstall(log);
            }
            if (watch.HasValue)
            {
                if (watch.Value && !_watching) InstallWatch(log);
                else if (!watch.Value && _watching) UninstallWatch(log);
            }
            if (loopdump == true) DumpPlayerLoop(log);
            if (recount > 0) { _recountLeft = _recountTotal = Mathf.Clamp(recount, 1, 60); _nextRecount = Time.unscaledTime; }
            if (cost.HasValue && cost.Value)
            {
                if (_cost != null) log.LogWarning($"[bounds:cost] a window is already running (tag='{_cost.Tag}'); request ignored");
                else StartCost(log, tag, seconds, cloff == true, kick == true, fps);
            }
            if (!_pollLogged)
            {
                _pollLogged = true;
                log.LogInfo($"[bounds:metrics] request: count={(_counting ? 1 : 0)} window={(_cost != null ? 1 : 0)}");
            }
        }
        catch (Exception e)
        {
            try { log.LogWarning($"[bounds:metrics] request read failed: {e.Message}"); } catch { }
        }
    }

    // =====================================================================================
    // step-callback counters
    // =====================================================================================
    private static void Install(IHostContext log)
    {
        if (_counting) return;
        var h = new Harmony("com.user0.alterum.clothmetrics." + Guid.NewGuid().ToString("N"));
        int ok = 0, fail = 0;
        void Patch(Type t, string m, HarmonyMethod post)
        {
            try
            {
                var mi = AccessTools.Method(t, m);
                if (mi == null) { log.LogWarning($"[bounds:steps] {t.Name}.{m} not found"); fail++; return; }
                h.Patch(mi, postfix: post);
                ok++;
            }
            catch (Exception e) { log.LogWarning($"[bounds:steps] {t.Name}.{m} failed: {e.Message}"); fail++; }
        }
        Patch(typeof(TeamManager), "SimulationStepTeamUpdate", new HarmonyMethod(typeof(ClothMetrics), nameof(P_StepTeam)));
        Patch(typeof(TeamManager), "SimulationPostTeamUpdate", new HarmonyMethod(typeof(ClothMetrics), nameof(P_PostTeam)));
        Patch(typeof(TeamManager), nameof(TeamManager.AlwaysTeamUpdate), new HarmonyMethod(typeof(ClothMetrics), nameof(P_Always)));
        Patch(typeof(TimeManager), nameof(TimeManager.FrameUpdate), new HarmonyMethod(typeof(ClothMetrics), nameof(P_FrameUpdate)));
        try
        {
            var setter = AccessTools.Method(typeof(SimulationManager), "set_SimulationStepCount");
            if (setter != null) { h.Patch(setter, postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(P_SetSteps))); ok++; }
            else { log.LogWarning("[bounds:steps] SimulationStepCount setter not found"); fail++; }
        }
        catch (Exception e) { log.LogWarning($"[bounds:steps] step setter failed: {e.Message}"); fail++; }

        _harmony = h;
        _counting = true;
        _nextReport = Time.unscaledTime + 1f;
        _pollLogged = true;
        log.LogInfo($"[bounds:steps] counters installed ({ok} ok, {fail} failed) module={ClothSim.ModuleId}; " +
                    $"one line per second");
    }

    private static void Uninstall(IHostContext log)
    {
        if (!_counting) return;
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _counting = false;
        _stepTeam = _postTeam = _setSteps = _always = _frameUpdate = _frames = 0;
        _sumDt = _sumUdt = 0f;
        log.LogInfo("[bounds:steps] counters uninstalled");
    }

    static void P_StepTeam() => _stepTeam++;
    static void P_PostTeam() => _postTeam++;
    static void P_SetSteps() => _setSteps++;
    static void P_Always() => _always++;
    static void P_FrameUpdate() => _frameUpdate++;

    // One report per second. "byDt" is the fixed-step reconciliation: the manager accumulates
    // frame deltas and runs clamp(1..maxSteps) steps per manager frame, so over a window the
    // nominal number of steps is sum(dt)/simDt and the hard ceiling is frames*maxSteps (never
    // "one step per rendered frame" - this install runs at ~15 fps against a 60 Hz sim).
    private static void CountTick(IHostContext log, float now, float dt)
    {
        _frames++;
        _sumDt += Time.deltaTime;
        _sumUdt += Time.unscaledDeltaTime;

        if (now < _nextReport) return;
        _nextReport = now + 1f;

        float simDt = -1f;
        int maxSteps = -1, stepsNow = -1;
        string loc = "?", loopReg = "?";
        try { simDt = MagicaManager.Time.SimulationDeltaTime; } catch { }
        try { maxSteps = MagicaManager.Time.maxSimulationCountPerFrame; } catch { }
        try { stepsNow = MagicaManager.Simulation.SimulationStepCount; } catch { }
        try { loc = ClothSim.LocName((int)MagicaManager.GetUpdateLocation()); } catch { }
        try { loopReg = ClothSim.LastLoopReg ? "1" : "0"; } catch { }

        double byDt = simDt > 0 ? _sumDt / simDt : -1;
        long cap = maxSteps > 0 ? _frames * maxSteps : -1;
        double ratio = _setSteps > 0 ? (double)_stepTeam / _setSteps : -1;

        log.LogInfo($"[bounds:steps] gen={ClothSim.GenId} module={ClothSim.ModuleId} " +
                    $"frames={_frames} sumDt={_sumDt:F3} sumUdt={_sumUdt:F3} " +
                    $"stepTeam={_stepTeam} postTeam={_postTeam} setSteps={_setSteps} always={_always} " +
                    $"frameUpdate={_frameUpdate} stepTeamPerSetSteps={(ratio < 0 ? "n/a" : ratio.ToString("F2"))} " +
                    $"byDt={(byDt < 0 ? "n/a" : byDt.ToString("F1"))} cap={(cap < 0 ? "n/a" : cap.ToString())} " +
                    $"simDt={simDt:F4} maxSteps={maxSteps} stepsNow={stepsNow} " +
                    $"running={ClothSim.LastRunning} teams={ClothSim.LastTeams} " +
                    $"ready={(ClothSim.LastReady ? 1 : 0)} loopReg={loopReg} loc={loc} " +
                    $"stepTeamPerFrame={(double)_stepTeam / _frames:F2}");

        _stepTeam = _postTeam = _setSteps = _always = _frameUpdate = _frames = 0;
        _sumDt = 0f; _sumUdt = 0f;
    }

    // Direct multiplicity test: call SetUpdateLocation while the counters run.
    private static void RecountTick(IHostContext log, float now)
    {
        if (_recountLeft <= 0 || now < _nextRecount) return;
        _nextRecount = now + 1f;
        _recountLeft--;
        int index = _recountTotal - _recountLeft;
        try
        {
            int before = (int)MagicaManager.GetUpdateLocation();
            MagicaManager.SetUpdateLocation(ClothSim.SimTarget);
            int after = (int)MagicaManager.GetUpdateLocation();
            int magica = CountMagicaSystems();
            log.LogWarning($"[bounds:steps] recount: explicit SetUpdateLocation " +
                           $"{ClothSim.LocName(before)} -> {ClothSim.LocName(after)} " +
                           $"#{index}/{_recountTotal} magicaSystems={magica}; compare the stepTeam/setSteps rates before and after this line");
        }
        catch (Exception e) { log.LogWarning($"[bounds:steps] recount failed: {e.Message}"); }
    }

    // =====================================================================================
    // PlayerLoop census: how many MagicaCloth systems are installed, and how many times.
    // Repeated SetUpdateLocation calls are the only way a second callback could appear; this
    // counts the actual systems rather than trusting CheckRegist's boolean.
    // =====================================================================================
    private static void DumpPlayerLoop(IHostContext log)
    {
        try
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            int magica = 0, total = 0;
            var sb = new StringBuilder();
            Walk(ref loop, 0, ref magica, ref total, sb, 3);
            log.LogInfo($"[bounds:loopdump] module={ClothSim.ModuleId} gen={ClothSim.GenId} " +
                        $"magicaSystems={magica} totalSystems={total}" + sb);
        }
        catch (Exception e) { log.LogWarning($"[bounds:loopdump] failed: {e.Message}"); }
    }

    private static int CountMagicaSystems()
    {
        try
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            int magica = 0, total = 0;
            Walk(ref loop, 0, ref magica, ref total, null, -1);
            return magica;
        }
        catch { return -1; }
    }

    private static void Walk(ref UnityEngine.LowLevel.PlayerLoopSystem sys, int depth,
                             ref int magica, ref int total, StringBuilder sb, int maxDepth)
    {
        if (depth > 8) return;
        string typeName = "";
        try { typeName = sys.type != null ? sys.type.Name : ""; } catch { }
        string delegateTarget = "";
        bool hasDelegate = false;
        try
        {
            var d = sys.updateDelegate;
            hasDelegate = d != null;
            if (d != null)
            {
                try { var t = d.Target; delegateTarget = t == null ? "" : t.GetIl2CppType().Name; } catch { }
            }
        }
        catch { }
        bool isMagica = typeName.IndexOf("Magica", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        delegateTarget.IndexOf("Magica", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        delegateTarget.IndexOf("__c", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasDelegate || (typeName.Length > 0 && depth > 0))
        {
            total++;
            if (isMagica) magica++;
            if (sb != null && depth <= maxDepth && (isMagica || hasDelegate))
                sb.Append($"\n  d{depth} type='{typeName}' del={(hasDelegate ? "1" : "0")} target='{delegateTarget}'");
        }
        var subs = sys.subSystemList;
        if (subs != null)
            for (int i = 0; i < subs.Length; i++)
            {
                var child = subs[i];
                Walk(ref child, depth + 1, ref magica, ref total, sb, maxDepth);
            }
    }

    // =====================================================================================
    // watch: which phase writes SimulationStepCount and where it goes back to 0
    // =====================================================================================
    private static Harmony _watchHarmony;

    private static void InstallWatch(IHostContext log)
    {
        if (_watching) return;
        var h = new Harmony("com.user0.alterum.clothwatch." + Guid.NewGuid().ToString("N"));
        try
        {
            h.Patch(AccessTools.Method(typeof(TimeManager), nameof(TimeManager.FrameUpdate)),
                    prefix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_FramePre)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Frame)));
            h.Patch(AccessTools.Method(typeof(TimeManager), nameof(TimeManager.AfterFixedUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Fixed)));
            h.Patch(AccessTools.Method(typeof(ClothManager), nameof(ClothManager.ClothUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Cloth)));
            h.Patch(AccessTools.Method(typeof(ClothManager), nameof(ClothManager.OnEarlyClothUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Early)));
            h.Patch(AccessTools.Method(typeof(ClothManager), nameof(ClothManager.OnFirstPreUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_FirstPre)));
            h.Patch(AccessTools.Method(typeof(TeamManager), nameof(TeamManager.AlwaysTeamUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Always)));
            h.Patch(AccessTools.Method(typeof(ClothManager), nameof(ClothManager.OnBeforeLateUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Before)));
            h.Patch(AccessTools.Method(typeof(ClothManager), nameof(ClothManager.OnAfterLateUpdate)),
                    postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_After)));
            var setter = AccessTools.Method(typeof(SimulationManager), "set_SimulationStepCount");
            if (setter != null) h.Patch(setter, postfix: new HarmonyMethod(typeof(ClothMetrics), nameof(W_Set)));
        }
        catch (Exception e) { log.LogWarning($"[bounds:watch] install failed: {e.Message}"); }
        _watchHarmony = h;
        _watching = true;
        _nextWatch = Time.unscaledTime + 1f;
        log.LogInfo("[bounds:watch] installed on FrameUpdate/AfterFixedUpdate/ClothUpdate/OnBefore/OnAfter/setSteps");
    }

    private static void UninstallWatch(IHostContext log)
    {
        if (!_watching) return;
        try { _watchHarmony?.UnpatchSelf(); } catch { }
        _watchHarmony = null;
        _watching = false;
        log.LogInfo("[bounds:watch] uninstalled");
    }

    static int WRead()
    {
        try
        {
            int v = MagicaManager.Simulation.SimulationStepCount;
            if (v > _stepsMaxSeen) _stepsMaxSeen = v;
            return v;
        }
        catch { return -1; }
    }
    static void W_FramePre() { _stepsBeforeFrame = WRead(); }
    static void W_Frame() { _wf++; _stepsAfterFrame = WRead(); }
    static void W_Fixed() => _wb++;
    static void W_Cloth() { _wa++; _stepsAfterCloth = WRead(); }
    static void W_Early() => _stepsAfterEarly = WRead();
    static void W_FirstPre() => _stepsAfterFirstPre = WRead();
    static void W_Always() => _stepsAfterAlways = WRead();
    static void W_Before() => _stepsAfterBefore = WRead();
    static void W_After() => _stepsAfterAfter = WRead();
    static void W_Set() => _wset++;

    private static void WatchTick(IHostContext log, float now)
    {
        if (!_watching || now < _nextWatch) return;
        _nextWatch = now + 1f;
        float fixedCount = -1;
        try { fixedCount = MagicaManager.Time.FixedUpdateCount; } catch { }
        log.LogInfo($"[bounds:watch] frames(timeFrame)={_wf} fixed={_wb} clothUpdate={_wa} setSteps={_wset} " +
                    $"stepsBeforeFrame={_stepsBeforeFrame} stepsAfterFrame={_stepsAfterFrame} " +
                    $"stepsAfterEarly={_stepsAfterEarly} stepsAfterFirstPre={_stepsAfterFirstPre} " +
                    $"stepsAfterCloth={_stepsAfterCloth} stepsAfterAlways={_stepsAfterAlways} " +
                    $"stepsAfterBefore={_stepsAfterBefore} stepsAfterLate={_stepsAfterAfter} maxSeen={_stepsMaxSeen} " +
                    $"fixedCount={fixedCount} stepsNow={ClothSim.LastSteps} gts={(float)SafeGts():F2}");
        _wf = _wb = _wa = _wset = 0;
    }

    private static double SafeGts()
    {
        try { return MagicaManager.GetGlobalTimeScale(); } catch { return -1; }
    }

    // =====================================================================================
    // cost window
    // =====================================================================================
    private static void StartCost(IHostContext log, string tag, float seconds, bool cloff, bool kick, int fps)
    {
        var c = new Cost { Tag = tag, Seconds = Mathf.Clamp(seconds, 3f, 120f), ClothOff = cloff, Kick = kick, Fps = fps };
        try { c.Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
        c.Start = Time.realtimeSinceStartupAsDouble;
        c.LastWall = c.Start;
        c.NextSecond = Time.unscaledTime + 1f;
        c.NextKick = 0;
        c.CpuStart = 0; c.CpuLast = 0;
        try
        {
            var p = Process.GetCurrentProcess();
            c.CpuStart = p.TotalProcessorTime.TotalSeconds;
            c.CpuLast = c.CpuStart;
        }
        catch { }

        if (fps != 0)
        {
            try
            {
                c.PriorTarget = Application.targetFrameRate;
                c.PriorVsync = QualitySettings.vSyncCount;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = fps < 0 ? -1 : fps;
                log.LogInfo($"[bounds:cost] frame cap {c.PriorTarget} (vSync {c.PriorVsync}) -> " +
                            $"{(fps < 0 ? "uncapped" : fps.ToString())} for the window");
            }
            catch (Exception e) { log.LogWarning($"[bounds:cost] frame cap change failed: {e.Message}"); }
        }

        if (cloff)
        {
            try
            {
                var set = MagicaManager.Cloth?.boneClothSet;
                if (set != null)
                    foreach (var cp in set)
                    {
                        if (cp == null) continue;
                        try { if (!cp.IsRunning()) continue; } catch { continue; }
                        var mc = cp.cloth;
                        if (mc == null) continue;
                        c.Disabled.Add(new ClothState { Mc = mc, Enabled = mc.enabled });
                        mc.enabled = false;
                    }
            }
            catch (Exception e) { log.LogWarning($"[bounds:cost] cloff failed: {e.Message}"); }
        }

        int running = ClothSim.LastRunning;
        log.LogInfo($"[bounds:cost] start tag='{c.Tag}' scene='{c.Scene}' seconds={c.Seconds:0.#} " +
                    $"cloff={(cloff ? 1 : 0)} (disabled {c.Disabled.Count} cloth component(s)) kick={(kick ? 1 : 0)} " +
                    $"fps={(fps == 0 ? "unchanged" : fps < 0 ? "uncapped" : fps.ToString())} " +
                    $"running={running} steps={ClothSim.LastSteps} gen={ClothSim.GenId} " +
                    $"loc={SafeLoc()} targetFps={Application.targetFrameRate}");
        _cost = c;
    }

    private static void CostTick(IHostContext log, float now)
    {
        var c = _cost;
        double wallNow = Time.realtimeSinceStartupAsDouble;
        double wallMs = (wallNow - c.LastWall) * 1000.0;
        c.LastWall = wallNow;
        if (wallMs > 0 && wallMs < 5000)
        {
            c.WallMs.Add(wallMs);
            c.DtMs.Add(Time.deltaTime * 1000f);
            c.Steps.Add(ClothSim.LastSteps);
        }
        c.Frames++;
        if (c.Fps != 0)
        {
            // ModelSwapRuntime re-applies its 15 fps cap every 2 s; keep the window at the
            // requested cap so the measurement is not silently clamped mid-window.
            try
            {
                int want = c.Fps < 0 ? -1 : c.Fps;
                if (Application.targetFrameRate != want) Application.targetFrameRate = want;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            }
            catch { }
        }

        if (c.Kick && now >= c.NextKick)
        {
            c.NextKick = now + 0.5;
            try { KickAll(); } catch { }
        }

        if (now >= c.NextSecond)
        {
            c.NextSecond = now + 1f;
            double cpu = 0;
            try
            {
                var p = Process.GetCurrentProcess();
                double cpuNow = p.TotalProcessorTime.TotalSeconds;
                cpu = cpuNow - c.CpuLast;
                c.CpuLast = cpuNow;
            }
            catch { }
            var w = c.WallMs.Count > 0 ? c.WallMs[c.WallMs.Count - 1] : 0;
            log.LogInfo($"[bounds:cost] t={wallNow - c.Start:0.0}s tag='{c.Tag}' frames={c.Frames} " +
                        $"wall(last)={w:F1}ms dt(last)={Time.deltaTime * 1000f:F1}ms cpu={cpu:F2}cores " +
                        $"stepsNow={ClothSim.LastSteps} running={ClothSim.LastRunning}");
        }

        if (wallNow - c.Start >= c.Seconds) Finish(log, null);
    }

    private static void Finish(IHostContext log, string reason)
    {
        var c = _cost;
        _cost = null;
        if (c == null) return;

        // restore in reverse order of mutation
        for (int i = c.Disabled.Count - 1; i >= 0; i--)
        {
            try { if (c.Disabled[i].Mc != null) c.Disabled[i].Mc.enabled = c.Disabled[i].Enabled; } catch { }
        }
        if (c.PriorTarget != -999)
        {
            try
            {
                QualitySettings.vSyncCount = c.PriorVsync;
                Application.targetFrameRate = c.PriorTarget;
            }
            catch { }
        }

        var wall = new List<double>(c.WallMs);
        var dt = new List<float>(c.DtMs);
        wall.Sort(); dt.Sort();
        string csvPath = "";
        try
        {
            csvPath = Path.Combine(log.ScreensDir, c.Tag + ".csv");
            var sb = new StringBuilder("frame,wall_ms,dt_ms,steps\n");
            for (int i = 0; i < c.WallMs.Count; i++)
                sb.Append(i).Append(',').Append(c.WallMs[i].ToString("F3")).Append(',')
                  .Append(c.DtMs[i].ToString("F3")).Append(',')
                  .Append(i < c.Steps.Count ? c.Steps[i] : -1).Append('\n');
            File.WriteAllText(csvPath, sb.ToString());
        }
        catch (Exception e) { log.LogWarning($"[bounds:cost] csv failed: {e.Message}"); }

        double cpuTotal = 0;
        try
        {
            var p = Process.GetCurrentProcess();
            cpuTotal = p.TotalProcessorTime.TotalSeconds - c.CpuStart;
        }
        catch { }
        double wallTotal = 0;
        for (int i = 0; i < c.WallMs.Count; i++) wallTotal += c.WallMs[i];
        wallTotal /= 1000.0;
        double mean = 0; for (int i = 0; i < wall.Count; i++) mean += wall[i];
        mean = wall.Count > 0 ? mean / wall.Count : 0;
        double dtMean = 0; for (int i = 0; i < dt.Count; i++) dtMean += dt[i];
        dtMean = dt.Count > 0 ? dtMean / dt.Count : 0;
        double p50 = Pct(wall, 0.50), p95 = Pct(wall, 0.95), p99 = Pct(wall, 0.99);
        double dtP95 = Pct(dt, 0.95);
        long stepsMean = 0; for (int i = 0; i < c.Steps.Count; i++) stepsMean += c.Steps[i];
        double stepsPerFrame = c.Steps.Count > 0 ? (double)stepsMean / c.Steps.Count : -1;

        string head = reason == null
            ? $"[bounds:cost] finish tag='{c.Tag}' scene='{c.Scene}'"
            : $"[bounds:cost] aborted tag='{c.Tag}' ({reason}) scene='{c.Scene}'";
        log.LogInfo($"{head} frames={c.Frames} wallMs mean={mean:F1} p50={p50:F1} p95={p95:F1} " +
                    $"p99={p99:F1} max={(wall.Count > 0 ? wall[wall.Count - 1] : 0):F1} " +
                    $"dtMs mean={dtMean:F1} p95={dtP95:F1} cpuCores={(wallTotal > 0 ? cpuTotal / wallTotal : 0):F2} " +
                    $"stepsPerFrame={stepsPerFrame:F2} cloff={(c.ClothOff ? 1 : 0)} kick={(c.Kick ? 1 : 0)} " +
                    $"fps={(c.Fps == 0 ? "unchanged" : c.Fps < 0 ? "uncapped" : c.Fps.ToString())} " +
                    $"running={ClothSim.LastRunning} gen={ClothSim.GenId} csv='{csvPath}' " +
                    $"(no ProfilerRecorder markers in the generated interop; cpu covers all threads)");
    }

    private static double Pct(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Round((sorted.Count - 1) * q);
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    private static double Pct(List<float> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Round((sorted.Count - 1) * q);
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    private static void KickAll()
    {
        var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MagicaCloth>());
        for (int i = 0; i < arr.Length; i++)
        {
            var mc = arr[i].TryCast<MagicaCloth>();
            if (mc == null) continue;
            try
            {
                var cp = mc.process;
                if (cp == null || !cp.IsRunning() || !mc.enabled) continue;
                var t = mc.transform;
                mc.AddForce(t != null ? t.right : Vector3.right, 30f, ClothForceMode.VelocityChange);
            }
            catch { }
        }
    }

    private static string SafeLoc()
    {
        try { return ClothSim.LocName((int)MagicaManager.GetUpdateLocation()); } catch { return "?"; }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "cost";
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length && sb.Length < 32; i++)
        {
            char ch = s[i];
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
        }
        return sb.Length == 0 ? "cost" : sb.ToString();
    }
}
