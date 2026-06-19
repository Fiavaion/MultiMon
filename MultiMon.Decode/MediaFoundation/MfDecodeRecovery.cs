using MultiMon.Core.Diagnostics;
using MultiMon.Graphics;
using Vortice.Direct3D11;

namespace MultiMon.Decode.MediaFoundation;

/// <summary>
/// The Media Foundation side of device-removed recovery — the <see cref="IDecodeRecovery"/> the
/// render-thread <see cref="DeviceRemovedHandler"/> drives. Quiesces every source before the device
/// dies, then rebinds the shared MF device manager and each source's reader to the recreated device
/// and restarts decode. Owns neither the manager nor the sources (they outlive recovery); it only
/// orchestrates them. Both methods run on the render thread.
/// </summary>
public sealed class MfDecodeRecovery : IDecodeRecovery
{
    private readonly MfDeviceManager _mf;
    private readonly IReadOnlyList<MediaFoundationSource> _sources;
    private readonly ILog _log;

    public MfDecodeRecovery(MfDeviceManager mf, IReadOnlyList<MediaFoundationSource> sources, ILog log)
    {
        _mf = mf;
        _sources = sources;
        _log = log;
    }

    /// <inheritdoc />
    public void Quiesce()
    {
        foreach (var source in _sources)
            source.QuiesceForDeviceLoss();
        _log.Info("Decode", $"Device-loss quiesce: {_sources.Count} source(s) stopped, readers disposed, timelines flushed.");
    }

    /// <inheritdoc />
    public void Rebind(ID3D11Device newDevice, TimeSpan resumeMediaTime)
    {
        _mf.Rebind(newDevice);
        foreach (var source in _sources)
        {
            source.RebindDevice(newDevice, _mf, resumeMediaTime);
            source.Start();
        }
        _log.Info("Decode", $"Device-loss rebind: {_sources.Count} source(s) restarted at {resumeMediaTime.TotalSeconds:0.000}s.");
    }
}
