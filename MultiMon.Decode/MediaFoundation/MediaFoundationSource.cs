using System.Runtime.InteropServices;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using MF = Vortice.MediaFoundation.MediaFactory;

namespace MultiMon.Decode.MediaFoundation;

/// <summary>
/// Decodes one clip with an <see cref="IMFSourceReader"/> on its OWN thread (off the UI and render
/// threads) and publishes the latest frame into a <see cref="TripleBuffer"/> for the render thread.
/// Output is BGRA (MF RGB32): on the hardware path the reader's D3D11 video processor writes the
/// frame straight into a texture on our shared device; on the software fallback it produces a CPU
/// buffer. Either way the render thread copies the payload into ONE persistent shader-resource
/// texture and samples it — a single, format-uniform output contract.
///
/// The reader, its threads, and the triple buffer are created ONCE and live for the session; entering
/// or leaving perform mode never touches them. At end-of-stream the clip loops by seeking the reader
/// back to zero (the supported source-reader loop) — this is NOT the forbidden "seek a live decoder
/// to correct drift" (decode-threading.md); drift correction lands with the MasterClock in M3.
/// </summary>
public sealed class MediaFoundationSource : ISource
{
    private ID3D11Device _device;       // re-pointed at the new device on device-removed recovery
    private readonly ILog _log;
    private readonly string _path;

    // 30fps fallback when a sample reports no duration — used only to advance the loop base by ~1 frame.
    private const long DefaultFrameTicks = TimeSpan.TicksPerSecond / 30;

    // Transient hardware-decoder HRESULTs: the sample-allocator pool is momentarily exhausted because WE
    // hold the buffered frames' textures (FrameTimeline). MF documents these as "release a sample and retry",
    // not a decode failure — see DecodeLoop.
    private const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);
    private const int MF_E_SAMPLEALLOCATOR_WAITING = unchecked((int)0xC00D4A3F);

    private IMFSourceReader? _reader;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _allocatorEmptyLogged;     // decode-thread only; throttles the transient-retry log to once per burst
    private long _ptsTicks;
    private long _loopBaseTicks;        // added to in-clip sample time so PTS ascend across loops
    private long _lastSampleEndTicks;   // in-clip end (time+duration) of the last sample this loop
    private long _clipDurationTicks;    // clip length (0 = unknown); used to position on device-loss resume
    private bool _softwareFallback;
    private int _loggedFirstFrame;

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
    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));

    public MediaFoundationSource(ID3D11Device device, MfDeviceManager mf, string path, ILog log, string? id = null)
    {
        _device = device;
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
        _log.Info("Decode", $"{Id}: opened {Width}x{Height}, path={(_softwareFallback ? "software" : "hardware")} decode, " +
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

    /// <summary>Builds a source reader configured for BGRA output. Returns false (and tidies up) on failure.</summary>
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

            using (var actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                MF.MFGetAttributeSize(actualType, MediaTypeAttributeKeys.FrameSize, out var w, out var h).CheckError();
                Width = (int)w;
                Height = (int)h;
            }

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
    /// </summary>
    public void RebindDevice(ID3D11Device device, MfDeviceManager mf, TimeSpan resumeMediaTime)
    {
        if (_thread is not null)
            throw new InvalidOperationException("RebindDevice requires the decode thread stopped — call QuiesceForDeviceLoss first.");

        _device = device;
        if (mf.HardwareBound && TryConfigure(mf.Manager))
            _softwareFallback = false;
        else if (TryConfigure(null))
            _softwareFallback = true;
        else
            throw new InvalidOperationException($"Could not reopen '{_path}' for decode after device loss.");

        PositionTo(resumeMediaTime.Ticks);
        // _stop is volatile; PositionTo's loop-base/position writes are plain longs, but the caller's
        // following Start() creates a NEW thread, and Thread.Start is a publication fence — so the fresh
        // decode thread is guaranteed to observe these writes. Do not read them from another live thread.
        _stop = false; // re-arm so Start() can spin a fresh decode thread
        Interlocked.Exchange(ref _loggedFirstFrame, 0);
        _log.Info("Decode", $"{Id}: rebound to recreated device ({(_softwareFallback ? "software" : "hardware")}), resumed at {resumeMediaTime.TotalSeconds:0.000}s.");
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
            var framesThisLoop = 0;
            while (!_stop)
            {
                IMFSample? sample;
                SourceReaderFlag flags;
                long timestamp;
                try
                {
                    sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None, out _, out flags, out timestamp);
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
                _allocatorEmptyLogged = false;

                if ((flags & SourceReaderFlag.Error) != 0)
                {
                    sample?.Dispose();
                    _log.Error("Decode", $"{Id}: source reader reported an error flag; stopping decode.");
                    break;
                }

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    sample?.Dispose();
                    // A clip that yields ZERO frames before EOS would spin SetCurrentPosition(0)
                    // forever (burning CPU, never presenting) — stop instead of wedging.
                    if (framesThisLoop == 0)
                    {
                        _log.Error("Decode", $"{Id}: clip produced no frames before end-of-stream; stopping decode.");
                        break;
                    }
                    // Advance the loop base by the clip length seen, so the next loop's PTS continue
                    // ascending on the same timeline as the MasterClock (ADR 0002 D1 looping).
                    _loopBaseTicks += _lastSampleEndTicks;
                    _lastSampleEndTicks = 0;
                    framesThisLoop = 0;
                    _reader!.SetCurrentPosition(0); // loop the clip (supported source-reader seek)
                    continue;
                }

                if (sample is null)
                    continue; // stream tick without data — keep reading
                framesThisLoop++;

                var duration = sample.SampleDuration > 0 ? sample.SampleDuration : DefaultFrameTicks;
                _lastSampleEndTicks = timestamp + duration;
                var globalPts = _loopBaseTicks + timestamp;
                Volatile.Write(ref _ptsTicks, globalPts);

                if (_softwareFallback)
                    PublishSoftware(sample, globalPts);
                else
                    PublishHardware(sample, globalPts);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Decode", $"{Id}: decode loop ended on exception: {ex}");
        }
    }

    /// <summary>Hardware: hand the decoder's D3D11 texture to the render thread, holding the MF sample alive.</summary>
    private void PublishHardware(IMFSample sample, long pts100ns)
    {
        var buffer = sample.GetBufferByIndex(0);
        var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is null)
        {
            // The reader did not return a D3D-backed buffer despite the device manager — degrade this
            // frame to a CPU copy rather than crash. (Rare; usually a one-off during stream start.)
            buffer.Dispose();
            PublishSoftware(sample, pts100ns);
            return;
        }

        var texturePtr = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
        var texture = new ID3D11Texture2D(texturePtr);
        var subresource = dxgi.SubresourceIndex;

        if (Interlocked.Exchange(ref _loggedFirstFrame, 1) == 0)
        {
            var d = texture.Description;
            _log.Info("Decode", $"{Id}: first HW frame texture format={d.Format} {d.Width}x{d.Height} array={d.ArraySize} bind={d.BindFlags} sub={subresource}");
        }

        // The frame OWNS these refs; releasing it returns the texture to the decoder pool.
        var frame = new DecodedFrame(texture, subresource, TimeSpan.FromTicks(pts100ns), () =>
        {
            texture.Dispose();
            dxgi.Dispose();
            buffer.Dispose();
            sample.Dispose();
        });
        Frames.Publish(frame);
    }

    /// <summary>Software fallback: copy the CPU BGRA buffer out so the sample can be released immediately.</summary>
    private void PublishSoftware(IMFSample sample, long pts100ns)
    {
        var buffer = sample.ConvertToContiguousBuffer();
        try
        {
            buffer.Lock(out var data, out _, out var currentLength);
            var pixels = new byte[currentLength];
            Marshal.Copy(data, pixels, 0, currentLength);
            buffer.Unlock();

            var frame = new DecodedFrame(pixels, Width * 4, TimeSpan.FromTicks(pts100ns), () => { });
            Frames.Publish(frame);
        }
        finally
        {
            buffer.Dispose();
            sample.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        Frames.Dispose();   // release any frame still in the mailbox (returns its texture to the pool)
        _reader?.Dispose();
        _reader = null;
    }
}
