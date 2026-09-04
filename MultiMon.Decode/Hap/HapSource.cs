using System.Buffers;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics;
using MultiMon.Hap;
using Vortice.DXGI;

namespace MultiMon.Decode.Hap;

/// <summary>
/// Decodes a HAP .mov on its OWN thread (off the UI and render threads) and publishes BCn texture
/// bytes into a <see cref="FrameTimeline"/> for the render thread, mirroring <c>MediaFoundationSource</c>.
/// HAP decode is CPU-only (demux → Snappy → concatenate compressed-texture bytes), so this source holds
/// NO D3D11 device state — it is inherently device-removed-safe: the render thread's pass owns the BCn
/// texture and uploads each published frame, and the HapQ YCoCg→RGB conversion happens in the shader.
///
/// The demux + frame index are built ONCE; the thread loops the clip with a monotonic PTS offset so
/// buffered frames share the MasterClock timeline (drift is corrected by frame selection, never seeking).
///
/// Only VALID frames are published: each decoded frame must be exactly the declared format and exactly
/// <see cref="_frameBytes"/> long (the pass uploads that many bytes with <see cref="_rowPitch"/>, so a
/// short frame would be a native over-read on the render thread). A bad frame is skipped and logged; the
/// source stops only after <see cref="MaxConsecutiveFailures"/> bad frames in a row (<see cref="IsFaulted"/>).
/// Frame buffers are pooled: each published frame rents from <see cref="ArrayPool{T}.Shared"/> and
/// returns the array in its release closure, which <see cref="DecodedFrame.Dispose"/> runs exactly once.
/// </summary>
public sealed class HapSource : ISource
{
    /// <summary>Consecutive undecodable frames before the source gives up (a burst, not one bad frame).</summary>
    private const int MaxConsecutiveFailures = 30;

    private readonly ILog _log;
    private readonly string _path;
    private readonly MovHapDemuxer _demux;
    private readonly int _rowPitch;
    private readonly int _frameBytes;
    private readonly long _loopDurationTicks;

    private FileStream? _file;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private long _ptsTicks;

    /// <summary>The decode→render handoff. Owned here; the render-side pass borrows from it.</summary>
    public FrameTimeline Frames { get; } = new();

    /// <summary>
    /// Texture width/height the pass must create — the clip's dimensions rounded UP to a multiple of 4.
    /// BCn textures are 4×4 blocks and D3D11 requires block-aligned top-level dimensions; a clip whose
    /// size is not a multiple of 4 is encoded with edge-padding blocks, which the padded texture holds.
    /// The pass samples the whole texture (UV 0..1), so such a clip shows its ≤3px encoder padding at
    /// the right/bottom edge — logged at open. Every real encoder (FFmpeg, the built-in converter) emits
    /// multiple-of-4 dimensions, where this is exactly the clip size.
    /// </summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>The DXGI format the pass must create its source texture in (BCn).</summary>
    public Format TextureFormat { get; }

    /// <summary>True for HAP Q (scaled YCoCg-DXT5) — the pass must use the YCoCg→RGB shader.</summary>
    public bool UseYCoCg { get; }

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>True once the decode loop has stopped on its own (a burst of undecodable frames or an
    /// unrecoverable error) — the output keeps its last frame; a consumer may poll this and rebuild.</summary>
    public bool IsFaulted => _faulted;

    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));

    public HapSource(string path, ILog log, string? id = null)
    {
        _log = log;
        _path = path;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("HAP clip not found.", path);

        _demux = MovHapDemuxer.Parse(path);
        if (_demux.Samples.Count == 0)
            throw new InvalidOperationException($"HAP clip '{path}' has no frames.");
        if (_demux.Width <= 0 || _demux.Height <= 0)
            throw new InvalidDataException($"HAP clip '{path}' declares an invalid size {_demux.Width}x{_demux.Height}.");

        (TextureFormat, var blockBytes) = MapFormat(_demux.DeclaredFormat);
        UseYCoCg = _demux.DeclaredFormat == HapTextureFormat.YCoCgDxt5;
        Width = (_demux.Width + 3) & ~3;
        Height = (_demux.Height + 3) & ~3;
        _rowPitch = (Width / 4) * blockBytes;   // BCn: one row of 4x4 blocks
        _frameBytes = _rowPitch * (Height / 4);
        // Loop on the last sample's end (its PTS + the clip's average frame duration) so PTS stays monotonic.
        _loopDurationTicks = Math.Max(1, _demux.Duration.Ticks);

        _log.Info("Decode", $"{Id}: HAP {_demux.Fourcc} {_demux.Width}x{_demux.Height}, {_demux.Samples.Count} frames, " +
                            $"format={TextureFormat} ycocg={UseYCoCg} dur={_demux.Duration.TotalSeconds:0.00}s.");
        if (Width != _demux.Width || Height != _demux.Height)
            _log.Info("Decode", $"{Id}: clip size is not a multiple of 4 — texture padded to {Width}x{Height}; " +
                                "the encoder's edge-padding blocks are visible at the right/bottom edge.");
    }

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Source already started.");
        if (_stop)
            throw new InvalidOperationException("Source cannot be restarted after Stop — create a new source.");
        _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _stop = false;
        _thread = new Thread(DecodeLoop) { Name = $"MultiMon.Decode.{Id}", IsBackground = true };
        try
        {
            _thread.Start();
        }
        catch
        {
            _file.Dispose();   // don't strand the open file handle if the thread fails to start (e.g. OOM)
            _file = null;
            _thread = null;
            throw;
        }
        _log.Info("Decode", $"{Id}: HAP decode thread started.");
    }

    public void Stop()
    {
        _stop = true;
        Frames.SignalStop(); // release the decode thread if it is blocked publishing into a full timeline
        var thread = _thread;
        thread?.Join();
        _thread = null;
        _file?.Dispose();
        _file = null;
    }

    private void DecodeLoop()
    {
        try
        {
            long loopBaseTicks = 0;
            var frameBuffer = Array.Empty<byte>();   // reused compressed-frame read buffer (sizes bounded by the demuxer)
            var consecutiveFailures = 0;

            while (!_stop)
            {
                for (var i = 0; i < _demux.Samples.Count; i++)
                {
                    if (_stop) return;
                    var sample = _demux.Samples[i];

                    if (frameBuffer.Length < sample.Size)
                        frameBuffer = new byte[sample.Size];

                    var globalPts = loopBaseTicks + sample.PtsTicks;
                    var pixels = ArrayPool<byte>.Shared.Rent(_frameBytes);
                    string? rejection;
                    try
                    {
                        ReadFrame(sample.FileOffset, frameBuffer, sample.Size);
                        var written = HapFrameDecoder.Decode(frameBuffer.AsSpan(0, sample.Size), pixels.AsSpan(0, _frameBytes), out var format);
                        rejection = format != _demux.DeclaredFormat
                            ? $"frame format {format} does not match the clip's declared {_demux.DeclaredFormat}"
                            : written != _frameBytes
                                ? $"decoded {written} bytes, expected {_frameBytes} for {Width}x{Height} {TextureFormat}"
                                : null;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException)
                    {
                        rejection = ex.Message;
                    }

                    if (rejection is not null)
                    {
                        ArrayPool<byte>.Shared.Return(pixels);
                        consecutiveFailures++;
                        if (consecutiveFailures == 1 || consecutiveFailures == MaxConsecutiveFailures)
                            _log.Error("Decode", $"{Id}: skipping bad HAP frame {i} ({rejection}); {consecutiveFailures} consecutive failure(s).");
                        if (consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            _faulted = true;
                            _log.Error("Decode", $"{Id}: {MaxConsecutiveFailures} consecutive undecodable frames; stopping decode.");
                            return;
                        }
                        continue;
                    }
                    consecutiveFailures = 0;

                    Volatile.Write(ref _ptsTicks, globalPts);
                    // The frame OWNS the rented array; DecodedFrame.Dispose runs this exactly once.
                    var frame = new DecodedFrame(pixels.AsMemory(0, _frameBytes), _rowPitch, TimeSpan.FromTicks(globalPts),
                        () => ArrayPool<byte>.Shared.Return(pixels));
                    Frames.Publish(frame);
                }
                loopBaseTicks += _loopDurationTicks; // keep PTS ascending across loops (ADR 0002 D1)
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error("Decode", $"{Id}: HAP decode loop ended on exception: {ex}");
        }
    }

    private void ReadFrame(long offset, byte[] buffer, int size)
    {
        _file!.Position = offset;
        var read = 0;
        while (read < size)
        {
            var n = _file.Read(buffer, read, size - read);
            if (n == 0) throw new EndOfStreamException($"{Id}: unexpected EOF reading a HAP frame.");
            read += n;
        }
    }

    /// <summary>HAP texture format → (DXGI BCn format, bytes per 4×4 block).</summary>
    private static (Format format, int blockBytes) MapFormat(HapTextureFormat hap) => hap switch
    {
        HapTextureFormat.RgbDxt1 => (Format.BC1_UNorm, 8),
        HapTextureFormat.RgbaDxt5 => (Format.BC3_UNorm, 16),
        HapTextureFormat.YCoCgDxt5 => (Format.BC3_UNorm, 16),
        HapTextureFormat.RgbaBptc => (Format.BC7_UNorm, 16),
        HapTextureFormat.ARgtc1 => (Format.BC4_UNorm, 8),
        _ => throw new NotSupportedException($"HAP texture format {hap} is not supported.")
    };

    public void Dispose()
    {
        Stop();
        Frames.Dispose();
    }
}
