using System.Buffers.Binary;
using MultiMon.Hap;
using MultiMon.Hap.Snappy;

namespace MultiMon.Tests;

/// <summary>
/// Untrusted-input hardening for the HAP path: the demuxer rejects what the frame decoder cannot play
/// (HapM/HapA multi-section fourccs), a zero timescale, and sample tables that point outside the file;
/// the frame/Snappy decoders never write past a caller-sized destination. Self-contained: a minimal
/// QuickTime moov+mdat is synthesised in memory, so no media fixture is needed.
/// </summary>
public class HapHardeningTests
{
    [Fact]
    public void Demuxer_ParsesMinimalSyntheticHap1Clip()
    {
        var frame = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
        using var mov = BuildMov("Hap1", width: 8, height: 4, timescale: 30, frameSizes: [frame.Length], mdat: frame);

        var demux = MovHapDemuxer.Parse(mov);

        Assert.Equal("Hap1", demux.Fourcc);
        Assert.Equal(HapTextureFormat.RgbDxt1, demux.DeclaredFormat);
        Assert.Equal(8, demux.Width);
        Assert.Equal(4, demux.Height);
        var sample = Assert.Single(demux.Samples);
        Assert.Equal(frame.Length, sample.Size);
        Assert.Equal(0, sample.PtsTicks);
        // The sample's bytes are exactly the mdat payload we wrote.
        mov.Position = sample.FileOffset;
        var read = new byte[sample.Size];
        Assert.Equal(sample.Size, mov.Read(read, 0, sample.Size));
        Assert.Equal(frame, read);
    }

    [Theory]
    [InlineData("HapM")]
    [InlineData("HapA")]
    public void Demuxer_RejectsMultiSectionFourccs(string fourcc)
    {
        using var mov = BuildMov(fourcc, 8, 4, 30, [8], new byte[8]);
        var ex = Assert.Throws<InvalidDataException>(() => MovHapDemuxer.Parse(mov));
        Assert.Contains(fourcc, ex.Message);
    }

    [Fact]
    public void Demuxer_RejectsZeroTimescale()
    {
        using var mov = BuildMov("Hap1", 8, 4, timescale: 0, [8], new byte[8]);
        var ex = Assert.Throws<InvalidDataException>(() => MovHapDemuxer.Parse(mov));
        Assert.Contains("timescale", ex.Message);
    }

    [Fact]
    public void Demuxer_RejectsSampleBeyondFileLength()
    {
        // stsz claims a 1 MB frame but the mdat holds 8 bytes: the reader would over-allocate and read past EOF.
        using var mov = BuildMov("Hap1", 8, 4, 30, [1024 * 1024], new byte[8]);
        var ex = Assert.Throws<InvalidDataException>(() => MovHapDemuxer.Parse(mov));
        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public void FrameDecoder_RawPayloadLargerThanDestination_Throws()
    {
        var payload = new byte[16];
        var frame = Section(0xAB, payload); // None + RgbDxt1
        var destination = new byte[8];
        Assert.Throws<InvalidDataException>(() => HapFrameDecoder.Decode(frame, destination, out _));
    }

    [Fact]
    public void FrameDecoder_WritesIntoDestinationAndReportsLengthAndFormat()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var frame = Section(0xAE, payload); // None + RgbaDxt5
        var destination = new byte[16];

        Assert.Equal(payload.Length, HapFrameDecoder.DecodedLength(frame));
        var written = HapFrameDecoder.Decode(frame, destination, out var format);

        Assert.Equal(payload.Length, written);
        Assert.Equal(HapTextureFormat.RgbaDxt5, format);
        Assert.Equal(payload, destination.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Snappy_DeclaredLengthLargerThanDestination_Throws()
    {
        // Snappy block: varint length 4, then one literal element of 4 bytes (tag = (4-1)<<2).
        var block = new byte[] { 0x04, 0x0C, 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.Equal(4, SnappyDecoder.DecodedLength(block));
        Assert.Throws<InvalidDataException>(() => SnappyDecoder.Decompress(block, new byte[3]));

        var ok = new byte[8];
        Assert.Equal(4, SnappyDecoder.Decompress(block, ok));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ok.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Snappy_ViaHapFrame_HonoursDestinationBound()
    {
        var block = new byte[] { 0x04, 0x0C, 1, 2, 3, 4 };
        var frame = Section(0xBB, block); // Snappy + RgbDxt1
        Assert.Equal(4, HapFrameDecoder.DecodedLength(frame));
        Assert.Throws<InvalidDataException>(() => HapFrameDecoder.Decode(frame, new byte[2], out _));
        Assert.Equal(4, HapFrameDecoder.Decode(frame, new byte[4], out var format));
        Assert.Equal(HapTextureFormat.RgbDxt1, format);
    }

    // --- synthetic MOV ---

    /// <summary>[moov][mdat]: one video track with the given fourcc and a constant-rate sample table.</summary>
    private static MemoryStream BuildMov(string fourcc, int width, int height, uint timescale, int[] frameSizes, byte[] mdat)
    {
        var stsd = Box("stsd", U32(0), U32(1), SampleEntry(fourcc, width, height));
        var stts = Box("stts", U32(0), U32(1), U32((uint)frameSizes.Length), U32(1));
        var stsc = Box("stsc", U32(0), U32(1), U32(1), U32((uint)frameSizes.Length), U32(1));
        var stszParts = new List<byte[]> { U32(0), U32(0), U32((uint)frameSizes.Length) };
        stszParts.AddRange(frameSizes.Select(s => U32((uint)s)));
        var stsz = Box("stsz", stszParts.ToArray());

        // The single chunk starts right after the moov header + mdat header; compute moov size first with
        // a placeholder offset (stco is fixed-size, so the moov length does not depend on the value).
        byte[] BuildMoov(uint chunkOffset)
        {
            var stco = Box("stco", U32(0), U32(1), U32(chunkOffset));
            var stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
            var minf = Box("minf", stbl);
            var mdhd = Box("mdhd", U32(0), U32(0), U32(0), U32(timescale), U32((uint)frameSizes.Length), U32(0));
            var mdia = Box("mdia", mdhd, minf);
            var trak = Box("trak", mdia);
            return Box("moov", trak);
        }

        var moovLength = BuildMoov(0).Length;
        var moov = BuildMoov((uint)(moovLength + 8));
        var stream = new MemoryStream();
        stream.Write(moov);
        stream.Write(Box("mdat", mdat));
        stream.Position = 0;
        return stream;
    }

    /// <summary>QuickTime VisualSampleEntry (86 bytes): width @ +32, height @ +34 from the entry start.</summary>
    private static byte[] SampleEntry(string fourcc, int width, int height)
    {
        var entry = new byte[86];
        BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)entry.Length);
        System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(entry, 4);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(32), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(34), (ushort)height);
        return entry;
    }

    private static byte[] Box(string type, params byte[][] payloads)
    {
        var size = 8 + payloads.Sum(p => p.Length);
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        var at = 8;
        foreach (var p in payloads)
        {
            p.CopyTo(box, at);
            at += p.Length;
        }
        return box;
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    /// <summary>A 4-byte-header HAP section: 3-byte little-endian size + 1 type byte + payload.</summary>
    private static byte[] Section(byte type, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)(payload.Length & 0xFF);
        frame[1] = (byte)((payload.Length >> 8) & 0xFF);
        frame[2] = (byte)((payload.Length >> 16) & 0xFF);
        frame[3] = type;
        payload.CopyTo(frame, 4);
        return frame;
    }
}
