using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Temporary diagnostic: who switches QFSW.QC.QuantumConsole off?
//
// The console component is serialized `enabled=1` in every scene but reads `enabled=false` at
// runtime, and `QuantumConsole.Instance` is null, so its own Tilde `Update` never runs. Instead of
// guessing at the gate, this patches the console's own lifecycle (private methods included) and
// prints one ordered line per call plus a managed stack trace:
//
//   Awake / OnEnable / OnDisable / DisableQC / IsSupportedState / Initialize / Update /
//   Activate / Deactivate / Toggle / set_IsActive
//
// A component that disables itself does it from one of those, so the sequence names the caller:
// a stack or an `IsSupportedState -> False` immediately before `DisableQC` is the answer, and an
// `OnDisable` that fires the moment `enabled` is written (with no `DisableQC`) means the write went
// to a dead object rather than being reverted by game code.
//
// Installed on hot-module initialization, all of it best-effort: any signature that does not
// resolve is a log line, never a throw. Request `qc.request` `gate` prints the ring buffer.
//
// Second job: the access gate (QcAccess below). Until the pause-menu code AEZAKMI unlocks it, the
// console is not reachable and does not execute commands. The mod's own routes are gated in
// GameConsole; the patches here are the *resolved* seams, because clearing the console's own
// ToggleConsoleVisibilityKey is necessary but not sufficient:
//
//   QuantumConsole.Activate() / Toggle()               - the console's own activation
//   QuantumConsole.InvokeCommand() / (string)           - the console's own execution entry points
//   QuantumConsoleProcessor.InvokeCommand(string)       - the processor's, the one statics call
//
// Not skipped while locked: QuantumConsole.Update (it drives the console's own input loop, so
// starving it would gate the console by breaking it - the key path it would run, Toggle(), is
// refused instead) and Initialize/Awake/OnEnable's type-guarded skip. The command table is
// deliberately NOT gated either: the console keeps initializing normally while locked, so
// unlocking is instant instead of a race against a cold start and the 109-command table is there
// the moment access is allowed.
//
// Third job, by delegation: the mod's own read-only test.* rows (GameConsole's native QC rows) live in
// that same command table only while access is allowed, and Dispose removes them together with
// everything else this generation added - they hold delegates created in this assembly, the same
// reason the text-input subscription above is torn down here.
internal static class QcGate
{
    private const string QcTypeName = "QFSW.QC.QuantumConsole";
    private const string ProcTypeName = "QFSW.QC.QuantumConsoleProcessor";
    private const int RingCap = 200;
    private const int StackLines = 14;
    private const int LogCap = 6;

    private static readonly List<string> _ring = new(RingCap);
    private static readonly Dictionary<string, int> _counts = new();
    private static Harmony _harmony;
    private static IHostContext _host;
    private static Type _qc;
    private static Type _proc;
    private static int _installed;
    private static int _failed;
    private static bool _verboseStacks = true;

    internal static bool Installed => _installed > 0;

    internal static void Install(IHostContext host)
    {
        _host = host;
        // The gate's own state first: every denial below reads it, and the state line has to be in
        // the log before the first console lifecycle line can appear.
        try { QcAccess.Initialize(host); } catch (Exception e) { host.LogWarning($"[qc:access] init threw: {e.Message}"); }
        if (_harmony != null) return;
        _qc = FindType(QcTypeName);
        _proc = FindType(ProcTypeName);
        if (_qc == null) { host.LogWarning($"[gate] type {QcTypeName} not found - no lifecycle hooks installed"); return; }

        var h = new Harmony("com.user0.alterum.qcgate." + Guid.NewGuid().ToString("N"));
        void Patch(Type owner, string name, int argc, HarmonyMethod pre, HarmonyMethod post)
        {
            try
            {
                var m = FindMethod(owner, name, argc);
                if (m == null) { host.LogWarning($"[gate] {owner?.FullName}.{name}/{argc} not found"); _failed++; return; }
                h.Patch(m, pre, post);
                _installed++;
            }
            catch (Exception e) { host.LogWarning($"[gate] {name}/{argc} patch failed: {e.GetType().Name}: {e.Message}"); _failed++; }
        }

        Patch(_qc, "Awake", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Awake)), null);
        Patch(_qc, "OnEnable", 0, new HarmonyMethod(typeof(QcGate), nameof(P_OnEnablePre)), null);
        Patch(_qc, "OnDisable", 0, new HarmonyMethod(typeof(QcGate), nameof(P_OnDisable)), null);
        Patch(_qc, "DisableQC", 0, new HarmonyMethod(typeof(QcGate), nameof(P_DisableQC)), null);
        Patch(_qc, "Initialize", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Initialize)), null);
        // ---- access gate: activation, the console's own key handler, and execution ------------
        Patch(_qc, "Activate", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Activate)), null);
        Patch(_qc, "Toggle", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Toggle)), null);
        Patch(_qc, "InvokeCommand", 0, new HarmonyMethod(typeof(QcGate), nameof(P_ConsoleInvoke0)), null);
        Patch(_qc, "InvokeCommand", 1, new HarmonyMethod(typeof(QcGate), nameof(P_ConsoleInvoke1)), null);
        // No `Activate(bool)` hook: its IL never compiles under Harmony for this build (IL2CPP
        // interop body), and it would only duplicate the line the 0-arg Activate() hook prints.
        // A patch that fails on every install is worse than one diagnostic line fewer.
        Patch(_qc, "Deactivate", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Deactivate)), null);
        Patch(_qc, "Update", 0, new HarmonyMethod(typeof(QcGate), nameof(P_Update)), null);
        if (_proc != null)
        {
            // The processor executes on its own (Help/Invoke fall back to it when Instance is null),
            // so the console's own seam is not the only way a command can run.
            Patch(_proc, "InvokeCommand", 1, new HarmonyMethod(typeof(QcGate), nameof(P_ProcessorInvoke)), null);
        }
        else host.LogWarning($"[gate] type {ProcTypeName} not found - the processor seam is not gated");

        // GameObject.SetActive(false) on the console's own object: cheap, name-filtered.
        try
        {
            var sa = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) });
            if (sa != null)
            {
                h.Patch(sa, null, new HarmonyMethod(typeof(QcGate), nameof(P_SetActivePost)));
                _installed++;
            }
        }
        catch (Exception e) { host.LogWarning($"[gate] GameObject.SetActive patch failed: {e.Message}"); _failed++; }

        // QC's IL2CPP reflection cannot invoke a converted delegate's managed target.
        // Keep QC parsing/overload validation, bridging only our registered rows at invocation.
        Patch(FindType("QFSW.QC.CommandData"), "Invoke", 2,
            new HarmonyMethod(typeof(QcGate), nameof(P_TestRowInvoke)), null);
        _harmony = h;
        host.LogInfo($"[gate] installed {_installed} lifecycle hook(s), {_failed} failure(s) on {_qc.Assembly.GetName().Name}");
        Ev($"installed {_installed} hook(s)", null, null);
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _installed = 0;
        _ring.Clear();
        _counts.Clear();
        _qc = null;
        // This generation's text-input subscription dies with the generation; the unlock state
        // does not (it lives in the AppDomain slot, see QcAccess). A hot reload therefore comes
        // back armed and unlocked without a second confirmation sound.
        try { GameConsole.ShutdownCapture(); } catch { }
        // Same rule for the console's own command table: the test.* rows the mod registered hold
        // delegates created in this assembly (GameConsole's native QC rows), so the retiring
        // generation takes its rows out before it unloads. Best-effort like everything here: a
        // failed removal is one log line, never a throw on the teardown path.
        try
        {
            int rows = GameConsole.TestRowsTeardown();
            if (rows > 0) _host?.LogInfo($"[qc:test] native rows: removed {rows} on generation teardown");
        }
        catch (Exception e) { _host?.LogWarning($"[qc:test] native rows: teardown threw {e.Message}"); }
    }

    internal static void Verbose(bool on) => _verboseStacks = on;

    internal static string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[gate] ring: {_ring.Count} event(s), installed={_installed} failed={_failed}");
        lock (_ring)
        {
            for (int i = 0; i < _ring.Count; i++) sb.AppendLine("[gate] " + _ring[i]);
        }
        lock (_counts)
        {
            foreach (var kv in _counts) sb.AppendLine($"[gate] count {kv.Key} = {kv.Value}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ patches

    private static void P_Awake(MonoBehaviour __instance)
    {
        if (Hook("Awake enter", __instance, false, quietAfter: 1)) GameConsole.ConsoleAwoke();
    }
    private static void P_OnDisable(MonoBehaviour __instance) => Hook("OnDisable enter", __instance, true, quietAfter: 2);
    private static void P_DisableQC(MonoBehaviour __instance)
    {
        if (!Hook("DisableQC enter", __instance, true)) return;
        Exiting(__instance, "DisableQC");
    }
    // OnEnable's only job before it disables the component is the IsSupportedState gate. Skipping
    // the original for a QuantumConsole keeps `enabled` true so the console's own Update (Tilde,
    // history, autocomplete) runs; GameConsole.Enable then calls the console's own Initialize() and
    // Activate(). Nothing of the console itself is reimplemented.
    private static bool P_OnEnablePre(MonoBehaviour __instance)
    {
        try
        {
            string full = __instance == null ? null : __instance.GetIl2CppType()?.FullName;
            if (full != QcTypeName) return true;
        }
        catch { return true; }
        Ev("OnEnable original skipped (supported gate compiled to false)", __instance, null, quietAfter: 3);
        GameConsole.ConsoleAwoke();
        return false;
    }

    private static void P_Initialize(MonoBehaviour __instance) => Hook("Initialize enter", __instance, false, quietAfter: 1);
    private static void P_Deactivate(MonoBehaviour __instance) => Hook("Deactivate enter", __instance, false, quietAfter: 1);

    // ---- access gate prefixes -------------------------------------------------------------
    // Every one of these returns true (run the original) unless the pause code unlocked the
    // console. A false return skips the original body: no activation, no execution of a command.
    // `Hook` filters by the il2cpp type first, because IL2CPP folds
    // methods with identical bodies onto one address and these detours can fire for a component
    // that is not this QuantumConsole.

    private static bool P_Activate(MonoBehaviour __instance)
    {
        if (!Hook("Activate() enter", __instance, false, quietAfter: 1)) return true;
        return Access("activate", "qc-seam");
    }

    private static bool P_Toggle(MonoBehaviour __instance)
    {
        if (!Hook("Toggle enter", __instance, false, quietAfter: 1)) return true;
        return Access("console-toggle", "qc-seam");
    }

    // Diagnostic only, on purpose. Update is where the console polls its own toggle key, but it
    // also drives the console's own UI and input loop; skipping it while locked would gate the
    // console by starving it, which is a different (and riskier) change than refusing access. The
    // key path is covered twice without it: SilenceOwnToggleKey clears the console's own
    // ToggleConsoleVisibilityKey, and the handler it would run - Toggle() - is refused above.
    private static void P_Update(MonoBehaviour __instance) => Hook("Update", __instance, false, quietAfter: 3);

    private static bool P_ConsoleInvoke0(MonoBehaviour __instance)
    {
        if (!Hook("InvokeCommand() enter", __instance, false, quietAfter: 1)) return true;
        return Access("console-invoke", "qc-seam");
    }

    private static bool P_ConsoleInvoke1(MonoBehaviour __instance, string command)
    {
        if (!Hook("InvokeCommand(string) enter", __instance, false, quietAfter: 1)) return true;
        return Access("console-invoke", "qc-seam", command);
    }

    // QuantumConsoleProcessor.InvokeCommand is static: no instance, no type filter. The processor
    // is a single process-wide table, so there is nothing else this detour could belong to.
    private static bool P_TestRowInvoke(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object> __0)
        => !GameConsole.TryInvokeTestRow(__instance, __0);

    private static bool P_ProcessorInvoke(string commandString)
    {
        return Access("processor-invoke", "qc-seam", commandString);
    }

    private static bool Access(string entry, string source, string detail = null)
    {
        if (QcAccess.IsUnlocked) return true;
        QcAccess.Deny(entry, source, detail);
        return false;
    }

    // IL2CPP folds methods with identical bodies onto one native address, so a hook can fire for a
    // component that is not a QuantumConsole. Only the real type is reported.
    private static bool Hook(string what, MonoBehaviour c, bool stack, int quietAfter = 0)
    {
        try
        {
            string full = c == null ? null : c.GetIl2CppType()?.FullName;
            if (full != QcTypeName) return false;
        }
        catch { return false; }
        Ev(what, c, stack ? Stack() : null, quietAfter);
        return true;
    }

    private static void P_SetActivePost(GameObject __instance, bool value)
    {
        try
        {
            string n = __instance == null ? "?" : __instance.name;
            if (n == null || !n.StartsWith("Quantum Console")) return;
            Ev($"GameObject.SetActive({value}) on '{n}'", null, Stack());
        }
        catch { }
    }

    private static void Exiting(Component c, string what)
    {
        try
        {
            var b = c == null ? null : c.TryCast<Behaviour>();
            Ev($"{what} returned: enabled={(b == null ? "?" : b.enabled.ToString())}", c, null);
        }
        catch (Exception e) { Ev($"{what} returned: enabled read threw {e.GetType().Name}", c, null); }
    }

    private static void Ev(string what, Component c, string stack, int quietAfter = 0)
    {
        int n;
        lock (_counts) { _counts.TryGetValue(what, out n); n++; _counts[what] = n; }
        if (quietAfter > 0 && n > quietAfter) return;

        string head = what;
        try
        {
            if (c != null)
            {
                var b = c.TryCast<Behaviour>();
                head += $" | '{c.gameObject.name}' enabled={(b == null ? "?" : b.enabled.ToString())}";
                object live = GetStatic(_qc, "Instance");
                head += $" Instance={(live == null ? "null" : "set")}";
            }
        }
        catch (Exception e) { head += $" | <state read threw {e.GetType().Name}>"; }
        if (n > 1) head += $" (x{n})";

        if (n > LogCap) return;
        string line = head;
        lock (_ring)
        {
            _ring.Add(line);
            if (_ring.Count > RingCap) _ring.RemoveAt(0);
        }
        try { _host?.LogInfo("[gate] " + line); } catch { }
        if (stack != null && (_verboseStacks || what.StartsWith("IsSupportedState") || what.StartsWith("DisableQC")))
        {
            var lines = stack.Replace("\r\n", "\n").Split('\n');
            int shown = 0;
            for (int i = 0; i < lines.Length && shown < StackLines; i++)
            {
                string l = lines[i].TrimEnd();
                if (l.Length == 0) continue;
                try { _host?.LogInfo("[gate]    " + l); } catch { }
                shown++;
            }
        }
    }

    private static string Stack()
    {
        try { return Environment.StackTrace; } catch { return null; }
    }

    // ------------------------------------------------------------------ reflection

    private static MethodInfo FindMethod(Type t, string name, int argc)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var m in t.GetMethods(flags))
            if (m.Name == name && m.GetParameters().Length == argc) return m;
        return null;
    }

    private static object GetStatic(Type t, string member)
    {
        if (t == null) return null;
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;
        try
        {
            var p = t.GetProperty(member, flags);
            if (p != null) return p.GetValue(null);
            var f = t.GetField(member, flags);
            if (f != null) return f.GetValue(null);
        }
        catch { }
        return null;
    }

    private static Type FindType(string fullName)
    {
        try
        {
            var direct = Type.GetType(fullName + ", QFSW.QC", false);
            if (direct != null) return direct;
        }
        catch { }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}

// ---------------------------------------------------------------------------------------------
// The console access gate's state, its storage, and the one place that says "no".
//
// Storage: ONE BOXED BOOLEAN in a namespaced AppDomain data slot. Nothing else, and the reasons are
// properties of this column, not preferences:
//
//   * process-local - AppDomain data belongs to the runtime, not to this collectible assembly. A
//     hot assembly is replaced on every reload, so every static here starts over in the new
//     generation; the slot does not. That is what "unlocked until the process exits" means.
//   * no hot-assembly retention - the payload is a boxed System.Boolean from mscorlib. A delegate
//     or an instance of a type declared in this file would pin the retiring assembly (and the
//     IHostContext it holds) for the life of the process, which is exactly what a collectible
//     load context exists to prevent.
//   * nothing on disk - no config file, no pid file, no Contracts change. A new process starts
//     locked, and a crash cannot leave a stale "unlocked" file behind.
//   * failure mode: if the slot cannot be written and read back, the gate FAILS LOCKED for this
//     process and says so once. Substituting disk persistence would silently turn "until the
//     process exits" into "forever", which is the one outcome this unit must not produce.
//
// The slot value is written only on unlock, and only after a read-back confirms the write landed.
internal static class QcAccess
{
    internal const string Slot = "AlterumModelSwap.Hot.qc-access.unlocked";
    private const float DenySeconds = 1f;

    private static readonly Dictionary<string, float> _denyAt = new(StringComparer.Ordinal);
    private static IHostContext _log;
    private static bool _checked;
    private static bool _storageOk;
    private static bool _unlocked;
    private static int _denied;

    internal static bool IsUnlocked { get { Initialize(null); return _unlocked; } }

    internal static string Summary =>
        $"state={(_unlocked ? "unlocked" : "locked")} storage={(_storageOk ? "process" : "unavailable")} " +
        $"denied={_denied}";

    // Once per generation: read the slot, or say that it is unusable. The slot holds
    // `false` after the first probe, so "readable but false" (locked) and "unreadable"
    // (unavailable) are different words in the log, never the same silence.
    internal static void Initialize(IHostContext host)
    {
        if (host != null) _log = host;
        if (_checked) return;
        _checked = true;
        _denyAt.Clear();
        _denied = 0;

        bool read;
        bool value = false;
        try { read = TryGet(ref value); }
        catch (Exception e)
        {
            _log?.LogWarning($"[qc:access] slot read threw {e.GetType().Name}: {e.Message}");
            read = false;
        }

        if (read)
        {
            _storageOk = true;
            _unlocked = value;
            if (value) _log?.LogInfo("[qc:access] restored unlocked=true storage=process");
            else _log?.LogInfo("[qc:access] state=locked storage=process");
            return;
        }

        // Nothing in the slot yet (first generation of this process). A write/read-back probe is
        // the only honest way to tell "empty" from "this runtime has no data slots".
        bool ok = false;
        try
        {
            AppDomain.CurrentDomain.SetData(Slot, (object)false);
            object back = AppDomain.CurrentDomain.GetData(Slot);
            ok = back is bool b && !b;
        }
        catch (Exception e)
        {
            _log?.LogWarning($"[qc:access] AppDomain data slot probe threw {e.GetType().Name}: {e.Message}");
        }

        _storageOk = ok;
        _unlocked = false;
        if (ok) _log?.LogInfo("[qc:access] state=locked storage=process");
        else
            _log?.LogWarning("[qc:access] state=locked storage=unavailable - AppDomain data slots are not usable " +
                             "in this runtime, so the console stays locked for the life of this process; there is no " +
                             "disk fallback on purpose (it would outlive the process)");
    }

    private static bool TryGet(ref bool value)
    {
        object v = AppDomain.CurrentDomain.GetData(Slot);
        if (v is bool b) { value = b; return true; }
        return false;
    }

    // Returns false when the unlock could not be recorded (storage unavailable, or the write did
    // not read back). Callers must treat false as "still locked".
    internal static bool Unlock(string source)
    {
        Initialize(null);
        if (!_storageOk)
        {
            _log?.LogWarning($"[qc:access] unlocked source={source} storage=unavailable result=failed; staying locked");
            return false;
        }
        try { AppDomain.CurrentDomain.SetData(Slot, (object)true); }
        catch (Exception e)
        {
            _log?.LogWarning($"[qc:access] unlocked source={source} storage=process result=failed ({e.GetType().Name}: {e.Message}); staying locked");
            return false;
        }
        bool now = false;
        try { if (!TryGet(ref now) || !now) now = false; }
        catch { now = false; }
        if (!now)
        {
            _log?.LogWarning($"[qc:access] unlocked source={source} storage=process result=failed (the slot did not read back); staying locked");
            return false;
        }
        _unlocked = true;
        _log?.LogInfo($"[qc:access] unlocked source={source}");
        return true;
    }

    // One line per entry per second: a held key or a per-frame seam must not flood the log, but
    // every distinct refusal is still visible. Never throws and never touches Unity outside a
    // try/catch: the processor seam can be reached from a worker thread, where Time/logging are not
    // safe, and a throw from a Harmony prefix would land in the caller.
    internal static void Deny(string entry, string source = null, string detail = null)
    {
        try
        {
            _denied++;
            var sb = new StringBuilder("[qc:access] denied entry=").Append(entry);
            if (!string.IsNullOrEmpty(source)) sb.Append(" source=").Append(source);
            if (!string.IsNullOrEmpty(detail))
            {
                string d = detail.Length > 80 ? detail.Substring(0, 80) + "..." : detail;
                sb.Append(" command='").Append(d.Replace('\'', '"')).Append('\'');
            }
            string line = sb.ToString();

            float now;
            try { now = Time.unscaledTime; } catch { now = -1f; }
            if (now >= 0f)
            {
                string key = source == null ? entry : entry + "|" + source;
                lock (_denyAt)
                {
                    if (_denyAt.TryGetValue(key, out float next) && now < next) return;
                    _denyAt[key] = now + DenySeconds;
                }
            }
            var log = _log;
            if (log != null) log.LogInfo(line);
        }
        catch { }
    }

    internal static void Note(string message)
    {
        Initialize(null);
        _log?.LogInfo("[qc:access] " + message);
    }
}
