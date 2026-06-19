using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MultiMon.Graphics;

/// <summary>
/// One persistent output: a bare Win32 HWND (no WPF) plus a flip-model <see cref="IDXGISwapChain1"/>
/// and its backbuffer RTV — created ONCE and kept for the whole session. Entering/leaving perform
/// mode only shows/hides the window and binds/unbinds content; it NEVER creates or destroys the
/// swapchain (LESSON-ARCH-002 — per-cycle native churn was an old-app root cause).
///
/// Ownership/threading: <see cref="RenderLoop"/> is the single owner — it constructs this on the
/// render thread (HWNDs are thread-affine), pumps its messages, renders/presents it, and disposes
/// it on the render thread during Stop. The public Show/Hide/SetContent methods marshal onto the
/// render thread via <see cref="RenderLoop.Invoke"/>, so callers never touch the HWND, swapchain,
/// or context directly.
/// </summary>
public sealed class OutputWindow
{
    private const string WindowClassName = "MultiMonOutputWindow";

    private static readonly object ClassGate = new();
    // Rooted for the process lifetime: if the GC collected this marshaled delegate, the next
    // window message would call into freed memory — a classic native crash.
    private static Win32.WndProc? _classWndProc;
    private static ushort _classAtom;

    private readonly GraphicsDeviceProvider _provider;
    private readonly RenderLoop _loop;
    private readonly ILog _log;
    private readonly ManualResetEventSlim _presented = new(false);

    private IntPtr _hwnd;
    private IDXGISwapChain1? _swapChain;
    private IntPtr _frameLatencyWaitable;   // owned by the swapchain; valid while _swapChain is alive
    private ID3D11RenderTargetView? _renderTargetView;
    private int _width;
    private int _height;
    private long _presentCount;
    private bool _disposed;
    private volatile string? _capturePath; // set off-thread, consumed once on the render thread

    public string Name { get; }

    /// <summary>
    /// Set when Present reported DXGI_ERROR_DEVICE_REMOVED/RESET; cleared by recovery
    /// (<see cref="RestoreAfterRecovery"/>). While set, <see cref="RenderAndPresent"/> skips this output
    /// so the render loop stops presenting to a dead swapchain until <see cref="DeviceRemovedHandler"/>
    /// recreates it. <see cref="Visible"/> is left untouched so it still records the intended state and
    /// presenting resumes automatically once the swapchain is rebuilt.
    /// </summary>
    public bool DeviceLost { get; private set; }

    /// <summary>Render-thread-owned state: window visible and presenting.</summary>
    public bool Visible { get; private set; }

    /// <summary>
    /// The swapchain's frame-latency waitable object (or <see cref="IntPtr.Zero"/> when not presentable).
    /// The render loop waits on this — bounded — to pace presents WITHOUT the indefinite Present(1) block
    /// that the 1ms system timer triggers (ADR 0002 D3). Render-thread-owned; re-fetched on recreate.
    /// </summary>
    internal IntPtr FrameLatencyWaitable => _frameLatencyWaitable;

    /// <summary>What this output renders; null = clear to black. Render-thread-owned.</summary>
    public FullscreenQuadPass? Content { get; private set; }

    /// <summary>The source sub-rect this output samples (M7). Full frame until a mode sets a slice.
    /// Render-thread-owned (set with <see cref="Content"/> via the same marshalled SetContent).</summary>
    private UvRect _uv = UvRect.Full;

    /// <summary>
    /// Optional per-output playback clock (M7 Stage A). When set, this output selects frames against it
    /// instead of the render loop's shared clock — the mechanism for Individual free-run (each source its
    /// own timeline). Null = use the loop's shared clock (the synced modes: Span/Hap/Split and
    /// synced-Individual). Render-thread-owned (set via the marshalled SetContent).
    /// </summary>
    private MasterClock? _clock;

    /// <summary>Total successful presents since creation (thread-safe read).</summary>
    public long PresentCount => Volatile.Read(ref _presentCount);

    /// <summary>Wedge diagnostic (M6): which step of RenderAndPresent this output is in — lets the harness
    /// pinpoint whether a frozen render thread is stuck in Draw vs Present. Set on the render thread.</summary>
    private volatile string _renderPhase = "idle";
    public string RenderPhase => _renderPhase;

    /// <summary>Render thread only — called by <see cref="RenderLoop.CreateOutputWindow"/>.</summary>
    internal OutputWindow(GraphicsDeviceProvider provider, RenderLoop loop, ILog log, string name, MonitorRect bounds)
    {
        _provider = provider;
        _loop = loop;
        _log = log;
        Name = name;
        _width = Math.Max(1, (int)bounds.Width);
        _height = Math.Max(1, (int)bounds.Height);

        EnsureWindowClass();
        // WS_EX_TOPMOST at creation puts the output in the always-on-top band for its whole life (it is just
        // hidden until perform), so it reliably sits above other apps' windows — more robust than relying
        // only on the per-Show SetWindowPos(HWND_TOPMOST). WS_EX_NOACTIVATE keeps it from stealing focus.
        _hwnd = Win32.CreateWindowExW(Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST, WindowClassName, name, Win32.WS_POPUP,
            (int)bounds.X, (int)bounds.Y, _width, _height, IntPtr.Zero, IntPtr.Zero,
            Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(); // captures GetLastError

        try
        {
            CreateSwapchainResources();
        }
        catch
        {
            // Partial construction must not leak natives: undo in reverse order and rethrow.
            DisposeCore();
            throw;
        }

        _log.Info("Graphics", $"{Name}: window + persistent swapchain created ({_width}x{_height} at {bounds.X},{bounds.Y})");
    }

    /// <summary>
    /// Creates the flip-model swapchain + its backbuffer RTV for this window's HWND on the provider's
    /// CURRENT device/factory. Used at construction and at device-removed recreate (the HWND outlives
    /// the device, so recovery rebuilds only these device-bound objects). Render thread only.
    /// </summary>
    private void CreateSwapchainResources()
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            // Waitable swapchain (ADR 0002 D3): the render loop paces on the frame-latency object and
            // presents with sync interval 0, so Present never blocks for seconds when the system timer is
            // raised to 1ms (the confirmed root cause of the multi-monitor present wedge).
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        };
        _swapChain = _provider.Factory.CreateSwapChainForHwnd(_provider.Device, _hwnd, description);
        _provider.Factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);

        using (var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            swapChain2.MaximumFrameLatency = 1;                       // render at most one frame ahead
            _frameLatencyWaitable = swapChain2.FrameLatencyWaitableObject; // handle owned by the swapchain
        }
        CreateRenderTargetView();
    }

    /// <summary>Positions and shows the window WITHOUT touching the swapchain (unless the size changed).</summary>
    public void Show(MonitorRect bounds) => _loop.Invoke(() => ShowCore(bounds));

    /// <summary>Hides the window. The swapchain stays alive (persistent pipeline rule).</summary>
    public void Hide() => _loop.Invoke(HideCore);

    /// <summary>
    /// Binds (or unbinds with null) the content this output renders and the source sub-rect it samples.
    /// A <c>default</c>/zero-area <paramref name="uv"/> means the full frame (so callers that don't care
    /// about a slice — and every pre-M7 caller — get full-frame passthrough).
    /// </summary>
    public void SetContent(FullscreenQuadPass? content, UvRect uv = default, MasterClock? clock = null)
    {
        var slice = uv.Width > 0 && uv.Height > 0 ? uv : UvRect.Full;
        _loop.Invoke(() => { Content = content; _uv = slice; _clock = clock; });
    }

    /// <summary>
    /// Diagnostic: dump the NEXT presented frame to a 32bpp BMP. The render thread consumes the path
    /// (volatile handoff) and captures via a staging copy, so it works regardless of window occlusion
    /// — the reliable visual gate for a flip-model swapchain that can't be screen-grabbed.
    /// </summary>
    public void RequestCapture(string path) => _capturePath = path;

    /// <summary>
    /// Waits until <paramref name="additionalFrames"/> more presents complete — the harness's wedge
    /// detector and per-cycle dwell. Event-driven (signaled per present), not a sleep-poll.
    /// </summary>
    public bool WaitForPresentedFrames(long additionalFrames, TimeSpan timeout)
    {
        var target = Volatile.Read(ref _presentCount) + additionalFrames;
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

    private void ShowCore(MonitorRect bounds)
    {
        var width = Math.Max(1, (int)bounds.Width);
        var height = Math.Max(1, (int)bounds.Height);
        if (width != _width || height != _height)
            ResizeCore(width, height);

        // Raise the output to TOPMOST so it sits above whatever apps were already on screen when perform
        // started (otherwise a foreground window stays over the video). SWP_NOACTIVATE keeps us from
        // stealing focus — the WS_EX_NOACTIVATE popup never takes activation, the control panel keeps it.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, (int)bounds.X, (int)bounds.Y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Visible = true;

        // Ground-truth diagnostic: log where the window ACTUALLY landed + whether the OS reports it visible,
        // vs the bounds we requested. A mismatch (off-screen / wrong size / not visible) explains a monitor
        // showing nothing even while the swapchain presents.
        Win32.GetWindowRect(_hwnd, out var actual);
        _log.Info("Graphics", $"{Name}: Show requested ({(int)bounds.X},{(int)bounds.Y} {width}x{height}) -> " +
            $"actual ({actual.Left},{actual.Top} {actual.Right - actual.Left}x{actual.Bottom - actual.Top}) " +
            $"visible={Win32.IsWindowVisible(_hwnd)}");
    }

    private void HideCore()
    {
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        Visible = false;
    }

    /// <summary>
    /// Resizes the EXISTING swapchain's buffers — never recreates the swapchain. Only runs when a
    /// Show requests different bounds (it never fires in steady-state cycling; the log proves it).
    /// </summary>
    private void ResizeCore(int width, int height)
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        // Keep the waitable flag across resize; the frame-latency object persists with the swapchain.
        _swapChain!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject);
        _width = width;
        _height = height;
        CreateRenderTargetView();
        _log.Info("Graphics", $"{Name}: swapchain buffers resized to {width}x{height}");
    }

    /// <summary>Render thread only. Returns true when a frame was presented.</summary>
    internal bool RenderAndPresent(ID3D11DeviceContext context, TimeSpan mediaTime)
    {
        if (!Visible || DeviceLost || _swapChain is null || _renderTargetView is null)
            return false;

        // Per-output clock overrides the loop's shared sample (Individual free-run); null = shared clock.
        var time = _clock?.CurrentMediaTime ?? mediaTime;

        _renderPhase = "draw";
        if (Content is { } pass)
        {
            pass.Draw(context, _renderTargetView, _width, _height, time, _uv);
        }
        else
        {
            context.OMSetRenderTargets(_renderTargetView);
            context.ClearRenderTargetView(_renderTargetView, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
        }

        // Diagnostic: dump exactly what the pipeline rendered (decode→copy→sample→draw) to disk,
        // independent of window z-order/occlusion — the reliable visual gate for a flip-model window.
        var capturePath = _capturePath;
        if (capturePath is not null)
        {
            _capturePath = null;
            try { CaptureBackbuffer(context, capturePath); }
            catch (Exception ex) { _log.Error("Graphics", $"{Name}: frame capture failed: {ex.Message}"); }
        }

        // Sync interval 0: queue the frame and return immediately. The render loop already waited on the
        // frame-latency object for pacing/back-pressure, so this never blocks for vsync (ADR 0002 D3). The
        // flip-model DWM still composites at vblank — no tearing (no ALLOW_TEARING flag).
        _renderPhase = "present";
        var result = _swapChain.Present(0, PresentFlags.None);
        _renderPhase = "post-present";
        // Unbind the backbuffer so nothing holds it across hide/resize.
        context.UnsetRenderTargets();

        if (result.Failure)
        {
            if (result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.DeviceReset)
            {
                // Stop presenting to the dead swapchain but keep Visible (the intended state) so the
                // render loop's DeviceRemovedHandler recreates and resumes this output (Milestone 4).
                DeviceLost = true;
                _log.Error("Graphics", $"{Name}: Present failed with {result} (DeviceRemovedReason={_provider.Device.DeviceRemovedReason}) — output paused for device-removed recovery.");
            }
            else
            {
                _log.Error("Graphics", $"{Name}: Present failed with {result}");
            }
            return false;
        }

        Interlocked.Increment(ref _presentCount);
        _presented.Set();
        _renderPhase = "idle";
        return true;
    }

    /// <summary>
    /// Render thread only — called by <see cref="RenderLoop"/> while stopping (HWND destruction is
    /// thread-affine). Order: RTV → swapchain → window (graphics-core disposal-order rule).
    /// </summary>
    internal void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        Content = null;
        Visible = false;
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _frameLatencyWaitable = IntPtr.Zero; // invalidated with the swapchain (swapchain owns the handle)
        _swapChain?.Dispose();
        _swapChain = null;
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _presented.Set(); // release any straggling waiter before the event dies
        _presented.Dispose();
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Releases the device-bound swapchain + RTV (the HWND
    /// and all window state survive). Called by <see cref="DeviceRemovedHandler"/> BEFORE the device is
    /// recreated; pair with <see cref="RecreateDeviceResources"/> after.
    /// </summary>
    internal void ReleaseDeviceResources()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _frameLatencyWaitable = IntPtr.Zero; // re-fetched by RecreateDeviceResources → CreateSwapchainResources
        _swapChain?.Dispose();
        _swapChain = null;
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Rebuilds the swapchain + RTV on the provider's NEW
    /// device for the surviving HWND (same size/position). Called AFTER <see cref="GraphicsDeviceProvider.Recreate"/>.
    /// </summary>
    internal void RecreateDeviceResources()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        CreateSwapchainResources();
        _log.Info("Graphics", $"{Name}: swapchain recreated on the new device ({_width}x{_height}).");
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Clears <see cref="DeviceLost"/> so the render loop
    /// resumes presenting; <see cref="Visible"/> was preserved through the loss, so no re-show is needed.
    /// </summary>
    internal void RestoreAfterRecovery() => DeviceLost = false;

    /// <summary>Render thread only. Copies the just-drawn backbuffer to a staging texture and writes a BMP.</summary>
    private void CaptureBackbuffer(ID3D11DeviceContext context, string path)
    {
        using var backbuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        var desc = backbuffer.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;

        using var staging = _provider.Device.CreateTexture2D(desc);
        context.CopyResource(staging, backbuffer);

        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { WriteBmp32(path, map.DataPointer, (int)map.RowPitch, _width, _height); }
        finally { context.Unmap(staging, 0); }
        _log.Info("Graphics", $"{Name}: captured presented frame to {path}");
    }

    /// <summary>Writes BGRX rows as a top-down 32bpp BMP (alpha forced opaque). No external dependency.</summary>
    private static void WriteBmp32(string path, IntPtr data, int rowPitch, int width, int height)
    {
        const int headerSize = 54;
        var imageBytes = width * height * 4;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B'); writer.Write((byte)'M');
        writer.Write(headerSize + imageBytes);
        writer.Write(0);
        writer.Write(headerSize);
        writer.Write(40);          // BITMAPINFOHEADER size
        writer.Write(width);
        writer.Write(-height);     // negative height = top-down
        writer.Write((short)1);    // planes
        writer.Write((short)32);   // bits per pixel
        writer.Write(0);           // BI_RGB
        writer.Write(imageBytes);
        writer.Write(2835); writer.Write(2835);
        writer.Write(0); writer.Write(0);

        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(data + y * rowPitch, row, 0, row.Length);
            for (var x = 3; x < row.Length; x += 4) row[x] = 255; // BGRX → opaque BGRA
            writer.Write(row, 0, row.Length);
        }
    }

    private void CreateRenderTargetView()
    {
        // Flip-model buffer 0 is a stable texture object, so this RTV lives until resize/teardown —
        // it is NOT recreated per frame or per cycle.
        using var backbuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _provider.Device.CreateRenderTargetView(backbuffer);
    }

    private static void EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (_classAtom != 0)
                return;

            _classWndProc = WndProcThunk;
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = _classWndProc,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = WindowClassName
            };
            _classAtom = Win32.RegisterClassExW(ref wc);
            if (_classAtom == 0)
                throw new Win32Exception();
        }
    }

    private static IntPtr WndProcThunk(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Outputs are not user-closable; lifetime belongs to RenderLoop, not the shell.
        if (msg == Win32.WM_CLOSE)
            return IntPtr.Zero;
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
