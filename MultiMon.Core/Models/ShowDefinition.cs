namespace MultiMon.Core.Models;

/// <summary>How sources bind to outputs. The pipeline is invariant across modes — only the
/// source→output binding, per-output UV sub-rect, and clock assignment change
/// (REBUILD_ARCHITECTURE.md §2.4). The four modes span the sync spectrum:</summary>
public enum ShowMode
{
    /// <summary>One source spans all monitors; each output samples its UV slice. Synced by construction.</summary>
    Span,
    /// <summary>A different video per monitor. Free-running by default (per-source clocks); an opt-in
    /// <see cref="ShowDefinition.SyncIndividual"/> toggle shares one clock to frame-lock same-duration clips.</summary>
    Individual,
    /// <summary>A different HAP video per monitor, tightly frame-synced (HAP's fast BCn decode aligns
    /// all streams against one clock).</summary>
    Hap,
    /// <summary>One source split into a rows×columns grid; each output samples its cell's UV sub-rect.
    /// Synced by construction. (Formerly "QuadSplit" — the grid is configurable, not fixed at 2×2.)</summary>
    Split
}

/// <summary>
/// One decode source participating in a show, addressed by a stable id and bound to a monitor.
/// The orchestrator builds the actual <c>ISource</c> from this (ADR 0003 D1/D3); the UV sub-rect is
/// DERIVED at bind time (<see cref="MultiMon.Core.Sync.UvLayout"/>), never stored here.
/// </summary>
public sealed class SourceBinding
{
    /// <summary>Stable id for this source within the show (used as the <c>FullscreenQuadPass</c>/source key).</summary>
    public required string SourceId { get; init; }

    /// <summary>Path to the media file this source decodes.</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Monitor this source targets, by <see cref="MonitorInfo.DeviceId"/>. Used by PerMonitor to map
    /// output i ← source i. Ignored by Spanning/Split (one source feeds all outputs).
    /// </summary>
    public string? MonitorDeviceId { get; init; }

    /// <summary>True if the file is a HAP clip (decoded by HapSource, not Media Foundation).</summary>
    public bool IsHap { get; init; }

    /// <summary>Per-clip playback offset applied against the MasterClock (drift corrected by frame selection).</summary>
    public TimeSpan TimeOffset { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Runtime binding of sources → outputs + mode for a perform session. The orchestrator
/// (<c>PerformanceController</c>) builds sources from <see cref="Sources"/> and binds them per
/// <see cref="Mode"/>; per-output UV sub-rects are derived at bind time, not stored (ADR 0003).
/// </summary>
public class ShowDefinition
{
    public ShowMode Mode { get; set; } = ShowMode.Span;

    /// <summary>The decode sources participating in this show.</summary>
    public List<SourceBinding> Sources { get; set; } = new();

    /// <summary>
    /// <see cref="ShowMode.Individual"/> only: when true, all sources share ONE master clock so
    /// same-duration clips stay frame-locked (drift-corrected together); when false (default) each
    /// source free-runs on its own clock. Ignored by the inherently-synced modes (Span/Hap/Split).
    /// </summary>
    public bool SyncIndividual { get; set; }

    /// <summary>Split grid (rows×columns), when <see cref="Mode"/> is <see cref="ShowMode.Split"/>.</summary>
    public VideoWallConfiguration? WallConfiguration { get; set; }

    /// <summary>Audio tracks to play with this show (routed to their <see cref="AudioTrack.OutputDeviceId"/>).</summary>
    public List<AudioTrack> AudioTracks { get; set; } = new();
}
