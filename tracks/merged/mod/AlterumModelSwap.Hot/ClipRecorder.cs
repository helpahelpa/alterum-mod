using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Records a PNG frame sequence of the game's 3D view into "<ScreensDir>/<tag>/frame_%04d.png"
// for assembly with make_video.sh. Self-contained like ScreenShot.cs: drop this file into any
// hot-assembly copy and call ClipRecorder.Tick(host, dt) once per frame from the module's Update;
// other code can start a clip directly through ClipRecorder.Record(...).
//
// It never uses ScreenCapture and never reads the desktop, so it keeps working while the Windows
// desktop is locked: each frame renders one camera into a private RenderTexture and copies the
// pixels back. One Render per recorded frame, sampled on a wall-clock schedule
// (Time.realtimeSinceStartupAsDouble) - never per game frame and never from Time.deltaTime,
// which is clamped. Recording therefore spans many Update ticks, so the animator keeps
// advancing and the result is motion, not N copies of one pose.
//
// Trigger (the only one; the F11 row was removed with the controls prune):
//   * a request file "<ScreensDir>/clip.request", deleted on pickup. Line-oriented:
//       tag=emotes_griddy      output folder name (default: clip)
//       seconds=6              wall-clock length, clamped 0.2..60 (default 6)
//       fps=15                 sample rate, clamped 1..60 (default 15)
//       view=showcase          "showcase" = own camera around the local third-person body;
//                              "main" = the game camera as the player sees it
//       yaw=0                  showcase orbit angle, degrees (0 = +X, 90 = +Z);
//                              yaw=auto frames the body from its own forward axis
//       dist=2.6               showcase distance; 0/absent derives it from the body bounds
//       fov=40                 showcase vertical field of view, degrees
//       lift=0.18              showcase height factor: eye = anchor + (cos yaw, lift, sin yaw)*dist
//       look=body              "body" (default): look at the body bounds centre + 0.15*extents.y;
//                              "head": look at the head bone (the anchor becomes the head point, so
//                              dist/lift orbit the head and a tight head shot is reachable)
//       lookoffset=0,0.15,0    metres added to the look point along world axes
//       eye=x,y,z              pin the camera position in world space (with lookat, the pose no
//       lookat=x,y,z           longer derives from body bounds, so an equipped/unequipped pair is
//                              pixel-comparable); both must be given or neither is used
//       width=960              output width, clamped 64..1920, forced even
//       height=540             output height, clamped 64..1080, forced even
//     Blank lines and lines starting with '#' are ignored; unknown keys are logged, not fatal.
//
// The lobby's screen camera sits at the player's eyes in both POVs (PovToggle flips the game's own
// third-person mode, it does not move that camera), so a body shot needs the showcase view: a
// dedicated camera framing the local player's ThirdViewPlayerModel, whose renderers
// MirrorPlayerVisibility isolates onto a mirror-only layer. The culling mask is therefore explicit
// and inclusive: the game camera's mask plus the body layer, the cosmetic layer, and whatever layer
// the body's renderers actually sit on. The cosmetic renderers themselves only enable in the game's
// third-person POV (CosmeticsRuntime's screen policy), so turn PovToggle on before a hat clip.
public static class ClipRecorder
{
    private const string RequestFileName = "clip.request";
    private const float RequestPollSeconds = 0.5f;

    private const int DefaultWidth = 960, DefaultHeight = 540;
    private const int MinSize = 64, MaxWidth = 1920, MaxHeight = 1080;
    private const float DefaultSeconds = 6f, MinSeconds = 0.2f, MaxSeconds = 60f;
    private const float DefaultFps = 15f, MinFps = 1f, MaxFps = 60f;
    // The game is capped at 15 fps, so 300 frames is 20 s of wall time; a runaway request cannot
    // fill the disk before it is done.
    private const int HardFrameCap = 300;
    private const float DefaultFov = 40f;
    private const float DefaultLift = 0.18f;
    private const float DefaultYaw = 30f;

    // Layers the showcase camera must include even where the game camera excludes them:
    // 14 = the isolated local-body layer MirrorPlayerVisibility moves the body to,
    // 23 = cosmetics (hats). The body's live layers are read from its renderers as well.
    private const int BodyLayer = 14, CosmeticLayer = 23;

    private sealed class Session
    {
        public string Tag, View, Dir;
        public float Seconds, Fps, Yaw, Dist, Fov, Lift;
        public int Width, Height, TotalFrames, Written;
        public bool YawAuto;
        // Optional framing/pose controls (additive; all default to the previous bounds-derived pose).
        public string Look;
        public Vector3 LookOffset;
        public bool PosePinned;
        public Vector3 PinnedEye, PinnedLook;
        public Camera Cam;
        public GameObject CamGo;
        public bool OwnsCam;
        public RenderTexture Rt;
        public Texture2D Tex;
        public Transform Body;
        public List<Renderer> BodyRenderers;
        public int Mask;
        public double StartedAt, NextDue;
        public int LastBytes;
    }

    // Public so other modules can start a clip without going through a request file.
    public struct ClipSpec
    {
        public string Tag;
        public string View;
        public float Seconds, Fps, Yaw, Dist, Fov, Lift;
        public int Width, Height;
        // yaw=auto: put the camera on the body's own forward axis instead of a world angle.
        public bool YawAuto;
        public string Look;                 // "body" (default) or "head"
        public Vector3 LookOffset;          // metres added to the look point (world axes)
        public bool HasEye, HasLookAt;      // both => pin the pose, ignore bounds-derived framing
        public Vector3 Eye, LookAt;

        public static ClipSpec Default(string tag) => new ClipSpec
        {
            Tag = tag,
            View = "showcase",
            Seconds = DefaultSeconds,
            Fps = DefaultFps,
            Yaw = DefaultYaw,
            Dist = 0f,
            Fov = DefaultFov,
            Lift = DefaultLift,
            Width = DefaultWidth,
            Height = DefaultHeight,
            Look = "body",
        };
    }

    private static Session _session;
    private static float _elapsed;
    private static float _nextRequestPoll;
    private static bool _announced;

    public static bool IsRecording => _session != null;

    // Frame hook: hotkey, request file, and the recorder's own per-frame step. Safe to call
    // every frame; never throws into the game loop.
    public static void Tick(IHostContext log, float unscaledDeltaTime)
    {
        if (log == null) return;
        _elapsed += unscaledDeltaTime;
        if (!_announced)
        {
            _announced = true;
            try { log.LogInfo($"[clip] ready trigger=none (request file only) request={RequestPath(log)} " +
                              $"default={DefaultWidth}x{DefaultHeight} cap={HardFrameCap} frames"); }
            catch { }
        }
        try { PollRequestFile(log); }
        catch (Exception e) { log.LogWarning($"[clip] request poll failed: {e.Message}"); }

        var s = _session;
        if (s == null) return;
        try { Step(log, s); }
        catch (Exception e) { Stop(log, s, false, $"tick failed: {e}"); }
    }

    // Convenience: defaults for the given tag.
    public static bool Record(IHostContext log, string tag) => Record(log, ClipSpec.Default(tag));

    // Start a clip. Returns false (without touching game state) if one is already running or the
    // requested view cannot be resolved. Frames are then written by Tick.
    public static bool Record(IHostContext log, ClipSpec spec)
    {
        if (log == null) return false;
        if (_session != null)
        {
            log.LogWarning($"[clip] already recording tag='{_session.Tag}' ({_session.Written}/{_session.TotalFrames}); request for '{spec.Tag}' dropped");
            return false;
        }

        var s = new Session();
        try
        {
            s.Tag = Sanitize(spec.Tag);
            if (s.Tag.Length == 0) s.Tag = "clip";
            s.View = (spec.View ?? "").Trim().ToLowerInvariant();
            if (s.View.Length == 0) s.View = "showcase";
            if (s.View != "main" && s.View != "showcase")
            {
                log.LogWarning($"[clip] unknown view '{spec.View}'; using showcase");
                s.View = "showcase";
            }

            s.Seconds = Clamp(spec.Seconds > 0f ? spec.Seconds : DefaultSeconds, MinSeconds, MaxSeconds);
            s.Fps = Clamp(spec.Fps > 0f ? spec.Fps : DefaultFps, MinFps, MaxFps);
            s.Yaw = spec.Yaw;
            s.YawAuto = spec.YawAuto;
            s.Dist = Mathf.Max(0f, spec.Dist);
            s.Fov = Clamp(spec.Fov > 0f ? spec.Fov : DefaultFov, 5f, 120f);
            s.Lift = spec.Lift;
            s.Look = string.IsNullOrEmpty(spec.Look) ? "body" : spec.Look.Trim().ToLowerInvariant();
            s.LookOffset = spec.LookOffset;
            if (s.Look != "head" && s.Look != "body") s.Look = "body";
            if (spec.HasEye && spec.HasLookAt) { s.PosePinned = true; s.PinnedEye = spec.Eye; s.PinnedLook = spec.LookAt; }
            else if (spec.HasEye || spec.HasLookAt)
                log.LogWarning("[clip] pose pinned only halfway (need both eye= and lookat=); using the bounds-derived pose");

            int w = Clamp(spec.Width > 0 ? spec.Width : DefaultWidth, MinSize, MaxWidth);
            int h = Clamp(spec.Height > 0 ? spec.Height : DefaultHeight, MinSize, MaxHeight);
            if ((w & 1) != 0) { w--; log.LogInfo($"[clip] width forced even -> {w} (yuv420p)"); }
            if ((h & 1) != 0) { h--; log.LogInfo($"[clip] height forced even -> {h} (yuv420p)"); }
            s.Width = w; s.Height = h;

            s.TotalFrames = Mathf.Clamp(Mathf.RoundToInt(s.Seconds * s.Fps), 1, HardFrameCap);
            s.Dir = Path.Combine(log.ScreensDir, s.Tag);

            var mainCam = FindGameCamera(log);
            if (mainCam == null) log.LogWarning("[clip] no game camera found (menu/loading?); mask starts empty");
            int baseMask = mainCam != null ? mainCam.cullingMask : 0;

            if (s.View == "main")
            {
                if (mainCam == null) { log.LogError("[clip] view=main: no game camera; not recording"); return false; }
                s.Cam = mainCam;
                s.OwnsCam = false;
                s.Mask = baseMask;
                log.LogInfo($"[clip] mask view=main camera='{PathOf(mainCam.transform)}' mask=0x{s.Mask:X8} (game camera mask, unchanged)");
                LogCameraCensus(log, mainCam);
            }
            else
            {
                if (!CreateShowcase(log, s, mainCam, baseMask)) return false;
            }

            log.LogInfo($"[clip] start tag='{s.Tag}' view={s.View} {s.Width}x{s.Height} fps={Inv(s.Fps)} seconds={Inv(s.Seconds)} frames={s.TotalFrames} " +
                        $"dir='{s.Dir}' camera='{PathOf(s.Cam.transform)}' camPos={Fmt(s.Cam.transform.position)} " +
                        $"camRot={Fmt(s.Cam.transform.eulerAngles)} mask=0x{s.Mask:X8}");

            Directory.CreateDirectory(s.Dir);
            int stale = DeleteStaleFrames(s.Dir);
            if (stale > 0) log.LogInfo($"[clip] removed {stale} stale frame(s) from '{s.Dir}'");

            s.Rt = new RenderTexture(s.Width, s.Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "AlterumClipRT",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!s.Rt.Create() || !s.Rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
            s.Tex = new Texture2D(s.Width, s.Height, TextureFormat.RGB24, false);

            s.StartedAt = Time.realtimeSinceStartupAsDouble;
            s.NextDue = s.StartedAt;
            _session = s;
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[clip] start failed (tag='{spec.Tag}'): {e}");
            Cleanup(s);
            return false;
        }
    }

    // Hot reload / shutdown: a recording lives in a static, so the retiring generation must
    // destroy its own native resources now - the next generation cannot see them.
    public static void Abort(IHostContext log)
    {
        var s = _session;
        if (s == null) return;
        _session = null;
        try { log?.LogWarning($"[clip] abort tag='{s.Tag}' after {s.Written} frame(s) (reload/shutdown); resources destroyed"); } catch { }
        Cleanup(s);
    }

    private static void Step(IHostContext log, Session s)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        double interval = 1.0 / s.Fps;

        // Half an interval of slack: the game ticks at its own 15 fps, so a strict deadline would
        // drop every other sample. Still wall-clock scheduling, never frame counting.
        if (now + 0.5 * interval >= s.NextDue)
        {
            if (s.BodyRenderers != null && s.Written % 10 == 0) RefreshMask(log, s);
            string path = Path.Combine(s.Dir, $"frame_{s.Written + 1:0000}.png");
            if (!CaptureFrame(log, s, path)) { Stop(log, s, false, $"frame {s.Written + 1} failed"); return; }
            s.Written++;
            s.NextDue += interval;
            if (s.NextDue < now) s.NextDue = now + interval;   // hitch: drop missed slots, do not burst
            if (s.Written % 10 == 0 || s.Written == s.TotalFrames)
                log.LogInfo($"[clip] {s.Tag} frame {s.Written}/{s.TotalFrames} t={Inv(now - s.StartedAt)}s bytes={s.LastBytes}");
        }

        // Wall-clock safety net for a stalled tick source (locked desktop, device loss): give the
        // requested duration a generous multiple before giving up on the frame count.
        double deadline = s.Seconds * 2.0 + 2.0;
        if (s.Written >= s.TotalFrames) Stop(log, s, true, null);
        else if (now - s.StartedAt > deadline) Stop(log, s, false, $"wall-clock deadline ({Inv(s.Seconds * 2.0 + 2.0)}s) reached at {s.Written}/{s.TotalFrames} frames");
    }

    private static bool CaptureFrame(IHostContext log, Session s, string path)
    {
        var cam = s.Cam;
        RenderTexture previousActive = null;
        RenderTexture previousTarget = null;
        bool swapped = false;
        try
        {
            if (cam == null || cam.gameObject == null) { log.LogError("[clip] camera is gone"); return false; }

            previousActive = RenderTexture.active;
            previousTarget = cam.targetTexture;
            cam.targetTexture = s.Rt;
            swapped = true;
            cam.Render();
            cam.targetTexture = previousTarget;
            swapped = false;

            RenderTexture.active = s.Rt;
            s.Tex.ReadPixels(new Rect(0, 0, s.Width, s.Height), 0, 0, false);

            var png = ImageConversion.EncodeToPNG(s.Tex);
            if (png == null || png.Length == 0) { log.LogError($"[clip] PNG encode returned no bytes for '{path}'"); return false; }
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            File.WriteAllBytes(path, bytes);
            s.LastBytes = bytes.Length;
            return true;
        }
        catch (Exception e)
        {
            log.LogError($"[clip] frame capture failed ('{path}'): {e}");
            return false;
        }
        finally
        {
            try { if (swapped && cam != null) cam.targetTexture = previousTarget; } catch { }
            try { RenderTexture.active = previousActive; } catch { }
        }
    }

    private static void Stop(IHostContext log, Session s, bool ok, string reason)
    {
        if (_session == s) _session = null;
        double wall = Time.realtimeSinceStartupAsDouble - s.StartedAt;
        try
        {
            if (ok)
            {
                double effective = wall > 0.01 ? s.Written / wall : 0.0;
                log.LogInfo($"[clip] finish tag='{s.Tag}' frames={s.Written} dir='{s.Dir}' pattern='frame_%04d.png' {s.Width}x{s.Height} " +
                            $"requested={Inv(s.Fps)}fps/{Inv(s.Seconds)}s wall={Inv(wall)}s effective={Inv(effective)}fps view={s.View}");
            }
            else
            {
                log.LogError($"[clip] stopped tag='{s.Tag}' after {s.Written}/{s.TotalFrames} frame(s): {reason} dir='{s.Dir}'");
            }
        }
        catch { }
        Cleanup(s);
    }

    private static void Cleanup(Session s)
    {
        if (s == null) return;
        try { if (s.Rt != null) { s.Rt.Release(); UnityEngine.Object.Destroy(s.Rt); } } catch { }
        try { if (s.Tex != null) UnityEngine.Object.Destroy(s.Tex); } catch { }
        try { if (s.OwnsCam && s.CamGo != null) UnityEngine.Object.Destroy(s.CamGo); } catch { }
        try
        {
            // Transactional: a clip that produced nothing leaves no folder behind.
            if (s.Written == 0 && s.Dir != null && Directory.Exists(s.Dir) && Directory.GetFileSystemEntries(s.Dir).Length == 0)
                Directory.Delete(s.Dir);
        }
        catch { }
        s.Rt = null; s.Tex = null; s.Cam = null; s.CamGo = null; s.BodyRenderers = null;
    }

    // ---- showcase view -------------------------------------------------------------------

    private static bool CreateShowcase(IHostContext log, Session s, Camera mainCam, int baseMask)
    {
        var body = FindBody(log, out var owner);
        if (body == null)
        {
            log.LogError("[clip] showcase: local third-person body not found (not in the Lobby yet?); not recording");
            return false;
        }
        s.Body = body.transform;

        var renderers = new List<Renderer>();
        try
        {
            var arr = body.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < arr.Length; i++)
            {
                var r = arr[i].TryCast<Renderer>();
                if (r != null) renderers.Add(r);
            }
        }
        catch (Exception e) { log.LogWarning($"[clip] body renderer scan failed: {e.Message}"); }
        s.BodyRenderers = renderers;

        var bounds = BodyBounds(log, body, renderers);
        float radius = bounds.extents.magnitude;
        float dist = s.Dist > 0f ? s.Dist : Clamp(radius / Mathf.Sin(0.5f * s.Fov * Mathf.Deg2Rad) * 1.05f, 0.5f, 20f);

        // Orbit direction: an explicit yaw in world degrees, or "yaw=auto" for the body's own
        // forward axis - the only way to get a face-on shot without knowing the lobby rotation.
        Vector3 planar;
        if (s.YawAuto)
        {
            var fwd = body.transform.forward;
            planar = new Vector3(fwd.x, 0f, fwd.z);
        }
        else
        {
            float yawRad = s.Yaw * Mathf.Deg2Rad;
            planar = new Vector3(Mathf.Cos(yawRad), 0f, Mathf.Sin(yawRad));
        }
        if (planar.sqrMagnitude < 1e-6f) planar = Vector3.forward;
        planar = planar.normalized;

        // Look point: the body default keeps the calibrated showcase framing; look=head uses the
        // head bone as the anchor too, which makes dist/lift frame a head shot instead of a body.
        Vector3 look;
        Vector3 anchor;
        Transform headBone = null;
        if (s.Look == "head")
        {
            headBone = HeadBoneOf(log, body);
            if (headBone != null) { look = headBone.position + s.LookOffset; anchor = look; }
            else
            {
                anchor = bounds.center + Vector3.up * (bounds.extents.y * 0.85f);
                look = anchor + s.LookOffset;
                log.LogWarning("[clip] look=head: head bone unavailable; anchoring on the body's upper bounds");
            }
        }
        else
        {
            look = bounds.center + Vector3.up * (bounds.extents.y * 0.15f) + s.LookOffset;
            anchor = bounds.center;
        }

        Vector3 eye = anchor + planar * dist + Vector3.up * (s.Lift * dist);
        if (s.PosePinned)
        {
            eye = s.PinnedEye;
            look = s.PinnedLook;
            log.LogInfo($"[clip] showcase pose pinned eye={Fmt(eye)} look={Fmt(look)} (camera pose does not derive from body bounds)");
        }
        if (s.Look == "head" && !s.PosePinned) log.LogInfo($"[clip] showcase look=head bone='{headBone?.name}' world={Fmt(headBone != null ? headBone.position : look)} offset={Fmt(s.LookOffset)} anchor=head");

        s.Mask = baseMask;
        var added = new List<int>();
        AddLayer(ref s.Mask, BodyLayer, added);
        AddLayer(ref s.Mask, CosmeticLayer, added);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            int layer = r.gameObject.layer;
            if (layer >= 0 && layer < 32) AddLayer(ref s.Mask, layer, added);
        }

        var go = new GameObject("AlterumClipCam");
        s.CamGo = go;
        go.hideFlags = HideFlags.HideAndDontSave;
        var cam = go.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
        if (cam == null) throw new InvalidOperationException("camera component creation failed");
        s.Cam = cam;
        s.OwnsCam = true;

        if (mainCam != null) cam.CopyFrom(mainCam);   // clipping/rendering settings only; overridden below
        cam.enabled = false;                          // manual Render only, the game never uses this camera
        cam.targetTexture = null;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.orthographic = false;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        cam.depth = 0;
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 1000f;
        cam.fieldOfView = s.Fov;
        cam.cullingMask = s.Mask;
        cam.aspect = (float)s.Width / s.Height;
        go.transform.position = eye;
        go.transform.rotation = Quaternion.LookRotation((look - eye).sqrMagnitude > 1e-8f ? (look - eye).normalized : Vector3.forward, Vector3.up);

        string addedText = "";
        foreach (var l in added) addedText += $" {l}('{LayerMask.LayerToName(l)}')";
        log.LogInfo($"[clip] mask showcase main=0x{baseMask:X8} added=[{addedText.Trim()}] final=0x{s.Mask:X8} " +
                    $"bodyLayers=[{LayersOf(renderers)}]");
        log.LogInfo($"[clip] showcase owner='{PathOf(owner)}' body='{PathOf(body.transform)}' renderers={renderers.Count} " +
                    $"center={bounds.center} size={bounds.size} dist={Inv(dist)} yaw={(s.YawAuto ? "auto" : Inv(s.Yaw))} " +
                    $"lift={Inv(s.Lift)} fov={Inv(s.Fov)} look={s.Look} lookOffset={Fmt(s.LookOffset)} bodyForward={body.transform.forward} eye={Fmt(eye)} look={Fmt(look)}");

        // A camera inside a wall renders a black frame; say so now rather than at review time.
        try
        {
            var ray = eye - look;
            var hits = Physics.RaycastAll(look, ray.normalized, Mathf.Max(0.01f, ray.magnitude));
            var names = new List<string>();
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null || Under(c.transform, s.Body)) continue;
                if (names.Count < 4) names.Add($"'{c.name}'@{Inv(hits[i].distance)}m");
            }
            if (names.Count > 0) log.LogWarning($"[clip] showcase line of sight crosses geometry (expect an occluded shot): {string.Join(", ", names)}");
            else log.LogInfo("[clip] showcase line of sight is clear");
        }
        catch (Exception e) { log.LogWarning($"[clip] line-of-sight probe failed: {e.Message}"); }
        return true;
    }

    // Diagnostic for view=main: the scene can hold several enabled cameras (menu, lobby, player,
    // offscreen), and the one that shows the player is not necessarily Camera.main. One line, so a
    // capture records what each candidate was looking at.
    private static void LogCameraCensus(IHostContext log, Camera chosen)
    {
        try
        {
            var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
            var sb = new StringBuilder();
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null || !go.activeInHierarchy || !c.enabled || c.targetTexture != null) continue;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{PathOf(c.transform)} pos={Fmt(c.transform.position)} rot={Fmt(c.transform.eulerAngles)} " +
                          $"fov={Inv(c.fieldOfView)} depth={c.depth} mask=0x{c.cullingMask:X8} {(c == chosen ? "<-chosen" : "")}");
            }
            log.LogInfo($"[clip] camera census: {sb}");
        }
        catch (Exception e) { log.LogWarning($"[clip] camera census failed: {e.Message}"); }
    }

    // The head bone, when the body exposes it. Used by look=head so the shot can be centred on the
    // head/hat instead of the body centre (the showcase default look point is fixed at
    // body-centre + 0.15*extents, which cannot reach a tight head framing).
    private static Transform HeadBoneOf(IHostContext log, ThirdViewPlayerModel body)
    {
        try
        {
            var bones = body.Bones;
            if (bones != null && bones.Head != null) return bones.Head;
        }
        catch (Exception e) { log.LogWarning($"[clip] head bone lookup failed: {e.Message}"); }
        return null;
    }

    // Same rule as ModelSwapRuntime: CurrentPlayerProvider first, then the owned Player, then the
    // body models living under a root named "Owner: 0".
    private static ThirdViewPlayerModel FindBody(IHostContext log, out Transform owner)
    {
        owner = null;
        try
        {
            var provs = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
            for (int i = 0; i < provs.Length; i++)
            {
                var prov = provs[i].TryCast<CurrentPlayerProvider>();
                if (prov == null) continue;
                var player = prov.CurrentPlayer;
                if (player == null) continue;
                var tr = player.transform;
                if (tr != null) { log.LogInfo($"[clip] local player from CurrentPlayerProvider: '{PathOf(tr)}'"); owner = tr; break; }
            }
        }
        catch (Exception e) { log.LogWarning($"[clip] CurrentPlayerProvider failed: {e.Message}"); }

        if (owner == null)
        {
            try
            {
                var players = SceneCache.Scan(Il2CppType.Of<Player>());
                for (int i = 0; i < players.Length; i++)
                {
                    var player = players[i].TryCast<Player>();
                    if (player == null || !player.IsOwner || string.IsNullOrEmpty(player.gameObject.scene.name)) continue;
                    owner = player.transform;
                    log.LogInfo($"[clip] local player by Netcode IsOwner: '{PathOf(owner)}'");
                    break;
                }
            }
            catch (Exception e) { log.LogWarning($"[clip] Player.IsOwner scan failed: {e.Message}"); }
        }

        if (owner != null)
        {
            try
            {
                var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
                ThirdViewPlayerModel inactive = null;
                for (int i = 0; i < models.Length; i++)
                {
                    var m = models[i].TryCast<ThirdViewPlayerModel>();
                    if (m == null) continue;
                    var go = m.gameObject;
                    if (go == null || !Under(go.transform, owner)) continue;
                    if (go.activeInHierarchy) return m;
                    if (inactive == null) inactive = m;
                }
                if (inactive != null)
                    log.LogWarning($"[clip] body '{PathOf(inactive.transform)}' is inactive; not force-activating game state");
            }
            catch (Exception e) { log.LogWarning($"[clip] ThirdViewPlayerModel scan failed: {e.Message}"); }
            return null;
        }

        // Fallback shared with ModelSwapRuntime: the owned player root is named "Owner: 0".
        // Find it through the (small) body-model array instead of scanning every Transform.
        try
        {
            var models = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            for (int i = 0; i < models.Length; i++)
            {
                var m = models[i].TryCast<ThirdViewPlayerModel>();
                if (m == null) continue;
                var go = m.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (PathOf(go.transform).IndexOf("Owner: 0", StringComparison.OrdinalIgnoreCase) < 0) continue;
                owner = go.transform;
                log.LogInfo($"[clip] local body by 'Owner: 0': '{PathOf(owner)}'");
                return m;
            }
        }
        catch (Exception e) { log.LogWarning($"[clip] 'Owner: 0' scan failed: {e.Message}"); }
        return null;
    }

    private static Bounds BodyBounds(IHostContext log, ThirdViewPlayerModel body, List<Renderer> renderers)
    {
        Bounds total = new Bounds(body.transform.position, Vector3.zero);
        bool any = false;
        int used = 0;
        for (int i = 0; i < renderers.Count; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            Bounds rb;
            try { rb = r.bounds; } catch { continue; }
            if (rb.size.sqrMagnitude <= 1e-8f) continue;
            if (!any) { total = rb; any = true; } else total.Encapsulate(rb);
            used++;
        }
        if (!any)
        {
            log.LogWarning($"[clip] no usable renderer bounds on '{PathOf(body.transform)}'; using a 1.8m fallback box");
            total = new Bounds(body.transform.position + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
        }
        return total;
    }

    // The body layer can change when the mirror picks a different free layer; keep the showcase
    // mask inclusive instead of going blind mid-clip.
    private static void RefreshMask(IHostContext log, Session s)
    {
        if (s.Cam == null || s.BodyRenderers == null) return;
        int mask = s.Cam.cullingMask;
        var added = new List<int>();
        foreach (var r in s.BodyRenderers)
        {
            if (r == null) continue;
            int layer = r.gameObject.layer;
            if (layer < 0 || layer >= 32) continue;
            AddLayer(ref mask, layer, added);
        }
        if (added.Count == 0) return;
        s.Cam.cullingMask = mask;
        s.Mask = mask;
        log.LogInfo($"[clip] mask widened mid-clip to 0x{mask:X8} (body moved to layer(s) {string.Join(",", added)})");
    }

    private static void AddLayer(ref int mask, int layer, List<int> added)
    {
        if (layer < 0 || layer >= 32) return;
        int bit = 1 << layer;
        if ((mask & bit) != 0) return;
        mask |= bit;
        if (!added.Contains(layer)) added.Add(layer);
    }

    private static string LayersOf(List<Renderer> renderers)
    {
        var seen = new List<int>();
        foreach (var r in renderers)
        {
            if (r == null) continue;
            int layer = r.gameObject.layer;
            if (!seen.Contains(layer)) seen.Add(layer);
        }
        seen.Sort();
        return string.Join(",", seen);
    }

    // ---- triggers ------------------------------------------------------------------------

    private static void PollRequestFile(IHostContext log)
    {
        if (_elapsed < _nextRequestPoll) return;
        _nextRequestPoll = _elapsed + RequestPollSeconds;

        string request = null;
        try
        {
            request = RequestPath(log);
            if (!File.Exists(request)) return;
            string text = "";
            try { text = File.ReadAllText(request); } catch (Exception e) { log.LogWarning($"[clip] request read failed: {e.Message}"); }
            try { File.Delete(request); } catch { }
            var spec = Parse(log, text);
            Record(log, spec);
        }
        catch (Exception e)
        {
            log.LogWarning($"[clip] request file '{request}' failed: {e.Message}");
            try { if (request != null && File.Exists(request)) File.Delete(request); } catch { }
        }
    }

    private static ClipSpec Parse(IHostContext log, string text)
    {
        var spec = ClipSpec.Default("clip");
        if (string.IsNullOrEmpty(text)) return spec;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { log.LogWarning($"[clip] request line {i + 1} ignored (not key=value): '{line}'"); continue; }
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "tag": spec.Tag = val; break;
                case "view": spec.View = val; break;
                case "seconds": if (!TryFloat(val, out spec.Seconds)) BadValue(log, key, val); break;
                case "fps": if (!TryFloat(val, out spec.Fps)) BadValue(log, key, val); break;
                case "yaw":
                    if (val.Equals("auto", StringComparison.OrdinalIgnoreCase) || val.Equals("front", StringComparison.OrdinalIgnoreCase)) spec.YawAuto = true;
                    else if (!TryFloat(val, out spec.Yaw)) BadValue(log, key, val);
                    break;
                case "dist": if (!TryFloat(val, out spec.Dist)) BadValue(log, key, val); break;
                case "fov": if (!TryFloat(val, out spec.Fov)) BadValue(log, key, val); break;
                case "lift": if (!TryFloat(val, out spec.Lift)) BadValue(log, key, val); break;
                case "look":
                {
                    string lv = val.Trim().ToLowerInvariant();
                    if (lv == "head" || lv == "body") spec.Look = lv;
                    else log.LogWarning($"[clip] request look='{val}' (head|body); default body kept");
                    break;
                }
                case "lookoffset":
                    if (!TryVec3(val, out spec.LookOffset)) log.LogWarning($"[clip] request lookoffset='{val}' is not x,y,z; ignored");
                    break;
                case "eye":
                    if (TryVec3(val, out spec.Eye)) spec.HasEye = true;
                    else log.LogWarning($"[clip] request eye='{val}' is not x,y,z; ignored");
                    break;
                case "lookat":
                    if (TryVec3(val, out spec.LookAt)) spec.HasLookAt = true;
                    else log.LogWarning($"[clip] request lookat='{val}' is not x,y,z; ignored");
                    break;
                case "width": if (!TryInt(val, out spec.Width)) BadValue(log, key, val); break;
                case "height": if (!TryInt(val, out spec.Height)) BadValue(log, key, val); break;
                default: log.LogWarning($"[clip] request key '{key}' unknown; ignored"); break;
            }
        }
        return spec;
    }

    private static void BadValue(IHostContext log, string key, string val) =>
        log.LogWarning($"[clip] request {key}='{val}' is not a number; default kept");

    // Request values are invariant ("2.6"), never the machine's locale.
    private static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    // "x,y,z" (commas, semicolons or spaces; parentheses optional).
    private static bool TryVec3(string s, out Vector3 v)
    {
        v = Vector3.zero;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Trim().Trim('(', ')').Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        if (!TryFloat(parts[0], out float x) || !TryFloat(parts[1], out float y) || !TryFloat(parts[2], out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    // Invariant, fixed-precision vector for log greps (Vector3.ToString is culture-dependent).
    private static string Fmt(Vector3 v) => $"({Inv(v.x)},{Inv(v.y)},{Inv(v.z)})";

    private static string RequestPath(IHostContext log) => Path.Combine(log.ScreensDir, RequestFileName);

    private static int DeleteStaleFrames(string dir)
    {
        int removed = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir, "frame_*.png")) { File.Delete(file); removed++; }
        }
        catch { }
        return removed;
    }

    // ---- helpers -------------------------------------------------------------------------

    // Alterum has several enabled cameras tagged MainCamera (menu + lobby) plus offscreen ones.
    // Score by what is actually being shown; never trust Camera.main.
    private static Camera FindGameCamera(IHostContext log)
    {
        Camera best = null;
        int bestScore = int.MinValue;
        try
        {
            var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
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
        catch (Exception e) { log?.LogWarning($"[clip] camera scan failed: {e.Message}"); }
        return best;
    }

    private static bool Under(Transform child, Transform parent)
    {
        for (int i = 0; child != null && i < 64; i++, child = child.parent)
            if (child == parent) return true;
        return false;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
        {
            char ch = s[i];
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }
        return sb.ToString();
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    // Log-friendly number: invariant so "2.6" never becomes "2,6" on a localised machine.
    private static string Inv(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
