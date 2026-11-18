using System;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using AlterumModelSwap.Contracts;
using Gameplay.Appearance;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Network.Spawners;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// Diagnostic trace for the question this feature exists to answer: what assigns a player's model
// when they join a lobby, and is it random?
//
// Every hook is read-only (a prefix that logs, a postfix that logs); none of them changes a value.
// The log lines are `[join:trace] ...` so a whole session's appearance history is one grep.
//
// Patched:
//   PlayersAppearanceManager.SetBaseModelIndex          - the wardrobe's authoritative setter
//   WardrobeController.OnModelSelectedServerRpc        - the only manual (click) caller of it
//   PlayersModelSetuper.SpawnPlayerModelOnServer/Client - the join-side spawn path
//   PlayersModelSetuper.ApplyModel                     - the index/data actually applied
//   PlayerModelSetuper.SetupModel                      - the body swap that lands it
//   PlayerPrefs.GetInt/SetInt                          - the saved index (PlayersModelSessionSaver)
internal static class JoinTrace
{
    private const string Tag = "[join:trace]";
    private static IHostContext _log;
    private static Harmony _harmony;
    private static bool _installed;

    private static string _scene = "";
    private static int _models = -1;
    private static float _nextPoll;
    private static bool _lastInLobby;
    private static ulong _lastLobby, _lastClient = ulong.MaxValue;
    private static bool _lastServer, _lastClientRole;

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_installed) return;
        var h = new Harmony("com.user0.alterum.jointrace." + Guid.NewGuid().ToString("N"));
        int ok = 0;

        void Patch(Type type, string name, Type[] args, string prefix, string postfix)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(type, name, args);
                if (m == null) { log?.LogWarning($"{Tag} {type.Name}.{name} not found"); return; }
                h.Patch(m,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(JoinTrace), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(JoinTrace), postfix));
                ok++;
            }
            catch (Exception e) { log?.LogWarning($"{Tag} patch {type.Name}.{name} failed: {e.Message}"); }
        }

        Patch(typeof(PlayersAppearanceManager), "SetBaseModelIndex", new[] { typeof(ulong), typeof(ushort) },
              null, nameof(PostSetBaseModelIndex));
        Patch(typeof(WardrobeController), "OnModelSelectedServerRpc", new[] { typeof(ulong), typeof(ushort) },
              nameof(PreWardrobeRpc), null);
        Patch(typeof(PlayersModelSetuper), "SpawnPlayerModelOnServer", new[] { typeof(ulong) },
              null, nameof(PostSpawnOnServer));
        Patch(typeof(PlayersModelSetuper), "SpawnPlayerModelOnClient", new[] { typeof(ulong) },
              null, nameof(PostSpawnOnClient));
        Patch(typeof(PlayersModelSetuper), "ApplyModel",
              new[] { typeof(Player), typeof(PlayerModelSetuper), typeof(PlayerModelData), typeof(ushort) },
              nameof(PreApplyModel), null);
        Patch(typeof(PlayerModelSetuper), "SetupModel", new[] { typeof(PlayerModelData) },
              null, nameof(PostSetupModel));
        Patch(typeof(PlayersModelSessionSaver), "AddToPlayerPrefs", new[] { typeof(ulong), typeof(ushort) },
              nameof(PreAddToPrefs), null);
        Patch(typeof(PlayerPrefs), "GetInt", new[] { typeof(string), typeof(int) },
              null, nameof(PostPrefsGetInt));
        Patch(typeof(PlayerPrefs), "SetInt", new[] { typeof(string), typeof(int) },
              nameof(PrePrefsSetInt), null);

        _harmony = h;
        _installed = true;
        log?.LogInfo($"{Tag} installed {ok} probes (appearance setter, join spawn, apply, setup, wardrobe RPC, PlayerPrefs)");
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _installed = false;
    }

    // ------------------------------------------------------------------ hooks

    private static void PostSetBaseModelIndex(ulong clientId, ushort modelIndex)
    {
        try
        {
            _log?.LogInfo($"{Tag} SetBaseModelIndex(client={clientId}, index={modelIndex}) " +
                          $"models={PlayerModelDatabase.Models?.Length ?? -1} from {Caller()}");
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} SetBaseModelIndex log failed: {e.Message}"); }
    }

    private static void PreWardrobeRpc(ulong localClientId, ushort chosenModelIndex)
    {
        try
        {
            _log?.LogInfo($"{Tag} WardrobeController.OnModelSelectedServerRpc(client={localClientId}, index={chosenModelIndex}) " +
                          "= MANUAL wardrobe pick");
        }
        catch { }
    }

    private static void PostSpawnOnServer(ulong clientId)
    {
        try
        {
            // A new player object exists: the provider/player/model caches from before the spawn
            // must not be held for the rest of the scene.
            try { SceneCache.InvalidatePlayer(); } catch { }
            string index = "?";
            try
            {
                var managers = SceneCache.Scan(Il2CppType.Of<PlayersModelSetuper>());
                var m = managers != null && managers.Length > 0 ? managers[0].TryCast<PlayersModelSetuper>() : null;
                if (m != null && m._playersModelsIndexes != null &&
                    m._playersModelsIndexes.TryGetValue(clientId, out ushort v)) index = v.ToString();
            }
            catch { }
            _log?.LogInfo($"{Tag} SpawnPlayerModelOnServer(client={clientId}) dictionaryIndex={index} " +
                          $"models={PlayerModelDatabase.Models?.Length ?? -1} from {Caller()}");
        }
        catch { }
    }

    private static void PostSpawnOnClient(ulong clientId)
    {
        try
        {
            try { SceneCache.InvalidatePlayer(); } catch { }
            _log?.LogInfo($"{Tag} SpawnPlayerModelOnClient(client={clientId}) " +
                          $"models={PlayerModelDatabase.Models?.Length ?? -1} from {Caller()}");
        }
        catch { }
    }

    private static void PreApplyModel(Player player, PlayerModelSetuper modelSetuper, PlayerModelData playerModelData, ushort targetModelIndex)
    {
        try
        {
            _log?.LogInfo($"{Tag} ApplyModel(client={(player == null ? "?" : player.OwnerClientId.ToString())}, " +
                          $"target={targetModelIndex}, data='{(playerModelData == null ? "null" : playerModelData.name)}')");
        }
        catch { }
    }

    private static void PostSetupModel(PlayerModelData modelData)
    {
        try
        {
            _log?.LogInfo($"{Tag} PlayerModelSetuper.SetupModel('{(modelData == null ? "null" : modelData.name)}') " +
                          $"models={PlayerModelDatabase.Models?.Length ?? -1}");
        }
        catch { }
    }

    private static void PreAddToPrefs(ulong clientId, ushort modelId)
    {
        try { _log?.LogInfo($"{Tag} PlayersModelSessionSaver.AddToPlayerPrefs(client={clientId}, model={modelId})"); }
        catch { }
    }

    private static void PostPrefsGetInt(string key, int __result)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return;
            if (key.IndexOf("model", StringComparison.OrdinalIgnoreCase) < 0 &&
                key.IndexOf("appear", StringComparison.OrdinalIgnoreCase) < 0 &&
                key.IndexOf("wardrobe", StringComparison.OrdinalIgnoreCase) < 0) return;
            _log?.LogInfo($"{Tag} PlayerPrefs.GetInt('{key}') = {__result}");
        }
        catch { }
    }

    private static void PrePrefsSetInt(string key, int value)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return;
            if (key.IndexOf("model", StringComparison.OrdinalIgnoreCase) < 0 &&
                key.IndexOf("appear", StringComparison.OrdinalIgnoreCase) < 0 &&
                key.IndexOf("wardrobe", StringComparison.OrdinalIgnoreCase) < 0) return;
            _log?.LogInfo($"{Tag} PlayerPrefs.SetInt('{key}', {value})");
        }
        catch { }
    }

    internal static string CallerPublic() { try { return Caller(); } catch { return "?"; } }

    private static string Caller()
    {
        var st = new StackTrace(false);
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 1; i < st.FrameCount && shown < 4; i++)
        {
            MethodBase m = st.GetFrame(i)?.GetMethod();
            if (m == null) continue;
            string type = m.DeclaringType == null ? "?" : m.DeclaringType.Name;
            if (type == "JoinTrace") continue;
            sb.Append(shown == 0 ? "" : " <- ").Append(type).Append('.').Append(m.Name);
            shown++;
        }
        return sb.Length == 0 ? "(no stack)" : sb.ToString();
    }

    // ------------------------------------------------------------------ tick

    internal static void Tick(IHostContext log, float dt)
    {
        _log = log;
        if (log == null) return;

        string scene = null;
        try { scene = SceneManager.GetActiveScene().name; } catch { }
        if (scene != _scene)
        {
            _scene = scene;
            log.LogInfo($"{Tag} scene -> '{scene}'");
        }

        int models = -1;
        try { models = PlayerModelDatabase.Models?.Length ?? -1; } catch { }
        if (models != _models)
        {
            _models = models;
            log.LogInfo($"{Tag} PlayerModelDatabase.Models.Length = {models}");
        }

        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + 0.5f;

        bool inLobby = false;
        ulong lobby = 0;
        try
        {
            inLobby = Steam.SteamLobbyManager.IsInLobby;
            if (inLobby) lobby = Steam.SteamLobbyManager.CurrentLobby.Id;
        }
        catch { }

        ulong client = 0;
        bool server = false, clientRole = false;
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null)
            {
                client = nm.LocalClientId;
                server = nm.IsServer;
                clientRole = nm.IsClient;
            }
        }
        catch { }

        if (inLobby != _lastInLobby || lobby != _lastLobby || client != _lastClient ||
            server != _lastServer || clientRole != _lastClientRole)
        {
            _lastInLobby = inLobby; _lastLobby = lobby; _lastClient = client;
            _lastServer = server; _lastClientRole = clientRole;
            log.LogInfo($"{Tag} net: inLobby={inLobby} lobby={lobby} localClient={client} " +
                        $"server={server} client={clientRole} models={models}");
        }
    }
}
