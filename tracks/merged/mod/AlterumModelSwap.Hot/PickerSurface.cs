using System;
using System.Collections.Generic;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The pickers draw into a 640x360 straight-alpha RGBA buffer. That one buffer is
//   * uploaded to a Texture2D and blitted 1:1 by OnGUI (stretched to the real screen), and
//   * alpha-composited over a game-camera RenderTexture when a capture is requested,
// so the live overlay and the screenshot are the same pixels. No Canvas, no font asset, no
// depending on whatever IMGUI skin the game happens to have.
internal sealed class PickerSurface : IDisposable
{
    internal const int Width = 640;
    internal const int Height = 360;

    // Straight RGBA, top-down (row 0 is the top of the screen).
    private readonly byte[] _px = new byte[Width * Height * 4];
    // Unity's raw texture upload is bottom-up; composed once per dirty rebuild.
    private readonly byte[] _flipped = new byte[Width * Height * 4];
    private Texture2D _texture;
    private bool _uploaded;

    internal void Clear()
    {
        Array.Clear(_px, 0, _px.Length);
        _uploaded = false;
    }

    internal void Invalidate() => _uploaded = false;

    // True when the texture holds the current pixel buffer. Read by the overlay's render probe:
    // "drew a texture" and "uploaded what it built" are separate failures.
    internal bool Uploaded => _uploaded && _texture != null;

    // ------------------------------------------------------------------ pixels

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private void Blend(int index, Color32 c)
    {
        if (c.a == 0) return;
        if (c.a == 255)
        {
            _px[index] = c.r; _px[index + 1] = c.g; _px[index + 2] = c.b; _px[index + 3] = 255;
            return;
        }
        int da = _px[index + 3];
        int sa = c.a;
        int oa = sa + da * (255 - sa) / 255;
        if (oa <= 0) { _px[index] = _px[index + 1] = _px[index + 2] = _px[index + 3] = 0; return; }
        int dr = _px[index], dg = _px[index + 1], db = _px[index + 2];
        _px[index]     = (byte)((c.r * sa * 255 + dr * da * (255 - sa)) / (255 * oa));
        _px[index + 1] = (byte)((c.g * sa * 255 + dg * da * (255 - sa)) / (255 * oa));
        _px[index + 2] = (byte)((c.b * sa * 255 + db * da * (255 - sa)) / (255 * oa));
        _px[index + 3] = (byte)oa;
    }

    internal void Set(int x, int y, Color32 c)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        Blend((y * Width + x) * 4, c);
    }

    internal void Fill(int x, int y, int w, int h, Color32 c)
    {
        if (c.a == 0 || w <= 0 || h <= 0) return;
        int x0 = Clamp(x, 0, Width), x1 = Clamp(x + w, 0, Width);
        int y0 = Clamp(y, 0, Height), y1 = Clamp(y + h, 0, Height);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
                Blend((yy * Width + xx) * 4, c);
    }

    internal void Frame(int x, int y, int w, int h, Color32 c, int t = 1)
    {
        Fill(x, y, w, t, c);
        Fill(x, y + h - t, w, t, c);
        Fill(x, y, t, h, c);
        Fill(x + w - t, y, t, h, c);
    }

    internal void HLine(int x, int y, int w, Color32 c) => Fill(x, y, w, 1, c);
    internal void VLine(int x, int y, int h, Color32 c) => Fill(x, y, 1, h, c);

    // A dimmed full screen: the live game remains visible behind the panel.
    internal void Dim(Color32 c) => Fill(0, 0, Width, Height, c);

    // ------------------------------------------------------------------ text

    internal static int Measure(string text, int scale)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = text.Length * PickerFont.Advance * scale;
        return Mathf.Max(0, w - scale);     // no trailing inter-glyph gap
    }

    // Draws one line, clipped. Returns the advance in pixels.
    internal int Text(int x, int y, string text, int scale, Color32 c)
    {
        if (string.IsNullOrEmpty(text) || scale <= 0) return 0;
        scale = Mathf.Clamp(scale, 1, 4);
        int pen = x;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (pen >= Width) break;
            if (ch == ' ')
            {
                pen += PickerFont.Advance * scale;
                continue;
            }
            if (pen + PickerFont.GlyphWidth * scale > 0)
            {
                for (int row = 0; row < PickerFont.GlyphHeight; row++)
                {
                    for (int col = 0; col < PickerFont.GlyphWidth; col++)
                    {
                        if (!PickerFont.Pixel(ch, col, row)) continue;
                        int px = pen + col * scale;
                        int py = y + row * scale;
                        if (px + scale <= 0 || py + scale <= 0) continue;
                        Fill(px, py, scale, scale, c);
                    }
                }
            }
            pen += PickerFont.Advance * scale;
        }
        return pen - x;
    }

    internal int TextRight(int right, int y, string text, int scale, Color32 c)
    {
        int w = Measure(text, scale);
        return Text(right - w, y, text, scale, c);
    }

    internal int TextCenter(int centerX, int y, string text, int scale, Color32 c)
    {
        int w = Measure(text, scale);
        return Text(centerX - w / 2, y, text, scale, c);
    }

    internal static int LineHeight(int scale) => PickerFont.GlyphHeight * scale + 2 * scale;

    private static string Ellipsize(string text, int maxWidth, int scale)
    {
        if (Measure(text, scale) <= maxWidth) return text;
        string ell = "...";
        int keep = text.Length;
        while (keep > 0 && Measure(text.Substring(0, keep).TrimEnd() + ell, scale) > maxWidth) keep--;
        return keep <= 0 ? ell : text.Substring(0, keep).TrimEnd() + ell;
    }

    // Wraps on spaces; hard-breaks over-long words; ellipsizes after maxLines. Returns the number
    // of lines drawn.
    internal int Wrap(int x, int y, string text, int maxWidth, int scale, int maxLines, Color32 c)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0) return 0;
        var lines = new List<string>();
        var words = text.Split(' ');
        string line = "";
        foreach (var raw in words)
        {
            string word = raw;
            while (Measure(word, scale) > maxWidth && word.Length > 1)
            {
                // Hard-break a word that cannot fit on a line of its own.
                int cut = word.Length;
                while (cut > 1 && Measure(word.Substring(0, cut), scale) > maxWidth) cut--;
                if (line.Length > 0) { lines.Add(line); line = ""; }
                lines.Add(word.Substring(0, cut));
                word = word.Substring(cut);
            }
            string test = line.Length == 0 ? word : line + " " + word;
            if (Measure(test, scale) <= maxWidth) line = test;
            else
            {
                if (line.Length > 0) lines.Add(line);
                line = word;
            }
        }
        if (line.Length > 0) lines.Add(line);

        bool truncated = lines.Count > maxLines;
        int n = Mathf.Min(lines.Count, maxLines);
        for (int i = 0; i < n; i++)
        {
            string s = lines[i];
            if (i == maxLines - 1 && truncated) s = Ellipsize(s + " ...", maxWidth, scale);
            else s = Ellipsize(s, maxWidth, scale);
            Text(x, y + i * LineHeight(scale), s, scale, c);
        }
        return n;
    }

    internal static string EllipsizeLine(string text, int maxWidth, int scale) => Ellipsize(text, maxWidth, scale);

    // ------------------------------------------------------------------ bitmap blit

    // `rgba` is top-down RGBA, e.g. a cosmetic preview. Alpha-blended, clipped.
    internal void Blit(byte[] rgba, int w, int h, int dx, int dy)
    {
        if (rgba == null || w <= 0 || h <= 0) return;
        for (int y = 0; y < h; y++)
        {
            int ty = dy + y;
            if ((uint)ty >= Height) continue;
            for (int x = 0; x < w; x++)
            {
                int tx = dx + x;
                if ((uint)tx >= Width) continue;
                int o = (y * w + x) * 4;
                int a = rgba[o + 3];
                if (a == 0) continue;
                Set(tx, ty, new Color32(rgba[o], rgba[o + 1], rgba[o + 2], (byte)a));
            }
        }
    }

    // ------------------------------------------------------------------ upload / draw

    private void Upload()
    {
        for (int y = 0; y < Height; y++)
        {
            int src = y * Width * 4;
            int dst = (Height - 1 - y) * Width * 4;
            Buffer.BlockCopy(_px, src, _flipped, dst, Width * 4);
        }
        if (_texture == null)
        {
            _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = "AlterumPickerSurface",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
        }
        _texture.LoadRawTextureData(_flipped);
        _texture.Apply(false, false);
        _uploaded = true;
    }

    // One stretched blit per frame. Nothing is drawn (and no IMGUI event is touched) while the
    // picker is closed, because the caller does not call this.
    internal void Draw()
    {
        if (!_uploaded) Upload();
        var previousDepth = GUI.depth;
        var previousColor = GUI.color;
        try
        {
            GUI.depth = -100;          // above the mirror overlay buttons
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _texture, ScaleMode.StretchToFill, true);
        }
        finally
        {
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }
    }

    // Alpha-composite this surface over a bottom-up camera image. Returns an RGBA buffer ready for
    // a Texture2D.LoadRawTextureData (bottom-up) and EncodeToPNG.
    internal byte[] CompositeOver(Color32[] screenBottomUp, int screenWidth, int screenHeight)
    {
        var outPx = new byte[screenWidth * screenHeight * 4];
        for (int sy = 0; sy < screenHeight; sy++)
        {
            // screen row sy counts from the bottom; overlay row 0 is the top.
            int oy = screenHeight - 1 - sy;
            if ((uint)oy >= Height) continue;
            for (int sx = 0; sx < screenWidth; sx++)
            {
                int ox = screenWidth == Width ? sx : sx * Width / screenWidth;
                int si = sy * screenWidth + sx;
                int oi = (oy * Width + ox) * 4;
                int di = si * 4;
                int a = _px[oi + 3];
                Color32 dst = screenBottomUp[si];
                int r, g, b;
                if (a <= 0) { r = dst.r; g = dst.g; b = dst.b; }
                else if (a >= 255) { r = _px[oi]; g = _px[oi + 1]; b = _px[oi + 2]; }
                else
                {
                    int ia = 255 - a;
                    r = (_px[oi] * a + dst.r * ia) / 255;
                    g = (_px[oi + 1] * a + dst.g * ia) / 255;
                    b = (_px[oi + 2] * a + dst.b * ia) / 255;
                }
                outPx[di] = (byte)r; outPx[di + 1] = (byte)g; outPx[di + 2] = (byte)b; outPx[di + 3] = 255;
            }
        }
        return outPx;
    }

    public void Dispose()
    {
        try { if (_texture != null) UnityEngine.Object.Destroy(_texture); } catch { }
        _texture = null;
        _uploaded = false;
    }
}
