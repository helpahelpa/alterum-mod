using System;
using System.Globalization;
using System.IO;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Free-camera proof frame, for evidence that does not depend on where a player happens to be
// looking. Same idea as BodyScreenProof (`screenproof.request`) - a throwaway camera that copies
// the live game camera's settings and renders one frame - with two differences that the map-side
// verification needs:
//
//   * it is not driven from MirrorPlayerVisibility, so it works in ANY scene. `screenproof` lives
//     inside the mirror module, which is built in the lobby only, so it cannot photograph a body in
//     a map; the respawn and hat-seat evidence both happen there.
//   * it takes the pose and the layer set from the request alone, and can force a layer INTO the
//     game camera's own mask (the hat/body layer 23 is not in the shipped mask, so a frame that
//     copies the mask verbatim would not show what the mod draws).
//
// Requests are line-oriented, "<ScreensDir>/proofcam.request", polled twice a second and deleted on
// pickup. Every key is optional except eye/lookat:
//   tag=before                 output name (ScreenShot naming: shot_<instance>_<tag>_<ts>_NN.png)
//   eye=-24.4,6.2,10.9         camera position, world space
//   lookat=-24.4,6.1,8.7       what the camera points at, world space
//   fov=45                     override field of view (default: the game camera's own)
//   layer=23                   force this layer into the mask (also layer2=, layer3=)
//   off=14                     remove this layer from the mask
//   source=game|mirror         whose mask/fov/clip planes to copy (default: the game camera)
//   pair=1                     write "<tag>_on.png" (masked) and "<tag>_off.png" (mask, no forced
//                              layers) into screens/bodyproof/, one frame apart, for a diff
internal static class ProofCam
{
    internal const string RequestName = "proofcam.request";
    private const float PollSeconds = 0.5f;

    private static float _nextPoll;

    internal static void Tick(IHostContext log)
    {
        if (log == null) return;
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestName); } catch { return; }
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path); }
        catch (Exception e) { log.LogWarning($"[proofcam] read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }
        try { Capture(log, text); }
        catch (Exception e) { log.LogError($"[proofcam] capture failed: {e}"); }
    }

    private static void Capture(IHostContext log, string text)
    {
        string tag = "proofcam";
        bool mirrorSource = false;
        float? fov = null;
        Vector3? eye = null, lookAt = null;
        int force = 0, remove = 0;
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
                case "eye": eye = Vec(val); break;
                case "lookat": case "look": lookAt = Vec(val); break;
                case "fov":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 1f && f < 170f) fov = f;
                    break;
                case "layer": case "layer2": case "layer3":
                    if (int.TryParse(val, out var l) && l >= 0 && l < 32) force |= 1 << l;
                    break;
                case "off":
                    if (int.TryParse(val, out var o) && o >= 0 && o < 32) remove |= 1 << o;
                    break;
                case "source": mirrorSource = val.StartsWith("mirror", StringComparison.OrdinalIgnoreCase); break;
                default: log.LogWarning($"[proofcam] unknown key '{key}' ignored"); break;
            }
        }
        if (!eye.HasValue || !lookAt.HasValue)
        {
            log.LogWarning("[proofcam] needs eye=x,y,z and lookat=x,y,z");
            return;
        }

        var source = mirrorSource ? FindMirrorCamera() : ScreenShot.FindGameCamera(log);
        if (source == null)
        {
            log.LogWarning(mirrorSource
                ? "[proofcam] no mirror camera named 'AlterumMirrorCam' in this scene"
                : "[proofcam] no game camera to copy (menu/loading?)");
            return;
        }

        GameObject go = null;
        try
        {
            int liveMask;
            try { liveMask = source.cullingMask; }
            catch (Exception e) { log.LogWarning($"[proofcam] camera mask unreadable: {e.Message}"); return; }
            int mask = (liveMask | force) & ~remove;

            go = new GameObject("AlterumProofCam");
            var cam = go.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) { log.LogWarning("[proofcam] camera creation failed"); return; }
            cam.CopyFrom(source);        // fov/near/far/clear flags/rendering path - and the transform
            cam.enabled = false;         // ScreenShot renders it explicitly; nothing else may pick it up
            cam.targetTexture = null;
            cam.cullingMask = mask;
            cam.depth = -100;
            if (fov.HasValue) cam.fieldOfView = fov.Value;
            // Pose last: CopyFrom copies the source transform too.
            go.transform.position = eye.Value;
            go.transform.rotation = Quaternion.LookRotation((lookAt.Value - eye.Value).normalized, Vector3.up);

            log.LogInfo($"[proofcam] tag='{tag}' source='{ClothInternals.PathOf(source.transform)}' " +
                        $"liveMask=0x{liveMask:X8} usedMask=0x{mask:X8} forced=0x{force:X8} off=0x{remove:X8} " +
                        $"eye={Fmt(eye.Value)} lookat={Fmt(lookAt.Value)} fov={cam.fieldOfView:F2} " +
                        $"pixels={cam.pixelWidth}x{cam.pixelHeight}");
            string file = ScreenShot.Capture(log, tag, cam);
            log.LogInfo($"[proofcam] {tag} -> {(file ?? "<failed>")}");
        }
        catch (Exception e) { log.LogError($"[proofcam] failed: {e}"); }
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

    private static Vector3? Vec(string text)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length != 3) return null;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return null;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return null;
        return new Vector3(x, y, z);
    }

    private static string Fmt(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
}
