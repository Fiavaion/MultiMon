using MultiMon.Core.Models;
using MultiMon.Core.Show;

namespace MultiMon.Core.Tests;

/// <summary>
/// Pins the show mapping — ShowDefinition + monitors → which source feeds which output, through which UV
/// sub-rect, on which clock. These were written against the behaviour PerformanceController had BEFORE the
/// mapping moved into <see cref="ShowPlanner"/>, so they are the regression gate for that extraction: every
/// expectation here is what the controller's own BuildSingleSource/BuildPerMonitor/TryGetCell produced.
/// </summary>
public class ShowPlannerTests
{
    private static MonitorInfo Monitor(string id, double x, double y, double w, double h)
        => new() { DeviceId = id, Bounds = new MonitorRect(x, y, w, h) };

    /// <summary>Two side-by-side screens of DIFFERENT resolution — the span is per screen, not per pixel.</summary>
    private static List<MonitorInfo> TwoMixedAcross() => new()
    {
        Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080),
        Monitor(@"\\.\DISPLAY2", 1920, 0, 3840, 2160),
    };

    private static ShowDefinition Show(ShowMode mode, params SourceBinding[] sources)
        => new() { Mode = mode, Sources = sources.ToList() };

    private static SourceBinding Binding(string id, string path, string? monitor = null, bool isHap = false)
        => new() { SourceId = id, FilePath = path, MonitorDeviceId = monitor, IsHap = isHap };

    private static void AssertUv(UvRect expected, UvRect actual)
    {
        Assert.Equal(expected.U0, actual.U0, 5);
        Assert.Equal(expected.V0, actual.V0, 5);
        Assert.Equal(expected.U1, actual.U1, 5);
        Assert.Equal(expected.V1, actual.V1, 5);
    }

    // ── Span ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Span_TwoMixedResolutionMonitors_SplitsInHalfPerScreen()
    {
        var plan = ShowPlanner.Plan(Show(ShowMode.Span, Binding("main", @"D:\wall.mp4")), TwoMixedAcross());

        Assert.Equal(new[] { "main" }, plan.Sources.Select(s => s.SourceId));
        Assert.Empty(plan.Warnings);
        Assert.Equal(2, plan.Bindings.Count);
        Assert.All(plan.Bindings, b => Assert.Equal(0, b.SourceIndex));
        Assert.All(plan.Bindings, b => Assert.Equal(ShowClock.Shared, b.Clock));

        Assert.Equal(new[] { 0, 1 }, plan.Bindings.Select(b => b.OutputIndex));
        // The 3840-wide screen takes exactly half, same as the 1920-wide one (equal share PER SCREEN).
        AssertUv(new UvRect(0f, 0f, 0.5f, 1f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(0.5f, 0f, 1f, 1f), plan.Bindings[1].Uv);
    }

    [Fact]
    public void Span_ThreeMixedResolutionMonitors_SplitsInThirdsPerScreen()
    {
        var monitors = new List<MonitorInfo>
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, 2560, 1440),
            Monitor(@"\\.\DISPLAY2", 2560, 0, 1920, 1080),
            Monitor(@"\\.\DISPLAY3", 4480, 0, 3840, 2160),
        };

        var plan = ShowPlanner.Plan(Show(ShowMode.Span, Binding("main", @"D:\wall.mp4")), monitors);

        Assert.Single(plan.Sources);
        Assert.Equal(3, plan.Bindings.Count);
        AssertUv(new UvRect(0f, 0f, 1f / 3f, 1f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(1f / 3f, 0f, 2f / 3f, 1f), plan.Bindings[1].Uv);
        AssertUv(new UvRect(2f / 3f, 0f, 1f, 1f), plan.Bindings[2].Uv);
    }

    [Fact]
    public void Span_StackedMonitors_ClusterIntoRows()
    {
        var monitors = new List<MonitorInfo>
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080),
            Monitor(@"\\.\DISPLAY2", 0, 1080, 1920, 1080),
        };

        var plan = ShowPlanner.Plan(Show(ShowMode.Span, Binding("main", @"D:\wall.mp4")), monitors);

        AssertUv(new UvRect(0f, 0f, 1f, 0.5f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(0f, 0.5f, 1f, 1f), plan.Bindings[1].Uv);
    }

    [Fact]
    public void Span_UsesTheFirstSourceOnly_AndHonoursItsHapFlag()
    {
        var show = Show(ShowMode.Span,
            Binding("first", @"D:\a.mov", isHap: true),
            Binding("second", @"D:\b.mp4"));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.Single(plan.Sources);
        Assert.Equal("first", plan.Sources[0].SourceId);
        Assert.True(plan.Sources[0].PreferHap);
    }

    [Fact]
    public void Span_NoSources_PlansNothing()
    {
        var plan = ShowPlanner.Plan(Show(ShowMode.Span), TwoMixedAcross());

        Assert.Empty(plan.Sources);
        Assert.Empty(plan.Bindings);
        Assert.Empty(plan.Warnings);
    }

    // ── Individual / Hap ─────────────────────────────────────────────────────

    [Fact]
    public void Individual_BindsByDeviceId_NotByPosition_FullUv_FreeRun()
    {
        // Sources listed in the REVERSE of the monitor order: the mapping must follow DeviceId.
        var show = Show(ShowMode.Individual,
            Binding("s2", @"D:\b.mp4", @"\\.\DISPLAY2"),
            Binding("s1", @"D:\a.mp4", @"\\.\DISPLAY1"));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.Equal(new[] { "s2", "s1" }, plan.Sources.Select(s => s.SourceId));
        Assert.Equal(new[] { 0, 1 }, plan.Bindings.Select(b => b.SourceIndex));
        Assert.Equal(new[] { 1, 0 }, plan.Bindings.Select(b => b.OutputIndex));
        Assert.All(plan.Bindings, b => AssertUv(UvRect.Full, b.Uv));
        Assert.All(plan.Bindings, b => Assert.Equal(ShowClock.FreeRun, b.Clock));
    }

    [Fact]
    public void Individual_DeviceIdMatchIsCaseInsensitive()
    {
        var show = Show(ShowMode.Individual, Binding("s1", @"D:\a.mp4", @"\\.\display2"));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.Equal(1, plan.Bindings[0].OutputIndex);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Individual_SyncIndividual_SharesOneClock()
    {
        var show = Show(ShowMode.Individual, Binding("s1", @"D:\a.mp4", @"\\.\DISPLAY1"));
        show.SyncIndividual = true;

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.Equal(ShowClock.Shared, plan.Bindings[0].Clock);
    }

    [Fact]
    public void Individual_NeverPrefersHap_EvenWhenTheBindingSaysSo()
    {
        var show = Show(ShowMode.Individual, Binding("s1", @"D:\a.mov", @"\\.\DISPLAY1", isHap: true));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.False(plan.Sources[0].PreferHap);
    }

    [Fact]
    public void Individual_UnknownMonitor_IsSkippedWithAWarning_AndIndicesStayContiguous()
    {
        var show = Show(ShowMode.Individual,
            Binding("ghost", @"D:\ghost.mp4", @"\\.\DISPLAY9"),
            Binding("real", @"D:\real.mp4", @"\\.\DISPLAY2"));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.Equal(new[] { "real" }, plan.Sources.Select(s => s.SourceId));
        var binding = Assert.Single(plan.Bindings);
        Assert.Equal(0, binding.SourceIndex);   // the skipped source must not leave a gap
        Assert.Equal(1, binding.OutputIndex);
        Assert.Equal(@"source 'D:\ghost.mp4' targets unknown monitor '\\.\DISPLAY9'; skipping.",
            Assert.Single(plan.Warnings));
    }

    [Fact]
    public void Individual_MissingMonitorId_IsSkipped()
    {
        var plan = ShowPlanner.Plan(Show(ShowMode.Individual, Binding("s1", @"D:\a.mp4")), TwoMixedAcross());

        Assert.Empty(plan.Bindings);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Hap_PrefersHapForEverySource_OnTheSharedClock()
    {
        var show = Show(ShowMode.Hap,
            Binding("s1", @"D:\a.mov", @"\\.\DISPLAY1"),
            Binding("s2", @"D:\b.mov", @"\\.\DISPLAY2", isHap: false));

        var plan = ShowPlanner.Plan(show, TwoMixedAcross());

        Assert.All(plan.Sources, s => Assert.True(s.PreferHap));
        Assert.All(plan.Bindings, b => Assert.Equal(ShowClock.Shared, b.Clock));
        Assert.All(plan.Bindings, b => AssertUv(UvRect.Full, b.Uv));
    }

    // ── Split ────────────────────────────────────────────────────────────────

    private static List<MonitorInfo> Screens(int n)
    {
        var list = new List<MonitorInfo>();
        for (var i = 0; i < n; i++)
            list.Add(Monitor($@"\\.\DISPLAY{i + 1}", i * 1920, 0, 1920, 1080));
        return list;
    }

    private static ShowDefinition SplitShow(int rows, int cols, Dictionary<string, string>? mapping = null)
    {
        var show = Show(ShowMode.Split, Binding("main", @"D:\wall.mp4"));
        show.WallConfiguration = new VideoWallConfiguration { Rows = rows, Columns = cols };
        if (mapping is not null)
            show.WallConfiguration.GridToMonitorMapping = mapping;
        return show;
    }

    [Fact]
    public void Split_AutoGridForFourScreens_IsTwoByTwoRowMajor()
    {
        var (rows, cols) = ShowPlanner.AutoGrid(4);
        Assert.Equal((2, 2), (rows, cols));

        var plan = ShowPlanner.Plan(SplitShow(rows, cols), Screens(4));

        Assert.Single(plan.Sources);
        Assert.Equal(4, plan.Bindings.Count);
        AssertUv(new UvRect(0f, 0f, 0.5f, 0.5f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(0.5f, 0f, 1f, 0.5f), plan.Bindings[1].Uv);
        AssertUv(new UvRect(0f, 0.5f, 0.5f, 1f), plan.Bindings[2].Uv);
        AssertUv(new UvRect(0.5f, 0.5f, 1f, 1f), plan.Bindings[3].Uv);
        Assert.All(plan.Bindings, b => Assert.Equal(ShowClock.Shared, b.Clock));
    }

    [Fact]
    public void Split_AutoGridForNineScreens_IsThreeByThreeRowMajor()
    {
        var (rows, cols) = ShowPlanner.AutoGrid(9);
        Assert.Equal((3, 3), (rows, cols));

        var plan = ShowPlanner.Plan(SplitShow(rows, cols), Screens(9));

        Assert.Equal(9, plan.Bindings.Count);
        for (var i = 0; i < 9; i++)
            AssertUv(new UvRect(i % 3 / 3f, i / 3 / 3f, (i % 3 + 1) / 3f, (i / 3 + 1) / 3f), plan.Bindings[i].Uv);
    }

    [Theory]
    [InlineData(0, 1, 1)]   // never a zero grid
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 2, 3)]
    [InlineData(9, 3, 3)]
    [InlineData(16, 4, 4)]
    public void AutoGrid_IsNearSquare(int screens, int expectedRows, int expectedCols)
    {
        Assert.Equal((expectedRows, expectedCols), ShowPlanner.AutoGrid(screens));
    }

    [Fact]
    public void Split_ManualGrid_OverridesTheScreenCount()
    {
        // Three screens, but the user asked for a 1x3 strip.
        var plan = ShowPlanner.Plan(SplitShow(1, 3), Screens(3));

        Assert.Equal(3, plan.Bindings.Count);
        AssertUv(new UvRect(0f, 0f, 1f / 3f, 1f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(1f / 3f, 0f, 2f / 3f, 1f), plan.Bindings[1].Uv);
        AssertUv(new UvRect(2f / 3f, 0f, 1f, 1f), plan.Bindings[2].Uv);
    }

    [Fact]
    public void Split_ExplicitMapping_PlacesMonitorsInTheirNamedCells()
    {
        var mapping = new Dictionary<string, string>
        {
            ["1,1"] = @"\\.\DISPLAY1",
            ["0,0"] = @"\\.\DISPLAY2",
        };

        var plan = ShowPlanner.Plan(SplitShow(2, 2, mapping), Screens(2));

        AssertUv(new UvRect(0.5f, 0.5f, 1f, 1f), plan.Bindings[0].Uv);  // DISPLAY1 → cell (1,1)
        AssertUv(new UvRect(0f, 0f, 0.5f, 0.5f), plan.Bindings[1].Uv);  // DISPLAY2 → cell (0,0)
    }

    [Fact]
    public void Split_MappingOutsideTheGrid_FallsBackToRowMajorOrder()
    {
        // "5,5" is not a cell of a 2x2 grid, so the fallback by output index applies.
        var mapping = new Dictionary<string, string> { ["5,5"] = @"\\.\DISPLAY1" };

        var plan = ShowPlanner.Plan(SplitShow(2, 2, mapping), Screens(2));

        AssertUv(new UvRect(0f, 0f, 0.5f, 0.5f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(0.5f, 0f, 1f, 0.5f), plan.Bindings[1].Uv);
    }

    [Fact]
    public void Split_MonitorsBeyondTheGridCells_AreLeftUnbound()
    {
        // Five screens over a 2x2 grid: the fifth has no cell and stays black rather than duplicating one.
        var plan = ShowPlanner.Plan(SplitShow(2, 2), Screens(5));

        Assert.Equal(4, plan.Bindings.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, plan.Bindings.Select(b => b.OutputIndex));
    }

    [Fact]
    public void Split_NoWallConfiguration_DefaultsToTwoByTwo()
    {
        var show = Show(ShowMode.Split, Binding("main", @"D:\wall.mp4"));

        var plan = ShowPlanner.Plan(show, Screens(4));

        Assert.Equal(4, plan.Bindings.Count);
        AssertUv(new UvRect(0f, 0f, 0.5f, 0.5f), plan.Bindings[0].Uv);
        AssertUv(new UvRect(0.5f, 0.5f, 1f, 1f), plan.Bindings[3].Uv);
    }

    [Fact]
    public void Split_NonPositiveGrid_IsClampedToOne()
    {
        var plan = ShowPlanner.Plan(SplitShow(0, 0), Screens(1));

        AssertUv(UvRect.Full, Assert.Single(plan.Bindings).Uv);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ShowPlanner.Plan(null!, TwoMixedAcross()));
        Assert.Throws<ArgumentNullException>(() => ShowPlanner.Plan(Show(ShowMode.Span), null!));
    }
}
