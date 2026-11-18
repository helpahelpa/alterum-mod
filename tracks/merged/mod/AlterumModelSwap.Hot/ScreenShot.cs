using System;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Offscreen capture of the game's rendered 3D view. Self-contained: drop this file into any
// hot-assembly copy and call ScreenShot.Tick(host, dt) once per frame from the module's
// Update; individual captures also go through ScreenShot.Capture(...).
//
// It never uses ScreenCapture.CaptureScreenshot (it has correlated with the process dying) and
// never reads the desktop, so it keeps working while the Windows desktop is locked: it renders
// the game camera into a private RenderTexture and copies the pixels back.
//
// Trigger, config-free and keyboard-free:
//   * a request file: write a tag (or any text) into "<ScreensDir>/shot.request"; the next poll
//     captures and deletes it. This is the only path and it needs no input device, so it works
//     with the desktop locked. The F10 row was removed with the controls prune: a capture is never
//     triggered by a key the overlay or the game also wants.
public static class ScreenShot
{
    // Written into ScreensDir by anything that wants a capture; contents become the tag.
    private const string RequestFileName = "shot.request";
    private const float RequestPollSeconds = 0.5f;
    private const float MinSecondsBetweenCaptures = 0.25f;
    // Keeps a capture from allocating an absurd RT if some camera reports a huge viewport.
    private const int MaxWidth = 2560, MaxHeight = 1600;

    private static float _elapsed;
    private static float _nextRequestPoll;
    private static float _lastCaptureAt = float.NegativeInfinity;
    private static int _captures;
    private static bool _announced;

    // Frame hook: the request file (and nothing else). Safe to call every frame; never throws.
    public static void Tick(IHostContext log, float unscaledDeltaTime)
    {
        if (log == null) return;
        // The mod's own UI work (shortcut registry + native Controls rows + the ESC menu section +
        // the menu proof rig) is ticked from HotModule.Update, not from here: the registry has to
        // run before the per-feature key pollers inside ModelSwapRuntime.Update, and it must keep
        // running even when the screenshot path below is skipped or throws.
        _elapsed += unscaledDeltaTime;
        if (!_announced)
        {
            _announced = true;
            try
            {
                log.LogInfo($"[shot] ready trigger=none (request file only) request={RequestPath(log)}");
            }
            catch { }
        }
        try { PollRequestFile(log); }
        catch (Exception e) { log.LogWarning($"[shot] request poll failed: {e.Message}"); }
    }

    // Capture the camera that is actually showing the game view.
    public static string Capture(IHostContext log, string tag)
        => Capture(log, tag, FindGameCamera(log));

    // Capture an explicit camera. Returns the PNG path, or null if nothing was written.
    public static string Capture(IHostContext log, string tag, Camera cam)
        => Capture(log, tag, cam, out _);

    // The same capture, reporting WHY it produced no artifact. One implementation, no second
    // renderer: `why` is null only when a PNG path was returned, and otherwise names the condition
    // (throttled / no game camera / camera without a pixel size / RenderTexture.Create failed /
    // exception), so a caller that has to answer a human or a test can quote it instead of guessing.
    public static string Capture(IHostContext log, string tag, Camera cam, out string why)
    {
        why = null;
        // Throttle so a burst of triggers cannot allocate several full-res RTs in one frame.
        if (Time.unscaledTime - _lastCaptureAt < MinSecondsBetweenCaptures)
        {
            why = $"throttled (at most one capture per {MinSecondsBetweenCaptures:0.00}s of unscaled time)";
            return null;
        }
        _lastCaptureAt = Time.unscaledTime;

        RenderTexture previousActive = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        RenderTexture previousTarget = null;
        bool targetSwapped = false, activeSaved = false;
        string tmp = null;
        try
        {
            // Save before the early returns: the finally restores this even when no capture ran, so
            // a refused capture cannot clear a render target some other code left active.
            previousActive = RenderTexture.active;
            activeSaved = true;
            if (log == null) { why = "no host context"; return null; }
            if (cam == null) { why = "no game camera"; log.LogWarning($"[shot] no game camera for tag '{tag}'"); return null; }

            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (w <= 0 || h <= 0)
            {
                why = $"camera '{cam.name}' has no pixel size ({w}x{h})";
                log.LogWarning($"[shot] camera '{cam.name}' has no pixel size ({w}x{h})");
                return null;
            }
            if (w > MaxWidth) { h = Mathf.Max(1, h * MaxWidth / w); w = MaxWidth; }
            if (h > MaxHeight) { w = Mathf.Max(1, w * MaxHeight / h); h = MaxHeight; }

            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                name = "AlterumShotRT", antiAliasing = 1, useMipMap = false,
                autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");

            // Render the real camera into our own target. Its own target is restored immediately
            // after, so the game keeps drawing to the screen this frame.
            previousTarget = cam.targetTexture;
            targetSwapped = true;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previousTarget;
            targetSwapped = false;

            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];

            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, FileName(log, tag));
            // A temp sibling plus one rename: a failed write can leave a partial .tmp, never a
            // partial artifact at the returned path.
            tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path);

            try { log.LogInfo($"[shot] {path} {w}x{h} camera={PathOf(cam.transform)}"); } catch { }
            return path;
        }
        catch (Exception e)
        {
            try { why = $"capture threw {e.GetType().Name}: {e.Message}"; } catch { }
            try { log?.LogError($"[shot] capture failed (tag='{tag}'): {e}"); } catch { }
            return null;
        }
        finally
        {
            try { if (targetSwapped && cam != null) cam.targetTexture = previousTarget; } catch { }
            try { if (activeSaved) RenderTexture.active = previousActive; } catch { }
            try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
        }
    }

    private static void PollRequestFile(IHostContext log)
    {
        if (_elapsed < _nextRequestPoll) return;
        _nextRequestPoll = _elapsed + RequestPollSeconds;

        string request = null;
        try
        {
            request = RequestPath(log);
            if (!File.Exists(request)) return;
            string tag = "";
            try { tag = File.ReadAllText(request).Trim(); } catch { }
            try { File.Delete(request); } catch { }
            if (tag.Length == 0) tag = "request";
            Capture(log, tag);
        }
        catch (Exception e)
        {
            log.LogWarning($"[shot] request file '{request}' failed: {e.Message}");
            try { if (request != null && File.Exists(request)) File.Delete(request); } catch { }
        }
    }

    private static string RequestPath(IHostContext log) => Path.Combine(log.ScreensDir, RequestFileName);

    // Alterum can have several enabled cameras tagged MainCamera (menu + lobby), plus the
    // mirror's offscreen camera. Score them by what they are actually showing instead of
    // trusting Camera.main. Prefab/asset cameras (no scene) and cameras that already render
    // into a RenderTexture are skipped.
    public static Camera FindGameCamera(IHostContext log)
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

                string path = PathOf(go.transform);
                int score = 0;
                if (path.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
                if (path.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) score -= 20;
                if (go.name.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                score += (int)c.depth;
                if (score > bestScore) { bestScore = score; best = c; }
            }
        }
        catch (Exception e)
        {
            try { log?.LogWarning($"[shot] camera scan failed: {e.Message}"); } catch { }
        }
        return best;
    }

    private static string FileName(IHostContext log, string tag)
    {
        string instance = log.InstanceName;
        if (string.IsNullOrEmpty(instance)) instance = "main";
        instance = Sanitize(instance);
        string name = Sanitize(tag);
        if (name.Length == 0) name = "shot";
        _captures++;
        // Timestamp to the millisecond plus a per-session counter: two captures in the same
        // millisecond (or after a hot reload) still cannot overwrite each other.
        return $"shot_{instance}_{name}_{DateTime.Now:yyyyMMdd-HHmmss-fff}_{_captures:00}.png";
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

    private static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }
}
