using System;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.Appearance;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// `[mapbody]` trace: why the local player's custom body is or is not on screen in a map run.
//
// Read-only. Two sources:
//   * a postfix on PlayerModelSetuper.SetupModel, which logs every body build the game performs
//     (index, data name, whether the data is one of our roster rows, the setuper's path);
//   * a 1 Hz census while the active scene is a map, covering the whole chain the map path can
//     break: local player resolution (CurrentPlayerProvider vs Netcode IsOwner), the applied
//     PlayerModelData, our roster registration, both body modules (wanted / bound / hidden), and
//     the renderer lease each module holds.
//
// It exists because the lobby works and the map did not: on entering a map the applied data went
// back to a native row while the mod still considered the custom entry selected. The census makes
// the first frame where those two disagree, and the game call that changed it, one grep away.
//   grep '\[mapbody' LogOutput.log
internal static class MapBody
{
    private const string Tag = "[mapbody]";
    private static IHostContext _log;
    private static Harmony _harmony;
    private static int _sceneHandle = int.MinValue;
    private static string _scene = "";
    private static float _next;
    private static bool _wasMap;
    private static int _setups, _lastModels = -1;
    private static string _lastCensus = "";
    private static int _quiet;

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_harmony != null) return;
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(PlayerModelSetuper), "SetupModel", new[] { typeof(PlayerModelData) });
            if (m == null) { log?.LogWarning($"{Tag} SetupModel not found; body-build trace off"); return; }
            _harmony = new Harmony("com.user0.alterum.mapbody." + Guid.NewGuid().ToString("N"));
            _harmony.Patch(m, postfix: new HarmonyMethod(typeof(MapBody), nameof(PostSetupModel)));
            log?.LogInfo($"{Tag} trace installed ({Tag} SetupModel postfix + 1 Hz map census)");
        }
        catch (Exception e)
        {
            log?.LogWarning($"{Tag} install failed: {e.Message}");
            _harmony = null;
        }
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _log = null;
    }

    // Called from the runtime's scene-change block, i.e. on the first frame of the new scene.
    internal static void OnScene(IHostContext log, string scene)
    {
        if (log == null) return;
        _scene = scene ?? "";
        _next = 0f;
        _sceneHandle = SafeHandle();
        bool map = ReelLogic.IsMapRun(out _);
        if (map || _wasMap)
        {
            _wasMap = map;
            log.LogInfo($"{Tag} scene '{_scene}' entered mapRun={map} :: {Census(log)}");
        }
        _wasMap = map;
    }

    internal static void Tick(IHostContext log, float dt)
    {
        if (log == null || _harmony == null) return;
        int h = SafeHandle();
        if (h != _sceneHandle) { _sceneHandle = h; _next = 0f; }
        if (!ReelLogic.IsMapRun(out _)) { _wasMap = false; return; }

        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _next) return;
        _next = now + 1f;

        // A census a second while it is changing, then one every 10 s: the first line after a
        // transition is the evidence, and a map left running must not fill the log.
        int models = ModelsCount();
        string census = Census(log);
        bool rosterChange = models != _lastModels;
        _lastModels = models;
        bool changed = rosterChange || census != _lastCensus;
        _lastCensus = census;
        if (changed) _quiet = 0; else _quiet++;
        if (!changed && _quiet % 10 != 0) return;
        log.LogInfo($"{Tag}{(rosterChange ? " roster-changed" : "")} {census}");
    }

    private static string Census(IHostContext log)
    {
        var sb = new StringBuilder(256);
        try
        {
            sb.Append($"t={(Time.unscaledTime):F1} models={ModelsCount()} ");

            // Local player: which resolution path works, right now.
            string via = "none";
            Gameplay.PlayerSystem.Player local = null;
            try
            {
                var provs = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
                for (int i = 0; provs != null && i < provs.Length; i++)
                {
                    var p = provs[i].TryCast<CurrentPlayerProvider>();
                    if (p?.CurrentPlayer == null) continue;
                    local = p.CurrentPlayer; via = "CurrentPlayerProvider"; break;
                }
            }
            catch (Exception e) { sb.Append($"provider=<{e.Message}> "); }
            if (local == null)
            {
                try
                {
                    var players = SceneCache.Scan(Il2CppType.Of<Gameplay.PlayerSystem.Player>());
                    for (int i = 0; players != null && i < players.Length; i++)
                    {
                        var p = players[i].TryCast<Gameplay.PlayerSystem.Player>();
                        if (p == null || !p.IsOwner || p.gameObject == null) continue;
                        if (string.IsNullOrEmpty(p.gameObject.scene.name)) continue;
                        local = p; via = "IsOwner"; break;
                    }
                }
                catch { }
            }
            if (local == null) sb.Append(" why=").Append(WhyNoLocalPlayer());
            sb.Append($"localPlayer={via}");
            if (local != null)
            {
                try { sb.Append($"='{ClothInternals.PathOf(local.transform)}'"); } catch { }
                sb.Append($" client={SafeOwner(local)}");
            }

            // Setuper under the local player: the game's own applied index for us.
            PlayerModelSetuper set = null;
            Transform owner = local != null ? local.transform : null;
            try
            {
                var all = SceneCache.Scan(Il2CppType.Of<PlayerModelSetuper>());
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    var s = all[i].TryCast<PlayerModelSetuper>();
                    if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy) continue;
                    if (owner != null && !MirrorPlayerVisibility.Under(s.transform, owner)) continue;
                    if (owner == null && string.IsNullOrEmpty(s.gameObject.scene.name)) continue;
                    set = s; break;
                }
            }
            catch { }
            if (set == null) sb.Append(" setuper=none");
            else
            {
                PlayerModelData d = null;
                try { d = set._playerModelData?.Value; } catch { }
                sb.Append($" setuper='{SafePath(set.transform)}' applied=");
                if (d == null) sb.Append("none");
                else
                {
                    int idx = -1; try { idx = d.Index; } catch { }
                    sb.Append($"'{d.name}' idx={idx} ours={(SahurWardrobe.IsOurData(d) ? "yes" : "no")}");
                }
            }

            sb.Append(" want='").Append(SahurWardrobe.WantedState()).Append("'");
            sb.Append(' ').Append(SahurWardrobe.MapBodyState());
            sb.Append(' ').Append(SahurRig.MapBodyState());
            sb.Append(' ').Append(SahurModel.MapBodyState());
            sb.Append($" setups={_setups}");
        }
        catch (Exception e) { sb.Append($" <{e.Message}>"); }
        return sb.ToString();
    }

    // One line, only when the local player did not resolve: every candidate with the bits that
    // decide ownership. A map player that CurrentPlayerProvider does not cover is the difference
    // between "the body was replaced" and "the mod never looked at the right body".
    private static string WhyNoLocalPlayer()
    {
        var sb = new StringBuilder(200);
        try
        {
            int providers = 0, players = 0, models = 0;
            try
            {
                var p = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
                providers = p?.Length ?? 0;
            }
            catch { }
            sb.Append("providers=").Append(providers);
            try
            {
                var arr = SceneCache.Scan(Il2CppType.Of<Gameplay.PlayerSystem.Player>());
                for (int i = 0; arr != null && i < arr.Length && players < 4; i++)
                {
                    var pl = arr[i].TryCast<Gameplay.PlayerSystem.Player>();
                    if (pl == null || pl.gameObject == null) continue;
                    players++;
                    string sc = ""; try { sc = pl.gameObject.scene.name ?? ""; } catch { }
                    string path = ""; try { path = ClothInternals.PathOf(pl.transform); } catch { }
                    bool owner = false; try { owner = pl.IsOwner; } catch { }
                    bool conn = false; try { conn = pl.IsConnected; } catch { }
                    ulong cid = ulong.MaxValue; try { cid = pl.OwnerClientId; } catch { }
                    sb.Append($" | player[{players - 1}] owner={owner} conn={conn} client={cid} " +
                              $"active={(pl.gameObject.activeInHierarchy ? 1 : 0)} scene='{sc}' path='{path}'");
                }
            }
            catch (Exception e) { sb.Append($" players=<{e.Message}>"); }
            try
            {
                var arr = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
                for (int i = 0; arr != null && i < arr.Length && models < 4; i++)
                {
                    var m = arr[i].TryCast<ThirdViewPlayerModel>();
                    if (m == null || m.gameObject == null) continue;
                    string sc = ""; try { sc = m.gameObject.scene.name ?? ""; } catch { }
                    if (string.IsNullOrEmpty(sc)) continue;
                    models++;
                    int bones = -1; try { bones = m.Bones?._allBones?.Count ?? -1; } catch { }
                    sb.Append($" | model[{models - 1}] bones={bones} active={(m.gameObject.activeInHierarchy ? 1 : 0)} " +
                              $"path='{SafePath(m.transform)}'");
                }
            }
            catch (Exception e) { sb.Append($" models=<{e.Message}>"); }
        }
        catch (Exception e) { sb.Append($" <{e.Message}>"); }
        return sb.ToString();
    }

    private static string SafePath(Transform t) { try { return ClothInternals.PathOf(t); } catch { return "?"; } }
    private static int SafeHandle() { try { return SceneManager.GetActiveScene().handle; } catch { return int.MinValue; } }
    private static int ModelsCount() { try { return PlayerModelDatabase.Models?.Length ?? -1; } catch { return -1; } }
    private static string SafeOwner(Gameplay.PlayerSystem.Player p)
    {
        try { return p.OwnerClientId.ToString(); } catch { return "?"; }
    }

    // Runs inside the game's own body build: the one call that replaces what is on screen.
    private static void PostSetupModel(PlayerModelSetuper __instance, PlayerModelData modelData)
    {
        try
        {
            _setups++;
            // The body object (and the renderer/cloth objects under it) just changed. Drop only
            // the body group: the provider/player/session objects are stable and must not be
            // rescanned for every wardrobe change.
            try { SceneCache.InvalidateBody(); } catch { }
            string path = __instance == null ? "?" : SafePath(__instance.transform);
            bool active = false; try { active = __instance != null && __instance.gameObject != null && __instance.gameObject.activeInHierarchy; } catch { }
            int idx = -1; try { idx = modelData?.Index ?? -1; } catch { }
            bool ours = SahurWardrobe.IsOurData(modelData);
            _log?.LogInfo($"{Tag} SetupModel('{(modelData == null ? "null" : modelData.name)}') idx={idx} " +
                          $"ours={(ours ? "yes" : "no")} models={ModelsCount()} setuper='{path}' active={active} " +
                          $"scene='{_scene}' from {JoinTrace.CallerPublic()}");
        }
        catch { }
    }
}
