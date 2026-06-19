using Vortice.Direct3D11;

namespace MultiMon.Graphics;

/// <summary>
/// The decode side of device-removed recovery, called by <see cref="DeviceRemovedHandler"/> on the
/// render thread. Implemented in MultiMon.Decode (which references Graphics, not the reverse) so the
/// render loop can drive the MF reader rebind without Graphics depending on Media Foundation.
///
/// Contract — both methods run on the render thread, bracketing the device recreate:
/// <list type="number">
///   <item><see cref="Quiesce"/> BEFORE the device is destroyed: stop every decode thread, dispose the
///   readers, and flush buffered frames (they hold the dying device's decoder-pool textures).</item>
///   <item><see cref="Rebind"/> AFTER the new device exists: re-point Media Foundation at it, position
///   each source to <paramref name="resumeMediaTime"/> (never seek a live decoder), and restart decode.</item>
/// </list>
/// </summary>
public interface IDecodeRecovery
{
    /// <summary>Stop + tear down all decode so no thread or buffered frame references the dying device.</summary>
    void Quiesce();

    /// <summary>Rebind decode to <paramref name="newDevice"/> and resume from the MasterClock time (no time jump).</summary>
    void Rebind(ID3D11Device newDevice, TimeSpan resumeMediaTime);
}
