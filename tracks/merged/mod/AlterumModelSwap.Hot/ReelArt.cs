using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Procedurally generated artwork for the Reel of Fortune prop.
//
// Nothing here comes from the reference video, from a screenshot, or from an OS font:
//   * every sign element (title, instruction banner, reel housing, footer) is drawn at load time
//     into an RGBA byte canvas by BitmapFont5x7, a hand-written 5x7 glyph table in this file;
//   * the card icons are Alterum's own item icons: a live Sprite object when the game has it
//     loaded (RenderTexture -> ReadPixels -> PngDecoder), otherwise the embedded
//     `reel_pool_icons.png` sheet that tools/gen-reel-pool.py packed from the same atlases;
//   * the strip, the five card faces, the rarity bands, frames, glyphs and codes are composed here.
//
// The strip is ONE ROW of cards - one card per deck slot, `ReelDeck.Cells` of them - scrolled in +u
// against a fixed centre marker. Card types come from ReelDeck (item / damage / debuff / double);
// this file only decides how each type looks.
//
// All textures are uploaded through Texture2D.LoadRawTextureData with a box-filtered mip chain,
// the same path PngDecoder uses, because ImageConversion.LoadImage throws in this build.
// ArtCanvas is top-down (row 0 = top); ToBottomUpRgba() flips it into Unity's raw row order.
internal static class ReelArt
{
    // ---- layout shared with LobbyProp ------------------------------------------------
    // The atlas bands are sized for the distance the prop is actually seen from. From the spawn
    // camera (4.6 m, 70 deg fov, 640x360) the 0.76 m art width is ~42 screen px, so one screen
    // pixel is ~24 atlas px. The title is therefore split over the two top bands ('REEL OF' /
    // 'FORTUNE') and drawn with the 3x5 display font at scale 32: 6.5 screen-px caps and 1.3 screen-px
    // strokes, which survive mip minification. A single 5x7 line of 15 glyphs would be 2.8 screen px
    // per character and unreadable.
    internal const int AtlasW = 1024, AtlasH = 1024;
    internal const int RowTitleTop = 0, RowTitleBottom = 186;
    internal const int RowBannerTop = 186, RowBannerBottom = 372;
    internal const int RowReelsTop = 372, RowReelsBottom = 802;
    internal const int RowFooterTop = 804, RowFooterBottom = 946;

    // One horizontal strip texture: `ReelDeck.Cells` cards side by side, card 0 at u=0 (texture
    // left). The card box is 0.240 x 0.300 m on the face, so a 128x160 px card is 533 px/m - about
    // 2.7x the 47 screen px a card covers in the close-up camera - and 64 cards stay inside the
    // maximum texture width of every GPU this build runs on (64 x 128 = 8192).
    internal const int CellW = 128, CellH = 160, Cells = ReelDeck.Cells;
    internal const int StripW = CellW * Cells, StripH = CellH;
    // Face geometry of that strip (metres). The window is 0.72 x 0.30 in the 0.337 m reels band and
    // shows exactly three cards; the well in the atlas is 25 mm wider all round.
    internal const float ScrollWindowW = 0.720f, ScrollWindowH = 0.300f;
    internal const float ScrollWellW = 0.745f, ScrollWellH = 0.318f;
    internal const float BandReels = 0.337f;
    internal const float CardAspect = 0.240f / 0.300f;
    // Fraction of the window at each end that ramps the cards into shadow (the case-opening fade).
    internal const float FadeEdge = 0.26f;

    // Palette from the reference sign (plate #4E1A22, type #FFD24A, bar #FFB43A) plus the darker
    // tones the plate needs to read as a lit panel rather than flat paint. The plate was raised a
    // step for the 640x360 view: at that size the sign is ~42x40 px, so the panel needs a brighter
    // base to separate from the tent while the title stays the brightest element.
    internal static readonly Color32 MaroonTop = Rgba(0x72, 0x28, 0x32);
    internal static readonly Color32 MaroonBottom = Rgba(0x45, 0x14, 0x1D);
    internal static readonly Color32 MaroonDeep = Rgba(0x34, 0x10, 0x19);
    internal static readonly Color32 Gold = Rgba(0xFF, 0xD2, 0x4A);
    internal static readonly Color32 GoldDim = Rgba(0xB0, 0x82, 0x2E);
    internal static readonly Color32 Amber = Rgba(0xFF, 0xB4, 0x3A);
    internal static readonly Color32 Cream = Rgba(0xF7, 0xEF, 0xC0);
    // Title gold: one step hotter than the trim so the two-line title out-luminates every other
    // mark on the plate (with the 1.2 material tint it lands above 1.0 in the red channel).
    internal static readonly Color32 TitleGold = Rgba(0xFF, 0xE0, 0x8A);
    internal static readonly Color32 Ink = Rgba(0x1A, 0x07, 0x0C);
    internal static readonly Color32 Black = Rgba(0x08, 0x03, 0x05);

    internal static Color32 Rgba(int r, int g, int b, int a = 255) =>
        new Color32((byte)r, (byte)g, (byte)b, (byte)a);

    // The five card kinds (see ReelDeck). Colour and glyph have to carry the type at a glance from
    // the spawn camera, where a card is ~13 screen px wide, so each type owns a card face colour,
    // a band colour and a motif:
    //   blank  - neutral grey face, plain grey band, NO motif, "--" (the quiet majority: nothing
    //            happens, the stake is gone). It must not look like damage or like a debuff;
    //   item   - the item's own icon + 3-letter code, category band, maroon face;
    //   damage - red face, cracked heart, "DMG";
    //   debuff - purple face, spiral (a real slow effect), "DBF";
    //   double - gold face, big X2, grants two of the inserted item.
    internal static readonly Color32 DamageTop = Rgba(0x8E, 0x1B, 0x22), DamageBottom = Rgba(0x46, 0x0A, 0x10);
    internal static readonly Color32 DebuffTop = Rgba(0x4A, 0x27, 0x86), DebuffBottom = Rgba(0x24, 0x10, 0x46);
    internal static readonly Color32 DoubleTop = Rgba(0x7C, 0x5A, 0x14), DoubleBottom = Rgba(0x3C, 0x2A, 0x06);
    // Blank is the only desaturated face in the deck, and the only one whose band is not a signal
    // colour at all - that is the point: it reads as "nothing" from across the room.
    internal static readonly Color32 BlankTop = Rgba(0x3E, 0x3C, 0x42), BlankBottom = Rgba(0x1C, 0x1B, 0x20);
    internal static readonly Color32 BlankBand = Rgba(0x8C, 0x8A, 0x94);
    internal static readonly Color32 BlankEdge = Rgba(0x6E, 0x6C, 0x76);
    internal static readonly Color32 DamageBand = Rgba(0xE8, 0x38, 0x2E);
    internal static readonly Color32 DebuffBand = Rgba(0x9B, 0x5C, 0xF0);
    internal static readonly Color32 DoubleBand = Rgba(0xFF, 0xC4, 0x2E);
    internal static readonly Color32 HeartRed = Rgba(0xF2, 0x4C, 0x3C);
    internal static readonly Color32 SpiralViolet = Rgba(0xD9, 0xB4, 0xFF);
    // One face per real debuff effect (ReelDebuffKind). Slow keeps the old violet; Fatigue is
    // green, Blind is a cold slate blue, so the three read apart at a glance and none of them
    // reuses the damage red or the double gold.
    internal static readonly Color32 FatigueTop = Rgba(0x2C, 0x60, 0x4A), FatigueBottom = Rgba(0x0F, 0x2E, 0x22);
    internal static readonly Color32 FatigueBand = Rgba(0x53, 0xCE, 0x90);
    internal static readonly Color32 BlindTop = Rgba(0x26, 0x3A, 0x6C), BlindBottom = Rgba(0x0D, 0x17, 0x33);
    internal static readonly Color32 BlindBand = Rgba(0x62, 0x96, 0xEE);
    internal static readonly Color32 FatigueGlyph = Rgba(0xB8, 0xF0, 0xC8);
    internal static readonly Color32 BlindGlyph = Rgba(0xCF, 0xE0, 0xFF);
    // The motif well is the card background darkened, not blended: an opaque colour so the whole
    // face can be drawn with direct writes instead of per-pixel alpha compositing.
    internal static readonly Color32 ShadowWell = Rgba(0x12, 0x08, 0x0B);

    // Rarity band per item category: a property of the item, so the same item always carries the
    // same band. The category table itself lives in ReelPool (our design, generated).
    internal static Color32 CategoryColour(string category)
    {
        switch (category)
        {
            case "consumable": return Rgba(0x4B, 0x69, 0xFF);   // blue
            case "scroll": return Rgba(0x3F, 0xBF, 0xD4);       // teal
            case "LQWeapon": return Rgba(0x5D, 0xC2, 0x4A);     // green
            case "HQWeapon": return Rgba(0x88, 0x47, 0xFF);     // violet
            case "occult": return Rgba(0xC8, 0x3C, 0x9B);       // magenta
            case "exit": return Rgba(0xD9, 0xA4, 0x41);         // old gold
            case "key": return Rgba(0xE0, 0xA0, 0x30);          // amber
            default: return Rgba(0x80, 0x80, 0x80);
        }
    }

    // Per-card tone, so identical cards never look cloned when they come round again.
    private static readonly float[] CellTone = { 1.00f, 0.93f, 1.07f, 0.96f, 1.03f, 0.92f, 0.95f, 1.08f,
                                                 0.94f, 1.02f, 0.95f, 1.06f, 1.04f, 0.92f, 0.98f, 1.08f };

    // The card under a strip cell. The deck order is reshuffled at every spin start, so this is the
    // one reader that decides what a settled spin means.
    internal static ReelCard CardAt(ReelCard[] cards, int cell)
    {
        if (cards == null || cards.Length == 0) return new ReelCard { Type = ReelCardType.Damage, Item = -1 };
        int k = ((cell % cards.Length) + cards.Length) % cards.Length;
        return cards[k];
    }

    internal static string CardCode(ReelCard[] cards, int cell) => ReelDeck.Code(CardAt(cards, cell));
    internal static string CardTypeName(ReelCard[] cards, int cell) => ReelDeck.TypeName(CardAt(cards, cell).Type);

    // Cell index whose centre is under the marker for a given u offset (see LobbyPropFactory:
    // the window samples u = uOffset + s * UScale, s=0.5 is the marker).
    internal static int CellAtMarker(float uOffset, float uScale)
    {
        float u = uOffset + uScale * 0.5f;
        return ((Mathf.FloorToInt(u * Cells) % Cells) + Cells) % Cells;
    }

    private const string TitleLine1 = "REEL OF";
    private const string TitleLine2 = "FORTUNE";
    // In-game instruction (this build). Both strings are shorter than the reference sign's
    // originals, so the 6-px footer scale that fit 26 glyphs still fits them; capping at 6 keeps
    // the reference glyph size instead of letting the fitter grow the two lines to scale 9.
    // The footer names the real outcome mix: "nothing" is the largest single card in the deck, and
    // a sign that promised only an item or a debuff would be lying about it.
    private const string BannerText = "INSERT ITEM TO PLAY";
    private const string FooterText = "ITEM DEBUFF OR NOTHING";
    private const int FooterScale = 6;

    // ---- icon pixels ----------------------------------------------------------------

    internal sealed class IconPixels
    {
        public string Name;
        public int W, H;
        public Color32[] Px;   // row 0 = top
    }

    // One pixel image per pool item (null when that item has no icon sprite). Runtime first - the
    // game's own atlas through RenderTexture -> ReadPixels -> PngDecoder, the path the six original
    // symbols used - then the embedded sheet the build tool packed from the same atlases, because
    // the Lobby only has ~14 item sprites in memory and the other 37 are never loaded there.
    internal static IconPixels[] LoadPoolIcons(IHostContext log)
    {
        var icons = new IconPixels[ReelPool.All.Length];
        var origin = new string[ReelPool.All.Length];
        var live = new Dictionary<string, IconPixels>(StringComparer.OrdinalIgnoreCase);
        int runtime = 0, sheet = 0;
        string census = "not scanned";
        IconPixels sheetPixels = null;

        try { census = ScanRuntime(log, live); }
        catch (Exception e) { log.LogWarning($"[reel:icons] runtime sprite scan failed: {e.Message}"); }
        try { sheetPixels = LoadEmbeddedSheet(log); }
        catch (Exception e) { log.LogWarning($"[reel:icons] embedded icon sheet failed: {e.Message}"); }

        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            var it = ReelPool.All[i];
            if (!it.Usable) continue;
            IconPixels px = null;
            if (!string.IsNullOrEmpty(it.Sprite))
            {
                try { live.TryGetValue(it.Sprite, out px); } catch { px = null; }
            }
            if (px != null) { origin[i] = "runtime"; runtime++; }
            else if (sheetPixels != null && it.HasIcon)
            {
                px = Crop(sheetPixels, it.IconX, it.IconY, it.IconW, it.IconH, it.Sprite);
                if (px != null) { origin[i] = "sheet"; sheet++; }
            }
            icons[i] = px;
        }

        var chosen = new StringBuilder();
        int none = 0;
        for (int i = 0; i < ReelPool.All.Length && chosen.Length < 900; i++)
        {
            if (!ReelPool.All[i].Usable) continue;
            if (icons[i] == null) { none++; chosen.Append(ReelPool.All[i].Code).Append("=code "); continue; }
            chosen.Append(ReelPool.All[i].Code).Append('=').Append(origin[i]).Append(' ');
        }
        log.LogInfo($"[reel:icons] pool={ReelPool.UsableCount()} runtime={runtime} sheet={sheet} " +
                    $"codeOnly={none} card={CellW}x{CellH}px sheet={(sheetPixels != null ? sheetPixels.W + "x" + sheetPixels.H : "none")} " +
                    $"chosen=[{chosen}]");
        log.LogInfo($"[reel:icons] census {census}");
        return icons;
    }

    // Enumerate live Sprite objects once, read only the names the pool actually wants, and log what
    // the scene has loaded - the Lobby and a map scene load different subsets of the atlas.
    private static string ScanRuntime(IHostContext log, Dictionary<string, IconPixels> found)
    {
        var sprites = SceneCache.Scan(Il2CppType.Of<Sprite>());
        var wanted = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        int live = 0;
        var itemLike = new List<string>();
        for (int i = 0; i < sprites.Length; i++)
        {
            Sprite s;
            try { s = sprites[i].TryCast<Sprite>(); } catch { continue; }
            if (s == null) continue;
            string n = s.name ?? "";
            if (n.Length == 0) continue;
            live++;
            if (!n.StartsWith("item", StringComparison.OrdinalIgnoreCase) &&
                !n.StartsWith("spells-icons", StringComparison.OrdinalIgnoreCase) &&
                !n.StartsWith("weapons-", StringComparison.OrdinalIgnoreCase)) continue;
            if (itemLike.Count < 24) itemLike.Add(n);
            if (!wanted.ContainsKey(n)) wanted[n] = s;
        }

        int read = 0;
        var texNames = new List<string>();
        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            string want = ReelPool.All[i].Sprite;
            if (string.IsNullOrEmpty(want) || found.ContainsKey(want)) continue;
            Sprite s;
            if (!wanted.TryGetValue(want, out s) || s == null) continue;
            try
            {
                var px = ReadSprite(log, s, want);
                if (px == null) continue;
                found[want] = px;
                read++;
                try
                {
                    string tn = s.texture != null ? s.texture.name : "?";
                    if (texNames.Count < 4 && !texNames.Contains(tn)) texNames.Add(tn);
                }
                catch { }
            }
            catch (Exception e) { log.LogWarning($"[reel:icons] sprite '{s.name}' read failed: {e.Message}"); }
        }
        return $"sprites loaded={live} iconLike={itemLike.Count} poolSpritesRead={read} atlases=[{string.Join(",", texNames)}] " +
               $"loadedIcons=[{string.Join(",", itemLike)}]";
    }

    // Copy a sprite (possibly a sub-rect of a packed atlas) through a RenderTexture, because the
    // game's textures are not CPU-readable. The PNG round-trip decodes with PngDecoder, which is
    // already the proven path in this build.
    private static IconPixels ReadSprite(IHostContext log, Sprite s, string label)
    {
        var tex = s.texture;
        if (tex == null) return null;
        Rect r = s.textureRect;
        if (r.width < 4f || r.height < 4f) return null;
        float tw = Mathf.Max(1, tex.width), th = Mathf.Max(1, tex.height);
        float scale = Mathf.Min(1f, 256f / Mathf.Max(r.width, r.height));
        int w = Mathf.Clamp(Mathf.RoundToInt(r.width * scale), 8, 256);
        int h = Mathf.Clamp(Mathf.RoundToInt(r.height * scale), 8, 256);

        RenderTexture rt = null;
        Texture2D copy = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "AlterumReelIconRT", antiAliasing = 1, useMipMap = false,
                autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!rt.Create() || !rt.IsCreated()) throw new InvalidOperationException("RenderTexture.Create failed");
            Graphics.Blit(tex, rt, new Vector2(r.width / tw, r.height / th), new Vector2(r.x / tw, r.y / th));
            RenderTexture.active = rt;
            copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(copy);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            var rgba = PngDecoder.DecodeRgbaBottomUp(bytes, out int dw, out int dh);
            var px = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int srcRow = dh - 1 - y;   // decoder is bottom-up; icons are top-down
                for (int x = 0; x < dw; x++)
                {
                    int i = (srcRow * dw + x) * 4;
                    px[y * dw + x] = new Color32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
                }
            }
            var icon = new IconPixels { Name = s.name ?? label, W = dw, H = dh, Px = px };
            log.LogInfo($"[reel:icons] sprite '{icon.Name}' texture='{tex.name}' {tex.width}x{tex.height} " +
                        $"rect=({r.x:F0},{r.y:F0},{r.width:F0},{r.height:F0}) -> {dw}x{dh}");
            return icon;
        }
        finally
        {
            try { RenderTexture.active = previous; } catch { }
            try { if (copy != null) UnityEngine.Object.Destroy(copy); } catch { }
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
        }
    }

    // The whole icon sheet, decoded once. Every rect in it was cropped from the game's own atlases
    // by tools/gen-reel-pool.py, at the card's native icon size (96 px).
    private static IconPixels LoadEmbeddedSheet(IHostContext log)
    {
        var asm = typeof(ReelArt).Assembly;
        byte[] png;
        using (var stream = asm.GetManifestResourceStream(ReelPool.IconSheetResource))
        {
            if (stream == null) throw new InvalidOperationException($"embedded resource '{ReelPool.IconSheetResource}' missing");
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                png = ms.ToArray();
            }
        }
        int width, height;
        var rgba = PngDecoder.DecodeRgbaBottomUp(png, out width, out height);
        if (width <= 0 || height <= 0) throw new FormatException("empty icon sheet");
        var px = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            int srcRow = height - 1 - y;                       // decoder is bottom-up; icons are top-down
            for (int x = 0; x < width; x++)
            {
                int i = (srcRow * width + x) * 4;
                px[y * width + x] = new Color32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            }
        }
        log.LogInfo($"[reel:icons] embedded sheet='{ReelPool.IconSheetResource}' png={png.Length}B {width}x{height} " +
                    $"cells={ReelPool.UsableCount()} generated={ReelPool.Generated}");
        return new IconPixels { Name = "reel_pool_icons", W = width, H = height, Px = px };
    }

    // Cut one item's rect out of the sheet (row 0 = top).
    private static IconPixels Crop(IconPixels sheet, int x, int y, int w, int h, string name)
    {
        if (sheet == null || w <= 0 || h <= 0) return null;
        if (x < 0 || y < 0 || x + w > sheet.W || y + h > sheet.H) return null;
        var px = new Color32[w * h];
        for (int row = 0; row < h; row++)
            Array.Copy(sheet.Px, (y + row) * sheet.W + x, px, row * w, w);
        return new IconPixels { Name = name ?? "icon", W = w, H = h, Px = px };
    }

    // ---- sign atlas -----------------------------------------------------------------

    internal static Texture2D BuildSignAtlas(IHostContext log)
    {
        var c = new ArtCanvas(AtlasW, AtlasH);
        c.Fill(Black);

        // Title block: the two top bands read as one plaque ('REEL OF' / 'FORTUNE'). The 3x5
        // display font at scale 32 is the largest that leaves a visible blank line between the two
        // rows and fits seven glyphs: a capsule is 5*32 = 160 atlas rows, i.e. 6.5 screen pixels at
        // the spawn camera, and its stroke is 32 atlas px = 1.3 screen px, thick enough to survive
        // GPU mip minification (the 5x7 font's 24 px stroke lands on one pixel and washes out).
        int titleScale = Mathf.Max(1, Mathf.Min((AtlasW - 12) / BitmapFont3x5.Measure(TitleLine1, 1),
                                               (RowBannerBottom - RowTitleTop - 52) / (BitmapFont3x5.GlyphH * 2)));
        int titleH = BitmapFont3x5.GlyphH * titleScale;
        int titleGap = 40;   // ~1.6 screen pixels: the two rows must not merge after minification
        int titleTop = (RowBannerBottom - RowTitleTop - (titleH * 2 + titleGap)) / 2;
        c.VGradient(0, RowTitleTop, AtlasW, RowBannerBottom - RowTitleTop, MaroonTop, MaroonBottom);
        c.Rect(0, RowTitleTop, AtlasW, 3, GoldDim);
        c.Rect(0, RowBannerBottom - 3, AtlasW, 3, GoldDim);
        BitmapFont3x5.DrawCentered(c, TitleLine1, AtlasW / 2, RowTitleTop + titleTop, titleScale, TitleGold);
        BitmapFont3x5.DrawCentered(c, TitleLine2, AtlasW / 2, RowTitleTop + titleTop + titleH + titleGap,
                                   titleScale, TitleGold);

        // Item window housing: gold rules around one wide recessed well. The live strip is a set of
        // quads in front; this well is the trim + the dark recess that surrounds them, so the ends
        // of the row sit against a shadowed border rather than on flat plate.
        int reelsH = RowReelsBottom - RowReelsTop;
        c.VGradient(0, RowReelsTop, AtlasW, reelsH, MaroonBottom, MaroonDeep);
        c.Rect(0, RowReelsTop, AtlasW, 4, GoldDim);
        c.Rect(0, RowReelsBottom - 4, AtlasW, 4, GoldDim);
        // Window geometry (see LobbyPropFactory): one row 0.72 x 0.30 m in a 0.76 m art width, so
        // 1 atlas px = 1.34 mm across and the well border is 9 atlas px = 12 mm.
        {
            int halfW = Mathf.RoundToInt(AtlasW * ScrollWellW / 0.76f * 0.5f);
            int halfH = Mathf.RoundToInt(reelsH * ScrollWellH / BandReels * 0.5f);
            int cx = AtlasW / 2, cy = RowReelsTop + reelsH / 2;
            c.FillRect(cx - halfW - 4, cy - halfH - 4, halfW * 2 + 8, halfH * 2 + 8, Black);
            c.FillRect(cx - halfW - 2, cy - halfH - 2, halfW * 2 + 4, halfH * 2 + 4, GoldDim);
            c.FillRect(cx - halfW, cy - halfH, halfW * 2, halfH * 2, MaroonDeep);
            // Inner shadow: the well is deeper than the card plane, so its top and bottom edges are
            // dark. Left/right are left clean for the end fade of the strip itself.
            c.FillRect(cx - halfW, cy - halfH, halfW * 2, 6, Black);
            c.FillRect(cx - halfW, cy + halfH - 6, halfW * 2, 6, Black);
            // Marker seats: the pointer pair is geometry that lives in the 9 mm gap between the card
            // face and the well's edge, so the only darkening the atlas needs is a small seat behind
            // each pointer, inside that gap. The old seat was an 18 px column down the FULL well,
            // i.e. a shadow across the cards under the rule - the rule no longer exists and the
            // card face must stay clean.
            int rowHalf = Mathf.RoundToInt(reelsH * ScrollWindowH / BandReels * 0.5f);
            int gap = Mathf.Max(2, halfH - rowHalf);
            c.FillRect(cx - 13, cy - halfH, 26, gap, Black, 0.55f);
            c.FillRect(cx - 13, cy + halfH - gap, 26, gap, Black, 0.55f);
        }

        // Footer: the instruction and the skin catalogue, two lines. They are decoration at 4.6 m
        // (a 6 px atlas scale is sub-pixel there) but still read in the showcase close-up.
        c.VGradient(0, RowFooterTop, AtlasW, RowFooterBottom - RowFooterTop, MaroonBottom, MaroonTop);
        c.Rect(0, RowFooterTop, AtlasW, 3, GoldDim);
        c.Rect(0, RowFooterBottom - 3, AtlasW, 3, GoldDim);
        int footerScale = Mathf.Min(FooterScale,
            Mathf.Max(1, Mathf.Min((AtlasW - 16) / BitmapFont5x7.Measure(BannerText, 1),
                                   (AtlasW - 16) / BitmapFont5x7.Measure(FooterText, 1))));
        int footerH = BitmapFont5x7.GlyphH * footerScale;
        int bannerW = BitmapFont5x7.Measure(BannerText, footerScale);
        int footerW = BitmapFont5x7.Measure(FooterText, footerScale);
        int footerGap = 14;
        int footerTop = RowFooterTop + (RowFooterBottom - RowFooterTop - (footerH * 2 + footerGap)) / 2;
        BitmapFont5x7.DrawCentered(c, BannerText, AtlasW / 2, footerTop, footerScale, Cream, Ink);
        BitmapFont5x7.DrawCentered(c, FooterText, AtlasW / 2, footerTop + footerH + footerGap, footerScale, Gold, Ink);

        var tex = Upload("AlterumPropSignAtlas", c, true, TextureWrapMode.Clamp, FilterMode.Bilinear, 4);
        log.LogInfo($"[prop:texture] procedural sign atlas {AtlasW}x{AtlasH} mips={tex.mipmapCount} " +
                    $"font=3x5(title)+5x7(footer) title='{TitleLine1} / {TitleLine2}' s={titleScale} " +
                    $"footer='{BannerText}'+'{FooterText}' s={footerScale} widths={bannerW}/{footerW}<={AtlasW - 16} " +
                    $"rows=title[{RowTitleTop},{RowTitleBottom}] banner[{RowBannerTop},{RowBannerBottom}] " +
                    $"reels[{RowReelsTop},{RowReelsBottom}] footer[{RowFooterTop},{RowFooterBottom}] " +
                    $"bytes={c.W * c.H * 4}");
        return tex;
    }

    // ---- item strip -----------------------------------------------------------------

    // One horizontal row of cards. Cards are laid left-to-right in texture u, so increasing u is
    // increasing u offset, and the face quad (built with uvMode 2, see LobbyPropRuntime.BuildQuad)
    // puts texture u=0 at the viewer's left - i.e. text and icons are not mirrored and a rising
    // offset scrolls the row leftwards, the way a case opening reads.
    internal static Texture2D BuildStrip(IHostContext log, ReelCard[] cards, IconPixels[] icons,
                                         StripCache cache, out int chainBytes)
    {
        if (cache != null) { cache.Cards.Clear(); cache.Built = 0; cache.Reused = 0; }
        // Draw through the same per-card cache a reshuffle uses, so the load-time build also warms
        // it: the first spin then only blits instead of drawing 60+ card faces mid-frame.
        var c = FromRows(RenderStripRows(cards, icons, cache, false), StripW, StripH);
        int levels;
        var chain = PngDecoder.BuildMipChain(c.ToBottomUpRgba(), c.W, c.H, out levels);
        chainBytes = chain.Length;
        var tex = Upload("AlterumPropItemStrip", c, true, TextureWrapMode.Repeat, FilterMode.Bilinear, 4, true);
        DumpCanvas(log, c, "reel_strip.png");
        log.LogInfo($"[reel] strip {StripW}x{StripH} cells={Cells} cell={CellW}x{CellH}px mips={tex.mipmapCount} " +
                    $"wrap={tex.wrapMode} filter={tex.filterMode} deck=[{ReelDeck.Census(cards)}] " +
                    $"aspect={CardAspect:F3} order(left->right)=[{Order(cards)}]");
        return tex;
    }

    // Per-card RGBA images, keyed by (type, item, tone). Drawing 64 card faces from scratch costs
    // ~1.3 M shaded pixels; after the first spin every distinct card is cached, so a reshuffle is
    // 64 row blits into the strip buffer instead.
    // Diagnostics: which card types are slow to draw (only the >15 ms ones, capped).
    internal static int ReelArtSlowLogs;
    internal static string StripSlowNote = "";
    internal sealed class StripCache
    {
        internal readonly Dictionary<int, byte[]> Cards = new Dictionary<int, byte[]>();
        internal int Built, Reused;
    }

    // Draw the row in a new order and upload it into the SAME texture: no material churn, and the
    // mips are regenerated in place. Called at every spin start so a landing position can never be
    // memorised. The stopwatch log is there to keep this path honest: it runs inside a frame.
    internal static void RewriteStrip(IHostContext log, Texture2D tex, ReelCard[] cards, IconPixels[] icons,
                                      StripCache cache, ref byte[] chainBuffer, int chainBytes)
    {
        if (tex == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int baseBytes = StripW * StripH * 4;
        var rows = RenderStripRows(cards, icons, cache, true);   // Unity raw rows are bottom-up
        long renderMs = sw.ElapsedMilliseconds;

        // The strip texture owns a full mip chain, so a re-upload has to supply the whole chain
        // buffer (the base level first). Only the base level is written; Apply(updateMipmaps: true)
        // rebuilds the rest from it.
        if (chainBuffer == null || chainBuffer.Length < chainBytes)
        {
            chainBuffer = new byte[chainBytes];
        }
        Buffer.BlockCopy(rows, 0, chainBuffer, 0, Mathf.Min(baseBytes, rows.Length));
        long copyMs = sw.ElapsedMilliseconds - renderMs;

        tex.LoadRawTextureData(new Il2CppStructArray<byte>(chainBuffer));
        long loadMs = sw.ElapsedMilliseconds - renderMs - copyMs;
        tex.Apply(true, false);   // keep the CPU copy: the next spin redraws this texture again
        long applyMs = sw.ElapsedMilliseconds - renderMs - copyMs - loadMs;
        if (log != null)
            log.LogInfo($"[reel] strip reshuffled cells={Cells} deck=[{ReelDeck.Census(cards)}] " +
                        $"cache=built{cache.Built}/reused{cache.Reused} ms=render{renderMs}/copy{copyMs}/" +
                        $"load{loadMs}/apply{applyMs}/total{sw.ElapsedMilliseconds} slow=[{StripSlowNote}] order=[{Order(cards)}]");
        StripSlowNote = "";
    }

    // The strip's raw RGBA rows for the current deck order. `bottomUp` selects Unity's raw texture
    // row order (the upload/reshuffle paths) vs the top-down order ArtCanvas authors in.
    internal static byte[] RenderStripRows(ReelCard[] cards, IconPixels[] icons, StripCache cache, bool bottomUp)
    {
        int rowBytes = CellW * 4;
        var buf = new byte[StripW * CellH * 4];
        for (int k = 0; k < Cells; k++)
        {
            var card = CardAt(cards, k);
            int key = CardKey(card);
            byte[] img;
            if (cache == null || !cache.Cards.TryGetValue(key, out img) || img == null)
            {
                var one = new ArtCanvas(CellW, CellH);
                var cardSw = System.Diagnostics.Stopwatch.StartNew();
                DrawCard(one, ToneOf(card), card, IconOf(icons, card));
                long drawMs = cardSw.ElapsedMilliseconds;
                cardSw.Restart();
                img = ToRgbaRows(one);
                long rowsMs = cardSw.ElapsedMilliseconds;
                if (drawMs + rowsMs > 15 && ReelArtSlowLogs < 6)
                {
                    ReelArtSlowLogs++;
                    StripSlowNote += $"{ReelDeck.Code(card)}:draw{drawMs}/rows{rowsMs} ";
                }
                if (cache != null) { cache.Cards[key] = img; cache.Built++; }
            }
            else if (cache != null) cache.Reused++;
            int dstCol = k * rowBytes;
            for (int y = 0; y < CellH; y++)
            {
                int dstRow = bottomUp ? (CellH - 1 - y) : y;
                Buffer.BlockCopy(img, y * rowBytes, buf, dstRow * StripW * 4 + dstCol, rowBytes);
            }
        }
        return buf;
    }

    // Cache key is the card itself, not its position: a card looks the same wherever it sits, so
    // the cache is bounded by the deck's distinct cards (~35) instead of 64 x 16 tone variants.
    // The debuff kind is part of the identity: three debuff kinds are three different faces.
    private static int CardKey(ReelCard card) =>
        ((int)card.Type << 24) ^ (card.Item & 0xFFFF) ^ ((int)card.Debuff << 16);

    // Per-card tone, so identical cards are identical and different cards do not look cloned.
    // Debuff kinds get their own tone slot (11/12/14) so a SLOW and a BLIND card never share one.
    private static int DebuffTone(ReelDebuffKind kind)
    {
        switch (kind)
        {
            case ReelDebuffKind.Fatigue: return 12;
            case ReelDebuffKind.Blind: return 14;
            default: return 11;
        }
    }

    private static int ToneOf(ReelCard card)
    {
        switch (card.Type)
        {
            case ReelCardType.Damage: return 9;
            case ReelCardType.Debuff: return DebuffTone(card.Debuff);
            case ReelCardType.Double: return 13;
            case ReelCardType.Blank: return 10;
            default: return card.Item < 0 ? 0 : 1 + (card.Item % 7);
        }
    }

    // Top-down rows (row 0 = top). Upload() flips for Unity; the strip buffer keeps this order and
    // ToBottomUp strips are produced once, in BuildStrip.
    private static byte[] ToRgbaRows(ArtCanvas c)
    {
        var o = new byte[c.W * c.H * 4];
        for (int i = 0; i < c.Px.Length; i++)
        {
            var p = c.Px[i];
            int k = i * 4;
            o[k] = p.r; o[k + 1] = p.g; o[k + 2] = p.b; o[k + 3] = p.a;
        }
        return o;
    }

    private static ArtCanvas FromRows(byte[] rows, int w, int h)
    {
        var c = new ArtCanvas(w, h);
        for (int i = 0; i < c.Px.Length; i++)
        {
            int k = i * 4;
            c.Px[i] = new Color32(rows[k], rows[k + 1], rows[k + 2], rows[k + 3]);
        }
        return c;
    }

    private static IconPixels IconOf(IconPixels[] icons, ReelCard card)
    {
        if (icons == null || card.Type != ReelCardType.Item) return null;
        if (card.Item < 0 || card.Item >= icons.Length) return null;
        return icons[card.Item];
    }

    private static string Order(ReelCard[] cards)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < Cells; k++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(k).Append(':').Append(ReelDeck.Code(CardAt(cards, k)));
        }
        return sb.ToString();
    }

    // Debug/evidence: the strip exactly as uploaded, so a still of the prop can be matched against
    // it column by column instead of being read by eye.
    private static void DumpCanvas(IHostContext log, ArtCanvas c, string fileName)
    {
        Texture2D readable = null;
        try
        {
            if (log == null || string.IsNullOrEmpty(log.ScreensDir)) return;
            readable = new Texture2D(c.W, c.H, TextureFormat.RGBA32, false, false) { name = "AlterumPropStripDump" };
            readable.LoadRawTextureData(new Il2CppStructArray<byte>(c.ToBottomUpRgba()));
            readable.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(readable);
            var bytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) bytes[i] = png[i];
            Directory.CreateDirectory(log.ScreensDir);
            string path = Path.Combine(log.ScreensDir, fileName);
            File.WriteAllBytes(path, bytes);
            log.LogInfo($"[reel:dump] {path} {c.W}x{c.H} {bytes.Length}B");
        }
        catch (Exception e) { log?.LogWarning($"[reel:dump] failed: {e.Message}"); }
        finally { try { if (readable != null) UnityEngine.Object.Destroy(readable); } catch { } }
    }

    // One card face per deck slot. The type has to be the first thing the eye gets at 0.24 m and
    // ~13 screen px: face colour, band colour and motif are all type-specific, and only the item
    // card carries real content (its icon and code).
    private static void DrawCard(ArtCanvas c, int toneIndex, ReelCard card, IconPixels icon)
    {
        int x0 = 0;
        float tone = CellTone[toneIndex % CellTone.Length];
        string code = ReelDeck.Code(card);
        string category = card.Type == ReelCardType.Item && card.Item >= 0 ? ReelPool.All[card.Item].Category : "";

        Color32 top, bottom, band, glyph;
        switch (card.Type)
        {
            case ReelCardType.Damage:
                top = DamageTop; bottom = DamageBottom; band = DamageBand; glyph = HeartRed; break;
            case ReelCardType.Debuff:
                DebuffPalette(card.Debuff, out top, out bottom, out band, out glyph); break;
            case ReelCardType.Double:
                top = DoubleTop; bottom = DoubleBottom; band = DoubleBand; glyph = Gold; break;
            case ReelCardType.Blank:
                top = BlankTop; bottom = BlankBottom; band = BlankBand; glyph = BlankBand; break;
            default:
                top = MaroonTop; bottom = MaroonBottom; band = CategoryColour(category); glyph = Cream; break;
        }

        // Gold frames mark the one type that is a jackpot and nothing else; the blank card gets a
        // dull grey frame so it does not borrow the trim that every "real" card carries.
        Color32 edge = card.Type == ReelCardType.Double ? Gold
                     : card.Type == ReelCardType.Blank ? BlankEdge
                     : GoldDim;

        c.FillRectOpaque(x0, 0, CellW, CellH, Black);
        c.VGradientOpaque(x0 + 2, 2, CellW - 4, CellH - 4, Scale(top, tone), Scale(bottom, tone));
        // Card edge: gold rule outside, ink line inside, so adjacent cards read as separate tiles.
        c.FillRectOpaque(x0 + 2, 2, CellW - 4, 2, edge);
        c.FillRectOpaque(x0 + 2, CellH - 4, CellW - 4, 2, edge);
        c.FillRectOpaque(x0 + 2, 2, 2, CellH - 4, edge);
        c.FillRectOpaque(x0 + CellW - 4, 2, 2, CellH - 4, edge);
        c.FillRectOpaque(x0 + 4, 4, CellW - 8, 1, Ink);
        c.FillRectOpaque(x0 + 4, CellH - 5, CellW - 8, 1, Ink);
        c.FillRectOpaque(x0 + 4, 4, 1, CellH - 8, Ink);
        c.FillRectOpaque(x0 + CellW - 5, 4, 1, CellH - 8, Ink);
        // Type band across the top, then the recessed motif well. A debuff card reserves 14 px at
        // the bottom of the well for its label line (the effect name in plain words), because a
        // generic purple face never told the player what it was going to do.
        c.FillRectOpaque(x0 + 6, 6, CellW - 12, 9, band);
        c.FillRectOpaque(x0 + 6, 15, CellW - 12, 1, Ink);
        bool debuff = card.Type == ReelCardType.Debuff;
        int bx = x0 + 9, by = 19, bw = CellW - 18, bh = debuff ? 76 : 92;
        c.FillRectOpaque(bx, by, bw, bh, ShadowWell);
        c.FillRectOpaque(bx, by, bw, 1, GoldDim);
        c.FillRectOpaque(bx, by + bh - 1, bw, 1, GoldDim);
        c.FillRectOpaque(bx, by, 1, bh, GoldDim);
        c.FillRectOpaque(bx + bw - 1, by, 1, bh, GoldDim);

        switch (card.Type)
        {
            case ReelCardType.Damage:
                DrawHeart(c, x0 + CellW / 2, by + 7, bw, bh - 14);
                break;
            case ReelCardType.Debuff:
                DrawDebuffMotif(c, card.Debuff, x0 + CellW / 2, by + bh / 2, glyph);
                break;
            case ReelCardType.Double:
                c.FillRect(bx + 4, by + 4, bw - 8, bh - 8, Gold, 0.16f);
                DrawTimesTwo(c, x0 + CellW / 2, by + bh / 2);
                break;
            case ReelCardType.Blank:
                // No motif on purpose: an empty recess is what "nothing" looks like. A faint
                // horizontal rule keeps the well from reading as a rendering hole.
                c.FillRect(bx + 10, by + bh / 2 - 1, bw - 20, 3, BlankEdge, 0.55f);
                break;
            default:
                if (icon != null)
                {
                    c.DrawIcon(icon, bx + 3, by + 3, bw - 6, bh - 6, Cream, 1f);
                }
                else
                {
                    // This item has no icon sprite in the game's atlases: its 3-letter code IS the
                    // face (the explicit fallback for the four items listed in ReelPool.g.cs).
                    int s = Mathf.Max(2, Mathf.Min(14, (bw - 10) / BitmapFont3x5.Measure(code, 1)));
                    int th = BitmapFont3x5.GlyphH * s;
                    int tw = BitmapFont3x5.Measure(code, s);
                    BitmapFont3x5.Draw(c, code, x0 + (CellW - tw) / 2, by + (bh - th) / 2, s, Cream);
                }
                break;
        }

        // Code plate: black, so the code holds contrast over any card tone. The motif above
        // disambiguates the five card types when two cards share a code stem (KEY1/KEY2/KEY3 aside).
        int plateY = 116;
        c.FillRectOpaque(x0 + 5, plateY, CellW - 10, CellH - plateY - 6, Ink);
        c.FillRectOpaque(x0 + 5, plateY, CellW - 10, 1, GoldDim);
        c.FillRectOpaque(x0 + 5, CellH - 7, CellW - 10, 1, GoldDim);
        // Fit the code to BOTH the plate width and its height (5 rows of pixels + padding), so the
        // glyphs are never clipped by the plate frame.
        int plateH = CellH - plateY - 6;
        int codeScale = Mathf.Max(1, Mathf.Min(Mathf.Min(7, (CellW - 16) / BitmapFont3x5.Measure(code, 1)),
                                               (plateH - 4) / BitmapFont3x5.GlyphH));
        int codeH = BitmapFont3x5.GlyphH * codeScale;
        int codeW = BitmapFont3x5.Measure(code, codeScale);
        BitmapFont3x5.Draw(c, code, x0 + (CellW - codeW) / 2, plateY + (CellH - plateY - 6 - codeH) / 2,
                           codeScale, card.Type == ReelCardType.Double ? Gold
                                     : card.Type == ReelCardType.Blank ? BlankBand : Cream);

        // Debuff label line: what the card will do, in the same bitmap font as the code, on its
        // own black strip under the motif. 10 card pixels tall = ~3 screen px at the 47 px/card
        // close-up; it is a secondary line, the big code stays the primary label.
        if (debuff)
        {
            string label = ReelDeck.Label(card);
            if (label.Length > 0)
            {
                int ly = by + bh + 3;
                c.FillRectOpaque(x0 + 9, ly, bw, 12, Ink);
                c.FillRectOpaque(x0 + 9, ly, bw, 1, GoldDim);
                int ls = 2;
                int lw = BitmapFont3x5.Measure(label, ls);
                while (ls > 1 && lw > bw - 6) { ls--; lw = BitmapFont3x5.Measure(label, ls); }
                BitmapFont3x5.Draw(c, label, x0 + (CellW - lw) / 2, ly + (12 - BitmapFont3x5.GlyphH * ls) / 2,
                                   ls, glyph);
            }
        }
    }

    // Per-kind debuff face palette. Each kind has its own band colour, gradient and glyph tint.
    private static void DebuffPalette(ReelDebuffKind kind, out Color32 top, out Color32 bottom,
                                      out Color32 band, out Color32 glyph)
    {
        switch (kind)
        {
            case ReelDebuffKind.Fatigue:
                top = FatigueTop; bottom = FatigueBottom; band = FatigueBand; glyph = FatigueGlyph; return;
            case ReelDebuffKind.Blind:
                top = BlindTop; bottom = BlindBottom; band = BlindBand; glyph = BlindGlyph; return;
            default:
                top = DebuffTop; bottom = DebuffBottom; band = DebuffBand; glyph = SpiralViolet; return;
        }
    }

    // Motif per effect: slow = spiral, fatigue = draining bolt, blind = struck-through eye.
    private static void DrawDebuffMotif(ArtCanvas c, ReelDebuffKind kind, int cx, int cy, Color32 glyph)
    {
        switch (kind)
        {
            case ReelDebuffKind.Fatigue:
                c.Disc(cx, cy, 27, glyph, 0.10f);
                for (int i = 0; i < 14; i++)
                {
                    int y = cy - 26 + i * 4;
                    int x = i < 7 ? cx - 12 + i * 3 : cx + 6 - (i - 6) * 3;
                    c.FillRect(x, y, 10, 4, glyph);
                }
                return;
            case ReelDebuffKind.Blind:
                c.Disc(cx, cy, 30, glyph, 0.10f);
                c.Disc(cx, cy, 24, glyph, 0.20f);
                c.Disc(cx, cy, 12, ShadowWell, 0.92f);
                c.Disc(cx, cy, 5, glyph, 0.95f);
                for (int i = 0; i < 30; i++)
                {
                    int x = cx - 27 + i * 2;
                    int y = cy - 27 + i * 2;
                    c.FillRect(x, y, 5, 5, ShadowWell);
                }
                return;
            default:
                c.Disc(cx, cy, 31, glyph, 0.14f);
                c.Spiral(cx, cy, 5f, 33f, 2.4f, 3, glyph);
                return;
        }
    }

    // Damage motif: a heart with a jagged crack through it (two discs + a triangle + a zigzag).
    private static void DrawHeart(ArtCanvas c, int cx, int top, int w, int h)
    {
        int r = Mathf.Max(7, Mathf.Min(w, h) / 5);
        int cy = top + r + 1;
        c.Disc(cx - r * 3 / 4, cy, r, HeartRed);
        c.Disc(cx + r * 3 / 4, cy, r, HeartRed);
        c.TriFill(cx - r * 7 / 4, cy + r / 2, cx + r * 7 / 4, cy + r / 2, cx, top + h - 2, HeartRed);
        // Highlight so the motif reads as a shape, not a blob, after mip minification.
        c.Disc(cx - r * 3 / 4 - r / 3, cy - r / 3, Mathf.Max(2, r / 3), Cream, 0.45f);
        int y0 = cy - r / 2 + 2, y1 = top + h - 6;
        int px = cx, py = y0;
        const int steps = 4;
        for (int i = 1; i <= steps; i++)
        {
            int nx = cx + ((i % 2 == 0) ? -1 : 1) * Mathf.Max(2, r / 6);
            int ny = y0 + (y1 - y0) * i / steps;
            c.Line(px, py, nx, ny, 3, Ink, 0.95f);
            px = nx; py = ny;
        }
    }

    // Double motif: a procedural x plus a big '2'. The multiplication sign is drawn as two crossing
    // bars so the card never depends on the glyph table carrying a U+00D7.
    private static void DrawTimesTwo(ArtCanvas c, int cx, int cy)
    {
        const int arm = 19, thick = 7, gap = 24;
        c.Line(cx - arm - gap, cy - arm, cx + arm - gap, cy + arm, thick, TitleGold);
        c.Line(cx - arm - gap, cy + arm, cx + arm - gap, cy - arm, thick, TitleGold);
        const int s = 12;
        int w = BitmapFont3x5.Measure("2", s);
        int h = BitmapFont3x5.GlyphH * s;
        BitmapFont3x5.Draw(c, "2", cx + 6 - w / 2, cy - h / 2, s, TitleGold);
    }

    private static Color32 Scale(Color32 c, float k) => Rgba(
        Mathf.Clamp(Mathf.RoundToInt(c.r * k), 0, 255),
        Mathf.Clamp(Mathf.RoundToInt(c.g * k), 0, 255),
        Mathf.Clamp(Mathf.RoundToInt(c.b * k), 0, 255), c.a);

    // ---- window fade ----------------------------------------------------------------

    // 256x8 black strip whose alpha is the end fade: opaque at u=0 and u=1, clear in the middle.
    // Drawn over the cards by a transparent URP/Unlit material, so the row emerges from darkness at
    // the left and vanishes into it at the right with a smooth gradient (a slice-tinted row can
    // only approximate this in visible steps).
    // ---- idle preview plate ------------------------------------------------------------
    // The idle face. It is drawn from the deck the booth will actually settle on (ReelDeck.
    // TypeCounts) and from items the live map can spawn, and it is a *preview*: three cards side
    // by side on the bare plate. No strip offset, no marker, no landed card - the caller removes
    // it on every accepted spin and puts it back after the result presentation, so it can never be
    // read as an outcome.
    //
    // The plate carries NO text: a disclaimer line ('ITEM PREVIEW - NOT A RESULT') and the deck's
    // odds used to be printed under the cards, and both are gone by design - the cards alone say
    // "preview", and they are enlarged to fill the plate top to bottom so no empty band is left
    // behind where that text used to sit. The odds and the census are still computed and logged
    // (PreviewOdds / ReelDeck.Census in the [reel:preview] lines): the composition of the composed
    // deck has to stay visible in the log, just not on the machine.
    internal const int PreviewW = 400, PreviewH = 170;

    // The odds as one line, derived from the composed deck: exactly the sentence the brief asks
    // for, and the same counts the strip was built from (ReelDeck.Census logs the same numbers).
    internal static string PreviewOdds(ReelCard[] deck)
    {
        if (deck == null) return "0: no deck";
        var n = ReelDeck.TypeCounts(deck);
        int cells = deck != null ? deck.Length : 0;
        return $"{cells}: blank {n[(int)ReelCardType.Blank]} \u00B7 item {n[(int)ReelCardType.Item]} \u00B7 " +
               $"damage {n[(int)ReelCardType.Damage]} \u00B7 debuff {n[(int)ReelCardType.Debuff]} \u00B7 " +
               $"double {n[(int)ReelCardType.Double]}";
    }

    // One plate for the item window while the machine is idle: three item cards and nothing else.
    // `preview` holds the three Item cards the caller picked (never a damage/debuff/blank card, and
    // never a card that is under the marker - the marker is not drawn). Plate size (400 x 170) and
    // the quad the caller hangs this on are unchanged; the cards are enlarged and centred so they
    // fill the plate instead of leaving the bare band the removed text used to occupy.
    internal static Texture2D BuildPreviewPlate(IHostContext log, ReelCard[] deck, ReelCard[] preview,
                                                IconPixels[] icons, out string note)
    {
        const int W = PreviewW, H = PreviewH;
        // Enlarged card box. 3 x 120 + 2 x 10 = 380, so the side margins are (400 - 380) / 2 = 10 px
        // and the card bounds are [10,130) [140,260) [270,390) - centres 70 / 200 / 330. Vertically
        // (170 - 142) / 2 = 14 px top and bottom, so the cards occupy rows [14,156) and the plate has
        // no band left for text. The faces are authored 128 x 160 (aspect 4/5) and the previous
        // preview box was 88 x 104 (aspect 11/13) - they were never equal, so the box already
        // stretched the face slightly and this box does so no more than it did. At width 120 the
        // height that keeps the *old box* ratio is 120 * 13/11 = 141.818, rounded to 142 because
        // DrawScaled takes integers. Relative to the old box that is a 0.128 % vertical stretch,
        // invisible at this size; relative to the source face nothing changed. The whole face is
        // still sampled - the cards are enlarged, never cropped. Side gap to the trim is
        // 10 - 2 = 8 px, top/bottom 14 - 2 = 12 px.
        const int cardW = 120, cardH = 142, gap = 10;
        var c = new ArtCanvas(W, H);
        c.Fill(MaroonDeep);
        // Plate trim: a dim gold rule inside the window so the plate reads as part of the machine
        // rather than a hole in it. Deliberately not the gold JACKPOT frame of an X2 card.
        c.FillRectOpaque(0, 0, W, 2, GoldDim);
        c.FillRectOpaque(0, H - 2, W, 2, GoldDim);
        c.FillRectOpaque(0, 0, 2, H, GoldDim);
        c.FillRectOpaque(W - 2, 0, 2, H, GoldDim);

        int count = preview != null ? preview.Length : 0;
        var labels = new StringBuilder();
        if (count > 0)
        {
            int total = count * cardW + (count - 1) * gap;
            int x = (W - total) / 2;          // count-centred; 3 cards -> x = 10, then +130 each
            int y = (H - cardH) / 2;          // 14: equal top and bottom margin
            for (int i = 0; i < count; i++)
            {
                var card = preview[i];
                // Full-size card face, then sampled down: the faces are authored for 128 x 160 and
                // the preview keeps that artwork instead of a second, differently-drawn card.
                var one = new ArtCanvas(CellW, CellH);
                DrawCard(one, ToneOf(card), card, IconOf(icons, card));
                c.DrawScaled(one, x, y, cardW, cardH);
                if (labels.Length > 0) labels.Append(',');
                labels.Append(ReelDeck.Code(card)).Append("=").Append(ReelDeck.IdOf(card));
                x += cardW + gap;
            }
        }

        // No text pass here on purpose: the disclaimer label and the two odds lines are gone. The
        // odds stay a diagnostic only - same numbers, same source (ReelDeck.TypeCounts).
        DumpCanvas(log, c, "reel_preview.png");
        note = $"cards=[{labels}] text=none odds='{PreviewOdds(deck)}' " +
               $"deck=[{ReelDeck.Census(deck)}] ";
        var tex = Upload("AlterumPropReelPreview", c, false, TextureWrapMode.Clamp, FilterMode.Bilinear, 1);
        log?.LogInfo($"[reel:preview] plate {W}x{H} px {note}");
        return tex;
    }

    internal static Texture2D BuildFadeRamp(IHostContext log)
    {
        const int N = 256, H = 8;
        var c = new ArtCanvas(N, H);
        c.Fill(Rgba(0, 0, 0, 255));
        for (int x = 0; x < N; x++)
        {
            float s = (x + 0.5f) / N;
            float e = Mathf.Clamp01(Mathf.Min(s, 1f - s) / FadeEdge);
            float keep = Mathf.Pow(e, 1.45f);          // 0 at the edges, 1 inside the plateau
            int a = Mathf.RoundToInt((1f - keep) * 255f);
            for (int y = 0; y < H; y++) c.Px[y * N + x] = Rgba(0, 0, 0, a);
        }
        var tex = Upload("AlterumPropFadeRamp", c, false, TextureWrapMode.Clamp, FilterMode.Bilinear, 1);
        log.LogInfo($"[prop:texture] window fade ramp {N}x{H} edge={FadeEdge:F2} alpha[end]={c.Px[0].a} " +
                    $"alpha[centre]={c.Px[(H / 2) * N + N / 2].a} alpha[quarter]={c.Px[(H / 2) * N + N / 4].a}");
        return tex;
    }

    // ---- upload ---------------------------------------------------------------------

    // `keepReadable` is for the card strip only: it is re-drawn on every spin (RewriteStrip), and a
    // texture whose CPU copy was released by Apply(..., makeNoLongerReadable: true) cannot take a
    // second LoadRawTextureData. Costs one 5 MB buffer for the 8192x160 strip.
    private static Texture2D Upload(string name, ArtCanvas c, bool mips, TextureWrapMode wrap, FilterMode filter,
                                    int aniso, bool keepReadable = false)
    {
        var rgba = c.ToBottomUpRgba();
        int levels;
        var chain = mips ? PngDecoder.BuildMipChain(rgba, c.W, c.H, out levels) : rgba;
        if (!mips) levels = 1;
        var tex = new Texture2D(c.W, c.H, TextureFormat.RGBA32, mips, false) { name = name };
        tex.LoadRawTextureData(new Il2CppStructArray<byte>(chain));
        tex.Apply(mips, !keepReadable);
        tex.wrapMode = wrap;
        tex.filterMode = filter;
        tex.anisoLevel = aniso;
        return tex;
    }
}

// Hand-written 5x7 bitmap font. Glyph rows are authored top-down in this file; the atlas builder
// flips the whole canvas once at upload. '1' is ink, '0' is background, '|' is a row separator.
// Covers A-Z, 0-9 and the punctuation the sign needs; unknown characters draw a hollow box.
internal static class BitmapFont5x7
{
    internal const int GlyphW = 5, GlyphH = 7;

    private static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        ['A'] = "01110|10001|10001|11111|10001|10001|10001",
        ['B'] = "11110|10001|10001|11110|10001|10001|11110",
        ['C'] = "01110|10001|10000|10000|10000|10001|01110",
        ['D'] = "11110|10001|10001|10001|10001|10001|11110",
        ['E'] = "11111|10000|10000|11110|10000|10000|11111",
        ['F'] = "11111|10000|10000|11110|10000|10000|10000",
        ['G'] = "01110|10001|10000|10111|10001|10001|01111",
        ['H'] = "10001|10001|10001|11111|10001|10001|10001",
        ['I'] = "11111|00100|00100|00100|00100|00100|11111",
        ['J'] = "00111|00010|00010|00010|00010|10010|01100",
        ['K'] = "10001|10010|10100|11000|10100|10010|10001",
        ['L'] = "10000|10000|10000|10000|10000|10000|11111",
        ['M'] = "10001|11011|10101|10101|10001|10001|10001",
        ['N'] = "10001|11001|10101|10011|10001|10001|10001",
        ['O'] = "01110|10001|10001|10001|10001|10001|01110",
        ['P'] = "11110|10001|10001|11110|10000|10000|10000",
        ['Q'] = "01110|10001|10001|10001|10101|10010|01101",
        ['R'] = "11110|10001|10001|11110|10100|10010|10001",
        ['S'] = "01111|10000|10000|01110|00001|00001|11110",
        ['T'] = "11111|00100|00100|00100|00100|00100|00100",
        ['U'] = "10001|10001|10001|10001|10001|10001|01110",
        ['V'] = "10001|10001|10001|10001|10001|01010|00100",
        ['W'] = "10001|10001|10001|10101|10101|11011|10001",
        ['X'] = "10001|10001|01010|00100|01010|10001|10001",
        ['Y'] = "10001|10001|01010|00100|00100|00100|00100",
        ['Z'] = "11111|00001|00010|00100|01000|10000|11111",
        ['0'] = "01110|10001|10011|10101|11001|10001|01110",
        ['1'] = "00100|01100|00100|00100|00100|00100|01110",
        ['2'] = "01110|10001|00001|00010|00100|01000|11111",
        ['3'] = "11111|00010|00100|00010|00001|10001|01110",
        ['4'] = "00010|00110|01010|10010|11111|00010|00010",
        ['5'] = "11111|10000|11110|00001|00001|10001|01110",
        ['6'] = "00110|01000|10000|11110|10001|10001|01110",
        ['7'] = "11111|00001|00010|00100|01000|01000|01000",
        ['8'] = "01110|10001|10001|01110|10001|10001|01110",
        ['9'] = "01110|10001|10001|01111|00001|00010|01100",
        [' '] = "00000|00000|00000|00000|00000|00000|00000",
        ['.'] = "00000|00000|00000|00000|00000|01100|01100",
        [','] = "00000|00000|00000|00000|00110|00110|01100",
        [':'] = "00000|01100|01100|00000|01100|01100|00000",
        [';'] = "00000|01100|01100|00000|01100|01100|01000",
        ['-'] = "00000|00000|00000|11111|00000|00000|00000",
        ['_'] = "00000|00000|00000|00000|00000|00000|11111",
        ['!'] = "00100|00100|00100|00100|00100|00000|00100",
        ['?'] = "01110|10001|00001|00010|00100|00000|00100",
        ['\''] = "00100|00100|00000|00000|00000|00000|00000",
        ['"'] = "01010|01010|00000|00000|00000|00000|00000",
        ['/'] = "00001|00010|00010|00100|01000|01000|10000",
        ['('] = "00010|00100|01000|01000|01000|00100|00010",
        [')'] = "01000|00100|00010|00010|00010|00100|01000",
        ['['] = "01110|01000|01000|01000|01000|01000|01110",
        [']'] = "01110|00010|00010|00010|00010|00010|01110",
        ['+'] = "00000|00100|00100|11111|00100|00100|00000",
        ['='] = "00000|00000|11111|00000|11111|00000|00000",
        ['*'] = "00000|00100|10101|01110|10101|00100|00000",
        ['#'] = "01010|01010|11111|01010|11111|01010|01010",
        ['%'] = "11001|11010|00010|00100|01000|01011|10011",
        ['&'] = "01100|10010|10100|01000|10101|10010|01101",
        ['<'] = "00010|00100|01000|10000|01000|00100|00010",
        ['>'] = "01000|00100|00010|00001|00010|00100|01000",
        // The two non-ASCII glyphs the preview face used to need: a full-width dash for the em dash
        // in its label and a centred dot for the odds separators. The plate draws no text any more,
        // but the strings that use them are still built (PreviewOdds, the log lines), and these stay
        // real glyphs rather than boxes so nothing that does draw them can regress to hollow squares.
        ['\u2014'] = "00000|00000|00000|11111|00000|00000|00000",
        ['\u00B7'] = "00000|00000|00000|01100|01100|00000|00000",
    };

    internal static int GlyphCount => Glyphs.Count;
    internal static int Advance(int scale) => (GlyphW + 1) * scale;
    internal static int Measure(string text, int scale) =>
        string.IsNullOrEmpty(text) ? 0 : Mathf.Max(0, text.Length * Advance(scale) - scale);

    internal static string Rows(char ch)
    {
        if (Glyphs.TryGetValue(char.ToUpperInvariant(ch), out var g)) return g;
        return "11110|10001|10101|10101|10101|10001|11110";   // hollow box for anything missing
    }

    internal static bool Ink(char ch, int col, int row)
    {
        var g = Rows(ch);
        int i = row * 6 + col;
        return i >= 0 && i < g.Length && g[i] == '1';
    }

    internal static void Draw(ArtCanvas c, string text, int x, int topY, int scale, Color32 fg, Color32 halo)
        => DrawCore(c, text, x, topY, scale, fg, halo, false);

    internal static void DrawCentered(ArtCanvas c, string text, int centerX, int topY, int scale, Color32 fg, Color32 halo)
        => DrawCore(c, text, centerX - Measure(text, scale) / 2, topY, scale, fg, halo, true);

    private static void DrawCore(ArtCanvas c, string text, int x0, int topY, int scale, Color32 fg, Color32 halo, bool centered)
    {
        if (string.IsNullOrEmpty(text) || scale <= 0) return;
        // Halo pass: a 1 px dilated copy in the dark ink colour, so gold type keeps its edge on
        // maroon at 640x360.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                int gx = x0 + i * Advance(scale);
                for (int row = 0; row < GlyphH; row++)
                {
                    for (int col = 0; col < GlyphW; col++)
                    {
                        if (!Ink(ch, col, row)) continue;
                        int px = gx + col * scale, py = topY + row * scale;
                        if (pass == 0)
                            c.Rect(px - 1, py - 1, scale + 2, scale + 2, halo, 0.92f);
                        else
                            c.Rect(px, py, scale, scale, fg, 1f);
                    }
                }
            }
        }
    }
}

// Compact 3x5 pixel font for the item codes. The code plate is ~4 screen px tall from spawn, and
// a 5-column glyph cannot hold a one-pixel stroke there: 3 columns with one-pixel gaps can (see
// ReelArt.DrawCard). No halo pass: a halo would close the single-pixel gaps at this size.
internal static class BitmapFont3x5
{
    internal const int GlyphW = 3, GlyphH = 5;

    private static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        ['A'] = "010|101|111|101|101",
        ['B'] = "110|101|110|101|110",
        ['C'] = "011|100|100|100|011",
        ['D'] = "110|101|101|101|110",
        ['E'] = "111|100|110|100|111",
        ['F'] = "111|100|110|100|100",
        ['G'] = "011|100|101|101|011",
        ['H'] = "101|101|111|101|101",
        ['I'] = "111|010|010|010|111",
        ['J'] = "001|001|001|101|010",
        ['K'] = "101|101|110|101|101",
        ['L'] = "100|100|100|100|111",
        ['M'] = "101|111|111|101|101",
        ['N'] = "110|101|101|101|101",
        ['O'] = "010|101|101|101|010",
        ['P'] = "110|101|110|100|100",
        ['Q'] = "010|101|101|110|011",
        ['R'] = "110|101|110|101|101",
        ['S'] = "011|100|010|001|110",
        ['T'] = "111|010|010|010|010",
        ['U'] = "101|101|101|101|111",
        ['V'] = "101|101|101|101|010",
        ['W'] = "101|101|101|111|101",
        ['X'] = "101|101|010|101|101",
        ['Y'] = "101|101|010|010|010",
        ['Z'] = "111|001|010|100|111",
        ['0'] = "111|101|101|101|111",
        ['1'] = "010|110|010|010|111",
        ['2'] = "111|001|111|100|111",
        ['3'] = "111|001|011|001|111",
        ['4'] = "101|101|111|001|001",
        ['5'] = "111|100|111|001|111",
        ['6'] = "111|100|111|101|111",
        [' '] = "000|000|000|000|000",
        ['-'] = "000|000|111|000|000",
    };

    internal static int Advance(int scale) => (GlyphW + 1) * scale;
    internal static int Measure(string text, int scale) =>
        string.IsNullOrEmpty(text) ? 0 : Mathf.Max(0, text.Length * Advance(scale) - scale);

    internal static string Rows(char ch)
        => Glyphs.TryGetValue(char.ToUpperInvariant(ch), out var g) ? g : "111|101|101|101|111";

    internal static void Draw(ArtCanvas c, string text, int x, int topY, int scale, Color32 fg)
    {
        if (string.IsNullOrEmpty(text) || scale <= 0) return;
        for (int i = 0; i < text.Length; i++)
        {
            var rows = Rows(text[i]).Split('|');
            int gx = x + i * Advance(scale);
            for (int row = 0; row < GlyphH; row++)
                for (int col = 0; col < GlyphW; col++)
                    if (rows[row][col] == '1')
                        c.Rect(gx + col * scale, topY + row * scale, scale, scale, fg, 1f);
        }
    }

    internal static void DrawCentered(ArtCanvas c, string text, int centerX, int topY, int scale, Color32 fg)
        => Draw(c, text, centerX - Measure(text, scale) / 2, topY, scale, fg);
}

// Small RGBA canvas, drawn top-down. Blending is straight alpha (no premultiply), which is what
// the icon sheets store.
internal sealed class ArtCanvas
{
    internal readonly int W, H;
    internal readonly Color32[] Px;

    internal ArtCanvas(int width, int height)
    {
        W = width; H = height;
        Px = new Color32[width * height];
    }

    internal void Fill(Color32 c)
    {
        for (int i = 0; i < Px.Length; i++) Px[i] = c;
    }

    internal void VLine(int x, int y0, int y1, Color32 c, float a)
    {
        if (x < 0 || x >= W) return;
        int from = Mathf.Clamp(Mathf.Min(y0, y1), 0, H), to = Mathf.Clamp(Mathf.Max(y0, y1), 0, H);
        for (int y = from; y < to; y++) Blend(x, y, c, a);
    }

    internal void HLine(int x0, int x1, int y, Color32 c, float a)
    {
        if (y < 0 || y >= H) return;
        int from = Mathf.Clamp(Mathf.Min(x0, x1), 0, W), to = Mathf.Clamp(Mathf.Max(x0, x1), 0, W);
        for (int x = from; x < to; x++) Blend(x, y, c, a);
    }

    internal void Rect(int x, int y, int w, int h, Color32 c, float a = 1f) => FillRect(x, y, w, h, c, a);

    internal void FillRect(int x, int y, int w, int h, Color32 c, float a = 1f)
    {
        if (w <= 0 || h <= 0 || a <= 0f) return;
        int x0 = Mathf.Max(0, x), y0 = Mathf.Max(0, y);
        int x1 = Mathf.Min(W, x + w), y1 = Mathf.Min(H, y + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
                Blend(xx, yy, c, a);
    }

    internal void FrameRect(int x, int y, int w, int h, int t, Color32 c, float a = 1f)
    {
        if (w <= 0 || h <= 0 || t <= 0) return;
        FillRect(x, y, w, t, c, a);
        FillRect(x, y + h - t, w, t, c, a);
        FillRect(x, y, t, h, c, a);
        FillRect(x + w - t, y, t, h, c, a);
    }

    // Filled circle. The card motifs (heart lobes, spiral centre, glows) are built from these.
    internal void Disc(int cx, int cy, int r, Color32 c, float a = 1f)
    {
        if (r <= 0) return;
        int r2 = r * r;
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r2) Blend(cx + x, cy + y, c, a);
    }

    // Filled triangle by scanline (any winding): the heart's point.
    internal void TriFill(int x0, int y0, int x1, int y1, int x2, int y2, Color32 c, float a = 1f)
    {
        int ymin = Mathf.Min(y0, Mathf.Min(y1, y2)), ymax = Mathf.Max(y0, Mathf.Max(y1, y2));
        for (int y = ymin; y <= ymax; y++)
        {
            float sy = y + 0.5f, lo = float.MaxValue, hi = float.MinValue;
            EdgeX(x0, y0, x1, y1);
            EdgeX(x1, y1, x2, y2);
            EdgeX(x2, y2, x0, y0);
            if (lo > hi) continue;
            HLine(Mathf.RoundToInt(lo), Mathf.RoundToInt(hi) + 1, y, c, a);

            void EdgeX(int ax, int ay, int bx, int by)
            {
                if (ay == by)
                {
                    if (Mathf.Abs(sy - (ay + 0.5f)) > 0.5f) return;
                    lo = Mathf.Min(lo, ax); hi = Mathf.Max(hi, bx);
                    return;
                }
                float t = (sy - ay) / (by - ay);
                if (t < 0f || t > 1f) return;
                float x = ax + t * (bx - ax);
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
        }
    }

    // Square-brush line; at card size (2-7 px) a square brush is indistinguishable from round caps.
    internal void Line(int x0, int y0, int x1, int y1, int thickness, Color32 c, float a = 1f)
    {
        if (thickness <= 0) return;
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
        int half = thickness / 2;
        if (steps == 0)
        {
            FillRect(x0 - half, y0 - half, thickness, thickness, c, a);
            return;
        }
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
            FillRect(x - half, y - half, thickness, thickness, c, a);
        }
    }

    // Archimedean spiral from r0 to r1 over `turns` rotations: the debuff (slow) motif.
    internal void Spiral(int cx, int cy, float r0, float r1, float turns, int thickness, Color32 c, float a = 1f)
    {
        int steps = Mathf.Max(24, Mathf.RoundToInt(turns * 40f));
        float px = cx + r0, py = cy;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ang = turns * 2f * Mathf.PI * t;
            float r = Mathf.Lerp(r0, r1, t);
            float x = cx + Mathf.Cos(ang) * r;
            float y = cy + Mathf.Sin(ang) * r;
            Line(Mathf.RoundToInt(px), Mathf.RoundToInt(py), Mathf.RoundToInt(x), Mathf.RoundToInt(y), thickness, c, a);
            px = x; py = y;
        }
    }

    internal void VGradient(int x, int y, int w, int h, Color32 top, Color32 bottom)
    {
        if (w <= 0 || h <= 0) return;
        for (int yy = 0; yy < h; yy++)
        {
            float t = h <= 1 ? 0f : yy / (float)(h - 1);
            var c = new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(top.r, bottom.r, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(top.g, bottom.g, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(top.b, bottom.b, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(top.a, bottom.a, t)));
            FillRect(x, y + yy, w, 1, c);
        }
    }

    // Opaque source pixels overwrite the destination: no blending maths, no Mathf calls. Every
    // card face is drawn on an opaque background, so this is the path the backgrounds use - and it
    // matters, because the whole strip is re-drawn on the CPU whenever the deck is reshuffled.
    internal void FillRectOpaque(int x, int y, int w, int h, Color32 c)
    {
        int x0 = x < 0 ? 0 : x, y0 = y < 0 ? 0 : y;
        int x1 = x + w > W ? W : x + w, y1 = y + h > H ? H : y + h;
        if (x1 <= x0 || y1 <= y0) return;
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W;
            for (int xx = x0; xx < x1; xx++) Px[row + xx] = c;
        }
    }

    internal void VGradientOpaque(int x, int y, int w, int h, Color32 top, Color32 bottom)
    {
        if (w <= 0 || h <= 0) return;
        for (int yy = 0; yy < h; yy++)
        {
            float t = h <= 1 ? 0f : yy / (float)(h - 1);
            var c = new Color32(
                (byte)(top.r + (bottom.r - top.r) * t),
                (byte)(top.g + (bottom.g - top.g) * t),
                (byte)(top.b + (bottom.b - top.b) * t),
                255);
            FillRectOpaque(x, y + yy, w, 1, c);
        }
    }

    internal void Blend(int x, int y, Color32 c, float a)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        float sa = a * (c.a * (1f / 255f));
        if (sa <= 0f) return;
        if (sa > 1f) sa = 1f;
        int i = y * W + x;
        if (sa >= 0.999f) { Px[i] = new Color32(c.r, c.g, c.b, 255); return; }
        var d = Px[i];
        float da = d.a * (1f / 255f);
        float oa = sa + da * (1f - sa);
        if (oa <= 1e-5f) { Px[i] = new Color32(0, 0, 0, 0); return; }
        float inv = da * (1f - sa);
        float ia = 1f / oa;
        int r = (int)((c.r * sa + d.r * inv) * ia + 0.5f);
        int g = (int)((c.g * sa + d.g * inv) * ia + 0.5f);
        int b = (int)((c.b * sa + d.b * inv) * ia + 0.5f);
        int al = (int)(oa * 255f + 0.5f);
        Px[i] = new Color32((byte)(r > 255 ? 255 : r), (byte)(g > 255 ? 255 : g),
                            (byte)(b > 255 ? 255 : b), (byte)(al > 255 ? 255 : al));
    }

    // Scale an icon into the given box (aspect preserved, centred), tinted and alpha-composited.
    // The source is sampled premultiplied so transparent surroundings never darken the edges. The
    // symbol is cropped to its alpha bounding box first: the item-icon sheets store each symbol in
    // a full square with wide transparent margins, and without the crop the visible symbol is about
    // half the requested box - at the reel windows' 12 screen px that reads as a speck.
    internal void DrawIcon(ReelArt.IconPixels icon, int dx, int dy, int dw, int dh, Color32 tint, float a)
    {
        if (icon == null || icon.Px == null || icon.W <= 0 || icon.H <= 0 || dw <= 0 || dh <= 0) return;
        int x0 = icon.W, y0 = icon.H, x1 = -1, y1 = -1;
        for (int y = 0; y < icon.H; y++)
            for (int x = 0; x < icon.W; x++)
                if (icon.Px[y * icon.W + x].a > 8)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < x0 || y1 < y0) return;
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        float scale = Mathf.Min(dw / (float)cw, dh / (float)ch);
        int w = Mathf.Max(1, Mathf.RoundToInt(cw * scale));
        int h = Mathf.Max(1, Mathf.RoundToInt(ch * scale));
        int ox = dx + (dw - w) / 2, oy = dy + (dh - h) / 2;
        for (int y = 0; y < h; y++)
        {
            float v = y0 + (y + 0.5f) / h * (ch - 1);
            for (int x = 0; x < w; x++)
            {
                float u = x0 + (x + 0.5f) / w * (cw - 1);
                var s = SamplePremultiplied(icon, u, v);   // s.a is already 0..1
                float sa = s.a * a;
                if (sa <= 0.002f) continue;
                // s.r/g/b are 0..1; tint channels are 0..255.
                var col = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(s.r * tint.r), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(s.g * tint.g), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(s.b * tint.b), 0, 255),
                    255);
                Blend(ox + x, oy + y, col, sa);
            }
        }
    }

    // Nearest-sample blit of a whole canvas into this one. Used by the idle preview to put a
    // full-size card face (128 x 160, the strip's own artwork) into a smaller plate without a
    // second card renderer. The source faces are opaque, so no alpha maths is needed.
    internal void DrawScaled(ArtCanvas src, int dx, int dy, int dw, int dh)
    {
        if (src == null || src.W <= 0 || src.H <= 0 || dw <= 0 || dh <= 0) return;
        for (int y = 0; y < dh; y++)
        {
            int sy = Mathf.Clamp((int)((y + 0.5f) / dh * src.H), 0, src.H - 1);
            for (int x = 0; x < dw; x++)
            {
                int sx = Mathf.Clamp((int)((x + 0.5f) / dw * src.W), 0, src.W - 1);
                int dxi = dx + x, dyi = dy + y;
                if ((uint)dxi >= (uint)W || (uint)dyi >= (uint)H) continue;
                Px[dyi * W + dxi] = src.Px[sy * src.W + sx];
            }
        }
    }

    // Bilinear tap of a straight-alpha icon, returned premultiplied (r,g,b in 0..1). Inlined on
    // purpose: this runs once per output pixel of every icon on the strip, and a local function
    // there allocates a closure per call.
    private static Color SamplePremultiplied(ReelArt.IconPixels icon, float u, float v)
    {
        int x0 = (int)u; if (x0 < 0) x0 = 0; else if (x0 > icon.W - 1) x0 = icon.W - 1;
        int x1 = x0 + 1 < icon.W ? x0 + 1 : icon.W - 1;
        int y0 = (int)v; if (y0 < 0) y0 = 0; else if (y0 > icon.H - 1) y0 = icon.H - 1;
        int y1 = y0 + 1 < icon.H ? y0 + 1 : icon.H - 1;
        float fx = u - x0, fy = v - y0;
        float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy);
        float w01 = (1f - fx) * fy, w11 = fx * fy;

        var p = icon.Px[y0 * icon.W + x0];
        float a00 = p.a * (1f / 255f) * w00;
        float r = p.r * (1f / 255f) * a00, g = p.g * (1f / 255f) * a00, b = p.b * (1f / 255f) * a00;
        p = icon.Px[y0 * icon.W + x1];
        float a10 = p.a * (1f / 255f) * w10;
        r += p.r * (1f / 255f) * a10; g += p.g * (1f / 255f) * a10; b += p.b * (1f / 255f) * a10;
        p = icon.Px[y1 * icon.W + x0];
        float a01 = p.a * (1f / 255f) * w01;
        r += p.r * (1f / 255f) * a01; g += p.g * (1f / 255f) * a01; b += p.b * (1f / 255f) * a01;
        p = icon.Px[y1 * icon.W + x1];
        float a11 = p.a * (1f / 255f) * w11;
        r += p.r * (1f / 255f) * a11; g += p.g * (1f / 255f) * a11; b += p.b * (1f / 255f) * a11;

        float a = a00 + a10 + a01 + a11;
        if (a <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        return new Color(r / a, g / a, b / a, a);
    }

    // Unity's raw texture rows are bottom-up; the canvas authors top-down.
    internal byte[] ToBottomUpRgba()
    {
        var o = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            int dy = H - 1 - y;
            int src = y * W;
            int dst = dy * W;
            for (int x = 0; x < W; x++)
            {
                var p = Px[src + x];
                int i = (dst + x) * 4;
                o[i] = p.r; o[i + 1] = p.g; o[i + 2] = p.b; o[i + 3] = p.a;
            }
        }
        return o;
    }
}
