using MultiMon.Decode.Hap.Snappy;

namespace MultiMon.Decode.Hap;

/// <summary>
/// Decodes ONE HAP frame (a single MOV sample's bytes) into its raw BCn texture payload, from scratch
/// per the HAP spec — no third-party dependency. Handles the three second-stage compressors: None,
/// Snappy, and Complex (a Decode-Instructions Container describing N Snappy/raw chunks that concatenate
/// into the texture). Pure + allocation-bounded; the GPU upload lives in HapSource. The output is exactly
/// the compressed-texture bytes a BCn D3D11 texture expects, written into a caller-owned destination so
/// the per-frame buffer can be pooled (no fresh LOH array per frame).
///
/// Single-section frames only: HapM / Hap Q Alpha frames carry a multi-section top-level container
/// (a colour texture plus an alpha texture) and are rejected by <see cref="MovHapDemuxer"/> up front.
/// </summary>
public static class HapFrameDecoder
{
    // Second-stage compressor constants (HAP spec) — the high nibble of the top section type byte,
    // and the raw per-chunk value in the Decode-Instructions compressor table.
    private const byte CompressorNone = 0x0A;
    private const byte CompressorSnappy = 0x0B;
    private const byte CompressorComplex = 0x0C;

    // Structural section types inside a Complex frame's Decode-Instructions Container.
    private const byte SectionDecodeInstructions = 0x01;
    private const byte SectionChunkCompressorTable = 0x02;
    private const byte SectionChunkSizeTable = 0x03;
    private const byte SectionChunkOffsetTable = 0x04;

    public readonly record struct Result(HapTextureFormat Format, byte[] Data);

    /// <summary>One chunk of a Complex frame: its compressor and byte range within the chunk region.</summary>
    private readonly record struct Chunk(byte Compressor, int Offset, int Size);

    /// <summary>Convenience: decodes the frame into a fresh array (tests / one-off use).</summary>
    public static Result Decode(ReadOnlySpan<byte> frame)
    {
        var data = new byte[DecodedLength(frame)];
        Decode(frame, data, out var format);
        return new Result(format, data);
    }

    /// <summary>The frame's decoded texture size in bytes, without decoding it. Throws <see cref="InvalidDataException"/> on a malformed frame.</summary>
    public static int DecodedLength(ReadOnlySpan<byte> frame)
    {
        var (payloadOffset, payloadSize, type) = ReadSectionHeader(frame, 0);
        var payload = frame.Slice(payloadOffset, payloadSize);
        switch ((byte)((type >> 4) & 0x0F))
        {
            case CompressorNone:
                return payloadSize;
            case CompressorSnappy:
                return SnappyDecoder.DecodedLength(payload);
            case CompressorComplex:
            {
                var chunks = ParseComplex(payload, out var chunkRegion);
                long total = 0;
                foreach (var chunk in chunks)
                {
                    var bytes = chunkRegion.Slice(chunk.Offset, chunk.Size);
                    total += chunk.Compressor == CompressorSnappy ? SnappyDecoder.DecodedLength(bytes) : bytes.Length;
                }
                if (total > HapLimits.MaxDecodedBytes)
                    throw new InvalidDataException($"HAP complex: implausible decoded size ({total} bytes).");
                return (int)total;
            }
            default:
                throw new InvalidDataException($"HAP: unknown second-stage compressor 0x{(type >> 4) & 0x0F:X}.");
        }
    }

    /// <summary>
    /// Decodes the frame into <paramref name="destination"/> and returns the byte count written. Throws
    /// <see cref="InvalidDataException"/> on a malformed frame or when the decoded texture would exceed
    /// <paramref name="destination"/> (the caller sizes it from the clip's declared dimensions).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> frame, Span<byte> destination, out HapTextureFormat format)
    {
        var (payloadOffset, payloadSize, type) = ReadSectionHeader(frame, 0);
        format = (HapTextureFormat)(type & 0x0F);
        var compressor = (byte)((type >> 4) & 0x0F);
        var payload = frame.Slice(payloadOffset, payloadSize);

        switch (compressor)
        {
            case CompressorNone:
                return CopyRaw(payload, destination);
            case CompressorSnappy:
                return SnappyDecoder.Decompress(payload, destination);
            case CompressorComplex:
            {
                var chunks = ParseComplex(payload, out var chunkRegion);
                var written = 0;
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var bytes = chunkRegion.Slice(chunk.Offset, chunk.Size);
                    var target = destination[written..];
                    written += chunk.Compressor switch
                    {
                        CompressorNone => CopyRaw(bytes, target),
                        CompressorSnappy => SnappyDecoder.Decompress(bytes, target),
                        _ => throw new InvalidDataException($"HAP complex: chunk {i} has unsupported compressor 0x{chunk.Compressor:X}.")
                    };
                }
                return written;
            }
            default:
                throw new InvalidDataException($"HAP: unknown second-stage compressor 0x{compressor:X}.");
        }
    }

    private static int CopyRaw(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
            throw new InvalidDataException($"HAP: raw payload ({source.Length}B) exceeds the {destination.Length}-byte destination.");
        source.CopyTo(destination);
        return source.Length;
    }

    /// <summary>
    /// Complex: a Decode-Instructions Container then N chunks (each None/Snappy) concatenated in order.
    /// Returns each chunk's compressor + range within <paramref name="chunkRegion"/> (the bytes after the
    /// container), range-checked against the region.
    /// </summary>
    private static Chunk[] ParseComplex(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> chunkRegion)
    {
        var (dicOffset, dicSize, dicType) = ReadSectionHeader(payload, 0);
        if (dicType != SectionDecodeInstructions)
            throw new InvalidDataException($"HAP complex: expected decode-instructions container (0x01), got 0x{dicType:X2}.");

        var dic = payload.Slice(dicOffset, dicSize);
        byte[]? compressors = null;
        uint[]? sizes = null;
        uint[]? offsets = null;

        var p = 0;
        while (p < dic.Length)
        {
            var (secOffset, secSize, secType) = ReadSectionHeader(dic, p);
            var section = dic.Slice(secOffset, secSize);
            switch (secType)
            {
                case SectionChunkCompressorTable: compressors = section.ToArray(); break;
                case SectionChunkSizeTable: sizes = ReadUInt32Array(section); break;
                case SectionChunkOffsetTable: offsets = ReadUInt32Array(section); break;
                // Unknown structural sections are skipped (forward-compatible).
            }
            p = secOffset + secSize;
        }

        if (compressors is null || sizes is null)
            throw new InvalidDataException("HAP complex: missing chunk compressor or size table.");
        if (compressors.Length != sizes.Length || (offsets is not null && offsets.Length != sizes.Length))
            throw new InvalidDataException("HAP complex: chunk table lengths disagree.");

        // Chunk data follows the decode-instructions container within the same top-level payload.
        chunkRegion = payload[(dicOffset + dicSize)..];
        var chunks = new Chunk[compressors.Length];
        long running = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            long start = offsets is not null ? offsets[i] : running;
            long size = sizes[i];
            if (start + size > chunkRegion.Length)
                throw new InvalidDataException($"HAP complex: chunk {i} ({start}+{size}) exceeds the frame.");
            chunks[i] = new Chunk(compressors[i], (int)start, (int)size);
            running += size;
        }
        return chunks;
    }

    /// <summary>
    /// Reads a HAP section header at <paramref name="at"/>: 3-byte little-endian size + 1 type byte; a
    /// zero 3-byte size means an 8-byte header whose extra 4 bytes hold the real (LE) size. Returns the
    /// payload start offset, payload size, and type byte.
    /// </summary>
    private static (int payloadOffset, int size, byte type) ReadSectionHeader(ReadOnlySpan<byte> data, int at)
    {
        if (at + 4 > data.Length)
            throw new InvalidDataException("HAP: truncated section header.");
        var size = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
        var type = data[at + 3];
        int payloadOffset;
        if (size == 0)
        {
            if (at + 8 > data.Length)
                throw new InvalidDataException("HAP: truncated 8-byte section header.");
            size = (int)(data[at + 4] | ((uint)data[at + 5] << 8) | ((uint)data[at + 6] << 16) | ((uint)data[at + 7] << 24));
            // A 4-byte size with the top bit set casts to a negative int, which would slip past the
            // "payloadOffset + size > length" bound below (and then throw a raw ArgumentOutOfRange on Slice).
            if (size < 0)
                throw new InvalidDataException("HAP: section size exceeds Int32 range.");
            payloadOffset = at + 8;
        }
        else
        {
            payloadOffset = at + 4;
        }
        if (payloadOffset + size > data.Length)
            throw new InvalidDataException($"HAP: section payload ({size}B) exceeds the buffer.");
        return (payloadOffset, size, type);
    }

    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> data)
    {
        if (data.Length % 4 != 0)
            throw new InvalidDataException("HAP: chunk table size is not a multiple of 4.");
        var result = new uint[data.Length / 4];
        for (var i = 0; i < result.Length; i++)
            result[i] = data[i * 4] | ((uint)data[i * 4 + 1] << 8) | ((uint)data[i * 4 + 2] << 16) | ((uint)data[i * 4 + 3] << 24);
        return result;
    }
}
