using MultiMon.Core.Diagnostics;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace MultiMon.Decode.MediaFoundation;

/// <summary>
/// Binds Media Foundation to the process's single shared <see cref="ID3D11Device"/> via an
/// <see cref="IMFDXGIDeviceManager"/> so the H.264/HEVC decoder runs in hardware and writes its
/// output into D3D11 textures on OUR device (no foreign device, no cross-device copy). If binding
/// fails on this GPU, <see cref="HardwareBound"/> is false and the source falls back to Media
/// Foundation software decode — the same texture-output contract, never a crash (decode-threading.md).
///
/// Owns the process MFStartup/MFShutdown lifetime (ref-counted so multiple managers are safe). The
/// device must already be multithread-protected (GraphicsDeviceProvider does this at creation) before
/// it is reset onto the manager, or MF's decode threads and the render thread would race the device.
/// </summary>
public sealed class MfDeviceManager : IDisposable
{
    private static readonly object StartupGate = new();
    private static int _startupCount;

    private readonly ILog _log;
    private IMFDXGIDeviceManager? _manager;
    private bool _started;
    private bool _disposed;

    /// <summary>The MF device manager to set as MF_SOURCE_READER_D3D_MANAGER. Null when software-only.</summary>
    public IMFDXGIDeviceManager? Manager => _manager;

    /// <summary>True when MF is bound to the shared device (hardware decode available).</summary>
    public bool HardwareBound => _manager is not null;

    public MfDeviceManager(ID3D11Device device, ILog log)
    {
        _log = log;
        Startup();

        // Test hook (cross-GPU): MULTIMON_FORCE_SW_DECODE makes MF take the software-decode path
        // deliberately (the no-HW-decode case on weak/Intel iGPUs) without needing such a GPU.
        if (Environment.GetEnvironmentVariable("MULTIMON_FORCE_SW_DECODE") is "1" or "true")
        {
            _manager = null;
            _log.Info("Decode", "MULTIMON_FORCE_SW_DECODE set — skipping the hardware bind; using Media Foundation software decode.");
            return;
        }

        try
        {
            var manager = global::Vortice.MediaFoundation.MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device).CheckError();
            _manager = manager;
            _log.Info("Decode", "Media Foundation bound to the shared D3D11 device — hardware decode path.");
        }
        catch (Exception ex)
        {
            _manager = null;
            _log.Error("Decode", $"MF device-manager binding failed ({ex.Message}); using software decode fallback.");
        }
    }

    /// <summary>
    /// Device-removed recovery: re-point the EXISTING device manager at the recreated device via
    /// <c>ResetDevice</c> (the supported rebind), keeping MF started and this manager object stable so
    /// each source can rebuild its reader against it. No-op on the software-only path (no manager).
    /// </summary>
    public void Rebind(ID3D11Device newDevice)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_manager is null)
            return; // software-only: nothing bound to a device
        _manager.ResetDevice(newDevice).CheckError();
        _log.Info("Decode", "Media Foundation device manager rebound to the recreated D3D11 device.");
    }

    private void Startup()
    {
        lock (StartupGate)
        {
            global::Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
            _startupCount++;
            _started = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _manager?.Dispose();
        _manager = null;

        if (!_started) return;
        lock (StartupGate)
        {
            if (--_startupCount == 0)
                global::Vortice.MediaFoundation.MediaFactory.MFShutdown();
        }
    }
}
