using MultiMon.Core.Diagnostics;
using MultiMon.Decode.Mac.Hap;
using MultiMon.Decode.Mac.VideoToolbox;
using MultiMon.Graphics.Mac;
using MultiMon.Hap;

namespace MultiMon.Decode.Mac;

/// <summary>
/// The symmetric decode ladder (LESSON-TEST-004: the gate and the app open clips the SAME way): a <c>.mov</c> is
/// probed as HAP first (<see cref="MovHapDemuxer"/>) and falls through to VideoToolbox when it is not a HAP
/// movie; everything else is VideoToolbox. <paramref name="requireHap"/> (the harness's <c>--hap</c>) refuses the
/// fallthrough so a HAP gate can never silently run the other decoder.
/// </summary>
public static class SourceLadder
{
    public static IMetalSource Open(string path, GraphicsDeviceProvider provider, ILog log, bool requireHap = false, bool forceSoftware = false, string? id = null)
    {
        var isMov = string.Equals(Path.GetExtension(path), ".mov", StringComparison.OrdinalIgnoreCase);
        if (requireHap || isMov)
        {
            try
            {
                return new HapSource(path, provider, log, id);
            }
            catch (Exception ex) when (!requireHap && ex is InvalidDataException or NotSupportedException)
            {
                log.Info("Decode", $"{id ?? Path.GetFileNameWithoutExtension(path)}: not a HAP movie ({ex.Message}); opening with VideoToolbox.");
            }
        }
        return new VideoToolboxSource(path, provider, log, forceSoftware, id);
    }
}
