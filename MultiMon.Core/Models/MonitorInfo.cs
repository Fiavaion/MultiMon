namespace MultiMon.Core.Models;

/// <summary>
/// Display monitor information. Salvaged from the old app; the WPF <c>Rect</c> fields are now
/// the framework-agnostic <see cref="MonitorRect"/>.
/// </summary>
public class MonitorInfo
{
    /// <summary>Win32 device name (e.g. <c>\\.\DISPLAY1</c>) — also the HMONITOR→adapter match key.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Friendly display name (e.g. "Monitor 1 (Primary)").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Work area (excludes taskbar).</summary>
    public MonitorRect WorkArea { get; set; }

    /// <summary>Full monitor bounds — used for fullscreen swapchain-window placement.</summary>
    public MonitorRect Bounds { get; set; }

    /// <summary>Resolution string, e.g. "1920x1080".</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Refresh rate in Hz (drives per-monitor present cadence in Milestone 3).</summary>
    public double RefreshRate { get; set; }

    public bool IsPrimary { get; set; }

    public override string ToString()
        => $"{DisplayName} ({Resolution}) @ {RefreshRate}Hz{(IsPrimary ? " [Primary]" : "")}";
}
