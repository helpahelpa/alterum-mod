using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Read-only census of the isolated local-body layer. It answers, before that layer is ever added to
// a screen camera's culling mask: which live renderers sit on it, which of them belong to the local
// player's own body, and whether anything else on it would start drawing if the mask widened.
//
// Nothing here mutates a renderer, a layer, a camera or a mask - it only reads and logs, so it is
// safe to run in the live instance. The same numbers are the leak guard's evidence when the body bit
// is admitted (see MirrorPlayerVisibility.SyncScreenView).
internal static class BodyLayerCensus
{
    // "<ScreensDir>/bodylayer.request": any text dumps one census of the body layer.
    private const string RequestFileName = "bodylayer.request";
    private const float PollSeconds = 0.5f;
    private const int MaxForeignLines = 40;

    private static float _nextPoll;

    internal sealed class Counts
    {
        public int LiveBody, LiveForeign, InactiveBody, InactiveForeign, Assets, Colliders, CollidersUnderOwner;
        public override string ToString() =>
            $"liveBody={LiveBody} liveForeign={LiveForeign} inactiveBody={InactiveBody} " +
            $"inactiveForeign={InactiveForeign} assetRenderers={Assets} " +
            $"collidersOnLayer={Colliders} collidersUnderLocalPlayer={CollidersUnderOwner}";
    }

    // One census pass. `isBody` must answer "is this renderer part of the local player's own third
    // person body" (the caller owns that set); `owner` is the local player root.
    internal static Counts Dump(IHostContext log, int layer, Transform owner, Func<Renderer, bool> isBody, string why)
    {
        var counts = new Counts();
        if (log == null || layer < 0) return counts;
        try
        {
            var foreign = new StringBuilder();
            var bodyNames = new StringBuilder();
            int foreignLines = 0;
            var all = SceneCache.Scan(Il2CppType.Of<Renderer>());
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i] == null ? null : all[i].TryCast<Renderer>();
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || go.layer != layer) continue;
                if (string.IsNullOrEmpty(go.scene.name)) { counts.Assets++; continue; }   // prefab/asset: never drawn
                bool body = false;
                try { body = isBody(r); } catch { }
                bool live = go.activeInHierarchy && r.enabled;
                if (live) { if (body) counts.LiveBody++; else counts.LiveForeign++; }
                else { if (body) counts.InactiveBody++; else counts.InactiveForeign++; }
                if (body)
                {
                    if (bodyNames.Length < 300) bodyNames.Append(bodyNames.Length == 0 ? "" : ", ").Append('\'').Append(go.name).Append('\'');
                }
                else if (foreignLines++ < MaxForeignLines)
                    foreign.Append($"\n  [bodylayer:census:foreign] '{go.name}' path='{MirrorPlayerVisibility.Hierarchy(go.transform)}' " +
                                   $"type={r.GetIl2CppType().Name} active={go.activeInHierarchy} enabled={r.enabled} " +
                                   $"forceOff={r.forceRenderingOff} bounds={r.bounds} " +
                                   $"material='{(r.sharedMaterial == null ? "null" : r.sharedMaterial.name)}' " +
                                   $"mine={MirrorPlayerVisibility.Under(go.transform, owner)}");
            }
            Colliders(log, layer, owner, counts);
            log.LogInfo($"[bodylayer:census] why={why} layer={layer} ('{LayerMask.LayerToName(layer)}') {counts} " +
                        $"bodyRenderers=[{bodyNames}]");
            if (foreign.Length > 0)
                log.LogInfo($"[bodylayer:census] foreign renderers on layer {layer}:{foreign}");
            else
                log.LogInfo($"[bodylayer:census] no foreign renderer on layer {layer}: a wider culling mask adds " +
                            $"only the local body's own {counts.LiveBody} live renderer(s)");
            return counts;
        }
        catch (Exception e) { log.LogWarning($"[bodylayer:census] failed: {e}"); return counts; }
    }

    // Colliders are NOT affected by a culling mask (a camera mask is a render filter only), but the
    // layer is named Hitbox, so the census has to show what physics puts there as well - and, since
    // 147 of the lobby's 162 layer-14 colliders are not the local player's, which objects own them.
    private static void Colliders(IHostContext log, int layer, Transform owner, Counts counts)
    {
        var roots = new Dictionary<int, (string name, int count, bool mine)>();
        var order = new List<int>();
        void Scan<T>(string kind) where T : Collider
        {
            Il2CppSystem.Type type;
            try { type = Il2CppType.Of<T>(); } catch { return; }
            var arr = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < arr.Length; i++)
            {
                var col = arr[i] == null ? null : arr[i].TryCast<Collider>();
                if (col == null) continue;
                var go = col.gameObject;
                if (go == null || go.layer != layer || string.IsNullOrEmpty(go.scene.name)) continue;
                bool mine = MirrorPlayerVisibility.Under(go.transform, owner);
                counts.Colliders++;
                if (mine) counts.CollidersUnderOwner++;
                var owner_ = MirrorPlayerVisibility.BranchRoot(go.transform);
                int id = 0;
                string name = owner_ != null ? owner_.name : go.name;
                try { id = owner_ != null ? owner_.gameObject.GetInstanceID() : 0; } catch { }
                if (!roots.TryGetValue(id, out var entry)) { entry = (name, 0, mine); order.Add(id); }
                roots[id] = (entry.name, entry.count + 1, entry.mine || mine);
            }
        }
        Scan<BoxCollider>("box");
        Scan<SphereCollider>("sphere");
        Scan<CapsuleCollider>("capsule");
        Scan<MeshCollider>("mesh");
        Scan<CharacterController>("character");
        if (counts.Colliders == 0) return;
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var id in order)
        {
            var e = roots[id];
            if (e.mine) continue;
            if (shown++ >= 16) { sb.Append($" ... +{order.Count - shown + 1} more root(s)"); break; }
            sb.Append($" '{e.name}'#0x{id:X}={e.count};").Replace("\n", " ");
        }
        log.LogInfo($"[bodylayer:census] colliders on layer {layer}: total={counts.Colliders} " +
                    $"underLocalPlayer={counts.CollidersUnderOwner} (a culling mask never touches these) " +
                    $"foreignRoots=[{(sb.Length == 0 ? "none" : sb.ToString().Trim())}]");
    }

    // Cheap guard used on every periodic rescan while the body bit is admitted: counts the live
    // renderers on the layer that are NOT this body's own, naming a few so a refused admission can
    // say what refused it. No collider scan, no per-renderer lines.
    internal static int LiveForeign(IHostContext log, int layer, Func<Renderer, bool> isBody, out string names)
    {
        names = null;
        int foreign = 0;
        var sb = new StringBuilder();
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Renderer>());
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i] == null ? null : all[i].TryCast<Renderer>();
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || go.layer != layer || string.IsNullOrEmpty(go.scene.name)) continue;
                if (!go.activeInHierarchy || !r.enabled) continue;
                bool body = false;
                try { body = isBody(r); } catch { }
                if (body) continue;
                if (foreign++ < 4) sb.Append(foreign == 1 ? "" : ", ").Append('\'').Append(go.name).Append('\'');
            }
        }
        catch (Exception e) { log?.LogWarning($"[bodylayer:guard] failed: {e}"); return 0; }
        if (foreign > 0) names = sb.ToString();
        return foreign;
    }

    // Called every frame from MirrorPlayerVisibility; cheap unless a request file is present.
    internal static void Tick(IHostContext log, int layer, Transform owner, Func<Renderer, bool> isBody)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path).Trim(); } catch { }
        try { File.Delete(path); } catch { }
        Dump(log, layer, owner, isBody, string.IsNullOrEmpty(text) ? "request" : "request:" + text);
        // "check" also dumps the live binding's own view of the body (enabled/selected per renderer,
        // which is what the mirror camera actually draws), because a layer census alone cannot say
        // whether the body is currently enabled for the mirror.
        if (text.IndexOf("check", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try { MirrorPlayerVisibility.Current?.Diagnose(null); }
            catch (Exception e) { log.LogWarning($"[bodylayer:check] failed: {e.Message}"); }
        }
    }
}
