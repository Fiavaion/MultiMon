using MultiMon.Hap;
using MultiMon.Hap.Snappy;

namespace MultiMon.Tests;

/// <summary>
/// HAP container/frame parsing (M5.1). The synthetic tests pin the section-header + compressor logic
/// deterministically; the fixture tests prove the full MOV demux + Snappy + complex-chunk path against
/// a real FFmpeg-produced HapY .mov. Fixture tests self-skip when the media isn't present.
/// </summary>
public class HapParserTests
{
    private const string FixturePath = @"D:\testing Videos\Hap\5sec_hap.mov";

    // 1920x1080 as YCoCg-DXT5 (BC3): ceil(w/4)*ceil(h/4)*16 bytes per block.
    private const int Bc3SizeFor1080p = (1920 / 4) * (1080 / 4) * 16; // 2,073,600

    [Fact]
    public void Decode_NoneCompressor_ReturnsPayloadVerbatim()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        // type 0xAB = compressor None (0xA) + format RgbDxt1 (0xB); 4-byte header with 3-byte LE size.
        var frame = BuildSection(0xAB, payload);

        var result = HapFrameDecoder.Decode(frame);

        Assert.Equal(HapTextureFormat.RgbDxt1, result.Format);
        Assert.Equal(payload, result.Data);
    }

    [Fact]
    public void Decode_EightByteHeader_ReadsExtendedSize()
    {
        var payload = new byte[300]; // > 255 to make the point; still fits 3 bytes, so force the 8-byte form
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        // 8-byte header: 3-byte size = 0 (=> extended), type 0xAF (None + YCoCgDXT5), then 4-byte LE size.
        var frame = new byte[8 + payload.Length];
        frame[3] = 0xAF;
        BitConverter.GetBytes((uint)payload.Length).CopyTo(frame, 4);
        payload.CopyTo(frame, 8);

        var result = HapFrameDecoder.Decode(frame);

        Assert.Equal(HapTextureFormat.YCoCgDxt5, result.Format);
        Assert.Equal(payload, result.Data);
    }

    [Fact]
    public void MovDemuxer_ParsesHapYFixture()
    {
        if (!File.Exists(FixturePath)) return; // media not present on this machine
        var demux = MovHapDemuxer.Parse(FixturePath);

        Assert.Equal("HapY", demux.Fourcc);
        Assert.Equal(HapTextureFormat.YCoCgDxt5, demux.DeclaredFormat);
        Assert.Equal(1920, demux.Width);
        Assert.Equal(1080, demux.Height);
        Assert.True(demux.Samples.Count > 1, "expected multiple frames in a 5s clip");

        // PTS must be strictly ascending and start at zero.
        Assert.Equal(0, demux.Samples[0].PtsTicks);
        for (var i = 1; i < demux.Samples.Count; i++)
            Assert.True(demux.Samples[i].PtsTicks > demux.Samples[i - 1].PtsTicks, $"PTS not ascending at frame {i}");

        Assert.InRange(demux.Duration.TotalSeconds, 4.0, 6.0);
    }

    [Fact]
    public void Decode_FirstFixtureFrame_YieldsFullBc3Texture()
    {
        if (!File.Exists(FixturePath)) return; // media not present on this machine
        var demux = MovHapDemuxer.Parse(FixturePath);
        var first = demux.Samples[0];

        var frame = new byte[first.Size];
        using (var fs = new FileStream(FixturePath, FileMode.Open, FileAccess.Read))
        {
            fs.Position = first.FileOffset;
            var read = 0;
            while (read < frame.Length)
            {
                var n = fs.Read(frame, read, frame.Length - read);
                if (n == 0) break;
                read += n;
            }
        }

        var result = HapFrameDecoder.Decode(frame);

        Assert.Equal(HapTextureFormat.YCoCgDxt5, result.Format);
        // The complex/chunked Snappy frame must reassemble to exactly one full BC3 surface.
        Assert.Equal(Bc3SizeFor1080p, result.Data.Length);
    }

    [Fact]
    public void Decode_EveryFixtureFrame_YieldsConsistentBc3Size()
    {
        if (!File.Exists(FixturePath)) return; // media not present on this machine
        var demux = MovHapDemuxer.Parse(FixturePath);
        using var fs = new FileStream(FixturePath, FileMode.Open, FileAccess.Read);

        foreach (var sample in demux.Samples)
        {
            var frame = new byte[sample.Size];
            fs.Position = sample.FileOffset;
            var read = 0;
            while (read < frame.Length)
            {
                var n = fs.Read(frame, read, frame.Length - read);
                if (n == 0) break;
                read += n;
            }
            var result = HapFrameDecoder.Decode(frame);
            Assert.Equal(Bc3SizeFor1080p, result.Data.Length);
        }
    }

    [Fact]
    public void Snappy_ImplausibleDeclaredLength_ThrowsInsteadOfOom()
    {
        // Varint preamble = 0x7FFFFFFF (~2 GB), far above the decoded-size ceiling. A naive decoder would
        // `new byte[2GB]` and OOM; the guard must reject it as malformed instead.
        var hostile = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        Assert.Throws<InvalidDataException>(() => SnappyDecoder.Decompress(hostile));
    }

    [Fact]
    public void Decode_ExtendedSizeBeyondInt32_ThrowsInsteadOfRawSliceError()
    {
        // 8-byte header: 3-byte size = 0 (extended), type 0xAF, then a 4-byte size with the top bit set.
        // Cast to int that's negative; without the guard it slips past the bound check and throws a raw
        // ArgumentOutOfRangeException on Slice — the guard must surface it as InvalidDataException.
        var frame = new byte[8];
        frame[3] = 0xAF;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(frame, 4);
        Assert.Throws<InvalidDataException>(() => HapFrameDecoder.Decode(frame));
    }

    /// <summary>Builds a 4-byte-header HAP section: 3-byte little-endian size + 1 type byte + payload.</summary>
    private static byte[] BuildSection(byte type, byte[] payload)
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
