using System;
using System.IO;
using System.IO.Compression;

namespace AlterumModelSwap.Hot;

// Minimal PNG reader for the extracted cosmetic textures.
//
// ImageConversion.LoadImage is unusable here: its Il2CppStructArray overload wraps the data in an
// Il2CppSystem.ReadOnlySpan and the shipped interop span type has no GetPinnableReference, so the
// call throws MissingMethodException at runtime. Decoding here and uploading through
// Texture2D.LoadRawTextureData + Apply(true, true) avoids that path and gives us a full mip chain.
//
// Output is RGBA8 in Unity's raw texture row order (row 0 = bottom), i.e. vertically flipped
// relative to the PNG's top-down rows.
internal static class PngDecoder
{
    internal static byte[] DecodeRgbaBottomUp(byte[] png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 33) throw new FormatException("png too small");
        var signature = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < signature.Length; i++)
            if (png[i] != signature[i]) throw new FormatException("not a png");

        int pos = 8;
        int bitDepth = 0, colorType = -1, interlace = 0;
        byte[] idat = null;
        int idatLength = 0;
        while (pos + 8 <= png.Length)
        {
            int length = ReadInt32BE(png, pos);
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int data = pos + 8;
            if (length < 0 || data + length + 4 > png.Length) throw new FormatException("truncated chunk");
            if (type == "IHDR")
            {
                width = ReadInt32BE(png, data);
                height = ReadInt32BE(png, data + 4);
                bitDepth = png[data + 8];
                colorType = png[data + 9];
                interlace = png[data + 12];
            }
            else if (type == "IDAT")
            {
                if (idat == null) idat = new byte[length];
                else if (idatLength + length > idat.Length) Array.Resize(ref idat, idatLength + length);
                Buffer.BlockCopy(png, data, idat, idatLength, length);
                idatLength += length;
            }
            else if (type == "IEND") break;
            pos = data + length + 4;
        }
        if (width <= 0 || height <= 0) throw new FormatException("bad dimensions");
        if (idat == null || idatLength < 6) throw new FormatException("no image data");
        if (bitDepth != 8) throw new FormatException($"unsupported bit depth {bitDepth}");
        if (interlace != 0) throw new FormatException("interlaced png");
        int channels = colorType switch
        {
            6 => 4, // RGBA
            2 => 3, // RGB
            4 => 2, // gray + alpha
            0 => 1, // gray
            _ => throw new FormatException($"unsupported colour type {colorType}"),
        };
        if ((long)width * height * channels > 256L * 1024 * 1024) throw new FormatException("png too large");

        int stride = width * channels;
        var raw = new byte[(long)(stride + 1) * height];
        // Skip the 2-byte zlib header; DeflateStream consumes the raw deflate payload.
        using (var compressed = new MemoryStream(idat, 2, idatLength - 2 - 4))
        using (var deflate = new DeflateStream(compressed, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = deflate.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != raw.Length) throw new FormatException($"inflated {read} of {raw.Length} bytes");
        }

        var outBytes = new byte[(long)width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[offset++];
            Buffer.BlockCopy(raw, offset, current, 0, stride);
            offset += stride;
            Unfilter(filter, current, previous, channels, stride);
            // Unity raw texture rows are bottom-up.
            int targetRow = height - 1 - y;
            int o = targetRow * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                switch (channels)
                {
                    case 4:
                        outBytes[o] = current[s];
                        outBytes[o + 1] = current[s + 1];
                        outBytes[o + 2] = current[s + 2];
                        outBytes[o + 3] = current[s + 3];
                        break;
                    case 3:
                        outBytes[o] = current[s];
                        outBytes[o + 1] = current[s + 1];
                        outBytes[o + 2] = current[s + 2];
                        outBytes[o + 3] = 255;
                        break;
                    case 2:
                        outBytes[o] = outBytes[o + 1] = outBytes[o + 2] = current[s];
                        outBytes[o + 3] = current[s + 1];
                        break;
                    default:
                        outBytes[o] = outBytes[o + 1] = outBytes[o + 2] = current[s];
                        outBytes[o + 3] = 255;
                        break;
                }
                o += 4;
            }
            var swap = previous;
            previous = current;
            current = swap;
        }
        return outBytes;
    }

    // LoadRawTextureData wants the whole mip chain when the texture was created with mipmaps.
    internal static byte[] BuildMipChain(byte[] mip0, int width, int height, out int levels)
    {
        int levelCount = 1;
        for (int w = width, h = height; w > 1 || h > 1; levelCount++)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        levels = levelCount;
        long total = 0;
        for (int i = 0, w = width, h = height; i < levelCount; i++)
        {
            total += (long)w * h * 4;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        if (total > int.MaxValue) throw new FormatException("mip chain too large");
        var chain = new byte[total];
        Buffer.BlockCopy(mip0, 0, chain, 0, width * height * 4);
        int offset = width * height * 4;
        int previousWidth = width, previousHeight = height;
        int previousOffset = 0;
        for (int level = 1; level < levelCount; level++)
        {
            int w = Math.Max(1, previousWidth >> 1), h = Math.Max(1, previousHeight >> 1);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(previousWidth - 1, x * 2), x1 = Math.Min(previousWidth - 1, x * 2 + 1);
                    int y0 = Math.Min(previousHeight - 1, y * 2), y1 = Math.Min(previousHeight - 1, y * 2 + 1);
                    int a = previousOffset + (y0 * previousWidth + x0) * 4;
                    int b = previousOffset + (y0 * previousWidth + x1) * 4;
                    int c = previousOffset + (y1 * previousWidth + x0) * 4;
                    int d = previousOffset + (y1 * previousWidth + x1) * 4;
                    for (int ch = 0; ch < 4; ch++)
                        chain[offset++] = (byte)((chain[a + ch] + chain[b + ch] + chain[c + ch] + chain[d + ch] + 2) >> 2);
                }
            }
            previousOffset += previousWidth * previousHeight * 4;
            previousWidth = w;
            previousHeight = h;
        }
        return chain;
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int channels, int stride)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1: // Sub
                for (int i = channels; i < stride; i++) row[i] = (byte)(row[i] + row[i - channels]);
                return;
            case 2: // Up
                for (int i = 0; i < stride; i++) row[i] = (byte)(row[i] + previous[i]);
                return;
            case 3: // Average
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= channels ? row[i - channels] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }
                return;
            case 4: // Paeth
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= channels ? row[i - channels] : 0;
                    int upLeft = i >= channels ? previous[i - channels] : 0;
                    row[i] = (byte)(row[i] + Paeth(left, previous[i], upLeft));
                }
                return;
            default:
                throw new FormatException($"bad png filter {filter}");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static int ReadInt32BE(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}
