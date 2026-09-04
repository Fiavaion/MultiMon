using MultiMon.Core.Models;
using MultiMon.Core.Sync;

namespace MultiMon.Core.Show;

/// <summary>Which timeline an output follows: the show's shared <c>MasterClock</c>, or its own.</summary>
public enum ShowClock
{
    /// <summary>The one show clock — synced by construction (Span/Split/Hap, and Individual when synced).</summary>
    Shared,

    /// <summary>Its own independent timeline (Individual free-run: clips of different lengths, unlocked).</summary>
    FreeRun
}

/// <summary>A decode source the show needs, resolved from a <see cref="SourceBinding"/>.</summary>
/// <param name="PreferHap">Try the HAP path FIRST for this source. Mode-driven, not a per-file flag:
/// Individual never sets it (it is the Media Foundation mode) even for a .mov that declares HAP.</param>
public sealed record PlannedSource(string SourceId, string FilePath, bool PreferHap);

/// <summary>One output fed by one planned source through a UV sub-rect, on the named clock.</summary>
/// <param name="SourceIndex">Index into <see cref="ShowPlan.Sources"/>. Several outputs share one source
/// in Span/Split — that is the whole point of the one-decode wall.</param>
/// <param name="OutputIndex">Index into the monitor list the plan was made for.</param>
public sealed record PlannedBinding(int SourceIndex, int OutputIndex, UvRect Uv, ShowClock Clock);

/// <summary>The complete source→output mapping for a show, plus any bindings that could not be placed.</summary>
public sealed record ShowPlan(
    IReadOnlyList<PlannedSource> Sources,
    IReadOnlyList<PlannedBinding> Bindings,
    IReadOnlyList<string> Warnings)
{
    public static ShowPlan Empty { get; } = new([], [], []);
}

/// <summary>
/// PURE show mapping: <see cref="ShowDefinition"/> + monitors → which source feeds which output, through
/// which UV sub-rect, on which clock. No GPU, no decoder, no OS — the orchestrator
/// (<c>PerformanceController</c>) turns a plan into D3D11 passes, MF/HAP sources and output windows, but
/// decides none of the mapping itself. Extracting it means the mode geometry is unit-testable without a
/// device and ports to macOS untouched; the pipeline stays invariant across modes (ADR 0003 D1/D2).
/// </summary>
public static class ShowPlanner
{
    /// <summary>
    /// Derives a near-square grid from the screen count: 1→1×1, 2→1×2, 4→2×2, 6→2×3, 9→3×3, 16→4×4.
    /// Shared by the control panel's auto-grid toggle and the stress harness so both tile a wall the
    /// same way.
    /// </summary>
    public static (int Rows, int Cols) AutoGrid(int screens)
    {
        var n = Math.Max(1, screens);
        var cols = (int)Math.Ceiling(Math.Sqrt(n));
        var rows = (int)Math.Ceiling((double)n / cols);
        return (rows, cols);
    }

    /// <summary>
    /// Plans <paramref name="show"/> across <paramref name="monitors"/>. Never throws on bad content: a
    /// source aimed at a monitor that isn't here is dropped with a message in
    /// <see cref="ShowPlan.Warnings"/> (the caller logs it), and a monitor with no cell is simply left
    /// unbound — it stays black rather than showing the wrong slice.
    /// </summary>
    public static ShowPlan Plan(ShowDefinition show, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(monitors);

        return show.Mode switch
        {
            // A clip per monitor. Free-run (own clock each) unless the user opts into shared-clock sync.
            ShowMode.Individual => PlanPerMonitor(show, monitors, preferHap: false,
                show.SyncIndividual ? ShowClock.Shared : ShowClock.FreeRun),
            // A HAP clip per monitor, tightly frame-synced (one shared clock).
            ShowMode.Hap => PlanPerMonitor(show, monitors, preferHap: true, ShowClock.Shared),
            ShowMode.Split => PlanSingleSource(show, monitors, split: true),
            _ => PlanSingleSource(show, monitors, split: false),
        };
    }

    /// <summary>Spanning / split: ONE source shared by every participating output; only the per-output UV
    /// slice differs (ADR 0003 D1/D2).</summary>
    private static ShowPlan PlanSingleSource(ShowDefinition show, IReadOnlyList<MonitorInfo> monitors, bool split)
    {
        var binding = show.Sources.FirstOrDefault();
        if (binding is null)
            return ShowPlan.Empty; // nothing to play

        var sources = new[] { new PlannedSource(binding.SourceId, binding.FilePath, binding.IsHap) };
        var bindings = new List<PlannedBinding>();

        if (split)
        {
            var wall = show.WallConfiguration;
            var rows = Math.Max(1, wall?.Rows ?? 2);
            var cols = Math.Max(1, wall?.Columns ?? 2);
            for (var i = 0; i < monitors.Count; i++)
            {
                if (!TryGetCell(wall, monitors[i].DeviceId, i, rows, cols, out var row, out var col))
                    continue; // monitor not mapped to a cell → left black
                bindings.Add(new PlannedBinding(0, i, UvLayout.Quadrant(row, col, rows, cols), ShowClock.Shared));
            }
        }
        else
        {
            // Equal share per screen (not per pixel): each output fills its cell of the layout-derived grid.
            var bounds = monitors.Select(m => m.Bounds).ToList();
            for (var i = 0; i < monitors.Count; i++)
                bindings.Add(new PlannedBinding(0, i, UvLayout.Spanning(i, bounds), ShowClock.Shared));
        }

        return new ShowPlan(sources, bindings, []);
    }

    /// <summary>Individual / Hap: each binding gets its OWN source, bound full-UV to its monitor's output.</summary>
    private static ShowPlan PlanPerMonitor(ShowDefinition show, IReadOnlyList<MonitorInfo> monitors,
        bool preferHap, ShowClock clock)
    {
        var sources = new List<PlannedSource>();
        var bindings = new List<PlannedBinding>();
        var warnings = new List<string>();

        foreach (var binding in show.Sources)
        {
            var index = IndexOfMonitor(monitors, binding.MonitorDeviceId);
            if (index < 0)
            {
                warnings.Add($"source '{binding.FilePath}' targets unknown monitor '{binding.MonitorDeviceId}'; skipping.");
                continue;
            }
            sources.Add(new PlannedSource(binding.SourceId, binding.FilePath, preferHap));
            bindings.Add(new PlannedBinding(sources.Count - 1, index, UvRect.Full, clock));
        }

        return new ShowPlan(sources, bindings, warnings);
    }

    private static int IndexOfMonitor(IReadOnlyList<MonitorInfo> monitors, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return -1;
        for (var i = 0; i < monitors.Count; i++)
            if (string.Equals(monitors[i].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Resolves a monitor's split-grid cell from the wall mapping ("row,col"→deviceId);
    /// falls back to row-major order by output index when the monitor isn't explicitly mapped.</summary>
    private static bool TryGetCell(VideoWallConfiguration? wall, string deviceId, int outputIndex,
        int rows, int cols, out int row, out int col)
    {
        if (wall is not null)
        {
            foreach (var (key, mappedId) in wall.GridToMonitorMapping)
            {
                if (!string.Equals(mappedId, deviceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = key.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out row) && int.TryParse(parts[1], out col)
                    && row >= 0 && row < rows && col >= 0 && col < cols)
                    return true;
            }
        }
        // Fallback: assign cells row-major by output index (so an un-mapped 2-monitor split still shows
        // two distinct cells rather than nothing).
        if (outputIndex < rows * cols)
        {
            row = outputIndex / cols;
            col = outputIndex % cols;
            return true;
        }
        row = col = 0;
        return false;
    }
}
