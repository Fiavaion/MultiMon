namespace MultiMon.Core.Models;

/// <summary>
/// A video assigned to one monitor slot in a project. Salvaged from the old app
/// (REBUILD_ARCHITECTURE.md §7) but the LibVLC crop fields (<c>CropX/Y/Width/Height</c> →
/// <c>CropGeometry</c> string) are DROPPED: in the rebuild the on-screen region is a UV sub-rect
/// derived at bind time (<see cref="MultiMon.Core.Sync.UvLayout"/>), not a stored crop.
/// </summary>
public sealed class VideoAssignment
{
    /// <summary>The monitor this video plays on, by <see cref="MonitorInfo.DeviceId"/>.</summary>
    public string MonitorDeviceId { get; set; } = string.Empty;

    /// <summary>Path to the video file.</summary>
    public string VideoFilePath { get; set; } = string.Empty;

    /// <summary>Per-clip offset for synchronization against the MasterClock.</summary>
    public TimeSpan TimeOffset { get; set; } = TimeSpan.Zero;

    /// <summary>Linear gain for this clip's audio, 0.0–1.0.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>True if the file is a HAP clip (HapSource decode path).</summary>
    public bool IsHap { get; set; }

    public override string ToString()
        => $"Monitor {MonitorDeviceId}: {System.IO.Path.GetFileName(VideoFilePath)} (+{TimeOffset})";
}
