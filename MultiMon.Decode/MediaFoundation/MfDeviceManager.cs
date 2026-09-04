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
    private int _disposed;   // 0/1; Interlocked so Dispose is idempotent across threads

    /// <summary>The MF device manager to set as MF_SOURCE_READER_D3D_MANAGER. Null when software-only.</summary>
    public IMFDXGIDeviceManager? Manager => _manager;

    /// <summary>True when MF is bound to the shared device (hardware decode available).</summary>
    public bool HardwareBound => _manager is not null;

    /// <param name="preferSoftwareDecode">Skip the hardware bind and decode in software — for GPUs on the
    /// software-decode list (drivers whose D3D11VA path is known-flaky). Same effect as the
    /// MULTIMON_FORCE_SW_DECODE test hook.</param>
    public MfDeviceManager(ID3D11Device device, ILog log, bool preferSoftwareDecode = false)
    {
        _log = log;
        Startup();

        if (preferSoftwareDecode)
        {
            _manager = null;
            _log.Info("Decode", "GPU on the software-decode list — skipping the hardware bind; using Media Foundation software decode.");
            return;
        }

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
    /// Never throws: if the rebind fails, the manager is released and the process degrades to software
    /// decode (<see cref="HardwareBound"/> becomes false) rather than killing the render loop mid-recovery.
    /// </summary>
    public void Rebind(ID3D11Device newDevice)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_manager is null)
            return; // software-only: nothing bound to a device
        try
        {
            _manager.ResetDevice(newDevice).CheckError();
            _log.Info("Decode", "Media Foundation device manager rebound to the recreated D3D11 device.");
        }
        catch (Exception ex)
        {
            _manager.Dispose();
            _manager = null;
            _log.Error("Decode", $"MF device-manager rebind failed ({ex.Message}); degrading to software decode for the rest of the session.");
        }
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

    /// <summary>Idempotent (a second or concurrent Dispose is a no-op): the static MF startup count is
    /// decremented exactly once per started manager, so a double Dispose can never shut MF down under
    /// a still-live manager (the same guard as MfAudioSource).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _manager?.Dispose();
        _manager = null;

        if (!_started) return;
        lock (StartupGate)
        {
            if (_startupCount > 0 && --_startupCount == 0)
                global::Vortice.MediaFoundation.MediaFactory.MFShutdown();
        }
    }
}
