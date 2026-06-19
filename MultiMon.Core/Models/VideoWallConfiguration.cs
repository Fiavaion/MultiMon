namespace MultiMon.Core.Models;

/// <summary>
/// Grid configuration for Split mode — one source video tiled across the monitors in a rows×columns
/// grid. The grid maps to per-output UV sub-rects sampled from ONE source texture (no per-cell decode).
/// </summary>
public class VideoWallConfiguration
{
    /// <summary>Source video file path (the large video to split).</summary>
    public string SourceVideoPath { get; set; } = string.Empty;

    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    /// <summary>When true, <see cref="Rows"/>/<see cref="Columns"/> are derived from the screen count
    /// (the near-square auto grid); when false they are the user's explicit override.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Columns in the grid (default 2 for 2×2).</summary>
    public int Columns { get; set; } = 2;

    /// <summary>Rows in the grid (default 2 for 2×2).</summary>
    public int Rows { get; set; } = 2;

    /// <summary>Grid position → monitor device id. Key format: "row,col" (e.g. "0,0" = top-left).</summary>
    public Dictionary<string, string> GridToMonitorMapping { get; set; } = new();

    public int TotalCells => Columns * Rows;
}
