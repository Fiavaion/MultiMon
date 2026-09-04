using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using MF = Vortice.MediaFoundation.MediaFactory;

namespace MultiMon.Decode.MediaFoundation;

/// <summary>
/// Decodes one clip with an <see cref="IMFSourceReader"/> on its OWN thread (off the UI and render
/// threads) and publishes the latest frame into a <see cref="FrameTimeline"/> for the render thread.
/// Output is BGRA (MF RGB32): on the hardware path the reader's D3D11 video processor writes the
/// frame straight into a texture on our shared device; on the software fallback it produces a CPU
/// buffer. Either way the render thread copies the payload into ONE persistent shader-resource
/// texture and samples it — a single, format-uniform output contract.
///
/// The reader, its threads, and the timeline are created ONCE and live for the session; entering
/// or leaving perform mode never touches them. At end-of-stream the clip loops by seeking the reader
/// back to zero (the supported source-reader loop) — this is NOT the forbidden "seek a live decoder
/// to correct drift" (decode-threading.md); drift is corrected by frame selection against the MasterClock.
///
/// Never crashes the show: hardware decode that fails at configure time OR at runtime before the first
/// frame reaches the render thread (an MFT that rejects the DXGI manager, an unsupported profile /
/// 10-bit stream under DXVA, MF_E_HW_MFT_FAILED_START_STREAMING, a decoder texture outside the pass's
/// B8G8R8X8 family or content size) falls back to software decode in-thread. Later per-frame failures
/// are skipped and counted; only <see cref="MaxConsecutiveFailures"/> in a row stop the source
/// (<see cref="IsFaulted"/>). Only VALID frames are published: the software path copies exactly
/// Width×4 × Height bytes out of the locked MF buffer after bounding every row against it.
/// </summary>
public sealed class MediaFoundationSource : ISource
{
    private readonly ILog _log;
    private readonly string _path;

    // 30fps fallback when a sample reports no duration — used only to advance the loop base by ~1 frame.
    private const long DefaultFrameTicks = TimeSpan.TicksPerSecond / 30;

    /// <summary>Consecutive decode failures (after the first published frame) before the source gives up.</summary>
    private const int MaxConsecutiveFailures = 30;

    // Transient hardware-decoder HRESULTs: the sample-allocator pool is momentarily exhausted because WE
    // hold the buffered frames' textures (FrameTimeline). MF documents these as "release a sample and retry",
    // not a decode failure — see DecodeLoop.
    private const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);
    private const int MF_E_SAMPLEALLOCATOR_WAITING = unchecked((int)0xC00D4A3F);

    private IMFSourceReader? _reader;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private bool _allocatorEmptyLogged;     // decode-thread only; throttles the transient-retry log to once per burst
    private long _ptsTicks;
    private long _loopBaseTicks;        // added to in-clip sample time so PTS ascend across loops
    private long _lastSampleEndTicks;   // in-clip end (time+duration) of the last sample this loop
    private long _clipDurationTicks;    // clip length (0 = unknown); used to position on device-loss resume
    private bool _softwareFallback;
    private bool _publishedAny;         // decode-thread only (reset while stopped): gates the in-thread HW→SW fallback
    private bool _hwFormatChecked;      // decode-thread only (reset while stopped): first-HW-frame format/size assertion

    // Geometry of the reader's CURRENT output type. Width/Height are the CONTENT size (the display
    // aperture when the decoder reports a larger coded frame, e.g. 1080 rows inside a 1088-row buffer)
    // and never change once the pass has bound to them; the coded size / aperture offset / default stride
    // are per-configuration details the software copy uses to lift the content rows out of the buffer.
    private int _codedWidth, _codedHeight;
    private int _apertureX, _apertureY;
    private int _defaultStride;         // MF_MT_DEFAULT_STRIDE if reported (may be negative = bottom-up), else 0

    /// <summary>Decode→render handoff depth. Hardware decode hands us the decoder's OWN pool textures
    /// (<see cref="PublishHardware"/> holds each <c>IMFSample</c> alive), so this depth = how many of that
    /// finite per-source output pool we pin at once. A pause freezes a full depth's worth; at depth 8 that
    /// exhausted the pool on resume (MF_E_SAMPLEALLOCATOR_EMPTY → frozen outputs, even with the retry). Keep
    /// it shallow so the decoder always has free output samples — a few frames is ample lookahead for
    /// clock-based frame selection. (Software/HAP copy pixels out and don't pin pool textures.)</summary>
    private const int TimelineDepth = 3;

    /// <summary>The decode→render handoff. Owned here; the render-side pass borrows from it.</summary>
    public FrameTimeline Frames { get; } = new(TimelineDepth);

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>True once the decode loop has stopped on its own (a burst of consecutive failures, or an
    /// unrecoverable reader error) — the output keeps its last frame; a consumer may poll this and rebuild.</summary>
    public bool IsFaulted => _faulted;

    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));

    private enum Step { Published, Skipped, Failed }

    /// <param name="device">The shared device MF is bound to through <paramref name="mf"/>; the reader
    /// reaches it via the MF device manager, so the source keeps no device reference of its own.</param>
    public MediaFoundationSource(ID3D11Device device, MfDeviceManager mf, string path, ILog log, string? id = null)
    {
        _log = log;
        _path = path;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Source clip not found.", path);

        // Hardware first (if MF bound to our device); fall back to software decode on any failure.
        if (mf.HardwareBound && TryConfigure(mf.Manager))
            _softwareFallback = false;
        else if (TryConfigure(null))
            _softwareFallback = true;
        else
            throw new InvalidOperationException($"Could not open '{path}' for decode (hardware or software).");

        _clipDurationTicks = QueryDurationTicks();
        _log.Info("Decode", $"{Id}: opened {Width}x{Height} (coded {_codedWidth}x{_codedHeight}, aperture +{_apertureX},+{_apertureY}, stride {_defaultStride}), " +
                            $"path={(_softwareFallback ? "software" : "hardware")} decode, " +
                            $"duration={(_clipDurationTicks > 0 ? TimeSpan.FromTicks(_clipDurationTicks).TotalSeconds.ToString("0.00") + "s" : "unknown")}.");
    }

    /// <summary>Clip duration in 100ns ticks from the media source, or 0 if unavailable.</summary>
    private long QueryDurationTicks()
    {
        try
        {
            var variant = _reader!.GetPresentationAttribute(SourceReaderIndex.MediaSource,
                PresentationDescriptionAttributeKeys.Duration);
            var ticks = Convert.ToInt64(variant.Value);
            return ticks > 0 ? ticks : 0;
        }
        catch (Exception ex)
        {
            _log.Info("Decode", $"{Id}: clip duration unavailable ({ex.Message}); device-loss resume will restart the clip on the live timeline.");
            return 0;
        }
    }

    /// <summary>
    /// Builds a source reader configured for BGRA output and records its output geometry. Returns false
    /// (and tidies up) on failure — including a reader whose content size differs from the one the pass
    /// already bound to (the persistent source texture cannot be resized from here).
    /// </summary>
    private bool TryConfigure(IMFDXGIDeviceManager? deviceManager)
    {
        IMFSourceReader? reader = null;
        try
        {
            using var attributes = MF.MFCreateAttributes(2);
            if (deviceManager is not null)
            {
                attributes.Set(SourceReaderAttributeKeys.D3DManager, deviceManager);
                attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            }
            else
            {
                attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            }

            reader = MF.MFCreateSourceReaderFromURL(_path, attributes);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using (var outputType = MF.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
            }

            int codedWidth, codedHeight, apertureX = 0, apertureY = 0, width, height, stride = 0;
            using (var actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                MF.MFGetAttributeSize(actualType, MediaTypeAttributeKeys.FrameSize, out var w, out var h).CheckError();
                codedWidth = (int)w;
                codedHeight = (int)h;
                width = codedWidth;
                height = codedHeight;
                if (TryReadAperture(actualType, out var ax, out var ay, out var aw, out var ah) &&
                    ax >= 0 && ay >= 0 && aw > 0 && ah > 0 && ax + aw <= codedWidth && ay + ah <= codedHeight)
                {
                    apertureX = ax;
                    apertureY = ay;
                    width = aw;
                    height = ah;
                }
                if (actualType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var s).Success)
                    stride = unchecked((int)s);
            }
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"reader reports an invalid frame size {width}x{height}");
            if (Width != 0 && (width != Width || height != Height))
                throw new InvalidOperationException($"reader reports {width}x{height} but the output is bound to {Width}x{Height}");

            Width = width;
            Height = height;
            _codedWidth = codedWidth;
            _codedHeight = codedHeight;
            _apertureX = apertureX;
            _apertureY = apertureY;
            _defaultStride = stride;
            _reader = reader;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Decode", $"{Id}: reader config failed ({(deviceManager is null ? "software" : "hardware")}): {ex.Message}");
            reader?.Dispose();
            return false;
        }
    }

    /// <summary>MF_MT_MINIMUM_DISPLAY_APERTURE as an MFVideoArea blob: OffsetX{fract u16, value i16}, OffsetY, SIZE{cx, cy}.</summary>
    private static bool TryReadAperture(IMFMediaType type, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!type.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out var size).Success || size < 16)
            return false;
        var blob = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
        x = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(2));
        y = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6));
        width = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8));
        height = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12));
        return true;
    }

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Source already started.");
        if (_stop)
            throw new InvalidOperationException("Source cannot be restarted after Stop — create a new source.");
        _stop = false;
        _thread = new Thread(DecodeLoop) { Name = $"MultiMon.Decode.{Id}", IsBackground = true };
        _thread.Start();
        _log.Info("Decode", $"{Id}: decode thread started.");
    }

    public void Stop()
    {
        _stop = true;
        Frames.SignalStop(); // release the decode thread if it is blocked publishing into a full timeline
        var thread = _thread;
        thread?.Join();
        _thread = null;
    }

    /// <summary>
    /// Device-removed recovery, step 1 (render thread). Stops the decode thread, disposes the reader
    /// (which holds the dying device's decoder-pool textures), and flushes the timeline. Unlike
    /// <see cref="Stop"/> it does NOT permanently retire the source — <see cref="RebindDevice"/> rebuilds
    /// it. Decode-threading rule: stop the producer before its resources are torn down.
    /// </summary>
    public void QuiesceForDeviceLoss()
    {
        _stop = true;
        Frames.SignalStop();
        _thread?.Join();
        _thread = null;
        _reader?.Dispose();
        _reader = null;
        Frames.Reset(); // dispose buffered (dead-device) frames and re-arm for republishing
    }

    /// <summary>
    /// Device-removed recovery, step 2 (render thread, AFTER the new device exists). Rebuilds the source
    /// reader against the rebound MF device manager and POSITIONS it to <paramref name="resumeMediaTime"/>
    /// — never seeks a live decoder (decode-threading.md): the old reader was disposed in
    /// <see cref="QuiesceForDeviceLoss"/> and this builds a fresh one, then seeks it before any frame is
    /// read. Leaves the source stopped; the caller restarts it with <see cref="Start"/>.
    /// Returns false (logged, <see cref="IsFaulted"/>) when the clip cannot be reopened at all — the
    /// source then stays stopped and its output keeps the last frame; the caller must not Start it.
    /// </summary>
    public bool RebindDevice(ID3D11Device device, MfDeviceManager mf, TimeSpan resumeMediaTime)
    {
        if (_thread is not null)
            throw new InvalidOperationException("RebindDevice requires the decode thread stopped — call QuiesceForDeviceLoss first.");

        if (mf.HardwareBound && TryConfigure(mf.Manager))
            _softwareFallback = false;
        else if (TryConfigure(null))
            _softwareFallback = true;
        else
        {
            _faulted = true;
            _log.Error("Decode", $"{Id}: could not reopen '{_path}' after device loss (hardware or software); output keeps its last frame.");
            return false;
        }

        PositionTo(resumeMediaTime.Ticks);
        // _stop is volatile; PositionTo's loop-base/position writes are plain longs, but the caller's
        // following Start() creates a NEW thread, and Thread.Start is a publication fence — so the fresh
        // decode thread is guaranteed to observe these writes. Do not read them from another live thread.
        _stop = false; // re-arm so Start() can spin a fresh decode thread
        _faulted = false;
        _publishedAny = false;
        _hwFormatChecked = false;
        _log.Info("Decode", $"{Id}: rebound to recreated device ({(_softwareFallback ? "software" : "hardware")}), resumed at {resumeMediaTime.TotalSeconds:0.000}s.");
        return true;
    }

    /// <summary>
    /// Positions the freshly-built reader so its produced PTS stay on the MasterClock timeline. With a
    /// known clip duration the in-clip offset is the resume time modulo the duration (preserving content
    /// continuity across loops); otherwise the clip restarts from 0 on the live global timeline.
    /// </summary>
    private void PositionTo(long resumeTicks)
    {
        long inClip;
        if (_clipDurationTicks > 0)
        {
            var loops = resumeTicks / _clipDurationTicks;
            inClip = resumeTicks - loops * _clipDurationTicks;
            _loopBaseTicks = loops * _clipDurationTicks; // so globalPts = loopBase + inClip == resumeTicks
        }
        else
        {
            inClip = 0;
            _loopBaseTicks = resumeTicks; // first frame's globalPts == resumeTicks (clip restarts visually)
        }
        _lastSampleEndTicks = 0;            // overwritten by the first decoded sample of this loop
        Volatile.Write(ref _ptsTicks, resumeTicks);
        _reader!.SetCurrentPosition(inClip); // MF position is in 100ns units, same as our ticks
    }

    private void DecodeLoop()
    {
        try
        {
            DecodeUntilStopped();
        }
        catch (Exception ex)
        {
            // Last line of defence: an unhandled exception on this thread would take the PROCESS down.
            _faulted = true;
            _log.Error("Decode", $"{Id}: decode loop ended on exception: {ex}");
        }
    }

    private void DecodeUntilStopped()
    {
        var framesThisLoop = 0;
        var consecutiveFailures = 0;
        while (!_stop)
        {
            Step step;
            string? failure;
            try
            {
                step = DecodeOne(ref framesThisLoop, out failure);
            }
            catch (SharpGenException ex) when (
                ex.ResultCode.Code is MF_E_SAMPLEALLOCATOR_EMPTY or MF_E_SAMPLEALLOCATOR_WAITING)
            {
                // The HW decoder's sample-allocator pool is MOMENTARILY exhausted: every buffered frame
                // we hold (PublishHardware keeps the IMFSample/decoder texture alive in FrameTimeline)
                // pins a pool texture. The timeline sits full through a pause, so on resume ReadSample can
                // beat the render thread to a free texture. MF documents this HRESULT as transient — back
                // off and retry; the render thread releases a consumed frame's texture within a few ms.
                // This is NOT a Sleep masking a wedge (the ownership is fully understood, like
                // FrameTimeline.Publish's bounded Monitor.Wait) — dying here froze the output until
                // teardown (the multi-output pause-resume freeze, MF_E_SAMPLEALLOCATOR_EMPTY on the rig).
                if (!_allocatorEmptyLogged)
                {
                    _log.Info("Decode", $"{Id}: HW sample allocator momentarily empty (buffered frames pinned) — backing off, retrying.");
                    _allocatorEmptyLogged = true;
                }
                Thread.Sleep(3); // brief; the while(!_stop) re-check bounds teardown latency to ~3ms
                continue;
            }
            catch (Exception ex)
            {
                step = Step.Failed;
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            _allocatorEmptyLogged = false;

            switch (step)
            {
                case Step.Published:
                    consecutiveFailures = 0;
                    break;
                case Step.Skipped:
                    break;
                case Step.Failed:
                    if (!_softwareFallback && !_publishedAny)
                    {
                        // Hardware decode broke before a single frame reached the render thread (an MFT
                        // rejecting the DXGI manager, an unsupported profile under DXVA, a texture the pass
                        // can't copy, ...): switch this reader to software decode in-thread and carry on.
                        if (TrySwitchToSoftware(failure!))
                        {
                            framesThisLoop = 0;
                            consecutiveFailures = 0;
                            continue;
                        }
                        return;
                    }
                    consecutiveFailures++;
                    if (consecutiveFailures == 1)
                        _log.Error("Decode", $"{Id}: decode failure ({failure}); skipping and continuing.");
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _faulted = true;
                        _log.Error("Decode", $"{Id}: {MaxConsecutiveFailures} consecutive decode failures (last: {failure}); stopping decode.");
                        return;
                    }
                    break;
            }
        }
    }

    /// <summary>One reader step: read a sample and publish it. Throws on native failure (the loop classifies).</summary>
    private Step DecodeOne(ref int framesThisLoop, out string? failure)
    {
        failure = null;
        var sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream,
            SourceReaderControlFlag.None, out _, out var flags, out var timestamp);

        if ((flags & SourceReaderFlag.Error) != 0)
        {
            sample?.Dispose();
            failure = "source reader reported an error flag";
            return Step.Failed;
        }

        if ((flags & SourceReaderFlag.EndOfStream) != 0)
        {
            sample?.Dispose();
            // A clip that yields ZERO frames before EOS would spin SetCurrentPosition(0) forever
            // (burning CPU, never presenting) — report it as a failure instead of wedging.
            if (framesThisLoop == 0)
            {
                failure = "clip produced no frames before end-of-stream";
                return Step.Failed;
            }
            // Advance the loop base by the clip length seen, so the next loop's PTS continue
            // ascending on the same timeline as the MasterClock (ADR 0002 D1 looping).
            _loopBaseTicks += _lastSampleEndTicks;
            _lastSampleEndTicks = 0;
            framesThisLoop = 0;
            _reader.SetCurrentPosition(0); // loop the clip (supported source-reader seek)
            return Step.Skipped;
        }

        if (sample is null)
            return Step.Skipped; // stream tick without data — keep reading

        long duration;
        try
        {
            duration = sample.SampleDuration > 0 ? sample.SampleDuration : DefaultFrameTicks;
        }
        catch
        {
            sample.Dispose();
            throw;
        }

        var globalPts = _loopBaseTicks + timestamp;
        var published = _softwareFallback
            ? PublishSoftware(sample, globalPts, out failure)
            : PublishHardware(sample, globalPts, out failure);
        if (!published)
            return Step.Failed;

        framesThisLoop++;
        _lastSampleEndTicks = timestamp + duration;
        Volatile.Write(ref _ptsTicks, globalPts);
        _publishedAny = true;
        return Step.Published;
    }

    /// <summary>
    /// Decode thread: replace the hardware reader with a software one at the same media position. Only
    /// valid before the first published frame (nothing buffered, geometry re-checked by TryConfigure).
    /// </summary>
    private bool TrySwitchToSoftware(string reason)
    {
        _log.Error("Decode", $"{Id}: hardware decode failed before the first frame ({reason}); falling back to Media Foundation software decode.");
        var resumeTicks = Volatile.Read(ref _ptsTicks);
        _reader?.Dispose();
        _reader = null;
        if (!TryConfigure(null))
        {
            _faulted = true;
            _log.Error("Decode", $"{Id}: software fallback could not open the clip; stopping decode (output stays black).");
            return false;
        }
        _softwareFallback = true;
        try
        {
            PositionTo(resumeTicks);
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error("Decode", $"{Id}: software fallback could not position the clip ({ex.Message}); stopping decode.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Hardware: hand the decoder's D3D11 texture to the render thread, holding the MF sample alive. The
    /// first frame's texture is asserted against what the pass copies into (LESSON-BUG-004: a copy across
    /// DXGI typeless families, or a texture larger than the bound Width×Height, silently no-ops — black
    /// output with a green harness). A mismatch rejects the frame so the loop falls back to software.
    /// Always consumes <paramref name="sample"/>.
    /// </summary>
    private bool PublishHardware(IMFSample sample, long pts100ns, out string? failure)
    {
        failure = null;
        IMFMediaBuffer? buffer = null;
        IMFDXGIBuffer? dxgi = null;
        ID3D11Texture2D? texture = null;
        var handedOff = false; // true once a DecodedFrame owns the refs (its release closure frees them)
        try
        {
            buffer = sample.GetBufferByIndex(0);
            dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi is null)
            {
                // The reader did not return a D3D-backed buffer despite the device manager — degrade this
                // frame to a CPU copy rather than crash. (Rare; usually a one-off during stream start.)
                buffer.Dispose();
                buffer = null;
                handedOff = true; // PublishSoftware consumes the sample
                return PublishSoftware(sample, pts100ns, out failure);
            }

            texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
            var subresource = dxgi.SubresourceIndex;

            if (!_hwFormatChecked)
            {
                _hwFormatChecked = true;
                var d = texture.Description;
                _log.Info("Decode", $"{Id}: first HW frame texture format={d.Format} {d.Width}x{d.Height} array={d.ArraySize} bind={d.BindFlags} sub={subresource}");
                if (d.Format is not (Format.B8G8R8X8_UNorm or Format.B8G8R8X8_Typeless or Format.B8G8R8X8_UNorm_SRgb))
                    failure = $"decoder texture format {d.Format} is outside the B8G8R8X8 family the render pass copies into";
                else if (d.Width != (uint)Width || d.Height != (uint)Height)
                    failure = $"decoder texture is {d.Width}x{d.Height} but the output is bound to {Width}x{Height} (coded size / aperture mismatch)";
                if (failure is not null)
                {
                    _log.Error("Decode", $"{Id}: {failure}.");
                    return false; // finally releases everything; the loop switches to software
                }
            }

            // The frame OWNS these refs; releasing it returns the texture to the decoder pool. Captured as
            // fresh locals so the closure never sees the nulled-out tracking variables below.
            var (t, x, b, s) = (texture, dxgi, buffer, sample);
            var frame = new DecodedFrame(t, subresource, TimeSpan.FromTicks(pts100ns), () =>
            {
                t.Dispose();
                x.Dispose();
                b.Dispose();
                s.Dispose();
            });
            handedOff = true;
            Frames.Publish(frame);
            return true;
        }
        finally
        {
            if (!handedOff)
            {
                texture?.Dispose();
                dxgi?.Dispose();
                buffer?.Dispose();
                sample.Dispose();
            }
        }
    }

    /// <summary>
    /// Software fallback: lift the content rows out of the MF buffer into a pooled, tightly packed BGRA
    /// buffer (row pitch = Width×4, the pass's upload contract) so the sample can be released at once.
    /// Uses the real 2D stride (IMF2DBuffer2.Lock2DSize; negative = bottom-up, flipped here) and the
    /// display aperture, and bounds every row against the locked buffer — a short or malformed buffer is
    /// rejected (logged by the loop), never published. Always consumes <paramref name="sample"/>.
    /// </summary>
    private bool PublishSoftware(IMFSample sample, long pts100ns, out string? failure)
    {
        var length = Width * 4 * Height;
        var buffer = sample.ConvertToContiguousBuffer();
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!TryCopyContentRows(buffer, pixels, out failure))
                return false;

            var owned = pixels;
            pixels = null!;
            // The frame OWNS the rented array; DecodedFrame.Dispose runs this exactly once.
            Frames.Publish(new DecodedFrame(owned.AsMemory(0, length), Width * 4, TimeSpan.FromTicks(pts100ns),
                () => ArrayPool<byte>.Shared.Return(owned)));
            return true;
        }
        finally
        {
            if (pixels is not null)
                ArrayPool<byte>.Shared.Return(pixels);
            buffer.Dispose();
            sample.Dispose();
        }
    }

    private bool TryCopyContentRows(IMFMediaBuffer buffer, byte[] destination, out string? failure)
    {
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer2>();
        if (buffer2D is not null)
        {
            buffer2D.Lock2DSize(Buffer2DLockFlags.Read, out var scanline0, out var pitch, out var bufferStart, out var bufferLength);
            try
            {
                return TryCopyRows(scanline0, pitch, bufferStart, bufferLength, destination, out failure);
            }
            finally
            {
                buffer2D.Unlock2D();
            }
        }

        // No 2D interface (a plain system-memory buffer): rows are packed at the media type's default
        // stride; a negative stride means bottom-up, whose scanline 0 is the LAST |stride| bytes.
        buffer.Lock(out var data, out _, out var currentLength);
        try
        {
            var pitch = _defaultStride != 0 ? _defaultStride : _codedWidth * 4;
            var scanline0 = pitch < 0 ? data + (nint)((long)(_codedHeight - 1) * -pitch) : data;
            return TryCopyRows(scanline0, pitch, data, currentLength, destination, out failure);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>Copies Height rows of Width×4 bytes (from the aperture offset) into <paramref name="destination"/>
    /// top-down, after proving every source row lies inside [bufferStart, bufferStart+bufferLength).</summary>
    private bool TryCopyRows(IntPtr scanline0, int pitch, IntPtr bufferStart, int bufferLength, byte[] destination, out string? failure)
    {
        var rowBytes = Width * 4;
        if (Math.Abs(pitch) < (_apertureX + Width) * 4)
        {
            failure = $"buffer pitch {pitch} cannot hold {Width}-pixel rows at aperture offset {_apertureX}";
            return false;
        }
        var firstRow = (long)scanline0 + (long)_apertureY * pitch + (long)_apertureX * 4;
        var lastRow = firstRow + (long)(Height - 1) * pitch;
        var lo = Math.Min(firstRow, lastRow);
        var hi = Math.Max(firstRow, lastRow) + rowBytes;
        var start = (long)bufferStart;
        if (lo < start || hi > start + bufferLength)
        {
            failure = $"buffer holds {bufferLength} bytes, not enough for {Width}x{Height} at pitch {pitch} (needs [{lo - start}, {hi - start}))";
            return false;
        }

        for (var row = 0; row < Height; row++)
            Marshal.Copy((IntPtr)(firstRow + (long)row * pitch), destination, row * rowBytes, rowBytes);
        failure = null;
        return true;
    }

    public void Dispose()
    {
        Stop();
        Frames.Dispose();   // release any frame still in the mailbox (returns its texture to the pool)
        _reader?.Dispose();
        _reader = null;
    }
}
