using System.Runtime.InteropServices;
using AppKit;
using Foundation;
using Metal;
using MultiMon.Core.Diagnostics;
using ObjCRuntime;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The single owner of the process-wide <see cref="IMTLDevice"/> and its ONE command queue (LESSON-ARCH-001:
/// native resources have ONE owner, ref-counted) — the Metal twin of <c>MultiMon.Graphics.GraphicsDeviceProvider</c>.
/// Device, queue and the shared <see cref="QuadPipeline"/> are created on the first <see cref="Acquire"/> and
/// destroyed only when the count returns to zero — which callers must do off the main thread, AFTER the render
/// loop is stopped and joined and every output window is closed (the graphics-core disposal-order rule).
///
/// Resource accounting is the <see cref="Tracker"/> (every Metal object this assembly creates) plus
/// <see cref="CurrentAllocatedSize"/> (the device's own byte count) — the harness samples both per cycle.
/// </summary>
public sealed class GraphicsDeviceProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly ILog _log;

    private int _refCount;
    private bool _disposed;

    private IMTLDevice? _device;
    private IMTLCommandQueue? _commandQueue;
    private QuadPipeline? _quadPipeline;

    public GraphicsDeviceProvider(ILog log)
    {
        _log = log;
    }

    /// <summary>Every Metal object created/disposed by this assembly, counted for the harness's growth diff.</summary>
    public MetalResourceTracker Tracker { get; } = new();

    public IMTLDevice Device => _device ?? throw new InvalidOperationException(
        "Device not available — call Acquire() first (or the last Release() already destroyed it).");

    /// <summary>The one command queue. The render thread is its EXCLUSIVE user (graphics-core rule).</summary>
    public IMTLCommandQueue CommandQueue => _commandQueue ?? throw new InvalidOperationException(
        "Command queue not available — call Acquire() first.");

    internal QuadPipeline QuadPipeline => _quadPipeline ?? throw new InvalidOperationException(
        "Quad pipeline not available — call Acquire() first.");

    /// <summary>Name of the device the session renders on, for the log.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Bytes the device has allocated for all resources right now (0 before Acquire). Any thread.</summary>
    public ulong CurrentAllocatedSize
    {
        get { lock (_gate) return _device?.CurrentAllocatedSize ?? 0; }
    }

    public void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (++_refCount != 1)
                return;
            try
            {
                CreateDevice();
            }
            catch
            {
                // A failed first Acquire owns nothing: undo the count and drop the partial graph.
                _refCount--;
                TeardownDeviceGraph();
                throw;
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_refCount <= 0)
                throw new InvalidOperationException("Release() without a matching Acquire().");
            if (--_refCount == 0)
            {
                TeardownDeviceGraph();
                _log.Info("Graphics", "Metal device released (reference count reached zero).");
            }
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
                TeardownDeviceGraph();
            }
        }
    }

    private void CreateDevice()
    {
        _device = SelectDevice();
        DeviceName = _device.Name;
        _commandQueue = _device.CreateCommandQueue()
            ?? throw new InvalidOperationException("Metal command queue creation failed.");
        Tracker.CommandQueueCreated();
        _log.Info("Graphics", $"Metal device created on '{DeviceName}' (registryId={_device.RegistryId}, unifiedMemory={_device.HasUnifiedMemory}, " +
                              $"recommendedWorkingSet={_device.RecommendedMaxWorkingSetSize / (1024.0 * 1024 * 1024):0.0}GB)");

        // Shaders + pipeline state: once per device, here — never per pass (LESSON-ARCH-002).
        _quadPipeline = new QuadPipeline(_device, Tracker);
    }

    /// <summary>
    /// Picks the GPU that drives the MOST displays (the Windows adapter rule: "the adapter driving the
    /// primary/most outputs"), tie-broken by the larger recommended working set. Every candidate is
    /// logged so the choice is auditable per run. A single-GPU Mac (every Apple Silicon machine) takes the
    /// system default without ceremony.
    /// </summary>
    private IMTLDevice SelectDevice()
    {
        var all = MTLDevice.GetAllDevices();
        if (all.Length <= 1)
        {
            foreach (var extra in all) extra.Dispose(); // SystemDefault below is our one owned reference
            return MTLDevice.SystemDefault
                ?? throw new InvalidOperationException("No Metal device available — this Mac cannot run MultiMon.");
        }

        // Which device drives each display: CoreGraphics answers per CGDirectDisplayID; match by registry id.
        var displaysPerDevice = new Dictionary<ulong, int>();
        foreach (var screen in NSScreen.Screens)
        {
            if (screen.DeviceDescription["NSScreenNumber"] is not NSNumber displayId) continue; // same key MonitorService uses
            var handle = CGDirectDisplayCopyCurrentMetalDevice(displayId.UInt32Value);
            if (handle == IntPtr.Zero) continue;
            using var driving = Runtime.GetINativeObject<IMTLDevice>(handle, owns: true)!;
            displaysPerDevice[driving.RegistryId] = displaysPerDevice.GetValueOrDefault(driving.RegistryId) + 1;
        }

        IMTLDevice? best = null;
        var bestDisplays = -1;
        var candidates = new List<string>();
        foreach (var device in all)
        {
            var displays = displaysPerDevice.GetValueOrDefault(device.RegistryId);
            candidates.Add($"'{device.Name}' (registryId={device.RegistryId}, displays={displays}, " +
                           $"recommendedWorkingSet={device.RecommendedMaxWorkingSetSize / (1024.0 * 1024 * 1024):0.0}GB)");
            var better = best is null
                || displays > bestDisplays
                || (displays == bestDisplays && device.RecommendedMaxWorkingSetSize > best.RecommendedMaxWorkingSetSize);
            if (better)
            {
                best?.Dispose();
                best = device;
                bestDisplays = displays;
            }
            else
            {
                device.Dispose();
            }
        }
        _log.Info("Graphics", $"Device selection: chose '{best!.Name}' (most displays, then largest working set). Candidates: {string.Join("; ", candidates)}");
        return best;
    }

    /// <summary>Disposes pipeline → queue → device. Callers guarantee the render loop is stopped and joined
    /// and every window closed, so no command buffer or drawable can still reference the device.</summary>
    private void TeardownDeviceGraph()
    {
        _quadPipeline?.Dispose();
        _quadPipeline = null;

        if (_commandQueue is not null)
        {
            _commandQueue.Dispose();
            _commandQueue = null;
            Tracker.CommandQueueDisposed();
        }

        _device?.Dispose();
        _device = null;
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGDirectDisplayCopyCurrentMetalDevice(uint display);
}
