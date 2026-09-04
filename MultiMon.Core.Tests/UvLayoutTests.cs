using MultiMon.Core.Models;
using MultiMon.Core.Sync;

namespace MultiMon.Core.Tests;

/// <summary>
/// Contract tests for UvLayout (ADR 0003 D1) — the pure per-output UV derivation for the three modes.
/// No GPU: spanning slices, quad-split cells, and the dev-box geometry are arithmetic.
/// </summary>
public class UvLayoutTests
{
    private const float Eps = 1e-5f;

    private static void AssertUv(UvRect r, float u0, float v0, float u1, float v1)
    {
        Assert.Equal(u0, r.U0, Eps);
        Assert.Equal(v0, r.V0, Eps);
        Assert.Equal(u1, r.U1, Eps);
        Assert.Equal(v1, r.V1, Eps);
    }

    // ── Spanning (equal share PER SCREEN, not per pixel) ─────────────────────────

    [Fact]
    public void Spanning_SingleMonitor_FullCoverage()
    {
        var bounds = new[] { new MonitorRect(0, 0, 1920, 1080) };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 1f, 1f);
    }

    [Fact]
    public void Spanning_TwoEqualMonitors_SplitHalves()
    {
        // Two 1920x1080 monitors side by side → left half / right half.
        var bounds = new[] { new MonitorRect(0, 0, 1920, 1080), new MonitorRect(1920, 0, 1920, 1080) };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 0.5f, 1f);
        AssertUv(UvLayout.Spanning(1, bounds), 0.5f, 0f, 1f, 1f);
    }

    [Fact]
    public void Spanning_MixedResolution_StillSplitsFiftyFifty()
    {
        // REGRESSION (the dev-box bug): a 2560-wide and a 3840-wide screen side by side must EACH get half
        // the source — NOT a pixel-proportional 40/60 slice. Each screen scales its half to fill itself.
        var bounds = new[] { new MonitorRect(0, 0, 2560, 1440), new MonitorRect(2560, 0, 3840, 2160) };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 0.5f, 1f);
        AssertUv(UvLayout.Spanning(1, bounds), 0.5f, 0f, 1f, 1f);
    }

    [Fact]
    public void Spanning_HarnessWindowedRowLayout_SplitsIntoDistinctColumns()
    {
        // REGRESSION (harness false-positive): windowed stress rects must be laid out NON-overlapping so span
        // clustering yields a real per-output slice (a prior overlapping/staggered layout merged into one
        // cell). Mirrors StressHarness's tiling: 960x540, 20px gap, same Y → one row x N columns.
        const int w = 960, h = 540, gap = 20;
        var bounds = new[]
        {
            new MonitorRect(120 + 0 * (w + gap), 120, w, h),
            new MonitorRect(120 + 1 * (w + gap), 120, w, h),
        };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 0.5f, 1f);
        AssertUv(UvLayout.Spanning(1, bounds), 0.5f, 0f, 1f, 1f);
    }

    [Fact]
    public void Spanning_VerticalStack_SplitsIntoEqualBands()
    {
        // Two monitors stacked vertically → top band / bottom band, full width each.
        var bounds = new[] { new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 1080, 1920, 1080) };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 1f, 0.5f);
        AssertUv(UvLayout.Spanning(1, bounds), 0f, 0.5f, 1f, 1f);
    }

    [Fact]
    public void Spanning_TwoByTwoGrid_EachScreenGetsItsQuadrant()
    {
        // A uniform 2x2 wall → four equal quadrants by arrangement.
        var bounds = new[]
        {
            new MonitorRect(0, 0, 1920, 1080),       // top-left
            new MonitorRect(1920, 0, 1920, 1080),    // top-right
            new MonitorRect(0, 1080, 1920, 1080),    // bottom-left
            new MonitorRect(1920, 1080, 1920, 1080), // bottom-right
        };
        AssertUv(UvLayout.Spanning(0, bounds), 0f, 0f, 0.5f, 0.5f);
        AssertUv(UvLayout.Spanning(1, bounds), 0.5f, 0f, 1f, 0.5f);
        AssertUv(UvLayout.Spanning(2, bounds), 0f, 0.5f, 0.5f, 1f);
        AssertUv(UvLayout.Spanning(3, bounds), 0.5f, 0.5f, 1f, 1f);
    }

    [Fact]
    public void Spanning_NegativeOffset_OrdersByPosition()
    {
        // Secondary monitor to the LEFT of primary (negative X) — the left screen still gets the left half.
        var bounds = new[] { new MonitorRect(0, 0, 1920, 1080), new MonitorRect(-2560, 0, 2560, 1440) };
        AssertUv(UvLayout.Spanning(1, bounds), 0f, 0f, 0.5f, 1f);   // the -2560 monitor is leftmost
        AssertUv(UvLayout.Spanning(0, bounds), 0.5f, 0f, 1f, 1f);
    }

    [Fact]
    public void Spanning_InvalidArgs_Throw()
    {
        Assert.Throws<ArgumentException>(() => UvLayout.Spanning(0, new List<MonitorRect>()));
        Assert.Throws<ArgumentException>(() => UvLayout.Spanning(0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UvLayout.Spanning(2, new[] { new MonitorRect(0, 0, 100, 100) }));
    }

    // ── Quadrant ────────────────────────────────────────────────────────────────

    [Fact]
    public void Quadrant_2x2_AllFourCells()
    {
        AssertUv(UvLayout.Quadrant(0, 0, 2, 2), 0f, 0f, 0.5f, 0.5f);   // top-left
        AssertUv(UvLayout.Quadrant(0, 1, 2, 2), 0.5f, 0f, 1f, 0.5f);   // top-right
        AssertUv(UvLayout.Quadrant(1, 0, 2, 2), 0f, 0.5f, 0.5f, 1f);   // bottom-left
        AssertUv(UvLayout.Quadrant(1, 1, 2, 2), 0.5f, 0.5f, 1f, 1f);   // bottom-right
    }

    [Fact]
    public void Quadrant_NonSquareGrid()
    {
        // 1 row, 3 cols → vertical thirds.
        AssertUv(UvLayout.Quadrant(0, 0, 1, 3), 0f, 0f, 1f / 3f, 1f);
        AssertUv(UvLayout.Quadrant(0, 2, 1, 3), 2f / 3f, 0f, 1f, 1f);
    }

    [Fact]
    public void Quadrant_3x3_CornersAndCentre()
    {
        // 9-screen split → 3×3 grid of equal thirds.
        var t = 1f / 3f;
        AssertUv(UvLayout.Quadrant(0, 0, 3, 3), 0f, 0f, t, t);          // top-left
        AssertUv(UvLayout.Quadrant(1, 1, 3, 3), t, t, 2 * t, 2 * t);    // centre
        AssertUv(UvLayout.Quadrant(2, 2, 3, 3), 2 * t, 2 * t, 1f, 1f);  // bottom-right
    }

    [Theory]
    [InlineData(0, 0, 0, 2)]   // zero rows
    [InlineData(0, 0, 2, 0)]   // zero cols
    [InlineData(2, 0, 2, 2)]   // row out of range
    [InlineData(0, 2, 2, 2)]   // col out of range
    [InlineData(-1, 0, 2, 2)]  // negative row
    public void Quadrant_InvalidArgs_Throw(int row, int col, int rows, int cols)
    {
        Assert.ThrowsAny<ArgumentException>(() => UvLayout.Quadrant(row, col, rows, cols));
    }
}
