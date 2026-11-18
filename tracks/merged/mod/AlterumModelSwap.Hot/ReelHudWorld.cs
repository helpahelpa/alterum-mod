using System;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// The drawn fallback for the interaction prompt and the result messages, rendered as textures on
// quads parented to the game camera.
//
// Layout is resolved in SCREEN PIXELS against the game's own inventory hotbar, then projected into
// the camera's local plane at the quad depth. That is a deliberate replacement for the old pair of
// fixed camera-local metre offsets: a metre offset at a fixed depth is a *different* screen line on
// every camera, resolution and FOV, and nothing in it knows where the hotbar is - which is how the
// banner ended up sitting on top of it.
//
//   s = min(cameraPixelWidth / 640, cameraPixelHeight / 360)      (reference pixel scale)
//   prompt bottom = hotbar top + 12 ref px
//   message bottom = prompt top + 8 ref px
//   everything clamped inside the camera viewport inset by 8 ref px
//   hotbar unresolvable -> reserve the bottom 96 ref px, prompt bottom at ref y = 264 of 360
//
// The same computed rectangle is handed to the IMGUI fallback (ScreenRect below), so the two
// paths cannot disagree. The layout is recomputed whenever the camera, its viewport, the screen
// resolution or the FOV changes.
//
// Reason for drawing it ourselves at all: the game's E prompt for a looked-at object is the
// world-space DistantInteractionPrompt owned by a LookableDistantInteractable, and the reel's
// cloned interactable normally shows that native prompt itself (ReelInteraction). This drawn path
// is used only when the native prompt cannot display, and never at the same time: the shared
// ActionPromptsController/KeyActionPrompt HUD list is NOT touched by the reel (see ReelHud).
//
// Camera-parented quads (rather than IMGUI) so the prompt and messages appear in ScreenShot/trace
// captures of the game camera, and so they survive a locked desktop (no window composition needed).
internal static class ReelHudWorld
{
    // 0.06 m: in front of first-person hands/held items, still far outside the 0.01 near plane.
    // The quad is scaled per layout so its texels land on the same physical pixels at any depth.
    private const float Depth = 0.06f;
    private const float TexelMeters = 0.000200f;
    private const int FontScale = 2;

    // Reference viewport the layout arithmetic is authored against (640x360 at 15 fps is also the
    // window both live clients run). All gaps are in these reference pixels.
    private const int RefW = 640, RefH = 360;
    private const float SafeRef = 8f;
    private const float PromptGapRef = 12f;
    private const float MessageGapRef = 8f;
    private const float ReservedBottomRef = 96f;
    // Plate box of one line, same numbers Line.Rebuild draws with (glyph rows + 12 pad).
    private const float LineHeightRef = 26f;

    private static Camera _cam;
    private static Line _prompt, _message;
    private static float _messageUntil;
    private static bool _failed;
    private static string _why = "not built";
    // The quads are children of a camera, so they die with the scene that camera came from. These
    // three fields are what makes that detectable: the generation the quads were built on, a retry
    // backoff for "no camera yet", and a count so a rebuild is visible in the log.
    private static int _sceneHandle;
    private static float _nextBuildAt;
    private static int _builds;

    internal static string Status => _why;

    // ---- layout state ------------------------------------------------------------------

    private struct Geo
    {
        internal float X, Y, W, H;      // screen pixels, bottom-left origin (y up)
        internal float Top => Y + H;
    }

    private static bool _layoutValid;
    private static int _layoutCamId = int.MinValue;
    private static Rect _layoutVp;
    private static int _layoutScreenW = -1, _layoutScreenH = -1;
    private static float _layoutFov;
    private static bool _layoutOrtho;
    private static float _layoutOrthoSize;
    private static float _s = 1f;
    private static float _halfW = 1f, _halfH = 1f;
    private static float _mPerRefPx = 0.001f;
    private static float _promptBottom, _messageBottom;
    private static string _layoutSource = "not computed";
    private static Geo _promptGeo, _messageGeo;

    // Resolved hotbar rectangle (screen px). Reset when the screen size changes or the scene goes.
    private static Rect _hotbar;
    private static bool _hotbarFound, _hotbarGaveUp;
    private static float _hotbarNextTry;
    private static int _hotbarTries;
    private static int _hotbarScreenW = -1, _hotbarScreenH = -1;
    private static string _hotbarWhy = "not probed";

    // Path tokens that name an inventory hotbar / quick-slot row. Matched case-insensitively on
    // the full hierarchy path, then validated by geometry (wide, short, in the lower half).
    private static readonly string[] HotbarTokens =
    {
        "hotbar", "hot bar", "quickbar", "quick bar", "quickslot", "quick slot",
        "slotbar", "slot bar", "itembar", "item bar", "inventorybar", "inventory bar",
    };
    private static readonly string[] SlotTokens = { "slot", "item", "icon", "cell", "weapon", "consumable" };

    // True only while a live quad exists on a live camera: OnGUI asks this every frame to decide
    // whether to draw the last-resort IMGUI line. It must not report "available" for a quad whose
    // GameObject died with the previous scene - that is exactly the state in which the message used
    // to be swallowed (a non-null managed wrapper over a destroyed object).
    internal static bool Available => !_failed && CamAlive &&
        _prompt != null && _prompt.Go != null && _message != null && _message.Go != null;

    private static bool CamAlive
    {
        get
        {
            try
            {
                return _cam != null && _cam.gameObject != null && _cam.gameObject.activeInHierarchy
                       && _cam.enabled && _cam.gameObject.scene.name != null;
            }
            catch { return false; }
        }
    }

    private sealed class Line : IDisposable
    {
        internal GameObject Go;
        internal Mesh Mesh;
        internal Material Mat;
        internal Texture2D Tex;
        internal float ShownAt;
        internal int RefW, RefH;      // canvas size in reference pixels (what the mesh was built for)
        private string _text;

        internal bool Build(string name, Camera cam, int layer)
        {
            Go = new GameObject(name);
            Go.layer = layer;
            Go.transform.SetParent(cam.transform, false);
            var mf = Go.AddComponent<MeshFilter>();
            var mr = Go.AddComponent<MeshRenderer>();
            Mesh = new Mesh { name = name + "Mesh" };
            mf.sharedMesh = Mesh;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            Mat = new Material(sh) { name = name + "Mat" };
            if (Mat.HasProperty("_Surface")) Mat.SetFloat("_Surface", 1f);
            if (Mat.HasProperty("_Blend")) Mat.SetFloat("_Blend", 0f);
            if (Mat.HasProperty("_SrcBlend")) Mat.SetFloat("_SrcBlend", 5f);
            if (Mat.HasProperty("_DstBlend")) Mat.SetFloat("_DstBlend", 10f);
            if (Mat.HasProperty("_ZWrite")) Mat.SetFloat("_ZWrite", 0f);
            if (Mat.HasProperty("_Cull")) Mat.SetFloat("_Cull", 0f);
            try { Mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
            Mat.renderQueue = 3000;
            mr.sharedMaterial = Mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Go.SetActive(false);
            return true;
        }

        // False when the object behind this wrapper is gone (the scene that owned the camera was
        // unloaded, an earlier generation disposed it). The caller must then not believe it drew.
        internal bool Show(string text, bool error)
        {
            if (Go == null) return false;
            if (text != _text) { _text = text; Rebuild(text, error); }
            Go.SetActive(true);
            foreach (var r in Go.GetComponentsInChildren<Renderer>()) r.enabled = true;
            ShownAt = Time.unscaledTime;
            return true;
        }

        internal string Debug()
        {
            var r = Go != null ? Go.GetComponent<Renderer>() : null;
            if (r == null) return "no renderer";
            var b = r.bounds;
            return $"active={Go.activeInHierarchy} enabled={r.enabled} layer={Go.layer} " +
                   $"tex={(Tex != null ? Tex.width + "x" + Tex.height : "null")} " +
                   $"mat={(Mat != null ? Mat.name + "/" + (Mat.shader != null ? Mat.shader.name : "null") : "null")} " +
                   $"world={Go.transform.position.x:F2},{Go.transform.position.y:F2},{Go.transform.position.z:F2} " +
                   $"local={Go.transform.localPosition.x:F4},{Go.transform.localPosition.y:F4},{Go.transform.localPosition.z:F3} " +
                   $"scale={Go.transform.localScale.x:F3} " +
                   $"bounds={b.size.x:F3}x{b.size.y:F3}";
        }

        internal void Hide()
        {
            _text = null;
            if (Go != null && Go.activeSelf) Go.SetActive(false);
        }

        internal bool Visible => Go != null && Go.activeSelf;

        private void Rebuild(string text, bool error)
        {
            int tw = BitmapFont5x7.Measure(text, FontScale);
            int th = BitmapFont5x7.GlyphH * FontScale;
            int w = tw + 26, h = th + 12;
            RefW = w; RefH = h;
            var c = new ArtCanvas(w, h);
            var plate = new Color32(14, 10, 12, 232);
            var edge = error ? new Color32(232, 92, 72, 255) : new Color32(196, 168, 92, 255);
            var ink = new Color32(247, 239, 192, 255);
            c.FillRectOpaque(0, 0, w, h, plate);
            c.FillRectOpaque(0, 0, w, 1, edge);
            c.FillRectOpaque(0, h - 1, w, 1, edge);
            c.FillRectOpaque(0, 0, 1, h, edge);
            c.FillRectOpaque(w - 1, 0, 1, h, edge);
            BitmapFont5x7.DrawCentered(c, text, w / 2, (h - th) / 2, FontScale, ink, plate);

            var rows = ToBottomUp(c);
            if (Tex != null) UnityEngine.Object.Destroy(Tex);
            Tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { name = "AlterumReelHudTex" };
            Tex.LoadRawTextureData(new Il2CppStructArray<byte>(rows));
            Tex.Apply(false, false);
            Tex.wrapMode = TextureWrapMode.Clamp;
            Tex.filterMode = FilterMode.Bilinear;
            if (Mat.HasProperty("_BaseMap")) Mat.SetTexture("_BaseMap", Tex);
            else if (Mat.HasProperty("_MainTex")) Mat.SetTexture("_MainTex", Tex);

            float mw = w * TexelMeters, mh = h * TexelMeters;
            Mesh.Clear();
            var verts = new Vector3[]
            {
                new Vector3(-mw * 0.5f, -mh * 0.5f, 0f),
                new Vector3( mw * 0.5f, -mh * 0.5f, 0f),
                new Vector3( mw * 0.5f,  mh * 0.5f, 0f),
                new Vector3(-mw * 0.5f,  mh * 0.5f, 0f),
            };
            var uvs = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            Mesh.vertices = new Il2CppStructArray<Vector3>(verts);
            Mesh.uv = new Il2CppStructArray<Vector2>(uvs);
            Mesh.triangles = new Il2CppStructArray<int>(new int[] { 0, 1, 2, 0, 2, 3 });
            Mesh.RecalculateBounds();
        }

        private static byte[] ToBottomUp(ArtCanvas c)
        {
            var o = new byte[c.W * c.H * 4];
            for (int y = 0; y < c.H; y++)
            {
                int dst = (c.H - 1 - y) * c.W * 4;
                for (int x = 0; x < c.W; x++)
                {
                    var p = c.Px[y * c.W + x];
                    int k = dst + x * 4;
                    o[k] = p.r; o[k + 1] = p.g; o[k + 2] = p.b; o[k + 3] = p.a;
                }
            }
            return o;
        }

        public void Dispose()
        {
            try { if (Mesh != null) UnityEngine.Object.Destroy(Mesh); } catch { }
            try { if (Mat != null) UnityEngine.Object.Destroy(Mat); } catch { }
            try { if (Tex != null) UnityEngine.Object.Destroy(Tex); } catch { }
            try { if (Go != null) UnityEngine.Object.Destroy(Go); } catch { }
            Mesh = null; Mat = null; Tex = null; Go = null;
        }
    }

    // ---- layout ------------------------------------------------------------------------

    // The camera the geometry is authored against. Prefer the camera the quads live on (it is the
    // one that renders them); fall back to Camera.main so the IMGUI path still has a viewport
    // before the quads exist.
    private static Camera LayoutCamera()
    {
        try { if (_cam != null) return _cam; } catch { }
        try { return Camera.main; } catch { return null; }
    }

    private static Rect Viewport(Camera cam)
    {
        try
        {
            if (cam != null && cam.pixelWidth > 0 && cam.pixelHeight > 0) return cam.pixelRect;
        }
        catch { }
        return new Rect(0f, 0f, Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
    }

    // Recalculate on camera, viewport, resolution or FOV change. Cheap when nothing moved: one
    // comparison against the values the last layout was computed from.
    internal static bool EnsureLayout(IHostContext log)
    {
        var cam = LayoutCamera();
        var vp = Viewport(cam);
        int camId = 0;
        float fov = 60f; bool ortho = false; float orthoSize = 1f;
        if (cam != null)
        {
            try { camId = cam.GetInstanceID(); } catch { }
            try { fov = cam.fieldOfView; } catch { }
            try { ortho = cam.orthographic; } catch { }
            try { orthoSize = cam.orthographicSize; } catch { }
        }
        if (Screen.width != _hotbarScreenW || Screen.height != _hotbarScreenH)
        {
            // A different window size means the cached hotbar rectangle (screen pixels) is stale.
            _hotbarScreenW = Screen.width; _hotbarScreenH = Screen.height;
            _hotbarFound = false; _hotbarGaveUp = false; _hotbarTries = 0; _hotbarNextTry = 0f;
            _hotbarWhy = "screen size changed";
            _layoutValid = false;
        }
        // A hotbar that was not there yet is retried on the next layout check: the booth can be
        // spawned before the map HUD exists, and an unresolved hotbar must not pin the fallback
        // layout for the rest of the generation.
        bool retryHotbar = !_hotbarFound && !_hotbarGaveUp && Time.unscaledTime >= _hotbarNextTry;
        if (_layoutValid && !retryHotbar && camId == _layoutCamId && vp == _layoutVp &&
            Mathf.Approximately(fov, _layoutFov) && ortho == _layoutOrtho &&
            Mathf.Approximately(orthoSize, _layoutOrthoSize) &&
            Screen.width == _layoutScreenW && Screen.height == _layoutScreenH)
            return true;

        _layoutCamId = camId; _layoutVp = vp; _layoutFov = fov; _layoutOrtho = ortho;
        _layoutOrthoSize = orthoSize;
        _layoutScreenW = Screen.width; _layoutScreenH = Screen.height;

        // Reference scale: 1 at 640x360, 3 at 1920x1080, and it follows a letterboxed viewport too.
        _s = Mathf.Min(vp.width / RefW, vp.height / RefH);
        if (_s <= 0.01f) _s = 1f;
        // Metres per reference pixel at the quad depth: half-height of the frustum at Depth, mapped
        // onto the reference height of this viewport.
        _halfH = ortho ? orthoSize : Depth * Mathf.Tan(Mathf.Clamp(fov, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
        _halfW = _halfH * (vp.width / Mathf.Max(1f, vp.height));
        _mPerRefPx = 2f * _halfH * _s / Mathf.Max(1f, vp.height);

        float safeB = vp.y + SafeRef * _s;
        float safeT = vp.y + vp.height - SafeRef * _s;
        float lineH = LineHeightRef * _s;

        bool hot = ResolveHotbar(log, vp, _s);
        float bottom = hot
            ? _hotbar.yMax + PromptGapRef * _s
            : vp.y + (RefH - ReservedBottomRef) * _s;
        float rawBottom = bottom;
        if (safeT - lineH >= safeB) bottom = Mathf.Clamp(bottom, safeB, safeT - lineH);
        else bottom = Mathf.Max(safeB, Mathf.Min(bottom, safeT - lineH));
        _promptBottom = bottom;
        _layoutSource = hot
            ? $"hotbar '{_hotbarWhy}' top={_hotbar.yMax:F0}px ref={(_hotbar.yMax - vp.y) / _s:F0} " +
              $"promptBottom={bottom:F1}px ref={(bottom - vp.y) / _s:F0}"
            : $"reserved bottom {ReservedBottomRef} ref px, promptBottom ref={(bottom - vp.y) / _s:F0} " +
              $"(hotbar: {_hotbarWhy})";

        float mb = bottom + lineH + MessageGapRef * _s;
        if (safeT - lineH >= safeB) mb = Mathf.Clamp(mb, safeB, safeT - lineH);
        _messageBottom = mb;

        bool moved = Mathf.Abs(rawBottom - bottom) > 0.5f;
        _layoutValid = true;
        if (log != null)
            log.LogInfo($"[reel:ui] layout source={_layoutSource} s={_s:F3} viewport=({vp.x:F0},{vp.y:F0}," +
                        $"{vp.width:F0}x{vp.height:F0}) screen={Screen.width}x{Screen.height} fov={fov:F1} " +
                        $"ortho={ortho} depth={Depth:F3}m half=({_halfW:F4},{_halfH:F4}) mPerRefPx={_mPerRefPx:F6} " +
                        $"promptBottom={_promptBottom:F1} messageBottom={_messageBottom:F1} " +
                        $"safe=({safeB:F1}..{safeT - lineH:F1}) clamped={moved}");
        return true;
    }

    // Where one line's quad goes, in screen pixels and in camera-local metres. `wRef`/`hRef` are
    // the canvas size (== reference pixels, because Rebuild draws at that scale).
    private static Geo ComputeGeo(float wRef, float hRef, bool prompt, out float fit)
    {
        var vp = _layoutVp;
        float safeL = vp.x + SafeRef * _s, safeR = vp.x + vp.width - SafeRef * _s;
        float safeW = Mathf.Max(1f, safeR - safeL);
        float wPx = wRef * _s, hPx = hRef * _s;
        // A single line cannot wrap, so a message wider than the safe width is scaled down rather
        // than allowed to leave the viewport: "clamped inside the safe area" has to hold for the
        // whole rect, not just its centre.
        fit = wPx > safeW ? safeW / wPx : 1f;
        wPx *= fit;
        hPx *= fit;
        float left = vp.x + (vp.width - wPx) * 0.5f;              // centred in the camera viewport
        if (safeR - wPx >= safeL) left = Mathf.Clamp(left, safeL, safeR - wPx);
        float bottom = prompt ? _promptBottom : _messageBottom;
        return new Geo { X = left, Y = bottom, W = wPx, H = hPx };
    }

    private static void ApplyLayout(Line l, bool prompt)
    {
        if (l == null || l.Go == null || !_layoutValid) return;
        float fit;
        var g = ComputeGeo(l.RefW, l.RefH, prompt, out fit);
        var vp = _layoutVp;
        float cx = g.X + g.W * 0.5f, cy = g.Y + g.H * 0.5f;
        float vx = (cx - vp.x) / Mathf.Max(1f, vp.width);
        float vy = (cy - vp.y) / Mathf.Max(1f, vp.height);
        try
        {
            l.Go.transform.localPosition = new Vector3((vx - 0.5f) * 2f * _halfW,
                                                       (vy - 0.5f) * 2f * _halfH, Depth);
            float sc = fit * _mPerRefPx / TexelMeters;
            l.Go.transform.localScale = new Vector3(sc, sc, 1f);
        }
        catch { }
        if (prompt) _promptGeo = g; else _messageGeo = g;
    }

    // The IMGUI fallback draws the SAME rectangle the quad occupies. IMGUI is top-left origin, the
    // geometry here is bottom-left, so the y is flipped against the screen height.
    internal static Rect ScreenRect(bool prompt, string text)
    {
        try
        {
            EnsureLayout(null);
            int wRef = BitmapFont5x7.Measure(text ?? "", FontScale) + 26;
            int hRef = BitmapFont5x7.GlyphH * FontScale + 12;
            float fit;
            var g = ComputeGeo(wRef, hRef, prompt, out fit);
            return new Rect(g.X, Screen.height - g.Top, g.W, g.H);
        }
        catch
        {
            // Only reached if the layout itself throws; keep the documented reservation (the bottom
            // 96 reference pixels are the hotbar's) rather than a fresh guess.
            float h = 26f;
            return new Rect((Screen.width - 260f) * 0.5f, Screen.height - 96f - h, 260f, h);
        }
    }

    // ---- native hotbar ------------------------------------------------------------------

    // The inventory hotbar's own rectangle in screen pixels. Scanned from the live UI, cached for
    // the generation and for the current window size, retried a bounded number of times (a map's
    // HUD is built a moment after the booth) and logged either way. When it cannot be resolved the
    // caller falls back to reserving the bottom 96 reference pixels, which is the documented
    // behaviour rather than a guess anchored to the reel.
    private static bool ResolveHotbar(IHostContext log, Rect vp, float s)
    {
        if (_hotbarFound) return true;
        if (_hotbarGaveUp) return false;
        if (Time.unscaledTime < _hotbarNextTry) return false;
        _hotbarNextTry = Time.unscaledTime + 8f;
        _hotbarTries++;

        Rect best = default;
        float bestScore = -1f;
        string bestPath = null;
        var census = new StringBuilder();
        int seen = 0;
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<RectTransform>());
            for (int i = 0; i < arr.Length; i++)
            {
                var rt = arr[i].TryCast<RectTransform>();
                if (rt == null) continue;
                var go = rt.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(go.scene.name)) continue;
                string path = PathOf(rt);
                string lower = path.ToLowerInvariant();
                int token = TokenScore(lower);
                if (token <= 0) continue;
                Rect r;
                string why;
                string reject = null;
                if (!ScreenRectOf(rt, out r, out why)) reject = "no-screen-rect:" + why;
                // Geometry gate: a hotbar is a wide, short strip in the lower part of the screen.
                else if (r.width < vp.width * 0.15f) reject = "too-narrow";
                else if (r.height < vp.height * 0.03f) reject = "too-short";
                else if (r.height > vp.height * 0.45f) reject = "too-tall";
                else if (r.width < r.height * 2f) reject = "not-wide";
                else if (r.y + r.height * 0.5f > vp.y + vp.height * 0.45f) reject = "not-lower-half";
                int slots = reject == null ? SlotChildren(rt) : 0;
                float score = token * 10f + Mathf.Min(12, slots) +
                              Mathf.Clamp01((r.width - vp.width * 0.15f) / vp.width);
                if (seen++ < 10 && census.Length < 1200)
                    census.Append($"'{path}' rect=({r.x:F0},{r.y:F0},{r.width:F0}x{r.height:F0}) " +
                                  $"ref={(r.y - vp.y) / s:F0}+{r.height / s:F0} token={token} slots={slots} " +
                                  (reject != null ? $"rejected={reject}; " : $"score={score:F1}; "));
                if (reject != null) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = r;
                    bestPath = path;
                }
            }
        }
        catch (Exception e) { _hotbarWhy = "scan failed: " + e.Message; }

        if (bestPath != null)
        {
            _hotbar = best;
            _hotbarFound = true;
            _hotbarWhy = bestPath;
            log?.LogInfo($"[reel:ui] hotbar resolved attempt={_hotbarTries} path='{bestPath}' " +
                         $"rect=({best.x:F0},{best.y:F0},{best.width:F0}x{best.height:F0}) " +
                         $"top={best.yMax:F0} candidates=[{census}]");
            return true;
        }
        if (_hotbarTries >= 8)
        {
            _hotbarGaveUp = true;
            _hotbarWhy = $"unresolved after {_hotbarTries} scans";
            log?.LogWarning($"[reel:ui] hotbar unresolved after {_hotbarTries} scans; " +
                            $"using the reserved bottom {ReservedBottomRef} reference pixels. last=[{census}]");
        }
        else
        {
            _hotbarWhy = $"attempt {_hotbarTries}/8";
            log?.LogInfo($"[reel:ui] hotbar not resolved yet attempt={_hotbarTries}/8 candidates=[{census}]");
        }
        return false;
    }

    private static int TokenScore(string lowerPath)
    {
        for (int i = 0; i < HotbarTokens.Length; i++)
            if (lowerPath.IndexOf(HotbarTokens[i], StringComparison.Ordinal) >= 0) return 5;
        bool inv = lowerPath.IndexOf("inventory", StringComparison.Ordinal) >= 0;
        bool item = lowerPath.IndexOf("item", StringComparison.Ordinal) >= 0;
        bool slot = lowerPath.IndexOf("slot", StringComparison.Ordinal) >= 0;
        bool bar = lowerPath.IndexOf("bar", StringComparison.Ordinal) >= 0;
        if (inv && (slot || bar)) return 4;
        if (item && (slot || bar)) return 3;
        if (lowerPath.IndexOf("handslot", StringComparison.Ordinal) >= 0 ||
            lowerPath.IndexOf("hand slot", StringComparison.Ordinal) >= 0) return 2;
        return 0;
    }

    // How many of the direct children look like slots. Used only as a ranking tie-breaker.
    private static int SlotChildren(RectTransform rt)
    {
        int n = 0;
        try
        {
            int count = rt.childCount;
            for (int i = 0; i < count && i < 24; i++)
            {
                var c = rt.GetChild(i);
                if (c == null || c.name == null) continue;
                string ln = c.name.ToLowerInvariant();
                for (int k = 0; k < SlotTokens.Length; k++)
                    if (ln.IndexOf(SlotTokens[k], StringComparison.Ordinal) >= 0) { n++; break; }
            }
        }
        catch { }
        return n;
    }

    // Screen-space rectangle of a RectTransform in pixels, bottom-left origin like the UI system.
    // Copied from the wardrobe's proven helper: overlay canvases use WorldToScreenPoint(null, ...),
    // camera-space canvases pass their own camera.
    private static bool ScreenRectOf(RectTransform rt, out Rect r, out string why)
    {
        r = default;
        why = "";
        if (rt == null) { why = "null"; return false; }
        try
        {
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            var canvas = rt.GetComponentInParent(Il2CppType.Of<Canvas>())?.TryCast<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            float x0 = 0f, y0 = 0f, x1 = 0f, y1 = 0f;
            bool any = false;
            for (int k = 0; k < 4; k++)
            {
                var sp = RectTransformUtility.WorldToScreenPoint(cam, c[k]);
                if (!any) { x0 = x1 = sp.x; y0 = y1 = sp.y; any = true; }
                else
                {
                    x0 = Math.Min(x0, sp.x); x1 = Math.Max(x1, sp.x);
                    y0 = Math.Min(y0, sp.y); y1 = Math.Max(y1, sp.y);
                }
            }
            if (!any) { why = "no corners"; return false; }
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    // Revalidate, never assume. The prompt and the result message share one pair of quads, and the
    // quads are parented to whichever camera was current when they were built. A booth looked at in
    // the Lobby built them on the lobby camera; the map load then destroyed that camera and with it
    // the quads, leaving non-null wrappers over dead GameObjects. The old check (`_prompt != null &&
    // _message != null`) passed, `Show` silently did nothing, `Message` still reported success and
    // the IMGUI fallback was suppressed by Available - so the result text vanished. So: rebuild
    // whenever the camera, the quad object, or the scene generation behind them is gone.
    internal static bool Ensure(IHostContext log)
    {
        int handle = ActiveHandle();
        bool built = _prompt != null || _message != null;
        string stale = built ? WhyStale(handle) : "not built";
        if (built && stale == null) { EnsureLayout(log); return true; }
        if (built)
        {
            log?.LogInfo($"[reel:ui] drawn hud rebuild #{_builds + 1}: {stale}");
            Dispose();
        }
        if (_failed) return false;
        if (Time.unscaledTime < _nextBuildAt) return false;
        _nextBuildAt = Time.unscaledTime + 1.0f;
        try
        {
            _cam = ResolveCamera(log, built ? "rebuild" : "build");
            if (_cam == null) { _why = "no game camera"; return false; }
            // Pick a layer the camera actually renders. A camera living on a custom layer may not
            // include its own layer in its culling mask, which silently culls camera-parented quads.
            int mask = _cam.cullingMask;
            int layer = -1;
            if ((mask & 1) != 0) layer = 0;
            else
                for (int i = 1; i < 32; i++)
                    if ((mask & (1 << i)) != 0) { layer = i; break; }
            if (layer < 0) { _why = $"camera mask 0x{mask:X8} includes no layer"; return false; }
            log?.LogInfo($"[reel:ui] drawn hud layer={layer} camLayer={_cam.gameObject.layer} mask=0x{mask:X8} cam='{PathOf(_cam)}'");
            _prompt = new Line();
            _message = new Line();
            _prompt.Build("AlterumReelPrompt", _cam, layer);
            _message.Build("AlterumReelMessage", _cam, layer);
            // The anchor is not a metre offset any more: layout puts both quads above the hotbar.
            _prompt.Go.transform.localPosition = new Vector3(0f, -0.0335f, Depth);
            _message.Go.transform.localPosition = new Vector3(0f, -0.0262f, Depth);
            _sceneHandle = handle;
            _builds++;
            _why = $"camera quads depth={Depth}m texel={TexelMeters:F4}m font={FontScale}px builds={_builds}";
            log?.LogInfo($"[reel:ui] drawn hud built {_why} cam='{PathOf(_cam)}' scene='{SceneName()}'");
            EnsureLayout(log);
            return true;
        }
        catch (Exception e)
        {
            _failed = true;
            _why = "build failed: " + e.Message;
            log?.LogWarning($"[reel:ui] drawn hud failed: {e.Message}");
            return false;
        }
    }

    // null while the quads are valid for the live scene; otherwise the reason they are not.
    private static string WhyStale(int handle)
    {
        if (!CamAlive) return _cam == null ? "camera null" : $"camera '{PathOf(_cam)}' gone or disabled";
        if (_prompt == null || _prompt.Go == null) return "prompt quad destroyed";
        if (_message == null || _message.Go == null) return "message quad destroyed";
        if (handle != _sceneHandle) return $"scene changed {_sceneHandle}->{handle} ('{SceneName()}')";
        return null;
    }

    private static int ActiveHandle()
    {
        try { return SceneManager.GetActiveScene().handle; } catch { return 0; }
    }

    private static string SceneName()
    {
        try { return SceneManager.GetActiveScene().name ?? ""; } catch { return "?"; }
    }

    // The camera the player is actually looking through. Camera.main is the first guess - the quads
    // and the prompt style were tuned against it - but it is only accepted while it is alive,
    // enabled, scene-resident and renders our layer. Otherwise the picker the captures use decides
    // (ScreenShot.FindGameCamera), so a notification lands in a screenshot as well as on screen.
    private static Camera ResolveCamera(IHostContext log, string why)
    {
        Camera main = null, shot = null;
        string mainWhy = "?", shotWhy = "?";
        try { main = Camera.main; } catch (Exception e) { mainWhy = e.Message; }
        try { shot = ScreenShot.FindGameCamera(null); } catch (Exception e) { shotWhy = e.Message; }
        bool mainOk = Good(main, out mainWhy);
        bool shotOk = !mainOk && Good(shot, out shotWhy);
        var pick = mainOk ? main : (shotOk ? shot : null);
        log?.LogInfo($"[reel:ui] draw camera why={why} main='{PathOf(main)}'{(mainOk ? " ok" : " rejected: " + mainWhy)} " +
                     $"shot='{PathOf(shot)}'{(shotOk ? " ok" : " rejected: " + shotWhy)} pick='{PathOf(pick)}'");
        return pick;
    }

    private static bool Good(Camera c, out string why)
    {
        why = "?";
        try
        {
            if (c == null) { why = "null"; return false; }
            var go = c.gameObject;
            if (go == null) { why = "no game object"; return false; }
            if (!go.activeInHierarchy) { why = "inactive"; return false; }
            if (!c.enabled) { why = "disabled"; return false; }
            if (c.targetTexture != null) { why = "renders to a texture"; return false; }
            if (string.IsNullOrEmpty(go.scene.name)) { why = "not in a scene"; return false; }
            if (c.pixelWidth <= 0 || c.pixelHeight <= 0) { why = "no pixel size"; return false; }
            why = "ok";
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    private static string PathOf(Camera c)
    {
        try
        {
            if (c == null) return "null";
            return PathOf(c.transform);
        }
        catch { return "?"; }
    }

    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "?";
            string s = t.name;
            int guard = 0;
            for (var p = t.parent; p != null && guard++ < 8; p = p.parent) s = p.name + "/" + s;
            return s;
        }
        catch { return "?"; }
    }

    internal static bool Prompt(IHostContext log, string text)
    {
        if (!Ensure(log)) return false;
        if (!_prompt.Show(text, false)) { log?.LogWarning($"[reel:ui] prompt quad could not show: {_prompt.Debug()}"); return false; }
        ApplyLayout(_prompt, true);
        return true;
    }

    internal static void HidePrompt()
    {
        _prompt?.Hide();
    }

    // True only when the text is now on a quad that exists, is active, and projects inside the
    // camera's viewport. The caller uses the answer to decide whether to fall back to IMGUI; a
    // "shown" that drew nothing is the regression this replaces.
    internal static bool Message(IHostContext log, string text, float seconds, bool error)
    {
        if (!Ensure(log)) { log?.LogInfo($"[reel:ui] drawn message unavailable: {_why}"); return false; }
        if (!_message.Show(text, error)) { log?.LogWarning($"[reel:ui] drawn message could not show: {_message.Debug()}"); return false; }
        ApplyLayout(_message, false);
        string off;
        if (!OnScreen(_message, out off))
        {
            log?.LogWarning($"[reel:ui] drawn message off-screen: {off} {_message.Debug()}");
            return false;
        }
        _messageUntil = Time.unscaledTime + seconds;
        log?.LogInfo($"[reel:ui] drawn message shown: {_message.Debug()} source={_layoutSource}");
        return true;
    }

    // The quad is a child of the camera, so a projection check is the cheapest proof that the
    // camera that renders it is the camera the player is looking through.
    private static bool OnScreen(Line l, out string why)
    {
        why = "";
        try
        {
            if (l == null || l.Go == null) { why = "no quad"; return false; }
            if (_cam == null) { why = "no camera"; return false; }
            Vector3 v = _cam.WorldToViewportPoint(l.Go.transform.position);
            bool ok = v.z > 0f && v.x > 0.0f && v.x < 1.0f && v.y > 0.0f && v.y < 1.0f;
            if (!ok) why = $"viewport=({v.x:F2},{v.y:F2},z={v.z:F2})";
            return ok;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    internal static void Tick(float dt)
    {
        if (_message != null && _message.Visible && Time.unscaledTime > _messageUntil) _message.Hide();
        if ((_prompt != null && _prompt.Visible) || (_message != null && _message.Visible)) EnsureLayout(null);
    }

    internal static void Dispose()
    {
        _prompt?.Dispose();
        _message?.Dispose();
        _prompt = null;
        _message = null;
        _failed = false;
        _sceneHandle = 0;
        _why = "not built";
        _layoutValid = false;
        _layoutCamId = int.MinValue;
        _hotbarFound = false; _hotbarGaveUp = false; _hotbarTries = 0; _hotbarNextTry = 0f;
        _hotbarWhy = "not probed";
    }
}
