namespace ResumeRelay.Live;

/// <summary>Reads an image's width and height from its header (JPEG, PNG, WebP), without decoding it.</summary>
public static class ImageInfo
{
    public static (int W, int H) Size(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[Math.Min(f.Length, 512 * 1024)];
            int n = f.Read(buf, 0, buf.Length);
            return Size(buf.AsSpan(0, n));
        }
        catch { return (0, 0); }
    }

    public static (int W, int H) Size(ReadOnlySpan<byte> b)
    {
        // PNG: IHDR right after the signature.
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return (BE32(b, 16), BE32(b, 20));
        // WebP: RIFF....WEBP then VP8 / VP8L / VP8X.
        if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
                return ((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF);
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
            {
                int bits = b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24;
                return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
            }
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        }
        // JPEG: walk the segments to the first start-of-frame.
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte m = b[i + 1];
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                int len = b[i + 2] << 8 | b[i + 3];
                if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                    return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
                i += 2 + len;
            }
        }
        return (0, 0);
    }

    /// <summary>Whether a PNG or WebP image has see-through parts (an alpha channel, or PNG transparency).</summary>
    public static bool HasAlpha(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[Math.Min(f.Length, 1024 * 1024)];
            int n = f.Read(buf, 0, buf.Length);
            return HasAlpha(buf.AsSpan(0, n));
        }
        catch { return false; }
    }

    public static bool HasAlpha(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 33 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            byte colorType = b[25];
            if (colorType is 4 or 6) return true;           // grey + alpha, RGBA
            // A palette or plain image with a tRNS chunk (before the image data).
            for (int i = 8; i + 8 <= b.Length;)
            {
                int len = BE32(b, i);
                if (b[i + 4] == 't' && b[i + 5] == 'R' && b[i + 6] == 'N' && b[i + 7] == 'S') return true;
                if (b[i + 4] == 'I' && b[i + 5] == 'D' && b[i + 6] == 'A' && b[i + 7] == 'T') return false;
                if (len < 0) return false;
                i += 12 + len;
            }
            return false;
        }
        if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X') return (b[20] & 0x10) != 0;
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
                return ((b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24) >> 28 & 1) == 1;
        }
        return false;
    }

    public static string Extension(ReadOnlySpan<byte> b) =>
        b.Length > 3 && b[0] == 0x89 && b[1] == 'P' ? ".png"
        : b.Length > 11 && b[0] == 'R' && b[8] == 'W' ? ".webp"
        : ".jpg";

    public static bool IsImage(ReadOnlySpan<byte> b) => Size(b).W > 0;

    private static int BE32(ReadOnlySpan<byte> b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
}
