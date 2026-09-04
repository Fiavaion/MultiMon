namespace MultiMon.Hap.Snappy;

/// <summary>
/// Minimal, vendored Snappy block-format DECOMPRESSOR — the second-stage codec HAP chunks use
/// (HAP spec compressor 0x0B). Decompress-only by design: MultiMon never encodes HAP. Implements the
/// Snappy format directly (varint length preamble + literal/copy elements) so there is no third-party
/// HAP/Snappy dependency (REBUILD_ARCHITECTURE §1). Decodes into a caller-owned buffer so the per-frame
/// texture bytes can come from an <c>ArrayPool</c> instead of a fresh LOH array per frame.
/// </summary>
public static class SnappyDecoder
{
    /// <summary>Convenience: decompresses one block into a fresh array (tests / one-off use).</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new byte[DecodedLength(input)];
        Decompress(input, output);
        return output;
    }

    /// <summary>
    /// The block's declared decoded length (its varint preamble), validated against the decoded-size
    /// ceiling. Throws <see cref="InvalidDataException"/> on a truncated or implausible preamble.
    /// </summary>
    public static int DecodedLength(ReadOnlySpan<byte> input)
    {
        var pos = 0;
        var outputLength = ReadVarint(input, ref pos);
        // Untrusted preamble: a crafted varint can claim a huge (or, via int overflow, negative) length;
        // cap the allocation before it OOMs (the real blast radius of a hostile .mov frame chunk).
        if (outputLength < 0 || outputLength > HapLimits.MaxDecodedBytes)
            throw new InvalidDataException($"Snappy: implausible decoded length ({outputLength} bytes).");
        return outputLength;
    }

    /// <summary>
    /// Decompresses one block into <paramref name="output"/> and returns the byte count written (the
    /// declared length). Throws <see cref="InvalidDataException"/> on malformed input or when the declared
    /// length exceeds <paramref name="output"/>.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var pos = 0;
        var outputLength = ReadVarint(input, ref pos);
        if (outputLength < 0 || outputLength > HapLimits.MaxDecodedBytes)
            throw new InvalidDataException($"Snappy: implausible decoded length ({outputLength} bytes).");
        if (outputLength > output.Length)
            throw new InvalidDataException($"Snappy: decoded length {outputLength} exceeds the {output.Length}-byte destination.");
        output = output[..outputLength];
        var outPos = 0;

        while (pos < input.Length)
        {
            var tag = input[pos++];
            switch (tag & 0x03)
            {
                case 0: // literal
                {
                    var length = tag >> 2;
                    if (length >= 60)
                    {
                        // 60..63: the next (length-59) bytes hold (actualLength-1), little-endian.
                        var byteCount = length - 59;
                        length = (int)ReadLittleEndian(input, ref pos, byteCount);
                    }
                    length += 1;
                    if (length < 0 || pos + length > input.Length || outPos + length > output.Length)
                        throw new InvalidDataException("Snappy: literal run exceeds buffer.");
                    input.Slice(pos, length).CopyTo(output[outPos..]);
                    pos += length;
                    outPos += length;
                    break;
                }
                case 1: // copy, 1-byte offset
                {
                    var length = 4 + ((tag >> 2) & 0x07);
                    if (pos >= input.Length)
                        throw new InvalidDataException("Snappy: truncated copy element.");
                    var offset = ((tag >> 5) << 8) | input[pos++];
                    outPos = CopyMatch(output, outPos, offset, length);
                    break;
                }
                case 2: // copy, 2-byte offset
                {
                    var length = (tag >> 2) + 1;
                    var offset = (int)ReadLittleEndian(input, ref pos, 2);
                    outPos = CopyMatch(output, outPos, offset, length);
                    break;
                }
                default: // 3: copy, 4-byte offset
                {
                    var length = (tag >> 2) + 1;
                    var offset = (int)ReadLittleEndian(input, ref pos, 4);
                    outPos = CopyMatch(output, outPos, offset, length);
                    break;
                }
            }
        }

        if (outPos != output.Length)
            throw new InvalidDataException($"Snappy: decoded {outPos} bytes, expected {output.Length}.");
        return outputLength;
    }

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="offset"/> back, byte-by-byte to allow overlap (run-length).</summary>
    private static int CopyMatch(Span<byte> output, int outPos, int offset, int length)
    {
        if (offset <= 0 || offset > outPos)
            throw new InvalidDataException("Snappy: copy offset out of range.");
        if (outPos + length > output.Length)
            throw new InvalidDataException("Snappy: copy run exceeds buffer.");
        var src = outPos - offset;
        for (var i = 0; i < length; i++)
            output[outPos + i] = output[src + i];
        return outPos + length;
    }

    private static int ReadVarint(ReadOnlySpan<byte> input, ref int pos)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= input.Length)
                throw new InvalidDataException("Snappy: truncated varint.");
            var b = input[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 28)
                throw new InvalidDataException("Snappy: varint too long.");
        }
    }

    private static uint ReadLittleEndian(ReadOnlySpan<byte> input, ref int pos, int byteCount)
    {
        if (pos + byteCount > input.Length)
            throw new InvalidDataException("Snappy: truncated little-endian field.");
        uint value = 0;
        for (var i = 0; i < byteCount; i++)
            value |= (uint)input[pos + i] << (8 * i);
        pos += byteCount;
        return value;
    }
}
