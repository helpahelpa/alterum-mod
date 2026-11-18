using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace AlterumModelSwap.Hot;

// "Reel of Fortune" signpost for the Alterum lobby: a wall-kiosk slab built from closed boxes,
// swept neon and flat art quads, standing on the lobby floor.
//
// Modelled from ~/assets/monorepo/alterum-data/lobby-model/refs/ (the cabinet from "How to Fish"). Layout follows
// the 1080p second measurement, which supersedes parts of REFS.md:
//
//   cabinet   1.00 w x 0.34 d x 2.95 h  (taller than the player)
//   screen    square 0.92 (cyan tube outer), bottom edge at 1.50 -> 1.50 .. 2.42
//   tray      0.70 x 0.52 recess with a 3 cm pale bevel, 2.42 .. 2.94 (below the 2.95 top)
//   arrow     amber, ~0.4 tall, curling at the sign's upper left, head pointing right
//
// There is no lever, no coin slot and no legs: it is a signpost. The sign face is four bands
// fitted *inside* the neon aperture, over a maroon plate: title / instruction / reel housing /
// footer. Every pixel of it is generated at load time by ReelArt (hand-written 5x7 bitmap font,
// procedural plate and rules) - no reference-video crop, no screenshot, no OS font.
//
// The item window on the housing is ONE horizontal row of cards - a CSGO-case read, not three
// vertical drums. The row is a procedural strip of Alterum item-icon symbols repeated over sixteen
// cards; it scrolls by animating the material's _BaseMap u offset behind a fixed centre marker,
// with an end fade from a slice of per-slice tints (no transparency) and a short overshoot/click.
// E or a `reel.request` file starts the spin. A spin grants nothing and changes no inventory state.
//
// The clip has no bloom, so the neon spill is faked with additive gradient quads around the tube.
//
// Materials (7): one URP/Lit clone for all shaded geometry (per-part colours through a
// MaterialPropertyBlock), one URP/Unlit for the neon and flat colour, one URP/Unlit with the
// generated sign atlas, one URP/Unlit additive for the glow, and one URP/Unlit item-strip material
// shared by every card slice (only the per-slice tint differs). Every mesh, material and texture
// created here is destroyed by Dispose(); the root GameObject owns the rest.
internal static class LobbyPropFactory
{
    // Root object name of every booth. It is the spawner's scene census key and the dispatcher's
    // "is this a booth?" test, so it lives in one place.
    internal const string RootName = "AlterumReelOfFortune";
    // The reel is map-only now: there is no lobby anchor. Every booth is created at a saved
    // anchor placement or at the player (`reel.request place=here`), with its floor Y raycast at
    // runtime. There is no placement shortcut key.
    private const float ProbeFromY = 3.30f;   // above the prop's own head height
    private const float ProbeDown = 8.00f;

    // ---- authoritative dimensions (1080p measurement) ----
    private const float W = 1.00f;            // cabinet width
    private const float D = 0.34f;            // cabinet depth: wall-mounted kiosk, not 0.60
    private const float H = 2.95f;            // total height (above the ~1.8 m player)
    private const float SignSize = 0.92f;     // cyan tube outer edge, square
    private const float SignBottom = 1.50f;   // screen bottom -------------------------------
    private const float SignMidY = SignBottom + SignSize * 0.5f;   // 1.96
    private const float SignTop = SignBottom + SignSize;           // 2.42
    internal const float SignBottomEdge = SignBottom;
    internal const float SignTopEdge = SignTop;
    internal const float TubeInnerEdge = SignSize * 0.5f - TubeSec * 2f;   // 0.428
    internal const float GlowZ = 0.189f;
    private const float FaceZ = 0.160f;       // front face of the planked body
    private const float BezelZ = 0.185f;      // front of the sign bezel and neon tube
    private const float TubeHalf = 0.444f;    // tube path centreline half-extent -> outer 0.460
    private const float TubeSec = 0.016f;     // tube section half -> 3.2 cm square
    private const float PlateHalf = 0.390f;   // maroon art plate, 0.78 square
    private const float ArtHalf = 0.380f;     // art quads, 0.76 wide (1 cm plate margin)
    private const float ArtTop = 2.343f;      // art stack top, 7 mm below the plate top
    private const float TrayOuterW = 0.70f;   // tray bevel outer
    private const float TrayOuterH = 0.52f;   // tray bevel outer height -> 2.42 .. 2.94
    private const float TrayBevel = 0.03f;    // thin pale bevel
    private const float TrayBottom = SignTop;                 // 2.42
    private const float TrayTop = TrayBottom + TrayOuterH;    // 2.94, 1 cm under the top
    private const float TrayHalfW = TrayOuterW * 0.5f;        // 0.35
    private const float TrayOpenHalfW = TrayHalfW - TrayBevel;   // 0.32
    private const float TrayOpenBottom = TrayBottom + TrayBevel; // 2.45
    private const float TrayOpenTop = TrayTop - TrayBevel;       // 2.91
    private const float TrayDepth = 0.056f;   // cavity back is the core face at z = 0.104

    // Palette (sRGB as authored).
    private static readonly Color Substrate = Hex(0x1E151B);
    private static readonly Color PlankA = Hex(0x3A2832);
    private static readonly Color PlankB = Hex(0x352430);
    private static readonly Color PlankC = Hex(0x412E38);
    private static readonly Color PlankD = Hex(0x31212C);
    private static readonly Color Batten = Hex(0x47323D);
    private static readonly Color UpperFace = Hex(0x33242D);
    private static readonly Color BezelPlum = Hex(0x2A1820);
    private static readonly Color MaroonPlate = Hex(0x4E1A22);
    private static readonly Color PaleBevel = Hex(0x8E8079);   // sides (warm, as sampled)
    private static readonly Color BevelBottom = Hex(0xC6BBB2);
    private static readonly Color BevelTopFlat = Hex(0x5E5049);
    private static readonly Color BevelTop = Hex(0x453C39);
    
    private static readonly Color Cavity = Hex(0x211E2E);
    // Neon is deliberately not at full cyan: after the face became self-lit the tube was the
    // brightest thing on the prop. Kept a bright filament (NeonHot) inside a dimmer tube so the
    // screen reads as the focal point, not the frame.
    private static readonly Color NeonCyan = Hex(0x63D6EE);
    private static readonly Color NeonHot = Hex(0xB9F7FF);
    private static readonly Color AmberCore = Hex(0xFFD34A);
    private static readonly Color AmberHot = Hex(0xFFD874);
    private static readonly Color AmberLine = Hex(0x8A4A16);
    private static readonly Color AmberBar = Hex(0xFFB43A);
    private static readonly Color BarHot = Hex(0xF7EFC0);
    private static readonly Color White = Color.white;
    private static readonly Color GlowWide = new Color(0.28f, 0.72f, 0.90f, 0.16f);
    private static readonly Color GlowCore = new Color(0.55f, 0.98f, 1.15f, 0.46f);
    // Centre marker, two colours: the glyph goes on the pointers, the glow spills behind them.
    // Both used to be far hotter than they needed to be - the old marker was 1.42/1.30/0.94 cream
    // over a 52 mm additive bar at alpha 0.40, i.e. the brightest thing on the cabinet, and it sat
    // across the card face. The winner is carried by its rarity band and the lamp pulse (see
    // ApplyScrollEmphasis); the marker only has to say "centre", so its glyph sits one step below
    // the title gold and its glow is a third of the old width and half the old alpha.
    internal static readonly Color MarkerGlyph = new Color(1.12f, 1.03f, 0.78f, 1f);
    internal static readonly Color MarkerGlow = new Color(0.92f, 0.76f, 0.40f, 0.18f);

    // Sign atlas UV rects derived from the row constants ReelArt draws with, so a layout change
    // cannot leave a quad sampling the wrong band. The PNG is pasted top-down; v=1 is its top row.
    private static Rect UvRows(int top, int bottom) =>
        Rect.MinMaxRect(0f, 1f - bottom / (float)ReelArt.AtlasH, 1f, 1f - top / (float)ReelArt.AtlasH);
    private static readonly Rect UvTitle = UvRows(ReelArt.RowTitleTop, ReelArt.RowTitleBottom);
    private static readonly Rect UvBanner = UvRows(ReelArt.RowBannerTop, ReelArt.RowBannerBottom);
    private static readonly Rect UvReels = UvRows(ReelArt.RowReelsTop, ReelArt.RowReelsBottom);
    private static readonly Rect UvSkins = UvRows(ReelArt.RowFooterTop, ReelArt.RowFooterBottom);

    // Art band heights inside the aperture (sum to the 0.725 stack, + 0.035 amber bottom bar =
    // 0.760). Title and banner are the two lines of the title at the spawn distance: each band is
    // 0.138 m, which is exactly 186 atlas rows on a 0.760 m wide face, so glyphs are square and a
    // 24-atlas-pixel stroke lands on one 640x360 screen pixel at 4.6 m.
    private const float BandTitle = 0.138f;
    private const float BandBanner = 0.138f;
    private const float BandReels = ReelArt.BandReels;
    private const float BandSkins = 0.112f;
    private const float BandAmber = 0.035f;

    // ---- the item window -------------------------------------------------------------
    // ONE horizontal row instead of three vertical drums. 0.72 m of window over 0.24 m cards shows
    // exactly three cards: the strip is a row of tiles with a fixed centre marker, not a ticker.
    internal const float ScrollWindowW = ReelArt.ScrollWindowW;
    internal const float ScrollWindowH = ReelArt.ScrollWindowH;
    internal const float ScrollCardW = 0.240f;
    // Texture u the window samples: three of the sixteen cards.
    internal const float ScrollUScale = 3f / ReelArt.Cells;
    internal const float ScrollUCard = 1f / ReelArt.Cells;
    private const float ScrollZ = 0.1830f;       // in front of the 0.1815 art stack, inside the bezel
    private const float ScrollFadeZ = 0.1842f;   // alpha ramp over the row (blended, queue 3000)
    internal const float MarkerZ = 0.1848f;      // the marker pointers sit in front of the cards
    // Pointer geometry. The card window is 0.300 m tall (half 0.150) and the recessed well behind
    // it is 0.318 (half 0.159), so there is a 9 mm dark band above and below the card face; the
    // pointer pair lives entirely inside that band, which is what keeps the winning card's icon
    // and code unobstructed (verified: filmstrips/reel_scroll.png of the previous build had the
    // old rule straight down the middle of the settled card).
    internal const float MarkPointerApex = 0.0005f;   // clear of the card face edge
    internal const float MarkPointerLength = 0.0075f; // apex -> base, inside the well's inner shadow
    internal const float MarkPointerHalfWidth = 0.0135f;
    // Flanking ticks at the pointer base: the pair turns the lone triangle into a bracket across the
    // 9 mm gap (`---v---`), which is what makes the mark read at the capture size. They sit in the
    // same gap, above/below the card face, never on it.
    internal const float MarkTickInner = 0.0170f;
    internal const float MarkTickOuter = 0.0480f;
    internal const float MarkTickHeight = 0.0048f;
    internal const float MarkTickY = 0.1556f;         // centre of the tick bar, from midY
    internal const float MarkGlowHalfLength = 0.0300f;
    internal const float MarkGlowWidth = 0.0140f;
    internal const float ScrollSpinSeconds = 3.00f;
    internal const float ScrollClickSeconds = 0.18f;
    internal const float ScrollOvershootCells = 0.06f;
    // Interaction: the booth is a real lookable now. The prompt appears when the camera is aimed
    // within ~26 degrees of the sign face at up to 2.8 m, the same envelope the pull accepts.
    internal const float InteractRange = 2.80f;
    internal const float InteractDot = 0.90f;
    internal const string InteractPrompt = "Reel of Fortune  -  bet an item";
    // Full strip widths of travel: 3 x 16 cards. "Several strip widths" is what sells it as a spin
    // rather than a nudge, and it is cheap because the strip only moves a texture offset.
    internal const int ScrollTravelCells = 3 * ReelArt.Cells;
    // d(u) = 1 - (1-u)^p. p = 1.74 puts 70 % of the distance in the first 50 % of the time, i.e. the
    // last 30 % of the travel takes the last half of the spin (measured in the [reel:scroll] trace).
    internal const float ScrollEasePower = 1.74f;
    internal const string ReelRequestFile = "reel.request";

    // Read `reel.request` without a prop instance (map runs start with no booth): consume it only
    // when it asks for `place=here`, so a spin/film request is left for LobbyPropRuntime.Tick.
    internal static bool TryConsumePlaceRequest(IHostContext log)
    {
        if (log == null || string.IsNullOrEmpty(log.ScreensDir)) return false;
        string path = Path.Combine(log.ScreensDir, ReelRequestFile);
        try
        {
            if (!File.Exists(path)) return false;
            string text = File.ReadAllText(path);
            if (!HasPlaceHere(text)) return false;
            try { File.Delete(path); } catch { }
            return true;
        }
        catch { return false; }
    }

    internal static bool HasPlaceHere(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line.Substring(0, eq).Trim().Equals("place", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Substring(eq + 1).Trim().Equals("here", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // Self-lit face gain. The art quads are URP/Unlit (no room light reaches them), so the only
    // brightness control is the colour the atlas is multiplied by. Each renderer carries its own
    // tint in a MaterialPropertyBlock, which overrides the material's _BaseColor, so the gain is
    // applied there. 1.20 keeps the plate dark and pushes the gold title past 1.0 (HDR): the title
    // is the brightest thing on the face without making the panel itself glow like a bulb.
    internal const float SignTintGain = 1.20f;
    // Cards are URP/Unlit too, so their gain is a MaterialPropertyBlock tint as well. The slice
    // tints multiply this, so the centre of the row sits at 1.15 and the ends fade to the recess.
    internal const float ScrollTintGain = 1.15f;

    public static LobbyPropRuntime Create(IHostContext log, float anchorX, float anchorZ, float yaw,
                                          float floorFromY = ProbeFromY, float floorDown = ProbeDown,
                                          float fallbackFloorY = 0.203f)
    {
        var rt = new LobbyPropRuntime { log = log, Yaw = yaw, Anchor = new Vector3(anchorX, 0f, anchorZ) };
        try
        {
            float floorY = GroundY(log, anchorX, anchorZ, floorFromY, floorDown, fallbackFloorY);
            rt.Anchor = new Vector3(anchorX, floorY, anchorZ);

            rt.Root = new GameObject(RootName);
            rt.Root.transform.position = rt.Anchor;
            rt.Root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            rt.Root.transform.localScale = Vector3.one;

            rt.litMaterial = CreateLitMaterial(log);
            rt.neonMaterial = CreateUnlitMaterial(log, "AlterumPropNeon", NeonCyan);
            rt.signMaterial = CreateSignMaterial(log, out rt.signTexture, out rt.signWidth, out rt.signHeight);
            rt.glowMaterial = CreateGlowMaterial(log, out rt.glowTexture);
            rt.materials.Add(rt.litMaterial);
            rt.materials.Add(rt.neonMaterial);
            rt.materials.Add(rt.signMaterial);
            rt.materials.Add(rt.glowMaterial);
            CreateScroll(log, rt);
            rt.fadeMaterial = CreateFadeMaterial(log, rt);
            rt.materials.Add(rt.fadeMaterial);

            Build(rt);
            // Each booth owns its own interaction anchor: a trigger box at the sign face (the same
            // box the lobby interactable used). It is a trigger, so the ground/headroom/room probes
            // (all QueryTriggerInteraction.Ignore) and player movement are unaffected.
            rt.AttachTrigger(log);
            rt.Measure(log);
            ProbeHeadroom(log, anchorX, anchorZ);
            ProbeRoom(log, rt.Anchor);
            log.LogInfo($"[prop] Reel of Fortune placed pose={rt.Anchor} euler=(0,{yaw:0.##},0) " +
                        $"floorY={floorY:F3} parts={rt.PartCount} tris={rt.TriangleCount} " +
                        $"renderers={rt.Renderers.Count} materials={rt.materials.Count} colliders={rt.Root.GetComponentsInChildren(Il2CppType.Of<Collider>()).Length} " +
                        $"spec=1.00x0.34x2.95 sign={SignSize:F2}@y{SignBottom:F2} tray={TrayOuterW:F2}x{TrayOuterH:F2}@y{TrayBottom:F2}-{TrayTop:F2}");
            return rt;
        }
        catch (Exception e)
        {
            log.LogError($"[prop] creation failed: {e}");
            rt.Dispose();
            return null;
        }
    }

    // Raycast the lobby floor under the anchor. Falls back to the mirror's floor (0.20 m) if the
    // probe hits nothing, so a missing collider can never float the prop 2 m up.
    internal static float GroundY(IHostContext log, float x, float z,
                                 float fromY = ProbeFromY, float down = ProbeDown, float fallbackY = 0.203f)
    {
        var from = new Vector3(x, fromY, z);
        float best = float.MaxValue;
        var sb = new StringBuilder();
        try
        {
            var hits = Physics.RaycastAll(from, Vector3.down, down, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                string path = NamePath(c.transform);
                if (sb.Length < 400) sb.Append($"'{path}'@y={from.y - hits[i].distance:F3}d={hits[i].distance:F2}; ");
                if (IsPlayerPart(c.transform)) continue;
                if (hits[i].distance < 0.10f) continue;
                // Anything less than 1 m below the 3.3 m start is an overhead collider (the lobby
                // has one at y 2.62), never the floor.
                if (hits[i].distance < 1.00f) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
        }
        catch (Exception e) { log.LogWarning($"[prop:floor] raycast failed: {e.Message}"); }
        log.LogInfo($"[prop:floor] probe=({x:F2},{fromY:F2},{z:F2}) down={down:F1} hits=[{sb}]");
        if (best < float.MaxValue)
        {
            log.LogInfo($"[prop:floor] ground={fromY - best:F3} (fallback {fallbackY:F3})");
            return fromY - best;
        }
        log.LogWarning($"[prop:floor] no ground hit; using fallback floor height {fallbackY:F3}");
        return fallbackY;
    }

    // Re-run the room/headroom census after a live re-anchor (PropView.Move).
    internal static void Probes(IHostContext log, float x, float z, Vector3 anchor)
    {
        ProbeHeadroom(log, x, z);
        ProbeRoom(log, anchor);
    }

    // Upward census: the 0.52 m tray sits at 2.42-2.94 and the lobby has a canopy at 2.62 over the
    // nominal anchor, so the prop needs a spot with >= 3.2 m of headroom to be seen whole.
    private static void ProbeHeadroom(IHostContext log, float x, float z)
    {
        var sb = new StringBuilder();
        try
        {
            float[] off = { -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f };
            for (int i = 0; i < off.Length; i++)
            {
                for (int k = 0; k < off.Length; k++)
                {
                    var from = new Vector3(x + off[i], 1.5f, z + off[k]);
                    var hits = Physics.RaycastAll(from, Vector3.up, 6f, ~0, QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue;
                    for (int h = 0; h < hits.Length; h++)
                    {
                        var c = hits[h].collider;
                        if (c == null || IsPlayerPart(c.transform) || hits[h].distance < 0.2f) continue;
                        if (hits[h].distance < best) best = hits[h].distance;
                    }
                    float free = best < float.MaxValue ? 1.5f + best : 9.99f;
                    sb.Append($"{off[i]:+0.0;-0.0;0}/{off[k]:+0.0;-0.0;0}={free:F2} ");
                }
            }
        }
        catch (Exception e) { log.LogWarning($"[prop:headroom] probe failed: {e.Message}"); }
        log.LogInfo($"[prop:headroom] from y=1.5 up 6 m, grid around ({x:F2},{z:F2}): [{sb}]");
    }

    // One-off room census so the capture cameras can be placed without flying into a wall.
    private static void ProbeRoom(IHostContext log, Vector3 at)
    {
        var sb = new StringBuilder();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var origin = at + Vector3.up * 1.2f;
                var hits = Physics.RaycastAll(origin, dir, 10f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; string name = "none";
                for (int k = 0; k < hits.Length; k++)
                {
                    var c = hits[k].collider;
                    if (c == null || IsPlayerPart(c.transform)) continue;
                    if (hits[k].distance < best) { best = hits[k].distance; name = NamePath(c.transform); }
                }
                sb.Append($"{(int)(a * Mathf.Rad2Deg)}deg={(best < 9.99f ? best.ToString("F2") : "-")}:{name}; ");
            }
        }
        catch (Exception e) { log.LogWarning($"[prop:room] probe failed: {e.Message}"); }
        log.LogInfo($"[prop:room] origin={at} y+1.2 fan=[{sb}]");
    }

    private static bool IsPlayerPart(Transform t)
    {
        int guard = 0;
        while (t != null && guard++ < 12)
        {
            if (t.name != null && t.name.IndexOf("Owner: 0", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            t = t.parent;
        }
        return false;
    }

    internal static string NamePath(Transform t)
    {
        if (t == null) return "null";
        var p = t.name ?? "";
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; p = (t.name ?? "") + "/" + p; }
        return p;
    }

    // ---- materials -------------------------------------------------------------------------

    // Cloning a material the game already renders keeps a working URP/Lit variant; Shader.Find
    // alone can return a shader whose Lit variants were stripped (same trick as CosmeticsRuntime).
    private static Material CreateLitMaterial(IHostContext log)
    {
        Material template = null;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Material>());
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i].TryCast<Material>();
                if (m == null) continue;
                var sh = m.shader;
                if (sh == null || sh.name != "Universal Render Pipeline/Lit") continue;
                if (!m.HasProperty("_BaseMap")) continue;
                if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) continue;
                template = m;
                log.LogInfo($"[prop:shader] cloning game material '{m.name}' shader='{sh.name}'");
                break;
            }
        }
        catch (Exception e) { log.LogWarning($"[prop:shader] template scan failed: {e.Message}"); }

        Material mat;
        if (template != null) mat = new Material(template);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            log.LogWarning($"[prop:shader] no game URP/Lit template; Shader.Find found={sh != null}");
            mat = new Material(sh);
        }
        // CosmeticsRuntime's template scan skips materials whose name starts with "Cosmetic_"
        // ("ours"); use the same mod-owned prefix so this material can never be chosen as the
        // template the cosmetics track clones ahead of a game material.
        mat.name = "Cosmetic_PropLit";
        // The template carries the party hat's dot atlas; a solid-colour prop must not inherit it.
        try
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
            if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", null);
        }
        catch (Exception e) { log.LogWarning($"[prop:material] clearing the template maps failed: {e.Message}"); }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", PlankA);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", PlankA);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.22f);
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.SetColor("_EmissionColor", Color.black);
            try { mat.DisableKeyword("_EMISSION"); } catch { }
        }
        log.LogInfo($"[prop:material] lit '{mat.name}' shader='{mat.shader?.name}' base=(0.27,0.19,0.24); per-part colours via MaterialPropertyBlock");
        return mat;
    }

    private static Material CreateUnlitMaterial(IHostContext log, string name, Color color)
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null || !sh.isSupported) sh = Shader.Find("Universal Render Pipeline/Lit");
        var mat = new Material(sh) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        SetSurfaceOpaque(mat);
        // Flat art and neon ribbons are single-sheet meshes; showing both sides removes any
        // winding mistake from a hand-built quad without changing how the boxes read.
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        var read = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.clear;
        log.LogInfo($"[prop:material] unlit '{mat.name}' shader='{mat.shader?.name}' requested=({color.r:F2},{color.g:F2},{color.b:F2}) readback=({read.r:F2},{read.g:F2},{read.b:F2}) cull=off (HDR value drives bloom; unlit has no _Emission)");
        return mat;
    }

    private static void SetSurfaceOpaque(Material mat)
    {
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
        if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
        mat.renderQueue = -1;
    }

    // Additive glow: a white texture whose alpha is a smooth falloff across one axis, blended
    // (SrcAlpha, One) so the cyan tint only ever adds light. The clip has no bloom pass, so this
    // is the spill around each tube.
    private static Material CreateGlowMaterial(IHostContext log, out Texture2D texture)
    {
        texture = CreateGlowTexture(log);
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "AlterumPropGlow" };
        bool additive = true;
        try
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texture);
            else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", texture);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", White);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", White);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); else additive = false;
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.One); else additive = false;
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (additive)
            {
                try { mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
                mat.renderQueue = 3000;
            }
            else
            {
                // No blend-mode properties on this shader: keep the quads opaque-looking instead
                // of turning them into solid rectangles, and say so.
                SetSurfaceOpaque(mat);
            }
        }
        catch (Exception e) { log.LogWarning($"[prop:material] glow setup failed: {e.Message}"); }
        log.LogInfo($"[prop:material] glow '{mat.name}' shader='{mat.shader?.name}' additive={additive} " +
                    $"hasSrc={mat.HasProperty("_SrcBlend")} hasDst={mat.HasProperty("_DstBlend")} rq={mat.renderQueue} " +
                    $"tex={(texture != null ? texture.width + "x" + texture.height : "null")}");
        return mat;
    }

    // 64x64 white with alpha = smooth falloff away from the horizontal centre line. Filled with
    // LoadRawTextureData (the same path the sign atlas uses; SetPixels32 is awkward in IL2CPP).
    private static Texture2D CreateGlowTexture(IHostContext log)
    {
        const int N = 64;
        var bytes = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
        {
            float v = Mathf.Abs((y + 0.5f) / N * 2f - 1f);        // 0 at the centre line, 1 at the edge
            float a = Mathf.Pow(Mathf.Clamp01(1f - v), 1.65f);
            for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 4;
                bytes[i] = 255; bytes[i + 1] = 255; bytes[i + 2] = 255;
                bytes[i + 3] = (byte)Mathf.RoundToInt(a * 255f);
            }
        }
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false, false) { name = "AlterumPropGlowRamp" };
        tex.LoadRawTextureData(new Il2CppStructArray<byte>(bytes));
        tex.Apply(false, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        log.LogInfo($"[prop:texture] glow ramp {N}x{N} rgba32 centreA=a[32]={bytes[(32 * N + 32) * 4 + 3]} edgeA={bytes[32 * 4 + 3]}");
        return tex;
    }

    private static Material CreateSignMaterial(IHostContext log, out Texture2D texture, out int width, out int height)
    {
        texture = null; width = height = 0;
        // Self-lit by construction: URP/Unlit samples the generated atlas and ignores the room, so
        // the face is legible in the dim tent interior. The brightness multiplier is applied to the
        // per-renderer tint (SignTintGain), because each art quad carries a MaterialPropertyBlock
        // _BaseColor that overrides this material's colour. Unlit is also outside the cosmetics
        // template scan, which only clones shader 'Universal Render Pipeline/Lit'.
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "AlterumPropSign" };
        try
        {
            texture = ReelArt.BuildSignAtlas(log);
            width = texture.width; height = texture.height;
            BindBaseMap(mat, texture);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            SetSurfaceOpaque(mat);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            string boundText = "none";
            try
            {
                var b = mat.GetTexture("_BaseMap");
                boundText = b != null ? $"_BaseMap={b.name}({b.width}x{b.height}) wrap={texture.wrapMode} filter={texture.filterMode}" : "_BaseMap=null";
            }
            catch (Exception e) { boundText = "read-back failed: " + e.Message; }
            log.LogInfo($"[prop:texture] bind hasBaseMap={mat.HasProperty("_BaseMap")} hasMainTex={mat.HasProperty("_MainTex")} " +
                        $"hasBaseColor={mat.HasProperty("_BaseColor")} {boundText} mips={texture.mipmapCount} " +
                        $"renderQueue={mat.renderQueue} pass={mat.passCount} uvTitle={Fmt(UvTitle)} uvBanner={Fmt(UvBanner)} " +
                        $"uvReels={Fmt(UvReels)} uvSkins={Fmt(UvSkins)}");
            log.LogInfo($"[prop:material] selflit '{mat.name}' shader='{mat.shader?.name}' atlas={texture.name} " +
                        $"tintGain={SignTintGain:F2} (per-renderer _BaseColor; independent of room light)");
            return mat;
        }
        catch (Exception e)
        {
            log.LogError($"[prop:texture] sign atlas failed: {e}");
            if (texture != null) { UnityEngine.Object.Destroy(texture); texture = null; }
            throw;
        }
    }

    // Fresh entropy for a deck / shuffle seed. Only the seed comes from Unity's RNG; the draw itself
    // is ReelDeck's own xorshift, so the sequence is the one the headless sample measures.
    internal static int Seed() => (int)(UnityEngine.Random.Range(0, int.MaxValue) ^ (Environment.TickCount * 2654435761u));

    private static void BindBaseMap(Material mat, Texture2D texture)
    {
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texture);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", texture);
    }

    // One strip texture and ONE material: the whole row scrolls together, and the per-slice tints
    // that fade the ends are MaterialPropertyBlock colours on the renderers, not materials. The
    // offset is a texture transform (_BaseMap_ST), cheaper than rebuilding mesh UVs every frame.
    private static void CreateScroll(IHostContext log, LobbyPropRuntime rt)
    {
        rt.poolIcons = ReelArt.LoadPoolIcons(log);
        rt.Cards = ReelDeck.Compose(ReelArt.Cells, Seed());
        rt.stripCache = new ReelArt.StripCache();
        rt.scrollTexture = ReelArt.BuildStrip(log, rt.Cards, rt.poolIcons, rt.stripCache, out rt.stripChainBytes);
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "AlterumPropItemStrip" };
        BindBaseMap(mat, rt.scrollTexture);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        SetSurfaceOpaque(mat);
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        mat.SetTextureScale("_BaseMap", new Vector2(ScrollUScale, 1f));
        mat.SetTextureOffset("_BaseMap", new Vector2(rt.InitialScrollOffset(), 0f));
        rt.scrollMaterials.Add(mat);
        rt.materials.Add(mat);
        Vector2 readScale = Vector2.one, readOffset = Vector2.zero;
        try { readScale = mat.GetTextureScale("_BaseMap"); readOffset = mat.GetTextureOffset("_BaseMap"); }
        catch (Exception e) { log.LogWarning($"[prop:scroll] scale/offset read-back failed: {e.Message}"); }
        log.LogInfo($"[prop:scroll] material '{mat.name}' shader='{mat.shader?.name}' tex={rt.scrollTexture.name} " +
                    $"wrap={rt.scrollTexture.wrapMode} scale=({readScale.x:F5},{readScale.y:F3}) " +
                    $"offset=({readOffset.x:F5},{readOffset.y:F3}) cards={ReelArt.Cells} visible=3 uPerCard={ScrollUCard:F5} cull=off");
        rt.InitScroll(log);
    }

    // Dark gradient overlay for the two ends of the row: an ordinary alpha-blended URP/Unlit quad
    // drawing a black strip whose alpha ramps up at both ends. The cards are opaque geometry behind
    // it and it draws at queue 3000, so it composites over them and under nothing else.
    private static Material CreateFadeMaterial(IHostContext log, LobbyPropRuntime rt)
    {
        rt.fadeTexture = ReelArt.BuildFadeRamp(log);
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "AlterumPropFade" };
        bool blended = true;
        try
        {
            BindBaseMap(mat, rt.fadeTexture);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); else blended = false;
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); else blended = false;
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (blended)
            {
                try { mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
                mat.renderQueue = 3000;
            }
            else SetSurfaceOpaque(mat);
        }
        catch (Exception e) { log.LogWarning($"[prop:material] fade setup failed: {e.Message}"); }
        log.LogInfo($"[prop:material] fade '{mat.name}' shader='{mat.shader?.name}' blended={blended} " +
                    $"hasSrc={mat.HasProperty("_SrcBlend")} hasDst={mat.HasProperty("_DstBlend")} rq={mat.renderQueue} " +
                    $"tex={(rt.fadeTexture != null ? rt.fadeTexture.width + "x" + rt.fadeTexture.height : "null")}");
        return mat;
    }

    // u offset that puts the centre of `cell` exactly under the marker.
    internal static float ScrollOffsetForCell(int cell) =>
        (cell + 0.5f) * ScrollUCard - ScrollUScale * 0.5f;

    // Cell under the marker at a given u offset.
    internal static int ScrollCellAt(float uOffset) => ReelArt.CellAtMarker(uOffset, ScrollUScale);

    // Card-position under the marker, in cards (fractional part 0.5 = a card is centred).
    internal static float ScrollMarkerCellPos(float uOffset) => (uOffset + ScrollUScale * 0.5f) / ScrollUCard;

    // Card-centre proximity 0..1: 1 when a card is dead centre under the marker, 0 in the gap
    // between two cards. Drives the marker pulse and the centre-card emphasis.
    internal static float ScrollCentreProximity(float uOffset)
    {
        float p = ScrollMarkerCellPos(uOffset);
        float f = p - Mathf.Floor(p);
        return Mathf.Clamp01(1f - Mathf.Abs(f - 0.5f) * 2f);
    }

    // The spin curve, shared by the live spin and the filmstrip seeks, so a still cannot show a
    // position the real spin never visits.
    internal static float ScrollEase(float u) => 1f - Mathf.Pow(1f - Mathf.Clamp01(u), ScrollEasePower);

    private static string Fmt(Rect r) => $"({r.xMin:F3},{r.yMin:F3})-({r.xMax:F3},{r.yMax:F3})";

    // ---- assembly --------------------------------------------------------------------------

    // Local coordinates: origin on the floor at the front centre, +Z is the face.
    private static void Build(LobbyPropRuntime rt)
    {
        // 1. core slab: the whole cabinet volume; its front face (z = 0.104) is the tray cavity back
        rt.AddBox("core", new Vector3(0f, H * 0.5f, -0.033f), new Vector3(W, H, 0.274f), Substrate);

        // 2. planked lower body, 0.00 - 1.50: six distinct faces, tone-shifted, with 6 mm seams so
        //    the core shows as a dark plank joint.
        var tones = new[] { PlankA, PlankB, PlankC, PlankA, PlankD, PlankB };
        const int planks = 6;
        const float seam = 0.010f;
        float plankW = (W - seam * (planks - 1)) / planks;
        for (int i = 0; i < planks; i++)
        {
            float x0 = -W * 0.5f + i * (plankW + seam);
            Color t = tones[i];
            // three strips: the edges of a board sit in their own shadow, the centre catches the key
            float edge = plankW * 0.18f;
            rt.AddBox($"plank{i}_l", new Vector3(x0 + edge * 0.5f, 0.75f, 0.130f), new Vector3(edge, 1.50f, 0.060f), Scale(t, 0.74f));
            rt.AddBox($"plank{i}_c", new Vector3(x0 + plankW * 0.5f, 0.75f, 0.130f), new Vector3(plankW - edge * 2f, 1.50f, 0.060f), t);
            rt.AddBox($"plank{i}_r", new Vector3(x0 + plankW - edge * 0.5f, 0.75f, 0.130f), new Vector3(edge, 1.50f, 0.060f), Scale(t, 0.86f));
        }
        // shadow band under the sign: the reference body is darkest right below the tube
        rt.AddBox("sign_shadow", new Vector3(0f, 1.485f, 0.166f), new Vector3(W, 0.050f, 0.012f), Hex(0x1A1218));
        // 3. horizontal batten + two vertical stiles, proud of the plank faces
        rt.AddBox("batten_mid", new Vector3(0f, 1.020f, 0.169f), new Vector3(W, 0.060f, 0.018f), Batten);
        rt.AddBox("stile_l", new Vector3(-0.300f, 0.750f, 0.169f), new Vector3(0.062f, 1.50f, 0.020f), Scale(Batten, 1.10f));
        rt.AddBox("stile_r", new Vector3(0.300f, 0.750f, 0.169f), new Vector3(0.062f, 1.50f, 0.020f), Scale(Batten, 1.10f));
        // 4. lower rail: a thin darker band at the sand line so the planks do not float
        rt.AddBox("rail", new Vector3(0f, 0.040f, 0.170f), new Vector3(W, 0.080f, 0.020f), Substrate);

        // 5. upper face beside/under the sign, 1.50 - 2.95 (the sign bezel sits on it)
        rt.AddBox("upper_face", new Vector3(0f, 1.960f, 0.130f), new Vector3(W, 0.920f, 0.060f), UpperFace);

        // 6. tray surround: four face panels around the 0.64 x 0.46 opening, so the tray is a real
        //    hole in the board rather than a frame sitting on it
        rt.AddBox("tray_face_above", new Vector3(0f, (TrayOpenTop + H) * 0.5f, 0.130f),
                  new Vector3(W, H - TrayOpenTop, 0.060f), UpperFace);
        rt.AddBox("tray_face_below", new Vector3(0f, (TrayBottom + TrayOpenBottom) * 0.5f, 0.130f),
                  new Vector3(W, TrayOpenBottom - TrayBottom, 0.060f), UpperFace);
        rt.AddBox("tray_face_left", new Vector3((-W * 0.5f - TrayOpenHalfW) * 0.5f, (TrayOpenBottom + TrayOpenTop) * 0.5f, 0.130f),
                  new Vector3(W * 0.5f - TrayOpenHalfW, TrayOpenTop - TrayOpenBottom, 0.060f), UpperFace);
        rt.AddBox("tray_face_right", new Vector3((W * 0.5f + TrayOpenHalfW) * 0.5f, (TrayOpenBottom + TrayOpenTop) * 0.5f, 0.130f),
                  new Vector3(W * 0.5f - TrayOpenHalfW, TrayOpenTop - TrayOpenBottom, 0.060f), UpperFace);

        // 7. the 3 cm pale bevel, flush on the face plane around the opening
        float zb = FaceZ + 0.0006f;
        rt.AddQuadRaw("bevel_top", P(-TrayHalfW, TrayOpenTop, zb), P(TrayHalfW, TrayOpenTop, zb),
                      P(TrayHalfW, TrayTop, zb), P(-TrayHalfW, TrayTop, zb), BevelTopFlat);
        rt.AddQuadRaw("bevel_bottom", P(-TrayHalfW, TrayBottom, zb), P(TrayHalfW, TrayBottom, zb),
                      P(TrayHalfW, TrayOpenBottom, zb), P(-TrayHalfW, TrayOpenBottom, zb), BevelBottom);
        rt.AddQuadRaw("bevel_left", P(-TrayHalfW, TrayOpenBottom, zb), P(-TrayOpenHalfW, TrayOpenBottom, zb),
                      P(-TrayOpenHalfW, TrayOpenTop, zb), P(-TrayHalfW, TrayOpenTop, zb), PaleBevel);
        rt.AddQuadRaw("bevel_right", P(TrayOpenHalfW, TrayOpenBottom, zb), P(TrayHalfW, TrayOpenBottom, zb),
                      P(TrayHalfW, TrayOpenTop, zb), P(TrayOpenHalfW, TrayOpenTop, zb), PaleBevel);

        // 8. bevelled inner lip: four trapezoids running back and in, brightest at the bottom where
        //    the light would sit, plus the near-black cavity behind
        float zi = FaceZ - TrayDepth;                       // 0.104
        float ix = TrayOpenHalfW - 0.048f, ib = TrayOpenBottom + 0.048f, it = TrayOpenTop - 0.048f;
        rt.AddQuadRaw("lip_top", P(-TrayOpenHalfW, TrayOpenTop, FaceZ), P(TrayOpenHalfW, TrayOpenTop, FaceZ),
                      P(ix, it, zi), P(-ix, it, zi), BevelTop);
        rt.AddQuadRaw("lip_bottom", P(-TrayOpenHalfW, TrayOpenBottom, FaceZ), P(TrayOpenHalfW, TrayOpenBottom, FaceZ),
                      P(ix, ib, zi), P(-ix, ib, zi), BevelBottom);
        rt.AddQuadRaw("lip_left", P(-TrayOpenHalfW, TrayOpenBottom, FaceZ), P(-TrayOpenHalfW, TrayOpenTop, FaceZ),
                      P(-ix, it, zi), P(-ix, ib, zi), PaleBevel);
        rt.AddQuadRaw("lip_right", P(TrayOpenHalfW, TrayOpenBottom, FaceZ), P(TrayOpenHalfW, TrayOpenTop, FaceZ),
                      P(ix, it, zi), P(ix, ib, zi), PaleBevel);
        rt.AddQuadRaw("cavity_back", P(-ix, ib, zi + 0.001f), P(ix, ib, zi + 0.001f),
                      P(ix, it, zi + 0.001f), P(-ix, it, zi + 0.001f), Cavity);

        // 9. sign bezel frame: dark plum, 0.88 outer with a 0.78 hole for the art plate
        rt.AddBox("bezel_top", new Vector3(0f, 2.375f, 0.1405f), new Vector3(0.880f, 0.050f, 0.081f), BezelPlum);
        rt.AddBox("bezel_bottom", new Vector3(0f, 1.545f, 0.1405f), new Vector3(0.880f, 0.050f, 0.081f), BezelPlum);
        rt.AddBox("bezel_left", new Vector3(-0.415f, SignMidY, 0.1405f), new Vector3(0.050f, 0.780f, 0.081f), BezelPlum);
        rt.AddBox("bezel_right", new Vector3(0.415f, SignMidY, 0.1405f), new Vector3(0.050f, 0.780f, 0.081f), BezelPlum);
        // 10. the maroon sign plate behind the art (the reference face colour, not near-black)
        rt.AddBox("sign_plate", new Vector3(0f, SignMidY, 0.140f), new Vector3(PlateHalf * 2f, PlateHalf * 2f, 0.080f), MaroonPlate);

        // 11. the four art bands + amber bottom bar, all inside the tube aperture. The reels band
        //     is the housing; the horizontal row of cards and its marker are separate quads in front.
        float y = ArtTop;
        y = AddArtQuad(rt, "title_art", y, BandTitle, UvTitle, Color.white);
        y = AddArtQuad(rt, "banner_art", y, BandBanner, UvBanner, Color.white);
        float reelsTop = y;
        y = AddArtQuad(rt, "reels_housing", y, BandReels, UvReels, Color.white);
        float reelsMid = (reelsTop + y) * 0.5f;
        AddScrollRow(rt, reelsMid);
        y = AddArtQuad(rt, "skins_art", y, BandSkins, UvSkins, new Color(0.90f, 0.90f, 0.90f));
        // 12. amber bottom bar with the cream hot edge (geometry: the atlas crop has none)
        rt.AddBox("amber_bar", new Vector3(0f, y - BandAmber * 0.5f, 0.1785f), new Vector3(ArtHalf * 2f, BandAmber, 0.005f), AmberBar);
        rt.AddQuadRaw("bar_hot", P(-ArtHalf, y, 0.1816f), P(ArtHalf, y, 0.1816f),
                      P(ArtHalf, y + 0.004f, 0.1816f), P(-ArtHalf, y + 0.004f, 0.1816f), BarHot);
        rt.LogBandEdges(ArtTop, y);

        // 13-14. neon: a swept square tube on a rounded-square path, plus a hotter core line
        var path = RoundedSquarePath(TubeHalf, 0.055f, 6);
        for (int i = 0; i < path.Count; i++) path[i] += new Vector2(0f, SignMidY);   // frame sits on the sign
        rt.AddTubePath("neon_tube", path, TubeSec, 0.169f, NeonCyan);
        rt.AddTubePath("neon_core", path, 0.0065f, 0.181f, NeonHot);

        // 15. bloom stand-in: two additive gradient skirts per tube side, tuned to read at 4 m.
        //     Kept inside the 1.00 m silhouette (x stays within +-0.50).
        AddGlowFrame(rt, 2.417f, 1.503f, 0.445f, 0.392f);

        // 16. the amber arrow: flat neon ribbons 3 cm proud of the tube face, curling at the sign's
        //     upper left with the head pointing right. Built from a swept arc + a triangular head.
        BuildArrow(rt);

        // 17. lights: a warm key so the planks read as warm wood rather than a silhouette, a cyan
        //     pool from the tube, and a small amber pool under the arrow.
        rt.AddLight("key_warm", new Vector3(-0.70f, 1.35f, 0.90f), new Color(1f, 0.80f, 0.63f), 2.1f, 4.5f);
        rt.AddLight("fill_cool", new Vector3(1.15f, 0.70f, 0.95f), new Color(0.42f, 0.66f, 0.82f), 0.55f, 4.5f);
        rt.AddLight("neon_light", new Vector3(0f, 1.70f, 0.55f), NeonCyan, 2.4f, 3.2f);
        rt.AddLight("arrow_light", new Vector3(-0.30f, 2.45f, 0.48f), new Color(1f, 0.78f, 0.28f), 1.5f, 2.0f);
    }

    private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

    // The item row and its marker: one quad for the whole row (one _BaseMap_ST offset), a black
    // alpha ramp over it for the two end fades, and the fixed marker in front. Local +x is the
    // viewer's LEFT (camera right is local -x); uvMode 2 in BuildQuad puts uv.xMin at local +x, so
    // texture u 0 lands at the viewer's left: the cards are not mirrored and a rising u offset
    // scrolls the row leftwards, the way a case opening reads.
    private static void AddScrollRow(LobbyPropRuntime rt, float midY)
    {
        float w = ScrollWindowW, h = ScrollWindowH;
        rt.AddScrollRowQuad("scroll_row", new Vector3(0f, midY, ScrollZ), new Vector2(w, h), ScrollTintGain);
        rt.scrollRowRenderer = rt.LastRenderer();
        rt.AddScrollFade("scroll_fade", new Vector3(0f, midY, ScrollFadeZ), new Vector2(w, h));
        rt.scrollFadeRenderer = rt.LastRenderer();
        rt.AddScrollMarker(midY);
        AddIdlePreview(rt, midY);
        if (rt.log != null)
            rt.log.LogInfo($"[prop:scroll] row {w:F3}x{h:F3}m at y={midY:F3} z={ScrollZ:F3} fade z={ScrollFadeZ:F3} " +
                           $"fadeEdge={ReelArt.FadeEdge:F2} cards={ReelArt.Cells} card={ScrollCardW:F3}m " +
                           $"uScale={ScrollUScale:F5} uPerCard={ScrollUCard:F5} travelPerSpin={ScrollTravelCells}cards " +
                           $"markerZ={MarkerZ:F4} marker=pointer-pair (edge+{MarkPointerApex:F4}..+" +
                           $"{MarkPointerApex + MarkPointerLength:F4}, {MarkPointerHalfWidth * 2f:F3}m wide) " +
                           $"overCardFace=none");
    }

    // The idle preview plate (item 5). It is a third quad in the window: its own texture and
    // material, shown only while the machine is idle, removed on every accepted spin. The three
    // cards come from the pool's eligible item rows - never from the settlement deck's order and
    // never by reshuffling it - so the deck, its odds and the strip stay untouched.
    internal const float PreviewZ = 0.1845f;   // in front of the fade (0.1842), behind the marker

    private static void AddIdlePreview(LobbyPropRuntime rt, float midY)
    {
        rt._previewPick = PickPreviewItems(3);
        var cards = new ReelCard[rt._previewPick.Length];
        var ids = new StringBuilder();
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i] = new ReelCard { Type = ReelCardType.Item, Item = rt._previewPick[i] };
            if (ids.Length > 0) ids.Append(',');
            ids.Append(ReelPool.All[rt._previewPick[i]].Id);
        }
        rt._previewItems = ids.Length > 0 ? ids.ToString() : "none";
        rt.previewTexture = ReelArt.BuildPreviewPlate(rt.log, rt.Cards, cards, rt.poolIcons, out string note);
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "AlterumPropIdlePreview" };
        BindBaseMap(mat, rt.previewTexture);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        SetSurfaceOpaque(mat);
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        rt.previewMaterial = mat;
        rt.materials.Add(mat);
        // Exactly the card window, so the well's own gold trim stays visible around the preview and
        // the three cards sit where the strip's three cards sit.
        rt.AddPreviewQuad("scroll_preview", new Vector3(0f, midY, PreviewZ),
                          new Vector2(ScrollWindowW, ScrollWindowH));
        rt._previewBuilt = rt.previewGo != null && rt.previewTexture != null;
        rt.log?.LogInfo($"[prop:preview] quad 'scroll_preview' {ScrollWindowW:F3}x{ScrollWindowH:F3}m " +
                        $"at y={midY:F3} z={PreviewZ:F4} tex={rt.previewTexture?.name} items=[{rt._previewItems}] " +
                        $"built={rt._previewBuilt} {note}");
    }

    // Three item rows the deck may actually name: the live map's own spawn table when ReelEligible
    // has resolved one (ReelDeck.AllowedItems), else the whole usable pool; excluded rows (the
    // quest lantern and rune) are never offered, and rows with no icon are only used when there is
    // nothing else. Deterministic and deck-independent: this never draws from, reorders or mutates
    // the settlement deck.
    internal static int[] PickPreviewItems(int count)
    {
        var allowed = ReelDeck.AllowedItems;
        var withIcon = new List<int>();
        var any = new List<int>();
        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            var it = ReelPool.All[i];
            if (!it.Usable || it.Excluded) continue;
            if (allowed != null && allowed.Length > 0)
            {
                bool ok = false;
                for (int k = 0; k < allowed.Length; k++) if (allowed[k] == i) { ok = true; break; }
                if (!ok) continue;
            }
            any.Add(i);
            if (it.HasIcon) withIcon.Add(i);
        }
        var src = withIcon.Count >= count ? withIcon : any;
        int n = src.Count;
        if (n == 0) return new int[0];
        int take = Mathf.Min(count, n);
        var pick = new int[take];
        var used = new bool[n];
        for (int k = 0; k < take; k++)
        {
            int idx = (int)(((long)k * n) / take + n / 2) % n;
            int guard = 0;
            while (used[idx] && guard++ < n) idx = (idx + 1) % n;
            used[idx] = true;
            pick[k] = src[idx];
        }
        return pick;
    }

    internal static Color Scale(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

    // One art band, placed from the running top edge downwards, never wider than ArtHalf.
    private static float AddArtQuad(LobbyPropRuntime rt, string name, float top, float h, Rect uv, Color tint)
    {
        rt.AddQuad(name, new Vector3(0f, top - h * 0.5f, 0.1815f), new Vector2(ArtHalf * 2f, h), uv, 2, tint);
        rt.LogArtQuad(name, ArtHalf, top, top - h);
        return top - h;
    }

    private static void AddGlowFrame(LobbyPropRuntime rt, float yTop, float yBottom, float xSide, float xWide)
    {
        // wide, dim layer first, then the tighter core: additive, so they stack
        rt.AddGlowStrip("glow_wide_top", new Vector2(-xWide, yTop), new Vector2(xWide, yTop), 0.150f, GlowWide);
        rt.AddGlowStrip("glow_wide_bottom", new Vector2(-xWide, yBottom), new Vector2(xWide, yBottom), 0.150f, GlowWide);
        rt.AddGlowStrip("glow_wide_left", new Vector2(-0.4275f, yBottom - 0.02f), new Vector2(-0.4275f, yTop + 0.02f), 0.145f, GlowWide);
        rt.AddGlowStrip("glow_wide_right", new Vector2(0.4275f, yBottom - 0.02f), new Vector2(0.4275f, yTop + 0.02f), 0.145f, GlowWide);

        rt.AddGlowStrip("glow_top", new Vector2(-xSide, yTop), new Vector2(xSide, yTop), 0.075f, GlowCore);
        rt.AddGlowStrip("glow_bottom", new Vector2(-xSide, yBottom), new Vector2(xSide, yBottom), 0.075f, GlowCore);
        rt.AddGlowStrip("glow_left", new Vector2(-xSide, yBottom - 0.014f), new Vector2(-xSide, yTop + 0.014f), 0.085f, GlowCore);
        rt.AddGlowStrip("glow_right", new Vector2(xSide, yBottom - 0.014f), new Vector2(xSide, yTop + 0.014f), 0.085f, GlowCore);
    }

    // Amber neon arrow. Centre C, radius r, swept clockwise from 300 deg to 30 deg (270 deg of
    // travel): the tail starts below the sign's top-left and the head ends at the tray's lower
    // left, pointing right. Three flat ribbons give the dark outline, the amber body and the hot
    // core; the head is a triangle each.
    private static void BuildArrow(LobbyPropRuntime rt)
    {
        var c = new Vector2(-0.272f, 2.520f);
        const float r = 0.160f;
        const float a0 = 300f, a1 = 60f;
        const int steps = 26;
        float z = 0.211f;

        var line = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, i / (float)steps);
            line[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }
        // Mirror across x: the cabinet's own right (+x) is the viewer's left in a front view, which
        // is where the reference arrow sits.
        for (int i = 0; i < line.Length; i++) line[i] = new Vector2(-line[i].x, line[i].y);
        rt.AddRibbonGlow("arrow_glow", line, 0.060f, z - 0.001f, new Color(1f, 0.72f, 0.24f, 0.34f));
        rt.AddRibbon("arrow_outline", line, 0.037f, z, AmberLine);
        rt.AddRibbon("arrow_body", line, 0.028f, z + 0.001f, AmberCore);
        rt.AddRibbon("arrow_hot", line, 0.012f, z + 0.002f, AmberHot);

        // head: tangent at the end of a clockwise sweep is (sin a, -cos a)
        float ea = Mathf.Deg2Rad * a1;
        var tan = new Vector2(-Mathf.Sin(ea), -Mathf.Cos(ea)).normalized;   // mirrored x
        var nrm = new Vector2(-tan.y, tan.x);
        var tipBase = line[steps];
        rt.AddTriangle("arrow_head_line", tipBase + nrm * 0.056f, tipBase - nrm * 0.056f, tipBase + tan * 0.112f, z, AmberLine);
        rt.AddTriangle("arrow_head_body", tipBase + nrm * 0.044f, tipBase - nrm * 0.044f, tipBase + tan * 0.096f, z + 0.001f, AmberCore);
        rt.AddTriangle("arrow_head_hot", tipBase + nrm * 0.019f, tipBase - nrm * 0.019f, tipBase + tan * 0.060f, z + 0.002f, AmberHot);
        rt.LogArrow(line, tipBase + tan * 0.112f, z + 0.002f);
    }

    // Sample a rounded square (half extent, corner radius) clockwise, starting on the +X side.
    private static List<Vector2> RoundedSquarePath(float half, float radius, int perCorner)
    {
        var pts = new List<Vector2>(perCorner * 4 + 4);
        float o = half - radius;
        var centres = new[]
        {
            new Vector2(o, o), new Vector2(-o, o), new Vector2(-o, -o), new Vector2(o, -o),
        };
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i <= perCorner; i++)
            {
                if (c > 0 && i == 0) continue;   // join handled by the connecting quad
                float a = Mathf.Deg2Rad * (90f * c + 90f * i / perCorner);
                pts.Add(centres[c] + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
        }
        return pts;
    }
}

// ============================================================================================
// THE CANONICAL DECK: the one base a spin may shuffle, and the only deck description that crosses
// the wire.
//
//   ReelDeck.Compose(cells, composeSeed, mask)   the frozen composition (weights live there)
//   ReelDeck.Shuffle(canonical, shuffleSeed)     this spin's order, applied to that base
//
// Two rules, both learned from a real divergence:
//
//  1. A shuffle ALWAYS starts from a freshly composed canonical deck. The host used to shuffle its
//     live `Cards` in place, so spin 2 shuffled spin 1's permutation - a permutation of a
//     permutation that no peer can rebuild from a seed pair. Divergence began at spin 2 (a headless
//     replay of the shipped shuffle: 47 of 64 cells disagreed at the same landing index, and 500 of
//     500 random seed pairs disagreed), while the old type-count check still reported agreement,
//     because a permutation preserves counts. `Deck()` below is the only composition a spin path
//     calls; `Cards` is a render cache, never an input.
//
//  2. The deck actually displayed is the HOST's, card for card (the ordered 64 cards ride in the
//     spin frame - see ReelNet.BuildSpin). ReelDeck.AllowedItems is derived per peer by ReelEligible
//     from that peer's own scene scan, so the item cards cannot be assumed identical just because
//     the seed pair matched: the seed route is kept, but only as the divergence *detector*, never as
//     the source of what a client shows.
internal static class ReelCanonical
{
    // The deck this spin uses: canonical composition, then one shuffle. Pure: the same arguments
    // give the same array on every peer that ships the same ReelDeck/ReelPool (both are seeded,
    // never sampled), and no global state is read except the item set inside ReelDeck.Compose.
    internal static ReelCard[] Deck(int composeSeed, int shuffleSeed, int debuffMask)
    {
        var cards = ReelDeck.Compose(ReelDeck.Cells, composeSeed, debuffMask);
        ReelDeck.Shuffle(cards, shuffleSeed);
        return cards;
    }

    // Ordered identity of a deck, for a one-line comparison in the logs. Counts cannot see a
    // permutation; this can.
    internal static uint Signature(ReelCard[] cards)
    {
        if (cards == null) return 0u;
        uint h = 2166136261u;                                  // FNV-1a, 32 bit
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            unchecked
            {
                h = (h ^ (byte)c.Type) * 16777619u;
                h = (h ^ (byte)(c.Item & 0xFF)) * 16777619u;
                h = (h ^ (byte)((c.Item >> 8) & 0xFF)) * 16777619u;
                h = (h ^ (byte)(c.HasDebuff ? (int)c.Debuff + 1 : 0)) * 16777619u;
            }
        }
        return h;
    }

    // THE DIVERGENCE DETECTOR. Compares the ordered sequence the two sides will use and names the
    // first cell that differs ("cell 17 host='item:FAS' local='blank'"). Empty = same deck.
    internal static string FirstDifference(ReelCard[] local, ReelCard[] host)
    {
        if (local == null || host == null)
            return $"missing deck (local={(local == null ? "null" : "present")} " +
                   $"host={(host == null ? "null" : "present")})";
        int n = Math.Min(local.Length, host.Length);
        for (int i = 0; i < n; i++)
        {
            var a = local[i];
            var b = host[i];
            if (a.Type == b.Type && a.Item == b.Item && a.Debuff == b.Debuff) continue;
            return $"cell {i} host='{Face(b)}' local='{Face(a)}'";
        }
        if (local.Length != host.Length) return $"cells {local.Length} vs {host.Length}";
        return "";
    }

    private static string Face(ReelCard c) =>
        c.Type == ReelCardType.Item ? $"item:{ReelDeck.Code(c)}" : ReelDeck.TypeName(c.Type);

    // ---- wire form: the ordered deck, 4 bytes per card ---------------------------------
    //   type:1  item:s16 (-1 = none)  debuff:1 (0 = no debuff, else kind+1)
    // Item is a ReelPool row index, and ReelPool.g.cs is generated into this same assembly, so the
    // two peers reference the same rows by construction.
    internal const int BytesPerCard = 4;
    internal static int WireBytes(int cells) => cells * BytesPerCard;

    internal static void Write(byte[] dst, int at, ReelCard[] cards)
    {
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            ushort item = unchecked((ushort)(short)c.Item);
            dst[at++] = (byte)c.Type;
            dst[at++] = (byte)(item & 0xFF);
            dst[at++] = (byte)((item >> 8) & 0xFF);
            dst[at++] = (byte)(c.HasDebuff ? (int)c.Debuff + 1 : 0);
        }
    }

    // Reads a deck the host published. False (and `cards` null) on a short or out-of-range frame, so
    // the caller can fall back to a local rebuild instead of showing a half-read strip.
    internal static bool TryRead(byte[] src, int at, int cells, out ReelCard[] cards)
    {
        cards = null;
        if (src == null) return false;
        int need = at + WireBytes(cells);
        if (need > src.Length || need < at) return false;
        var list = new ReelCard[cells];
        for (int i = 0; i < cells; i++)
        {
            byte type = src[at++];
            int item = unchecked((short)(ushort)(src[at] | (src[at + 1] << 8)));
            at += 2;
            byte debuff = src[at++];
            if (type > (byte)ReelCardType.Blank) return false;
            if (item != -1 && (item < 0 || item >= ReelPool.All.Length)) return false;
            if (debuff > ReelTable.Debuffs.Length) return false;
            list[i] = new ReelCard
            {
                Type = (ReelCardType)type,
                Item = item,
                Debuff = debuff == 0 ? default : (ReelDebuffKind)(debuff - 1),
                HasDebuff = debuff != 0,
            };
        }
        cards = list;
        return true;
    }
}

internal sealed class LobbyPropRuntime : IDisposable
{
    private enum MatKind { Lit, Unlit, Sign, Glow, Reel, Fade, Preview }

    // The scrolling row: one material, the u offset at the window's left edge, and the eased spin
    // state. Phase: 0 idle, 1 main travel, 2 overshoot click, 3 done.
    internal sealed class ScrollState
    {
        public Material Material;
        public float UOffset;         // u at the window's left edge; rises as the row moves left
        public float From, To, ClickTo;
        public float Duration, ClickDuration;
        public int Phase;
        public int StartCell, TargetCell;
    }

    // The booth's native interactable. One instance per booth, so two map booths have two
    // colliders, two look centres and two InteractedLocally subscriptions (see ReelInteraction).
    internal readonly ReelInteraction Interaction = new ReelInteraction();
    public GameObject Root;
    public IHostContext log;
    public Vector3 Anchor;
    public float Yaw;
    // Which saved anchor this booth stands on ("lair-2"); empty for a manual (place=here) booth.
    internal string PlacementName = "";
    // The network id of this machine: the placement's own name, sent by the host. Two booths never
    // share one. A request names its machine by this, never by a scene handle or an array index.
    internal string MachineId = "";
    internal bool IsMapSpawn;
    internal bool IsManual;
    // Only one booth reads <ScreensDir>/reel.request; ModelSwapRuntime points this at its manual
    // booth, or at the first map booth when there is none. Every booth still answers E.
    internal bool RequestOwner = true;
    // The booth's own trigger box at the sign face. Not a movement collider: it exists so each map
    // booth has its own interaction volume rather than sharing the (removed) lobby interactable.
    public BoxCollider Trigger;
    internal Vector3 LookCentre => (Root != null ? Root.transform.position : Anchor) + Vector3.up * 1.60f;

    // The sign face box the game's interactable uses: 1.00 x 1.60 x 0.34 at 1.96 m up, 0.26 m in
    // front of the root. The root sits at the face, so the box covers the cabinet.
    internal void AttachTrigger(IHostContext log)
    {
        if (_disposed || Root == null) return;
        try
        {
            var go = new GameObject("ReelTrigger");
            go.transform.SetParent(Root.transform, false);
            go.transform.localPosition = new Vector3(0f, 1.960f, 0.260f);
            go.transform.localRotation = Quaternion.identity;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, 0f, -0.05f);
            box.size = new Vector3(1.00f, 1.60f, 0.34f);
            Trigger = box;
        }
        catch (Exception e) { log?.LogWarning($"[prop] trigger failed: {e.Message}"); }
    }
    public Material litMaterial, neonMaterial, signMaterial, glowMaterial;
    public Texture2D signTexture, glowTexture, scrollTexture, fadeTexture;
    // The deck: one card per strip cell, reshuffled at every spin start. `poolIcons` is indexed by
    // ReelPool row (null = that item has no icon sprite and its card shows the code).
    internal ReelCard[] Cards;
    internal ReelArt.IconPixels[] poolIcons;
    internal ReelArt.StripCache stripCache;
    internal byte[] stripChain;      // full mip-chain buffer for the strip (base level + regenerated mips)
    internal int stripChainBytes;
    public Material fadeMaterial;
    public int signWidth, signHeight;
    public readonly List<Material> scrollMaterials = new();
    internal ScrollState Scroll;
    // The marker glow is the only animated tint: it brightens as a card centres under the marker.
    private MeshRenderer _markerGlowTop, _markerGlowBottom;

    // ---- idle preview (item 5) ---------------------------------------------------------
    // While the machine is idle the item window shows a separate preview plate - three eligible
    // item cards, the "not a result" label and the deck's real odds - and the genuine strip, its
    // end fade and the outcome marker are switched off. On an accepted spin the preview is
    // removed and the strip comes back; after the result/cooldown presentation it returns. The
    // preview never moves, never ticks, never plays a cue and is never under the marker.
    internal const float PreviewHoldSeconds = 3.0f;   // result/cooldown presentation after a settle
    public GameObject previewGo;
    public Texture2D previewTexture;
    public Material previewMaterial;
    public Renderer scrollRowRenderer, scrollFadeRenderer;
    public readonly List<Renderer> markerRenderers = new();
    private bool _previewShown;
    internal bool _previewBuilt;
    private float _resultUntil;               // strip stays visible until this clock time
    internal string _previewItems = "none";   // the three item ids on the plate (log/evidence)
    internal int[] _previewPick;              // ReelPool indices on the plate
    private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
    public readonly List<Renderer> Renderers = new();
    public readonly List<Light> Lights = new();
    public readonly List<Mesh> meshes = new();
    public readonly List<Material> materials = new();
    public int PartCount, TriangleCount;
    public Bounds Bounds;
    private readonly StringBuilder _parts = new StringBuilder();
    private readonly StringBuilder _art = new StringBuilder();
    private bool _disposed;

    // Spin/input state. The clock is accumulated from the module's Update dt so the [reel:uv]
    // trace and the spin timeline cannot drift apart.
    private float _clock, _spinT, _nextRequestPoll;
    private bool _spinActive, _inputFailed, _inputWarned, _requestWarned;
    private int _spins;
    private ReelLogic.Pull _pull;
    private bool _viewProbed;

    // Interaction + feedback state (item 5/6/7): the prompt is shown only while looking, the deck
    // remembers which debuff kinds it was composed with, and the selection scheme keeps its own
    // state so bag/pity modes work without touching the spin code.
    private readonly ReelDrawState _draw = new ReelDrawState();
    private int _deckDebuffMask = -1;
    private int _deckComposeSeed;
    private bool _looked, _promptLogged, _promptViaGame;
    private float _promptNextAt;
    private float _lastLookDot = -2f, _lastLookDist = -1f;
    private int _lastTickCell = -1;

    // ---- stage 3B: the networked spin
    private uint _spinId;            // 0 = not networked (solo play)
    private uint _requestId;         // host: the request this spin serves
    private ulong _spinActor;        // the player the outcome belongs to
    private bool _remoteSpin;        // this animation came from the host: apply nothing
    private string _pendingText;     // the host's result line, shown when the strip stops
    private bool _pendingMine;       // ...only if it is this client's own pull
    private float _nextSpinAt;       // cooldown after a settle

    internal uint SpinId => _spinId;
    /// <summary>The machine answers an interaction only when idle and off cooldown.</summary>
    internal bool SpinBusy => _spinActive || _clock < _nextSpinAt;

    public void AddBox(string name, Vector3 centre, Vector3 size, Color color, bool unlit = false)
    {
        var mesh = BuildBox(name, centre, size);
        Attach(name, mesh, unlit ? MatKind.Unlit : MatKind.Lit, color);
    }

    public void AddQuadUnlit(string name, Vector3 centre, Vector2 size, Color color)
    {
        var mesh = BuildQuad(name, centre, size, Rect.MinMaxRect(0, 0, 1, 1), 0);
        Attach(name, mesh, MatKind.Unlit, color);
    }

    public void AddQuad(string name, Vector3 centre, Vector2 size, Rect uv, int uvMode = 3, Color? tint = null)
    {
        var mesh = BuildQuad(name, centre, size, uv, uvMode);
        // The sign is URP/Unlit: the atlas is the only light it gets. Scale the per-renderer tint
        // past 1.0 so the face carries its own brightness in the dim tent (see SignTintGain).
        Attach(name, mesh, MatKind.Sign, LobbyPropFactory.Scale(tint ?? Color.white, LobbyPropFactory.SignTintGain));
    }

    // The whole item row: one full-window quad drawn with the shared strip material, whose
    // _BaseMap_ST offset is the scroll. Everything the eye reads as motion comes from that offset.
    public void AddScrollRowQuad(string name, Vector3 centre, Vector2 size, float tint)
    {
        var mesh = BuildQuad(name, centre, size, Rect.MinMaxRect(0f, 0f, 1f, 1f), 2);
        Attach(name, mesh, MatKind.Reel, new Color(tint, tint, tint, 1f), 0, false);
    }

    // Alpha-ramp overlay for the two ends of the row (the case-opening fade). Opaque cards behind.
    public void AddScrollFade(string name, Vector3 centre, Vector2 size)
    {
        var mesh = BuildQuad(name, centre, size, Rect.MinMaxRect(0f, 0f, 1f, 1f), 2);
        Attach(name, mesh, MatKind.Fade, Color.white, 0, false);
    }

    // Fixed centre marker, now OUTSIDE the card face: a pointer above the row and its mirror below,
    // each flanked by a tick on either side, all of it in the 9 mm gap between the card window's
    // edge and the recessed well's inner shadow (see LobbyPropFactory.Mark*). No geometry crosses
    // the cards any more, so the settled card's icon and code read unobstructed; the bracket still
    // marks the centre column while the row moves, and it is the only thing on the face that does
    // not scroll. All of it is static geometry - only the two glow tints are animated.
    public void AddScrollMarker(float midY)
    {
        float edge = LobbyPropFactory.ScrollWindowH * 0.5f;
        float apex = edge + LobbyPropFactory.MarkPointerApex;
        float baseY = apex + LobbyPropFactory.MarkPointerLength;
        float z = LobbyPropFactory.MarkerZ;
        float hw = LobbyPropFactory.MarkPointerHalfWidth;
        var glyph = LobbyPropFactory.MarkerGlyph;
        markerRenderers.Clear();
        AddTriangle("marker_pointer_top", new Vector2(-hw, midY + baseY), new Vector2(hw, midY + baseY),
                    new Vector2(0f, midY + apex), z, glyph);
        markerRenderers.Add(LastRenderer());
        AddTriangle("marker_pointer_bottom", new Vector2(-hw, midY - baseY), new Vector2(hw, midY - baseY),
                    new Vector2(0f, midY - apex), z, glyph);
        markerRenderers.Add(LastRenderer());
        float tickMid = (LobbyPropFactory.MarkTickInner + LobbyPropFactory.MarkTickOuter) * 0.5f;
        float tickW = LobbyPropFactory.MarkTickOuter - LobbyPropFactory.MarkTickInner;
        for (int side = -1; side <= 1; side += 2)
        {
            AddQuadUnlit("marker_tick_top_" + side, new Vector3(side * tickMid, midY + LobbyPropFactory.MarkTickY, z),
                         new Vector2(tickW, LobbyPropFactory.MarkTickHeight), glyph);
            markerRenderers.Add(LastRenderer());
            AddQuadUnlit("marker_tick_bottom_" + side, new Vector3(side * tickMid, midY - LobbyPropFactory.MarkTickY, z),
                         new Vector2(tickW, LobbyPropFactory.MarkTickHeight), glyph);
            markerRenderers.Add(LastRenderer());
        }
        float glowY = LobbyPropFactory.MarkTickY;
        float gl = LobbyPropFactory.MarkGlowHalfLength;
        AddGlowStrip("marker_glow_top", new Vector2(-gl, midY + glowY), new Vector2(gl, midY + glowY),
                     LobbyPropFactory.MarkGlowWidth, LobbyPropFactory.MarkerGlow);
        _markerGlowTop = Renderers[Renderers.Count - 1] as MeshRenderer;
        markerRenderers.Add(_markerGlowTop);
        AddGlowStrip("marker_glow_bottom", new Vector2(-gl, midY - glowY), new Vector2(gl, midY - glowY),
                     LobbyPropFactory.MarkGlowWidth, LobbyPropFactory.MarkerGlow);
        _markerGlowBottom = Renderers[Renderers.Count - 1] as MeshRenderer;
        markerRenderers.Add(_markerGlowBottom);
    }

    internal Renderer LastRenderer() => Renderers.Count > 0 ? Renderers[Renderers.Count - 1] : null;

    // The idle preview plate. Its own texture and material, parented to the root like every other
    // face quad, shown only while the machine is idle (see UpdatePreview).
    public void AddPreviewQuad(string name, Vector3 centre, Vector2 size)
    {
        var mesh = BuildQuad(name, centre, size, Rect.MinMaxRect(0f, 0f, 1f, 1f), 2);
        Attach(name, mesh, MatKind.Preview, Color.white, 0, false);
        var r = LastRenderer();
        previewGo = r != null ? r.gameObject : null;
        if (previewGo != null) previewGo.SetActive(false);
    }

    // Arbitrary flat quad (four corners, +Z facing), used for the tray bevel and lip.
    public void AddQuadRaw(string name, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
    {
        var verts = new List<Vector3> { p0, p1, p2, p3 };
        var norms = new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        var uvs = new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        Attach(name, NewMesh(name, verts, norms, uvs, tris), MatKind.Unlit, color);
    }

    public void AddTriangle(string name, Vector3 p0, Vector3 p1, Vector3 p2, float z, Color color)
    {
        var verts = new List<Vector3> { new Vector3(p0.x, p0.y, z), new Vector3(p1.x, p1.y, z), new Vector3(p2.x, p2.y, z) };
        var norms = new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward };
        var uvs = new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1) };
        var tris = new List<int> { 0, 1, 2 };
        Attach(name, NewMesh(name, verts, norms, uvs, tris), MatKind.Unlit, color);
    }

    // Flat ribbon along a polyline in the face plane (the arrow strokes).
    public void AddRibbon(string name, Vector2[] line, float halfWidth, float z, Color color)
    {
        Attach(name, BuildRibbon(name, line, halfWidth, z), MatKind.Unlit, color);
    }

    private static Mesh BuildRibbon(string name, Vector2[] line, float halfWidth, float z)
    {
        int n = line.Length;
        var verts = new List<Vector3>(n * 2);
        var norms = new List<Vector3>(n * 2);
        var uvs = new List<Vector2>(n * 2);
        var tris = new List<int>((n - 1) * 6);
        for (int i = 0; i < n; i++)
        {
            Vector2 a = line[Mathf.Max(0, i - 1)], b = line[Mathf.Min(n - 1, i + 1)];
            Vector2 t = (b - a).normalized;
            Vector2 nrm = new Vector2(-t.y, t.x);
            verts.Add(new Vector3(line[i].x + nrm.x * halfWidth, line[i].y + nrm.y * halfWidth, z));
            verts.Add(new Vector3(line[i].x - nrm.x * halfWidth, line[i].y - nrm.y * halfWidth, z));
            norms.Add(Vector3.forward); norms.Add(Vector3.forward);
            uvs.Add(new Vector2(i / (float)(n - 1), 0f)); uvs.Add(new Vector2(i / (float)(n - 1), 1f));
        }
        for (int i = 0; i < n - 1; i++)
        {
            int o = i * 2;
            tris.Add(o); tris.Add(o + 2); tris.Add(o + 1);
            tris.Add(o + 1); tris.Add(o + 2); tris.Add(o + 3);
        }
        return NewMesh(name, verts, norms, uvs, tris);
    }

    // Additive glow ribbon along a polyline (the amber spill around the arrow).
    public void AddRibbonGlow(string name, Vector2[] line, float halfWidth, float z, Color color)
    {
        var mesh = BuildRibbon(name, line, halfWidth, z);
        Attach(name, mesh, MatKind.Glow, color);
    }

    // Swept square-section tube along a closed path in the face plane (the neon frame).
    public void AddTubePath(string name, List<Vector2> path, float halfSection, float zCentre, Color color)
    {
        int n = path.Count;
        var verts = new List<Vector3>(n * 4);
        var norms = new List<Vector3>(n * 4);
        var uvs = new List<Vector2>(n * 4);
        var tris = new List<int>(n * 24);
        for (int i = 0; i < n; i++)
        {
            Vector2 a = path[(i - 1 + n) % n], b = path[(i + 1) % n];
            Vector2 p = path[i];
            Vector2 t = (b - a).normalized;
            Vector2 nrm = new Vector2(-t.y, t.x);
            Vector2 po = p + nrm * halfSection, pi = p - nrm * halfSection;
            // 0 outer-front, 1 outer-back, 2 inner-front, 3 inner-back
            verts.Add(new Vector3(po.x, po.y, zCentre + halfSection));
            verts.Add(new Vector3(po.x, po.y, zCentre - halfSection));
            verts.Add(new Vector3(pi.x, pi.y, zCentre + halfSection));
            verts.Add(new Vector3(pi.x, pi.y, zCentre - halfSection));
            for (int k = 0; k < 4; k++) { norms.Add(Vector3.forward); uvs.Add(new Vector2(i / (float)n, k * 0.25f)); }
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int a = i * 4, b = j * 4;
            void Quad(int i0, int i1, int i2, int i3)
            {
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                tris.Add(i0); tris.Add(i2); tris.Add(i3);
            }
            Quad(a + 0, b + 0, b + 2, a + 2);   // front ring face
            Quad(a + 1, a + 3, b + 3, b + 1);   // back ring face
            Quad(a + 0, a + 1, b + 1, b + 0);   // outer wall
            Quad(a + 2, b + 2, b + 3, a + 3);   // inner wall
        }
        Attach(name, NewMesh(name, verts, norms, uvs, tris), MatKind.Unlit, color);
    }

    // Additive gradient skirt in the face plane: `a` to `b`, `width` across. The ramp texture's
    // alpha runs across the width, so this spills light outward from the tube.
    public void AddGlowStrip(string name, Vector2 a, Vector2 b, float width, Color color)
    {
        Vector2 d = (b - a).normalized;
        Vector2 nrm = new Vector2(-d.y, d.x) * (width * 0.5f);
        float z = LobbyPropFactory.GlowZ;
        var verts = new List<Vector3>
        {
            new Vector3(a.x - nrm.x, a.y - nrm.y, z), new Vector3(b.x - nrm.x, b.y - nrm.y, z),
            new Vector3(b.x + nrm.x, b.y + nrm.y, z), new Vector3(a.x + nrm.x, a.y + nrm.y, z),
        };
        var norms = new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        var uvs = new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        Attach(name, NewMesh(name, verts, norms, uvs, tris), MatKind.Glow, color);
    }

    private void Attach(string name, Mesh mesh, MatKind kind, Color color, int reelIndex = -1, bool logIt = true)
    {
        meshes.Add(mesh);
        var go = new GameObject("AlterumProp_" + name);
        go.transform.SetParent(Root.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        var mf = go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
        var mr = go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
        if (mf == null || mr == null) throw new InvalidOperationException("mesh components failed for " + name);
        mf.sharedMesh = mesh;
        mr.sharedMaterial = kind == MatKind.Sign ? signMaterial
                          : kind == MatKind.Glow ? glowMaterial
                          : kind == MatKind.Reel ? (reelIndex >= 0 && reelIndex < scrollMaterials.Count ? scrollMaterials[reelIndex] : neonMaterial)
                          : kind == MatKind.Fade ? fadeMaterial
                          : kind == MatKind.Preview ? previewMaterial
                          : kind == MatKind.Unlit ? neonMaterial : litMaterial;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        {
            try
            {
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", color);
                block.SetColor("_Color", color);
                mr.SetPropertyBlock(block);
            }
            catch (Exception e) { log?.LogWarning($"[prop:mpb] '{name}' colour block failed ({color}): {e.Message}"); }
        }
        Renderers.Add(mr);
        if (kind == MatKind.Sign)
        {
            try
            {
                var tex = signMaterial != null && signMaterial.HasProperty("_BaseMap") ? signMaterial.GetTexture("_BaseMap") : null;
                log?.LogInfo($"[prop:sign] '{name}' verts={mesh.vertexCount} uv0={mesh.uv[0]} uv2={mesh.uv[2]} " +
                             $"mat='{signMaterial?.name}' shader='{signMaterial?.shader?.name}' tex={(tex != null ? tex.name : "null")} " +
                             $"rq={signMaterial?.renderQueue} bounds={mesh.bounds}");
            }
            catch (Exception e) { log?.LogWarning($"[prop:sign] '{name}' diagnostic failed: {e.Message}"); }
        }
        else if (kind == MatKind.Reel && logIt)
        {
            try
            {
                var mat = mr.sharedMaterial;
                var tex = mat != null && mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
                log?.LogInfo($"[prop:reel] window '{name}' reel={reelIndex} verts={mesh.vertexCount} uv0={mesh.uv[0]} " +
                             $"mat='{mat?.name}' tex={(tex != null ? tex.name : "null")} bounds={mesh.bounds}");
            }
            catch (Exception e) { log?.LogWarning($"[prop:reel] '{name}' diagnostic failed: {e.Message}"); }
        }
        PartCount++;
        int partTris = mesh.triangles.Length / 3;
        TriangleCount += partTris;
        if (_parts.Length < 900) _parts.Append(name).Append(':').Append(partTris).Append("t; ");
    }

    // Art-fit evidence: every band's extent against the tube's inner edge.
    public void LogArtQuad(string name, float halfW, float top, float bottom)
    {
        float inner = LobbyPropFactory.TubeInnerEdge;
        bool inside = halfW <= inner + 1e-4f && bottom >= LobbyPropFactory.SignBottomEdge + 1e-4f && top <= LobbyPropFactory.SignTopEdge - 1e-4f;
        _art.Append($"{name}[x+-{halfW:F3} y {bottom:F3}..{top:F3}] ");
        if (log != null)
            log.LogInfo($"[prop:art] '{name}' x=(-{halfW:F3},+{halfW:F3}) y=({bottom:F3},{top:F3}) " +
                        $"tubeInner={inner:F3} screenY=({LobbyPropFactory.SignBottomEdge:F3},{LobbyPropFactory.SignTopEdge:F3}) " +
                        $"insideNeonFrame={inside}");
    }

    public void LogBandEdges(float top, float bottom)
    {
        if (log != null) log.LogInfo($"[prop:art] stack top={top:F3} bottom={bottom:F3} height={top - bottom:F3} art={_art}");
    }

    public void LogArrow(Vector2[] line, Vector2 tip, float z)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < line.Length; i++)
        {
            minX = Mathf.Min(minX, line[i].x); maxX = Mathf.Max(maxX, line[i].x);
            minY = Mathf.Min(minY, line[i].y); maxY = Mathf.Max(maxY, line[i].y);
        }
        minX = Mathf.Min(minX, tip.x); maxX = Mathf.Max(maxX, tip.x);
        minY = Mathf.Min(minY, tip.y); maxY = Mathf.Max(maxY, tip.y);
        minX -= 0.037f; maxX += 0.037f; minY -= 0.037f; maxY += 0.037f;   // stroked outline
        if (log != null)
            log.LogInfo($"[prop:arrow] x=({minX:F3},{maxX:F3}) y=({minY:F3},{maxY:F3}) height={maxY - minY:F3} " +
                        $"stroke=0.056 z={z:F3} proudOfFace={z - 0.160f:F3} withinBoard={minX >= -0.5f && maxX <= 0.5f}");
    }

    public void AddLight(string name, Vector3 localPos, Color color, float intensity, float range)
    {
        var go = new GameObject("AlterumProp_" + name);
        go.transform.SetParent(Root.transform, false);
        go.transform.localPosition = localPos;
        var light = go.AddComponent(Il2CppType.Of<Light>()).TryCast<Light>();
        if (light == null) return;
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = LightShadows.None;
        Lights.Add(light);
    }

    // ---- idle preview (item 5) ---------------------------------------------------------

    // One place decides which face the item window shows. Idle -> preview plate (strip, end fade
    // and marker off). Spin, result and cooldown -> the genuine strip and marker. Nothing here
    // touches the deck, the odds or the spin; it only switches which of the two faces is drawn.
    private void UpdatePreview()
    {
        if (_disposed || Root == null) return;
        bool want = _previewBuilt && !_spinActive && !_filmActive && _clock >= _resultUntil;
        if (want == _previewShown) return;
        // No emphasis call here: while the preview is up the marker (and its glow) is switched off,
        // and ApplyScrollEmphasis itself is a no-op in this state.
        _previewShown = want;
        try { if (previewGo != null) previewGo.SetActive(want); } catch { }
        try { if (scrollRowRenderer != null) scrollRowRenderer.enabled = !want; } catch { }
        try { if (scrollFadeRenderer != null) scrollFadeRenderer.enabled = !want; } catch { }
        for (int i = 0; i < markerRenderers.Count; i++)
            try { if (markerRenderers[i] != null) markerRenderers[i].enabled = !want; } catch { }
        string why = want ? "idle" : (_spinActive ? "spin" : (_filmActive ? "film" : "result"));
        if (want)
            // The plate itself carries no text any more; the odds and the deck census live here, in
            // the log, so the composed deck's composition stays checkable.
            log?.LogInfo($"[reel:preview] show reason={why} items=[{_previewItems}] " +
                         $"text=none odds='{ReelArt.PreviewOdds(Cards)}' " +
                         $"deck=[{ReelDeck.Census(Cards)}] marker=hidden strip=hidden film={_filmActive}");
        else
            log?.LogInfo($"[reel:preview] hide reason={why} resultUntil={_resultUntil:F2} clock={_clock:F2} " +
                         $"strip=visible marker=visible spin={_spinActive} spins={_spins}");
    }

    // Keep the strip (not the preview) on screen for `seconds`: the filmstrip and the seek
    // diagnostics move the row by hand, and they must photograph the real row.
    internal void ShowStripFor(float seconds)
    {
        float until = _clock + Mathf.Max(0f, seconds);
        if (until > _resultUntil) _resultUntil = until;
        UpdatePreview();
    }

    // ---- mesh building ---------------------------------------------------------------------

    // Closed box with hard normals and per-face UVs: 6 quads, 12 triangles.
    private static Mesh BuildBox(string name, Vector3 c, Vector3 s)
    {
        var h = s * 0.5f;
        var verts = new List<Vector3>(24);
        var norms = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);
        var tris = new List<int>(36);
        void Face(Vector3 fc, Vector3 right, Vector3 up, Vector3 n)
        {
            int i = verts.Count;
            verts.Add(fc - right - up); verts.Add(fc + right - up);
            verts.Add(fc + right + up); verts.Add(fc - right + up);
            for (int k = 0; k < 4; k++) norms.Add(n);
            uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
            uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }
        Face(c + new Vector3(0, 0, h.z), new Vector3(h.x, 0, 0), new Vector3(0, h.y, 0), Vector3.forward);
        Face(c - new Vector3(0, 0, h.z), new Vector3(-h.x, 0, 0), new Vector3(0, h.y, 0), Vector3.back);
        Face(c + new Vector3(h.x, 0, 0), new Vector3(0, h.y, 0), new Vector3(0, 0, h.z), Vector3.right);
        Face(c - new Vector3(h.x, 0, 0), new Vector3(0, 0, h.z), new Vector3(0, h.y, 0), Vector3.left);
        Face(c + new Vector3(0, h.y, 0), new Vector3(h.x, 0, 0), new Vector3(0, 0, -h.z), Vector3.up);
        Face(c - new Vector3(0, h.y, 0), new Vector3(h.x, 0, 0), new Vector3(0, 0, h.z), Vector3.down);
        return NewMesh(name, verts, norms, uvs, tris);
    }

    // Single +Z-facing quad with a sub-rect of the sign atlas.
    private static Mesh BuildQuad(string name, Vector3 c, Vector2 size, Rect uv, int uvMode = 3)
    {
        float x = size.x * 0.5f, y = size.y * 0.5f;
        var verts = new[] { c + new Vector3(-x, -y, 0), c + new Vector3(x, -y, 0), c + new Vector3(x, y, 0), c + new Vector3(-x, y, 0) };
        var norms = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        // uvMode: 0 normal, 1 flip V, 2 flip U, 3 rotate 180. The winning mode is applied to the
        // sign quads after the on-screen A/B probe in ~/assets/monorepo/alterum-data/lobby-model/art.
        bool fu = (uvMode & 2) != 0, fv = (uvMode & 1) != 0;
        float u0 = fu ? uv.xMax : uv.xMin, u1 = fu ? uv.xMin : uv.xMax;
        float v0 = fv ? uv.yMax : uv.yMin, v1 = fv ? uv.yMin : uv.yMax;
        var uvs = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1) };
        var tris = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };  // double-sided: sign art must never vanish
        return NewMesh(name, new List<Vector3>(verts), new List<Vector3>(norms), new List<Vector2>(uvs), new List<int>(tris));
    }

    private static Mesh NewMesh(string name, List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
    {
        var mesh = new Mesh { name = "AlterumProp_" + name };
        try
        {
            mesh.vertices = new Il2CppStructArray<Vector3>(verts.ToArray());
            mesh.normals = new Il2CppStructArray<Vector3>(norms.ToArray());
            mesh.uv = new Il2CppStructArray<Vector2>(uvs.ToArray());
            mesh.triangles = new Il2CppStructArray<int>(tris.ToArray());
            mesh.RecalculateBounds();
            return mesh;
        }
        catch { UnityEngine.Object.Destroy(mesh); throw; }
    }

    // ---- item row spin -----------------------------------------------------------------

    // Idle position: card 4 centred, so the row starts settled with a card under the marker.
    internal float InitialScrollOffset() => LobbyPropFactory.ScrollOffsetForCell(4);

    // Seed the row on its idle card and push the offset into the material once, so a fresh
    // generation starts with a settled strip even if the spin is never triggered.
    internal void InitScroll(IHostContext log)
    {
        Scroll = new ScrollState
        {
            Material = scrollMaterials.Count > 0 ? scrollMaterials[0] : null,
            UOffset = InitialScrollOffset(),
            StartCell = 4,
            TargetCell = 4,
            Phase = 0,
        };
        ApplyOffsets();
        ApplyScrollEmphasis(0f);
        if (log != null)
        {
            int cell = LobbyPropFactory.ScrollCellAt(Scroll.UOffset);
            log.LogInfo($"[reel] idle cell={cell} type={ReelArt.CardTypeName(Cards, cell)} " +
                        $"code={ReelArt.CardCode(Cards, cell)} deck=[{ReelDeck.Census(Cards)}] " +
                        $"{ReelTable.Summary()} excluded=[{ReelExclusions.TableSummary()}] " +
                        $"u={Inv(Scroll.UOffset, 5)} window={LobbyPropFactory.ScrollWindowW:F3}x{LobbyPropFactory.ScrollWindowH:F3}m " +
                        $"uScale={LobbyPropFactory.ScrollUScale:F5} uPerCard={LobbyPropFactory.ScrollUCard:F5} " +
                        $"cards={ReelArt.Cells} visible=3 fadeEdge={ReelArt.FadeEdge:F2} " +
                        $"strip={ReelArt.StripW}x{ReelArt.StripH}");
        }
    }

    // Frame hook, called from ModelSwapRuntime.Update while the prop exists. Never throws.
    internal void Tick(float dt)
    {
        if (_disposed || Root == null) return;
        try
        {
            _clock += dt;
            if (RequestOwner)
            {
                PollReelRequest();
                ReelHud.Tick(dt);
            }
            Interaction.Tick(log, this, dt);
            UpdateInteraction(dt);
            PollReelKey();
            StepSpin(dt);
            StepFilmstrip();
            UpdatePreview();
            // The camera exists a moment after the placement (the lobby camera flies in), so the
            // first view probe waits; after that a `prop.request` drives it on demand.
            if (!_viewProbed && _clock > 1.5f)
            {
                _viewProbed = true;
                PropView.Probe(log, this, "place");
            }
            if (_viewProbed) PropView.Poll(log, this, t => ScreenShot.Capture(log, t));
        }
        catch (Exception e) { log?.LogWarning($"[reel] tick failed: {e.Message}"); }
    }

    // Looking at the booth shows ONE prompt: the game's own world-space prompt owned by the cloned
    // interactable while that can display (ReelInteraction verifies its localised label), and the
    // drawn camera quad only when it cannot. The same look test gates the E key, so the machine only
    // answers when it is looked at - like every other interactable in the game. The reel never
    // inserts a row into the shared ActionPromptsController HUD list (see ReelHud).
    private void UpdateInteraction(float dt)
    {
        if (_disposed || Root == null) return;
        float dot, dist;
        bool looking = Looking(out dot, out dist);
        _lastLookDot = dot; _lastLookDist = dist;
        if (looking)
        {
            _looked = true;
            if (_clock >= _promptNextAt)
            {
                _promptNextAt = _clock + 0.75f;
                bool native = Interaction.NativePromptLive;
                bool viaGame;
                if (native)
                {
                    // The game's own presenter draws the E prompt + its label on the booth. Hide any
                    // drawn prompt left from the other path so the two can never be up together.
                    ReelHud.HidePrompt(log);
                    viaGame = true;
                }
                else
                {
                    viaGame = false;
                    ReelHud.Prompt(log, LobbyPropFactory.InteractPrompt);
                }
                if (!_promptLogged || viaGame != _promptViaGame)
                {
                    _promptLogged = true;
                    _promptViaGame = viaGame;
                    log?.LogInfo($"[reel:ui] looking dot={dot:F3} dist={dist:F2}m prompt='{LobbyPropFactory.InteractPrompt}' " +
                                 $"via={(viaGame ? "native" : "drawn")} label={Interaction.PromptLabelSource} " +
                                 $"hud='{ReelHud.Path}'");
                }
            }
            return;
        }
        _looked = false;
        if (_promptLogged)
        {
            _promptLogged = false;
            log?.LogInfo($"[reel:ui] not looking dot={dot:F3} dist={dist:F2}m prompt=hidden");
            ReelHud.HidePrompt(log);
        }
    }

    private bool Looking(out float dot, out float dist)
    {
        dot = -2f; dist = -1f;
        try
        {
            if (Root == null) return false;
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 eye = cam.transform.position;
            Vector3 target = Root.transform.position + Vector3.up * 1.60f;   // sign face centre
            Vector3 to = target - eye;
            dist = to.magnitude;
            if (dist < 0.30f || dist > LobbyPropFactory.InteractRange) return false;
            dot = Vector3.Dot(cam.transform.forward, to / dist);
            return dot >= LobbyPropFactory.InteractDot;
        }
        catch { return false; }
    }

    internal bool PlayerLookingAtBooth => _looked;

    // Fallback only. The booth is a real interactable now: the game's looker promotes it, the
    // game's InteractableInputHandler binds 'e' and Interactable.InteractedLocally starts the spin.
    // Reading the key here as well would put two handlers on one press (the native path runs, then
    // this one starts a second spin). So this whole block is skipped whenever the native
    // interactable for this booth is live; it survives only for a booth whose interactable could
    // not be built (e.g. no provider yet), where there is nothing else to answer E.
    private bool _fallbackKeyLogged;

    private void PollReelKey()
    {
        if (_inputFailed) return;
        if (Interaction.HandlesKey)
        {
            if (!_fallbackKeyLogged)
            {
                _fallbackKeyLogged = true;
                log?.LogInfo("[reel] E is handled by the game's interaction handler (native interactable live); " +
                             "the raw-key fallback is off for this booth");
            }
            return;
        }
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            var control = kb[Key.E];
            if (control == null || !control.wasPressedThisFrame) return;
            // E on the machine only counts while the machine is being looked at; E elsewhere keeps
            // whatever meaning the rest of the game gives it.
            if (!_looked) return;
            StartSpin("E", null, null);
        }
        catch (Exception e)
        {
            _inputFailed = true;
            if (!_inputWarned)
            {
                _inputWarned = true;
                log?.LogWarning($"[reel] E key unavailable (Input System): {e.Message}");
            }
        }
    }

    // Unattended path: <ScreensDir>/reel.request containing "spin" (plus optional capture=<mode>
    // and tag=...). Works with the desktop locked because it needs no input device.
    //   capture=closeup  3.6 s of the window from 1.3 m (default when bare 'capture')
    //   capture=showcase 3.8 s of the whole machine from 3.5 m
    //   capture=main     3.8 s through the game camera as it is aimed right now
    //   capture=spawn    3.9 s through the game camera's recorded spawn pose (what a player at
    //                    spawn sees, fov 70, 640x360)
    //   film             7 frozen stills on the spin curve, for the filmstrip
    //   seek=<u>         freeze at an exact u offset, log the window mapping, one still (diagnostic)
    private void PollReelRequest()
    {
        if (!RequestOwner) return;
        if (log == null) return;
        if (_clock < _nextRequestPoll) return;
        _nextRequestPoll = _clock + 0.35f;
        string path = null;
        try
        {
            string dir = log.ScreensDir;
            if (string.IsNullOrEmpty(dir)) return;
            path = Path.Combine(dir, LobbyPropFactory.ReelRequestFile);
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            try { File.Delete(path); } catch { }
            bool placeHere = LobbyPropFactory.HasPlaceHere(text);
            if (placeHere) PlaceHere(log, "reel.request");
            // Named placements (save=/load=/list/delete=) share this file. Handled before the spin
            // parsing so a request like `save=necropolis-1` is never mistaken for "no spin asked".
            if (ReelPlacements.HandleRequest(log, this, text)) return;
            // Network-authority hooks (pull@1 / pull2@1 / pullstale@1 / pullwrong@1 / forge /
            // nextoutcome=item / inv=id / reelstate) - they belong to the request file for the same
            // reason the placement lines do: unattended verification cannot press E.
            if (ReelNet.HandleRequestLine(log, text)) return;
            if (text.IndexOf("startmap", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReelLogic.StartMap(log);
                return;
            }
            string debugGive = ParseValue(text, "give");
            if (debugGive != null)
            {
                ReelLogic.DebugGive(log, debugGive);
                return;
            }
            string debugHold = ParseValue(text, "hold");
            if (debugHold != null)
            {
                ReelLogic.DebugHold(log, debugHold);
                return;
            }
            if (text.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReelLogic.DebugHeal(log);
                return;
            }
            if (text.IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReelLogic.Probe(log);
                return;
            }
            string mode = ParseCapture(text);
            string tag = ParseTag(text);
            string forceOutcome = ParseOutcome(text);
            string algorithm = ParseAlgorithm(text);
            if (!string.IsNullOrEmpty(algorithm))
            {
                ReelDrawMode dm;
                if (TryParseAlgorithm(algorithm, out dm))
                {
                    _draw.Reset(dm);
                    ReelTable.Mode = dm;
                    log.LogInfo($"[reel] algorithm={dm} selected by request (pulls reset)");
                }
                else log.LogWarning($"[reel] algorithm='{algorithm}' is not independent|fixed|bag|pity; keeping {_draw.Mode}");
            }
            float seek;
            if (TryParseFloat(text, "seek", out seek))
            {
                SeekTo(seek, tag);
                return;
            }
            bool wantsSpin = text.IndexOf("spin", StringComparison.OrdinalIgnoreCase) >= 0;
            bool wantsFilm = text.IndexOf("film", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!wantsSpin && !wantsFilm && forceOutcome == null)
            {
                if (placeHere) return;
                log.LogWarning($"[reel] request had no 'spin'/'film'/'seek='/'outcome=' line; ignored ({text.Length} bytes)");
                return;
            }
            if (wantsFilm && !wantsSpin && mode == null)
            {
                StartFilmstrip(tag);
                return;
            }
            // A client does not settle: its file-driven pull becomes the same request its E sends.
            if (ReelNet.IsClient)
            {
                log?.LogInfo($"[reel:net] reel.request spin on a CLIENT -> InteractRequest machine='{MachineId}'");
                ReelNet.Request(log, MachineId, "request");
                return;
            }
            StartSpin("request", mode, tag, forceOutcome);
        }
        catch (Exception e)
        {
            if (!_requestWarned)
            {
                _requestWarned = true;
                log.LogWarning($"[reel] request poll failed ('{path}'): {e.Message}");
            }
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static string ParseCapture(string text)
    {
        foreach (var raw in Lines(text))
        {
            string line = raw.Trim();
            if (!line.StartsWith("capture", StringComparison.OrdinalIgnoreCase)) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) return "closeup";
            string val = line.Substring(eq + 1).Trim().ToLowerInvariant();
            if (val.Length == 0) return "closeup";
            if (val == "clip" || val == "closeup" || val == "showcase" || val == "main" || val == "spawn") return val;
            return "closeup";
        }
        return null;
    }

    private static string ParseAlgorithm(string text)
    {
        foreach (var raw in Lines(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line.Substring(0, eq).Trim().Equals("algorithm", StringComparison.OrdinalIgnoreCase))
                return line.Substring(eq + 1).Trim();
        }
        return null;
    }

    private static bool TryParseAlgorithm(string text, out ReelDrawMode mode)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            // The default: roll the payout class first, then show a card of that class (LethalCasino's
            // mechanism). `lc` is the same thing under the reference's name.
            case "independent": case "lc": case "default": mode = ReelDrawMode.Independent; return true;
            case "fixed": case "multiset": mode = ReelDrawMode.FixedMultisetReshuffled; return true;
            case "bag": mode = ReelDrawMode.BagWithoutReplacement; return true;
            case "pity": mode = ReelDrawMode.PityCounter; return true;
            default: mode = ReelDrawMode.Independent; return false;
        }
    }

    private static string ParseValue(string text, string key)
    {
        foreach (var raw in Lines(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(eq + 1).Trim();
        }
        return null;
    }

    private static string ParseOutcome(string text)
    {
        foreach (var raw in Lines(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line.Substring(0, eq).Trim().Equals("outcome", StringComparison.OrdinalIgnoreCase)) continue;
            string val = line.Substring(eq + 1).Trim().ToLowerInvariant();
            if (val == "item" || val == "double" || val == "debuff" || val == "blank") return val;
            if (val == "damage" || val == "hp") return "damage";   // `hp` kept as the old spelling
            // Per-effect debuff cards (item 3), so each face can be forced and captured separately.
            if (val == "slow" || val == "fatigue" || val == "tired" || val == "blind") return val;
            return null;
        }
        return null;
    }

    private static bool TryParseFloat(string text, string key, out float value)
    {
        value = 0f;
        foreach (var raw in Lines(text))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            if (!raw.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return float.TryParse(raw.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    // Diagnostic: freeze the row at an exact u offset, log where each window edge lands, and take
    // one still from the close-up camera (same lens as the filmstrip). Used to verify the ST
    // mapping against the dumped strip texture.
    private void SeekTo(float u, string tag)
    {
        if (Scroll == null) return;
        ShowStripFor(8f);   // a seek photographs the real row, never the preview plate
        _spinActive = false;
        _filmActive = false;
        Scroll.UOffset = u;
        Scroll.Phase = 3;
        ApplyOffsets();
        float left = u;
        float right = u + LobbyPropFactory.ScrollUScale;
        log?.LogInfo($"[reel:seek] u={Inv(u, 5)} windowU=[{Inv(left, 5)},{Inv(right, 5)}] " +
                     $"cells=[{Inv(left * ReelArt.Cells, 2)},{Inv(right * ReelArt.Cells, 2)}] " +
                     $"cellLeft={ReelArt.CellAtMarker(u - LobbyPropFactory.ScrollUScale * 0.5f, LobbyPropFactory.ScrollUScale)} " +
                     $"cellMarker={LobbyPropFactory.ScrollCellAt(u)} " +
                     $"typeMarker={ReelArt.CardTypeName(Cards, LobbyPropFactory.ScrollCellAt(u))} " +
                     $"codeMarker={ReelArt.CardCode(Cards, LobbyPropFactory.ScrollCellAt(u))} " +
                     $"cellRight={ReelArt.CellAtMarker(u + LobbyPropFactory.ScrollUScale * 0.5f, LobbyPropFactory.ScrollUScale)} " +
                     $"uScale={LobbyPropFactory.ScrollUScale:F5} uPerCard={LobbyPropFactory.ScrollUCard:F5}");
        StartReelStill(tag ?? "seek");
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string ParseTag(string text)
    {
        foreach (var raw in Lines(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line.Substring(0, eq).Trim().Equals("tag", StringComparison.OrdinalIgnoreCase)) continue;
            string val = line.Substring(eq + 1).Trim();
            var sb = new StringBuilder();
            for (int i = 0; i < val.Length && sb.Length < 32; i++)
                sb.Append(char.IsLetterOrDigit(val[i]) || val[i] == '-' || val[i] == '_' ? val[i] : '_');
            return sb.ToString();
        }
        return null;
    }

    // Guard: a spin without a scene-local player is a no-op. It also matches the requirement that
    // the prop can be absent (then Tick is never called at all).
    private bool HasLocalPlayer() => FindLocalPlayerEntity() != null;

    private static Player FindLocalPlayerEntity()
    {
        try
        {
            var provs = SceneCache.Scan(Il2CppType.Of<CurrentPlayerProvider>());
            for (int i = 0; i < provs.Length; i++)
            {
                var prov = provs[i].TryCast<CurrentPlayerProvider>();
                if (prov == null) continue;
                var player = prov.CurrentPlayer;
                if (player != null && player.gameObject != null && !string.IsNullOrEmpty(player.gameObject.scene.name))
                    return player;
            }
        }
        catch { }

        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i].TryCast<Player>();
                if (p == null || !p.IsOwner) continue;
                var go = p.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(go.scene.name)) continue;
                return p;
            }
        }
        catch (Exception e) { }
        return null;
    }

    // `reel.request place=here` re-anchors an existing booth through this one call, in front of the
    // player without rebuilding it (the art/material state survives). A scene that never had a booth (a map run) goes through CreateAtPlayer instead,
    // which resolves the same pose with the same helper. Returns false when there is nothing to
    // move or no scene-local player; the caller keeps whatever booth it had.
    internal bool PlaceHere(IHostContext log, string why)
    {
        if (_disposed || Root == null) return false;
        try
        {
            if (!TryResolvePlacement(log, why, out Placement pl)) return false;
            Anchor = pl.Anchor;
            Root.transform.position = Anchor;
            Root.transform.rotation = Quaternion.Euler(0f, pl.Yaw, 0f);
            Yaw = pl.Yaw;
            // Re-measure: Bounds is cached at creation, and PropView's [prop:view] distance and
            // verdicts read it. Without this a booth moved by place=here keeps reporting the AABB of
            // the spot it was built at. Move() does the same after it re-anchors.
            Measure(log);
            log?.LogInfo($"[prop] placed at player why={why} player='{pl.PlayerPath}' pose={pl.PoseSource} " +
                         $"pos=({pl.Pos.x:F2},{pl.Pos.y:F2},{pl.Pos.z:F2}) at=({Anchor.x:F2},{Anchor.y:F3},{Anchor.z:F2}) " +
                         $"yaw={Yaw:F1} colliders=0");
            return true;
        }
        catch (Exception e) { log?.LogError($"[prop] placed at player failed: {e}"); return false; }
    }

    // Move an existing booth to a recorded anchor (a loaded or restored placement). The trigger
    // collider, the look centre and the WS-UI anchor are all children of the root, so they move with
    // it; Measure re-caches the bounds that PropView and the diagnostics read. Nothing here
    // re-resolves a player pose: a placement is absolute.
    internal void MoveTo(IHostContext log, Vector3 position, float yaw)
    {
        if (_disposed || Root == null) return;
        Anchor = position;
        Yaw = yaw;
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        Measure(log);
        // Force the physics sync so the trigger collider (the looker's can-see target) is reported at
        // the new spot in this same frame instead of the previous one.
        try { Physics.SyncTransforms(); } catch { }
        var box = Root.GetComponentInChildren<Collider>(true);
        log?.LogInfo($"[prop] moved pos=({position.x:F2},{position.y:F3},{position.z:F2}) yaw={yaw:F1} " +
                     $"collider={(box != null ? ReelInteraction.V(box.bounds.center) + "/" + ReelInteraction.V(box.bounds.size) : "-")} " +
                     $"bounds={(Bounds != null ? ReelInteraction.V(Bounds.center) + "/" + ReelInteraction.V(Bounds.size) : "-")}");
        Interaction.ReportAnchors(log, "moved");
    }

    // Create a booth at an absolute anchor (a restored placement in a map run, which starts with no
    // booth). The floor probe starts above the saved spot and falls back to the saved height, so a
    // map whose collider is gone still lands where the placement says.
    internal static LobbyPropRuntime CreateAt(IHostContext log, Vector3 at, float yaw)
    {
        try
        {
            var rt = LobbyPropFactory.Create(log, at.x, at.z, yaw, at.y + 1.70f, 4.0f, at.y);
            if (rt == null) return null;
            rt.MoveTo(log, at, yaw);
            return rt;
        }
        catch (Exception e) { log?.LogError($"[prop] create at placement failed: {e}"); return null; }
    }

    // A scene that never had a booth (a map run) gets one where the player stands. The pose comes
    // from the same resolver PlaceHere uses, and the floor probe is Create's own GroundY call with
    // the same arguments, so the two placement branches cannot diverge.
    internal static LobbyPropRuntime CreateAtPlayer(IHostContext log, string why)
    {
        try
        {
            if (!TryResolvePlacement(log, why, out Placement pl)) return null;
            var rt = LobbyPropFactory.Create(log, pl.Anchor.x, pl.Anchor.z, pl.Yaw, pl.Pos.y + 1.70f, 4.0f, pl.Pos.y);
            if (rt == null)
            {
                log?.LogWarning($"[prop] {why}: creation failed at ({pl.Anchor.x:F2},{pl.Anchor.z:F2})");
                return null;
            }
            log?.LogInfo($"[prop] placed at player why={why} player='{pl.PlayerPath}' pose={pl.PoseSource} " +
                         $"pos=({pl.Pos.x:F2},{pl.Pos.y:F2},{pl.Pos.z:F2}) at=({rt.Anchor.x:F2},{rt.Anchor.y:F3},{rt.Anchor.z:F2}) " +
                         $"yaw={pl.Yaw:F1} colliders=0");
            return rt;
        }
        catch (Exception e) { log?.LogError($"[prop] placed at player failed: {e}"); return null; }
    }

    // Where "at the player" means: 1.15 m in front, floor-snapped, facing them. One resolution for
    // both placement branches, so the key and the request file cannot drift apart.
    internal struct Placement
    {
        public Vector3 Pos;      // player pose (feet)
        public Vector3 Anchor;   // booth root: 1.15 m ahead of the player, base on the floor
        public float Yaw;
        public string PoseSource, PlayerPath;
    }

    internal static bool TryResolvePlacement(IHostContext log, string why, out Placement pl)
    {
        pl = default;
        try
        {
            var player = FindLocalPlayerEntity();
            if (player == null) { log?.LogWarning($"[prop] {why}: no local player in this scene"); return false; }
            var tr = player.transform;
            string poseSource;
            Vector3 p, fwd;
            if (!ReelLogic.TryGetPlayerPose(out p, out fwd, out poseSource)) { p = tr.position; fwd = tr.forward; poseSource = "Player.transform"; }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 at = p + fwd * 1.15f;
            // The face is +Z, so the prop's yaw is the reverse of the player's facing vector.
            float yaw = Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg;
            float floorY = LobbyPropFactory.GroundY(log, at.x, at.z, p.y + 1.70f, 4.0f, p.y);
            pl = new Placement
            {
                Pos = p,
                Anchor = new Vector3(at.x, floorY, at.z),
                Yaw = yaw,
                PoseSource = poseSource,
                PlayerPath = LobbyPropFactory.NamePath(tr),
            };
            return true;
        }
        catch (Exception e) { log?.LogError($"[prop] {why}: placement resolve failed: {e}"); return false; }
    }

    // Entry point for the game's own accepted interaction (ReelInteraction subscribes to the
    // Interactable's InteractedLocally). The native looker has already applied its own dot/range/
    // can-see checks, so this path repeats none of them; the one-spin guard and the inventory
    // guards inside StartSpin are what still apply.
    internal void NativeInteract(IHostContext log, string interactorPath)
    {
        if (_disposed || Root == null) return;
        log?.LogInfo($"[reel] native interact why=InteractedLocally interactor='{interactorPath}' machine='{MachineId}'");
        // A client never settles: its E is a request, and only the host's answer can change anything.
        if (ReelNet.IsClient)
        {
            log?.LogInfo($"[reel:net] E on machine='{MachineId}' -> InteractRequest (settlement is the host's)");
            ReelNet.Request(log, MachineId, "E");
            return;
        }
        StartSpin("interact", null, null);
    }

    private void StartSpin(string trigger, string captureMode, string captureTag, string forceType = null)
    {
        if (_disposed || Root == null) return;
        if (_spinActive) return;                        // one spin at a time, no queue
        if (Scroll == null) return;
        if (!HasLocalPlayer())
        {
            log?.LogWarning($"[reel] ignored trigger={trigger}: no local player in the scene");
            return;
        }

        ReelLogic.Pull pull = null;
        string scene;
        if (ReelLogic.IsMapRun(out scene))
        {
            // The pull is map-only. Range is measured to the booth the player can see; a request
            // spin from across the map must not reach into the inventory.
            Vector3 playerPos, playerFwd; string poseSource;
            bool havePose = ReelLogic.TryGetPlayerPose(out playerPos, out playerFwd, out poseSource);
            float dist = havePose ? Vector3.Distance(playerPos, Root.transform.position) : -1f;
            if (!havePose || dist > 2.8f)
            {
                log?.LogInfo($"[reel] pull rejected reason=out-of-range scene='{scene}' dist={dist:F2} limit=2.80");
                return;
            }
            if (!ReelLogic.TryBeginPull(log, out pull))
                return;                                  // no held item / refused take: no spin
        }
        else if (!string.IsNullOrEmpty(forceType))
        {
            log?.LogWarning($"[reel] outcome={forceType} ignored: scene='{scene}' is not a map run");
            return;
        }

        // In a session every spin is broadcast, so the host's own pull animates on the clients
        // exactly like a request: one id space for both paths.
        uint spinId = ReelNet.Broadcasting ? ReelNet.NextSpinId() : 0;
        BeginSpin(trigger, captureMode, captureTag, forceType, pull, 0, spinId, ReelNet.LocalId);
    }

    // Host path for a validated network request: the stake has already been consumed from the
    // REQUESTING player by ReelNet, and `pull.Player` is that player, so the settle below applies to
    // them (inventory/health/effects) through the same code the local path uses.
    internal void StartRequestSpin(IHostContext log, ReelLogic.Pull pull, uint requestId, uint spinId, ulong actor,
                                   string forceType)
    {
        if (_disposed || Root == null || Scroll == null)
        {
            log?.LogWarning($"[reel:net] request {requestId}: booth '{MachineId}' cannot spin (disposed/no scroll)");
            return;
        }
        if (_spinActive)
        {
            log?.LogWarning($"[reel:net] request {requestId}: booth '{MachineId}' is already spinning; not started");
            return;
        }
        log?.LogInfo($"[reel:net] host spin {spinId} start request={requestId} machine='{MachineId}' actor={actor}");
        BeginSpin("request-net", null, null, forceType, pull, requestId, spinId, actor);
    }

    // ---- client side: play what the host broadcast ----------------------------------------

    // The deck is the HOST's: the ordered 64 cards arrive in the spin frame and are displayed exactly
    // as sent, then this client travels to the SAME landing cell, so both peers see the same card
    // under the marker. The seed pair is still applied locally and compared against the host's order -
    // that comparison is the divergence detector, not the source of what is shown. Nothing here reads
    // an outcome: the result line arrives in the settle frame and is displayed when the strip stops.
    internal void RemoteSpin(IHostContext log, uint spinId, ulong actor, float age, float duration, int targetCell,
                             int composeSeed, int shuffleSeed, byte mask, int[] hostCounts, ReelCard[] hostDeck)
    {
        if (_disposed || Root == null || Scroll == null) return;
        this.log = log;
        if (_spinActive)
        {
            log?.LogWarning($"[reel:net] spin {spinId} ignored: booth '{MachineId}' is already spinning");
            return;
        }

        // The authoritative deck for display: the host's, byte for byte. A client's own eligible-item
        // set (ReelDeck.AllowedItems is derived per peer by ReelEligible) and its composition are never
        // trusted for what the player sees, so a difference there can only ever be a log line.
        var localRebuild = ReelCanonical.Deck(composeSeed, shuffleSeed, mask);
        Cards = hostDeck ?? localRebuild;
        _deckDebuffMask = mask;
        _deckComposeSeed = composeSeed;
        ReelArt.RewriteStrip(log, scrollTexture, Cards, poolIcons, stripCache, ref stripChain, stripChainBytes);

        // VALIDATION: the ordered sequence, not the type counts. A permutation preserves counts, so
        // the old check could not see the very divergence it was there to catch; this one names the
        // first differing cell and both faces, so a future divergence identifies itself in one line.
        string orderDiff = ReelCanonical.FirstDifference(localRebuild, hostDeck);
        string deckLine;
        bool deckAgrees;
        if (hostDeck == null)
        {
            // Host predates the deck frame: fall back to the local rebuild and keep the count check.
            var counts = ReelDeck.TypeCounts(localRebuild);
            deckAgrees = hostCounts != null && hostCounts.Length >= 5 && counts.Length >= 5 &&
                         counts[0] == hostCounts[0] && counts[1] == hostCounts[1] && counts[2] == hostCounts[2] &&
                         counts[3] == hostCounts[3] && counts[4] == hostCounts[4];
            deckLine = $"[reel:net] spin {spinId} deck ORDER NOT SENT by the host; rebuilt locally from " +
                       $"seeds={composeSeed}/{shuffleSeed} sig=0x{ReelCanonical.Signature(localRebuild):x8} " +
                       $"{ReelDeck.Census(localRebuild)} countCheck={(deckAgrees ? "agrees" : "DIFFERS")}" +
                       " (counts cannot detect a permutation: display may diverge)";
        }
        else if (orderDiff.Length != 0)
        {
            deckAgrees = false;
            deckLine = $"[reel:net] spin {spinId} DECK ORDER DIVERGES from the host's at {orderDiff} " +
                       $"(host sig=0x{ReelCanonical.Signature(hostDeck):x8} local sig=0x{ReelCanonical.Signature(localRebuild):x8}); " +
                       $"showing the host's deck, seeds={composeSeed}/{shuffleSeed} mask=0x{mask:x2} " +
                       $"cens=[{ReelDeck.Census(hostDeck)}]";
        }
        else
        {
            deckAgrees = true;
            deckLine = $"[reel:net] spin {spinId} deck order matches the host's at all {Cards.Length} cells " +
                       $"sig=0x{ReelCanonical.Signature(hostDeck):x8} {ReelDeck.Census(Cards)}";
        }
        if (!deckAgrees) log?.LogWarning(deckLine); else log?.LogInfo(deckLine);

        _spinId = spinId;
        _spinActor = actor;
        _requestId = 0;
        _remoteSpin = true;
        _pendingText = null;
        _pendingMine = actor == ReelNet.LocalId;
        _spinT = 0f;
        var s = Scroll;
        s.StartCell = LobbyPropFactory.ScrollCellAt(s.UOffset);
        s.TargetCell = Mathf.Clamp(targetCell, 0, ReelDeck.Cells - 1);
        float from = s.UOffset;
        float canonical = LobbyPropFactory.ScrollOffsetForCell(s.TargetCell);
        float distance = canonical - from;
        float minTravel = LobbyPropFactory.ScrollTravelCells * LobbyPropFactory.ScrollUCard;
        while (distance < minTravel) distance += 1f;
        s.From = from;
        s.To = from + distance + LobbyPropFactory.ScrollOvershootCells * LobbyPropFactory.ScrollUCard;
        s.ClickTo = from + distance;
        s.Duration = Mathf.Max(0.25f, duration - Mathf.Max(0f, age));
        s.ClickDuration = LobbyPropFactory.ScrollClickSeconds;
        s.Phase = 1;
        _spinActive = true;
        _lastTickCell = s.StartCell;
        UpdatePreview();   // an accepted spin removes the preview and restores the strip at once
        ReelAudio.Cue(log, ReelCue.Lever);
        log?.LogInfo($"[reel:net] spin {spinId} animate machine='{MachineId}' actor={actor}{( _pendingMine ? " (you)" : "")} " +
                     $"fromCell={s.StartCell} target={s.TargetCell} " +
                     $"type={ReelArt.CardTypeName(Cards, s.TargetCell)} code={ReelArt.CardCode(Cards, s.TargetCell)} " +
                     $"age={age:F2}s remaining={s.Duration:F2}s seeds={composeSeed}/{shuffleSeed} " +
                     "(this client applies nothing)");
    }

    // The host's result line for this spin. Shown when the strip stops (or at once if it already has).
    internal void RemoteSettle(IHostContext log, uint spinId, bool mine, string text)
    {
        this.log = log;
        if (spinId != _spinId)
        {
            if (mine) ReelHud.Result(log, text);
            return;
        }
        _pendingText = text;
        _pendingMine = mine;
        if (_spinActive) return;
        FlushRemoteText();
        // The strip already stopped: this is the result presentation, not a spin. Hold the genuine
        // row for it and let UpdatePreview bring the idle plate back afterwards.
        _resultUntil = _clock + PreviewHoldSeconds;
        _spinId = 0;
        _spinActor = 0;
        _remoteSpin = false;
    }

    // The card under the marker IS the outcome: apply exactly that card, nothing else. On the host
    // a networked spin additionally broadcasts what it settled, and a spin that came FROM the host
    // (a client's booth) only shows the text it was given: nothing is applied on a client.
    private void SettleNetworked(ReelLogic.Pull pull, int cell, ReelCard card)
    {
        if (_remoteSpin)
        {
            FlushRemoteText();
            return;
        }
        if (pull != null) ReelLogic.Resolve(log, pull, cell, card);
        if (_spinId != 0 && ReelNet.Broadcasting)
            ReelNet.OnHostSettled(log, _spinId, _requestId, _spinActor, ReelLogic.LastOutcome);
    }

    // The result line the host sent is shown here, at the instant this client's strip stops. Read
    // only: the client has applied nothing.
    private void FlushRemoteText()
    {
        string text = _pendingText;
        bool mine = _pendingMine;
        _pendingText = null;
        if (string.IsNullOrEmpty(text)) return;
        if (!mine)
        {
            log?.LogInfo($"[reel:net] spin {_spinId} settled for another player; text not shown ('{text}')");
            return;
        }
        log?.LogInfo($"[reel:net] spin {_spinId} result text shown by this client: '{text}' (host applied the outcome)");
        ReelHud.Result(log, text);
    }

    private void BeginSpin(string trigger, string captureMode, string captureTag, string forceType,
                           ReelLogic.Pull pull, uint requestId, uint spinId, ulong actor)
    {
        _pull = pull;
        _requestId = requestId;
        _spinId = spinId;
        _spinActor = actor;
        _remoteSpin = false;
        _pendingText = null;
        try
        {
            // A debuff card must never be composed if its effect family has no loaded variant:
            // a changed scan mask starts a new deck GENERATION, and the generation is a compose seed
            // (which item cards are in the deck). Its seed is published with every spin.
            int mask = ReelDeck.DebuffMask;
            bool newGeneration = _deckDebuffMask != mask;
            if (newGeneration)
            {
                _deckDebuffMask = mask;
                _deckComposeSeed = LobbyPropFactory.Seed();
            }

            // New order every spin, and ALWAYS from the canonical base: this spin's deck is
            // ReelCanonical.Deck(composeSeed, shuffleSeed) and nothing else. The previous spin's
            // `Cards` is a render cache, never an input - shuffling it again is the permutation of a
            // permutation that no client can reproduce from the seed pair (see ReelCanonical).
            // Contents stay fixed for the generation (only the order changes), so the composition
            // weights and ReelDeck.Census are untouched across spins.
            int shuffleSeed = LobbyPropFactory.Seed();
            Cards = ReelCanonical.Deck(_deckComposeSeed, shuffleSeed, _deckDebuffMask);
            if (newGeneration)
                log?.LogInfo($"[reel] deck recomposed debuffMask={mask} composeSeed={_deckComposeSeed} variants=[{ReelLogic.DebuffScanSummary}] " +
                             $"deck=[{ReelDeck.Census(Cards)}] excluded=[{ReelExclusions.TableSummary()}]");
            ReelArt.RewriteStrip(log, scrollTexture, Cards, poolIcons, stripCache, ref stripChain, stripChainBytes);

            _spinT = 0f;
            var s = Scroll;
            s.StartCell = LobbyPropFactory.ScrollCellAt(s.UOffset);

            // Land on a card of the forced type (test hook) or on any cell (natural spin). Both
            // paths end in the same settle: the card under the marker is read and applied.
            if (!string.IsNullOrEmpty(forceType))
            {
                ReelCardType type;
                int forcedKind;
                if (!TryParseCardType(forceType, out type, out forcedKind))
                {
                    log?.LogWarning($"[reel] outcome={forceType} is not a card type; spin not started");
                    _pull = null;
                    return;
                }
                int forced = ReelDeck.CellOfType(Cards, type, forcedKind, LobbyPropFactory.Seed());
                if (forced < 0)
                {
                    log?.LogWarning($"[reel] outcome={forceType}: no card of that type in this deck");
                    _pull = null;
                    return;
                }
                s.TargetCell = forced;
                log?.LogInfo($"[reel] outcome={forceType} injection: deck card={forced} " +
                             $"type={ReelDeck.TypeName(type)} code={ReelArt.CardCode(Cards, forced)} " +
                             $"landed by the same settle path as a natural spin");
            }
            else
            {
                // THE SELECTION ALGORITHM: one call, whatever scheme the table is in. The scheme
                // and its numbers live in ReelTable/ReelDraw, never here.
                var draw = ReelDraw.Next(_draw, Cards, LobbyPropFactory.Seed());
                s.TargetCell = draw.Cell;
                log?.LogInfo($"[reel] drawn cell={draw.Cell} type={ReelDeck.TypeName(draw.Type)} " +
                             $"via={_draw.Mode} why='{draw.Why}' pulls={_draw.Pulls} sinceItem={_draw.SinceItem}");
            }

            float from = s.UOffset;
            float canonical = LobbyPropFactory.ScrollOffsetForCell(s.TargetCell);
            float distance = canonical - from;
            float minTravel = LobbyPropFactory.ScrollTravelCells * LobbyPropFactory.ScrollUCard;
            while (distance < minTravel) distance += 1f;      // whole strip widths, never a nudge
            s.From = from;
            s.To = from + distance + LobbyPropFactory.ScrollOvershootCells * LobbyPropFactory.ScrollUCard;
            s.ClickTo = from + distance;
            s.Duration = LobbyPropFactory.ScrollSpinSeconds;
            s.ClickDuration = LobbyPropFactory.ScrollClickSeconds;
            s.Phase = 1;
            _spinActive = true;
            _spins++;
            _lastTickCell = s.StartCell;
            UpdatePreview();   // an accepted spin removes the preview and restores the strip at once
            ReelAudio.Cue(log, ReelCue.Lever);
            log?.LogInfo($"[reel] spin start trigger={trigger} spins={_spins} machine='{MachineId}' spinId={_spinId} " +
                         $"actor={_spinActor} fromCell={s.StartCell} " +
                         $"targetCell={s.TargetCell} targetType={ReelArt.CardTypeName(Cards, s.TargetCell)} " +
                         $"targetCode={ReelArt.CardCode(Cards, s.TargetCell)} " +
                         $"travel={Inv(distance / LobbyPropFactory.ScrollUCard)}cards ({(distance / LobbyPropFactory.ScrollUCard / ReelArt.Cells):F2} strips) " +
                         $"duration={LobbyPropFactory.ScrollSpinSeconds:F2}s ease=1-(1-u)^{LobbyPropFactory.ScrollEasePower:F2} " +
                         $"click={LobbyPropFactory.ScrollClickSeconds:F2}s overshoot={LobbyPropFactory.ScrollOvershootCells:F2}card " +
                         $"capture={(captureMode ?? "none")} pull={(pull != null)} forced={(forceType ?? "-")} " +
                         $"algorithm={_draw.Mode} composeSeed={_deckComposeSeed} shuffleSeed={shuffleSeed} " +
                         $"deckSig=0x{ReelCanonical.Signature(Cards):x8} sound=lever");
            if (captureMode != null) StartReelCapture(captureMode, captureTag);

            // Clients show exactly this deck at the same landing cell: the ordered card array rides in
            // the frame (authoritative - a client's own eligible-item scan and composition are never
            // trusted for display), and the seed pair travels with it so a client can *check* its own
            // rebuild against the host's order. Still a broadcast of what already happened here,
            // never a request to agree on an outcome.
            if (_spinId != 0 && ReelNet.Broadcasting)
                ReelNet.BroadcastSpin(log, _spinId, actor, MachineId, RemoteModels.Clock(),
                                      LobbyPropFactory.ScrollSpinSeconds + LobbyPropFactory.ScrollClickSeconds,
                                      s.StartCell, s.TargetCell, _deckComposeSeed, shuffleSeed, _deckDebuffMask,
                                      ReelDeck.TypeCounts(Cards), Cards);
        }
        catch (Exception e)
        {
            _spinActive = false;
            _pull = null;
            log?.LogWarning($"[reel] spin start failed: {e.Message}");
        }
    }


    private static bool TryParseCardType(string text, out ReelCardType type) =>
        TryParseCardType(text, out type, out _);

    // `debuff` forces any debuff card; `slow`/`fatigue`/`blind` force that exact effect's card, so
    // every debuff face can be captured from its own settle path.
    private static bool TryParseCardType(string text, out ReelCardType type, out int debuffKind)
    {
        type = ReelCardType.Item;
        debuffKind = -1;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "item": type = ReelCardType.Item; return true;
            case "damage": type = ReelCardType.Damage; return true;
            case "hp": type = ReelCardType.Damage; return true;
            case "debuff": type = ReelCardType.Debuff; return true;
            case "double": type = ReelCardType.Double; return true;
            case "blank": case "blnk": type = ReelCardType.Blank; return true;
            case "slow": type = ReelCardType.Debuff; debuffKind = (int)ReelDebuffKind.Slow; return true;
            case "fatigue": case "tired": type = ReelCardType.Debuff; debuffKind = (int)ReelDebuffKind.Fatigue; return true;
            case "blind": type = ReelCardType.Debuff; debuffKind = (int)ReelDebuffKind.Blind; return true;
            default: return false;
        }
    }

    private void StepSpin(float dt)
    {
        if (!_spinActive) return;
        _spinT += dt;
        var s = Scroll;
        float last = s.UOffset;
        bool done = false;
        if (s.Phase == 1)
        {
            float u = Mathf.Clamp01(_spinT / Mathf.Max(0.01f, s.Duration));
            s.UOffset = Mathf.LerpUnclamped(s.From, s.To, LobbyPropFactory.ScrollEase(u));
            if (u >= 1f) s.Phase = 2;
        }
        else if (s.Phase == 2)
        {
            float u = Mathf.Clamp01((_spinT - s.Duration) / Mathf.Max(0.01f, s.ClickDuration));
            s.UOffset = Mathf.LerpUnclamped(s.To, s.ClickTo, 1f - Mathf.Pow(1f - u, 2f));   // click back
            if (u >= 1f) { s.Phase = 3; s.UOffset = s.ClickTo; done = true; }
        }
        ApplyOffsets();
        float v = dt > 0.0001f ? (s.UOffset - last) / dt / LobbyPropFactory.ScrollUCard : 0f;   // cards/s
        float travelled = (s.UOffset - s.From) / LobbyPropFactory.ScrollUCard;
        int cell = LobbyPropFactory.ScrollCellAt(s.UOffset);
        // Per-card tick: one click each time a new card passes the marker, pitch rising with the
        // strip speed, so the spin is audible without a single looped sound. One per frame at most
        // (the game is capped at 15 fps, the strip peaks near 30 cards/s).
        if (cell != _lastTickCell)
        {
            _lastTickCell = cell;
            if (Mathf.Abs(v) > 0.20f) ReelAudio.Tick(log, v);
        }
        log?.LogInfo($"[reel:scroll] t={Inv(_spinT)} u={Inv(s.UOffset, 5)} travel={Inv(travelled)}cards " +
                     $"cell={cell} type={ReelArt.CardTypeName(Cards, cell)} code={ReelArt.CardCode(Cards, cell)} " +
                     $"centre={Inv(LobbyPropFactory.ScrollCentreProximity(s.UOffset), 3)} " +
                     $"v={Inv(v, 3)}cards/s phase={s.Phase}");
        if (!done) return;

        _spinActive = false;
        s.UOffset = LobbyPropFactory.ScrollOffsetForCell(s.TargetCell);   // canonical small offset
        s.Phase = 3;
        ApplyOffsets();
        ReelAudio.Cue(log, ReelCue.Settle);
        int settledCell = LobbyPropFactory.ScrollCellAt(s.UOffset);
        var settledCard = ReelArt.CardAt(Cards, settledCell);
        float proximity = LobbyPropFactory.ScrollCentreProximity(s.UOffset);
        bool match = settledCell == s.TargetCell;
        bool sameCard = settledCard.Type == ReelArt.CardAt(Cards, s.TargetCell).Type &&
                        settledCard.Item == ReelArt.CardAt(Cards, s.TargetCell).Item;
        var pull = _pull;
        _pull = null;
        log?.LogInfo($"[reel] settled card={settledCell} type={ReelDeck.TypeName(settledCard.Type)} " +
                     $"code={ReelDeck.Code(settledCard)} id='{ReelDeck.IdOf(settledCard)}' " +
                     $"targetCell={s.TargetCell} targetType={ReelArt.CardTypeName(Cards, s.TargetCell)} " +
                     $"u={Inv(s.UOffset, 5)} centre={Inv(proximity, 3)} match={match} sameCard={sameCard} " +
                     $"total={Inv(_spinT)}s pull={(pull != null)} grants={(pull != null ? "deferred-to-card" : "0")} " +
                     "sound=settle");
        ApplyScrollEmphasis(proximity);
        // The card under the marker IS the outcome: apply exactly that card, nothing else. A spin
        // that came from the host applies nothing here; it shows the host's result line instead.
        SettleNetworked(pull, settledCell, settledCard);
        _nextSpinAt = _clock + 0.75f;
        // The result is on screen: hold the genuine strip (and the marker) for the toast, then the
        // preview comes back. UpdatePreview is frame-driven, so this only sets the moment.
        _resultUntil = _clock + PreviewHoldSeconds;
        if (_remoteSpin) { _remoteSpin = false; _spinId = 0; _spinActor = 0; }
    }

    private void ApplyOffsets()
    {
        var s = Scroll;
        if (s?.Material == null) return;
        s.Material.SetTextureOffset("_BaseMap", new Vector2(s.UOffset - Mathf.Floor(s.UOffset), 0f));
        ApplyScrollEmphasis(LobbyPropFactory.ScrollCentreProximity(s.UOffset));
    }

    // Per-card emphasis: the two pointer lamps brighten as a card comes to rest between them.
    // At full speed this is a 24 Hz pulse that no 15 fps frame can resolve; through the deceleration
    // it reads as each card "lighting" as it lands, which is the emphasis the case-opening look
    // needs - the card itself is not scaled, so the strip never stutters. Both pointers share one
    // property block, so the pair pulses as one mark.
    private void ApplyScrollEmphasis(float proximity)
    {
        if (_previewShown) return;   // idle preview: no marker, so no emphasis to pulse
        if (_markerGlowTop == null && _markerGlowBottom == null) return;
        float p = Mathf.Clamp01(proximity);
        {
            var c = LobbyPropFactory.MarkerGlow;
            float g = 0.70f + 0.60f * p;
            try
            {
                _mpb.Clear();
                _mpb.SetColor("_BaseColor", new Color(c.r * g, c.g * g, c.b * g, c.a * (0.75f + 0.5f * p)));
                _mpb.SetColor("_Color", new Color(c.r * g, c.g * g, c.b * g, c.a * (0.75f + 0.5f * p)));
                if (_markerGlowTop != null) _markerGlowTop.SetPropertyBlock(_mpb);
                if (_markerGlowBottom != null) _markerGlowBottom.SetPropertyBlock(_mpb);
            }
            catch { }
        }
    }

    private static string Inv(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    private static string Inv(float v, int digits) => v.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);
    private static string FmtV(Vector3 v) => $"({Inv(v.x)},{Inv(v.y)},{Inv(v.z)})";

    // ---- filmstrip ---------------------------------------------------------------------

    // Seven points on the spin curve, including the settle. Each one freezes the row, logs the
    // offset and the card under the marker, then records ONE frame from the same pinned close-up
    // camera, so the filmstrip is seven stills of seven positions the real spin passes through.
    private static readonly float[] FilmTimes = { 0.06f, 0.24f, 0.50f, 0.85f, 1.35f, 2.10f, 3.17f };
    private bool _filmActive;
    private int _filmStep;
    private float _filmNextAt;
    private float _filmFrom, _filmTo, _filmClickTo;
    private string _filmTag;

    private void StartFilmstrip(string tag)
    {
        if (_filmActive) return;
        if (_spinActive) { log?.LogWarning("[reel:film] a spin is running; ignored"); return; }
        if (Scroll == null) return;
        if (!HasLocalPlayer()) { log?.LogWarning("[reel:film] no local player in the scene"); return; }
        // Deterministic curve: target card 7, exactly three strip widths of travel, then the click.
        int target = 7;
        _filmFrom = InitialScrollOffset();
        float canonical = LobbyPropFactory.ScrollOffsetForCell(target);
        float distance = canonical - _filmFrom;
        float minTravel = LobbyPropFactory.ScrollTravelCells * LobbyPropFactory.ScrollUCard;
        while (distance < minTravel) distance += 1f;
        _filmTo = _filmFrom + distance + LobbyPropFactory.ScrollOvershootCells * LobbyPropFactory.ScrollUCard;
        _filmClickTo = _filmFrom + distance;
        _filmTag = string.IsNullOrEmpty(tag) ? "reelfilm" : tag;
        _filmStep = 0;
        _filmActive = true;
        _filmNextAt = _clock + 0.30f;
        ShowStripFor(FilmTimes[FilmTimes.Length - 1] + 1.0f);   // the film photographs the real row
        log?.LogInfo($"[reel:film] start tag='{_filmTag}' points={FilmTimes.Length} targetCell={target} " +
                     $"from={Inv(_filmFrom, 5)} to={Inv(_filmTo, 5)} clickTo={Inv(_filmClickTo, 5)} " +
                     $"canonical={Inv(canonical, 5)} travel={Inv(distance / LobbyPropFactory.ScrollUCard)}cards " +
                     $"curve=1-(1-u)^{LobbyPropFactory.ScrollEasePower:F2} duration={LobbyPropFactory.ScrollSpinSeconds:F2}s");
    }

    private void StepFilmstrip()
    {
        if (!_filmActive) return;
        if (ClipRecorder.IsRecording) return;
        if (_clock < _filmNextAt) return;
        if (_filmStep >= FilmTimes.Length)
        {
            _filmActive = false;
            log?.LogInfo($"[reel:film] done points={FilmTimes.Length} grants=0");
            return;
        }
        int k = _filmStep;
        float t = FilmTimes[k];
        float dur = LobbyPropFactory.ScrollSpinSeconds;
        float u;
        int phase;
        if (t <= dur)
        {
            u = Mathf.LerpUnclamped(_filmFrom, _filmTo, LobbyPropFactory.ScrollEase(t / dur));
            phase = 1;
        }
        else
        {
            float c = Mathf.Clamp01((t - dur) / Mathf.Max(0.01f, LobbyPropFactory.ScrollClickSeconds));
            u = Mathf.LerpUnclamped(_filmTo, _filmClickTo, 1f - Mathf.Pow(1f - c, 2f));
            phase = c >= 1f ? 3 : 2;
        }
        Scroll.UOffset = u;
        Scroll.Phase = phase;
        ApplyOffsets();
        int cell = LobbyPropFactory.ScrollCellAt(u);
        float travelled = (u - _filmFrom) / LobbyPropFactory.ScrollUCard;
        log?.LogInfo($"[reel:film] k={k} t={Inv(t)} u={Inv(u, 5)} travel={Inv(travelled)}cards " +
                     $"cell={cell} type={ReelArt.CardTypeName(Cards, cell)} code={ReelArt.CardCode(Cards, cell)} " +
                     $"centre={Inv(LobbyPropFactory.ScrollCentreProximity(u), 3)}");
        StartReelStill(_filmStep.ToString());
        _filmStep++;
        _filmNextAt = _clock + 0.45f;
    }

    private void StartReelStill(string name)
    {
        int index = int.TryParse(name, out int parsed) ? parsed : -1;
        var t = Root.transform;
        Vector3 look = t.position + Vector3.up * 1.96f;
        Vector3 eye = t.position + t.forward * 1.30f + Vector3.up * 1.96f;
        string tagName = index >= 0 ? (_filmTag ?? "reelfilm") + "_" + index : (_filmTag ?? "reelfilm") + "_" + name;
        var spec = ClipRecorder.ClipSpec.Default(tagName);
        spec.Seconds = 0.2f;
        spec.Fps = 5f;
        spec.Width = 960;
        spec.Height = 540;
        spec.Fov = 40f;
        spec.HasEye = true;
        spec.HasLookAt = true;
        spec.Eye = eye;
        spec.LookAt = look;
        bool ok = ClipRecorder.Record(log, spec);
        log?.LogInfo($"[reel:film] shot {(index >= 0 ? "k=" + index : name)} tag='{spec.Tag}' ok={ok} eye={FmtV(eye)} look={FmtV(look)}");
    }

    // Verification captures from the close-up (window), the showcase (whole machine) and the game
    // camera. RenderTexture only, so the desktop can be locked.
    private void StartReelCapture(string mode, string tag)
    {
        try
        {
            if (ClipRecorder.IsRecording)
            {
                log?.LogWarning("[reel:capture] a clip is already recording; spin capture skipped");
                return;
            }
            var t = Root.transform;
            var spec = ClipRecorder.ClipSpec.Default(string.IsNullOrEmpty(tag) ? "reelspin_" + mode : tag);
            if (mode == "main")
            {
                spec.View = "main";
                spec.Seconds = 3.9f;
                spec.Fps = 12f;
                spec.Width = 640;
                spec.Height = 360;
            }
            else if (mode == "showcase")
            {
                // Whole machine in frame: 4.2 m out with the look point at 1.50 m and fov 42 covers
                // -0.1 .. 3.1 m, i.e. floor to above the crown at 3.04 m.
                Vector3 look = t.position + Vector3.up * 1.50f;
                Vector3 eye = t.position + t.forward * 4.20f + Vector3.up * 1.55f;
                spec.Seconds = 3.9f;
                spec.Fps = 10f;
                spec.Width = 960;
                spec.Height = 540;
                spec.Fov = 42f;
                spec.HasEye = true;
                spec.HasLookAt = true;
                spec.Eye = eye;
                spec.LookAt = look;
            }
            else if (mode == "spawn")
            {
                // The game camera's own recorded spawn pose (4.6 m ahead of the machine, 9.7 deg off
                // its axis, fov 70, 640x360) - the lens and the distance the sign is actually seen
                // from - pinned because the local player in this session stands behind the cabinet.
                Vector3 eye = new Vector3(5.35f, 1.80f, -7.57f);
                Vector3 fwd = new Vector3(-1f, 0f, -0.04f).normalized;
                spec.Seconds = 3.9f;
                spec.Fps = 12f;
                spec.Width = 640;
                spec.Height = 360;
                spec.Fov = 70f;
                spec.HasEye = true;
                spec.HasLookAt = true;
                spec.Eye = eye;
                spec.LookAt = eye + fwd * 5f;
            }
            else
            {
                // Window close-up: the 0.72 m row fills most of a 960 px frame at 1.3 m / fov 40.
                Vector3 look = t.position + Vector3.up * 1.96f;
                Vector3 eye = t.position + t.forward * 1.30f + Vector3.up * 1.96f;
                spec.Seconds = 3.6f;
                spec.Fps = 10f;
                spec.Width = 960;
                spec.Height = 540;
                spec.Fov = 40f;
                spec.HasEye = true;
                spec.HasLookAt = true;
                spec.Eye = eye;
                spec.LookAt = look;
            }
            bool ok = ClipRecorder.Record(log, spec);
            log?.LogInfo($"[reel:capture] mode={mode} clip='{spec.Tag}' ok={ok} view={spec.View} " +
                         $"seconds={spec.Seconds:F1} fps={spec.Fps:F0} {spec.Width}x{spec.Height} fov={spec.Fov:F0} " +
                         $"pinned={spec.HasEye && spec.HasLookAt} eye={(spec.HasEye ? FmtV(spec.Eye) : "-")} " +
                         $"look={(spec.HasLookAt ? FmtV(spec.LookAt) : "-")}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:capture] failed: {e.Message}"); }
    }

    // ---- measurement / teardown ------------------------------------------------------------

    public void Measure(IHostContext log)
    {
        try
        {
            Bounds = new Bounds(Anchor + Vector3.up * 1.475f, Vector3.zero);
            bool first = true;
            for (int i = 0; i < Renderers.Count; i++)
            {
                var r = Renderers[i];
                if (r == null) continue;
                if (first) { Bounds = r.bounds; first = false; }
                else Bounds.Encapsulate(r.bounds);
            }
            var size = Bounds.size;
            var centre = Bounds.center;
            float relY = centre.y - Anchor.y;
            // True part dimensions in the prop's own frame (the world AABB is inflated by the yaw).
            var local = new Bounds(Vector3.zero, Vector3.zero);
            bool lfirst = true;
            for (int i = 0; i < meshes.Count; i++)
            {
                if (meshes[i] == null) continue;
                if (lfirst) { local = meshes[i].bounds; lfirst = false; }
                else local.Encapsulate(meshes[i].bounds);
            }
            log.LogInfo($"[prop:local] bounds centre={local.center} size=({local.size.x:F3},{local.size.y:F3},{local.size.z:F3}) " +
                        $"width={local.size.x:F3} height={local.size.y:F3} depth={local.size.z:F3} " +
                        $"aspect={local.size.y / Mathf.Max(local.size.x, 0.001f):F2}:1 " +
                        $"(body 1.00x0.34x2.95; z above 0.160 is the neon/arrow trim) (yaw-inflated world AABB below)");
            log.LogInfo($"[prop:measure] worldBounds centre={centre} size=({size.x:F3},{size.y:F3},{size.z:F3}) " +
                        $"height={size.y:F3} width={size.x:F3} depth={size.z:F3} floorY={Anchor.y:F3} " +
                        $"baseY={Bounds.min.y:F3} topY={Bounds.max.y:F3} centreAboveFloor={relY:F3} " +
                        $"aspect={size.y / Mathf.Max(size.x, 0.001f):F2}:1 parts={PartCount} tris={TriangleCount}");
            var sb = new StringBuilder();
            sb.Append(_parts);
            log.LogInfo($"[prop:parts] {sb}");
            // Diagnostic: the six highest renderers, to prove the tray geometry is on screen.
            var order = new List<Renderer>(Renderers);
            order.Sort((a, b) => (b != null ? b.bounds.max.y : 0f).CompareTo(a != null ? a.bounds.max.y : 0f));
            var top = new StringBuilder();
            for (int i = 0; i < 6 && i < order.Count; i++)
            {
                var r = order[i];
                if (r == null) continue;
                var bb = r.bounds;
                top.Append($"{r.gameObject.name} y=({bb.min.y:F2}..{bb.max.y:F2}) x=({bb.min.x:F2}..{bb.max.x:F2}) " +
                           $"zCentre={bb.center.z:F2} enabled={r.enabled} mat='{r.sharedMaterial?.name}' layer={r.gameObject.layer}; ");
            }
            log.LogInfo($"[prop:top] {top}");
        }
        catch (Exception e) { log.LogWarning($"[prop:measure] failed: {e.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Interaction.Teardown(log); } catch { }
        try { ReelHud.HidePrompt(log); } catch { }
        try { ReelAudio.Dispose(); } catch { }
        try { if (Root != null) Root.SetActive(false); } catch { }
        for (int i = 0; i < Renderers.Count; i++)
            try { if (Renderers[i] != null) Renderers[i].enabled = false; } catch { }
        for (int i = 0; i < meshes.Count; i++)
            try { if (meshes[i] != null) UnityEngine.Object.Destroy(meshes[i]); } catch { }
        for (int i = 0; i < materials.Count; i++)
            try { if (materials[i] != null) UnityEngine.Object.Destroy(materials[i]); } catch { }
        try { if (signTexture != null) UnityEngine.Object.Destroy(signTexture); } catch { }
        try { if (glowTexture != null) UnityEngine.Object.Destroy(glowTexture); } catch { }
        try { if (scrollTexture != null) UnityEngine.Object.Destroy(scrollTexture); } catch { }
        try { if (Root != null) UnityEngine.Object.Destroy(Root); } catch { }
        if (log != null) log.LogInfo($"[prop:disposed] parts={PartCount} meshes={meshes.Count} materials={materials.Count} " +
                                     $"textures={(signTexture != null ? 1 : 0) + (glowTexture != null ? 1 : 0) + (scrollTexture != null ? 1 : 0) + (fadeTexture != null ? 1 : 0)} lights={Lights.Count}");
        meshes.Clear(); materials.Clear(); Renderers.Clear(); Lights.Clear();
        scrollMaterials.Clear();
        markerRenderers.Clear();
        previewGo = null;
        scrollRowRenderer = null; scrollFadeRenderer = null;
        Scroll = null; _markerGlowTop = null; _markerGlowBottom = null;
        try { if (fadeTexture != null) UnityEngine.Object.Destroy(fadeTexture); } catch { }
        try { if (previewTexture != null) UnityEngine.Object.Destroy(previewTexture); } catch { }
        signTexture = null; glowTexture = null; scrollTexture = null; fadeTexture = null;
        previewTexture = null; previewMaterial = null; _previewBuilt = false; _previewShown = false;
        fadeMaterial = null;
        litMaterial = neonMaterial = signMaterial = glowMaterial = null; Root = null;
    }
}
