namespace MultiMon.Core.Models;

/// <summary>
/// Identified GPU vendor. In the rebuild this gates HAP capability and adapter selection,
/// not the old LibVLC backend ladder.
/// </summary>
public enum GpuVendor
{
    Unknown,
    Amd,
    Nvidia,
    Intel
}
