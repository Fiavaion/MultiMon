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
///
/// Degrades, never throws: a source that cannot be reopened is left stopped and faulted (its output keeps
/// showing the last frame) while the others resume — one bad clip must not end the render loop.
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
        _mf.Rebind(newDevice); // degrades to software internally on failure
        var restarted = 0;
        foreach (var source in _sources)
        {
            try
            {
                if (!source.RebindDevice(newDevice, _mf, resumeMediaTime))
                    continue; // logged + faulted by the source; leave it stopped
                source.Start();
                restarted++;
            }
            catch (Exception ex)
            {
                _log.Error("Decode", $"{source.Id}: restart after device loss failed ({ex.Message}); output keeps its last frame.");
            }
        }
        _log.Info("Decode", $"Device-loss rebind: {restarted}/{_sources.Count} source(s) restarted at {resumeMediaTime.TotalSeconds:0.000}s.");
    }
}
