namespace MultiMon.Decode.Hap.Snappy;

/// <summary>
/// Minimal, vendored Snappy block-format DECOMPRESSOR — the second-stage codec HAP chunks use
/// (HAP spec compressor 0x0B). Decompress-only by design: MultiMon never encodes HAP. Implements the
/// Snappy format directly (varint length preamble + literal/copy elements) so there is no third-party
/// HAP/Snappy dependency (REBUILD_ARCHITECTURE §1).
/// </summary>
public static class SnappyDecoder
{
    /// <summary>Decompresses one Snappy block. Throws <see cref="InvalidDataException"/> on malformed input.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var pos = 0;
        var outputLength = ReadVarint(input, ref pos);
        // Untrusted preamble: a crafted varint can claim a huge (or, via int overflow, negative) length;
        // cap the allocation before it OOMs (the real blast radius of a hostile .mov frame chunk).
        if (outputLength < 0 || outputLength > HapLimits.MaxDecodedBytes)
            throw new InvalidDataException($"Snappy: implausible decoded length ({outputLength} bytes).");
        var output = new byte[outputLength];
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
                    if (pos + length > input.Length || outPos + length > output.Length)
                        throw new InvalidDataException("Snappy: literal run exceeds buffer.");
                    input.Slice(pos, length).CopyTo(output.AsSpan(outPos));
                    pos += length;
                    outPos += length;
                    break;
                }
                case 1: // copy, 1-byte offset
                {
                    var length = 4 + ((tag >> 2) & 0x07);
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
        return output;
    }

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="offset"/> back, byte-by-byte to allow overlap (run-length).</summary>
    private static int CopyMatch(byte[] output, int outPos, int offset, int length)
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
