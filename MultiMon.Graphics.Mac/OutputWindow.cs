using System.Diagnostics;
using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// One persistent output: a borderless <see cref="NSWindow"/> whose content view hosts a
/// <see cref="CAMetalLayer"/> — created ONCE and kept for the whole session. Entering/leaving perform mode
/// only shows/hides the window and binds/unbinds content; it NEVER creates or destroys the window, the layer
/// or any Metal object (LESSON-ARCH-002 — per-cycle native churn was an old-app root cause).
///
/// Ownership/threading: <see cref="RenderLoop"/> is the single owner. AppKit window operations (create,
/// orderFront, orderOut, close) run on the main thread via the bounded <see cref="MainThread.Invoke"/>,
/// called from the controller/harness thread — never from the render thread, which must not block on the
/// main thread (the V0087 deadlock rule). The render thread alone acquires drawables, encodes and presents;
/// it reads only the volatile <see cref="Visible"/> flag and the render-thread-owned content binding.
/// </summary>
public sealed class OutputWindow
{
    /// <summary>Drawables/command buffers in flight per output; also the uniform ring depth.</summary>
    private const int MaxFramesInFlight = 3;
    private static readonly TimeSpan InFlightTimeout = TimeSpan.FromMilliseconds(250);

    private readonly GraphicsDeviceProvider _provider;
    private readonly RenderLoop _loop;
    private readonly ILog _log;
    private readonly ManualResetEventSlim _presented = new(false);
    private readonly SemaphoreSlim _inFlight = new(MaxFramesInFlight, MaxFramesInFlight);
    private readonly Action<IMTLCommandBuffer> _onCompleted;

    // Main-thread-owned AppKit objects (created once; touched only inside MainThread.Invoke).
    private NSWindow? _window;
    private NSView? _view;
    // The layer is created on the main thread and then used from the render thread: CAMetalLayer's
    // nextDrawable is documented thread-safe; its geometry is only changed on the main thread while hidden.
    private CAMetalLayer? _layer;

    // Render-thread-owned Metal objects (persistent).
    private IMTLBuffer? _uniforms;              // MaxFramesInFlight ring slots of FullscreenQuadPass.UniformSlotStride bytes
    private MTLRenderPassDescriptor? _renderPass;
    private MTLRenderPassColorAttachmentDescriptor? _colorAttachment; // ColorAttachments[0], fetched once (each fetch is a new wrapper)
    private int _frameIndex;

    private volatile bool _visible;
    private int _width;
    private int _height;
    private long _presentCount;
    private int _commandBufferErrorLogged;
    private bool _disposed;

    // Live diagnostics. All written with Interlocked/Volatile on the render thread and read by anyone
    // (the panel's poll, the harness); the per-perform ones are zeroed by Show.
    private long _drawableNulls;
    private long _inFlightTimeouts;
    private long _lateFrames;
    private double _sourceFrameSeconds;  // one frame period of the bound clip, 0 when unknown (render-thread-owned)

    // Start-up timing of the current perform: the controller stamps the origin (and what the mode switch and
    // the Show cost) around its Show, and the render thread records when this output first drew real content.
    private long _startupOrigin;
    private double _startupMs;
    private double _startupModeSwitchMs;
    private double _startupShowMs;
    private int _startupPending;

    public string Name { get; }

    /// <summary>True between Show and Hide: the render thread presents this output. Written on the
    /// caller thread around the main-thread window op; read on the render thread.</summary>
    public bool Visible => _visible;

    /// <summary>What this output renders; null = clear to black. Render-thread-owned.</summary>
    public FullscreenQuadPass? Content { get; private set; }

    private UvRect _uv = UvRect.Full;   // render-thread-owned (set with Content)
    private MasterClock? _clock;         // per-output clock override (Individual free-run); render-thread-owned

    /// <summary>Frames whose command buffer COMPLETED on the GPU since creation (thread-safe read).</summary>
    public long PresentCount => Volatile.Read(ref _presentCount);

    /// <summary>Wedge diagnostic: which step of RenderAndPresent this output is in. Set on the render thread.</summary>
    private volatile string _renderPhase = "idle";
    public string RenderPhase => _renderPhase;

    /// <summary>Beats skipped this perform because <c>nextDrawable</c> returned null — the compositor was not
    /// consuming (window occluded/offscreen), including its ~1 s timeout.</summary>
    public long DrawableNullCount => Volatile.Read(ref _drawableNulls);

    /// <summary>Beats skipped this perform because no uniform ring slot freed within the in-flight timeout.</summary>
    public long InFlightTimeoutCount => Volatile.Read(ref _inFlightTimeouts);

    /// <summary>Presents this perform whose selected frame was more than one frame period behind the clock —
    /// i.e. decode did not keep up and the picture repeated a frame it should have moved past.</summary>
    public long LateFrameCount => Volatile.Read(ref _lateFrames);

    /// <summary>Milliseconds from the EnterPerform stamped by <see cref="BeginStartupTiming"/> to this output's
    /// first frame carrying decoded content; 0 until that frame is committed.</summary>
    public double StartupMs => Volatile.Read(ref _startupMs);

    /// <summary>How long this output's display-refresh match took inside that EnterPerform.</summary>
    public double StartupModeSwitchMs => Volatile.Read(ref _startupModeSwitchMs);

    /// <summary>How long this output's <see cref="Show"/> took (the bounded main-thread window op).</summary>
    public double StartupShowMs => Volatile.Read(ref _startupShowMs);

    /// <summary>Called by <see cref="RenderLoop.CreateOutputWindow"/> on the controller/harness thread (never the
    /// render thread): the NSWindow + layer are created on the main thread, the Metal objects here.</summary>
    internal OutputWindow(GraphicsDeviceProvider provider, RenderLoop loop, ILog log, string name, MonitorRect bounds)
    {
        _provider = provider;
        _loop = loop;
        _log = log;
        Name = name;
        _width = Math.Max(1, (int)bounds.Width);
        _height = Math.Max(1, (int)bounds.Height);
        _onCompleted = OnCommandBufferCompleted;

        try
        {
            MainThread.Invoke(() => CreateWindowOnMainThread(bounds));
            _uniforms = provider.Device.CreateBuffer((nuint)(MaxFramesInFlight * FullscreenQuadPass.UniformSlotStride), MTLResourceOptions.StorageModeShared)
                ?? throw new InvalidOperationException("Metal uniform buffer creation failed.");
            provider.Tracker.BufferCreated();
            _renderPass = new MTLRenderPassDescriptor();
            _colorAttachment = _renderPass.ColorAttachments[0];
            _colorAttachment.LoadAction = MTLLoadAction.Clear;
            _colorAttachment.StoreAction = MTLStoreAction.Store;
            _colorAttachment.ClearColor = new MTLClearColor(0, 0, 0, 1);
        }
        catch
        {
            DisposeCore(); // partial construction must not leak natives
            throw;
        }
        _log.Info("Graphics", $"{Name}: window + persistent Metal layer created ({_width}x{_height} px at {bounds.X},{bounds.Y})");
    }

    private void CreateWindowOnMainThread(MonitorRect bounds)
    {
        var frame = ScreenGeometry.ToPoints(bounds, out var scale);
        _window = new NSWindow(frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            Level = (NSWindowLevel)((nint)NSWindowLevel.MainMenu + 1), // above the menu bar for perform
            BackgroundColor = NSColor.Black,
            IsOpaque = true,
            HasShadow = false,
            IgnoresMouseEvents = true,
            HidesOnDeactivate = false,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.Stationary |
                                 NSWindowCollectionBehavior.IgnoresCycle | NSWindowCollectionBehavior.FullScreenAuxiliary
        };
        _layer = new CAMetalLayer
        {
            Device = _provider.Device,
            PixelFormat = QuadPipeline.DrawableFormat,
            FramebufferOnly = true,
            DisplaySyncEnabled = true,         // presents pace to the display's vsync — the loop's only pacing
            MaximumDrawableCount = MaxFramesInFlight,
            AllowsNextDrawableTimeout = true,  // a stalled compositor returns null after ~1s instead of wedging
            PresentsWithTransaction = false,
            Opaque = true,
            ContentsScale = (NFloat)scale,
            DrawableSize = new CGSize(_width, _height)
        };
        _view = new NSView(new CGRect(0, 0, frame.Width, frame.Height));
        _view.Layer = _layer;      // layer-hosting: assign the layer BEFORE WantsLayer
        _view.WantsLayer = true;
        _view.LayerContentsRedrawPolicy = NSViewLayerContentsRedrawPolicy.Never;
        _window.ContentView = _view;
    }

    /// <summary>Positions and shows the window (main thread, bounded); the layer is resized only when the
    /// pixel size changed (never in steady-state cycling). Caller thread: controller/harness, never render.</summary>
    public void Show(MonitorRect bounds)
    {
        _loop.ThrowIfRenderThread(nameof(Show));
        // Per-perform counters start clean, so the panel and the harness read THIS show's figures.
        Volatile.Write(ref _drawableNulls, 0);
        Volatile.Write(ref _inFlightTimeouts, 0);
        Volatile.Write(ref _lateFrames, 0);
        MainThread.Invoke(() =>
        {
            var width = Math.Max(1, (int)bounds.Width);
            var height = Math.Max(1, (int)bounds.Height);
            var frame = ScreenGeometry.ToPoints(bounds, out var scale);
            _window!.SetFrame(frame, true);
            if (width != _width || height != _height || (double)_layer!.ContentsScale != scale)
            {
                _width = width;
                _height = height;
                _layer!.ContentsScale = (NFloat)scale;
                _layer.DrawableSize = new CGSize(width, height);
                _log.Info("Graphics", $"{Name}: layer resized to {width}x{height}");
            }
            _window.OrderFrontRegardless();
            _visible = true;
            _log.Info("Graphics", $"{Name}: Show requested ({(int)bounds.X},{(int)bounds.Y} {width}x{height} px) -> " +
                                  $"frame ({frame.X:0},{frame.Y:0} {frame.Width:0}x{frame.Height:0} pt) visible={_window.IsVisible}");
        });
    }

    /// <summary>Hides the window. The window, layer and Metal objects stay alive (persistent pipeline rule).</summary>
    public void Hide()
    {
        _loop.ThrowIfRenderThread(nameof(Hide));
        _visible = false; // the render thread stops presenting before the window leaves the screen
        // A perform that never showed content must not leave the measurement armed: the next one would report
        // against an origin from the perform before it.
        Interlocked.Exchange(ref _startupPending, 0);
        MainThread.Invoke(() => _window!.OrderOut(null));
    }

    /// <summary>
    /// Arms this output's start-up timing for the perform that just showed it: <paramref name="originTimestamp"/>
    /// is the <see cref="Stopwatch"/> stamp taken when EnterPerform began, and the two costs already paid on the
    /// way here are recorded with it. The render thread reports the elapsed time when it commits the first frame
    /// carrying decoded content, and logs the line once. Called on the controller/harness thread immediately
    /// AFTER <see cref="Show"/> (arming before it would time a window that is not on screen yet); a frame that
    /// slipped between the two is simply not the one counted, which costs at most one vsync of accuracy.
    /// </summary>
    public void BeginStartupTiming(long originTimestamp, double modeSwitchMs, double showMs)
    {
        _loop.ThrowIfRenderThread(nameof(BeginStartupTiming));
        _startupOrigin = originTimestamp;
        Volatile.Write(ref _startupMs, 0);
        Volatile.Write(ref _startupModeSwitchMs, modeSwitchMs);
        Volatile.Write(ref _startupShowMs, showMs);
        Interlocked.Exchange(ref _startupPending, 1); // publishes the three writes above to the render thread
    }

    /// <summary>Binds (or unbinds with null) the content this output renders and the source sub-rect it
    /// samples; a zero-area <paramref name="uv"/> means the full frame. <paramref name="sourceFps"/> is the
    /// bound clip's frame rate, which sets the "one frame period" the late-frame counter compares against
    /// (0 = unknown, and nothing is counted late). Marshalled onto the render thread.</summary>
    public void SetContent(FullscreenQuadPass? content, UvRect uv = default, MasterClock? clock = null, double sourceFps = 0)
    {
        var slice = uv.Width > 0 && uv.Height > 0 ? uv : UvRect.Full;
        var frameSeconds = sourceFps > 0 ? 1.0 / sourceFps : 0;
        _loop.Invoke(() => { Content = content; _uv = slice; _clock = clock; _sourceFrameSeconds = frameSeconds; });
    }

    /// <summary>Waits until <see cref="PresentCount"/> reaches <paramref name="target"/> (bounded) — the harness's
    /// wedge detector and per-cycle dwell: it snapshots every output's target first, then waits for all, so N
    /// outputs are held concurrently. Event-driven, not a sleep-poll.</summary>
    public bool WaitForPresentCount(long target, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _presentCount) < target)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;
            _presented.Reset();
            if (Volatile.Read(ref _presentCount) >= target)
                return true;
            _presented.Wait(remaining);
        }
        return true;
    }

    /// <summary>
    /// Waits until every in-flight command buffer of this output has completed (bounded). The harness calls
    /// it after Hide so its per-cycle resource sample sees no drawable still counted in flight.
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        _loop.ThrowIfRenderThread(nameof(WaitForIdle));
        var stopwatch = Stopwatch.StartNew();
        var held = 0;
        try
        {
            for (; held < MaxFramesInFlight; held++)
            {
                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero || !_inFlight.Wait(remaining))
                    return false;
            }
            return true;
        }
        finally
        {
            if (held > 0) _inFlight.Release(held);
        }
    }

    /// <summary>Render thread only. Returns true when a frame was committed for presentation.</summary>
    internal bool RenderAndPresent(IMTLCommandQueue queue, TimeSpan mediaTime)
    {
        if (!_visible || _disposed)
            return false;

        // In-flight gate: the uniform ring slot we are about to write was last read by the command buffer
        // three frames back; its completion released this permit. A bounded wait — a stalled GPU drops a beat,
        // never wedges the loop.
        _renderPhase = "in-flight-wait";
        if (!_inFlight.Wait(InFlightTimeout))
        {
            Interlocked.Increment(ref _inFlightTimeouts);
            _renderPhase = "idle";
            return false;
        }

        _renderPhase = "next-drawable";
        var drawable = _layer!.NextDrawable();
        if (drawable is null)
        {
            Interlocked.Increment(ref _drawableNulls);
            _inFlight.Release();
            _renderPhase = "idle";
            return false; // compositor not consuming (window offscreen/occluded, or timed out): skip this beat
        }
        _provider.Tracker.DrawableAcquired();

        var time = _clock?.CurrentMediaTime ?? mediaTime;
        var slot = _frameIndex++ % MaxFramesInFlight;
        var offset = slot * FullscreenQuadPass.UniformSlotStride;

        _renderPhase = "encode";
        using var drawableTexture = drawable.Texture;
        _colorAttachment!.Texture = drawableTexture;
        var commandBuffer = queue.CommandBuffer()
            ?? throw new InvalidOperationException($"{Name}: command buffer creation failed.");
        try
        {
            DecodedFrame? read = null;
            if (Content is { } pass)
            {
                read = pass.Draw(commandBuffer, _renderPass!, _uniforms!, offset, _width, _height, time, _uv);
                NoteFrameTiming(pass, time);
            }
            else
            {
                var encoder = commandBuffer.CreateRenderCommandEncoder(_renderPass!); // clear-only pass
                encoder.EndEncoding();
                encoder.Dispose();
            }
            commandBuffer.AddCompletedHandler(_onCompleted);
            _renderPhase = "present";
            commandBuffer.PresentDrawable(drawable);
            read?.HoldUntilCompleted(commandBuffer); // last thing before Commit: nothing can abandon the buffer in between
            commandBuffer.Commit();
        }
        finally
        {
            // Drop OUR references now: the descriptor must not pin the drawable's texture (it would never
            // return to the layer's pool), and the wrappers go — Metal retains what the command buffer needs.
            _colorAttachment.Texture = null;
            commandBuffer.Dispose();
            drawable.Dispose();
            _renderPhase = "idle";
        }
        return true;
    }

    /// <summary>
    /// Render thread, right after the pass encoded this beat: records the first frame that carried content (the
    /// start-up measurement) and counts a late frame when the frame the selector chose is more than one frame
    /// period behind the clock — decode not keeping up, which is invisible in the present rate because the
    /// pipeline happily re-presents the frame it already has. Counters only; no allocation, no lock.
    /// </summary>
    private void NoteFrameTiming(FullscreenQuadPass pass, TimeSpan time)
    {
        if (!pass.HasDrawnContent)
            return; // nothing decoded yet: the pass cleared to black, and that is not a late frame
        if (Interlocked.CompareExchange(ref _startupPending, 0, 1) == 1)
            LogFirstContentPresent();
        if (_sourceFrameSeconds > 0 && (time - pass.LastSelectedPts).TotalSeconds > _sourceFrameSeconds)
            Interlocked.Increment(ref _lateFrames);
    }

    /// <summary>Render thread, once per perform (the arming flag has already been taken).</summary>
    private void LogFirstContentPresent()
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - _startupOrigin) * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _startupMs, elapsedMs);
        _log.Info("Graphics", $"{Name}: first frame {elapsedMs:0.0} ms after EnterPerform " +
                              $"(mode switch {Volatile.Read(ref _startupModeSwitchMs):0.0} ms, show {Volatile.Read(ref _startupShowMs):0.0} ms)");
    }

    /// <summary>Metal completion thread: the frame is on its way to the display and its uniform slot is free.</summary>
    private void OnCommandBufferCompleted(IMTLCommandBuffer commandBuffer)
    {
        if (commandBuffer.Status == MTLCommandBufferStatus.Error && Interlocked.Exchange(ref _commandBufferErrorLogged, 1) == 0)
            _log.Error("Graphics", $"{Name}: command buffer failed: {commandBuffer.Error?.LocalizedDescription} (further failures not logged).");
        // The binding hands us a fresh wrapper (a retain) per callback. Release it NOW: left to the GC, the
        // retained command buffer pins its drawable's texture — under the Metal debug layer that showed as
        // one drawable-sized allocation leaked per frame until a finalizer pass.
        commandBuffer.Dispose();
        _provider.Tracker.DrawableCompleted();
        Interlocked.Increment(ref _presentCount);
        _presented.Set();
        _inFlight.Release();
    }

    /// <summary>
    /// Teardown, called by <see cref="RenderLoop.Stop"/> on the stopping thread AFTER the render thread is
    /// joined (so nothing encodes any more). Order: drain in-flight command buffers → close the window on the
    /// main thread → release the Metal objects (graphics-core disposal-order rule).
    /// </summary>
    internal void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        _visible = false;
        Content = null;

        // Every in-flight command buffer must complete before its uniform buffer and drawable go away.
        for (var i = 0; i < MaxFramesInFlight; i++)
            if (!_inFlight.Wait(TimeSpan.FromSeconds(2)))
                _log.Error("Graphics", $"{Name}: an in-flight command buffer did not complete within 2s of teardown.");

        MainThread.Invoke(() =>
        {
            if (_window is not null)
            {
                // Never Close(): a programmatic NSWindow is releasedWhenClosed by default, and Close + our
                // Dispose would double-release it. OrderOut, drop the view, and release our one reference.
                _window.OrderOut(null);
                _window.ContentView = null;
                _window.Dispose();
                _window = null;
            }
            _view?.Dispose();
            _view = null;
            _layer?.Dispose();
            _layer = null;
        });

        _colorAttachment?.Dispose();
        _colorAttachment = null;
        _renderPass?.Dispose();
        _renderPass = null;
        if (_uniforms is not null)
        {
            _uniforms.Dispose();
            _uniforms = null;
            _provider.Tracker.BufferDisposed();
        }
        // _presented and _inFlight are deliberately NOT disposed: a completion that outlived the bounded drain
        // above would otherwise Release() a disposed semaphore on Metal's thread. Both are handle-free managed
        // objects; releasing straggling waiters is all teardown owes them.
        _presented.Set();
    }
}
