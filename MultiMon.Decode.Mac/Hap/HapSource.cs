using System.Diagnostics;
using System.Runtime.InteropServices;
using Foundation;
using Metal;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics.Mac;
using MultiMon.Hap;

namespace MultiMon.Decode.Mac.Hap;

/// <summary>
/// The Mac twin of <c>MultiMon.Decode.Hap.HapSource</c>: decodes a HAP .mov on its OWN thread (off the
/// AppKit main thread and the render thread) and publishes BCn Metal textures into a
/// <see cref="FrameTimeline"/> for the render thread. Decode is CPU-only (demux → Snappy → compressed-texture
/// bytes, from <c>MultiMon.Hap</c>); the upload is one <c>replaceRegion</c> per frame into a texture from a
/// fixed <see cref="TexturePool"/> created ONCE here at the clip's size/format — no per-frame texture
/// creation. The pass blits the published texture into its own persistent texture of the SAME BCn format and
/// the HapQ YCoCg→RGB conversion happens in the shader.
///
/// Fence rule: a pooled texture is handed back to the pool by the frame's release closure, which
/// <see cref="DecodedFrame"/> fires only after the timeline passed the frame AND the command buffer that
/// blitted it has completed — never by the timeline's Dispose alone (the blit is asynchronous). The pool is
/// sized <see cref="TimelineDepth"/> + <see cref="InFlightMargin"/>: every slot the timeline can hold plus the
/// frames the GPU can still be reading (three command buffers in flight per output) plus the one being
/// written. If that is ever exceeded the decode thread blocks in <see cref="TexturePool.Rent"/>, it does not
/// overwrite.
///
/// The demux + frame index are built ONCE; the thread loops the clip with a monotonic PTS offset so buffered
/// frames share the MasterClock timeline (drift is corrected by frame selection, never seeking). Only VALID
/// frames are published: each decoded frame must be exactly the declared format and exactly
/// <see cref="_frameBytes"/> long (the upload takes that many bytes at <see cref="_rowPitch"/>, so a short
/// frame would be a native over-read). A bad frame is skipped and logged; the source stops only after
/// <see cref="MaxConsecutiveFailures"/> bad frames in a row (<see cref="IsFaulted"/>). Every decode iteration
/// runs inside its own NSAutoreleasePool (LESSON-BUG-008).
/// </summary>
public sealed class HapSource : IMetalSource
{
    /// <summary>Consecutive undecodable frames before the source gives up (a burst, not one bad frame).</summary>
    private const int MaxConsecutiveFailures = 30;

    /// <summary>Frames the timeline buffers ahead of the clock (the Windows default depth).</summary>
    private const int TimelineDepth = 8;

    /// <summary>Pool slack beyond the timeline: the render side's three in-flight command buffers may each
    /// still be reading a passed frame, plus the one the decode thread is writing.</summary>
    private const int InFlightMargin = 4;

    private static readonly TimeSpan DecodeRateLogInterval = TimeSpan.FromSeconds(2);

    private readonly ILog _log;
    private readonly string _path;
    private readonly MovHapDemuxer _demux;
    private readonly TexturePool _pool;
    private readonly int _rowPitch;
    private readonly int _frameBytes;
    private readonly long _loopDurationTicks;
    private readonly byte[] _pixels;   // the one decode/upload staging buffer (replaceRegion copies synchronously)

    private FileStream? _file;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private long _ptsTicks;
    private long _decodedFrames;

    /// <summary>The decode→render handoff. Owned here; the render-side pass borrows from it.</summary>
    public FrameTimeline Frames { get; } = new(TimelineDepth);

    /// <summary>
    /// Texture width/height the pass must create — the clip's dimensions rounded UP to a multiple of 4
    /// (BCn is 4×4 blocks; a non-multiple-of-4 clip is encoded with edge-padding blocks, which the padded
    /// texture holds and the full-frame UV shows at the right/bottom edge — logged at open).
    /// </summary>
    public int Width { get; }
    /// <summary>Frames over the track duration (the mdhd timescale already scaled the sample deltas into ticks).</summary>
    public double FrameRate { get; }
    public int Height { get; }

    /// <summary>The Metal BCn format the pass must create its source texture in.</summary>
    public MTLPixelFormat TextureFormat { get; }

    /// <summary>True for HAP Q (scaled YCoCg-DXT5) — the pass must use the YCoCg→RGB shader.</summary>
    public bool UseYCoCg { get; }

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>True once the decode loop has stopped on its own (a burst of undecodable frames or an
    /// unrecoverable error) — the output keeps its last frame; a consumer may poll this and rebuild.</summary>
    public bool IsFaulted => _faulted;

    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));

    /// <summary>Frames decoded and published since Start (thread-safe read) — the harness's advance check.</summary>
    public long DecodedFrames => Volatile.Read(ref _decodedFrames);

    /// <summary>Pooled textures still held by in-flight GPU work when <see cref="Dispose"/> ran — non-zero means the
    /// fence and the teardown order disagree (the render side had not completed its reads). The harness fails on it.</summary>
    public int OutstandingAtDispose { get; private set; }

    /// <summary>Opens the clip and creates the texture pool on <paramref name="provider"/>'s device. Caller thread:
    /// controller/harness — never the main thread (file + device work).</summary>
    public HapSource(string path, GraphicsDeviceProvider provider, ILog log, string? id = null)
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
        _pixels = new byte[_frameBytes];
        _loopDurationTicks = Math.Max(1, _demux.Duration.Ticks);
        FrameRate = _demux.Duration.Ticks > 0 ? _demux.Samples.Count / _demux.Duration.TotalSeconds : 0;

        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(TextureFormat, (nuint)Width, (nuint)Height, false);
        descriptor.Usage = MTLTextureUsage.ShaderRead;
        // CPU-written textures are Shared on unified memory, Managed on discrete-GPU (Intel) Macs; replaceRegion
        // keeps a Managed texture's GPU copy current itself.
        descriptor.StorageMode = provider.Device.HasUnifiedMemory ? MTLStorageMode.Shared : MTLStorageMode.Managed;
        _pool = new TexturePool(provider.Device, descriptor, TimelineDepth + InFlightMargin, provider.Tracker);

        _log.Info("Decode", $"{Id}: HAP {_demux.Fourcc} {_demux.Width}x{_demux.Height}, {_demux.Samples.Count} frames @ {FrameRate:0.###} fps, " +
                            $"format={TextureFormat} ycocg={UseYCoCg} dur={_demux.Duration.TotalSeconds:0.00}s, " +
                            $"pool={_pool.Count}x{_frameBytes / 1024}KB ({descriptor.StorageMode}).");
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

    /// <summary>Signals the loop, releases it from a full timeline or an empty pool, and joins. Off the main
    /// thread (the V0087 rule).</summary>
    public void Stop()
    {
        _stop = true;
        Frames.SignalStop();
        _pool.SignalStop();
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
            var rateClock = Stopwatch.StartNew();
            long rateFrames = 0;

            while (!_stop)
            {
                for (var i = 0; i < _demux.Samples.Count; i++)
                {
                    if (_stop) return;
                    using var pool = new NSAutoreleasePool(); // replaceRegion + the frame's wrappers are autoreleased (LESSON-BUG-008)
                    var sample = _demux.Samples[i];

                    if (frameBuffer.Length < sample.Size)
                        frameBuffer = new byte[sample.Size];

                    string? rejection;
                    try
                    {
                        ReadFrame(sample.FileOffset, frameBuffer, sample.Size);
                        var written = HapFrameDecoder.Decode(frameBuffer.AsSpan(0, sample.Size), _pixels, out var format);
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

                    // A texture with no pending reader (null only when Stop released the wait).
                    var texture = _pool.Rent();
                    if (texture is null) return;
                    Upload(texture);

                    var globalPts = loopBaseTicks + sample.PtsTicks;
                    Volatile.Write(ref _ptsTicks, globalPts);
                    Interlocked.Increment(ref _decodedFrames);
                    // The frame OWNS the rented texture; its release (timeline passed it + GPU read completed)
                    // returns it to the pool exactly once.
                    Frames.Publish(new DecodedFrame(texture, TimeSpan.FromTicks(globalPts), () => _pool.Return(texture)));

                    rateFrames++;
                    if (rateClock.Elapsed >= DecodeRateLogInterval)
                    {
                        _log.Info("Decode", $"{Id}: decoded {rateFrames} frames in {rateClock.Elapsed.TotalSeconds:0.0}s " +
                                            $"({rateFrames / rateClock.Elapsed.TotalSeconds:0.0} fps), pts={CurrentPts.TotalSeconds:0.00}s.");
                        rateFrames = 0;
                        rateClock.Restart();
                    }
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

    /// <summary>One replaceRegion of the whole (block-aligned) texture from the staging buffer: a synchronous CPU
    /// copy, so the buffer is reusable as soon as it returns.</summary>
    private void Upload(IMTLTexture texture)
    {
        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            texture.ReplaceRegion(MTLRegion.Create2D(0, 0, Width, Height), 0, handle.AddrOfPinnedObject(), (nuint)_rowPitch);
        }
        finally
        {
            handle.Free();
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

    /// <summary>HAP texture format → (Metal BCn format, bytes per 4×4 block). Same table as Windows.</summary>
    private static (MTLPixelFormat format, int blockBytes) MapFormat(HapTextureFormat hap) => hap switch
    {
        HapTextureFormat.RgbDxt1 => (MTLPixelFormat.BC1RGBA, 8),
        HapTextureFormat.RgbaDxt5 => (MTLPixelFormat.BC3RGBA, 16),
        HapTextureFormat.YCoCgDxt5 => (MTLPixelFormat.BC3RGBA, 16),
        HapTextureFormat.RgbaBptc => (MTLPixelFormat.BC7_RGBAUnorm, 16),
        HapTextureFormat.ARgtc1 => (MTLPixelFormat.BC4_RUnorm, 8),
        _ => throw new NotSupportedException($"HAP texture format {hap} is not supported.")
    };

    /// <summary>Stop → dispose the timeline (its frames return their textures) → dispose the pool. PRECONDITION:
    /// the render side has unbound this source and its in-flight command buffers have completed.</summary>
    public void Dispose()
    {
        Stop();
        Frames.Dispose();
        OutstandingAtDispose = _pool.Outstanding;
        if (OutstandingAtDispose > 0)
            _log.Error("Decode", $"{Id}: {OutstandingAtDispose} pooled texture(s) still held by in-flight GPU work at dispose — freed on completion (teardown-order violation).");
        _pool.Dispose();
    }
}
