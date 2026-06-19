using MultiMon.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using Vortice.DXGI;

namespace MultiMon.Graphics;

/// <summary>
/// The single owner of the process-wide <see cref="ID3D11Device"/> (LESSON-ARCH-001: native
/// resources have ONE owner, ref-counted). The device, immediate context, and DXGI factory are
/// created on the first <see cref="Acquire"/> and destroyed only when the count returns to zero —
/// which callers must do off the UI thread, AFTER the render loop is stopped and joined and all
/// swapchains are released (the graphics-core disposal-order rule).
///
/// When the debug layer is enabled (explicit flag or MULTIMON_D3D_DEBUG=1), the provider exposes
/// <see cref="GetLiveObjectCount"/> so the stress harness can diff live D3D11 objects across
/// perform cycles — any growth is an ownership bug, not something to mask.
/// </summary>
public sealed class GraphicsDeviceProvider : IGraphicsDeviceProvider
{
    // Floor is Direct3D 11.0: the render pass compiles vs_5_0/ps_5_0 shaders, which REQUIRE feature level
    // 11_0. Accepting a 10_x device (as before) let creation succeed and then crash at shader creation on a
    // true FL10 GPU. Pinning the floor to 11_0 turns that into an honest, logged refusal up front. Every
    // AMD/NVIDIA/Intel GPU since ~2012 is FL11+, so this excludes nothing we target.
    private static readonly FeatureLevel[] RequestedFeatureLevels =
    {
        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0
    };

    private readonly object _gate = new();
    private readonly ILog _log;
    private readonly bool _debugRequested;
    private readonly string? _adapterSelector;

    private int _refCount;
    private bool _disposed;

    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _immediateContext;
    private ID3D11Debug? _debug;
    private ID3D11InfoQueue? _infoQueue;

    public GraphicsDeviceProvider(ILog log, bool? enableDebugLayer = null, string? adapterSelector = null)
    {
        _log = log;
        _debugRequested = enableDebugLayer
            ?? Environment.GetEnvironmentVariable("MULTIMON_D3D_DEBUG") is "1" or "true";
        // Cross-GPU test hook: pick a non-default adapter (warp | amd | nvidia | intel | <index>). Empty =
        // the normal primary-output hardware adapter. Constructor arg wins; else the MULTIMON_ADAPTER env.
        _adapterSelector = adapterSelector ?? Environment.GetEnvironmentVariable("MULTIMON_ADAPTER");
    }

    /// <inheritdoc />
    public ID3D11Device Device => _device ?? throw new InvalidOperationException(
        "Device not available — call Acquire() first (or the last Release() already destroyed it).");

    /// <summary>The immediate context. The render thread is its EXCLUSIVE user (graphics-core rule).</summary>
    public ID3D11DeviceContext ImmediateContext => _immediateContext ?? throw new InvalidOperationException(
        "Immediate context not available — call Acquire() first.");

    /// <summary>The DXGI factory, used by <see cref="OutputWindow"/> to create its persistent swapchain.</summary>
    public IDXGIFactory2 Factory => _factory ?? throw new InvalidOperationException(
        "DXGI factory not available — call Acquire() first.");

    /// <summary>
    /// True if the device supports the given DXGI format as a 2D texture. Wraps
    /// <c>ID3D11Device.CheckFormatSupport</c> so OPTIONAL formats (e.g. BC7 for HAP) can be GATED before
    /// use instead of crashing on a GPU that lacks them (cross-GPU robustness — G1). Returns false on any
    /// error or before the device exists.
    /// </summary>
    public bool SupportsTextureFormat(Format format)
    {
        lock (_gate)
        {
            if (_device is null) return false;
            try
            {
                return (_device.CheckFormatSupport(format) & FormatSupport.Texture2D) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when the D3D11 debug layer is active and <see cref="GetLiveObjectCount"/> works.</summary>
    public bool DebugLayerActive
    {
        get { lock (_gate) return _infoQueue is not null; }
    }

    /// <summary>Description of the adapter the device was created on, for the HMONITOR→adapter log (ADR 0002 D4).</summary>
    public string? DeviceAdapterName { get; private set; }

    /// <summary>The DXGI device-removed reason of the current device, as text (for recovery logging).</summary>
    public string DeviceRemovedReasonText
    {
        get { lock (_gate) return _device is null ? "(no device)" : _device.DeviceRemovedReason.ToString(); }
    }

    /// <inheritdoc />
    public void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (++_refCount == 1)
                CreateDevice();
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        lock (_gate)
        {
            if (_refCount <= 0)
                throw new InvalidOperationException("Release() without a matching Acquire().");
            if (--_refCount == 0)
                DestroyDevice();
        }
    }

    /// <summary>
    /// Counts live D3D11 device children via the debug layer (ReportLiveDeviceObjects → info queue).
    /// Returns -1 when the debug layer is unavailable. Safe to call from any thread (the debug
    /// interfaces are not the immediate context); the harness calls it between cycles while all
    /// output windows are hidden and the render thread is idle.
    /// </summary>
    public int GetLiveObjectCount()
    {
        lock (_gate)
            return CountLiveObjectsNoLock();
    }

    /// <summary>Live-object count without taking <see cref="_gate"/> — callers already hold it.</summary>
    private int CountLiveObjectsNoLock()
    {
        if (_debug is null || _infoQueue is null)
            return -1;

        _infoQueue.ClearStoredMessages();
        _debug.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Detail);

        var count = 0;
        var stored = _infoQueue.NumStoredMessages;
        for (ulong i = 0; i < stored; i++)
        {
            // Child-object lines are tab-indented ("\tLive ID3D11Texture2D at ..."), so trim
            // before matching — otherwise only the device line is counted.
            if (_infoQueue.GetMessage(i).Description.TrimStart().StartsWith("Live ", StringComparison.Ordinal))
                count++;
        }
        _infoQueue.ClearStoredMessages();
        return count;
    }

    /// <summary>
    /// Device-removed recovery (Milestone 4). Tears down the old device graph and creates a fresh one,
    /// WITHOUT changing the reference count — the persistent owners (RenderLoop, FullscreenQuadPass,
    /// sources) are recreated around it, not released. MUST run on the render thread (it owns the
    /// immediate context) and ONLY after the caller has released every device-bound child it owns
    /// (swapchains/RTVs, pass GPU resources, MF readers); otherwise the old device's debug residual
    /// below would flag the leak. After this returns, <see cref="Device"/>/<see cref="ImmediateContext"/>/
    /// <see cref="Factory"/> are the NEW objects — cached references the caller held are now stale.
    /// </summary>
    public void Recreate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_refCount <= 0 || _device is null)
                throw new InvalidOperationException("Recreate() requires an acquired device.");

            // With all device children released by the caller, the old device should report only its
            // own internals here; a swapchain/texture still listed means the caller leaked it.
            var residual = CountLiveObjectsNoLock();
            _log.Info("Graphics", $"Device recreate: removedReason={_device.DeviceRemovedReason}; " +
                                  $"old-device residual live objects={(residual < 0 ? "n/a" : residual.ToString())}.");

            TeardownDeviceGraph();

            // After a REAL TDR the adapter can be briefly unavailable while the driver resets — device
            // creation then fails transiently. Retry with a short bounded backoff: this is WAITING FOR
            // THE HARDWARE to come back, not a sleep masking an ownership bug. If it never returns within
            // the window, the final attempt throws and the render loop stops loudly (no infinite wedge).
            const int maxAttempts = 50;          // ~10s total — a removed adapter (real TDR / driver
            const int backoffMs = 200;           // restart) can stay unavailable longer than a transparent reset.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    CreateDevice();
                    break;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    _log.Error("Graphics", $"Device recreate attempt {attempt}/{maxAttempts} failed ({ex.Message}); adapter may still be resetting — retrying in {backoffMs}ms.");
                    TeardownDeviceGraph(); // clear any partial state before the next attempt
                    Thread.Sleep(backoffMs);
                }
            }
            _log.Info("Graphics", $"Device recreate: new device ready on '{DeviceAdapterName}', debugLayer={_infoQueue is not null}.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_refCount > 0)
            {
                _log.Error("Graphics", $"GraphicsDeviceProvider disposed with {_refCount} outstanding Acquire(s) — releasing the device anyway.");
                _refCount = 0;
                DestroyDevice();
            }
        }
    }

    private void CreateDevice()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        using var adapter = ResolveAdapter(_factory, out var driverType);
        var featureLevels = ResolveFeatureLevels();

        var flags = DeviceCreationFlags.BgraSupport;
        if (_debugRequested)
            flags |= DeviceCreationFlags.Debug;

        var result = D3D11.D3D11CreateDevice(adapter, driverType, flags, featureLevels,
            out _device, out var achieved, out _immediateContext);

        if (result.Failure && _debugRequested)
        {
            // No SDK layers installed on this machine — run without them rather than fail.
            _log.Error("Graphics", $"Device creation with debug layer failed ({result}); retrying without it. Live-object checks will be unavailable.");
            flags &= ~DeviceCreationFlags.Debug;
            result = D3D11.D3D11CreateDevice(adapter, driverType, flags, featureLevels,
                out _device, out achieved, out _immediateContext);
        }
        if (result.Failure)
            _log.Error("Graphics", $"D3D11 device creation failed ({result}) — this GPU is below the required " +
                                   "Direct3D 11.0 feature level (vs_5_0/ps_5_0 shaders need FL 11.0). Unsupported hardware.");
        result.CheckError();

        // Media Foundation decode runs on its own threads but shares THIS device (bound via
        // IMFDXGIDeviceManager in MultiMon.Decode). D3D11 requires the device to be marked
        // multithread-protected before it is used concurrently, so its internal context lock
        // serializes MF's decode/convert calls against the render thread's copy/present. Set it once,
        // here, before any other thread touches the device.
        using (var multithread = _immediateContext!.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        if ((flags & DeviceCreationFlags.Debug) != 0)
        {
            _debug = _device!.QueryInterfaceOrNull<ID3D11Debug>();
            _infoQueue = _device!.QueryInterfaceOrNull<ID3D11InfoQueue>();
            _infoQueue?.PushEmptyStorageFilter();
        }

        var adapterName = adapter?.Description1.Description
            ?? (driverType == DriverType.Warp ? "(WARP software rasterizer)" : "(default hardware adapter)");
        DeviceAdapterName = adapterName;
        _log.Info("Graphics", $"D3D11 device created on '{adapterName}', driverType={driverType}, feature level {achieved}, debugLayer={_infoQueue is not null}");
    }

    /// <summary>
    /// Resolves which adapter + driver type to create the device on. Honors <see cref="_adapterSelector"/>
    /// (constructor arg / MULTIMON_ADAPTER) for cross-GPU testing: <c>warp</c> (software rasterizer), a
    /// vendor substring (<c>amd</c>/<c>nvidia</c>/<c>intel</c> — first matching hardware adapter), or a
    /// numeric hardware-adapter index. Empty/unmatched → the normal primary-output hardware adapter.
    /// </summary>
    private IDXGIAdapter1? ResolveAdapter(IDXGIFactory2 factory, out DriverType driverType)
    {
        var selector = (_adapterSelector ?? string.Empty).Trim().ToLowerInvariant();

        if (selector.Length == 0)
        {
            var primary = SelectPrimaryAdapter(factory);
            driverType = primary is null ? DriverType.Hardware : DriverType.Unknown;
            return primary;
        }

        if (selector == "warp")
        {
            _log.Info("Graphics", "Adapter selector 'warp' — creating the device on the WARP software rasterizer.");
            driverType = DriverType.Warp;
            return null; // WARP = null adapter + DriverType.Warp
        }

        var byIndex = int.TryParse(selector, out var wantIndex);
        var hwIndex = 0;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var isSoftware = (adapter.Description1.Flags & AdapterFlags.Software) != 0;
            var name = adapter.Description1.Description ?? string.Empty;
            var match = !isSoftware && (byIndex ? hwIndex == wantIndex : name.ToLowerInvariant().Contains(selector));
            if (!isSoftware) hwIndex++;
            if (match)
            {
                _log.Info("Graphics", $"Adapter selector '{_adapterSelector}' matched '{name}'.");
                driverType = DriverType.Unknown;
                return adapter;
            }
            adapter.Dispose();
        }

        _log.Error("Graphics", $"Adapter selector '{_adapterSelector}' matched no adapter — using the primary hardware adapter.");
        var fallback = SelectPrimaryAdapter(factory);
        driverType = fallback is null ? DriverType.Hardware : DriverType.Unknown;
        return fallback;
    }

    /// <summary>The requested feature levels, optionally pinned to a single level for testing via
    /// MULTIMON_FEATURE_LEVEL (11_1 | 11_0). Default = the full <see cref="RequestedFeatureLevels"/> list.</summary>
    private static FeatureLevel[] ResolveFeatureLevels() =>
        Environment.GetEnvironmentVariable("MULTIMON_FEATURE_LEVEL") switch
        {
            "11_1" => new[] { FeatureLevel.Level_11_1 },
            "11_0" => new[] { FeatureLevel.Level_11_0 },
            _ => RequestedFeatureLevels,
        };

    /// <summary>
    /// Picks the hardware adapter driving the primary output (the output whose desktop rect starts
    /// at the virtual-desktop origin). Returns null to fall back to the default hardware adapter.
    /// </summary>
    private IDXGIAdapter1? SelectPrimaryAdapter(IDXGIFactory2 factory)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
                continue;
            }

            for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
            {
                var rect = output.Description.DesktopCoordinates;
                var isPrimary = rect.Left == 0 && rect.Top == 0;
                output.Dispose();
                if (isPrimary)
                    return adapter;
            }
            adapter.Dispose();
        }

        _log.Info("Graphics", "No hardware adapter with the primary output found — using the default adapter.");
        return null;
    }

    /// <summary>
    /// Destroys the device. Callers guarantee the render loop is stopped+joined and all swapchains
    /// are already released (RenderLoop.Stop does this on the render thread), and that this runs
    /// off the UI thread. Order: info queue → context (clear+flush) → debug → device → factory.
    /// </summary>
    private void DestroyDevice()
    {
        TeardownDeviceGraph();
        _log.Info("Graphics", "D3D11 device released (reference count reached zero).");
    }

    /// <summary>Disposes the device, context, factory, and debug interfaces. Shared by final release and recreate.</summary>
    private void TeardownDeviceGraph()
    {
        _infoQueue?.Dispose();
        _infoQueue = null;

        if (_immediateContext is not null)
        {
            _immediateContext.ClearState();
            _immediateContext.Flush();
            _immediateContext.Dispose();
            _immediateContext = null;
        }

        if (_debug is not null)
        {
            // Final report goes to the native debug output — useful under a debugger post-mortem.
            _debug.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Summary);
            _debug.Dispose();
            _debug = null;
        }

        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;
    }
}
