using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Unattended verification helpers for the bone pass. Nothing here runs unless a request file is
// dropped into the instance's ScreensDir, which is the only trigger that works while the desktop
// is locked:
//
//   mirror.request  -> capture the *mirror* camera (the only camera that draws the local
//                      player's body) with the file's contents as the tag
//   probe.request   -> log every live rig's current bone scales + cloth state
//   probe-force     -> same, plus a measured impulse on the shipped breast cloth: the chest_02
//                      leaf's local rotation is sampled for ~2.5 s and the largest deviation
//                      from the pre-impulse rotation is reported, which is direct evidence that
//                      the shipped MagicaCloth is driving (and therefore still jiggling) the
//                      scaled breast chain.
//   pose            -> front + side silhouette of the local player's body through a temporary
//                      ORTHOGRAPHIC camera on the body layer only, flat magenta background. The
//                      mirror's own camera looks at the room from the baked lobby pose, not at the
//                      player, so this is the comparable before/after view: same pose, same
//                      framing, only the bone scales differ between two builds.
//                      While capturing, the actor's Animator is forced to AlwaysAnimate and its
//                      skinned renderers to updateWhenOffscreen (offscreen culling otherwise freezes
//                      bones); every value is restored immediately afterwards.
internal static class BonesProbe
{
    private const string MirrorRequest = "mirror.request";
    private const string ProbeRequest = "probe.request";
    private const string PoseRequest = "pose.request";
    private const float PollSeconds = 0.5f;

    private static float _nextPoll;

    private sealed class Impulse
    {
        public readonly List<(Transform bone, Quaternion rest, float max, string name)> Bones = new();
        public float Until;
        public string Label;
        public int Phase;            // 0 = poked, 1 = restored, sampling
        public Transform Poke;       // bone whose pose we displace for one frame
        public Quaternion PokeRest;
    }

    private static Impulse _impulse;

    internal static void Tick(IHostContext log, Func<Camera> mirrorCamera, float dt)
    {
        if (Time.unscaledTime >= _nextPoll)
        {
            _nextPoll = Time.unscaledTime + PollSeconds;
            try { PollMirror(log, mirrorCamera); } catch (Exception e) { log.LogWarning($"[probe] mirror request failed: {e.Message}"); }
            try { PollProbe(log); } catch (Exception e) { log.LogWarning($"[probe] probe request failed: {e.Message}"); }
            try { PollPose(log); } catch (Exception e) { log.LogWarning($"[probe] pose request failed: {e.Message}"); }
        }
        SampleImpulse(log);
        SamplePose(log);
    }

    private static string RequestPath(IHostContext log, string name) => Path.Combine(log.ScreensDir, name);

    private static string Take(IHostContext log, string name)
    {
        string path = RequestPath(log, name);
        if (!File.Exists(path)) return null;
        string body = "";
        try { body = File.ReadAllText(path).Trim(); } catch { }
        try { File.Delete(path); } catch { }
        return string.IsNullOrEmpty(body) ? name : body;
    }

    private static void PollMirror(IHostContext log, Func<Camera> mirrorCamera)
    {
        string tag = Take(log, MirrorRequest);
        if (tag == null) return;
        var cam = mirrorCamera?.Invoke();
        if (cam == null) { log.LogWarning("[probe] mirror.request: no mirror camera (is the mirror placed? scene=not-Lobby?)"); return; }
        string path = ScreenShot.Capture(log, "mirror_" + Sanitize(tag), cam);
        log.LogInfo($"[probe] mirror capture tag='{tag}' -> {path ?? "<failed>"}");
    }

    private static void PollProbe(IHostContext log)
    {
        string body = Take(log, ProbeRequest);
        if (body == null) return;
        bool force = body.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0;

        var arr = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
        int rigs = 0;
        var sb = new StringBuilder();
        for (int i = 0; i < arr.Length; i++)
        {
            var m = arr[i].TryCast<ThirdViewPlayerModel>();
            if (m == null) continue;
            var go = m.gameObject;
            if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
            var all = m.Bones?._allBones;
            if (all == null || all.Count == 0) continue;
            rigs++;
            sb.AppendLine($"  '{go.name}' active={go.activeInHierarchy} bones={all.Count} {Scales(all)}");
            if (force && m.Bones != null) ImpulseModel(log, m, all);
        }
        log.LogInfo($"[probe:{Sanitize(body)}] {rigs} rig(s) in scenes:\n{sb}");
    }

    // Current localScale of the bones the pass touches, plus the cloth's own state.
    private static string Scales(Il2CppSystem.Collections.Generic.List<Transform> all)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (n.Equals("pelvis", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_02_l", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_02_r", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_01_l", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chest_01_r", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("thigh_l", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("thigh_r", StringComparison.OrdinalIgnoreCase))
                sb.Append($"{n}.scale={t.localScale} ");
        }
        return sb.ToString();
    }

    private static void ImpulseModel(IHostContext log, ThirdViewPlayerModel m, Il2CppSystem.Collections.Generic.List<Transform> all)
    {
        if (!m.gameObject.activeInHierarchy) return;
        try
        {
            Transform leaf = null, root = null;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name ?? "";
                if (leaf == null && n.Equals("chest_02_l", StringComparison.OrdinalIgnoreCase)) leaf = t;
                else if (root == null && n.Equals("chest_01_l", StringComparison.OrdinalIgnoreCase)) root = t;
            }
            if (leaf == null) return;

            MagicaCloth cloth = null;
            var comps = m.GetComponentsInChildren(Il2CppType.Of<MagicaCloth>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var mc = comps[i]?.TryCast<MagicaCloth>();
                if (mc == null) continue;
                var rb = mc.serializeData?.rootBones;
                for (int j = 0; rb != null && j < rb.Count; j++)
                {
                    var r = rb[j];
                    if (r != null && (r.name ?? "").IndexOf("chest_01", StringComparison.OrdinalIgnoreCase) >= 0) { cloth = mc; break; }
                }
                if (cloth != null) break;
            }
            if (cloth == null) { log.LogWarning($"[probe:force] '{m.gameObject.name}': no breast cloth to push"); return; }

            var imp = new Impulse { Until = Time.unscaledTime + 2.5f, Label = m.gameObject.name };
            imp.Bones.Add((root, root != null ? root.localRotation : Quaternion.identity, 0f, "chest_01_l"));
            imp.Bones.Add((leaf, leaf.localRotation, 0f, "chest_02_l"));
            _impulse = imp;
            // both a sideways and a downward impulse: the second one is what "gravity + sway" feels like
            try { cloth.AddForce(new Vector3(1f, 0f, 0f), 5f, ClothForceMode.VelocityAdd); } catch (Exception e) { log.LogWarning($"[probe:force] X impulse failed: {e.Message}"); }
            try { cloth.AddForce(new Vector3(0f, -1f, 0f), 5f, ClothForceMode.VelocityChange); } catch (Exception e) { log.LogWarning($"[probe:force] Y impulse failed: {e.Message}"); }
            bool running = false;
            try { var p = cloth.process; running = p != null && p.IsRunning(); } catch { }
            string clothGo = "?";
            try { clothGo = $"componentEnabled={cloth.enabled} goActive={cloth.gameObject.activeInHierarchy} clothType={cloth.serializeData?.clothType}"; } catch { }
            log.LogInfo($"[probe:force] '{m.gameObject.name}' pushed breast cloth (root='{root?.name}' leaf='{leaf.name}') running={running} {clothGo}; sampling 2.5s");

            // Give the chain a real displacement for one frame and release it: if the cloth is
            // simulating, its points lag behind the released pose and the bones spring back,
            // which is exactly the jiggle we are trying to prove. A cloth that is not running
            // leaves the bones exactly at the animated pose (deviation 0).
            // The game may have told the cloth to stop writing bones while this body is hidden from
            // every screen camera; ask it to write again so the poke below can be observed.
            try { cloth.SetSkipWriting(false); cloth.SetParameterChange(); } catch (Exception e) { log.LogWarning($"[probe:force] SetSkipWriting/ParameterChange failed: {e.Message}"); }
            if (root != null)
            {
                imp.Poke = root; imp.PokeRest = root.localRotation; imp.Phase = 0;
                root.localRotation = imp.PokeRest * Quaternion.Euler(0f, 0f, 30f);
            }
        }
        catch (Exception e) { log.LogWarning($"[probe:force] failed: {e.Message}"); }
    }

    private static void SampleImpulse(IHostContext log)
    {
        var imp = _impulse;
        if (imp == null) return;
        try
        {
            if (imp.Phase == 0)
            {
                // release the poke on the next frame, then start measuring
                if (imp.Poke != null) { try { imp.Poke.localRotation = imp.PokeRest; } catch { } }
                for (int i = 0; i < imp.Bones.Count; i++)
                {
                    var e = imp.Bones[i];
                    if (e.bone == null) continue;
                    imp.Bones[i] = (e.bone, e.bone.localRotation, 0f, e.name);   // baseline after release
                }
                imp.Phase = 1;
                return;
            }
            for (int i = 0; i < imp.Bones.Count; i++)
            {
                var e = imp.Bones[i];
                if (e.bone == null) continue;
                float deg = Quaternion.Angle(e.rest, e.bone.localRotation);
                if (deg > e.max) { var r = imp.Bones[i]; r.max = deg; imp.Bones[i] = r; }
            }
            if (Time.unscaledTime >= imp.Until)
            {
                var sb = new StringBuilder();
                float max = 0f;
                foreach (var e in imp.Bones) { sb.Append($"{e.name} maxDeviation={e.max:F2}deg "); if (e.max > max) max = e.max; }
                log.LogInfo($"[probe:force-result] '{imp.Label}' {sb}" +
                            (max > 0.5f ? "-> cloth is writing these bones (jiggle active on the scaled chain)"
                                        : "-> neither bone rotated: no cloth-driven motion observed in this state"));
                _impulse = null;
            }
        }
        catch { _impulse = null; }
    }

    // ---------------------------------------------------------------- pose silhouette
    // ScreenShot throttles to one capture per 0.25 s, so the two views are taken one per frame
    // from the same camera setup.
    private sealed class PoseRig
    {
        public GameObject CamGo;
        public Camera Cam;
        public ThirdViewPlayerModel Model;
        public Vector3 Center, Forward, Right;
        public float Dist;
        public int View;
        public string Tag = "";
        public readonly List<SkinnedMeshRenderer> Skinned = new();
        public readonly List<bool> Enabled = new(), Offscreen = new();
        public Animator Animator;
        public AnimatorCullingMode Culling;
    }

    private static PoseRig _pose;

    private static void PollPose(IHostContext log)
    {
        if (_pose != null) return;                  // one rig at a time
        string tag = Take(log, PoseRequest);
        if (tag == null) return;

        ThirdViewPlayerModel model = null;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (go.scene.name == null || go.scene.name.Length == 0) continue;
                if (PathOf(go.transform).IndexOf("Wardrobe", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                model = m;
                break;
            }
        }
        catch (Exception e) { log.LogWarning($"[probe:pose] model scan failed: {e.Message}"); }
        if (model == null) { log.LogWarning("[probe:pose] no active player ThirdViewPlayerModel"); return; }

        var rig = new PoseRig { Model = model, Tag = tag };
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            var layers = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            bool first = true;
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var r = comps[i]?.TryCast<Renderer>();
                if (r == null) continue;
                layers |= 1 << r.gameObject.layer;
                var b = r.bounds;
                if (first) { min = b.min; max = b.max; first = false; }
                else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
                var sk = r.TryCast<SkinnedMeshRenderer>();
                if (sk == null) continue;
                rig.Skinned.Add(sk);
                rig.Enabled.Add(sk.enabled);
                rig.Offscreen.Add(sk.updateWhenOffscreen);
            }
            if (rig.Skinned.Count == 0) { log.LogWarning("[probe:pose] no skinned renderers on the body"); return; }

            // Offscreen culling freezes bones: force animation + bounds updates while we sample.
            var anims = model.GetComponentsInChildren(Il2CppType.Of<Animator>(), true);
            for (int i = 0; anims != null && i < anims.Length; i++)
            {
                var a = anims[i]?.TryCast<Animator>();
                if (a == null) continue;
                rig.Animator = a; rig.Culling = a.cullingMode; a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                break;
            }
            for (int i = 0; i < rig.Skinned.Count; i++) { rig.Skinned[i].enabled = true; rig.Skinned[i].updateWhenOffscreen = true; }

            rig.Center = (min + max) * 0.5f;
            float halfHeight = Mathf.Max(0.5f, (max.y - min.y) * 0.5f);
            Vector3 forward = model.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            rig.Forward = forward;
            rig.Right = Vector3.Cross(Vector3.up, forward).normalized;
            rig.Dist = halfHeight * 3f;

            rig.CamGo = new GameObject("AlterumPoseCam");
            var cam = rig.CamGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) { log.LogWarning("[probe:pose] camera creation failed"); RestorePose(rig); return; }
            rig.Cam = cam;
            cam.enabled = false;                       // manual Render only, never on screen
            cam.orthographic = true;
            cam.orthographicSize = halfHeight * 1.08f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = rig.Dist * 4f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1f, 0f, 1f, 1f);   // flat magenta = trivially segmentable
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.cullingMask = layers;                  // body renderers only (they live on the isolated layer)

            _pose = rig;
            log.LogInfo($"[probe:pose] rig model='{model.gameObject.name}' center={rig.Center} " +
                        $"halfHeight={halfHeight:F3} mask=0x{layers:X} renderers={rig.Skinned.Count} tag='{tag}'");
            SamplePose(log);                           // first view immediately, side next frame
        }
        catch (Exception e)
        {
            log.LogError($"[probe:pose] failed: {e}");
            RestorePose(rig);
        }
    }

    private static void SamplePose(IHostContext log)
    {
        var rig = _pose;
        if (rig == null) return;
        try
        {
            if (rig.View >= 2) { RestorePose(rig); return; }
            var cam = rig.Cam;
            if (cam == null) { RestorePose(rig); return; }
            Vector3 dir = rig.View == 0 ? rig.Forward : rig.Right;
            rig.CamGo.transform.position = rig.Center + dir * rig.Dist;
            rig.CamGo.transform.rotation = Quaternion.LookRotation(rig.Center - rig.CamGo.transform.position, Vector3.up);
            string view = rig.View == 0 ? "front" : "side";
            string path = ScreenShot.Capture(log, $"pose_{view}_{Sanitize(rig.Tag)}", cam);
            if (path == null) return;                  // throttled: retry this view next frame
            log.LogInfo($"[probe:pose] {view} tag='{rig.Tag}' model='{rig.Model.gameObject.name}' -> {path}");
            rig.View++;
            if (rig.View >= 2) RestorePose(rig);
        }
        catch (Exception e)
        {
            log.LogError($"[probe:pose] sample failed: {e}");
            RestorePose(rig);
        }
    }

    private static void RestorePose(PoseRig rig)
    {
        if (rig == null) return;
        if (ReferenceEquals(_pose, rig)) _pose = null;
        for (int i = 0; i < rig.Skinned.Count; i++)
        {
            try { rig.Skinned[i].enabled = rig.Enabled[i]; rig.Skinned[i].updateWhenOffscreen = rig.Offscreen[i]; } catch { }
        }
        try { if (rig.Animator != null) rig.Animator.cullingMode = rig.Culling; } catch { }
        try { if (rig.CamGo != null) UnityEngine.Object.Destroy(rig.CamGo); } catch { }
        rig.Cam = null; rig.CamGo = null;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int guard = 0; p != null && guard < 12; guard++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
            sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' ? s[i] : '_');
        return sb.ToString();
    }
}
