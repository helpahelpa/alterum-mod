using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace AlterumModelSwap.Hot;

// MirrorDecor's model: a stationary, mirror-owned camera feeding textured glass.
// This is intentionally NOT a planar reflection and never follows the viewer.
public static class MirrorFactory
{
    internal const int Resolution = 1536, Samples = 1;
    // The reference prefab's sheared Frame/Mesh/Camera hierarchy is not our quad's
    // coordinate system. Use the plan's equivalent behind-glass lens, looking +Z.
    private static readonly Vector3 LensOffset = new Vector3(0f, 0f, -0.10f);

    public static MirrorRuntime Create(IHostContext log, Transform anchor, Camera mainCam, float width, float height,
        float offset, float yOffset, bool addFrame, bool showPlayer, float tiltDegrees,
        bool bottomOnFloor, bool snapToWall, float eyeHeight,
        bool hasFixedPose, Vector3 fixedPos, Quaternion fixedRot)
    {
        var ctrl = new MirrorRuntime { log = log, Generation = Time.frameCount };
        try
        {
            if (mainCam == null || width <= 0 || height <= 0) throw new InvalidOperationException("Invalid camera/glass dimensions");
            ctrl.Root = new GameObject("AlterumMirror");
            ctrl.Root.transform.position = fixedPos;
            ctrl.Root.transform.rotation = fixedRot;
            ctrl.Root.transform.localScale = Vector3.one;
            ctrl.glassMesh = BuildQuad(width, height);
            var mf = ctrl.Root.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
            var mr = ctrl.Root.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
            if (mf == null || mr == null) throw new InvalidOperationException("Mesh components failed");
            mf.sharedMesh = ctrl.glassMesh;
            ctrl.quadRenderer = mr;
            ctrl.mirrorRenderers.Add(mr);

            ctrl.rt = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32)
            {
                name = "AlterumMirrorRT", antiAliasing = Samples, useMipMap = false,
                autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!ctrl.rt.Create() || !ctrl.rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Compatible URP/Unlit shader unavailable");
            ctrl.glassMat = new Material(shader) { name = "AlterumMirrorMat" };
            ApplyTexture(ctrl.glassMat, ctrl.rt);
            mr.sharedMaterial = ctrl.glassMat;
            NoShadows(mr);
            if (addFrame)
            {
                var frame = new GameObject("AlterumMirrorFrame");
                frame.transform.SetParent(ctrl.Root.transform, false);
                frame.transform.localPosition = new Vector3(0, 0, -0.012f);
                var ff = frame.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
                var fr = frame.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
                if (ff == null || fr == null) throw new InvalidOperationException("Frame components failed");
                ctrl.mirrorRenderers.Add(fr);
                ctrl.frameMesh = BuildQuad(width + 0.14f, height + 0.14f);
                ff.sharedMesh = ctrl.frameMesh;
                ctrl.frameMat = new Material(shader) { name = "AlterumMirrorFrameMat" };
                if (ctrl.frameMat.HasProperty("_BaseColor")) ctrl.frameMat.SetColor("_BaseColor", new Color(.09f, .07f, .05f, 1));
                if (ctrl.frameMat.HasProperty("_Color")) ctrl.frameMat.SetColor("_Color", new Color(.09f, .07f, .05f, 1));
                fr.sharedMaterial = ctrl.frameMat;
                NoShadows(fr);
            }

            int bodyLayer = showPlayer ? SelectBodyLayer(log) : -1;
            var camGo = new GameObject("AlterumMirrorCam");
            camGo.transform.SetParent(ctrl.Root.transform, false);
            camGo.transform.localPosition = LensOffset;
            camGo.transform.localRotation = Quaternion.identity;
            camGo.transform.localScale = Vector3.one;
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) throw new InvalidOperationException("Camera creation failed");
            ctrl.reflectCam = cam;
            cam.enabled = false;
            cam.targetTexture = ctrl.rt;
            cam.orthographic = false;
            cam.fieldOfView = 65;
            cam.aspect = width / height;
            cam.nearClipPlane = .01f;
            cam.farClipPlane = 1000;
            cam.depth = 0;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.cullingMask = (mainCam.cullingMask | (bodyLayer >= 0 ? 1 << bodyLayer : 0)) & ~(1 << 24) & ~(1 << 5) & ~(1 << 15);
            // Layer 23 is the game's shared TV_PlayerModelMesh layer, not a cosmetic-only layer.
            // It is admitted only while the cosmetic runtime actually has a live cosmetic, and that
            // gate is re-evaluated every render (SyncCosmeticLayer). Admitting it unconditionally
            // rendered native objects on that layer in the mirror while the source camera excluded
            // it, with nothing equipped or with the player body hidden (P2.3).
            ctrl.BodyLayer = bodyLayer;
            if (showPlayer && bodyLayer >= 0)
            {
                ctrl.Visibility = new MirrorPlayerVisibility(log);
                ctrl.Visibility.Attach(anchor, bodyLayer);
            }
            var layers = new StringBuilder();
            for (int i = 0; i < 32; i++) layers.Append($"{i}={LayerMask.LayerToName(i)}; ");
            log.LogInfo($"[mirror:layers] gameMask=0x{mainCam.cullingMask:X8} {layers}");
            int colliders = ctrl.Root.GetComponentsInChildren(Il2CppType.Of<Collider>()).Length;
            log.LogInfo($"[mirror] created at {fixedPos} ({width}x{height}), colliders={colliders}");
            log.LogInfo($"[mirror:static-created] root={fixedPos} euler={fixedRot.eulerAngles} cameraWorld={cam.transform.position} local={cam.transform.localPosition} localRotation={cam.transform.localRotation} mask=0x{cam.cullingMask:X8} fov={cam.fieldOfView} aspect={cam.aspect:F4} RT={ctrl.rt.width}x{ctrl.rt.height} format={ctrl.rt.format} depth={ctrl.rt.depth} samples={ctrl.rt.antiAliasing} created={ctrl.rt.IsCreated()} shader={shader.name} colliders={colliders}");
            var hits = Physics.RaycastAll(ctrl.Root.transform.position, -ctrl.Root.transform.forward, .10f);
            log.LogInfo($"[mirror:camera-placement] fixed behind-glass offset={LensOffset}; glass-to-lens intersections={hits.Length}; reference sheared prefab TRS not copied onto Alterum quad");
            foreach (var hit in hits) log.LogInfo($"[mirror:camera-placement] intersect '{hit.collider?.name}' distance={hit.distance:F4}");
            ctrl.PrepareRendererStates();
            ctrl.PrepareCosmeticLayerGate();
            return ctrl;
        }
        catch (Exception e)
        {
            log.LogError($"[mirror] creation failed: {e}");
            ctrl.Dispose();
            return null;
        }
    }

    internal static bool ScreenCamera(Camera c) => c != null && c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null && !string.IsNullOrEmpty(c.gameObject.scene.name);

    internal static bool BodyLayerConflicts(int layer)
    {
        if (layer < 0) return false;
        var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i].TryCast<Camera>();
            if (ScreenCamera(c) && (c.cullingMask & (1 << layer)) != 0) return true;
        }
        return false;
    }

    // Layer choice must satisfy two facts established at runtime:
    //  - this URP pipeline does not render every GameObject layer (a game renderer layer mask
    //    silently drops layers 3,5,8,12,13,16..19,21,22,25,26,28..31), so a "free" layer is
    //    invisible to the mirror too;
    //  - the body layer must be absent from every screen camera so the owner never sees it.
    // Probe renderability once, then pick the quietest layer that satisfies both.
    private static int SelectBodyLayer(IHostContext log)
    {
        int screenMask = 0;
        var cams = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i].TryCast<Camera>();
            if (!ScreenCamera(c)) continue;
            screenMask |= c.cullingMask;
            log.LogInfo($"[mirror:screen-camera] '{CamPath(c.transform)}' mask=0x{c.cullingMask:X8} depth={c.depth}");
        }
        var counts = new int[32];
        var renderers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i].TryCast<Renderer>();
            if (r != null && r.gameObject.activeInHierarchy && !string.IsNullOrEmpty(r.gameObject.scene.name)) counts[r.gameObject.layer]++;
        }
        var renderable = RenderableLayers(log);
        int best = -1;
        for (int layer = 31; layer >= 0; layer--)
        {
            if (layer == 5 || layer == 15 || layer == 23 || layer == 24) continue;
            if (!renderable[layer] || (screenMask & (1 << layer)) != 0) continue;
            if (counts[layer] != 0) continue;
            best = layer;
            break;
        }
        int usedBy = best >= 0 ? 0 : -1;
        if (best < 0)
        {
            // No renderable layer is unused by the scene: take the least-used renderable one that
            // screen cameras exclude. Reusing e.g. Water/Hitbox is safer than an invisible layer.
            int least = int.MaxValue;
            for (int layer = 31; layer >= 0; layer--)
            {
                if (layer == 5 || layer == 15 || layer == 23 || layer == 24) continue;
                if (!renderable[layer] || (screenMask & (1 << layer)) != 0) continue;
                if (counts[layer] < least) { least = counts[layer]; best = layer; }
            }
            usedBy = least == int.MaxValue ? -1 : least;
        }
        if (best < 0) { log.LogWarning("[mirror:body-layer-unavailable] no renderable layer outside every screen camera; body unchanged"); return -1; }
        log.LogInfo($"[mirror:body-layer] selected={best} name='{LayerMask.LayerToName(best)}' screenCamerasExclude=true renderable=true otherActiveRenderers={usedBy}");
        return best;
    }

    private static string CamPath(Transform t)
    {
        var path = t.name;
        for (int i = 0; i < 6 && t.parent != null; i++) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    // The renderability probe costs 32 synchronous Camera.Render() + ReadPixels round trips, so it
    // is a per-process result (a hot reload is a new generation and re-probes), not a per-mirror one.
    private static bool[] _renderableLayers;

    // Explicit invalidation for a pipeline/renderer-asset change at runtime.
    internal static void InvalidateRenderableLayers() => _renderableLayers = null;

    // Shared per-generation renderable-layer map for the cosmetics runtime (the local-cosmetic
    // layer must be one the pipeline actually renders, same probe, same result).
    internal static bool[] LayerRenderability(IHostContext log) => RenderableLayers(log);

    private static bool[] RenderableLayers(IHostContext log)
    {
        if (_renderableLayers != null)
        {
            log.LogInfo("[mirror:layer-probe] cached renderable map (probed once per generation; InvalidateRenderableLayers to re-probe)");
            return _renderableLayers;
        }
        return _renderableLayers = ProbeRenderableLayers(log);
    }

    private static bool[] ProbeRenderableLayers(IHostContext log)
    {
        var result = new bool[32];
        GameObject camGo = null, probe = null;
        RenderTexture tmp = null;
        Texture2D tex = null;
        Mesh mesh = null;
        Material mat = null;
        var previous = RenderTexture.active;
        try
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) { log.LogWarning("[mirror:layer-probe] no URP/Unlit shader"); return result; }
            camGo = new GameObject("AlterumLayerProbeCam");
            camGo.transform.position = new Vector3(0f, 1000f, 0f);
            camGo.transform.rotation = Quaternion.identity;
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).TryCast<Camera>();
            if (cam == null) return result;
            cam.enabled = false;
            cam.useOcclusionCulling = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 10f;
            tmp = new RenderTexture(16, 16, 16, RenderTextureFormat.ARGB32);
            tmp.Create();
            cam.targetTexture = tmp;
            probe = new GameObject("AlterumLayerProbe");
            probe.transform.position = camGo.transform.position + Vector3.forward * 0.5f;
            probe.transform.rotation = camGo.transform.rotation;
            probe.transform.localScale = Vector3.one * 0.1f;
            var mf = probe.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
            var mr = probe.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
            if (mf == null || mr == null) return result;
            mesh = new Mesh { name = "AlterumLayerProbeQuad" };
            mesh.vertices = new Il2CppStructArray<Vector3>(new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) });
            mesh.triangles = new Il2CppStructArray<int>(new[] { 0,1,2,0,2,3,0,2,1,0,3,2 });
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            mat = new Material(shader) { name = "AlterumLayerProbeMat" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.magenta);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            tex = new Texture2D(16, 16, TextureFormat.RGB24, false);
            var sb = new StringBuilder();
            for (int layer = 0; layer < 32; layer++)
            {
                probe.layer = layer;
                cam.cullingMask = 1 << layer;
                cam.Render();
                RenderTexture.active = tmp;
                tex.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
                tex.Apply();
                var px = tex.GetPixels32(0);
                bool hit = false;
                for (int i = 0; i < px.Length; i++) if (px[i].r > 140 && px[i].b > 140 && px[i].g < 110) { hit = true; break; }
                result[layer] = hit;
                sb.Append($"{layer}={(hit ? 1 : 0)}; ");
                RenderTexture.active = previous;
            }
            log.LogInfo($"[mirror:layer-probe] renderable={sb}");
            return result;
        }
        catch (Exception e) { log.LogError($"[mirror:layer-probe-failed] {e}"); return result; }
        finally
        {
            RenderTexture.active = previous;
            try { if (probe != null) UnityEngine.Object.Destroy(probe); } catch { }
            try { if (mesh != null) UnityEngine.Object.Destroy(mesh); } catch { }
            try { if (mat != null) UnityEngine.Object.Destroy(mat); } catch { }
            try { if (tmp != null) { tmp.Release(); UnityEngine.Object.Destroy(tmp); } } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
            try { if (camGo != null) UnityEngine.Object.Destroy(camGo); } catch { }
        }
    }

    private static void NoShadows(Renderer r) { r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false; }

    private static void ApplyTexture(Material mat, RenderTexture rt)
    {
        bool bound = false;
        foreach (var property in new[] { "_BaseMap", "_MainTex" })
        {
            if (!mat.HasProperty(property)) continue;
            mat.SetTexture(property, rt);
            mat.SetTextureScale(property, Vector2.one);
            mat.SetTextureOffset(property, Vector2.zero);
            bound = true;
        }
        if (!bound) throw new InvalidOperationException("Glass shader has no compatible texture property");
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0);
    }

    private static Mesh BuildQuad(float w, float h)
    {
        var mesh = new Mesh { name = "AlterumMirrorQuad" };
        try
        {
            float x = w * .5f, y = h * .5f;
            mesh.vertices = new Il2CppStructArray<Vector3>(new[] { new Vector3(-x,-y,0), new Vector3(x,-y,0), new Vector3(x,y,0), new Vector3(-x,y,0) });
            mesh.uv = new Il2CppStructArray<Vector2>(new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) });
            mesh.triangles = new Il2CppStructArray<int>(new[] { 0,1,2,0,2,3,0,2,1,0,3,2 });
            mesh.RecalculateBounds();
            return mesh;
        }
        catch { UnityEngine.Object.Destroy(mesh); throw; }
    }
}

public sealed class MirrorRuntime : IDisposable
{
    public GameObject Root;
    public Camera reflectCam;
    public RenderTexture rt;
    public Renderer quadRenderer;
    public readonly List<Renderer> mirrorRenderers = new();
    public IHostContext log;
    public Material glassMat, frameMat;
    public Mesh glassMesh, frameMesh;
    internal MirrorPlayerVisibility Visibility;
    internal CosmeticsRuntime Cosmetics;
    internal int BodyLayer = -1;
    internal int Generation;
    private bool[] _rendererStates;
    private int _frames, _failures;
    private bool _disposed;

    internal void PrepareRendererStates() => _rendererStates = new bool[mirrorRenderers.Count];

    // The mirror camera may only add layer 23 (TV_PlayerModelMesh, shared with the game's own
    // third-person meshes) on top of what the source camera already renders, and only while a
    // cosmetic is actually present. The bit the source camera already has is left exactly as the
    // original mirror had it; the extra admission is tracked so it can be withdrawn again.
    internal const int CosmeticLayer = 23;
    private int _baseMask;
    private bool _baseHasCosmeticLayer;
    private bool _ownCosmeticBit;
    private bool _cosmeticGateLogged;

    internal void PrepareCosmeticLayerGate()
    {
        if (reflectCam == null) return;
        _baseMask = reflectCam.cullingMask;
        _baseHasCosmeticLayer = (_baseMask & (1 << CosmeticLayer)) != 0;
        _ownCosmeticBit = false;
        _cosmeticGateLogged = false;
    }

    internal void SyncCosmeticLayer()
    {
        if (_disposed || reflectCam == null) return;
        bool want;
        try { want = Cosmetics != null && Cosmetics.CosmeticsInUse; } catch { want = false; }
        // Only a bit this mirror added is ours to withdraw. A bit the source camera already had is
        // part of the unmerged mirror's mask and is not this gate's business.
        bool shouldOwn = want && !_baseHasCosmeticLayer;
        if (shouldOwn == _ownCosmeticBit && _cosmeticGateLogged) return;
        int bit = 1 << CosmeticLayer;
        if (shouldOwn != _ownCosmeticBit)
        {
            if (shouldOwn) reflectCam.cullingMask |= bit; else reflectCam.cullingMask &= ~bit;
            _ownCosmeticBit = shouldOwn;
        }
        string action = !_cosmeticGateLogged ? "unchanged" : (shouldOwn ? "added" : "removed");
        _cosmeticGateLogged = true;
        try
        {
            log.LogInfo($"[mirror:cosmetic-layer] cosmeticsInUse={want} baseHadBit{CosmeticLayer}={_baseHasCosmeticLayer} " +
                        $"ownBit={action} extra=0x{(reflectCam.cullingMask & ~(_baseMask)):X8} " +
                        $"mask=0x{reflectCam.cullingMask:X8} layer{CosmeticLayer}Census={LayerCensus(CosmeticLayer)}");
        }
        catch { }
    }

    // Renderer census for one layer: live, enabled renderers, split into mod-owned cosmetics and
    // everything else. This is the evidence that a wider mask does not draw native objects.
    internal static string LayerCensus(int layer)
    {
        int total = 0, cosmetic = 0;
        var names = new StringBuilder();
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i]?.TryCast<Renderer>();
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || go.layer != layer || !go.activeInHierarchy || !r.enabled) continue;
                bool mod = CosmeticMarkers.IsCosmetic(r);
                if (mod) cosmetic++;
                if (total < 12) names.Append(total == 0 ? "" : "; ").Append(go.name).Append(mod ? "(mod)" : "(native)");
                total++;
            }
        }
        catch { }
        return $"count={total} cosmetic={cosmetic} native={total - cosmetic} [{names}]";
    }

    public void Tick()
    {
        if (_disposed || _failures >= 3 || reflectCam == null || rt == null || quadRenderer == null || !rt.IsCreated()) return;
        SyncCosmeticLayer();
        int saved = 0;
        IDisposable cosmeticScope = null;
        try
        {
            for (int i = 0; i < mirrorRenderers.Count; i++)
            {
                var r = mirrorRenderers[i];
                _rendererStates[i] = r != null && r.enabled;
                saved = i + 1;
                if (r != null) r.enabled = false;
            }
            // Cosmetics are excluded from MirrorPlayerVisibility's sweep (marker), so their screen
            // policy (first person hides them) would also hide them here. The scope enables the
            // mirror-eligible cosmetic renderers for exactly this render and restores screen state.
            cosmeticScope = Cosmetics?.BeginMirrorView(reflectCam);
            reflectCam.Render();
            _frames++;
            if (_frames == 1) log.LogInfo($"[mirror:render-ok] frame={Time.frameCount} fixedCamera={reflectCam.transform.position} RT={rt.width}x{rt.height}; Render returned (not proof of body pixels)");
        }
        catch (Exception e)
        {
            _failures++;
            log.LogError($"[mirror:render-failed] failures={_failures}/3 shader={glassMat?.shader?.name} RTcreated={rt?.IsCreated()} stoppingAt=3: {e}");
        }
        finally
        {
            try { cosmeticScope?.Dispose(); } catch (Exception e) { log.LogWarning($"[mirror:restore-failed] cosmetic scope: {e}"); }
            for (int i = 0; i < saved; i++)
                try { if (mirrorRenderers[i] != null) mirrorRenderers[i].enabled = _rendererStates[i]; }
                catch (Exception e) { log.LogWarning($"[mirror:restore-failed] glass renderer {i}: {e}"); }
        }
        // Bounded acceptance diagnostics, never part of body visibility control. Kept unconditional:
        // this RT dump is the only proof-of-pixels channel, and Debug/Screenshots is off in both live
        // instances. Filenames carry the mirror generation so evidence stops overwriting itself.
        if (_frames == 90 || _frames == 180) DumpRenderTexture(_frames);
    }

    private void DumpRenderTexture(int frame)
    {
        var previous = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            var png = ImageConversion.EncodeToPNG(tex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, $"mirror_static_g{Generation}_{frame}.png");
            File.WriteAllBytes(path, bytes);
            log.LogInfo($"[mirror:rt-dump] renders={frame} fixedCamera={reflectCam.transform.position} -> {path} ({bytes.Length} bytes)");
            Visibility?.Diagnose(reflectCam);
        }
        catch (Exception e) { log.LogError($"[mirror:rt-dump-failed] {e}"); }
        finally { RenderTexture.active = previous; if (tex != null) UnityEngine.Object.Destroy(tex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Visibility?.Disarm();
        Cleanup("disable renderers", () => { foreach (var r in mirrorRenderers) if (r != null) r.enabled = false; });
        Cleanup("camera", () => { if (reflectCam != null) { reflectCam.enabled = false; reflectCam.targetTexture = null; } });
        try { Visibility?.Dispose(); }
        finally
        {
            Visibility = null;
            Cleanup("RT", () => { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } });
            Cleanup("glass material", () => { if (glassMat != null) UnityEngine.Object.Destroy(glassMat); });
            Cleanup("frame material", () => { if (frameMat != null) UnityEngine.Object.Destroy(frameMat); });
            Cleanup("glass mesh", () => { if (glassMesh != null) UnityEngine.Object.Destroy(glassMesh); });
            Cleanup("frame mesh", () => { if (frameMesh != null) UnityEngine.Object.Destroy(frameMesh); });
            Cleanup("root", () => { if (Root != null) UnityEngine.Object.Destroy(Root); });
            log?.LogInfo($"[mirror:disposed] renders={_frames} failures={_failures}");
            rt = null; glassMat = null; frameMat = null; glassMesh = null; frameMesh = null;
            reflectCam = null; quadRenderer = null; Root = null; mirrorRenderers.Clear();
        }
    }

    private void Cleanup(string step, Action action)
    {
        try { action(); } catch (Exception e) { log?.LogWarning($"[mirror:restore-failed] {step}: {e}"); }
    }
}
