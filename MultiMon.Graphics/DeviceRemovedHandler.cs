using MultiMon.Core.Diagnostics;
using MultiMon.Core.Timing;

namespace MultiMon.Graphics;

/// <summary>
/// Device-removed / TDR recovery (Milestone 4). On <c>DXGI_ERROR_DEVICE_REMOVED/RESET</c> (detected by
/// <see cref="OutputWindow"/> on Present, or injected by the stress harness) the entire device-bound
/// graph is recreated IN ORDER and playback resumes from the <see cref="MasterClock"/> — never on the
/// UI thread (the V0087 rule). <see cref="RenderLoop"/> calls <see cref="Recover"/> on the render thread,
/// which exclusively owns the immediate context and swapchains.
///
/// Ordering (graphics-core disposal rule — stop the producer before releasing the resource it feeds):
/// quiesce decode → release pass GPU resources → release each window's swapchain/RTV → recreate the
/// device → recreate swapchains/RTVs → recreate pass GPU resources → rebind decode to the new device
/// and restart → clear the device-lost flags so presenting resumes. The MasterClock keeps running
/// throughout, so the resume is seamless on the timeline (no time jump).
/// </summary>
public sealed class DeviceRemovedHandler
{
    private readonly GraphicsDeviceProvider _provider;
    private readonly IReadOnlyList<FullscreenQuadPass> _passes;
    private readonly IDecodeRecovery? _decode;
    private readonly MasterClock? _clock;
    private readonly ILog _log;
    private int _recoveryCount;

    /// <summary>Total successful recoveries since construction (thread-safe read). The soak/TDR gate reads this.</summary>
    public int RecoveryCount => Volatile.Read(ref _recoveryCount);

    /// <summary>
    /// <paramref name="passes"/> is every pass that owns device-bound GPU resources — one for the
    /// single-source modes, N for per-monitor (M7). All are released/recreated together so recovery
    /// covers the whole pipeline regardless of mode.
    /// </summary>
    public DeviceRemovedHandler(GraphicsDeviceProvider provider, IReadOnlyList<FullscreenQuadPass> passes,
        IDecodeRecovery? decode, MasterClock? clock, ILog log)
    {
        _provider = provider;
        _passes = passes ?? Array.Empty<FullscreenQuadPass>();
        _decode = decode;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Recreates the device-bound graph and resumes. Render thread ONLY (called from the render loop).
    /// <paramref name="windows"/> is the render loop's own window list. <paramref name="injected"/> is
    /// true for a simulated loss (the device was still healthy) vs a real device-removed event.
    /// </summary>
    public void Recover(IReadOnlyList<OutputWindow> windows, bool injected)
    {
        var resumeTime = _clock?.CurrentMediaTime ?? TimeSpan.Zero;
        _log.Error("Graphics", $"=== DEVICE-REMOVED RECOVERY (injected={injected}, reason={_provider.DeviceRemovedReasonText}, " +
                               $"resume={resumeTime.TotalSeconds:0.000}s) ===");

        // 1. Quiesce decode first: stop the decode threads, dispose readers, flush frames that hold the
        //    dying device's textures. After this nothing off-thread references the old device.
        _decode?.Quiesce();

        // 2. Release device-bound GPU resources we own (passes first, then each window's swapchain/RTV).
        foreach (var pass in _passes)
            pass.ReleaseDeviceResources();
        foreach (var window in windows)
            window.ReleaseDeviceResources();

        // 3. Recreate the device (the old device now has no live children; the provider logs the residual).
        _provider.Recreate();

        // 4. Recreate the device-bound graph on the NEW device, in reverse of release order.
        foreach (var window in windows)
            window.RecreateDeviceResources();
        foreach (var pass in _passes)
            pass.RecreateDeviceResources(_provider.Device);

        // 5. Rebind decode to the new device and restart it, positioned to the clock time (no time jump).
        _decode?.Rebind(_provider.Device, resumeTime);

        // 6. Clear the device-lost flags so the render loop resumes presenting these outputs.
        foreach (var window in windows)
            window.RestoreAfterRecovery();

        Interlocked.Increment(ref _recoveryCount);
        _log.Info("Graphics", "=== DEVICE-REMOVED RECOVERY COMPLETE — presenting resumed ===");
    }
}
