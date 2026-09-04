using System.Buffers.Binary;

namespace MultiMon.Hap;

/// <summary>
/// A minimal, vendored QuickTime/MP4 atom demuxer that locates the HAP video track and resolves each
/// frame's byte range + presentation time from the sample tables (stsd/stsz/stco/co64/stsc/stts +
/// mdhd timescale). Zero third-party dependency (REBUILD_ARCHITECTURE §1, Q2). It reads only the
/// <c>moov</c> atom into memory; <see cref="HapSource"/> then reads each frame's bytes on demand.
///
/// Scope: enough of the spec to demux FFmpeg-produced HAP .mov files (the only HAP producer in play).
/// HAP fourccs: Hap1=RGB DXT1, Hap5=RGBA DXT5, HapY=scaled YCoCg DXT5 (Hap Q). Audio/other tracks are ignored.
/// HapM (Hap Q Alpha) and HapA (Hap Alpha-Only) are rejected up front: their frames are multi-section
/// containers (colour + alpha textures) that <see cref="HapFrameDecoder"/> does not decode.
/// Every sample's byte range is bounded by the file length at parse time, so a crafted table can neither
/// over-allocate the frame buffer nor read past the file.
/// </summary>
public sealed class MovHapDemuxer
{
    public readonly record struct SampleRef(long FileOffset, int Size, long PtsTicks);

    public string Fourcc { get; private init; } = "";
    public int Width { get; private init; }
    public int Height { get; private init; }
    public HapTextureFormat DeclaredFormat { get; private init; }
    public IReadOnlyList<SampleRef> Samples { get; private init; } = Array.Empty<SampleRef>();
    public TimeSpan Duration { get; private init; }

    public static MovHapDemuxer Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Parse(stream);
    }

    /// <summary>Parses a seekable .mov stream (the file, or an in-memory fixture in tests).</summary>
    public static MovHapDemuxer Parse(Stream stream)
    {
        var moov = ReadMoov(stream);
        return ParseMoov(moov, stream.Length);
    }

    /// <summary>Reads the moov atom fully into memory (it is small relative to mdat).</summary>
    private static byte[] ReadMoov(Stream stream)
    {
        long offset = 0;
        var header = new byte[16];
        while (offset < stream.Length)
        {
            stream.Position = offset;
            if (stream.Read(header, 0, 8) != 8)
                break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var payloadStart = offset + 8;
            if (size == 1)
            {
                if (stream.Read(header, 8, 8) != 8)
                    break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                payloadStart = offset + 16;
            }
            else if (size == 0)
            {
                size = stream.Length - offset; // extends to EOF
            }

            if (type == "moov")
            {
                var moovSize = size - (payloadStart - offset);
                // Untrusted atom size: reject a moov that can't fit the file or that would OOM the read.
                if (moovSize < 0 || payloadStart + moovSize > stream.Length || moovSize > HapLimits.MaxMoovBytes)
                    throw new InvalidDataException($"HAP .mov: implausible moov size ({moovSize} bytes).");
                var moov = new byte[moovSize];
                stream.Position = payloadStart;
                ReadExactly(stream, moov);
                return moov;
            }
            offset += size;
        }
        throw new InvalidDataException("HAP .mov: no 'moov' atom found.");
    }

    private static MovHapDemuxer ParseMoov(byte[] moov, long fileLength)
    {
        foreach (var trak in FindChildren(moov, 0, moov.Length, "trak"))
        {
            var mdia = FindFirst(moov, trak, "mdia");
            if (mdia is null) continue;
            var minf = FindFirst(moov, mdia.Value, "minf");
            var mdhd = FindFirst(moov, mdia.Value, "mdhd");
            if (minf is null || mdhd is null) continue;
            var stbl = FindFirst(moov, minf.Value, "stbl");
            if (stbl is null) continue;
            var stsd = FindFirst(moov, stbl.Value, "stsd");
            if (stsd is null) continue;

            var (fourcc, width, height) = ReadStsd(moov, stsd.Value);
            if (fourcc is "HapM" or "HapA")
                throw new InvalidDataException($"HAP .mov: '{fourcc}' (Hap Q Alpha / Hap Alpha-Only) is not supported — " +
                                               "its frames are multi-section (colour + alpha); re-encode as Hap, Hap Alpha or Hap Q.");
            if (!IsHapFourcc(fourcc)) continue; // skip audio / non-HAP tracks

            var timescale = ReadTimescale(moov, mdhd.Value);
            if (timescale == 0)
                throw new InvalidDataException("HAP .mov: mdhd timescale is 0.");
            var sizes = ReadStsz(moov, RequireChild(moov, stbl.Value, "stsz"));
            var chunkOffsets = ReadChunkOffsets(moov, stbl.Value);
            var stsc = ReadStsc(moov, RequireChild(moov, stbl.Value, "stsc"));
            var samples = BuildSamples(sizes, chunkOffsets, stsc, moov, stbl.Value, timescale, fileLength, out var durationTicks);

            return new MovHapDemuxer
            {
                Fourcc = fourcc,
                Width = width,
                Height = height,
                DeclaredFormat = FourccToFormat(fourcc),
                Samples = samples,
                Duration = TimeSpan.FromTicks(durationTicks)
            };
        }
        throw new InvalidDataException("HAP .mov: no HAP video track found.");
    }

    // --- sample-table readers ---

    private static (string fourcc, int width, int height) ReadStsd(byte[] buf, Box stsd)
    {
        // stsd: 4 version/flags + 4 entry_count, then the first sample entry (size, fourcc, ...).
        var entry = stsd.PayloadStart + 8;
        if (entry + 36 > stsd.PayloadEnd)
            throw new InvalidDataException("HAP .mov: truncated stsd sample entry.");
        var fourcc = System.Text.Encoding.ASCII.GetString(buf, entry + 4, 4);
        // QuickTime VisualSampleEntry: width @ +32, height @ +34 (big-endian uint16) from the entry start.
        var width = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(entry + 32));
        var height = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(entry + 34));
        return (fourcc, width, height);
    }

    private static uint ReadTimescale(byte[] buf, Box mdhd)
    {
        // mdhd: version(1) + flags(3); v0: creation(4) modification(4) timescale(4); v1: 8/8/4.
        var p = mdhd.PayloadStart;
        var version = buf[p];
        var timescaleOffset = p + 4 + (version == 1 ? 16 : 8);
        return BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(timescaleOffset));
    }

    private static int[] ReadStsz(byte[] buf, Box stsz)
    {
        var p = stsz.PayloadStart;
        var sampleSize = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 8));
        // Variable-size entries are read from the buffer (bound by it); fixed-size has no backing bytes,
        // so the sizes[] allocation is bounded by the moov ceiling instead.
        RequireCount(count, 4, sampleSize != 0 ? HapLimits.MaxMoovBytes : buf.Length, "stsz");
        var sizes = new int[count];
        if (sampleSize != 0)
        {
            Array.Fill(sizes, sampleSize);
        }
        else
        {
            var entries = p + 12;
            for (var i = 0; i < count; i++)
                sizes[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(entries + i * 4));
        }
        return sizes;
    }

    private static long[] ReadChunkOffsets(byte[] buf, Box stbl)
    {
        var stco = FindFirst(buf, stbl, "stco");
        if (stco is not null)
        {
            var p = stco.Value.PayloadStart;
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
            RequireCount(count, 4, buf.Length, "stco");
            var offsets = new long[count];
            for (var i = 0; i < count; i++)
                offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 8 + i * 4));
            return offsets;
        }
        var co64 = FindFirst(buf, stbl, "co64") ?? throw new InvalidDataException("HAP .mov: no stco/co64.");
        var q = co64.PayloadStart;
        var n = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(q + 4));
        RequireCount(n, 8, buf.Length, "co64");
        var result = new long[n];
        for (var i = 0; i < n; i++)
            result[i] = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(q + 8 + i * 8));
        return result;
    }

    private static (uint firstChunk, uint samplesPerChunk)[] ReadStsc(byte[] buf, Box stsc)
    {
        var p = stsc.PayloadStart;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
        RequireCount(count, 12, buf.Length, "stsc"); // each entry is 12 bytes in the buffer
        var entries = new (uint, uint)[count];
        var e = p + 8;
        for (var i = 0; i < count; i++)
        {
            var firstChunk = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + i * 12));
            var samplesPerChunk = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + i * 12 + 4));
            entries[i] = (firstChunk, samplesPerChunk);
        }
        return entries;
    }

    private static SampleRef[] BuildSamples(int[] sizes, long[] chunkOffsets,
        (uint firstChunk, uint samplesPerChunk)[] stsc, byte[] buf, Box stbl, uint timescale, long fileLength, out long durationTicks)
    {
        var sampleDurations = ReadSttsDurations(buf, RequireChild(buf, stbl, "stts"), sizes.Length);
        var samples = new SampleRef[sizes.Length];
        var sampleIndex = 0;
        long cumulativeUnits = 0;

        for (var chunk = 0; chunk < chunkOffsets.Length && sampleIndex < sizes.Length; chunk++)
        {
            var samplesPerChunk = SamplesPerChunk(stsc, chunk + 1); // stsc first_chunk is 1-based
            var offsetInChunk = 0L;
            for (var s = 0; s < samplesPerChunk && sampleIndex < sizes.Length; s++)
            {
                var ptsTicks = (long)(cumulativeUnits * (double)TimeSpan.TicksPerSecond / timescale);
                var fileOffset = chunkOffsets[chunk] + offsetInChunk;
                var size = sizes[sampleIndex];
                // Untrusted tables: a sample must lie inside the file, or the frame read would over-allocate
                // (size) or read past EOF (offset). Reject at parse time rather than mid-playback.
                if (size < 0 || fileOffset < 0 || fileOffset + size > fileLength)
                    throw new InvalidDataException($"HAP .mov: sample {sampleIndex} ({fileOffset}+{size}) lies outside the {fileLength}-byte file.");
                samples[sampleIndex] = new SampleRef(fileOffset, size, ptsTicks);
                offsetInChunk += sizes[sampleIndex];
                cumulativeUnits += sampleDurations[sampleIndex];
                sampleIndex++;
            }
        }

        if (sampleIndex != sizes.Length)
            throw new InvalidDataException($"HAP .mov: chunk table covers {sampleIndex} of {sizes.Length} samples.");
        durationTicks = (long)(cumulativeUnits * (double)TimeSpan.TicksPerSecond / timescale);
        return samples;
    }

    private static long[] ReadSttsDurations(byte[] buf, Box stts, int sampleCount)
    {
        var p = stts.PayloadStart;
        var entryCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
        RequireCount(entryCount, 8, buf.Length, "stts"); // each (run,delta) entry is 8 bytes in the buffer
        var durations = new long[sampleCount];
        var idx = 0;
        var e = p + 8;
        for (var i = 0; i < entryCount && idx < sampleCount; i++)
        {
            var runCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + i * 8));
            var delta = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + i * 8 + 4));
            for (var r = 0; r < runCount && idx < sampleCount; r++)
                durations[idx++] = delta;
        }
        // Any uncovered tail keeps the last delta (defensive; FFmpeg HAP is constant-rate).
        var last = idx > 0 ? durations[idx - 1] : 0;
        for (; idx < sampleCount; idx++)
            durations[idx] = last;
        return durations;
    }

    private static uint SamplesPerChunk((uint firstChunk, uint samplesPerChunk)[] stsc, int chunk)
    {
        uint result = 0;
        foreach (var (firstChunk, samplesPerChunk) in stsc)
        {
            if (firstChunk > chunk) break;
            result = samplesPerChunk;
        }
        return result;
    }

    // --- atom walking ---

    private readonly record struct Box(int PayloadStart, int PayloadEnd);

    private static IEnumerable<Box> FindChildren(byte[] buf, int start, int end, string type)
    {
        var p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
            var t = System.Text.Encoding.ASCII.GetString(buf, p + 4, 4);
            var payloadStart = p + 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(p + 8));
                payloadStart = p + 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }
            if (size < 8 || p + size > end)
                break; // malformed — stop rather than read past the box
            if (t == type)
                yield return new Box(payloadStart, (int)(p + size));
            p += (int)size;
        }
    }

    private static Box? FindFirst(byte[] buf, Box container, string type)
    {
        foreach (var child in FindChildren(buf, container.PayloadStart, container.PayloadEnd, type))
            return child;
        return null;
    }

    private static Box RequireChild(byte[] buf, Box container, string type)
        => FindFirst(buf, container, type) ?? throw new InvalidDataException($"HAP .mov: missing '{type}' atom.");

    /// <summary>Reject an untrusted, file-claimed entry count that would over-allocate. For buffer-backed
    /// tables <paramref name="maxBytes"/> is the moov length (you can't have more entries than bytes to
    /// hold them); for the entry-less fixed-size stsz it is the moov ceiling.</summary>
    private static void RequireCount(int count, long entryBytes, long maxBytes, string atom)
    {
        if (count < 0 || (long)count * entryBytes > maxBytes)
            throw new InvalidDataException($"HAP .mov: implausible {atom} entry count ({count}).");
    }

    private static bool IsHapFourcc(string fourcc) => fourcc is "Hap1" or "Hap5" or "HapY";

    private static HapTextureFormat FourccToFormat(string fourcc) => fourcc switch
    {
        "Hap1" => HapTextureFormat.RgbDxt1,
        "Hap5" => HapTextureFormat.RgbaDxt5,
        "HapY" => HapTextureFormat.YCoCgDxt5,
        _ => throw new InvalidDataException($"HAP .mov: unsupported fourcc '{fourcc}'.")
    };

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("HAP .mov: unexpected EOF reading moov.");
            read += n;
        }
    }
}
