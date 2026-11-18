using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Before/after capture rig for the proportions tuning pass. Three orthographic views of the local
// player's body (front, three-quarter, side) on a flat background, from a camera whose framing is
// derived from the *frozen* skeleton, not from the inflated renderer bounds - so the previous
// values and the new values are pixel-comparable, same camera, same pose, same lighting.
//
// Request file "<ScreensDir>/propcap.request", line-oriented, polled at 2 Hz:
//   freeze=1          disable the body's animators (pose frozen until release=1); computes and
//                     PINNERS the framing once, so every later capture uses the same camera
//   release=1         restore animators, destroy the capture camera/RT
//   tag=before        capture tag -> shot_t1_prop_<view>_<tag>_<timestamp>_NN.png
//   views=front,three,side   subset/order (default all three)
//   breastscale=1.55  hipscale=1.50  chestscale=1.097
//                     re-apply Proportions at these values before capturing (breast = leaf,
//                     chestscale = garment cage; total chest = breast * chestscale). A capture
//                     taken in the same frame as the change can still show the PREVIOUS state:
//                     send the same request twice and keep the second capture.
//   measure=1         log bone-derived hip/shoulder width and height for the current rig
//   names=1           log every bone name + world position once (to pick measurement landmarks)
//   hier=1            log the chest/spine/shoulder subtree (parent, local pos/scale, children)
//   weights=<match>   per skinned mesh: bone list + per-bone weight mass, and the same mass
//                     restricted to the chest band (between the chest pivot and the neck), which
//                     is what tells us which bones actually carry the tank top over the bust
//   chestdump=<match> write one CSV per matched skinned mesh: bind-space vertex positions, the four
//                     weights per vertex, every bone's bind-pose head and the triangle list (how
//                     the pass-5 chest region rule was chosen from the real mesh)
//   chestfix=0|1      enable/disable the garment chest-weight rewrite live (A/B captures); off
//                     restores the authored sharedMesh and destroys the clones
//   list=1            log every live ThirdViewPlayerModel in a scene (name/path/renderers/layers)
//   width=720 height=1080   capture RT size (defaults; clamped 240..1600 x 240..1600)
//
// On completion it writes "<ScreensDir>/propcap_<tag>.txt" with the ortho parameters, the
// metres-per-pixel and the screen-space position of every landmark, which is what the Python
// measurement/montage script uses to convert silhouette pixels back to metres.
internal static class ProportionShots
{
    private const string RequestName = "propcap.request";
    private const float PollSeconds = 0.5f;
    private const float BetweenViews = 0.4f;
    private const int DefaultWidth = 720, DefaultHeight = 1080;
    private const int MinSize = 240, MaxSize = 1600;

    private sealed class AnimState
    {
        public Animator A;
        public bool Enabled;
        public AnimatorCullingMode Culling;
    }

    private sealed class SkinState
    {
        public SkinnedMeshRenderer R;
        public bool Enabled;
        public bool Offscreen;
    }

    private sealed class Session
    {
        public string Tag = "shot";
        public List<string> Views = new();
        public int Next;
        public double NextAt;
        public int Width = DefaultWidth, Height = DefaultHeight;
        public readonly StringBuilder Info = new();
    }

    private static float _nextPoll;
    private static DateTime _reqMtime = DateTime.MinValue;

    // Frozen state, kept between requests so before/after share the exact pose and framing.
    private static readonly List<AnimState> _frozen = new();
    private static readonly List<SkinState> _skins = new();
    private static ThirdViewPlayerModel _model;
    private static Vector3 _center, _forward, _right;
    private static float _halfHeight, _dist;
    private static int _mask;
    private static bool _framed;

    private static GameObject _camGo;
    private static Camera _cam;
    private static RenderTexture _rt;
    private static int _rtW, _rtH;
    private static Session _session;
    private static Proportions _props;

    // An inactive wardrobe preview has to be shown for a moment before it animates; the freeze is
    // deferred so the captured pose is the preview's idle pose, not the asset's bind pose.
    private static ThirdViewPlayerModel _spawned;
    private static ThirdViewPlayerModel _pendingFreeze;
    private static bool _pendingRebind, _pendingPoseCopy;
    private static double _freezeDue;
    private static readonly List<GameObject> _activated = new();

    // The wardrobe preview carousel keeps moving/animating its rig even with the Animator
    // component disabled. The pose is snapshotted at freeze time and restored immediately before
    // every render, so all six before/after images are the same pose at the same world transform.
    private sealed class BoneSnap { public Transform T; public bool IsPelvis; public bool IsPelvisChild; public Vector3 Pos; public Quaternion Rot; }
    private static readonly List<BoneSnap> _snap = new();
    private static Vector3 _rootPos, _snapHead;
    private static Quaternion _rootRot;

    internal static void Tick(IHostContext log, Proportions props)
    {
        _props = props;
        try { Poll(log, props); } catch (Exception e) { log.LogWarning($"[propcap] request failed: {e.Message}"); }
        try
        {
            if (_pendingFreeze != null && Time.realtimeSinceStartupAsDouble >= _freezeDue)
            {
                var m = _pendingFreeze;
                _pendingFreeze = null;
                Freeze(log, m, _pendingRebind, _pendingPoseCopy);
            }
        }
        catch (Exception e) { log.LogWarning($"[propcap] deferred freeze failed: {e.Message}"); }
        try { Advance(log); } catch (Exception e) { log.LogWarning($"[propcap] capture failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- request
    private static void Poll(IHostContext log, Proportions props)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;

        string path;
        try { path = Path.Combine(log.ScreensDir, RequestName); } catch { return; }
        if (!File.Exists(path)) return;
        var mt = File.GetLastWriteTimeUtc(path);
        if (mt == _reqMtime) return;
        _reqMtime = mt;
        string body;
        try { body = File.ReadAllText(path); } catch { return; }
        try { File.Delete(path); } catch { }      // one-shot: never re-runs on a timestamp bump

        string tag = null, views = null, match = null;
        bool freeze = false, release = false, measure = false, names = false, list = false, activate = false, datas = false;
        bool hier = false;
        string setModel = null, spawn = null, hide = null, show = null, play = null, weights = null;
        string hideParts = null, showParts = null;
        bool partsAll = false, solo = false, comps = false;
        string pen = null;
        string chestDump = null;
        bool? chestFix = null;
        bool rebind = false, posecopy = false, clips = false;
        string posefrom = null;
        bool worldrot = false;
        float breast = Proportions.BreastLeafScale, hip = Proportions.HipScale, cage = Proportions.ChestCageScale;
        bool clavGrow = Proportions.ClavicleGrow;
        int width = DefaultWidth, height = DefaultHeight;
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = line.Substring(eq + 1).Trim();
            bool on = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);
            switch (k)
            {
                case "tag": tag = Sanitize(v); break;
                case "model": match = v; break;
                case "views": views = v; break;
                case "freeze": freeze = on; break;
                case "release": release = on; break;
                case "measure": measure = on; break;
                case "names": names = on; break;
                case "hier": hier = on; break;
                case "weights": weights = v; break;
                case "list": list = on; break;
                case "activate": activate = on; break;
                case "datas": datas = on; break;
                case "setmodel": setModel = v; break;
                case "spawn": spawn = v; break;
                case "hide": hide = v; break;
                case "hideparts": hideParts = v; break;
                case "showparts": showParts = v; break;
                case "solo": solo = on; break;
                case "comps": comps = on; break;
                case "pen": pen = v; break;
                case "chestdump": chestDump = v; break;
                case "chestfix": chestFix = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                case "partsall":
                    if (v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase)) partsAll = true;
                    break;
                case "clips": clips = on; break;
                case "play": play = v; break;
                case "show": show = v; break;
                case "rebind": rebind = on; break;
                case "posecopy": posecopy = on; break;
                case "posefrom": posecopy = true; posefrom = v; break;
                case "worldrot": worldrot = on; break;
                case "width": width = Clamp(I(v, width), MinSize, MaxSize); break;
                case "height": height = Clamp(I(v, height), MinSize, MaxSize); break;
                case "breastscale": breast = F(v, breast); break;
                case "hipscale": hip = F(v, hip); break;
                case "chestscale": cage = F(v, cage); break;
                case "clavgrow": clavGrow = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                default: log.LogWarning($"[propcap] key '{k}' unknown; ignored"); break;
            }
        }

        if (release) { Release(log); return; }
        if (hide != null) SetModelsActive(log, hide, false);
        if (show != null) SetModelsActive(log, show, true);

        ThirdViewPlayerModel model;
        if (spawn != null)
        {
            model = Spawn(log, spawn);
            if (model == null) return;
        }
        else if (_spawned != null && (match == null || (_spawned.gameObject.name ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            model = _spawned;    // keep using our own display clone
        }
        else
        {
            model = FindModel(log, match);
            if (model == null) { log.LogWarning("[propcap] no ThirdViewPlayerModel matching the request"); return; }
        }

        // Capture isolation: the local player's own third-person body sits on the same layer as
        // the display clone and keeps animating (the freeze only owns our clone), so a different
        // walk-cycle frame leaks into every capture. solo=1 deactivates every other model and
        // release=1 puts them back.
        if (solo) Solo(log, model);
        if (comps) DumpComponents(log, model);

        // Per-renderer hide/show, by GameObject or mesh name. Used by the clipping check: render
        // the full body, the body without the garment, and the garment without the body.
        if (partsAll) SetPartsEnabled(log, model, "", true);
        if (hideParts != null) foreach (var m in hideParts.Split(',')) if (m.Trim().Length > 0) SetPartsEnabled(log, model, m.Trim(), false);
        if (showParts != null) foreach (var m in showParts.Split(',')) if (m.Trim().Length > 0) SetPartsEnabled(log, model, m.Trim(), true);

        if (list) ListModels(log);
        if (datas) ListData(log);
        if (setModel != null) SwitchModel(log, setModel);

        bool activated = false;
        if (activate && !model.gameObject.activeInHierarchy) activated = Activate(log, model);
        if (spawn != null) activated = true;

        if (clips) DumpClips(log, model);
        if (posefrom != null) _poseFrom = posefrom;
        _worldRot = worldrot;
        if (freeze)
        {
            if (activated)
            {
                _pendingFreeze = model;
                _pendingRebind = rebind;
                _pendingPoseCopy = posecopy;
                _freezeDue = Time.realtimeSinceStartupAsDouble + 0.9;
                log.LogInfo($"[propcap] freeze deferred 0.9s after activating '{model.gameObject.name}' (idle pose, not bind pose)");
            }
            else Freeze(log, model, rebind, posecopy);
        }
        if (play != null) PlayClip(log, model, play);
        if (_model == null)
        {
            if (names || measure || tag != null)
                log.LogWarning("[propcap] no framed rig yet: activate+freeze first, then send the capture request");
            return;
        }

        // Re-apply the bone pass BEFORE measuring/capturing; SetScales restores from unit values
        // and recomputes the counter-scale, so nothing derived from the old ratio survives.
        if (clavGrow != Proportions.ClavicleGrow) props.SetClavicleGrow(clavGrow, "propcap.request");
        if (chestFix.HasValue && chestFix.Value != props.ChestFixEnabled) props.SetChestFix(chestFix.Value, "propcap.request");
        if (Math.Abs(breast - Proportions.BreastLeafScale) > 1e-4f || Math.Abs(hip - Proportions.HipScale) > 1e-4f ||
            Math.Abs(cage - Proportions.ChestCageScale) > 1e-4f)
            props.SetScales(breast, hip, cage, "propcap.request");

        RestoreSnap();
        _props?.ReapplyAll("measure/capture");
        if (names) DumpNames(log, _model);
        if (hier) DumpHierarchy(log, _model);
        if (pen != null) DumpPenetration(log, _model, pen);
        if (weights != null) DumpWeights(log, _model, weights);
        if (chestDump != null) ChestWeights.Dump(log, _model, chestDump);
        if (measure) Measure(log, _model, tag ?? "measure");

        if (tag == null) return;
        if (_session != null)
        {
            log.LogWarning($"[propcap] capture '{_session.Tag}' still running ({_session.Next}/{_session.Views.Count}); request '{tag}' ignored");
            return;
        }

        var s = new Session { Tag = tag, Width = width, Height = height };
        foreach (var v in (views ?? "front,three,side").Split(','))
        {
            string n = v.Trim().ToLowerInvariant();
            if (n == "front" || n == "three" || n == "side" || n == "back") s.Views.Add(n);
        }
        if (s.Views.Count == 0) s.Views.Add("front");
        s.NextAt = Time.realtimeSinceStartupAsDouble;     // first view as soon as the throttle allows
        s.Info.AppendLine($"tag={s.Tag}");
        s.Info.AppendLine(Fmt($"center={_center} forward={_forward} right={_right} halfHeight={_halfHeight:F5} dist={_dist:F4}"));
        s.Info.AppendLine(Fmt($"orthoSize={_halfHeight:F6} aspect={(float)s.Width / s.Height:F6} width={s.Width} height={s.Height} " +
                              $"mpp={(2f * _halfHeight / s.Height):F8} mask=0x{_mask:X8}"));
        _session = s;
        EnsureCamera(log, s.Width, s.Height);
        log.LogInfo($"[propcap] capture tag='{s.Tag}' views=[{string.Join(",", s.Views)}] {s.Width}x{s.Height} " +
                    $"pose=FROZEN breast={Proportions.BreastLeafScale:F2} hip={Proportions.HipScale:F2} " +
                    $"cage={Proportions.ChestCageScale:F3} totalChest={Proportions.ChestTotalScale:F2} " +
                    $"camera=ortho/{(2f * _halfHeight / s.Height) * 1000f:F2}mm-per-px centered on the frozen skeleton");
    }

    private static void Advance(IHostContext log)
    {
        var s = _session;
        if (s == null) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < s.NextAt) return;
        if (s.Next >= s.Views.Count) { Finish(log, s); return; }

        var cam = _cam;
        if (cam == null) { log.LogWarning("[propcap] camera gone; aborting capture"); _session = null; return; }

        RestoreSnap();
        _props?.ReapplyAll("capture view");
        string view = s.Views[s.Next];
        Vector3 dir = view switch
        {
            "three" => Quaternion.AngleAxis(-40f, Vector3.up) * _forward,
            "side" => _right,
            "back" => -_forward,
            _ => _forward,
        };
        var pos = _center + dir * _dist;
        var go = cam.gameObject;
        go.transform.position = pos;
        go.transform.rotation = Quaternion.LookRotation((_center - pos).normalized, Vector3.up);

        // Diagnostic: how far is the live skeleton from the snapshot we just restored?
        int rotDiff = 0;
        for (int i = 0; i < _snap.Count; i++)
        {
            var sn = _snap[i];
            if (sn.T == null) continue;
            if (Quaternion.Angle(sn.T.localRotation, sn.Rot) > 0.05f) rotDiff++;
        }
        var headNow = Bone(_model, "head");
        log.LogInfo($"[propcap:pose] view={view} rotDiff={rotDiff}/{_snap.Count} " +
                    $"headSnap={_snapHead} headNow={(headNow != null ? headNow.position.ToString() : "null")}");

        // Warm render: forces the skinning/bounds update for the restored pose into our own RT
        // before ScreenShot allocates its RT and renders the frame that is written to disk.
        try { cam.Render(); } catch { }
        string path = ScreenShot.Capture(log, $"prop_{view}_{s.Tag}", cam);
        if (path == null) { s.NextAt = now + 0.05; return; }   // ScreenShot throttle: retry next frame

        // Landmark pixels for this view, so the Python side can measure at exact bone heights.
        var sb = new StringBuilder();
        sb.AppendLine($"view={view} camera={Fmt(pos.ToString())} path={Path.GetFileName(path)}");
        Landmark(sb, cam, "pelvis");
        Landmark(sb, cam, "thigh_l");
        Landmark(sb, cam, "thigh_r");
        Landmark(sb, cam, "chest_01_l");
        Landmark(sb, cam, "chest_01_r");
        Landmark(sb, cam, "chest_02_l");
        Landmark(sb, cam, "chest_02_r");
        Landmark(sb, cam, "head");
        Landmark(sb, cam, "neck");
        foreach (var n in new[] { "shoulder_l", "shoulder_r", "clavicle_l", "clavicle_r", "upper_arm_l", "upper_arm_r", "foot_l", "foot_r" })
            Landmark(sb, cam, n);
        s.Info.Append(sb);

        log.LogInfo($"[propcap:shot] tag='{s.Tag}' view={view} -> {path}");
        s.Next++;
        s.NextAt = now + BetweenViews;
        if (s.Next >= s.Views.Count) Finish(log, s);
    }

    private static void Finish(IHostContext log, Session s)
    {
        _session = null;
        try
        {
            string info = Path.Combine(log.ScreensDir, "propcap_" + Sanitize(s.Tag) + ".txt");
            File.WriteAllText(info, s.Info.ToString());
            log.LogInfo($"[propcap:done] tag='{s.Tag}' views={s.Views.Count} info={info}");
        }
        catch (Exception e) { log.LogWarning($"[propcap:done] info write failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- freeze / framing
    private static bool Activate(IHostContext log, ThirdViewPlayerModel model)
    {
        try
        {
            for (var t = model.transform; t != null; t = t.parent)
            {
                var go = t.gameObject;
                if (go == null || go.activeSelf) continue;
                go.SetActive(true);
                _activated.Add(go);
                if (_activated.Count > 8) break;
            }
            model.gameObject.SetActive(true);
            model.enabled = true;
            log?.LogInfo($"[propcap:activate] '{model.gameObject.name}' + {_activated.Count} ancestor(s) -> " +
                         $"activeInHierarchy={model.gameObject.activeInHierarchy}");
            return true;
        }
        catch (Exception e) { log?.LogWarning($"[propcap] activation failed: {e.Message}"); return false; }
    }

    private static void Freeze(IHostContext log, ThirdViewPlayerModel model, bool rebind, bool posecopy)
    {
        if (_model != null && Ptr(_model) == Ptr(model) && _framed) return;
        bool keepSpawn = ReferenceEquals(_spawned, model);
        Release(log, keepSpawn); // a different rig: drop the old freeze/camera first
        _model = model;

        // A disabled Animator no longer marks the skinned meshes dirty; make them update their
        // bounds/skinning when the capture camera renders off-screen. Otherwise the first render
        // after a pose change shows the previous deformation (stale mesh, current skeleton).
        var skins = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        for (int i = 0; skins != null && i < skins.Length; i++)
        {
            var sk = skins[i]?.TryCast<SkinnedMeshRenderer>();
            if (sk == null) continue;
            _skins.Add(new SkinState { R = sk, Enabled = sk.enabled, Offscreen = sk.updateWhenOffscreen });
            sk.enabled = true;
            sk.updateWhenOffscreen = true;
        }

        FreezePhysics(log, model);
        var anims = model.GetComponentsInChildren(Il2CppType.Of<Animator>(), true);
        for (int i = 0; anims != null && i < anims.Length; i++)
        {
            var a = anims[i]?.TryCast<Animator>();
            if (a == null) continue;
            var st = new AnimState { A = a, Enabled = a.enabled, Culling = a.cullingMode };
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            a.enabled = false;
            _frozen.Add(st);
        }
        if (posecopy)
        {
            int copied = CopyPoseFromPlayer(log, model);
            log.LogInfo($"[propcap] posecopy: {copied} bone local rotations copied from the local player's idle");
        }
        if (rebind)
        {
            // Deterministic neutral pose (the asset's bind pose) for every capture, instead of
            // whatever frame the prefab's default animation happens to be on.
            int n = 0;
            foreach (var st in _frozen)
                try { if (st.A != null) { st.A.Rebind(); st.A.Update(0f); n++; } } catch { }
            log.LogInfo($"[propcap] rebind requested: {n} animator(s) reset to the bind pose");
        }

        TakeSnap(model);

        // Mask from the renderers; VERTICAL FRAMING FROM BONES. Renderer.bounds on a freshly
        // instantiated prefab comes back in the wrong space in this interop build, while bone
        // world positions are exact. Feet and head are posed, but the two captures happen in the
        // same frozen pose, so the frame is identical and independent of the scaling.
        float minY = float.MaxValue, maxY = float.MinValue;
        var rends = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
        _mask = 0;
        int skinned = 0;
        for (int i = 0; rends != null && i < rends.Length; i++)
        {
            var r = rends[i]?.TryCast<Renderer>();
            if (r == null) continue;
            _mask |= 1 << r.gameObject.layer;
            if (r.TryCast<SkinnedMeshRenderer>() != null) skinned++;
        }

        Transform pelvis = Bone(model, "pelvis");
        Transform head = Bone(model, "head");
        if (pelvis == null) { log.LogWarning("[propcap] no pelvis bone; cannot frame"); Release(log); return; }

        var all = model.Bones?._allBones;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (n.IndexOf("foot", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("toe", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try { if (t.position.y < minY) minY = t.position.y; } catch { }
        }
        if (minY == float.MaxValue) minY = pelvis.position.y - 0.95f;
        maxY = head != null ? head.position.y + 0.20f : pelvis.position.y + 1.1f;

        _center = new Vector3(pelvis.position.x, (minY + maxY) * 0.5f, pelvis.position.z);
        _halfHeight = Mathf.Max(0.4f, (maxY - minY) * 0.5f * 1.08f);
        // Camera axes from the posed CHEST axis, not the model root: a retargeted pose can rotate
        // the body inside its root, and the front view must still look at the chest.
        var chestL = Bone(model, "chest_01_l");
        var chestR = Bone(model, "chest_01_r");
        var right = chestL != null && chestR != null ? chestR.position - chestL.position : Vector3.zero;
        right.y = 0f;
        if (right.sqrMagnitude > 1e-6f)
        {
            _right = right.normalized;
            _forward = Vector3.Cross(_right, Vector3.up).normalized;   // right x up = forward
        }
        else
        {
            var fwd = model.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            _forward = fwd.normalized;
            _right = Vector3.Cross(Vector3.up, _forward).normalized;
        }
        _dist = Mathf.Max(1.2f, (maxY - minY) * 2.2f);
        _framed = true;

        log.LogInfo($"[propcap:freeze] model='{model.gameObject.name}' animators={_frozen.Count} renderers={skinned} " +
                    $"mask=0x{_mask:X8} bodyY={minY:F3}..{maxY:F3} frameCenter={_center} halfHeight={_halfHeight:F3} " +
                    "pose frozen (identical for every capture until release=1)");
    }

    // Copies the local rotations of every same-named bone from the local player's rig onto the
    // capture rig. The display prefab's own default animation is a random crouch; the player is
    // standing in its idle, which is the pose a proportions change should be judged in.
    private static string _poseFrom;
    private static bool _worldRot;

    private static int CopyPoseFromPlayer(IHostContext log, ThirdViewPlayerModel target)
    {
        ThirdViewPlayerModel src = null;
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                if (ReferenceEquals(m, target)) continue;
                var go = m.gameObject;
                if (go.scene.name == null || go.scene.name.Length == 0) continue;
                if (_poseFrom != null)
                {
                    if ((go.name ?? "").IndexOf(_poseFrom, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    src = m; break;   // inactive rigs keep their bone transforms; a valid pose either way
                }
                if (!go.activeInHierarchy) continue;
                string path = PathOf(go.transform);
                if (path.IndexOf("LobbyPlayer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                src = m;
                break;
            }
        }
        catch { }
        if (src == null) { log.LogWarning($"[propcap] posecopy: no source rig (posefrom='{_poseFrom}')"); return 0; }

        var ta = target.Bones?._allBones;
        var sa = src.Bones?._allBones;
        if (ta == null || sa == null) return 0;
        var byName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sa.Count; i++)
        {
            var t = sa[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (n.Length > 0 && !byName.ContainsKey(n)) byName[n] = t;
        }
        int copied = 0;
        if (_worldRot)
        {
            // Retarget by WORLD orientation. Local rotations are relative to each rig's own rest
            // orientation; a cross-generation local copy folds the source's stance into the
            // target's rest pose (that is what produced the seated pose). Setting world
            // rotations drives both rigs to the same limb directions, parent before child.
            var order = new List<Transform>();
            for (int i = 0; i < ta.Count; i++) if (ta[i] != null) order.Add(ta[i]);
            order.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            foreach (var t in order)
            {
                if (!byName.TryGetValue(t.name ?? "", out var s2) || s2 == null) continue;
                t.rotation = s2.rotation;
                copied++;
            }
        }
        else
        {
            for (int i = 0; i < ta.Count; i++)
            {
                var t = ta[i];
                if (t == null) continue;
                if (!byName.TryGetValue(t.name ?? "", out var s2) || s2 == null) continue;
                t.localRotation = s2.localRotation;
                if ((t.name ?? "").Equals("pelvis", StringComparison.OrdinalIgnoreCase)) t.localPosition = s2.localPosition;
                copied++;
            }
        }
        return copied;
    }

    private static void DumpClips(IHostContext log, ThirdViewPlayerModel model)
    {
        try
        {
            var a = model.GetComponentInChildren(Il2CppType.Of<Animator>())?.TryCast<Animator>();
            if (a == null) { log.LogWarning("[propcap:clips] no animator"); return; }
            var ctrl = a.runtimeAnimatorController;
            var names = new StringBuilder();
            if (ctrl != null)
            {
                var cs = ctrl.animationClips;
                for (int i = 0; cs != null && i < cs.Length; i++)
                {
                    var c = cs[i];
                    if (c == null) continue;
                    names.Append(c.name).Append(':').Append(c.length.ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
                }
            }
            var cur = a.GetCurrentAnimatorClipInfo(0);
            var curNames = new StringBuilder();
            for (int i = 0; cur != null && i < cur.Length; i++)
                try { var c = cur[i].clip; if (c != null) curNames.Append(c.name).Append(' '); } catch { }
            log.LogInfo($"[propcap:clips] model='{model.gameObject.name}' controller='{(ctrl != null ? ctrl.name : "null")}' " +
                        $"current=[{curNames}] clips=[{names}] speed={a.speed} enabled={a.enabled}");
        }
        catch (Exception e) { log.LogWarning($"[propcap:clips] failed: {e.Message}"); }
    }

    private static void PlayClip(IHostContext log, ThirdViewPlayerModel model, string spec)
    {
        try
        {
            int at = spec.LastIndexOf('@');
            string match = at > 0 ? spec.Substring(0, at) : spec;
            float t = at > 0 ? F(spec.Substring(at + 1), 0.5f) : 0.5f;
            var a = model.GetComponentInChildren(Il2CppType.Of<Animator>())?.TryCast<Animator>();
            var ctrl = a?.runtimeAnimatorController;
            if (a == null || ctrl == null) { log.LogWarning("[propcap:play] no animator/controller"); return; }
            string found = null;
            var cs = ctrl.animationClips;
            for (int i = 0; cs != null && i < cs.Length; i++)
            {
                var c = cs[i];
                if (c == null) continue;
                if ((c.name ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                found = c.name; break;
            }
            if (found == null) { log.LogWarning($"[propcap:play] no clip matching '{match}'"); return; }
            var st = a.GetCurrentAnimatorStateInfo(0);
            a.enabled = true;
            a.Play(found, 0, Mathf.Clamp01(t));
            a.Update(0f);
            a.enabled = false;
            log.LogInfo($"[propcap:play] '{found}' at {t:F2}; animator frozen on that frame (state='{st.shortNameHash}')");
        }
        catch (Exception e) { log.LogWarning($"[propcap:play] failed: {e.Message}"); }
    }

    private static void TakeSnap(ThirdViewPlayerModel model)
    {
        _snap.Clear();
        try
        {
            var all = model.Bones?._allBones;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                bool pelvis = (t.name ?? "").Equals("pelvis", StringComparison.OrdinalIgnoreCase);
                bool child = t.parent != null && (t.parent.name ?? "").Equals("pelvis", StringComparison.OrdinalIgnoreCase);
                _snap.Add(new BoneSnap { T = t, IsPelvis = pelvis, IsPelvisChild = child, Pos = t.localPosition, Rot = t.localRotation });
            }
            _rootPos = model.transform.position;
            _rootRot = model.transform.rotation;
            var h = Bone(model, "head");
            _snapHead = h != null ? h.position : Vector3.zero;
        }
        catch { }
    }

    // Restores the rotation of every bone (the animation/preview writes those) and the local
    // position of every bone we do not own. Pelvis + its direct children are scaled/offset by
    // Proportions, so their localPosition is left to the bone pass.
    private static void RestoreSnap()
    {
        if (_model == null || _snap.Count == 0) return;
        try
        {
            if (!_model.gameObject.activeInHierarchy) Activate(null, _model);
            _model.transform.SetPositionAndRotation(_rootPos, _rootRot);
            for (int i = 0; i < _snap.Count; i++)
            {
                var s = _snap[i];
                if (s.T == null) continue;
                if (!s.IsPelvisChild) s.T.localPosition = s.Pos;
                s.T.localRotation = s.Rot;
            }
        }
        catch { }
    }

    private static void Release(IHostContext log) => Release(log, false);

    private static void Release(IHostContext log, bool keepSpawn)
    {
        if (_session != null)
        {
            log.LogWarning($"[propcap] release with capture '{_session.Tag}' unfinished; frame(s) may be short");
            _session = null;
        }
        int restored = 0;
        foreach (var st in _frozen)
        {
            try
            {
                if (st.A != null) { st.A.enabled = st.Enabled; st.A.cullingMode = st.Culling; restored++; }
            }
            catch { }
        }
        _pendingFreeze = null;
        ThawPhysics(log);
        if (_soloHidden.Count > 0)
        {
            int back = 0;
            foreach (var go in _soloHidden)
                try { if (go != null) { go.SetActive(true); back++; } } catch { }
            log.LogInfo($"[propcap:solo] restored {back}/{_soloHidden.Count} model(s)");
            _soloHidden.Clear();
        }
        if (!keepSpawn && _spawned != null)
        {
            try { if (_spawned.gameObject != null) UnityEngine.Object.Destroy(_spawned.gameObject); } catch { }
            _spawned = null;
        }
        _snap.Clear();
        foreach (var sk in _skins)
        {
            try { if (sk.R != null) { sk.R.enabled = sk.Enabled; sk.R.updateWhenOffscreen = sk.Offscreen; } } catch { }
        }
        _skins.Clear();
        _frozen.Clear();
        for (int i = _activated.Count - 1; i >= 0; i--)
        {
            try { if (_activated[i] != null) _activated[i].SetActive(false); } catch { }
        }
        _activated.Clear();
        if (!keepSpawn) _model = null;
        _framed = false;
        try { if (_camGo != null) UnityEngine.Object.Destroy(_camGo); } catch { }
        _camGo = null; _cam = null;
        try { if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); } } catch { }
        _rt = null; _rtW = _rtH = 0;
        if (restored > 0) log.LogInfo($"[propcap:release] animators restored={restored}, capture camera destroyed");
    }

    private static void EnsureCamera(IHostContext log, int w, int h)
    {
        if (_camGo != null && _rtW == w && _rtH == h) return;
        try { if (_camGo != null) UnityEngine.Object.Destroy(_camGo); } catch { }
        try { if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); } } catch { }
        _camGo = new GameObject("AlterumPropCapture");
        _camGo.hideFlags = HideFlags.HideAndDontSave;
        _cam = _camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
        if (_cam == null) throw new InvalidOperationException("camera creation failed");
        _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
        { name = "AlterumPropCaptureRT", antiAliasing = 1, useMipMap = false, autoGenerateMips = false };
        if (!_rt.Create() || !_rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
        _rtW = w; _rtH = h;
        _cam.targetTexture = _rt;     // gives the camera a pixel size ScreenShot.Capture can read
        _cam.enabled = false;
        _cam.orthographic = true;
        _cam.orthographicSize = _halfHeight;
        _cam.aspect = (float)w / h;
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = Mathf.Max(10f, _dist * 4f);
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
        _cam.allowHDR = false;
        _cam.allowMSAA = false;
        _cam.useOcclusionCulling = false;
        _cam.cullingMask = _mask;
        _camGo.transform.position = _center + _forward * _dist;
        _camGo.transform.rotation = Quaternion.LookRotation((_center - _camGo.transform.position).normalized, Vector3.up);
        log.LogInfo($"[propcap:camera] {w}x{h} ortho={_halfHeight:F4} mask=0x{_mask:X8} bg=solid " +
                    $"mpp={(2f * _halfHeight / h):F5} dist={_dist:F2}");
    }

    // Instantiates the model prefab from a PlayerModelData asset into the scene as a private
    // display clone. Unlike the wardrobe carousel this does not move or re-pose itself, so the
    // frozen snapshot stays valid. Destroyed by release=1 / another spawn.
    private static ThirdViewPlayerModel Spawn(IHostContext log, string match)
    {
        try
        {
            PlayerModelData data = null;
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null) continue;
                if ((d.name ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                data = d;
                break;
            }
            if (data == null) { log.LogWarning($"[propcap:spawn] no PlayerModelData matching '{match}'"); return null; }
            var prefab = data.ThirdViewPlayerModel;
            if (prefab == null || prefab.gameObject == null) { log.LogWarning($"[propcap:spawn] '{data.name}' has no third-view prefab"); return null; }

            DestroySpawns();
            _spawned = null;

            var cloneObj = UnityEngine.Object.Instantiate(prefab.gameObject);
            var cloneGo = cloneObj?.TryCast<GameObject>();
            var clone = cloneGo?.GetComponent(Il2CppType.Of<ThirdViewPlayerModel>())?.TryCast<ThirdViewPlayerModel>();
            if (clone == null) { log.LogWarning("[propcap:spawn] clone has no ThirdViewPlayerModel; destroyed"); try { UnityEngine.Object.Destroy(cloneGo); } catch { } return null; }

            // Put it in front of the local player, on the floor, facing the player's forward.
            Vector3 basePos = prefab.gameObject.transform.position;
            float yaw = 0f;
            var player = FindModel(log, null);
            if (player != null)
            {
                basePos = player.transform.position;
                yaw = player.transform.eulerAngles.y;
            }
            cloneGo.transform.position = basePos;
            cloneGo.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            cloneGo.name = "PropCaptureBody";
            cloneGo.SetActive(true);

            _spawned = clone;
            FreezePhysics(log, clone);     // before it starts falling: the clone is a live ragdoll
            _mask = 0;
            log.LogInfo($"[propcap:spawn] '{data.name}' -> '{cloneGo.name}' at {cloneGo.transform.position} " +
                        $"renderers={clone.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true)?.Length} " +
                        $"bones={clone.Bones?._allBones?.Count ?? 0}");
            return clone;
        }
        catch (Exception e) { log.LogWarning($"[propcap:spawn] failed: {e.Message}"); return null; }
    }

    // Removes every display clone this probe ever created, including ones orphaned by a hot
    // reload (statics are lost, the GameObject is not).
    private static void DestroySpawns()
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                if ((m.gameObject.name ?? "") != "PropCaptureBody") continue;
                try { UnityEngine.Object.Destroy(m.gameObject); } catch { }
            }
        }
        catch { }
    }

    private static readonly List<GameObject> _soloHidden = new();

    // The instantiated 3P prefab is a live ragdoll (17 Rigidbody, 16 CharacterJoint,
    // TransformDeltaGetter, SafeRigidBodyController, Animancer + PersonIK). Disabling the Animator
    // does not stop it: the clone collapses to the floor and the leg IK keeps writing the skeleton
    // between the pose restore and the render, so two captures of the "same frozen rig" differ by a
    // whole pose. Every rigid body is made kinematic and the components that drive the skeleton are
    // switched off for the duration of the capture session, then restored.
    private sealed class PhysState { public Rigidbody Rb; public bool Kinematic, Gravity, Collisions; }
    private sealed class CompState { public Behaviour B; public bool Enabled; }
    private static readonly List<PhysState> _phys = new();
    private static readonly List<CompState> _compsOff = new();

    private static readonly string[] PhysStopTypes =
    {
        "Ragdoll", "Animancer", "TransformDeltaGetter", "SafeRigidBodyController",
        "RigidbodyFallImpact", "NetworkTransform", "PersonIK", "IKContainer", "TwoBoneIK",
    };

    private static void FreezePhysics(IHostContext log, ThirdViewPlayerModel model)
    {
        if (model == null || model.gameObject == null) return;
        int rbs = 0, beh = 0;
        try
        {
            var arr = model.GetComponentsInChildren(Il2CppType.Of<Rigidbody>(), true);
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var rb = arr[i]?.TryCast<Rigidbody>();
                if (rb == null) continue;
                bool known = false;
                foreach (var st in _phys) if (ReferenceEquals(st.Rb, rb)) { known = true; break; }
                if (known) continue;
                _phys.Add(new PhysState { Rb = rb, Kinematic = rb.isKinematic, Gravity = rb.useGravity, Collisions = rb.detectCollisions });
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.detectCollisions = false;
                rbs++;
            }
            var bs = model.GetComponentsInChildren(Il2CppType.Of<Behaviour>(), true);
            for (int i = 0; bs != null && i < bs.Length; i++)
            {
                var b = bs[i]?.TryCast<Behaviour>();
                if (b == null || !b.enabled) continue;
                string t = "";
                try { t = b.GetIl2CppType().FullName ?? ""; } catch { try { t = b.GetType().Name; } catch { } }
                bool stop = false;
                foreach (var key in PhysStopTypes) if (t.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { stop = true; break; }
                if (!stop) continue;
                bool known = false;
                foreach (var cs in _compsOff) if (ReferenceEquals(cs.B, b)) { known = true; break; }
                if (known) continue;
                _compsOff.Add(new CompState { B = b, Enabled = b.enabled });
                b.enabled = false;
                beh++;
            }
        }
        catch (Exception e) { log?.LogWarning($"[propcap:phys] freeze failed: {e.Message}"); }
        log?.LogInfo($"[propcap:phys] '{model.gameObject.name}' ragdoll frozen: {rbs} rigidbody(s) kinematic, {beh} driver component(s) off");
    }

    private static void ThawPhysics(IHostContext log)
    {
        int rb = 0, b = 0;
        foreach (var st in _phys)
        {
            try { if (st.Rb != null) { st.Rb.isKinematic = st.Kinematic; st.Rb.useGravity = st.Gravity; st.Rb.detectCollisions = st.Collisions; rb++; } } catch { }
        }
        foreach (var cs in _compsOff)
        {
            try { if (cs.B != null) { cs.B.enabled = cs.Enabled; b++; } } catch { }
        }
        _phys.Clear(); _compsOff.Clear();
        if (rb + b > 0) log?.LogInfo($"[propcap:phys] thawed {rb} rigidbody(s), {b} driver component(s)");
    }

    private static void Solo(IHostContext log, ThirdViewPlayerModel keep)
    {
        int n = 0;
        var names = new List<string>();
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                if (Ptr(m) == Ptr(keep)) continue;
                if (!m.gameObject.activeInHierarchy) continue;
                if (m.gameObject.scene.name == null || m.gameObject.scene.name.Length == 0) continue;
                _soloHidden.Add(m.gameObject);
                m.gameObject.SetActive(false);
                n++;
                if (names.Count < 6) names.Add(m.gameObject.name);
            }
        }
        catch (Exception e) { log.LogWarning($"[propcap:solo] failed: {e.Message}"); }
        log.LogInfo($"[propcap:solo] kept '{(keep.gameObject != null ? keep.gameObject.name : "?")}', hid {n} other model(s) [{string.Join(",", names)}]");
    }

    // ---------------------------------------------------------------- penetration
    // Skins the garment and the body mesh on the CPU (boneWeights x bindposes x bone world matrix)
    // and reports garment vertices that are INSIDE the body surface: nearest body vertex within
    // 2.5 cm whose skinned normal points away from the garment vertex. Independent of camera,
    // colour, lighting and shadows, so it is the objective answer to "is there any poke-through".
    private static void DumpPenetration(IHostContext log, ThirdViewPlayerModel model, string garmentMatch)
    {
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            SkinnedMeshRenderer body = null, garment = null;
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var r = comps[i]?.TryCast<SkinnedMeshRenderer>();
                if (r == null) continue;
                string nm = r.gameObject != null ? r.gameObject.name : "";
                if (nm.IndexOf(garmentMatch, StringComparison.OrdinalIgnoreCase) >= 0) garment = r;
                if (nm.IndexOf("Optimized_Body", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.Equals("body_Optimized", StringComparison.OrdinalIgnoreCase)) body = r;
            }
            if (body == null || garment == null)
            {
                log.LogWarning($"[propcap:pen] body={(body != null ? body.gameObject.name : "null")} garment={(garment != null ? garment.gameObject.name : "null")} (match='{garmentMatch}') - not both found");
                return;
            }
            if (!Skin(body, out var bpos, out var bnorm)) { log.LogWarning("[propcap:pen] body mesh not readable"); return; }
            if (!Skin(garment, out var gpos, out var gnrm)) { log.LogWarning("[propcap:pen] garment mesh not readable"); return; }

            const float cell = 0.02f, maxDist = 0.025f;
            var grid = new Dictionary<long, List<int>>();
            long Key(Vector3 p) => ((long)Mathf.FloorToInt(p.x / cell) << 40) ^
                                   ((long)Mathf.FloorToInt(p.y / cell) << 20) ^
                                   (long)Mathf.FloorToInt(p.z / cell);
            for (int i = 0; i < bpos.Length; i++)
            {
                long k = Key(bpos[i]);
                if (!grid.TryGetValue(k, out var list)) { list = new List<int>(); grid[k] = list; }
                list.Add(i);
            }

            int inside = 0, checkedVerts = 0, near = 0;
            float maxDepth = 0f, sumDepth = 0f;
            int d1 = 0, d3 = 0, d5 = 0, d10 = 0;
            int frontInside = 0, front3 = 0, front5 = 0;
            float frontMax = 0f;
            var frontWhere = new List<string>();
            var camDir = _forward.sqrMagnitude > 1e-6f ? -_forward.normalized : Vector3.forward;
            for (int i = 0; i < gpos.Length; i++)
            {
                Vector3 gp = gpos[i];
                checkedVerts++;
                int best = -1;
                float bestSq = maxDist * maxDist;
                int cx = Mathf.FloorToInt(gp.x / cell), cy = Mathf.FloorToInt(gp.y / cell), cz = Mathf.FloorToInt(gp.z / cell);
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dz = -2; dz <= 2; dz++)
                        {
                            long k = ((long)(cx + dx) << 40) ^ ((long)(cy + dy) << 20) ^ (long)(cz + dz);
                            if (!grid.TryGetValue(k, out var list)) continue;
                            for (int j = 0; j < list.Count; j++)
                            {
                                int bi = list[j];
                                float dsq = (bpos[bi] - gp).sqrMagnitude;
                                if (dsq < bestSq) { bestSq = dsq; best = bi; }
                            }
                        }
                if (best < 0) continue;
                near++;
                float d = Mathf.Sqrt(bestSq);
                Vector3 n = bnorm[best];
                float signed = Vector3.Dot(gp - bpos[best], n);
                if (signed < -0.0005f)   // more than 0.5 mm inside the body surface
                {
                    inside++;
                    float depth = -signed;
                    sumDepth += depth;
                    if (depth > maxDepth) maxDepth = depth;
                    bool facing = Vector3.Dot(gnrm[i], camDir) > 0.25f;
                    if (facing)
                    {
                        frontInside++;
                        if (depth > 0.003f) front3++;
                        if (depth > 0.005f) front5++;
                        if (depth > frontMax) frontMax = depth;
                        if (frontWhere.Count < 4) frontWhere.Add($"({gp.x:F2},{gp.y:F2},{gp.z:F2})/{depth * 1000f:F1}mm");
                    }
                    if (depth > 0.001f) d1++;
                    if (depth > 0.003f) d3++;
                    if (depth > 0.005f) d5++;
                    if (depth > 0.010f) d10++;
                }
            }
            log.LogInfo($"[propcap:pen] garment='{(garment.gameObject != null ? garment.gameObject.name : "?")}' body='{(body.gameObject != null ? body.gameObject.name : "?")}' " +
                        $"verts={gpos.Length} matched={near} inside={inside} " +
                        $"max={maxDepth * 1000f:F2}mm mean={(inside > 0 ? sumDepth / inside * 1000f : 0f):F2}mm " +
                        $"gt1mm={d1} gt3mm={d3} gt5mm={d5} gt10mm={d10} " +
                        $"frontFacing={frontInside} frontGT3={front3} frontGT5={front5} frontMax={frontMax * 1000f:F2}mm " +
                        $"frontAt=[{string.Join(" ", frontWhere)}] cage={Proportions.ChestCageScale:F3} leaf={Proportions.BreastLeafScale:F2}");
        }
        catch (Exception e) { log.LogWarning($"[propcap:pen] failed: {e.Message}"); }
    }

    private static bool Skin(SkinnedMeshRenderer r, out Vector3[] pos, out Vector3[] nrm)
    {
        pos = null; nrm = null;
        var mesh = r.sharedMesh;
        if (mesh == null) return false;
        var verts = mesh.vertices;
        var normals = mesh.normals;
        var bw = mesh.boneWeights;
        var binds = mesh.bindposes;
        var bones = r.bones;
        if (verts == null || bw == null || binds == null || bones == null) return false;
        int n = verts.Length;
        if (bw.Length != n || binds.Length != bones.Length) return false;
        var M = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) { M[i] = Matrix4x4.identity; continue; }
            M[i] = bones[i].localToWorldMatrix * binds[i];
        }
        pos = new Vector3[n];
        nrm = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            var w = bw[v];
            Vector3 p = Vector3.zero, nn = Vector3.zero;
            Accum(w.boneIndex0, w.weight0);
            Accum(w.boneIndex1, w.weight1);
            Accum(w.boneIndex2, w.weight2);
            Accum(w.boneIndex3, w.weight3);
            if (p.sqrMagnitude < 1e-12f) p = verts[v];
            pos[v] = p;
            nrm[v] = nn.sqrMagnitude > 1e-9f ? nn.normalized : Vector3.up;

            void Accum(int bi, float wt)
            {
                if (wt <= 0f || bi < 0 || bi >= M.Length) return;
                var m = M[bi];
                p += (m.MultiplyPoint3x4(verts[v])) * wt;
                nn += (m.MultiplyVector(normals[v])) * wt;
            }
        }
        return true;
    }

    private static void DumpComponents(IHostContext log, ThirdViewPlayerModel model)
    {
        var seen = new Dictionary<string, int>();
        var sb = new StringBuilder();
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string t = "";
                try { t = c.GetIl2CppType().FullName; } catch { try { t = c.GetType().Name; } catch { } }
                if (t.Length == 0) continue;
                seen.TryGetValue(t, out int n);
                seen[t] = n + 1;
            }
            foreach (var kv in seen) sb.Append($" {kv.Key}x{kv.Value}");
        }
        catch (Exception e) { log.LogWarning($"[propcap:comps] failed: {e.Message}"); }
        log.LogInfo($"[propcap:comps] '{model.gameObject.name}' types:{sb}");
    }

    private static void SetPartsEnabled(IHostContext log, ThirdViewPlayerModel model, string match, bool on)
    {
        int n = 0;
        var names = new List<string>();
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var r = comps[i]?.TryCast<Renderer>();
                if (r == null) continue;
                string go = r.gameObject != null ? r.gameObject.name : "";
                string mesh = "";
                try { var smr = r.TryCast<SkinnedMeshRenderer>(); mesh = smr != null && smr.sharedMesh != null ? smr.sharedMesh.name : ""; } catch { }
                if (go.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0 &&
                    mesh.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                r.enabled = on;
                n++;
                if (names.Count < 8) names.Add(go + (mesh.Length > 0 && mesh != go ? "(" + mesh + ")" : ""));
            }
        }
        catch (Exception e) { log.LogWarning($"[propcap:parts] failed: {e.Message}"); }
        log.LogInfo($"[propcap:parts] '{match}' enabled={on} on {n} renderer(s) [{string.Join(",", names)}]");
    }

    private static void SetModelsActive(IHostContext log, string match, bool on)
    {
        int n = 0;
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.gameObject == null) continue;
                if ((m.gameObject.name ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                m.gameObject.SetActive(on);
                n++;
            }
        }
        catch { }
        log.LogInfo($"[propcap:setactive] '{match}' -> {on} on {n} model(s)");
    }

    private static void ListData(IHostContext log)
    {
        var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelData>());
        var sb = new StringBuilder();
        for (int i = 0; i < arr.Length; i++)
        {
            var d = arr[i].TryCast<PlayerModelData>();
            if (d == null) continue;
            sb.Append("'").Append(d.name).Append("' ");
        }
        log.LogInfo($"[propcap:datas] {arr.Length} PlayerModelData: {sb}");

        var set = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelSetuper>());
        var sb2 = new StringBuilder();
        int n = 0;
        for (int i = 0; i < set.Length; i++)
        {
            var s2 = set[i].TryCast<PlayerModelSetuper>();
            if (s2 == null) continue;
            var go = s2.gameObject;
            if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
            n++;
            if (sb2.Length < 900)
                sb2.AppendLine($"  setuper path='{PathOf(go.transform)}' active={go.activeInHierarchy} setup={s2.IsSetup}");
        }
        log.LogInfo($"[propcap:datas] {n} live PlayerModelSetuper(s):\n{sb2}");
    }

    // Switches the local player's third-person body to another PlayerModelData through the game's
    // own public SetupModel path. Test/capture only: called by request, logged, and reverted by
    // sending setmodel= again with the original data name.
    private static void SwitchModel(IHostContext log, string match)
    {
        try
        {
            PlayerModelData target = null;
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelData>());
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<PlayerModelData>();
                if (d == null) continue;
                if ((d.name ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                target = d;
                break;
            }
            if (target == null) { log.LogWarning($"[propcap:setmodel] no PlayerModelData matching '{match}'"); return; }

            PlayerModelSetuper setuper = null;
            var set = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerModelSetuper>());
            for (int i = 0; i < set.Length; i++)
            {
                var s2 = set[i].TryCast<PlayerModelSetuper>();
                if (s2 == null) continue;
                var go = s2.gameObject;
                if (go == null || go.scene.name == null || go.scene.name.Length == 0) continue;
                string path = PathOf(go.transform);
                if (path.IndexOf("LobbyPlayer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                setuper = s2;
                break;
            }
            if (setuper == null) { log.LogWarning("[propcap:setmodel] no local PlayerModelSetuper"); return; }

            Release(log);            // unfreeze the old rig before it is replaced
            setuper.SetupModel(target);
            log.LogInfo($"[propcap:setmodel] '{setuper.gameObject.name}' SetupModel('{target.name}') sent");
        }
        catch (Exception e) { log.LogWarning($"[propcap:setmodel] failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- measurements
    private static void Measure(IHostContext log, ThirdViewPlayerModel model, string tag)
    {
        Transform thighL = Bone(model, "thigh_l"), thighR = Bone(model, "thigh_r");
        Transform shoulderL = Bone(model, "shoulder_l") ?? Bone(model, "clavicle_l");
        Transform shoulderR = Bone(model, "shoulder_r") ?? Bone(model, "clavicle_r");
        Transform armL = Bone(model, "upperarm_l") ?? Bone(model, "upper_arm_l");
        Transform armR = Bone(model, "upperarm_r") ?? Bone(model, "upper_arm_r");
        Transform head = Bone(model, "head");
        Transform chestL = Bone(model, "chest_02_l"), chestR = Bone(model, "chest_02_r");
        Transform pivotL = Bone(model, "chest_01_l"), pivotR = Bone(model, "chest_01_r");

        float hip = thighL != null && thighR != null ? Vector3.Distance(thighL.position, thighR.position) : float.NaN;
        float shoulder = shoulderL != null && shoulderR != null ? Vector3.Distance(shoulderL.position, shoulderR.position) : float.NaN;
        float armJoint = armL != null && armR != null ? Vector3.Distance(armL.position, armR.position) : float.NaN;
        float headY = head != null ? head.position.y : float.NaN;
        float footY = LowestBoneY(model);
        float rL = chestL != null && pivotL != null ? Vector3.Distance(chestL.position, pivotL.position) : float.NaN;
        float rR = chestR != null && pivotR != null ? Vector3.Distance(chestR.position, pivotR.position) : float.NaN;
        float bodyH = headY - footY;

        log.LogInfo(Fmt($"[propcap:measure] tag='{tag}' breast={Proportions.BreastLeafScale:F2} hip={Proportions.HipScale:F2} " +
                        $"cage={Proportions.ChestCageScale:F3} totalChest={Proportions.ChestTotalScale:F2} " +
                        $"hipWidth={hip:F4}m (thigh roots) shoulderWidth={shoulder:F4}m ({NameOf(shoulderL)}..{NameOf(shoulderR)}) " +
                        $"shoulderJointWidth={armJoint:F4}m ({NameOf(armL)}..{NameOf(armR)}) " +
                        $"heightHeadToLowestBone={bodyH:F4}m chestPivotToLeaf={rL:F4}/{rR:F4}m " +
                        $"hipHalf={hip * 0.5f:F4}m"));
    }

    private static float LowestBoneY(ThirdViewPlayerModel model)
    {
        float y = float.MaxValue;
        var all = model.Bones?._allBones;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (n.IndexOf("foot", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("toe", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("ball", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try { if (t.position.y < y) y = t.position.y; } catch { }
        }
        if (y == float.MaxValue)
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                try { if (t.position.y < y) y = t.position.y; } catch { }
            }
        return y;
    }

    private static void DumpNames(IHostContext log, ThirdViewPlayerModel model)
    {
        var all = model.Bones?._allBones;
        var sb = new StringBuilder();
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            sb.Append(t.name).Append('@').Append(Fmt(t.position.ToString())).Append(" ");
        }
        log.LogInfo($"[propcap:names] '{model.gameObject.name}' bones={all?.Count ?? 0}: {sb}");
    }

    // Chest/spine/shoulder subtree with parent, local transform and children. This is what a
    // garment-carrying bone scale has to be derived from (which children to counter-scale so the
    // shoulders, arms and height do not move).
    private static void DumpHierarchy(IHostContext log, ThirdViewPlayerModel model)
    {
        var all = model.Bones?._allBones;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name ?? "";
            if (!Interesting(n)) continue;
            string kids = "";
            try
            {
                var k = new List<string>();
                for (int c = 0; c < t.childCount && c < 24; c++)
                {
                    var ch = t.GetChild(c);
                    if (ch != null) k.Add(ch.name);
                }
                kids = string.Join(",", k);
            }
            catch { }
            string parent = "<none>";
            try { parent = t.parent != null ? t.parent.name : "<none>"; } catch { }
            log.LogInfo(Fmt($"[propcap:hier] '{model.gameObject.name}' bone='{n}' parent='{parent}' " +
                            $"localPos={t.localPosition} localScale={t.localScale} world={t.position} children=[{kids}]"));
        }
    }

    private static bool Interesting(string n)
    {
        foreach (var key in new[] { "chest", "spine", "pelvis", "clavicle", "shoulder", "upperarm", "upper_arm", "neck", "head" })
            if (n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // Per skinned mesh: the bone list, the per-bone weight mass over all vertices, and the same
    // mass restricted to the chest band (chest pivot -> neck in the renderer's own space). The band
    // figures are the ones that say whether the tank top over the bust follows chest_01 or a spine.
    private static void DumpWeights(IHostContext log, ThirdViewPlayerModel model, string match)
    {
        var chestL = Bone(model, "chest_01_l") ?? Bone(model, "Chest_01_l");
        var neck = Bone(model, "neck") ?? Bone(model, "neck_01") ?? Bone(model, "neck_02");
        var comps = model.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        int dumped = 0;
        for (int i = 0; comps != null && i < comps.Length; i++)
        {
            var r = comps[i]?.TryCast<SkinnedMeshRenderer>();
            if (r == null) continue;
            string name = r.gameObject != null ? r.gameObject.name : "?";
            if (match.Length > 0 && match != "*" && name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (name.IndexOf("Optimized", StringComparison.OrdinalIgnoreCase) >= 0) { /* keep: body mesh */ }
            dumped++;
            var boneArr = r.bones;
            var names = new List<string>();
            for (int b = 0; boneArr != null && b < boneArr.Length; b++) names.Add(boneArr[b] != null ? boneArr[b].name : "<null>");
            log.LogInfo($"[propcap:weights] mesh='{name}' bones={names.Count}: {string.Join(",", names)}");

            var mesh = r.sharedMesh;
            if (mesh == null) { log.LogWarning($"[propcap:weights] mesh='{name}' sharedMesh=null"); continue; }
            Il2CppStructArray<BoneWeight> src = null;
            BoneWeight[] bw = null;
            string err = "";
            try { src = mesh.boneWeights; } catch (Exception e) { err += "boneWeights:" + e.GetType().Name + " "; }
            if (src == null || src.Length == 0)
                try
                {
                    var lst = new Il2CppSystem.Collections.Generic.List<BoneWeight>();
                    mesh.GetBoneWeights(lst);
                    src = new Il2CppStructArray<BoneWeight>(lst.Count);
                    for (int k = 0; k < lst.Count; k++) src[k] = lst[k];
                }
                catch (Exception e) { err += "GetBoneWeights:" + e.GetType().Name; }
            if (src != null && src.Length > 0)
            {
                bw = new BoneWeight[src.Length];
                for (int k = 0; k < src.Length; k++) bw[k] = src[k];
            }
            if (bw == null || bw.Length == 0)
            {
                log.LogWarning($"[propcap:weights] mesh='{name}' vertex weights not readable ({err.Trim()})");
                continue;
            }
            int nv = bw.Length;
            var mass = new float[names.Count];
            var bandMass = new float[names.Count];
            var cnt = new int[names.Count];
            var bandCnt = new int[names.Count];
            float loY = float.MinValue, hiY = float.MaxValue;
            bool band = chestL != null && neck != null;
            if (band)
            {
                try
                {
                    loY = r.transform.InverseTransformPoint(chestL.position).y;
                    hiY = r.transform.InverseTransformPoint(neck.position).y;
                    if (hiY < loY) { float tmp = loY; loY = hiY; hiY = tmp; }
                }
                catch { band = false; }
            }
            Vector3[] verts = null;
            try { verts = mesh.vertices; } catch { }
            for (int v = 0; v < nv; v++)
            {
                var w = bw[v];
                bool inBand = band && verts != null && verts.Length == nv && verts[v].y >= loY && verts[v].y <= hiY;
                Add(mass, cnt, w.boneIndex0, w.weight0, inBand, bandMass, bandCnt);
                Add(mass, cnt, w.boneIndex1, w.weight1, inBand, bandMass, bandCnt);
                Add(mass, cnt, w.boneIndex2, w.weight2, inBand, bandMass, bandCnt);
                Add(mass, cnt, w.boneIndex3, w.weight3, inBand, bandMass, bandCnt);
            }
            var order = new List<int>();
            for (int b = 0; b < names.Count; b++) if (cnt[b] > 0) order.Add(b);
            order.Sort((a, b) => mass[b].CompareTo(mass[a]));
            var top = new StringBuilder();
            int shown = 0;
            foreach (int b in order)
            {
                if (shown++ >= 8) break;
                top.Append($" {names[b]}:{mass[b]:F0}({cnt[b]}v");
                if (band) top.Append($" band {bandMass[b]:F0}/{bandCnt[b]}v");
                top.Append(")");
            }
            string bandTxt = band ? $" bandY=[{loY:F3},{hiY:F3}] verts={nv}" : $" band=none verts={nv}";
            log.LogInfo($"[propcap:weights] mesh='{name}'{bandTxt} topByMass:{top}");
        }
        if (dumped == 0) log.LogWarning($"[propcap:weights] no SkinnedMeshRenderer matched '{match}'");
    }

    private static void Add(float[] mass, int[] cnt, int idx, float w, bool inBand, float[] bandMass, int[] bandCnt)
    {
        if (w <= 0f || idx < 0 || idx >= mass.Length) return;
        mass[idx] += w; cnt[idx]++;
        if (inBand) { bandMass[idx] += w; bandCnt[idx]++; }
    }

    private static void ListModels(IHostContext log)
    {
        var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
        var seen = new HashSet<int>();
        var sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var m = arr[i].TryCast<ThirdViewPlayerModel>();
            if (m == null || !seen.Add(Ptr(m))) continue;
            var go = m.gameObject;
            if (go == null) continue;
            bool scene = go.scene.name != null && go.scene.name.Length > 0;
            if (!scene) continue;
            int rends = 0, layers = 0;
            var comps = m.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int j = 0; comps != null && j < comps.Length; j++)
            {
                var r = comps[j]?.TryCast<Renderer>();
                if (r == null) continue;
                rends++; layers |= 1 << r.gameObject.layer;
            }
            sb.AppendLine($"  '{go.name}' active={go.activeInHierarchy} path='{PathOf(go.transform)}' " +
                          $"renderers={rends} layers=0x{layers:X} bones={m.Bones?._allBones?.Count ?? 0}");
            n++;
        }
        log.LogInfo($"[propcap:list] {n} live model(s):\n{sb}");
    }

    private static void Landmark(StringBuilder sb, Camera cam, string name)
    {
        var t = _model != null ? Bone(_model, name) : null;
        if (t == null || cam == null) return;
        try
        {
            // Exact ortho projection in RT pixels (row counted from the TOP, like the PNG).
            int w = cam.pixelWidth, h = cam.pixelHeight;
            float aspect = (float)w / h;
            var d = t.position - _center;
            float x = w * 0.5f + Vector3.Dot(d, cam.transform.right) / (_halfHeight * aspect) * (w * 0.5f);
            float yUp = h * 0.5f + Vector3.Dot(d, cam.transform.up) / _halfHeight * (h * 0.5f);
            float row = h - yUp;
            sb.AppendLine(Fmt($"bone={name} px={x:F2},{row:F2} world={t.position}"));
        }
        catch { }
    }

    // ---------------------------------------------------------------- helpers
    private static ThirdViewPlayerModel FindModel(IHostContext log, string match)
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < arr.Length; i++)
            {
                var m = arr[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null) continue;
                if (go.scene.name == null || go.scene.name.Length == 0) continue;
                // A named request may pick an inactive wardrobe preview rig (activate=1); the
                // default is the active local player.
                if (match == null && (PathOf(go.transform).IndexOf("Wardrobe", StringComparison.OrdinalIgnoreCase) >= 0 || !go.activeInHierarchy)) continue;
                if (match != null && go.name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return m;
            }
        }
        catch (Exception e) { log.LogWarning($"[propcap] model scan failed: {e.Message}"); }
        return null;
    }

    private static Transform Bone(ThirdViewPlayerModel model, string name)
    {
        var all = model.Bones?._allBones;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var t = all[i];
            if (t == null) continue;
            if ((t.name ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return null;
    }

    private static string NameOf(Transform t) => t != null ? t.name : "null";

    private static int Ptr(Il2CppObjectBase o) => o == null ? 0 : (int)o.Pointer.ToInt64();

    private static int Depth(Transform t)
    {
        int d = 0;
        for (var p = t.parent; p != null && d < 64; p = p.parent) d++;
        return d;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int guard = 0; p != null && guard < 14; guard++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "shot";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
            sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' ? s[i] : '_');
        return sb.Length == 0 ? "shot" : sb.ToString();
    }

    private static string Fmt(string s) => s;

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int I(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
