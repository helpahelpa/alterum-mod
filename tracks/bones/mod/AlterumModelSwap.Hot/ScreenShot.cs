using System;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AlterumModelSwap.Hot;

// Offscreen capture of the game's rendered 3D view. Self-contained: drop this file into any
// hot-assembly copy and call ScreenShot.Tick(host, dt) once per frame from the module's
// Update; individual captures also go through ScreenShot.Capture(...).
//
// It never uses ScreenCapture.CaptureScreenshot (it has correlated with the process dying) and
// never reads the desktop, so it keeps working while the Windows desktop is locked: it renders
// the game camera into a private RenderTexture and copies the pixels back.
//
// Triggers, both config-free:
//   * F10 while the game runs (or component has focus),
//   * a request file: write a tag (or any text) into "<ScreensDir>/shot.request"; the next poll
//     captures and deletes it. This is the unattended path - it needs no input device, so it
//     works with the desktop locked.
public static class ScreenShot
{
    public const Key Hotkey = Key.F10;

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
    private static bool _inputFailed;
    private static bool _inputFailureLogged;
    private static bool _announced;

    // Frame hook: hotkey + request file. Safe to call every frame; never throws.
    public static void Tick(IHostContext log, float unscaledDeltaTime)
    {
        _elapsed += unscaledDeltaTime;
        if (!_announced)
        {
            _announced = true;
            try
            {
                string keyboard = Keyboard.current != null ? "ok" : "null";
                log.LogInfo($"[shot] ready hotkey={Hotkey} keyboard={keyboard} request={RequestPath(log)}");
            }
            catch { }
        }
        try { PollHotkey(log); }
        catch (Exception e)
        {
            _inputFailed = true;
            if (!_inputFailureLogged) { _inputFailureLogged = true; log.LogWarning($"[shot] input unavailable: {e.Message}"); }
        }
        try { PollRequestFile(log); }
        catch (Exception e) { log.LogWarning($"[shot] request poll failed: {e.Message}"); }
    }

    // Capture the camera that is actually showing the game view.
    public static string Capture(IHostContext log, string tag)
        => Capture(log, tag, FindGameCamera(log));

    // Capture an explicit camera. Returns the PNG path, or null if nothing was written.
    public static string Capture(IHostContext log, string tag, Camera cam)
    {
        // Throttle so a burst of triggers cannot allocate several full-res RTs in one frame.
        if (Time.unscaledTime - _lastCaptureAt < MinSecondsBetweenCaptures) return null;
        _lastCaptureAt = Time.unscaledTime;

        RenderTexture previousActive = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        RenderTexture previousTarget = null;
        bool targetSwapped = false;
        try
        {
            if (log == null) return null;
            if (cam == null) { log.LogWarning($"[shot] no game camera for tag '{tag}'"); return null; }

            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (w <= 0 || h <= 0) { log.LogWarning($"[shot] camera '{cam.name}' has no pixel size ({w}x{h})"); return null; }
            if (w > MaxWidth) { h = Mathf.Max(1, h * MaxWidth / w); w = MaxWidth; }
            if (h > MaxHeight) { w = Mathf.Max(1, w * MaxHeight / h); h = MaxHeight; }

            previousActive = RenderTexture.active;
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                name = "AlterumShotRT", antiAliasing = 1, useMipMap = false,
                autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");

            // Render the real camera into our own target. Its own target is restored immediately
            // after, so the game keeps drawing to the screen this frame.
            previousTarget = cam.targetTexture;
            cam.targetTexture = rt;
            targetSwapped = true;
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
            File.WriteAllBytes(path, bytes);

            log.LogInfo($"[shot] {path} {w}x{h} camera={PathOf(cam.transform)}");
            return path;
        }
        catch (Exception e)
        {
            try { log?.LogError($"[shot] capture failed (tag='{tag}'): {e}"); } catch { }
            return null;
        }
        finally
        {
            try { if (targetSwapped && cam != null) cam.targetTexture = previousTarget; } catch { }
            try { RenderTexture.active = previousActive; } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
        }
    }

    private static void PollHotkey(IHostContext log)
    {
        if (_inputFailed) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        var control = kb[Hotkey];
        if (control == null || !control.wasPressedThisFrame) return;
        Capture(log, "key");
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
