using System;
using System.Globalization;
using System.IO;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Pixels for the third-person screen-camera body view. It renders one frame through a throwaway
// camera that copies the live game camera's own settings - same culling mask (that is the whole
// point: the mask the game is really using right now), same fov, near/far, clear flags - from a
// pose the automation can pin, because the lobby camera sits at the owner's eyes and an unattended
// run cannot turn that camera.
//
// "<ScreensDir>/screenproof.request", deleted on pickup, line-oriented:
//   tag=third-person-body      output name (ScreenShot's usual file naming)
//   source=game|mirror         whose mask/fov/clip planes to copy: the game (screen) camera, or the
//                              mod's own mirror camera ("AlterumMirrorCam", which is disabled and is
//                              therefore not found by the screen-camera scan). Default: game.
//   bit=game|off               "game" (default): the source camera's live mask. "off": the same mask
//                              with the isolated body layer removed - the pre-change view, so the
//                              pair differs by exactly the body's pixels.
//   pair=1|live|bit            write both mask variants from one request, in ONE frame (two Render()
//                              calls back to back, same pose, same animation state), into
//                              "<ScreensDir>/bodyproof/<tag>_on.png" (body bit PRESENT) and
//                              "_off.png" (body bit ABSENT). "live" (default) keeps the rest of the
//                              game camera's mask as it is; "bit" forces the body bit into the on-image,
//                              which is how a first-person frame shows what its mask leaves out. A
//                              same-frame pair is the only diff that isolates the mask: two requests
//                              seconds apart also differ by the idle animation.
//   pitch=-38                  degrees added to the game camera's own rotation (negative = down)
//   eye=x,y,z / lookat=x,y,z   world pose; the game camera's transform when absent
//   layer=14                   the body layer removed by bit=off (default: the body layer)
internal static class BodyScreenProof
{
    private const string RequestFileName = "screenproof.request";
    private const float PollSeconds = 0.5f;

    private static float _nextPoll;

    internal static void Tick(IHostContext log, int bodyLayer, Func<Camera> gameCamera)
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestFileName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"[bodyview:proof] read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }
        Capture(log, bodyLayer, gameCamera, text);
    }

    private static void Capture(IHostContext log, int bodyLayer, Func<Camera> gameCamera, string text)
    {
        string tag = "screenproof";
        bool bitOff = false, pair = false, pairForce = false;
        bool mirrorSource = false;
        float pitch = 0f;
        Vector3? eye = null, lookAt = null;
        int layer = bodyLayer;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "tag": tag = val; break;
                case "bit": bitOff = val.Equals("off", StringComparison.OrdinalIgnoreCase) || val == "0"; break;
                case "pair":
                    pair = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                    pairForce = val.Equals("bit", StringComparison.OrdinalIgnoreCase);
                    break;
                case "pitch": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out pitch); break;
                case "source": mirrorSource = val.StartsWith("mirror", StringComparison.OrdinalIgnoreCase); break;
                case "eye": eye = Vec(val); break;
                case "lookat": lookAt = Vec(val); break;
                case "layer": int.TryParse(val, out layer); break;
                default: log.LogWarning($"[bodyview:proof] unknown key '{key}' ignored"); break;
            }
        }

        GameObject go = null;
        try
        {
            var game = mirrorSource ? FindMirrorCamera() : gameCamera?.Invoke();
            if (game == null)
            {
                log.LogWarning(mirrorSource
                    ? "[bodyview:proof] no mirror camera named 'AlterumMirrorCam' (mirror not placed?); nothing captured"
                    : "[bodyview:proof] no screen camera to copy (menu/loading?); nothing captured");
                return;
            }
            int liveMask;
            try { liveMask = game.cullingMask; } catch (Exception e) { log.LogWarning($"[bodyview:proof] camera mask unreadable: {e.Message}"); return; }
            int mask = liveMask;
            if (bitOff && layer >= 0 && layer < 32) mask &= ~(1 << layer);

            go = new GameObject(MirrorPlayerVisibility.ProofCameraName);
            var cam = go.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) { log.LogWarning("[bodyview:proof] camera creation failed"); return; }
            cam.CopyFrom(game);          // fov/near/far/clear flags/rendering path - and the transform
            cam.enabled = false;         // ScreenShot renders it explicitly; nothing else may pick it up
            cam.targetTexture = null;
            cam.cullingMask = mask;
            cam.depth = -100;
            // Pose last: Camera.CopyFrom copies the source transform too, so eye/lookat must win.
            go.transform.position = eye ?? game.transform.position;
            go.transform.rotation = lookAt.HasValue
                ? Quaternion.LookRotation((lookAt.Value - go.transform.position).normalized, Vector3.up)
                : game.transform.rotation * Quaternion.Euler(pitch, 0f, 0f);

            log.LogInfo($"[bodyview:proof] tag='{tag}' source='{MirrorPlayerVisibility.Hierarchy(game.transform)}' " +
                        $"liveMask=0x{liveMask:X8} usedMask=0x{mask:X8} bit={((liveMask & (1 << layer)) != 0 ? "present" : "absent")} " +
                        $"layer={layer} bitOff={bitOff} eye={Fmt(go.transform.position)} rot={Fmt(go.transform.eulerAngles)} " +
                        $"fov={cam.fieldOfView:F2} near={cam.nearClipPlane} far={cam.farClipPlane} " +
                        $"pixels={cam.pixelWidth}x{cam.pixelHeight}");
            string file = pair ? CapturePair(log, cam, tag, liveMask, layer, pairForce) : ScreenShot.Capture(log, tag, cam);
            log.LogInfo($"[bodyview:proof] {tag} -> {(file ?? "<failed>")}");
        }
        catch (Exception e) { log.LogError($"[bodyview:proof] failed: {e}"); }
        finally
        {
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
        }
    }

    private static Camera FindMirrorCamera()
    {
        try
        {
            var cams = SceneCache.Scan(Il2CppType.Of<Camera>());
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i].TryCast<Camera>();
                if (c != null && c.name == "AlterumMirrorCam") return c;
            }
        }
        catch { }
        return null;
    }

    // Two renders of one camera in one frame: the live mask, then the same mask without the body
    // layer. Same pose, same animator pose, same lighting - so the pixel difference is the mask's
    // effect, nothing else.
    private static string CapturePair(IHostContext log, Camera cam, string tag, int liveMask, int layer, bool forceBit)
    {
        RenderTexture rt = null;
        Texture2D tex = null;
        var previous = RenderTexture.active;
        try
        {
            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (w <= 0 || h <= 0) { log.LogWarning($"[bodyview:proof] pair: camera has no pixel size ({w}x{h})"); return null; }
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "AlterumBodyProofRT", antiAliasing = 1 };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            Directory.CreateDirectory(Path.Combine(log.ScreensDir, "bodyproof"));
            string on = Path.Combine(log.ScreensDir, "bodyproof", tag + "_on.png");
            string off = Path.Combine(log.ScreensDir, "bodyproof", tag + "_off.png");
            var previousTarget = cam.targetTexture;
            int bit = layer >= 0 && layer < 32 ? 1 << layer : 0;
            int onMask = forceBit ? liveMask | bit : liveMask;
            int offMask = liveMask & ~bit;
            try
            {
                cam.targetTexture = rt;
                cam.cullingMask = onMask;
                cam.Render();
                Save(rt, tex, on);
                cam.cullingMask = offMask;
                cam.Render();
                Save(rt, tex, off);
            }
            finally { cam.targetTexture = previousTarget; cam.cullingMask = liveMask; }
            log.LogInfo($"[bodyview:proof] pair tag='{tag}' {w}x{h} masks on=0x{onMask:X8} off=0x{offMask:X8} " +
                        $"forcedBit={forceBit} -> {on} | {off}");
            return on;
        }
        catch (Exception e) { log.LogError($"[bodyview:proof] pair failed: {e}"); return null; }
        finally
        {
            RenderTexture.active = previous;
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
        }
    }

    private static void Save(RenderTexture rt, Texture2D tex, string path)
    {
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        var png = ImageConversion.EncodeToPNG(tex);
        var bytes = new byte[png.Length];
        for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
        File.WriteAllBytes(path, bytes);
    }

    private static Vector3? Vec(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 3) return null;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return null;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return null;
        return new Vector3(x, y, z);
    }

    private static string Fmt(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
}
