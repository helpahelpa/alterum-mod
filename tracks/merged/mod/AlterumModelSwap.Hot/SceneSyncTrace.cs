using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Unity.Netcode;

namespace AlterumModelSwap.Hot;

// `[scenesync]`: read-only trace of the lobby -> map scene handoff, both sides.
//
// Why this exists: the host reached the map while a joining client stayed in the Lobby, and the
// only lines the game itself emits about the handoff are on the host. The questions that separate
// the possible causes are all answerable from the game's own loader calls:
//
//   * was the load even requested, and with which client ids?      NetworkSceneLoader.LoadSceneOnServer
//   * did the client receive the preload broadcast?                PreloadAndReport (runs on receipt)
//   * did the client report back, and did the barrier wait for it?  FinishedPreLoadServerRpc,
//                                                                  ShouldBlockBarrier
//   * did the guarded load complete or abort?                      CompleteGuardedLoad / AbortGuardedLoad
//   * was the client still connected when all that happened?        Tick's connection line
//
// Nothing here writes game state: every hook is a log, and a failed install disables the trace
// instead of touching the transition. The connection line is an edge log (one per change), not a
// poll, and the per-frame cost is one integer compare.
internal static class SceneSyncTrace
{
    private const string Tag = "[scenesync]";
    private static IHostContext _log;
    private static Harmony _harmony;
    private static int _lines;

    // Connection state, edge-logged from Tick: a disconnected client is the one cause that makes
    // the host's transition legitimately skip the peer, and it must be visible in the log.
    private static string _conn = "";
    private static float _nextPoll;

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_harmony != null) return;
        try
        {
            var t = typeof(SceneHandle.NetworkSceneLoader);
            _harmony = new Harmony("com.user0.alterum.scenesync." + Guid.NewGuid().ToString("N"));
            int n = 0;
            n += Patch(t, "LoadSceneOnServer", prefix: nameof(PreLoadSceneOnServer), stringFirst: true);
            n += Patch(t, "PreloadAndReport", postfix: nameof(PostPreloadAndReport));
            n += Patch(t, "FinishedPreLoadServerRpc", prefix: nameof(PreFinishedPreLoad));
            n += Patch(t, "ShouldBlockBarrier", postfix: nameof(PostShouldBlockBarrier));
            n += Patch(t, "CompleteGuardedLoad", prefix: nameof(PreCompleteGuardedLoad));
            n += Patch(t, "AbortGuardedLoad", prefix: nameof(PreAbortGuardedLoad));
            if (n == 0) { _harmony.UnpatchSelf(); _harmony = null; log?.LogWarning($"{Tag} no loader methods found; trace off"); return; }
            log?.LogInfo($"{Tag} trace installed ({n} hook(s) on {t.FullName})");
        }
        catch (Exception e)
        {
            log?.LogWarning($"{Tag} install failed: {e.Message}");
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
        }
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _log = null;
    }

    private static int Patch(Type t, string name, string prefix = null, string postfix = null, bool stringFirst = false)
    {
        int n = 0;
        try
        {
            var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int i = 0; i < ms.Length; i++)
            {
                var m = ms[i];
                if (m == null || m.Name != name) continue;
                // LoadSceneOnServer has a String and a SceneReference overload; the hooks below are
                // typed for the String one, and patching the other can only fail to compile.
                if (stringFirst)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                }
                try
                {
                    _harmony.Patch(m,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(SceneSyncTrace).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(SceneSyncTrace).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
                    n++;
                }
                catch (Exception e) { _log?.LogWarning($"{Tag} patch {name}/{m.GetParameters().Length} failed: {e.Message}"); }
            }
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} patch lookup {name} failed: {e.Message}"); }
        return n;
    }

    // ------------------------------------------------------------------ hooks

    private static void PreLoadSceneOnServer(string sceneName)
    {
        try { Line("server LoadSceneOnServer", $"scene='{sceneName}' {State()}"); } catch { }
    }

    private static void PostPreloadAndReport()
    {
        try { Line("PreloadAndReport (preload received/started)", State()); } catch { }
    }

    private static void PreFinishedPreLoad()
    {
        try { Line("client finished preload -> FinishedPreLoadServerRpc", State()); } catch { }
    }

    private static void PostShouldBlockBarrier(bool __result)
    {
        try { Line("ShouldBlockBarrier", $"block={__result} {State()}"); } catch { }
    }

    private static void PreCompleteGuardedLoad(string sceneName)
    {
        try { Line("CompleteGuardedLoad", $"scene='{sceneName}' {State()}"); } catch { }
    }

    private static void PreAbortGuardedLoad(string reason)
    {
        try { Line("AbortGuardedLoad", $"reason='{reason}' {State()}"); } catch { }
    }

    // ------------------------------------------------------------------ connection edge

    // Cheap: one string build per connection-state change, nothing per frame.
    internal static void Tick(float dt)
    {
        if (_harmony == null) return;
        float now = 0f;
        try { now = UnityEngine.Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + 1f;
        string s;
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) s = "no NetworkManager";
            else
            {
                var ids = new StringBuilder(48);
                try
                {
                    // ConnectedClientsIds is Il2CppSystem's IReadOnlyList<ulong>, and that interop
                    // type is a projection class exposing only Item - there is no Count on it. The
                    // object behind it is a List<ulong>, so count and index through the cast.
                    var list = nm.ConnectedClientsIds;
                    var concrete = list != null ? list.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>() : null;
                    if (concrete == null) ids.Append('?');
                    else
                        for (int i = 0; i < concrete.Count; i++)
                        {
                            if (i > 0) ids.Append(',');
                            ids.Append(concrete[i]);
                        }
                }
                catch { ids.Append('?'); }
                s = $"listening={nm.IsListening} server={nm.IsServer} client={nm.IsClient} local={nm.LocalClientId} connected=[{ids}]";
            }
        }
        catch (Exception e) { s = "state threw: " + e.Message; }
        if (s == _conn) return;
        _conn = s;
        Line("connection", s);
    }

    // ------------------------------------------------------------------ log

    private static string State()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            string role = nm == null ? "nm=null" : $"server={nm.IsServer} local={nm.LocalClientId}";
            string loading = "?";
            try
            {
                var l = SceneHandle.NetworkSceneLoader.Instance;
                // Concrete List<ulong> on the loader: this is the set the barrier will wait for, so
                // it is the authoritative answer to "was the client part of this load".
                loading = Ids(l != null ? l.ClientsLoadingNetworkScene : null);
            }
            catch { }
            return $"{role} clientsLoading={loading}";
        }
        catch (Exception e) { return "<" + e.Message + ">"; }
    }

    // Ids of an Il2Cpp list of client ids. Both list flavours the loader hands out are projection
    // types without a usable Count, so count through the concrete List<ulong> behind them.
    private static string Ids(object list)
    {
        if (list == null) return "none";
        try
        {
            var b = list as Il2CppObjectBase;
            if (b == null) return "?";
            var l = b.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>();
            if (l == null) return "?";
            var sb = new StringBuilder(24);
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i]); }
            return "[" + sb + "]";
        }
        catch (Exception e) { return "<" + e.Message + ">"; }
    }

    private static void Line(string what, string detail)
    {
        if (_log == null) return;
        if (++_lines > 400) return;      // a broken loop must not fill the log
        _log.LogInfo($"{Tag} {what}: {detail}");
    }
}
