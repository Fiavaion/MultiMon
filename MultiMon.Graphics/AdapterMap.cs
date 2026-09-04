using MultiMon.Core.Diagnostics;
using Vortice;
using Vortice.DXGI;

namespace MultiMon.Graphics;

/// <summary>
/// HMONITOR→adapter topology (ADR 0002 D4). Enumerates each DXGI adapter and the outputs it drives,
/// and flags outputs that are NOT on the device's adapter — those present via DWM cross-adapter
/// composition (a single device serves every monitor; the per-adapter-device split stays deferred
/// until Checkpoint B proves it necessary). This is diagnostic: it surfaces, per run, exactly which
/// monitors are cross-adapter so a garbage/perf failure there is immediately attributable.
/// Adapters are identified by LUID — descriptions are not unique (two identical cards share a string).
/// </summary>
public static class AdapterMap
{
    /// <summary>Logs the adapter→outputs map for <paramref name="provider"/>'s device and returns the number of
    /// cross-adapter (non-device) outputs.</summary>
    public static int LogTopology(GraphicsDeviceProvider provider, ILog log)
        => LogTopology(provider.Factory, provider.DeviceAdapterLuid, log);

    /// <summary>Logs the adapter→outputs map and returns the number of cross-adapter (non-device) outputs.</summary>
    public static int LogTopology(IDXGIFactory2 factory, Luid deviceAdapterLuid, ILog log)
    {
        var crossAdapterOutputs = 0;

        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                var isDeviceAdapter = desc.Luid == deviceAdapterLuid;

                var outputs = new List<string>();
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    using (output)
                    {
                        var rect = output.Description.DesktopCoordinates;
                        outputs.Add($"[{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}]");
                    }
                }

                if (!isDeviceAdapter)
                    crossAdapterOutputs += outputs.Count;

                var tag = isDeviceAdapter ? "DEVICE adapter"
                    : outputs.Count > 0 ? "CROSS-ADAPTER (DWM-composited)"
                    : "inactive";
                log.Info("Graphics", $"Adapter '{desc.Description}' (luid={desc.Luid.LowPart:X8}) [{tag}] drives {outputs.Count} output(s): {string.Join(" ", outputs)}");
            }
        }

        if (crossAdapterOutputs > 0)
            log.Info("Graphics", $"{crossAdapterOutputs} output(s) are on a non-device adapter — presenting cross-adapter via the DWM (ADR 0002 D4).");
        return crossAdapterOutputs;
    }
}
