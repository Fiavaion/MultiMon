using MultiMon.Hap;

namespace MultiMon.Stress.Mac;

/// <summary>
/// CPU reference for the <c>--source-check</c> HAP check: decodes single BC1 / BC3 texels from a decoded HAP
/// frame's compressed bytes and bilinearly filters them exactly as the pass's linear-clamp sampler does, then
/// (for HapQ) applies the same scaled-YCoCg→RGB math as <c>sample_ycocg_main</c> — filter first, convert after,
/// which is what the shader does. GPU BCn decoders may round the 1/3–2/3 endpoint blends differently, so the
/// caller compares within a small tolerance. BC4/BC7 have no reference here (the check degrades to
/// non-clear + non-uniform for those).
/// </summary>
internal static class BcnReference
{
    public static bool Supports(HapTextureFormat format) =>
        format is HapTextureFormat.RgbDxt1 or HapTextureFormat.RgbaDxt5 or HapTextureFormat.YCoCgDxt5;

    /// <summary>
    /// The colour the pass produces at normalised source coordinate (<paramref name="u"/>, <paramref name="v"/>)
    /// on a <paramref name="width"/>×<paramref name="height"/> (block-aligned) texture holding
    /// <paramref name="data"/> in <paramref name="format"/>.
    /// </summary>
    public static (byte R, byte G, byte B) Sample(ReadOnlySpan<byte> data, HapTextureFormat format, int width, int height, float u, float v)
    {
        // Linear filtering: texel centres sit at integer+0.5, so the sample point in texel space is uv*size-0.5.
        var x = u * width - 0.5f;
        var y = v * height - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var t00 = Texel(data, format, width, Math.Clamp(x0, 0, width - 1), Math.Clamp(y0, 0, height - 1));
        var t10 = Texel(data, format, width, Math.Clamp(x0 + 1, 0, width - 1), Math.Clamp(y0, 0, height - 1));
        var t01 = Texel(data, format, width, Math.Clamp(x0, 0, width - 1), Math.Clamp(y0 + 1, 0, height - 1));
        var t11 = Texel(data, format, width, Math.Clamp(x0 + 1, 0, width - 1), Math.Clamp(y0 + 1, 0, height - 1));
        var s = new float[4];
        for (var c = 0; c < 4; c++)
            s[c] = ((t00[c] * (1 - fx) + t10[c] * fx) * (1 - fy) + (t01[c] * (1 - fx) + t11[c] * fx) * fy) / 255f;

        float r, g, b;
        if (format == HapTextureFormat.YCoCgDxt5)
        {
            // sample_ycocg_main: Co in R, Cg in G, scale in B, Y in A.
            var co = s[0] - 0.50196078431373f;
            var cg = s[1] - 0.50196078431373f;
            var scale = s[2] * (255f / 8f) + 1f;
            co /= scale;
            cg /= scale;
            var yy = s[3];
            (r, g, b) = (yy + co - cg, yy + cg, yy - co - cg);
        }
        else
            (r, g, b) = (s[0], s[1], s[2]);
        return (ToByte(r), ToByte(g), ToByte(b));
    }

    /// <summary>Decoded luma below which a texel is too dark to discriminate anything (clamping hides chroma errors).</summary>
    private const int ChromaLumaFloor = 64;

    /// <summary>
    /// The two texels the check compares, found on a <paramref name="step"/>-texel grid of decoded colours:
    /// <c>Chromatic</c> = the texel with the highest <see cref="ChromaSensitivity"/> among texels with luma above
    /// <see cref="ChromaLumaFloor"/> — the one where a swapped chroma channel, a flipped sign or a dropped scale
    /// divide moves the RGB the most, whereas a bright neutral texel decodes to white under every one of those
    /// faults; <c>Brightest</c> = max r+g+b, kept as the second comparison (a clip black at its centre, e.g. a
    /// timecode burn-in, still gets a real luma comparison). Chromatic falls back to Brightest when nothing clears the floor.
    /// </summary>
    public static ((int X, int Y) Chromatic, (int X, int Y) Brightest) FindReferenceTexels(ReadOnlySpan<byte> data, HapTextureFormat format, int width, int height, int step)
    {
        var brightest = (X: 0, Y: 0);
        var bestSum = -1;
        (int X, int Y)? chromatic = null;
        var bestChroma = -1;
        for (var y = 0; y < height; y += step)
            for (var x = 0; x < width; x += step)
            {
                var (r, g, b) = Sample(data, format, width, height, (x + 0.5f) / width, (y + 0.5f) / height);
                var sum = r + g + b;
                if (sum > bestSum)
                {
                    bestSum = sum;
                    brightest = (x, y);
                }
                var luma = (r + 2 * g + b) / 4;
                var chroma = ChromaSensitivity((r, g, b));
                if (luma > ChromaLumaFloor && chroma > bestChroma)
                {
                    bestChroma = chroma;
                    chromatic = (x, y);
                }
            }
        return (chromatic ?? brightest, brightest);
    }

    /// <summary>How visible a chroma fault is at an RGB colour: the SMALLEST of |Co|, |Cg| and |Co−Cg|
    /// (Co=(R−B)/2, Cg=(2G−R−B)/4). A flipped Co or Cg sign moves the colour by ~2|Co| / ~2|Cg|, a swapped Co/Cg by
    /// ~|Co−Cg|, a dropped scale divide by the whole chroma — so the minimum is the fault the texel hides best.
    /// 0 for grey and for colours with Co == Cg (where a swap is invisible; |Co|+|Cg| alone would pick those).</summary>
    public static int ChromaSensitivity((byte R, byte G, byte B) c)
    {
        var co = (c.R - c.B) / 2;
        var cg = (2 * c.G - c.R - c.B) / 4;
        return Math.Min(Math.Min(Math.Abs(co), Math.Abs(cg)), Math.Abs(co - cg));
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    /// <summary>One texel as (r, g, b, a) 0..255 from its 4×4 block.</summary>
    private static int[] Texel(ReadOnlySpan<byte> data, HapTextureFormat format, int width, int x, int y)
    {
        var blockBytes = format == HapTextureFormat.RgbDxt1 ? 8 : 16;
        var block = data.Slice(((y / 4) * (width / 4) + x / 4) * blockBytes, blockBytes);
        var index = (y % 4) * 4 + x % 4;
        var alpha = 255;
        if (blockBytes == 16)
        {
            alpha = Bc3Alpha(block[..8], index);
            block = block[8..];
        }
        // Colour block: two RGB565 endpoints + 2-bit indices. BC3's colour half is always 4-colour mode; BC1
        // switches to 3-colour + transparent black when c0 <= c1.
        var c0 = (ushort)(block[0] | (block[1] << 8));
        var c1 = (ushort)(block[2] | (block[3] << 8));
        var indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));
        var e0 = Expand565(c0);
        var e1 = Expand565(c1);
        var selector = (int)((indices >> (2 * index)) & 3);
        var fourColour = blockBytes == 16 || c0 > c1;
        int[] rgb = selector switch
        {
            0 => e0,
            1 => e1,
            2 => fourColour ? Blend(e0, e1, 2, 1) : Blend(e0, e1, 1, 1),
            _ => fourColour ? Blend(e0, e1, 1, 2) : [0, 0, 0]
        };
        if (selector == 3 && !fourColour) alpha = 0;
        return [rgb[0], rgb[1], rgb[2], alpha];
    }

    private static int[] Expand565(ushort c)
    {
        var r = (c >> 11) & 0x1F;
        var g = (c >> 5) & 0x3F;
        var b = c & 0x1F;
        return [(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)];
    }

    private static int[] Blend(int[] a, int[] b, int wa, int wb) =>
        [(a[0] * wa + b[0] * wb) / (wa + wb), (a[1] * wa + b[1] * wb) / (wa + wb), (a[2] * wa + b[2] * wb) / (wa + wb)];

    /// <summary>BC3 alpha block: two 8-bit endpoints + 16 three-bit indices (8- or 6-step interpolation).</summary>
    private static int Bc3Alpha(ReadOnlySpan<byte> block, int index)
    {
        int a0 = block[0], a1 = block[1];
        ulong bits = 0;
        for (var i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);
        var code = (int)((bits >> (3 * index)) & 7);
        if (code == 0) return a0;
        if (code == 1) return a1;
        if (a0 > a1)
            return ((8 - code) * a0 + (code - 1) * a1) / 7;
        return code switch
        {
            6 => 0,
            7 => 255,
            _ => ((6 - code) * a0 + (code - 1) * a1) / 5
        };
    }
}
