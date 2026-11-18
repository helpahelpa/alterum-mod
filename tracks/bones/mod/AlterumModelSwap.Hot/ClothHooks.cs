using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using MagicaCloth2;

namespace AlterumModelSwap.Hot;

// Call counters for the MagicaCloth2 update phases.
//
// Everything the manager reports (Time, Team, TeamData) looks healthy while no cloth moves, so the
// question is whether the simulation phase is invoked at all. These are plain Harmony postfixes on
// the public manager entry points; each only increments a counter, and they are removed on dispose.
internal static class ClothHooks
{
    private static Harmony _harmony;
    private static bool _installed;
    private static readonly Dictionary<string, long> _total = new();
    private static readonly Dictionary<string, long> _last = new();

    private static void Bump(string k)
    {
        _total.TryGetValue(k, out long v);
        _total[k] = v + 1;
    }

    internal static void Install(IHostContext log)
    {
        if (_installed) return;
        var h = new Harmony("com.user0.alterum.clothhooks." + Guid.NewGuid().ToString("N"));
        int ok = 0, fail = 0;
        void Patch(Type t, string m, HarmonyMethod post)
        {
            try
            {
                var mi = AccessTools.Method(t, m);
                if (mi == null) { log.LogWarning($"[hooks] {t.Name}.{m} not found"); fail++; return; }
                h.Patch(mi, postfix: post);
                ok++;
            }
            catch (Exception e) { log.LogWarning($"[hooks] {t.Name}.{m} failed: {e.Message}"); fail++; }
        }

        Patch(typeof(TeamManager), nameof(TeamManager.AlwaysTeamUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_Always)));
        Patch(typeof(ClothManager), nameof(ClothManager.ClothUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_ClothUpdate)));
        Patch(typeof(ClothManager), nameof(ClothManager.OnEarlyClothUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_Early)));
        Patch(typeof(ClothManager), nameof(ClothManager.OnFirstPreUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_FirstPre)));
        Patch(typeof(ClothManager), nameof(ClothManager.OnBeforeLateUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_BeforeLate)));
        Patch(typeof(ClothManager), nameof(ClothManager.OnAfterLateUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_AfterLate)));
        Patch(typeof(TeamManager), "SimulationStepTeamUpdate", new HarmonyMethod(typeof(ClothHooks), nameof(P_StepTeam)));
        Patch(typeof(TeamManager), "SimulationPostTeamUpdate", new HarmonyMethod(typeof(ClothHooks), nameof(P_PostTeam)));
        Patch(typeof(TeamManager), "SimulationCalcCenterAndInertiaAndWind", new HarmonyMethod(typeof(ClothHooks), nameof(P_CalcCenter)));
        Patch(typeof(TimeManager), nameof(TimeManager.FrameUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_TimeFrame)));
        Patch(typeof(TimeManager), nameof(TimeManager.AfterFixedUpdate), new HarmonyMethod(typeof(ClothHooks), nameof(P_TimeFixed)));
        Patch(typeof(TimeManager), nameof(TimeManager.AfterRenderring), new HarmonyMethod(typeof(ClothHooks), nameof(P_TimeRender)));
        Patch(typeof(TeamManager), nameof(TeamManager.CameraCullingPreProcess), new HarmonyMethod(typeof(ClothHooks), nameof(P_CullPre)));
        Patch(typeof(TeamManager), nameof(TeamManager.CameraCullingPostProcess), new HarmonyMethod(typeof(ClothHooks), nameof(P_CullPost)));

        try
        {
            var getter = AccessTools.Method(typeof(SimulationManager), "get_SimulationStepCount");
            if (getter != null) { h.Patch(getter, postfix: new HarmonyMethod(typeof(ClothHooks), nameof(P_GetSteps))); ok++; }
            else { log.LogWarning("[hooks] SimulationStepCount getter not found"); fail++; }
        }
        catch (Exception e) { log.LogWarning($"[hooks] step getter failed: {e.Message}"); fail++; }
        try
        {
            var setter = AccessTools.Method(typeof(SimulationManager), "set_SimulationStepCount");
            if (setter != null) { h.Patch(setter, postfix: new HarmonyMethod(typeof(ClothHooks), nameof(P_SetSteps))); ok++; }
            else { log.LogWarning("[hooks] SimulationStepCount setter not found"); fail++; }
        }
        catch (Exception e) { log.LogWarning($"[hooks] step setter failed: {e.Message}"); fail++; }

        _harmony = h;
        _installed = true;
        log.LogInfo($"[hooks] installed {ok} counter(s), {fail} failure(s)");
    }

    internal static void Uninstall(IHostContext log = null)
    {
        if (!_installed) return;
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _installed = false;
        _total.Clear();
        _last.Clear();
        // Forced steps are a global read override on SimulationManager.get_SimulationStepCount.
        // Leaving it set meant a later probe silently inherited a forced step count; reset it as
        // part of uninstalling the hooks that made it meaningful.
        int forced = ForceSteps;
        ForceSteps = 0;
        LastStepCount = -1;
        LastTimeLine = "";
        if (forced > 0)
            log?.LogInfo($"[hooks] uninstall reset forcesteps {forced} -> 0 (read override removed)");
    }

    // One line per second with the number of calls since the previous line.
    internal static string Line()
    {
        if (!_installed) return "";
        var sb = new StringBuilder(" hooks[");
        foreach (var kv in _total)
        {
            _last.TryGetValue(kv.Key, out long prev);
            _last[kv.Key] = kv.Value;
            sb.Append($"{kv.Key}+{kv.Value - prev} ");
        }
        sb.Append(']');
        if (LastStepCount >= 0) sb.Append($" stepCount={LastStepCount} [{LastTimeLine}]");
        return sb.ToString();
    }

    static void P_Always() => Bump("always");
    static void P_ClothUpdate() => Bump("clothUpdate");
    static void P_TimeFrame() => Bump("timeFrame");
    static void P_StepTeam() => Bump("stepTeam");

    internal static int ForceSteps;
    internal static bool Installed => _installed;
    static void P_GetSteps(ref int __result)
    {
        Bump("getSteps");
        if (ForceSteps > 0) __result = ForceSteps;
    }
    static void P_SetSteps(SimulationManager __instance)
    {
        Bump("setSteps");
        try { LastStepCount = __instance.SimulationStepCount; } catch { }
        try
        {
            var t = MagicaManager.Time;
            if (t != null) LastTimeLine = $"dt={t.SimulationDeltaTime:F4} max={t.MaxDeltaTime:F3} gts={t.GlobalTimeScale:F2} fixed={t.FixedUpdateCount}";
        }
        catch { }
    }

    internal static int LastStepCount = -1;
    internal static string LastTimeLine = "";
    static void P_PostTeam() => Bump("postTeam");
    static void P_CalcCenter() => Bump("calcCenter");
    static void P_TimeFixed() => Bump("timeFixed");
    static void P_TimeRender() => Bump("timeRender");
    static void P_Early() => Bump("early");
    static void P_FirstPre() => Bump("firstPre");
    static void P_BeforeLate() => Bump("beforeLate");
    static void P_AfterLate() => Bump("afterLate");
    static void P_CullPre() => Bump("cullPre");
    static void P_CullPost() => Bump("cullPost");
}
