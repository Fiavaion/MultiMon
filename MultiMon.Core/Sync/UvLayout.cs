using MultiMon.Core.Models;

namespace MultiMon.Core.Sync;

/// <summary>
/// PURE per-output UV sub-rect derivation for the three perform modes (ADR 0003 D1). No GPU, no Win32 —
/// unit-tested without the harness. <see cref="FullscreenQuadPass"/> only consumes the <see cref="UvRect"/>;
/// every mode's geometry is decided here.
/// </summary>
public static class UvLayout
{
    /// <summary>
    /// Spanning: the one source is divided EQUALLY PER SCREEN — not per pixel. The participating monitors
    /// are clustered (by physical overlap) into a logical grid that matches their arrangement, and the
    /// monitor at <paramref name="outputIndex"/> gets its whole grid cell. So two side-by-side screens each
    /// show half the source — and a 2560-wide and a 3840-wide screen each take 50%, scaling their half
    /// independently to fill (the optimal span; REBUILD_ARCHITECTURE.md §2.4, ADR 0003 amendment).
    ///
    /// <para>This deliberately ignores resolution: an earlier build normalized each monitor's pixel rect
    /// into the virtual-desktop union, which handed the higher-res screen a proportionally larger slice —
    /// rejected because it sub-divides by pixels rather than by screens.</para>
    /// </summary>
    public static UvRect Spanning(int outputIndex, IReadOnlyList<MonitorRect> bounds)
    {
        if (bounds is null || bounds.Count == 0)
            throw new ArgumentException("At least one monitor rect is required.", nameof(bounds));
        if (outputIndex < 0 || outputIndex >= bounds.Count)
            throw new ArgumentOutOfRangeException(nameof(outputIndex),
                $"Output index {outputIndex} is outside the {bounds.Count}-monitor set.");

        var rowOf = ClusterByOverlap(bounds, vertical: true, out int rows);
        var colOf = ClusterByOverlap(bounds, vertical: false, out int cols);
        return Quadrant(rowOf[outputIndex], colOf[outputIndex], rows, cols);
    }

    /// <summary>
    /// Groups monitors into ordered bands along one axis: monitors whose intervals overlap share a band
    /// (a row when <paramref name="vertical"/>, else a column); abutting/disjoint monitors start a new band.
    /// Returns each monitor's 0-based band index (ordered by position) and the band count via
    /// <paramref name="bandCount"/>. This is what turns an arbitrary monitor layout into an N×M grid.
    /// </summary>
    private static int[] ClusterByOverlap(IReadOnlyList<MonitorRect> bounds, bool vertical, out int bandCount)
    {
        int n = bounds.Count;
        var start = new double[n];
        var end = new double[n];
        for (int i = 0; i < n; i++)
        {
            start[i] = vertical ? bounds[i].Top : bounds[i].Left;
            end[i] = vertical ? bounds[i].Bottom : bounds[i].Right;
        }

        var order = Enumerable.Range(0, n).OrderBy(i => start[i]).ToArray();
        var bandOf = new int[n];
        int band = -1;
        double bandEnd = double.NegativeInfinity;
        foreach (var i in order)
        {
            if (band < 0 || start[i] >= bandEnd)   // no overlap with the current band → start a new one
            {
                band++;
                bandEnd = end[i];
            }
            else                                    // overlaps → same band, extend its far edge
            {
                bandEnd = Math.Max(bandEnd, end[i]);
            }
            bandOf[i] = band;
        }

        bandCount = band + 1;
        return bandOf;
    }

    /// <summary>Split mode: cell (<paramref name="row"/>,<paramref name="col"/>) of a <paramref name="rows"/>×<paramref name="cols"/> grid over one source.</summary>
    public static UvRect Quadrant(int row, int col, int rows, int cols)
    {
        if (rows <= 0 || cols <= 0)
            throw new ArgumentException("Grid rows and cols must be positive.");
        if (row < 0 || row >= rows || col < 0 || col >= cols)
            throw new ArgumentOutOfRangeException(nameof(row), $"Cell ({row},{col}) is outside a {rows}x{cols} grid.");

        float u0 = (float)col / cols;
        float u1 = (float)(col + 1) / cols;
        float v0 = (float)row / rows;
        float v1 = (float)(row + 1) / rows;
        return new UvRect(u0, v0, u1, v1);
    }
}
