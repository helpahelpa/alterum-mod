using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using MagicaCloth2;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Frame-by-frame witness for the shipped breast cloth (MagicaCloth2 "Breast" BoneCloth).
//
// Unattended, locked-desktop safe: everything is driven by a request file in ScreensDir and the
// evidence is (a) a PNG sequence of the rig's torso rendered into a private RenderTexture and
// (b) a CSV of the four chest bones' local rotation/position per recorded frame.
//
//   jiggle.request (line-oriented, invariant numbers, '#' comments, unknown keys logged):
//     tag=diana_jiggle        output folder + CSV name
//     model=Diana             rig name substring; must be an active ThirdViewPlayerModel
//     seconds=6 fps=15        wall-clock length / sample rate (same cadence as ClipRecorder)
//     width=960 height=540    PNG size
//     yaw=0                   camera angle around the torso, degrees clockwise from the rig's
//                             own forward axis (0 = straight in front, 90 = from its left)
//     dist=1.15               camera distance from the chest centre
//     fov=40
//     lift=0.02               extra eye height, fraction of dist
//     poke=45                 frame index at which a pose disturbance starts (0 = never)
//     hold=3                  how many recorded frames the poke is held for
//     degrees=25              poke magnitude on chest_01_l's local Z
//     force=15                frame index of the first AddForce impulse (0 = never)
//     mult=6                  AddForce influence
//
// Why this shape: the cloth drives the *leaves* (chest_02_l/r); chest_01_l/r are its roots, so
// they are read from the transform and never written back. A held poke on a root therefore
// moves the whole simulated chain rigidly (visible, but not jiggle), while real simulation
// shows up as the leaf's LOCAL rotation/position changing after the root was moved or released.
// The probe records both, so "0.00" and "cloth writes every frame" can be told apart.
internal static class JiggleProbe
{
    private const string RequestName = "jiggle.request";
    private const float PollSeconds = 0.5f;
    private const int HardFrameCap = 300;

    private sealed class Row
    {
        public Transform T;
        public string Name;
        public Quaternion Start;
        public Vector3 StartPos;
        public float UpAngle, LateAngle, UpPos, LatePos;
        public float MaxAngle, MaxPos;
        public float BaseAngleMax;             // worst deviation before any disturbance
    }

    private sealed class Sess
    {
        public string Tag, Dir, ModelName;
        public float Seconds, Fps, Dist, Fov, Lift, Yaw;
        public int Width, Height, Total, Written;
        public GameObject CamGo; public Camera Cam;
        public RenderTexture Rt; public Texture2D Tex;
        public ThirdViewPlayerModel Model;
        public MagicaCloth Cloth;
        public readonly List<Row> Rows = new();
        public readonly List<SkinnedMeshRenderer> Skinned = new();
        public readonly List<bool> Enabled = new(), Offscreen = new();
        public Animator Anim; public AnimatorCullingMode AnimCulling;
        public double StartedAt, NextDue;
        public int PokeFrame, Hold, ForceFrame;
        public float PokeDeg, Multiplier;
        public bool Poked; public int PokeEnd; public bool PokeDone;
        public Quaternion PokeRest;
        public Vector3 ChestCenter;
        public bool LastRun;
        public int PokeApplied, ForceApplied;
        public readonly StringBuilder Csv = new();
        public bool RowPending;
        public bool ForceActive; public int ForceEnd;
        public bool Activate; public string CullingMode = ""; public float Warmup;
        public int LeafFrame; public float LeafDeg = 25f; public bool Rebuild; public int SkipWrite = -1;
        public string UpdateMode = "";
        public float Gravity = float.NaN;
        public float Place;
        public bool Freeze; public bool AnimWasEnabled; public bool AnimEnabledCaptured; public bool AnimDisabled; public bool Rebind;
        public bool Census; public bool CamLive; public double NextInternals;
        public bool ClothOff; public bool ClothWasEnabled;
        public float GravityProp = float.NaN; public float GravityPropRest; public bool GravityPropSaved;
        public float GravityField = float.NaN; public float GravityFieldRest; public bool GravityFieldSaved;
        public int Pump; public long Pumped; public bool KickAll; public double NextKick;
        public bool LoopFix; public bool SpawnCloth; public ClothControl.Rig Control; public bool Hooks;
        public int Cycle; public bool _cycled; public int UpdLocation = -1; public int SimFreq = -1;
        public bool UpdLocSaved; public int UpdLocRest = -1;
        public bool SimFreqSaved; public int SimFreqRest = -1;
        public float Sway; public float SwayHz = 1f; public bool Swaying; public Vector3 SwayBase;
        public bool Follow = true; public Vector3 AimFixed;
        public int CamHold; public bool CamFrozen; public Vector3 CamPos; public Quaternion CamRot;
        public float AnimSpeed = float.NaN; public float AnimSpeedRest; public bool AnimSpeedSaved;
        public float UprightMin; public Transform Pelvis; public float LastDy;
        public bool Placed; public Vector3 PlaceRest; public Quaternion PlaceRestRot;
        public float GravityRest; public bool GravitySaved;
        public ClothUpdateMode UpdateRest; public bool UpdateSaved;
        public bool LeafPoked; public Quaternion LeafRest;
        public double WarmupUntil;
        public readonly List<GameObject> Reactivated = new();
        public bool CullingSaved; public int CullingRest;
        public double LastWarmLog;
        public Vector3 ViewDir; public Bounds Bounds;
    }

    private static Sess _s;
    private static float _nextPoll;

    internal static bool IsActive => _s != null;

    // ---- hooks ---------------------------------------------------------------------------

    // Called from Update: request polling, disturbance, frame scheduling/rendering.
    internal static void Tick(IHostContext log, float dt)
    {
        if (Time.unscaledTime >= _nextPoll)
        {
            _nextPoll = Time.unscaledTime + PollSeconds;
            try { Poll(log); } catch (Exception e) { log.LogWarning($"[jiggle] request failed: {e.Message}"); }
        }
        var s = _s;
        if (s == null) return;
        try { Step(log, s); }
        catch (Exception e) { log.LogError($"[jiggle] step failed: {e}"); Stop(log, s, $"exception: {e.Message}"); }
    }

    // Called from LateUpdate: second sample point for the same recorded frame (after whatever the
    // cloth does between Update and LateUpdate), and the CSV row.
    internal static void LateTick(IHostContext log)
    {
        var s = _s;
        if (s == null || !s.RowPending) return;
        try { FinishRow(log, s); }
        catch (Exception e) { log.LogWarning($"[jiggle] late sample failed: {e.Message}"); s.RowPending = false; }
    }

    internal static void Abort(IHostContext log)
    {
        var s = _s;
        if (s == null) return;
        _s = null;
        try { log?.LogWarning($"[jiggle] abort tag='{s.Tag}' after {s.Written} frame(s) (reload/shutdown)"); } catch { }
        Cleanup(log, s);
    }

    // ---- request --------------------------------------------------------------------------

    private static void Poll(IHostContext log)
    {
        if (_s != null) return;
        string path = Path.Combine(log.ScreensDir, RequestName);
        if (!File.Exists(path)) return;
        string text = "";
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[jiggle] request read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }

        var s = new Sess
        {
            Tag = "jiggle", ModelName = "", Seconds = 6f, Fps = 15f, Dist = 1.15f, Fov = 40f,
            Lift = 0.02f, Yaw = 0f, Width = 960, Height = 540,
            PokeFrame = 0, Hold = 3, ForceFrame = 0, PokeDeg = 25f, Multiplier = 6f,
        };
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "tag": s.Tag = Sanitize(v); break;
                case "model": s.ModelName = v; break;
                case "seconds": s.Seconds = F(v, s.Seconds); break;
                case "fps": s.Fps = F(v, s.Fps); break;
                case "dist": s.Dist = F(v, s.Dist); break;
                case "fov": s.Fov = F(v, s.Fov); break;
                case "lift": s.Lift = F(v, s.Lift); break;
                case "yaw": s.Yaw = F(v, s.Yaw); break;
                case "width": s.Width = I(v, s.Width); break;
                case "height": s.Height = I(v, s.Height); break;
                case "poke": s.PokeFrame = I(v, s.PokeFrame); break;
                case "hold": s.Hold = I(v, s.Hold); break;
                case "degrees": s.PokeDeg = F(v, s.PokeDeg); break;
                case "force": s.ForceFrame = I(v, s.ForceFrame); break;
                case "mult": s.Multiplier = F(v, s.Multiplier); break;
                case "activate": s.Activate = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "culling": s.CullingMode = v.Trim().ToLowerInvariant(); break;
                case "warm": s.Warmup = F(v, s.Warmup); break;
                case "leaf": s.LeafFrame = I(v, s.LeafFrame); break;
                case "leafdeg": s.LeafDeg = F(v, s.LeafDeg); break;
                case "rebuild": s.Rebuild = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "skip": s.SkipWrite = I(v, 0); break;
                case "update": s.UpdateMode = v.Trim().ToLowerInvariant(); break;
                case "gravity": s.Gravity = F(v, s.Gravity); break;
                case "place": s.Place = F(v, s.Place); break;
                case "freeze": s.Freeze = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "census": s.Census = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "gprop": s.GravityProp = F(v, s.GravityProp); break;
                case "gfield": s.GravityField = F(v, s.GravityField); break;
                case "pump": s.Pump = I(v, s.Pump); break;
                case "kickall": s.KickAll = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "loopfix": s.LoopFix = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "cycle": s.Cycle = I(v, s.Cycle); break;
                case "sway": s.Sway = F(v, 0f); break;
                case "camhold": s.CamHold = I(v, 0); break;
                case "animspeed": s.AnimSpeed = F(v, s.AnimSpeed); break;
                case "follow": s.Follow = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                case "swayhz": s.SwayHz = F(v, 1f); break;
                case "updlocation": s.UpdLocation = I(v, -1); break;
                case "simfreq": s.SimFreq = I(v, -1); break;
                case "forcesteps": ClothHooks.ForceSteps = I(v, 0); s.Hooks = true; break;
                case "hooks": s.Hooks = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "spawncloth": s.SpawnCloth = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "cloff": s.ClothOff = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "cam": s.CamLive = v.Trim().Equals("live", StringComparison.OrdinalIgnoreCase) || v.Trim() == "1"; break;
                case "rebind": s.Rebind = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "upright": s.UprightMin = F(v, s.UprightMin); break;
                default: log.LogWarning($"[jiggle] request key '{k}' unknown; ignored"); break;
            }
        }
        s.Seconds = Clamp(s.Seconds, 0.5f, 60f);
        s.Fps = Clamp(s.Fps, 1f, 60f);
        s.Total = Mathf.Clamp(Mathf.RoundToInt(s.Seconds * s.Fps), 1, HardFrameCap);
        if ((s.Width & 1) != 0) s.Width--;
        if ((s.Height & 1) != 0) s.Height--;
        s.Dir = Path.Combine(log.ScreensDir, s.Tag);

        if (!Arm(log, s)) { Cleanup(log, s); return; }

        try
        {
            Directory.CreateDirectory(s.Dir);
            int stale = 0;
            foreach (var f in Directory.GetFiles(s.Dir, "frame_*.png")) { try { File.Delete(f); stale++; } catch { } }
            if (stale > 0) log.LogInfo($"[jiggle] removed {stale} stale frame(s)");
        }
        catch (Exception e) { log.LogWarning($"[jiggle] frame dir: {e.Message}"); }

        s.Csv.AppendLine("frame,t,run,event,bone,up_deg,late_deg,up_pos,late_pos,geom");
        // A freshly activated display rig needs a moment before MagicaCloth has built and run.
        if (s.Reactivated.Count > 0 && s.Warmup <= 0f) s.Warmup = 2f;
        s.WarmupUntil = Time.realtimeSinceStartupAsDouble + s.Warmup;
        s.StartedAt = 0d;
        s.NextDue = 0d;
        _s = s;
        log.LogInfo($"[jiggle] start tag='{s.Tag}' model='{s.Model.gameObject.name}' {s.Width}x{s.Height} " +
                    $"fps={s.Fps:0.##} seconds={s.Seconds:0.##} frames={s.Total} dir='{s.Dir}' warmup={s.Warmup:0.##}s " +
                    $"chest={s.ChestCenter} yaw={s.Yaw:0.##} dist={s.Dist:0.##} fov={s.Fov:0.##} " +
                    $"poke={s.PokeFrame}/{s.Hold}f@{s.PokeDeg:0.##}deg force={s.ForceFrame}x{s.Multiplier:0.##}");
    }

    private static bool Arm(IHostContext log, Sess s)
    {
        ThirdViewPlayerModel model = null;
        ThirdViewPlayerModel inactive = null;
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
                if (s.ModelName.Length > 0 && (go.name ?? "").IndexOf(s.ModelName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var all = m.Bones?._allBones;
                if (all == null || all.Count == 0) continue;
                if (go.activeInHierarchy) { model = m; if (s.ModelName.Length > 0) break; }
                else if (inactive == null) inactive = m;
            }
        }
        catch (Exception e) { log.LogWarning($"[jiggle] model scan failed: {e.Message}"); }

        if (model == null && inactive != null)
        {
            log.LogInfo($"[jiggle] candidate '{inactive.gameObject.name}' is inactive: {Chain(inactive.gameObject)}");
            if (!s.Activate) { log.LogWarning($"[jiggle] '{inactive.gameObject.name}' is inactive (request activate=1 to show it)"); return false; }
            try
            {
                for (var t = inactive.transform; t != null; t = t.parent)
                {
                    var go = t.gameObject;
                    if (go == null || go.activeSelf) continue;
                    if (t.parent == null && t != inactive.transform) break;
                    go.SetActive(true);
                    s.Reactivated.Add(go);
                    if (s.Reactivated.Count > 8) break;
                }
                inactive.gameObject.SetActive(true);
                inactive.enabled = true;
                log.LogInfo($"[jiggle] activated '{inactive.gameObject.name}' and {s.Reactivated.Count} ancestor(s); " +
                            $"now activeInHierarchy={inactive.gameObject.activeInHierarchy} cloth={inactive.gameObject.GetComponentsInChildren(Il2CppType.Of<MagicaCloth>(), true)?.Length}");
                model = inactive;
            }
            catch (Exception e) { log.LogWarning($"[jiggle] activation failed: {e.Message}"); return false; }
        }
        if (model == null) { log.LogWarning($"[jiggle] no rig matching model='{s.ModelName}'"); return false; }
        s.Model = model;

        try
        {
            var all = model.Bones._allBones;
            var wanted = new[] { "chest_01_l", "chest_02_l", "chest_01_r", "chest_02_r" };
            foreach (var want in wanted)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t == null || !string.Equals(t.name, want, StringComparison.OrdinalIgnoreCase)) continue;
                    var row = new Row { T = t, Name = want, Start = t.localRotation, StartPos = t.localPosition };
                    s.Rows.Add(row);
                    break;
                }
            }
            if (s.Rows.Count == 0) { log.LogWarning($"[jiggle] '{model.gameObject.name}' has no chest bones"); return false; }

            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t != null && string.Equals(t.name, "pelvis", StringComparison.OrdinalIgnoreCase)) { s.Pelvis = t; break; }
            }

            var comps = model.GetComponentsInChildren(Il2CppType.Of<MagicaCloth>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var mc = comps[i]?.TryCast<MagicaCloth>();
                if (mc == null) continue;
                var rb = mc.serializeData?.rootBones;
                bool chest = false;
                for (int j = 0; rb != null && j < rb.Count; j++)
                    if (rb[j] != null && (rb[j].name ?? "").IndexOf("chest_01", StringComparison.OrdinalIgnoreCase) >= 0) chest = true;
                if (!chest) continue;
                s.Cloth = mc;
                break;
            }
            if (s.Cloth == null) log.LogWarning($"[jiggle] no breast cloth found on '{model.gameObject.name}'; measuring the bones only");
            else
            {
                try
                {
                    var cs = s.Cloth.serializeData?.cullingSettings;
                    if (cs != null)
                    {
                        s.CullingRest = (int)cs.cameraCullingMode; s.CullingSaved = true;
                        log.LogInfo($"[jiggle] cloth culling cameraCullingMode={(int)cs.cameraCullingMode} method={(int)cs.cameraCullingMethod}");
                        if (s.CullingMode.Length > 0)
                        {
                            var mode = s.CullingMode == "off" ? CullingSettings.CameraCullingMode.Off
                                     : s.CullingMode == "keep" ? CullingSettings.CameraCullingMode.Keep
                                     : s.CullingMode == "reset" ? CullingSettings.CameraCullingMode.Reset
                                     : CsMode(s.CullingMode);
                            cs.cameraCullingMode = mode;
                            s.Cloth.SetParameterChange();
                            log.LogInfo($"[jiggle] cloth culling -> {(int)mode} (requested '{s.CullingMode}'), SetParameterChange sent");
                        }
                    }
                    else log.LogInfo("[jiggle] cloth has no cullingSettings object");
                }
                catch (Exception e) { log.LogWarning($"[jiggle] culling control failed: {e.Message}"); }

                if (s.UpdateMode.Length > 0)
                {
                    try
                    {
                        var sd = s.Cloth.serializeData;
                        if (sd != null)
                        {
                            s.UpdateRest = sd.updateMode; s.UpdateSaved = true;
                            var mode = s.UpdateMode == "normal" ? ClothUpdateMode.Normal
                                     : s.UpdateMode == "unscaled" ? ClothUpdateMode.Unscaled
                                     : ClothUpdateMode.AnimatorLinkage;
                            sd.updateMode = mode;
                            s.Cloth.SetParameterChange();
                            log.LogInfo($"[jiggle] cloth updateMode {s.UpdateRest} -> {mode} (requested '{s.UpdateMode}'), SetParameterChange sent");
                        }
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] updateMode control failed: {e.Message}"); }
                }

                if (!float.IsNaN(s.Gravity))
                {
                    try
                    {
                        var sd = s.Cloth.serializeData;
                        if (sd != null)
                        {
                            s.GravityRest = sd.gravity; s.GravitySaved = true;
                            sd.gravity = s.Gravity;
                            s.Cloth.SetParameterChange();
                            log.LogInfo($"[jiggle] cloth gravity {s.GravityRest:F2} -> {sd.gravity:F2}falloff={sd.gravityFalloff:F2}, SetParameterChange sent");
                        }
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] gravity control failed: {e.Message}"); }
                }

                if (!float.IsNaN(s.GravityProp))
                {
                    try
                    {
                        s.GravityPropRest = s.Cloth.gravityProperty; s.GravityPropSaved = true;
                        s.Cloth.gravityProperty = s.GravityProp;
                        log.LogInfo($"[jiggle] cloth gravityProperty {s.GravityPropRest:F2} -> {s.Cloth.gravityProperty:F2} (runtime property, no SetParameterChange)");
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] gravityProperty failed: {e.Message}"); }
                }
                if (!float.IsNaN(s.GravityField))
                {
                    try
                    {
                        s.GravityFieldRest = s.Cloth._gravityProperty; s.GravityFieldSaved = true;
                        s.Cloth._gravityProperty = s.GravityField;
                        log.LogInfo($"[jiggle] cloth _gravityProperty {s.GravityFieldRest:F2} -> {s.Cloth._gravityProperty:F2} (runtime field)");
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] _gravityProperty failed: {e.Message}"); }
                }
                if (s.Rebuild)
                {
                    try
                    {
                        s.Cloth.Initialize();
                        bool ok = s.Cloth.BuildAndRun();
                        log.LogInfo($"[jiggle] rebuild Initialize/BuildAndRun -> {ok}");
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] rebuild failed: {e.Message}"); }
                }
                if (s.SkipWrite >= 0)
                {
                    try { s.Cloth.SetSkipWriting(s.SkipWrite == 1); log.LogInfo($"[jiggle] SetSkipWriting({s.SkipWrite == 1})"); }
                    catch (Exception e) { log.LogWarning($"[jiggle] SetSkipWriting failed: {e.Message}"); }
                }
                if (s.ClothOff)
                {
                    try
                    {
                        s.ClothWasEnabled = s.Cloth.enabled;
                        s.Cloth.enabled = false;
                        log.LogInfo($"[jiggle] cloth component enabled={s.ClothWasEnabled} -> false (writer isolation)");
                    }
                    catch (Exception e) { log.LogWarning($"[jiggle] cloff failed: {e.Message}"); }
                }
                ClothDiag(log, s, "armed");
            }

            // The provider never draws this body itself: make sure bones and skinning keep
            // updating for the duration of the clip, then restore every value.
            var anims = model.GetComponentsInChildren(Il2CppType.Of<Animator>(), true);
            for (int i = 0; anims != null && i < anims.Length; i++)
            {
                var a = anims[i]?.TryCast<Animator>();
                if (a == null) continue;
                s.Anim = a; s.AnimCulling = a.cullingMode; a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (!float.IsNaN(s.AnimSpeed)) { s.AnimSpeedRest = a.speed; s.AnimSpeedSaved = true; a.speed = s.AnimSpeed; }
                break;
            }
            if (s.Freeze && s.Anim != null)
            {
                // Capture the pre-probe value at arm time in every freeze mode. The previous code
                // only captured it when the freeze happened here, so freeze=1 with an upright
                // warmup (freeze applied later in Step) could be cleaned up with the default
                // false and disable an animator the probe had never disabled.
                s.AnimWasEnabled = s.Anim.enabled;
                s.AnimEnabledCaptured = true;
                if (s.UprightMin <= 0f)
                {
                    s.Anim.enabled = false;
                    s.AnimDisabled = true;
                    log.LogInfo($"[jiggle] animator '{PathOf(s.Anim.transform)}' enabled={s.AnimWasEnabled} -> false (frozen pose)");
                    if (s.Rebind)
                    {
                        try { s.Anim.Rebind(); log.LogInfo("[jiggle] Animator.Rebind() -> bind pose"); }
                        catch (Exception e) { log.LogWarning($"[jiggle] Rebind failed: {e.Message}"); }
                    }
                }
                else
                {
                    log.LogInfo($"[jiggle] animator '{PathOf(s.Anim.transform)}' enabled={s.AnimWasEnabled} captured; " +
                                $"freeze deferred until the upright warmup reaches dy>={s.UprightMin:0.##}m");
                }
            }
            var rends = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            int layers = 0;
            for (int i = 0; rends != null && i < rends.Length; i++)
            {
                var r = rends[i]?.TryCast<Renderer>();
                if (r == null) continue;
                layers |= 1 << r.gameObject.layer;
                var sk = r.TryCast<SkinnedMeshRenderer>();
                if (sk == null) continue;
                s.Skinned.Add(sk); s.Enabled.Add(sk.enabled); s.Offscreen.Add(sk.updateWhenOffscreen);
                sk.updateWhenOffscreen = true;
            }
            if (s.Skinned.Count == 0) { log.LogWarning($"[jiggle] '{model.gameObject.name}' has no skinned renderers"); return false; }

            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var row in s.Rows) { if (row.T != null) { sum += row.T.position; n++; } }
            s.ChestCenter = n > 0 ? sum / n : model.transform.position + Vector3.up * 1.3f;

            var fwd = model.transform.forward;
            var planar = new Vector3(fwd.x, 0f, fwd.z);
            if (planar.sqrMagnitude < 1e-6f) planar = Vector3.forward;
            planar.Normalize();

            // place=metres: put the rig in front of the real game camera. A body the game camera
            // actually draws is the one case where no visibility/culling path can skip it, which
            // is exactly the state the local player's body is never in during normal play.
            if (s.Place > 0f)
            {
                var gameCam = FindGameCamera(log);
                if (gameCam != null)
                {
                    var cp = gameCam.transform.position;
                    var cf = gameCam.transform.forward;
                    var flat = new Vector3(cf.x, 0f, cf.z);
                    if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
                    flat.Normalize();
                    var target = cp + flat * Mathf.Max(0.5f, s.Place);
                    var t0 = model.transform.position;
                    s.PlaceRest = t0; s.PlaceRestRot = model.transform.rotation;
                    model.transform.position = new Vector3(target.x, t0.y, target.z);
                    model.transform.rotation = Quaternion.LookRotation(-flat, Vector3.up);
                    s.Placed = true;
                    log.LogInfo($"[jiggle] placed '{model.gameObject.name}' at {model.transform.position} facing {model.transform.forward} " +
                                $"(game camera '{PathOf(gameCam.transform)}' at {cp} fwd={cf}); was {t0}");
                }
                else log.LogWarning("[jiggle] place requested but no game camera found");
            }

            float yawRad = s.Yaw * Mathf.Deg2Rad;
            s.ViewDir = Quaternion.AngleAxis(yawRad, Vector3.up) * planar;
            s.Bounds = RenderBounds(s);
            Vector3 chestSum = Vector3.zero;
            int cn = 0;
            foreach (var row in s.Rows) { if (row.T != null) { chestSum += row.T.position; cn++; } }
            s.ChestCenter = cn > 0 ? chestSum / cn : s.Bounds.center;
            // Aim at the chest bones, not at Renderer.bounds: in this interop build Bounds reads
            // intermittently come back as garbage (values ~1e8), which threw the camera far away
            // and produced single-colour frames.
            var aim = s.ChestCenter + Vector3.up * s.Lift;
            var eye = aim + s.ViewDir * s.Dist;

            s.CamGo = new GameObject("AlterumJiggleCam");
            s.CamGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = s.CamGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) throw new InvalidOperationException("camera creation failed");
            s.Cam = cam;
            cam.enabled = s.CamLive;   // live = a normal enabled camera writing into the RT
            cam.orthographic = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.08f, 0.09f, 1f);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 200f;
            cam.fieldOfView = s.Fov;
            cam.cullingMask = layers;
            cam.aspect = (float)s.Width / s.Height;
            cam.depth = 0;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            s.CamGo.transform.position = eye;
            s.CamGo.transform.rotation = Quaternion.LookRotation((aim - eye).normalized, Vector3.up);

            s.Rt = new RenderTexture(s.Width, s.Height, 24, RenderTextureFormat.ARGB32)
            { name = "AlterumJiggleRT", antiAliasing = 1, useMipMap = false, autoGenerateMips = false };
            if (!s.Rt.Create() || !s.Rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
            s.Tex = new Texture2D(s.Width, s.Height, TextureFormat.RGB24, false);
            if (s.CamLive) { cam.targetTexture = s.Rt; cam.Render(); }   // one warm render so frame 1 is not blank

            bool run = false;
            try { var p = s.Cloth?.process; run = p != null && p.IsRunning(); } catch { }
            s.LastRun = run;
            if (s.UpdLocation >= 0)
            {
                try
                {
                    int priorLoc = (int)MagicaCloth2.MagicaManager.GetUpdateLocation();
                    s.UpdLocRest = priorLoc;
                    s.UpdLocSaved = true;
                    MagicaCloth2.MagicaManager.SetUpdateLocation((MagicaCloth2.TimeManager.UpdateLocation)s.UpdLocation);
                    log.LogInfo($"[jiggle] updateLocation {priorLoc} -> {s.UpdLocation} " +
                                $"(now {(int)MagicaCloth2.MagicaManager.GetUpdateLocation()}), prior saved for cleanup");
                }
                catch (Exception e) { log.LogWarning($"[jiggle] updateLocation failed: {e.Message}"); }
            }
            if (s.SimFreq > 0)
            {
                try
                {
                    int priorFreq = MagicaCloth2.MagicaManager.GetSimulationFrequency();
                    s.SimFreqRest = priorFreq;
                    s.SimFreqSaved = true;
                    MagicaCloth2.MagicaManager.SetSimulationFrequency(s.SimFreq);
                    log.LogInfo($"[jiggle] simulationFrequency {priorFreq} -> " +
                                $"{MagicaCloth2.MagicaManager.GetSimulationFrequency()}, prior saved for cleanup");
                }
                catch (Exception e) { log.LogWarning($"[jiggle] simulationFrequency failed: {e.Message}"); }
            }
            if (s.Hooks) ClothHooks.Install(log);
            if (s.SpawnCloth)
            {
                try
                {
                    var local = new Vector3(0.45f, 1.30f, 0.25f);
                    s.Control = ClothControl.Spawn(s.Model.transform, local);
                    log.LogInfo($"[jiggle] control cloth spawned on '{s.Model.gameObject.name}' at local {local}:{ClothControl.Line(s.Control)}");
                }
                catch (Exception e) { log.LogWarning($"[jiggle] control cloth failed: {e}"); }
            }
            if (s.LoopFix)
            {
                try
                {
                    var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                    bool before = MagicaCloth2.MagicaManager.CheckRegist(ref loop);
                    MagicaCloth2.MagicaManager.InitCustomGameLoop();
                    var loop2 = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                    bool after = MagicaCloth2.MagicaManager.CheckRegist(ref loop2);
                    log.LogInfo($"[jiggle] loopReg {before} -> {after} after InitCustomGameLoop()");
                }
                catch (Exception e) { log.LogWarning($"[jiggle] loopfix failed: {e}"); }
            }
            log.LogInfo($"[jiggle] armed model='{model.gameObject.name}' path='{PathOf(model.transform)}' mask=0x{layers:X} " +
                        $"renderers={s.Skinned.Count} bones=[{string.Join(",", s.Rows.ConvertAll(r => r.Name))}] " +
                        $"cloth='{(s.Cloth != null ? PathOf(s.Cloth.transform) : "none")}' running={run} " +
                        $"center={s.ChestCenter} eye={eye}");
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[jiggle] arm failed: {e}");
            return false;
        }
    }

    // ---- per frame ------------------------------------------------------------------------

    private static void Step(IHostContext log, Sess s)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        double interval = 1.0 / s.Fps;

        if (now < s.WarmupUntil && s.UprightMin > 0f && s.Pelvis != null && s.Rows.Count > 0 && s.Rows[0].T != null)
        {
            Vector3 c = Vector3.zero; int cn = 0;
            foreach (var r in s.Rows) if (r.T != null) { c += r.T.position; cn++; }
            if (cn > 0)
            {
                c /= cn;
                float dy = c.y - s.Pelvis.position.y;
                s.LastDy = dy;
                if (dy >= s.UprightMin)
                {
                    if (s.Freeze && s.Anim != null && s.Anim.enabled)
                    {
                        if (!s.AnimEnabledCaptured) { s.AnimWasEnabled = s.Anim.enabled; s.AnimEnabledCaptured = true; }
                        s.Anim.enabled = false;
                        s.AnimDisabled = true;
                        log.LogInfo($"[jiggle] animator '{PathOf(s.Anim.transform)}' enabled={s.AnimWasEnabled} -> false (upright freeze)");
                    }
                    s.WarmupUntil = now;
                    log.LogInfo($"[jiggle] upright pose reached (chest-pelvis dy={dy:0.###}m >= {s.UprightMin:0.##}), freezing pose now");
                }
            }
        }

        if (now < s.WarmupUntil)
        {
            if (s.LastWarmLog + 0.5 < now)
            {
                s.LastWarmLog = now;
                log.LogInfo($"[jiggle] warmup active={s.Model.gameObject.activeInHierarchy} run={ClothRunning(s)} " +
                            $"chest={s.Rows[0].T.position}");
            }
            return;
        }
        if (s.StartedAt <= 0d)
        {
            s.StartedAt = now; s.NextDue = now;
            // The pose at arm time is not the pose after the animator is frozen; lock the rigid
            // camera on the chest as it is when recording actually starts.
            s.AimFixed = s.ChestCenter;
            for (int i = 0; i < s.Rows.Count; i++)
            {
                var r = s.Rows[i];
                if (r.T == null) continue;
                r.Start = r.T.localRotation; r.StartPos = r.T.localPosition;
            }
            log.LogInfo($"[jiggle] warmup done after {((now - s.WarmupUntil) + s.Warmup):0.##}s " +
                        $"active={s.Model.gameObject.activeInHierarchy} run={ClothRunning(s)}");
            ClothDiag(log, s, "warmup-done");
        }

        // Once a second: what the MagicaCloth2 runtime thinks of this cloth (team step counter,
        // culling state) plus, on request, every cloth in the scene for the control comparison.
        if (now >= s.NextInternals)
        {
            s.NextInternals = now + 1.0;
            if (s.Control != null) log.LogInfo($"[jiggle:control] t={(s.StartedAt > 0d ? now - s.StartedAt : 0d):0.0}s{ClothControl.Line(s.Control)}");
            log.LogInfo($"[jiggle:internals] t={(s.StartedAt > 0d ? now - s.StartedAt : 0d):0.0}s" +
                        $" cloth={ClothInternals.Proc(s.Cloth)}{ClothInternals.Global()}\n" +
                        $"[jiggle:managers] {ClothInternals.Managers()}{ClothHooks.Line()}");
            ClothInternals.TickCensus(log, s.Census, s.Tag);
        }

        // Scripted body sway: a still display rig gives the cloth nothing to react to, so move the
        // whole rig like a body leaning side to side. Restored on cleanup.
        if (s.Sway > 0f && s.StartedAt > 0d)
        {
            if (!s.Swaying) { s.SwayBase = s.Model.transform.position; s.Swaying = true; }
            float phase = (float)((now - s.StartedAt) * s.SwayHz) * Mathf.PI * 2f;
            float off = Mathf.Sin(phase) * s.Sway;
            s.Model.transform.position = s.SwayBase + s.Model.transform.right * off;
        }

        // Manual simulation pump: call the manager's own cloth update phase ourselves. If this
        // moves the cloth, the simulation pipeline works and only its PlayerLoop hook is missing.
        if (s.Pump > 0 && s.StartedAt > 0d)
        {
            try
            {
                if ((s.Pump & 1) != 0) MagicaCloth2.MagicaManager.Cloth.ClothUpdate();
                if ((s.Pump & 2) != 0) MagicaCloth2.MagicaManager.Cloth.OnBeforeLateUpdate();
                if ((s.Pump & 4) != 0) MagicaCloth2.MagicaManager.Cloth.OnEarlyClothUpdate();
                if ((s.Pump & 8) != 0) MagicaCloth2.MagicaManager.Cloth.OnAfterLateUpdate();
                if ((s.Pump & 16) != 0) { MagicaCloth2.MagicaManager.Cloth.CompleteMasterJob(); MagicaCloth2.MagicaManager.Cloth.ClothUpdate(); }
                s.Pumped++;
            }
            catch (Exception e) { if (s.Pumped == 0) log.LogWarning($"[jiggle] pump failed: {e.Message}"); }
        }

        // Push every cloth in the scene. The census motion meter then shows whether any cloth
        // in this build responds to a force at all (the control for the owner-body cloth).
        if (s.KickAll && now >= s.NextKick)
        {
            s.NextKick = now + 0.5;
            ClothInternals.KickAll(log);
        }

        bool due = now + 0.5 * interval >= s.NextDue;

        if (s.Cycle > 0 && s.StartedAt > 0d && !s._cycled && (now - s.StartedAt) > 1.0)
        {
            s._cycled = true;
            try
            {
                var cp = s.Cloth.process;
                int team = cp.TeamId;
                log.LogInfo($"[jiggle] cycle{ s.Cycle } team={team} before: " + ClothInternals.Team(cp));
                if ((s.Cycle & 1) != 0) { MagicaCloth2.MagicaManager.Team.SetEnable(team, false); MagicaCloth2.MagicaManager.Team.SetEnable(team, true); }
                if ((s.Cycle & 2) != 0) { s.Cloth.enabled = false; s.Cloth.enabled = true; }
                if ((s.Cycle & 4) != 0) { s.Cloth.Initialize(); log.LogInfo($"[jiggle] cycle rebuild={s.Cloth.BuildAndRun()}"); }
                log.LogInfo($"[jiggle] cycle{ s.Cycle } after: " + ClothInternals.Team(cp));
            }
            catch (Exception e) { log.LogWarning($"[jiggle] cycle failed: {e}"); }
        }

        if (due)
        {
            int frame = s.Written + 1;
            string events = ApplyDisturbance(log, s, frame);
            if (s.CamHold > 0 && s.Written >= s.CamHold)
            {
                // Keep tracking for a few frames, then pin the camera: with the animator frozen
                // the only thing that can move after this is the cloth, so a frame diff is cloth.
                if (!s.CamFrozen && s.CamGo != null)
                {
                    s.CamFrozen = true; s.CamPos = s.CamGo.transform.position; s.CamRot = s.CamGo.transform.rotation;
                    log.LogInfo($"[jiggle] camera pinned at {s.CamPos} after {s.Written} frame(s)");
                }
                if (s.CamFrozen && s.CamGo != null)
                {
                    s.CamGo.transform.position = s.CamPos;
                    s.CamGo.transform.rotation = s.CamRot;
                }
            }
            else if (s.Follow) Follow(s);
            else
            {
                // Rigid aim: the rig's own transform only, so a cloth that moves the chest cannot
                // drag the camera with it (needed for a clean before/after still).
                if (s.CamGo != null && s.Model != null)
                {
                    var center = s.AimFixed + Vector3.up * s.Lift;
                    var eye = center + s.ViewDir * s.Dist;
                    s.CamGo.transform.position = eye;
                    var look = center - eye;
                    if (look.sqrMagnitude > 1e-8f) s.CamGo.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                }
            }
            SampleUpdate(s);
            string path = Path.Combine(s.Dir, $"frame_{frame:0000}.png");
            if (!Capture(log, s, path)) { Stop(log, s, $"frame {frame} capture failed"); return; }
            s.Written = frame;
            s.NextDue += interval;
            if (s.NextDue < now) s.NextDue = now + interval;
            s.RowPending = true;
            // the row itself is written by LateTick; stash the frame metadata on the session
            _pendingFrame = frame;
            _pendingT = now - s.StartedAt;
            _pendingEvents = events.Replace(',', ';');
            _pendingRun = ClothRunning(s);
            s.LastRun = _pendingRun;
            _pendingGeom = $"{s.Bounds.center.x:0.000}/{s.Bounds.center.y:0.000}/{s.Bounds.center.z:0.000}" +
                           $"|{s.CamGo.transform.position.x:0.000}/{s.CamGo.transform.position.y:0.000}/{s.CamGo.transform.position.z:0.000}" +
                           $"|{s.Bounds.size.x:0.000}/{s.Bounds.size.y:0.000}/{s.Bounds.size.z:0.000}" +
                           $"|{s.ChestCenter.x:0.000}/{s.ChestCenter.y:0.000}/{s.ChestCenter.z:0.000}";
            if (frame % 10 == 0 || frame == s.Total)
                log.LogInfo($"[jiggle] frame {frame}/{s.Total} t={(_pendingT):0.##}s events='{events}' run={_pendingRun} geom={_pendingGeom}");
        }

        double deadline = s.Seconds * 2.0 + 2.0;
        if (s.Written >= s.Total) Stop(log, s, null);
        else if (now - s.StartedAt > deadline) Stop(log, s, $"wall-clock deadline at {s.Written}/{s.Total} frames");
    }

    private static int _pendingFrame;
    private static double _pendingT;
    private static string _pendingEvents = "";
    private static string _pendingGeom = "";
    private static bool _pendingRun;

    // Re-aim every recorded frame: an activated display rig can be laid out or animated by the
    // game, so a camera fixed at arm time loses the subject.
    private static void Follow(Sess s)
    {
        if (s.CamGo == null || s.Cam == null) return;
        var b = RenderBounds(s);
        if (b.size.sqrMagnitude > 1e-8f) s.Bounds = b;
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var r in s.Rows)
        {
            if (r.T == null) continue;
            sum += r.T.position; n++;
        }
        s.ChestCenter = n > 0 ? sum / n : s.Bounds.center;
        var center = s.ChestCenter + Vector3.up * s.Lift;
        var eye = center + s.ViewDir * s.Dist;
        s.CamGo.transform.position = eye;
        var look = center - eye;
        if (look.sqrMagnitude > 1e-8f) s.CamGo.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
    }

    // Union of the rig's live renderer bounds; skinned bounds follow the bones only when the
    // renderer is updating, which is exactly what this probe forces.
    private static Bounds RenderBounds(Sess s)
    {
        Bounds total = new Bounds(s.Model != null ? s.Model.transform.position : Vector3.zero, Vector3.zero);
        bool any = false;
        for (int i = 0; i < s.Skinned.Count; i++)
        {
            var r = s.Skinned[i];
            if (r == null) continue;
            Bounds b;
            try { b = r.bounds; } catch { continue; }
            if (b.size.sqrMagnitude <= 1e-8f) continue;
            if (!any) { total = b; any = true; } else total.Encapsulate(b);
        }
        return total;
    }

    private static bool ClothRunning(Sess s)
    {
        try { var p = s.Cloth?.process; return p != null && p.IsRunning(); } catch { return false; }
    }

    // Applies the scripted disturbance for this frame index and returns a short event label.
    private static string ApplyDisturbance(IHostContext log, Sess s, int frame)
    {
        string ev = "";
        if (s.ForceFrame > 0 && frame >= s.ForceFrame && frame < s.ForceFrame + 3)
        {
            try
            {
                // A horizontal shove in the rig's own frame: the same direction an idle sway or a
                // step would push the chest.
                var dir = s.Model.transform.right;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.right;
                dir.Normalize();
                s.Cloth?.AddForce(dir, s.Multiplier, ClothForceMode.VelocityAdd);
                s.ForceApplied++;
                ev += $"force+{frame} ";
            }
            catch (Exception e) { log.LogWarning($"[jiggle] AddForce failed: {e.Message}"); }
        }

        if (s.PokeFrame > 0 && frame >= s.PokeFrame && !s.Poked && !s.PokeDone)
        {
            var root = FindRow(s, "chest_01_l");
            if (root?.T != null)
            {
                s.Poked = true; s.PokeDone = true; s.PokeRest = root.T.localRotation; s.PokeEnd = frame + Mathf.Max(1, s.Hold);
                root.T.localRotation = s.PokeRest * Quaternion.Euler(0f, 0f, s.PokeDeg);
                s.PokeApplied++;
                ev += $"poke+{frame}(hold {s.Hold}) ";
            }
        }
        else if (s.Poked && frame >= s.PokeEnd)
        {
            var root = FindRow(s, "chest_01_l");
            if (root?.T != null) root.T.localRotation = s.PokeRest;
            s.Poked = false;
            ev += $"poke-release+{frame} ";
        }

        // Leaf fork test: rotate the bone the cloth is supposed to *drive* and never restore it.
        // If the cloth writes that bone every frame, the offset is gone on the next sample; if it
        // stays, nothing writes the leaf and there is no cloth-driven motion to see.
        if (s.LeafFrame > 0 && frame == s.LeafFrame)
        {
            var leaf = FindRow(s, "chest_02_l");
            if (leaf?.T != null)
            {
                s.LeafRest = leaf.T.localRotation;
                leaf.T.localRotation = s.LeafRest * Quaternion.Euler(0f, 0f, s.LeafDeg);
                s.LeafPoked = true;
                ev += $"leaf+{frame}({s.LeafDeg:0.##}deg, not restored) ";
            }
        }
        return ev.Trim();
    }

    private static Row FindRow(Sess s, string name)
    {
        foreach (var r in s.Rows) if (r.Name == name) return r;
        return null;
    }

    // Everything MagicaCloth2 exposes about whether this cloth can simulate at all.
    private static void ClothDiag(IHostContext log, Sess s, string when)
    {
        var mc = s.Cloth;
        if (mc == null) return;
        try
        {
            var p = mc.process;
            bool valid = false, run = false, enabled = false;
            int clothType = 0, resultVal = 0;
            try { valid = p != null && p.IsValid(); } catch { }
            try { run = p != null && p.IsRunning(); } catch { }
            try { enabled = mc.enabled && mc.gameObject.activeInHierarchy; } catch { }
            try { if (p != null) { resultVal = (int)p.result.Result; clothType = (int)p.clothType; } } catch { }
            float ts = -1f;
            try { ts = mc.GetTimeScale(); } catch { }
            string extra = "";
            try { var sd = mc.serializeData; extra = $"blend={sd.blendWeight:F2} poseRatio={sd.animationPoseRatio:F2} gravity={sd.gravity:F2} update={sd.updateMode}"; } catch { }
            string par = "";
            try
            {
                var sd = mc.serializeData;
                var ic = sd.inertiaConstraint;
                var ar = sd.angleRestorationConstraint;
                var al = sd.angleLimitConstraint;
                var dc = sd.distanceConstraint;
                var tt = sd.tetherConstraint;
                string Curve(CurveSerializeData c, string vn)
                {
                    try { var v = c != null ? c.GetType().GetProperty(vn)?.GetValue(c) : null; return v?.ToString() ?? "?"; } catch { return "?"; }
                }
                par = $" worldInertia={ic.worldInertia:F2} localInertia={ic.localInertia:F2} moveSmooth={ic.movementInertiaSmoothing:F2} depthInertia={ic.depthInertia:F2} " +
                      $"anchorInertia={ic.anchorInertia:F2} teleportDist={ic.teleportDistance:F2} " +
                      $"angleRestore={ar.useAngleRestoration} restoreStiff={Curve(ar.stiffness, "value")} atten={ar.velocityAttenuation:F2} " +
                      $"angleLimit={al.useAngleLimit} limitStiff={al.stiffness:F2} " +
                      $"tetherCompress={tt.distanceCompression:F2} " +
                      $"stabilize={sd.stablizationTimeAfterReset:F2} rotInterp={sd.rotationalInterpolation:F2} rootRot={sd.rootRotation:F2} connection={sd.connectionMode}";
            }
            catch (Exception e) { par = $" <params {e.GetType().Name}: {e.Message}>"; }
            log.LogInfo($"[jiggle:cloth] {when} valid={valid} running={run} compActive={enabled} result={resultVal} type={clothType} timeScale={ts:F2} {extra}{par}");
            log.LogInfo($"[jiggle:internals] {when} cloth={ClothInternals.Proc(mc)}{ClothInternals.Global()}");
        }
        catch (Exception e) { log.LogWarning($"[jiggle:cloth] diag failed: {e.Message}"); }
    }

    private static void SampleUpdate(Sess s)
    {
        foreach (var r in s.Rows)
        {
            if (r.T == null) continue;
            r.UpAngle = Quaternion.Angle(r.Start, r.T.localRotation);
            r.UpPos = (r.T.localPosition - r.StartPos).magnitude;
        }
    }

    private static void FinishRow(IHostContext log, Sess s)
    {
        s.RowPending = false;
        float worst = 0f;
        foreach (var r in s.Rows)
        {
            if (r.T == null) continue;
            r.LateAngle = Quaternion.Angle(r.Start, r.T.localRotation);
            r.LatePos = (r.T.localPosition - r.StartPos).magnitude;
            if (_pendingFrame < s.PokeFrame && (_pendingFrame < s.ForceFrame || s.ForceFrame <= 0))
            {
                if (r.LateAngle > r.BaseAngleMax) r.BaseAngleMax = r.LateAngle;
            }
            if (r.LateAngle > r.MaxAngle) r.MaxAngle = r.LateAngle;
            if (r.LatePos > r.MaxPos) r.MaxPos = r.LatePos;
            if (r.LateAngle > worst) worst = r.LateAngle;
            s.Csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:0.000},{2},{3},{4},{5:F4},{6:F4},{7:F6},{8:F6},{9}",
                _pendingFrame, _pendingT, _pendingRun ? 1 : 0, _pendingEvents, r.Name,
                r.UpAngle, r.LateAngle, r.UpPos, r.LatePos, _pendingGeom));
        }

        // A disturbance frame is reported immediately, in full detail: this is the evidence.
        if (_pendingEvents.Length > 0)
        {
            var sb = new StringBuilder();
            foreach (var r in s.Rows)
            {
                if (r.T == null) continue;
                sb.Append($"{r.Name}:up={r.UpAngle:F2}deg late={r.LateAngle:F2}deg dPos={r.LatePos * 1000f:F2}mm ");
            }
            log.LogInfo($"[jiggle:event] f={_pendingFrame} t={_pendingT:0.000}s '{_pendingEvents}' run={_pendingRun} {sb}");
        }
    }

    private static bool Capture(IHostContext log, Sess s, string path)
    {
        RenderTexture prevActive = null, prevTarget = null;
        bool swapped = false;
        try
        {
            var cam = s.Cam;
            if (cam == null || cam.gameObject == null) { log.LogError("[jiggle] camera is gone"); return false; }
            prevActive = RenderTexture.active;
            prevTarget = cam.targetTexture;
            if (!s.CamLive)
            {
                cam.targetTexture = s.Rt;
                swapped = true;
                cam.Render();
                cam.targetTexture = prevTarget;
                swapped = false;
            }
            RenderTexture.active = s.Rt;
            s.Tex.ReadPixels(new Rect(0, 0, s.Width, s.Height), 0, 0, false);
            var png = ImageConversion.EncodeToPNG(s.Tex);
            if (png == null || png.Length == 0) { log.LogError("[jiggle] PNG encode returned nothing"); return false; }
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception e) { log.LogError($"[jiggle] capture failed: {e}"); return false; }
        finally
        {
            try { if (swapped && s.Cam != null) s.Cam.targetTexture = prevTarget; } catch { }
            try { RenderTexture.active = prevActive; } catch { }
        }
    }

    private static void Stop(IHostContext log, Sess s, string reason)
    {
        if (_s == s) _s = null;
        double wall = Time.realtimeSinceStartupAsDouble - s.StartedAt;
        var sb = new StringBuilder();
        foreach (var r in s.Rows)
        {
            if (r.T == null) continue;
            sb.Append($"\n  {r.Name}: preDisturbanceMax={r.BaseAngleMax:F3}deg maxLate={r.MaxAngle:F3}deg " +
                      $"maxLocalPos={r.MaxPos * 1000f:F2}mm up={r.UpAngle:F3}deg late={r.LateAngle:F3}deg");
        }
        string csvPath = Path.Combine(log.ScreensDir, s.Tag + ".csv");
        try { File.WriteAllText(csvPath, s.Csv.ToString()); } catch (Exception e) { log.LogWarning($"[jiggle] csv: {e.Message}"); }
        string head = reason == null
            ? $"[jiggle] finish tag='{s.Tag}' frames={s.Written} wall={wall:0.##}s effective={s.Written / Math.Max(0.01, wall):0.##}fps " +
              $"pokeApplied={s.PokeApplied} forceApplied={s.ForceApplied} run={s.LastRun} csv='{csvPath}'"
            : $"[jiggle] stopped tag='{s.Tag}' after {s.Written}/{s.Total} frame(s): {reason} csv='{csvPath}'";
        try { if (reason == null) log.LogInfo(head + sb); else log.LogError(head + sb); } catch { }
        Cleanup(log, s);
    }

    private static void Cleanup(IHostContext log, Sess s)
    {
        if (s == null) return;
        try
        {
            var root = FindRow(s, "chest_01_l");
            if (s.Poked && root?.T != null) root.T.localRotation = s.PokeRest;
        }
        catch { }
        try
        {
            var leaf = FindRow(s, "chest_02_l");
            if (s.LeafPoked && leaf?.T != null && !s.LeafDeg.Equals(0f)) leaf.T.localRotation = s.LeafRest;
        }
        catch { }
        for (int i = 0; i < s.Skinned.Count; i++)
        {
            try { s.Skinned[i].enabled = s.Enabled[i]; s.Skinned[i].updateWhenOffscreen = s.Offscreen[i]; } catch { }
        }
        try { if (s.Anim != null) s.Anim.cullingMode = s.AnimCulling; } catch { }
        try
        {
            if (s.AnimDisabled && s.Anim != null)
                s.Anim.enabled = s.AnimWasEnabled;
        }
        catch { }

        try { if (s.AnimSpeedSaved && s.Anim != null) s.Anim.speed = s.AnimSpeedRest; } catch { }
        try { if (s.ClothOff && s.Cloth != null) s.Cloth.enabled = s.ClothWasEnabled; } catch { }
        try { ClothControl.Dispose(s.Control); } catch { }
        try { if (s.Swaying && s.Model != null) s.Model.transform.position = s.SwayBase; } catch { }
        try { ClothHooks.Uninstall(log); } catch { }
        try { if (s.Rt != null) { s.Rt.Release(); UnityEngine.Object.Destroy(s.Rt); } } catch { }
        try { if (s.Tex != null) UnityEngine.Object.Destroy(s.Tex); } catch { }
        try { if (s.CamGo != null) UnityEngine.Object.Destroy(s.CamGo); } catch { }
        try
        {
            if (s.GravitySaved && s.Cloth != null)
            {
                var sd = s.Cloth.serializeData;
                if (sd != null) { sd.gravity = s.GravityRest; s.Cloth.SetParameterChange(); }
            }
        }
        catch { }
        try
        {
            if (s.UpdateSaved && s.Cloth != null)
            {
                var sd = s.Cloth.serializeData;
                if (sd != null) { sd.updateMode = s.UpdateRest; s.Cloth.SetParameterChange(); }
            }
        }
        catch { }
        try
        {
            // Probing must not leave skip-writing latched: SetSkipWriting only takes effect after
            // a parameter change, so a later SetParameterChange (gravity/updateMode) would silently
            // turn it on and starve the next measurement.
            if (s.SkipWrite >= 0 && s.Cloth != null)
            {
                s.Cloth.SetSkipWriting(false);
                s.Cloth.SetParameterChange();
            }
        }
        catch { }
        try
        {
            if (s.CullingSaved && s.Cloth != null)
            {
                var cs = s.Cloth.serializeData?.cullingSettings;
                if (cs != null) { cs.cameraCullingMode = (CullingSettings.CameraCullingMode)s.CullingRest; s.Cloth.SetParameterChange(); }
            }
        }
        catch { }
        try
        {
            if (s.GravityPropSaved && s.Cloth != null)
            {
                s.Cloth.gravityProperty = s.GravityPropRest;
                log.LogInfo($"[jiggle] cleanup gravityProperty -> {s.GravityPropRest:F2} (probe value was {s.GravityProp:F2})");
            }
        }
        catch { }
        try
        {
            if (s.GravityFieldSaved && s.Cloth != null)
            {
                s.Cloth._gravityProperty = s.GravityFieldRest;
                log.LogInfo($"[jiggle] cleanup _gravityProperty -> {s.GravityFieldRest:F2} (probe value was {s.GravityField:F2})");
            }
        }
        catch { }
        try
        {
            if (s.UpdLocSaved)
            {
                int now = (int)MagicaCloth2.MagicaManager.GetUpdateLocation();
                if (now != s.UpdLocRest) MagicaCloth2.MagicaManager.SetUpdateLocation((MagicaCloth2.TimeManager.UpdateLocation)s.UpdLocRest);
                log.LogInfo($"[jiggle] cleanup updateLocation {now} -> {(int)MagicaCloth2.MagicaManager.GetUpdateLocation()} " +
                            $"(prior={s.UpdLocRest}; the PlayerLoop registration itself is global and is not undone)");
            }
        }
        catch { }
        try
        {
            if (s.SimFreqSaved)
            {
                int now = MagicaCloth2.MagicaManager.GetSimulationFrequency();
                if (now != s.SimFreqRest) MagicaCloth2.MagicaManager.SetSimulationFrequency(s.SimFreqRest);
                log.LogInfo($"[jiggle] cleanup simulationFrequency {now} -> {MagicaCloth2.MagicaManager.GetSimulationFrequency()} " +
                            $"(prior={s.SimFreqRest})");
            }
        }
        catch { }
        try
        {
            if (s.Placed && s.Model != null)
            {
                s.Model.transform.position = s.PlaceRest;
                s.Model.transform.rotation = s.PlaceRestRot;
            }
        }
        catch { }
        // Undo the temporary activation: the game expects these display rigs inactive.
        for (int i = s.Reactivated.Count - 1; i >= 0; i--)
        {
            try { if (s.Reactivated[i] != null) s.Reactivated[i].SetActive(false); } catch { }
        }
        s.Reactivated.Clear();
        s.Rt = null; s.Tex = null; s.Cam = null; s.CamGo = null;
        LogState(log, s);
        try
        {
            if (s.Written == 0 && s.Dir != null && Directory.Exists(s.Dir) && Directory.GetFileSystemEntries(s.Dir).Length == 0)
                Directory.Delete(s.Dir);
        }
        catch { }
    }

    // One line per cleanup with every field the probe may have touched, read back from the live
    // objects. This is the comparison point for an aborted probe: it shows what was restored and
    // what was never mutated (e.g. an animator the upright warmup never reached).
    private static void LogState(IHostContext log, Sess s)
    {
        var sb = new StringBuilder();
        try
        {
            sb.Append($" freeze={(s.Freeze ? 1 : 0)} animCaptured={(s.AnimEnabledCaptured ? 1 : 0)}");
            sb.Append($" animWas={(s.AnimEnabledCaptured ? (s.AnimWasEnabled ? "on" : "off") : "?")}");
            sb.Append($" animTouched={(s.AnimDisabled ? 1 : 0)}");
            sb.Append($" animNow={(s.Anim != null ? (s.Anim.enabled ? "on" : "off") : "none")}");
            if (s.Anim != null) sb.Append($" animSpeedNow={s.Anim.speed:F2} animCullNow={(int)s.Anim.cullingMode}");
            var mc = s.Cloth;
            if (mc != null)
            {
                try { var sd = mc.serializeData; if (sd != null) sb.Append($" gravityNow={sd.gravity:F3} updateModeNow={(int)sd.updateMode}"); } catch { }
                try { sb.Append($" gPropNow={mc.gravityProperty:F3}"); } catch { }
                try { sb.Append($" gFieldNow={mc._gravityProperty:F3}"); } catch { }
                try { sb.Append($" skipWriteNow={mc.process?.IsSkipWriting()}"); } catch { }
                try { sb.Append($" cullNow={(int)(mc.serializeData?.cullingSettings?.cameraCullingMode ?? 0)}"); } catch { }
            }
            sb.Append($" updLocNow={(int)MagicaCloth2.MagicaManager.GetUpdateLocation()}");
            sb.Append($" simFreqNow={MagicaCloth2.MagicaManager.GetSimulationFrequency()}");
            sb.Append($" forceStepsNow={ClothHooks.ForceSteps}");
            sb.Append($" hooksInstalled={ClothHooks.Installed}");
            sb.Append($" modelPlaced={(s.Placed ? 1 : 0)} swaying={(s.Swaying ? 1 : 0)}");
        }
        catch (Exception e) { sb.Append($" <log-state {e.GetType().Name}: {e.Message}>"); }
        log.LogInfo($"[jiggle:cleanup] tag='{s.Tag}' reason=cleanup{sb}");
    }

    // ---- helpers --------------------------------------------------------------------------

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        for (int i = 0; p != null && i < 12; i++) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        if (s == null) return "jiggle";
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
        {
            char c = s[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        return sb.Length == 0 ? "jiggle" : sb.ToString();
    }

    private static CullingSettings.CameraCullingMode CsMode(string v)
    {
        switch (v)
        {
            case "animatorlinkage": return CullingSettings.CameraCullingMode.AnimatorLinkage;
            case "keep": return CullingSettings.CameraCullingMode.Keep;
            default: return CullingSettings.CameraCullingMode.Reset;
        }
    }

    // +active/-inactive along the chain, for deciding whether a display rig can be shown at all.
    // Same scoring rule as ClipRecorder/Pocedures: the lobby camera that is actually on screen.
    private static Camera FindGameCamera(IHostContext log)
    {
        Camera best = null;
        int bestScore = int.MinValue;
        try
        {
            var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null || !go.activeInHierarchy || !c.enabled) continue;
                if (c.targetTexture != null) continue;
                if (go.scene.name == null || go.scene.name.Length == 0) continue;
                if (c.pixelWidth <= 0 || c.pixelHeight <= 0) continue;
                int score = 0;
                string path = PathOf(go.transform);
                if (path.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
                if (path.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) score -= 20;
                if (go.name.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                score += (int)c.depth;
                if (score > bestScore) { bestScore = score; best = c; }
            }
        }
        catch (Exception e) { log?.LogWarning($"[jiggle] camera scan failed: {e.Message}"); }
        return best;
    }

    private static string Chain(GameObject go)
    {
        var sb = new StringBuilder();
        Transform t = go != null ? go.transform : null;
        for (int i = 0; t != null && i < 8; i++, t = t.parent)
            sb.Insert(0, $"{(t.gameObject.activeSelf ? "+" : "-")}{t.name}/");
        return sb.ToString();
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int I(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
