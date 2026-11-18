using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using ConsoleCommands.Network;
using Gameplay.Cycle.Locations;
using Il2CppInterop.Runtime;
using QuestSystem.Core;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Quest admin surface: "<ScreensDir>/quest.request", line-oriented, polled twice a second and
// deleted on pickup (same file protocol as ui.request / prop.request).
//
//   state                 every task and subtask of the location's quest runner, with state and
//                         progress, plus the exit constraints' CanOpen(out reason)
//   completeall           the game's own completion entry point for the current location
//   complete=<taskId>     QuestService.CompleteTask(taskId)
//   subtask=<subTaskId>   QuestRunner.MarkSubTaskCompleted(subTaskId)
//   exit                  LocationExit.CanOpen(out reason) only
//   probe                 resolution diagnostics (cycle/transitioner/location/runner/wrapper)
//
// Why these calls and not a state write: the point is that the game's own downstream effects run
// (rewards, unlock hooks, LocationExitQuestsConstraint seeing the completion). QuestService /
// QuestRunner / QuestsCommandsWrapper are the game's own admin path (the last one is what its
// Quantum Console command "quest" uses); nothing here assigns a TaskState or a progress counter.
//
// Location index resolution mirrors the console wrapper: LocationCycleTransitioner is the
// authoritative object, DayLocationIndex/DayLocation give today's location, and the location's
// QuestLocationProperties hands over the QuestRunner. QuestsCommandsWrapper.GetQuestSessionManager
// is the same lookup, so the wrapper is used as a cross-check when it exists.
internal static class QuestAdmin
{
    internal const string RequestName = "quest.request";

    private const float PollSeconds = 0.5f;
    private static float _nextPoll;
    private static bool _announced;

    internal static void Tick(IHostContext log)
    {
        if (log == null) return;
        if (!_announced)
        {
            _announced = true;
            try { log.LogInfo($"[quest] admin ready request={Path.Combine(log.ScreensDir ?? "", RequestName)}"); }
            catch { }
        }
        float now;
        try { now = Time.unscaledTime; } catch { return; }
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        string dir = log.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        string path = Path.Combine(dir, RequestName);
        string text;
        try
        {
            if (!File.Exists(path)) return;
            text = File.ReadAllText(path);
            try { File.Delete(path); } catch { }
        }
        catch (Exception e) { log.LogWarning($"[quest] read failed: {e.Message}"); return; }
        if (text == null) return;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            try { Run(log, line); }
            catch (Exception e) { log.LogWarning($"[quest] '{line}' threw: {e}"); }
        }
    }

    // Same request line, from the mod console (`quest state|completeall|...`).
    internal static void RunLine(IHostContext log, string action) => Run(log, action);

    private static void Run(IHostContext log, string action)
    {
        int eq = action.IndexOf('=');
        string key = (eq > 0 ? action.Substring(0, eq) : action).Trim().ToLowerInvariant();
        string val = eq > 0 ? action.Substring(eq + 1).Trim() : "";

        var ctx = Resolve(log);
        if (ctx == null || ctx.Runner == null)
        {
            log.LogWarning($"[quest] no quest runner for '{action}' (scene='{SceneName()}')");
            return;
        }

        switch (key)
        {
            case "state": State(log, ctx); return;
            case "probe": Probe(log); return;
            case "exit": ExitReport(log, ctx, "exit"); return;
            case "completeall": CompleteAll(log, ctx); return;
            case "current": CompleteCurrent(log, ctx); return;
            case "complete":
                if (val.Length == 0) { log.LogWarning("[quest] complete needs a task id"); return; }
                CompleteTask(log, ctx, val);
                return;
            case "subtask":
                if (val.Length == 0) { log.LogWarning("[quest] subtask needs a subtask id"); return; }
                CompleteSubTask(log, ctx, val);
                return;
            default:
                log.LogWarning($"[quest] unknown request line '{action}'");
                return;
        }
    }

    // ---- resolution ---------------------------------------------------------------------

    private sealed class Ctx
    {
        public LocationCycleTransitioner Cycle;
        public QuestsCommandsWrapper Wrapper;
        public byte Index;
        public string IndexSource;
        public Location Loc;
        public QuestLocationProperties Props;
        public QuestRunner Runner;
        public string RunnerSource;
    }

    private static Ctx Resolve(IHostContext log)
    {
        var ctx = new Ctx();

        ctx.Wrapper = FindFirst<QuestsCommandsWrapper>();
        try { if (ctx.Wrapper != null && ctx.Wrapper._locationCycleTransitioner != null) ctx.Cycle = ctx.Wrapper._locationCycleTransitioner; }
        catch { }
        if (ctx.Cycle == null) ctx.Cycle = FindFirst<LocationCycleTransitioner>();

        if (ctx.Cycle != null)
        {
            try
            {
                var idx = ctx.Cycle.DayLocationIndex;
                if (idx != null) { ctx.Index = idx.Value; ctx.IndexSource = "DayLocationIndex.Value"; }
            }
            catch (Exception e) { log.LogWarning($"[quest] DayLocationIndex failed: {e.Message}"); }

            try
            {
                ctx.Loc = ctx.Cycle.DayLocation;
                if (ctx.Loc == null) ctx.Loc = ctx.Cycle.GetLocation(ctx.Index);
                if (ctx.Loc != null && ctx.IndexSource == null) { ctx.Index = ctx.Loc.Index; ctx.IndexSource = "DayLocation.Index"; }
            }
            catch (Exception e) { log.LogWarning($"[quest] DayLocation failed: {e.Message}"); }

            if (ctx.Loc != null)
            {
                try { ctx.Props = ctx.Loc.GetProperty<QuestLocationProperties>(); }
                catch (Exception e) { log.LogWarning($"[quest] GetProperty<QuestLocationProperties> failed: {e.Message}"); }
                if (ctx.Props != null)
                {
                    try { ctx.Runner = ctx.Props.QuestRunner; } catch (Exception e) { log.LogWarning($"[quest] props.QuestRunner failed: {e.Message}"); }
                    if (ctx.Runner != null) ctx.RunnerSource = "Location.GetProperty<QuestLocationProperties>().QuestRunner";
                }
            }
        }

        // Cross-check through the console wrapper's own lookup, which is the exact call the game's
        // "quest" command makes. If it resolves a different runner, prefer it (the wrapper is what
        // the game itself would complete through).
        if (ctx.Wrapper != null)
        {
            try
            {
                var viaWrapper = ctx.Wrapper.GetQuestSessionManager(ctx.Index);
                if (viaWrapper != null)
                {
                    if (ctx.Runner == null || ctx.Runner.Pointer != viaWrapper.Pointer)
                        log.LogInfo($"[quest] wrapper lookup runner='{PathOf(viaWrapper)}' differs from location lookup");
                    ctx.Runner = viaWrapper;
                    ctx.RunnerSource = "QuestsCommandsWrapper.GetQuestSessionManager(index)";
                }
            }
            catch (Exception e) { log.LogWarning($"[quest] wrapper GetQuestSessionManager failed: {e.Message}"); }
        }

        if (ctx.Runner == null)
        {
            // Last resort, and only for reporting: the loaded runner in this scene. Completion still
            // goes through that runner's own service, so the API stays the game's.
            var all = SceneCache.Scan(Il2CppType.Of<QuestRunner>());
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i].TryCast<QuestRunner>();
                if (r == null) continue;
                try { if (r.gameObject == null || !r.gameObject.scene.isLoaded) continue; } catch { }
                ctx.Runner = r;
                ctx.RunnerSource = "Resources.FindObjectsOfTypeAll<QuestRunner>[0]";
                break;
            }
        }

        if (log != null && ctx.Runner != null)
            log.LogInfo($"[quest] resolved scene='{SceneName()}' cycle='{PathOf(ctx.Cycle)}' index={ctx.Index} " +
                        $"from={ctx.IndexSource ?? "?"} location='{PathOf(ctx.Loc)}' runner='{PathOf(ctx.Runner)}' " +
                        $"via={ctx.RunnerSource} wrapper={(ctx.Wrapper != null)}");
        return ctx;
    }

    private static T FindFirst<T>() where T : Component
    {
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<T>());
            for (int i = 0; i < all.Length; i++)
            {
                var o = all[i].TryCast<T>();
                if (o == null) continue;
                try { if (o.gameObject == null || !o.gameObject.scene.isLoaded) continue; } catch { }
                return o;
            }
        }
        catch { }
        return null;
    }

    private static string PathOf(Component c)
    {
        try { return c == null || c.transform == null ? "?" : ClothInternals.PathOf(c.transform); }
        catch { return "?"; }
    }

    private static string SceneName()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
        catch { return "?"; }
    }

    // ---- completion ---------------------------------------------------------------------

    private static void CompleteAll(IHostContext log, Ctx ctx)
    {
        ExitReport(log, ctx, "before");
        var svc = Service(ctx);
        int before = CountCompleted(svc);
        log.LogInfo($"[quest] completeall via runner='{PathOf(ctx.Runner)}' api=QuestRunner.CompleteAllTasks() " +
                    $"tasks={TaskCount(svc)} completedBefore={before}");
        string used = "QuestRunner.CompleteAllTasks()";
        try { ctx.Runner.CompleteAllTasks(); }
        catch (Exception e)
        {
            log.LogWarning($"[quest] QuestRunner.CompleteAllTasks threw: {e.Message}");
            used = "QuestService.CompleteAllTasks()";
            try { svc.CompleteAllTasks(); }
            catch (Exception e2)
            {
                log.LogError($"[quest] QuestService.CompleteAllTasks threw: {e2.Message}");
                return;
            }
        }
        log.LogInfo($"[quest] completeall done api={used} completedNow={CountCompleted(svc)}/{TaskCount(svc)} " +
                    $"localCompleted={LocalCompleted(ctx)}");
        State(log, ctx);
        ExitReport(log, ctx, "after");
    }

    // One global task, through the same entry point the console's timed variant drives. Spacing
    // the calls (separate request files) lets the server's activation of the next task and its
    // completion broadcast reach LocalPlayerQuestHandler before the following completion, which is
    // what the exit constraint reads.
    private static void CompleteCurrent(IHostContext log, Ctx ctx)
    {
        var svc = Service(ctx);
        string id = "";
        try { var cur = svc.GetCurrentTasks(); if (cur != null && cur.Count > 0) id = cur[0].Id; } catch { }
        log.LogInfo($"[quest] completecurrent api=QuestService.CompleteCurrentTask() id='{id}'");
        try { svc.CompleteCurrentTask(); }
        catch (Exception e) { log.LogError($"[quest] CompleteCurrentTask threw: {e.Message}"); return; }
        State(log, ctx);
        ExitReport(log, ctx, "after");
    }

    private static void CompleteTask(IHostContext log, Ctx ctx, string id)
    {
        var svc = Service(ctx);
        bool isCurrent = false;
        try { isCurrent = svc.IsCurrentTask(id); } catch { }
        log.LogInfo($"[quest] complete id='{id}' api=QuestService.CompleteTask current={isCurrent}");
        try { svc.CompleteTask(id); }
        catch (Exception e) { log.LogError($"[quest] CompleteTask('{id}') threw: {e.Message}"); return; }
        State(log, ctx);
        ExitReport(log, ctx, "after");
    }

    private static void CompleteSubTask(IHostContext log, Ctx ctx, string id)
    {
        log.LogInfo($"[quest] subtask id='{id}' api=QuestRunner.MarkSubTaskCompleted");
        try { ctx.Runner.MarkSubTaskCompleted(id); }
        catch (Exception e) { log.LogError($"[quest] MarkSubTaskCompleted('{id}') threw: {e.Message}"); return; }
        State(log, ctx);
        ExitReport(log, ctx, "after");
    }

    private static QuestService Service(Ctx ctx)
    {
        try { return ctx.Runner.QuestService; } catch { return null; }
    }

    // ---- state --------------------------------------------------------------------------

    private static void State(IHostContext log, Ctx ctx)
    {
        var svc = Service(ctx);
        var handler = null as LocalPlayerQuestHandler;
        try { handler = ctx.Runner.LocalHandler; } catch { }

        log.LogInfo($"[quest:state] scene='{SceneName()}' location='{(ctx.Loc != null ? SafeName(ctx.Loc) : "?")}' " +
                    $"index={ctx.Index} from={ctx.IndexSource ?? "?"} runner='{PathOf(ctx.Runner)}' " +
                    $"source={ctx.RunnerSource} service={(svc != null)} handler={(handler != null)}");
        if (svc == null) { log.LogWarning("[quest:state] no QuestService"); return; }

        int tasks = 0, active = 0, completed = 0;
        try
        {
            var dict = svc._taskById;
            if (dict != null)
            {
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    var kv = en.Current;
                    string key = kv.Key;
                    var t = kv.Value;
                    if (t == null) continue;
                    tasks++;
                    var st = TaskStateOf(t);
                    if (st == TaskState.Active) active++;
                    if (st == TaskState.Completed) completed++;
                    int cur = 0, req = 0;
                    try { cur = t.CurrentCount; } catch { }
                    try { req = t.ExecutionsNumber != null ? t.ExecutionsNumber.GetValue() : 0; } catch { }
                    bool current = false;
                    try { current = svc.IsCurrentTask(key); } catch { }
                    log.LogInfo($"[quest:task] id='{key}' state={st} progress={cur}/{req} current={current} " +
                                $"title='{SafeStr(t.Title)}'");
                }
            }
        }
        catch (Exception e) { log.LogWarning($"[quest:state] tasks failed: {e.Message}"); }
        log.LogInfo($"[quest:state] tasks={tasks} active={active} completed={completed}");

        // Subtasks: the live SubTask objects hang off LocalPlayerQuestHandler's sequences; the
        // GlobalTask only carries the aggregate progress shown on the HUD.
        try
        {
            if (handler != null)
            {
                var seqs = handler._sequences;
                if (seqs != null)
                {
                    var en = seqs.GetEnumerator();
                    while (en.MoveNext())
                    {
                        var kv = en.Current;
                        string taskId = kv.Key;
                        var seq = kv.Value.Item2;
                        var concrete = seq != null ? seq.TryCast<SubTaskSequence>() : null;
                        if (concrete == null) { log.LogInfo($"[quest:sub] task='{taskId}' sequence=<unresolved>"); continue; }
                        var list = concrete._tasks;
                        if (list == null) { log.LogInfo($"[quest:sub] task='{taskId}' sequence=<no tasks> current={CurrentSubId(seq)}"); continue; }
                        log.LogInfo($"[quest:sub] task='{taskId}' tasks={list.Count} current={CurrentSubId(seq)} started={SafeStarted(seq)}");
                        for (int i = 0; i < list.Count; i++)
                        {
                            var entry = list[i];
                            var st = entry != null ? entry.TryCast<SubTask>() : null;
                            if (st == null) continue;
                            log.LogInfo($"[quest:sub]   task='{taskId}' id='{SafeStr(st.Id)}' state={SafeState(st)} " +
                                        $"current={(CurrentSubId(seq) == SafeStr(st.Id))} title='{SafeStr(st.Title)}'");
                        }
                    }
                }
                else log.LogInfo("[quest:sub] sequences=<null>");
            }
        }
        catch (Exception e) { log.LogWarning($"[quest:sub] subtasks failed: {e.Message}"); }

        log.LogInfo($"[quest:state] localCompleted=[{LocalCompleted(ctx)}]");
        ExitReport(log, ctx, "state");
    }

    private static string CurrentSubId(ISubTaskSequence seq)
    {
        try
        {
            var c = seq.Current;
            var st = c != null ? c.TryCast<SubTask>() : null;
            return st != null ? SafeStr(st.Id) : "-";
        }
        catch { return "?"; }
    }

    private static string SafeStarted(ISubTaskSequence seq)
    {
        try { return seq.IsStarted ? "true" : "false"; } catch { return "?"; }
    }

    private static TaskState TaskStateOf(GlobalTask t)
    {
        try { return t.State; } catch { return TaskState.NotStarted; }
    }

    private static TaskState SafeState(SubTask s)
    {
        try { return s.State; } catch { return TaskState.NotStarted; }
    }

    private static int TaskCount(QuestService svc)
    {
        try { return svc._taskById != null ? svc._taskById.Count : 0; } catch { return 0; }
    }

    private static int CountCompleted(QuestService svc)
    {
        int n = 0;
        try
        {
            var dict = svc._taskById;
            if (dict == null) return 0;
            var en = dict.GetEnumerator();
            while (en.MoveNext())
            {
                var t = en.Current.Value;
                if (t != null && TaskStateOf(t) == TaskState.Completed) n++;
            }
        }
        catch { }
        return n;
    }

    private static string LocalCompleted(Ctx ctx)
    {
        try
        {
            var h = ctx.Runner.LocalHandler;
            if (h == null) return "";
            var ids = h._globallyCompletedTaskIds;
            if (ids == null) return "";
            var sb = new StringBuilder();
            var en = ids.GetEnumerator();
            while (en.MoveNext()) { if (sb.Length > 0) sb.Append(','); sb.Append(en.Current); }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static string SafeName(UnityEngine.Object o)
    {
        try { return o.name ?? "?"; } catch { return "?"; }
    }

    private static string SafeStr(string s) => s ?? "";

    // ---- exit ---------------------------------------------------------------------------

    private static void ExitReport(IHostContext log, Ctx ctx, string tag)
    {
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<LocationExit>());
            int shown = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var exit = all[i].TryCast<LocationExit>();
                if (exit == null) continue;
                try { if (exit.gameObject == null || !exit.gameObject.scene.isLoaded) continue; } catch { }
                shown++;
                string why = "";
                bool can = false;
                try { can = exit.CanOpen(out why); }
                catch (Exception e) { why = "<" + e.Message + ">"; }
                string state = "";
                try { state = exit.CurrentState.ToString(); } catch { }
                log.LogInfo($"[quest:exit] {tag} exit='{PathOf(exit)}' scene='{SafeSceneOf(exit)}' CanOpen={can} " +
                            $"state='{state}' reason='{why}'");
                try
                {
                    var cons = exit._constraints;
                    if (cons != null)
                    {
                        for (int c = 0; c < cons.Length; c++)
                        {
                            var con = cons[c];
                            if (con == null) continue;
                            var qc = con.TryCast<LocationExitQuestsConstraint>();
                            string ctype = qc != null ? "LocationExitQuestsConstraint"
                                : (con.GetType() != null ? con.GetType().Name : "?");
                            string reason = "";
                            try { reason = con.ConstraintReasonLocalizedString != null ? con.ConstraintReasonLocalizedString.ToString() : ""; } catch { }
                            bool ccan = false;
                            try { ccan = con.CanOpen(exit); } catch { }
                            string qids = "";
                            if (qc != null && qc._exitGlobalTasksUD != null)
                            {
                                var sb = new StringBuilder();
                                for (int q = 0; q < qc._exitGlobalTasksUD.Length; q++)
                                {
                                    var ud = qc._exitGlobalTasksUD[q];
                                    if (ud == null) continue;
                                    if (sb.Length > 0) sb.Append(',');
                                    try { sb.Append(ud.Id); } catch { }
                                }
                                qids = sb.ToString();
                            }
                            log.LogInfo($"[quest:exit] {tag}   constraint={ctype} CanOpen={ccan} questIds=[{qids}] reason='{reason}'");
                        }
                    }
                }
                catch (Exception e) { log.LogWarning($"[quest:exit] constraints failed: {e.Message}"); }
            }
            if (shown == 0) log.LogInfo($"[quest:exit] {tag} no LocationExit in loaded scenes");
        }
        catch (Exception e) { log.LogWarning($"[quest:exit] {tag} failed: {e.Message}"); }
    }

    private static string SafeSceneOf(Component c)
    {
        try { return c.gameObject.scene.name ?? ""; } catch { return "?"; }
    }

    // ---- probe --------------------------------------------------------------------------

    private static void Probe(IHostContext log)
    {
        log.LogInfo($"[quest:probe] scene='{SceneName()}'");
        try
        {
            var cycles = SceneCache.Scan(Il2CppType.Of<LocationCycleTransitioner>());
            log.LogInfo($"[quest:probe] LocationCycleTransitioner count={cycles.Length}");
            for (int i = 0; i < cycles.Length; i++)
            {
                var c = cycles[i].TryCast<LocationCycleTransitioner>();
                if (c == null) continue;
                string idx = "?";
                try { idx = c.DayLocationIndex != null ? c.DayLocationIndex.Value.ToString() : "null"; } catch { }
                log.LogInfo($"[quest:probe]   cycle='{PathOf(c)}' scene='{SafeSceneOf(c)}' dayIndex={idx}");
                try
                {
                    var locs = c.OrderedLocations;
                    if (locs != null)
                        for (int j = 0; j < locs.Length; j++)
                        {
                            var l = locs[j];
                            if (l == null) continue;
                            log.LogInfo($"[quest:probe]     ordered[{j}]='{PathOf(l)}' index={SafeIndex(l)}");
                        }
                }
                catch (Exception e) { log.LogWarning($"[quest:probe] ordered failed: {e.Message}"); }
            }
        }
        catch (Exception e) { log.LogWarning($"[quest:probe] cycles failed: {e.Message}"); }

        try { log.LogInfo($"[quest:probe] QuestsCommandsWrapper={FindFirst<QuestsCommandsWrapper>() != null}"); } catch { }
        try
        {
            var runners = SceneCache.Scan(Il2CppType.Of<QuestRunner>());
            log.LogInfo($"[quest:probe] QuestRunner count={runners.Length}");
            for (int i = 0; i < runners.Length; i++)
            {
                var r = runners[i].TryCast<QuestRunner>();
                if (r == null) continue;
                log.LogInfo($"[quest:probe]   runner='{PathOf(r)}' scene='{SafeSceneOf(r)}' service={r.QuestService != null} handler={r.LocalHandler != null}");
            }
        }
        catch (Exception e) { log.LogWarning($"[quest:probe] runners failed: {e.Message}"); }

        try
        {
            var props = SceneCache.Scan(Il2CppType.Of<QuestLocationProperties>());
            log.LogInfo($"[quest:probe] QuestLocationProperties count={props.Length}");
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i].TryCast<QuestLocationProperties>();
                if (p == null) continue;
                log.LogInfo($"[quest:probe]   props='{PathOf(p)}' scene='{SafeSceneOf(p)}' runner='{PathOf(p.QuestRunner)}'");
            }
        }
        catch (Exception e) { log.LogWarning($"[quest:probe] props failed: {e.Message}"); }
    }

    private static string SafeIndex(Location l)
    {
        try { return l.Index.ToString(); } catch { return "?"; }
    }
}
