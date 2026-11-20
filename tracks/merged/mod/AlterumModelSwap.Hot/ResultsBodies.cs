using System;
using System.Collections.Generic;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Gameplay.Results;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// `[results:body]`: re-apply the mod's custom bodies to the models the game builds in the Results
// scene.
//
// Why: Gameplay.Results.ModelsCreatorResultsPostloadable.InstantiatePlayerModel instantiates a
// fresh ThirdViewPlayerModel prefab clone per ResultPlayer from PlayerModelDatabase._models
// [resultPlayer.ModelIndex] (docs/GAMEPLAY.md §4). ModelIndex is the client's stored base row,
// which the wire guard keeps a native donor, so the outcome screen always shows donor bodies and
// every mod body from the map is gone the moment the result screen appears. Observed live
// (task `.mixed-client-active-task.md`, Results reproduction): the local player who wore
// tc2-demoman through the map appeared as the donor model in Results.
//
// Design (research notes in the task file):
//   * a postfix on InstantiatePlayerModel records (model instance, ResultPlayer.FirstOwnerId).
//     Results models are plain Object.Instantiate clones with no owner concept; the only client
//     id handle is FirstOwnerId (NetworkManager.LocalClientId for the local player). No Unity
//     work happens inside the game's call stack beyond reading the two references;
//   * our own per-frame tick resolves each owner's slug - peers from RemoteModels.RemoteSlug,
//     the local player from RemoteModels.LocalPublishedSlug (which survives scene changes,
//     unlike SahurWardrobe.Snapshot/ModelPresentation, whose local sources degrade in Results)
//     - and attaches the matching body with the same owner-agnostic machinery RemoteModels and
//     SahurPreview already use on non-player models: rigid SahurModel.BuildPreviewBody,
//     rigged SahurRig.AttachExternal (+PoseExternal in the late pass). Native renderers are
//     leased hidden (disabled, restored on teardown) exactly like RemoteModels does it;
//   * the body's layer is the host model's own layer (Results camera policy belongs to the
//     scene, not to the local-cosmetic layer 6);
//   * teardown is pointer-keyed: a destroyed model, a model whose scene left Results, an empty
//     slug, or dispose each restores exactly that model's renderers and removes its body.
//     Postload never destroys old instances (a re-run duplicates models in the scene), so
//     pointers, not list positions, are the only stable identity.
//
// Scope: body only. Cosmetics (hats/chest/hips) stay map-scene features; the Results screen
// shows the chosen model, not the full cosmetic set.
internal static class ResultsBodies
{
    private const string Tag = "[results:body]";
    private const string ResultsSceneName = "Results";
    private const int MaxTracked = 64;

    private static IHostContext _log;
    private static Harmony _harmony;

    private sealed class Tracked
    {
        public ThirdViewPlayerModel Model;
        public ulong OwnerId;
        public IntPtr Pointer;          // identity survives even when Unity's == lies
        public string Slug = "";        // slug the current body was built from
        public int BuiltKind;           // 0 none, 1 rigid, 2 rigged
        public GameObject Rigid;
        public Mesh RigidMesh;
        public SahurRig.RigLease Rig;
        public readonly List<HiddenRenderer> Hidden = new();
        public bool LoggedAttach;
        public bool LoggedSkip;
    }

    private struct HiddenRenderer
    {
        public Renderer R;
        public bool HadEnabled;
        public bool Hidden;
    }

    private static readonly List<Tracked> _tracked = new();
    private static readonly List<(ThirdViewPlayerModel Model, ulong OwnerId)> _pending = new();

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_harmony != null) return;
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(ModelsCreatorResultsPostloadable),
                "InstantiatePlayerModel",
                new[] { typeof(ResultPlayer), typeof(PlayerModelData), typeof(Transform) });
            if (m == null)
            {
                log?.LogWarning($"{Tag} InstantiatePlayerModel not found; Results bodies off");
                return;
            }
            _harmony = new Harmony("com.user0.alterum.results-bodies." + Guid.NewGuid().ToString("N"));
            _harmony.Patch(m, postfix: new HarmonyMethod(typeof(ResultsBodies), nameof(PostInstantiate)));
            log?.LogInfo($"{Tag} installed (InstantiatePlayerModel postfix; bodies re-apply on the Results screen)");
        }
        catch (Exception e)
        {
            log?.LogWarning($"{Tag} install failed: {e.Message}");
            _harmony = null;
        }
    }

    // Runs inside the game's InstantiatePlayerModel: record only. The decompiled method returns a
    // NullReferenceException object cast to the model type on failure paths, so the recorded
    // reference is validated again in the tick before any use.
    private static void PostInstantiate(ResultPlayer resultPlayer, ThirdViewPlayerModel __result)
    {
        try
        {
            if (__result == null || resultPlayer == null) return;
            if (_pending.Count >= MaxTracked) _pending.RemoveAt(0);
            _pending.Add((__result, resultPlayer.FirstOwnerId));
        }
        catch { /* a hook must never break the game's own model creation */ }
    }

    internal static void Tick(IHostContext log, float dt)
    {
        if (_harmony == null) return;
        if (_pending.Count > 0)
        {
            var drained = _pending.ToArray();
            _pending.Clear();
            foreach (var (model, ownerId) in drained) Adopt(model, ownerId);
        }
        Reconcile(log);
    }

    internal static void LateTick(IHostContext log)
    {
        for (int i = 0; i < _tracked.Count; i++)
        {
            var t = _tracked[i];
            if (t.BuiltKind != 2) continue;
            try { SahurRig.PoseExternal(log, t.Rig); }
            catch (Exception e) { log?.LogWarning($"{Tag} pose '{t.Slug}' threw: {e.Message}"); }
        }
    }

    private static void Adopt(ThirdViewPlayerModel model, ulong ownerId)
    {
        if (model == null) return;
        foreach (var t in _tracked)
            try { if (t.Model != null && t.Model.Pointer == model.Pointer) return; } catch { }
        if (_tracked.Count >= MaxTracked) Detach(_log, _tracked[0], "tracked-limit");
        _tracked.Add(new Tracked { Model = model, OwnerId = ownerId, Pointer = model.Pointer });
    }

    private static void Reconcile(IHostContext log)
    {
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            var t = _tracked[i];
            string modelPath;
            try
            {
                if (t.Model == null || t.Model.gameObject == null) { Detach(log, t, "model-gone"); _tracked.RemoveAt(i); continue; }
                modelPath = ClothInternals.PathOf(t.Model.transform);
            }
            catch { Detach(log, t, "model-gone"); _tracked.RemoveAt(i); continue; }

            // Scene gate on the model's own scene: Results loads additively, so the active scene
            // can still be the map while Results models exist.
            string scene;
            try { scene = t.Model.gameObject.scene.name ?? ""; } catch (Exception) { scene = ""; }
            if (!string.Equals(scene, ResultsSceneName, StringComparison.Ordinal))
            {
                if (t.BuiltKind != 0 || !t.LoggedSkip)
                    log?.LogInfo($"{Tag} '{modelPath}' scene='{scene}' is not Results; body {(t.BuiltKind == 0 ? "not attached" : "removed")}");
                Detach(log, t, "scene");
                if (t.BuiltKind == 0) { _tracked.RemoveAt(i); }
                continue;
            }

            string slug = SlugFor(t.OwnerId);
            if (slug.Length == 0)
            {
                if (t.BuiltKind != 0) Detach(log, t, "slug-empty");
                else if (!t.LoggedSkip)
                {
                    t.LoggedSkip = true;
                    log?.LogInfo($"{Tag} '{modelPath}' owner={t.OwnerId} has no custom body to apply; the game's own prefab stays");
                }
                continue;
            }

            var spec = ModelRoster.BySlug(slug);
            if (spec == null)
            {
                if (t.BuiltKind != 0) Detach(log, t, "slug-unknown");
                else if (!t.LoggedSkip)
                {
                    t.LoggedSkip = true;
                    log?.LogWarning($"{Tag} '{modelPath}' owner={t.OwnerId} slug='{slug}' is not in the roster; leaving the game's model");
                }
                continue;
            }

            if (t.BuiltKind != 0 && t.Slug == slug) continue;      // already wearing this body
            if (t.BuiltKind != 0) Detach(log, t, "slug-changed");

            int layer = 0;
            try { layer = t.Model.gameObject.layer; } catch { }

            try
            {
                if (spec.Kind == ModelKind.Rigged)
                {
                    // AttachExternal hides the native renderers itself and restores on detach.
                    t.Rig = SahurRig.AttachExternal(log, spec, t.Model, t.Model.transform,
                        $"results:{t.OwnerId}:{slug}", layer);
                    if (t.Rig == null)
                    {
                        log?.LogWarning($"{Tag} '{modelPath}' owner={t.OwnerId} rigged attach failed for '{slug}'; the game's model stays");
                        continue;
                    }
                    t.BuiltKind = 2;
                }
                else
                {
                    HideNative(log, t, modelPath);
                    t.Rigid = SahurModel.BuildPreviewBody(log, spec, t.Model, layer, out var mesh, out var note);
                    t.RigidMesh = mesh;
                    if (t.Rigid == null)
                    {
                        log?.LogWarning($"{Tag} '{modelPath}' owner={t.OwnerId} rigid build failed for '{slug}': {note}");
                        RestoreNative(log, t);
                        continue;
                    }
                    t.BuiltKind = 1;
                }
                t.Slug = slug;
                t.LoggedSkip = false;
                if (!t.LoggedAttach)
                {
                    t.LoggedAttach = true;
                    log?.LogInfo($"{Tag} attached '{slug}' ({(t.BuiltKind == 2 ? "rigged skeleton" : "rigid bind")}) to " +
                                 $"'{modelPath}' owner={t.OwnerId} layer={layer} hiddenNative={t.Hidden.Count}");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"{Tag} '{modelPath}' owner={t.OwnerId} attach threw for '{slug}': {e.Message}");
                RestoreNative(log, t);
                t.BuiltKind = 0;
                t.Slug = "";
            }
        }
    }

    // Local published slug survives scene changes (cleared only on dispose); peers' states
    // survive CheckScene. The local player is never a remote key, so the local check runs when
    // the owner id is not a known remote.
    private static string SlugFor(ulong ownerId)
    {
        string slug = RemoteModels.RemoteSlug(ownerId);
        if (slug.Length > 0) return slug;
        if (RemoteModels.HasRemote(ownerId)) return "";
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.LocalClientId == ownerId) return RemoteModels.LocalPublishedSlug;
        }
        catch { }
        return "";
    }

    private static void HideNative(IHostContext log, Tracked t, string modelPath)
    {
        if (t.Hidden.Count > 0) return;
        try
        {
            var comps = t.Model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var rr = comps[i]?.TryCast<Renderer>();
                if (rr == null) continue;
                if (CosmeticMarkers.IsCosmetic(rr)) continue;      // our own body must not be leased
                if (DefaultHands.IsLeased(rr)) continue;
                var go = rr.gameObject;
                if (go == null || ModelRoster.IsOurObjectName(go.name ?? "")) continue;
                bool had;
                try { had = rr.enabled; } catch { continue; }
                var e = new HiddenRenderer { R = rr, HadEnabled = had };
                try { rr.enabled = false; e.Hidden = true; } catch { }
                t.Hidden.Add(e);
            }
            log?.LogInfo($"{Tag} lease '{modelPath}' hid {t.Hidden.Count} native renderer(s) (disabled, not destroyed)");
        }
        catch (Exception e) { log?.LogWarning($"{Tag} hide '{modelPath}' failed: {e.Message}"); }
    }

    private static void RestoreNative(IHostContext log, Tracked t)
    {
        int restored = 0;
        for (int i = 0; i < t.Hidden.Count; i++)
        {
            var e = t.Hidden[i];
            if (!e.Hidden || e.R == null) continue;
            try { e.R.enabled = e.HadEnabled; restored++; } catch { }
            e.Hidden = false;
            t.Hidden[i] = e;
        }
        if (restored > 0) log?.LogInfo($"{Tag} lease '{t.Slug}' restored {restored} native renderer(s)");
        t.Hidden.Clear();
    }

    private static void Detach(IHostContext log, Tracked t, string why)
    {
        try
        {
            if (t.BuiltKind == 2)
            {
                SahurRig.DetachExternal(log, t.Rig, $"results:{why}");
            }
            else if (t.BuiltKind == 1 && t.Rigid != null)
            {
                SahurModel.DestroyPreviewBody(log, t.Rigid, t.RigidMesh);
            }
            RestoreNative(log, t);
            if (t.BuiltKind != 0)
                log?.LogInfo($"{Tag} detached '{t.Slug}' from owner={t.OwnerId} ({why})");
        }
        catch (Exception e) { log?.LogWarning($"{Tag} detach owner={t.OwnerId} threw: {e.Message}"); }
        finally
        {
            t.BuiltKind = 0;
            t.Slug = "";
            t.Rigid = null;
            t.RigidMesh = null;
            t.Rig = null;
            t.LoggedAttach = false;
        }
    }

    internal static void Dispose()
    {
        try
        {
            for (int i = _tracked.Count - 1; i >= 0; i--) Detach(_log, _tracked[i], "dispose");
        }
        catch { }
        _tracked.Clear();
        _pending.Clear();
        try { _harmony?.UnpatchSelf(); } catch (Exception e) { _log?.LogWarning($"{Tag} unpatch failed: {e.Message}"); }
        _harmony = null;
    }
}
