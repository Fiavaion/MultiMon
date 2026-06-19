using MultiMon.Core.Sync;

namespace MultiMon.Tests;

/// <summary>
/// Contract tests for FrameSelector.Select (ADR 0002 D2).
/// FrameSelector is pure and allocation-free — no sleeps, no tolerances needed.
/// </summary>
public class FrameSelectorTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Build a PTS list from whole-millisecond values for readable test data.</summary>
    private static List<TimeSpan> Pts(params int[] msValues) =>
        msValues.Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();

    // ── Guard conditions ───────────────────────────────────────────────────────

    [Fact]
    public void Null_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            FrameSelector.Select(null!, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Empty_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            FrameSelector.Select(new List<TimeSpan>(), TimeSpan.FromMilliseconds(100)));
    }

    // ── Single-frame list ─────────────────────────────────────────────────────

    [Fact]
    public void Single_TargetBeforeFrame_Returns0()
    {
        var pts = Pts(500);
        // targetTime = 0 ms, frame at 500 ms → target precedes first → 0
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.Zero));
    }

    [Fact]
    public void Single_TargetExactlyAtFrame_Returns0()
    {
        var pts = Pts(500);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Single_TargetAfterFrame_Returns0()
    {
        var pts = Pts(500);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(9000)));
    }

    // ── Target before the first frame ─────────────────────────────────────────

    [Fact]
    public void TargetBeforeAll_ReturnsIndex0()
    {
        var pts = Pts(100, 200, 300);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void TargetAtZero_BeforeAll_ReturnsIndex0()
    {
        var pts = Pts(33, 66, 100);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.Zero));
    }

    // ── Exact match ───────────────────────────────────────────────────────────

    [Fact]
    public void ExactMatchFirst_Returns0()
    {
        var pts = Pts(100, 200, 300);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void ExactMatchMiddle_ReturnsCorrectIndex()
    {
        var pts = Pts(100, 200, 300, 400, 500);
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void ExactMatchLast_ReturnsLastIndex()
    {
        var pts = Pts(100, 200, 300);
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(300)));
    }

    // ── Target strictly between two frames ────────────────────────────────────

    [Fact]
    public void BetweenFirstAndSecond_Returns0()
    {
        var pts = Pts(0, 100, 200);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void BetweenMiddleFrames_ReturnsLowerIndex()
    {
        var pts = Pts(0, 100, 200, 300, 400);
        // target = 150 ms → between index 1 (100) and index 2 (200) → returns 1
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void BetweenLastAndBeyond_ReturnsSecondToLast()
    {
        var pts = Pts(0, 100, 200);
        // target = 150 ms → between index 1 (100) and index 2 (200) → returns 1
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(150)));
    }

    // ── Target after the last frame ───────────────────────────────────────────

    [Fact]
    public void TargetAfterAll_ReturnsLastIndex()
    {
        var pts = Pts(100, 200, 300);
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(99999)));
    }

    [Fact]
    public void TargetFarBeyondLast_ReturnsLastIndex()
    {
        var pts = Pts(0, 33, 66, 100);
        Assert.Equal(3, FrameSelector.Select(pts, TimeSpan.FromHours(1)));
    }

    // ── Duplicate adjacent PTS ────────────────────────────────────────────────

    [Fact]
    public void DuplicatePts_AtTarget_ReturnsHighestSuchIndex()
    {
        // Frames 1, 2, 3 all at 200 ms; target = 200 ms → highest index with pts <= target = 3
        var pts = Pts(100, 200, 200, 200, 300);
        Assert.Equal(3, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void DuplicatePtsAtStart_TargetExact_ReturnsHighestDuplicateIndex()
    {
        var pts = Pts(0, 0, 0, 100);
        // target = 0 → all three 0-ms frames qualify; highest = index 2
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.Zero));
    }

    [Fact]
    public void DuplicatePts_TargetBeyondDuplicates_ReturnsBeyondThem()
    {
        var pts = Pts(100, 200, 200, 300);
        // target = 250 ms → highest pts <= 250 ms is 200 ms at index 2
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(250)));
    }

    // ── 10-frame list — multiple target sweeps ────────────────────────────────

    [Fact]
    public void TenFrames_MultipleSweeps()
    {
        // 10 frames at 0, 33, 66, 100, 133, 166, 200, 233, 266, 300 ms (≈30 fps)
        var pts = Pts(0, 33, 66, 100, 133, 166, 200, 233, 266, 300);

        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.Zero));            // exact first
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(16)));  // before index 1
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(33)));  // exact index 1
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(50)));  // between 1 and 2
        Assert.Equal(2, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(66)));  // exact index 2
        Assert.Equal(4, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(133))); // exact index 4
        Assert.Equal(5, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(180))); // between 5 and 6
        Assert.Equal(9, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(300))); // exact last
        Assert.Equal(9, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(500))); // beyond last
    }

    // ── Negative / zero boundary ──────────────────────────────────────────────

    [Fact]
    public void PtsStartingAtZero_TargetAtZero_Returns0()
    {
        var pts = Pts(0, 100, 200);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.Zero));
    }

    [Fact]
    public void NegativeTargetBeforeFirstFrame_Returns0()
    {
        var pts = Pts(0, 100, 200);
        // Negative media time is unusual but the contract says "precedes all → return 0".
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(-50)));
    }

    // ── Two-frame edge cases ───────────────────────────────────────────────────

    [Fact]
    public void TwoFrames_TargetBeforeFirst_Returns0()
    {
        var pts = Pts(100, 200);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void TwoFrames_TargetExactFirst_Returns0()
    {
        var pts = Pts(100, 200);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void TwoFrames_TargetBetween_Returns0()
    {
        var pts = Pts(100, 200);
        Assert.Equal(0, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void TwoFrames_TargetExactSecond_Returns1()
    {
        var pts = Pts(100, 200);
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void TwoFrames_TargetAfterSecond_Returns1()
    {
        var pts = Pts(100, 200);
        Assert.Equal(1, FrameSelector.Select(pts, TimeSpan.FromMilliseconds(999)));
    }
}
