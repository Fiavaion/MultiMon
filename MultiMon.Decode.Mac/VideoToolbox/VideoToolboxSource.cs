using System.Diagnostics;
using AVFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics.Mac;
using ObjCRuntime;
using VideoToolbox;

namespace MultiMon.Decode.Mac.VideoToolbox;

/// <summary>
/// The Mac twin of <c>MultiMon.Decode.MediaFoundation.MediaFoundationSource</c>: demuxes one H.264/HEVC clip with
/// an <see cref="AVAssetReader"/> (compressed <see cref="CMSampleBuffer"/>s, no output settings), decodes it with a
/// <see cref="VTDecompressionSession"/> into Metal-compatible BGRA <see cref="CVPixelBuffer"/>s and publishes them
/// ZERO-COPY into a <see cref="FrameTimeline"/> as <see cref="CVMetalTextureCache"/>-vended textures. Output is
/// BGRA for parity with the Windows pass (one format-uniform contract; the pass blits BGRA→BGRA).
///
/// Decode path: the session is created REQUIRING the hardware decoder; if that fails it is recreated allowing
/// software and the fallback is logged (the Windows HW→SW fallback line). <c>forceSoftware</c> (the harness's
/// <c>--force-sw-decode</c>) skips the hardware attempt. Which path is active is read back from the session
/// (<see cref="IsHardwareDecode"/>) and logged, never assumed.
///
/// Threading: the decode thread (own thread, per-iteration NSAutoreleasePool — LESSON-BUG-008) reads samples and
/// submits them asynchronously; the callback only retains the pixel buffer and queues it. VideoToolbox's hardware
/// decoder fires that callback in DECODE order, not presentation order, whatever <c>EnableTemporalProcessing</c>
/// says (measured 2026-09-13 on Apple Silicon: the callback PTS sequence for a B-frame clip equals the sample
/// order with or without the flag). The decode thread therefore drains the queue into a small reorder window
/// (<see cref="ReorderDepth"/>) and publishes the SMALLEST PTS once the window is over-full — the ascending-PTS
/// contract of <see cref="FrameTimeline"/>, on which <c>FrameSelector</c>'s binary search depends. The window is
/// flushed in order at every loop boundary. <see cref="FrameTimeline.Publish"/> blocking on a full timeline paces
/// decode to real time (no further samples are submitted while blocked, so the queue is bounded by the decoder's
/// own lookahead). The clip loops by recreating the reader (a reader cannot rewind); the session is reused. The
/// loop base is carried per sample through VideoToolbox's sourceFrame token and VT is drained (FinishDelayedFrames
/// + WaitForAsynchronousFrames) at every loop boundary so PTS stay ascending across loops (ADR 0002 D1) — drift is
/// corrected by frame selection, never seeking.
///
/// Lifetime / fence: a published frame OWNS its CVPixelBuffer, the CVMetalTexture the cache vended for it and
/// the IMTLTexture wrapper. The frame's release closure — run by <see cref="DecodedFrame"/> only when the
/// timeline has passed the frame AND every command buffer that blitted it has completed (the hold-count fence) —
/// releases all three, which lets VideoToolbox's pool reuse the buffer. Every vended texture is counted in the
/// <see cref="MetalResourceTracker"/> while it is held, so the harness's tracked count sees leaked frames. The
/// cache is flushed every <see cref="CacheFlushInterval"/> frames, as Apple requires.
///
/// Only VALID frames are published: the first frame's pixel format and size are asserted against the format the
/// pass bound to (logged like the Windows first-HW-frame check) — a mismatch faults the source rather than feeding
/// the blit a wrong-sized texture. Later per-frame failures are skipped and counted; the source stops only after
/// <see cref="MaxConsecutiveFailures"/> in a row (<see cref="IsFaulted"/>). Nothing is ever thrown into the
/// render thread.
/// </summary>
public sealed class VideoToolboxSource : IMetalSource
{
    /// <summary>Consecutive decode failures before the source gives up (a burst, not one bad frame).</summary>
    private const int MaxConsecutiveFailures = 30;

    /// <summary>Decode→render handoff depth. Every buffered frame pins one of the decoder's pool buffers (33 MB
    /// each at 4K BGRA) plus up to the render side's three in-flight reads, so this stays shallow — a few frames is
    /// ample lookahead for clock-based frame selection (the Windows MF depth is 3 for the same reason).</summary>
    private const int TimelineDepth = 4;

    /// <summary>Presentation-order reorder window: a decoded frame is published only once this many LATER-decoded
    /// frames have arrived, so every B-frame that precedes it in display order is already in the window. Measured
    /// need on the fixture clips (ffmpeg defaults, 3 B-frames): 3; one of margin. A stream needing more publishes an
    /// inversion, which <see cref="FrameTimeline.Publish"/> refuses — the source faults loudly, it never feeds the
    /// selector a non-ascending timeline. Each held frame pins one decoder pool buffer, like the timeline's.</summary>
    private const int ReorderDepth = 4;

    /// <summary>Frames between <see cref="CVMetalTextureCache.Flush"/> calls (drops cache entries whose textures
    /// have been released; Apple requires periodic flushing).</summary>
    private const int CacheFlushInterval = 60;

    private static readonly TimeSpan DecodeRateLogInterval = TimeSpan.FromSeconds(2);

    private readonly ILog _log;
    private readonly MetalResourceTracker _tracker;
    private readonly AVUrlAsset _asset;
    private readonly AVAssetTrack _track;
    private readonly CMVideoFormatDescription _formatDescription;
    private readonly CVMetalTextureCache _cache;
    private readonly VTDecompressionSession _session;
    private readonly long _loopDurationTicks;

    // Callback → decode-thread handoff: frames VideoToolbox has emitted, in DECODE order, not yet reordered.
    private readonly object _decodedGate = new();
    private readonly List<PendingFrame> _decoded = new();
    // Decode thread only: decoded frames sorted by PTS, waiting for the window to prove nothing earlier is coming.
    private readonly List<PendingFrame> _reorder = new(ReorderDepth + 1);

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private volatile string _phase = "not started"; // where the decode thread is, for the Stop() line and stuck-source triage
    private long _ptsTicks;
    private long _decodedFrames;
    private int _outstanding;   // vended textures held by published frames / in-flight GPU reads

    /// <summary>One frame VideoToolbox delivered: the retained pixel buffer (null = a decode failure to count).</summary>
    private readonly record struct PendingFrame(CVPixelBuffer? Buffer, long PtsTicks, string? Failure);

    public FrameTimeline Frames { get; } = new(TimelineDepth);
    public int Width { get; }
    /// <summary>The track's nominal frame rate as AVFoundation reads it from the container (0 if absent).</summary>
    public double FrameRate { get; }
    public int Height { get; }
    public MTLPixelFormat TextureFormat => MTLPixelFormat.BGRA8Unorm;
    public bool UseYCoCg => false;

    /// <summary>True when the session reports the hardware decoder (read back from VideoToolbox, not assumed).</summary>
    public bool IsHardwareDecode { get; }

    public string DecodePath => IsHardwareDecode ? "hardware" : "software";

    /// <summary>The stream's codec, for the log.</summary>
    public CMVideoCodecType Codec { get; }

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsFaulted => _faulted;
    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));
    public long DecodedFrames => Volatile.Read(ref _decodedFrames);
    public int OutstandingAtDispose { get; private set; }

    /// <summary>Opens the clip, creates the decode session (hardware, else software) and the texture cache on
    /// <paramref name="provider"/>'s device. Caller thread: controller/harness — never the main thread.</summary>
    public VideoToolboxSource(string path, GraphicsDeviceProvider provider, ILog log, bool forceSoftware = false, string? id = null)
    {
        _log = log;
        _tracker = provider.Tracker;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Video clip not found.", path);

        _asset = new AVUrlAsset(NSUrl.FromFilename(path));
        CMTime duration;
        try
        {
            var tracks = _asset.GetTracks(AVMediaTypes.Video);
            if (tracks.Length == 0)
                throw new InvalidDataException($"'{path}' has no video track.");
            _track = tracks[0];
            for (var i = 1; i < tracks.Length; i++) tracks[i].Dispose();

            var descriptions = _track.FormatDescriptions;
            if (descriptions.Length == 0)
                throw new InvalidDataException($"'{path}' video track has no format description.");
            _formatDescription = Runtime.GetINativeObject<CMVideoFormatDescription>(descriptions[0].Handle, owns: false)
                ?? throw new InvalidDataException($"'{path}' video track format description is not a video format.");
            foreach (var d in descriptions) d.Dispose();

            Codec = _formatDescription.VideoCodecType;
            var dimensions = _formatDescription.Dimensions;
            Width = dimensions.Width;
            Height = dimensions.Height;
            FrameRate = Math.Max(0, _track.NominalFrameRate);
            if (Width <= 0 || Height <= 0)
                throw new InvalidDataException($"'{path}' declares an invalid size {Width}x{Height}.");
            duration = _track.TimeRange.Duration;
            _loopDurationTicks = Math.Max(1, ToTicks(duration));

            _session = CreateSession(forceSoftware, out var hardware);
            IsHardwareDecode = hardware;
            _cache = CVMetalTextureCache.FromDevice(provider.Device)
                ?? throw new NotSupportedException("CVMetalTextureCache creation failed.");
        }
        catch
        {
            _session?.Dispose();
            _formatDescription?.Dispose();
            _track?.Dispose();
            _asset.Dispose();
            throw;
        }

        _log.Info("Decode", $"{Id}: {Codec} {Width}x{Height} @ {FrameRate:0.###} fps, dur={duration.Seconds:0.00}s, " +
                            $"matrix={StreamMatrix()}, decode path={(IsHardwareDecode ? "hardware" : "software")} (VideoToolbox), output=BGRA8 via CVMetalTextureCache.");
    }

    /// <summary>The Windows HW→SW ladder: require the hardware decoder; on failure (or when forced) allow software
    /// and log the fallback. Which path is active is read back from the session.</summary>
    private VTDecompressionSession CreateSession(bool forceSoftware, out bool hardware)
    {
        var attributes = new CVPixelBufferAttributes { PixelFormatType = CVPixelFormatType.CV32BGRA };
        attributes.Dictionary[CVPixelBuffer.MetalCompatibilityKey] = NSNumber.FromBoolean(true);

        VTDecompressionSession? session = null;
        if (!forceSoftware)
        {
            var requireHardware = new VTVideoDecoderSpecification { RequireHardwareAcceleratedVideoDecoder = true };
            session = VTDecompressionSession.Create(OnFrameDecoded, _formatDescription, requireHardware, attributes);
            if (session is null)
                _log.Error("Decode", $"{Id}: hardware decode unavailable for {Codec} {Width}x{Height}; falling back to VideoToolbox software decode.");
        }
        else
            _log.Info("Decode", $"{Id}: software decode forced.");

        if (session is null)
        {
            var allowSoftware = new VTVideoDecoderSpecification { EnableHardwareAcceleratedVideoDecoder = false };
            session = VTDecompressionSession.Create(OnFrameDecoded, _formatDescription, allowSoftware, attributes)
                ?? throw new NotSupportedException($"VideoToolbox cannot decode {Codec} {Width}x{Height} in hardware or software.");
        }

        using var usingHardware = session.GetProperty(VTDecompressionPropertyKey.UsingHardwareAcceleratedVideoDecoder) as NSNumber;
        hardware = usingHardware?.BoolValue ?? false;
        return session;
    }

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Source already started.");
        if (_stop)
            throw new InvalidOperationException("Source cannot be restarted after Stop — create a new source.");
        _thread = new Thread(DecodeLoop) { Name = $"MultiMon.Decode.{Id}", IsBackground = true };
        _thread.Start();
        _log.Info("Decode", $"{Id}: VideoToolbox decode thread started.");
    }

    /// <summary>Signals the loop, releases it from a full timeline, and joins. Off the main thread (the V0087 rule).
    /// The thread drains VideoToolbox before exiting, so no callback fires after this returns.</summary>
    public void Stop()
    {
        _stop = true;
        Frames.SignalStop();
        var thread = _thread;
        if (thread is not null)
            _log.Info("Decode", $"{Id}: stop requested while {_phase}, decoded={DecodedFrames}, pts={CurrentPts.TotalSeconds:0.000}s.");
        thread?.Join();
        _thread = null;
    }

    private void DecodeLoop()
    {
        var consecutiveFailures = 0;
        var rateClock = Stopwatch.StartNew();
        long rateFrames = 0;
        var flushCountdown = CacheFlushInterval;
        var firstFrameChecked = false;
        try
        {
            long loopBaseTicks = 0;
            while (!_stop)
            {
                using var loopPool = new NSAutoreleasePool();
                using var reader = new AVAssetReader(_asset, out var error);
                if (error is not null)
                    throw new InvalidOperationException($"AVAssetReader: {error.LocalizedDescription}");
                using var output = new AVAssetReaderTrackOutput(_track, (NSDictionary?)null!) { AlwaysCopiesSampleData = false };
                if (!reader.CanAddOutput(output))
                    throw new InvalidOperationException("AVAssetReader rejected the compressed video output.");
                reader.AddOutput(output);
                if (!reader.StartReading())
                    throw new InvalidOperationException($"AVAssetReader.StartReading failed: {reader.Error?.LocalizedDescription}");

                var sampleIndex = 0;
                try
                {
                    while (!_stop)
                    {
                        using var pool = new NSAutoreleasePool(); // sample buffers, the frame's wrappers (LESSON-BUG-008)
                        _phase = "read-sample";
                        using var sample = output.CopyNextSampleBuffer();
                        if (sample is null)
                        {
                            if (reader.Status == AVAssetReaderStatus.Failed)
                                throw new InvalidOperationException($"AVAssetReader failed mid-clip: {reader.Error?.LocalizedDescription}");
                            break; // end of stream → loop
                        }
                        if (sample.NumSamples == 0)
                            continue; // a timing-only marker (edit-list gap), not a frame — VideoToolbox rejects it as a parameter error

                        _phase = "decode-frame";
                        var status = _session.DecodeFrame(sample, VTDecodeFrameFlags.EnableAsynchronousDecompression | VTDecodeFrameFlags.EnableTemporalProcessing,
                            (IntPtr)loopBaseTicks, out _);
                        if (status != VTStatus.Ok)
                            Enqueue(new PendingFrame(null, 0, $"DecodeFrame returned {status} for sample #{sampleIndex} pts={sample.PresentationTimeStamp.Seconds:0.000}s bytes={sample.TotalSampleSize}"));
                        sampleIndex++;
                        if (!Drain(flush: false, ref consecutiveFailures, ref firstFrameChecked, ref rateFrames, rateClock, ref flushCountdown))
                            return;
                    }
                }
                finally
                {
                    reader.CancelReading(); // a no-op once the reader completed; required before releasing a reader mid-clip
                }

                // Loop boundary: everything VideoToolbox still holds belongs to THIS loop base — emit and publish it
                // before the next reader starts at PTS 0 (temporal reordering would otherwise interleave the loops).
                _phase = "loop-boundary-drain";
                _session.FinishDelayedFrames();
                _session.WaitForAsynchronousFrames();
                if (!Drain(flush: true, ref consecutiveFailures, ref firstFrameChecked, ref rateFrames, rateClock, ref flushCountdown))
                    return;
                loopBaseTicks += _loopDurationTicks;
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error("Decode", $"{Id}: VideoToolbox decode loop ended on exception: {ex}");
        }
        finally
        {
            // Quiesce VideoToolbox on THIS thread so no callback can enqueue after Stop returns, then release every
            // buffer it emitted meanwhile — nothing is published after the loop ends (stopped or faulted).
            _phase = "exit-drain";
            _session.WaitForAsynchronousFrames();
            DiscardQueued();
            _phase = "exited";
        }
    }

    private void DiscardQueued()
    {
        lock (_decodedGate)
        {
            foreach (var pending in _decoded)
                pending.Buffer?.Dispose();
            _decoded.Clear();
        }
        foreach (var pending in _reorder)
            pending.Buffer!.Dispose();
        _reorder.Clear();
    }

    /// <summary>VideoToolbox's callback thread: retain the buffer and hand it to the decode thread. Nothing else —
    /// never block here (VideoToolbox holds its own locks while calling out).</summary>
    private void OnFrameDecoded(IntPtr sourceFrame, VTStatus status, VTDecodeInfoFlags flags, CVImageBuffer? buffer, CMTime pts, CMTime duration)
    {
        if (status != VTStatus.Ok || buffer is null)
        {
            Enqueue(new PendingFrame(null, 0, $"decode callback status {status}{(flags.HasFlag(VTDecodeInfoFlags.FrameDropped) ? " (frame dropped)" : "")}"));
            return;
        }
        // The binding disposes ITS wrapper when this returns; this one is our own retain, released by the frame.
        var owned = Runtime.GetINativeObject<CVPixelBuffer>(buffer.Handle, owns: false)!;
        Enqueue(new PendingFrame(owned, (long)sourceFrame + ToTicks(pts), null));
    }

    private void Enqueue(PendingFrame frame)
    {
        lock (_decodedGate) _decoded.Add(frame);
    }

    /// <summary>Decode thread: take every frame VideoToolbox has emitted, reorder it into presentation order and
    /// publish what the window has proved complete — everything when <paramref name="flush"/> (loop boundary, after
    /// VT was drained). Returns false when the source faulted (the caller exits the loop).</summary>
    private bool Drain(bool flush, ref int consecutiveFailures, ref bool firstFrameChecked, ref long rateFrames, Stopwatch rateClock, ref int flushCountdown)
    {
        while (true)
        {
            PendingFrame pending;
            lock (_decodedGate)
            {
                if (_decoded.Count == 0) break;
                pending = _decoded[0];
                _decoded.RemoveAt(0);
            }
            if (pending.Buffer is null)
            {
                // A failure carries no PTS and holds nothing: count it now, it does not enter the window.
                if (!CountFailure(pending.Failure!, decodedFrame: false, ref consecutiveFailures, firstFrameChecked))
                    return false;
                continue;
            }
            // Sorted insert (the window is at most ReorderDepth + 1 long): scan back from the end.
            var at = _reorder.Count;
            while (at > 0 && _reorder[at - 1].PtsTicks > pending.PtsTicks) at--;
            _reorder.Insert(at, pending);
        }

        while (_reorder.Count > ReorderDepth || flush && _reorder.Count > 0)
        {
            var next = _reorder[0];
            _reorder.RemoveAt(0);
            if (!PublishFrame(next, ref consecutiveFailures, ref firstFrameChecked, ref rateFrames, rateClock, ref flushCountdown))
                return false;
        }
        return true;
    }

    /// <summary>Wraps one decoded buffer through the texture cache and publishes it. Returns false when the source
    /// faulted.</summary>
    private bool PublishFrame(PendingFrame pending, ref int consecutiveFailures, ref bool firstFrameChecked, ref long rateFrames, Stopwatch rateClock, ref int flushCountdown)
    {
        var buffer = pending.Buffer!;
        CVMetalTexture? metalTexture = null;
        IMTLTexture? texture = null;
        var failure = Validate(buffer, ref firstFrameChecked);
        if (failure is null)
        {
            metalTexture = _cache.TextureFromImage(buffer, TextureFormat, Width, Height, 0, out var cvStatus);
            texture = metalTexture?.Texture;
            if (texture is null)
                failure = $"CVMetalTextureCache could not wrap the frame ({cvStatus})";
        }

        if (failure is not null)
        {
            texture?.Dispose();
            metalTexture?.Dispose();
            buffer.Dispose();
            return CountFailure(failure, decodedFrame: true, ref consecutiveFailures, firstFrameChecked);
        }
        consecutiveFailures = 0;

        _tracker.TextureCreated();
        Interlocked.Increment(ref _outstanding);
        Volatile.Write(ref _ptsTicks, pending.PtsTicks);
        Interlocked.Increment(ref _decodedFrames);
        // The frame OWNS buffer + cache texture + wrapper; its release (timeline passed it + GPU reads completed)
        // frees them exactly once, on whichever thread drops the last hold.
        var owner = (Buffer: buffer, Metal: metalTexture!, Texture: texture!);
        _phase = "publish";
        Frames.Publish(new DecodedFrame(texture!, TimeSpan.FromTicks(pending.PtsTicks), () => ReleaseFrame(owner.Texture, owner.Metal, owner.Buffer)));

        if (--flushCountdown == 0)
        {
            _cache.Flush(CVOptionFlags.None);
            flushCountdown = CacheFlushInterval;
        }
        rateFrames++;
        if (rateClock.Elapsed >= DecodeRateLogInterval)
        {
            _log.Info("Decode", $"{Id}: decoded {rateFrames} frames in {rateClock.Elapsed.TotalSeconds:0.0}s " +
                                $"({rateFrames / rateClock.Elapsed.TotalSeconds:0.0} fps, {(IsHardwareDecode ? "hardware" : "software")}), pts={CurrentPts.TotalSeconds:0.00}s.");
            rateFrames = 0;
            rateClock.Restart();
        }
        return true;
    }

    /// <summary>Counts one skipped frame (its buffer, if any, is already released). Returns false — the source faulted —
    /// after a burst, or when the FIRST decoded frame (<paramref name="decodedFrame"/>) is one the pass cannot bind to
    /// (format/size); a decode status failure before any frame is only counted.</summary>
    private bool CountFailure(string failure, bool decodedFrame, ref int consecutiveFailures, bool firstFrameChecked)
    {
        consecutiveFailures++;
        if (consecutiveFailures == 1 || consecutiveFailures == MaxConsecutiveFailures)
            _log.Error("Decode", $"{Id}: decode failure ({failure}); {consecutiveFailures} consecutive failure(s).");
        if (consecutiveFailures < MaxConsecutiveFailures && (firstFrameChecked || !decodedFrame))
            return true;
        _faulted = true;
        _log.Error("Decode", $"{Id}: stopping decode ({(consecutiveFailures >= MaxConsecutiveFailures ? $"{MaxConsecutiveFailures} consecutive failures" : $"first frame unusable: {failure}")}).");
        return false;
    }

    /// <summary>The first-frame format/size assertion (the Windows first-HW-frame check): every published texture must
    /// be exactly what the pass bound to. Returns the rejection, or null when the frame is valid.</summary>
    private string? Validate(CVPixelBuffer buffer, ref bool firstFrameChecked)
    {
        var format = buffer.PixelFormatType;
        var width = (int)buffer.Width;
        var height = (int)buffer.Height;
        var ok = format == CVPixelFormatType.CV32BGRA && width == Width && height == Height;
        if (!firstFrameChecked)
        {
            _log.Info("Decode", $"{Id}: first frame format={format} {width}x{height} (bound {TextureFormat} {Width}x{Height}), " +
                                $"matrix={BufferMatrix(buffer)}, {(IsHardwareDecode ? "hardware" : "software")} decode.");
            if (ok) firstFrameChecked = true;
        }
        return ok ? null : $"frame is {format} {width}x{height}, the pass bound {TextureFormat} {Width}x{Height}";
    }

    private void ReleaseFrame(IMTLTexture texture, CVMetalTexture metalTexture, CVPixelBuffer buffer)
    {
        texture.Dispose();
        metalTexture.Dispose();
        buffer.Dispose();
        _tracker.TextureDisposed();
        Interlocked.Decrement(ref _outstanding);
    }

    /// <summary>The YCbCr matrix tagged on the stream (what VideoToolbox converts with), or "untagged".</summary>
    private string StreamMatrix()
    {
        using var matrix = _formatDescription.GetExtension(CVImageBuffer.YCbCrMatrixKey);
        return matrix?.ToString() ?? "untagged";
    }

    /// <summary>The matrix attachment propagated onto a decoded buffer (the one VideoToolbox actually applied).</summary>
    private static string BufferMatrix(CVPixelBuffer buffer)
    {
        using var matrix = buffer.GetAttachment<NSString>(CVImageBuffer.YCbCrMatrixKey, out _);
        return matrix?.ToString() ?? "untagged";
    }

    private static long ToTicks(CMTime time) =>
        time.IsInvalid || time.TimeScale == 0 ? 0 : time.Value * TimeSpan.TicksPerSecond / time.TimeScale;

    /// <summary>Stop → dispose the timeline (its frames release their buffers) → session, cache, asset. PRECONDITION:
    /// the render side has unbound this source and its in-flight command buffers have completed.</summary>
    public void Dispose()
    {
        Stop();
        Frames.Dispose();
        OutstandingAtDispose = Volatile.Read(ref _outstanding);
        if (OutstandingAtDispose > 0)
            _log.Error("Decode", $"{Id}: {OutstandingAtDispose} decoded frame(s) still held by in-flight GPU work at dispose — freed on completion (teardown-order violation).");
        _session.Dispose();   // invalidates the session
        _cache.Flush(CVOptionFlags.None);
        _cache.Dispose();
        _formatDescription.Dispose();
        _track.Dispose();
        _asset.Dispose();
    }
}
